using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterOnlineUpdateClientTests : IClassFixture<FeedSigningTestKeys>
{
	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<
			HttpRequestMessage,
			CancellationToken,
			HttpResponseMessage> _responseFactory;

		internal int SendCount { get; private set; }

		internal StubHttpMessageHandler(
			Func<
				HttpRequestMessage,
				CancellationToken,
				HttpResponseMessage> responseFactory)
		{
			_responseFactory = responseFactory;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			SendCount++;
			HttpResponseMessage response = _responseFactory(
				request,
				cancellationToken);
			response.RequestMessage ??= request;
			return Task.FromResult(response);
		}
	}

	private sealed class CancelAfterFirstReadStream : Stream
	{
		private readonly byte[] _firstChunk;
		private bool _wasRead;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		internal CancelAfterFirstReadStream(byte[] firstChunk)
		{
			_firstChunk = firstChunk;
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			if (!_wasRead)
			{
				_wasRead = true;
				_firstChunk.AsMemory().CopyTo(buffer);
				return _firstChunk.Length;
			}

			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	private const string FeedUri =
		"https://updates.example.test/internal/latest.json";
	private readonly FeedSigningTestKeys _signingKeys;

	public UpdaterOnlineUpdateClientTests(FeedSigningTestKeys signingKeys)
	{
		_signingKeys = signingKeys;
	}

	[Theory]
	[InlineData("unsigned")]
	[InlineData("unknown-signer")]
	[InlineData("tampered-payload")]
	public async Task FetchFeedAsync_WithUnauthenticatedFeed_RejectsWithoutArtifactRequests(string failure)
	{
		System.Text.Json.Nodes.JsonObject envelope = System.Text.Json.Nodes.JsonNode.Parse(CreateFeedBytes())!.AsObject();
		byte[] responseBytes;
		if (failure == "unsigned")
		{
			responseBytes = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
		}
		else
		{
			if (failure == "unknown-signer")
			{
				envelope["signerKeyId"] = "unknown";
			}
			else
			{
				envelope["payload"] = Convert.ToBase64String("untrusted payload"u8);
			}
			responseBytes = System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());
		}
		StubHttpMessageHandler handler = new((_, _) => CreateResponse(responseBytes));
		using HttpClient httpClient = new(handler);
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));
		await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchFeedAsync(new Uri(FeedUri), "internal"));
		Assert.Equal(1, handler.SendCount);
	}

	[Fact]
	public async Task FetchFeedAsync_WithValidFeed_ReturnsValidatedFinalUri()
	{
		byte[] feedBytes = CreateFeedBytes();
		Uri finalUri = new(
			"https://cdn.example.test/releases/internal/latest.json");
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(feedBytes, finalUri: finalUri));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		ResolvedUpdateReleaseFeed result = await client.FetchFeedAsync(
			new Uri(FeedUri),
			"internal");

		Assert.Equal("internal", result.Feed.Channel);
		Assert.Equal(42, result.Feed.ReleaseSequence);
		Assert.Equal(finalUri, result.ResponseUri);
	}

	[Fact]
	public async Task FetchFeedAsync_WithOversizedContentLength_RejectsBeforeRead()
	{
		StubHttpMessageHandler handler = new((request, _) =>
		{
			StreamContent content = new(Stream.Null);
			content.Headers.ContentLength = SignedUpdateFeed.MaximumEnvelopeSizeBytes + 1;
			return CreateResponse(content, request.RequestUri);
		});
		using HttpClient httpClient = new(handler);
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(new Uri(FeedUri), "internal"));
	}

	[Fact]
	public async Task FetchFeedAsync_WithOversizedStream_RejectsAtBound()
	{
		byte[] oversizedFeed = new byte[SignedUpdateFeed.MaximumEnvelopeSizeBytes + 1];
		using HttpClient httpClient = CreateClient(
			_ => CreateUnknownLengthResponse(oversizedFeed));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(new Uri(FeedUri), "internal"));
	}

	[Fact]
	public async Task FetchFeedAsync_WhenCancelled_PropagatesCancellation()
	{
		using HttpClient httpClient = CreateClient(_ =>
			CreateUnknownLengthResponse(
				new CancelAfterFirstReadStream([1, 2, 3])));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));
		using CancellationTokenSource cancellationSource = new(
			TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.FetchFeedAsync(
				new Uri(FeedUri),
				"internal",
				cancellationSource.Token));
	}

	[Fact]
	public async Task FetchFeedAsync_WithHttpUri_RejectsBeforeRequest()
	{
		StubHttpMessageHandler handler = new((_, _) =>
			throw new InvalidOperationException("Request must not be sent."));
		using HttpClient httpClient = new(handler);
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(
				new Uri("http://updates.example.test/latest.json"),
				"internal"));
		Assert.Equal(0, handler.SendCount);
	}

	[Theory]
	[InlineData("https://userinfo@updates.example.test/latest.json")]
	[InlineData("https://updates.example.test/latest.json#release")]
	public async Task FetchFeedAsync_WithUnsafeUriComponents_RejectsBeforeRequest(
		string feedUri)
	{
		StubHttpMessageHandler handler = new((_, _) =>
			throw new InvalidOperationException("Request must not be sent."));
		using HttpClient httpClient = new(handler);
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(new Uri(feedUri), "internal"));
		Assert.Equal(0, handler.SendCount);
	}

	[Fact]
	public async Task FetchFeedAsync_WithExternalHttpAndTestFlag_Rejects()
	{
		StubHttpMessageHandler handler = new((_, _) =>
			throw new InvalidOperationException("Request must not be sent."));
		using HttpClient httpClient = new(handler);
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"),
			allowInsecureLoopbackForTests: true);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(
				new Uri("http://updates.example.test/latest.json"),
				"internal"));
		Assert.Equal(0, handler.SendCount);
	}

	[Fact]
	public async Task FetchFeedAsync_WithLoopbackHttpAndTestFlag_AllowsRequest()
	{
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(CreateFeedBytes()));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"),
			allowInsecureLoopbackForTests: true);

		ResolvedUpdateReleaseFeed result = await client.FetchFeedAsync(
			new Uri("http://127.0.0.1:32123/latest.json"),
			"internal");

		Assert.Equal("internal", result.Feed.Channel);
	}

	[Fact]
	public async Task FetchFeedAsync_WithFinalHttpDowngrade_Rejects()
	{
		Uri downgradedUri = new("http://127.0.0.1:32123/latest.json");
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(CreateFeedBytes(), finalUri: downgradedUri));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.FetchFeedAsync(new Uri(FeedUri), "internal"));
	}

	[Fact]
	public async Task FetchFeedAsync_WithErrorStatus_Rejects()
	{
		using HttpClient httpClient = CreateClient(_ => new HttpResponseMessage(
			HttpStatusCode.NotFound));
		SignedUpdateFeedClient client = new(
			httpClient,
			_signingKeys.Trust("test-first"));

		await Assert.ThrowsAsync<HttpRequestException>(() =>
			client.FetchFeedAsync(new Uri(FeedUri), "internal"));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithValidBody_WritesVerifiedFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] body = [1, 2, 3, 4, 5];
		UpdateReleaseArtifact artifact = CreateArtifact(body);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(body));
		OnlineUpdateClient client = new(httpClient);

		await client.DownloadArtifactAsync(artifact, destinationPath);

		Assert.Equal(body, await File.ReadAllBytesAsync(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithOversizedBody_RejectsAndCleansFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] expectedBody = [1, 2, 3];
		UpdateReleaseArtifact artifact = CreateArtifact(expectedBody);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(
			_ => CreateUnknownLengthResponse([1, 2, 3, 4]));
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithTruncatedBody_RejectsAndCleansFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateReleaseArtifact artifact = CreateArtifact([1, 2, 3, 4]);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(
			_ => CreateUnknownLengthResponse([1, 2, 3]));
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithWrongHash_RejectsAndCleansFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] body = [1, 2, 3, 4];
		UpdateReleaseArtifact artifact = CreateArtifact(
			body,
			sha256: new string('0', 64));
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(body));
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithMismatchedContentLength_Rejects()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] body = [1, 2, 3, 4];
		UpdateReleaseArtifact artifact = CreateArtifact(body);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(_ =>
		{
			HttpResponseMessage response = CreateResponse(body);
			response.Content.Headers.ContentLength = body.Length + 1;
			return response;
		});
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithErrorStatus_RejectsWithoutFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateReleaseArtifact artifact = CreateArtifact([1, 2, 3]);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(_ => new HttpResponseMessage(
			HttpStatusCode.BadGateway));
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<HttpRequestException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WhenCancelled_CleansOwnedFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] firstChunk = [1, 2, 3];
		UpdateReleaseArtifact artifact = CreateArtifact([1, 2, 3, 4]);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		using HttpClient httpClient = CreateClient(_ =>
			CreateUnknownLengthResponse(
				new CancelAfterFirstReadStream(firstChunk)));
		OnlineUpdateClient client = new(httpClient);
		using CancellationTokenSource cancellationSource = new(
			TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.DownloadArtifactAsync(
				artifact,
				destinationPath,
				cancellationSource.Token));

		Assert.False(File.Exists(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithExistingDestination_PreservesIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] existingBody = [9, 9, 9];
		byte[] downloadedBody = [1, 2, 3];
		UpdateReleaseArtifact artifact = CreateArtifact(downloadedBody);
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			artifact.FileName);
		await File.WriteAllBytesAsync(destinationPath, existingBody);
		using HttpClient httpClient = CreateClient(
			_ => CreateResponse(downloadedBody));
		OnlineUpdateClient client = new(httpClient);

		await Assert.ThrowsAsync<IOException>(() =>
			client.DownloadArtifactAsync(artifact, destinationPath));

		Assert.Equal(existingBody, await File.ReadAllBytesAsync(destinationPath));
	}

	[Fact]
	public async Task DownloadArtifactAsync_WithReparsePointParent_RejectsBeforeRequest()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "target");
		string junctionPath = Path.Combine(temporaryDirectory.Path, "junction");
		Directory.CreateDirectory(targetPath);
		await JunctionTestHelper.CreateAsync(junctionPath, targetPath);

		try
		{
			UpdateReleaseArtifact artifact = CreateArtifact([1, 2, 3]);
			StubHttpMessageHandler handler = new((_, _) =>
				throw new InvalidOperationException("Request must not be sent."));
			using HttpClient httpClient = new(handler);
			OnlineUpdateClient client = new(httpClient);

			await Assert.ThrowsAsync<InvalidDataException>(() =>
				client.DownloadArtifactAsync(
					artifact,
					Path.Combine(junctionPath, artifact.FileName)));
			Assert.Equal(0, handler.SendCount);
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	private static UpdateReleaseArtifact CreateArtifact(
		byte[] body,
		string? sha256 = null)
	{
		return new UpdateReleaseArtifact(
			"AiUsageDashboard",
			"1.2.3",
			"win-x64",
			"AiUsageDashboard-1.2.3-win-x64.zip",
			"https://cdn.example.test/AiUsageDashboard-1.2.3-win-x64.zip",
			body.LongLength,
			sha256 ?? ComputeSha256(body),
			"0123456789abcdef0123456789abcdef01234567");
	}

	private static HttpClient CreateClient(
		Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
	{
		return new HttpClient(new StubHttpMessageHandler(
			(request, _) => responseFactory(request)));
	}

	private byte[] CreateFeedBytes()
	{
		UpdateReleaseFeed feed = new(
			1,
			"internal",
			42,
			"0.1.0",
			CreateArtifact([1, 2, 3]),
			new UpdateReleaseArtifact(
				"AiUsageDashboard.Updater",
				"0.1.0",
				"win-x64",
				"AiUsageDashboard-Updater-0.1.0-win-x64.exe",
				"https://cdn.example.test/AiUsageDashboard-Updater-0.1.0-win-x64.exe",
				3,
				ComputeSha256([4, 5, 6]),
				"0123456789abcdef0123456789abcdef01234567"));
		return System.Text.Encoding.UTF8.GetBytes(_signingKeys.Sign(JsonSerializer.Serialize(feed)));
	}

	private static HttpResponseMessage CreateResponse(
		byte[] body,
		HttpStatusCode statusCode = HttpStatusCode.OK,
		Uri? finalUri = null)
	{
		return CreateResponse(
			new ByteArrayContent(body),
			finalUri,
			statusCode);
	}

	private static HttpResponseMessage CreateResponse(
		HttpContent content,
		Uri? finalUri,
		HttpStatusCode statusCode = HttpStatusCode.OK)
	{
		HttpResponseMessage response = new(statusCode)
		{
			Content = content
		};

		if (finalUri is not null)
		{
			response.RequestMessage = new HttpRequestMessage(
				HttpMethod.Get,
				finalUri);
		}

		return response;
	}

	private static HttpResponseMessage CreateUnknownLengthResponse(byte[] body)
	{
		return CreateUnknownLengthResponse(new MemoryStream(body));
	}

	private static HttpResponseMessage CreateUnknownLengthResponse(Stream stream)
	{
		StreamContent content = new(stream);
		content.Headers.ContentLength = null;
		content.Headers.ContentType = new MediaTypeHeaderValue(
			"application/octet-stream");
		return CreateResponse(content, finalUri: null);
	}

	private static string ComputeSha256(byte[] body)
	{
		return Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
	}
}
