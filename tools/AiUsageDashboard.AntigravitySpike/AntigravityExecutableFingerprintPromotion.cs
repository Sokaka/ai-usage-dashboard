using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityExecutableFingerprintPromotionMetadata(
	bool WasWritten,
	string ApprovedSha256);

internal static class AntigravityExecutableFingerprintPromotion
{
	private const int MaximumCliVersionLength = 256;
	private const int MaximumProfileBytes = 1024 * 1024;

	internal static Task<AntigravityExecutableFingerprintPromotionMetadata>
		PromoteAsync(
			string sourceProfilePath,
			string outputPath,
			string approvedSha256,
			CancellationToken cancellationToken = default)
	{
		return PromoteAsync(
			sourceProfilePath,
			outputPath,
			approvedSha256,
			new WindowsAntigravityExecutableInspector(),
			new ConPtyAntigravityCliVersionProbe(),
			cancellationToken);
	}

	internal static async Task<AntigravityExecutableFingerprintPromotionMetadata>
		PromoteAsync(
			string sourceProfilePath,
			string outputPath,
			string approvedSha256,
			IAntigravityExecutableInspector executableInspector,
			IAntigravityCliVersionProbe versionProbe,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(executableInspector);
		ArgumentNullException.ThrowIfNull(versionProbe);
		string stage = "PathValidation";
		byte[]? hmacKey = null;
		byte[]? outputBytes = null;
		byte[]? sourceBytes = null;
		string? temporaryPath = null;

		try
		{
			if (!IsFingerprint(approvedSha256) ||
				!Path.IsPathFullyQualified(sourceProfilePath) ||
				!Path.IsPathFullyQualified(outputPath))
			{
				throw new InvalidDataException();
			}

			string source = Path.GetFullPath(sourceProfilePath);
			string output = Path.GetFullPath(outputPath);
			string directory = Path.GetDirectoryName(source) ??
				throw new InvalidDataException();

			if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					directory,
					Path.GetDirectoryName(output),
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					Path.GetExtension(output),
					".json",
					StringComparison.OrdinalIgnoreCase) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(directory) ||
				!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(source) ||
				Directory.Exists(output) ||
				(File.Exists(output) &&
					!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(output)))
			{
				throw new InvalidDataException();
			}

			stage = "ProfileRead";
			sourceBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					source,
					MaximumProfileBytes,
					cancellationToken);
			AntigravityLiveR0ProfileFile sourceProfile =
				AntigravityLiveR0ProfileJson.Deserialize(sourceBytes);
			stage = "ProfileValidation";
			(AntigravityLiveR0Profile _, byte[] loadedHmacKey) =
				await sourceProfile.ToProfileAsync(source, cancellationToken);
			hmacKey = loadedHmacKey;

			stage = "ExecutableLease";
			using AntigravityExecutableLease executableLease =
				AntigravityExecutableLease.Acquire(
					sourceProfile.ExecutableFingerprint.AbsolutePath);
			stage = "PreInspectionFileStateValidation";
			AntigravityExecutableFileState before =
				executableInspector.CaptureFileState(executableLease.AbsolutePath);

			if (HasReparsePoint(before))
			{
				throw new InvalidDataException();
			}

			stage = "ExecutableInspection";
			AntigravityExecutableInspection inspection =
				await executableInspector.InspectAsync(
					executableLease.AbsolutePath,
					cancellationToken);
			AntigravityExecutableFileState afterInspection =
				executableInspector.CaptureFileState(executableLease.AbsolutePath);

			stage = "InspectionShapeValidation";
			if (!IsFingerprint(inspection.Sha256 ?? string.Empty) ||
				string.IsNullOrWhiteSpace(inspection.SignerSubject) ||
				!IsSha1Thumbprint(inspection.SignerThumbprint))
			{
				throw new InvalidDataException();
			}

			string fileVersion = NormalizeVersion(inspection.FileVersion);
			string productVersion = NormalizeVersion(inspection.ProductVersion);
			string observedSha256 = inspection.Sha256!.ToUpperInvariant();
			string signerThumbprint =
				inspection.SignerThumbprint!.ToUpperInvariant();
			AntigravityCliFingerprint expected =
				sourceProfile.ExecutableFingerprint;

			stage = "PostInspectionFileStateValidation";

			if (HasReparsePoint(afterInspection) ||
				(before != afterInspection))
			{
				throw new InvalidDataException();
			}

			stage = "ExecutablePathValidation";

			if (!string.Equals(
					executableLease.AbsolutePath,
					expected.AbsolutePath,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException();
			}

			stage = "ApprovedShaValidation";

			if (!string.Equals(
					observedSha256,
					approvedSha256,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException();
			}

			stage = "FileVersionValidation";

			if (!string.Equals(
					fileVersion,
					expected.FileVersion,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException();
			}

			stage = "ProductVersionValidation";

			if (!string.Equals(
					productVersion,
					expected.ProductVersion,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException();
			}

			stage = "TrustValidation";

			if (inspection.WinVerifyTrustStatus != 0)
			{
				throw new InvalidDataException();
			}

			stage = "SignerSubjectValidation";

			if (!string.Equals(
					inspection.SignerSubject,
					expected.SignerSubject,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException();
			}

			stage = "SignerThumbprintValidation";

			if (!string.Equals(
					signerThumbprint,
					expected.SignerThumbprint,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException();
			}

			stage = "VersionProbe";
			string cliVersion = await versionProbe.ProbeAsync(
				executableLease.AbsolutePath,
				cancellationToken);
			AntigravityExecutableFileState afterProbe =
				executableInspector.CaptureFileState(executableLease.AbsolutePath);

			stage = "PostProbeFileStateValidation";

			if (HasReparsePoint(afterProbe) ||
				(afterInspection != afterProbe))
			{
				throw new InvalidDataException();
			}

			stage = "CliVersionObservationValidation";
			string normalizedCliVersion = cliVersion.Trim();

			if ((normalizedCliVersion.Length == 0) ||
				(normalizedCliVersion.Length > MaximumCliVersionLength) ||
				normalizedCliVersion.Any(char.IsControl))
			{
				throw new InvalidDataException();
			}

			AntigravityCliFingerprint promotedFingerprint = expected with
			{
				CliVersion = normalizedCliVersion,
				Sha256 = observedSha256,
				WinVerifyTrustStatus = inspection.WinVerifyTrustStatus,
				SignerThumbprint = signerThumbprint
			};
			AntigravityLiveR0ProfileFile promotedProfile = sourceProfile with
			{
				ExecutableFingerprint = promotedFingerprint
			};
			stage = "Serialization";
			outputBytes = AntigravityLiveR0ProfileJson.Serialize(promotedProfile);

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

				return new(false, observedSha256);
			}

			stage = "TemporaryWrite";
			temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(output)}.fingerprint.{Guid.NewGuid():N}.tmp");
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

			return new(true, observedSha256);
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

			ZeroBytes(hmacKey);
			ZeroBytes(outputBytes);
			ZeroBytes(sourceBytes);
		}
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
					MaximumProfileBytes,
					cancellationToken);

			if (!CryptographicOperations.FixedTimeEquals(
					observedBytes,
					expectedBytes.Span))
			{
				return false;
			}

			AntigravityLiveR0ProfileFile profile =
				AntigravityLiveR0ProfileJson.Deserialize(observedBytes);
			(AntigravityLiveR0Profile _, byte[] loadedHmacKey) =
				await profile.ToProfileAsync(path, cancellationToken);
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

	private static bool IsFingerprint(string value)
	{
		return (value.Length == 64) && value.All(Uri.IsHexDigit);
	}

	private static bool IsSha1Thumbprint(string? value)
	{
		return (value?.Length == 40) && value.All(Uri.IsHexDigit);
	}

	private static bool HasReparsePoint(AntigravityExecutableFileState state)
	{
		return state.HasReparsePoint ||
			((state.Attributes & FileAttributes.ReparsePoint) != 0);
	}

	private static string NormalizeVersion(string? value)
	{
		return string.IsNullOrWhiteSpace(value)
			? AntigravityCliCapabilityValidator.AbsentVersionSentinel
			: value.Trim();
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
