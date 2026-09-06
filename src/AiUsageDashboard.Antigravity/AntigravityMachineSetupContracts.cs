using System.Collections.ObjectModel;

namespace AiUsageDashboard.AntigravitySpike;

public static class AntigravityMachineSetupLaunchArguments
{
	public const string DashboardManagedResultProtocolEnvironmentVariable =
		"AI_USAGE_DASHBOARD_AGY_SETUP_RESULT_PROTOCOL";

	public const string DashboardManagedSetupAttemptIdEnvironmentVariable =
		"AI_USAGE_DASHBOARD_AGY_SETUP_ATTEMPT_ID";

	public const string DashboardManagedSourceKindExitProtocol =
		"source-kind-exit-v1";

	public const string DashboardManaged =
		"--dashboard-managed";

	public const string EnsureDashboardAccount =
		"--ensure-antigravity-account";

	public const int DashboardManagedCancelledExitCode = 10;

	public const int DashboardManagedFailedExitCode = 11;

	public const int DashboardManagedOfficialPrintSuccessExitCode = 12;

	public const int DashboardManagedCompletionUnknownExitCode = 13;

	public const int DashboardManagedSuccessExitCode = 0;
}

public static class AntigravityMachineSetupEnvironmentVariables
{
	public const string OfficialExecutable =
		"AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE";
}

public enum AntigravityMachineSetupSourceKind
{
	ReviewedConPty,
	OfficialPrint
}

public enum AntigravityMachineSetupStage
{
	LoadingReviewedContract,
	DiscoveringExecutable,
	PreparingPrivateStorage,
	CalibratingPrompt,
	MaterializingProfile,
	ValidatingUsage,
	ReadyForApproval,
	Approving
}

public enum AntigravityMachineSetupFailureKind
{
	None,
	ConsentRequired,
	UnsupportedBuild,
	AmbiguousExecutable,
	ExistingProcessDetected,
	PrivateStorageRejected,
	SettingsRejected,
	PromptRejected,
	UsageRejected,
	ApprovalRejected,
	UnexpectedFailure,
	ExecutionBusy,
	SafetyRevalidationRequired
}

internal sealed record AntigravityMachineSetupPreviewFailure(
	AntigravityMachineSetupSourceKind SourceKind,
	AntigravityProductionUsageFailureKind FailureKind,
	AntigravityUsageSafetyFailureReason SafetyFailureReason,
	bool AutomaticRevalidationPending);

public sealed record AntigravityMachineSetupConsent(
	bool IsLiveCaptureApproved,
	bool HasConfirmedCommandReadyPrompt);

public sealed record AntigravityMachineSetupProgress(
	AntigravityMachineSetupStage Stage);

public interface IAntigravityMachineSetupService
{
	Task<AntigravityMachineSetupPreparationResult> PrepareAsync(
		AntigravityMachineSetupConsent consent,
		IProgress<AntigravityMachineSetupProgress>? progress,
		CancellationToken cancellationToken);
}

public sealed class AntigravityMachineSetupPreparationResult
{
	private Func<
		CancellationToken,
		Task<AntigravityMachineSetupPreparationResult>>?
		_revalidateSafetyAsync;

	public AntigravityMachineSetupCandidate? Candidate { get; }

	public AntigravityMachineSetupFailureKind FailureKind { get; }

	public bool CanRevalidateSafety =>
		Volatile.Read(ref _revalidateSafetyAsync) is not null;

	internal AntigravityMachineSetupPreviewFailure? PreviewFailure { get; }

	public bool IsSuccessful => Candidate is not null;

	internal AntigravityMachineSetupPreparationResult(
		AntigravityMachineSetupCandidate candidate)
	{
		Candidate = candidate ??
			throw new ArgumentNullException(nameof(candidate));
		FailureKind = AntigravityMachineSetupFailureKind.None;
	}

	internal AntigravityMachineSetupPreparationResult(
		AntigravityMachineSetupFailureKind failureKind)
	{
		if ((failureKind == AntigravityMachineSetupFailureKind.None) ||
			!Enum.IsDefined(failureKind))
		{
			throw new ArgumentOutOfRangeException(nameof(failureKind));
		}

		FailureKind = failureKind;
	}

	internal AntigravityMachineSetupPreparationResult(
		AntigravityMachineSetupFailureKind failureKind,
		AntigravityMachineSetupSourceKind previewSourceKind,
		AntigravityProductionUsageResult previewFailure,
		Func<
			CancellationToken,
			Task<AntigravityMachineSetupPreparationResult>>?
			revalidateSafetyAsync = null)
		: this(failureKind)
	{
		ArgumentNullException.ThrowIfNull(previewFailure);

		if (!Enum.IsDefined(previewSourceKind) ||
			previewFailure.IsSuccessful ||
			(previewFailure.FailureKind ==
				AntigravityProductionUsageFailureKind.None))
		{
			throw new ArgumentException(
				"The AGY setup preview failure shape is invalid.",
				nameof(previewFailure));
		}

		bool isManualSafetyRevalidation =
			failureKind ==
				AntigravityMachineSetupFailureKind.SafetyRevalidationRequired;

		if (isManualSafetyRevalidation != (revalidateSafetyAsync is not null) ||
			(isManualSafetyRevalidation &&
				((previewFailure.FailureKind !=
					AntigravityProductionUsageFailureKind.SafetyLatched) ||
				previewFailure.AutomaticRevalidationPending)))
		{
			throw new ArgumentException(
				"The AGY setup safety-revalidation shape is invalid.",
				nameof(revalidateSafetyAsync));
		}

		PreviewFailure = new AntigravityMachineSetupPreviewFailure(
			previewSourceKind,
			previewFailure.FailureKind,
			previewFailure.SafetyFailureReason,
			previewFailure.AutomaticRevalidationPending);
		_revalidateSafetyAsync = revalidateSafetyAsync;
	}

	public async Task<AntigravityMachineSetupPreparationResult>
		RevalidateSafetyAsync(
			CancellationToken cancellationToken = default)
	{
		Func<
			CancellationToken,
			Task<AntigravityMachineSetupPreparationResult>> operation =
			Interlocked.Exchange(ref _revalidateSafetyAsync, null) ??
			throw new InvalidOperationException(
				"This AGY setup result does not permit safety revalidation.");
		return await operation(cancellationToken) ??
			throw new InvalidOperationException(
				"AGY setup safety revalidation returned no result.");
	}
}

public sealed class AntigravityMachineSetupCandidate : IAsyncDisposable
{
	private const int PreparedState = 0;
	private const int ApprovedState = 1;
	private const int DisposedState = 2;
	private const int DisposedApprovedState = 3;
	private readonly Func<CancellationToken, Task> _approveAsync;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Func<ValueTask> _releaseAsync;
	private int _state;

	public string AccountIdentity { get; }

	public string CliVersion { get; }

	public AntigravityMachineSetupSourceKind SourceKind { get; }

	public bool IsApproved =>
		Volatile.Read(ref _state) is ApprovedState or DisposedApprovedState;

	public IReadOnlyList<AntigravityProductionUsageWindow> Windows { get; }

	internal AntigravityMachineSetupCandidate(
		string cliVersion,
		string accountIdentity,
		IEnumerable<AntigravityProductionUsageWindow> windows,
		Func<CancellationToken, Task> approveAsync,
		Func<ValueTask> releaseAsync,
		AntigravityMachineSetupSourceKind sourceKind =
			AntigravityMachineSetupSourceKind.ReviewedConPty)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cliVersion);
		ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentity);
		ArgumentNullException.ThrowIfNull(windows);
		ArgumentNullException.ThrowIfNull(approveAsync);
		ArgumentNullException.ThrowIfNull(releaseAsync);

		if (!Enum.IsDefined(sourceKind))
		{
			throw new ArgumentOutOfRangeException(nameof(sourceKind));
		}

		AntigravityProductionUsageWindow[] copy = windows.ToArray();

		if (copy.Length != 4)
		{
			throw new ArgumentException(
				"An AGY setup candidate must contain exactly four usage windows.",
				nameof(windows));
		}

		CliVersion = cliVersion;
		AccountIdentity = accountIdentity;
		SourceKind = sourceKind;
		Windows = new ReadOnlyCollection<AntigravityProductionUsageWindow>(
			copy);
		_approveAsync = approveAsync;
		_releaseAsync = releaseAsync;
	}

	public async Task ApproveAsync(
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);

		try
		{
			if (_state == ApprovedState)
			{
				return;
			}

			if (_state != PreparedState)
			{
				throw new ObjectDisposedException(
					nameof(AntigravityMachineSetupCandidate));
			}

			await _approveAsync(cancellationToken);
			Volatile.Write(ref _state, ApprovedState);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _gate.WaitAsync();

		try
		{
			if (_state is DisposedState or DisposedApprovedState)
			{
				return;
			}

			await _releaseAsync();
			Volatile.Write(
				ref _state,
				_state == ApprovedState
					? DisposedApprovedState
					: DisposedState);
		}
		finally
		{
			_gate.Release();
		}
	}
}
