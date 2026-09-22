namespace AiUsageDashboard.Updater.Core;

internal sealed record ResolvedUpdateReleaseFeed(
	UpdateReleaseFeed Feed,
	Uri ResponseUri);

internal sealed class SignedUpdateFeedClient
{
	private const int MaximumFeedSizeBytes =
		SignedUpdateFeed.MaximumEnvelopeSizeBytes;
	private readonly bool _allowInsecureLoopbackForTests;
	private readonly HttpClient _httpClient;
	private readonly UpdateFeedTrustStore _trustedKeys;

	internal SignedUpdateFeedClient(
		HttpClient httpClient,
		UpdateFeedTrustStore trustedKeys)
		: this(
			httpClient,
			trustedKeys,
			allowInsecureLoopbackForTests: false)
	{
	}

	internal SignedUpdateFeedClient(
		HttpClient httpClient,
		UpdateFeedTrustStore trustedKeys,
		bool allowInsecureLoopbackForTests)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(
			nameof(httpClient));
		_trustedKeys = trustedKeys ?? throw new ArgumentNullException(
			nameof(trustedKeys));
		_allowInsecureLoopbackForTests = allowInsecureLoopbackForTests;
	}

	internal async Task<ResolvedUpdateReleaseFeed> FetchFeedAsync(
		Uri feedUri,
		string expectedChannel,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(feedUri);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);
		UpdateHttpUriPolicy.EnsureAllowed(
			feedUri,
			_allowInsecureLoopbackForTests);

		using HttpRequestMessage request = new(HttpMethod.Get, feedUri);
		using HttpResponseMessage response = await _httpClient.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken);
		Uri responseUri = GetAndValidateResponseUri(response);
		response.EnsureSuccessStatusCode();

		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength > MaximumFeedSizeBytes)
		{
			throw new InvalidDataException(
				"Update release feed exceeds the size limit.");
		}

		await using Stream feedStream = await response.Content.ReadAsStreamAsync(
			cancellationToken);
		byte[] feedBytes = await ReadBoundedFeedAsync(
			feedStream,
			cancellationToken);
		UpdateReleaseFeed feed = SignedUpdateFeed.VerifyAndParse(
			feedBytes,
			_trustedKeys,
			expectedChannel);
		return new ResolvedUpdateReleaseFeed(feed, responseUri);
	}

	private Uri GetAndValidateResponseUri(HttpResponseMessage response)
	{
		Uri responseUri = response.RequestMessage?.RequestUri ??
			throw new InvalidDataException(
				"Update server response does not identify its final URI.");
		UpdateHttpUriPolicy.EnsureAllowed(
			responseUri,
			_allowInsecureLoopbackForTests);
		return responseUri;
	}

	private static async Task<byte[]> ReadBoundedFeedAsync(
		Stream stream,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[MaximumFeedSizeBytes + 1];
		int totalBytes = 0;

		while (true)
		{
			int bytesRead = await stream.ReadAsync(
				buffer.AsMemory(totalBytes, buffer.Length - totalBytes),
				cancellationToken);
			totalBytes += bytesRead;

			if (totalBytes > MaximumFeedSizeBytes)
			{
				throw new InvalidDataException(
					"Update release feed exceeds the size limit.");
			}

			if (bytesRead == 0)
			{
				return buffer[..totalBytes];
			}
		}
	}
}
