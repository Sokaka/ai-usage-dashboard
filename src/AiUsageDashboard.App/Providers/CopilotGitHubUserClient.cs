using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiUsageDashboard.App.Providers;

internal interface ICopilotGitHubUserClient
{
	Task<CopilotAccountIdentity> GetAuthenticatedUserAsync(
		string host,
		string accessToken,
		CancellationToken cancellationToken);
}

internal sealed class CopilotGitHubUserClient : ICopilotGitHubUserClient
{
	private const int MaximumErrorResponseBytes = 32 * 1024;
	private const int MaximumResponseBytes = 256 * 1024;
	private readonly HttpClient _httpClient;

	internal CopilotGitHubUserClient(HttpClient httpClient)
	{
		_httpClient = httpClient ??
			throw new ArgumentNullException(nameof(httpClient));
	}

	public async Task<CopilotAccountIdentity> GetAuthenticatedUserAsync(
		string host,
		string accessToken,
		CancellationToken cancellationToken)
	{
		if (!CopilotAccountIdentityRules.TryNormalizeHost(
				host,
				out string normalizedHost))
		{
			throw new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"GitHub host 格式無效。");
		}

		if (!string.Equals(
				normalizedHost,
				"github.com",
				StringComparison.Ordinal))
		{
			throw new CopilotClientException(
				CopilotFailureKind.PermissionDenied,
				"目前只支援 github.com 帳號。");
		}

		if (string.IsNullOrWhiteSpace(accessToken) ||
			accessToken.Any(character =>
				char.IsControl(character) || char.IsWhiteSpace(character)))
		{
			throw new CopilotClientException(
				CopilotFailureKind.AuthenticationRequired,
				"Copilot credential 格式無效。");
		}

		using HttpRequestMessage request = new(
			HttpMethod.Get,
			"https://api.github.com/user");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
			"application/vnd.github+json"));
		request.Headers.Authorization = new AuthenticationHeaderValue(
			"Bearer",
			accessToken);
		request.Headers.UserAgent.ParseAdd("AiUsageDashboard/1.0");
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

		using HttpResponseMessage response = await _httpClient.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw await CreateResponseExceptionAsync(
				response,
				cancellationToken);
		}

		long? contentLength = response.Content.Headers.ContentLength;
		if (contentLength is > MaximumResponseBytes)
		{
			throw new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"GitHub user response 超過允許大小。");
		}

		await using Stream contentStream = await response.Content
			.ReadAsStreamAsync(cancellationToken);
		using MemoryStream boundedResponse = new();
		byte[] buffer = new byte[8192];
		while (true)
		{
			int read = await contentStream.ReadAsync(
				buffer,
				cancellationToken);
			if (read == 0)
			{
				break;
			}

			if ((boundedResponse.Length + read) > MaximumResponseBytes)
			{
				throw new CopilotClientException(
					CopilotFailureKind.InvalidResponse,
					"GitHub user response 超過允許大小。");
			}

			boundedResponse.Write(buffer, 0, read);
		}

		boundedResponse.Position = 0;
		try
		{
			using JsonDocument document = await JsonDocument.ParseAsync(
				boundedResponse,
				cancellationToken: cancellationToken);
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("id", out JsonElement databaseIdElement) ||
				!databaseIdElement.TryGetInt64(out long databaseId) ||
				(databaseId <= 0) ||
				!root.TryGetProperty("node_id", out JsonElement nodeIdElement) ||
				(nodeIdElement.ValueKind != JsonValueKind.String) ||
				!root.TryGetProperty("login", out JsonElement loginElement) ||
				(loginElement.ValueKind != JsonValueKind.String))
			{
				throw new CopilotClientException(
					CopilotFailureKind.InvalidResponse,
					"GitHub user response 缺少必要身分欄位。");
			}

			string? nodeId = nodeIdElement.GetString();
			string? login = loginElement.GetString();
			CopilotAccountIdentity identity = new(
				normalizedHost,
				nodeId ?? string.Empty,
				databaseId,
				login ?? string.Empty);
			_ = CopilotAccountIdentityRules.Create(identity);
			return identity;
		}
		catch (CopilotClientException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is JsonException or ArgumentException or OverflowException)
		{
			throw new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"無法解析 GitHub user response。",
				innerException: exception);
		}
	}

	private static async Task<CopilotClientException>
		CreateResponseExceptionAsync(
			HttpResponseMessage response,
			CancellationToken cancellationToken)
	{
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			return new CopilotClientException(
				CopilotFailureKind.AuthenticationRequired,
				"GitHub credential 已失效。");
		}

		if ((response.StatusCode == HttpStatusCode.TooManyRequests) ||
			((response.StatusCode == HttpStatusCode.Forbidden) &&
				(IsRateLimited(response) ||
					await HasSecondaryRateLimitResponseAsync(
						response,
						cancellationToken))))
		{
			return new CopilotClientException(
				CopilotFailureKind.RateLimited,
				"GitHub 暫時限制身分查詢。",
				GetRetryAt(response));
		}

		if (response.StatusCode == HttpStatusCode.Forbidden)
		{
			return new CopilotClientException(
				CopilotFailureKind.PermissionDenied,
				"GitHub credential 無權讀取 authenticated user。");
		}

		return new CopilotClientException(
			CopilotFailureKind.Transient,
			"GitHub authenticated user 查詢失敗。");
	}

	private static bool IsRateLimited(HttpResponseMessage response)
	{
		return (response.Headers.RetryAfter is not null) ||
			(response.Headers.TryGetValues(
				"X-RateLimit-Remaining",
				out IEnumerable<string>? values) &&
			values.Any(value => string.Equals(
				value.Trim(),
				"0",
				StringComparison.Ordinal)));
	}

	private static async Task<bool> HasSecondaryRateLimitResponseAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		if (response.Content.Headers.ContentLength is
			> MaximumErrorResponseBytes)
		{
			return false;
		}

		try
		{
			await using Stream contentStream = await response.Content
				.ReadAsStreamAsync(cancellationToken);
			using MemoryStream boundedResponse = new();
			byte[] buffer = new byte[4096];

			while (true)
			{
				int read = await contentStream.ReadAsync(
					buffer,
					cancellationToken);
				if (read == 0)
				{
					break;
				}

				if ((boundedResponse.Length + read) >
					MaximumErrorResponseBytes)
				{
					return false;
				}

				boundedResponse.Write(buffer, 0, read);
			}

			boundedResponse.Position = 0;
			using JsonDocument document = await JsonDocument.ParseAsync(
				boundedResponse,
				cancellationToken: cancellationToken);
			return document.RootElement.TryGetProperty(
					"message",
					out JsonElement messageElement) &&
				(messageElement.ValueKind == JsonValueKind.String) &&
				(messageElement.GetString()?.Contains(
					"secondary rate limit",
					StringComparison.OrdinalIgnoreCase) == true);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is IOException or JsonException)
		{
			return false;
		}
	}

	private static DateTimeOffset? GetRetryAt(HttpResponseMessage response)
	{
		if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
		{
			return retryDate.ToUniversalTime();
		}

		if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelay)
		{
			return DateTimeOffset.UtcNow + retryDelay;
		}

		if (response.Headers.TryGetValues(
				"X-RateLimit-Reset",
				out IEnumerable<string>? resetValues) &&
			long.TryParse(
				resetValues.FirstOrDefault(),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out long resetSeconds))
		{
			try
			{
				return DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
			}
			catch (ArgumentOutOfRangeException)
			{
			}
		}

		return null;
	}
}
