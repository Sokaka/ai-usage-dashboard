namespace AiUsageDashboard.Tests;

internal static class RepositoryTestPaths
{
	private static readonly Lazy<string> _repositoryRoot =
		new(FindRepositoryRoot);

	internal static string Root => _repositoryRoot.Value;

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);

		while (directory is not null)
		{
			if (File.Exists(Path.Combine(
					directory.FullName,
					"AiUsageDashboard.sln")) &&
				File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
				Directory.Exists(Path.Combine(directory.FullName, "src")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException(
			"Could not locate the AiUsageDashboard repository root.");
	}
}
