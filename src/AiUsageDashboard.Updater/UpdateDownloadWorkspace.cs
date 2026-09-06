using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class UpdateDownloadWorkspace
{
	private const string DownloadDirectoryName = "downloads";

	internal string DirectoryPath { get; }

	private UpdateDownloadWorkspace(string directoryPath)
	{
		DirectoryPath = directoryPath;
	}

	internal static UpdateDownloadWorkspace Create(string installRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
		string normalizedInstallRoot = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(installRoot));
		string? volumeRoot = Path.GetPathRoot(normalizedInstallRoot);

		if (string.Equals(
			normalizedInstallRoot,
			volumeRoot,
			StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				"The install root cannot be a volume root.",
				nameof(installRoot));
		}

		string downloadRoot = Path.Combine(
			normalizedInstallRoot,
			DownloadDirectoryName);
		string workspacePath = Path.Combine(
			downloadRoot,
			Guid.NewGuid().ToString("N"));
		EnsureSafeDirectory(normalizedInstallRoot);
		EnsureSafeDirectory(downloadRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			workspacePath,
			mustExist: false);
		Directory.CreateDirectory(workspacePath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			workspacePath,
			mustExist: true);
		return new UpdateDownloadWorkspace(workspacePath);
	}

	internal string GetFilePath(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

		if (!string.Equals(
				fileName,
				Path.GetFileName(fileName),
				StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"A download filename cannot contain a path.",
				nameof(fileName));
		}

		string path = Path.Combine(DirectoryPath, fileName);
		UpdateTransaction.ThrowIfNotOrdinaryFile(path, mustExist: false);
		return path;
	}

	internal string? TryCleanup()
	{
		try
		{
			if (!Directory.Exists(DirectoryPath))
			{
				return null;
			}

			UpdateTransaction.ThrowIfNotOrdinaryDirectory(
				DirectoryPath,
				mustExist: true);
			FileSystemInfo[] entries = new DirectoryInfo(DirectoryPath)
				.GetFileSystemInfos("*", SearchOption.AllDirectories);

			if (entries.Any(entry =>
				(entry.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				throw new IOException(
					"The owned download workspace contains a reparse point.");
			}

			Directory.Delete(DirectoryPath, recursive: true);
			return null;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException)
		{
			return exception.Message;
		}
	}

	private static void EnsureSafeDirectory(string path)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(path);
		Directory.CreateDirectory(path);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(path);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(path, mustExist: true);
	}
}
