using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class VerifiedUpdaterArtifactLease : IDisposable
{
	private FileStream? _stream;

	internal string ExecutablePath { get; }

	internal VerifiedUpdaterArtifactLease(
		string executablePath,
		FileStream stream)
	{
		ExecutablePath = executablePath;
		_stream = stream;
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref _stream, null)?.Dispose();
	}
}

internal sealed class UpdaterArtifactCache
{
	private const string CacheDirectoryName = "updater-cache";
	private readonly OnlineUpdateClient _onlineClient;

	internal UpdaterArtifactCache(OnlineUpdateClient onlineClient)
	{
		ArgumentNullException.ThrowIfNull(onlineClient);
		_onlineClient = onlineClient;
	}

	internal async Task<VerifiedUpdaterArtifactLease> GetOrDownloadAsync(
		UpdateReleaseArtifact artifact,
		string installRoot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
		string normalizedInstallRoot = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(installRoot));
		string cacheRoot = Path.Combine(
			normalizedInstallRoot,
			CacheDirectoryName);
		string generationRoot = Path.Combine(cacheRoot, artifact.Sha256);
		string finalPath = Path.Combine(generationRoot, artifact.FileName);
		string temporaryPath = finalPath + "." +
			Guid.NewGuid().ToString("N") + ".download";

		EnsureSafeDirectory(normalizedInstallRoot);
		EnsureSafeDirectory(cacheRoot);
		EnsureSafeDirectory(generationRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(finalPath, mustExist: false);

		if (File.Exists(finalPath))
		{
			return await VerifyAndLeaseAsync(
				finalPath,
				artifact,
				cancellationToken);
		}

		try
		{
			await _onlineClient.DownloadArtifactAsync(
				artifact,
				temporaryPath,
				cancellationToken);

			try
			{
				File.Move(temporaryPath, finalPath);
			}
			catch (IOException) when (File.Exists(finalPath))
			{
				DeleteOwnedTemporaryFile(temporaryPath);
			}

			return await VerifyAndLeaseAsync(
				finalPath,
				artifact,
				cancellationToken);
		}
		finally
		{
			DeleteOwnedTemporaryFile(temporaryPath);
		}
	}

	private static void EnsureSafeDirectory(string path)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(path);
		Directory.CreateDirectory(path);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(path);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(path, mustExist: true);
	}

	private static async Task<VerifiedUpdaterArtifactLease> VerifyAndLeaseAsync(
		string path,
		UpdateReleaseArtifact artifact,
		CancellationToken cancellationToken)
	{
		UpdateTransaction.ThrowIfNotOrdinaryFile(path, mustExist: true);
		FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		try
		{
			if (stream.Length != artifact.SizeBytes)
			{
				throw new InvalidDataException(
					"The cached updater size does not match the update feed.");
			}

			byte[] hash = await SHA256.HashDataAsync(
				stream,
				cancellationToken);
			string observedHash = Convert.ToHexString(hash).ToLowerInvariant();

			if (!string.Equals(
					observedHash,
					artifact.Sha256,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The cached updater SHA-256 does not match the update feed.");
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(path, mustExist: true);
			return new VerifiedUpdaterArtifactLease(path, stream);
		}
		catch
		{
			await stream.DisposeAsync();
			throw;
		}
	}

	private static void DeleteOwnedTemporaryFile(string temporaryPath)
	{
		if (!File.Exists(temporaryPath))
		{
			return;
		}

		UpdateTransaction.ThrowIfNotOrdinaryFile(
			temporaryPath,
			mustExist: true);
		File.Delete(temporaryPath);
	}
}
