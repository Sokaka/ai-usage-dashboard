namespace AiUsageDashboard.Updater.Core;

internal static class MaintenanceUpdaterPathContract
{
	internal const string ApplicationFileName =
		"AiUsageDashboard.Updater.exe";
	internal const string DirectoryName = "AiUsageDashboardUpdater";

	internal static string GetCanonicalRoot()
	{
		string localApplicationDataDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
		{
			throw new InvalidOperationException(
				"The current user's local application data directory is unavailable.");
		}

		return GetCanonicalRoot(localApplicationDataDirectory);
	}

	internal static string GetCanonicalRoot(
		string localApplicationDataDirectory)
	{
		return Path.Combine(
			NormalizeDirectory(localApplicationDataDirectory),
			"Programs",
			DirectoryName);
	}

	internal static string GetCanonicalExecutablePath()
	{
		return Path.Combine(GetCanonicalRoot(), ApplicationFileName);
	}

	internal static string GetCanonicalExecutablePath(
		string localApplicationDataDirectory)
	{
		return Path.Combine(
			GetCanonicalRoot(localApplicationDataDirectory),
			ApplicationFileName);
	}

	private static string NormalizeDirectory(string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

		if (!Path.IsPathFullyQualified(directoryPath))
		{
			throw new ArgumentException(
				"The local application data directory must be fully qualified.",
				nameof(directoryPath));
		}

		return Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(directoryPath));
	}
}
