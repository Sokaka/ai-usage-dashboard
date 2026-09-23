using System.IO;
using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Antigravity.Setup;

internal enum AntigravitySetupWorkflowState
{
	Ready,
	Preparing,
	Reviewing,
	Approving,
	Completed,
	Failed,
	Cancelled
}

internal sealed class AntigravitySetupWorkflow : IAsyncDisposable
{
	private const int MaximumApprovalRequestPersistenceAttempts = 7;
	private const int MaximumPreparationAttempts = 3;
	private static readonly TimeSpan InitialApprovalRequestPersistenceRetryDelay =
		TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan MaximumApprovalRequestPersistenceRetryDelay =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan InitialPreparationRetryDelay =
		TimeSpan.FromMilliseconds(250);
	private readonly Func<
		AntigravityMachineSetupCandidate,
		CancellationToken,
		Task>? _afterApprovalCommittedAsync;
	private readonly Func<
		AntigravityMachineSetupCandidate,
		CancellationToken,
		Task>? _beforeApprovalCommitAsync;
	private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
	private readonly object _stateLock = new();
	private readonly IAntigravityMachineSetupService _setupService;
	private AntigravityMachineSetupCandidate? _candidate;
	private AntigravityMachineSetupPreparationResult?
		_pendingSafetyRevalidation;
	private CancellationTokenSource? _operationCancellation;
	private long _revision;
	private bool _hasApprovalStarted;
	private bool _hasCommittedSetting;
	private bool _isDisposed;

	internal AntigravityMachineSetupCandidate? Candidate
	{
		get
		{
			lock (_stateLock)
			{
				return _candidate;
			}
		}
	}

	internal AntigravityMachineSetupFailureKind FailureKind { get; private set; }

	internal bool CanRevalidateSafety
	{
		get
		{
			lock (_stateLock)
			{
				return
					State == AntigravitySetupWorkflowState.Failed &&
					_pendingSafetyRevalidation?.CanRevalidateSafety == true;
			}
		}
	}

	internal AntigravitySetupDialogOutcome DialogOutcome
	{
		get
		{
			lock (_stateLock)
			{
				return State switch
				{
					AntigravitySetupWorkflowState.Completed =>
						AntigravitySetupDialogOutcome.CompletedOfficialPrint,
					_ when _hasApprovalStarted || _hasCommittedSetting =>
						AntigravitySetupDialogOutcome.CompletionUnknown,
					AntigravitySetupWorkflowState.Failed =>
						AntigravitySetupDialogOutcome.Failed,
					_ =>
						AntigravitySetupDialogOutcome.Cancelled
				};
			}
		}
	}

	internal bool HasCommittedSetting
	{
		get
		{
			lock (_stateLock)
			{
				return _hasCommittedSetting;
			}
		}
	}

	internal bool HasApprovalStarted
	{
		get
		{
			lock (_stateLock)
			{
				return _hasApprovalStarted;
			}
		}
	}

	internal AntigravitySetupWorkflowState State { get; private set; } =
		AntigravitySetupWorkflowState.Ready;

	internal AntigravitySetupWorkflow(
		IAntigravityMachineSetupService setupService,
		Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
		Func<
			AntigravityMachineSetupCandidate,
			CancellationToken,
			Task>? beforeApprovalCommitAsync = null,
		Func<
			AntigravityMachineSetupCandidate,
			CancellationToken,
			Task>? afterApprovalCommittedAsync = null)
	{
		_setupService = setupService ??
			throw new ArgumentNullException(nameof(setupService));
		_delayAsync = delayAsync ??
			((delay, cancellationToken) =>
				Task.Delay(delay, cancellationToken));
		_beforeApprovalCommitAsync = beforeApprovalCommitAsync;
		_afterApprovalCommittedAsync = afterApprovalCommittedAsync;
	}

	internal async Task PrepareAsync(
		bool hasConfirmedCommandReadyPrompt,
		bool isLiveCaptureApproved,
		IProgress<AntigravityMachineSetupProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		long revision;
		CancellationTokenSource operationCancellation;
		AntigravityMachineSetupCandidate? staleCandidate;

		lock (_stateLock)
		{
			ThrowIfDisposed();

			if (State is AntigravitySetupWorkflowState.Preparing or
				AntigravitySetupWorkflowState.Approving)
			{
				throw new InvalidOperationException(
					"An Antigravity setup operation is already running.");
			}

			staleCandidate = _candidate;
			_candidate = null;
			_pendingSafetyRevalidation = null;
			_operationCancellation?.Dispose();
			_operationCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken);
			operationCancellation = _operationCancellation;
			revision = ++_revision;
			FailureKind = AntigravityMachineSetupFailureKind.None;
			State = AntigravitySetupWorkflowState.Preparing;
		}

		if (staleCandidate is not null)
		{
			if (!await TryDisposeCandidateAsync(staleCandidate))
			{
				SetFailureIfCurrent(
					revision,
					AntigravityMachineSetupFailureKind.PrivateStorageRejected);
				return;
			}
		}

		if (!hasConfirmedCommandReadyPrompt || !isLiveCaptureApproved)
		{
			SetFailureIfCurrent(
				revision,
				AntigravityMachineSetupFailureKind.ConsentRequired);

			lock (_stateLock)
			{
				if (ReferenceEquals(
					_operationCancellation,
					operationCancellation))
				{
					_operationCancellation = null;
				}
			}

			operationCancellation.Dispose();
			return;
		}

		AntigravityMachineSetupPreparationResult result;

		try
		{
			result = await PrepareWithTransientRetryAsync(
				new AntigravityMachineSetupConsent(
					isLiveCaptureApproved &&
					hasConfirmedCommandReadyPrompt),
				progress,
				operationCancellation.Token);
		}
		catch (OperationCanceledException) when (
			operationCancellation.IsCancellationRequested)
		{
			SetCancelledIfCurrent(revision);
			return;
		}
		catch (Exception)
		{
			SetFailureIfCurrent(
				revision,
				AntigravityMachineSetupFailureKind.UnexpectedFailure);
			return;
		}
		finally
		{
			lock (_stateLock)
			{
				if (ReferenceEquals(
					_operationCancellation,
					operationCancellation))
				{
					_operationCancellation = null;
				}
			}

			operationCancellation.Dispose();
		}

		await ApplyPreparationResultAsync(result, revision);
	}

	internal async Task<bool> RevalidateSafetyAsync(
		CancellationToken cancellationToken = default)
	{
		AntigravityMachineSetupPreparationResult pendingRevalidation;
		CancellationTokenSource operationCancellation;
		long revision;

		lock (_stateLock)
		{
			ThrowIfDisposed();

			if ((State != AntigravitySetupWorkflowState.Failed) ||
				_pendingSafetyRevalidation?.CanRevalidateSafety != true)
			{
				return false;
			}

			pendingRevalidation = _pendingSafetyRevalidation;
			_pendingSafetyRevalidation = null;
			_operationCancellation?.Dispose();
			_operationCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken);
			operationCancellation = _operationCancellation;
			revision = ++_revision;
			FailureKind = AntigravityMachineSetupFailureKind.None;
			State = AntigravitySetupWorkflowState.Preparing;
		}

		AntigravityMachineSetupPreparationResult result;

		try
		{
			result = await pendingRevalidation.RevalidateSafetyAsync(
				operationCancellation.Token);
		}
		catch (OperationCanceledException) when (
			operationCancellation.IsCancellationRequested)
		{
			SetCancelledIfCurrent(revision);
			return true;
		}
		catch (Exception)
		{
			SetFailureIfCurrent(
				revision,
				AntigravityMachineSetupFailureKind.UnexpectedFailure);
			return true;
		}
		finally
		{
			lock (_stateLock)
			{
				if (ReferenceEquals(
					_operationCancellation,
					operationCancellation))
				{
					_operationCancellation = null;
				}
			}

			operationCancellation.Dispose();
		}

		await ApplyPreparationResultAsync(result, revision);
		return true;
	}

	private async Task ApplyPreparationResultAsync(
		AntigravityMachineSetupPreparationResult result,
		long revision)
	{
		AntigravityMachineSetupCandidate? candidate = result.Candidate;
		bool shouldKeepCandidate;

		lock (_stateLock)
		{
			shouldKeepCandidate =
				!_isDisposed &&
				(revision == _revision) &&
				(State == AntigravitySetupWorkflowState.Preparing) &&
				result.IsSuccessful &&
				(candidate is not null) &&
				(result.FailureKind ==
					AntigravityMachineSetupFailureKind.None);

			if (shouldKeepCandidate)
			{
				_candidate = candidate;
				_pendingSafetyRevalidation = null;
				FailureKind = AntigravityMachineSetupFailureKind.None;
				State = AntigravitySetupWorkflowState.Reviewing;
			}
			else if (!_isDisposed &&
				(revision == _revision) &&
				(State == AntigravitySetupWorkflowState.Preparing))
			{
				_pendingSafetyRevalidation = result.CanRevalidateSafety
					? result
					: null;
				FailureKind = NormalizeFailure(result.FailureKind);
				State = AntigravitySetupWorkflowState.Failed;
			}
			else if (!_isDisposed &&
				(State == AntigravitySetupWorkflowState.Cancelled) &&
				(result.FailureKind ==
					AntigravityMachineSetupFailureKind.PrivateStorageRejected))
			{
				_pendingSafetyRevalidation = null;
				FailureKind =
					AntigravityMachineSetupFailureKind.PrivateStorageRejected;
				State = AntigravitySetupWorkflowState.Failed;
			}
		}

		if (!shouldKeepCandidate && (candidate is not null))
		{
			if (!await TryDisposeCandidateAsync(candidate))
			{
				SetPrivateStorageFailureUnlessCompleted();
			}
		}
	}

	private async Task<AntigravityMachineSetupPreparationResult>
		PrepareWithTransientRetryAsync(
			AntigravityMachineSetupConsent consent,
			IProgress<AntigravityMachineSetupProgress>? progress,
			CancellationToken cancellationToken)
	{
		for (int attempt = 1; ; attempt++)
		{
			AntigravityMachineSetupPreparationResult result =
				await _setupService.PrepareAsync(
					consent,
					progress,
					cancellationToken);

			if ((result.FailureKind !=
					AntigravityMachineSetupFailureKind.ExecutionBusy) ||
				(result.Candidate is not null) ||
				(attempt >= MaximumPreparationAttempts))
			{
				return result;
			}

			TimeSpan retryDelay = TimeSpan.FromTicks(
				InitialPreparationRetryDelay.Ticks << (attempt - 1));
			await _delayAsync(retryDelay, cancellationToken);
		}
	}

	private async Task PersistApprovalRequestWithTransientRetryAsync(
		AntigravityMachineSetupCandidate candidate,
		CancellationToken cancellationToken)
	{
		if (_beforeApprovalCommitAsync is null)
		{
			return;
		}

		for (int attempt = 1; ; attempt++)
		{
			try
			{
				await _beforeApprovalCommitAsync(
					candidate,
					cancellationToken);
				return;
			}
			catch (Exception exception) when (
				(attempt < MaximumApprovalRequestPersistenceAttempts) &&
				IsTransientApprovalRequestPersistenceFailure(exception))
			{
				long exponentialDelayTicks =
					InitialApprovalRequestPersistenceRetryDelay.Ticks <<
					(attempt - 1);
				TimeSpan retryDelay = TimeSpan.FromTicks(Math.Min(
					exponentialDelayTicks,
					MaximumApprovalRequestPersistenceRetryDelay.Ticks));
				await _delayAsync(retryDelay, cancellationToken);
			}
		}
	}

	private static bool IsTransientApprovalRequestPersistenceFailure(
		Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException;
	}

	internal async Task<bool> ApproveAsync(
		bool isReviewConfirmed,
		CancellationToken cancellationToken = default)
	{
		AntigravityMachineSetupCandidate? candidate;
		CancellationTokenSource operationCancellation;
		long revision;

		lock (_stateLock)
		{
			ThrowIfDisposed();

			if (!isReviewConfirmed ||
				(State != AntigravitySetupWorkflowState.Reviewing) ||
				(_candidate is null))
			{
				return false;
			}

			candidate = _candidate;
			_operationCancellation?.Dispose();
			_operationCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken);
			operationCancellation = _operationCancellation;
			revision = ++_revision;
			FailureKind = AntigravityMachineSetupFailureKind.None;
			State = AntigravitySetupWorkflowState.Approving;
		}

		try
		{
			await PersistApprovalRequestWithTransientRetryAsync(
				candidate,
				operationCancellation.Token);

			lock (_stateLock)
			{
				_hasApprovalStarted = true;
			}

			await candidate.ApproveAsync(operationCancellation.Token);

			if (!candidate.IsApproved)
			{
				throw new InvalidOperationException(
					"Antigravity setup approval did not commit.");
			}

			lock (_stateLock)
			{
				_hasCommittedSetting = true;
			}

			if (!await TryDisposeCandidateAsync(candidate))
			{
				lock (_stateLock)
				{
					if (!_isDisposed &&
						(revision == _revision) &&
						(State == AntigravitySetupWorkflowState.Approving))
					{
						_candidate = null;
						FailureKind =
							AntigravityMachineSetupFailureKind
								.PrivateStorageRejected;
						State = AntigravitySetupWorkflowState.Failed;
					}
				}

				return false;
			}

			if (_afterApprovalCommittedAsync is not null)
			{
				await _afterApprovalCommittedAsync(
					candidate,
					CancellationToken.None);
			}

			lock (_stateLock)
			{
				if (!_isDisposed &&
					(revision == _revision) &&
					(State == AntigravitySetupWorkflowState.Approving))
				{
					_candidate = null;
					State = AntigravitySetupWorkflowState.Completed;
				}
			}

			return true;
		}
		catch (OperationCanceledException) when (
			operationCancellation.IsCancellationRequested)
		{
			lock (_stateLock)
			{
				if (!_isDisposed && (revision == _revision))
				{
					_candidate = null;
					State = AntigravitySetupWorkflowState.Cancelled;
				}
			}

			if (!await TryDisposeCandidateAsync(candidate))
			{
				SetPrivateStorageFailureUnlessCompleted();
			}

			return false;
		}
		catch (Exception)
		{
			lock (_stateLock)
			{
				if (!_isDisposed && (revision == _revision))
				{
					_candidate = null;
					FailureKind =
						AntigravityMachineSetupFailureKind.ApprovalRejected;
					State = AntigravitySetupWorkflowState.Failed;
				}
			}

			if (!await TryDisposeCandidateAsync(candidate))
			{
				SetPrivateStorageFailureUnlessCompleted();
			}

			return false;
		}
		finally
		{
			lock (_stateLock)
			{
				if (ReferenceEquals(
					_operationCancellation,
					operationCancellation))
				{
					_operationCancellation = null;
				}
			}

			operationCancellation.Dispose();
		}
	}

	internal async Task CancelAsync()
	{
		CancellationTokenSource? operationCancellation;
		AntigravityMachineSetupCandidate? candidate;

		lock (_stateLock)
		{
			if (_isDisposed)
			{
				return;
			}

			// The explicit approval click is the commit point. Once that short
			// operation starts, closing waits for it instead of reporting a
			// cancellation after the current-user setting may have been saved.
			if (State == AntigravitySetupWorkflowState.Approving)
			{
				return;
			}

			_revision++;
			operationCancellation = _operationCancellation;
			_operationCancellation = null;
			candidate = _candidate;
			_candidate = null;
			_pendingSafetyRevalidation = null;

			if ((State != AntigravitySetupWorkflowState.Completed) &&
				(State != AntigravitySetupWorkflowState.Failed))
			{
				State = AntigravitySetupWorkflowState.Cancelled;
			}
		}

		try
		{
			operationCancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		if (candidate is not null)
		{
			if (!await TryDisposeCandidateAsync(candidate))
			{
				SetPrivateStorageFailureUnlessCompleted();
			}
		}
	}

	internal void Reset()
	{
		lock (_stateLock)
		{
			ThrowIfDisposed();

			if (State is AntigravitySetupWorkflowState.Preparing or
				AntigravitySetupWorkflowState.Approving ||
				(_candidate is not null))
			{
				throw new InvalidOperationException(
					"Antigravity setup cannot reset while work is pending.");
			}

			FailureKind = AntigravityMachineSetupFailureKind.None;
			_pendingSafetyRevalidation = null;
			State = AntigravitySetupWorkflowState.Ready;
		}
	}

	public async ValueTask DisposeAsync()
	{
		await CancelAsync();

		lock (_stateLock)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			_pendingSafetyRevalidation = null;
			_operationCancellation?.Dispose();
			_operationCancellation = null;
		}
	}

	private static AntigravityMachineSetupFailureKind NormalizeFailure(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return (failureKind != AntigravityMachineSetupFailureKind.None) &&
			Enum.IsDefined(failureKind)
				? failureKind
				: AntigravityMachineSetupFailureKind.UnexpectedFailure;
	}

	private static async Task<bool> TryDisposeCandidateAsync(
		AntigravityMachineSetupCandidate candidate)
	{
		try
		{
			await candidate.DisposeAsync();
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void SetPrivateStorageFailureUnlessCompleted()
	{
		lock (_stateLock)
		{
			if (!_isDisposed &&
				(State != AntigravitySetupWorkflowState.Completed))
			{
				_pendingSafetyRevalidation = null;
				FailureKind =
					AntigravityMachineSetupFailureKind.PrivateStorageRejected;
				State = AntigravitySetupWorkflowState.Failed;
			}
		}
	}

	private void SetCancelledIfCurrent(long revision)
	{
		lock (_stateLock)
		{
			if (!_isDisposed && (revision == _revision))
			{
				_pendingSafetyRevalidation = null;
				State = AntigravitySetupWorkflowState.Cancelled;
			}
		}
	}

	private void SetFailureIfCurrent(
		long revision,
		AntigravityMachineSetupFailureKind failureKind)
	{
		lock (_stateLock)
		{
			if (!_isDisposed && (revision == _revision))
			{
				_pendingSafetyRevalidation = null;
				FailureKind = NormalizeFailure(failureKind);
				State = AntigravitySetupWorkflowState.Failed;
			}
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(
			_isDisposed,
			nameof(AntigravitySetupWorkflow));
	}
}
