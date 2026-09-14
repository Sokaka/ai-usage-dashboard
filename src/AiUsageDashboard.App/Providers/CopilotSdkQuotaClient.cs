#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;

using GitHub.Copilot;

namespace AiUsageDashboard.App.Providers;

internal sealed record CopilotSdkQuotaValue(
	long EntitlementRequests,
	long UsedRequests,
	double RemainingPercentage,
	DateTimeOffset? ResetDate,
	bool IsUnlimitedEntitlement,
	bool OverageAllowedWithExhaustedQuota,
	bool UsageAllowedWithExhaustedQuota);

internal sealed record CopilotSdkSubscriptionMetadata(
	string? PlanTier,
	bool? IsTokenBasedBilling);

internal interface ICopilotSdkClient : IAsyncDisposable
{
	Task StartAsync(CancellationToken cancellationToken);

	Task<CopilotSdkSubscriptionMetadata?> GetSubscriptionMetadataAsync(
		string expectedHost,
		string expectedLogin,
		CancellationToken cancellationToken);

	Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>> GetQuotaAsync(
		CancellationToken cancellationToken);

	Task StopAsync();

	Task ForceStopAsync();
}

internal sealed class CopilotSdkQuotaClient : ICopilotQuotaClient
{
	private sealed record SubscriptionProbeResult(
		CopilotSdkSubscriptionMetadata? Metadata,
		string? Warning);

	private sealed record QuotaCoreResult(
		CopilotUsageReport? Report,
		Exception? Failure,
		Task LifecycleCompletion);

	private sealed class CleanupLeaseTracker
	{
		private readonly Task _lifecycleCompletion;
		private readonly long _trackerId;
		private IDisposable? _operationLease;

		internal CleanupLeaseTracker(
			long trackerId,
			IDisposable operationLease,
			Task lifecycleCompletion)
		{
			_trackerId = trackerId;
			_operationLease = operationLease;
			_lifecycleCompletion = lifecycleCompletion;
		}

		internal Task Start()
		{
			return ReleaseLeaseAsync();
		}

		private async Task ReleaseLeaseAsync()
		{
			try
			{
				await _lifecycleCompletion.ConfigureAwait(false);
			}
			catch
			{
			}
			finally
			{
				try
				{
					Interlocked.Exchange(ref _operationLease, null)?.Dispose();
				}
				catch
				{
				}

				ActiveCleanupLeaseTrackers.TryRemove(_trackerId, out _);
			}
		}
	}

	private sealed class SdkClient : ICopilotSdkClient
	{
		private readonly string _accessToken;
		private readonly CopilotClient _client;

		internal SdkClient(
			string homeDirectory,
			string accessToken,
			string executablePath)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
			ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
			ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
			_accessToken = accessToken;
			_client = new CopilotClient(new CopilotClientOptions
			{
				BaseDirectory = homeDirectory,
				Connection = RuntimeConnection.ForStdio(path: executablePath),
				Environment = CopilotProcessEnvironment
					.CreateSanitizedEnvironment(homeDirectory),
				GitHubToken = accessToken,
				LogLevel = CopilotLogLevel.None,
				Mode = CopilotClientMode.Empty,
				UseLoggedInUser = false,
				WorkingDirectory = homeDirectory
			});
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			return _client.StartAsync(cancellationToken);
		}

		public async Task<CopilotSdkSubscriptionMetadata?>
			GetSubscriptionMetadataAsync(
			string expectedHost,
			string expectedLogin,
			CancellationToken cancellationToken)
		{
			JsonElement currentAuth = await CopilotSubscriptionRpc.ReadAsync(
				_client.Rpc.Account,
				cancellationToken);
			return CopilotSubscriptionMetadataReader.Read(
				currentAuth,
				expectedHost,
				expectedLogin);
		}

		public async Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>>
			GetQuotaAsync(CancellationToken cancellationToken)
		{
			GitHub.Copilot.Rpc.AccountGetQuotaResult result =
				await _client.Rpc.Account.GetQuotaAsync(
					gitHubToken: _accessToken,
					cancellationToken: cancellationToken);
			Dictionary<string, CopilotSdkQuotaValue> quotas =
				new(StringComparer.Ordinal);

			foreach ((string key, GitHub.Copilot.Rpc.AccountQuotaSnapshot value) in
				result.QuotaSnapshots)
			{
				quotas[key] = new CopilotSdkQuotaValue(
					value.EntitlementRequests,
					value.UsedRequests,
					value.RemainingPercentage,
					value.ResetDate,
					value.IsUnlimitedEntitlement,
					value.OverageAllowedWithExhaustedQuota,
					value.UsageAllowedWithExhaustedQuota);
			}

			return quotas;
		}

		public Task StopAsync()
		{
			return _client.StopAsync();
		}

		public Task ForceStopAsync()
		{
			return _client.ForceStopAsync();
		}

		public ValueTask DisposeAsync()
		{
			return _client.DisposeAsync();
		}
	}

	private static readonly ConcurrentDictionary<long, CleanupLeaseTracker>
		ActiveCleanupLeaseTrackers = new();
	private static readonly TimeSpan DefaultCleanupTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan DefaultOperationTimeout =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan DefaultPlanProbeTimeout =
		TimeSpan.FromSeconds(3);
	private static long _nextCleanupLeaseTrackerId;
	private readonly CopilotAccountOperationGate _accountOperationGate;
	private readonly TimeSpan _cleanupTimeout;
	private readonly Func<string, string, string, ICopilotSdkClient> _clientFactory;
	private readonly ICopilotCredentialStore _credentialStore;
	private readonly Func<WindowsOfficialCliExecutableLease?>? _executableResolver;
	private readonly ICopilotGitHubUserClient _githubUserClient;
	private readonly Func<Guid, string> _homeDirectoryResolver;
	private readonly TimeSpan _operationTimeout;
	private readonly TimeSpan _planProbeTimeout;
	private readonly Func<string, Exception?, bool>? _reportSubscriptionDiagnostic;
	private readonly Func<DateTimeOffset> _utcNow;

	internal int ActiveLateCleanupCount => ActiveCleanupLeaseTrackers.Count;

	internal CopilotSdkQuotaClient(
		Func<Guid, string> homeDirectoryResolver,
		CopilotAccountOperationGate accountOperationGate,
		ICopilotCredentialStore credentialStore,
		ICopilotGitHubUserClient githubUserClient)
		: this(
			homeDirectoryResolver,
			accountOperationGate,
			credentialStore,
			githubUserClient,
			CopilotCliExecutableResolver.Resolve,
			static (homeDirectory, accessToken, executablePath) =>
				new SdkClient(homeDirectory, accessToken, executablePath),
			static () => DateTimeOffset.UtcNow,
			reportSubscriptionDiagnostic: static (summary, exception) =>
				AppDiagnostics.TryWrite("copilot-subscription", summary, exception).WasWritten)
	{
	}

	internal CopilotSdkQuotaClient(
		Func<Guid, string> homeDirectoryResolver,
		CopilotAccountOperationGate accountOperationGate,
		ICopilotCredentialStore credentialStore,
		ICopilotGitHubUserClient githubUserClient,
		Func<string, string, ICopilotSdkClient> clientFactory,
		Func<DateTimeOffset>? utcNow = null,
		TimeSpan? operationTimeout = null,
		TimeSpan? cleanupTimeout = null,
		TimeSpan? planProbeTimeout = null,
		Func<string, Exception?, bool>? reportSubscriptionDiagnostic = null)
		: this(
			homeDirectoryResolver,
			accountOperationGate,
			credentialStore,
			githubUserClient,
			executableResolver: null,
			AdaptClientFactory(clientFactory),
			utcNow,
			operationTimeout,
			cleanupTimeout,
			planProbeTimeout,
			reportSubscriptionDiagnostic)
	{
	}

	internal CopilotSdkQuotaClient(
		Func<Guid, string> homeDirectoryResolver,
		CopilotAccountOperationGate accountOperationGate,
		ICopilotCredentialStore credentialStore,
		ICopilotGitHubUserClient githubUserClient,
		Func<WindowsOfficialCliExecutableLease?>? executableResolver,
		Func<string, string, string, ICopilotSdkClient> clientFactory,
		Func<DateTimeOffset>? utcNow = null,
		TimeSpan? operationTimeout = null,
		TimeSpan? cleanupTimeout = null,
		TimeSpan? planProbeTimeout = null,
		Func<string, Exception?, bool>? reportSubscriptionDiagnostic = null)
	{
		_homeDirectoryResolver = homeDirectoryResolver ??
			throw new ArgumentNullException(nameof(homeDirectoryResolver));
		_accountOperationGate = accountOperationGate ??
			throw new ArgumentNullException(nameof(accountOperationGate));
		_credentialStore = credentialStore ??
			throw new ArgumentNullException(nameof(credentialStore));
		_executableResolver = executableResolver;
		_githubUserClient = githubUserClient ??
			throw new ArgumentNullException(nameof(githubUserClient));
		_clientFactory = clientFactory ??
			throw new ArgumentNullException(nameof(clientFactory));
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_operationTimeout = operationTimeout ?? DefaultOperationTimeout;
		_cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
		_planProbeTimeout = planProbeTimeout ?? DefaultPlanProbeTimeout;
		_reportSubscriptionDiagnostic = reportSubscriptionDiagnostic;

		if (_operationTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(operationTimeout));
		}

		if (_cleanupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
		}

		if (_planProbeTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(planProbeTimeout));
		}
	}

	public async Task ReconcileCredentialAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using IDisposable operationLease =
			await _accountOperationGate.EnterAsync(accountId, cancellationToken);
		_credentialStore.RecoverStaged(
			accountId,
			expectedProviderAccountIdentity);
	}

	public async Task<CopilotUsageReport> GetAccountQuotaAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IDisposable? operationLease = await _accountOperationGate.EnterAsync(
			accountId,
			cancellationToken);

		try
		{
			_credentialStore.RecoverStaged(
				accountId,
				expectedProviderAccountIdentity);
			CopilotStoredCredential credential =
				_credentialStore.Read(accountId) ??
				throw new CopilotClientException(
					CopilotFailureKind.AuthenticationRequired,
					"找不到這張卡片的 Copilot credential。");

			if (!CopilotAccountIdentityRules.TryParse(
					credential.ProviderAccountIdentity,
					out string? storedIdentity,
					out string? storedHost))
			{
				throw new CopilotClientException(
					CopilotFailureKind.AuthenticationRequired,
					"Copilot credential binding 格式無效。");
			}

			CopilotAccountIdentity principal = await _githubUserClient
				.GetAuthenticatedUserAsync(
					storedHost!,
					credential.AccessToken,
					cancellationToken);
			string observedIdentity = CopilotAccountIdentityRules.Create(
				principal);
			if (!CopilotAccountIdentityRules.AreEquivalent(
					storedIdentity,
					observedIdentity) ||
				!CopilotAccountIdentityRules.AreEquivalent(
					expectedProviderAccountIdentity,
					observedIdentity))
			{
				throw new CopilotClientException(
					CopilotFailureKind.AccountMismatch,
					"Copilot credential 與卡片身分不同。");
			}

			QuotaCoreResult result = await GetQuotaCoreAsync(
				accountId,
				credential.AccessToken,
				principal,
				cancellationToken);
			if (!result.LifecycleCompletion.IsCompleted)
			{
				_ = RetainLeaseUntilLifecycleCompletes(
					operationLease,
					result.LifecycleCompletion);
				operationLease = null;
			}

			if (result.Failure is not null)
			{
				ThrowNormalized(result.Failure);
			}

			return result.Report ?? throw new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"GitHub Copilot 未回傳用量資料。");
		}
		finally
		{
			operationLease?.Dispose();
		}
	}

	internal async Task<(
		CopilotUsageReport? Report,
		Exception? Failure,
		Task LifecycleCompletion)> ProbeTokenWithLifecycleAsync(
		Guid accountId,
		string accessToken,
		string host,
		CancellationToken cancellationToken)
	{
		CopilotAccountIdentity principal = await _githubUserClient
			.GetAuthenticatedUserAsync(host, accessToken, cancellationToken);
		QuotaCoreResult result = await GetQuotaCoreAsync(
			accountId,
			accessToken,
			principal,
			cancellationToken);
		return (result.Report, result.Failure, result.LifecycleCompletion);
	}

	private async Task<QuotaCoreResult> GetQuotaCoreAsync(
		Guid accountId,
		string accessToken,
		CopilotAccountIdentity principal,
		CancellationToken cancellationToken)
	{
		ICopilotSdkClient? client = null;
		WindowsOfficialCliExecutableLease? executableLease = null;
		CopilotUsageReport? report = null;
		Exception? operationFailure = null;
		bool didStart = false;
		bool startWasInvoked = false;
		List<Task> lifecycleTasks = new();
		using CancellationTokenSource timeoutSource = new(_operationTimeout);
		using CancellationTokenSource operationSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			string homeDirectory = ResolveHomeDirectory(accountId);
			executableLease = await CopilotCliExecutableResolver.ResolveAsync(
				ResolveExecutableLease,
				operationSource.Token);
			operationSource.Token.ThrowIfCancellationRequested();
			client = _clientFactory(
				homeDirectory,
				accessToken,
				executableLease?.ExecutablePath ?? string.Empty);
			startWasInvoked = true;
			Task startTask = client.StartAsync(operationSource.Token);
			lifecycleTasks.Add(startTask);
			await startTask.WaitAsync(operationSource.Token);
			didStart = true;

			Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>> quotaTask =
				client.GetQuotaAsync(operationSource.Token);
			lifecycleTasks.Add(quotaTask);
			IReadOnlyDictionary<string, CopilotSdkQuotaValue> quotaValues =
				await quotaTask.WaitAsync(operationSource.Token);
			IReadOnlyList<CopilotQuotaSnapshot> quotas = MapQuotas(quotaValues);
			SubscriptionProbeResult subscription =
				await TryGetSubscriptionMetadataAsync(
				client,
				principal,
				cancellationToken,
				operationSource.Token,
				lifecycleTasks);
			report = new CopilotUsageReport(
				principal,
				quotas,
				_utcNow().ToUniversalTime(),
				subscription.Metadata?.PlanTier,
				subscription.Metadata?.IsTokenBasedBilling,
				subscription.Warning);
		}
		catch (OperationCanceledException exception) when (
			!cancellationToken.IsCancellationRequested &&
			timeoutSource.IsCancellationRequested)
		{
			operationFailure = new TimeoutException(
				"GitHub Copilot quota request exceeded its deadline.",
				exception);
		}
		catch (Exception exception)
		{
			operationFailure = exception;
		}

		bool shouldAttemptStop = didStart ||
			(startWasInvoked &&
				(operationFailure is OperationCanceledException or TimeoutException));
		Task? lifecycleCompletion = null;
		try
		{
			Exception? cleanupFailure = client is null
				? null
				: await CleanupClientAsync(
					client,
					shouldAttemptStop,
					_cleanupTimeout,
					lifecycleTasks);
			lifecycleCompletion = ObserveTasksAsync(lifecycleTasks);
			if (executableLease is not null)
			{
				lifecycleCompletion = RetainLeaseUntilLifecycleCompletes(
					executableLease,
					lifecycleCompletion);
				executableLease = null;
			}

			return new QuotaCoreResult(
				report,
				operationFailure ?? cleanupFailure,
				lifecycleCompletion);
		}
		finally
		{
			if (executableLease is not null)
			{
				// 面向使用者的逾時不代表 SDK 已停止使用執行檔。
				_ = RetainLeaseUntilLifecycleCompletes(
					executableLease,
					lifecycleCompletion ?? ObserveTasksAsync(lifecycleTasks));
			}
		}
	}

	private string ResolveHomeDirectory(Guid accountId)
	{
		try
		{
			string homeDirectory = Path.GetFullPath(
				_homeDirectoryResolver(accountId));
			Directory.CreateDirectory(homeDirectory);
			return homeDirectory;
		}
		catch (Exception exception) when (
			exception is ArgumentException or
			IOException or
			NotSupportedException or
			UnauthorizedAccessException)
		{
			throw new InvalidOperationException(
				"GitHub Copilot isolated home is unavailable.",
				exception);
		}
	}

	private static Func<string, string, string, ICopilotSdkClient> AdaptClientFactory(
		Func<string, string, ICopilotSdkClient> clientFactory)
	{
		ArgumentNullException.ThrowIfNull(clientFactory);
		return (homeDirectory, accessToken, _) => clientFactory(homeDirectory, accessToken);
	}

	private WindowsOfficialCliExecutableLease? ResolveExecutableLease()
	{
		if (_executableResolver is null)
		{
			return null;
		}

		const string Guidance =
			"本機官方 GitHub Copilot CLI 無法使用。請依 GitHub 官方安裝說明安裝或更新後重試；" +
			"卡片與登入資料會保留，不必重新登入：" +
			"https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli";
		try
		{
			return _executableResolver() ?? throw new CopilotClientException(
				CopilotFailureKind.RuntimeUnavailable,
				Guidance);
		}
		catch (IOException exception)
		{
			throw new CopilotClientException(
				CopilotFailureKind.RuntimeUnavailable,
				Guidance,
				innerException: exception);
		}
	}

	private static async Task<Exception?> CleanupClientAsync(
		ICopilotSdkClient client,
		bool shouldAttemptStop,
		TimeSpan cleanupTimeout,
		ICollection<Task> lifecycleTasks)
	{
		List<Exception> failures = new();

		if (shouldAttemptStop)
		{
			try
			{
				Task stopTask = client.StopAsync();
				lifecycleTasks.Add(stopTask);
				await stopTask.WaitAsync(cleanupTimeout);
			}
			catch (Exception stopException)
			{
				failures.Add(stopException);

				try
				{
					Task forceStopTask = client.ForceStopAsync();
					lifecycleTasks.Add(forceStopTask);
					await forceStopTask.WaitAsync(cleanupTimeout);
				}
				catch (Exception forceStopException)
				{
					failures.Add(forceStopException);
				}
			}
		}

		try
		{
			Task disposeTask = client.DisposeAsync().AsTask();
			lifecycleTasks.Add(disposeTask);
			await disposeTask.WaitAsync(cleanupTimeout);
		}
		catch (Exception disposeException)
		{
			failures.Add(disposeException);
		}

		return failures.Count switch
		{
			0 => null,
			1 => failures[0],
			_ => new AggregateException(failures)
		};
	}

	private static async Task ObserveTasksAsync(IReadOnlyCollection<Task> tasks)
	{
		foreach (Task task in tasks)
		{
			_ = task.ContinueWith(
				static faultedTask => _ = faultedTask.Exception,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously |
					TaskContinuationOptions.OnlyOnFaulted,
				TaskScheduler.Default);
		}

		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private async Task<SubscriptionProbeResult> TryGetSubscriptionMetadataAsync(
		ICopilotSdkClient client,
		CopilotAccountIdentity principal,
		CancellationToken callerCancellationToken,
		CancellationToken operationCancellationToken,
		ICollection<Task> lifecycleTasks)
	{
		using CancellationTokenSource planProbeSource =
			CancellationTokenSource.CreateLinkedTokenSource(operationCancellationToken);
		planProbeSource.CancelAfter(_planProbeTimeout);

		try
		{
			Task<CopilotSdkSubscriptionMetadata?> probe = client.GetSubscriptionMetadataAsync(
				principal.Host,
				principal.Login,
				planProbeSource.Token);
			lifecycleTasks.Add(probe);
			CopilotSdkSubscriptionMetadata? metadata = await probe.WaitAsync(planProbeSource.Token);
			return metadata is null
				? RecordSubscriptionFailure("Copilot 未提供訂閱資訊；用量仍可使用。")
				: new SubscriptionProbeResult(metadata, Warning: null);
		}
		catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			callerCancellationToken.ThrowIfCancellationRequested();
			string summary = exception switch
			{
				OperationCanceledException or TimeoutException =>
					"Copilot 訂閱資訊查詢逾時；用量仍可使用。",
				JsonException or InvalidDataException =>
					"Copilot 訂閱資訊格式或帳號無法確認；用量仍可使用。",
				NotSupportedException =>
					"Copilot 訂閱查詢介面不相容；用量仍可使用。",
				_ => "Copilot 訂閱資訊暫時無法讀取；用量仍可使用。"
			};
			return RecordSubscriptionFailure(summary, exception);
		}
	}

	private SubscriptionProbeResult RecordSubscriptionFailure(
		string summary,
		Exception? exception = null)
	{
		if ((_reportSubscriptionDiagnostic is not null) &&
			!_reportSubscriptionDiagnostic(summary, exception))
		{
			summary += " 診斷紀錄無法寫入。";
		}
		return new SubscriptionProbeResult(Metadata: null, summary);
	}

	private static Task RetainLeaseUntilLifecycleCompletes(
		IDisposable operationLease,
		Task lifecycleCompletion)
	{
		long trackerId = Interlocked.Increment(
			ref _nextCleanupLeaseTrackerId);
		CleanupLeaseTracker tracker = new(
			trackerId,
			operationLease,
			lifecycleCompletion);
		ActiveCleanupLeaseTrackers[trackerId] = tracker;
		return tracker.Start();
	}

	private static CopilotClientException CreateNormalizedException(
		Exception exception)
	{
		if (exception is CopilotClientException clientException)
		{
			return clientException;
		}

		string diagnosticText = GetDiagnosticText(exception);

		if (ContainsAny(
				diagnosticText,
				"rate limit",
				"too many requests",
				"status code 429",
				"http 429"))
		{
			return new CopilotClientException(
				CopilotFailureKind.RateLimited,
				"GitHub Copilot 用量查詢已達速率限制。");
		}

		if (ContainsAny(
				diagnosticText,
				"not authenticated",
				"authentication required",
				"not logged in",
				"unauthorized",
				"status code 401",
				"http 401"))
		{
			return new CopilotClientException(
				CopilotFailureKind.AuthenticationRequired,
				"GitHub Copilot credential 已失效。");
		}

		if (ContainsAny(
				diagnosticText,
				"forbidden",
				"status code 403",
				"http 403"))
		{
			return new CopilotClientException(
				CopilotFailureKind.PermissionDenied,
				"GitHub Copilot credential 無權讀取用量。");
		}

		if ((exception is FileNotFoundException) ||
			(exception is DllNotFoundException) ||
			(exception is BadImageFormatException) ||
			(exception is Win32Exception) ||
			(exception is InvalidOperationException) ||
			ContainsAny(
				diagnosticText,
				"copilot runtime not found",
				"copilot cli process exited",
				"protocol version mismatch",
				"failed to start"))
		{
			return new CopilotClientException(
				CopilotFailureKind.RuntimeUnavailable,
				"GitHub Copilot CLI runtime 無法使用。");
		}

		if ((exception is JsonException) ||
			(exception is FormatException) ||
			(exception is OverflowException))
		{
			return new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"GitHub Copilot 用量資料無法辨識。");
		}

		return new CopilotClientException(
			CopilotFailureKind.Transient,
			"暫時無法讀取 GitHub Copilot 用量。");
	}

	private static string GetDiagnosticText(Exception exception)
	{
		List<string> messages = new();
		HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
		Queue<Exception> pending = new();
		pending.Enqueue(exception);

		while (pending.Count > 0)
		{
			Exception current = pending.Dequeue();
			if (!visited.Add(current))
			{
				continue;
			}

			if (!string.IsNullOrWhiteSpace(current.Message))
			{
				messages.Add(current.Message);
			}

			if (current is AggregateException aggregateException)
			{
				foreach (Exception innerException in
					aggregateException.InnerExceptions)
				{
					pending.Enqueue(innerException);
				}
			}
			else if (current.InnerException is not null)
			{
				pending.Enqueue(current.InnerException);
			}
		}

		return string.Join("\n", messages);
	}

	private static bool ContainsAny(string value, params string[] candidates)
	{
		return candidates.Any(candidate =>
			value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
	}

	internal static IReadOnlyList<CopilotQuotaSnapshot> MapQuotas(
		IReadOnlyDictionary<string, CopilotSdkQuotaValue>? quotaValues)
	{
		if (quotaValues is null)
		{
			throw new CopilotClientException(
				CopilotFailureKind.InvalidResponse,
				"GitHub Copilot 未回傳 quota snapshots。");
		}

		List<CopilotQuotaSnapshot> quotas = new(quotaValues.Count);
		foreach ((string key, CopilotSdkQuotaValue? value) in
			quotaValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (string.IsNullOrWhiteSpace(key) ||
				(value is null) ||
				(value.EntitlementRequests < -1) ||
				(value.UsedRequests < 0) ||
				!double.IsFinite(value.RemainingPercentage) ||
				(value.RemainingPercentage < 0) ||
				(value.RemainingPercentage > 100))
			{
				throw new CopilotClientException(
					CopilotFailureKind.InvalidResponse,
					"GitHub Copilot quota snapshot 無法辨識。");
			}

			quotas.Add(new CopilotQuotaSnapshot(
				key,
				value.EntitlementRequests,
				value.UsedRequests,
				value.RemainingPercentage,
				value.ResetDate?.ToUniversalTime(),
				value.IsUnlimitedEntitlement,
				value.OverageAllowedWithExhaustedQuota,
				value.UsageAllowedWithExhaustedQuota));
		}

		return quotas;
	}

	internal static void ThrowNormalized(Exception exception)
	{
		if (exception is OperationCanceledException)
		{
			ExceptionDispatchInfo.Capture(exception).Throw();
		}

		throw CreateNormalizedException(exception);
	}
}

#pragma warning restore GHCP001
