using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal static class AppCurrentVersionParser
{
	internal static bool TryParse(
		string? productVersion,
		out string normalizedVersion)
	{
		normalizedVersion = string.Empty;
		if (string.IsNullOrWhiteSpace(productVersion))
		{
			return false;
		}

		string candidate = productVersion.Trim();
		int buildMetadataSeparator = candidate.IndexOf('+');
		if (buildMetadataSeparator >= 0)
		{
			if ((buildMetadataSeparator == 0) ||
				(buildMetadataSeparator == candidate.Length - 1) ||
				(candidate.IndexOf('+', buildMetadataSeparator + 1) >= 0) ||
				!IsValidBuildMetadata(
					candidate[(buildMetadataSeparator + 1)..]))
			{
				return false;
			}

			candidate = candidate[..buildMetadataSeparator];
		}

		if (!ReleaseVersion.TryParse(candidate, out ReleaseVersion version))
		{
			return false;
		}

		normalizedVersion = version.ToString();
		return true;
	}

	private static bool IsValidBuildMetadata(string buildMetadata)
	{
		return buildMetadata.Split('.').All(identifier =>
			(identifier.Length > 0) &&
			identifier.All(character =>
				char.IsAsciiLetterOrDigit(character) ||
				(character == '-')));
	}
}
