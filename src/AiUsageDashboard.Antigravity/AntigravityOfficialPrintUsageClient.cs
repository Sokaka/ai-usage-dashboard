using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.AntigravitySpike;

public interface IAntigravityOfficialPrintUsageClient
{
	Task<AntigravityProductionUsageResult> CaptureAsync(
		string executablePath,
		CancellationToken cancellationToken);

	Task<AntigravityProductionUsageResult> RevalidateAsync(
		string executablePath,
		Action onSafetyTrackedAttemptStarted,
		CancellationToken cancellationToken);
}

internal interface IAntigravityOfficialPrintCapabilityValidationClient
{
	Task<AntigravityOfficialPrintCapabilityValidationResult> ValidateCapabilityAsync(
		string executablePath,
		CancellationToken cancellationToken);
}

public static class AntigravityOfficialPrintTiming
{
	public static readonly TimeSpan SafetyStateOperationTimeout =
		TimeSpan.FromSeconds(10);
	public static readonly TimeSpan ExecutionGateTimeout =
		TimeSpan.FromSeconds(10);
	public static readonly TimeSpan AutomaticRevalidationDelay =
		TimeSpan.FromMinutes(1);
	public static readonly TimeSpan CapabilityValidationTimeout =
		TimeSpan.FromSeconds(60);
	public static readonly TimeSpan CapabilityCommandTimeout =
		TimeSpan.FromSeconds(30);
	public static readonly TimeSpan CapabilityCleanupTimeout =
		TimeSpan.FromSeconds(5);
	public static readonly TimeSpan UsageCommandTimeout =
		TimeSpan.FromMinutes(1);
	public static readonly TimeSpan UsageCleanupTimeout =
		TimeSpan.FromSeconds(5);
	public static readonly TimeSpan MaximumCaptureDuration =
		ExecutionGateTimeout +
		SafetyStateOperationTimeout +
		CapabilityValidationTimeout +
		SafetyStateOperationTimeout +
		UsageCommandTimeout +
		UsageCleanupTimeout +
		SafetyStateOperationTimeout;
}

public sealed record AntigravityOfficialPrintSafetyState(
	AntigravityUsageSafetyFailureReason Reason,
	DateTimeOffset LatchedAtUtc,
	bool AutomaticRevalidationAllowed = false,
	bool HasAttemptedAutomaticRevalidation = false,
	string? AttemptId = null,
	string? DetectedCliVersion = null);

public interface IAntigravityOfficialPrintSafetyStateStore
{
	Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
		CancellationToken cancellationToken);

	Task SaveAsync(
		AntigravityOfficialPrintSafetyState state,
		CancellationToken cancellationToken);

	Task ClearAsync(CancellationToken cancellationToken);
}

internal interface IAntigravityOfficialPrintExecutionGate
{
	Task<IDisposable> AcquireExecutionLeaseAsync(
		CancellationToken cancellationToken);
}

internal enum AntigravityOfficialPrintExecutionGateFailureKind
{
	ContentionTimedOut,
	Unavailable
}

internal sealed class AntigravityOfficialPrintExecutionGateException :
	Exception
{
	internal AntigravityOfficialPrintExecutionGateFailureKind FailureKind {
		get;
	}

	internal AntigravityOfficialPrintExecutionGateException(
		AntigravityOfficialPrintExecutionGateFailureKind failureKind,
		Exception innerException)
		: base(
			"The official AGY execution gate could not be acquired.",
			innerException)
	{
		if (!Enum.IsDefined(failureKind))
		{
			throw new ArgumentOutOfRangeException(nameof(failureKind));
		}

		FailureKind = failureKind;
	}
}

internal sealed class AntigravityOfficialPrintExecutionLeaseScope : IDisposable
{
	private readonly object _syncRoot = new();
	private IDisposable? _lease;
	private List<Task>? _quiescenceTasks;
	private bool _isDisposed;

	internal AntigravityOfficialPrintExecutionLeaseScope(IDisposable lease)
	{
		_lease = lease ?? throw new ArgumentNullException(nameof(lease));
	}

	internal void RetainUntil(Task quiescenceTask)
	{
		ArgumentNullException.ThrowIfNull(quiescenceTask);

		lock (_syncRoot)
		{
			if (_isDisposed)
			{
				throw new ObjectDisposedException(GetType().Name);
			}

			if (!quiescenceTask.IsCompletedSuccessfully)
			{
				(_quiescenceTasks ??= new List<Task>()).Add(quiescenceTask);
			}
		}
	}

	public void Dispose()
	{
		IDisposable? lease;
		Task[] quiescenceTasks;

		lock (_syncRoot)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			lease = _lease;
			_lease = null;
			quiescenceTasks = _quiescenceTasks?.ToArray() ?? Array.Empty<Task>();
			_quiescenceTasks = null;
		}

		if (lease is null)
		{
			return;
		}

		if (quiescenceTasks.Length == 0)
		{
			lease.Dispose();
			return;
		}

		_ = ReleaseAfterQuiescenceAsync(lease, quiescenceTasks);
	}

	private static async Task ReleaseAfterQuiescenceAsync(
		IDisposable lease,
		IReadOnlyCollection<Task> quiescenceTasks)
	{
		try
		{
			await Task.WhenAll(quiescenceTasks);
		}
		catch
		{
			// Completion, rather than success, is the lock-release condition.
		}
		finally
		{
			try
			{
				lease.Dispose();
			}
			catch
			{
				// A late lease-disposal failure must not become unobserved.
			}
		}
	}
}

internal sealed class AntigravityOfficialPrintProcessResult : IDisposable
{
	private byte[]? _stdout;

	internal int ExitCode { get; }

	internal bool HasStderr { get; }

	internal bool StderrLimitExceeded { get; }

	internal ReadOnlyMemory<byte> Stdout =>
		Volatile.Read(ref _stdout) ?? ReadOnlyMemory<byte>.Empty;

	internal bool StdoutLimitExceeded { get; }

	internal AntigravityOfficialPrintProcessResult(
		int exitCode,
		byte[] stdout,
		bool stdoutLimitExceeded,
		bool hasStderr,
		bool stderrLimitExceeded)
	{
		ArgumentNullException.ThrowIfNull(stdout);
		ExitCode = exitCode;
		_stdout = stdout;
		StdoutLimitExceeded = stdoutLimitExceeded;
		HasStderr = hasStderr;
		StderrLimitExceeded = stderrLimitExceeded;
	}

	public void Dispose()
	{
		byte[]? stdout = Interlocked.Exchange(ref _stdout, null);

		if (stdout is not null)
		{
			CryptographicOperations.ZeroMemory(stdout);
		}
	}
}

internal interface IAntigravityOfficialPrintProcessRunner
{
	Task<AntigravityOfficialPrintProcessResult> RunAsync(
		string executablePath,
		CancellationToken cancellationToken);

	async Task<AntigravityOfficialPrintProcessResult> RunAsync(
		string executablePath,
		string attemptId,
		Func<CancellationToken, Task> beforeStartAsync,
		CancellationToken cancellationToken)
	{
		await beforeStartAsync(cancellationToken);
		return await RunAsync(executablePath, cancellationToken);
	}

	Task<bool> TryRecoverInterruptedAttemptAsync(
		string attemptId,
		CancellationToken cancellationToken)
	{
		return Task.FromResult(false);
	}
}

internal sealed class AntigravityOfficialPrintProcessRunException : Exception
{
	private static readonly Task NeverCompletingQuiescenceTask =
		new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously).Task;

	internal bool WasProcessStarted { get; }

	internal bool WasTerminationConfirmed { get; }

	internal Task QuiescenceTask { get; }

	internal Task PositiveQuiescenceTask { get; }

	internal AntigravityOfficialPrintProcessRunException(
		bool wasProcessStarted,
		Exception innerException,
		bool wasTerminationConfirmed = false,
		Task? quiescenceTask = null,
		Task? positiveQuiescenceTask = null)
		: base(
			"The official AGY print process did not complete safely.",
			innerException)
	{
		WasProcessStarted = wasProcessStarted;
		WasTerminationConfirmed = wasTerminationConfirmed;
		QuiescenceTask = quiescenceTask ??
			(wasProcessStarted && !wasTerminationConfirmed
				? NeverCompletingQuiescenceTask
				: Task.CompletedTask);
		// Existing process-runner fakes use the containment task itself as their
		// eventual positive signal. Real bounded cleanup supplies a separate
		// evidence task so kill-on-close completion cannot be mistaken for a
		// positively observed empty process tree.
		PositiveQuiescenceTask = positiveQuiescenceTask ?? QuiescenceTask;
	}
}

public sealed class AntigravityOfficialPrintUsageClient :
	IAntigravityOfficialPrintUsageClient,
	IAntigravityOfficialPrintCapabilityValidationClient
{
	public const string LocalSessionIdentity = "agy.local-session.v1";

	private readonly SemaphoreSlim _captureGate = new(1, 1);
	private readonly ConcurrentDictionary<
		string,
		AntigravityOfficialPrintSafetyState> _safetyLatchedExecutables =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Func<
		string,
		CancellationToken,
		Task<AntigravityOfficialPrintCapabilityValidationResult>>
		_validateCapabilityAsync;
	private readonly IAntigravityOfficialPrintProcessRunner _processRunner;
	private readonly IAntigravityOfficialPrintSafetyStateStore? _safetyStateStore;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _executionGateTimeout;

	public AntigravityOfficialPrintUsageClient()
		: this(safetyStateStore: null)
	{
	}

	public AntigravityOfficialPrintUsageClient(
		IAntigravityOfficialPrintSafetyStateStore? safetyStateStore)
		: this(
			(executablePath, cancellationToken) =>
				new AntigravityOfficialPrintCapabilityValidator().ValidateAsync(
					executablePath,
					cancellationToken),
			new WindowsAntigravityOfficialPrintProcessRunner(),
			TimeProvider.System,
			safetyStateStore,
			executionGateTimeout: null)
	{
	}

	internal AntigravityOfficialPrintUsageClient(
		Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintCapabilityValidationResult>>
			validateCapabilityAsync,
		IAntigravityOfficialPrintProcessRunner processRunner,
		TimeProvider? timeProvider = null,
		IAntigravityOfficialPrintSafetyStateStore? safetyStateStore = null,
		TimeSpan? executionGateTimeout = null)
	{
		_validateCapabilityAsync = validateCapabilityAsync ??
			throw new ArgumentNullException(nameof(validateCapabilityAsync));
		_processRunner = processRunner ??
			throw new ArgumentNullException(nameof(processRunner));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_safetyStateStore = safetyStateStore;
		_executionGateTimeout = executionGateTimeout ??
			AntigravityOfficialPrintTiming.ExecutionGateTimeout;

		if (_executionGateTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(executionGateTimeout));
		}
	}

	public async Task<AntigravityProductionUsageResult> CaptureAsync(
		string executablePath,
		CancellationToken cancellationToken)
	{
		return await CaptureCoreAsync(
			executablePath,
			allowSingleRevalidation: false,
			onSafetyTrackedAttemptStarted: null,
			cancellationToken);
	}

	public async Task<AntigravityProductionUsageResult> RevalidateAsync(
		string executablePath,
		Action onSafetyTrackedAttemptStarted,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(onSafetyTrackedAttemptStarted);
		return await CaptureCoreAsync(
			executablePath,
			allowSingleRevalidation: true,
			onSafetyTrackedAttemptStarted,
			cancellationToken);
	}

	async Task<AntigravityOfficialPrintCapabilityValidationResult>
		IAntigravityOfficialPrintCapabilityValidationClient.ValidateCapabilityAsync(
			string executablePath,
			CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryNormalizeExecutablePath(
			executablePath,
			out string normalizedExecutablePath))
		{
			return new AntigravityOfficialPrintCapabilityValidationResult(
				isSupported: false);
		}

		await _captureGate.WaitAsync(cancellationToken);
		AntigravityOfficialPrintExecutionLeaseScope? executionLease = null;

		try
		{
			try
			{
				executionLease = await AcquireExecutionLeaseWithinBudgetAsync(
					cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (AntigravityOfficialPrintExecutionGateException)
			{
				throw;
			}
			catch
			{
				return new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false);
			}

			try
			{
				return await ValidateCapabilityWithinBudgetAsync(
					normalizedExecutablePath,
					cancellationToken,
					executionLease);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				return new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false);
			}
		}
		finally
		{
			try
			{
				executionLease?.Dispose();
			}
			finally
			{
				_captureGate.Release();
			}
		}
	}

	private async Task<AntigravityProductionUsageResult> CaptureCoreAsync(
		string executablePath,
		bool allowSingleRevalidation,
		Action? onSafetyTrackedAttemptStarted,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryNormalizeExecutablePath(
			executablePath,
			out string normalizedExecutablePath))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.ProvenanceRejected);
		}

		await _captureGate.WaitAsync(cancellationToken);
		AntigravityOfficialPrintExecutionLeaseScope? executionLease = null;

		try
		{
			try
			{
				executionLease = await AcquireExecutionLeaseWithinBudgetAsync(
					cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (AntigravityOfficialPrintExecutionGateException exception)
			{
				return exception.FailureKind ==
					AntigravityOfficialPrintExecutionGateFailureKind
						.ContentionTimedOut
					? Failure(
						AntigravityProductionUsageFailureKind.ExecutionBusy)
					: Failure(
						AntigravityProductionUsageFailureKind.SafetyLatched,
						AntigravityUsageSafetyFailureReason
							.SafetyStateUnavailable,
						automaticRevalidationPending: true);
			}

			bool isAutomaticRevalidation = false;
			bool hasBegunSafetyTrackedAttempt = false;
			string? activeAttemptId = null;
			AntigravityOfficialPrintSafetyState? existingLatch =
				await TryGetExistingSafetyStateAsync(
					normalizedExecutablePath,
					cancellationToken,
					executionLease);
			bool preserveExplicitUsageActivityLatch =
				allowSingleRevalidation &&
				existingLatch?.Reason ==
					AntigravityUsageSafetyFailureReason.UsageActivityDetected;

			if (!allowSingleRevalidation)
			{
				if (existingLatch is not null)
				{
					if (RequiresInterruptedAttemptRecovery(existingLatch) &&
						!existingLatch.AutomaticRevalidationAllowed)
					{
						if (!await TryRecoverInterruptedAttemptAsync(
								existingLatch,
								cancellationToken))
						{
							return CreateExistingSafetyLatchFailure(existingLatch);
						}

						return await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					if (!CanAutomaticallyRevalidate(existingLatch))
					{
						return CreateExistingSafetyLatchFailure(existingLatch);
					}

					if (HasFutureAutomaticRevalidationTimestamp(existingLatch))
					{
						return await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					if (!IsAutomaticRevalidationDue(existingLatch))
					{
						return CreateExistingSafetyLatchFailure(existingLatch);
					}

					isAutomaticRevalidation = true;

					if (RequiresInterruptedAttemptRecovery(existingLatch) &&
						!await TryRecoverInterruptedAttemptAsync(
							existingLatch,
							cancellationToken))
					{
						return await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					activeAttemptId = await TryBeginSafetyTrackedAttemptAsync(
							normalizedExecutablePath,
							automaticRecoveryAfterInterruptionAllowed: true,
							hasAttemptedAutomaticRevalidation: true,
							executionLease);

					if (activeAttemptId is null)
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending:
								!allowSingleRevalidation);
					}

					hasBegunSafetyTrackedAttempt = true;
				}
			}

			AntigravityOfficialPrintCapabilityValidationResult validation;

			try
			{
				validation = await ValidateCapabilityWithinBudgetAsync(
					normalizedExecutablePath,
					cancellationToken,
					executionLease);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				if (isAutomaticRevalidation && existingLatch is not null)
				{
					await RescheduleAutomaticRevalidationAsync(
						normalizedExecutablePath,
						existingLatch,
						executionLease);
				}

				throw;
			}
			catch
			{
				if (isAutomaticRevalidation && existingLatch is not null)
				{
					return await RescheduleAutomaticRevalidationAsync(
						normalizedExecutablePath,
						existingLatch,
						executionLease);
				}

				return Failure(
					AntigravityProductionUsageFailureKind.ProvenanceRejected);
			}

			if (validation is null)
			{
				if (isAutomaticRevalidation && existingLatch is not null)
				{
					return await RescheduleAutomaticRevalidationAsync(
						normalizedExecutablePath,
						existingLatch,
						executionLease);
				}

				return Failure(
					AntigravityProductionUsageFailureKind.ProvenanceRejected);
			}

			using (validation)
			{
				if (!validation.IsSupported ||
					(validation.ExecutableLease is null))
				{
					if (isAutomaticRevalidation &&
						!await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease))
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending: true);
					}

					return Failure(
						AntigravityProductionUsageFailureKind.ProvenanceRejected);
				}

				if (!hasBegunSafetyTrackedAttempt)
				{
					activeAttemptId = await TryBeginSafetyTrackedAttemptAsync(
						normalizedExecutablePath,
						automaticRecoveryAfterInterruptionAllowed:
							!allowSingleRevalidation,
						hasAttemptedAutomaticRevalidation:
							isAutomaticRevalidation,
						executionLease);

					if (activeAttemptId is null)
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending:
								!allowSingleRevalidation);
					}

					hasBegunSafetyTrackedAttempt = true;
				}

				onSafetyTrackedAttemptStarted?.Invoke();

				AntigravityOfficialPrintProcessResult processResult;

				try
				{
					processResult = await _processRunner.RunAsync(
						normalizedExecutablePath,
						activeAttemptId!,
						async token =>
						{
							await MarkSafetyTrackedAttemptAsStartedAsync(
								normalizedExecutablePath,
								activeAttemptId!,
								executionLease);
						},
						cancellationToken);
				}
				catch (AntigravityOfficialPrintProcessRunException exception) when (
					!exception.WasProcessStarted &&
					(exception.InnerException is OperationCanceledException) &&
					cancellationToken.IsCancellationRequested)
				{
					RetainProcessQuiescence(
						validation,
						executionLease,
						exception.QuiescenceTask);
					if (existingLatch is not null)
					{
						if (isAutomaticRevalidation)
						{
							await RescheduleAutomaticRevalidationAsync(
								normalizedExecutablePath,
								existingLatch,
								executionLease);
						}
						else
						{
							await TryRestoreSafetyStateAsync(
								normalizedExecutablePath,
								existingLatch,
								executionLease);
						}
					}
					else
					{
						await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease);
					}
					throw new OperationCanceledException(
						"The official AGY print process was canceled before command execution began.",
						exception.InnerException,
						cancellationToken);
				}
				catch (AntigravityOfficialPrintProcessRunException exception) when (
					exception.WasProcessStarted &&
					(exception.InnerException is OperationCanceledException) &&
					cancellationToken.IsCancellationRequested)
				{
					RetainProcessQuiescence(
						validation,
						executionLease,
						exception.QuiescenceTask);

					if (preserveExplicitUsageActivityLatch &&
						existingLatch is not null)
					{
						await TryRestoreSafetyStateAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
						throw new OperationCanceledException(
							"The official AGY print process was canceled after it started.",
							exception.InnerException,
							cancellationToken);
					}

					string attemptId = CreateAttemptId();
					bool canContinueAutomaticRevalidation =
						exception.WasTerminationConfirmed;
					await LatchSafetyAsync(
						normalizedExecutablePath,
						AntigravityUsageSafetyFailureReason.CanceledAfterStart,
						automaticRevalidationAllowed:
							canContinueAutomaticRevalidation,
						hasAttemptedAutomaticRevalidation:
							canContinueAutomaticRevalidation
								? false
								: isAutomaticRevalidation,
						executionLease,
						attemptId);

					if (!exception.WasTerminationConfirmed)
					{
						ScheduleAutomaticRevalidationAfterQuiescence(
							normalizedExecutablePath,
							attemptId,
							AntigravityUsageSafetyFailureReason.CanceledAfterStart,
							exception.PositiveQuiescenceTask);
					}
					throw new OperationCanceledException(
						"The official AGY print process was canceled after it started.",
						exception.InnerException,
						cancellationToken);
				}
				catch (AntigravityOfficialPrintProcessRunException exception) when (
					exception.WasProcessStarted)
				{
					RetainProcessQuiescence(
						validation,
						executionLease,
						exception.QuiescenceTask);

					if (preserveExplicitUsageActivityLatch &&
						existingLatch is not null)
					{
						return await RestoreExistingSafetyLatchFailureAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					bool isTimeout =
						exception.InnerException is TimeoutException;
					bool canContinueAutomaticRevalidation =
						exception.WasTerminationConfirmed;
					AntigravityUsageSafetyFailureReason reason =
						isTimeout && exception.WasTerminationConfirmed
							? AntigravityUsageSafetyFailureReason.TimedOut
							: AntigravityUsageSafetyFailureReason
								.ProcessFailedAfterStart;
					string attemptId = CreateAttemptId();
					AntigravityProductionUsageResult failure =
						await LatchSafetyAsync(
							normalizedExecutablePath,
							reason,
							automaticRevalidationAllowed:
								canContinueAutomaticRevalidation,
							hasAttemptedAutomaticRevalidation:
								canContinueAutomaticRevalidation
									? false
									: isAutomaticRevalidation,
							executionLease,
							attemptId);

					if (!exception.WasTerminationConfirmed)
					{
						ScheduleAutomaticRevalidationAfterQuiescence(
							normalizedExecutablePath,
							attemptId,
							isTimeout
								? AntigravityUsageSafetyFailureReason.TimedOut
								: AntigravityUsageSafetyFailureReason
									.ProcessFailedAfterStart,
							exception.PositiveQuiescenceTask);
					}

					return failure;
				}
				catch (AntigravityOfficialPrintProcessRunException exception)
				{
					RetainProcessQuiescence(
						validation,
						executionLease,
						exception.QuiescenceTask);

					if (isAutomaticRevalidation && existingLatch is not null)
					{
						return await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					bool restoredOrCleared = existingLatch is not null
						? await TryRestoreSafetyStateAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease)
						: await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease);

					if (!restoredOrCleared)
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending:
								existingLatch is null);
					}

					return Failure(
						AntigravityProductionUsageFailureKind.CaptureRejected);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					if (isAutomaticRevalidation && existingLatch is not null)
					{
						await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}
					else if (existingLatch is not null)
					{
						await TryRestoreSafetyStateAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}
					else
					{
						await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease);
					}
					throw;
				}
				catch
				{
					// The production runner wraps every failure after CreateProcess
					// returns a handle. A raw exception therefore occurred before the
					// usage command was created.
					if (isAutomaticRevalidation && existingLatch is not null)
					{
						return await RescheduleAutomaticRevalidationAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					bool restoredOrCleared = existingLatch is not null
						? await TryRestoreSafetyStateAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease)
						: await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease);

					if (!restoredOrCleared)
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending:
								existingLatch is null);
					}

					return Failure(
						AntigravityProductionUsageFailureKind.CaptureRejected);
				}

				if (processResult is null)
				{
					if (preserveExplicitUsageActivityLatch &&
						existingLatch is not null)
					{
						return await RestoreExistingSafetyLatchFailureAsync(
							normalizedExecutablePath,
							existingLatch,
							executionLease);
					}

					return await LatchSafetyAsync(
						normalizedExecutablePath,
						AntigravityUsageSafetyFailureReason
							.ProcessFailedAfterStart,
						automaticRevalidationAllowed: false,
						isAutomaticRevalidation,
						executionLease);
				}

				using (processResult)
				{
					AntigravityUsageSafetyFailureReason resultFailureReason =
						GetProcessResultSafetyFailureReason(processResult);

					if (resultFailureReason !=
						AntigravityUsageSafetyFailureReason.None)
					{
						if (HasExplicitUsageActivity(processResult))
						{
							resultFailureReason =
								AntigravityUsageSafetyFailureReason
									.UsageActivityDetected;
						}

						if (preserveExplicitUsageActivityLatch &&
							existingLatch is not null)
						{
							return await RestoreExistingSafetyLatchFailureAsync(
								normalizedExecutablePath,
								existingLatch,
								executionLease);
						}

						bool canContinueAutomaticRevalidation =
							AntigravityAutomaticRecoveryPolicy
								.IsRecoverableReason(resultFailureReason);
						return await LatchSafetyAsync(
							normalizedExecutablePath,
							resultFailureReason,
							automaticRevalidationAllowed:
								canContinueAutomaticRevalidation,
							hasAttemptedAutomaticRevalidation:
								canContinueAutomaticRevalidation
									? false
									: isAutomaticRevalidation,
							executionLease,
							versionAssessment: validation.VersionAssessment);
					}

					AntigravityOfficialPrintParseResult parsed;

					try
					{
						parsed = AntigravityOfficialPrintEnvelopeParser.Parse(
							processResult.Stdout,
							_timeProvider.GetUtcNow());
					}
					catch
					{
						if (preserveExplicitUsageActivityLatch &&
							existingLatch is not null)
						{
							return await RestoreExistingSafetyLatchFailureAsync(
								normalizedExecutablePath,
								existingLatch,
								executionLease);
						}

						AntigravityUsageSafetyFailureReason reason =
							AntigravityUsageSafetyFailureReason.UnverifiableOutput;
						bool canContinueAutomaticRevalidation =
							AntigravityAutomaticRecoveryPolicy
								.IsRecoverableReason(reason);
						return await LatchSafetyAsync(
							normalizedExecutablePath,
							reason,
							automaticRevalidationAllowed:
								canContinueAutomaticRevalidation,
							hasAttemptedAutomaticRevalidation:
								canContinueAutomaticRevalidation
									? false
									: isAutomaticRevalidation,
							executionLease,
							versionAssessment: validation.VersionAssessment);
					}

					if (parsed.RequiresSafetyLatch || !parsed.Result.IsSuccessful)
					{
						if (preserveExplicitUsageActivityLatch &&
							existingLatch is not null)
						{
							return await RestoreExistingSafetyLatchFailureAsync(
								normalizedExecutablePath,
								existingLatch,
								executionLease);
						}

						AntigravityUsageSafetyFailureReason reason =
							parsed.SafetyFailureReason ==
								AntigravityUsageSafetyFailureReason.None
								? AntigravityUsageSafetyFailureReason
									.UnverifiableOutput
								: parsed.SafetyFailureReason;
						bool canContinueAutomaticRevalidation =
							AntigravityAutomaticRecoveryPolicy
								.IsRecoverableReason(reason);
						return await LatchSafetyAsync(
							normalizedExecutablePath,
							reason,
							automaticRevalidationAllowed:
								canContinueAutomaticRevalidation,
							hasAttemptedAutomaticRevalidation:
								canContinueAutomaticRevalidation
									? false
									: isAutomaticRevalidation,
							executionLease,
							versionAssessment: validation.VersionAssessment);
					}

					if (!await TryClearSafetyStateAsync(
							normalizedExecutablePath,
							executionLease))
					{
						return Failure(
							AntigravityProductionUsageFailureKind.SafetyLatched,
							AntigravityUsageSafetyFailureReason
								.SafetyStateUnavailable,
							automaticRevalidationPending: true);
					}

					return parsed.Result;
				}
			}
		}
		finally
		{
			try
			{
				executionLease?.Dispose();
			}
			finally
			{
				_captureGate.Release();
			}
		}
	}

	private async Task<AntigravityOfficialPrintExecutionLeaseScope?>
		AcquireExecutionLeaseWithinBudgetAsync(
			CancellationToken cancellationToken)
	{
		if (_safetyStateStore is not
			IAntigravityOfficialPrintExecutionGate executionGate)
		{
			return null;
		}

		using CancellationTokenSource timeoutSource =
			new(_executionGateTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			IDisposable lease =
				await executionGate.AcquireExecutionLeaseAsync(
					linkedSource.Token);
			return new AntigravityOfficialPrintExecutionLeaseScope(lease);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException exception) when (
			timeoutSource.IsCancellationRequested)
		{
			throw new AntigravityOfficialPrintExecutionGateException(
				AntigravityOfficialPrintExecutionGateFailureKind
					.ContentionTimedOut,
				new TimeoutException(
					"The official AGY execution gate remained busy.",
					exception));
		}
		catch (AntigravityOfficialPrintExecutionGateException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new AntigravityOfficialPrintExecutionGateException(
				AntigravityOfficialPrintExecutionGateFailureKind.Unavailable,
				exception);
		}
	}

	private async Task<AntigravityOfficialPrintSafetyState?>
		TryGetExistingSafetyStateAsync(
			string normalizedExecutablePath,
			CancellationToken cancellationToken,
			AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		if (_safetyStateStore is null)
		{
			return _safetyLatchedExecutables.TryGetValue(
				normalizedExecutablePath,
				out AntigravityOfficialPrintSafetyState? inMemoryState)
					? inMemoryState
					: null;
		}

		try
		{
			AntigravityOfficialPrintSafetyState? state =
				await RunSafetyStateOperationWithinBudgetAsync(
					token => _safetyStateStore.LoadAsync(token),
					cancellationToken,
					executionLease);

			if (state is null)
			{
				_safetyLatchedExecutables.TryRemove(
					normalizedExecutablePath,
					out _);
				return null;
			}

			if (!IsValidSafetyState(state))
			{
				return LatchStateInMemory(
					normalizedExecutablePath,
					CreateSafetyState(
						AntigravityUsageSafetyFailureReason.Unknown,
						automaticRevalidationAllowed: false,
						hasAttemptedAutomaticRevalidation: true));
			}

			return LatchStateInMemory(normalizedExecutablePath, state);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException)
		{
			return LatchStateInMemory(
				normalizedExecutablePath,
				CreateSafetyState(
					AntigravityUsageSafetyFailureReason.Unknown,
					automaticRevalidationAllowed: false,
					hasAttemptedAutomaticRevalidation: true));
		}
		catch
		{
			return LatchStateInMemory(
				normalizedExecutablePath,
				CreateSafetyState(
					AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
					automaticRevalidationAllowed: true,
					hasAttemptedAutomaticRevalidation: false));
		}
	}

	private async Task<string?> TryBeginSafetyTrackedAttemptAsync(
		string normalizedExecutablePath,
		bool automaticRecoveryAfterInterruptionAllowed,
		bool hasAttemptedAutomaticRevalidation,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		AntigravityUsageSafetyFailureReason reason =
			AntigravityUsageSafetyFailureReason.AttemptInterrupted;
		string attemptId = CreateAttemptId();
		AntigravityOfficialPrintSafetyState state = CreateSafetyState(
			reason,
			automaticRevalidationAllowed:
				automaticRecoveryAfterInterruptionAllowed,
			hasAttemptedAutomaticRevalidation,
			attemptId);
		_safetyLatchedExecutables[normalizedExecutablePath] = state;

		if (_safetyStateStore is null)
		{
			return attemptId;
		}

		try
		{
			await RunSafetyStateOperationWithinBudgetAsync(
				token => _safetyStateStore.SaveAsync(
					state,
					token),
				CancellationToken.None,
				executionLease);
			return attemptId;
		}
		catch
		{
			return null;
		}
	}

	private async Task MarkSafetyTrackedAttemptAsStartedAsync(
		string normalizedExecutablePath,
		string attemptId,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		if (!_safetyLatchedExecutables.TryGetValue(
				normalizedExecutablePath,
				out AntigravityOfficialPrintSafetyState? preparedState) ||
			!string.Equals(
				preparedState.AttemptId,
				attemptId,
				StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"The prepared AGY safety marker is unavailable before process creation.");
		}

		AntigravityOfficialPrintSafetyState startedState = preparedState with
		{
			AutomaticRevalidationAllowed = false
		};

		if (_safetyStateStore is not null)
		{
			await RunSafetyStateOperationWithinBudgetAsync(
				token => _safetyStateStore.SaveAsync(startedState, token),
				CancellationToken.None,
				executionLease);
		}

		_safetyLatchedExecutables[normalizedExecutablePath] = startedState;
	}

	private async Task<AntigravityProductionUsageResult> LatchSafetyAsync(
		string normalizedExecutablePath,
		AntigravityUsageSafetyFailureReason reason,
		bool automaticRevalidationAllowed,
		bool hasAttemptedAutomaticRevalidation,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease,
		string? attemptId = null,
		AntigravityOfficialPrintVersionAssessment? versionAssessment = null)
	{
		reason = NormalizeSafetyFailureReason(reason);
		string? detectedCliVersion =
			AntigravityOfficialPrintVersionDiagnosticPolicy
				.SelectDetectedVersionForPersistence(reason, versionAssessment);
		AntigravityOfficialPrintSafetyState state = CreateSafetyState(
			reason,
			automaticRevalidationAllowed,
			hasAttemptedAutomaticRevalidation,
			attemptId,
			detectedCliVersion);
		_safetyLatchedExecutables[normalizedExecutablePath] = state;

		if (_safetyStateStore is not null)
		{
			try
			{
				await RunSafetyStateOperationWithinBudgetAsync(
					token => _safetyStateStore.SaveAsync(
						state,
						token),
					CancellationToken.None,
					executionLease);
			}
			catch
			{
				_safetyLatchedExecutables[normalizedExecutablePath] =
					CreateSafetyState(
						AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
						automaticRevalidationAllowed: false,
						hasAttemptedAutomaticRevalidation: true);
				return Failure(
					AntigravityProductionUsageFailureKind.SafetyLatched,
					AntigravityUsageSafetyFailureReason.SafetyStateUnavailable);
			}
		}

		return CreateExistingSafetyLatchFailure(state);
	}

	private async Task<bool> TryRestoreSafetyStateAsync(
		string normalizedExecutablePath,
		AntigravityOfficialPrintSafetyState state,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		if (_safetyStateStore is not null)
		{
			try
			{
				await RunSafetyStateOperationWithinBudgetAsync(
					token => _safetyStateStore.SaveAsync(state, token),
					CancellationToken.None,
					executionLease);
			}
			catch
			{
				// TryBeginSafetyTrackedAttemptAsync already persisted a
				// durable AttemptInterrupted marker. Keep its current safety
				// boundary if the original automatic eligibility cannot be restored.
				return false;
			}
		}

		_safetyLatchedExecutables[normalizedExecutablePath] = state;
		return true;
	}

	private async Task<AntigravityProductionUsageResult>
		RestoreExistingSafetyLatchFailureAsync(
			string normalizedExecutablePath,
			AntigravityOfficialPrintSafetyState existingLatch,
			AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		return await TryRestoreSafetyStateAsync(
				normalizedExecutablePath,
				existingLatch,
				executionLease)
			? CreateExistingSafetyLatchFailure(existingLatch)
			: Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.SafetyStateUnavailable);
	}

	private async Task<AntigravityProductionUsageResult>
		RescheduleAutomaticRevalidationAsync(
			string normalizedExecutablePath,
			AntigravityOfficialPrintSafetyState existingLatch,
			AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		AntigravityOfficialPrintSafetyState rescheduledState = CreateSafetyState(
			existingLatch.Reason,
			automaticRevalidationAllowed: true,
			hasAttemptedAutomaticRevalidation: false,
			existingLatch.AttemptId,
			existingLatch.DetectedCliVersion);

		return await TryRestoreSafetyStateAsync(
				normalizedExecutablePath,
				rescheduledState,
				executionLease)
			? CreateExistingSafetyLatchFailure(rescheduledState)
			: Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
				automaticRevalidationPending: true);
	}

	private async Task<bool> TryClearSafetyStateAsync(
		string normalizedExecutablePath,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		if (_safetyStateStore is not null)
		{
			try
			{
				await RunSafetyStateOperationWithinBudgetAsync(
					_safetyStateStore.ClearAsync,
					CancellationToken.None,
					executionLease);
			}
			catch
			{
				AntigravityOfficialPrintSafetyState unavailableState =
					CreateSafetyState(
						AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
						automaticRevalidationAllowed: true,
						hasAttemptedAutomaticRevalidation: false);
				_safetyLatchedExecutables[normalizedExecutablePath] =
					unavailableState;

				try
				{
					await RunSafetyStateOperationWithinBudgetAsync(
						token => _safetyStateStore.SaveAsync(
							unavailableState,
							token),
						CancellationToken.None,
						executionLease);
				}
				catch
				{
					// Fail closed in memory if the store cannot be repaired.
				}

				return false;
			}
		}

		_safetyLatchedExecutables.TryRemove(normalizedExecutablePath, out _);
		return true;
	}

	private async Task<AntigravityOfficialPrintCapabilityValidationResult>
		ValidateCapabilityWithinBudgetAsync(
			string normalizedExecutablePath,
			CancellationToken cancellationToken,
			AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		CancellationTokenSource? timeoutSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(
			AntigravityOfficialPrintTiming.CapabilityValidationTimeout);
		Task<AntigravityOfficialPrintCapabilityValidationResult> validationTask =
			_validateCapabilityAsync(
				normalizedExecutablePath,
				timeoutSource.Token);

		try
		{
			try
			{
				return await validationTask.WaitAsync(
					AntigravityOfficialPrintTiming.CapabilityValidationTimeout,
					cancellationToken);
			}
			catch (TimeoutException)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateCapabilityValidationAsync(
					validationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateCapabilityValidationAsync(
					validationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
		}
		finally
		{
			timeoutSource?.Dispose();
		}
	}

	private static async Task ObserveLateCapabilityValidationAsync(
		Task<AntigravityOfficialPrintCapabilityValidationResult> validationTask,
		CancellationTokenSource timeoutSource)
	{
		try
		{
			using AntigravityOfficialPrintCapabilityValidationResult result =
				await validationTask;
		}
		catch
		{
			// A timed-out validation is display-safe and must only release any
			// late executable lease when its non-cancelable OS inspection returns.
		}
		finally
		{
			timeoutSource.Dispose();
		}
	}

	private static async Task<T> RunSafetyStateOperationWithinBudgetAsync<T>(
		Func<CancellationToken, Task<T>> operation,
		CancellationToken cancellationToken,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		CancellationTokenSource? timeoutSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(
			AntigravityOfficialPrintTiming.SafetyStateOperationTimeout);
		Task<T> operationTask = operation(timeoutSource.Token);

		try
		{
			try
			{
				return await operationTask.WaitAsync(
					AntigravityOfficialPrintTiming.SafetyStateOperationTimeout,
					cancellationToken);
			}
			catch (TimeoutException)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateOperationAsync(
					operationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateOperationAsync(
					operationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
		}
		finally
		{
			timeoutSource?.Dispose();
		}
	}

	private static async Task RunSafetyStateOperationWithinBudgetAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease)
	{
		CancellationTokenSource? timeoutSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(
			AntigravityOfficialPrintTiming.SafetyStateOperationTimeout);
		Task operationTask = operation(timeoutSource.Token);

		try
		{
			try
			{
				await operationTask.WaitAsync(
					AntigravityOfficialPrintTiming.SafetyStateOperationTimeout,
					cancellationToken);
			}
			catch (TimeoutException)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateOperationAsync(
					operationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				TryCancel(timeoutSource);
				Task observerTask = ObserveLateOperationAsync(
					operationTask,
					timeoutSource);
				timeoutSource = null;
				executionLease?.RetainUntil(observerTask);
				throw;
			}
		}
		finally
		{
			timeoutSource?.Dispose();
		}
	}

	private static async Task ObserveLateOperationAsync(
		Task operationTask,
		CancellationTokenSource timeoutSource)
	{
		try
		{
			await operationTask;
		}
		catch
		{
			// Observe a late storage failure after the bounded caller moved on.
		}
		finally
		{
			timeoutSource.Dispose();
		}
	}

	private static void TryCancel(CancellationTokenSource source)
	{
		try
		{
			source.Cancel();
		}
		catch
		{
			// The operation is already ending.
		}
	}

	private static void RetainProcessQuiescence(
		AntigravityOfficialPrintCapabilityValidationResult validation,
		AntigravityOfficialPrintExecutionLeaseScope? executionLease,
		Task containmentCompletionTask)
	{
		Task executableLeaseQuiescence =
			validation.RetainExecutableLeaseUntil(containmentCompletionTask);
		executionLease?.RetainUntil(executableLeaseQuiescence);
	}

	private void ScheduleAutomaticRevalidationAfterQuiescence(
		string normalizedExecutablePath,
		string attemptId,
		AntigravityUsageSafetyFailureReason recoveredReason,
		Task positiveQuiescenceTask)
	{
		_ = PromoteSafetyStateAfterQuiescenceAsync(
			normalizedExecutablePath,
			attemptId,
			recoveredReason,
			positiveQuiescenceTask);
	}

	private async Task PromoteSafetyStateAfterQuiescenceAsync(
		string normalizedExecutablePath,
		string attemptId,
		AntigravityUsageSafetyFailureReason recoveredReason,
		Task positiveQuiescenceTask)
	{
		try
		{
			await positiveQuiescenceTask;
		}
		catch
		{
			// A faulted or canceled task is not positive proof of quiescence.
			return;
		}

		await _captureGate.WaitAsync(CancellationToken.None);
		AntigravityOfficialPrintExecutionLeaseScope? executionLease = null;

		try
		{
			try
			{
				executionLease = await AcquireExecutionLeaseWithinBudgetAsync(
					CancellationToken.None);
			}
			catch
			{
				// Preserve the fail-closed marker if serialization is unavailable.
				return;
			}

			AntigravityOfficialPrintSafetyState? currentState;

			try
			{
				currentState = await TryGetExistingSafetyStateAsync(
					normalizedExecutablePath,
					CancellationToken.None,
					executionLease);
			}
			catch
			{
				// A storage failure must never turn a manual latch into an auto retry.
				return;
			}

			if (currentState is null ||
				!string.Equals(
					currentState.AttemptId,
					attemptId,
					StringComparison.Ordinal) ||
				currentState.AutomaticRevalidationAllowed)
			{
				return;
			}

			AntigravityOfficialPrintSafetyState recoveredState =
				CreateSafetyState(
					recoveredReason,
					automaticRevalidationAllowed: true,
					hasAttemptedAutomaticRevalidation: false,
					attemptId);

			if (_safetyStateStore is not null)
			{
				try
				{
					await RunSafetyStateOperationWithinBudgetAsync(
						token => _safetyStateStore.SaveAsync(
							recoveredState,
							token),
						CancellationToken.None,
						executionLease);
				}
				catch
				{
					// Keep the existing fail-closed marker when promotion cannot persist.
					return;
				}
			}

			_safetyLatchedExecutables[normalizedExecutablePath] = recoveredState;
		}
		catch
		{
			// Background recovery is best effort and always fails closed.
		}
		finally
		{
			try
			{
				executionLease?.Dispose();
			}
			catch
			{
				// A late lease-disposal failure must not become unobserved.
			}
			finally
			{
				_captureGate.Release();
			}
		}
	}

	private AntigravityOfficialPrintSafetyState CreateSafetyState(
		AntigravityUsageSafetyFailureReason reason,
		bool automaticRevalidationAllowed,
		bool hasAttemptedAutomaticRevalidation,
		string? attemptId = null,
		string? detectedCliVersion = null)
	{
		return new AntigravityOfficialPrintSafetyState(
			NormalizeSafetyFailureReason(reason),
			_timeProvider.GetUtcNow().ToUniversalTime(),
			automaticRevalidationAllowed,
			hasAttemptedAutomaticRevalidation,
			attemptId ?? CreateAttemptId(),
			detectedCliVersion);
	}

	private static string CreateAttemptId() => Guid.NewGuid().ToString("N");

	private AntigravityOfficialPrintSafetyState LatchStateInMemory(
		string normalizedExecutablePath,
		AntigravityOfficialPrintSafetyState state)
	{
		_safetyLatchedExecutables[normalizedExecutablePath] = state;
		return state;
	}

	private bool IsAutomaticRevalidationDue(
		AntigravityOfficialPrintSafetyState state)
	{
		DateTimeOffset retryAt;

		try
		{
			retryAt = state.LatchedAtUtc +
				AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}

		return _timeProvider.GetUtcNow().ToUniversalTime() >= retryAt;
	}

	private bool HasFutureAutomaticRevalidationTimestamp(
		AntigravityOfficialPrintSafetyState state)
	{
		return state.LatchedAtUtc >
			_timeProvider.GetUtcNow().ToUniversalTime();
	}

	private static bool CanAutomaticallyRevalidate(
		AntigravityOfficialPrintSafetyState state)
	{
		bool hasCurrentSchemaProvenance =
			state.AttemptId is string attemptId && IsValidAttemptId(attemptId);

		if (state.Reason is
			AntigravityUsageSafetyFailureReason.AttemptInterrupted or
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable)
		{
			return state.AutomaticRevalidationAllowed &&
				hasCurrentSchemaProvenance;
		}

		if (hasCurrentSchemaProvenance &&
			AntigravityAutomaticRecoveryPolicy.CanMigrateCurrentSchemaMarker(
				state.Reason))
		{
			return true;
		}

		return state.AutomaticRevalidationAllowed &&
			AntigravityAutomaticRecoveryPolicy.IsRecoverableReason(state.Reason) &&
			(state.Reason == AntigravityUsageSafetyFailureReason.TimedOut ||
			 hasCurrentSchemaProvenance);
	}

	private static bool RequiresInterruptedAttemptRecovery(
		AntigravityOfficialPrintSafetyState state)
	{
		return state.Reason ==
				AntigravityUsageSafetyFailureReason.AttemptInterrupted &&
			state.AttemptId is string attemptId &&
			IsValidAttemptId(attemptId);
	}

	private async Task<bool> TryRecoverInterruptedAttemptAsync(
		AntigravityOfficialPrintSafetyState state,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _processRunner.TryRecoverInterruptedAttemptAsync(
				state.AttemptId!,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return false;
		}
	}

	private static AntigravityProductionUsageResult
		CreateExistingSafetyLatchFailure(
			AntigravityOfficialPrintSafetyState state)
	{
		AntigravityProductionUsageResult failure = Failure(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			state.Reason,
			automaticRevalidationPending:
				CanAutomaticallyRevalidate(state));
		AntigravityOfficialPrintVersionAssessment? versionAssessment =
			AntigravityOfficialPrintVersionDiagnosticPolicy
				.AssessPersistedVersion(
					state.Reason,
					state.DetectedCliVersion);
		return versionAssessment?.Kind ==
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed
				? failure.WithOfficialPrintVersionAssessment(versionAssessment)
				: failure;
	}

	private static AntigravityProductionUsageResult
		CreateInterruptedAutomaticRevalidationFailure()
	{
		return Failure(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			AntigravityUsageSafetyFailureReason.AttemptInterrupted);
	}

	private static AntigravityUsageSafetyFailureReason
		GetProcessResultSafetyFailureReason(
			AntigravityOfficialPrintProcessResult processResult)
	{
		if (processResult.StdoutLimitExceeded)
		{
			return AntigravityUsageSafetyFailureReason.StdoutTooLarge;
		}

		if (processResult.StderrLimitExceeded)
		{
			return AntigravityUsageSafetyFailureReason.StderrTooLarge;
		}

		if (processResult.ExitCode != 0)
		{
			return AntigravityUsageSafetyFailureReason.NonZeroExit;
		}

		if (processResult.Stdout.IsEmpty)
		{
			return AntigravityUsageSafetyFailureReason.EmptyOutput;
		}

		return AntigravityUsageSafetyFailureReason.None;
	}

	private bool HasExplicitUsageActivity(
		AntigravityOfficialPrintProcessResult processResult)
	{
		if (processResult.StdoutLimitExceeded ||
			processResult.Stdout.IsEmpty)
		{
			return false;
		}

		try
		{
			AntigravityOfficialPrintParseResult parsed =
				AntigravityOfficialPrintEnvelopeParser.Parse(
					processResult.Stdout,
					_timeProvider.GetUtcNow());
			return parsed.SafetyFailureReason ==
				AntigravityUsageSafetyFailureReason.UsageActivityDetected;
		}
		catch
		{
			return false;
		}
	}

	private static AntigravityUsageSafetyFailureReason
		NormalizeSafetyFailureReason(
			AntigravityUsageSafetyFailureReason reason)
	{
		return Enum.IsDefined(reason) &&
			reason != AntigravityUsageSafetyFailureReason.None
				? reason
				: AntigravityUsageSafetyFailureReason.Unknown;
	}

	private static bool IsValidSafetyState(
		AntigravityOfficialPrintSafetyState state)
	{
		return Enum.IsDefined(state.Reason) &&
			state.Reason != AntigravityUsageSafetyFailureReason.None &&
			state.LatchedAtUtc != default &&
			state.LatchedAtUtc.Offset == TimeSpan.Zero &&
			(state.AttemptId is null || IsValidAttemptId(state.AttemptId)) &&
			((state.DetectedCliVersion is null) ||
			 ((state.AttemptId is not null) &&
			  AntigravityOfficialPrintVersionDiagnosticPolicy
				  .IsValidPersistedState(
					  state.Reason,
					  state.DetectedCliVersion))) &&
			(!state.AutomaticRevalidationAllowed ||
			 (state.Reason is
					AntigravityUsageSafetyFailureReason.AttemptInterrupted or
					AntigravityUsageSafetyFailureReason.SafetyStateUnavailable
				? state.AttemptId is not null
				: AntigravityAutomaticRecoveryPolicy.IsRecoverableReason(
					state.Reason) &&
				  (state.Reason == AntigravityUsageSafetyFailureReason.TimedOut ||
				   state.AttemptId is not null)));
	}

	private static bool IsValidAttemptId(string attemptId)
	{
		return Guid.TryParseExact(attemptId, "N", out _);
	}

	private static bool TryNormalizeExecutablePath(
		string? executablePath,
		out string normalizedExecutablePath)
	{
		normalizedExecutablePath = string.Empty;

		try
		{
			if (string.IsNullOrWhiteSpace(executablePath) ||
				!Path.IsPathFullyQualified(executablePath) ||
				!string.Equals(
					Path.GetExtension(executablePath),
					".exe",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			normalizedExecutablePath = Path.GetFullPath(executablePath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static AntigravityProductionUsageResult Failure(
		AntigravityProductionUsageFailureKind failureKind,
		AntigravityUsageSafetyFailureReason safetyFailureReason =
			AntigravityUsageSafetyFailureReason.None,
		bool automaticRevalidationPending = false)
	{
		return new AntigravityProductionUsageResult(
			isSuccessful: false,
			failureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			safetyFailureReason,
			automaticRevalidationPending);
	}
}

internal sealed class WindowsAntigravityOfficialPrintProcessRunner :
	IAntigravityOfficialPrintProcessRunner
{
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		internal IntPtr Process;
		internal IntPtr Thread;
		internal uint ProcessId;
		internal uint ThreadId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SecurityAttributes
	{
		internal uint Length;
		internal IntPtr SecurityDescriptor;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool InheritHandle;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		internal uint Size;
		internal IntPtr Reserved;
		internal IntPtr Desktop;
		internal IntPtr Title;
		internal uint X;
		internal uint Y;
		internal uint XSize;
		internal uint YSize;
		internal uint XCountCharacters;
		internal uint YCountCharacters;
		internal uint FillAttribute;
		internal uint Flags;
		internal ushort ShowWindow;
		internal ushort ReservedByteCount;
		internal IntPtr ReservedBytes;
		internal IntPtr StandardInput;
		internal IntPtr StandardOutput;
		internal IntPtr StandardError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfoEx
	{
		internal StartupInfo StartupInfo;
		internal IntPtr AttributeList;
	}

	private sealed class ProcessCreationAttributeList : IDisposable
	{
		private IntPtr _handleArray;
		private IntPtr _jobHandleArray;
		private WindowsProcessJob.SafeKernelHandleLease? _jobHandleLease;
		private IntPtr _pointer;

		internal IntPtr Pointer => _pointer;

		private ProcessCreationAttributeList(
			IntPtr pointer,
			IntPtr handleArray,
			IntPtr jobHandleArray,
			WindowsProcessJob.SafeKernelHandleLease jobHandleLease)
		{
			_pointer = pointer;
			_handleArray = handleArray;
			_jobHandleArray = jobHandleArray;
			_jobHandleLease = jobHandleLease;
		}

		internal static ProcessCreationAttributeList Create(
			IReadOnlyList<SafeFileHandle> handles,
			WindowsProcessJob job)
		{
			ArgumentNullException.ThrowIfNull(handles);
			ArgumentNullException.ThrowIfNull(job);

			if (handles.Count == 0)
			{
				throw new ArgumentException(
					"At least one inherited handle is required.",
					nameof(handles));
			}

			nuint requiredBytes = 0;
			_ = NativeMethods.InitializeProcThreadAttributeList(
				IntPtr.Zero,
				2,
				0,
				ref requiredBytes);

			if (requiredBytes == 0)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to determine the redirected process attribute-list size.");
			}

			IntPtr pointer = IntPtr.Zero;
			IntPtr handleArray = IntPtr.Zero;
			IntPtr jobHandleArray = IntPtr.Zero;
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease = null;
			bool isInitialized = false;

			try
			{
				pointer = Marshal.AllocHGlobal(checked((int)requiredBytes));
				handleArray = Marshal.AllocHGlobal(
					checked(handles.Count * IntPtr.Size));
				jobHandleArray = Marshal.AllocHGlobal(IntPtr.Size);

				if (!NativeMethods.InitializeProcThreadAttributeList(
					pointer,
					2,
					0,
					ref requiredBytes))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to initialize the redirected process attribute list.");
				}

				isInitialized = true;

				for (int index = 0; index < handles.Count; index++)
				{
					SafeFileHandle handle = handles[index];

					if (handle.IsInvalid || handle.IsClosed)
					{
						throw new ArgumentException(
							"Inherited handles must be open and valid.",
							nameof(handles));
					}

					Marshal.WriteIntPtr(
						handleArray,
						checked(index * IntPtr.Size),
						handle.DangerousGetHandle());
				}

				if (!NativeMethods.UpdateProcThreadAttribute(
					pointer,
					0,
					ProcThreadAttributeHandleList,
					handleArray,
					checked((nuint)(handles.Count * IntPtr.Size)),
					IntPtr.Zero,
					IntPtr.Zero))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to restrict inherited redirected-process handles.");
				}

				jobHandleLease = job.AcquireHandleLease();
				Marshal.WriteIntPtr(jobHandleArray, jobHandleLease.Value);
				if (!NativeMethods.UpdateProcThreadAttribute(
					pointer,
					0,
					ProcThreadAttributeJobList,
					jobHandleArray,
					checked((nuint)IntPtr.Size),
					IntPtr.Zero,
					IntPtr.Zero))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to assign the redirected process Job Object at creation.");
				}

				GC.KeepAlive(handles);
				GC.KeepAlive(job);
				return new ProcessCreationAttributeList(
					pointer,
					handleArray,
					jobHandleArray,
					jobHandleLease);
			}
			catch
			{
				if (isInitialized)
				{
					NativeMethods.DeleteProcThreadAttributeList(pointer);
				}

				if (jobHandleArray != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(jobHandleArray);
				}

				jobHandleLease?.Dispose();

				if (handleArray != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(handleArray);
				}

				if (pointer != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(pointer);
				}

				throw;
			}
		}

		public void Dispose()
		{
			IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
			IntPtr handleArray = Interlocked.Exchange(
				ref _handleArray,
				IntPtr.Zero);
			IntPtr jobHandleArray = Interlocked.Exchange(
				ref _jobHandleArray,
				IntPtr.Zero);
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease =
				Interlocked.Exchange(ref _jobHandleLease, null);

			if (pointer != IntPtr.Zero)
			{
				NativeMethods.DeleteProcThreadAttributeList(pointer);
				Marshal.FreeHGlobal(pointer);
			}

			if (handleArray != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(handleArray);
			}

			if (jobHandleArray != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(jobHandleArray);
			}

			jobHandleLease?.Dispose();
		}
	}

	private sealed class RedirectedJobProcess : IDisposable
	{
		private WindowsProcessJob? _job;
		private SafeKernelHandle? _processHandle;
		private FileStream? _standardError;
		private FileStream? _standardOutput;

		internal Stream StandardError =>
			Volatile.Read(ref _standardError) ??
			throw new ObjectDisposedException(GetType().Name);

		internal Stream StandardOutput =>
			Volatile.Read(ref _standardOutput) ??
			throw new ObjectDisposedException(GetType().Name);

		internal RedirectedJobProcess(
			WindowsProcessJob job,
			SafeKernelHandle processHandle,
			FileStream standardOutput,
			FileStream standardError)
		{
			_job = job;
			_processHandle = processHandle;
			_standardOutput = standardOutput;
			_standardError = standardError;
		}

		internal async Task<int> WaitForExitAsync(
			CancellationToken cancellationToken)
		{
			SafeKernelHandle processHandle =
				Volatile.Read(ref _processHandle) ??
				throw new ObjectDisposedException(GetType().Name);
			return await WaitForProcessExitAsync(
				processHandle,
				cancellationToken);
		}

		internal Task WaitForTreeEmptyAsync(
			CancellationToken cancellationToken)
		{
			WindowsProcessJob job = Volatile.Read(ref _job) ??
				throw new ObjectDisposedException(GetType().Name);
			return job.WaitForEmptyAsync(cancellationToken);
		}

		internal Task<bool> WaitForPositiveTreeEmptyAsync(TimeSpan timeout)
		{
			WindowsProcessJob job = Volatile.Read(ref _job) ??
				throw new ObjectDisposedException(GetType().Name);
			return job.WaitForPositiveEmptyConfirmationAsync(timeout);
		}

		internal void TryTerminateTree()
		{
			WindowsProcessJob? job = Volatile.Read(ref _job);

			if (job is null)
			{
				return;
			}

			try
			{
				if (job.GetActiveProcessCount() > 0)
				{
					job.Terminate(ForcedExitCode);
				}
			}
			catch
			{
				// Failed termination remains unconfirmed. The Job handle stays
				// alive while the positive-empty observer retries fail-closed.
			}
		}

		internal void CloseOutputStreams()
		{
			TryDispose(Interlocked.Exchange(ref _standardOutput, null));
			TryDispose(Interlocked.Exchange(ref _standardError, null));
		}

		public void Dispose()
		{
			CloseOutputStreams();
			Interlocked.Exchange(ref _processHandle, null)?.Dispose();
			Interlocked.Exchange(ref _job, null)?.Dispose();
		}
	}

	private static class NativeMethods
	{
		[DllImport(
			"kernel32.dll",
			CharSet = CharSet.Unicode,
			EntryPoint = "CreateProcessW",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CreateProcess(
			[MarshalAs(UnmanagedType.LPWStr)] string applicationName,
			[In, Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder commandLine,
			IntPtr processAttributes,
			IntPtr threadAttributes,
			[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
			uint creationFlags,
			IntPtr environment,
			[MarshalAs(UnmanagedType.LPWStr)] string currentDirectory,
			ref StartupInfoEx startupInfo,
			out ProcessInformation processInformation);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "CreatePipe",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CreatePipe(
			out SafeFileHandle readPipe,
			out SafeFileHandle writePipe,
			ref SecurityAttributes pipeAttributes,
			uint size);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "DeleteProcThreadAttributeList",
			ExactSpelling = true)]
		internal static extern void DeleteProcThreadAttributeList(
			IntPtr attributeList);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "GetExitCodeProcess",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetExitCodeProcess(
			SafeKernelHandle process,
			out uint exitCode);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "InitializeProcThreadAttributeList",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool InitializeProcThreadAttributeList(
			IntPtr attributeList,
			int attributeCount,
			uint flags,
			ref nuint size);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "SetHandleInformation",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool SetHandleInformation(
			SafeFileHandle handle,
			uint mask,
			uint flags);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "TerminateProcess",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool TerminateProcess(
			SafeKernelHandle process,
			uint exitCode);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "UpdateProcThreadAttribute",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool UpdateProcThreadAttribute(
			IntPtr attributeList,
			uint flags,
			nuint attribute,
			IntPtr value,
			nuint size,
			IntPtr previousValue,
			IntPtr returnSize);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "WaitForSingleObject",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern uint WaitForSingleObject(
			SafeKernelHandle handle,
			uint milliseconds);
	}

	internal sealed record BoundedReadResult(
		byte[] Bytes,
		bool HadAnyBytes,
		bool LimitExceeded);

	private const int MaximumStderrBytes = 64 * 1024;
	private const int MaximumStdoutBytes = 1024 * 1024;
	private const uint CreateNoWindow = 0x08000000;
	private const uint CreateUnicodeEnvironment = 0x00000400;
	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const uint ForcedExitCode = 0xC0DE0002;
	internal const uint ProductionActiveProcessLimit = 1;
	private const uint HandleFlagInherit = 0x00000001;
	private const uint StartfUseStdHandles = 0x00000100;
	private const uint WaitFailed = 0xFFFFFFFF;
	private const uint WaitObject0 = 0x00000000;
	private const uint WaitTimeout = 0x00000102;
	private const string AttemptJobNamePrefix =
		"Local\\AiUsageDashboard.Antigravity.";
	private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;
	private static readonly nuint ProcThreadAttributeJobList = 0x0002000D;
	private static readonly TimeSpan ProcessPollInterval =
		TimeSpan.FromMilliseconds(20);
	private readonly Action<uint>? _afterProcessCreated;
	private readonly uint? _activeProcessLimit;
	private readonly Func<CancellationToken, Task>? _beforeStartAsync;
	private readonly TimeSpan _cleanupTimeout;
	private readonly TimeSpan _commandTimeout;

	internal WindowsAntigravityOfficialPrintProcessRunner()
		: this(
			AntigravityOfficialPrintTiming.UsageCommandTimeout,
			AntigravityOfficialPrintTiming.UsageCleanupTimeout,
			activeProcessLimit: ProductionActiveProcessLimit)
	{
	}

	internal WindowsAntigravityOfficialPrintProcessRunner(
		TimeSpan commandTimeout,
		TimeSpan cleanupTimeout,
		Func<CancellationToken, Task>? beforeStartAsync = null,
		Action<uint>? afterProcessCreated = null,
		uint? activeProcessLimit = null)
	{
		if (commandTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(commandTimeout));
		}

		if (cleanupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
		}

		if (activeProcessLimit == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(activeProcessLimit));
		}

		_commandTimeout = commandTimeout;
		_cleanupTimeout = cleanupTimeout;
		_beforeStartAsync = beforeStartAsync;
		_afterProcessCreated = afterProcessCreated;
		_activeProcessLimit = activeProcessLimit;
	}

	public Task<AntigravityOfficialPrintProcessResult> RunAsync(
		string executablePath,
		CancellationToken cancellationToken)
	{
		return RunCoreAsync(
			executablePath,
			CreateAttemptJobName(Guid.NewGuid().ToString("N")),
			beforeStartAsync: null,
			cancellationToken);
	}

	public Task<AntigravityOfficialPrintProcessResult> RunAsync(
		string executablePath,
		string attemptId,
		Func<CancellationToken, Task> beforeStartAsync,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(beforeStartAsync);
		return RunCoreAsync(
			executablePath,
			CreateAttemptJobName(attemptId),
			beforeStartAsync,
			cancellationToken);
	}

	internal Task<AntigravityOfficialPrintProcessResult> RunAsync(
		ProcessStartInfo startInfo,
		string jobName,
		Func<CancellationToken, Task>? beforeStartAsync,
		CancellationToken cancellationToken)
	{
		return RunCoreAsync(
			startInfo,
			jobName,
			beforeStartAsync,
			cancellationToken);
	}

	public async Task<bool> TryRecoverInterruptedAttemptAsync(
		string attemptId,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string jobName = CreateAttemptJobName(attemptId);

		if (!WindowsProcessJob.TryOpenExisting(
				jobName,
				out WindowsProcessJob? job))
		{
			return true;
		}

		using WindowsProcessJob recoveredJob = job!;
		if (recoveredJob.GetActiveProcessCount() == 0)
		{
			return true;
		}

		recoveredJob.Terminate(ForcedExitCode);
		return await recoveredJob.WaitForPositiveEmptyConfirmationAsync(
			_cleanupTimeout,
			cancellationToken);
	}

	internal static string CreateAttemptJobName(string attemptId)
	{
		if (!Guid.TryParseExact(attemptId, "N", out _))
		{
			throw new ArgumentException(
				"The AGY attempt identifier is invalid.",
				nameof(attemptId));
		}

		return AttemptJobNamePrefix + attemptId;
	}

	private async Task<AntigravityOfficialPrintProcessResult> RunCoreAsync(
		string executablePath,
		string jobName,
		Func<CancellationToken, Task>? beforeStartAsync,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ProcessStartInfo startInfo = CreateStartInfo(executablePath);
		return await RunCoreAsync(
			startInfo,
			jobName,
			beforeStartAsync,
			cancellationToken);
	}

	private async Task<AntigravityOfficialPrintProcessResult> RunCoreAsync(
		ProcessStartInfo startInfo,
		string jobName,
		Func<CancellationToken, Task>? beforeStartAsync,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ValidateRedirectedJobStartInfo(startInfo, jobName);
		RedirectedJobProcess process = await StartRedirectedJobProcessAsync(
			startInfo,
			jobName,
			beforeStartAsync,
			cancellationToken);
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		Task<BoundedReadResult>? stdoutTask = null;
		Task<BoundedReadResult>? stderrTask = null;
		bool processOwnershipTransferred = false;

		try
		{
			stdoutTask = ReadBoundedAsync(
				process.StandardOutput,
				MaximumStdoutBytes,
				saveBytes: true,
				linkedSource.Token);
			stderrTask = ReadBoundedAsync(
				process.StandardError,
				MaximumStderrBytes,
				saveBytes: false,
				linkedSource.Token);

			int exitCode = await process.WaitForExitAsync(linkedSource.Token);
			BoundedReadResult stdout = await stdoutTask;
			BoundedReadResult stderr = await stderrTask;
			await process.WaitForTreeEmptyAsync(linkedSource.Token);
			cancellationToken.ThrowIfCancellationRequested();

			return new AntigravityOfficialPrintProcessResult(
				exitCode,
				stdout.Bytes,
				stdout.LimitExceeded,
				stderr.HadAnyBytes,
				stderr.LimitExceeded);
		}
		catch (Exception exception)
		{
			TryCancel(linkedSource);
			process.TryTerminateTree();
			Task<bool> quiescenceTask = CompleteFailedProcessCleanupAsync(
				process,
				stdoutTask,
				stderrTask,
				_cleanupTimeout);
			processOwnershipTransferred = true;
			bool isTerminationConfirmed = await quiescenceTask;

			Exception containedException =
				(exception is OperationCanceledException) &&
				!cancellationToken.IsCancellationRequested &&
				timeoutSource.IsCancellationRequested
					? new TimeoutException(
						"The official AGY print command exceeded its time limit.",
						exception)
					: exception;
			throw new AntigravityOfficialPrintProcessRunException(
				wasProcessStarted: true,
				containedException,
				isTerminationConfirmed,
				quiescenceTask,
				CreatePositiveQuiescenceEvidence(isTerminationConfirmed));
		}
		finally
		{
			if (!processOwnershipTransferred)
			{
				process.Dispose();
			}
		}
	}

	private static void ValidateRedirectedJobStartInfo(
		ProcessStartInfo startInfo,
		string jobName)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

		if ((jobName.Length > 260) || jobName.Contains('\0'))
		{
			throw new ArgumentException(
				"The process Job Object name is invalid.",
				nameof(jobName));
		}

		if (string.IsNullOrWhiteSpace(startInfo.FileName) ||
			startInfo.FileName.Contains('\0') ||
			!Path.IsPathFullyQualified(startInfo.FileName))
		{
			throw new ArgumentException(
				"The redirected process executable must use a fully qualified path.",
				nameof(startInfo));
		}

		if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ||
			startInfo.WorkingDirectory.Contains('\0') ||
			!Path.IsPathFullyQualified(startInfo.WorkingDirectory))
		{
			throw new ArgumentException(
				"The redirected process working directory must use a fully qualified path.",
				nameof(startInfo));
		}

		if (startInfo.UseShellExecute)
		{
			throw new ArgumentException(
				"The redirected process cannot use shell execution.",
				nameof(startInfo));
		}

		if (!startInfo.CreateNoWindow ||
			!startInfo.RedirectStandardInput ||
			!startInfo.RedirectStandardOutput ||
			!startInfo.RedirectStandardError)
		{
			throw new ArgumentException(
				"The redirected process must be windowless with all standard streams redirected.",
				nameof(startInfo));
		}

		if (!string.IsNullOrEmpty(startInfo.Arguments))
		{
			throw new ArgumentException(
				"The redirected process must provide arguments through ArgumentList.",
				nameof(startInfo));
		}
	}

	private async Task<RedirectedJobProcess> StartRedirectedJobProcessAsync(
		ProcessStartInfo startInfo,
		string jobName,
		Func<CancellationToken, Task>? beforeStartAsync,
		CancellationToken cancellationToken)
	{
		WindowsProcessJob? job = null;
		SafeFileHandle? inputReadHandle = null;
		SafeFileHandle? inputWriteHandle = null;
		SafeFileHandle? outputReadHandle = null;
		SafeFileHandle? outputWriteHandle = null;
		SafeFileHandle? errorReadHandle = null;
		SafeFileHandle? errorWriteHandle = null;
		ProcessCreationAttributeList? processAttributes = null;
		SafeKernelHandle? processHandle = null;
		SafeKernelHandle? threadHandle = null;
		FileStream? outputStream = null;
		FileStream? errorStream = null;
		IntPtr environment = IntPtr.Zero;
		bool isJobMembershipVerified = false;
		bool hasProcessStarted = false;
		bool ownershipTransferred = false;

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			job = _activeProcessLimit.HasValue
				? WindowsProcessJob.CreateKillOnClose(
					jobName,
					_activeProcessLimit.Value)
				: WindowsProcessJob.CreateKillOnClose(jobName);
			CreateRedirectedPipe(
				out inputReadHandle,
				out inputWriteHandle,
				parentReads: false);
			CreateRedirectedPipe(
				out outputReadHandle,
				out outputWriteHandle,
				parentReads: true);
			CreateRedirectedPipe(
				out errorReadHandle,
				out errorWriteHandle,
				parentReads: true);
			processAttributes = ProcessCreationAttributeList.Create(
				new[]
				{
					inputReadHandle,
					outputWriteHandle,
					errorWriteHandle
				},
				job);
			environment = Marshal.StringToHGlobalUni(
				BuildEnvironmentBlock(startInfo.Environment));
			StartupInfoEx startupInfo = default;
			startupInfo.StartupInfo.Size =
				checked((uint)Marshal.SizeOf<StartupInfoEx>());
			startupInfo.StartupInfo.Flags = StartfUseStdHandles;
			startupInfo.StartupInfo.StandardInput =
				inputReadHandle.DangerousGetHandle();
			startupInfo.StartupInfo.StandardOutput =
				outputWriteHandle.DangerousGetHandle();
			startupInfo.StartupInfo.StandardError =
				errorWriteHandle.DangerousGetHandle();
			startupInfo.AttributeList = processAttributes.Pointer;
			StringBuilder commandLine = BuildCommandLine(startInfo);

			if (beforeStartAsync is not null)
			{
				await beforeStartAsync(cancellationToken);
			}

			if (_beforeStartAsync is not null)
			{
				await _beforeStartAsync(cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();

			if (!NativeMethods.CreateProcess(
				startInfo.FileName,
				commandLine,
				IntPtr.Zero,
				IntPtr.Zero,
				inheritHandles: true,
				BuildCreationFlags(),
				environment,
				startInfo.WorkingDirectory,
				ref startupInfo,
				out ProcessInformation processInformation))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to create the official AGY process.");
			}

			GC.KeepAlive(inputReadHandle);
			GC.KeepAlive(outputWriteHandle);
			GC.KeepAlive(errorWriteHandle);
			GC.KeepAlive(job);
			processHandle = new SafeKernelHandle(processInformation.Process);
			threadHandle = new SafeKernelHandle(processInformation.Thread);
			hasProcessStarted = true;
			job.VerifyMembership(processHandle);
			isJobMembershipVerified = true;
			_afterProcessCreated?.Invoke(processInformation.ProcessId);

			inputReadHandle.Dispose();
			inputReadHandle = null;
			outputWriteHandle.Dispose();
			outputWriteHandle = null;
			errorWriteHandle.Dispose();
			errorWriteHandle = null;
			inputWriteHandle.Dispose();
			inputWriteHandle = null;
			outputStream = new FileStream(
				outputReadHandle,
				FileAccess.Read,
				bufferSize: 8192,
				isAsync: false);
			outputReadHandle = null;
			errorStream = new FileStream(
				errorReadHandle,
				FileAccess.Read,
				bufferSize: 8192,
				isAsync: false);
			errorReadHandle = null;

			cancellationToken.ThrowIfCancellationRequested();

			RedirectedJobProcess result = new(
				job,
				processHandle,
				outputStream,
				errorStream);
			job = null;
			processHandle = null;
			outputStream = null;
			errorStream = null;
			ownershipTransferred = true;
			return result;
		}
		catch (Exception exception)
		{
			if (processHandle is null)
			{
				throw;
			}

			TryTerminateFailedStart(
				processHandle,
				job,
				isJobMembershipVerified);
			Task<bool> quiescenceTask = CompleteFailedStartCleanupAsync(
				job,
				processHandle,
				outputStream,
				errorStream,
				isJobMembershipVerified,
				_cleanupTimeout);
			job = null;
			processHandle = null;
			outputStream = null;
			errorStream = null;
			ownershipTransferred = true;
			bool isTerminationConfirmed = await quiescenceTask;
			throw new AntigravityOfficialPrintProcessRunException(
				hasProcessStarted,
				exception,
				isTerminationConfirmed,
				quiescenceTask,
				CreatePositiveQuiescenceEvidence(isTerminationConfirmed));
		}
		finally
		{
			threadHandle?.Dispose();
			processAttributes?.Dispose();

			if (environment != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(environment);
			}

			inputReadHandle?.Dispose();
			inputWriteHandle?.Dispose();
			outputReadHandle?.Dispose();
			outputWriteHandle?.Dispose();
			errorReadHandle?.Dispose();
			errorWriteHandle?.Dispose();

			if (!ownershipTransferred)
			{
				TryDispose(outputStream);
				TryDispose(errorStream);
				processHandle?.Dispose();
				job?.Dispose();
			}
		}
	}

	internal static uint BuildCreationFlags()
	{
		return CreateNoWindow |
			CreateUnicodeEnvironment |
			ExtendedStartupInfoPresent;
	}

	internal static ProcessStartInfo CreateStartInfo(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		string? workingDirectory = Path.GetDirectoryName(executablePath);

		if (string.IsNullOrWhiteSpace(workingDirectory) ||
			!Path.IsPathFullyQualified(executablePath))
		{
			throw new ArgumentException(
				"The official AGY executable path is invalid.",
				nameof(executablePath));
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = executablePath,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("-p");
		startInfo.ArgumentList.Add("/usage");
		startInfo.ArgumentList.Add("--output-format");
		startInfo.ArgumentList.Add("stream-json");
		startInfo.Environment.Clear();

		foreach ((string name, string value) in
			AntigravityCliProcessEnvironment.BuildAllowlist())
		{
			startInfo.Environment[name] = value;
		}

		return startInfo;
	}

	internal static async Task<BoundedReadResult> ReadBoundedAsync(
		Stream stream,
		int maximumSavedBytes,
		bool saveBytes,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[8192];
		MemoryStream? saved = saveBytes
			? new MemoryStream(Math.Min(maximumSavedBytes, 64 * 1024))
			: null;
		long totalBytes = 0;
		bool hadAnyBytes = false;

		try
		{
			while (true)
			{
				int read = await stream.ReadAsync(buffer, cancellationToken);

				if (read == 0)
				{
					break;
				}

				hadAnyBytes = true;
				totalBytes = totalBytes > long.MaxValue - read
					? long.MaxValue
					: totalBytes + read;

				if ((saved is not null) && (saved.Length < maximumSavedBytes))
				{
					int bytesToSave = checked((int)Math.Min(
						read,
						maximumSavedBytes - saved.Length));
					await saved.WriteAsync(
						buffer.AsMemory(0, bytesToSave),
						cancellationToken);
				}
			}

			return new BoundedReadResult(
				saved?.ToArray() ?? Array.Empty<byte>(),
				hadAnyBytes,
				totalBytes > maximumSavedBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(buffer);
			ZeroAndDispose(saved);
		}
	}

	private static async Task<bool> CompleteFailedProcessCleanupAsync(
		RedirectedJobProcess process,
		Task<BoundedReadResult>? stdoutTask,
		Task<BoundedReadResult>? stderrTask,
		TimeSpan cleanupTimeout)
	{
		bool isTerminationConfirmed = false;
		Stopwatch cleanupStopwatch = Stopwatch.StartNew();
		Task readCleanupTask = Task.WhenAll(
			ObserveAndZeroReadTaskAsync(stdoutTask),
			ObserveAndZeroReadTaskAsync(stderrTask));

		try
		{
			isTerminationConfirmed =
				await process.WaitForPositiveTreeEmptyAsync(cleanupTimeout);
			process.CloseOutputStreams();
			process.Dispose();

			TimeSpan remainingCleanupBudget =
				cleanupTimeout - cleanupStopwatch.Elapsed;

			try
			{
				if (remainingCleanupBudget > TimeSpan.Zero)
				{
					await readCleanupTask.WaitAsync(
						remainingCleanupBudget,
						CancellationToken.None);
				}
			}
			catch (TimeoutException)
			{
				// Stream ownership and the kill-on-close job have already been
				// released. The observers keep draining/zeroing without retaining
				// either the executable or cross-process execution lease.
			}

			return isTerminationConfirmed;
		}
		finally
		{
			try
			{
				process.Dispose();
			}
			catch
			{
				// Disposing the kill-on-close job is the bounded final
				// containment fallback when positive observation times out.
			}
		}
	}

	private static Task CreatePositiveQuiescenceEvidence(
		bool isTerminationConfirmed)
	{
		if (isTerminationConfirmed)
		{
			return Task.CompletedTask;
		}

		Task unavailableEvidence = Task.FromException(
			new InvalidOperationException(
				"The process tree was contained without positive empty-tree evidence."));
		// Some pre-start failures do not schedule automatic revalidation. Mark
		// the deliberately negative evidence observed in that path too.
		_ = unavailableEvidence.Exception;
		return unavailableEvidence;
	}

	private static async Task<bool> CompleteFailedStartCleanupAsync(
		WindowsProcessJob? job,
		SafeKernelHandle processHandle,
		FileStream? outputStream,
		FileStream? errorStream,
		bool isJobMembershipVerified,
		TimeSpan cleanupTimeout)
	{
		bool isTerminationConfirmed = false;

		try
		{
			isTerminationConfirmed = isJobMembershipVerified && (job is not null)
				? await job.WaitForPositiveEmptyConfirmationAsync(cleanupTimeout)
				: await WaitForPositiveProcessExitAsync(
					processHandle,
					cleanupTimeout);
			return isTerminationConfirmed;
		}
		finally
		{
			TryDispose(outputStream);
			TryDispose(errorStream);
			processHandle.Dispose();
			// Kill-on-close is the final bounded containment fallback when
			// the process tree could not be positively observed empty.
			job?.Dispose();
		}
	}

	private static async Task ObserveAndZeroReadTaskAsync(
		Task<BoundedReadResult>? readTask)
	{
		if (readTask is null)
		{
			return;
		}

		try
		{
			BoundedReadResult result = await readTask;
			CryptographicOperations.ZeroMemory(result.Bytes);
		}
		catch
		{
			// The late read failure is deliberately observed and contained.
		}
	}

	private static void ZeroAndDispose(MemoryStream? stream)
	{
		if (stream is null)
		{
			return;
		}

		try
		{
			if (stream.TryGetBuffer(out ArraySegment<byte> segment) &&
				(segment.Array is not null))
			{
				CryptographicOperations.ZeroMemory(segment.Array);
			}
		}
		finally
		{
			stream.Dispose();
		}
	}

	private static void TryCancel(CancellationTokenSource source)
	{
		try
		{
			source.Cancel();
		}
		catch
		{
			// The operation is already ending.
		}
	}

	private static void TryTerminateFailedStart(
		SafeKernelHandle processHandle,
		WindowsProcessJob? job,
		bool isJobMembershipVerified)
	{
		try
		{
			if (isJobMembershipVerified && (job is not null))
			{
				if (job.GetActiveProcessCount() > 0)
				{
					job.Terminate(ForcedExitCode);
				}
			}
			else
			{
				_ = NativeMethods.TerminateProcess(
					processHandle,
					ForcedExitCode);
			}
		}
		catch
		{
			// The positive observer below keeps ownership fail-closed.
		}
	}

	private static async Task<int> WaitForProcessExitAsync(
		SafeKernelHandle processHandle,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			uint waitResult = NativeMethods.WaitForSingleObject(
				processHandle,
				0);

			if (waitResult == WaitObject0)
			{
				if (!NativeMethods.GetExitCodeProcess(
					processHandle,
					out uint exitCode))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to read the official AGY print process exit code.");
				}

				return unchecked((int)exitCode);
			}

			if (waitResult == WaitFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to wait for the official AGY print process.");
			}

			if (waitResult != WaitTimeout)
			{
				throw new InvalidOperationException(
					$"The official AGY print process wait returned unexpected status 0x{waitResult:X8}.");
			}

			await Task.Delay(ProcessPollInterval, cancellationToken);
		}
	}

	private static Task<bool> WaitForPositiveProcessExitAsync(
		SafeKernelHandle processHandle,
		TimeSpan timeout)
	{
		return WaitForPositiveProcessExitCoreAsync(
			() => NativeMethods.WaitForSingleObject(processHandle, 0),
			timeout);
	}

	internal static async Task<bool> WaitForPositiveProcessExitCoreAsync(
		Func<uint> waitForProcessExit,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(waitForProcessExit);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				if (waitForProcessExit() == WaitObject0)
				{
					return true;
				}
			}
			catch
			{
				// Query failures are not positive exit confirmation. The caller
				// still has a bounded final containment fallback.
			}

			TimeSpan remaining = timeout - stopwatch.Elapsed;

			if (remaining <= TimeSpan.Zero)
			{
				return false;
			}

			await Task.Delay(
				remaining < ProcessPollInterval
					? remaining
					: ProcessPollInterval,
				cancellationToken);
		}
	}

	private static void CreateRedirectedPipe(
		out SafeFileHandle readHandle,
		out SafeFileHandle writeHandle,
		bool parentReads)
	{
		SecurityAttributes attributes = new()
		{
			Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
			InheritHandle = true
		};

		if (!NativeMethods.CreatePipe(
			out readHandle,
			out writeHandle,
			ref attributes,
			0))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to create an official AGY print redirection pipe.");
		}

		SafeFileHandle parentHandle = parentReads ? readHandle : writeHandle;

		if (!NativeMethods.SetHandleInformation(
			parentHandle,
			HandleFlagInherit,
			0))
		{
			int errorCode = Marshal.GetLastWin32Error();
			readHandle.Dispose();
			writeHandle.Dispose();
			throw new Win32Exception(
				errorCode,
				"Unable to restrict an official AGY print parent pipe handle.");
		}
	}

	private static StringBuilder BuildCommandLine(ProcessStartInfo startInfo)
	{
		StringBuilder commandLine = new();
		AppendCommandLineArgument(commandLine, startInfo.FileName);

		foreach (string argument in startInfo.ArgumentList)
		{
			commandLine.Append(' ');
			AppendCommandLineArgument(commandLine, argument);
		}

		return commandLine;
	}

	private static void AppendCommandLineArgument(
		StringBuilder commandLine,
		string argument)
	{
		if (argument.Contains('\0'))
		{
			throw new ArgumentException(
				"Process arguments must not contain NUL.",
				nameof(argument));
		}

		if ((argument.Length > 0) &&
			!argument.Any(character =>
				char.IsWhiteSpace(character) || (character == '"')))
		{
			commandLine.Append(argument);
			return;
		}

		commandLine.Append('"');
		int backslashCount = 0;

		foreach (char character in argument)
		{
			if (character == '\\')
			{
				backslashCount++;
				continue;
			}

			if (character == '"')
			{
				commandLine.Append('\\', checked((backslashCount * 2) + 1));
				commandLine.Append('"');
				backslashCount = 0;
				continue;
			}

			commandLine.Append('\\', backslashCount);
			commandLine.Append(character);
			backslashCount = 0;
		}

		commandLine.Append('\\', checked(backslashCount * 2));
		commandLine.Append('"');
	}

	private static string BuildEnvironmentBlock(
		IDictionary<string, string?> environment)
	{
		StringBuilder block = new();

		foreach ((string name, string? value) in environment
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (string.IsNullOrWhiteSpace(name) ||
				name.Contains('=') ||
				name.Contains('\0') ||
				(value is null) ||
				value.Contains('\0'))
			{
				throw new ArgumentException(
					"The official AGY print environment contains an invalid entry.",
					nameof(environment));
			}

			block.Append(name);
			block.Append('=');
			block.Append(value);
			block.Append('\0');
		}

		block.Append('\0');
		return block.ToString();
	}

	private static void TryDispose(IDisposable? disposable)
	{
		try
		{
			disposable?.Dispose();
		}
		catch
		{
			// Cleanup remains best-effort after positive process-tree exit.
		}
	}
}
