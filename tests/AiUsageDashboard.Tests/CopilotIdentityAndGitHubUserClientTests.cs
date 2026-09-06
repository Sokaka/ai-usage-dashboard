using System.Net;
using System.Text;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CopilotIdentityAndGitHubUserClientTests
{
	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage>
			_responseFactory;

		internal string? AuthorizationParameter { get; private set; }
		internal string? AuthorizationScheme { get; private set; }
		internal bool HasExpectedAcceptHeader { get; private set; }
		internal bool HasExpectedApiVersion { get; private set; }
		internal bool HasExpectedUserAgent { get; private set; }
		internal HttpMethod? Method { get; private set; }
		internal Uri? RequestUri { get; private set; }

		internal StubHttpMessageHandler(
			Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
		{
			_responseFactory = responseFactory;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Method = request.Method;
			RequestUri = request.RequestUri;
			AuthorizationScheme = request.Headers.Authorization?.Scheme;
			AuthorizationParameter = request.Headers.Authorization?.Parameter;
			HasExpectedAcceptHeader = request.Headers.Accept.Any(value =>
				string.Equals(
					value.MediaType,
					"application/vnd.github+json",
					StringComparison.Ordinal));
			HasExpectedApiVersion = request.Headers.TryGetValues(
				"X-GitHub-Api-Version",
				out IEnumerable<string>? versions) &&
				versions.Contains("2022-11-28", StringComparer.Ordinal);
			HasExpectedUserAgent = request.Headers.UserAgent.Any(value =>
				string.Equals(
					value.Product?.Name,
					"AiUsageDashboard",
					StringComparison.Ordinal) &&
				string.Equals(
					value.Product?.Version,
					"1.0",
					StringComparison.Ordinal));

			HttpResponseMessage response = _responseFactory(request);
			response.RequestMessage ??= request;
			return Task.FromResult(response);
		}
	}

	[Fact]
	public void StableIdentity_SameNodeIdWithDifferentLogin_RemainsEquivalent()
	{
		CopilotAccountIdentity first = new(
			"https://GitHub.com/",
			"MDQ6VXNlcjEyMzQ=",
			1234,
			"old-login");
		CopilotAccountIdentity renamed = new(
			"github.com",
			"MDQ6VXNlcjEyMzQ=",
			1234,
			"new-login");

		string firstIdentity = CopilotAccountIdentityRules.Create(first);
		string renamedIdentity = CopilotAccountIdentityRules.Create(renamed);

		Assert.Equal(firstIdentity, renamedIdentity);
		Assert.True(CopilotAccountIdentityRules.AreEquivalent(
			firstIdentity,
			renamedIdentity));
	}

	[Fact]
	public void StableIdentity_DifferentNodeId_IsDifferent()
	{
		string firstIdentity = CopilotAccountIdentityRules.Create(
			new CopilotAccountIdentity(
				"github.com",
				"NODE_FIRST",
				1234,
				"same-login"));
		string secondIdentity = CopilotAccountIdentityRules.Create(
			new CopilotAccountIdentity(
				"github.com",
				"NODE_SECOND",
				1234,
				"same-login"));

		Assert.NotEqual(firstIdentity, secondIdentity);
		Assert.False(CopilotAccountIdentityRules.AreEquivalent(
			firstIdentity,
			secondIdentity));
	}

	[Fact]
	public async Task GetAuthenticatedUserAsync_ForSuccess_UsesExpectedRequestAndReturnsIdentity()
	{
		const string accessToken = "synthetic-account-specific-token";
		StubHttpMessageHandler handler = new(_ => CreateJsonResponse(
			HttpStatusCode.OK,
			"""
			{"id":1234,"node_id":"MDQ6VXNlcjEyMzQ=","login":"octocat"}
			"""));
		using HttpClient httpClient = new(handler);
		CopilotGitHubUserClient client = new(httpClient);

		CopilotAccountIdentity identity = await client.GetAuthenticatedUserAsync(
			" HTTPS://GitHub.COM/ ",
			accessToken,
			CancellationToken.None);

		Assert.Equal("github.com", identity.Host);
		Assert.Equal("MDQ6VXNlcjEyMzQ=", identity.NodeId);
		Assert.Equal(1234, identity.DatabaseId);
		Assert.Equal("octocat", identity.Login);
		Assert.Equal(HttpMethod.Get, handler.Method);
		Assert.Equal(new Uri("https://api.github.com/user"), handler.RequestUri);
		Assert.Equal("Bearer", handler.AuthorizationScheme);
		Assert.Equal(accessToken, handler.AuthorizationParameter);
		Assert.True(handler.HasExpectedAcceptHeader);
		Assert.True(handler.HasExpectedApiVersion);
		Assert.True(handler.HasExpectedUserAgent);
	}

	[Theory]
	[InlineData(401, (int)CopilotFailureKind.AuthenticationRequired)]
	[InlineData(403, (int)CopilotFailureKind.PermissionDenied)]
	[InlineData(429, (int)CopilotFailureKind.RateLimited)]
	public async Task GetAuthenticatedUserAsync_MapsHttpFailure(
		int statusCode,
		int expectedKindValue)
	{
		const string accessToken = "synthetic-must-not-leak-token";
		using HttpClient httpClient = CreateClient(_ =>
			new HttpResponseMessage((HttpStatusCode)statusCode));
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					accessToken,
					CancellationToken.None));

		Assert.Equal((CopilotFailureKind)expectedKindValue, failure.Kind);
		Assert.DoesNotContain(
			accessToken,
			failure.ToString(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAuthenticatedUserAsync_ForRateLimitedForbidden_UsesResetTime()
	{
		DateTimeOffset expectedRetryAt = DateTimeOffset.FromUnixTimeSeconds(
			1_800_000_000);
		using HttpClient httpClient = CreateClient(_ =>
		{
			HttpResponseMessage response = new(HttpStatusCode.Forbidden);
			response.Headers.Add("X-RateLimit-Remaining", "0");
			response.Headers.Add(
				"X-RateLimit-Reset",
				expectedRetryAt.ToUnixTimeSeconds().ToString(
					System.Globalization.CultureInfo.InvariantCulture));
			return response;
		});
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					"synthetic-rate-limited-token",
					CancellationToken.None));

		Assert.Equal(CopilotFailureKind.RateLimited, failure.Kind);
		Assert.Equal(expectedRetryAt, failure.RetryAt);
	}

	[Fact]
	public async Task GetAuthenticatedUserAsync_ForSecondaryRateLimitRetryAfter_MapsRateLimit()
	{
		using HttpClient httpClient = CreateClient(_ =>
		{
			HttpResponseMessage response = new(HttpStatusCode.Forbidden);
			response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
			return response;
		});
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					"synthetic-rate-limited-token",
					CancellationToken.None));

		Assert.Equal(CopilotFailureKind.RateLimited, failure.Kind);
		Assert.NotNull(failure.RetryAt);
	}

	[Fact]
	public async Task GetAuthenticatedUserAsync_ForHeaderlessSecondaryRateLimit_MapsRateLimit()
	{
		using HttpClient httpClient = CreateClient(_ => CreateJsonResponse(
			HttpStatusCode.Forbidden,
			"""
			{"message":"You have exceeded a secondary rate limit. Please wait before you try again."}
			"""));
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					"synthetic-secondary-limit-token",
					CancellationToken.None));

		Assert.Equal(CopilotFailureKind.RateLimited, failure.Kind);
	}

	[Fact]
	public async Task GetAuthenticatedUserAsync_ForOversizeResponse_FailsClosed()
	{
		using HttpClient httpClient = CreateClient(_ =>
		{
			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(new byte[(256 * 1024) + 1])
			};
			response.Content.Headers.ContentLength = (256 * 1024) + 1;
			return response;
		});
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					"synthetic-oversize-token",
					CancellationToken.None));

		Assert.Equal(CopilotFailureKind.InvalidResponse, failure.Kind);
	}

	[Theory]
	[InlineData("not-json")]
	[InlineData("{\"id\":1234,\"login\":\"octocat\"}")]
	[InlineData("{\"id\":1234,\"node_id\":\"NODE\\u0001ONE\",\"login\":\"octocat\"}")]
	public async Task GetAuthenticatedUserAsync_ForInvalidResponse_FailsClosed(
		string responseBody)
	{
		using HttpClient httpClient = CreateClient(_ => CreateJsonResponse(
			HttpStatusCode.OK,
			responseBody));
		CopilotGitHubUserClient client = new(httpClient);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAuthenticatedUserAsync(
					"github.com",
					"synthetic-invalid-response-token",
					CancellationToken.None));

		Assert.Equal(CopilotFailureKind.InvalidResponse, failure.Kind);
	}

	private static HttpClient CreateClient(
		Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
	{
		return new HttpClient(new StubHttpMessageHandler(responseFactory));
	}

	private static HttpResponseMessage CreateJsonResponse(
		HttpStatusCode statusCode,
		string body)
	{
		return new HttpResponseMessage(statusCode)
		{
			Content = new StringContent(
				body,
				Encoding.UTF8,
				"application/json")
		};
	}
}
