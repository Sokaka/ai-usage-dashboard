using System.IO;

namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityManagedFilePathGuard
{
	internal static void CreateDirectoryAndEnsureSafe(
		string directoryPath,
		string pathDescription)
	{
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription);
		Directory.CreateDirectory(directoryPath);
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription);
	}

	internal static void EnsureExistingFilePathIsSafe(
		string filePath,
		string pathDescription)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(pathDescription);

		string fullFilePath = Path.GetFullPath(filePath);
		string? directoryPath = Path.GetDirectoryName(fullFilePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new IOException($"{pathDescription} has no parent directory.");
		}

		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription);
		EnsureExistingPathIsRegularFileOrMissing(
			fullFilePath,
			pathDescription);
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription);
	}

	private static void EnsureExistingDirectoryAncestorsAreNotReparsePoints(
		string directoryPath,
		string pathDescription)
	{
		DirectoryInfo? current = new(Path.GetFullPath(directoryPath));
		while (current is not null)
		{
			if (current.Exists &&
				(current.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException(
					$"{pathDescription} must not traverse a reparse point.");
			}

			current = current.Parent;
		}
	}

	private static void EnsureExistingPathIsRegularFileOrMissing(
		string filePath,
		string pathDescription)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException($"{pathDescription} has an invalid file type.");
			}
		}
		catch (FileNotFoundException)
		{
			// A missing managed file is valid until its owning operation creates it.
		}
		catch (DirectoryNotFoundException)
		{
			// A missing managed directory is valid until a save creates it safely.
		}
	}
}
