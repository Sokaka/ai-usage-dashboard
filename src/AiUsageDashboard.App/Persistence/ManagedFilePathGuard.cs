using System.IO;

namespace AiUsageDashboard.App.Persistence;

internal static class ManagedFilePathGuard
{
	internal static void CreateDirectoryAndEnsureSafe(
		string directoryPath,
		string pathDescription,
		Func<string, Exception>? rejectionExceptionFactory = null)
	{
		rejectionExceptionFactory ??= CreateDefaultRejectionException;
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription,
			rejectionExceptionFactory);
		Directory.CreateDirectory(directoryPath);
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription,
			rejectionExceptionFactory);
	}

	internal static void EnsureExistingFilePathIsSafe(
		string filePath,
		string pathDescription,
		Func<string, Exception>? rejectionExceptionFactory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(pathDescription);
		rejectionExceptionFactory ??= CreateDefaultRejectionException;

		string fullFilePath = Path.GetFullPath(filePath);
		string? directoryPath = Path.GetDirectoryName(fullFilePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw rejectionExceptionFactory(
				$"{pathDescription} has no parent directory.");
		}

		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription,
			rejectionExceptionFactory);
		EnsureExistingPathIsRegularFileOrMissing(
			fullFilePath,
			pathDescription,
			rejectionExceptionFactory);
		EnsureExistingDirectoryAncestorsAreNotReparsePoints(
			directoryPath,
			pathDescription,
			rejectionExceptionFactory);
	}

	private static void EnsureExistingDirectoryAncestorsAreNotReparsePoints(
		string directoryPath,
		string pathDescription,
		Func<string, Exception> rejectionExceptionFactory)
	{
		DirectoryInfo? current = new(Path.GetFullPath(directoryPath));
		while (current is not null)
		{
			if (current.Exists &&
				(current.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw rejectionExceptionFactory(
					$"{pathDescription} must not traverse a reparse point.");
			}

			current = current.Parent;
		}
	}

	private static void EnsureExistingPathIsRegularFileOrMissing(
		string filePath,
		string pathDescription,
		Func<string, Exception> rejectionExceptionFactory)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw rejectionExceptionFactory(
					$"{pathDescription} has an invalid file type.");
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

	private static Exception CreateDefaultRejectionException(string message)
	{
		return new IOException(message);
	}
}
