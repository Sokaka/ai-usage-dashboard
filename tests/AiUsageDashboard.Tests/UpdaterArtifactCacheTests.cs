using System.Net;
using System.Security.Cryptography;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterArtifactCacheTests
{
	[Fact]
	public async Task GetOrDownloadAsync_DownloadsOnceAndReusesVerifiedCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] content = [1, 2, 3, 4, 5];
		CountingArtifactHandler handler = new(content);
		using HttpClient httpClient = new(handler);
		OnlineUpdateClient onlineClient = new(
			httpClient,
			allowInsecureLoopbackForTests: true);
		UpdaterArtifactCache cache = new(onlineClient);
		UpdateReleaseArtifact artifact = CreateArtifact(content);

		using VerifiedUpdaterArtifactLease firstLease =
			await cache.GetOrDownloadAsync(
			artifact,
			temporaryDirectory.Path);
		using VerifiedUpdaterArtifactLease secondLease =
			await cache.GetOrDownloadAsync(
			artifact,
			temporaryDirectory.Path);

		Assert.Equal(firstLease.ExecutablePath, secondLease.ExecutablePath);
		Assert.Equal(
			content,
			await File.ReadAllBytesAsync(firstLease.ExecutablePath));
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task GetOrDownloadAsync_WithTamperedCachedFile_RejectsWithoutDownload()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] content = [1, 2, 3, 4, 5];
		CountingArtifactHandler handler = new(content);
		using HttpClient httpClient = new(handler);
		OnlineUpdateClient onlineClient = new(
			httpClient,
			allowInsecureLoopbackForTests: true);
		UpdaterArtifactCache cache = new(onlineClient);
		UpdateReleaseArtifact artifact = CreateArtifact(content);
		string cachedPath;
		using (VerifiedUpdaterArtifactLease lease =
			await cache.GetOrDownloadAsync(
				artifact,
				temporaryDirectory.Path))
		{
			cachedPath = lease.ExecutablePath;
		}
		await File.WriteAllBytesAsync(cachedPath, [5, 4, 3, 2, 1]);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			cache.GetOrDownloadAsync(artifact, temporaryDirectory.Path));
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task GetOrDownloadAsync_HoldsVerifiedFileAgainstMutationUntilDisposed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] content = [1, 2, 3, 4, 5];
		CountingArtifactHandler handler = new(content);
		using HttpClient httpClient = new(handler);
		OnlineUpdateClient onlineClient = new(
			httpClient,
			allowInsecureLoopbackForTests: true);
		UpdaterArtifactCache cache = new(onlineClient);
		UpdateReleaseArtifact artifact = CreateArtifact(content);
		VerifiedUpdaterArtifactLease lease = await cache.GetOrDownloadAsync(
			artifact,
			temporaryDirectory.Path);

		try
		{
			Assert.Throws<IOException>(() => File.Open(
				lease.ExecutablePath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.Read));
		}
		finally
		{
			lease.Dispose();
		}

		using FileStream writer = File.Open(
			lease.ExecutablePath,
			FileMode.Open,
			FileAccess.Write,
			FileShare.Read);
	}

	private static UpdateReleaseArtifact CreateArtifact(byte[] content)
	{
		string hash = Convert.ToHexString(SHA256.HashData(content))
			.ToLowerInvariant();
		return new UpdateReleaseArtifact(
			"AiUsageDashboard.Updater",
			"1.2.3",
			"win-x64",
			"AiUsageDashboard-Updater-1.2.3-win-x64.exe",
			"http://127.0.0.1/updater.exe",
			content.Length,
			hash,
			"0123456789abcdef0123456789abcdef01234567");
	}

	private sealed class CountingArtifactHandler : HttpMessageHandler
	{
		private readonly byte[] _content;

		internal int RequestCount { get; private set; }

		internal CountingArtifactHandler(byte[] content)
		{
			_content = content;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			RequestCount++;
			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(_content),
				RequestMessage = request
			};
			return Task.FromResult(response);
		}
	}
}
