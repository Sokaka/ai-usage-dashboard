using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityLiveR1SectionProfileComposer
{
	private const int MaximumFileBytes = 1024 * 1024;

	internal static async Task ComposeAsync(
		string r0ProfilePath,
		string reviewedLayoutPath,
		string outputPath,
		CancellationToken cancellationToken = default,
		Func<AntigravityLiveR0Profile, CancellationToken, Task<string>>?
			settingsBaselineFingerprintProvider = null)
	{
		string stage = "PathValidation";
		byte[]? profileBytes = null;
		byte[]? layoutBytes = null;
		byte[]? outputBytes = null;
		byte[]? profileHmacKey = null;
		string? temporaryPath = null;

		try
		{
			if (!Path.IsPathFullyQualified(r0ProfilePath) ||
				!Path.IsPathFullyQualified(reviewedLayoutPath) ||
				!Path.IsPathFullyQualified(outputPath))
			{
				throw new InvalidDataException();
			}

			string profile = Path.GetFullPath(r0ProfilePath);
			string layout = Path.GetFullPath(reviewedLayoutPath);
			string output = Path.GetFullPath(outputPath);
			string directory = Path.GetDirectoryName(profile) ??
				throw new InvalidDataException();

			if (string.Equals(profile, layout, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(profile, output, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(layout, output, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					directory,
					Path.GetDirectoryName(layout),
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					directory,
					Path.GetDirectoryName(output),
					StringComparison.OrdinalIgnoreCase) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(directory) ||
				!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(profile) ||
				!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(layout) ||
				Directory.Exists(output) ||
				(File.Exists(output) &&
					!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(output)))
			{
				throw new InvalidDataException();
			}

			stage = "InputRead";
			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profile,
					MaximumFileBytes,
					cancellationToken);
			layoutBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					layout,
					MaximumFileBytes,
					cancellationToken);

			stage = "InputDeserialize";
			AntigravityLiveR0ProfileFile r0 =
				AntigravityLiveR0ProfileJson.Deserialize(profileBytes);
			AntigravityUsageR1SectionLayoutFile reviewed =
				AntigravityUsageR1SectionCalibrationJson.DeserializeLayout(
					layoutBytes);

			stage = "ProvenanceValidation";
			(AntigravityLiveR0Profile captureProfile, byte[] loadedHmacKey) =
				await r0.ToProfileAsync(profile, cancellationToken);
			profileHmacKey = loadedHmacKey;
			Func<AntigravityLiveR0Profile, CancellationToken, Task<string>>
				baselineProvider = settingsBaselineFingerprintProvider ??
				CaptureCurrentSettingsBaselineFingerprintAsync;
			string settingsBaselineFingerprint = await baselineProvider(
				captureProfile,
				cancellationToken);
			string captureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					captureProfile,
					reviewed.Spec.IsAlternateScreen,
					settingsBaselineFingerprint);

			if (!string.Equals(
					reviewed.CaptureContractFingerprint,
					captureContractFingerprint,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException();
			}

			stage = "Compose";
			AntigravityLiveR1SectionProfileFile composed = new(
				AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
				r0.Id,
				r0.ExecutableFingerprint,
				r0.WorkingDirectory,
				r0.Environment,
				r0.Columns,
				r0.Rows,
				new AntigravityLiveR1PromptGuard(
					r0.ExpectedPromptStructuralFingerprint,
					r0.ExpectedExactPromptFingerprint,
					r0.ExactPromptFingerprintHmacKeyPath),
				reviewed,
				r0.NonCredentialSettingsFiles,
				r0.MaximumSavedOutputBytes,
				r0.PromptTimeout,
				r0.UsageTimeout,
				r0.StableScreenDuration,
				r0.PollInterval,
				r0.CleanupTimeout);

			stage = "Serialize";
			outputBytes = AntigravityLiveR1SectionProfileJson.Serialize(composed);

			if (File.Exists(output))
			{
				stage = "ExistingOutputValidation";

				if (!await HasExactPrivateProfileAsync(
						output,
						outputBytes,
						cancellationToken))
				{
					throw new InvalidDataException();
				}

				return;
			}

			stage = "TemporaryCreate";
			temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
			await WritePrivateFileAsync(
				temporaryPath,
				outputBytes,
				cancellationToken);

			stage = "PreCommitValidation";

			if (!await HasExactPrivateProfileAsync(
					temporaryPath,
					outputBytes,
					cancellationToken))
			{
				throw new InvalidDataException();
			}

			stage = "Commit";
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, output, overwrite: false);
			temporaryPath = null;

			stage = "OutputVerification";

			if (!await HasExactPrivateProfileAsync(
					output,
					outputBytes,
					CancellationToken.None))
			{
				throw new InvalidDataException();
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			throw new InvalidDataException(stage, exception);
		}
		finally
		{
			if (temporaryPath is not null)
			{
				AntigravityUsageR1CalibrationFile.TryDelete(temporaryPath);
			}

			ZeroBytes(profileBytes);
			ZeroBytes(layoutBytes);
			ZeroBytes(outputBytes);
			ZeroBytes(profileHmacKey);
		}
	}

	private static Task<string> CaptureCurrentSettingsBaselineFingerprintAsync(
		AntigravityLiveR0Profile profile,
		CancellationToken cancellationToken)
	{
		AntigravityLiveR0Runner runner = new();
		return runner.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
			profile,
			cancellationToken);
	}

	private static async Task<bool> HasExactPrivateProfileAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		byte[]? hmacKey = null;
		byte[]? observedBytes = null;

		try
		{
			observedBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					path,
					MaximumFileBytes,
					cancellationToken);

			if (!CryptographicOperations.FixedTimeEquals(
					observedBytes,
					expectedBytes.Span))
			{
				return false;
			}

			AntigravityLiveR1SectionProfileFile persisted =
				AntigravityLiveR1SectionProfileJson.Deserialize(observedBytes);
			(AntigravityLiveR1SectionProfile _, byte[] loadedHmacKey) =
				await persisted.ToProfileAsync(path, cancellationToken);
			hmacKey = loadedHmacKey;
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return false;
		}
		finally
		{
			ZeroBytes(hmacKey);
			ZeroBytes(observedBytes);
		}
	}

	private static async Task WritePrivateFileAsync(
		string path,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken)
	{
		await using (FileStream create = new(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough))
		{
			await create.FlushAsync(cancellationToken);
			create.Flush(flushToDisk: true);
		}

		if (!AntigravityPrivateKeyAcl.TryProtectNewFile(path) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(path))
		{
			throw new IOException();
		}

		await using FileStream write = new(
			path,
			FileMode.Open,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await write.WriteAsync(bytes, cancellationToken);
		await write.FlushAsync(cancellationToken);
		write.Flush(flushToDisk: true);
	}

	private static void ZeroBytes(byte[]? bytes)
	{
		if (bytes is not null)
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}
