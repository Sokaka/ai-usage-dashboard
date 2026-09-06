using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.Core.Refreshing;

public sealed class UsageRefreshCoordinator : IUsageRefreshCoordinator
{
	private sealed class AccountRefreshState
	{
		public ProviderKind Provider { get; }

		public int ConsecutiveRetryableFailures { get; set; }

		public long Generation { get; set; }

		public TimeSpan? GenericRetryDelay { get; set; }

		public long? GenericRetryStartedTimestamp { get; set; }

		public long? LastAttemptTimestamp { get; set; }

		public DateTimeOffset? ProviderRetryNotBefore { get; set; }

		public UsageSnapshot? Current { get; set; }

		public UsageSnapshot? LastKnownGood { get; set; }

		public Task<UsageSnapshot>? InFlight { get; set; }

		public CancellationTokenSource? InFlightCancellationSource { get; set; }

		public AccountRefreshState(ProviderKind provider)
		{
			Provider = provider;
		}
	}

	private const int MaxRetryBackoffStep = 5;
	private const int DefaultMaximumConcurrentRefreshes = 4;
	private const string GenericErrorMessage =
		"暫時無法取得用量，AI Usage 稍後會自動再試。";
	private const string ResetBoundaryCooldownMessage =
		"用量已重置，AI Usage 稍後會自動再試。";
	private const string RefreshStoppedMessage = "用量檢查已停止。";
	private readonly Dictionary<Guid, AccountRefreshState> _accountStates = new();
	private readonly Action<ProviderKind, Exception>? _reportUnexpectedException;
	private readonly SemaphoreSlim _refreshConcurrencyGate;
	private readonly object _stateGate = new();
	private readonly TimeProvider _timeProvider;
	private readonly UsageProviderRegistry _providerRegistry;

	public UsageRefreshCoordinator(
		UsageProviderRegistry providerRegistry,
		TimeProvider? timeProvider = null,
		int maximumConcurrentRefreshes = DefaultMaximumConcurrentRefreshes,
		Action<ProviderKind, Exception>? reportUnexpectedException = null)
	{
		if (maximumConcurrentRefreshes <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(maximumConcurrentRefreshes),
				maximumConcurrentRefreshes,
				"同時刷新數量必須大於零。");
		}

		_providerRegistry = providerRegistry ??
			throw new ArgumentNullException(nameof(providerRegistry));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_refreshConcurrencyGate = new SemaphoreSlim(
			maximumConcurrentRefreshes,
			maximumConcurrentRefreshes);
		_reportUnexpectedException = reportUnexpectedException;
	}

	public TimeSpan? GetRemainingCooldown(AccountProfile account)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Id == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(account));
		}

		IUsageProvider provider = _providerRegistry.GetRequired(account.Provider);
		DateTimeOffset now = _timeProvider.GetUtcNow();
		long nowTimestamp = _timeProvider.GetTimestamp();

		lock (_stateGate)
		{
			if (!_accountStates.TryGetValue(
				account.Id,
				out AccountRefreshState? state) ||
				(state.Provider != account.Provider))
			{
				return null;
			}

			return GetRemainingCooldownCore(
				state,
				provider,
				now,
				nowTimestamp);
		}
	}

	public Task<UsageSnapshot> RefreshAsync(
		AccountProfile account,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);
		cancellationToken.ThrowIfCancellationRequested();

		if (account.Id == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(account));
		}

		IUsageProvider provider = _providerRegistry.GetRequired(account.Provider);
		TaskCompletionSource<UsageSnapshot>? completionSource = null;
		CancellationTokenSource? refreshCancellationSource = null;
		CancellationTokenSource? replacedCancellationSource = null;
		ProviderKind? replacedProvider = null;
		Task<UsageSnapshot> sharedTask;
		AccountRefreshState state;
		UsageSnapshot? lastKnownGood = null;
		long generation = 0;
		DateTimeOffset now = _timeProvider.GetUtcNow();
		long nowTimestamp = _timeProvider.GetTimestamp();

		lock (_stateGate)
		{
			if (_accountStates.TryGetValue(account.Id, out AccountRefreshState? existingState) &&
				(existingState.Provider != account.Provider))
			{
				existingState.Generation++;
				replacedCancellationSource = existingState.InFlightCancellationSource;
				replacedProvider = existingState.Provider;
				_accountStates.Remove(account.Id);
			}

			if (!_accountStates.TryGetValue(account.Id, out state!))
			{
				state = new AccountRefreshState(account.Provider);
				_accountStates.Add(account.Id, state);
			}

			PruneExpiredSnapshots(state, now);

			if (state.InFlight is not null)
			{
				sharedTask = state.InFlight;
			}
			else if (CanUseCachedSnapshot(
				state,
				provider,
				now,
				nowTimestamp))
			{
				UsageSnapshot cachedSnapshot = NormalizeCachedSnapshot(
					state.Current!,
					account,
					now);
				state.Current = cachedSnapshot;
				sharedTask = Task.FromResult(cachedSnapshot);
			}
			else if (GetRemainingCooldownCore(
					state,
					provider,
					now,
					nowTimestamp) is TimeSpan remainingCooldown)
			{
				DateTimeOffset nextEligibleAttemptAt =
					AddCooldown(now, remainingCooldown);
				UsageSnapshot cooldownSnapshot = CreateFailureSnapshot(
					account,
					null,
					now,
					ResetBoundaryCooldownMessage,
					UsageRecoveryAction.Retry,
					nextEligibleAttemptAt);
				state.Current = cooldownSnapshot;
				sharedTask = Task.FromResult(cooldownSnapshot);
			}
			else
			{
				completionSource = new TaskCompletionSource<UsageSnapshot>(
					TaskCreationOptions.RunContinuationsAsynchronously);
				refreshCancellationSource = new CancellationTokenSource();
				sharedTask = completionSource.Task;
				state.InFlight = sharedTask;
				state.InFlightCancellationSource = refreshCancellationSource;
				lastKnownGood = state.LastKnownGood;
				generation = state.Generation;
			}
		}

		try
		{
			InvalidateProvider(account.Id, replacedProvider);
		}
		finally
		{
			CancelRefresh(replacedCancellationSource);
		}

		if ((completionSource is not null) && (refreshCancellationSource is not null))
		{
			_ = CompleteRefreshAsync(
				account,
				provider,
				state,
				generation,
				lastKnownGood,
				completionSource,
				refreshCancellationSource);
		}

		return WaitForSharedRefreshAsync(sharedTask, cancellationToken);
	}

	public bool SeedLastKnownGood(UsageSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if ((snapshot.Account.Id == Guid.Empty) || !IsUsableUsageSnapshot(snapshot))
		{
			throw new ArgumentException(
				"快取快照必須屬於有效帳號，且包含可用的用量資料。",
				nameof(snapshot));
		}

		DateTimeOffset now = _timeProvider.GetUtcNow();
		UsageSnapshot? lastKnownGood = PrepareLastKnownGood(snapshot, now);

		if (lastKnownGood is null)
		{
			return false;
		}

		lock (_stateGate)
		{
			if (!_accountStates.TryGetValue(
				snapshot.Account.Id,
				out AccountRefreshState? state))
			{
				state = new AccountRefreshState(snapshot.Account.Provider);
				_accountStates.Add(snapshot.Account.Id, state);
			}
			else if ((state.Provider != snapshot.Account.Provider) ||
				(state.InFlight is not null))
			{
				return false;
			}

			PruneExpiredSnapshots(state, now);
			DateTimeOffset snapshotFreshness = GetSnapshotFreshness(lastKnownGood);

			if ((state.LastKnownGood is not null) &&
				(GetSnapshotFreshness(state.LastKnownGood) >= snapshotFreshness))
			{
				return false;
			}

			state.Current = lastKnownGood;
			state.LastKnownGood = lastKnownGood;
			state.GenericRetryDelay = null;
			state.GenericRetryStartedTimestamp = null;
			state.LastAttemptTimestamp = null;
			state.ConsecutiveRetryableFailures = 0;
			state.ProviderRetryNotBefore = null;
			return true;
		}
	}

	public void Invalidate(Guid accountId)
	{
		CancellationTokenSource? cancellationSource = null;
		ProviderKind? provider = null;

		lock (_stateGate)
		{
			if (_accountStates.Remove(accountId, out AccountRefreshState? state))
			{
				state.Generation++;
				cancellationSource = state.InFlightCancellationSource;
				provider = state.Provider;
			}
		}

		try
		{
			InvalidateProvider(accountId, provider);
		}
		finally
		{
			CancelRefresh(cancellationSource);
		}
	}

	private void InvalidateProvider(Guid accountId, ProviderKind? provider)
	{
		if ((provider is not null) &&
			(_providerRegistry.GetRequired(provider.Value) is
				IUsageProviderInvalidator invalidator))
		{
			invalidator.Invalidate(accountId);
		}
	}

	private static void CancelRefresh(CancellationTokenSource? cancellationSource)
	{
		if (cancellationSource is null)
		{
			return;
		}

		try
		{
			cancellationSource.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The refresh completed while invalidation was taking its state snapshot.
		}
	}

	private bool CanUseCachedSnapshot(
		AccountRefreshState state,
		IUsageProvider provider,
		DateTimeOffset now,
		long nowTimestamp)
	{
		return (state.Current is not null) &&
			(GetRemainingCooldownCore(
				state,
				provider,
				now,
				nowTimestamp) is not null);
	}

	private TimeSpan? GetRemainingCooldownCore(
		AccountRefreshState state,
		IUsageProvider provider,
		DateTimeOffset now,
		long nowTimestamp)
	{
		TimeSpan? remainingCooldown = null;

		if (state.ProviderRetryNotBefore is DateTimeOffset providerRetryNotBefore)
		{
			remainingCooldown = SelectLongerCooldown(
				remainingCooldown,
				providerRetryNotBefore - now);
		}

		if ((state.LastAttemptTimestamp is long lastAttemptTimestamp) &&
			(provider.MinimumRefreshInterval > TimeSpan.Zero))
		{
			remainingCooldown = SelectLongerCooldown(
				remainingCooldown,
				GetRemainingMonotonicDelay(
					lastAttemptTimestamp,
					provider.MinimumRefreshInterval,
					nowTimestamp));
		}

		if ((state.GenericRetryStartedTimestamp is long retryStartedTimestamp) &&
			(state.GenericRetryDelay is TimeSpan retryDelay))
		{
			remainingCooldown = SelectLongerCooldown(
				remainingCooldown,
				GetRemainingMonotonicDelay(
					retryStartedTimestamp,
					retryDelay,
					nowTimestamp));
		}

		return remainingCooldown;
	}

	private TimeSpan GetRemainingMonotonicDelay(
		long startedTimestamp,
		TimeSpan delay,
		long nowTimestamp)
	{
		TimeSpan elapsed = _timeProvider.GetElapsedTime(
			startedTimestamp,
			nowTimestamp);

		if (elapsed <= TimeSpan.Zero)
		{
			return delay;
		}

		return elapsed >= delay
			? TimeSpan.Zero
			: delay - elapsed;
	}

	private static TimeSpan? SelectLongerCooldown(
		TimeSpan? current,
		TimeSpan candidate)
	{
		if (candidate <= TimeSpan.Zero)
		{
			return current;
		}

		return (current is null) || (candidate > current.Value)
			? candidate
			: current;
	}

	private static DateTimeOffset AddCooldown(
		DateTimeOffset now,
		TimeSpan cooldown)
	{
		return cooldown > (DateTimeOffset.MaxValue - now)
			? DateTimeOffset.MaxValue
			: now + cooldown;
	}

	private static UsageSnapshot CreateFailureSnapshot(
		AccountProfile account,
		UsageSnapshot? lastKnownGood,
		DateTimeOffset failedAt,
		string error,
		UsageRecoveryAction recoveryAction,
		DateTimeOffset? retryNotBefore = null,
		UsageSnapshot? providerFailure = null)
	{
		bool hasObservedSubscriptionFailure =
			(providerFailure is not null) &&
			(providerFailure.SubscriptionVerificationState !=
				SubscriptionVerificationState.Unverified);

		if ((providerFailure?.SubscriptionVerificationState ==
				SubscriptionVerificationState.UsageUnavailable) &&
			!HasSameProviderBinding(lastKnownGood, providerFailure))
		{
			return providerFailure with { Account = account };
		}

		UsageSnapshot? usableLastKnownGood = PrepareLastKnownGood(
			lastKnownGood,
			failedAt);

		if ((usableLastKnownGood is not null) &&
			!IsTerminalIdentityRecovery(recoveryAction))
		{
			UsageSnapshot failureSnapshot = usableLastKnownGood with
			{
				Account = account,
				Status = SnapshotStatus.Stale,
				Error = error,
				RecoveryAction = recoveryAction,
				RetryNotBefore = retryNotBefore
			};

			return hasObservedSubscriptionFailure
				? failureSnapshot with
				{
					ProviderAccountIdentity =
						providerFailure!.ProviderAccountIdentity ??
							failureSnapshot.ProviderAccountIdentity,
					ProviderAccountDisplayIdentity =
						providerFailure.ProviderAccountDisplayIdentity ??
							failureSnapshot.ProviderAccountDisplayIdentity,
					SubscriptionScopeDisplayName =
						providerFailure.SubscriptionScopeDisplayName ??
							failureSnapshot.SubscriptionScopeDisplayName,
					PlanTier = providerFailure.PlanTier ??
						failureSnapshot.PlanTier,
					SubscriptionVerificationState =
						providerFailure.SubscriptionVerificationState
				}
				: failureSnapshot;
		}

		if (hasObservedSubscriptionFailure)
		{
			return providerFailure! with { Account = account };
		}

		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			failedAt,
			Error: error,
			RecoveryAction: recoveryAction,
			RetryNotBefore: retryNotBefore);
	}

	private static bool HasSameProviderBinding(
		UsageSnapshot? lastKnownGood,
		UsageSnapshot providerFailure)
	{
		return (lastKnownGood is not null) &&
			((lastKnownGood.SubscriptionVerificationState ==
					SubscriptionVerificationState.Verified) ||
				(lastKnownGood.SubscriptionVerificationState ==
					SubscriptionVerificationState.TransientProbeError) ||
				(lastKnownGood.SubscriptionVerificationState ==
					SubscriptionVerificationState.UsageUnavailable)) &&
			!string.IsNullOrWhiteSpace(lastKnownGood.ProviderAccountIdentity) &&
			!string.IsNullOrWhiteSpace(providerFailure.ProviderAccountIdentity) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				lastKnownGood.ProviderAccountIdentity,
				providerFailure.ProviderAccountIdentity);
	}

	private static bool IsTerminalIdentityRecovery(
		UsageRecoveryAction recoveryAction)
	{
		return (recoveryAction == UsageRecoveryAction.ConnectAccount) ||
			(recoveryAction == UsageRecoveryAction.SwitchAccount) ||
			(recoveryAction == UsageRecoveryAction.ConfirmSubscription);
	}

	private static bool IsUsableUsageSnapshot(UsageSnapshot snapshot)
	{
		return ((snapshot.Status == SnapshotStatus.Ready) ||
				(snapshot.Status == SnapshotStatus.Stale)) &&
			(snapshot.Metrics is not null) &&
			(snapshot.Metrics.Count > 0) &&
			Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) &&
			(snapshot.SourceTrust != SourceTrust.Unavailable);
	}

	private static bool IsRetryableFailure(UsageSnapshot snapshot)
	{
		return (snapshot.RecoveryAction == UsageRecoveryAction.Retry) &&
			((snapshot.Status == SnapshotStatus.Error) ||
				(snapshot.Status == SnapshotStatus.Stale));
	}

	private static TimeSpan GetRetryBackoff(int consecutiveFailures)
	{
		int backoffMinutes = consecutiveFailures switch
		{
			<= 1 => 1,
			2 => 2,
			3 => 4,
			4 => 8,
			_ => 15
		};
		return TimeSpan.FromMinutes(backoffMinutes);
	}

	private static DateTimeOffset GetSnapshotFreshness(UsageSnapshot snapshot)
	{
		return snapshot.ObservedAt ?? snapshot.FetchedAt;
	}

	private static UsageSnapshot NormalizeCachedSnapshot(
		UsageSnapshot snapshot,
		AccountProfile account,
		DateTimeOffset now)
	{
		snapshot = FilterExpiredMetrics(snapshot, now);
		SnapshotStatus status = snapshot.IsStaleAt(now)
			? SnapshotStatus.Stale
			: snapshot.Status;

		return snapshot with
		{
			Account = account,
			Status = status
		};
	}

	private static UsageSnapshot NormalizeProviderSnapshot(
		UsageSnapshot snapshot,
		AccountProfile account,
		DateTimeOffset now)
	{
		if ((snapshot.Account.Id != account.Id) ||
			(snapshot.Account.Provider != account.Provider))
		{
			throw new InvalidOperationException(
				"服務回傳的帳號與目前帳號不同。");
		}

		if (snapshot.Status == SnapshotStatus.Refreshing)
		{
			throw new InvalidOperationException(
				"服務仍在檢查用量，無法當作完成結果。");
		}

		bool wasUsableUsageSnapshot = IsUsableUsageSnapshot(snapshot);

		if (!Enum.IsDefined(typeof(SnapshotStatus), snapshot.Status) ||
			!Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) ||
			(snapshot.Metrics is null) ||
			((snapshot.RetryNotBefore is not null) &&
				((snapshot.RetryNotBefore.Value == default) ||
				(snapshot.RecoveryAction != UsageRecoveryAction.Retry))) ||
			(((snapshot.Status == SnapshotStatus.Ready) ||
				(snapshot.Status == SnapshotStatus.Stale)) &&
				!wasUsableUsageSnapshot))
		{
			throw new InvalidOperationException(
				"服務回傳的用量資料不完整或格式不符。");
		}

		snapshot = FilterExpiredMetrics(snapshot, now);

		if (wasUsableUsageSnapshot && (snapshot.Metrics.Count == 0))
		{
			return CreateFailureSnapshot(
				account,
				lastKnownGood: null,
				now,
				ResetBoundaryCooldownMessage,
				UsageRecoveryAction.Retry);
		}

		return NormalizeCachedSnapshot(snapshot, account, now);
	}

	private static UsageSnapshot FilterExpiredMetrics(
		UsageSnapshot snapshot,
		DateTimeOffset now)
	{
		if ((snapshot.Metrics is null) || (snapshot.Metrics.Count == 0))
		{
			return snapshot;
		}

		UsageMetric[] currentMetrics = snapshot.Metrics
			.Where(metric =>
				(metric.ResetsAt is null) || (metric.ResetsAt.Value > now))
			.ToArray();

		return currentMetrics.Length == snapshot.Metrics.Count
			? snapshot
			: snapshot with { Metrics = currentMetrics };
	}

	private static UsageSnapshot? PrepareCachedSnapshot(
		UsageSnapshot? snapshot,
		DateTimeOffset now)
	{
		if (snapshot is null)
		{
			return null;
		}

		UsageSnapshot currentSnapshot = FilterExpiredMetrics(snapshot, now);

		if (((currentSnapshot.Status == SnapshotStatus.Ready) ||
			(currentSnapshot.Status == SnapshotStatus.Stale)) &&
			(!IsUsableUsageSnapshot(currentSnapshot) ||
				IsTerminalIdentityRecovery(currentSnapshot.RecoveryAction)))
		{
			return null;
		}

		return currentSnapshot;
	}

	private static UsageSnapshot? PrepareLastKnownGood(
		UsageSnapshot? snapshot,
		DateTimeOffset now)
	{
		if ((snapshot is null) ||
			!IsUsableUsageSnapshot(snapshot) ||
			IsTerminalIdentityRecovery(snapshot.RecoveryAction))
		{
			return null;
		}

		UsageSnapshot currentSnapshot = FilterExpiredMetrics(snapshot, now);
		return IsUsableUsageSnapshot(currentSnapshot)
			? currentSnapshot
			: null;
	}

	private static void PruneExpiredSnapshots(
		AccountRefreshState state,
		DateTimeOffset now)
	{
		state.Current = PrepareCachedSnapshot(state.Current, now);
		state.LastKnownGood = PrepareLastKnownGood(state.LastKnownGood, now);
	}

	private static async Task<UsageSnapshot> WaitForSharedRefreshAsync(
		Task<UsageSnapshot> sharedTask,
		CancellationToken cancellationToken)
	{
		return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task CompleteRefreshAsync(
		AccountProfile account,
		IUsageProvider provider,
		AccountRefreshState state,
		long generation,
		UsageSnapshot? lastKnownGood,
		TaskCompletionSource<UsageSnapshot> completionSource,
		CancellationTokenSource refreshCancellationSource)
	{
		bool hasConcurrencyLease = false;

		try
		{
			await _refreshConcurrencyGate.WaitAsync(
				refreshCancellationSource.Token).ConfigureAwait(false);
			hasConcurrencyLease = true;
			RecordAttemptStartIfCurrent(
				account.Id,
				state,
				generation,
				completionSource.Task);
			UsageSnapshot providerSnapshot = await provider.GetUsageAsync(
				account,
				refreshCancellationSource.Token).ConfigureAwait(false);
			UsageSnapshot snapshot = NormalizeProviderSnapshot(
				providerSnapshot ?? throw new InvalidOperationException(
					"服務沒有回傳用量資料。"),
				account,
				_timeProvider.GetUtcNow());

			if (snapshot.Status == SnapshotStatus.Error)
			{
				snapshot = CreateFailureSnapshot(
					account,
					lastKnownGood,
					_timeProvider.GetUtcNow(),
					string.IsNullOrWhiteSpace(snapshot.Error)
						? GenericErrorMessage
						: snapshot.Error,
					snapshot.RecoveryAction,
					snapshot.RetryNotBefore,
					snapshot);
			}

			PublishCompletedSnapshot(
				account.Id,
				state,
				generation,
				completionSource.Task,
				snapshot);
			completionSource.TrySetResult(snapshot);
		}
		catch (OperationCanceledException)
		{
			if (refreshCancellationSource.IsCancellationRequested)
			{
				ClearInFlight(
					account.Id,
					state,
					generation,
					completionSource.Task);

				DateTimeOffset stoppedAt = _timeProvider.GetUtcNow();
				UsageSnapshot? usableLastKnownGood = PrepareLastKnownGood(
					lastKnownGood,
					stoppedAt);
				UsageSnapshot stoppedSnapshot = usableLastKnownGood is not null
					? CreateFailureSnapshot(
						account,
						usableLastKnownGood,
						stoppedAt,
						RefreshStoppedMessage,
						UsageRecoveryAction.None)
					: new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.NotConfigured,
						stoppedAt,
						Error: RefreshStoppedMessage);
				completionSource.TrySetResult(stoppedSnapshot);
			}
			else
			{
				UsageSnapshot failureSnapshot = CreateFailureSnapshot(
					account,
					lastKnownGood,
					_timeProvider.GetUtcNow(),
					GenericErrorMessage,
					UsageRecoveryAction.Retry);
				PublishCompletedSnapshot(
					account.Id,
					state,
					generation,
					completionSource.Task,
					failureSnapshot);
				completionSource.TrySetResult(failureSnapshot);
			}
		}
		catch (Exception exception)
		{
			TryReportUnexpectedException(account.Provider, exception);
			UsageSnapshot failureSnapshot = CreateFailureSnapshot(
				account,
				lastKnownGood,
				_timeProvider.GetUtcNow(),
				GenericErrorMessage,
				UsageRecoveryAction.Retry);
			PublishCompletedSnapshot(
				account.Id,
				state,
				generation,
				completionSource.Task,
				failureSnapshot);
			completionSource.TrySetResult(failureSnapshot);
		}
		finally
		{
			if (hasConcurrencyLease)
			{
				_refreshConcurrencyGate.Release();
			}

			refreshCancellationSource.Dispose();
		}
	}

	private void RecordAttemptStartIfCurrent(
		Guid accountId,
		AccountRefreshState state,
		long generation,
		Task<UsageSnapshot> sharedTask)
	{
		lock (_stateGate)
		{
			if (_accountStates.TryGetValue(accountId, out AccountRefreshState? currentState) &&
				ReferenceEquals(currentState, state) &&
				(currentState.Generation == generation) &&
				ReferenceEquals(currentState.InFlight, sharedTask))
			{
				currentState.LastAttemptTimestamp = _timeProvider.GetTimestamp();
			}
		}
	}

	private void TryReportUnexpectedException(
		ProviderKind provider,
		Exception exception)
	{
		try
		{
			_reportUnexpectedException?.Invoke(provider, exception);
		}
		catch
		{
			// Diagnostics must never alter refresh behavior.
		}
	}

	private void ClearInFlight(
		Guid accountId,
		AccountRefreshState state,
		long generation,
		Task<UsageSnapshot> sharedTask)
	{
		lock (_stateGate)
		{
			if (_accountStates.TryGetValue(accountId, out AccountRefreshState? currentState) &&
				ReferenceEquals(currentState, state) &&
				(currentState.Generation == generation) &&
				ReferenceEquals(currentState.InFlight, sharedTask))
			{
				currentState.InFlight = null;
				currentState.InFlightCancellationSource = null;
			}
		}
	}

	private void PublishCompletedSnapshot(
		Guid accountId,
		AccountRefreshState state,
		long generation,
		Task<UsageSnapshot> sharedTask,
		UsageSnapshot snapshot)
	{
		lock (_stateGate)
		{
			if (!_accountStates.TryGetValue(accountId, out AccountRefreshState? currentState) ||
				!ReferenceEquals(currentState, state) ||
				(currentState.Generation != generation) ||
				!ReferenceEquals(currentState.InFlight, sharedTask))
			{
				return;
			}

			currentState.Current = snapshot;

			if (IsRetryableFailure(snapshot))
			{
				if (snapshot.RetryNotBefore is not null)
				{
					currentState.ConsecutiveRetryableFailures = 0;
					currentState.GenericRetryDelay = null;
					currentState.GenericRetryStartedTimestamp = null;
					currentState.ProviderRetryNotBefore = snapshot.RetryNotBefore;
				}
				else
				{
					currentState.ConsecutiveRetryableFailures = Math.Min(
						currentState.ConsecutiveRetryableFailures + 1,
						MaxRetryBackoffStep);
					currentState.GenericRetryDelay =
						GetRetryBackoff(currentState.ConsecutiveRetryableFailures);
					currentState.GenericRetryStartedTimestamp =
						_timeProvider.GetTimestamp();
					currentState.ProviderRetryNotBefore = null;
				}
			}
			else
			{
				currentState.ConsecutiveRetryableFailures = 0;
				currentState.GenericRetryDelay = null;
				currentState.GenericRetryStartedTimestamp = null;
				currentState.ProviderRetryNotBefore = null;
			}

			UsageSnapshot? lastKnownGood = PrepareLastKnownGood(
				snapshot,
				_timeProvider.GetUtcNow());

			if (lastKnownGood is not null)
			{
				currentState.LastKnownGood = lastKnownGood;
			}
			else if ((snapshot.SubscriptionVerificationState ==
					SubscriptionVerificationState.UsageUnavailable) ||
				IsTerminalIdentityRecovery(snapshot.RecoveryAction) ||
				(snapshot.Status == SnapshotStatus.Ready) ||
				(snapshot.Status == SnapshotStatus.NotConfigured) ||
				(snapshot.Status == SnapshotStatus.Unsupported))
			{
				currentState.LastKnownGood = null;
			}

			currentState.InFlight = null;
			currentState.InFlightCancellationSource = null;
		}
	}
}
