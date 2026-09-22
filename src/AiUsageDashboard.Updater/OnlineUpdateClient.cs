using System.Buffers;
using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class OnlineUpdateClient
{
	private const int DownloadBufferSizeBytes = 81920;
	private readonly bool _allowInsecureLoopbackForTests;
	private readonly HttpClient _httpClient;

	internal OnlineUpdateClient(
		HttpClient httpClient,
		bool allowInsecureLoopbackForTests = false)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(
			nameof(httpClient));
		_allowInsecureLoopbackForTests = allowInsecureLoopbackForTests;
	}

	internal async Task DownloadArtifactAsync(
		UpdateReleaseArtifact artifact,
		string destinationPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
		ValidateArtifact(artifact);
		Uri downloadUri = artifact.DownloadUri;
		EnsureAllowedUri(downloadUri);
		(string resolvedDestinationPath, string parentPath) =
			ValidateDestination(destinationPath);

		using HttpRequestMessage request = new(HttpMethod.Get, downloadUri);
		using HttpResponseMessage response = await _httpClient.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken);
		_ = GetAndValidateResponseUri(response);
		response.EnsureSuccessStatusCode();

		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength.HasValue &&
			(contentLength.Value != artifact.SizeBytes))
		{
			throw new InvalidDataException(
				"Update artifact Content-Length does not match the release feed.");
		}

		ValidateDestination(resolvedDestinationPath);
		bool ownsDestination = false;

		try
		{
			await using FileStream destinationStream = new(
				resolvedDestinationPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				DownloadBufferSizeBytes,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			ownsDestination = true;
			ValidateCreatedDestination(resolvedDestinationPath, parentPath);

			await using Stream artifactStream =
				await response.Content.ReadAsStreamAsync(cancellationToken);
			using IncrementalHash sha256 = IncrementalHash.CreateHash(
				HashAlgorithmName.SHA256);
			byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSizeBytes);

			try
			{
				long bytesWritten = await CopyAndHashExactlyAsync(
					artifactStream,
					destinationStream,
					sha256,
					buffer,
					artifact.SizeBytes,
					cancellationToken);
				if (bytesWritten != artifact.SizeBytes)
				{
					throw new InvalidDataException(
						"Update artifact is truncated.");
				}

				string actualSha256 = Convert.ToHexString(
					sha256.GetHashAndReset()).ToLowerInvariant();
				if (!string.Equals(
						actualSha256,
						artifact.Sha256,
						StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException(
						"Update artifact SHA-256 does not match the release feed.");
				}

				await destinationStream.FlushAsync(cancellationToken);
				ValidateCreatedDestination(
					resolvedDestinationPath,
					parentPath);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
		catch
		{
			if (ownsDestination)
			{
				TryDeleteOwnedDestination(
					resolvedDestinationPath,
					parentPath);
			}

			throw;
		}
	}

	private static async Task<long> CopyAndHashExactlyAsync(
		Stream source,
		Stream destination,
		IncrementalHash sha256,
		byte[] buffer,
		long expectedSizeBytes,
		CancellationToken cancellationToken)
	{
		long totalBytes = 0;

		while (true)
		{
			long remainingBytes = expectedSizeBytes - totalBytes;
			int maximumRead = remainingBytes >= buffer.Length
				? buffer.Length
				: checked((int)remainingBytes + 1);
			int bytesRead = await source.ReadAsync(
				buffer.AsMemory(0, maximumRead),
				cancellationToken);
			if (bytesRead == 0)
			{
				return totalBytes;
			}

			if (bytesRead > (expectedSizeBytes - totalBytes))
			{
				throw new InvalidDataException(
					"Update artifact exceeds the size declared by the release feed.");
			}

			await destination.WriteAsync(
				buffer.AsMemory(0, bytesRead),
				cancellationToken);
			sha256.AppendData(buffer, 0, bytesRead);
			totalBytes += bytesRead;
		}
	}

	private Uri GetAndValidateResponseUri(
		HttpResponseMessage response)
	{
		Uri responseUri = response.RequestMessage?.RequestUri ??
			throw new InvalidDataException(
				"Update server response does not identify its final URI.");
		EnsureAllowedUri(responseUri);
		return responseUri;
	}

	private static void ValidateArtifact(UpdateReleaseArtifact artifact)
	{
		if (!Uri.TryCreate(
				artifact.DownloadUrl,
				UriKind.Absolute,
				out _))
		{
			throw new InvalidDataException(
				"Update artifact download URL is invalid.");
		}

		if (artifact.SizeBytes <= 0)
		{
			throw new InvalidDataException(
				"Update artifact size must be positive.");
		}

		if (string.IsNullOrEmpty(artifact.Sha256) ||
			(artifact.Sha256.Length != 64) ||
			!artifact.Sha256.All(Uri.IsHexDigit))
		{
			throw new InvalidDataException(
				"Update artifact SHA-256 is invalid.");
		}
	}

	private static (string DestinationPath, string ParentPath)
		ValidateDestination(string destinationPath)
	{
		string fullPath = Path.GetFullPath(destinationPath);
		string? parentPath = Path.GetDirectoryName(fullPath);

		if (string.IsNullOrEmpty(parentPath) ||
			string.IsNullOrEmpty(Path.GetFileName(fullPath)))
		{
			throw new ArgumentException(
				"Download destination must name a file within a parent directory.",
				nameof(destinationPath));
		}

		if (Path.GetFileName(fullPath).Contains(Path.VolumeSeparatorChar))
		{
			throw new InvalidDataException(
				"Download destination must be an ordinary file path.");
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(parentPath, mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: false);
		return (fullPath, parentPath);
	}

	private static void ValidateCreatedDestination(
		string destinationPath,
		string parentPath)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(parentPath, mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(destinationPath, mustExist: true);
	}

	private static void TryDeleteOwnedDestination(
		string destinationPath,
		string parentPath)
	{
		try
		{
			UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
			UpdateTransaction.ThrowIfNotOrdinaryDirectory(
				parentPath,
				mustExist: true);
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				destinationPath,
				mustExist: true);
			File.Delete(destinationPath);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException)
		{
			// 清理失敗不可掩蓋原始下載錯誤；保留檔案供診斷。
		}
	}

	private void EnsureAllowedUri(Uri uri)
	{
		UpdateHttpUriPolicy.EnsureAllowed(
			uri,
			_allowInsecureLoopbackForTests);
	}
}
