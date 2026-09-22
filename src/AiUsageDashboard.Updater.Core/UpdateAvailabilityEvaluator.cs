namespace AiUsageDashboard.Updater.Core;

internal enum UpdateAvailabilityStatus
{
	UnknownCurrentVersion,
	UpToDate,
	UpdateAvailable
}

internal static class UpdateAvailabilityEvaluator
{
	internal static UpdateAvailabilityStatus EvaluateManagedPayload(
		UpdateManifest? installed,
		UpdateManifest available)
	{
		ArgumentNullException.ThrowIfNull(available);
		available.Validate();

		if (installed is null)
		{
			return UpdateAvailabilityStatus.UpdateAvailable;
		}

		installed.Validate();

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
			return UpdateAvailabilityStatus.UpdateAvailable;
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

		return UpdateAvailabilityStatus.UpToDate;
	}

	internal static UpdateAvailabilityStatus EvaluateUnmanagedProductVersion(
		string? currentProductVersion,
		UpdateManifest available)
	{
		ArgumentNullException.ThrowIfNull(available);
		available.Validate();

		if (!TryParseProductVersion(
				currentProductVersion,
				out ReleaseVersion? currentVersion) ||
			(currentVersion is null))
		{
			return UpdateAvailabilityStatus.UnknownCurrentVersion;
		}

		ReleaseVersion availableVersion = ReleaseVersion.Parse(
			available.Version);
		return availableVersion.CompareTo(currentVersion) > 0
			? UpdateAvailabilityStatus.UpdateAvailable
			: UpdateAvailabilityStatus.UpToDate;
	}

	private static bool TryParseProductVersion(
		string? productVersion,
		out ReleaseVersion? version)
	{
		version = null;

		if (string.IsNullOrEmpty(productVersion))
		{
			return false;
		}

		int buildMetadataSeparator = productVersion.IndexOf('+');
		if (buildMetadataSeparator < 0)
		{
			bool wasParsed = ReleaseVersion.TryParse(
				productVersion,
				out ReleaseVersion parsedVersion);
			version = wasParsed ? parsedVersion : null;
			return wasParsed;
		}

		if ((buildMetadataSeparator == 0) ||
			(productVersion.IndexOf('+', buildMetadataSeparator + 1) >= 0) ||
			!IsValidBuildMetadata(productVersion[(buildMetadataSeparator + 1)..]))
		{
			return false;
		}

		bool wasCoreVersionParsed = ReleaseVersion.TryParse(
			productVersion[..buildMetadataSeparator],
			out ReleaseVersion parsedCoreVersion);
		version = wasCoreVersionParsed ? parsedCoreVersion : null;
		return wasCoreVersionParsed;
	}

	private static bool IsValidBuildMetadata(string buildMetadata)
	{
		string[] identifiers = buildMetadata.Split('.');
		return (identifiers.Length > 0) && identifiers.All(identifier =>
			(identifier.Length > 0) &&
			identifier.All(character =>
				char.IsAsciiLetterOrDigit(character) || (character == '-')));
	}
}
