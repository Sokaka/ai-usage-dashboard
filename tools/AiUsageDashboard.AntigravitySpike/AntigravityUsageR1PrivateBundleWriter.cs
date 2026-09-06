using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsageR1PrivateBundleFailureStage
{
	PathValidation,
	SnapshotValidation,
	RecoveryStateValidation,
	LockAcquisition,
	ExistingBundleRead,
	ExistingBundleValidation,
	CapacityValidation,
	TemporaryWrite,
	Commit,
	OutputVerification
}

internal sealed class AntigravityUsageR1PrivateBundleException : IOException
{
	internal AntigravityUsageR1PrivateBundleFailureStage Stage { get; }

	internal AntigravityUsageR1PrivateBundleException(
		AntigravityUsageR1PrivateBundleFailureStage stage)
		: base($"The private AGY R1 screen bundle operation failed at stage: {stage}.")
	{
		Stage = stage;
	}
}

internal sealed record AntigravityUsageR1PrivateBundleMetadata(
	bool Exists,
	bool WasUpdated,
	int SequenceCount,
	string? CaptureContractFingerprint,
	int? Columns,
	int? Rows,
	bool? IsAlternateScreen);

internal interface IAntigravityUsageR1PrivateBundleFileCommitter
{
	void Move(string sourceFileName, string destinationFileName);

	void Replace(
		string sourceFileName,
		string destinationFileName,
		string destinationBackupFileName);
}

internal sealed class AntigravityUsageR1PrivateBundleFileCommitter :
	IAntigravityUsageR1PrivateBundleFileCommitter
{
	internal static AntigravityUsageR1PrivateBundleFileCommitter Instance
	{
		get;
	} = new();

	private AntigravityUsageR1PrivateBundleFileCommitter()
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

internal static class AntigravityUsageR1PrivateBundleWriter
{
	private readonly record struct BundleFileState(
		long Length,
		DateTime CreationTimeUtc,
		DateTime LastWriteTimeUtc,
		FileAttributes Attributes);

	private const int MaximumBundleBytes = 4 * 1024 * 1024;
	private const int MaximumLineLength = 4096;
	private const int MaximumSequences = 16;
	private const int MaximumViewportDimension = 4096;
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		MaxDepth = 32,
		WriteIndented = true
	};

	internal static async Task<AntigravityUsageR1PrivateBundleMetadata>
		PreflightAsync(
			string outputPath,
			CancellationToken cancellationToken = default)
	{
		AntigravityUsageR1PrivateBundleFailureStage stage =
			AntigravityUsageR1PrivateBundleFailureStage.PathValidation;
		byte[]? existingBytes = null;

		try
		{
			ResolvePrivateOutputPath(
				outputPath,
				out string absoluteOutputPath,
				out string parentPath,
				out string lockPath,
				out string debrisPrefix);

			stage = AntigravityUsageR1PrivateBundleFailureStage
				.RecoveryStateValidation;

			if (PathExists(lockPath) || HasRecoveryDebris(parentPath, debrisPrefix))
			{
				throw CreateFailure(stage);
			}

			if (!File.Exists(absoluteOutputPath))
			{
				if (Directory.Exists(absoluteOutputPath))
				{
					throw CreateFailure(
						AntigravityUsageR1PrivateBundleFailureStage
							.PathValidation);
				}

				return CreateEmptyMetadata();
			}

			stage = AntigravityUsageR1PrivateBundleFailureStage
				.ExistingBundleRead;
			BundleFileState beforeReadState = CapturePrivateFileState(
				absoluteOutputPath);
			existingBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					absoluteOutputPath,
					MaximumBundleBytes,
					requirePrivateFile: true,
					cancellationToken);

			stage = AntigravityUsageR1PrivateBundleFailureStage
				.ExistingBundleValidation;
			BundleFileState afterReadState = CapturePrivateFileState(
				absoluteOutputPath);

			if ((beforeReadState != afterReadState) ||
				PathExists(lockPath) ||
				HasRecoveryDebris(parentPath, debrisPrefix))
			{
				throw CreateFailure(stage);
			}

			AntigravityUsageR1PrivateScreenBundleFile bundle =
				DeserializeAndValidateBundle(existingBytes);

			if (bundle.Sequences.Count >= MaximumSequences)
			{
				throw CreateFailure(
					AntigravityUsageR1PrivateBundleFailureStage
						.CapacityValidation);
			}

			return CreateMetadata(bundle, wasUpdated: false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (AntigravityUsageR1PrivateBundleException)
		{
			throw;
		}
		catch
		{
			throw CreateFailure(stage);
		}
		finally
		{
			ZeroBytes(existingBytes);
		}
	}

	internal static Task<AntigravityUsageR1PrivateBundleMetadata> AppendAsync(
		string outputPath,
		TerminalScreenSnapshot snapshot,
		string captureContractFingerprint,
		CancellationToken cancellationToken = default)
	{
		return AppendAsync(
			outputPath,
			snapshot,
			captureContractFingerprint,
			AntigravityUsageR1PrivateBundleFileCommitter.Instance,
			cancellationToken);
	}

	internal static async Task<AntigravityUsageR1PrivateBundleMetadata>
		AppendAsync(
			string outputPath,
			TerminalScreenSnapshot snapshot,
			string captureContractFingerprint,
			IAntigravityUsageR1PrivateBundleFileCommitter fileCommitter,
			CancellationToken cancellationToken = default)
	{
		AntigravityUsageR1PrivateBundleFailureStage stage =
			AntigravityUsageR1PrivateBundleFailureStage.SnapshotValidation;
		FileStream? lockStream = null;
		string? lockPath = null;
		string? temporaryPath = null;
		string? recoveryPath = null;
		string? backupPath = null;
		string? failedNewPath = null;
		byte[]? originalBytes = null;
		byte[]? updatedBytes = null;
		bool ownsLockFile = false;
		bool preserveRecoveryDebris = false;

		try
		{
			ArgumentNullException.ThrowIfNull(fileCommitter);
			AntigravityUsageR1PrivateScreen newScreen = FreezeSnapshot(snapshot);

			if (!AntigravityUsageR1SchemaParser.IsFingerprint(
					captureContractFingerprint))
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1PrivateBundleFailureStage.PathValidation;
			ResolvePrivateOutputPath(
				outputPath,
				out string absoluteOutputPath,
				out string parentPath,
				out lockPath,
				out string debrisPrefix);

			stage = AntigravityUsageR1PrivateBundleFailureStage.LockAcquisition;
			lockStream = new FileStream(
				lockPath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				1,
				FileOptions.WriteThrough);
			ownsLockFile = true;
			lockStream.Flush(flushToDisk: true);

			if (!AntigravityPrivateKeyAcl.IsPrivateDirectory(parentPath))
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1PrivateBundleFailureStage
				.RecoveryStateValidation;

			if (HasRecoveryDebris(parentPath, debrisPrefix))
			{
				throw CreateFailure(stage);
			}

			AntigravityUsageR1PrivateScreenBundleFile? originalBundle = null;
			BundleFileState? originalState = null;

			if (File.Exists(absoluteOutputPath))
			{
				stage = AntigravityUsageR1PrivateBundleFailureStage
					.ExistingBundleRead;
				originalState = CapturePrivateFileState(absoluteOutputPath);
				originalBytes =
					await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
						absoluteOutputPath,
						MaximumBundleBytes,
						requirePrivateFile: true,
						cancellationToken);

				stage = AntigravityUsageR1PrivateBundleFailureStage
					.ExistingBundleValidation;

				if (CapturePrivateFileState(absoluteOutputPath) != originalState)
				{
					throw CreateFailure(stage);
				}

				originalBundle = DeserializeAndValidateBundle(originalBytes);

				if (!string.Equals(
						originalBundle.CaptureContractFingerprint,
						captureContractFingerprint,
						StringComparison.Ordinal) ||
					!HasSameShape(originalBundle.Sequences[0].Pages[0], newScreen))
				{
					throw CreateFailure(stage);
				}

				if (originalBundle.Sequences.Any(sequence =>
					AreScreensEqual(sequence.Pages[0], newScreen)))
				{
					return CreateMetadata(originalBundle, wasUpdated: false);
				}

				if (originalBundle.Sequences.Count >= MaximumSequences)
				{
					throw CreateFailure(
						AntigravityUsageR1PrivateBundleFailureStage
							.CapacityValidation);
				}
			}
			else if (Directory.Exists(absoluteOutputPath))
			{
				throw CreateFailure(
					AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
			}

			AntigravityUsageR1PrivateScreenSequence newSequence = new(
				Array.AsReadOnly(new[] { newScreen }));
			AntigravityUsageR1PrivateScreenSequence[] sequences =
				originalBundle is null
					? new[] { newSequence }
					: originalBundle.Sequences.Concat(new[] { newSequence }).ToArray();
			AntigravityUsageR1PrivateScreenBundleFile updatedBundle = new(
				AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
				captureContractFingerprint,
				Array.AsReadOnly(sequences));
			updatedBytes = SerializeAndValidateBundle(updatedBundle);

			stage = AntigravityUsageR1PrivateBundleFailureStage.TemporaryWrite;
			temporaryPath = CreateSiblingPath(
				parentPath,
				debrisPrefix,
				"new");
			await WritePrivateFileAsync(
				temporaryPath,
				updatedBytes,
				cancellationToken);

			if (!await HasExactPrivateBytesAsync(
					temporaryPath,
					updatedBytes,
					cancellationToken))
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1PrivateBundleFailureStage.Commit;
			cancellationToken.ThrowIfCancellationRequested();

			if (originalBundle is null)
			{
				if (PathExists(absoluteOutputPath))
				{
					throw CreateFailure(stage);
				}

				fileCommitter.Move(temporaryPath, absoluteOutputPath);
				temporaryPath = null;
			}
			else
			{
				if ((originalState is null) ||
					(CapturePrivateFileState(absoluteOutputPath) != originalState) ||
					!await HasExactPrivateBytesAsync(
						absoluteOutputPath,
						originalBytes!,
						cancellationToken))
				{
					throw CreateFailure(stage);
				}

				recoveryPath = CreateSiblingPath(
					parentPath,
					debrisPrefix,
					"recovery");
				backupPath = CreateSiblingPath(
					parentPath,
					debrisPrefix,
					"backup");
				failedNewPath = CreateSiblingPath(
					parentPath,
					debrisPrefix,
					"failed-new");
				await WritePrivateFileAsync(
					recoveryPath,
					originalBytes!,
					cancellationToken);

				try
				{
					fileCommitter.Replace(
						temporaryPath,
						absoluteOutputPath,
						backupPath);
				}
				catch
				{
					if (!File.Exists(temporaryPath))
					{
						temporaryPath = null;
					}

					if (await IsExpectedCommittedStateAsync(
							absoluteOutputPath,
							backupPath,
							originalBytes!,
							updatedBytes))
					{
						goto CommitComplete;
					}

					bool wasRacedStateRestored =
						await TryRestoreRacedBackupAsync(
							absoluteOutputPath,
							backupPath,
							failedNewPath,
							updatedBytes);
					preserveRecoveryDebris = !wasRacedStateRestored;
					throw CreateFailure(stage);
				}

				temporaryPath = null;

				if (!await IsExpectedCommittedStateAsync(
						absoluteOutputPath,
						backupPath,
						originalBytes!,
						updatedBytes))
				{
					bool wasRacedStateRestored =
						await TryRestoreRacedBackupAsync(
							absoluteOutputPath,
							backupPath,
							failedNewPath,
							updatedBytes);
					preserveRecoveryDebris = !wasRacedStateRestored;
					throw CreateFailure(stage);
				}
			}

		CommitComplete:
			stage = AntigravityUsageR1PrivateBundleFailureStage
				.OutputVerification;

			if (!await HasExactPrivateBytesAsync(
					absoluteOutputPath,
					updatedBytes,
					CancellationToken.None))
			{
				preserveRecoveryDebris = originalBundle is not null;
				throw CreateFailure(stage);
			}

			return CreateMetadata(updatedBundle, wasUpdated: true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (AntigravityUsageR1PrivateBundleException)
		{
			throw;
		}
		catch
		{
			throw CreateFailure(stage);
		}
		finally
		{
			ZeroBytes(originalBytes);
			ZeroBytes(updatedBytes);

			if (!preserveRecoveryDebris)
			{
				TryDeletePrivateSibling(temporaryPath);
				TryDeletePrivateSibling(recoveryPath);
				TryDeletePrivateSibling(backupPath);
				TryDeletePrivateSibling(failedNewPath);
			}

			try
			{
				lockStream?.Dispose();
			}
			catch
			{
				// Lock cleanup must never mask a fixed-stage failure.
			}

			if (ownsLockFile)
			{
				TryDeletePrivateSibling(lockPath);
			}
		}
	}

	private static bool AreScreensEqual(
		AntigravityUsageR1PrivateScreen first,
		AntigravityUsageR1PrivateScreen second)
	{
		return HasSameShape(first, second) &&
			first.Lines.SequenceEqual(second.Lines, StringComparer.Ordinal);
	}

	private static BundleFileState CapturePrivateFileState(string path)
	{
		if (!AntigravityPrivateKeyAcl.IsPrivateFile(path))
		{
			throw new InvalidDataException();
		}

		FileInfo file = new(path);
		file.Refresh();

		if (!file.Exists ||
			((file.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) ||
			(file.Length <= 0) ||
			(file.Length > MaximumBundleBytes))
		{
			throw new InvalidDataException();
		}

		return new BundleFileState(
			file.Length,
			file.CreationTimeUtc,
			file.LastWriteTimeUtc,
			file.Attributes);
	}

	private static AntigravityUsageR1PrivateBundleException CreateFailure(
		AntigravityUsageR1PrivateBundleFailureStage stage)
	{
		return new AntigravityUsageR1PrivateBundleException(stage);
	}

	private static AntigravityUsageR1PrivateBundleMetadata CreateEmptyMetadata()
	{
		return new AntigravityUsageR1PrivateBundleMetadata(
			Exists: false,
			WasUpdated: false,
			SequenceCount: 0,
			CaptureContractFingerprint: null,
			Columns: null,
			Rows: null,
			IsAlternateScreen: null);
	}

	private static AntigravityUsageR1PrivateBundleMetadata CreateMetadata(
		AntigravityUsageR1PrivateScreenBundleFile bundle,
		bool wasUpdated)
	{
		AntigravityUsageR1PrivateScreen screen =
			bundle.Sequences[0].Pages[0];
		return new AntigravityUsageR1PrivateBundleMetadata(
			Exists: true,
			wasUpdated,
			bundle.Sequences.Count,
			bundle.CaptureContractFingerprint,
			screen.Columns,
			screen.Rows,
			screen.IsAlternateScreen);
	}

	private static string CreateSiblingPath(
		string parentPath,
		string debrisPrefix,
		string purpose)
	{
		return Path.Combine(
			parentPath,
			$"{debrisPrefix}{purpose}.{Guid.NewGuid():N}.tmp");
	}

	private static AntigravityUsageR1PrivateScreenBundleFile
		DeserializeAndValidateBundle(ReadOnlyMemory<byte> bytes)
	{
		AntigravityUsageR1PrivateScreenBundleFile bundle =
			AntigravityUsageR1CalibrationJson.DeserializePrivateBundle(bytes);
		ValidateBundle(bundle);
		return bundle;
	}

	private static AntigravityUsageR1PrivateScreen FreezeSnapshot(
		TerminalScreenSnapshot snapshot)
	{
		if ((snapshot is null) ||
			(snapshot.Columns <= 0) ||
			(snapshot.Columns > MaximumViewportDimension) ||
			(snapshot.Rows <= 0) ||
			(snapshot.Rows > MaximumViewportDimension) ||
			(snapshot.Lines is null) ||
			(snapshot.Lines.Count != snapshot.Rows) ||
			snapshot.Lines.Any(line =>
				(line is null) ||
				(line.Length > MaximumLineLength) ||
				line.Contains('\r', StringComparison.Ordinal) ||
				line.Contains('\n', StringComparison.Ordinal)))
		{
			throw CreateFailure(
				AntigravityUsageR1PrivateBundleFailureStage
					.SnapshotValidation);
		}

		return new AntigravityUsageR1PrivateScreen(
			snapshot.Columns,
			snapshot.Rows,
			snapshot.IsAlternateScreen,
			Array.AsReadOnly(snapshot.Lines.ToArray()));
	}

	private static bool HasRecoveryDebris(
		string parentPath,
		string debrisPrefix)
	{
		return Directory.EnumerateFileSystemEntries(parentPath)
			.Select(Path.GetFileName)
			.Any(fileName =>
				(fileName is not null) &&
				fileName.StartsWith(debrisPrefix, StringComparison.OrdinalIgnoreCase) &&
				fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
	}

	private static bool HasSameShape(
		AntigravityUsageR1PrivateScreen first,
		AntigravityUsageR1PrivateScreen second)
	{
		return (first.Columns == second.Columns) &&
			(first.Rows == second.Rows) &&
			(first.IsAlternateScreen == second.IsAlternateScreen) &&
			(first.Lines.Count == second.Lines.Count);
	}

	private static async Task<bool> HasExactPrivateBytesAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		byte[]? observedBytes = null;

		try
		{
			observedBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					path,
					MaximumBundleBytes,
					requirePrivateFile: true,
					cancellationToken);
			_ = DeserializeAndValidateBundle(observedBytes);
			return observedBytes.AsSpan().SequenceEqual(expectedBytes.Span) &&
				AntigravityPrivateKeyAcl.IsPrivateFile(path);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return false;
		}
		finally
		{
			ZeroBytes(observedBytes);
		}
	}

	private static async Task<bool> IsExpectedCommittedStateAsync(
		string outputPath,
		string backupPath,
		ReadOnlyMemory<byte> originalBytes,
		ReadOnlyMemory<byte> updatedBytes)
	{
		if (File.Exists(backupPath) &&
			!AntigravityPrivateKeyAcl.IsPrivateFile(backupPath) &&
			!AntigravityPrivateKeyAcl.TryProtectNewFile(backupPath))
		{
			return false;
		}

		return await HasExactPrivateBytesAsync(
				backupPath,
				originalBytes,
				CancellationToken.None) &&
			await HasExactPrivateBytesAsync(
				outputPath,
				updatedBytes,
				CancellationToken.None);
	}

	private static bool PathExists(string path)
	{
		return File.Exists(path) || Directory.Exists(path);
	}

	private static void ResolvePrivateOutputPath(
		string outputPath,
		out string absoluteOutputPath,
		out string parentPath,
		out string lockPath,
		out string debrisPrefix)
	{
		if (string.IsNullOrWhiteSpace(outputPath) ||
			!Path.IsPathFullyQualified(outputPath) ||
			!string.Equals(
				Path.GetExtension(outputPath),
				".json",
				StringComparison.OrdinalIgnoreCase))
		{
			throw CreateFailure(
				AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
		}

		absoluteOutputPath = Path.GetFullPath(outputPath);
		parentPath = Path.GetDirectoryName(absoluteOutputPath) ?? string.Empty;

		if (!AntigravityPrivateKeyAcl.IsPrivateDirectory(parentPath))
		{
			throw CreateFailure(
				AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
		}

		if (File.Exists(absoluteOutputPath))
		{
			FileAttributes attributes = File.GetAttributes(absoluteOutputPath);

			if (((attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath))
			{
				throw CreateFailure(
					AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
			}
		}
		else if (Directory.Exists(absoluteOutputPath))
		{
			throw CreateFailure(
				AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
		}

		string outputFileName = Path.GetFileName(absoluteOutputPath);
		lockPath = Path.Combine(
			parentPath,
			$".{outputFileName}.r1-bundle.lock");
		debrisPrefix = $".{outputFileName}.r1-bundle.";
	}

	private static byte[] SerializeAndValidateBundle(
		AntigravityUsageR1PrivateScreenBundleFile bundle)
	{
		ValidateBundle(bundle);
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, WriteOptions);

		try
		{
			if ((bytes.Length <= 0) ||
				(bytes.Length > MaximumBundleBytes))
			{
				throw new InvalidDataException();
			}

			AntigravityUsageR1PrivateScreenBundleFile roundTrip =
				DeserializeAndValidateBundle(bytes);

			if (!string.Equals(
					bundle.CaptureContractFingerprint,
					roundTrip.CaptureContractFingerprint,
					StringComparison.Ordinal) ||
				(bundle.Sequences.Count != roundTrip.Sequences.Count))
			{
				throw new InvalidDataException();
			}

			return bytes;
		}
		catch
		{
			ZeroBytes(bytes);
			throw;
		}
	}

	private static async Task<bool> TryRestoreRacedBackupAsync(
		string outputPath,
		string backupPath,
		string failedNewPath,
		ReadOnlyMemory<byte> updatedBytes)
	{
		byte[]? racedBytes = null;

		try
		{
			if (!File.Exists(backupPath) ||
				(!AntigravityPrivateKeyAcl.IsPrivateFile(backupPath) &&
				 !AntigravityPrivateKeyAcl.TryProtectNewFile(backupPath)) ||
				!await HasExactPrivateBytesAsync(
					outputPath,
					updatedBytes,
					CancellationToken.None))
			{
				return false;
			}

			racedBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					backupPath,
					MaximumBundleBytes,
					requirePrivateFile: true,
					CancellationToken.None);
			_ = DeserializeAndValidateBundle(racedBytes);
			File.Replace(
				backupPath,
				outputPath,
				failedNewPath,
				ignoreMetadataErrors: false);

			if ((!AntigravityPrivateKeyAcl.IsPrivateFile(outputPath) &&
				 !AntigravityPrivateKeyAcl.TryProtectNewFile(outputPath)) ||
				(!AntigravityPrivateKeyAcl.IsPrivateFile(failedNewPath) &&
				 !AntigravityPrivateKeyAcl.TryProtectNewFile(failedNewPath)))
			{
				return false;
			}

			return await HasExactPrivateBytesAsync(
					outputPath,
					racedBytes,
					CancellationToken.None) &&
				await HasExactPrivateBytesAsync(
					failedNewPath,
					updatedBytes,
					CancellationToken.None);
		}
		catch
		{
			return false;
		}
		finally
		{
			ZeroBytes(racedBytes);
		}
	}

	private static void TryDeletePrivateSibling(string? path)
	{
		if (path is null)
		{
			return;
		}

		try
		{
			if (File.Exists(path) &&
				((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0))
			{
				File.Delete(path);
			}
		}
		catch
		{
			// Cleanup must never mask a fixed-stage failure.
		}
	}

	private static void ValidateBundle(
		AntigravityUsageR1PrivateScreenBundleFile bundle)
	{
		if (!string.Equals(
				bundle.FormatVersion,
				AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
				StringComparison.Ordinal) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				bundle.CaptureContractFingerprint) ||
			(bundle.Sequences is null) ||
			(bundle.Sequences.Count <= 0) ||
			(bundle.Sequences.Count > MaximumSequences))
		{
			throw new InvalidDataException();
		}

		AntigravityUsageR1PrivateScreen? baseline = null;

		foreach (AntigravityUsageR1PrivateScreenSequence sequence in
			bundle.Sequences)
		{
			if ((sequence is null) ||
				(sequence.Pages is null) ||
				(sequence.Pages.Count != 1))
			{
				throw new InvalidDataException();
			}

			AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];
			ValidateScreen(screen);
			baseline ??= screen;

			if (!HasSameShape(baseline, screen))
			{
				throw new InvalidDataException();
			}
		}
	}

	private static void ValidateScreen(AntigravityUsageR1PrivateScreen screen)
	{
		if ((screen is null) ||
			(screen.Columns <= 0) ||
			(screen.Columns > MaximumViewportDimension) ||
			(screen.Rows <= 0) ||
			(screen.Rows > MaximumViewportDimension) ||
			(screen.Lines is null) ||
			(screen.Lines.Count != screen.Rows) ||
			screen.Lines.Any(line =>
				(line is null) ||
				(line.Length > MaximumLineLength) ||
				line.Contains('\r', StringComparison.Ordinal) ||
				line.Contains('\n', StringComparison.Ordinal)))
		{
			throw new InvalidDataException();
		}
	}

	private static async Task WritePrivateFileAsync(
		string path,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken)
	{
		await using (FileStream createStream = new(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough))
		{
			await createStream.FlushAsync(cancellationToken);
			createStream.Flush(flushToDisk: true);
		}

		if (!AntigravityPrivateKeyAcl.TryProtectNewFile(path) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(path))
		{
			throw new IOException();
		}

		await using (FileStream writeStream = new(
			path,
			FileMode.Open,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough))
		{
			await writeStream.WriteAsync(bytes, cancellationToken);
			await writeStream.FlushAsync(cancellationToken);
			writeStream.Flush(flushToDisk: true);
		}
	}

	private static void ZeroBytes(byte[]? bytes)
	{
		if (bytes is not null)
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}
