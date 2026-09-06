using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.Tests;

public sealed class UsageRefreshCoordinatorTests
{
	private sealed class FakeTimeProvider : TimeProvider
	{
		private long _timestamp;
		private DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		public override long GetTimestamp()
		{
			return _timestamp;
		}

		internal void Advance(TimeSpan duration)
		{
			_utcNow += duration;
			_timestamp += duration.Ticks;
		}

		internal void RewindUtc(TimeSpan duration)
		{
			_utcNow -= duration;
		}
	}

	private sealed class FakeUsageProvider : IUsageProvider, IUsageProviderInvalidator
	{
		private readonly Func<AccountProfile, int, CancellationToken, Task<UsageSnapshot>> _handler;
		private int _callCount;

		public ProviderKind Provider { get; }

		public TimeSpan MinimumRefreshInterval { get; }

		internal int CallCount => Volatile.Read(ref _callCount);

		internal List<Guid> InvalidatedAccountIds { get; } = new();

		internal FakeUsageProvider(
			ProviderKind provider,
			TimeSpan minimumRefreshInterval,
			Func<AccountProfile, int, CancellationToken, Task<UsageSnapshot>> handler)
		{
			Provider = provider;
			MinimumRefreshInterval = minimumRefreshInterval;
			_handler = handler;
		}

		public Task<UsageSnapshot> GetUsageAsync(
			AccountProfile account,
			CancellationToken cancellationToken)
		{
			int callNumber = Interlocked.Increment(ref _callCount);
			return _handler(account, callNumber, cancellationToken);
		}

		public void Invalidate(Guid accountId)
		{
			InvalidatedAccountIds.Add(accountId);
		}
	}

	[Fact]
	public async Task RefreshAsync_WhenSameAccountIsConcurrent_CallsProviderOnce()
	{
		DateTimeOffset now = new(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		TaskCompletionSource<UsageSnapshot> providerResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(_, _, _) => providerResult.Task);
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		Task<UsageSnapshot> firstRefresh = coordinator.RefreshAsync(account);
		Task<UsageSnapshot> secondRefresh = coordinator.RefreshAsync(account);

		Assert.Equal(1, provider.CallCount);
		providerResult.SetResult(CreateReadySnapshot(account, now, "first"));
		UsageSnapshot[] snapshots = await Task.WhenAll(firstRefresh, secondRefresh);

		Assert.Equal(1, provider.CallCount);
		Assert.All(
			snapshots,
			snapshot => Assert.Equal("first", snapshot.Metrics[0].DisplayValue));
	}

	[Fact]
	public async Task RefreshAsync_WhenManyAccountsAreConcurrent_LimitsProviderCalls()
	{
		DateTimeOffset now = new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		TaskCompletionSource<bool> fourCallsStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseCalls = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		object activeCallsGate = new();
		int activeCalls = 0;
		int peakActiveCalls = 0;
		FakeUsageProvider provider = new(
			ProviderKind.Claude,
			TimeSpan.Zero,
			async (account, _, _) =>
			{
				int currentActiveCalls = Interlocked.Increment(ref activeCalls);

				lock (activeCallsGate)
				{
					peakActiveCalls = Math.Max(peakActiveCalls, currentActiveCalls);
				}

				if (currentActiveCalls == 4)
				{
					fourCallsStarted.TrySetResult(true);
				}

				await releaseCalls.Task;
				Interlocked.Decrement(ref activeCalls);
				return CreateReadySnapshot(account, now, "usage");
			});
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			maximumConcurrentRefreshes: 4);
		AccountProfile[] accounts = Enumerable.Range(0, 20)
			.Select(index => new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				$"Claude {index + 1}"))
			.ToArray();

		Task<UsageSnapshot>[] refreshes = accounts
			.Select(account => coordinator.RefreshAsync(account))
			.ToArray();
		await fourCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(4, provider.CallCount);
		releaseCalls.SetResult(true);
		await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(20, provider.CallCount);
		Assert.Equal(4, peakActiveCalls);
	}

	[Fact]
	public async Task RefreshAsync_WhenQueuedPastMinimumInterval_StartsCooldownAtProviderCall()
	{
		DateTimeOffset now = new(2026, 8, 1, 1, 15, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile activeAccount = CreateAccount();
		AccountProfile queuedAccount = CreateAccount();
		TaskCompletionSource<bool> activeCallStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseActiveCall = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageProvider provider = new(
			ProviderKind.Claude,
			TimeSpan.FromMinutes(1),
			async (account, callNumber, cancellationToken) =>
			{
				if (account.Id == activeAccount.Id)
				{
					activeCallStarted.TrySetResult(true);
					await releaseActiveCall.Task.WaitAsync(cancellationToken);
				}

				return CreateReadySnapshot(
					account,
					timeProvider.GetUtcNow(),
					$"call-{callNumber}");
			});
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			maximumConcurrentRefreshes: 1);

		Task<UsageSnapshot> activeRefresh = coordinator.RefreshAsync(activeAccount);
		await activeCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<UsageSnapshot> queuedRefresh = coordinator.RefreshAsync(queuedAccount);

		Assert.Equal(1, provider.CallCount);
		timeProvider.Advance(TimeSpan.FromMinutes(2));
		releaseActiveCall.SetResult(true);
		UsageSnapshot[] completedSnapshots = await Task.WhenAll(
			activeRefresh,
			queuedRefresh).WaitAsync(TimeSpan.FromSeconds(5));

		UsageSnapshot cachedSnapshot = await coordinator.RefreshAsync(queuedAccount);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal("call-2", completedSnapshots[1].Metrics[0].DisplayValue);
		Assert.Equal(
			completedSnapshots[1].Metrics[0].DisplayValue,
			cachedSnapshot.Metrics[0].DisplayValue);
		Assert.Equal(completedSnapshots[1].FetchedAt, cachedSnapshot.FetchedAt);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(queuedAccount));
	}

	[Fact]
	public async Task Invalidate_WhenRefreshIsWaitingForConcurrency_DoesNotCallProvider()
	{
		DateTimeOffset now = new(2026, 8, 1, 1, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile activeAccount = CreateAccount();
		AccountProfile queuedAccount = CreateAccount();
		TaskCompletionSource<bool> activeCallStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseActiveCall = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageProvider provider = new(
			ProviderKind.Claude,
			TimeSpan.Zero,
			async (account, _, cancellationToken) =>
			{
				if (account.Id == activeAccount.Id)
				{
					activeCallStarted.TrySetResult(true);
					await releaseActiveCall.Task.WaitAsync(cancellationToken);
				}

				return CreateReadySnapshot(account, now, "usage");
			});
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			maximumConcurrentRefreshes: 1);

		Task<UsageSnapshot> activeRefresh = coordinator.RefreshAsync(activeAccount);
		await activeCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<UsageSnapshot> queuedRefresh = coordinator.RefreshAsync(queuedAccount);

		Assert.Equal(1, provider.CallCount);
		coordinator.Invalidate(queuedAccount.Id);
		UsageSnapshot stopped = await queuedRefresh.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, provider.CallCount);
		Assert.Equal(SnapshotStatus.NotConfigured, stopped.Status);
		Assert.Equal("用量檢查已停止。", stopped.Error);
		Assert.Contains(queuedAccount.Id, provider.InvalidatedAccountIds);

		releaseActiveCall.SetResult(true);
		await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderThrows_ReportsProviderAndExceptionToDiagnosticSink()
	{
		DateTimeOffset now = new(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		InvalidOperationException failure = new("private provider output");
		ProviderKind? reportedProvider = null;
		Exception? reportedException = null;
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(_, _, _) => Task.FromException<UsageSnapshot>(failure));
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			reportUnexpectedException: (providerKind, exception) =>
			{
				reportedProvider = providerKind;
				reportedException = exception;
			});

		UsageSnapshot snapshot = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(account.Provider, reportedProvider);
		Assert.Same(failure, reportedException);
	}

	[Fact]
	public async Task RefreshAsync_WithinMinimumInterval_ReturnsCachedSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => Task.FromResult(CreateReadySnapshot(
				providerAccount,
				timeProvider.GetUtcNow(),
				$"call-{callNumber}")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot first = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(30));
		UsageSnapshot cached = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(31));
		UsageSnapshot refreshed = await coordinator.RefreshAsync(account);

		Assert.Equal("call-1", first.Metrics[0].DisplayValue);
		Assert.Equal("call-1", cached.Metrics[0].DisplayValue);
		Assert.Equal("call-2", refreshed.Metrics[0].DisplayValue);
		Assert.Equal(2, provider.CallCount);
	}

	[Fact]
	public async Task RefreshAsync_WhenCachedMetricReachesResetDuringMinimumInterval_ReturnsEmptyRetryUntilEligible()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 15, 0, TimeSpan.Zero);
		DateTimeOffset resetsAt = now + TimeSpan.FromMinutes(1);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(5),
			(providerAccount, callNumber, _) => Task.FromResult(new UsageSnapshot(
				providerAccount,
				new[]
				{
					new UsageMetric(
						"usage",
						"Usage",
						42,
						$"call-{callNumber}",
						callNumber == 1 ? resetsAt : resetsAt + TimeSpan.FromDays(7))
				},
				SourceTrust.Official,
				SnapshotStatus.Ready,
				timeProvider.GetUtcNow())));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot first = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot coolingDown = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(4));
		UsageSnapshot refreshed = await coordinator.RefreshAsync(account);

		Assert.Equal("call-1", Assert.Single(first.Metrics).DisplayValue);
		Assert.Equal(SnapshotStatus.Error, coolingDown.Status);
		Assert.Empty(coolingDown.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, coolingDown.RecoveryAction);
		Assert.Equal(now + TimeSpan.FromMinutes(5), coolingDown.RetryNotBefore);
		Assert.Equal("call-2", Assert.Single(refreshed.Metrics).DisplayValue);
		Assert.Equal(2, provider.CallCount);
	}

	[Fact]
	public async Task GetRemainingCooldown_AfterRefresh_TracksProviderMinimumInterval()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(2),
			(providerAccount, _, _) => Task.FromResult(CreateReadySnapshot(
				providerAccount,
				timeProvider.GetUtcNow(),
				"usage")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		Assert.Null(coordinator.GetRemainingCooldown(account));

		await coordinator.RefreshAsync(account);

		Assert.Equal(
			TimeSpan.FromMinutes(2),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromSeconds(75));

		Assert.Equal(
			TimeSpan.FromSeconds(45),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromSeconds(45));

		Assert.Null(coordinator.GetRemainingCooldown(account));
	}

	[Fact]
	public async Task GetRemainingCooldown_WhenUtcClockMovesBackward_DoesNotExtendMinimumInterval()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 40, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => Task.FromResult(CreateReadySnapshot(
				providerAccount,
				timeProvider.GetUtcNow(),
				$"call-{callNumber}")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.RewindUtc(TimeSpan.FromHours(1));

		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot refreshed = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal("call-2", Assert.Single(refreshed.Metrics).DisplayValue);
	}

	[Fact]
	public async Task RefreshAsync_WhenRetryableFailuresRepeat_AppliesBoundedExponentialBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 45, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromSeconds(10),
			(providerAccount, _, _) => Task.FromResult(
				CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow())));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		int[] expectedBackoffMinutes = new[] { 1, 2, 4, 8, 15, 15 };

		for (int index = 0; index < expectedBackoffMinutes.Length; index++)
		{
			UsageSnapshot failed = await coordinator.RefreshAsync(account);
			int expectedCallCount = index + 1;
			TimeSpan expectedBackoff =
				TimeSpan.FromMinutes(expectedBackoffMinutes[index]);

			Assert.Equal(expectedCallCount, provider.CallCount);
			Assert.Equal(SnapshotStatus.Error, failed.Status);
			Assert.Equal(expectedBackoff, coordinator.GetRemainingCooldown(account));

			timeProvider.Advance(expectedBackoff - TimeSpan.FromSeconds(1));
			UsageSnapshot cached = await coordinator.RefreshAsync(account);

			Assert.Equal(expectedCallCount, provider.CallCount);
			Assert.Equal(failed, cached);

			timeProvider.Advance(TimeSpan.FromSeconds(1));
		}
	}

	[Fact]
	public async Task RefreshAsync_WhenUtcClockMovesBackward_DoesNotExtendGenericBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 47, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => Task.FromResult(callNumber == 1
				? CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow())
				: CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"recovered")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.RewindUtc(TimeSpan.FromHours(1));

		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot recovered = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(SnapshotStatus.Ready, recovered.Status);
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderSuppliesRetryDeadline_DoesNotCompoundGenericBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 48, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromSeconds(10),
			(providerAccount, callNumber, _) => Task.FromResult(callNumber switch
			{
				1 => CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					timeProvider.GetUtcNow() + TimeSpan.FromMinutes(1)),
				2 => CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow()),
				3 => CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2)),
				_ => CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"recovered")
			}));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot firstFailure = await coordinator.RefreshAsync(account);

		Assert.Equal(now + TimeSpan.FromMinutes(1), firstFailure.RetryNotBefore);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot preflightFailure = await coordinator.RefreshAsync(account);

		Assert.Null(preflightFailure.RetryNotBefore);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot secondSafetyFailure = await coordinator.RefreshAsync(account);

		Assert.Equal(
			now + TimeSpan.FromMinutes(4),
			secondSafetyFailure.RetryNotBefore);
		Assert.Equal(
			TimeSpan.FromMinutes(2),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(2));
		UsageSnapshot recovered = await coordinator.RefreshAsync(account);

		Assert.Equal(4, provider.CallCount);
		Assert.Equal(SnapshotStatus.Ready, recovered.Status);
	}

	[Fact]
	public async Task RefreshAsync_WhenReadyAfterRetryableFailure_ResetsBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 50, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromSeconds(10),
			(providerAccount, callNumber, _) => Task.FromResult(callNumber switch
			{
				2 => CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"recovered"),
				_ => CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow())
			}));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot recovered = await coordinator.RefreshAsync(account);
		Assert.Equal(SnapshotStatus.Ready, recovered.Status);
		Assert.Equal(
			TimeSpan.FromSeconds(10),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromSeconds(10));
		UsageSnapshot failedAgain = await coordinator.RefreshAsync(account);

		Assert.Equal(3, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, failedAgain.Status);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));
	}

	[Fact]
	public async Task RefreshAsync_WhenRetryableFailureUsesLastKnownGood_PreservesIdentityDuringBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 55, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromSeconds(10),
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"last-good") with
				{
					ProviderAccountIdentity = "stable-account"
				})
				: Task.FromResult(CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow())));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(10));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(30));
		UsageSnapshot cached = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal("last-good", Assert.Single(stale.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal("stable-account", stale.ProviderAccountIdentity);
		Assert.Equal(stale, cached);
		Assert.Equal(
			TimeSpan.FromSeconds(30),
			coordinator.GetRemainingCooldown(account));
	}

	[Fact]
	public async Task RefreshAsync_WhenTransientProbeErrorHasLastKnownGood_PreservesMetricsWithFailureContext()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"last-good") with
				{
					ProviderAccountIdentity = "verified-fingerprint",
					ProviderAccountDisplayIdentity = "old@example.com",
					SubscriptionScopeDisplayName = "Old Organization",
					PlanTier = "max",
					SubscriptionVerificationState =
						SubscriptionVerificationState.Verified
				})
				: Task.FromResult(new UsageSnapshot(
					providerAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					timeProvider.GetUtcNow(),
					Error: "本次 subscription probe 暫時失敗。",
					ProviderAccountIdentity: "failure-fingerprint",
					RecoveryAction: UsageRecoveryAction.Retry,
					ProviderAccountDisplayIdentity: "current@example.com",
					SubscriptionScopeDisplayName: "Current Organization",
					PlanTier: "team",
					SubscriptionVerificationState:
						SubscriptionVerificationState.TransientProbeError)));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal("last-good", Assert.Single(stale.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal("本次 subscription probe 暫時失敗。", stale.Error);
		Assert.Equal("failure-fingerprint", stale.ProviderAccountIdentity);
		Assert.Equal("current@example.com", stale.ProviderAccountDisplayIdentity);
		Assert.Equal("Current Organization", stale.SubscriptionScopeDisplayName);
		Assert.Equal("team", stale.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			stale.SubscriptionVerificationState);
		Assert.NotEqual(
			SubscriptionVerificationState.Verified,
			stale.SubscriptionVerificationState);
	}

	[Fact]
	public async Task RefreshAsync_WhenTransientProbeErrorMetadataIsMissing_PreservesLastKnownGoodContext()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 5, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"last-good") with
				{
					ProviderAccountIdentity = "opaque-binding",
					ProviderAccountDisplayIdentity = "owner@example.com",
					SubscriptionScopeDisplayName = "Verified Organization",
					PlanTier = "max",
					SubscriptionVerificationState =
						SubscriptionVerificationState.Verified
				})
				: Task.FromResult(new UsageSnapshot(
					providerAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					timeProvider.GetUtcNow(),
					Error: "本次 subscription probe 暫時失敗。",
					RecoveryAction: UsageRecoveryAction.Retry,
					SubscriptionVerificationState:
						SubscriptionVerificationState.TransientProbeError)));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal("last-good", Assert.Single(stale.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal("opaque-binding", stale.ProviderAccountIdentity);
		Assert.Equal("owner@example.com", stale.ProviderAccountDisplayIdentity);
		Assert.Equal(
			"Verified Organization",
			stale.SubscriptionScopeDisplayName);
		Assert.Equal("max", stale.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			stale.SubscriptionVerificationState);
	}

	[Fact]
	public async Task RefreshAsync_WhenUsageUnavailableHasLastKnownGood_ClearsItBeforeTransientFailure()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 10, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => Task.FromResult(
				callNumber switch
				{
					1 => new UsageSnapshot(
						providerAccount,
						new[]
						{
							new UsageMetric(
								"old-plan-quota",
								"Old plan quota",
								42,
								"old quota")
						},
						SourceTrust.Official,
						SnapshotStatus.Ready,
						timeProvider.GetUtcNow(),
						ProviderAccountIdentity: "verified-fingerprint",
						ProviderAccountDisplayIdentity: "old@example.com",
						SubscriptionScopeDisplayName: "Old Organization",
						PlanTier: "max",
						SubscriptionVerificationState:
							SubscriptionVerificationState.Verified),
					2 => new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "Team plan usage 尚未驗證。",
						ProviderAccountIdentity: "current-fingerprint",
						RecoveryAction: UsageRecoveryAction.Retry,
						ProviderAccountDisplayIdentity: "current@example.com",
						SubscriptionScopeDisplayName: "Current Organization",
						PlanTier: "team",
						SubscriptionVerificationState:
							SubscriptionVerificationState.UsageUnavailable),
					_ => new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "subscription probe 暫時失敗。",
						ProviderAccountIdentity: "current-fingerprint",
						RecoveryAction: UsageRecoveryAction.Retry,
						ProviderAccountDisplayIdentity: "current@example.com",
						SubscriptionScopeDisplayName: "Current Organization",
						PlanTier: "team",
						SubscriptionVerificationState:
							SubscriptionVerificationState.TransientProbeError)
				}));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot unavailable = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot transientFailure = await coordinator.RefreshAsync(account);

		Assert.Equal(3, provider.CallCount);
		Assert.Equal(SnapshotStatus.Error, unavailable.Status);
		Assert.Equal(SourceTrust.Unavailable, unavailable.SourceTrust);
		Assert.Empty(unavailable.Metrics);
		Assert.Equal(now + TimeSpan.FromMinutes(1), unavailable.FetchedAt);
		Assert.Equal("Team plan usage 尚未驗證。", unavailable.Error);
		Assert.Equal("current-fingerprint", unavailable.ProviderAccountIdentity);
		Assert.Equal(
			"current@example.com",
			unavailable.ProviderAccountDisplayIdentity);
		Assert.Equal(
			"Current Organization",
			unavailable.SubscriptionScopeDisplayName);
		Assert.Equal("team", unavailable.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			unavailable.SubscriptionVerificationState);
		Assert.Equal(SnapshotStatus.Error, transientFailure.Status);
		Assert.Equal(SourceTrust.Unavailable, transientFailure.SourceTrust);
		Assert.Empty(transientFailure.Metrics);
		Assert.Equal("subscription probe 暫時失敗。", transientFailure.Error);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			transientFailure.SubscriptionVerificationState);
	}

	[Fact]
	public async Task RefreshAsync_WhenUsageUnavailableMatchesBinding_PreservesLastKnownGood()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 20, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => Task.FromResult(
				callNumber switch
				{
					1 => new UsageSnapshot(
						providerAccount,
						new[]
						{
							new UsageMetric(
								"historical-quota",
								"Historical quota",
								42,
								"confirmed history")
						},
						SourceTrust.Official,
						SnapshotStatus.Ready,
						timeProvider.GetUtcNow(),
						ProviderAccountIdentity: "opaque-binding",
						ProviderAccountDisplayIdentity: "owner@example.com",
						SubscriptionScopeDisplayName: "Verified Organization",
						PlanTier: "team",
						SubscriptionVerificationState:
							SubscriptionVerificationState.Verified),
					2 => new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "Team plan usage 尚未驗證。",
						ProviderAccountIdentity: "opaque-binding",
						RecoveryAction: UsageRecoveryAction.Retry,
						ProviderAccountDisplayIdentity: "owner@example.com",
						SubscriptionScopeDisplayName: "Verified Organization",
						PlanTier: "enterprise",
						SubscriptionVerificationState:
							SubscriptionVerificationState.UsageUnavailable),
					_ => new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "subscription probe 暫時失敗。",
						ProviderAccountIdentity: "opaque-binding",
						RecoveryAction: UsageRecoveryAction.Retry,
						SubscriptionVerificationState:
							SubscriptionVerificationState.TransientProbeError)
				}));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot unavailable = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot transientFailure = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Stale, unavailable.Status);
		Assert.Equal("confirmed history", Assert.Single(unavailable.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, unavailable.FetchedAt);
		Assert.Equal("Team plan usage 尚未驗證。", unavailable.Error);
		Assert.Equal("enterprise", unavailable.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			unavailable.SubscriptionVerificationState);
		Assert.Equal(SnapshotStatus.Stale, transientFailure.Status);
		Assert.Equal(
			"confirmed history",
			Assert.Single(transientFailure.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, transientFailure.FetchedAt);
		Assert.Equal("subscription probe 暫時失敗。", transientFailure.Error);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			transientFailure.SubscriptionVerificationState);
	}

	[Fact]
	public async Task RefreshAsync_WhenUsageUnavailableBindingDiffers_ClearsLastKnownGood()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => Task.FromResult(
				callNumber == 1
					? new UsageSnapshot(
						providerAccount,
						new[]
						{
							new UsageMetric(
								"old-quota",
								"Old quota",
								42,
								"old quota")
						},
						SourceTrust.Official,
						SnapshotStatus.Ready,
						timeProvider.GetUtcNow(),
						ProviderAccountIdentity: "opaque-binding",
						PlanTier: "max",
						SubscriptionVerificationState:
							SubscriptionVerificationState.Verified)
					: new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "usage shape 尚未驗證。",
						ProviderAccountIdentity: "other-binding",
						RecoveryAction: UsageRecoveryAction.Retry,
						PlanTier: "team",
						SubscriptionVerificationState:
							SubscriptionVerificationState.UsageUnavailable)));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot unavailable = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, unavailable.Status);
		Assert.Empty(unavailable.Metrics);
		Assert.Equal("other-binding", unavailable.ProviderAccountIdentity);
		Assert.Equal("team", unavailable.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			unavailable.SubscriptionVerificationState);
	}

	[Fact]
	public async Task Invalidate_AfterRetryableFailure_ClearsBackoff()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 58, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromSeconds(10),
			(providerAccount, _, _) => Task.FromResult(
				CreateRetryableFailureSnapshot(
					providerAccount,
					timeProvider.GetUtcNow())));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		coordinator.Invalidate(account.Id);
		await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderFailsAfterSuccess_ReturnsStaleLastKnownGood()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"last-good"))
				: Task.FromException<UsageSnapshot>(new InvalidOperationException(
					"secret-path-that-must-not-leak")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Ready, ready.Status);
		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal("last-good", stale.Metrics[0].DisplayValue);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			stale.Error);
		Assert.Equal(UsageRecoveryAction.Retry, stale.RecoveryAction);
		Assert.DoesNotContain("secret-path", stale.Error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.ConnectAccount)]
	[InlineData(UsageRecoveryAction.SwitchAccount)]
	[InlineData(UsageRecoveryAction.ConfirmSubscription)]
	public async Task RefreshAsync_WhenProviderReturnsTerminalIdentityRecovery_DoesNotApplyOrRetainLastKnownGood(
		UsageRecoveryAction recoveryAction)
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 10, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => callNumber switch
			{
				1 => Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"old-account")),
				2 => Task.FromResult(new UsageSnapshot(
					providerAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					timeProvider.GetUtcNow(),
					Error: "需要確認帳號。",
					ProviderAccountIdentity: "current-fingerprint",
					RecoveryAction: recoveryAction,
					ProviderAccountDisplayIdentity: "current@example.com",
					SubscriptionScopeDisplayName: "Current Organization",
					PlanTier: "pro",
					SubscriptionVerificationState:
						SubscriptionVerificationState.DefiniteScopeMismatch)),
				_ => Task.FromException<UsageSnapshot>(new IOException("private path"))
			});
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		UsageSnapshot terminal = await coordinator.RefreshAsync(account);
		UsageSnapshot laterFailure = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, terminal.Status);
		Assert.Empty(terminal.Metrics);
		Assert.Equal(recoveryAction, terminal.RecoveryAction);
		Assert.Equal("current-fingerprint", terminal.ProviderAccountIdentity);
		Assert.Equal(
			"current@example.com",
			terminal.ProviderAccountDisplayIdentity);
		Assert.Equal(
			"Current Organization",
			terminal.SubscriptionScopeDisplayName);
		Assert.Equal("pro", terminal.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			terminal.SubscriptionVerificationState);
		Assert.Equal(SnapshotStatus.Error, laterFailure.Status);
		Assert.Empty(laterFailure.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, laterFailure.RecoveryAction);
		Assert.Equal(3, provider.CallCount);
	}

	[Fact]
	public async Task RefreshAsync_WhenReadyMetricsExpireBeforeNormalization_ReturnsResetRetryWithoutUnexpectedDiagnostic()
	{
		DateTimeOffset now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
		DateTimeOffset resetsAt = now + TimeSpan.FromMinutes(1);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		Exception? reportedException = null;
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, _, _) =>
			{
				UsageSnapshot snapshot = new(
					providerAccount,
					new[]
					{
						new UsageMetric(
							"usage",
							"Usage",
							42,
							"old-period",
							resetsAt)
					},
					SourceTrust.Official,
					SnapshotStatus.Ready,
					timeProvider.GetUtcNow());
				timeProvider.Advance(TimeSpan.FromMinutes(1));
				return Task.FromResult(snapshot);
			});
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			reportUnexpectedException: (_, exception) =>
				reportedException = exception);

		UsageSnapshot result = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, result.Status);
		Assert.Empty(result.Metrics);
		Assert.Equal(SourceTrust.Unavailable, result.SourceTrust);
		Assert.Equal("用量已重置，AI Usage 稍後會自動再試。", result.Error);
		Assert.Equal(UsageRecoveryAction.Retry, result.RecoveryAction);
		Assert.Null(reportedException);
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderReturnsReadyWithoutMetrics_PreservesLastKnownGood()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 15, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		Exception? reportedException = null;
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => Task.FromResult(
				callNumber == 1
					? CreateReadySnapshot(
						providerAccount,
						timeProvider.GetUtcNow(),
						"last-good")
					: new UsageSnapshot(
						providerAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Official,
						SnapshotStatus.Ready,
						timeProvider.GetUtcNow())));
		UsageRefreshCoordinator coordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider,
			reportUnexpectedException: (_, exception) =>
				reportedException = exception);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Ready, ready.Status);
		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal("last-good", Assert.Single(stale.Metrics).DisplayValue);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			stale.Error);
		Assert.Equal(UsageRecoveryAction.Retry, stale.RecoveryAction);
		Assert.IsType<InvalidOperationException>(reportedException);
	}

	[Fact]
	public async Task RefreshAsync_WhenReadySnapshotUsesUnavailableTrust_RejectsSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 20, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, _, _) => Task.FromResult(
				CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"untrusted") with
				{
					SourceTrust = SourceTrust.Unavailable
				}));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot snapshot = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			snapshot.Error);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task SeedLastKnownGood_WithMixedResetMetrics_FiltersExpiredMetricsBeforeFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 25, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(_, _, _) => Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		UsageSnapshot seed = new(
			account,
			new[]
			{
				new UsageMetric("past", "Past", 10, "past", now - TimeSpan.FromTicks(1)),
				new UsageMetric("boundary", "Boundary", 20, "boundary", now),
				new UsageMetric("future", "Future", 30, "future", now + TimeSpan.FromMinutes(1)),
				new UsageMetric("no-reset", "No reset", 40, "no-reset")
			},
			SourceTrust.Official,
			SnapshotStatus.Stale,
			now - TimeSpan.FromMinutes(1));

		Assert.True(coordinator.SeedLastKnownGood(seed));
		UsageSnapshot fallback = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Stale, fallback.Status);
		Assert.Equal(new[] { "future", "no-reset" }, fallback.Metrics.Select(metric => metric.Key));
	}

	[Fact]
	public async Task SeedLastKnownGood_WhenAllMetricsHaveReset_ReturnsFalseAndDoesNotFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 27, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(_, _, _) => Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		UsageSnapshot seed = new(
			account,
			new[]
			{
				new UsageMetric("past", "Past", 10, "past", now - TimeSpan.FromTicks(1)),
				new UsageMetric("boundary", "Boundary", 20, "boundary", now)
			},
			SourceTrust.Official,
			SnapshotStatus.Stale,
			now - TimeSpan.FromMinutes(1));

		Assert.False(coordinator.SeedLastKnownGood(seed));
		UsageSnapshot failed = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, failed.Status);
		Assert.Empty(failed.Metrics);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.ConnectAccount)]
	[InlineData(UsageRecoveryAction.SwitchAccount)]
	[InlineData(UsageRecoveryAction.ConfirmSubscription)]
	public async Task SeedLastKnownGood_WithTerminalIdentityRecovery_ReturnsFalseAndDoesNotFallback(
		UsageRecoveryAction recoveryAction)
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 28, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(_, _, _) => Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		UsageSnapshot seed = CreateReadySnapshot(account, now, "wrong-account") with
		{
			Status = SnapshotStatus.Stale,
			RecoveryAction = recoveryAction
		};

		Assert.False(coordinator.SeedLastKnownGood(seed));
		UsageSnapshot failed = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, failed.Status);
		Assert.Empty(failed.Metrics);
	}

	[Fact]
	public async Task RefreshAsync_WhenSeededSnapshotExistsAndProviderReturnsError_ReturnsStaleSeed()
	{
		DateTimeOffset cachedAt = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		DateTimeOffset now = cachedAt + TimeSpan.FromHours(1);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, _, _) => Task.FromResult(new UsageSnapshot(
				providerAccount,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				now,
				Error: "Claude /usage polling 暫時失敗。",
				RecoveryAction: UsageRecoveryAction.RestartApplication)));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(
			account,
			cachedAt,
			"cached") with
		{
			Status = SnapshotStatus.Stale,
			ProviderAccountIdentity = "person@example.com"
		};
		coordinator.SeedLastKnownGood(cachedSnapshot);

		UsageSnapshot snapshot = await coordinator.RefreshAsync(account);

		Assert.Equal(1, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, snapshot.Status);
		Assert.Equal("cached", Assert.Single(snapshot.Metrics).DisplayValue);
		Assert.Equal(cachedAt, snapshot.FetchedAt);
		Assert.Equal("person@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal("Claude /usage polling 暫時失敗。", snapshot.Error);
		Assert.Equal(
			UsageRecoveryAction.RestartApplication,
			snapshot.RecoveryAction);
	}

	[Fact]
	public async Task RefreshAsync_WhenLastKnownGoodResetsDuringFailedRefresh_DoesNotReturnExpiredFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 3, 40, 0, TimeSpan.Zero);
		DateTimeOffset resetsAt = now + TimeSpan.FromMinutes(1);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		TaskCompletionSource<UsageSnapshot> failedRefresh = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.Zero,
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(new UsageSnapshot(
					providerAccount,
					new[]
					{
						new UsageMetric("usage", "Usage", 42, "old-period", resetsAt)
					},
					SourceTrust.Official,
					SnapshotStatus.Ready,
					timeProvider.GetUtcNow()))
				: failedRefresh.Task);
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(30));
		Task<UsageSnapshot> refresh = coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromSeconds(30));
		failedRefresh.SetException(new IOException("private path"));
		UsageSnapshot failed = await refresh;

		Assert.Equal(SnapshotStatus.Error, failed.Status);
		Assert.Empty(failed.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, failed.RecoveryAction);
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderReportsNotConfigured_ClearsSeededFallback()
	{
		DateTimeOffset cachedAt = new(2026, 7, 15, 3, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(cachedAt + TimeSpan.FromHours(1));
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(new UsageSnapshot(
					providerAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					timeProvider.GetUtcNow()))
				: Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(
			account,
			cachedAt,
			"old-account");
		Assert.True(coordinator.SeedLastKnownGood(cachedSnapshot));

		UsageSnapshot notConfigured = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot failed = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.NotConfigured, notConfigured.Status);
		Assert.Equal(SnapshotStatus.Error, failed.Status);
		Assert.Empty(failed.Metrics);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			failed.Error);
	}

	[Fact]
	public async Task RefreshAsync_WhenFirstProviderCallFails_ReturnsEmptyErrorSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(_, _, _) => Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot snapshot = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			snapshot.Error);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task RefreshAsync_WhenProviderCancelsItself_CachesStaleFailure()
	{
		DateTimeOffset now = new(2026, 7, 15, 4, 15, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"last-good"))
				: Task.FromException<UsageSnapshot>(new OperationCanceledException()));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot canceled = await coordinator.RefreshAsync(account);
		UsageSnapshot cached = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal(SnapshotStatus.Stale, canceled.Status);
		Assert.Equal("last-good", canceled.Metrics[0].DisplayValue);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			canceled.Error);
		Assert.Equal(UsageRecoveryAction.Retry, canceled.RecoveryAction);
		Assert.Equal(canceled, cached);
	}

	[Fact]
	public async Task RefreshAsync_WhenFirstProviderCallCancelsItself_CachesErrorFailure()
	{
		DateTimeOffset now = new(2026, 7, 15, 4, 20, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(_, _, _) => Task.FromException<UsageSnapshot>(
				new OperationCanceledException()));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot canceled = await coordinator.RefreshAsync(account);
		UsageSnapshot cached = await coordinator.RefreshAsync(account);

		Assert.Equal(1, provider.CallCount);
		Assert.Equal(SnapshotStatus.Error, canceled.Status);
		Assert.Empty(canceled.Metrics);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			canceled.Error);
		Assert.Equal(UsageRecoveryAction.Retry, canceled.RecoveryAction);
		Assert.Equal(canceled, cached);
	}

	[Fact]
	public async Task RefreshAsync_WhenStaleSnapshotHasMetrics_UsesItAsFailureFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 4, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, _) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"stale-capture") with
				{
					Status = SnapshotStatus.Stale,
					Error = "Capture 已過期。"
				})
				: Task.FromException<UsageSnapshot>(new IOException("private path")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		UsageSnapshot initialStale = await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot failedRefresh = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Stale, initialStale.Status);
		Assert.Equal(SnapshotStatus.Stale, failedRefresh.Status);
		Assert.Equal("stale-capture", failedRefresh.Metrics[0].DisplayValue);
		Assert.Equal(
			"暫時無法取得用量，AI Usage 稍後會自動再試。",
			failedRefresh.Error);
	}

	[Fact]
	public async Task Invalidate_WhenOldRefreshCompletes_DoesNotReplaceNewGenerationCache()
	{
		DateTimeOffset now = new(2026, 7, 15, 5, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		TaskCompletionSource<UsageSnapshot> oldProviderResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(5),
			(providerAccount, callNumber, _) => callNumber == 1
				? oldProviderResult.Task
				: Task.FromResult(CreateReadySnapshot(
					providerAccount,
					timeProvider.GetUtcNow(),
					"new-generation")));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		Task<UsageSnapshot> oldRefresh = coordinator.RefreshAsync(account);
		coordinator.Invalidate(account.Id);
		UsageSnapshot newRefresh = await coordinator.RefreshAsync(account);
		oldProviderResult.SetResult(CreateReadySnapshot(account, now, "old-generation"));
		await oldRefresh;
		UsageSnapshot cached = await coordinator.RefreshAsync(account);

		Assert.Equal(2, provider.CallCount);
		Assert.Equal("new-generation", newRefresh.Metrics[0].DisplayValue);
		Assert.Equal("new-generation", cached.Metrics[0].DisplayValue);
	}

	[Fact]
	public async Task Invalidate_WhenProviderObservesCancellation_ReturnsSafeTerminalSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 5, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, _, cancellationToken) => WaitForCancellationAsync(
				providerAccount,
				now,
				cancellationToken));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		Task<UsageSnapshot> refresh = coordinator.RefreshAsync(account);
		coordinator.Invalidate(account.Id);
		UsageSnapshot stopped = await refresh;

		Assert.Equal(SnapshotStatus.NotConfigured, stopped.Status);
		Assert.Empty(stopped.Metrics);
		Assert.Equal("用量檢查已停止。", stopped.Error);
		Assert.Equal(UsageRecoveryAction.None, stopped.RecoveryAction);
		Assert.Equal(new[] { account.Id }, provider.InvalidatedAccountIds);
	}

	[Fact]
	public async Task Invalidate_WhenLastKnownGoodExists_ReturnsItAsStaleInsteadOfThrowing()
	{
		DateTimeOffset now = new(2026, 7, 15, 5, 45, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeUsageProvider provider = new(
			account.Provider,
			TimeSpan.FromMinutes(1),
			(providerAccount, callNumber, cancellationToken) => callNumber == 1
				? Task.FromResult(CreateReadySnapshot(
					providerAccount,
					now,
					"last-good"))
				: WaitForCancellationAsync(
					providerAccount,
					now,
					cancellationToken));
		UsageRefreshCoordinator coordinator = CreateCoordinator(provider, timeProvider);

		await coordinator.RefreshAsync(account);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		Task<UsageSnapshot> refresh = coordinator.RefreshAsync(account);
		coordinator.Invalidate(account.Id);
		UsageSnapshot stopped = await refresh;

		Assert.Equal(SnapshotStatus.Stale, stopped.Status);
		Assert.Equal("last-good", stopped.Metrics[0].DisplayValue);
		Assert.Equal("用量檢查已停止。", stopped.Error);
		Assert.Equal(UsageRecoveryAction.None, stopped.RecoveryAction);
	}

	[Fact]
	public void IsStaleAt_UsesProvidedTimestamp()
	{
		DateTimeOffset staleAfter = new(2026, 7, 15, 6, 0, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		UsageSnapshot snapshot = CreateReadySnapshot(
			account,
			staleAfter - TimeSpan.FromMinutes(1),
			"usage") with
		{
			StaleAfter = staleAfter
		};

		Assert.False(snapshot.IsStaleAt(staleAfter - TimeSpan.FromTicks(1)));
		Assert.True(snapshot.IsStaleAt(staleAfter));
	}

	private static AccountProfile CreateAccount()
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"測試帳號");
	}

	private static UsageRefreshCoordinator CreateCoordinator(
		IUsageProvider provider,
		TimeProvider timeProvider)
	{
		UsageProviderRegistry registry = new(new[] { provider });
		return new UsageRefreshCoordinator(registry, timeProvider);
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		string displayValue)
	{
		return new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric(
					"usage",
					"Usage",
					42,
					displayValue)
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			fetchedAt);
	}

	private static UsageSnapshot CreateRetryableFailureSnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		DateTimeOffset? retryNotBefore = null)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			fetchedAt,
			Error: "暫時無法取得用量。",
			RecoveryAction: UsageRecoveryAction.Retry,
			RetryNotBefore: retryNotBefore);
	}

	private static async Task<UsageSnapshot> WaitForCancellationAsync(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		CancellationToken cancellationToken)
	{
		await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		return CreateReadySnapshot(account, fetchedAt, "unexpected");
	}
}
