using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal enum OnlinePayloadUpdateAction
{
	InstallAvailable,
	UseInstalled
}

internal static class OnlinePayloadUpdatePolicy
{
	internal static OnlinePayloadUpdateAction Evaluate(
		UpdateManifest? installed,
		UpdateManifest available)
	{
		ArgumentNullException.ThrowIfNull(available);

		if (installed is null)
		{
			return OnlinePayloadUpdateAction.InstallAvailable;
		}

		if (installed.ReleaseSequence.HasValue &&
			available.ReleaseSequence.HasValue &&
			(available.ReleaseSequence.Value < installed.ReleaseSequence.Value))
		{
			throw new InvalidDataException(
				"The update feed release sequence is older than the installed release.");
		}

		int versionComparison = ReleaseVersion.Parse(available.Version)
			.CompareTo(ReleaseVersion.Parse(installed.Version));

		if (versionComparison > 0)
		{
			return OnlinePayloadUpdateAction.InstallAvailable;
		}

		if (versionComparison < 0)
		{
			throw new InvalidDataException(
				"The update feed would downgrade the installed application.");
		}

		if ((installed.ArchiveSizeBytes != available.ArchiveSizeBytes) ||
			!string.Equals(
				installed.ArchiveSha256,
				available.ArchiveSha256,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The update feed reuses the installed version with different bytes.");
		}

		return OnlinePayloadUpdateAction.UseInstalled;
	}
}
