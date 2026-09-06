using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class UpdateInstallLock : IDisposable
{
	private const string LockFileName = ".updater.lock";
	private readonly FileStream _stream;

	internal string LockFilePath { get; }

	private UpdateInstallLock(FileStream stream, string lockFilePath)
	{
		_stream = stream;
		LockFilePath = lockFilePath;
	}

	internal static UpdateInstallLock Acquire(string installRootPath)
	{
		return Acquire(installRootPath, createInstallRoot: true);
	}

	internal static UpdateInstallLock AcquireExisting(string installRootPath)
	{
		return Acquire(installRootPath, createInstallRoot: false);
	}

	private static UpdateInstallLock Acquire(
		string installRootPath,
		bool createInstallRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installRootPath);
		string resolvedInstallRoot = UpdateTransaction.NormalizeInstallRoot(
			installRootPath);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
			resolvedInstallRoot);

		if (!Directory.Exists(resolvedInstallRoot))
		{
			if (!createInstallRoot)
			{
				throw new DirectoryNotFoundException(
					"The managed installation root was not found.");
			}

			Directory.CreateDirectory(resolvedInstallRoot);
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
			resolvedInstallRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			resolvedInstallRoot,
			mustExist: true);
		string lockPath = Path.Combine(resolvedInstallRoot, LockFileName);

		if (Directory.Exists(lockPath))
		{
			throw new IOException("Updater lock path is not a file.");
		}

		if (File.Exists(lockPath) &&
			((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0))
		{
			throw new InvalidDataException(
				"Updater lock file cannot be a reparse point.");
		}

		try
		{
			FileStream stream = new(
				lockPath,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None);

			try
			{
				UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
					resolvedInstallRoot);
				UpdateTransaction.ThrowIfNotOrdinaryFile(
					lockPath,
					mustExist: true);
				return new UpdateInstallLock(stream, lockPath);
			}
			catch
			{
				stream.Dispose();
				throw;
			}
		}
		catch (IOException exception)
		{
			throw new UpdateInstallLockException(
				"Another updater is already using this installation root.",
				exception);
		}
	}

	public void Dispose()
	{
		_stream.Dispose();
	}
}

internal sealed class UpdateInstallLockException : IOException
{
	internal UpdateInstallLockException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
