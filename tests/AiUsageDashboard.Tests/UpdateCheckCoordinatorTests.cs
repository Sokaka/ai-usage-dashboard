using System.Collections.Concurrent;

using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class UpdateCheckCoordinatorTests
{
	private sealed class MutableTimeProvider : TimeProvider
	{
		private DateTimeOffset _utcNow;

		internal MutableTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		internal void Advance(TimeSpan duration)
		{
			_utcNow += duration;
		}
	}

	private sealed class InMemoryStateStore : IUpdateCheckStateStore
	{
		private readonly object _syncRoot = new();
		private UpdateCheckPersistentState? _state;

		internal int SaveCount { get; private set; }
		internal bool SaveResult { get; set; } = true;

		internal UpdateCheckPersistentState? State
		{
			get
			{
				lock (_syncRoot)
				{
					return _state;
				}
			}
		}

		internal InMemoryStateStore(
			UpdateCheckPersistentState? state = null)
		{
			_state = state;
		}

		public Task<UpdateCheckPersistentState?> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(State);
		}

		public Task<bool> TrySaveAsync(
			UpdateCheckPersistentState state,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (_syncRoot)
			{
				SaveCount++;
				if (SaveResult)
				{
					_state = state;
				}
			}

			return Task.FromResult(SaveResult);
		}
	}

	private sealed class BlockingSaveStateStore : IUpdateCheckStateStore
	{
		private readonly TaskCompletionSource _allowSave = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource SaveStarted { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal void AllowSave()
		{
			_allowSave.TrySetResult();
		}

		public Task<UpdateCheckPersistentState?> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<UpdateCheckPersistentState?>(null);
		}

		public async Task<bool> TrySaveAsync(
			UpdateCheckPersistentState state,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(state);
			SaveStarted.TrySetResult();
			await _allowSave.Task.WaitAsync(cancellationToken);
			return true;
		}
	}

	private sealed class BlockingReadTimeProvider : TimeProvider, IDisposable
	{
		private readonly TaskCompletionSource _readBlocked = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _releaseRead = new(false);
		private DateTimeOffset _utcNow;
		private int _blockOnReadCount;
		private int _readCount;

		internal Task ReadBlocked => _readBlocked.Task;

		internal BlockingReadTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			int readCount = Interlocked.Increment(ref _readCount);
			if (readCount == Volatile.Read(ref _blockOnReadCount))
			{
				_readBlocked.TrySetResult();
				_releaseRead.Wait();
			}

			return _utcNow;
		}

		public void Dispose()
		{
			_releaseRead.Set();
			_releaseRead.Dispose();
		}

		internal void Advance(TimeSpan duration)
		{
			_utcNow += duration;
		}

		internal void BlockAfterAdditionalReads(int additionalReadCount)
		{
			if (additionalReadCount <= 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(additionalReadCount));
			}

			Volatile.Write(
				ref _blockOnReadCount,
				Volatile.Read(ref _readCount) + additionalReadCount);
		}

		internal void ReleaseRead()
		{
			_releaseRead.Set();
		}
	}

	private sealed class FakeAvailabilityChecker : IUpdateAvailabilityChecker
	{
		private int _callCount;

		internal int CallCount => Volatile.Read(ref _callCount);

		internal Func<
			UpdateAvailabilityCheckRequest,
			CancellationToken,
			Task<UpdateAvailabilityCheckResult>> Handler { get; set; } =
			(_, _) => Task.FromResult(new UpdateAvailabilityCheckResult(
				UpdateAvailabilityStatus.UpToDate,
				"1.0.3",
				1017));

		public Task<UpdateAvailabilityCheckResult> CheckAsync(
			UpdateAvailabilityCheckRequest request,
			CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _callCount);
			return Handler(request, cancellationToken);
		}
	}

	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		8,
		0,
		0,
		TimeSpan.Zero);

	[Fact]
	public async Task AutomaticAndManualChecks_RespectStartupDelayAndManualBypass()
	{
		MutableTimeProvider clock = new(TestNow);
		FakeAvailabilityChecker checker = new();
		InMemoryStateStore store = new();
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock);
		await coordinator.InitializeAsync();

		Assert.True(coordinator.ShouldShowAutomaticCheckNotice);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			coordinator.NextAutomaticCheckUtc);
		UpdateCheckExecutionResult automaticResult =
			await coordinator.TryCheckAutomaticallyAsync();
		UpdateCheckExecutionResult manualResult =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(
			UpdateCheckExecutionOutcome.SkippedNotDue,
			automaticResult.Outcome);
		Assert.Equal(UpdateCheckExecutionOutcome.Completed, manualResult.Outcome);
		Assert.True(manualResult.IsInteractive);
		Assert.Equal(1, checker.CallCount);
		Assert.Equal(UpdatePresentationStatus.UpToDate, coordinator.CurrentState.Status);
		Assert.True(coordinator.CurrentState.IsManualCheck);
		Assert.Equal(
			TestNow + TimeSpan.FromHours(24),
			coordinator.NextAutomaticCheckUtc);
	}

	[Fact]
	public async Task ManualCheck_DuringAutomaticCheck_JoinsSingleFlightAndBecomesInteractive()
	{
		MutableTimeProvider clock = new(TestNow);
		TaskCompletionSource checkStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UpdateAvailabilityCheckResult> checkCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAvailabilityChecker checker = new()
		{
			Handler = async (_, cancellationToken) =>
			{
				checkStarted.TrySetResult();
				return await checkCompletion.Task.WaitAsync(cancellationToken);
			}
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			clock);
		await coordinator.InitializeAsync();
		clock.Advance(TimeSpan.FromSeconds(30));

		Task<UpdateCheckExecutionResult> automaticTask =
			coordinator.TryCheckAutomaticallyAsync();
		await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<UpdateCheckExecutionResult> manualTask =
			coordinator.CheckManuallyAsync();

		Assert.Equal(UpdatePresentationStatus.Checking, coordinator.CurrentState.Status);
		Assert.True(coordinator.CurrentState.IsManualCheck);
		Assert.Equal(1, checker.CallCount);
		checkCompletion.SetResult(new UpdateAvailabilityCheckResult(
			UpdateAvailabilityStatus.UpdateAvailable,
			"1.0.4",
			1018));
		UpdateCheckExecutionResult[] results = await Task.WhenAll(
			automaticTask,
			manualTask);

		Assert.All(
			results,
			result => Assert.Equal(
				UpdateCheckExecutionOutcome.Completed,
				result.Outcome));
		Assert.False(results[0].IsInteractive);
		Assert.True(results[1].IsInteractive);
		Assert.Equal(1, checker.CallCount);
		Assert.Equal(
			UpdatePresentationStatus.UpdateAvailable,
			coordinator.CurrentState.Status);
	}

	[Fact]
	public async Task ManualCheck_JoiningDuringFailureFinalization_UpgradesFinalState()
	{
		using BlockingReadTimeProvider clock = new(TestNow);
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromException<UpdateAvailabilityCheckResult>(
				new IOException("synthetic network failure"))
		};
		await using UpdateCheckCoordinator coordinator = new(
			checker,
			new InMemoryStateStore(),
			new UpdateCheckCoordinatorOptions(
				AppInstallationContext.CreateUnmanaged(
					"C:\\portable\\AiUsageDashboard.App.exe"),
				CurrentProductVersion: "1.0.3+abcdef",
				IsFeatureAvailableInBuild: true,
				clock,
				StartedAtUtc: TestNow));
		await coordinator.InitializeAsync();
		clock.Advance(TimeSpan.FromSeconds(30));
		clock.BlockAfterAdditionalReads(additionalReadCount: 5);

		Task<UpdateCheckExecutionResult> automaticTask =
			coordinator.TryCheckAutomaticallyAsync();
		Task<UpdateCheckExecutionResult>? manualTask = null;
		try
		{
			await clock.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(5));
			manualTask = coordinator.CheckManuallyAsync();
		}
		finally
		{
			clock.ReleaseRead();
		}

		if (manualTask is null)
		{
			throw new InvalidOperationException(
				"The manual check did not start at the finalization boundary.");
		}

		UpdateCheckExecutionResult[] results = await Task.WhenAll(
			automaticTask,
			manualTask);

		Assert.False(results[0].IsInteractive);
		Assert.True(results[1].IsInteractive);
		Assert.True(results[1].State.IsManualCheck);
		Assert.NotNull(results[1].State.FailureMessage);
		Assert.True(coordinator.CurrentState.IsManualCheck);
		Assert.NotNull(coordinator.CurrentState.FailureMessage);
		Assert.Equal(1, checker.CallCount);
	}

	[Fact]
	public async Task AutoCheckPreferenceAndNotice_ArePersistedWithoutCreatingDueTimer()
	{
		MutableTimeProvider clock = new(TestNow);
		InMemoryStateStore store = new();
		FakeAvailabilityChecker checker = new();
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock);
		await coordinator.InitializeAsync();

		await coordinator.MarkAutomaticCheckNoticeShownAsync();
		AutoCheckPreferenceChangeResult disabledResult =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled: false);

		Assert.True(disabledResult.IsPersisted);
		Assert.Null(disabledResult.PersistenceWarning);
		Assert.False(coordinator.ShouldShowAutomaticCheckNotice);
		Assert.Null(coordinator.NextAutomaticCheckUtc);
		Assert.Equal(
			UpdatePresentationStatus.DisabledByUser,
			coordinator.CurrentState.Status);
		UpdateCheckPersistentState savedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.False(savedState.IsAutoCheckEnabled);
		Assert.True(savedState.HasShownAutomaticCheckNotice);
		Assert.Equal(
			UpdateCheckExecutionOutcome.SkippedDisabled,
			(await coordinator.TryCheckAutomaticallyAsync()).Outcome);
		Assert.Equal(0, checker.CallCount);

		AutoCheckPreferenceChangeResult enabledResult =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled: true);

		Assert.True(enabledResult.IsPersisted);
		Assert.Null(enabledResult.PersistenceWarning);
		Assert.NotNull(coordinator.NextAutomaticCheckUtc);
		Assert.Equal(UpdatePresentationStatus.IdleStale, coordinator.CurrentState.Status);
	}

	[Fact]
	public async Task Initialize_WhenAppVersionChanged_InvalidatesVersionScopedState()
	{
		UpdateKnownResult oldResult = new(
			"1.0.2",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState oldState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow,
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = oldResult,
				LastBalloonAttemptKey = oldResult.NotificationKey.ToString(),
				Snooze = new UpdateSnoozeState(
					"1.0.4",
					1018,
					TestNow + TimeSpan.FromHours(2))
			};
		InMemoryStateStore store = new(oldState);
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			store,
			new MutableTimeProvider(TestNow),
			currentProductVersion: "1.0.3+abcdef");

		await coordinator.InitializeAsync();

		Assert.Equal(UpdatePresentationStatus.IdleStale, coordinator.CurrentState.Status);
		Assert.Null(coordinator.CurrentState.LastKnownResult);
		UpdateCheckPersistentState savedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.Null(savedState.LastAttemptUtc);
		Assert.Null(savedState.LastSuccessfulCheckUtc);
		Assert.Null(savedState.LastKnownResult);
		Assert.Null(savedState.LastBalloonAttemptKey);
		Assert.Null(savedState.Snooze);
		Assert.Equal(1018, savedState.HighestObservedReleaseSequence);
	}

	[Fact]
	public async Task Initialize_WhenCachedResultIsStale_DoesNotRestoreUpdateStatus()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState staleState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow - TimeSpan.FromHours(25),
				LastSuccessfulCheckUtc = TestNow - TimeSpan.FromHours(25),
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = knownResult
			};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			new InMemoryStateStore(staleState),
			new MutableTimeProvider(TestNow));

		await coordinator.InitializeAsync();

		Assert.Equal(
			UpdatePresentationStatus.IdleStale,
			coordinator.CurrentState.Status);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			coordinator.NextAutomaticCheckUtc);
	}

	[Fact]
	public async Task UnknownCurrentVersionResult_RestoresAcrossRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		MutableTimeProvider clock = new(TestNow);
		JsonUpdateCheckStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"update-state.json"));
		FakeAvailabilityChecker firstChecker = new()
		{
			Handler = (_, _) => Task.FromResult(
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UnknownCurrentVersion,
					"1.0.4",
					1018))
		};
		await using (UpdateCheckCoordinator firstCoordinator = CreateCoordinator(
			firstChecker,
			store,
			clock,
			currentProductVersion: null))
		{
			await firstCoordinator.InitializeAsync();
			UpdateCheckExecutionResult result =
				await firstCoordinator.CheckManuallyAsync();

			Assert.Equal(UpdateCheckExecutionOutcome.Completed, result.Outcome);
			Assert.Equal(
				UpdatePresentationStatus.UnknownCurrentVersion,
				firstCoordinator.CurrentState.Status);
		}

		FakeAvailabilityChecker secondChecker = new();
		await using UpdateCheckCoordinator secondCoordinator = CreateCoordinator(
			secondChecker,
			store,
			clock,
			currentProductVersion: null);

		await secondCoordinator.InitializeAsync();

		Assert.Equal(
			UpdatePresentationStatus.UnknownCurrentVersion,
			secondCoordinator.CurrentState.Status);
		Assert.Equal("1.0.4", secondCoordinator.CurrentState.AvailableVersion);
		Assert.Equal(1018, secondCoordinator.CurrentState.ReleaseSequence);
		Assert.Equal(
			TestNow + TimeSpan.FromHours(24),
			secondCoordinator.NextAutomaticCheckUtc);
		Assert.Equal(0, secondChecker.CallCount);
	}

	[Fact]
	public async Task Initialize_WhenUnknownResultNowHasKnownVersion_InvalidatesThrottle()
	{
		UpdateCheckPersistentState unknownState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow,
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = new UpdateKnownResult(
					CheckedAgainstVersion: null,
					AvailableVersion: "1.0.4",
					ReleaseSequence: 1018,
					IsUpdateAvailable: false)
			};
		InMemoryStateStore store = new(unknownState);
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			store,
			new MutableTimeProvider(TestNow));

		await coordinator.InitializeAsync();

		Assert.Equal(
			UpdatePresentationStatus.IdleStale,
			coordinator.CurrentState.Status);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			coordinator.NextAutomaticCheckUtc);
		UpdateCheckPersistentState persistedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.Null(persistedState.LastSuccessfulCheckUtc);
		Assert.Null(persistedState.LastKnownResult);
		Assert.Equal(1018, persistedState.HighestObservedReleaseSequence);
	}

	[Fact]
	public async Task StateWriteFailure_KeepsUpdatedPreferenceInMemory()
	{
		InMemoryStateStore store = new()
		{
			SaveResult = false
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			store,
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();

		AutoCheckPreferenceChangeResult result =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled: false);

		Assert.False(result.IsEnabled);
		Assert.False(result.IsPersisted);
		Assert.Contains("本次執行期間關閉", result.PersistenceWarning);
		Assert.Contains("重新啟動 AI Usage 後", result.PersistenceWarning);
		Assert.Contains("可能會恢復", result.PersistenceWarning);
		Assert.Contains("本機資料夾可寫入", result.PersistenceWarning);
		Assert.False(coordinator.CurrentState.IsAutoCheckEnabled);
		Assert.Equal(
			UpdatePresentationStatus.DisabledByUser,
			coordinator.CurrentState.Status);
		Assert.Null(coordinator.NextAutomaticCheckUtc);
		Assert.Null(store.State);
		Assert.Equal(1, store.SaveCount);
	}

	[Fact]
	public async Task EnableSaveFailure_ThenDisable_RestoresPersistedRuntimeState()
	{
		UpdateCheckPersistentState disabledCache =
			UpdateCheckPersistentState.Default with
			{
				IsAutoCheckEnabled = false,
				HasShownAutomaticCheckNotice = true
			};
		InMemoryStateStore store = new(disabledCache)
		{
			SaveResult = false
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			store,
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();

		AutoCheckPreferenceChangeResult enableResult =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled: true);

		Assert.False(enableResult.IsPersisted);
		Assert.True(coordinator.CurrentState.IsAutoCheckEnabled);
		Assert.Equal(1, store.SaveCount);

		AutoCheckPreferenceChangeResult disableResult =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled: false);

		Assert.True(disableResult.IsPersisted);
		Assert.False(coordinator.CurrentState.IsAutoCheckEnabled);
		Assert.Equal(
			UpdatePresentationStatus.DisabledByUser,
			coordinator.CurrentState.Status);
		Assert.Null(coordinator.NextAutomaticCheckUtc);
		Assert.Equal(1, store.SaveCount);
		Assert.Same(disabledCache, store.State);
	}

	[Fact]
	public async Task DisableSaveFailure_WithEnabledCache_RestoresOldPreferenceAfterRestart()
	{
		UpdateCheckPersistentState enabledCache =
			UpdateCheckPersistentState.Default with
			{
				IsAutoCheckEnabled = true,
				HasShownAutomaticCheckNotice = true
			};
		InMemoryStateStore store = new(enabledCache)
		{
			SaveResult = false
		};
		MutableTimeProvider clock = new(TestNow);

		await using (UpdateCheckCoordinator firstCoordinator = CreateCoordinator(
			new FakeAvailabilityChecker(),
			store,
			clock))
		{
			await firstCoordinator.InitializeAsync();

			AutoCheckPreferenceChangeResult result =
				await firstCoordinator.SetAutoCheckEnabledAsync(isEnabled: false);

			Assert.False(result.IsPersisted);
			Assert.False(firstCoordinator.CurrentState.IsAutoCheckEnabled);
			Assert.Null(firstCoordinator.NextAutomaticCheckUtc);
			Assert.Same(enabledCache, store.State);
		}

		FakeAvailabilityChecker restartedChecker = new();
		await using UpdateCheckCoordinator restartedCoordinator =
			CreateCoordinator(restartedChecker, store, clock);
		await restartedCoordinator.InitializeAsync();

		Assert.True(restartedCoordinator.CurrentState.IsAutoCheckEnabled);
		Assert.False(restartedCoordinator.ShouldShowAutomaticCheckNotice);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			restartedCoordinator.NextAutomaticCheckUtc);
		clock.Advance(TimeSpan.FromSeconds(30));

		UpdateCheckExecutionResult automaticResult =
			await restartedCoordinator.TryCheckAutomaticallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Completed, automaticResult.Outcome);
		Assert.Equal(1, restartedChecker.CallCount);
	}

	[Fact]
	public async Task FailedChecks_KeepLastSuccessAndExposeOnlyManualFailureAsInteractive()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState initialState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow - TimeSpan.FromHours(24),
				LastSuccessfulCheckUtc = TestNow - TimeSpan.FromHours(24),
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = knownResult
			};
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromException<UpdateAvailabilityCheckResult>(
				new IOException("synthetic network failure"))
		};
		MutableTimeProvider clock = new(TestNow);
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(initialState),
			clock);
		await coordinator.InitializeAsync();
		clock.Advance(TimeSpan.FromSeconds(30));

		UpdateCheckExecutionResult automaticResult =
			await coordinator.TryCheckAutomaticallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Failed, automaticResult.Outcome);
		Assert.False(automaticResult.IsInteractive);
		Assert.Null(automaticResult.State.FailureMessage);
		Assert.Equal(UpdatePresentationStatus.CheckFailed, coordinator.CurrentState.Status);
		Assert.False(coordinator.CurrentState.IsManualCheck);
		Assert.Equal(knownResult, coordinator.CurrentState.LastKnownResult);
		Assert.Equal(
			TestNow - TimeSpan.FromHours(24),
			coordinator.CurrentState.LastSuccessfulCheckUtc);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(15),
			coordinator.NextAutomaticCheckUtc);

		UpdateCheckExecutionResult manualResult =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Failed, manualResult.Outcome);
		Assert.True(manualResult.IsInteractive);
		Assert.True(coordinator.CurrentState.IsManualCheck);
		Assert.NotNull(manualResult.State.FailureMessage);
		Assert.Equal(2, checker.CallCount);
	}

	[Fact]
	public async Task ManualFailure_WhenAutoCheckIsDisabled_DoesNotPromiseAutoRetry()
	{
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromException<UpdateAvailabilityCheckResult>(
				new HttpRequestException("synthetic network failure"))
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();
		await coordinator.SetAutoCheckEnabledAsync(isEnabled: false);

		UpdateCheckExecutionResult result =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Failed, result.Outcome);
		Assert.Equal(
			"無法連線到更新服務，請稍後再試。",
			result.State.FailureMessage);
		Assert.DoesNotContain(
			"自動重試",
			result.State.FailureMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Resume_WhenClockRolledBack_RebuildsThrottleAndChecksAtNewDueTime()
	{
		UpdateCheckPersistentState futureState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow + TimeSpan.FromHours(2),
				ConsecutiveFailureCount = 2
			};
		InMemoryStateStore store = new(futureState);
		List<string> diagnostics = new();
		MutableTimeProvider clock = new(TestNow);
		FakeAvailabilityChecker checker = new();
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock,
			reportDiagnostic: (summary, _) => diagnostics.Add(summary));

		await coordinator.InitializeAsync();

		Assert.Contains(
			diagnostics,
			summary => summary.Contains("clock anomaly", StringComparison.Ordinal));
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			coordinator.NextAutomaticCheckUtc);
		UpdateCheckPersistentState savedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.Null(savedState.LastAttemptUtc);
		Assert.Equal(0, savedState.ConsecutiveFailureCount);
		clock.Advance(TimeSpan.FromSeconds(29));
		Assert.Equal(
			UpdateCheckExecutionOutcome.SkippedNotDue,
			(await coordinator.HandleResumeAsync()).Outcome);
		clock.Advance(TimeSpan.FromSeconds(1));

		Assert.Equal(
			UpdateCheckExecutionOutcome.Completed,
			(await coordinator.HandleResumeAsync()).Outcome);
		Assert.Equal(1, checker.CallCount);
	}

	[Fact]
	public async Task SnoozeAndBalloonAttempt_AreVersionScopedAndDeduplicated()
	{
		MutableTimeProvider clock = new(TestNow);
		ConcurrentQueue<UpdateAvailabilityCheckResult> results = new(
			new[]
			{
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UpdateAvailable,
					"1.0.4",
					1018),
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UpdateAvailable,
					"1.0.5",
					1019)
			});
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => results.TryDequeue(out UpdateAvailabilityCheckResult? result)
				? Task.FromResult(result)
				: Task.FromException<UpdateAvailabilityCheckResult>(
					new InvalidOperationException("No synthetic result remains."))
		};
		InMemoryStateStore store = new();
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock);
		await coordinator.InitializeAsync();
		await coordinator.CheckManuallyAsync();

		Assert.False(coordinator.TryGetBalloonAttemptKey(
			new UpdateWindowActivity(
				IsVisible: true,
				IsCollapsed: false,
				IsActive: true),
			out _));
		Assert.True(coordinator.TryGetBalloonAttemptKey(
			new UpdateWindowActivity(
				IsVisible: true,
				IsCollapsed: false,
				IsActive: false),
			out UpdateNotificationKey attemptKey));
		Assert.True(await coordinator.RecordBalloonAttemptAsync(attemptKey));
		Assert.False(coordinator.TryGetBalloonAttemptKey(
			new UpdateWindowActivity(
				IsVisible: false,
				IsCollapsed: false,
				IsActive: false),
			out _));

		SnoozeUpdateResult snoozeResult = await coordinator.SnoozeAsync();
		Assert.True(snoozeResult.IsApplied);
		Assert.True(snoozeResult.IsPersisted);
		Assert.Null(snoozeResult.PersistenceWarning);
		Assert.True(coordinator.CurrentState.IsSnoozed);
		Assert.Equal(
			TestNow + TimeSpan.FromHours(24),
			coordinator.CurrentState.SnoozedUntilUtc);
		Assert.False(coordinator.TryGetBalloonAttemptKey(
			new UpdateWindowActivity(
				IsVisible: false,
				IsCollapsed: true,
				IsActive: false),
			out _));

		UpdateCheckExecutionResult newerResult =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Completed, newerResult.Outcome);
		Assert.Equal("1.0.5", coordinator.CurrentState.AvailableVersion);
		Assert.False(coordinator.CurrentState.IsSnoozed);
		UpdateCheckPersistentState persistedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.Null(persistedState.Snooze);
	}

	[Fact]
	public async Task Snooze_WhenStateWriteFails_ReturnsActionableWarning()
	{
		InMemoryStateStore store = new();
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromResult(
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UpdateAvailable,
					"1.0.4",
					1018))
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();
		await coordinator.CheckManuallyAsync();
		store.SaveResult = false;

		SnoozeUpdateResult result = await coordinator.SnoozeAsync();

		Assert.True(result.IsApplied);
		Assert.False(result.IsPersisted);
		Assert.Contains("本次執行期間", result.PersistenceWarning);
		Assert.Contains("重新啟動 AI Usage 後", result.PersistenceWarning);
		Assert.Contains("可能會再次顯示", result.PersistenceWarning);
		Assert.Contains("本機資料夾可寫入", result.PersistenceWarning);
		Assert.True(coordinator.CurrentState.IsSnoozed);
		UpdateCheckPersistentState persistedState =
			Assert.IsType<UpdateCheckPersistentState>(store.State);
		Assert.Null(persistedState.Snooze);
	}

	[Fact]
	public async Task AutomaticTick_WhenDisabledSnoozeExpires_RestoresKnownUpdateWithoutNetwork()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState initialState =
			UpdateCheckPersistentState.Default with
			{
				IsAutoCheckEnabled = false,
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow,
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = knownResult,
				Snooze = new UpdateSnoozeState(
					"1.0.4",
					1018,
					TestNow + TimeSpan.FromHours(24))
			};
		FakeAvailabilityChecker checker = new();
		MutableTimeProvider clock = new(TestNow);
		InMemoryStateStore store = new(initialState);
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock);
		await coordinator.InitializeAsync();
		Assert.True(coordinator.CurrentState.IsSnoozed);

		clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
		UpdateCheckExecutionResult result =
			await coordinator.TryCheckAutomaticallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.SkippedDisabled, result.Outcome);
		Assert.Equal(0, checker.CallCount);
		Assert.Equal(
			UpdatePresentationStatus.UpdateAvailable,
			coordinator.CurrentState.Status);
		Assert.False(coordinator.CurrentState.IsSnoozed);
		Assert.Null(coordinator.CurrentState.SnoozedUntilUtc);
		Assert.Null(coordinator.NextAutomaticCheckUtc);
		Assert.Null(Assert.IsType<UpdateCheckPersistentState>(store.State).Snooze);
	}

	[Fact]
	public async Task AutomaticCheck_WhenNewResultArrives_ClearsDifferentSnoozeKey()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState initialState =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow - TimeSpan.FromHours(24),
				LastSuccessfulCheckUtc = TestNow - TimeSpan.FromHours(24),
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = knownResult,
				Snooze = new UpdateSnoozeState(
					"1.0.4",
					1018,
					TestNow + TimeSpan.FromHours(1))
			};
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromResult(
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UpdateAvailable,
					"1.0.5",
					1019))
		};
		MutableTimeProvider clock = new(TestNow);
		InMemoryStateStore store = new(initialState);
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			store,
			clock);
		await coordinator.InitializeAsync();
		clock.Advance(TimeSpan.FromSeconds(30));

		UpdateCheckExecutionResult result =
			await coordinator.TryCheckAutomaticallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Completed, result.Outcome);
		Assert.False(coordinator.CurrentState.IsSnoozed);
		Assert.Null(Assert.IsType<UpdateCheckPersistentState>(store.State).Snooze);
	}

	[Fact]
	public async Task Check_WhenResultContradictsKnownCurrentVersion_FailsClosed()
	{
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromResult(
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UnknownCurrentVersion,
					"1.0.4",
					1018))
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();

		UpdateCheckExecutionResult result =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Failed, result.Outcome);
		Assert.IsType<InvalidDataException>(result.Failure);
		Assert.Equal(
			UpdatePresentationStatus.CheckFailed,
			coordinator.CurrentState.Status);
	}

	[Fact]
	public async Task Check_WhenAvailabilityStatusContradictsVersions_FailsClosed()
	{
		FakeAvailabilityChecker checker = new()
		{
			Handler = (_, _) => Task.FromResult(
				new UpdateAvailabilityCheckResult(
					UpdateAvailabilityStatus.UpToDate,
					"1.0.4",
					1018))
		};
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();

		UpdateCheckExecutionResult result =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Failed, result.Outcome);
		Assert.IsType<InvalidDataException>(result.Failure);
	}

	[Fact]
	public async Task DisposeAsync_CancelsAndObservesActiveCheck()
	{
		TaskCompletionSource checkStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAvailabilityChecker checker = new()
		{
			Handler = async (_, cancellationToken) =>
			{
				checkStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				throw new InvalidOperationException(
					"The cancelled synthetic check continued unexpectedly.");
			}
		};
		UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			new MutableTimeProvider(TestNow));
		await coordinator.InitializeAsync();
		Task<UpdateCheckExecutionResult> checkTask =
			coordinator.CheckManuallyAsync();
		await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await coordinator.DisposeAsync();
		UpdateCheckExecutionResult result = await checkTask;

		Assert.Equal(UpdateCheckExecutionOutcome.Cancelled, result.Outcome);
	}

	[Fact]
	public async Task DisposeAsync_WaitsForOperationThatStartsCheckAfterDisposalBegins()
	{
		BlockingSaveStateStore store = new();
		FakeAvailabilityChecker checker = new()
		{
			Handler = async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				throw new InvalidOperationException(
					"The cancelled synthetic check continued unexpectedly.");
			}
		};
		UpdateCheckCoordinator coordinator = new(
			checker,
			store,
			new UpdateCheckCoordinatorOptions(
				AppInstallationContext.CreateUnmanaged(
					"C:\\portable\\AiUsageDashboard.App.exe"),
				CurrentProductVersion: "1.0.3+abcdef",
				IsFeatureAvailableInBuild: true,
				new MutableTimeProvider(TestNow),
				StartedAtUtc: TestNow));
		await coordinator.InitializeAsync();
		Task preferenceTask = coordinator.SetAutoCheckEnabledAsync(
			isEnabled: false);
		await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<UpdateCheckExecutionResult> manualTask =
			coordinator.CheckManuallyAsync();
		Task disposeTask = coordinator.DisposeAsync().AsTask();

		try
		{
			Assert.False(disposeTask.IsCompleted);
		}
		finally
		{
			store.AllowSave();
		}

		await preferenceTask;
		UpdateCheckExecutionResult result = await manualTask;
		await disposeTask;
		await coordinator.DisposeAsync();

		Assert.Equal(UpdateCheckExecutionOutcome.Cancelled, result.Outcome);
		Assert.Equal(1, checker.CallCount);
	}

	[Fact]
	public async Task ManualCheck_WhenFeatureUnavailable_DoesNotCallNetworkChecker()
	{
		FakeAvailabilityChecker checker = new();
		await using UpdateCheckCoordinator coordinator = CreateCoordinator(
			checker,
			new InMemoryStateStore(),
			new MutableTimeProvider(TestNow),
			isFeatureAvailableInBuild: false);
		await coordinator.InitializeAsync();

		UpdateCheckExecutionResult result =
			await coordinator.CheckManuallyAsync();

		Assert.Equal(
			UpdateCheckExecutionOutcome.UnavailableInThisBuild,
			result.Outcome);
		Assert.True(result.IsInteractive);
		Assert.Equal(0, checker.CallCount);
		Assert.Null(coordinator.NextAutomaticCheckUtc);
	}

	private static UpdateCheckCoordinator CreateCoordinator(
		FakeAvailabilityChecker checker,
		IUpdateCheckStateStore store,
		MutableTimeProvider clock,
		string? currentProductVersion = "1.0.3+abcdef",
		bool isFeatureAvailableInBuild = true,
		Action<string, Exception?>? reportDiagnostic = null)
	{
		return new UpdateCheckCoordinator(
			checker,
			store,
			new UpdateCheckCoordinatorOptions(
				AppInstallationContext.CreateUnmanaged(
					"C:\\portable\\AiUsageDashboard.App.exe"),
				currentProductVersion,
				isFeatureAvailableInBuild,
				clock,
				StartedAtUtc: TestNow,
				reportDiagnostic));
	}
}
