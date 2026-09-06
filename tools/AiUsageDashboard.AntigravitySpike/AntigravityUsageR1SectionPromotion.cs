using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityUsageR1SectionPromotionMetadata(
	bool WasWritten,
	string ApprovedDraftFingerprint,
	string SchemaFingerprint);

internal static class AntigravityUsageR1SectionPromotion
{
	private const int MaximumFileBytes = 1024 * 1024;
	private const int MaximumPrivateBundleBytes = 4 * 1024 * 1024;

	internal static async Task<AntigravityUsageR1SectionPromotionMetadata>
		PromoteAsync(
			string privateBundlePath,
			string draftPath,
			string outputPath,
			string approvedDraftFingerprint,
			ReadOnlyMemory<byte> draftFingerprintHmacKey,
			CancellationToken cancellationToken = default)
	{
		byte[]? privateBundleBytes = null;
		byte[]? draftBytes = null;
		byte[]? reviewedBytes = null;
		string? temporaryPath = null;

		try
		{
			if (!AntigravityUsageR1SchemaParser.IsFingerprint(
					approvedDraftFingerprint) ||
				draftFingerprintHmacKey.Length < 32 ||
				!Path.IsPathFullyQualified(privateBundlePath) ||
				!Path.IsPathFullyQualified(draftPath) ||
				!Path.IsPathFullyQualified(outputPath))
			{
				throw new InvalidDataException();
			}

			string absoluteBundlePath = Path.GetFullPath(privateBundlePath);
			string absoluteDraftPath = Path.GetFullPath(draftPath);
			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string draftDirectory = Path.GetDirectoryName(absoluteDraftPath) ??
				throw new InvalidDataException();
			string outputDirectory = Path.GetDirectoryName(absoluteOutputPath) ??
				throw new InvalidDataException();

			if (string.Equals(
					absoluteBundlePath,
					absoluteDraftPath,
					StringComparison.OrdinalIgnoreCase) ||
				string.Equals(
					absoluteBundlePath,
					absoluteOutputPath,
					StringComparison.OrdinalIgnoreCase) ||
				string.Equals(
					absoluteDraftPath,
					absoluteOutputPath,
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					draftDirectory,
					Path.GetDirectoryName(absoluteBundlePath),
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					draftDirectory,
					outputDirectory,
					StringComparison.OrdinalIgnoreCase) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(draftDirectory) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteBundlePath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteDraftPath) ||
				(File.Exists(absoluteOutputPath) &&
					!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath)))
			{
				throw new InvalidDataException();
			}

			privateBundleBytes = await AntigravityUsageR1CalibrationFile
				.ReadBoundedAsync(
					absoluteBundlePath,
					MaximumPrivateBundleBytes,
					requirePrivateFile: true,
					cancellationToken);
			AntigravityUsageR1PrivateScreenBundleFile privateBundle =
				AntigravityUsageR1CalibrationJson.DeserializePrivateBundle(
					privateBundleBytes);
			draftBytes = await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
				absoluteDraftPath,
				MaximumFileBytes,
				requirePrivateFile: true,
				cancellationToken);
			AntigravityUsageR1SectionPrivateDraftFile draft =
				AntigravityUsageR1SectionPrivateDraftJson.Deserialize(draftBytes);

			if (!AntigravityUsageR1SectionPrivateDraftGenerator
				.IsExactApprovedDraft(
					privateBundle,
					draftBytes,
					approvedDraftFingerprint,
					draftFingerprintHmacKey))
			{
				throw new InvalidDataException();
			}

			AntigravityUsageR1SectionSpec frozen =
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
					draft.Spec);
			string schemaFingerprint =
				AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
					frozen);
			AntigravityUsageR1SectionSpecFile reviewed = new(
				AntigravityUsageR1SectionSchemaParser.ReviewedSpecFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				frozen,
				privateBundle.CaptureContractFingerprint,
				approvedDraftFingerprint);
			reviewedBytes = AntigravityUsageR1SectionCalibrationJson
				.SerializeReviewedSpec(reviewed);

			if (File.Exists(absoluteOutputPath))
			{
				byte[] existing = await AntigravityUsageR1CalibrationFile
					.ReadBoundedAsync(
						absoluteOutputPath,
						MaximumFileBytes,
						requirePrivateFile: true,
						cancellationToken);

				try
				{
					_ = AntigravityUsageR1SectionCalibrationJson
						.DeserializeReviewedSpec(existing);

					if (!existing.AsSpan().SequenceEqual(reviewedBytes))
					{
						throw new InvalidDataException();
					}
				}
				finally
				{
					CryptographicOperations.ZeroMemory(existing);
				}

				return new(false, approvedDraftFingerprint, schemaFingerprint);
			}

			temporaryPath = Path.Combine(
				draftDirectory,
				$".{Path.GetFileName(absoluteOutputPath)}.promotion.{Guid.NewGuid():N}.tmp");
			await using (FileStream create = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await create.FlushAsync(cancellationToken);
				create.Flush(flushToDisk: true);
			}

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(temporaryPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(temporaryPath))
			{
				throw new IOException();
			}

			await File.WriteAllBytesAsync(
				temporaryPath,
				reviewedBytes,
				cancellationToken);
			File.Move(temporaryPath, absoluteOutputPath);
			temporaryPath = null;

			if (!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath))
			{
				throw new IOException();
			}

			return new(true, approvedDraftFingerprint, schemaFingerprint);
		}
		finally
		{
			if (temporaryPath is not null)
			{
				AntigravityUsageR1CalibrationFile.TryDelete(temporaryPath);
			}

			if (privateBundleBytes is not null)
			{
				CryptographicOperations.ZeroMemory(privateBundleBytes);
			}

			if (draftBytes is not null)
			{
				CryptographicOperations.ZeroMemory(draftBytes);
			}

			if (reviewedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(reviewedBytes);
			}
		}
	}
}
