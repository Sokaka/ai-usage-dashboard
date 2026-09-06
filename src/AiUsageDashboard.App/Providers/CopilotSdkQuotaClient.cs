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

		internal void Start()
		{
			_ = ReleaseLeaseAsync();
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

		internal SdkClient(string homeDirectory, string accessToken)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
			ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
			string executablePath = CopilotCliAccountConnector
				.ResolveExecutablePath() ??
				throw new FileNotFoundException(
					"找不到 AI Usage 隨附的 GitHub Copilot CLI runtime。");
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
			GitHub.Copilot.Rpc.AccountGetCurrentAuthResult currentAuth =
				await _client.Rpc.Account.GetCurrentAuthAsync(cancellationToken);
			return TryGetMatchingSubscriptionMetadata(
				currentAuth,
				expectedHost,
				expectedLogin);
		}

		public async Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>>
			GetQuotaAsync(CancellationToken cancellationToken)
		{
			GitHub.Copilot.Rpc.AccountGetQuotaResult result =
				await _client.Rpc.Account.GetQuotaAsync(
					_accessToken,
					cancellationToken);
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

	private const string CopilotFreeAccessTypeSku = "free_limited_copilot";
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
	private readonly Func<string, string, ICopilotSdkClient> _clientFactory;
	private readonly ICopilotCredentialStore _credentialStore;
	private readonly ICopilotGitHubUserClient _githubUserClient;
	private readonly Func<Guid, string> _homeDirectoryResolver;
	private readonly TimeSpan _operationTimeout;
	private readonly TimeSpan _planProbeTimeout;
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
			static (homeDirectory, accessToken) =>
				new SdkClient(homeDirectory, accessToken),
			static () => DateTimeOffset.UtcNow)
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
		TimeSpan? planProbeTimeout = null)
	{
		_homeDirectoryResolver = homeDirectoryResolver ??
			throw new ArgumentNullException(nameof(homeDirectoryResolver));
		_accountOperationGate = accountOperationGate ??
			throw new ArgumentNullException(nameof(accountOperationGate));
		_credentialStore = credentialStore ??
			throw new ArgumentNullException(nameof(credentialStore));
		_githubUserClient = githubUserClient ??
			throw new ArgumentNullException(nameof(githubUserClient));
		_clientFactory = clientFactory ??
			throw new ArgumentNullException(nameof(clientFactory));
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_operationTimeout = operationTimeout ?? DefaultOperationTimeout;
		_cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
		_planProbeTimeout = planProbeTimeout ?? DefaultPlanProbeTimeout;

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
				RetainLeaseUntilLifecycleCompletes(
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
			client = _clientFactory(homeDirectory, accessToken);
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
			CopilotSdkSubscriptionMetadata? subscriptionMetadata =
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
				subscriptionMetadata?.PlanTier,
				subscriptionMetadata?.IsTokenBasedBilling);
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
		Exception? cleanupFailure = client is null
			? null
			: await CleanupClientAsync(
				client,
				shouldAttemptStop,
				_cleanupTimeout,
				lifecycleTasks);
		Task lifecycleCompletion = ObserveTasksAsync(lifecycleTasks);

		return new QuotaCoreResult(
			report,
			operationFailure ?? cleanupFailure,
			lifecycleCompletion);
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

	private async Task<CopilotSdkSubscriptionMetadata?>
		TryGetSubscriptionMetadataAsync(
		ICopilotSdkClient client,
		CopilotAccountIdentity principal,
		CancellationToken callerCancellationToken,
		CancellationToken operationCancellationToken,
		ICollection<Task> lifecycleTasks)
	{
		using CancellationTokenSource planProbeSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				operationCancellationToken);
		planProbeSource.CancelAfter(_planProbeTimeout);

		Task<CopilotSdkSubscriptionMetadata?> planProbeTask;
		try
		{
			planProbeTask = client.GetSubscriptionMetadataAsync(
				principal.Host,
				principal.Login,
				planProbeSource.Token);
		}
		catch (OperationCanceledException) when (
			callerCancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			callerCancellationToken.ThrowIfCancellationRequested();
			return null;
		}

		lifecycleTasks.Add(planProbeTask);
		try
		{
			return await planProbeTask.WaitAsync(planProbeSource.Token);
		}
		catch (OperationCanceledException) when (
			callerCancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			callerCancellationToken.ThrowIfCancellationRequested();
			return null;
		}
	}

	internal static CopilotSdkSubscriptionMetadata?
		TryGetMatchingSubscriptionMetadata(
		GitHub.Copilot.Rpc.AccountGetCurrentAuthResult? currentAuth,
		string? expectedHost,
		string? expectedLogin)
	{
		(string? Host, string? Login, GitHub.Copilot.Rpc.CopilotUserResponse? User)
			metadata = currentAuth?.AuthInfo switch
			{
				GitHub.Copilot.Rpc.AuthInfoHmac value =>
					(value.Host, null, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoEnv value =>
					(value.Host, value.Login, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoToken value =>
					(value.Host, null, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoCopilotApiToken value =>
					(value.Host, null, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoUser value =>
					(value.Host, value.Login, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoGhCli value =>
					(value.Host, value.Login, value.CopilotUser),
				GitHub.Copilot.Rpc.AuthInfoApiKey value =>
					(value.Host, null, value.CopilotUser),
				_ => (null, null, null)
			};

		bool hasFreeAccessTypeSku = string.Equals(
			metadata.User?.AccessTypeSku?.Trim(),
			CopilotFreeAccessTypeSku,
			StringComparison.OrdinalIgnoreCase);
		if (!CopilotAccountIdentityRules.TryNormalizeHost(
				expectedHost,
				out string normalizedExpectedHost) ||
			!CopilotAccountIdentityRules.TryNormalizeHost(
				metadata.Host,
				out string normalizedObservedHost) ||
			!string.Equals(
				normalizedExpectedHost,
				normalizedObservedHost,
				StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(expectedLogin) ||
			(metadata.User is null) ||
			!string.Equals(
				metadata.User.Login?.Trim(),
				expectedLogin.Trim(),
				StringComparison.OrdinalIgnoreCase) ||
			(!string.IsNullOrWhiteSpace(metadata.Login) &&
				!string.Equals(
					metadata.Login.Trim(),
					expectedLogin.Trim(),
					StringComparison.OrdinalIgnoreCase)))
		{
			return null;
		}

		string? planTier = hasFreeAccessTypeSku
			? "free"
			: metadata.User.CopilotPlan;
		bool? isTokenBasedBilling = metadata.User.QuotaSnapshots?
			.PremiumInteractions?.TokenBasedBilling ??
			metadata.User.TokenBasedBilling;
		return new CopilotSdkSubscriptionMetadata(
			planTier,
			isTokenBasedBilling);
	}

	private static void RetainLeaseUntilLifecycleCompletes(
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
		tracker.Start();
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
