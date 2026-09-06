using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeUsageProvider : IUsageProvider, IUsageProviderInvalidator
{
	private sealed class AccountPollingState
	{
		internal SemaphoreSlim Gate { get; } = new(1, 1);

		internal DateTimeOffset? LastAttemptAt { get; set; }

		internal UsageSnapshot? Snapshot { get; set; }

		internal bool ShouldSkipFallback { get; set; }
	}

	private sealed record AccountPollingOutcome(
		UsageSnapshot Snapshot,
		bool ShouldSkipFallback);

	private const int MaximumAccountIdentityLength = 320;
	private static readonly TimeSpan PollingRefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan PollingStaleAfter = TimeSpan.FromMinutes(2);
	private readonly Dictionary<Guid, string> _accountIdentitySeeds = new();
	private readonly Dictionary<Guid, AccountPollingState> _accountPollingStates = new();
	private readonly IClaudeAccountBindingStore? _bindingStore;
	private readonly IUsageProvider _fallbackProvider;
	private readonly object _pollingStateGate = new();
	private readonly IClaudeUsagePoller _poller;
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider => ProviderKind.Claude;

	public TimeSpan MinimumRefreshInterval => _fallbackProvider.MinimumRefreshInterval;

	internal int CachedAccountIdentitySeedCount
	{
		get
		{
			lock (_pollingStateGate)
			{
				return _accountIdentitySeeds.Count;
			}
		}
	}

	public ClaudeUsageProvider(
		IClaudeUsagePoller poller,
		IUsageProvider fallbackProvider,
		TimeProvider? timeProvider = null,
		IClaudeAccountBindingStore? bindingStore = null)
	{
		_poller = poller ?? throw new ArgumentNullException(nameof(poller));
		_fallbackProvider = fallbackProvider ??
			throw new ArgumentNullException(nameof(fallbackProvider));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_bindingStore = bindingStore;

		if (_fallbackProvider.Provider != ProviderKind.Claude)
		{
			throw new ArgumentException(
				"Claude fallback provider 必須屬於 Claude。",
				nameof(fallbackProvider));
		}
	}

	internal async Task CompleteAccountLoginAsync(
		Guid accountId,
		string accountIdentity,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		string identity = NormalizeRequiredAccountIdentity(accountIdentity);

		await DisableFallbackAsync(accountId, cancellationToken);

		lock (_pollingStateGate)
		{
			if (_bindingStore is null)
			{
				_accountIdentitySeeds[accountId] = identity;
			}
			else
			{
				_accountIdentitySeeds.Remove(accountId);
			}
			_accountPollingStates.Remove(accountId);
		}

	}

	public void Invalidate(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		lock (_pollingStateGate)
		{
			_accountPollingStates.Remove(accountId);
		}

	}

	internal void PurgeAccountState(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		lock (_pollingStateGate)
		{
			_accountIdentitySeeds.Remove(accountId);
			_accountPollingStates.Remove(accountId);
		}
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Claude)
		{
			throw new ArgumentException(
				"Claude provider 只能讀取 Claude 帳號。",
				nameof(account));
		}

		if (!account.HasAcceptedClaudeQuotaRisk)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				_timeProvider.GetUtcNow(),
				"請先確認 Claude 用量讀取的風險；確認後會自動更新，不會重新登入。",
				UsageRecoveryAction.ConnectAccount);
		}

		AccountPollingOutcome pollingOutcome = await GetPollingSnapshotAsync(
			account,
			cancellationToken);
		UsageSnapshot primary = pollingOutcome.Snapshot;

		if (IsFreshReady(primary))
		{
			return primary;
		}

		if (!string.IsNullOrWhiteSpace(primary.ProviderAccountIdentity))
		{
			return primary;
		}

		if (pollingOutcome.ShouldSkipFallback)
		{
			return primary;
		}

		UsageSnapshot fallback;

		try
		{
			fallback = await _fallbackProvider.GetUsageAsync(account, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			return HasUsableMetrics(primary) ||
				(primary.Status == SnapshotStatus.Error)
				? primary
				: CreateEmptySnapshot(
					account,
					SnapshotStatus.Error,
					_timeProvider.GetUtcNow(),
					"暫時無法讀取 Claude 用量，稍後會自動再試。",
					UsageRecoveryAction.Retry);
		}

		if (HasUsableMetrics(fallback))
		{
			if (!HasUsableMetrics(primary) ||
				(fallback.Status == SnapshotStatus.Ready) ||
				(primary.ObservedAt is null) ||
				(GetFreshness(fallback) > GetFreshness(primary)))
			{
				return PreservePrimaryRecoveryOnStaleFallback(primary, fallback);
			}
		}

		if (HasUsableMetrics(primary) ||
			(primary.Status == SnapshotStatus.Error) ||
			(primary.Status == SnapshotStatus.NotConfigured))
		{
			return primary;
		}

		return fallback;
	}

	private static UsageSnapshot PreservePrimaryRecoveryOnStaleFallback(
		UsageSnapshot primary,
		UsageSnapshot fallback)
	{
		if ((fallback.Status != SnapshotStatus.Stale) ||
			(primary.RecoveryAction == UsageRecoveryAction.None))
		{
			return fallback;
		}

		return fallback with
		{
			Error = primary.Error,
			RecoveryAction = primary.RecoveryAction,
			RetryNotBefore = primary.RetryNotBefore
		};
	}

	private static DateTimeOffset GetFreshness(UsageSnapshot snapshot)
	{
		return snapshot.ObservedAt ?? snapshot.FetchedAt;
	}

	private static bool HasUsableMetrics(UsageSnapshot snapshot)
	{
		return ((snapshot.Status == SnapshotStatus.Ready) ||
			(snapshot.Status == SnapshotStatus.Stale)) &&
			(snapshot.Metrics.Count > 0);
	}

	private static string NormalizeRequiredAccountIdentity(string accountIdentity)
	{
		string identity = accountIdentity?.Trim() ?? string.Empty;

		if ((identity.Length == 0) ||
			(identity.Length > MaximumAccountIdentityLength) ||
			identity.Any(char.IsControl))
		{
			throw new ArgumentException(
				"Claude 帳號資訊格式無效。",
				nameof(accountIdentity));
		}

		return identity;
	}

	private bool IsFreshReady(UsageSnapshot snapshot)
	{
		return (snapshot.Status == SnapshotStatus.Ready) &&
			(snapshot.Metrics.Count > 0) &&
			!snapshot.IsStaleAt(_timeProvider.GetUtcNow());
	}

	private async Task<AccountPollingOutcome> GetPollingSnapshotAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		AccountPollingState state = GetPollingState(account.Id);
		await state.Gate.WaitAsync(cancellationToken);

		try
		{
			DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

			if ((state.Snapshot is not null) &&
				(state.LastAttemptAt is not null) &&
				((fetchedAt - state.LastAttemptAt.Value) < PollingRefreshInterval))
			{
				return new AccountPollingOutcome(
					state.Snapshot with { Account = account },
					state.ShouldSkipFallback);
			}

			UsageSnapshot snapshot;
			bool shouldSkipFallback = false;

			try
			{
				snapshot = await PollCoreAsync(
					account,
					fetchedAt,
					cancellationToken);
				shouldSkipFallback = (_bindingStore is not null) ||
					(snapshot.RecoveryAction ==
						UsageRecoveryAction.RestartApplication);
			}
			catch (InvalidDataException exception) when (
				ClaudeCliUsagePoller.IsAccountIdentityUnavailable(exception) ||
				ClaudeCliUsagePoller.IsSubscriptionScopeUnavailable(exception))
			{
				shouldSkipFallback = true;
				snapshot = CreateEmptySnapshot(
					account,
					SnapshotStatus.Error,
					fetchedAt,
					exception.Message,
					UsageRecoveryAction.Retry,
					GetAccountIdentitySeed(account.Id),
					subscriptionVerificationState:
						_bindingStore is null
							? SubscriptionVerificationState.Unverified
							: SubscriptionVerificationState.TransientProbeError);
			}

			state.LastAttemptAt = _timeProvider.GetUtcNow();
			state.Snapshot = snapshot;
			state.ShouldSkipFallback = shouldSkipFallback;
			return new AccountPollingOutcome(snapshot, shouldSkipFallback);
		}
		finally
		{
			state.Gate.Release();
		}
	}

	private AccountPollingState GetPollingState(Guid accountId)
	{
		lock (_pollingStateGate)
		{
			if (!_accountPollingStates.TryGetValue(
				accountId,
				out AccountPollingState? state))
			{
				state = new AccountPollingState();
				_accountPollingStates.Add(accountId, state);
			}

			return state;
		}
	}

	private async Task<UsageSnapshot> PollCoreAsync(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		CancellationToken cancellationToken)
	{
		string? accountIdentity = GetAccountIdentitySeed(account.Id);
		bool isSubscriptionBound = _bindingStore is not null;
		ClaudeSubscriptionContext? subscriptionContext = null;

		if (isSubscriptionBound)
		{
			if (!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
					account.ProviderAccountIdentity,
					out accountIdentity) ||
				(accountIdentity is null))
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					fetchedAt,
					"尚未確認這張卡片的 Claude 訂閱。請使用原本的帳號，確認組織與訂閱方案。",
					UsageRecoveryAction.ConfirmSubscription);
			}

			SetAccountIdentitySeed(account.Id, accountIdentity);
		}

		try
		{
			ClaudeUsagePollResult pollResult = isSubscriptionBound
				? await _poller.PollBoundAsync(
					account.Id,
					accountIdentity!,
					cancellationToken)
				: await _poller.PollAsync(
					account.Id,
					cancellationToken);

			if (isSubscriptionBound)
			{
				subscriptionContext = pollResult.SubscriptionContext;

				if (subscriptionContext is null)
				{
					throw ClaudeCliUsagePoller
						.CreateSubscriptionScopeUnavailableException(
							"Claude Code 已登入有效訂閱，但暫時無法確認目前的訂閱範圍。AI Usage 稍後會自動再試。");
				}
			}
			else if (string.IsNullOrWhiteSpace(pollResult.AccountIdentity))
			{
				throw ClaudeCliUsagePoller.CreateAccountIdentityUnavailableException(
					"Claude Code 已登入有效訂閱，但暫時無法確認目前登入的帳號。AI Usage 稍後會自動再試。");
			}

			if (!isSubscriptionBound)
			{
				accountIdentity = NormalizeRequiredAccountIdentity(
					pollResult.AccountIdentity!);
			}

			SetAccountIdentitySeed(account.Id, accountIdentity!);
			await DisableFallbackAsync(account.Id, cancellationToken);

			ClaudeUsageTextParser.Result parsed;

			try
			{
				parsed = ClaudeUsageTextParser.Parse(
					pollResult.Output,
					pollResult.ObservedAt);
			}
			catch (InvalidDataException exception)
			{
				if (pollResult.VersionEvidence is not null)
				{
					exception.AttachVersionEvidence(
						pollResult.VersionEvidence);
				}

				throw;
			}
			SnapshotStatus status = parsed.IsStale
				? SnapshotStatus.Stale
				: SnapshotStatus.Ready;
			return new UsageSnapshot(
				account,
				parsed.Metrics,
				SourceTrust.OfficialExperimental,
				status,
				fetchedAt,
				parsed.IsStale ? null : pollResult.ObservedAt,
				parsed.IsStale
					? fetchedAt
					: pollResult.ObservedAt + PollingStaleAfter,
				parsed.IsStale
					? "目前顯示 Claude 上次成功讀取的用量（時間不明）。"
					: null,
				ProviderAccountIdentity: accountIdentity,
				ProviderAccountDisplayIdentity:
					subscriptionContext?.AccountIdentity,
				SubscriptionScopeDisplayName:
					subscriptionContext?.SubscriptionScopeDisplayName,
				PlanTier: subscriptionContext?.PlanTier,
				SubscriptionVerificationState:
					subscriptionContext is null
						? SubscriptionVerificationState.Unverified
						: SubscriptionVerificationState.Verified);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (ClaudeUsageSafetyException exception)
		{
			subscriptionContext = exception.SubscriptionContext;
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				exception.AppendVersionDiagnostic(exception.Message),
				exception.CanRetryAutomatically
					? UsageRecoveryAction.Retry
					: UsageRecoveryAction.RevalidateUsage,
				!isSubscriptionBound
					? exception.AccountIdentity ?? accountIdentity
					: accountIdentity,
				exception.AutomaticRetryNotBefore,
				providerAccountDisplayIdentity:
					subscriptionContext?.AccountIdentity ??
						exception.AccountIdentity,
				subscriptionScopeDisplayName:
					subscriptionContext?.SubscriptionScopeDisplayName,
				planTier: subscriptionContext?.PlanTier,
				subscriptionVerificationState:
					!isSubscriptionBound
						? SubscriptionVerificationState.Unverified
						: subscriptionContext is null
							? SubscriptionVerificationState.TransientProbeError
							: SubscriptionVerificationState.Verified);
		}
		catch (ClaudeCliContainmentException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				ClaudeCliContainmentException.RestartRequiredMessage,
				UsageRecoveryAction.RestartApplication,
				accountIdentity);
		}
		catch (ClaudeCliUntrustedException)
		{
			ClearAccountIdentitySeed(account.Id);
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"Claude Code CLI 未通過官方簽章、路徑或版本驗證；請從 Anthropic 官方來源重新安裝或更新。",
				UsageRecoveryAction.InstallOrUpdate);
		}
		catch (FileNotFoundException)
		{
			ClearAccountIdentitySeed(account.Id);
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"找不到 Claude Code CLI；請先安裝或更新 Claude Code。AI Usage 稍後會自動再試。",
				UsageRecoveryAction.InstallOrUpdate);
		}
		catch (DirectoryNotFoundException)
		{
			ClearAccountIdentitySeed(account.Id);
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"尚未連接 Claude 帳號；請使用卡片上的「連接 Claude 帳號」。",
				UsageRecoveryAction.ConnectAccount);
		}
		catch (ClaudeUsageNotConfiguredException exception)
		{
			string? notConfiguredIdentity = !isSubscriptionBound
				? exception.AccountIdentity
				: accountIdentity;

			if (string.IsNullOrWhiteSpace(notConfiguredIdentity))
			{
				ClearAccountIdentitySeed(account.Id);
			}
			else
			{
				SetAccountIdentitySeed(account.Id, notConfiguredIdentity);
			}

			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				exception.Message,
				exception.RecoveryAction,
				notConfiguredIdentity);
		}
		catch (ClaudeSubscriptionBindingMissingException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"Claude 訂閱連接資料不完整；請重新連接原本的帳號，確認訂閱範圍。",
				UsageRecoveryAction.ConnectAccount);
		}
		catch (ClaudeSubscriptionBindingInvalidException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"Claude 訂閱連接資料無效；請重新連接這張帳號卡片。",
				UsageRecoveryAction.SwitchAccount);
		}
		catch (ClaudeSubscriptionScopeMismatchException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"Claude Code 目前的帳號或組織與這張卡片的訂閱範圍不同；請切換或重新連接。",
				UsageRecoveryAction.SwitchAccount,
				accountIdentity,
				subscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch);
		}
		catch (ClaudeSubscriptionUsageUnavailableException exception)
		{
			subscriptionContext = exception.SubscriptionContext;
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				$"Claude {subscriptionContext.PlanTier} 訂閱範圍已確認，但這個方案的 /usage 格式尚未經驗證；AI Usage 不會猜測用量。",
				UsageRecoveryAction.Retry,
				accountIdentity,
				providerAccountDisplayIdentity:
					subscriptionContext.AccountIdentity,
				subscriptionScopeDisplayName:
					subscriptionContext.SubscriptionScopeDisplayName,
				planTier: subscriptionContext.PlanTier,
				subscriptionVerificationState:
					SubscriptionVerificationState.UsageUnavailable);
		}
		catch (ClaudeUsageQuotaUnavailableException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"Claude 暫時未回傳用量額度；AI Usage 稍後會自動再試，不需要操作。",
				UsageRecoveryAction.Retry,
				accountIdentity,
				providerAccountDisplayIdentity:
					subscriptionContext?.AccountIdentity,
				subscriptionScopeDisplayName:
					subscriptionContext?.SubscriptionScopeDisplayName,
				planTier: subscriptionContext?.PlanTier,
				subscriptionVerificationState:
					!isSubscriptionBound
						? SubscriptionVerificationState.Unverified
						: SubscriptionVerificationState.TransientProbeError);
		}
		catch (InvalidDataException exception) when (
			ClaudeCliUsagePoller.IsAccountIdentityUnavailable(exception) ||
			ClaudeCliUsagePoller.IsSubscriptionScopeUnavailable(exception))
		{
			throw;
		}
		catch (Exception exception) when (IsPollingFailure(exception))
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				exception.AppendVersionDiagnostic(
					"暫時無法讀取 Claude 用量，稍後會自動再試。"),
				UsageRecoveryAction.Retry,
				accountIdentity,
				subscriptionVerificationState:
					!isSubscriptionBound
						? SubscriptionVerificationState.Unverified
						: SubscriptionVerificationState.TransientProbeError);
		}
	}

	private static UsageSnapshot CreateEmptySnapshot(
		AccountProfile account,
		SnapshotStatus status,
		DateTimeOffset fetchedAt,
		string message,
		UsageRecoveryAction recoveryAction,
		string? providerAccountIdentity = null,
		DateTimeOffset? retryNotBefore = null,
		string? providerAccountDisplayIdentity = null,
		string? subscriptionScopeDisplayName = null,
		string? planTier = null,
		SubscriptionVerificationState subscriptionVerificationState =
			SubscriptionVerificationState.Unverified)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			fetchedAt,
			Error: message,
			ProviderAccountIdentity: providerAccountIdentity,
			RecoveryAction: recoveryAction,
			RetryNotBefore: retryNotBefore,
			ProviderAccountDisplayIdentity: providerAccountDisplayIdentity,
			SubscriptionScopeDisplayName: subscriptionScopeDisplayName,
			PlanTier: planTier,
			SubscriptionVerificationState: subscriptionVerificationState);
	}

	private void ClearAccountIdentitySeed(Guid accountId)
	{
		lock (_pollingStateGate)
		{
			_accountIdentitySeeds.Remove(accountId);
		}
	}

	private string? GetAccountIdentitySeed(Guid accountId)
	{
		lock (_pollingStateGate)
		{
			return _accountIdentitySeeds.GetValueOrDefault(accountId);
		}
	}

	private void SetAccountIdentitySeed(Guid accountId, string accountIdentity)
	{
		string identity = NormalizeRequiredAccountIdentity(accountIdentity);

		lock (_pollingStateGate)
		{
			_accountIdentitySeeds[accountId] = identity;
		}
	}

	private async Task DisableFallbackAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (_fallbackProvider is not IClaudeStatusLineFallbackControl fallbackControl)
		{
			throw new InvalidOperationException(
				"Claude fallback provider 無法安全停用舊 capture。");
		}

		await fallbackControl.DisableAsync(accountId, cancellationToken);
	}

	private static bool IsPollingFailure(Exception exception)
	{
		return (exception is InvalidDataException) ||
			(exception is DecoderFallbackException) ||
			(exception is InvalidOperationException) ||
			(exception is IOException) ||
			(exception is JsonException) ||
			(exception is OperationCanceledException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception) ||
			(exception is NotSupportedException);
	}
}
