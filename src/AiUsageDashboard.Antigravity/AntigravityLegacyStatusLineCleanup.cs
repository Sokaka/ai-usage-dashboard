using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.AntigravitySpike;

internal static partial class AntigravityLegacyStatusLineCleanup
{
	internal const string OwnershipMarker =
		"--ai-usage-dashboard-agy-statusline-v1";
	internal const string CaptureFileName = "account-display-v1.json";
	internal const string CaptureLockFileName = ".account-display-v1.lock";
	private const int MaximumSettingsBytes = 1024 * 1024;
	private const int MaximumRollbackFingerprintBytes = 16 * 1024 * 1024;
	private const int MaximumRollbackReactivationAttempts = 8;
	private const int MaximumHelperBytes = 128 * 1024 * 1024;
	private const int MaximumNativePathCharacters = 32 * 1024;
	private const AccessControlSections SettingsSecuritySections =
		AccessControlSections.Access |
		AccessControlSections.Owner |
		AccessControlSections.Group;
	private const FileSystemRights CommittedSettingsRepairRights =
		FileSystemRights.ReadAttributes |
		FileSystemRights.ReadPermissions |
		FileSystemRights.ChangePermissions |
		FileSystemRights.Synchronize;
	private const FileSystemRights SettingsStateReadRights =
		FileSystemRights.ReadData |
		FileSystemRights.ReadAttributes |
		FileSystemRights.ReadPermissions |
		FileSystemRights.Synchronize;
	private static readonly TimeSpan MutationMutexWaitTimeout =
		TimeSpan.FromSeconds(3);
	private static readonly UTF8Encoding StrictUtf8 = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private static readonly object MutationLock = new();

	private enum SettingsReadStatus
	{
		Available,
		Missing,
		Failed
	}

	private enum StatusLineConfigurationKind
	{
		Missing,
		Owned,
		Custom
	}

	private sealed class CrossProcessMutationLease : IDisposable
	{
		private Mutex? _mutex;

		internal CrossProcessMutationLease(Mutex mutex)
		{
			_mutex = mutex;
		}

		public void Dispose()
		{
			Mutex? mutex = Interlocked.Exchange(ref _mutex, null);

			if (mutex is null)
			{
				return;
			}

			try
			{
				mutex.ReleaseMutex();
			}
			catch (ApplicationException)
			{
				// Losing a lease cannot broaden the cleanup target.
			}
			finally
			{
				mutex.Dispose();
			}
		}
	}

	internal static bool TryRemoveOwnedArtifacts()
	{
		try
		{
			string? settingsPath = TryGetDefaultSettingsPath();
			string? privateRoot = TryGetDefaultPrivateRoot();

			return settingsPath is not null &&
				privateRoot is not null &&
				TryRemoveOwnedArtifacts(settingsPath, privateRoot);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryRemoveOwnedArtifacts(
		string settingsPath,
		string privateRoot,
		Action? beforeArtifactCleanup = null)
	{
		if (!OperatingSystem.IsWindows() ||
			!Path.IsPathFullyQualified(settingsPath) ||
			!Path.IsPathFullyQualified(privateRoot))
		{
			return false;
		}

		lock (MutationLock)
		{
			using CrossProcessMutationLease? mutationLease =
				TryAcquireCrossProcessMutationLease(settingsPath);

			if (mutationLease is null)
			{
				return false;
			}

			SettingsReadStatus readStatus = TryReadSettings(
				settingsPath,
				out byte[]? originalBytes);

			if (readStatus == SettingsReadStatus.Failed)
			{
				return false;
			}

			if (readStatus == SettingsReadStatus.Missing)
			{
				return IsPathAbsent(privateRoot);
			}

			string? configuredOwnedHelperPath = null;

			try
			{
				if (readStatus == SettingsReadStatus.Available)
				{
					if (originalBytes is null ||
						!TryInspectStatusLineConfiguration(
							originalBytes,
							privateRoot,
							out StatusLineConfigurationKind configurationKind,
							out configuredOwnedHelperPath))
					{
						return false;
					}

					if (configurationKind == StatusLineConfigurationKind.Custom)
					{
						return true;
					}

					if (configurationKind == StatusLineConfigurationKind.Owned)
					{
						if (!TryPrepareConfiguredHelperForRemoval(
								configuredOwnedHelperPath,
								privateRoot) ||
							!TryCreateSettingsWithoutOwnedStatusLine(
								originalBytes,
								privateRoot,
								out byte[]? updatedBytes) ||
							updatedBytes is null)
						{
							return false;
						}

						try
						{
							if (!TryReplaceSettingsAtomically(
									settingsPath,
									originalBytes,
									updatedBytes))
							{
								return false;
							}
						}
						finally
						{
							CryptographicOperations.ZeroMemory(updatedBytes);
						}
					}
				}

				beforeArtifactCleanup?.Invoke();
				return TryDeleteOwnedArtifacts(
					settingsPath,
					privateRoot,
					configuredOwnedHelperPath);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (originalBytes is not null)
				{
					CryptographicOperations.ZeroMemory(originalBytes);
				}
			}
		}
	}

	internal static bool TryCreateSettingsWithoutOwnedStatusLine(
		ReadOnlyMemory<byte> originalBytes,
		string privateRoot,
		out byte[]? updatedBytes)
	{
		updatedBytes = null;

		try
		{
			if (!TryInspectStatusLineConfiguration(
					originalBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out _) ||
				configurationKind != StatusLineConfigurationKind.Owned)
			{
				return false;
			}

			using JsonDocument document = JsonDocument.Parse(
				originalBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});
			using MemoryStream output = new();

			using (Utf8JsonWriter writer = new(
				output,
				new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();

				foreach (JsonProperty property in
					document.RootElement.EnumerateObject())
				{
					if (!property.NameEquals("statusLine"))
					{
						property.WriteTo(writer);
					}
				}

				writer.WriteEndObject();
			}

			updatedBytes = output.ToArray();
			return updatedBytes.Length <= MaximumSettingsBytes;
		}
		catch
		{
			updatedBytes = null;
			return false;
		}
	}

	private static CrossProcessMutationLease?
		TryAcquireCrossProcessMutationLease(string settingsPath)
	{
		Mutex? mutex = null;
		bool acquired = false;

		try
		{
			string normalizedSettingsPath = Path
				.GetFullPath(settingsPath)
				.ToUpperInvariant();
			byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedSettingsPath);
			string pathHash;

			try
			{
				pathHash = Convert.ToHexString(SHA256.HashData(pathBytes));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(pathBytes);
			}

			mutex = new Mutex(
				initiallyOwned: false,
				name:
					$"Local\\AiUsageDashboard.AntigravityStatusLine.{pathHash}");

			try
			{
				acquired = mutex.WaitOne(MutationMutexWaitTimeout);
			}
			catch (AbandonedMutexException)
			{
				acquired = true;
			}

			return acquired ? new CrossProcessMutationLease(mutex) : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (!acquired)
			{
				mutex?.Dispose();
			}
		}
	}

	private static bool TryInspectStatusLineConfiguration(
		ReadOnlyMemory<byte> settingsBytes,
		string privateRoot,
		out StatusLineConfigurationKind configurationKind,
		out string? ownedHelperPath)
	{
		configurationKind = StatusLineConfigurationKind.Custom;
		ownedHelperPath = null;

		try
		{
			_ = StrictUtf8.GetString(settingsBytes.Span);
			using JsonDocument document = JsonDocument.Parse(
				settingsBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});

			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!HasUniqueProperties(document.RootElement))
			{
				return false;
			}

			if (!document.RootElement.TryGetProperty(
					"statusLine",
					out JsonElement statusLine))
			{
				configurationKind = StatusLineConfigurationKind.Missing;
				return true;
			}

			if (statusLine.ValueKind != JsonValueKind.Object ||
				!statusLine.TryGetProperty("type", out JsonElement type) ||
				type.ValueKind != JsonValueKind.String ||
				!string.Equals(type.GetString(), "command", StringComparison.Ordinal) ||
				!statusLine.TryGetProperty("command", out JsonElement command) ||
				command.ValueKind != JsonValueKind.String ||
				!TryGetOwnedHelperPath(
					command.GetString(),
					privateRoot,
					out ownedHelperPath))
			{
				configurationKind = StatusLineConfigurationKind.Custom;
				return true;
			}

			if (!TryGetStatusLineEnabled(statusLine, out _))
			{
				return false;
			}

			configurationKind = StatusLineConfigurationKind.Owned;
			return true;
		}
		catch
		{
			configurationKind = StatusLineConfigurationKind.Custom;
			ownedHelperPath = null;
			return false;
		}
	}

	private static bool TryGetStatusLineEnabled(
		JsonElement statusLine,
		out bool isEnabled)
	{
		isEnabled = true;

		if (!statusLine.TryGetProperty("enabled", out JsonElement enabled))
		{
			return true;
		}

		if (enabled.ValueKind == JsonValueKind.True)
		{
			return true;
		}

		if (enabled.ValueKind == JsonValueKind.False)
		{
			isEnabled = false;
			return true;
		}

		return false;
	}

	private static bool HasUniqueProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> names = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!names.Add(property.Name) ||
					!HasUniqueProperties(property.Value))
				{
					return false;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement child in element.EnumerateArray())
			{
				if (!HasUniqueProperties(child))
				{
					return false;
				}
			}
		}

		return true;
	}

	private static bool TryGetOwnedHelperPath(
		string? command,
		string privateRoot,
		out string? helperPath)
	{
		helperPath = null;

		if (string.IsNullOrEmpty(command) ||
			!command.EndsWith($" {OwnershipMarker}", StringComparison.Ordinal))
		{
			return false;
		}

		string commandPath = command[..^(OwnershipMarker.Length + 1)];
		string candidate;
		bool isQuotedPath;

		if (commandPath.StartsWith("call \"", StringComparison.Ordinal) &&
			commandPath.EndsWith('"'))
		{
			candidate = commandPath[6..^1];
			isQuotedPath = true;

			if (candidate.Length == 0 || candidate.Contains('"'))
			{
				return false;
			}
		}
		else if (commandPath.Length >= 2 &&
			commandPath[0] == '"' &&
			commandPath[^1] == '"')
		{
			candidate = commandPath[1..^1];
			isQuotedPath = true;

			if (candidate.Contains('"'))
			{
				return false;
			}
		}
		else
		{
			candidate = commandPath;
			isQuotedPath = false;
		}

		if (isQuotedPath
			? !IsSafeQuotedCommandPath(candidate)
			: !IsSafeUnquotedCommandPath(candidate))
		{
			return false;
		}

		try
		{
			string fullCandidate = Path.GetFullPath(candidate);
			string fullRoot = Path.GetFullPath(privateRoot);
			string? candidateDirectory = Path.GetDirectoryName(fullCandidate);

			if (!string.Equals(
					candidateDirectory,
					fullRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				if (!TryGetVerifiedLongPath(
						fullCandidate,
						out string? expandedCandidate) ||
					expandedCandidate is null)
				{
					return false;
				}

				fullCandidate = expandedCandidate;
				candidateDirectory = Path.GetDirectoryName(fullCandidate);
			}

			if (!string.Equals(
					candidateDirectory,
					fullRoot,
					StringComparison.OrdinalIgnoreCase) ||
				!OwnedHelperFileNameRegex().IsMatch(
					Path.GetFileName(fullCandidate)))
			{
				return false;
			}

			helperPath = fullCandidate;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsSafeQuotedCommandPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		foreach (char character in path)
		{
			if (char.IsControl(character) ||
				character is '"' or '%' or '!' or '$' or '`' or '&' or '|' or
					'<' or '>' or '^' or ';')
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsSafeUnquotedCommandPath(string path)
	{
		return IsSafeQuotedCommandPath(path) &&
			!path.Any(character =>
				char.IsWhiteSpace(character) ||
				character is '(' or ')' or ',' or '=');
	}

	private static bool TryPrepareConfiguredHelperForRemoval(
		string? configuredOwnedHelperPath,
		string privateRoot)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(configuredOwnedHelperPath))
			{
				return false;
			}

			string fullRoot = Path.GetFullPath(privateRoot);
			string fullHelperPath = Path.GetFullPath(configuredOwnedHelperPath);

			if (!string.Equals(
					Path.GetDirectoryName(fullHelperPath),
					fullRoot,
					StringComparison.OrdinalIgnoreCase) ||
				!OwnedHelperFileNameRegex().IsMatch(
					Path.GetFileName(fullHelperPath)))
			{
				return false;
			}

			if (IsPathAbsent(fullHelperPath))
			{
				return true;
			}

			if (!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					fullHelperPath))
			{
				return false;
			}

			return AntigravityPrivateKeyAcl.IsPrivateFile(fullHelperPath) ||
				AntigravityPrivateKeyAcl.TryProtectNewFile(fullHelperPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryDeleteOwnedArtifacts(
		string settingsPath,
		string privateRoot,
		string? configuredOwnedHelperPath)
	{
		try
		{
			if (!TryVerifyStatusLineIsAbsent(settingsPath, privateRoot))
			{
				return false;
			}

			string fullRoot = Path.GetFullPath(privateRoot);

			if (IsPathAbsent(fullRoot))
			{
				return true;
			}

			if (!AntigravityPrivateKeyAcl.IsPrivateDirectory(fullRoot))
			{
				return false;
			}

			bool cleanupSucceeded = true;
			cleanupSucceeded &= TryDeletePrivateOwnedFile(
				Path.Combine(fullRoot, CaptureFileName),
				fullRoot);
			cleanupSucceeded &= TryDeletePrivateOwnedFile(
				Path.Combine(fullRoot, CaptureLockFileName),
				fullRoot);

			if (!string.IsNullOrWhiteSpace(configuredOwnedHelperPath))
			{
				cleanupSucceeded &= TryDeletePrivateOwnedFile(
					configuredOwnedHelperPath,
					fullRoot);
			}

			foreach (string helperPath in Directory.EnumerateFiles(
				fullRoot,
				"AiUsageDashboard.AntigravityCapture-*.exe",
				SearchOption.TopDirectoryOnly))
			{
				if (!TryPrepareEnumeratedHelperForRemoval(
						helperPath,
						fullRoot,
						out bool shouldDelete))
				{
					cleanupSucceeded = false;
					continue;
				}

				if (shouldDelete)
				{
					cleanupSucceeded &= TryDeletePrivateOwnedFile(
						helperPath,
						fullRoot);
				}
			}

			bool rootIsEmpty = !Directory.EnumerateFileSystemEntries(fullRoot).Any();
			AntigravityPrivateKeyAcl.TryDeleteEmptyCreatedDirectory(fullRoot);

			return cleanupSucceeded &&
				(!rootIsEmpty || IsPathAbsent(fullRoot));
		}
		catch
		{
			return false;
		}
	}

	private static bool TryVerifyStatusLineIsAbsent(
		string settingsPath,
		string privateRoot)
	{
		SettingsReadStatus readStatus = TryReadSettings(
			settingsPath,
			out byte[]? settingsBytes);

		if (readStatus == SettingsReadStatus.Missing)
		{
			return false;
		}

		if (readStatus != SettingsReadStatus.Available || settingsBytes is null)
		{
			return false;
		}

		try
		{
			return TryInspectStatusLineConfiguration(
					settingsBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out _) &&
				configurationKind == StatusLineConfigurationKind.Missing;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(settingsBytes);
		}
	}

	private static bool TryPrepareEnumeratedHelperForRemoval(
		string helperPath,
		string privateRoot,
		out bool shouldDelete)
	{
		shouldDelete = false;

		try
		{
			string fullHelperPath = Path.GetFullPath(helperPath);

			if (!string.Equals(
					Path.GetDirectoryName(fullHelperPath),
					privateRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			Match fileNameMatch = OwnedHelperFileNameRegex().Match(
				Path.GetFileName(fullHelperPath));

			if (!fileNameMatch.Success)
			{
				return true;
			}

			if (!TryGetHelperHashPrefix(
					fullHelperPath,
					out string? actualHashPrefix) ||
				!string.Equals(
					actualHashPrefix,
					fileNameMatch.Groups["hash"].Value,
					StringComparison.Ordinal))
			{
				return true;
			}

			if (AntigravityPrivateKeyAcl.IsPrivateFile(fullHelperPath))
			{
				shouldDelete = true;
				return true;
			}

			if (!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					fullHelperPath) ||
				!AntigravityPrivateKeyAcl.TryProtectNewFile(fullHelperPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(fullHelperPath))
			{
				return false;
			}

			shouldDelete = true;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetHelperHashPrefix(
		string helperPath,
		out string? hashPrefix)
	{
		hashPrefix = null;

		try
		{
			using FileStream stream = new(
				helperPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 81920,
				FileOptions.SequentialScan);

			if ((stream.Length <= 0) || (stream.Length > MaximumHelperBytes))
			{
				return false;
			}

			byte[] hash = SHA256.HashData(stream);

			try
			{
				hashPrefix = Convert.ToHexString(hash)
					.ToLowerInvariant()[..16];
				return true;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(hash);
			}
		}
		catch
		{
			return false;
		}
	}

	private static bool TryDeletePrivateOwnedFile(
		string path,
		string expectedDirectory)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);

			if (!string.Equals(
					Path.GetDirectoryName(fullPath),
					expectedDirectory,
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (IsPathAbsent(fullPath))
			{
				return true;
			}

			return AntigravityPrivateKeyAcl.IsPrivateFile(fullPath) &&
				TryDeleteFile(fullPath);
		}
		catch
		{
			return false;
		}
	}

	private static SettingsReadStatus TryReadSettings(
		string settingsPath,
		out byte[]? bytes)
	{
		bytes = null;

		try
		{
			if (!Path.IsPathFullyQualified(settingsPath))
			{
				return SettingsReadStatus.Failed;
			}

			if (IsPathAbsent(settingsPath))
			{
				return SettingsReadStatus.Missing;
			}

			if ((File.GetAttributes(settingsPath) &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return SettingsReadStatus.Failed;
			}

			using FileStream stream = new(
				settingsPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);

			if ((stream.Length <= 0) || (stream.Length > MaximumSettingsBytes))
			{
				return SettingsReadStatus.Failed;
			}

			bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			_ = StrictUtf8.GetString(bytes);
			return SettingsReadStatus.Available;
		}
		catch
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}

			bytes = null;
			return SettingsReadStatus.Failed;
		}
	}

	internal static bool TryReplaceSettingsAtomically(
		string settingsPath,
		ReadOnlySpan<byte> expectedBytes,
		ReadOnlySpan<byte> updatedBytes,
		Action? beforeCommittedSecurityValidation = null,
		Action<string>? beforeBackupDeletion = null,
		Action? afterReplacementBeforeCommitAcknowledged = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);

		if (directory is null)
		{
			return false;
		}

		string temporaryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.tmp");
		string backupPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.bak");
		bool mayDeleteBackup = false;
		bool preserveBackup = false;
		bool replacementAttempted = false;
		bool replacementCommitted = false;
		byte[]? displacedBytes = null;
		string? displacedSddl = null;

		try
		{
			FileInfo settingsFile = new(settingsPath);
			FileSecurity originalSecurity = settingsFile.GetAccessControl(
				SettingsSecuritySections);
			string originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(
				SettingsSecuritySections);

			using (FileStream output = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.WriteThrough))
			{
				output.Write(updatedBytes);
				output.Flush(flushToDisk: true);
			}

			if (!TryApplyReplacementSettingsSecurity(
					temporaryPath,
					originalSddl))
			{
				return false;
			}

			if (!TryFileContentMatches(settingsPath, expectedBytes))
			{
				return false;
			}

			replacementAttempted = true;
			File.Replace(
				temporaryPath,
				settingsPath,
				backupPath,
				ignoreMetadataErrors: false);
			afterReplacementBeforeCommitAcknowledged?.Invoke();
			replacementCommitted = true;
			if (!TryCaptureSettingsFileState(
					backupPath,
					out displacedBytes,
					out displacedSddl) ||
				displacedBytes is null ||
				displacedSddl is null)
			{
				// Preserve recovery when the displaced version cannot be proven
				// safe to delete or restore.
				if (TryRestoreBackupPreservingRecoveryIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath))
				{
					replacementCommitted = false;
				}
				preserveBackup = true;
				return false;
			}

			if (!CryptographicOperations.FixedTimeEquals(
					displacedBytes,
					expectedBytes))
			{
				// The backup is the exact version displaced by File.Replace. A
				// mismatch proves that an external writer saved after our read.
				if (TryRestoreBackupIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath,
						displacedBytes,
						displacedSddl,
						displacedSddl))
				{
					replacementCommitted = false;
				}
				else
				{
					preserveBackup = true;
				}

				return false;
			}

			beforeCommittedSecurityValidation?.Invoke();
			if (!TryValidateAndRepairCommittedSettingsSecurity(
					settingsPath,
					updatedBytes,
					displacedSddl))
			{
				if (TryRestoreBackupIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath,
						displacedBytes,
						displacedSddl,
						displacedSddl))
				{
					replacementCommitted = false;
				}
				else
				{
					// Never overwrite an external save after the target stops matching
					// this operation; keep the displaced backup recoverable.
					preserveBackup = true;
				}

				return false;
			}

			beforeBackupDeletion?.Invoke(backupPath);
			if (!TryDeleteFile(backupPath))
			{
				preserveBackup = true;
				return false;
			}

			mayDeleteBackup = true;
			return true;
		}
		catch
		{
			if (replacementAttempted && File.Exists(backupPath))
			{
				bool restored = (!File.Exists(settingsPath) &&
					TryRestoreMissingTarget(backupPath, settingsPath)) ||
					(displacedBytes is not null && displacedSddl is not null
						? TryRestoreBackupIfTargetMatches(
							settingsPath,
							updatedBytes,
							backupPath,
							displacedBytes,
							displacedSddl,
							displacedSddl)
						: TryRestoreBackupIfTargetMatches(
							settingsPath,
							updatedBytes,
							backupPath));
				if (restored)
				{
					replacementCommitted = false;
				}
				else
				{
					preserveBackup = true;
				}
			}

			return false;
		}
		finally
		{
			if (displacedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(displacedBytes);
			}

			TryDeleteFile(temporaryPath);

			if (!preserveBackup && (mayDeleteBackup || !replacementCommitted))
			{
				TryDeleteFile(backupPath);
			}
		}
	}

	internal static bool TryValidateAndRepairCommittedSettingsSecurity(
		string settingsPath,
		ReadOnlySpan<byte> expectedBytes,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl = null)
	{
		try
		{
			if (!Path.IsPathFullyQualified(settingsPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!HasCompleteSettingsSecurityDescriptor(originalSddl) ||
				!File.Exists(settingsPath) ||
				(File.GetAttributes(settingsPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			// Pin the committed file while validating or repairing its DACL.
			using FileStream committedFile = new FileInfo(settingsPath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);

			if (!TryStreamContentMatches(committedFile, expectedBytes))
			{
				return false;
			}

			if (!TryValidateAndRepairSettingsSecurityDescriptor(
					settingsPath,
					committedFile,
					originalSddl,
					authoritativeRepairPrerequisiteSddl))
			{
				return false;
			}

			return TryStreamContentMatches(committedFile, expectedBytes);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryValidateAndRepairCommittedSettingsSecurityOnly(
		string settingsPath,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl,
		long expectedLength,
		ReadOnlySpan<byte> expectedHash)
	{
		try
		{
			if (!Path.IsPathFullyQualified(settingsPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!HasCompleteSettingsSecurityDescriptor(originalSddl) ||
				!File.Exists(settingsPath) ||
				(File.GetAttributes(settingsPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream committedFile = new FileInfo(settingsPath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.None,
				fileSecurity: null);
			if (!TryStreamFingerprintMatches(
					committedFile,
					expectedLength,
					expectedHash) ||
				!TryValidateAndRepairSettingsSecurityDescriptor(
					settingsPath,
					committedFile,
					originalSddl,
					authoritativeRepairPrerequisiteSddl))
			{
				return false;
			}

			return TryStreamFingerprintMatches(
				committedFile,
				expectedLength,
				expectedHash);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryValidateAndRepairSettingsSecurityDescriptor(
		string settingsPath,
		FileStream committedFile,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl)
	{
		string committedSddl = committedFile
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);

		if (SettingsSecurityMatchesAfterReplacement(
				originalSddl,
				committedSddl))
		{
			return true;
		}

		bool canRepairLegacyExpansion = SettingsSecurityCanRepairLegacyExpansion(
			originalSddl,
			committedSddl);
		bool canRepairAuthoritatively =
			authoritativeRepairPrerequisiteSddl is not null &&
			(SettingsSecurityMatchesAfterReplacement(
				authoritativeRepairPrerequisiteSddl,
				committedSddl) ||
			SettingsSecurityCanRepairLegacyExpansion(
				authoritativeRepairPrerequisiteSddl,
				committedSddl));
		if (!SettingsSecurityIdentityMatches(originalSddl, committedSddl) ||
			(!canRepairLegacyExpansion && !canRepairAuthoritatively))
		{
			return false;
		}

		using FileStream securityRepair = new FileInfo(settingsPath).Create(
			FileMode.Open,
			CommittedSettingsRepairRights,
			FileShare.Read,
			4096,
			FileOptions.None,
			fileSecurity: null);
		string repairSddl = securityRepair
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
		if (!string.Equals(
				committedSddl,
				repairSddl,
				StringComparison.Ordinal))
		{
			return false;
		}

		FileSecurity replacementSecurity = new();
		replacementSecurity.SetSecurityDescriptorSddlForm(
			originalSddl,
			AccessControlSections.Access);
		securityRepair.SetAccessControl(replacementSecurity);

		committedSddl = committedFile
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
		return SettingsSecurityMatchesAfterReplacement(
			originalSddl,
			committedSddl);
	}

	private static bool HasCompleteSettingsSecurityDescriptor(string sddl)
	{
		try
		{
			RawSecurityDescriptor descriptor = new(sddl);
			return descriptor.Owner is not null &&
				descriptor.Group is not null &&
				descriptor.DiscretionaryAcl is not null;
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryApplyReplacementSettingsSecurity(
		string temporaryPath,
		string originalSddl)
	{
		try
		{
			if (!Path.IsPathFullyQualified(temporaryPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!File.Exists(temporaryPath) ||
				(File.GetAttributes(temporaryPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			FileInfo temporaryFile = new(temporaryPath);
			string currentSddl = temporaryFile
				.GetAccessControl(SettingsSecuritySections)
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);

			if (!SettingsSecurityIdentityMatches(originalSddl, currentSddl))
			{
				return false;
			}

			if (string.Equals(
					originalSddl,
					currentSddl,
					StringComparison.Ordinal))
			{
				return true;
			}

			FileSecurity replacementSecurity = new();
			replacementSecurity.SetSecurityDescriptorSddlForm(
				originalSddl,
				AccessControlSections.Access);
			temporaryFile.SetAccessControl(replacementSecurity);

			string preparedSddl = temporaryFile
				.GetAccessControl(SettingsSecuritySections)
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			return SettingsSecurityIdentityMatches(originalSddl, preparedSddl) &&
				string.Equals(
					originalSddl,
					preparedSddl,
					StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	internal static bool SettingsSecurityMatchesAfterReplacement(
		string originalSddl,
		string committedSddl)
	{
		if (string.Equals(
				originalSddl,
				committedSddl,
				StringComparison.Ordinal))
		{
			return true;
		}

		try
		{
			RawSecurityDescriptor original = new(originalSddl);
			RawSecurityDescriptor committed = new(committedSddl);

			if (!SecurityDescriptorIdentityMatches(original, committed) ||
				original.DiscretionaryAcl is not RawAcl originalDacl ||
				committed.DiscretionaryAcl is not RawAcl committedDacl ||
				!HasInheritedAce(originalDacl))
			{
				return false;
			}

			const ControlFlags AllowedNormalization =
				ControlFlags.DiscretionaryAclAutoInherited;
			ControlFlags originalFlags = original.ControlFlags;
			ControlFlags committedFlags = committed.ControlFlags;

			if ((originalFlags & (
					AllowedNormalization |
					ControlFlags.DiscretionaryAclProtected)) != 0 ||
				committedFlags != (originalFlags | AllowedNormalization))
			{
				return false;
			}

			return RawAclBinaryEquals(originalDacl, committedDacl);
		}
		catch
		{
			return false;
		}
	}

	internal static bool SettingsSecurityCanRepairLegacyExpansion(
		string originalSddl,
		string committedSddl)
	{
		try
		{
			RawSecurityDescriptor original = new(originalSddl);
			RawSecurityDescriptor committed = new(committedSddl);
			if (!SecurityDescriptorIdentityMatches(original, committed) ||
				original.DiscretionaryAcl is not RawAcl originalDacl ||
				committed.DiscretionaryAcl is not RawAcl committedDacl ||
				originalDacl.Count == 0 ||
				originalDacl.Revision != committedDacl.Revision ||
				committedDacl.Count != checked(originalDacl.Count * 2))
			{
				return false;
			}

			const ControlFlags AllowedNormalization =
				ControlFlags.DiscretionaryAclAutoInherited;
			ControlFlags originalFlags = original.ControlFlags;
			if ((originalFlags & (
					AllowedNormalization |
					ControlFlags.DiscretionaryAclProtected)) != 0 ||
				committed.ControlFlags !=
					(originalFlags | AllowedNormalization))
			{
				return false;
			}

			for (int index = 0; index < originalDacl.Count; index++)
			{
				if ((originalDacl[index].AceFlags & AceFlags.Inherited) == 0)
				{
					return false;
				}
			}

			Dictionary<string, int> expectedAceCounts = new(
				StringComparer.Ordinal);
			for (int index = 0; index < originalDacl.Count; index++)
			{
				string key = GetAceKeyIgnoringInherited(originalDacl[index]);
				expectedAceCounts.TryGetValue(key, out int count);
				expectedAceCounts[key] = checked(count + 2);
			}

			for (int index = 0; index < committedDacl.Count; index++)
			{
				string key = GetAceKeyIgnoringInherited(committedDacl[index]);
				if (!expectedAceCounts.TryGetValue(key, out int count) ||
					count == 0)
				{
					return false;
				}

				expectedAceCounts[key] = count - 1;
			}

			return expectedAceCounts.Values.All(count => count == 0);
		}
		catch
		{
			return false;
		}
	}

	private static bool SettingsSecurityIdentityMatches(
		string expectedSddl,
		string actualSddl)
	{
		try
		{
			return SecurityDescriptorIdentityMatches(
				new RawSecurityDescriptor(expectedSddl),
				new RawSecurityDescriptor(actualSddl));
		}
		catch
		{
			return false;
		}
	}

	private static bool SecurityDescriptorIdentityMatches(
		RawSecurityDescriptor expected,
		RawSecurityDescriptor actual)
	{
		return expected.Owner is not null &&
			actual.Owner is not null &&
			expected.Owner.Equals(actual.Owner) &&
			expected.Group is not null &&
			actual.Group is not null &&
			expected.Group.Equals(actual.Group);
	}

	private static bool HasInheritedAce(RawAcl dacl)
	{
		for (int index = 0; index < dacl.Count; index++)
		{
			if ((dacl[index].AceFlags & AceFlags.Inherited) != 0)
			{
				return true;
			}
		}

		return false;
	}

	private static bool RawAclBinaryEquals(RawAcl expected, RawAcl actual)
	{
		if (expected.BinaryLength != actual.BinaryLength)
		{
			return false;
		}

		byte[] expectedBytes = new byte[expected.BinaryLength];
		byte[] actualBytes = new byte[actual.BinaryLength];
		expected.GetBinaryForm(expectedBytes, 0);
		actual.GetBinaryForm(actualBytes, 0);
		return expectedBytes.AsSpan().SequenceEqual(actualBytes);
	}

	private static string GetAceKeyIgnoringInherited(GenericAce ace)
	{
		byte[] bytes = new byte[ace.BinaryLength];
		ace.GetBinaryForm(bytes, 0);
		bytes[1] &= unchecked((byte)~(byte)AceFlags.Inherited);
		return Convert.ToHexString(bytes);
	}

	private static bool TryFileContentMatches(
		string filePath,
		ReadOnlySpan<byte> expectedBytes)
	{
		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);
			return TryStreamContentMatches(stream, expectedBytes);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryStreamContentMatches(
		FileStream stream,
		ReadOnlySpan<byte> expectedBytes)
	{
		byte[]? bytes = null;

		try
		{
			if (!stream.CanRead || stream.Length != expectedBytes.Length)
			{
				return false;
			}

			stream.Position = 0;
			bytes = new byte[expectedBytes.Length];
			stream.ReadExactly(bytes);
			return CryptographicOperations.FixedTimeEquals(bytes, expectedBytes);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
	}

	private static bool TryStreamFingerprintMatches(
		FileStream stream,
		long expectedLength,
		ReadOnlySpan<byte> expectedHash)
	{
		byte[]? actualHash = null;

		try
		{
			if (!stream.CanRead ||
				expectedLength <= 0 ||
				expectedLength > MaximumRollbackFingerprintBytes ||
				stream.Length != expectedLength ||
				expectedHash.Length != SHA256.HashSizeInBytes)
			{
				return false;
			}

			stream.Position = 0;
			actualHash = SHA256.HashData(stream);
			return CryptographicOperations.FixedTimeEquals(
				actualHash,
				expectedHash);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (actualHash is not null)
			{
				CryptographicOperations.ZeroMemory(actualHash);
			}
		}
	}

	private static bool TryCaptureSettingsFileState(
		string filePath,
		out byte[]? bytes,
		out string? securitySddl)
	{
		bytes = null;
		securitySddl = null;

		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);
			if (stream.Length <= 0 || stream.Length > MaximumSettingsBytes)
			{
				return false;
			}

			bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			string capturedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (!HasCompleteSettingsSecurityDescriptor(capturedSddl) ||
				!TryStreamContentMatches(stream, bytes))
			{
				CryptographicOperations.ZeroMemory(bytes);
				bytes = null;
				securitySddl = null;
				return false;
			}

			string confirmedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (!string.Equals(
					capturedSddl,
					confirmedSddl,
					StringComparison.Ordinal))
			{
				CryptographicOperations.ZeroMemory(bytes);
				bytes = null;
				return false;
			}

			securitySddl = capturedSddl;
			return true;
		}
		catch
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}

			bytes = null;
			securitySddl = null;
			return false;
		}
	}

	private static bool TryCaptureSettingsFileSecurityAndFingerprint(
		string filePath,
		out string? securitySddl,
		out long length,
		out byte[]? hash)
	{
		securitySddl = null;
		length = 0;
		hash = null;

		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.None,
				fileSecurity: null);
			long capturedLength = stream.Length;
			if (capturedLength <= 0)
			{
				return false;
			}

			string capturedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (capturedLength <= MaximumRollbackFingerprintBytes)
			{
				stream.Position = 0;
				hash = SHA256.HashData(stream);
			}
			string confirmedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (stream.Length != capturedLength ||
				!HasCompleteSettingsSecurityDescriptor(capturedSddl) ||
				!string.Equals(
					capturedSddl,
					confirmedSddl,
					StringComparison.Ordinal))
			{
				if (hash is not null)
				{
					CryptographicOperations.ZeroMemory(hash);
					hash = null;
				}
				length = 0;
				return false;
			}

			securitySddl = capturedSddl;
			length = capturedLength;
			return true;
		}
		catch
		{
			if (hash is not null)
			{
				CryptographicOperations.ZeroMemory(hash);
			}

			hash = null;
			length = 0;
			return false;
		}
	}

	internal static bool TryRestoreBackupIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		string? expectedCurrentSddl = null,
		Action? beforeReplacement = null,
		Action<int>? beforeReactivationReplacement = null)
	{
		byte[]? expectedRestoredBytes = null;

		try
		{
			return TryCaptureSettingsFileState(
					backupPath,
					out expectedRestoredBytes,
					out string? expectedRestoredSddl) &&
				expectedRestoredBytes is not null &&
				expectedRestoredSddl is not null &&
				TryRestoreBackupIfTargetMatches(
					settingsPath,
					expectedCurrentBytes,
					backupPath,
					expectedRestoredBytes,
					expectedRestoredSddl,
					expectedCurrentSddl,
					beforeReplacement,
					beforeReactivationReplacement:
						beforeReactivationReplacement);
		}
		finally
		{
			if (expectedRestoredBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRestoredBytes);
			}
		}
	}

	internal static bool TryRestoreBackupPreservingRecoveryIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		Action? beforeRestoredSecurityValidation = null,
		Action? beforeReplacement = null,
		Action<int>? beforeReactivationReplacement = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);
		if (directory is null)
		{
			return false;
		}

		string recoveryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.recovery.bak");
		byte[]? expectedRecoveryBytes = null;
		byte[]? actualRecoveryBytes = null;
		byte[]? expectedRestoredHash = null;
		string? actualRecoverySddl = null;

		try
		{
			if (!File.Exists(backupPath) ||
				!File.Exists(settingsPath) ||
				!TryCaptureSettingsFileSecurityAndFingerprint(
					backupPath,
					out string? expectedRestoredSddl,
					out long expectedRestoredLength,
					out expectedRestoredHash) ||
				expectedRestoredSddl is null ||
				!TryCaptureSettingsFileState(
					settingsPath,
					out expectedRecoveryBytes,
					out string? expectedRecoverySddl) ||
				expectedRecoveryBytes is null ||
				expectedRecoverySddl is null ||
				!CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					expectedCurrentBytes))
			{
				return false;
			}

			bool capturedStateIsKnownWrite =
				SettingsSecurityMatchesAfterReplacement(
					expectedRestoredSddl,
					expectedRecoverySddl) ||
				SettingsSecurityCanRepairLegacyExpansion(
					expectedRestoredSddl,
					expectedRecoverySddl);

			beforeReplacement?.Invoke();
			File.Replace(
				backupPath,
				settingsPath,
				destinationBackupFileName: recoveryPath,
				ignoreMetadataErrors: false);
			bool recoveryWasCaptured = TryCaptureSettingsFileState(
					recoveryPath,
					out actualRecoveryBytes,
					out actualRecoverySddl) &&
				actualRecoveryBytes is not null &&
				actualRecoverySddl is not null;
			bool recoveryMatchesCapturedTarget = recoveryWasCaptured &&
				CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					actualRecoveryBytes) &&
				string.Equals(
					expectedRecoverySddl,
					actualRecoverySddl,
					StringComparison.Ordinal);
			bool recoveryIsKnownWrite = capturedStateIsKnownWrite &&
				recoveryMatchesCapturedTarget;

			if (!recoveryMatchesCapturedTarget)
			{
				if (recoveryWasCaptured &&
					expectedRestoredHash is not null &&
					actualRecoveryBytes is not null)
				{
					_ = TryReactivateDisplacedSettingsIfTargetFingerprintMatches(
						settingsPath,
						expectedRestoredSddl,
						expectedRestoredLength,
						expectedRestoredHash,
						recoveryPath,
						actualRecoveryBytes,
						beforeReactivationReplacement:
							beforeReactivationReplacement);
				}

				return false;
			}

			beforeRestoredSecurityValidation?.Invoke();
			bool restoredSecurityMatches = expectedRestoredHash is not null &&
				TryValidateAndRepairCommittedSettingsSecurityOnly(
					settingsPath,
					expectedRestoredSddl,
					authoritativeRepairPrerequisiteSddl:
						recoveryIsKnownWrite ? expectedRecoverySddl : null,
					expectedRestoredLength,
					expectedRestoredHash);
			return File.Exists(settingsPath) &&
				File.Exists(recoveryPath) &&
				restoredSecurityMatches;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (expectedRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRecoveryBytes);
			}
			if (actualRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(actualRecoveryBytes);
			}
			if (expectedRestoredHash is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRestoredHash);
			}
		}
	}

	private static bool
		TryReactivateDisplacedSettingsIfTargetFingerprintMatches(
			string settingsPath,
			string expectedCurrentSddl,
			long expectedCurrentLength,
			ReadOnlySpan<byte> expectedCurrentHash,
			string displacedPath,
			byte[] displacedBytes,
			Action<int>? beforeReactivationReplacement = null,
			int reactivationAttemptsRemaining =
				MaximumRollbackReactivationAttempts)
	{
		string? directory = Path.GetDirectoryName(settingsPath);
		if (directory is null || reactivationAttemptsRemaining <= 0)
		{
			return false;
		}

		string evidencePath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.recovery.bak");
		byte[]? currentHash = null;
		byte[]? evidenceHash = null;
		byte[]? activeBytes = null;
		byte[]? latestDisplacedBytes = null;

		try
		{
			if (!File.Exists(displacedPath) ||
				!TryCaptureSettingsFileSecurityAndFingerprint(
					settingsPath,
					out string? currentSddl,
					out long currentLength,
					out currentHash) ||
				currentSddl is null ||
				currentHash is null ||
				currentLength != expectedCurrentLength ||
				!CryptographicOperations.FixedTimeEquals(
					currentHash,
					expectedCurrentHash) ||
				(!SettingsSecurityMatchesAfterReplacement(
					expectedCurrentSddl,
					currentSddl) &&
				!SettingsSecurityCanRepairLegacyExpansion(
					expectedCurrentSddl,
					currentSddl)))
			{
				return false;
			}

			beforeReactivationReplacement?.Invoke(
				reactivationAttemptsRemaining);
			File.Replace(
				displacedPath,
				settingsPath,
				destinationBackupFileName: evidencePath,
				ignoreMetadataErrors: false);

			bool activeWasCaptured = TryCaptureSettingsFileState(
				settingsPath,
				out activeBytes,
				out string? activeSddl) &&
				activeBytes is not null &&
				activeSddl is not null;
			bool activeMatchesDisplaced = activeWasCaptured &&
				CryptographicOperations.FixedTimeEquals(
					activeBytes,
					displacedBytes);
			bool evidenceWasCaptured =
				TryCaptureSettingsFileSecurityAndFingerprint(
					evidencePath,
					out string? evidenceSddl,
					out long evidenceLength,
					out evidenceHash) &&
				evidenceSddl is not null &&
				evidenceHash is not null;
			bool evidenceMatchesExpectedCurrent = evidenceWasCaptured &&
				evidenceLength == currentLength &&
				CryptographicOperations.FixedTimeEquals(
					evidenceHash,
					currentHash) &&
				string.Equals(
					evidenceSddl,
					currentSddl,
					StringComparison.Ordinal);

			if (evidenceMatchesExpectedCurrent)
			{
				return activeMatchesDisplaced;
			}

			if (activeMatchesDisplaced &&
				activeSddl is not null &&
				reactivationAttemptsRemaining > 1 &&
				TryCaptureSettingsFileState(
					evidencePath,
					out latestDisplacedBytes,
					out string? latestDisplacedSddl) &&
				latestDisplacedBytes is not null &&
				latestDisplacedSddl is not null)
			{
				_ = TryRestoreBackupIfTargetMatches(
					settingsPath,
					displacedBytes,
					evidencePath,
					latestDisplacedBytes,
					latestDisplacedSddl,
					activeSddl,
					beforeReplacement: () =>
						beforeReactivationReplacement?.Invoke(
							reactivationAttemptsRemaining - 1),
					preserveRecoveryOnSuccess: true,
					reactivationAttemptsRemaining:
						reactivationAttemptsRemaining - 1,
					beforeReactivationReplacement:
						beforeReactivationReplacement);
			}

			return false;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (currentHash is not null)
			{
				CryptographicOperations.ZeroMemory(currentHash);
			}
			if (evidenceHash is not null)
			{
				CryptographicOperations.ZeroMemory(evidenceHash);
			}
			if (activeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(activeBytes);
			}
			if (latestDisplacedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(latestDisplacedBytes);
			}
		}
	}

	private static bool TryRestoreBackupIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		byte[]? expectedRestoredBytes,
		string? expectedRestoredSddl,
		string? expectedCurrentSddl,
		Action? beforeReplacement = null,
		bool preserveRecoveryOnSuccess = false,
		int reactivationAttemptsRemaining =
			MaximumRollbackReactivationAttempts,
		Action<int>? beforeReactivationReplacement = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);
		if (directory is null ||
			expectedRestoredBytes is null ||
			expectedRestoredSddl is null ||
			reactivationAttemptsRemaining <= 0)
		{
			return false;
		}

		string recoveryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.recovery.bak");
		bool recoveryIsKnownWrite = false;
		bool restoredStateMatches = false;
		byte[]? expectedRecoveryBytes = null;
		byte[]? actualRecoveryBytes = null;
		string? actualRecoverySddl = null;

		try
		{
			if (!File.Exists(backupPath) ||
				!File.Exists(settingsPath) ||
				!TryCaptureSettingsFileState(
					settingsPath,
					out expectedRecoveryBytes,
					out string? expectedRecoverySddl) ||
				expectedRecoveryBytes is null ||
				expectedRecoverySddl is null ||
				!CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					expectedCurrentBytes))
			{
				return false;
			}

			bool capturedStateIsKnownWrite = expectedCurrentSddl is not null &&
				(SettingsSecurityMatchesAfterReplacement(
					expectedCurrentSddl,
					expectedRecoverySddl) ||
				SettingsSecurityCanRepairLegacyExpansion(
					expectedCurrentSddl,
					expectedRecoverySddl));
			if (expectedCurrentSddl is not null &&
				!capturedStateIsKnownWrite)
			{
				return false;
			}

			beforeReplacement?.Invoke();
			File.Replace(
				backupPath,
				settingsPath,
				destinationBackupFileName: recoveryPath,
				ignoreMetadataErrors: false);

			bool recoveryWasCaptured = TryCaptureSettingsFileState(
					recoveryPath,
					out actualRecoveryBytes,
					out actualRecoverySddl) &&
				actualRecoveryBytes is not null &&
				actualRecoverySddl is not null;
			bool recoveryMatchesCapturedTarget = recoveryWasCaptured &&
				CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					actualRecoveryBytes) &&
				string.Equals(
					expectedRecoverySddl,
					actualRecoverySddl,
					StringComparison.Ordinal);
			recoveryIsKnownWrite = capturedStateIsKnownWrite &&
				recoveryMatchesCapturedTarget;

			if (!recoveryMatchesCapturedTarget)
			{
				if (recoveryWasCaptured && reactivationAttemptsRemaining > 1)
				{
					_ = TryRestoreBackupIfTargetMatches(
						settingsPath,
						expectedRestoredBytes,
						recoveryPath,
						actualRecoveryBytes,
						actualRecoverySddl,
						expectedRestoredSddl,
						beforeReplacement: () =>
							beforeReactivationReplacement?.Invoke(
								reactivationAttemptsRemaining - 1),
						preserveRecoveryOnSuccess: true,
						reactivationAttemptsRemaining:
							reactivationAttemptsRemaining - 1,
						beforeReactivationReplacement:
							beforeReactivationReplacement);
				}

				return false;
			}

			restoredStateMatches = TryValidateAndRepairCommittedSettingsSecurity(
				settingsPath,
				expectedRestoredBytes,
				expectedRestoredSddl,
				authoritativeRepairPrerequisiteSddl:
					recoveryIsKnownWrite ? expectedRecoverySddl : null);
			return restoredStateMatches;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (expectedRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRecoveryBytes);
			}
			if (actualRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(actualRecoveryBytes);
			}

			if (recoveryIsKnownWrite &&
				restoredStateMatches &&
				!preserveRecoveryOnSuccess)
			{
				TryDeleteFile(recoveryPath);
			}
		}
	}

	private static bool TryRestoreMissingTarget(
		string backupPath,
		string settingsPath)
	{
		try
		{
			if (!File.Exists(backupPath) || File.Exists(settingsPath))
			{
				return false;
			}

			File.Move(backupPath, settingsPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetVerifiedLongPath(
		string path,
		out string? longPath)
	{
		longPath = null;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(path) ||
				!File.Exists(path))
			{
				return false;
			}

			StringBuilder buffer = new(MaximumNativePathCharacters);
			uint written = GetLongPathName(
				Path.GetFullPath(path),
				buffer,
				checked((uint)buffer.Capacity));

			if (written == 0 || written >= checked((uint)buffer.Capacity))
			{
				return false;
			}

			longPath = Path.GetFullPath(buffer.ToString());
			return Path.IsPathFullyQualified(longPath);
		}
		catch
		{
			longPath = null;
			return false;
		}
	}

	private static string? TryGetDefaultSettingsPath()
	{
		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);

		return string.IsNullOrWhiteSpace(userProfile)
			? null
			: Path.Combine(
				userProfile,
				".gemini",
				"antigravity-cli",
				"settings.json");
	}

	private static string? TryGetDefaultPrivateRoot()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		return string.IsNullOrWhiteSpace(localApplicationData)
			? null
			: Path.Combine(
				localApplicationData,
				"AiUsageDashboard",
				"private",
				"antigravity-statusline");
	}

	private static bool TryDeleteFile(string path)
	{
		try
		{
			if (IsPathAbsent(path))
			{
				return true;
			}

			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			{
				return false;
			}

			File.Delete(path);
			return IsPathAbsent(path);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsPathAbsent(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return false;
		}
		catch (FileNotFoundException)
		{
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch
		{
			return false;
		}
	}

	[DllImport(
		"kernel32.dll",
		EntryPoint = "GetLongPathNameW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static extern uint GetLongPathName(
		string shortPath,
		StringBuilder longPath,
		uint longPathLength);

	[GeneratedRegex(
		"^AiUsageDashboard\\.AntigravityCapture-(?<hash>[0-9a-f]{16})\\.exe$",
		RegexOptions.CultureInvariant)]
	private static partial Regex OwnedHelperFileNameRegex();
}
