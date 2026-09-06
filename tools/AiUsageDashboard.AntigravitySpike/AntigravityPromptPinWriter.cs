using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal interface IAntigravityPromptPinFileReplacer
{
	void Move(string sourceFileName, string destinationFileName);

	void Replace(
		string sourceFileName,
		string destinationFileName,
		string destinationBackupFileName);
}

internal sealed class AntigravityPromptPinFileReplacer :
	IAntigravityPromptPinFileReplacer
{
	internal static AntigravityPromptPinFileReplacer Instance { get; } = new();

	private AntigravityPromptPinFileReplacer()
	{
	}

	public void Move(string sourceFileName, string destinationFileName)
	{
		File.Move(sourceFileName, destinationFileName);
	}

	public void Replace(
		string sourceFileName,
		string destinationFileName,
		string destinationBackupFileName)
	{
		File.Replace(
			sourceFileName,
			destinationFileName,
			destinationBackupFileName,
			ignoreMetadataErrors: false);
	}
}

internal static class AntigravityPromptPinWriter
{
	private enum PinTarget
	{
		Prompt,
		Usage
	}

	private enum PinUpdateMode
	{
		Pin,
		Repin
	}

	private enum ReconciliationResult
	{
		Unrecoverable,
		RestoredCleanable,
		RestoredPreserveDebris
	}

	private readonly record struct ProfileFileState(
		long Length,
		DateTime CreationTimeUtc,
		DateTime LastWriteTimeUtc,
		FileAttributes Attributes);

	private readonly record struct JsonTokenRange(int Start, int Length);

	private const int MaximumProfileBytes = 1024 * 1024;
	private static readonly HashSet<string> ExecutableFingerprintPropertyNames =
		new(StringComparer.Ordinal)
		{
			"AbsolutePath",
			"CliVersion",
			"FileVersion",
			"ProductVersion",
			"Sha256",
			"WinVerifyTrustStatus",
			"SignerSubject",
			"SignerThumbprint"
		};
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};
	private static readonly HashSet<string> ProfilePropertyNames =
		new(StringComparer.Ordinal)
		{
			"Id",
			"ExecutableFingerprint",
			"WorkingDirectory",
			"Environment",
			"Columns",
			"Rows",
			"ExpectedPromptStructuralFingerprint",
			"ExactPromptFingerprintHmacKeyPath",
			"ExpectedExactPromptFingerprint",
			"ExpectedUsageStructuralFingerprint",
			"NonCredentialSettingsFiles",
			"MaximumSavedOutputBytes",
			"PromptTimeout",
			"UsageTimeout",
			"StableScreenDuration",
			"PollInterval",
			"CleanupTimeout",
			"ExpectedExactUsageFingerprint"
		};
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);

	internal static Task<bool> TryPinAsync(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint,
		CancellationToken cancellationToken = default)
	{
		return TryPinAsync(
			profilePath,
			structuralFingerprint,
			exactFingerprint,
			AntigravityPromptPinFileReplacer.Instance,
			cancellationToken);
	}

	internal static Task<bool> TryPinAsync(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint,
		IAntigravityPromptPinFileReplacer fileReplacer,
		CancellationToken cancellationToken = default)
	{
		return TryUpdateAsync(
			profilePath,
			expectedStructuralFingerprint: null,
			expectedExactFingerprint: null,
			structuralFingerprint,
			exactFingerprint,
			PinTarget.Prompt,
			PinUpdateMode.Pin,
			fileReplacer,
			cancellationToken);
	}

	internal static Task<bool> TryRepinAsync(
		string profilePath,
		string expectedStructuralFingerprint,
		string expectedExactFingerprint,
		string structuralFingerprint,
		string exactFingerprint,
		CancellationToken cancellationToken = default)
	{
		return TryRepinAsync(
			profilePath,
			expectedStructuralFingerprint,
			expectedExactFingerprint,
			structuralFingerprint,
			exactFingerprint,
			AntigravityPromptPinFileReplacer.Instance,
			cancellationToken);
	}

	internal static Task<bool> TryRepinAsync(
		string profilePath,
		string expectedStructuralFingerprint,
		string expectedExactFingerprint,
		string structuralFingerprint,
		string exactFingerprint,
		IAntigravityPromptPinFileReplacer fileReplacer,
		CancellationToken cancellationToken = default)
	{
		return TryUpdateAsync(
			profilePath,
			expectedStructuralFingerprint,
			expectedExactFingerprint,
			structuralFingerprint,
			exactFingerprint,
			PinTarget.Prompt,
			PinUpdateMode.Repin,
			fileReplacer,
			cancellationToken);
	}

	internal static Task<bool> TryPinUsageAsync(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint,
		CancellationToken cancellationToken = default)
	{
		return TryPinUsageAsync(
			profilePath,
			structuralFingerprint,
			exactFingerprint,
			AntigravityPromptPinFileReplacer.Instance,
			cancellationToken);
	}

	internal static Task<bool> TryPinUsageAsync(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint,
		IAntigravityPromptPinFileReplacer fileReplacer,
		CancellationToken cancellationToken = default)
	{
		return TryUpdateAsync(
			profilePath,
			expectedStructuralFingerprint: null,
			expectedExactFingerprint: null,
			structuralFingerprint,
			exactFingerprint,
			PinTarget.Usage,
			PinUpdateMode.Pin,
			fileReplacer,
			cancellationToken);
	}

	private static async Task<bool> TryUpdateAsync(
		string profilePath,
		string? expectedStructuralFingerprint,
		string? expectedExactFingerprint,
		string structuralFingerprint,
		string exactFingerprint,
		PinTarget pinTarget,
		PinUpdateMode updateMode,
		IAntigravityPromptPinFileReplacer fileReplacer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fileReplacer);
		string? temporaryPath = null;
		string? backupPath = null;
		string? failedNewBackupPath = null;
		string? recoveryPath = null;
		string? lockPath = null;
		FileStream? lockStream = null;
		byte[]? originalBytes = null;
		byte[]? updatedBytes = null;
		AntigravityLiveR0ProfileFile? originalProfile = null;
		string absoluteProfilePath = string.Empty;
		string parentPath = string.Empty;
		bool shouldCleanDebris = true;
		bool hasReplaceAttempted = false;
		bool ownsLockFile = false;

		try
		{
			if (!IsHexFingerprint(structuralFingerprint) ||
				!IsHexFingerprint(exactFingerprint) ||
				((updateMode == PinUpdateMode.Repin) &&
				 (!IsHexFingerprint(expectedStructuralFingerprint!) ||
				  !IsHexFingerprint(expectedExactFingerprint!) ||
				  ArePinsEqual(
					expectedStructuralFingerprint!,
					expectedExactFingerprint!,
					structuralFingerprint,
					exactFingerprint))) ||
				((pinTarget == PinTarget.Usage) &&
				 (updateMode != PinUpdateMode.Pin)) ||
				!TryResolveSafeProfile(
					profilePath,
					out absoluteProfilePath,
					out parentPath))
			{
				return false;
			}

			lockPath = Path.Combine(
				parentPath,
				string.Concat(
					".",
					Path.GetFileName(absoluteProfilePath),
					".pin-prompt.lock"));
			lockStream = new FileStream(
				lockPath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				1,
				FileOptions.WriteThrough);
			ownsLockFile = true;
			lockStream.Flush(flushToDisk: true);

			if (!IsSafeProfileFile(lockPath, parentPath) ||
				!TryCaptureProfileFileState(
					absoluteProfilePath,
					parentPath,
					out ProfileFileState originalState))
			{
				return false;
			}

			originalBytes = await ReadExactFileAsync(
				absoluteProfilePath,
				originalState,
				cancellationToken);

			if (!TryCaptureProfileFileState(
					absoluteProfilePath,
					parentPath,
					out ProfileFileState afterReadState) ||
				(afterReadState != originalState) ||
				!TryDeserializeProfile(
					originalBytes,
					out originalProfile) ||
				(originalProfile is null) ||
				!IsWellFormedProfile(originalProfile) ||
				((pinTarget == PinTarget.Usage) &&
				 (!IsHexFingerprint(
					originalProfile.ExpectedPromptStructuralFingerprint) ||
				  !IsHexFingerprint(
					originalProfile.ExpectedExactPromptFingerprint))))
			{
				return false;
			}

			(string? currentStructuralFingerprint,
				string? currentExactFingerprint) = GetPins(
				originalProfile,
				pinTarget);
			bool isAlreadyPinned = ArePinsEqual(
				currentStructuralFingerprint,
				currentExactFingerprint,
				structuralFingerprint,
				exactFingerprint);
			bool canUpdate = updateMode switch
			{
				PinUpdateMode.Pin => IsUnpinned(
					currentStructuralFingerprint,
					currentExactFingerprint,
					pinTarget),
				PinUpdateMode.Repin => ArePinsEqual(
					currentStructuralFingerprint,
					currentExactFingerprint,
					expectedStructuralFingerprint!,
					expectedExactFingerprint!),
				_ => false
			};

			if (!canUpdate && !isAlreadyPinned)
			{
				return false;
			}

			if (!IsTargetExactFingerprintDistinct(
				originalProfile,
				pinTarget,
				exactFingerprint))
			{
				return false;
			}

			if (isAlreadyPinned)
			{
				return true;
			}

			AntigravityLiveR0ProfileFile updatedProfile = CreateUpdatedProfile(
				originalProfile,
				pinTarget,
				structuralFingerprint,
				exactFingerprint);

			if (!HasSameNonTargetFields(
				originalProfile,
				updatedProfile,
				pinTarget))
			{
				return false;
			}

			updatedBytes = ReplacePinTokens(
				originalBytes,
				pinTarget,
				structuralFingerprint,
				exactFingerprint);

			if (updatedBytes is null)
			{
				return false;
			}

			temporaryPath = CreateSiblingPath(
				absoluteProfilePath,
				"new");
			backupPath = CreateSiblingPath(
				absoluteProfilePath,
				"backup");
			failedNewBackupPath = CreateSiblingPath(
				absoluteProfilePath,
				"failed-new");
			recoveryPath = CreateSiblingPath(
				absoluteProfilePath,
				"recovery");

			if (!IsSafePrivateDirectory(parentPath))
			{
				return false;
			}

			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(updatedBytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
				stream.Flush(flushToDisk: true);
			}

			if (!TryCaptureProfileFileState(
					temporaryPath,
					parentPath,
					out ProfileFileState temporaryState) ||
				(temporaryState.Length != updatedBytes.Length))
			{
				return false;
			}

			byte[] verifiedTemporaryBytes = await ReadExactFileAsync(
				temporaryPath,
				temporaryState,
				cancellationToken);

			if (!verifiedTemporaryBytes.AsSpan().SequenceEqual(updatedBytes) ||
				!TryDeserializeProfile(
					verifiedTemporaryBytes,
					out AntigravityLiveR0ProfileFile? temporaryProfile) ||
				(temporaryProfile is null) ||
				!IsWellFormedProfile(temporaryProfile) ||
				!HasExpectedUpdate(
					originalProfile,
					temporaryProfile,
					pinTarget,
					structuralFingerprint,
					exactFingerprint) ||
				!TryCaptureProfileFileState(
					absoluteProfilePath,
					parentPath,
					out ProfileFileState beforeReplaceState) ||
				(beforeReplaceState != originalState))
			{
				return false;
			}

			if (!await TryCreatePrivateSiblingAsync(
				recoveryPath,
				parentPath,
				originalBytes,
				cancellationToken))
			{
				return false;
			}

			byte[] currentBytes = await ReadExactFileAsync(
				absoluteProfilePath,
				beforeReplaceState,
				cancellationToken);

			if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
			{
				return false;
			}

			cancellationToken.ThrowIfCancellationRequested();
			hasReplaceAttempted = true;
			shouldCleanDebris = false;
			fileReplacer.Replace(
				temporaryPath,
				absoluteProfilePath,
				backupPath);

			bool isBackupValid = await HasExactSafeFileBytesAsync(
				backupPath,
				parentPath,
				originalBytes,
				CancellationToken.None);
			bool isRecoveryValid = await HasExactSafeFileBytesAsync(
				recoveryPath,
				parentPath,
				originalBytes,
				CancellationToken.None);
			bool isFinalValid = isBackupValid &&
				isRecoveryValid &&
				await IsExpectedUpdatedFileAsync(
					absoluteProfilePath,
					parentPath,
					originalProfile,
					pinTarget,
					structuralFingerprint,
					exactFingerprint,
					updatedBytes,
					CancellationToken.None);

			if (!isFinalValid)
			{
				ReconciliationResult reconciliationResult =
					await TryReconcileAfterReplaceAttemptAsync(
					recoveryPath,
					backupPath,
					absoluteProfilePath,
					failedNewBackupPath,
					parentPath,
					originalBytes,
					updatedBytes,
					fileReplacer);
				shouldCleanDebris =
					reconciliationResult ==
						ReconciliationResult.RestoredCleanable;

				return false;
			}

			shouldCleanDebris = true;
			return true;
		}
		catch
		{
			if (hasReplaceAttempted &&
				(originalBytes is not null) &&
				(updatedBytes is not null) &&
				(recoveryPath is not null) &&
				(backupPath is not null) &&
				(failedNewBackupPath is not null))
			{
				ReconciliationResult reconciliationResult =
					await TryReconcileAfterReplaceAttemptAsync(
					recoveryPath,
					backupPath,
					absoluteProfilePath,
					failedNewBackupPath,
					parentPath,
					originalBytes,
					updatedBytes,
					fileReplacer);
				shouldCleanDebris =
					reconciliationResult ==
						ReconciliationResult.RestoredCleanable;
			}

			return false;
		}
		finally
		{
			if (shouldCleanDebris && (temporaryPath is not null))
			{
				TryDeleteTemporaryFile(temporaryPath);
			}

			if (shouldCleanDebris && (backupPath is not null))
			{
				TryDeleteTemporaryFile(backupPath);
			}

			if (shouldCleanDebris && (failedNewBackupPath is not null))
			{
				TryDeleteTemporaryFile(failedNewBackupPath);
			}

			if (shouldCleanDebris && (recoveryPath is not null))
			{
				TryDeleteTemporaryFile(recoveryPath);
			}

			try
			{
				lockStream?.Dispose();
			}
			catch
			{
				// Lock cleanup must never mask the fail-closed result.
			}

			if (ownsLockFile && (lockPath is not null))
			{
				TryDeleteTemporaryFile(lockPath);
			}
		}
	}

	private static bool ArePinsEqual(
		string? firstStructuralFingerprint,
		string? firstExactFingerprint,
		string? secondStructuralFingerprint,
		string? secondExactFingerprint)
	{
		return string.Equals(
				firstStructuralFingerprint,
				secondStructuralFingerprint,
				StringComparison.Ordinal) &&
			string.Equals(
				firstExactFingerprint,
				secondExactFingerprint,
				StringComparison.Ordinal);
	}

	private static AntigravityLiveR0ProfileFile CreateUpdatedProfile(
		AntigravityLiveR0ProfileFile originalProfile,
		PinTarget pinTarget,
		string structuralFingerprint,
		string exactFingerprint)
	{
		return pinTarget switch
		{
			PinTarget.Prompt => originalProfile with
			{
				ExpectedPromptStructuralFingerprint = structuralFingerprint,
				ExpectedExactPromptFingerprint = exactFingerprint
			},
			PinTarget.Usage => originalProfile with
			{
				ExpectedUsageStructuralFingerprint = structuralFingerprint,
				ExpectedExactUsageFingerprint = exactFingerprint
			},
			_ => throw new InvalidOperationException(
				"The profile pin target is invalid.")
		};
	}

	private static bool DictionaryEquals(
		IReadOnlyDictionary<string, string> first,
		IReadOnlyDictionary<string, string> second)
	{
		if (first.Count != second.Count)
		{
			return false;
		}

		foreach ((string key, string value) in first)
		{
			if (!second.TryGetValue(key, out string? otherValue) ||
				!string.Equals(value, otherValue, StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	private static (string? StructuralFingerprint, string? ExactFingerprint)
		GetPins(
		AntigravityLiveR0ProfileFile profile,
		PinTarget pinTarget)
	{
		return pinTarget switch
		{
			PinTarget.Prompt =>
				(profile.ExpectedPromptStructuralFingerprint,
				 profile.ExpectedExactPromptFingerprint),
			PinTarget.Usage =>
				(profile.ExpectedUsageStructuralFingerprint,
				 profile.ExpectedExactUsageFingerprint),
			_ => throw new InvalidOperationException(
				"The profile pin target is invalid.")
		};
	}

	private static (string StructuralPropertyName, string ExactPropertyName)
		GetPinPropertyNames(PinTarget pinTarget)
	{
		return pinTarget switch
		{
			PinTarget.Prompt =>
				(nameof(AntigravityLiveR0ProfileFile.
					ExpectedPromptStructuralFingerprint),
				 nameof(AntigravityLiveR0ProfileFile.
					ExpectedExactPromptFingerprint)),
			PinTarget.Usage =>
				(nameof(AntigravityLiveR0ProfileFile.
					ExpectedUsageStructuralFingerprint),
				 nameof(AntigravityLiveR0ProfileFile.
					ExpectedExactUsageFingerprint)),
			_ => throw new InvalidOperationException(
				"The profile pin target is invalid.")
		};
	}

	private static bool IsTargetExactFingerprintDistinct(
		AntigravityLiveR0ProfileFile profile,
		PinTarget pinTarget,
		string exactFingerprint)
	{
		string? otherExactFingerprint = pinTarget switch
		{
			PinTarget.Prompt => profile.ExpectedExactUsageFingerprint,
			PinTarget.Usage => profile.ExpectedExactPromptFingerprint,
			_ => null
		};
		return !string.Equals(
			exactFingerprint,
			otherExactFingerprint,
			StringComparison.Ordinal);
	}

	private static bool IsUnpinned(
		string? structuralFingerprint,
		string? exactFingerprint,
		PinTarget pinTarget)
	{
		return pinTarget switch
		{
			PinTarget.Prompt =>
				string.Equals(
					structuralFingerprint,
					string.Empty,
					StringComparison.Ordinal) &&
				string.Equals(
					exactFingerprint,
					string.Empty,
					StringComparison.Ordinal),
			PinTarget.Usage =>
				(structuralFingerprint is null) &&
				(exactFingerprint is null),
			_ => false
		};
	}

	private static string CreateSiblingPath(
		string absoluteProfilePath,
		string purpose)
	{
		string parentPath = Path.GetDirectoryName(absoluteProfilePath) ??
			throw new InvalidDataException(
				"The live R0 profile directory is invalid.");
		return Path.Combine(
			parentPath,
			string.Concat(
				".",
				Path.GetFileName(absoluteProfilePath),
				".",
				purpose,
				".",
				Guid.NewGuid().ToString("N"),
				".tmp"));
	}

	private static async Task<bool> HasExactSafeFileBytesAsync(
		string absoluteFilePath,
		string parentPath,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			if (!TryCaptureProfileFileState(
					absoluteFilePath,
					parentPath,
					out ProfileFileState state) ||
				(state.Length != expectedBytes.Length))
			{
				return false;
			}

			byte[] bytes = await ReadExactFileAsync(
				absoluteFilePath,
				state,
				cancellationToken);
			return bytes.AsSpan().SequenceEqual(expectedBytes.Span);
		}
		catch
		{
			return false;
		}
	}

	private static async Task<bool> IsExpectedUpdatedFileAsync(
		string absoluteProfilePath,
		string parentPath,
		AntigravityLiveR0ProfileFile originalProfile,
		PinTarget pinTarget,
		string structuralFingerprint,
		string exactFingerprint,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			if (!TryCaptureProfileFileState(
					absoluteProfilePath,
					parentPath,
					out ProfileFileState state) ||
				(state.Length != expectedBytes.Length))
			{
				return false;
			}

			byte[] bytes = await ReadExactFileAsync(
				absoluteProfilePath,
				state,
				cancellationToken);
			return bytes.AsSpan().SequenceEqual(expectedBytes.Span) &&
				TryDeserializeProfile(
					bytes,
					out AntigravityLiveR0ProfileFile? profile) &&
				(profile is not null) &&
				IsWellFormedProfile(profile) &&
				HasExpectedUpdate(
					originalProfile,
					profile,
					pinTarget,
					structuralFingerprint,
					exactFingerprint);
		}
		catch
		{
			return false;
		}
	}

	private static byte[]? ReplacePinTokens(
		ReadOnlySpan<byte> originalBytes,
		PinTarget pinTarget,
		string structuralFingerprint,
		string exactFingerprint)
	{
		if (!TryFindPinTokenRanges(
				originalBytes,
				pinTarget,
				out JsonTokenRange structuralRange,
				out JsonTokenRange exactRange))
		{
			return null;
		}

		byte[] structuralToken = Encoding.UTF8.GetBytes(
			string.Concat("\"", structuralFingerprint, "\""));
		byte[] exactToken = Encoding.UTF8.GetBytes(
			string.Concat("\"", exactFingerprint, "\""));

		if (structuralRange.Start > exactRange.Start)
		{
			(structuralRange, exactRange) = (exactRange, structuralRange);
			(structuralToken, exactToken) = (exactToken, structuralToken);
		}

		int newLength = checked(
			originalBytes.Length -
			structuralRange.Length -
			exactRange.Length +
			structuralToken.Length +
			exactToken.Length);
		byte[] updatedBytes = new byte[newLength];
		int sourceOffset = 0;
		int destinationOffset = 0;

		originalBytes[..structuralRange.Start].CopyTo(
			updatedBytes.AsSpan(destinationOffset));
		destinationOffset += structuralRange.Start;
		structuralToken.CopyTo(updatedBytes, destinationOffset);
		destinationOffset += structuralToken.Length;
		sourceOffset = structuralRange.Start + structuralRange.Length;
		originalBytes[sourceOffset..exactRange.Start].CopyTo(
			updatedBytes.AsSpan(destinationOffset));
		destinationOffset += exactRange.Start - sourceOffset;
		exactToken.CopyTo(updatedBytes, destinationOffset);
		destinationOffset += exactToken.Length;
		sourceOffset = exactRange.Start + exactRange.Length;
		originalBytes[sourceOffset..].CopyTo(
			updatedBytes.AsSpan(destinationOffset));
		return updatedBytes;
	}

	private static async Task<bool> TryCreatePrivateSiblingAsync(
		string absoluteFilePath,
		string parentPath,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken)
	{
		try
		{
			await using (FileStream stream = new(
				absoluteFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(bytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
				stream.Flush(flushToDisk: true);
			}

			return await HasExactSafeFileBytesAsync(
				absoluteFilePath,
				parentPath,
				bytes,
				cancellationToken);
		}
		catch
		{
			return false;
		}
	}

	private static async Task<ReconciliationResult>
		TryReconcileAfterReplaceAttemptAsync(
		string recoveryPath,
		string backupPath,
		string absoluteProfilePath,
		string failedNewBackupPath,
		string parentPath,
		ReadOnlyMemory<byte> originalBytes,
		ReadOnlyMemory<byte> updatedBytes,
		IAntigravityPromptPinFileReplacer fileReplacer)
	{
		try
		{
			bool hasBackupPath =
				File.Exists(backupPath) || Directory.Exists(backupPath);
			bool isBackupOriginal =
				hasBackupPath &&
				await HasExactSafeFileBytesAsync(
					backupPath,
					parentPath,
					originalBytes,
					CancellationToken.None);
			bool shouldPreserveDebris =
				hasBackupPath && !isBackupOriginal;

			if (await HasExactSafeFileBytesAsync(
				absoluteProfilePath,
				parentPath,
				originalBytes,
				CancellationToken.None))
			{
				bool canCleanFailedNewBackup =
					await IsFailedNewBackupCleanableAsync(
						failedNewBackupPath,
						parentPath,
						updatedBytes);
				return shouldPreserveDebris || !canCleanFailedNewBackup
					? ReconciliationResult.RestoredPreserveDebris
					: ReconciliationResult.RestoredCleanable;
			}

			if (!await HasExactSafeFileBytesAsync(
				recoveryPath,
				parentPath,
				originalBytes,
				CancellationToken.None))
			{
				return ReconciliationResult.Unrecoverable;
			}

			if (!File.Exists(absoluteProfilePath) &&
				!Directory.Exists(absoluteProfilePath))
			{
				string restoreSourcePath = isBackupOriginal
					? backupPath
					: recoveryPath;

				try
				{
					fileReplacer.Move(
						restoreSourcePath,
						absoluteProfilePath);
				}
				catch
				{
					// Re-inspect the documented partial state below.
				}

				if (await HasExactSafeFileBytesAsync(
					absoluteProfilePath,
					parentPath,
					originalBytes,
					CancellationToken.None))
				{
					bool canCleanFailedNewBackup =
						await IsFailedNewBackupCleanableAsync(
							failedNewBackupPath,
							parentPath,
							updatedBytes);
					return shouldPreserveDebris ||
						!canCleanFailedNewBackup
						? ReconciliationResult.RestoredPreserveDebris
						: ReconciliationResult.RestoredCleanable;
				}

				if (isBackupOriginal &&
					!File.Exists(absoluteProfilePath) &&
					!Directory.Exists(absoluteProfilePath) &&
					await HasExactSafeFileBytesAsync(
						recoveryPath,
						parentPath,
						originalBytes,
						CancellationToken.None))
				{
					try
					{
						fileReplacer.Move(
							recoveryPath,
							absoluteProfilePath);
					}
					catch
					{
						// Final byte-and-ACL verification decides the result.
					}
				}

				if (await HasExactSafeFileBytesAsync(
					absoluteProfilePath,
					parentPath,
					originalBytes,
					CancellationToken.None))
				{
					bool canCleanFailedNewBackup =
						await IsFailedNewBackupCleanableAsync(
							failedNewBackupPath,
							parentPath,
							updatedBytes);
					return canCleanFailedNewBackup
						? ReconciliationResult.RestoredCleanable
						: ReconciliationResult.RestoredPreserveDebris;
				}

				return ReconciliationResult.Unrecoverable;
			}

			bool isProfileUpdated = await HasExactSafeFileBytesAsync(
				absoluteProfilePath,
				parentPath,
				updatedBytes,
				CancellationToken.None);

			if (!isProfileUpdated ||
				File.Exists(failedNewBackupPath) ||
				Directory.Exists(failedNewBackupPath))
			{
				return ReconciliationResult.Unrecoverable;
			}

			try
			{
				string rollbackSourcePath = isBackupOriginal
					? backupPath
					: recoveryPath;
				fileReplacer.Replace(
					rollbackSourcePath,
					absoluteProfilePath,
					failedNewBackupPath);
			}
			catch
			{
				// Reconcile both success-then-throw and partial rollback states.
			}

			if (await HasExactSafeFileBytesAsync(
				absoluteProfilePath,
				parentPath,
				originalBytes,
				CancellationToken.None))
			{
				bool canCleanFailedNewBackup =
					await IsFailedNewBackupCleanableAsync(
						failedNewBackupPath,
						parentPath,
						updatedBytes);
				return shouldPreserveDebris || !canCleanFailedNewBackup
					? ReconciliationResult.RestoredPreserveDebris
					: ReconciliationResult.RestoredCleanable;
			}

			if (!File.Exists(absoluteProfilePath) &&
				!Directory.Exists(absoluteProfilePath) &&
				(isBackupOriginal ||
				 await HasExactSafeFileBytesAsync(
					recoveryPath,
					parentPath,
					originalBytes,
					CancellationToken.None)))
			{
				string restoreSourcePath = isBackupOriginal
					? backupPath
					: recoveryPath;

				try
				{
					fileReplacer.Move(
						restoreSourcePath,
						absoluteProfilePath);
				}
				catch
				{
					// Final byte-and-ACL verification decides the result.
				}

				if (isBackupOriginal &&
					!File.Exists(absoluteProfilePath) &&
					!Directory.Exists(absoluteProfilePath) &&
					await HasExactSafeFileBytesAsync(
						recoveryPath,
						parentPath,
						originalBytes,
						CancellationToken.None))
				{
					try
					{
						fileReplacer.Move(
							recoveryPath,
							absoluteProfilePath);
					}
					catch
					{
						// Final byte-and-ACL verification decides the result.
					}
				}
			}

			if (!await HasExactSafeFileBytesAsync(
				absoluteProfilePath,
				parentPath,
				originalBytes,
				CancellationToken.None))
			{
				return ReconciliationResult.Unrecoverable;
			}

			bool canCleanFinalFailedNewBackup =
				await IsFailedNewBackupCleanableAsync(
					failedNewBackupPath,
					parentPath,
					updatedBytes);
			return shouldPreserveDebris || !canCleanFinalFailedNewBackup
				? ReconciliationResult.RestoredPreserveDebris
				: ReconciliationResult.RestoredCleanable;
		}
		catch
		{
			return ReconciliationResult.Unrecoverable;
		}
	}

	private static async Task<bool> IsFailedNewBackupCleanableAsync(
		string failedNewBackupPath,
		string parentPath,
		ReadOnlyMemory<byte> updatedBytes)
	{
		if (Directory.Exists(failedNewBackupPath))
		{
			return false;
		}

		if (!File.Exists(failedNewBackupPath))
		{
			return true;
		}

		return await HasExactSafeFileBytesAsync(
			failedNewBackupPath,
			parentPath,
			updatedBytes,
			CancellationToken.None);
	}

	private static bool TryFindPinTokenRanges(
		ReadOnlySpan<byte> bytes,
		PinTarget pinTarget,
		out JsonTokenRange structuralRange,
		out JsonTokenRange exactRange)
	{
		structuralRange = default;
		exactRange = default;

		try
		{
			(string structuralPropertyName, string exactPropertyName) =
				GetPinPropertyNames(pinTarget);
			JsonTokenType expectedTokenType = pinTarget switch
			{
				PinTarget.Prompt => JsonTokenType.String,
				PinTarget.Usage => JsonTokenType.Null,
				_ => JsonTokenType.None
			};
			int offset = bytes.StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
			Utf8JsonReader reader = new(bytes[offset..], true, default);
			string? pendingPropertyName = null;

			while (reader.Read())
			{
				if ((reader.TokenType == JsonTokenType.PropertyName) &&
					(reader.CurrentDepth == 1))
				{
					pendingPropertyName = reader.GetString();
					continue;
				}

				if (pendingPropertyName is null)
				{
					continue;
				}

				if (string.Equals(
						pendingPropertyName,
						structuralPropertyName,
						StringComparison.Ordinal) ||
					string.Equals(
						pendingPropertyName,
						exactPropertyName,
						StringComparison.Ordinal))
				{
					if ((reader.TokenType != expectedTokenType) ||
						(reader.CurrentDepth != 1))
					{
						return false;
					}

					JsonTokenRange range = new(
						checked(offset + (int)reader.TokenStartIndex),
						checked((int)(reader.BytesConsumed -
							reader.TokenStartIndex)));

					if (string.Equals(
						pendingPropertyName,
						structuralPropertyName,
						StringComparison.Ordinal))
					{
						structuralRange = range;
					}
					else
					{
						exactRange = range;
					}
				}

				pendingPropertyName = null;
			}

			return (structuralRange.Length > 0) &&
				(exactRange.Length > 0) &&
				((structuralRange.Start + structuralRange.Length <=
					exactRange.Start) ||
				 (exactRange.Start + exactRange.Length <=
					structuralRange.Start));
		}
		catch
		{
			return false;
		}
	}

	private static HashSet<SecurityIdentifier> GetAllowedIdentities()
	{
		using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
		SecurityIdentifier currentSid = currentIdentity.User ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");
		return new HashSet<SecurityIdentifier>
		{
			currentSid,
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
			new SecurityIdentifier(
				WellKnownSidType.BuiltinAdministratorsSid,
				null)
		};
	}

	private static bool HasExpectedUpdate(
		AntigravityLiveR0ProfileFile originalProfile,
		AntigravityLiveR0ProfileFile candidateProfile,
		PinTarget pinTarget,
		string structuralFingerprint,
		string exactFingerprint)
	{
		(string? candidateStructuralFingerprint,
			string? candidateExactFingerprint) = GetPins(
			candidateProfile,
			pinTarget);
		return ArePinsEqual(
				candidateStructuralFingerprint,
				candidateExactFingerprint,
				structuralFingerprint,
				exactFingerprint) &&
			HasSameNonTargetFields(
				originalProfile,
				candidateProfile,
				pinTarget);
	}

	private static bool HasSafeInheritedProfileAcl(string absoluteFilePath)
	{
		try
		{
			FileInfo file = new(absoluteFilePath);
			FileSecurity security = file.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);

			if (security.AreAccessRulesProtected ||
				(security.GetOwner(typeof(SecurityIdentifier)) is not
					SecurityIdentifier owner) ||
				!AntigravityPrivateKeyAcl.IsAllowedOwner(owner))
			{
				return false;
			}

			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();
			HashSet<SecurityIdentifier> observedIdentities = new();

			foreach (FileSystemAccessRule rule in security.GetAccessRules(
				true,
				true,
				typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
			{
				if (!rule.IsInherited ||
					(rule.AccessControlType != AccessControlType.Allow) ||
					(rule.IdentityReference is not SecurityIdentifier identity) ||
					!allowedIdentities.Contains(identity) ||
					((rule.FileSystemRights & FileSystemRights.FullControl) !=
						FileSystemRights.FullControl) ||
					(rule.InheritanceFlags != InheritanceFlags.None) ||
					(rule.PropagationFlags != PropagationFlags.None) ||
					!observedIdentities.Add(identity))
				{
					return false;
				}
			}

			return allowedIdentities.SetEquals(observedIdentities);
		}
		catch
		{
			return false;
		}
	}

	private static bool HasSameNonTargetFields(
		AntigravityLiveR0ProfileFile first,
		AntigravityLiveR0ProfileFile second,
		PinTarget pinTarget)
	{
		return string.Equals(first.Id, second.Id, StringComparison.Ordinal) &&
			Equals(
				first.ExecutableFingerprint,
				second.ExecutableFingerprint) &&
			string.Equals(
				first.WorkingDirectory,
				second.WorkingDirectory,
				StringComparison.Ordinal) &&
			DictionaryEquals(first.Environment, second.Environment) &&
			(first.Columns == second.Columns) &&
			(first.Rows == second.Rows) &&
			((pinTarget == PinTarget.Prompt) ||
			 (string.Equals(
				first.ExpectedPromptStructuralFingerprint,
				second.ExpectedPromptStructuralFingerprint,
				StringComparison.Ordinal) &&
			  string.Equals(
				first.ExpectedExactPromptFingerprint,
				second.ExpectedExactPromptFingerprint,
				StringComparison.Ordinal))) &&
			string.Equals(
				first.ExactPromptFingerprintHmacKeyPath,
				second.ExactPromptFingerprintHmacKeyPath,
				StringComparison.Ordinal) &&
			((pinTarget == PinTarget.Usage) ||
			 (string.Equals(
				first.ExpectedUsageStructuralFingerprint,
				second.ExpectedUsageStructuralFingerprint,
				StringComparison.Ordinal) &&
			  string.Equals(
				first.ExpectedExactUsageFingerprint,
				second.ExpectedExactUsageFingerprint,
				StringComparison.Ordinal))) &&
			first.NonCredentialSettingsFiles.SequenceEqual(
				second.NonCredentialSettingsFiles,
				StringComparer.Ordinal) &&
			(first.MaximumSavedOutputBytes == second.MaximumSavedOutputBytes) &&
			(first.PromptTimeout == second.PromptTimeout) &&
			(first.UsageTimeout == second.UsageTimeout) &&
			(first.StableScreenDuration == second.StableScreenDuration) &&
			(first.PollInterval == second.PollInterval) &&
			(first.CleanupTimeout == second.CleanupTimeout);
	}

	private static bool HasExactPropertySet(
		JsonElement element,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> actualPropertyNames = element
			.EnumerateObject()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);
		return actualPropertyNames.SetEquals(expectedPropertyNames);
	}

	private static bool HasExpectedJsonShape(JsonElement root)
	{
		return HasExactPropertySet(root, ProfilePropertyNames) &&
			root.TryGetProperty(
				"ExecutableFingerprint",
				out JsonElement executableFingerprint) &&
			HasExactPropertySet(
				executableFingerprint,
				ExecutableFingerprintPropertyNames);
	}

	private static bool HasUniqueJsonProperties(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				HashSet<string> names = new(StringComparer.Ordinal);

				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (!names.Add(property.Name) ||
						!HasUniqueJsonProperties(property.Value))
					{
						return false;
					}
				}

				return true;
			case JsonValueKind.Array:
				return element.EnumerateArray().All(HasUniqueJsonProperties);
			default:
				return true;
		}
	}

	private static bool IsHexFingerprint(string fingerprint)
	{
		return (fingerprint.Length == 64) &&
			fingerprint.All(character =>
				((character >= '0') && (character <= '9')) ||
				((character >= 'A') && (character <= 'F')) ||
				((character >= 'a') && (character <= 'f')));
	}

	private static bool IsSafePrivateDirectory(string absoluteDirectoryPath)
	{
		return IsNonReparseDirectoryChain(absoluteDirectoryPath) &&
			AntigravityPrivateKeyAcl.IsPrivateDirectory(
				absoluteDirectoryPath);
	}

	private static bool IsSafeProfileFile(
		string absoluteFilePath,
		string parentPath)
	{
		try
		{
			string fullPath = Path.GetFullPath(absoluteFilePath);
			string? actualParentPath = Path.GetDirectoryName(fullPath);
			FileInfo file = new(fullPath);
			file.Refresh();

			return (actualParentPath is not null) &&
				string.Equals(
					actualParentPath,
					parentPath,
					StringComparison.OrdinalIgnoreCase) &&
				IsSafePrivateDirectory(parentPath) &&
				file.Exists &&
				((file.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0) &&
				(AntigravityPrivateKeyAcl.IsPrivateFile(fullPath) ||
					HasSafeInheritedProfileAcl(fullPath));
		}
		catch
		{
			return false;
		}
	}

	private static bool IsNonReparseDirectoryChain(
		string absoluteDirectoryPath)
	{
		try
		{
			DirectoryInfo? directory = new(absoluteDirectoryPath);

			while (directory is not null)
			{
				directory.Refresh();

				if (!directory.Exists ||
					((directory.Attributes &
						(FileAttributes.Directory |
							FileAttributes.ReparsePoint)) !=
						FileAttributes.Directory))
				{
					return false;
				}

				directory = directory.Parent;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsWellFormedProfile(
		AntigravityLiveR0ProfileFile profile)
	{
		AntigravityCliFingerprint? executableFingerprint =
			profile.ExecutableFingerprint;
		bool hasUsageStructuralFingerprint =
			profile.ExpectedUsageStructuralFingerprint is not null;
		bool hasExactUsageFingerprint =
			profile.ExpectedExactUsageFingerprint is not null;

		return !string.IsNullOrWhiteSpace(profile.Id) &&
			(executableFingerprint is not null) &&
			IsAbsoluteRegularLookingPath(
				executableFingerprint.AbsolutePath,
				".exe") &&
			!string.IsNullOrWhiteSpace(executableFingerprint.CliVersion) &&
			!string.IsNullOrWhiteSpace(executableFingerprint.FileVersion) &&
			!string.IsNullOrWhiteSpace(executableFingerprint.ProductVersion) &&
			IsHexFingerprint(executableFingerprint.Sha256) &&
			(executableFingerprint.WinVerifyTrustStatus == 0) &&
			!string.IsNullOrWhiteSpace(executableFingerprint.SignerSubject) &&
			IsHexValue(executableFingerprint.SignerThumbprint, 40) &&
			IsAbsoluteRegularLookingPath(profile.WorkingDirectory, null) &&
			(profile.Environment is not null) &&
			profile.Environment.All(pair =>
				!string.IsNullOrWhiteSpace(pair.Key) &&
				!pair.Key.Contains('=') &&
				!pair.Key.Contains('\0') &&
				(pair.Value is not null) &&
				!pair.Value.Contains('\0')) &&
			(profile.Columns > 0) &&
			(profile.Rows > 0) &&
			(profile.ExpectedPromptStructuralFingerprint is not null) &&
			((profile.ExpectedPromptStructuralFingerprint.Length == 0) ||
				IsHexFingerprint(
					profile.ExpectedPromptStructuralFingerprint)) &&
			IsAbsoluteRegularLookingPath(
				profile.ExactPromptFingerprintHmacKeyPath,
				".key") &&
			(profile.ExpectedExactPromptFingerprint is not null) &&
			((profile.ExpectedExactPromptFingerprint.Length == 0) ||
				IsHexFingerprint(profile.ExpectedExactPromptFingerprint)) &&
			(hasUsageStructuralFingerprint == hasExactUsageFingerprint) &&
			(!hasUsageStructuralFingerprint ||
				(IsHexFingerprint(
						profile.ExpectedUsageStructuralFingerprint!) &&
				 IsHexFingerprint(profile.ExpectedExactUsageFingerprint!) &&
				 !string.Equals(
					profile.ExpectedExactPromptFingerprint,
					profile.ExpectedExactUsageFingerprint,
					StringComparison.Ordinal))) &&
			(profile.NonCredentialSettingsFiles is not null) &&
			(profile.NonCredentialSettingsFiles.Count == 1) &&
			profile.NonCredentialSettingsFiles.All(path =>
				IsAbsoluteRegularLookingPath(path, null)) &&
			(profile.MaximumSavedOutputBytes > 0) &&
			(profile.PromptTimeout > TimeSpan.Zero) &&
			(profile.UsageTimeout > TimeSpan.Zero) &&
			(profile.StableScreenDuration > TimeSpan.Zero) &&
			(profile.StableScreenDuration <= profile.PromptTimeout) &&
			(profile.StableScreenDuration <= profile.UsageTimeout) &&
			(profile.PollInterval > TimeSpan.Zero) &&
			(profile.PollInterval <= profile.StableScreenDuration) &&
			(profile.CleanupTimeout > TimeSpan.Zero);
	}

	private static bool IsAbsoluteRegularLookingPath(
		string? path,
		string? requiredExtension)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path) ||
				!Path.IsPathFullyQualified(path))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(path);
			string fileName = Path.GetFileName(fullPath);
			return (fileName.Length != 0) &&
				((requiredExtension is null) ||
				 string.Equals(
					Path.GetExtension(fullPath),
					requiredExtension,
					StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	private static bool IsHexValue(string? value, int expectedLength)
	{
		return (value?.Length == expectedLength) &&
			value.All(character =>
				((character >= '0') && (character <= '9')) ||
				((character >= 'A') && (character <= 'F')) ||
				((character >= 'a') && (character <= 'f')));
	}

	private static async Task<byte[]> ReadExactFileAsync(
		string absoluteFilePath,
		ProfileFileState expectedState,
		CancellationToken cancellationToken)
	{
		if ((expectedState.Length < 0) ||
			(expectedState.Length > MaximumProfileBytes))
		{
			throw new InvalidDataException(
				"The live R0 profile length is invalid.");
		}

		byte[] bytes = new byte[checked((int)expectedState.Length)];
		byte[] trailingByte = new byte[1];

		await using FileStream stream = new(
			absoluteFilePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		if (stream.Length != expectedState.Length)
		{
			throw new IOException("The live R0 profile changed while read.");
		}

		await stream.ReadExactlyAsync(bytes, cancellationToken);
		int trailingByteCount = await stream.ReadAsync(
			trailingByte,
			cancellationToken);

		if (trailingByteCount != 0)
		{
			throw new IOException("The live R0 profile changed while read.");
		}

		return bytes;
	}

	private static bool TryCaptureProfileFileState(
		string absoluteFilePath,
		string parentPath,
		out ProfileFileState state)
	{
		state = default;

		try
		{
			if (!IsSafeProfileFile(absoluteFilePath, parentPath))
			{
				return false;
			}

			FileInfo file = new(absoluteFilePath);
			file.Refresh();

			if (!file.Exists ||
				(file.Length > MaximumProfileBytes) ||
				((file.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
			{
				return false;
			}

			state = new ProfileFileState(
				file.Length,
				file.CreationTimeUtc,
				file.LastWriteTimeUtc,
				file.Attributes);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void TryDeleteTemporaryFile(string temporaryPath)
	{
		try
		{
			string fullPath = Path.GetFullPath(temporaryPath);
			string? parentPath = Path.GetDirectoryName(fullPath);
			FileInfo file = new(fullPath);
			file.Refresh();

			if ((parentPath is null) ||
				!IsSafePrivateDirectory(parentPath) ||
				!file.Exists ||
				((file.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
			{
				return;
			}

			File.Delete(fullPath);
		}
		catch
		{
			// Best-effort cleanup must never mask the fail-closed result.
		}
	}

	private static bool TryDeserializeProfile(
		ReadOnlySpan<byte> bytes,
		out AntigravityLiveR0ProfileFile? profile)
	{
		profile = null;

		try
		{
			int offset = bytes.StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
			string json = StrictUtf8Encoding.GetString(bytes[offset..]);
			using JsonDocument document = JsonDocument.Parse(json);

			if (!HasUniqueJsonProperties(document.RootElement))
			{
				return false;
			}

			if (!HasExpectedJsonShape(document.RootElement))
			{
				return false;
			}

			profile = JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
				json,
				JsonOptions);
			return profile is not null;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryResolveSafeProfile(
		string profilePath,
		out string absoluteProfilePath,
		out string parentPath)
	{
		absoluteProfilePath = string.Empty;
		parentPath = string.Empty;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(profilePath) ||
				!string.Equals(
					Path.GetExtension(profilePath),
					".json",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			absoluteProfilePath = Path.GetFullPath(profilePath);
			parentPath = Path.GetDirectoryName(absoluteProfilePath) ??
				string.Empty;
			return (parentPath.Length != 0) &&
				IsSafeProfileFile(absoluteProfilePath, parentPath);
		}
		catch
		{
			return false;
		}
	}
}
