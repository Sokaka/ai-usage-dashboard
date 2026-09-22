using System.IO;
using System.Net.Http;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal sealed record UpdateCheckCoordinatorOptions(
	AppInstallationContext InstallationContext,
	string? CurrentProductVersion,
	bool IsFeatureAvailableInBuild,
	TimeProvider? Clock = null,
	DateTimeOffset? StartedAtUtc = null,
	Action<string, Exception?>? ReportDiagnostic = null);

internal sealed record AutoCheckPreferenceChangeResult(
	bool IsEnabled,
	bool IsPersisted,
	string? PersistenceWarning);

internal sealed record SnoozeUpdateResult(
	bool IsApplied,
	bool IsPersisted,
	string? PersistenceWarning);

internal sealed class UpdateCheckCoordinator : IAsyncDisposable
{
	private sealed class OperationLease : IDisposable
	{
		private UpdateCheckCoordinator? _owner;

		internal OperationLease(UpdateCheckCoordinator owner)
		{
			_owner = owner;
		}

		public void Dispose()
		{
			UpdateCheckCoordinator? owner = Interlocked.Exchange(
				ref _owner,
				null);
			owner?.ExitOperation();
		}
	}

	internal event EventHandler<UpdatePresentationStateChangedEventArgs>?
		StateChanged;

	private const string DiagnosticOperation = "update-check-coordinator";
	private const int MaximumFailureCount = 1_000_000;
	private readonly IUpdateAvailabilityChecker _availabilityChecker;
	private readonly TaskCompletionSource _disposeCompletion = new(
		TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly SemaphoreSlim _mutationGate = new(1, 1);
	private readonly Action<string, Exception?> _reportDiagnostic;
	private readonly object _stateLock = new();
	private readonly IUpdateCheckStateStore _stateStore;
	private readonly TimeProvider _timeProvider;
	private readonly AppInstallationContext _installationContext;
	private readonly bool _isFeatureAvailableInBuild;
	private readonly string? _currentVersion;
	private DateTimeOffset _automaticChecksNotBeforeUtc;
	private Task<UpdateCheckExecutionResult>? _activeCheckTask;
	private TaskCompletionSource? _operationsDrained;
	private int _activeOperationCount;
	private bool _activeCheckHasManualObserver;
	private bool _persistedAutoCheckEnabled;
	private UpdateCheckPersistentState _persistentState;
	private UpdatePresentationState _presentationState;
	private DateTimeOffset? _nextAutomaticCheckUtc;
	private bool _isDisposed;
	private bool _isInitialized;

	internal UpdatePresentationState CurrentState
	{
		get
		{
			lock (_stateLock)
			{
				return _presentationState;
			}
		}
	}

	internal DateTimeOffset? NextAutomaticCheckUtc
	{
		get
		{
			lock (_stateLock)
			{
				return _nextAutomaticCheckUtc;
			}
		}
	}

	internal bool ShouldShowAutomaticCheckNotice
	{
		get
		{
			lock (_stateLock)
			{
				return _isInitialized &&
					_isFeatureAvailableInBuild &&
					_persistentState.IsAutoCheckEnabled &&
					!_persistentState.HasShownAutomaticCheckNotice;
			}
		}
	}

	internal UpdateCheckCoordinator(
		IUpdateAvailabilityChecker availabilityChecker,
		IUpdateCheckStateStore stateStore,
		UpdateCheckCoordinatorOptions options)
	{
		_availabilityChecker = availabilityChecker ??
			throw new ArgumentNullException(nameof(availabilityChecker));
		_stateStore = stateStore ??
			throw new ArgumentNullException(nameof(stateStore));
		ArgumentNullException.ThrowIfNull(options);
		_installationContext = options.InstallationContext ??
			throw new ArgumentNullException(
				nameof(options.InstallationContext));
		_isFeatureAvailableInBuild = options.IsFeatureAvailableInBuild;
		_timeProvider = options.Clock ?? TimeProvider.System;
		_reportDiagnostic = options.ReportDiagnostic ?? ReportDiagnostic;
		_currentVersion = ResolveCurrentVersion(
			options.CurrentProductVersion,
			_installationContext);
		DateTimeOffset startedAtUtc =
			(options.StartedAtUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime();
		_automaticChecksNotBeforeUtc = AddWithoutOverflow(
			startedAtUtc,
			UpdateCheckScheduler.AutomaticCheckNoticeDelay);
		_persistentState = UpdateCheckPersistentState.Default;
		_persistedAutoCheckEnabled =
			UpdateCheckPersistentState.Default.IsAutoCheckEnabled;
		_presentationState = UpdatePresentationState.CreateInitial(
			_isFeatureAvailableInBuild,
			_currentVersion);
	}

	public async ValueTask DisposeAsync()
	{
		Task operationsDrainedTask;
		bool ownsDisposal;
		lock (_stateLock)
		{
			if (_isDisposed)
			{
				ownsDisposal = false;
				operationsDrainedTask = Task.CompletedTask;
			}
			else
			{
				_isDisposed = true;
				ownsDisposal = true;
				if (_activeOperationCount == 0)
				{
					operationsDrainedTask = Task.CompletedTask;
				}
				else
				{
					_operationsDrained = new TaskCompletionSource(
						TaskCreationOptions.RunContinuationsAsynchronously);
					operationsDrainedTask = _operationsDrained.Task;
				}
			}
		}

		if (!ownsDisposal)
		{
			await _disposeCompletion.Task.ConfigureAwait(false);
			return;
		}

		try
		{
			try
			{
				_lifetimeCancellation.Cancel();
			}
			catch (Exception exception)
			{
				TryReportDiagnostic(
					"An update check cancellation callback failed during shutdown.",
					exception);
			}

			await operationsDrainedTask.ConfigureAwait(false);
			Task<UpdateCheckExecutionResult>? activeCheckTask;
			lock (_stateLock)
			{
				activeCheckTask = _activeCheckTask;
			}

			if (activeCheckTask is not null)
			{
				try
				{
					_ = await activeCheckTask.ConfigureAwait(false);
				}
				catch (Exception exception)
				{
					TryReportDiagnostic(
						"The active update check faulted during shutdown.",
						exception);
				}
			}

			_lifetimeCancellation.Dispose();
			_mutationGate.Dispose();
			_disposeCompletion.TrySetResult();
		}
		catch (Exception exception)
		{
			_disposeCompletion.TrySetException(exception);
			throw;
		}
	}

	internal async Task InitializeAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: false);
		UpdatePresentationState? stateToPublish = null;
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			if (_isInitialized)
			{
				return;
			}

			UpdateCheckPersistentState loadedState;
			bool wasCacheMiss;

			try
			{
				UpdateCheckPersistentState? persistedState =
					await _stateStore.LoadAsync(cancellationToken)
						.ConfigureAwait(false);
				loadedState = persistedState ??
					UpdateCheckPersistentState.Default;
				wasCacheMiss = persistedState is null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				TryReportDiagnostic(
					"The update check state store failed during initialization.",
					exception);
				loadedState = UpdateCheckPersistentState.Default;
				wasCacheMiss = true;
			}

			UpdateCheckPersistentState versionScopedState =
				InvalidateStateForCurrentVersion(loadedState);
			UpdateScheduleEvaluation schedule = UpdateCheckScheduler.Evaluate(
				versionScopedState,
				_timeProvider.GetUtcNow(),
				_automaticChecksNotBeforeUtc);

			if (schedule.WasClockAnomaly)
			{
				TryReportDiagnostic(
					"Future update scheduling timestamps were ignored after a clock anomaly.",
					exception: null);
			}

			bool shouldPersistCleanup =
				!wasCacheMiss &&
				((versionScopedState != loadedState) ||
					schedule.WasStateCleanedUp);
			if (shouldPersistCleanup)
			{
				_ = await PersistBestEffortAsync(
					schedule.State,
					cancellationToken).ConfigureAwait(false);
			}

			UpdatePresentationState presentation =
				CreatePresentationState(schedule.State, failureMessage: null);
			lock (_stateLock)
			{
				_persistentState = schedule.State;
				_persistedAutoCheckEnabled =
					schedule.State.IsAutoCheckEnabled;
				_automaticChecksNotBeforeUtc =
					schedule.AutomaticChecksNotBeforeUtc;
				_nextAutomaticCheckUtc =
					_isFeatureAvailableInBuild
						? schedule.NextAutomaticCheckUtc
						: null;
				_presentationState = presentation;
				_isInitialized = true;
				stateToPublish = presentation;
			}
		}
		finally
		{
			_mutationGate.Release();
		}

		if (stateToPublish is not null)
		{
			RaiseStateChanged(stateToPublish);
		}
	}

	internal async Task<UpdateCheckExecutionResult> CheckManuallyAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		if (!_isFeatureAvailableInBuild)
		{
			return new UpdateCheckExecutionResult(
				UpdateCheckExecutionOutcome.UnavailableInThisBuild,
				CurrentState,
				Failure: null,
				IsInteractive: true);
		}

		Task<UpdateCheckExecutionResult>? joinedCheckTask =
			TryJoinActiveCheckAsManual();
		Task<UpdateCheckExecutionResult>? checkTask = null;
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateCheckPersistentState persistentState =
				GetPersistentStateSnapshot();
			if (ShouldClearCurrentSnooze(persistentState))
			{
				persistentState = persistentState with { Snooze = null };
				_ = await PersistBestEffortAsync(
					persistentState,
					cancellationToken).ConfigureAwait(false);
				UpdateScheduleEvaluation schedule = EvaluateSchedule(
					persistentState);
				UpdatePresentationState clearedSnoozeState = CurrentState with
				{
					IsSnoozed = false,
					SnoozedUntilUtc = null
				};
				CommitRuntimeState(
					schedule,
					clearedSnoozeState,
					raiseEvent: true);
			}

			checkTask = joinedCheckTask ?? GetOrStartCheck(isManual: true);
		}
		finally
		{
			_mutationGate.Release();
		}

		if (checkTask is null)
		{
			throw new InvalidOperationException(
				"A manual update check did not start or join a check task.");
		}

		UpdateCheckExecutionResult result = await WaitForCallerAsync(
			checkTask,
			cancellationToken).ConfigureAwait(false);
		return await CreateInteractiveManualResultAsync(result)
			.ConfigureAwait(false);
	}

	internal async Task<UpdateCheckExecutionResult>
		TryCheckAutomaticallyAsync(
			CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		Task<UpdateCheckExecutionResult>? checkTask = null;
		UpdateCheckExecutionResult? skippedResult = null;
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateScheduleEvaluation schedule = EvaluateSchedule(
				GetPersistentStateSnapshot());
			if (schedule.WasClockAnomaly)
			{
				TryReportDiagnostic(
					"Future update scheduling timestamps were ignored after a clock anomaly.",
					exception: null);
			}

			if (schedule.WasStateCleanedUp)
			{
				_ = await PersistBestEffortAsync(
					schedule.State,
					cancellationToken).ConfigureAwait(false);
				UpdatePresentationState currentState = CurrentState;
				UpdatePresentationState stateToPublish = CreatePresentationState(
					schedule.State,
					failureMessage: null);
				if (currentState.IsSnoozed &&
					(schedule.State.Snooze is null) &&
					(schedule.State.LastKnownResult is
						{ IsUpdateAvailable: true } knownResult) &&
					(currentState.LastKnownResult == knownResult))
				{
					stateToPublish = currentState with
					{
						IsAutoCheckEnabled = schedule.State.IsAutoCheckEnabled,
						IsSnoozed = false,
						SnoozedUntilUtc = null
					};
				}

				if (currentState.Status == UpdatePresentationStatus.Checking)
				{
					stateToPublish = stateToPublish with
					{
						Status = UpdatePresentationStatus.Checking,
						IsManualCheck = currentState.IsManualCheck
					};
				}
				CommitRuntimeState(
					schedule,
					stateToPublish,
					raiseEvent: true);
			}
			else
			{
				UpdatePresentationState currentState = CurrentState;
				CommitRuntimeState(
					schedule,
					currentState,
					raiseEvent: false);
			}

			if (!_isFeatureAvailableInBuild)
			{
				skippedResult = new UpdateCheckExecutionResult(
					UpdateCheckExecutionOutcome.UnavailableInThisBuild,
					CurrentState);
			}
			else if (!schedule.State.IsAutoCheckEnabled)
			{
				skippedResult = new UpdateCheckExecutionResult(
					UpdateCheckExecutionOutcome.SkippedDisabled,
					CurrentState);
			}
			else if (!schedule.IsDue(_timeProvider.GetUtcNow()))
			{
				skippedResult = new UpdateCheckExecutionResult(
					UpdateCheckExecutionOutcome.SkippedNotDue,
					CurrentState);
			}
			else
			{
				checkTask = GetOrStartCheck(isManual: false);
			}
		}
		finally
		{
			_mutationGate.Release();
		}

		if (skippedResult is not null)
		{
			return skippedResult;
		}

		if (checkTask is null)
		{
			throw new InvalidOperationException(
				"An automatic update check was due but no check task was started.");
		}

		UpdateCheckExecutionResult result = await WaitForCallerAsync(
			checkTask,
			cancellationToken).ConfigureAwait(false);
		return result with { IsInteractive = false };
	}

	internal Task<UpdateCheckExecutionResult> HandleResumeAsync(
		CancellationToken cancellationToken = default)
	{
		return TryCheckAutomaticallyAsync(cancellationToken);
	}

	internal async Task<AutoCheckPreferenceChangeResult>
		SetAutoCheckEnabledAsync(
		bool isEnabled,
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateCheckPersistentState currentPersistentState =
				GetPersistentStateSnapshot();
			UpdatePresentationState currentPresentation = CurrentState;
			bool requiresPersistence =
				GetPersistedAutoCheckEnabled() != isEnabled;
			if (!requiresPersistence &&
				(currentPresentation.IsAutoCheckEnabled == isEnabled))
			{
				return new AutoCheckPreferenceChangeResult(
					isEnabled,
					IsPersisted: true,
					PersistenceWarning: null);
			}

			UpdateCheckPersistentState updatedPersistentState =
				currentPersistentState with
				{
					IsAutoCheckEnabled = isEnabled
				};
			bool isPersisted = !requiresPersistence ||
				await PersistBestEffortAsync(
					updatedPersistentState,
					cancellationToken).ConfigureAwait(false);
			UpdateScheduleEvaluation schedule = EvaluateSchedule(
				updatedPersistentState);
			UpdatePresentationStatus status = currentPresentation.Status switch
			{
				UpdatePresentationStatus.DisabledByUser when isEnabled =>
					UpdatePresentationStatus.IdleStale,
				UpdatePresentationStatus.IdleStale when !isEnabled =>
					UpdatePresentationStatus.DisabledByUser,
				_ => currentPresentation.Status
			};
			UpdatePresentationState stateToPublish = currentPresentation with
			{
				Status = status,
				IsAutoCheckEnabled = isEnabled
			};
			CommitRuntimeState(
				schedule,
				stateToPublish,
				raiseEvent: true);
			return new AutoCheckPreferenceChangeResult(
				isEnabled,
				isPersisted,
				isPersisted
					? null
					: CreateAutoCheckPreferencePersistenceWarning(isEnabled));
		}
		finally
		{
			_mutationGate.Release();
		}

	}

	private static string CreateAutoCheckPreferencePersistenceWarning(
		bool isEnabled)
	{
		string currentSessionState = isEnabled ? "開啟" : "關閉";
		string restartState = isEnabled ? "再次關閉" : "恢復";
		return
			$"自動檢查更新已在本次執行期間{currentSessionState}，但無法儲存這項設定。重新啟動 AI Usage 後，自動檢查可能會{restartState}。請確認 AI Usage 本機資料夾可寫入，再重新設定。";
	}

	internal async Task MarkAutomaticCheckNoticeShownAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateCheckPersistentState currentPersistentState =
				GetPersistentStateSnapshot();
			if (currentPersistentState.HasShownAutomaticCheckNotice)
			{
				return;
			}

			UpdateCheckPersistentState updatedPersistentState =
				currentPersistentState with
				{
					HasShownAutomaticCheckNotice = true
				};
			_ = await PersistBestEffortAsync(
				updatedPersistentState,
				cancellationToken).ConfigureAwait(false);
			UpdateScheduleEvaluation schedule = EvaluateSchedule(
				updatedPersistentState);
			UpdatePresentationState stateToPublish = CurrentState with
			{
				HasShownAutomaticCheckNotice = true
			};
			CommitRuntimeState(
				schedule,
				stateToPublish,
				raiseEvent: true);
		}
		finally
		{
			_mutationGate.Release();
		}

	}

	internal async Task<SnoozeUpdateResult> SnoozeAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateCheckPersistentState currentPersistentState =
				GetPersistentStateSnapshot();
			UpdatePresentationState currentPresentation = CurrentState;
			if ((currentPresentation.Status !=
					UpdatePresentationStatus.UpdateAvailable) ||
				(currentPersistentState.LastKnownResult is not
					UpdateKnownResult knownResult) ||
				!knownResult.IsUpdateAvailable)
			{
				return new SnoozeUpdateResult(
					IsApplied: false,
					IsPersisted: true,
					PersistenceWarning: null);
			}

			DateTimeOffset snoozedUntilUtc = AddWithoutOverflow(
				_timeProvider.GetUtcNow(),
				UpdateCheckScheduler.SnoozeDuration);
			UpdateCheckPersistentState updatedPersistentState =
				currentPersistentState with
				{
					Snooze = new UpdateSnoozeState(
						knownResult.AvailableVersion,
						knownResult.ReleaseSequence,
						snoozedUntilUtc)
				};
			bool isPersisted = await PersistBestEffortAsync(
				updatedPersistentState,
				cancellationToken).ConfigureAwait(false);
			UpdateScheduleEvaluation schedule = EvaluateSchedule(
				updatedPersistentState);
			UpdatePresentationState stateToPublish = currentPresentation with
			{
				IsSnoozed = true,
				SnoozedUntilUtc = snoozedUntilUtc
			};
			CommitRuntimeState(
				schedule,
				stateToPublish,
				raiseEvent: true);
			return new SnoozeUpdateResult(
				IsApplied: true,
				isPersisted,
				isPersisted
					? null
					: CreateSnoozePersistenceWarning());
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private static string CreateSnoozePersistenceWarning()
	{
		return
			"已在本次執行期間暫停這次更新提醒，但無法儲存稍後提醒設定。重新啟動 AI Usage 後，這次更新可能會再次顯示。請確認 AI Usage 本機資料夾可寫入，再重新設定。";
	}

	internal bool TryGetBalloonAttemptKey(
		UpdateWindowActivity windowActivity,
		out UpdateNotificationKey key)
	{
		lock (_stateLock)
		{
			key = default;
			if (!_isInitialized ||
				(_presentationState.Status !=
					UpdatePresentationStatus.UpdateAvailable) ||
				_presentationState.IsSnoozed ||
				!windowActivity.AllowsBalloonAttempt ||
				(_persistentState.LastKnownResult is not
					UpdateKnownResult knownResult) ||
				!knownResult.IsUpdateAvailable)
			{
				return false;
			}

			key = knownResult.NotificationKey;
			return !string.Equals(
				_persistentState.LastBalloonAttemptKey,
				key.ToString(),
				StringComparison.Ordinal);
		}
	}

	internal async Task<bool> RecordBalloonAttemptAsync(
		UpdateNotificationKey key,
		CancellationToken cancellationToken = default)
	{
		using IDisposable operationLease = EnterOperation(
			requiresInitialization: true);
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			UpdateCheckPersistentState currentPersistentState =
				GetPersistentStateSnapshot();
			if ((currentPersistentState.LastKnownResult is not
					UpdateKnownResult knownResult) ||
				!knownResult.IsUpdateAvailable ||
				(knownResult.NotificationKey != key) ||
				IsSnoozed(currentPersistentState, knownResult, _timeProvider.GetUtcNow()) ||
				string.Equals(
					currentPersistentState.LastBalloonAttemptKey,
					key.ToString(),
					StringComparison.Ordinal))
			{
				return false;
			}

			UpdateCheckPersistentState updatedPersistentState =
				currentPersistentState with
				{
					LastBalloonAttemptKey = key.ToString()
				};
			_ = await PersistBestEffortAsync(
				updatedPersistentState,
				cancellationToken).ConfigureAwait(false);
			UpdateScheduleEvaluation schedule = EvaluateSchedule(
				updatedPersistentState);
			CommitRuntimeState(
				schedule,
				CurrentState,
				raiseEvent: false);
			return true;
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private static DateTimeOffset AddWithoutOverflow(
		DateTimeOffset value,
		TimeSpan duration)
	{
		try
		{
			return value.ToUniversalTime() + duration;
		}
		catch (ArgumentOutOfRangeException)
		{
			return DateTimeOffset.MaxValue;
		}
	}

	private static bool IsSnoozed(
		UpdateCheckPersistentState persistentState,
		UpdateKnownResult knownResult,
		DateTimeOffset utcNow)
	{
		return persistentState.Snooze is UpdateSnoozeState snooze &&
			(snooze.NotificationKey == knownResult.NotificationKey) &&
			(snooze.SnoozedUntilUtc.ToUniversalTime() >
				utcNow.ToUniversalTime());
	}

	private static string? ResolveCurrentVersion(
		string? productVersion,
		AppInstallationContext installationContext)
	{
		if ((installationContext.Kind != AppInstallationKind.Unmanaged) &&
			AppCurrentVersionParser.TryParse(
				installationContext.InstalledManifest?.Version,
				out string parsedManagedVersion))
		{
			return parsedManagedVersion;
		}

		if (AppCurrentVersionParser.TryParse(
				productVersion,
				out string parsedProductVersion))
		{
			return parsedProductVersion;
		}

		return AppCurrentVersionParser.TryParse(
			installationContext.InstalledManifest?.Version,
			out string parsedManifestVersion)
			? parsedManifestVersion
			: null;
	}

	private static async Task<UpdateCheckExecutionResult> WaitForCallerAsync(
		Task<UpdateCheckExecutionResult> checkTask,
		CancellationToken cancellationToken)
	{
		return cancellationToken.CanBeCanceled
			? await checkTask.WaitAsync(cancellationToken).ConfigureAwait(false)
			: await checkTask.ConfigureAwait(false);
	}

	private async Task<UpdateCheckExecutionResult>
		CreateInteractiveManualResultAsync(UpdateCheckExecutionResult result)
	{
		if (result.State.IsManualCheck ||
			(result.Outcome == UpdateCheckExecutionOutcome.Cancelled))
		{
			return result with { IsInteractive = true };
		}

		if ((result.Outcome != UpdateCheckExecutionOutcome.Completed) &&
			(result.Outcome != UpdateCheckExecutionOutcome.Failed))
		{
			throw new InvalidOperationException(
				"A manual update check joined a task with an unexpected outcome.");
		}

		string? failureMessage = result.State.FailureMessage;
		if ((result.Outcome == UpdateCheckExecutionOutcome.Failed) &&
			(result.Failure is Exception failure))
		{
			failureMessage = GetUserFacingFailureMessage(failure);
		}

		UpdatePresentationState interactiveState = result.State with
		{
			FailureMessage = failureMessage,
			IsManualCheck = true
		};
		await _mutationGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (CurrentState == result.State)
			{
				UpdateScheduleEvaluation schedule = EvaluateSchedule(
					GetPersistentStateSnapshot());
				CommitRuntimeState(
					schedule,
					interactiveState,
					raiseEvent: true);
			}
		}
		finally
		{
			_mutationGate.Release();
		}

		return result with
		{
			State = interactiveState,
			IsInteractive = true
		};
	}

	private void CommitRuntimeState(
		UpdateScheduleEvaluation schedule,
		UpdatePresentationState presentationState,
		bool raiseEvent)
	{
		lock (_stateLock)
		{
			_persistentState = schedule.State;
			_automaticChecksNotBeforeUtc =
				schedule.AutomaticChecksNotBeforeUtc;
			_nextAutomaticCheckUtc = _isFeatureAvailableInBuild
				? schedule.NextAutomaticCheckUtc
				: null;
			_presentationState = presentationState;
		}

		if (raiseEvent)
		{
			RaiseStateChanged(presentationState);
		}
	}

	private UpdatePresentationState CreatePresentationState(
		UpdateCheckPersistentState persistentState,
		string? failureMessage)
	{
		DateTimeOffset utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
		UpdateKnownResult? knownResult = persistentState.LastKnownResult;
		bool isCurrentKnownResult = knownResult is not null &&
			((_currentVersion is null)
				? knownResult.CheckedAgainstVersion is null
				: string.Equals(
					knownResult.CheckedAgainstVersion,
					_currentVersion,
					StringComparison.Ordinal));
		DateTimeOffset freshnessThresholdUtc = SubtractWithoutUnderflow(
			utcNow,
			UpdateCheckScheduler.ResultFreshness);
		bool isKnownResultFresh = isCurrentKnownResult &&
			(persistentState.LastSuccessfulCheckUtc is
				DateTimeOffset successfulCheckUtc) &&
			(successfulCheckUtc.ToUniversalTime() <= AddWithoutOverflow(
				utcNow,
				UpdateCheckScheduler.ClockFutureTolerance)) &&
			(successfulCheckUtc.ToUniversalTime() >= freshnessThresholdUtc);
		UpdatePresentationStatus status;

		if (!_isFeatureAvailableInBuild)
		{
			status = UpdatePresentationStatus.UnavailableInThisBuild;
		}
		else if (persistentState.ConsecutiveFailureCount > 0)
		{
			status = UpdatePresentationStatus.CheckFailed;
		}
		else if (isKnownResultFresh)
		{
			if (knownResult is null)
			{
				throw new InvalidOperationException(
					"A fresh update result was expected but is unavailable.");
			}

			status = knownResult.Status switch
			{
				UpdateAvailabilityStatus.UnknownCurrentVersion =>
					UpdatePresentationStatus.UnknownCurrentVersion,
				UpdateAvailabilityStatus.UpToDate =>
					UpdatePresentationStatus.UpToDate,
				UpdateAvailabilityStatus.UpdateAvailable =>
					UpdatePresentationStatus.UpdateAvailable,
				_ => throw new InvalidOperationException(
					"The cached update result has an unknown availability status.")
			};
		}
		else
		{
			status = persistentState.IsAutoCheckEnabled
				? UpdatePresentationStatus.IdleStale
				: UpdatePresentationStatus.DisabledByUser;
		}

		bool isSnoozed = false;
		if (isCurrentKnownResult)
		{
			if (knownResult is null)
			{
				throw new InvalidOperationException(
					"A current update result was expected but is unavailable.");
			}

			isSnoozed = knownResult.IsUpdateAvailable &&
				IsSnoozed(persistentState, knownResult, utcNow);
		}
		return new UpdatePresentationState(
			status,
			persistentState.IsAutoCheckEnabled,
			persistentState.HasShownAutomaticCheckNotice,
			_currentVersion,
			knownResult?.AvailableVersion,
			knownResult?.ReleaseSequence,
			knownResult,
			persistentState.LastSuccessfulCheckUtc,
			persistentState.ConsecutiveFailureCount > 0
				? persistentState.LastAttemptUtc
				: null,
			failureMessage,
			HasManualObserver(),
			isSnoozed,
			isSnoozed ? persistentState.Snooze?.SnoozedUntilUtc : null);
	}

	private UpdateScheduleEvaluation EvaluateSchedule(
		UpdateCheckPersistentState persistentState)
	{
		return UpdateCheckScheduler.Evaluate(
			persistentState,
			_timeProvider.GetUtcNow(),
			_automaticChecksNotBeforeUtc);
	}

	private async Task<UpdateCheckExecutionResult> ExecuteCheckAsync()
	{
		await Task.Yield();
		UpdatePresentationState checkingState = CurrentState with
		{
			Status = UpdatePresentationStatus.Checking,
			LastFailureUtc = null,
			FailureMessage = null,
			IsManualCheck = HasManualObserver()
		};
		PublishPresentationState(checkingState);

		try
		{
			UpdateCheckPersistentState requestState =
				GetPersistentStateSnapshot();
			UpdateAvailabilityCheckResult result =
				await _availabilityChecker.CheckAsync(
					new UpdateAvailabilityCheckRequest(
						_installationContext,
						_currentVersion,
						requestState.HighestObservedReleaseSequence),
					_lifetimeCancellation.Token).ConfigureAwait(false);
			ValidateAvailabilityResult(result, requestState);

			await _mutationGate.WaitAsync(_lifetimeCancellation.Token)
				.ConfigureAwait(false);
			try
			{
				return await CompleteSuccessfulCheckAsync(
					result,
					_lifetimeCancellation.Token).ConfigureAwait(false);
			}
			finally
			{
				_mutationGate.Release();
			}
		}
		catch (OperationCanceledException) when (
			_lifetimeCancellation.IsCancellationRequested)
		{
			UpdatePresentationState restoredState = CreatePresentationState(
				GetPersistentStateSnapshot(),
				failureMessage: null);
			PublishPresentationState(restoredState);
			return new UpdateCheckExecutionResult(
				UpdateCheckExecutionOutcome.Cancelled,
				restoredState);
		}
		catch (Exception exception)
		{
			TryReportDiagnostic("An update availability check failed.", exception);
			await _mutationGate.WaitAsync().ConfigureAwait(false);
			try
			{
				return await CompleteFailedCheckAsync(exception)
					.ConfigureAwait(false);
			}
			finally
			{
				_mutationGate.Release();
			}
		}
	}

	private async Task<UpdateCheckExecutionResult> CompleteFailedCheckAsync(
		Exception exception)
	{
		UpdateCheckPersistentState currentPersistentState =
			GetPersistentStateSnapshot();
		DateTimeOffset attemptedAtUtc =
			_timeProvider.GetUtcNow().ToUniversalTime();
		int failureCount = Math.Min(
			currentPersistentState.ConsecutiveFailureCount + 1,
			MaximumFailureCount);
		UpdateCheckPersistentState updatedPersistentState =
			currentPersistentState with
			{
				LastAttemptUtc = attemptedAtUtc,
				ConsecutiveFailureCount = failureCount
			};
		_ = await PersistBestEffortAsync(
			updatedPersistentState,
			CancellationToken.None).ConfigureAwait(false);
		UpdateScheduleEvaluation schedule = EvaluateSchedule(
			updatedPersistentState);
		bool isManualCheck = HasManualObserver();
		UpdatePresentationState failureState = CreatePresentationState(
			updatedPersistentState,
			isManualCheck
				? GetUserFacingFailureMessage(exception)
				: null) with
		{
			IsManualCheck = isManualCheck
		};
		CommitRuntimeState(schedule, failureState, raiseEvent: true);
		return new UpdateCheckExecutionResult(
			UpdateCheckExecutionOutcome.Failed,
			failureState,
			exception,
			IsInteractive: failureState.IsManualCheck);
	}

	private async Task<UpdateCheckExecutionResult> CompleteSuccessfulCheckAsync(
		UpdateAvailabilityCheckResult result,
		CancellationToken cancellationToken)
	{
		UpdateCheckPersistentState currentPersistentState =
			GetPersistentStateSnapshot();
		DateTimeOffset checkedAtUtc =
			_timeProvider.GetUtcNow().ToUniversalTime();
		long highestObservedSequence = Math.Max(
			currentPersistentState.HighestObservedReleaseSequence ?? 0,
			result.ReleaseSequence);
		UpdateKnownResult knownResult = new(
			_currentVersion,
			result.AvailableVersion,
			result.ReleaseSequence,
			result.Status == UpdateAvailabilityStatus.UpdateAvailable);
		UpdateSnoozeState? snooze = currentPersistentState.Snooze;
		if ((snooze is not null) &&
			(knownResult.NotificationKey != snooze.NotificationKey))
		{
			snooze = null;
		}

		UpdateCheckPersistentState updatedPersistentState =
			currentPersistentState with
			{
				LastAttemptUtc = checkedAtUtc,
				LastSuccessfulCheckUtc = checkedAtUtc,
				ConsecutiveFailureCount = 0,
				HighestObservedReleaseSequence = highestObservedSequence,
				LastKnownResult = knownResult,
				Snooze = snooze
			};
		_ = await PersistBestEffortAsync(
			updatedPersistentState,
			cancellationToken).ConfigureAwait(false);
		UpdateScheduleEvaluation schedule = EvaluateSchedule(
			updatedPersistentState);
		UpdatePresentationState successfulState = CreatePresentationState(
			updatedPersistentState,
			failureMessage: null);
		successfulState = successfulState with
		{
			IsManualCheck = HasManualObserver()
		};
		CommitRuntimeState(schedule, successfulState, raiseEvent: true);
		return new UpdateCheckExecutionResult(
			UpdateCheckExecutionOutcome.Completed,
			successfulState,
			Failure: null,
			IsInteractive: successfulState.IsManualCheck);
	}

	private Task<UpdateCheckExecutionResult> GetOrStartCheck(bool isManual)
	{
		UpdatePresentationState? stateToPublish = null;
		Task<UpdateCheckExecutionResult> checkTask;
		lock (_stateLock)
		{
			if ((_activeCheckTask is not null) &&
				_activeCheckTask.IsCompleted)
			{
				_activeCheckTask = null;
				_activeCheckHasManualObserver = false;
			}

			if (_activeCheckTask is not null)
			{
				checkTask = _activeCheckTask;
				if (isManual && !_activeCheckHasManualObserver)
				{
					_activeCheckHasManualObserver = true;
					if (_presentationState.Status ==
						UpdatePresentationStatus.Checking)
					{
						_presentationState = _presentationState with
						{
							IsManualCheck = true
						};
						stateToPublish = _presentationState;
					}
				}
			}
			else
			{
				_activeCheckHasManualObserver = isManual;
				checkTask = ExecuteCheckAsync();
				_activeCheckTask = checkTask;
				_ = ObserveCheckCompletionAsync(checkTask);
			}
		}

		if (stateToPublish is not null)
		{
			RaiseStateChanged(stateToPublish);
		}

		return checkTask;
	}

	private Task<UpdateCheckExecutionResult>? TryJoinActiveCheckAsManual()
	{
		UpdatePresentationState? stateToPublish = null;
		Task<UpdateCheckExecutionResult>? checkTask;
		lock (_stateLock)
		{
			if ((_activeCheckTask is not Task<UpdateCheckExecutionResult> activeTask) ||
				activeTask.IsCompleted)
			{
				return null;
			}

			checkTask = activeTask;
			if (!_activeCheckHasManualObserver)
			{
				_activeCheckHasManualObserver = true;
				if (_presentationState.Status ==
					UpdatePresentationStatus.Checking)
				{
					_presentationState = _presentationState with
					{
						IsManualCheck = true
					};
					stateToPublish = _presentationState;
				}
			}
		}

		if (stateToPublish is not null)
		{
			RaiseStateChanged(stateToPublish);
		}

		return checkTask;
	}

	private bool HasManualObserver()
	{
		lock (_stateLock)
		{
			return _activeCheckHasManualObserver;
		}
	}

	private UpdateCheckPersistentState GetPersistentStateSnapshot()
	{
		lock (_stateLock)
		{
			return _persistentState;
		}
	}

	private bool GetPersistedAutoCheckEnabled()
	{
		lock (_stateLock)
		{
			return _persistedAutoCheckEnabled;
		}
	}

	private static string GetUserFacingFailureMessage(Exception exception)
	{
		return exception switch
		{
			HttpRequestException =>
				"無法連線到更新服務，請稍後再試。",
			InvalidDataException or FormatException =>
				"更新資訊未通過驗證，已停止本次檢查。",
			_ => "無法完成更新檢查，請稍後再試。"
		};
	}

	private UpdateCheckPersistentState InvalidateStateForCurrentVersion(
		UpdateCheckPersistentState state)
	{
		if (state.LastKnownResult is null)
		{
			return state.LastSuccessfulCheckUtc is not null
				? ClearVersionScopedState(state)
				: state;
		}

		UpdateKnownResult knownResult = state.LastKnownResult;
		bool canRestoreCurrentResult = (_currentVersion is null)
			? knownResult.CheckedAgainstVersion is null
			: string.Equals(
				knownResult.CheckedAgainstVersion,
				_currentVersion,
				StringComparison.Ordinal);
		if (canRestoreCurrentResult)
		{
			return state;
		}

		return ClearVersionScopedState(state);
	}

	private static UpdateCheckPersistentState ClearVersionScopedState(
		UpdateCheckPersistentState state)
	{
		return state with
		{
			LastAttemptUtc = null,
			LastSuccessfulCheckUtc = null,
			ConsecutiveFailureCount = 0,
			LastKnownResult = null,
			LastBalloonAttemptKey = null,
			Snooze = null
		};
	}

	private IDisposable EnterOperation(bool requiresInitialization)
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			if (requiresInitialization && !_isInitialized)
			{
				throw new InvalidOperationException(
					"The update check coordinator must be initialized first.");
			}

			_activeOperationCount = checked(_activeOperationCount + 1);
			return new OperationLease(this);
		}
	}

	private void ExitOperation()
	{
		TaskCompletionSource? operationsDrained = null;
		lock (_stateLock)
		{
			if (_activeOperationCount <= 0)
			{
				throw new InvalidOperationException(
					"The update coordinator operation count is invalid.");
			}

			_activeOperationCount--;
			if (_activeOperationCount == 0)
			{
				operationsDrained = _operationsDrained;
				_operationsDrained = null;
			}
		}

		operationsDrained?.TrySetResult();
	}

	private async Task ObserveCheckCompletionAsync(
		Task<UpdateCheckExecutionResult> checkTask)
	{
		try
		{
			_ = await checkTask.ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			TryReportDiagnostic(
				"An update check escaped the coordinator boundary.",
				exception);
		}
		finally
		{
			lock (_stateLock)
			{
				if (ReferenceEquals(_activeCheckTask, checkTask))
				{
					_activeCheckTask = null;
					_activeCheckHasManualObserver = false;
				}
			}
		}
	}

	private async Task<bool> PersistBestEffortAsync(
		UpdateCheckPersistentState state,
		CancellationToken cancellationToken)
	{
		try
		{
			bool isPersisted = await _stateStore.TrySaveAsync(
				state,
				cancellationToken).ConfigureAwait(false);
			if (isPersisted)
			{
				lock (_stateLock)
				{
					_persistedAutoCheckEnabled = state.IsAutoCheckEnabled;
				}
			}

			return isPersisted;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			TryReportDiagnostic(
				"The update check state store failed while saving; in-memory state remains active.",
				exception);
			return false;
		}
	}

	private void PublishPresentationState(UpdatePresentationState state)
	{
		lock (_stateLock)
		{
			_presentationState = state;
		}

		RaiseStateChanged(state);
	}

	private void RaiseStateChanged(UpdatePresentationState state)
	{
		EventHandler<UpdatePresentationStateChangedEventArgs>? handlers =
			StateChanged;
		if (handlers is null)
		{
			return;
		}

		UpdatePresentationStateChangedEventArgs eventArgs = new(state);
		foreach (EventHandler<UpdatePresentationStateChangedEventArgs> handler in
			handlers.GetInvocationList()
				.Cast<EventHandler<UpdatePresentationStateChangedEventArgs>>())
		{
			try
			{
				handler(this, eventArgs);
			}
			catch (Exception exception)
			{
				TryReportDiagnostic(
					"An update presentation state subscriber failed.",
					exception);
			}
		}
	}

	private static void ReportDiagnostic(
		string summary,
		Exception? exception)
	{
		_ = AppDiagnostics.TryWrite(
			DiagnosticOperation,
			summary,
			exception);
	}

	private bool ShouldClearCurrentSnooze(
		UpdateCheckPersistentState state)
	{
		return (state.Snooze is UpdateSnoozeState snooze) &&
			(state.LastKnownResult is UpdateKnownResult knownResult) &&
			(knownResult.NotificationKey == snooze.NotificationKey);
	}

	private void TryReportDiagnostic(
		string summary,
		Exception? exception)
	{
		try
		{
			_reportDiagnostic(summary, exception);
		}
		catch (Exception diagnosticException)
		{
			_ = AppDiagnostics.TryWrite(
				DiagnosticOperation,
				"The update coordinator diagnostic callback failed.",
				diagnosticException);
		}
	}

	private void ValidateAvailabilityResult(
		UpdateAvailabilityCheckResult result,
		UpdateCheckPersistentState requestState)
	{
		ArgumentNullException.ThrowIfNull(result);
		if (!Enum.IsDefined(result.Status) ||
			!ReleaseVersion.TryParse(
				result.AvailableVersion,
				out ReleaseVersion availableVersion) ||
			(result.ReleaseSequence <= 0))
		{
			throw new InvalidDataException(
				"The update availability checker returned an invalid result.");
		}

		bool isUnknownCurrentVersion = _currentVersion is null;
		if ((result.Status == UpdateAvailabilityStatus.UnknownCurrentVersion) !=
			isUnknownCurrentVersion)
		{
			throw new InvalidDataException(
				"The update availability checker returned a result that does not match the current-version state.");
		}

		if (_currentVersion is not null)
		{
			ReleaseVersion currentVersion = ReleaseVersion.Parse(
				_currentVersion);
			int versionComparison = availableVersion.CompareTo(currentVersion);
			if ((_installationContext.Kind != AppInstallationKind.Unmanaged) &&
				(versionComparison < 0))
			{
				throw new InvalidDataException(
					"The update availability checker returned a managed downgrade.");
			}

			UpdateAvailabilityStatus expectedStatus = versionComparison > 0
				? UpdateAvailabilityStatus.UpdateAvailable
				: UpdateAvailabilityStatus.UpToDate;
			if (result.Status != expectedStatus)
			{
				throw new InvalidDataException(
					"The update availability checker returned a status that does not match its versions.");
			}
		}

		if (requestState.HighestObservedReleaseSequence is
			long highestObservedSequence &&
			(result.ReleaseSequence < highestObservedSequence))
		{
			throw new InvalidDataException(
				"The update availability checker returned an older release sequence.");
		}
	}

	private static DateTimeOffset SubtractWithoutUnderflow(
		DateTimeOffset value,
		TimeSpan duration)
	{
		try
		{
			return value.ToUniversalTime() - duration;
		}
		catch (ArgumentOutOfRangeException)
		{
			return DateTimeOffset.MinValue;
		}
	}
}
