using System.Reflection;
using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityReviewedPackageManifestCatalog
{
	private const int MaximumManifestBytes = 1024 * 1024;
	private const string ResourceName =
		"AiUsageDashboard.Antigravity.Resources.AntigravityReviewedPackageManifest.json";
	private static readonly Lazy<AntigravityReviewedPackageManifestFile>
		Manifest = new(
			LoadCore,
			LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<IReadOnlyList<
		AntigravityReviewedPackageContract>> ExpandedContracts = new(
			LoadExpandedContractsCore,
			LazyThreadSafetyMode.ExecutionAndPublication);

	internal static IReadOnlyList<AntigravityReviewedPackageContract>
		Contracts => ExpandedContracts.Value;

	internal static AntigravityReviewedPackageContract? FindMatchingContract(
		AntigravityLiveR1SectionProfileFile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		AntigravityReviewedPackageContract[] matches = Contracts
			.Where(contract => Matches(contract, profile))
			.ToArray();
		return matches.Length == 1 ? matches[0] : null;
	}

	private static AntigravityReviewedPackageManifestFile LoadCore()
	{
		Assembly assembly =
			typeof(AntigravityReviewedPackageManifestCatalog).Assembly;
		using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
			throw new InvalidDataException(
				"The reviewed AGY package manifest is unavailable.");

		if (!stream.CanRead ||
			(stream.Length <= 0) ||
			(stream.Length > MaximumManifestBytes))
		{
			throw new InvalidDataException(
				"The reviewed AGY package manifest size is invalid.");
		}

		byte[] bytes = new byte[checked((int)stream.Length)];

		try
		{
			stream.ReadExactly(bytes);

			if (stream.ReadByte() != -1)
			{
				throw new InvalidDataException(
					"The reviewed AGY package manifest changed while it was read.");
			}

			return AntigravityReviewedPackageManifestJson.Deserialize(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static IReadOnlyList<AntigravityReviewedPackageContract>
		LoadExpandedContractsCore()
	{
		List<AntigravityReviewedPackageContract> contracts = new();

		foreach (AntigravityReviewedPackageContract contract in
			Manifest.Value.Contracts)
		{
			contracts.Add(contract with { CompatibleBuilds = null });

			if (contract.CompatibleBuilds is null)
			{
				continue;
			}

			foreach (AntigravityReviewedPackageCompatibleBuild compatibleBuild in
				contract.CompatibleBuilds)
			{
				contracts.Add(new AntigravityReviewedPackageContract(
					compatibleBuild.ContractId,
					compatibleBuild.Executable,
					contract.ExpectedPromptStructuralFingerprint,
					contract.UsageLayout,
					compatibleBuild.ContractFingerprint));
			}
		}

		return Array.AsReadOnly(contracts.ToArray());
	}

	private static bool Matches(
		AntigravityReviewedPackageContract contract,
		AntigravityLiveR1SectionProfileFile profile)
	{
		AntigravityCliFingerprint executable = profile.ExecutableFingerprint;
		AntigravityReviewedPackageExecutable reviewed = contract.Executable;
		AntigravityUsageR1SectionLayoutFile layout =
			profile.ReviewedUsageLayout;

		return
			string.Equals(
				reviewed.CliVersion,
				executable.CliVersion,
				StringComparison.Ordinal) &&
			string.Equals(
				reviewed.FileVersion,
				executable.FileVersion,
				StringComparison.Ordinal) &&
			string.Equals(
				reviewed.ProductVersion,
				executable.ProductVersion,
				StringComparison.Ordinal) &&
			string.Equals(
				reviewed.Sha256,
				executable.Sha256,
				StringComparison.Ordinal) &&
			(reviewed.WinVerifyTrustStatus ==
				executable.WinVerifyTrustStatus) &&
			string.Equals(
				reviewed.SignerSubject,
				executable.SignerSubject,
				StringComparison.Ordinal) &&
			string.Equals(
				reviewed.SignerThumbprint,
				executable.SignerThumbprint,
				StringComparison.Ordinal) &&
			string.Equals(
				contract.ExpectedPromptStructuralFingerprint,
				profile.PromptGuard.ExpectedPromptStructuralFingerprint,
				StringComparison.Ordinal) &&
			string.Equals(
				contract.UsageLayout.SchemaFingerprint,
				layout.SchemaFingerprint,
				StringComparison.Ordinal) &&
			(layout.ExpectedPageFingerprints.Count == 1) &&
			(contract.UsageLayout.ExpectedPageFingerprints.Count == 1) &&
			string.Equals(
				contract.UsageLayout.ExpectedPageFingerprints[0],
				layout.ExpectedPageFingerprints[0],
				StringComparison.Ordinal);
	}
}
