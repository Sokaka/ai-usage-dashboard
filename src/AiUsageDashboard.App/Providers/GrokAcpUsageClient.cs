using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal enum GrokContainedCommand
{
	AcpStdio,
	Version
}

internal sealed record GrokAcpLaunchOptions(
	string ExecutablePath,
	string HomeDirectory,
	string WorkingDirectory,
	GrokContainedCommand Command = GrokContainedCommand.AcpStdio);

internal interface IGrokAcpProcess : IAsyncDisposable
{
	Stream StandardInput { get; }

	Stream StandardOutput { get; }

	Stream StandardError { get; }

	Task<int> WaitForExitAsync(CancellationToken cancellationToken);

	Task TerminateTreeAsync(CancellationToken cancellationToken);
}

internal interface IGrokAcpProcessFactory
{
	Task<IGrokAcpProcess> StartAsync(
		GrokAcpLaunchOptions options,
		CancellationToken cancellationToken);
}

internal static class GrokAcpProcessExecution
{
	internal static Task<IGrokAcpProcess> StartAsync(
		IGrokAcpProcessFactory processFactory,
		GrokAcpLaunchOptions options,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null)
	{
		ArgumentNullException.ThrowIfNull(processFactory);
		ArgumentNullException.ThrowIfNull(options);
		return ProviderProcessExecution.RunAsynchronousAsync(
			operationCancellationToken => processFactory.StartAsync(
				options,
				operationCancellationToken),
			cancellationToken,
			operationTracker,
			CleanupLateStartedProcessAsync);
	}

	private static async Task CleanupLateStartedProcessAsync(
		IGrokAcpProcess process)
	{
		try
		{
			await process.TerminateTreeAsync(CancellationToken.None)
				.ConfigureAwait(false);
		}
		finally
		{
			await process.DisposeAsync().ConfigureAwait(false);
		}
	}
}

internal interface IGrokAcpUsageClient
{
	Task<GrokUsagePollResult> QueryAsync(
		GrokAcpLaunchOptions options,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null,
		Func<GrokPrincipal, bool>? principalMatcher = null);
}

internal sealed class GrokAcpUsageClient : IGrokAcpUsageClient
{
	private const int AuthInfoRequestId = 4;
	private const int BillingRequestId = 2;
	private const int InitializeRequestId = 1;
	private const int LegacyAuthInfoRequestId = 5;
	private const int LegacyBillingRequestId = 3;
	private const int MaximumJsonDepth = 32;
	private const int MaximumLineBytes = 128 * 1024;
	private const int MaximumMethodLength = 256;
	private const int MaximumResponseCount = 8;
	private const int MaximumStderrBytes = 64 * 1024;
	private const int MaximumTotalStdoutBytes = 512 * 1024;
	private const string AuthenticationRequiredMessage =
		"Authentication required";
	private const string BillingAuthenticationRequiredData =
		"Authentication required to fetch billing data";
	private const string BillingNonXaiAuthenticationData =
		"Billing data requires auth with grok.com. Run `grok login` to authenticate.";
	private const string CurrentExtensionNamespace = "x.ai/";
	private const string LegacyExtensionNamespace = "_x.ai/";
	private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan MaximumWeeklyPeriod = TimeSpan.FromDays(8);
	private static readonly TimeSpan MinimumWeeklyPeriod = TimeSpan.FromDays(6);
	private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
	private static readonly string[] Rfc3339TimestampFormats =
	[
		"yyyy-MM-dd'T'HH:mm:ss'Z'",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
		"yyyy-MM-dd'T'HH:mm:sszzz",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
	];
	private readonly TimeSpan _cleanupTimeout;
	private readonly TimeSpan _operationTimeout;
	private readonly IGrokAcpProcessFactory _processFactory;
	private readonly TimeProvider _timeProvider;

	internal GrokAcpUsageClient(
		IGrokAcpProcessFactory processFactory,
		TimeProvider? timeProvider = null,
		TimeSpan? operationTimeout = null,
		TimeSpan? cleanupTimeout = null)
	{
		_processFactory = processFactory ??
			throw new ArgumentNullException(nameof(processFactory));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_operationTimeout = operationTimeout ?? DefaultOperationTimeout;
		_cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;

		if (_operationTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(operationTimeout));
		}

		if (_cleanupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
		}
	}

	public async Task<GrokUsagePollResult> QueryAsync(
		GrokAcpLaunchOptions options,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null,
		Func<GrokPrincipal, bool>? principalMatcher = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		ValidateLaunchOptions(options);
		cancellationToken.ThrowIfCancellationRequested();

		using CancellationTokenSource timeoutSource = new(_operationTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		await using IGrokAcpProcess process = await StartProcessAsync(
			options,
			cancellationToken,
			timeoutSource,
			linkedSource.Token,
			operationTracker);
		Task stderrDrainTask = DrainStderrAsync(
			process.StandardError,
			linkedSource.Token);
		Task<GrokUsagePollResult> protocolTask = ExecuteProtocolAsync(
			process,
			linkedSource.Token,
			principalMatcher);
		bool isTreeTerminationConfirmed = false;

		try
		{
			Task firstCompletedTask = await Task.WhenAny(
				protocolTask,
				stderrDrainTask);

			if (ReferenceEquals(firstCompletedTask, stderrDrainTask))
			{
				await stderrDrainTask;
			}

			GrokUsagePollResult result = await protocolTask;
			await TerminateAndConfirmAsync(process, CancellationToken.None);
			isTreeTerminationConfirmed = true;
			await ObserveDrainAfterTerminationAsync(stderrDrainTask);
			return result;
		}
		catch (OperationCanceledException exception)
			when (!cancellationToken.IsCancellationRequested &&
				timeoutSource.IsCancellationRequested)
		{
			throw new GrokAcpFailureException(
				"request",
				GrokAcpFailureCategory.Transient,
				innerException: new TimeoutException(
					"Grok ACP request exceeded its deadline.",
					exception));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		finally
		{
			linkedSource.Cancel();
			GrokProcessContainmentException? containmentFailure = null;

			if (!isTreeTerminationConfirmed)
			{
				try
				{
					await TerminateAndConfirmAsync(process, CancellationToken.None);
				}
				catch (GrokProcessContainmentException exception)
				{
					containmentFailure = exception;
				}
				catch
				{
					// DisposeAsync performs one final contained termination attempt
					// and surfaces a hard failure if positive quiescence still cannot
					// be confirmed.
				}
			}

			await ObserveProtocolTasksAfterCancellationAsync(
				protocolTask,
				stderrDrainTask,
				_cleanupTimeout);

			if (containmentFailure is not null)
			{
				ExceptionDispatchInfo.Capture(containmentFailure).Throw();
			}
		}
	}

	private async Task<IGrokAcpProcess> StartProcessAsync(
		GrokAcpLaunchOptions options,
		CancellationToken callerCancellationToken,
		CancellationTokenSource timeoutSource,
		CancellationToken operationCancellationToken,
		ProviderProcessOperationTracker? operationTracker)
	{
		try
		{
			return await GrokAcpProcessExecution.StartAsync(
				_processFactory,
				options,
				operationCancellationToken,
				operationTracker);
		}
		catch (OperationCanceledException exception)
			when (!callerCancellationToken.IsCancellationRequested &&
				timeoutSource.IsCancellationRequested)
		{
			throw new GrokAcpFailureException(
				"request",
				GrokAcpFailureCategory.Transient,
				innerException: new TimeoutException(
					"Grok ACP request exceeded its deadline.",
					exception));
		}
		catch (OperationCanceledException)
			when (callerCancellationToken.IsCancellationRequested)
		{
			throw;
		}
	}

	private async Task<GrokUsagePollResult> ExecuteProtocolAsync(
		IGrokAcpProcess process,
		CancellationToken cancellationToken,
		Func<GrokPrincipal, bool>? principalMatcher)
	{
		await using StreamWriter writer = new(
			process.StandardInput,
			new UTF8Encoding(
				encoderShouldEmitUTF8Identifier: false,
				throwOnInvalidBytes: true),
			bufferSize: 4096,
			leaveOpen: true)
		{
			AutoFlush = true,
			NewLine = "\n"
		};
		GrokBoundedJsonLineReader reader = new(
			process.StandardOutput,
			MaximumLineBytes,
			MaximumTotalStdoutBytes,
			MaximumResponseCount);

		using JsonDocument initializeResponse = await ExchangeAsync(
			writer,
			reader,
			InitializeRequestId,
			"initialize",
			new
			{
				protocolVersion = 1,
				clientCapabilities = new
				{
					fs = new
					{
						readTextFile = false,
						writeTextFile = false
					},
					terminal = false
				},
				clientInfo = new
				{
					name = "AI Usage Dashboard",
					version = "1"
				}
			},
			cancellationToken);
		_ = GetResult(initializeResponse.RootElement, "initialize");

		string authInfoNamespace = CurrentExtensionNamespace;
		JsonDocument authInfoResponse;

		try
		{
			authInfoResponse = await ExchangeAsync(
				writer,
				reader,
				AuthInfoRequestId,
				CurrentExtensionNamespace + "auth/info",
				new { },
				cancellationToken);
		}
		catch (GrokAcpFailureException exception) when (
			(exception.Category == GrokAcpFailureCategory.Compatibility) &&
			(exception.ErrorCode == -32601))
		{
			authInfoNamespace = LegacyExtensionNamespace;
			authInfoResponse = await ExchangeAsync(
				writer,
				reader,
				LegacyAuthInfoRequestId,
				LegacyExtensionNamespace + "auth/info",
				new { },
				cancellationToken);
		}

		GrokPrincipal principal;

		using (authInfoResponse)
		{
			principal = ParsePrincipal(
				GetResult(
					authInfoResponse.RootElement,
					authInfoNamespace + "auth/info"));
		}

		if ((principalMatcher is not null) && !principalMatcher(principal))
		{
			throw new GrokPrincipalMismatchException();
		}

		string billingNamespace = CurrentExtensionNamespace;
		JsonDocument billingResponse;

		try
		{
			try
			{
				billingResponse = await ExchangeAsync(
					writer,
					reader,
					BillingRequestId,
					CurrentExtensionNamespace + "billing",
					new { },
					cancellationToken);
			}
			catch (GrokAcpFailureException exception) when (
				(exception.Category == GrokAcpFailureCategory.Compatibility) &&
				(exception.ErrorCode == -32601))
			{
				billingNamespace = LegacyExtensionNamespace;
				billingResponse = await ExchangeAsync(
					writer,
					reader,
					LegacyBillingRequestId,
					LegacyExtensionNamespace + "billing",
					new { },
					cancellationToken);
			}
		}
		catch (GrokAcpFailureException exception) when (
			exception.Category == GrokAcpFailureCategory.Authentication)
		{
			return new GrokUsagePollResult(
				principal,
				weeklyUsage: null,
				_timeProvider.GetUtcNow(),
				usageAvailability:
					GrokUsageAvailability.AuthenticationRequired,
				currentAuthUsability: GrokCurrentAuthUsability.Unusable);
		}

		using (billingResponse)
		{
			DateTimeOffset observedAt = _timeProvider.GetUtcNow();
			JsonElement billingResult = GetResult(
				billingResponse.RootElement,
				billingNamespace + "billing");
			string? planTier = ParsePlanTier(billingResult);

			try
			{
				GrokWeeklyUsage weeklyUsage = ParseWeeklyUsage(
					billingResult,
					observedAt);
				return new GrokUsagePollResult(
					principal,
					weeklyUsage,
					observedAt,
					planTier: planTier);
			}
			catch (GrokUnsupportedBillingException)
			{
				return new GrokUsagePollResult(
					principal,
					weeklyUsage: null,
					observedAt,
					usageAvailability: GrokUsageAvailability.Unsupported,
					currentAuthUsability: GrokCurrentAuthUsability.Usable,
					planTier: planTier);
			}
			catch (GrokUsageSchemaException)
			{
				return new GrokUsagePollResult(
					principal,
					weeklyUsage: null,
					observedAt,
					usageAvailability:
						GrokUsageAvailability.TransientProbeError,
					currentAuthUsability: GrokCurrentAuthUsability.Usable,
					planTier: planTier);
			}
		}
	}

	private async Task TerminateAndConfirmAsync(
		IGrokAcpProcess process,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource cleanupSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cleanupSource.CancelAfter(_cleanupTimeout);
		await process.TerminateTreeAsync(cleanupSource.Token);
	}

	private static async Task<JsonDocument> ExchangeAsync(
		StreamWriter writer,
		GrokBoundedJsonLineReader reader,
		int requestId,
		string method,
		object parameters,
		CancellationToken cancellationToken)
	{
		string request = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = requestId,
			method,
			@params = parameters
		});
		await writer.WriteLineAsync(request.AsMemory(), cancellationToken);

		while (true)
		{
			string responseLine = await reader.ReadLineAsync(cancellationToken);
			JsonDocument response;

			try
			{
				response = JsonDocument.Parse(
					responseLine,
					new JsonDocumentOptions
					{
						AllowTrailingCommas = false,
						CommentHandling = JsonCommentHandling.Disallow,
						MaxDepth = MaximumJsonDepth
					});
			}
			catch (JsonException exception)
			{
				throw new GrokUsageSchemaException(
					$"Grok ACP {method} 回應不是有效的 bounded JSON：{exception.GetType().Name}。");
			}

			try
			{
				ValidateNoDuplicateProperties(response.RootElement);

				if (IsIgnorableNotification(response.RootElement))
				{
					response.Dispose();
					continue;
				}

				ValidateResponseEnvelope(response.RootElement, requestId, method);
				return response;
			}
			catch
			{
				response.Dispose();
				throw;
			}
		}
	}

	private static void ValidateResponseEnvelope(
		JsonElement response,
		int expectedRequestId,
		string method)
	{
		if (response.ValueKind != JsonValueKind.Object)
		{
			throw new GrokUsageSchemaException("Grok ACP 回應 envelope 格式無效。");
		}

		if (!response.TryGetProperty("jsonrpc", out JsonElement jsonRpc) ||
			(jsonRpc.ValueKind != JsonValueKind.String) ||
			!string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal) ||
			!response.TryGetProperty("id", out JsonElement id) ||
			(id.ValueKind != JsonValueKind.Number) ||
			!id.TryGetInt32(out int actualRequestId) ||
			(actualRequestId != expectedRequestId))
		{
			throw new GrokUsageSchemaException("Grok ACP 回應 id 或 protocol version 不符。");
		}

		bool hasResult = response.TryGetProperty("result", out _);
		bool hasError = response.TryGetProperty("error", out JsonElement error);

		if (hasResult == hasError)
		{
			throw new GrokUsageSchemaException("Grok ACP 回應必須且只能包含 result 或 error。");
		}

		if (hasError)
		{
			throw CreateAcpFailure(method, error);
		}
	}

	private static bool IsIgnorableNotification(JsonElement response)
	{
		if ((response.ValueKind != JsonValueKind.Object) ||
			!response.TryGetProperty("jsonrpc", out JsonElement jsonRpc) ||
			(jsonRpc.ValueKind != JsonValueKind.String) ||
			!string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal) ||
			response.TryGetProperty("id", out _) ||
			response.TryGetProperty("result", out _) ||
			response.TryGetProperty("error", out _) ||
			!response.TryGetProperty("method", out JsonElement method) ||
			(method.ValueKind != JsonValueKind.String))
		{
			return false;
		}

		if (response.TryGetProperty("params", out JsonElement parameters) &&
			(parameters.ValueKind is not
				(JsonValueKind.Object or JsonValueKind.Array)))
		{
			return false;
		}

		string? methodName = method.GetString();
		return !string.IsNullOrWhiteSpace(methodName) &&
			(methodName.Length <= MaximumMethodLength) &&
			string.Equals(
				methodName,
				methodName.Trim(),
				StringComparison.Ordinal) &&
			!methodName.Any(char.IsControl);
	}

	private static GrokAcpFailureException CreateAcpFailure(
		string method,
		JsonElement error)
	{
		if ((error.ValueKind != JsonValueKind.Object) ||
			!error.TryGetProperty("code", out JsonElement codeElement) ||
			(codeElement.ValueKind != JsonValueKind.Number) ||
			!codeElement.TryGetInt64(out long code))
		{
			return new GrokAcpFailureException(
				method,
				GrokAcpFailureCategory.Unknown);
		}

		GrokAcpFailureCategory category = code switch
		{
			-32000 when IsStructuredAuthenticationRequired(method, error) =>
				GrokAcpFailureCategory.Authentication,
			-32601 when IsKnownExtensionMethod(method) =>
				GrokAcpFailureCategory.Compatibility,
			-32003 => GrokAcpFailureCategory.Transient,
			_ => GrokAcpFailureCategory.Unknown
		};
		return new GrokAcpFailureException(method, category, code);
	}

	private static bool IsStructuredAuthenticationRequired(
		string method,
		JsonElement error)
	{
		if (!IsBillingMethod(method) ||
			!error.TryGetProperty("message", out JsonElement messageElement) ||
			(messageElement.ValueKind != JsonValueKind.String))
		{
			return false;
		}

		if (!string.Equals(
			messageElement.GetString(),
			AuthenticationRequiredMessage,
			StringComparison.Ordinal) ||
			!error.TryGetProperty("data", out JsonElement dataElement) ||
			(dataElement.ValueKind != JsonValueKind.String))
		{
			return false;
		}

		string? data = dataElement.GetString();
		return string.Equals(
				data,
				BillingAuthenticationRequiredData,
				StringComparison.Ordinal) ||
			string.Equals(
				data,
				BillingNonXaiAuthenticationData,
				StringComparison.Ordinal);
	}

	private static bool IsBillingMethod(string method)
	{
		return string.Equals(
				method,
				CurrentExtensionNamespace + "billing",
				StringComparison.Ordinal) ||
			string.Equals(
				method,
				LegacyExtensionNamespace + "billing",
				StringComparison.Ordinal);
	}

	private static bool IsKnownExtensionMethod(string method)
	{
		return method.StartsWith(
				CurrentExtensionNamespace,
				StringComparison.Ordinal) ||
			method.StartsWith(
				LegacyExtensionNamespace,
				StringComparison.Ordinal);
	}

	private static JsonElement GetResult(JsonElement response, string method)
	{
		if (!response.TryGetProperty("result", out JsonElement result))
		{
			throw new GrokUsageSchemaException($"Grok ACP {method} 缺少 result。");
		}

		return result;
	}

	internal static GrokPrincipal ParsePrincipal(JsonElement result)
	{
		if ((result.ValueKind != JsonValueKind.Object) ||
			!result.TryGetProperty("principalType", out JsonElement principalType) ||
			(principalType.ValueKind != JsonValueKind.String) ||
			!result.TryGetProperty("principalId", out JsonElement principalId) ||
			(principalId.ValueKind != JsonValueKind.String))
		{
			throw new GrokUsageSchemaException("Grok Build CLI 沒有提供可用來確認帳號的資料。");
		}

		string? type = principalType.GetString();
		string? id = principalId.GetString();

		if (!GrokAccountBinding.IsValidPrincipal(type, id))
		{
			throw new GrokUsageSchemaException("Grok Build CLI 提供的帳號資料格式無效。");
		}

		string? email = null;

		if (result.TryGetProperty("email", out JsonElement emailProperty) &&
			(emailProperty.ValueKind == JsonValueKind.String))
		{
			ProviderAccountIdentityRules.TryNormalize(
				emailProperty.GetString(),
				out email);
		}

		return new GrokPrincipal(type!, id!, email);
	}

	internal static string? ParsePlanTier(JsonElement result)
	{
		if (result.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		bool hasCurrentTier = result.TryGetProperty(
			"subscriptionTier",
			out JsonElement currentTier);
		bool hasLegacyTier = result.TryGetProperty(
			"subscription_tier",
			out JsonElement legacyTier);
		string? currentPlan = hasCurrentTier &&
			(currentTier.ValueKind == JsonValueKind.String)
			? GrokPlanTierRules.Normalize(currentTier.GetString())
			: null;
		string? legacyPlan = hasLegacyTier &&
			(legacyTier.ValueKind == JsonValueKind.String)
			? GrokPlanTierRules.Normalize(legacyTier.GetString())
			: null;

		if (hasCurrentTier && hasLegacyTier &&
			!string.Equals(currentPlan, legacyPlan, StringComparison.Ordinal))
		{
			return null;
		}

		return currentPlan ?? legacyPlan;
	}

	internal static GrokWeeklyUsage ParseWeeklyUsage(
		JsonElement result,
		DateTimeOffset observedAt)
	{
		if ((result.ValueKind != JsonValueKind.Object) ||
			!result.TryGetProperty("config", out JsonElement config) ||
			(config.ValueKind != JsonValueKind.Object) ||
			!config.TryGetProperty(
				"isUnifiedBillingUser",
				out JsonElement isUnifiedBillingUser) ||
			(isUnifiedBillingUser.ValueKind is not
				(JsonValueKind.True or JsonValueKind.False)))
		{
			throw new GrokUsageSchemaException("Grok billing 未提供有效的 unified billing 設定。");
		}

		if (!isUnifiedBillingUser.GetBoolean())
		{
			throw new GrokUnsupportedBillingException(
				"Grok 帳號未提供 unified billing 用量。");
		}

		if (!config.TryGetProperty("currentPeriod", out JsonElement currentPeriod) ||
			(currentPeriod.ValueKind != JsonValueKind.Object))
		{
			throw new GrokUsageSchemaException("Grok billing 不是 unified current-period 結構。");
		}

		if (!currentPeriod.TryGetProperty("type", out JsonElement periodType) ||
			(periodType.ValueKind != JsonValueKind.String))
		{
			throw new GrokUsageSchemaException("Grok billing current period 未提供有效的 type。");
		}

		if (!string.Equals(
				periodType.GetString(),
				"USAGE_PERIOD_TYPE_WEEKLY",
				StringComparison.Ordinal))
		{
			throw new GrokUnsupportedBillingException(
				"Grok 帳號未提供 weekly billing period。");
		}

		if (!TryReadTimestamp(currentPeriod, "start", out DateTimeOffset startsAt) ||
			!TryReadTimestamp(currentPeriod, "end", out DateTimeOffset resetsAt))
		{
			throw new GrokUsageSchemaException("Grok billing current period 不是有效的 weekly period。");
		}

		TimeSpan duration = resetsAt - startsAt;

		if ((duration < MinimumWeeklyPeriod) ||
			(duration > MaximumWeeklyPeriod) ||
			(startsAt > observedAt + MaximumClockSkew) ||
			(resetsAt <= observedAt - MaximumClockSkew))
		{
			throw new GrokUsageSchemaException("Grok weekly period 時間範圍無效或已過期。");
		}

		double? usedPercent = null;

		if (config.TryGetProperty("creditUsagePercent", out JsonElement percent) &&
			(percent.ValueKind != JsonValueKind.Null))
		{
			if ((percent.ValueKind != JsonValueKind.Number) ||
				!percent.TryGetDouble(out double parsedPercent) ||
				!double.IsFinite(parsedPercent) ||
				(parsedPercent < 0) ||
				(parsedPercent > 100))
			{
				throw new GrokUsageSchemaException("Grok weekly usage percent 超出有效範圍。");
			}

			usedPercent = parsedPercent;
		}

		return new GrokWeeklyUsage(usedPercent, startsAt, resetsAt);
	}

	private static bool TryReadTimestamp(
		JsonElement value,
		string propertyName,
		out DateTimeOffset timestamp)
	{
		timestamp = default;

		if (!value.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind != JsonValueKind.String))
		{
			return false;
		}

		string? valueText = property.GetString();

		return !string.IsNullOrWhiteSpace(valueText) &&
			DateTimeOffset.TryParseExact(
				valueText,
				Rfc3339TimestampFormats,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal |
					DateTimeStyles.AdjustToUniversal,
				out timestamp);
	}

	private static void ValidateNoDuplicateProperties(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> names = new(StringComparer.Ordinal);

			foreach (JsonProperty property in value.EnumerateObject())
			{
				if (!names.Add(property.Name))
				{
					throw new GrokUsageSchemaException("Grok ACP JSON 含有重複欄位。");
				}

				ValidateNoDuplicateProperties(property.Value);
			}
		}
		else if (value.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in value.EnumerateArray())
			{
				ValidateNoDuplicateProperties(item);
			}
		}
	}

	private static async Task DrainStderrAsync(
		Stream standardError,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
		int totalBytes = 0;

		try
		{
			while (true)
			{
				int read = await standardError.ReadAsync(
					buffer.AsMemory(0, buffer.Length),
					cancellationToken);

				if (read == 0)
				{
					return;
				}

				totalBytes = checked(totalBytes + read);

				if (totalBytes > MaximumStderrBytes)
				{
					throw new GrokUsageSchemaException("Grok ACP stderr 超過安全上限。");
				}
			}
		}
		finally
		{
			Array.Clear(buffer, 0, buffer.Length);
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static async Task ObserveDrainAfterTerminationAsync(Task stderrDrainTask)
	{
		await stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(1));
	}

	private static void ObserveTaskFault(Task task)
	{
		if (task.IsCompleted)
		{
			_ = task.Exception;
			return;
		}

		_ = task.ContinueWith(
			completedTask =>
			{
				_ = completedTask.Exception;
			},
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted |
				TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private static async Task ObserveProtocolTasksAfterCancellationAsync(
		Task protocolTask,
		Task stderrDrainTask,
		TimeSpan timeout)
	{
		Task allTasks = Task.WhenAll(protocolTask, stderrDrainTask);

		try
		{
			await allTasks.WaitAsync(timeout);
		}
		catch
		{
			// The primary protocol or stderr exception has already selected the
			// QueryAsync result. Cleanup only needs to bound and observe the
			// remaining work without replacing that result.
		}
		finally
		{
			ObserveTaskFault(allTasks);
		}
	}

	private static void ValidateLaunchOptions(GrokAcpLaunchOptions options)
	{
		if (!Path.IsPathFullyQualified(options.ExecutablePath) ||
			!Path.IsPathFullyQualified(options.HomeDirectory) ||
			!Path.IsPathFullyQualified(options.WorkingDirectory))
		{
			throw new ArgumentException("Grok ACP launch paths must be fully qualified.", nameof(options));
		}
	}
}

internal sealed class GrokBoundedJsonLineReader
{
	private readonly byte[] _buffer = new byte[4096];
	private readonly int _maximumLineBytes;
	private readonly int _maximumResponseCount;
	private readonly int _maximumTotalBytes;
	private readonly Stream _stream;
	private int _bufferLength;
	private int _bufferOffset;
	private int _responseCount;
	private int _totalBytes;

	internal GrokBoundedJsonLineReader(
		Stream stream,
		int maximumLineBytes,
		int maximumTotalBytes,
		int maximumResponseCount)
	{
		_stream = stream ?? throw new ArgumentNullException(nameof(stream));
		_maximumLineBytes = maximumLineBytes;
		_maximumTotalBytes = maximumTotalBytes;
		_maximumResponseCount = maximumResponseCount;

		if ((maximumLineBytes <= 0) ||
			(maximumTotalBytes < maximumLineBytes) ||
			(maximumResponseCount <= 0))
		{
			throw new ArgumentOutOfRangeException(nameof(maximumLineBytes));
		}
	}

	internal async Task<string> ReadLineAsync(CancellationToken cancellationToken)
	{
		using MemoryStream line = new(Math.Min(_maximumLineBytes, 4096));

		while (true)
		{
			if (_bufferOffset >= _bufferLength)
			{
				_bufferLength = await _stream.ReadAsync(_buffer, cancellationToken);
				_bufferOffset = 0;

				if (_bufferLength == 0)
				{
					throw new GrokAcpFailureException(
						"read",
						GrokAcpFailureCategory.Transient);
				}
			}

			int newlineIndex = Array.IndexOf(
				_buffer,
				(byte)'\n',
				_bufferOffset,
				_bufferLength - _bufferOffset);
			int segmentEnd = newlineIndex >= 0 ? newlineIndex : _bufferLength;
			int segmentLength = segmentEnd - _bufferOffset;
			AddConsumedBytes(segmentLength + (newlineIndex >= 0 ? 1 : 0));

			if ((line.Length + segmentLength) > _maximumLineBytes)
			{
				throw new GrokUsageSchemaException("Grok ACP JSON line 超過安全上限。");
			}

			line.Write(_buffer, _bufferOffset, segmentLength);
			_bufferOffset = newlineIndex >= 0 ? newlineIndex + 1 : segmentEnd;

			if (newlineIndex < 0)
			{
				continue;
			}

			_responseCount++;

			if (_responseCount > _maximumResponseCount)
			{
				throw new GrokUsageSchemaException("Grok ACP response count 超過安全上限。");
			}

			byte[] bytes = line.ToArray();
			int byteCount = bytes.Length;

			if ((byteCount > 0) && (bytes[byteCount - 1] == (byte)'\r'))
			{
				byteCount--;
			}

			if ((byteCount == 0) ||
				(Array.IndexOf(bytes, (byte)0, 0, byteCount) >= 0))
			{
				throw new GrokUsageSchemaException("Grok ACP 回應含空行或 NUL。");
			}

			try
			{
				return new UTF8Encoding(
					encoderShouldEmitUTF8Identifier: false,
					throwOnInvalidBytes: true).GetString(bytes, 0, byteCount);
			}
			catch (DecoderFallbackException)
			{
				throw new GrokUsageSchemaException("Grok ACP 回應不是有效 UTF-8。");
			}
		}
	}

	private void AddConsumedBytes(int count)
	{
		_totalBytes = checked(_totalBytes + count);

		if (_totalBytes > _maximumTotalBytes)
		{
			throw new GrokUsageSchemaException("Grok ACP stdout 超過安全上限。");
		}
	}
}
