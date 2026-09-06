using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal enum ClaudeUsageSafetyStateClearPurpose
{
	AccountRemoval,
	UserConfirmedReset
}

internal sealed class ClaudeCliUsagePoller :
	IClaudeUsagePoller,
	IClaudeSubscriptionContextProbe
{
	internal sealed class ContainedUsageCleanupUnconfirmedException :
		InvalidOperationException
	{
		internal ContainedUsageCleanupUnconfirmedException(Exception innerException)
			: base(
				"Claude contained usage process cleanup was not positively confirmed.",
				innerException)
		{
		}
	}

	private sealed class AccountSafetyState
	{
		internal readonly object SyncRoot = new();
		internal int AutomaticRevalidationAttemptCount;
		internal string? AccountIdentity;
		internal string? DiagnosticCliVersion;
		internal string? FailureReason;
		internal Guid? AttemptId;
		internal ClaudeUsageSafetyAttemptPhase? AttemptPhase;
		internal bool IsAttemptInProgress;
		internal bool IsHydrated;
		internal ClaudeUsageSafetyRecoveryMode RecoveryMode;
		internal DateTimeOffset? RetryNotBefore;
	}

	private sealed class TransferableExecutableLease : IDisposable
	{
		private IDisposable? _lease;

		internal TransferableExecutableLease(IDisposable lease)
		{
			_lease = lease ?? throw new ArgumentNullException(nameof(lease));
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _lease, null)?.Dispose();
		}

		internal IDisposable Take()
		{
			return Interlocked.Exchange(ref _lease, null) ??
				throw new ObjectDisposedException(nameof(TransferableExecutableLease));
		}
	}

	private sealed record SafetyFailureSnapshot(
		string FailureReason,
		string? AccountIdentity,
		DateTimeOffset? AutomaticRetryNotBefore,
		string? DiagnosticCliVersion = null);
	private sealed record CommandContext(
		string ExecutablePath,
		string ConfigDirectory,
		WindowsOfficialCliExecutableLease ExecutableLease);

	internal sealed record ProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);

	private const int MaximumStandardErrorBytes = 64 * 1024;
	private const int MaximumStandardOutputBytes = 1024 * 1024;
	private const int MaximumAccountIdentityLength = 320;
	private const int MaximumAuthStatusClassificationLength = 64;
	private const int MaximumAutomaticRevalidationBackoffStep = 3;
	private const int MaximumFastModeMetadataLength = 128;
	private const int MaximumSessionIdLength = 320;
	private const string ExplicitUsageActivityDataKey =
		"AiUsageDashboard.Claude.ExplicitUsageActivity";
	private const string AccountIdentityUnavailableDataKey =
		"AiUsageDashboard.Claude.AccountIdentityUnavailable";
	private const string SubscriptionScopeUnavailableDataKey =
		"AiUsageDashboard.Claude.SubscriptionScopeUnavailable";
	private const string InterruptedAttemptFailureReason =
		"Claude Code `/usage` 尚未確認是否產生用量就中斷。";
	private const string SafetyStateUnavailableFailureReason =
		"無法讀取或儲存上次 Claude 用量檢查的狀態。";
	private const string ContainedUsageJobNamePrefix =
		"Local\\AiUsageDashboard.Claude.";
	private const string ExpectedSignerCommonName = "Anthropic, PBC";
	private const uint ContainedUsageForcedExitCode = 0xC0DE0003;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
	private static readonly Version MinimumSafeModeVersion =
		CliVersionPolicies.ClaudeMinimumCapabilityVersion;
	private static readonly WindowsOfficialCliExecutableValidator
		OfficialExecutableValidator = CreateOfficialExecutableValidator(
			HasExpectedEmbeddedVersion);
	private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan SafetyStateOperationTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan[] SafetyStateClearRetryDelays =
	[
		TimeSpan.FromMilliseconds(100),
		TimeSpan.FromMilliseconds(300),
		TimeSpan.FromMilliseconds(700)
	];
	private static readonly HashSet<string> SafeResultPropertyNames = new(
		new[]
		{
			"type",
			"subtype",
			"is_error",
			"num_turns",
			"queued_turn_count",
			"total_cost_usd",
			"duration_ms",
			"duration_api_ms",
			"session_id",
			"uuid",
			"stop_reason",
			"terminal_reason",
			"fast_mode_state",
			"fast_mode_disabled_reason",
			"usage",
			"modelUsage",
			"subagent_stats",
			"permission_denials",
			"result"
		},
		StringComparer.Ordinal);
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);
	private readonly ConcurrentDictionary<Guid, AccountSafetyState> _accountSafetyStates = new();
	private readonly ConcurrentDictionary<Guid, IDisposable>
		_containedAttemptExecutableLeases = new();
	private readonly Func<Guid, string> _configDirectoryResolver;
	private readonly IClaudeAccountBindingStore? _accountBindingStore;
	private readonly ClaudeBindingCommitGate? _bindingCommitGate;
	private readonly Func<WindowsOfficialCliExecutableLease?> _executableResolver;
	private readonly ClaudeAccountOperationGate _operationGate;
	private readonly Func<
		ProcessStartInfo,
		ProviderProcessOperationTracker,
		CancellationToken,
		Task<ProcessResult>> _processRunner;
	private readonly Func<
		ProcessStartInfo,
		string,
		Func<CancellationToken, Task>,
		CancellationToken,
		Task<ProcessResult>>? _containedUsageProcessRunner;
	private readonly Func<
		Guid,
		TimeSpan,
		CancellationToken,
		Task<bool>> _containedUsageAttemptRecovery;
	private readonly IClaudeUsageSafetyStateStore? _safetyStateStore;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _commandTimeout;

	internal int CachedAccountSafetyStateCount => _accountSafetyStates.Count;

	public ClaudeCliUsagePoller(Func<Guid, string> configDirectoryResolver)
		: this(
			configDirectoryResolver,
			new ClaudeAccountOperationGate())
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		ClaudeAccountOperationGate operationGate)
		: this(
			configDirectoryResolver,
			ResolveExecutablePath,
			RunProcessAsync,
			TimeProvider.System,
			operationGate,
			CommandTimeout,
			safetyStateStore: null,
			enableCrashContainedUsageRunner: true,
			protectedExecutableResolver: ResolveExecutable,
			trackedProcessRunner: RunProcessAsync)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		ClaudeAccountOperationGate operationGate,
		IClaudeUsageSafetyStateStore safetyStateStore)
		: this(
			configDirectoryResolver,
			ResolveExecutablePath,
			RunProcessAsync,
			TimeProvider.System,
			operationGate,
			CommandTimeout,
			safetyStateStore,
			enableCrashContainedUsageRunner: true,
			protectedExecutableResolver: ResolveExecutable,
			trackedProcessRunner: RunProcessAsync)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		ClaudeAccountOperationGate operationGate,
		IClaudeUsageSafetyStateStore safetyStateStore,
		IClaudeAccountBindingStore accountBindingStore,
		ClaudeBindingCommitGate? bindingCommitGate = null)
		: this(
			configDirectoryResolver,
			ResolveExecutablePath,
			RunProcessAsync,
			TimeProvider.System,
			operationGate,
			CommandTimeout,
			safetyStateStore,
			enableCrashContainedUsageRunner: true,
			protectedExecutableResolver: ResolveExecutable,
			trackedProcessRunner: RunProcessAsync,
			accountBindingStore: accountBindingStore ??
				throw new ArgumentNullException(nameof(accountBindingStore)),
			bindingCommitGate: bindingCommitGate)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		TimeProvider timeProvider)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			timeProvider,
			new ClaudeAccountOperationGate(),
			CommandTimeout)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		TimeProvider timeProvider,
		IClaudeUsageSafetyStateStore safetyStateStore)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			timeProvider,
			new ClaudeAccountOperationGate(),
			CommandTimeout,
			safetyStateStore)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		TimeProvider timeProvider,
		ClaudeAccountOperationGate operationGate)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			timeProvider,
			operationGate,
			CommandTimeout)
	{
	}

	internal ClaudeCliUsagePoller(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		TimeProvider timeProvider,
		ClaudeAccountOperationGate operationGate,
		TimeSpan commandTimeout,
		IClaudeUsageSafetyStateStore? safetyStateStore = null,
		bool enableCrashContainedUsageRunner = false,
		Func<
			ProcessStartInfo,
			string,
			Func<CancellationToken, Task>,
			CancellationToken,
			Task<ProcessResult>>? containedUsageProcessRunner = null,
		Func<
			Guid,
			TimeSpan,
			CancellationToken,
			Task<bool>>? containedUsageAttemptRecovery = null,
		Func<WindowsOfficialCliExecutableLease?>? protectedExecutableResolver = null,
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker,
			CancellationToken,
			Task<ProcessResult>>? trackedProcessRunner = null,
		IClaudeAccountBindingStore? accountBindingStore = null,
		ClaudeBindingCommitGate? bindingCommitGate = null)
	{
		_configDirectoryResolver = configDirectoryResolver ??
			throw new ArgumentNullException(nameof(configDirectoryResolver));
		ArgumentNullException.ThrowIfNull(executableResolver);
		_executableResolver = protectedExecutableResolver ??
			WrapExecutableResolver(executableResolver);
		ArgumentNullException.ThrowIfNull(processRunner);
		_processRunner = trackedProcessRunner ??
			((startInfo, _, token) => processRunner(startInfo, token));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
		_accountBindingStore = accountBindingStore;
		_bindingCommitGate = accountBindingStore is null
			? null
			: bindingCommitGate ??
				throw new ArgumentNullException(nameof(bindingCommitGate));
		_safetyStateStore = safetyStateStore;
		_commandTimeout = commandTimeout > TimeSpan.Zero
			? commandTimeout
			: throw new ArgumentOutOfRangeException(nameof(commandTimeout));
		if (enableCrashContainedUsageRunner && containedUsageProcessRunner is not null)
		{
			throw new ArgumentException(
				"A contained Claude usage runner cannot be both injected and enabled.",
				nameof(containedUsageProcessRunner));
		}

		if (containedUsageProcessRunner is not null)
		{
			_containedUsageProcessRunner = containedUsageProcessRunner;
		}
		else if (enableCrashContainedUsageRunner)
		{
			WindowsAntigravityOfficialPrintProcessRunner windowsProcessRunner = new(
				_commandTimeout,
				ProcessCleanupTimeout);
			_containedUsageProcessRunner =
				(startInfo, jobName, beforeResumeAsync, token) =>
					RunCrashContainedUsageProcessAsync(
						windowsProcessRunner,
						startInfo,
						jobName,
						beforeResumeAsync,
						token);
		}

		_containedUsageAttemptRecovery = containedUsageAttemptRecovery ??
			TryRecoverContainedUsageAttemptAsync;
	}

	public Task<ClaudeUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return PollAsyncCore(
			accountId,
			expectedPublicBindingIdentity: null,
			cancellationToken);
	}

	public Task<ClaudeUsagePollResult> PollBoundAsync(
		Guid accountId,
		string expectedPublicBindingIdentity,
		CancellationToken cancellationToken = default)
	{
		if (!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				expectedPublicBindingIdentity,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null))
		{
			throw new ArgumentException(
				"Claude public binding identity 格式無效。",
				nameof(expectedPublicBindingIdentity));
		}

		return PollAsyncCore(
			accountId,
			normalizedIdentity,
			cancellationToken);
	}

	private async Task<ClaudeUsagePollResult> PollAsyncCore(
		Guid accountId,
		string? expectedPublicBindingIdentity,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		using CancellationTokenSource gateTimeoutSource = new(_commandTimeout);
		using CancellationTokenSource gateLinkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				gateTimeoutSource.Token);
		IDisposable? bindingLease = null;
		IDisposable rawLease;

		try
		{
			if (_bindingCommitGate is not null)
			{
				bindingLease = await _bindingCommitGate.EnterAsync(
					gateLinkedSource.Token);
			}

			rawLease = await _operationGate.EnterAsync(
				accountId,
				gateLinkedSource.Token);
		}
		catch (OperationCanceledException) when (gateLinkedSource.IsCancellationRequested)
		{
			bindingLease?.Dispose();

			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			_operationGate.ThrowIfContainmentCompromised(accountId);

			throw new TimeoutException("等待前一個 Claude 帳號作業逾時。");
		}
		catch
		{
			bindingLease?.Dispose();
			throw;
		}

		using (bindingLease)
		{
			return await PollWithAccountLeaseAsync(
				accountId,
				expectedPublicBindingIdentity,
				rawLease,
				cancellationToken);
		}
	}

	public async Task<ClaudeSubscriptionObservation> ProbeAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Claude 帳號識別碼不可為空。",
				nameof(accountId));
		}

		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		IDisposable rawLease;

		try
		{
			rawLease = await _operationGate.EnterAsync(
				accountId,
				linkedSource.Token);
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			_operationGate.ThrowIfContainmentCompromised(accountId);
			throw new TimeoutException(
				"等待前一個 Claude 訂閱範圍 probe 逾時。");
		}

		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		using IDisposable lease = operationTracker.HoldLease(rawLease);

		try
		{
			ClaudeSubscriptionObservation observation =
				await ProbeCoreAsync(
					accountId,
					operationTracker,
					cancellationToken);

			if (operationTracker.IsContainmentCompromised)
			{
				throw new ClaudeCliContainmentException();
			}

			return observation;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (ClaudeCliContainmentException)
		{
			throw;
		}
		catch (Exception exception) when (
			operationTracker.IsContainmentCompromised)
		{
			throw new ClaudeCliContainmentException(exception);
		}
	}

	private async Task<ClaudeSubscriptionObservation> ProbeCoreAsync(
		Guid accountId,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		CommandContext commandContext = await ResolveCommandContextAsync(
			accountId,
			operationTracker,
			cancellationToken).ConfigureAwait(false);
		using IDisposable executableLease = operationTracker.HoldLease(
			commandContext.ExecutableLease);
		ProcessResult versionResult = await RunCommandAsync(
			CreateVersionStartInfo(
				commandContext.ExecutablePath,
				commandContext.ConfigDirectory),
			operationTracker,
			cancellationToken);

		if (versionResult.ExitCode != 0)
		{
			throw new InvalidOperationException(
				"Claude Code `--version` 執行失敗。");
		}

		Version validatedVersion = EnsureSupportedVersion(
			versionResult.StandardOutput);
		CliVersionEvidence versionEvidence =
			CliVersionPolicies.AssessClaude(validatedVersion);
		ProcessResult authResult = await RunCommandAsync(
			CreateAuthStatusStartInfo(
				commandContext.ExecutablePath,
				commandContext.ConfigDirectory),
			operationTracker,
			cancellationToken);

		if ((authResult.ExitCode != 0) && (authResult.ExitCode != 1))
		{
			InvalidOperationException exception = new(
				"Claude Code `auth status` 執行失敗。");
			exception.AttachVersionEvidence(versionEvidence);
			throw exception;
		}

		ClaudeSubscriptionContext context;

		try
		{
			context = ParseSubscriptionContext(authResult.StandardOutput);
		}
		catch (ClaudeUsageNotConfiguredException)
		{
			throw;
		}
		catch (Exception exception)
		{
			exception.AttachVersionEvidence(versionEvidence);
			throw;
		}

		if (authResult.ExitCode != 0)
		{
			InvalidDataException exception = new(
				"Claude `auth status` 的 exit code 與登入狀態互相衝突。");
			exception.AttachVersionEvidence(versionEvidence);
			throw exception;
		}

		return new ClaudeSubscriptionObservation(context, versionEvidence);
	}

	private async Task<ClaudeUsagePollResult> PollWithAccountLeaseAsync(
		Guid accountId,
		string? expectedPublicBindingIdentity,
		IDisposable rawLease,
		CancellationToken cancellationToken)
	{
		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		using IDisposable lease = operationTracker.HoldLease(rawLease);

		try
		{
			ClaudeUsagePollResult result = await PollCoreAsync(
				accountId,
				expectedPublicBindingIdentity,
				operationTracker,
				cancellationToken);

			if (operationTracker.IsContainmentCompromised)
			{
				throw new ClaudeCliContainmentException();
			}

			return result;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (ClaudeCliContainmentException)
		{
			throw;
		}
		catch (Exception exception) when (
			operationTracker.IsContainmentCompromised)
		{
			throw new ClaudeCliContainmentException(exception);
		}
	}

	internal async Task PurgeAccountSafetyStateAsync(
		Guid accountId,
		ClaudeUsageSafetyStateClearPurpose purpose,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		if (!Enum.IsDefined(purpose))
		{
			throw new ArgumentOutOfRangeException(nameof(purpose));
		}

		AccountSafetyState safetyState = _accountSafetyStates.GetOrAdd(
			accountId,
			_ => new AccountSafetyState());

		try
		{
			try
			{
				await EnsureSafetyStateHydratedAsync(
					accountId,
					safetyState,
					cancellationToken).ConfigureAwait(false);
			}
			catch (ClaudeUsageSafetyException exception) when (
				purpose == ClaudeUsageSafetyStateClearPurpose.AccountRemoval &&
				exception.InnerException is InvalidDataException)
			{
				// The card is already being removed under its exclusive account
				// gate. An unreadable latch has no trustworthy attempt identity to
				// recover, so commit an identity-free clear intent instead of
				// permanently retaining corrupt account data.
				await ClearPersistedSafetyStateWithRetryAsync(
					accountId,
					cancellationToken).ConfigureAwait(false);
				return;
			}

			bool isAttemptInProgress;

			lock (safetyState.SyncRoot)
			{
				isAttemptInProgress = safetyState.IsAttemptInProgress;
			}

			if (isAttemptInProgress)
			{
				if (purpose == ClaudeUsageSafetyStateClearPurpose.AccountRemoval)
				{
					await RedactPendingAttemptAccountIdentityAsync(
						accountId,
						safetyState,
						cancellationToken).ConfigureAwait(false);
				}

				await RecoverInterruptedAttemptAsync(
					accountId,
					safetyState).ConfigureAwait(false);
			}

			if (_safetyStateStore is not null)
			{
				await ClearPersistedSafetyStateWithRetryAsync(
					accountId,
					cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			// The durable store remains the source of truth after a failed purge;
			// evict the cache so a later attempt must hydrate that durable state.
			_accountSafetyStates.TryRemove(accountId, out _);
		}
	}

	private async Task<ClaudeUsagePollResult> PollCoreAsync(
		Guid accountId,
		string? expectedPublicBindingIdentity,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		ClaudeAccountBinding? expectedBinding =
			expectedPublicBindingIdentity is null
				? null
				: await LoadValidatedSubscriptionBindingAsync(
					accountId,
					expectedPublicBindingIdentity,
					cancellationToken).ConfigureAwait(false);
		AccountSafetyState safetyState = _accountSafetyStates.GetOrAdd(
			accountId,
			static _ => new AccountSafetyState());
		await EnsureSafetyStateHydratedAsync(
			accountId,
			safetyState,
			cancellationToken).ConfigureAwait(false);
		await RecoverInterruptedAttemptAsync(
			accountId,
			safetyState).ConfigureAwait(false);

		bool isAutomaticRevalidation = false;
		SafetyFailureSnapshot? blockedSafetyFailure = null;
		DateTimeOffset now = _timeProvider.GetUtcNow();

		lock (safetyState.SyncRoot)
		{
			if (safetyState.IsAttemptInProgress ||
				safetyState.RecoveryMode ==
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired)
			{
				blockedSafetyFailure = CreateSafetyFailureSnapshot(safetyState);
			}
			else if (safetyState.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending)
			{
				if ((safetyState.RetryNotBefore is not null) &&
					(now >= safetyState.RetryNotBefore.Value))
				{
					isAutomaticRevalidation = true;
				}
				else
				{
					blockedSafetyFailure = CreateSafetyFailureSnapshot(safetyState);
				}
			}
		}

		if (blockedSafetyFailure is not null)
		{
			throw CreateSafetyException(blockedSafetyFailure);
		}

		cancellationToken.ThrowIfCancellationRequested();
		CommandContext commandContext = await ResolveCommandContextAsync(
				accountId,
				operationTracker,
				cancellationToken)
				.ConfigureAwait(false);
		using TransferableExecutableLease executableLease = new(
			operationTracker.HoldLease(commandContext.ExecutableLease));
		string fullExecutablePath = commandContext.ExecutablePath;
		string configDirectory = commandContext.ConfigDirectory;
		ProcessResult versionResult = await RunCommandAsync(
			CreateVersionStartInfo(fullExecutablePath, configDirectory),
			operationTracker,
			cancellationToken);

		if (versionResult.ExitCode != 0)
		{
			throw new InvalidOperationException("Claude Code `--version` 執行失敗。");
		}

		Version validatedVersion = EnsureSupportedVersion(versionResult.StandardOutput);
		CliVersionEvidence versionEvidence =
			CliVersionPolicies.AssessClaude(validatedVersion);
		string? diagnosticCliVersion =
			versionEvidence.ShouldAppendFailureDiagnostic
				? versionEvidence.DetectedVersion
				: null;
		ProcessResult authResult = await RunCommandAsync(
			CreateAuthStatusStartInfo(fullExecutablePath, configDirectory),
			operationTracker,
			cancellationToken);

		if ((authResult.ExitCode != 0) && (authResult.ExitCode != 1))
		{
			InvalidOperationException exception = new(
				"Claude Code `auth status` 執行失敗。");
			exception.AttachVersionEvidence(versionEvidence);
			throw exception;
		}

		ClaudeSubscriptionContext subscriptionContext;

		try
		{
			subscriptionContext = ParseSubscriptionContext(
				authResult.StandardOutput);
		}
		catch (ClaudeUsageNotConfiguredException)
		{
			throw;
		}
		catch (Exception exception)
		{
			exception.AttachVersionEvidence(versionEvidence);
			throw;
		}

		if (authResult.ExitCode != 0)
		{
			InvalidDataException exception = new(
				"Claude `auth status` 的 exit code 與登入狀態互相衝突。");
			exception.AttachVersionEvidence(versionEvidence);
			throw exception;
		}

		await ValidateSubscriptionBindingAsync(
			accountId,
			expectedPublicBindingIdentity,
			expectedBinding,
			subscriptionContext,
			cancellationToken).ConfigureAwait(false);
		string accountIdentity = subscriptionContext.AccountIdentity;

		if (subscriptionContext.PlanTier is "enterprise")
		{
			throw new ClaudeSubscriptionUsageUnavailableException(
				subscriptionContext,
				versionEvidence);
		}

		Guid? containedAttemptId =
			_containedUsageProcessRunner is null || _safetyStateStore is null
			? null
			: Guid.NewGuid();
		await BeginSafetyTrackedAttemptAsync(
			accountId,
			safetyState,
			accountIdentity,
			containedAttemptId,
			cancellationToken).ConfigureAwait(false);

		ProcessResult usageResult;

		try
		{
			ProcessStartInfo usageStartInfo =
				CreateStartInfo(fullExecutablePath, configDirectory);
			usageResult = containedAttemptId is Guid attemptId
				? await RunCrashContainedUsageCommandAsync(
					accountId,
					attemptId,
					usageStartInfo,
					safetyState,
					cancellationToken)
				: await RunCommandAsync(
					usageStartInfo,
					operationTracker,
					cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			string failureReason =
				$"Claude Code {validatedVersion} 的 `/usage` 尚未確認是否產生用量就取消。";
			await LatchSafetyFailureAsync(
				accountId,
				safetyState,
				accountIdentity,
				failureReason,
				isAutomaticRevalidation,
				canRetryAutomatically: true,
				_timeProvider.GetUtcNow()).ConfigureAwait(false);
			throw;
		}
		catch (ContainedUsageCleanupUnconfirmedException exception)
		{
			if (containedAttemptId is Guid attemptId)
			{
				QuarantineContainedAttemptExecutableLease(
					attemptId,
					executableLease.Take());
			}

			throw CreateSafetyException(
				new SafetyFailureSnapshot(
					InterruptedAttemptFailureReason,
					accountIdentity,
					_timeProvider.GetUtcNow().ToUniversalTime() +
						TimeSpan.FromMinutes(1)),
				exception,
				subscriptionContext);
		}
		catch (Exception exception) when (IsUsageInvocationFailure(exception))
		{
			string failureReason =
				$"無法確認 Claude Code {validatedVersion} 的 `/usage` 是否產生用量。";
			SafetyFailureSnapshot safetyFailure = await LatchSafetyFailureAsync(
				accountId,
				safetyState,
				accountIdentity,
				failureReason,
				isAutomaticRevalidation,
				canRetryAutomatically: true,
				_timeProvider.GetUtcNow()).ConfigureAwait(false);
			throw CreateSafetyException(
				safetyFailure,
				exception,
				subscriptionContext);
		}

		ClaudeUsagePollResult pollResult;

		try
		{
			pollResult = ParseSafeResult(
				usageResult.StandardOutput,
				_timeProvider.GetUtcNow());
		}
		catch (Exception exception) when (IsExplicitUsageActivity(exception))
		{
			string failureReason =
				$"Claude Code {validatedVersion} 的 `/usage` 可能產生用量，或結果需要人工確認。";
			SafetyFailureSnapshot safetyFailure = await LatchSafetyFailureAsync(
				accountId,
				safetyState,
				accountIdentity,
				failureReason,
				isAutomaticRevalidation,
				canRetryAutomatically: false,
				_timeProvider.GetUtcNow()).ConfigureAwait(false);
			throw CreateSafetyException(
				safetyFailure,
				exception,
				subscriptionContext);
		}
		catch (Exception exception) when (IsFormatFailure(exception))
		{
			string failureReason =
				$"Claude Code {validatedVersion} 回傳的用量格式暫時無法辨識。";
			SafetyFailureSnapshot safetyFailure = await LatchSafetyFailureAsync(
				accountId,
				safetyState,
				accountIdentity,
				failureReason,
				isAutomaticRevalidation,
				canRetryAutomatically: true,
				_timeProvider.GetUtcNow(),
				diagnosticCliVersion).ConfigureAwait(false);
			throw CreateSafetyException(
				safetyFailure,
				exception,
				subscriptionContext);
		}

		if (usageResult.ExitCode != 0)
		{
			string failureReason =
				$"Claude Code {validatedVersion} 的 `/usage` 回報錯誤，但用量結果顯示成功，暫時無法確認這次結果。";
			SafetyFailureSnapshot safetyFailure = await LatchSafetyFailureAsync(
				accountId,
				safetyState,
				accountIdentity,
				failureReason,
				isAutomaticRevalidation,
				canRetryAutomatically: true,
				_timeProvider.GetUtcNow(),
				diagnosticCliVersion).ConfigureAwait(false);
			throw CreateSafetyException(
				safetyFailure,
				subscriptionContext: subscriptionContext);
		}

		await ClearSafetyFailureAsync(accountId, safetyState)
			.ConfigureAwait(false);
		return pollResult with
		{
			AccountIdentity = accountIdentity,
			VersionEvidence = versionEvidence,
			SubscriptionContext = subscriptionContext
		};
	}

	private async Task ValidateSubscriptionBindingAsync(
		Guid accountId,
		string? expectedPublicBindingIdentity,
		ClaudeAccountBinding? expectedBinding,
		ClaudeSubscriptionContext context,
		CancellationToken cancellationToken)
	{
		if (_accountBindingStore is null)
		{
			if (expectedPublicBindingIdentity is not null)
			{
				throw new InvalidOperationException(
					"Claude bound poll 缺少 private binding store。");
			}

			return;
		}

		ClaudeAccountBinding binding = expectedBinding ??
			await LoadValidatedSubscriptionBindingAsync(
				accountId,
				expectedPublicBindingIdentity,
				cancellationToken).ConfigureAwait(false);

		if (!binding.MatchesEntitlement(context))
		{
			throw new ClaudeSubscriptionScopeMismatchException();
		}

		IReadOnlyList<ClaudeAccountBinding> bindings =
			await _accountBindingStore.LoadAllAsync(cancellationToken)
				.ConfigureAwait(false);

		if (bindings.Any(candidate =>
				(candidate.AccountId != accountId) &&
				candidate.MatchesEntitlement(context)))
		{
			throw new ClaudeSubscriptionScopeMismatchException();
		}
	}

	private async Task<ClaudeAccountBinding>
		LoadValidatedSubscriptionBindingAsync(
		Guid accountId,
		string? expectedPublicBindingIdentity,
		CancellationToken cancellationToken)
	{
		if (_accountBindingStore is null)
		{
			throw new InvalidOperationException(
				"Claude bound poll 缺少 private binding store。");
		}

		ClaudeAccountBinding? binding;

		try
		{
			binding = await _accountBindingStore.LoadAsync(
				accountId,
				cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidDataException)
		{
			throw new ClaudeSubscriptionBindingInvalidException();
		}

		if (binding is null)
		{
			throw new ClaudeSubscriptionBindingMissingException();
		}

		if ((binding.AccountId != accountId) ||
			(binding.PublicBindingId == Guid.Empty) ||
			!ClaudeAccountBinding.IsCanonicalSalt(binding.SaltBase64) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.AccountFingerprintSha256) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.SubscriptionScopeFingerprintSha256) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.EntitlementFingerprintSha256))
		{
			throw new ClaudeSubscriptionBindingInvalidException();
		}

		if ((expectedPublicBindingIdentity is not null) &&
			!ProviderAccountIdentityRules.Comparer.Equals(
				expectedPublicBindingIdentity,
				ClaudeAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)))
		{
			throw new ClaudeSubscriptionBindingInvalidException();
		}

		return binding;
	}

	private async Task<CommandContext> ResolveCommandContextAsync(
			Guid accountId,
			ProviderProcessOperationTracker operationTracker,
			CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			return await ProviderProcessExecution.RunSynchronousAsync(
				() =>
				{
					WindowsOfficialCliExecutableLease? executableLease =
						_executableResolver();

					try
					{
						if ((executableLease is null) ||
							!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
								executableLease.ExecutablePath,
								out string normalizedExecutablePath) ||
							!File.Exists(normalizedExecutablePath))
						{
							throw new FileNotFoundException("找不到 Claude Code CLI。");
						}

						string configDirectory = Path.GetFullPath(
							_configDirectoryResolver(accountId));

						if (!Directory.Exists(configDirectory))
						{
							throw new DirectoryNotFoundException(
								"Claude 帳號設定目錄尚未建立。");
						}

						return new CommandContext(
							normalizedExecutablePath,
							configDirectory,
							executableLease);
					}
					catch
					{
						executableLease?.Dispose();
						throw;
					}
				},
				linkedSource.Token,
				operationTracker,
				static commandContext =>
				{
					commandContext.ExecutableLease.Dispose();
					return Task.CompletedTask;
				});
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new TimeoutException("Claude Code CLI 路徑解析逾時。");
		}
	}

	private async Task<ProcessResult> RunCommandAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			return await ProviderProcessExecution.RunAsynchronousAsync(
				token => _processRunner(
					startInfo,
					operationTracker,
					token),
				linkedSource.Token,
				operationTracker);
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new TimeoutException("Claude Code CLI 執行逾時。");
		}
	}

	internal static ProcessStartInfo CreateVersionStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateBaseStartInfo(executablePath, configDirectory);
		startInfo.ArgumentList.Add("--version");
		return startInfo;
	}

	internal static ProcessStartInfo CreateAuthStatusStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateBaseStartInfo(executablePath, configDirectory);
		startInfo.ArgumentList.Add("auth");
		startInfo.ArgumentList.Add("status");
		startInfo.ArgumentList.Add("--json");
		return startInfo;
	}

	internal static ProcessStartInfo CreateStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateBaseStartInfo(executablePath, configDirectory);
		startInfo.ArgumentList.Add("-p");
		startInfo.ArgumentList.Add("/usage");
		startInfo.ArgumentList.Add("--output-format");
		startInfo.ArgumentList.Add("json");
		startInfo.ArgumentList.Add("--max-turns");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("--permission-mode");
		startInfo.ArgumentList.Add("dontAsk");
		startInfo.ArgumentList.Add("--no-session-persistence");
		startInfo.ArgumentList.Add("--safe-mode");
		startInfo.ArgumentList.Add("--tools");
		startInfo.ArgumentList.Add(string.Empty);
		return startInfo;
	}

	internal static Version EnsureSupportedVersion(string standardOutput)
	{
		if (string.IsNullOrWhiteSpace(standardOutput))
		{
			throw new InvalidDataException("Claude `--version` 輸出不可為空。");
		}

		const string productSuffix = " (Claude Code)";
		string versionText = standardOutput.Trim();

		if (!versionText.EndsWith(productSuffix, StringComparison.Ordinal))
		{
			throw new InvalidDataException("Claude CLI 版本輸出未確認產品身分。");
		}

		versionText = versionText[..^productSuffix.Length];

		if (!CliVersionPolicies.TryParseCanonicalThreePartVersion(
			versionText,
			out Version? version))
		{
			throw new InvalidDataException("無法辨識 Claude Code CLI 版本格式。");
		}

		if (version < MinimumSafeModeVersion)
		{
			throw new ClaudeUsageNotConfiguredException(
				$"Claude Code {version} 不支援目前的用量檢查方式，請更新至 {MinimumSafeModeVersion} 或更新版本。",
				UsageRecoveryAction.InstallOrUpdate);
		}

		return version;
	}

	internal static string EnsureSubscriptionAuthentication(string standardOutput)
	{
		return ParseSubscriptionContext(standardOutput).AccountIdentity;
	}

	internal static ClaudeSubscriptionContext ParseSubscriptionContext(
		string standardOutput)
	{
		if (string.IsNullOrWhiteSpace(standardOutput))
		{
			throw new InvalidDataException("Claude `auth status` JSON 不可為空。");
		}

		using JsonDocument document = ParseJson(standardOutput);
		JsonElement root = document.RootElement;
		EnsureNoDuplicateProperties(root);
		ThrowIfExplicitUsageActivity(root);

		if ((root.ValueKind != JsonValueKind.Object) ||
			!root.TryGetProperty("loggedIn", out JsonElement loggedInElement) ||
			((loggedInElement.ValueKind is not JsonValueKind.True) &&
				(loggedInElement.ValueKind is not JsonValueKind.False)))
		{
			throw new InvalidDataException("Claude `auth status` envelope 不符合預期。");
		}

		if (loggedInElement.ValueKind == JsonValueKind.False)
		{
			throw new ClaudeUsageNotConfiguredException(
				"Claude Code 帳號尚未登入。",
				UsageRecoveryAction.ConnectAccount);
		}

		string? accountIdentity = ReadOptionalAccountIdentity(root);

		string authMethod = ReadRequiredAuthStatusClassification(
			root,
			"authMethod");
		string apiProvider = ReadRequiredAuthStatusClassification(
			root,
			"apiProvider");
		string subscriptionType = ReadRequiredAuthStatusClassification(
			root,
			"subscriptionType");

		if (!string.Equals(authMethod, "claude.ai", StringComparison.Ordinal) ||
			!string.Equals(apiProvider, "firstParty", StringComparison.Ordinal) ||
			!IsSupportedSubscriptionType(subscriptionType))
		{
			throw new ClaudeUsageNotConfiguredException(
				"Claude Code 目前不是以支援的 claude.ai subscription 帳號登入。",
				UsageRecoveryAction.SwitchAccount,
				accountIdentity);
		}

		if (string.IsNullOrWhiteSpace(accountIdentity))
		{
			throw CreateAccountIdentityUnavailableException(
				"Claude Code 已登入有效訂閱，但暫時無法確認目前登入的帳號。AI Usage 稍後會自動再試。");
		}

		string? subscriptionScopeIdentity = ReadOptionalBoundedString(
			root,
			"orgId",
			256);
		if (string.IsNullOrWhiteSpace(subscriptionScopeIdentity))
		{
			throw CreateSubscriptionScopeUnavailableException(
				"Claude Code 已登入有效訂閱，但暫時無法確認目前的組織。AI Usage 稍後會自動再試。");
		}

		string? subscriptionScopeDisplayName = ReadOptionalBoundedString(
			root,
			"orgName",
			256);

		try
		{
			return ClaudeSubscriptionContext.CreateVerified(
				accountIdentity,
				subscriptionScopeIdentity,
				subscriptionType,
				subscriptionScopeDisplayName);
		}
		catch (ArgumentException exception)
		{
			InvalidDataException invalidDataException =
				CreateSubscriptionScopeUnavailableException(
				"Claude Code 訂閱範圍格式無效。");
			throw new InvalidDataException(
				invalidDataException.Message,
				exception)
			{
				Data =
				{
					[SubscriptionScopeUnavailableDataKey] = true
				}
			};
		}
	}

	internal static InvalidDataException
		CreateAccountIdentityUnavailableException(string message)
	{
		InvalidDataException exception = new(message);
		exception.Data[AccountIdentityUnavailableDataKey] = true;
		return exception;
	}

	internal static bool IsAccountIdentityUnavailable(Exception exception)
	{
		return exception is InvalidDataException &&
			exception.Data[AccountIdentityUnavailableDataKey] is true;
	}

	internal static ClaudeUsagePollResult ParseSafeResult(
		string standardOutput,
		DateTimeOffset observedAt)
	{
		if (string.IsNullOrWhiteSpace(standardOutput))
		{
			throw new InvalidDataException("Claude `/usage` JSON 不可為空。");
		}

		using JsonDocument document = ParseJson(standardOutput);
		JsonElement root = document.RootElement;
		EnsureNoDuplicateProperties(root);
		ThrowIfExplicitUsageActivity(root);

		if ((root.ValueKind != JsonValueKind.Object) ||
			!ContainsOnlyProperties(root, SafeResultPropertyNames) ||
			!HasStringValue(root, "type", "result") ||
			!HasStringValue(root, "subtype", "success") ||
			!root.TryGetProperty("is_error", out JsonElement isErrorElement) ||
			(isErrorElement.ValueKind is not JsonValueKind.False) ||
			!root.TryGetProperty("num_turns", out JsonElement numTurnsElement) ||
			!numTurnsElement.TryGetInt32(out int numTurns) ||
			(numTurns != 0) ||
			(root.TryGetProperty(
				"queued_turn_count",
				out JsonElement queuedTurnCountElement) &&
				!IsExactJsonZero(queuedTurnCountElement)) ||
			!root.TryGetProperty("total_cost_usd", out JsonElement costElement) ||
			!IsExactJsonZero(costElement) ||
			!HasOptionalNonNegativeInt64Property(root, "duration_ms") ||
			!HasOptionalNonNegativeInt64Property(root, "duration_api_ms") ||
			!HasOptionalBoundedNonEmptyStringProperty(
				root,
				"session_id",
				MaximumSessionIdLength) ||
			!HasOptionalUuidProperty(root, "uuid") ||
			!HasOptionalNullProperty(root, "stop_reason") ||
			!HasOptionalStringValue(root, "terminal_reason", "completed") ||
			!HasOptionalBoundedNonEmptyStringProperty(
				root,
				"fast_mode_state",
				MaximumFastModeMetadataLength) ||
			!HasOptionalBoundedNonEmptyStringProperty(
				root,
				"fast_mode_disabled_reason",
				MaximumFastModeMetadataLength))
		{
			throw new InvalidDataException(
				"Claude `/usage` envelope 顯示非零 turn、非零 cost，或格式不符。");
		}

		if (!root.TryGetProperty("usage", out JsonElement usageElement) ||
			(usageElement.ValueKind != JsonValueKind.Object) ||
			!HasZeroNumericProperty(usageElement, "input_tokens") ||
			!HasZeroNumericProperty(usageElement, "output_tokens") ||
			!HasZeroNumericProperty(usageElement, "cache_read_input_tokens") ||
			!HasZeroNumericProperty(usageElement, "cache_creation_input_tokens") ||
			!HasSafeUsageShape(usageElement))
		{
			throw new InvalidDataException(
				"Claude `/usage` token usage 不是嚴格的零用量結果。");
		}

		if (root.TryGetProperty("modelUsage", out JsonElement modelUsageElement) &&
			((modelUsageElement.ValueKind != JsonValueKind.Object) ||
				!ContainsOnlyZeroNumericValues(modelUsageElement)))
		{
			throw new InvalidDataException(
				"Claude `/usage` model usage 不是嚴格的零用量物件。");
		}

		if (root.TryGetProperty(
				"subagent_stats",
				out JsonElement subagentStatsElement) &&
			((subagentStatsElement.ValueKind != JsonValueKind.Object) ||
				!ContainsOnlyZeroNumericValues(subagentStatsElement)))
		{
			throw new InvalidDataException(
				"Claude `/usage` subagent stats 不是嚴格的零活動物件。");
		}

		if (root.TryGetProperty("permission_denials", out JsonElement permissionDenialsElement) &&
			((permissionDenialsElement.ValueKind != JsonValueKind.Array) ||
				(permissionDenialsElement.GetArrayLength() != 0)))
		{
			throw new InvalidDataException("Claude `/usage` 發生 permission denial。");
		}

		if (!root.TryGetProperty("result", out JsonElement resultElement) ||
			(resultElement.ValueKind != JsonValueKind.String))
		{
			throw new InvalidDataException("Claude `/usage` 缺少文字結果。");
		}

		string output = resultElement.GetString() ?? string.Empty;

		if (string.IsNullOrWhiteSpace(output))
		{
			throw new InvalidDataException("Claude `/usage` 文字結果不可為空。");
		}

		return new ClaudeUsagePollResult(output, observedAt);
	}

	internal static string? ResolveExecutablePath()
	{
		using WindowsOfficialCliExecutableLease? executableLease =
			ResolveExecutable();
		return executableLease?.ExecutablePath;
	}

	internal static InvalidDataException
		CreateSubscriptionScopeUnavailableException(string message)
	{
		InvalidDataException exception = new(message);
		exception.Data[SubscriptionScopeUnavailableDataKey] = true;
		return exception;
	}

	internal static bool IsSubscriptionScopeUnavailable(Exception exception)
	{
		return exception is InvalidDataException &&
			exception.Data[SubscriptionScopeUnavailableDataKey] is true;
	}

	internal static WindowsOfficialCliExecutableLease? ResolveExecutable()
	{
		return ResolveExecutable(
			GetExecutableCandidates(),
			OfficialExecutableValidator,
			ClaudeCliExecutableStager.Shared);
	}

	internal static string? ResolveExecutablePath(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator)
	{
		return ResolveExecutablePath(candidates, validator, stager: null);
	}

	internal static string? ResolveExecutablePath(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator,
		IClaudeCliExecutableStager? stager)
	{
		using WindowsOfficialCliExecutableLease? executableLease =
			ResolveExecutable(candidates, validator, stager);
		return executableLease?.ExecutablePath;
	}

	private static WindowsOfficialCliExecutableLease? ResolveExecutable(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator,
		IClaudeCliExecutableStager? stager)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(validator);
		HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
		Exception? lastValidationFailure = null;

		foreach (string candidate in candidates)
		{
			if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
					candidate,
					out string fullPath))
			{
				continue;
			}

			if (fullPath.Contains(
				Path.Combine("Microsoft", "WindowsApps"),
				StringComparison.OrdinalIgnoreCase) ||
				!visitedPaths.Add(fullPath))
			{
				continue;
			}

			if (!File.Exists(fullPath))
			{
				continue;
			}

			try
			{
				if (stager is null)
				{
					return WindowsOfficialCliExecutableLease.CreateUnprotected(
						validator.Validate(fullPath));
				}

				WindowsOfficialCliExecutableLease executableLease =
					stager.Stage(fullPath);

				try
				{
					_ = validator.ValidateStaged(executableLease);
					return executableLease;
				}
				catch
				{
					executableLease.Dispose();
					throw;
				}
			}
			catch (OfficialCliExecutableValidationException exception)
			{
				lastValidationFailure = exception;
			}
			catch (ClaudeCliUntrustedException exception)
			{
				lastValidationFailure = exception;
			}
		}

		if (lastValidationFailure is not null)
		{
			throw new ClaudeCliUntrustedException(
				"Claude Code CLI 未通過官方簽章、路徑或版本驗證；請從 Anthropic 官方來源重新安裝或更新。",
				lastValidationFailure);
		}

		return null;
	}

	private static Func<WindowsOfficialCliExecutableLease?> WrapExecutableResolver(
		Func<string?> executableResolver)
	{
		return () =>
		{
			string? executablePath = executableResolver();
			return string.IsNullOrWhiteSpace(executablePath)
				? null
				: WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		};
	}

	internal static WindowsOfficialCliExecutableValidator
		CreateOfficialExecutableValidator(
			Func<string, bool> hasExpectedVersion,
			Func<string, WindowsAuthenticodeInspection>? inspectSignature = null)
	{
		ArgumentNullException.ThrowIfNull(hasExpectedVersion);
		Func<string, WindowsAuthenticodeInspection> signatureInspector =
			inspectSignature ??
			(path => new WindowsAuthenticodeInspector().Inspect(path));
		return new WindowsOfficialCliExecutableValidator(
			ExpectedSignerCommonName,
			hasExpectedVersion,
			signatureInspector,
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			IsOfficialExecutablePathAclSafe);
	}

	internal static bool IsOfficialExecutablePathAclSafe(string executablePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(executablePath) ||
				!Path.IsPathFullyQualified(executablePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(executablePath);

			if (WindowsExecutablePathSecurity.IsPathAclSafe(fullPath))
			{
				return true;
			}

			string? trustedRootPath = Path.GetDirectoryName(fullPath);
			return !string.IsNullOrWhiteSpace(trustedRootPath) &&
				WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot(
					fullPath,
					trustedRootPath);
		}
		catch
		{
			return false;
		}
	}

	internal static bool HasExpectedEmbeddedVersion(string executablePath)
	{
		try
		{
			FileVersionInfo versionInfo =
				FileVersionInfo.GetVersionInfo(executablePath);

			if (!string.Equals(
					versionInfo.ProductName,
					"Claude Code",
					StringComparison.Ordinal) ||
				(versionInfo.FileMajorPart < 0) ||
				(versionInfo.FileMinorPart < 0) ||
				(versionInfo.FileBuildPart < 0))
			{
				return false;
			}

			Version version = new(
				versionInfo.FileMajorPart,
				versionInfo.FileMinorPart,
				versionInfo.FileBuildPart);
			return version >= MinimumSafeModeVersion;
		}
		catch
		{
			return false;
		}
	}

	private static ProcessStartInfo CreateBaseStartInfo(
		string executablePath,
		string configDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedExecutablePath) ||
			!Path.IsPathFullyQualified(configDirectory))
		{
			throw new ArgumentException(
				"Claude CLI 執行檔必須是本機絕對路徑，設定目錄必須使用絕對路徑。");
		}

		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = true,
			FileName = normalizedExecutablePath,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = StrictUtf8Encoding,
			StandardOutputEncoding = StrictUtf8Encoding,
			UseShellExecute = false,
			WorkingDirectory = configDirectory
		};

		ClaudeProcessEnvironment.Apply(startInfo, configDirectory);
		return startInfo;
	}

	private static bool ContainsOnlyZeroNumericValues(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (!ContainsOnlyZeroNumericValues(property.Value))
					{
						return false;
					}
				}

				return true;
			case JsonValueKind.Number:
				return IsExactJsonZero(element);
			default:
				return false;
		}
	}

	private static void EnsureNoDuplicateProperties(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				HashSet<string> propertyNames = new(StringComparer.Ordinal);

				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (!propertyNames.Add(property.Name))
					{
						throw new InvalidDataException(
							$"Claude CLI JSON 含有重複欄位：{property.Name}。");
					}

					EnsureNoDuplicateProperties(property.Value);
				}

				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in element.EnumerateArray())
				{
					EnsureNoDuplicateProperties(item);
				}

				break;
		}
	}

	private static IEnumerable<string> GetExecutableCandidates()
	{
		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);

		if (!string.IsNullOrWhiteSpace(userProfile))
		{
			yield return Path.Combine(userProfile, ".local", "bin", "claude.exe");
		}

		string applicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.ApplicationData);

		if (!string.IsNullOrWhiteSpace(applicationData))
		{
			yield return Path.Combine(
				applicationData,
				"npm",
				"node_modules",
				"@anthropic-ai",
				"claude-code",
				"bin",
				"claude.exe");
		}

		string? pathValue = Environment.GetEnvironmentVariable("PATH");

		if (string.IsNullOrWhiteSpace(pathValue))
		{
			yield break;
		}

		foreach (string pathEntry in pathValue.Split(Path.PathSeparator))
		{
			string directory = pathEntry.Trim().Trim('"');

			if (!string.IsNullOrWhiteSpace(directory) &&
				Path.IsPathFullyQualified(directory))
			{
				yield return Path.Combine(directory, "claude.exe");
			}
		}
	}

	private static bool HasStringValue(
		JsonElement element,
		string propertyName,
		string expectedValue)
	{
		return element.TryGetProperty(propertyName, out JsonElement property) &&
			(property.ValueKind == JsonValueKind.String) &&
			string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal);
	}

	private static bool HasOptionalBoundedNonEmptyStringProperty(
		JsonElement element,
		string propertyName,
		int maximumLength)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return true;
		}

		if (property.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		string value = property.GetString() ?? string.Empty;
		return (value.Length > 0) &&
			(value.Length <= maximumLength) &&
			!value.Any(char.IsControl);
	}

	private static bool HasOptionalNonNegativeInt64Property(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return true;
		}

		return property.TryGetInt64(out long value) &&
			(value >= 0);
	}

	private static bool HasOptionalNullProperty(
		JsonElement element,
		string propertyName)
	{
		return !element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind == JsonValueKind.Null);
	}

	private static bool HasOptionalStringValue(
		JsonElement element,
		string propertyName,
		string expectedValue)
	{
		return !element.TryGetProperty(propertyName, out JsonElement property) ||
			((property.ValueKind == JsonValueKind.String) &&
			 string.Equals(
				property.GetString(),
				expectedValue,
				StringComparison.Ordinal));
	}

	private static bool HasOptionalUuidProperty(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return true;
		}

		return (property.ValueKind == JsonValueKind.String) &&
			Guid.TryParseExact(property.GetString(), "D", out _);
	}

	private static bool ContainsOnlyProperties(
		JsonElement element,
		IReadOnlySet<string> allowedPropertyNames)
	{
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!allowedPropertyNames.Contains(property.Name))
			{
				return false;
			}
		}

		return true;
	}

	private static string? ReadOptionalAccountIdentity(JsonElement element)
	{
		if (!element.TryGetProperty("email", out JsonElement emailElement) ||
			(emailElement.ValueKind == JsonValueKind.Null) ||
			(emailElement.ValueKind != JsonValueKind.String))
		{
			return null;
		}

		string identity = emailElement.GetString()?.Trim() ?? string.Empty;

		return (identity.Length > 0) &&
			(identity.Length <= MaximumAccountIdentityLength) &&
			!identity.Any(char.IsControl)
			? identity
			: null;
	}

	private static bool HasSafeUsageShape(JsonElement usageElement)
	{
		foreach (JsonProperty property in usageElement.EnumerateObject())
		{
			bool isSafe = property.Name switch
			{
				"input_tokens" or
				"output_tokens" or
				"cache_read_input_tokens" or
				"cache_creation_input_tokens" => IsExactJsonZero(property.Value),
				"output_tokens_details" or
				"server_tool_use" or
				"cache_creation" =>
					(property.Value.ValueKind == JsonValueKind.Object) &&
					ContainsOnlyZeroNumericValues(property.Value),
				"service_tier" or
				"inference_geo" or
				"speed" => property.Value.ValueKind == JsonValueKind.String,
				"iterations" =>
					(property.Value.ValueKind == JsonValueKind.Array) &&
					(property.Value.GetArrayLength() == 0),
				_ => false
			};

			if (!isSafe)
			{
				return false;
			}
		}

		return true;
	}

	private static bool HasZeroNumericProperty(
		JsonElement element,
		string propertyName)
	{
		return element.TryGetProperty(propertyName, out JsonElement property) &&
			IsExactJsonZero(property);
	}

	private static bool IsExactJsonZero(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Number)
		{
			return false;
		}

		string number = element.GetRawText();

		foreach (char character in number)
		{
			if ((character == 'e') || (character == 'E'))
			{
				break;
			}

			if ((character >= '1') && (character <= '9'))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsFormatFailure(Exception exception)
	{
		return (exception is DecoderFallbackException) ||
			(exception is InvalidDataException) ||
			(exception is JsonException);
	}

	private async Task BeginSafetyTrackedAttemptAsync(
		Guid accountId,
		AccountSafetyState safetyState,
		string? accountIdentity,
		Guid? attemptId,
		CancellationToken cancellationToken)
	{
		ClaudeUsageSafetyAttemptPhase attemptPhase = attemptId is null
			? ClaudeUsageSafetyAttemptPhase.Uncontained
			: ClaudeUsageSafetyAttemptPhase.Prepared;
		ClaudeUsageSafetyState attemptState;

		lock (safetyState.SyncRoot)
		{
			string? identity = !string.IsNullOrWhiteSpace(accountIdentity)
				? accountIdentity
				: safetyState.AccountIdentity;
			attemptState = new ClaudeUsageSafetyState(
				safetyState.AutomaticRevalidationAttemptCount,
				identity,
				InterruptedAttemptFailureReason,
				ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase: attemptPhase);
		}

		if (_safetyStateStore is not null)
		{
			try
			{
				await RunSafetyStateOperationAsync(
					token => _safetyStateStore.SaveAsync(
						accountId,
						attemptState,
						token),
					cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				SafetyFailureSnapshot safetyFailure =
					SetSafetyStateUnavailable(
						safetyState,
						accountIdentity,
						exception: exception);
				throw CreateSafetyException(safetyFailure, exception);
			}
		}

		lock (safetyState.SyncRoot)
		{
			if (!string.IsNullOrWhiteSpace(accountIdentity))
			{
				safetyState.AccountIdentity = accountIdentity;
			}

			safetyState.AttemptId = attemptId;
			safetyState.AttemptPhase = attemptPhase;
			safetyState.IsAttemptInProgress = true;
		}
	}

	private async Task MarkSafetyTrackedAttemptStartedAsync(
		Guid accountId,
		AccountSafetyState safetyState,
		Guid attemptId,
		CancellationToken cancellationToken)
	{
		IClaudeUsageSafetyStateStore safetyStateStore =
			_safetyStateStore ??
			throw new InvalidOperationException(
				"Claude durable safety state is unavailable.");
		ClaudeUsageSafetyState startedState;

		lock (safetyState.SyncRoot)
		{
			if (!safetyState.IsAttemptInProgress ||
				safetyState.AttemptId != attemptId ||
				safetyState.AttemptPhase !=
					ClaudeUsageSafetyAttemptPhase.Prepared)
			{
				throw new InvalidOperationException(
					"Claude contained usage attempt state changed before process resume.");
			}

			startedState = new ClaudeUsageSafetyState(
				safetyState.AutomaticRevalidationAttemptCount,
				safetyState.AccountIdentity,
				InterruptedAttemptFailureReason,
				ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase:
					ClaudeUsageSafetyAttemptPhase.StartedContained);
		}

		await RunSafetyStateOperationAsync(
			token => safetyStateStore.SaveAsync(
				accountId,
				startedState,
				token),
			cancellationToken).ConfigureAwait(false);

		lock (safetyState.SyncRoot)
		{
			if (!safetyState.IsAttemptInProgress ||
				safetyState.AttemptId != attemptId)
			{
				throw new InvalidOperationException(
					"Claude contained usage attempt state changed during process startup.");
			}

			safetyState.AttemptPhase =
				ClaudeUsageSafetyAttemptPhase.StartedContained;
		}
	}

	private async Task<ProcessResult> RunCrashContainedUsageCommandAsync(
		Guid accountId,
		Guid attemptId,
		ProcessStartInfo startInfo,
		AccountSafetyState safetyState,
		CancellationToken cancellationToken)
	{
		Func<
			ProcessStartInfo,
			string,
			Func<CancellationToken, Task>,
			CancellationToken,
			Task<ProcessResult>> processRunner =
			_containedUsageProcessRunner ??
			throw new InvalidOperationException(
				"Claude crash-contained usage runner is unavailable.");

		return await Task.Run(
			() => processRunner(
				startInfo,
				CreateContainedUsageJobName(attemptId),
				resumeToken => MarkSafetyTrackedAttemptStartedAsync(
					accountId,
					safetyState,
					attemptId,
					resumeToken),
				cancellationToken),
			CancellationToken.None).ConfigureAwait(false);
	}

	private static async Task<ProcessResult>
		RunCrashContainedUsageProcessAsync(
			WindowsAntigravityOfficialPrintProcessRunner processRunner,
			ProcessStartInfo startInfo,
			string jobName,
			Func<CancellationToken, Task> beforeResumeAsync,
			CancellationToken cancellationToken)
	{
		try
		{
			using AntigravityOfficialPrintProcessResult result =
				await processRunner.RunAsync(
					startInfo,
					jobName,
					beforeResumeAsync,
					cancellationToken);

			if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
			{
				throw new InvalidOperationException(
					"Claude CLI 輸出超過安全大小上限。");
			}

			string standardOutput =
				StrictUtf8Encoding.GetString(result.Stdout.Span);
			string standardError = result.HasStderr
				? "Claude CLI wrote to standard error."
				: string.Empty;
			return new ProcessResult(
				result.ExitCode,
				standardOutput,
				standardError);
		}
		catch (AntigravityOfficialPrintProcessRunException exception)
		{
			throw TranslateContainedUsageProcessFailure(
				exception,
				cancellationToken);
		}
	}

	internal static Exception TranslateContainedUsageProcessFailure(
		AntigravityOfficialPrintProcessRunException exception,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(exception);

		if (exception.WasProcessStarted && !exception.WasTerminationConfirmed)
		{
			return new ContainedUsageCleanupUnconfirmedException(exception);
		}

		if (exception.InnerException is OperationCanceledException &&
			cancellationToken.IsCancellationRequested)
		{
			return new OperationCanceledException(
				"Claude crash-contained usage process was canceled.",
				exception,
				cancellationToken);
		}

		return new InvalidOperationException(
			"Claude crash-contained usage process failed.",
			exception);
	}

	private static void ThrowIfExplicitUsageActivity(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		if (root.TryGetProperty("num_turns", out JsonElement turns) &&
			turns.TryGetInt32(out int turnCount) &&
			(turnCount != 0))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 turn。");
		}

		if (root.TryGetProperty(
				"queued_turn_count",
				out JsonElement queuedTurnCount) &&
			(queuedTurnCount.ValueKind == JsonValueKind.Number) &&
			!IsExactJsonZero(queuedTurnCount))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 queued turn。");
		}

		if (root.TryGetProperty("total_cost_usd", out JsonElement cost) &&
			(cost.ValueKind == JsonValueKind.Number) &&
			!IsExactJsonZero(cost))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 cost。");
		}

		if (root.TryGetProperty("usage", out JsonElement usage) &&
			HasExplicitNonZeroNumericValue(usage))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 token usage。");
		}

		if (root.TryGetProperty("modelUsage", out JsonElement modelUsage) &&
			HasExplicitNonZeroNumericValue(modelUsage))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 model usage。");
		}

		if (root.TryGetProperty("subagent_stats", out JsonElement subagentStats) &&
			HasExplicitNonZeroNumericValue(subagentStats))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了非零 subagent activity。");
		}

		if (root.TryGetProperty(
				"permission_denials",
				out JsonElement permissionDenials) &&
			(permissionDenials.ValueKind == JsonValueKind.Array) &&
			(permissionDenials.GetArrayLength() != 0))
		{
			throw CreateExplicitUsageActivityException(
				"Claude `/usage` 回報了 permission denial。");
		}
	}

	private static InvalidDataException CreateExplicitUsageActivityException(
		string message)
	{
		InvalidDataException exception = new(message);
		exception.Data[ExplicitUsageActivityDataKey] = true;
		return exception;
	}

	private static bool IsExplicitUsageActivity(Exception exception)
	{
		return exception is InvalidDataException &&
			exception.Data[ExplicitUsageActivityDataKey] is true;
	}

	private static bool HasExplicitNonZeroNumericValue(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				return element.EnumerateObject().Any(
					property => HasExplicitNonZeroNumericValue(property.Value));
			case JsonValueKind.Array:
				return element.EnumerateArray().Any(HasExplicitNonZeroNumericValue);
			case JsonValueKind.Number:
				return !IsExactJsonZero(element);
			default:
				return false;
		}
	}

	private async Task ClearSafetyFailureAsync(
		Guid accountId,
		AccountSafetyState safetyState)
	{
		if (_safetyStateStore is not null)
		{
			try
			{
				await ClearPersistedSafetyStateWithRetryAsync(
					accountId,
					CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				SafetyFailureSnapshot safetyFailure =
					SetSafetyStateUnavailable(
						safetyState,
						accountIdentity: null,
						exception: exception);
				throw CreateSafetyException(safetyFailure, exception);
			}
		}

		lock (safetyState.SyncRoot)
		{
			safetyState.AutomaticRevalidationAttemptCount = 0;
			safetyState.AccountIdentity = null;
			safetyState.DiagnosticCliVersion = null;
			safetyState.FailureReason = null;
			safetyState.AttemptId = null;
			safetyState.AttemptPhase = null;
			safetyState.IsAttemptInProgress = false;
			safetyState.IsHydrated = true;
			safetyState.RecoveryMode =
				ClaudeUsageSafetyRecoveryMode.Healthy;
			safetyState.RetryNotBefore = null;
		}
	}

	private static ClaudeUsageSafetyException CreateSafetyException(
		SafetyFailureSnapshot safetyFailure,
		Exception? innerException = null,
		ClaudeSubscriptionContext? subscriptionContext = null)
	{
		bool canRetryAutomatically =
			safetyFailure.AutomaticRetryNotBefore is not null;
		string recoveryMessage = canRetryAutomatically
			? "AI Usage 稍後會自動再試，並保留上次成功讀取的資料。"
			: "AI Usage 不會自動再試。請按「重新檢查 Claude 用量」再試一次。";
		string message = $"{safetyFailure.FailureReason}{recoveryMessage}";

		ClaudeUsageSafetyException exception = innerException is null
			? new ClaudeUsageSafetyException(
				message,
				safetyFailure.AccountIdentity,
				safetyFailure.AutomaticRetryNotBefore,
				subscriptionContext)
			: new ClaudeUsageSafetyException(
				message,
				innerException,
				safetyFailure.AccountIdentity,
				safetyFailure.AutomaticRetryNotBefore,
				subscriptionContext);

		if (CliVersionPolicies.TryAssessClaudeDiagnostic(
				safetyFailure.DiagnosticCliVersion,
				out CliVersionEvidence? versionEvidence))
		{
			exception.AttachVersionEvidence(versionEvidence);
		}

		return exception;
	}

	private static SafetyFailureSnapshot CreateSafetyFailureSnapshot(
		AccountSafetyState safetyState)
	{
		return new SafetyFailureSnapshot(
			safetyState.IsAttemptInProgress
				? InterruptedAttemptFailureReason
				: safetyState.FailureReason ??
					"無法確認上次 Claude 用量檢查是否完成。",
			safetyState.AccountIdentity,
			!safetyState.IsAttemptInProgress &&
				safetyState.RecoveryMode ==
					ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending
				? safetyState.RetryNotBefore
				: null,
			safetyState.IsAttemptInProgress
				? null
				: safetyState.DiagnosticCliVersion);
	}

	private static TimeSpan GetAutomaticRevalidationDelay(
		int completedAutomaticAttempts)
	{
		int delayMinutes = completedAutomaticAttempts switch
		{
			0 => 1,
			1 => 2,
			_ => 4
		};
		return TimeSpan.FromMinutes(delayMinutes);
	}

	private async Task EnsureSafetyStateHydratedAsync(
		Guid accountId,
		AccountSafetyState safetyState,
		CancellationToken cancellationToken)
	{
		lock (safetyState.SyncRoot)
		{
			if (safetyState.IsHydrated)
			{
				return;
			}
		}

		ClaudeUsageSafetyState? persistedState = null;

		if (_safetyStateStore is not null)
		{
			try
			{
				persistedState = await RunSafetyStateOperationAsync(
					token => _safetyStateStore.LoadAsync(accountId, token),
					cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				SafetyFailureSnapshot safetyFailure =
					SetSafetyStateUnavailable(
						safetyState,
						accountIdentity: null,
						exception: exception);
				throw CreateSafetyException(safetyFailure, exception);
			}
		}

		bool wasLegacyStateNormalized = false;

		if (persistedState is not null)
		{
			if (CanMigrateLegacyManualRetryState(persistedState))
			{
				persistedState = persistedState with
				{
					RecoveryMode =
						ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
					RetryNotBefore = _timeProvider.GetUtcNow().ToUniversalTime() +
						GetAutomaticRevalidationDelay(
							persistedState.AutomaticRevalidationAttemptCount),
					LegacySourceSchemaVersion = null
				};
				wasLegacyStateNormalized = true;
			}
			else if (RequiresLegacyQuiescenceConfirmation(persistedState))
			{
				persistedState = persistedState with
				{
					RecoveryMode =
						ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
					RetryNotBefore = null
				};
				wasLegacyStateNormalized = true;
			}

			if (wasLegacyStateNormalized && _safetyStateStore is not null)
			{
				try
				{
					await RunSafetyStateOperationAsync(
						token => _safetyStateStore.SaveAsync(
							accountId,
							persistedState,
							token),
						cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					SafetyFailureSnapshot safetyFailure =
						SetSafetyStateUnavailable(
							safetyState,
							persistedState.AccountIdentity,
							exception);
					throw CreateSafetyException(safetyFailure, exception);
				}
			}
		}

		lock (safetyState.SyncRoot)
		{
			if (persistedState is null)
			{
				safetyState.AutomaticRevalidationAttemptCount = 0;
				safetyState.AccountIdentity = null;
				safetyState.DiagnosticCliVersion = null;
				safetyState.FailureReason = null;
				safetyState.AttemptId = null;
				safetyState.AttemptPhase = null;
				safetyState.IsAttemptInProgress = false;
				safetyState.RecoveryMode =
					ClaudeUsageSafetyRecoveryMode.Healthy;
				safetyState.RetryNotBefore = null;
			}
			else
			{
				safetyState.AutomaticRevalidationAttemptCount =
					persistedState.AutomaticRevalidationAttemptCount;
				safetyState.AccountIdentity = persistedState.AccountIdentity;
				safetyState.DiagnosticCliVersion =
					persistedState.DiagnosticCliVersion;
				safetyState.FailureReason = persistedState.FailureReason;
				safetyState.AttemptId = persistedState.AttemptId;
				safetyState.AttemptPhase = persistedState.AttemptPhase;
				safetyState.IsAttemptInProgress =
					persistedState.RecoveryMode ==
						ClaudeUsageSafetyRecoveryMode.AttemptInProgress;
				safetyState.RecoveryMode = safetyState.IsAttemptInProgress
					? ClaudeUsageSafetyRecoveryMode.Healthy
					: persistedState.RecoveryMode;
				safetyState.RetryNotBefore = persistedState.RetryNotBefore;
			}

			safetyState.IsHydrated = true;
		}
	}

	private static bool CanMigrateLegacyManualRetryState(
		ClaudeUsageSafetyState state)
	{
		return state.LegacySourceSchemaVersion == 1 &&
			state.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired &&
			state.FailureReason.Contains(
				"的 `/usage` exit code 與安全結果互相衝突。",
				StringComparison.Ordinal);
	}

	private static bool RequiresLegacyQuiescenceConfirmation(
		ClaudeUsageSafetyState state)
	{
		return state.LegacySourceSchemaVersion == 1 &&
			state.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending &&
			!state.FailureReason.Contains(
				"的 `/usage` exit code 與安全結果互相衝突。",
				StringComparison.Ordinal);
	}

	private async Task RedactPendingAttemptAccountIdentityAsync(
		Guid accountId,
		AccountSafetyState safetyState,
		CancellationToken cancellationToken)
	{
		ClaudeUsageSafetyState? redactedState;

		lock (safetyState.SyncRoot)
		{
			if (!safetyState.IsAttemptInProgress ||
				safetyState.AccountIdentity is null)
			{
				return;
			}

			redactedState = safetyState.AttemptId is null
				? new ClaudeUsageSafetyState(
					safetyState.AutomaticRevalidationAttemptCount,
					null,
					safetyState.FailureReason ?? InterruptedAttemptFailureReason,
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
					RetryNotBefore: null)
				: new ClaudeUsageSafetyState(
					safetyState.AutomaticRevalidationAttemptCount,
					null,
					safetyState.FailureReason ?? InterruptedAttemptFailureReason,
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
					RetryNotBefore: null,
					safetyState.AttemptId,
					safetyState.AttemptPhase);
		}

		if (_safetyStateStore is not null)
		{
			await RunSafetyStateOperationAsync(
				token => _safetyStateStore.SaveAsync(
					accountId,
					redactedState,
					token),
				cancellationToken).ConfigureAwait(false);
		}

		lock (safetyState.SyncRoot)
		{
			safetyState.AccountIdentity = null;
			if (redactedState.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired)
			{
				safetyState.DiagnosticCliVersion = null;
				safetyState.FailureReason = redactedState.FailureReason;
				safetyState.AttemptId = null;
				safetyState.AttemptPhase = null;
				safetyState.IsAttemptInProgress = false;
				safetyState.RecoveryMode = redactedState.RecoveryMode;
				safetyState.RetryNotBefore = null;
			}
		}
	}

	private async Task RecoverInterruptedAttemptAsync(
		Guid accountId,
		AccountSafetyState safetyState)
	{
		ClaudeUsageSafetyState recoveredState;
		Guid? attemptId;
		string? accountIdentity;
		int automaticAttemptCount;

		lock (safetyState.SyncRoot)
		{
			if (!safetyState.IsAttemptInProgress)
			{
				return;
			}

			attemptId = safetyState.AttemptId;
			accountIdentity = safetyState.AccountIdentity;
			automaticAttemptCount =
				safetyState.AutomaticRevalidationAttemptCount;
		}

		if (attemptId is Guid containedAttemptId)
		{
			bool wasRecoveryConfirmed;

			try
			{
				wasRecoveryConfirmed =
					await _containedUsageAttemptRecovery(
						containedAttemptId,
						ProcessCleanupTimeout,
						CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				throw CreateSafetyException(
					new SafetyFailureSnapshot(
						InterruptedAttemptFailureReason,
						accountIdentity,
						_timeProvider.GetUtcNow().ToUniversalTime() +
							TimeSpan.FromMinutes(1)),
					exception);
			}

			if (!wasRecoveryConfirmed)
			{
				throw CreateSafetyException(
					new SafetyFailureSnapshot(
						InterruptedAttemptFailureReason,
						accountIdentity,
						_timeProvider.GetUtcNow().ToUniversalTime() +
							TimeSpan.FromMinutes(1)));
			}

			ReleaseContainedAttemptExecutableLease(containedAttemptId);

			recoveredState = new ClaudeUsageSafetyState(
				automaticAttemptCount,
				accountIdentity,
				InterruptedAttemptFailureReason,
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
				RetryNotBefore:
					_timeProvider.GetUtcNow().ToUniversalTime() +
						GetAutomaticRevalidationDelay(automaticAttemptCount));
		}
		else
		{
			recoveredState = new ClaudeUsageSafetyState(
				automaticAttemptCount,
				accountIdentity,
				InterruptedAttemptFailureReason,
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
				RetryNotBefore: null);
		}

		if (_safetyStateStore is not null)
		{
			try
			{
				await RunSafetyStateOperationAsync(
					token => _safetyStateStore.SaveAsync(
						accountId,
						recoveredState,
						token),
					CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				SafetyFailureSnapshot safetyFailure =
					SetSafetyStateUnavailable(
						safetyState,
						recoveredState.AccountIdentity,
						exception);
				throw CreateSafetyException(safetyFailure, exception);
			}
		}

		lock (safetyState.SyncRoot)
		{
			safetyState.AutomaticRevalidationAttemptCount =
				recoveredState.AutomaticRevalidationAttemptCount;
			safetyState.AccountIdentity = recoveredState.AccountIdentity;
			safetyState.DiagnosticCliVersion = null;
			safetyState.FailureReason = recoveredState.FailureReason;
			safetyState.AttemptId = null;
			safetyState.AttemptPhase = null;
			safetyState.IsAttemptInProgress = false;
			safetyState.RecoveryMode = recoveredState.RecoveryMode;
			safetyState.RetryNotBefore = recoveredState.RetryNotBefore;
		}
	}

	private static string ReadRequiredAuthStatusClassification(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement valueElement) ||
			(valueElement.ValueKind != JsonValueKind.String))
		{
			throw new InvalidDataException(
				"Claude `auth status` 暫時缺少可驗證的訂閱資訊。AI Usage 稍後會自動再試。");
		}

		string value = valueElement.GetString() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(value) ||
			(value.Length > MaximumAuthStatusClassificationLength) ||
			!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
			value.Any(char.IsControl))
		{
			throw new InvalidDataException(
				"Claude `auth status` 暫時缺少可驗證的訂閱資訊。AI Usage 稍後會自動再試。");
		}

		return value;
	}

	private static string? ReadOptionalBoundedString(
		JsonElement element,
		string propertyName,
		int maximumLength)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement valueElement) ||
			(valueElement.ValueKind == JsonValueKind.Null) ||
			(valueElement.ValueKind != JsonValueKind.String))
		{
			return null;
		}

		string value = valueElement.GetString()?.Trim() ?? string.Empty;
		return (value.Length > 0) &&
			(value.Length <= maximumLength) &&
			!value.Any(char.IsControl)
			? value
			: null;
	}

	private void QuarantineContainedAttemptExecutableLease(
		Guid attemptId,
		IDisposable executableLease)
	{
		if (!_containedAttemptExecutableLeases.TryAdd(
				attemptId,
				executableLease))
		{
			executableLease.Dispose();
		}
	}

	private void ReleaseContainedAttemptExecutableLease(Guid attemptId)
	{
		if (_containedAttemptExecutableLeases.TryRemove(
				attemptId,
				out IDisposable? executableLease))
		{
			executableLease.Dispose();
		}
	}

	internal static string CreateContainedUsageJobName(Guid attemptId)
	{
		if (attemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Claude contained usage attempt identifier cannot be empty.",
				nameof(attemptId));
		}

		return ContainedUsageJobNamePrefix + attemptId.ToString("N");
	}

	internal static async Task<bool> TryRecoverContainedUsageAttemptAsync(
		Guid attemptId,
		TimeSpan cleanupTimeout,
		CancellationToken cancellationToken = default)
	{
		if (cleanupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
		}

		string jobName = CreateContainedUsageJobName(attemptId);

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

		recoveredJob.Terminate(ContainedUsageForcedExitCode);
		return await recoveredJob.WaitForPositiveEmptyConfirmationAsync(
			cleanupTimeout,
			cancellationToken);
	}

	private async Task<SafetyFailureSnapshot> LatchSafetyFailureAsync(
		Guid accountId,
		AccountSafetyState safetyState,
		string? accountIdentity,
		string failureReason,
		bool isAutomaticRevalidation,
		bool canRetryAutomatically,
		DateTimeOffset failedAt,
		string? diagnosticCliVersion = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

		lock (safetyState.SyncRoot)
		{
			safetyState.AttemptId = null;
			safetyState.AttemptPhase = null;
			safetyState.IsAttemptInProgress = false;

			if (!string.IsNullOrWhiteSpace(accountIdentity))
			{
				safetyState.AccountIdentity = accountIdentity;
			}

			if (!canRetryAutomatically)
			{
				safetyState.DiagnosticCliVersion = diagnosticCliVersion;
				safetyState.FailureReason = failureReason;
				safetyState.RecoveryMode =
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired;
				safetyState.RetryNotBefore = null;
			}
			else
			{
				if (safetyState.RecoveryMode ==
					ClaudeUsageSafetyRecoveryMode.Healthy)
				{
					safetyState.AutomaticRevalidationAttemptCount = 0;
					safetyState.DiagnosticCliVersion = diagnosticCliVersion;
					safetyState.FailureReason = failureReason;
				}

				if (isAutomaticRevalidation)
				{
					safetyState.AutomaticRevalidationAttemptCount = Math.Min(
						MaximumAutomaticRevalidationBackoffStep,
						safetyState.AutomaticRevalidationAttemptCount + 1);
				}

				safetyState.RecoveryMode =
					ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending;
				safetyState.RetryNotBefore = failedAt.ToUniversalTime() +
					GetAutomaticRevalidationDelay(
						safetyState.AutomaticRevalidationAttemptCount);
			}
		}

		if (_safetyStateStore is not null)
		{
			ClaudeUsageSafetyState persistedState;

			lock (safetyState.SyncRoot)
			{
				persistedState = new ClaudeUsageSafetyState(
					safetyState.AutomaticRevalidationAttemptCount,
					safetyState.AccountIdentity,
					safetyState.FailureReason ?? failureReason,
					safetyState.RecoveryMode,
					safetyState.RetryNotBefore,
					DiagnosticCliVersion:
						safetyState.DiagnosticCliVersion);
			}

			try
			{
				await RunSafetyStateOperationAsync(
					token => _safetyStateStore.SaveAsync(
						accountId,
						persistedState,
						token),
					CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				SafetyFailureSnapshot safetyFailure =
					SetSafetyStateUnavailable(
						safetyState,
						accountIdentity,
						exception);
				throw CreateSafetyException(safetyFailure, exception);
			}
		}

		lock (safetyState.SyncRoot)
		{
			return CreateSafetyFailureSnapshot(safetyState);
		}
	}

	private async Task RunSafetyStateOperationAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken)
	{
		await RunSafetyStateOperationAsync(
			async token =>
			{
				await operation(token).ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async Task ClearPersistedSafetyStateWithRetryAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (_safetyStateStore is null)
		{
			return;
		}

		await RunSafetyStateOperationAsync(
			async token =>
			{
				for (int attempt = 0; ; attempt++)
				{
					try
					{
						await _safetyStateStore.ClearAsync(
							accountId,
							token).ConfigureAwait(false);
						return;
					}
					catch (ClaudeUsageSafetyStateCleanupPendingException)
					{
						// The durable clear intent is already committed. Later store
						// access continues best-effort physical cleanup.
						return;
					}
					catch (Exception exception) when (
						(attempt < SafetyStateClearRetryDelays.Length) &&
						IsTransientSafetyStateClearFailure(exception))
					{
						await Task.Delay(
							SafetyStateClearRetryDelays[attempt],
							token).ConfigureAwait(false);
					}
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	private static bool IsTransientSafetyStateClearFailure(Exception exception)
	{
		return (exception is IOException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException);
	}

	private static async Task<T> RunSafetyStateOperationAsync<T>(
		Func<CancellationToken, Task<T>> operation,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(
			SafetyStateOperationTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			return await operation(linkedSource.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new TimeoutException("Claude 用量安全狀態存取逾時。");
		}
	}

	private SafetyFailureSnapshot SetSafetyStateUnavailable(
		AccountSafetyState safetyState,
		string? accountIdentity,
		Exception exception)
	{
		bool canRetryAutomatically =
			exception is not InvalidDataException &&
			(exception is IOException || exception is TimeoutException);

		lock (safetyState.SyncRoot)
		{
			if (!string.IsNullOrWhiteSpace(accountIdentity))
			{
				safetyState.AccountIdentity = accountIdentity;
			}

			safetyState.FailureReason = SafetyStateUnavailableFailureReason;
			safetyState.DiagnosticCliVersion = null;
			safetyState.AttemptId = null;
			safetyState.AttemptPhase = null;
			safetyState.IsAttemptInProgress = false;
			safetyState.IsHydrated = !canRetryAutomatically;
			safetyState.RecoveryMode = canRetryAutomatically
				? ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending
				: ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired;
			safetyState.RetryNotBefore = canRetryAutomatically
				? _timeProvider.GetUtcNow().ToUniversalTime() +
					TimeSpan.FromMinutes(1)
				: null;
			return CreateSafetyFailureSnapshot(safetyState);
		}
	}

	private static bool IsSupportedSubscriptionType(string? subscriptionType)
	{
		return string.Equals(subscriptionType, "pro", StringComparison.Ordinal) ||
			string.Equals(subscriptionType, "max", StringComparison.Ordinal) ||
			string.Equals(subscriptionType, "team", StringComparison.Ordinal) ||
			string.Equals(subscriptionType, "enterprise", StringComparison.Ordinal);
	}

	private static bool IsUsageInvocationFailure(Exception exception)
	{
		return (exception is DecoderFallbackException) ||
			(exception is IOException) ||
			(exception is InvalidOperationException) ||
			(exception is NotSupportedException) ||
			(exception is OperationCanceledException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception);
	}

	private static JsonDocument ParseJson(string json)
	{
		return JsonDocument.Parse(
			json,
			new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 16
			});
	}

	private static Task<ProcessResult> RunProcessAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken)
	{
		return RunProcessAsync(
			startInfo,
			new ProviderProcessOperationTracker(),
			cancellationToken);
	}

	private static async Task<ProcessResult> RunProcessAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Process process = new()
		{
			StartInfo = startInfo
		};
		using CancellationTokenSource timeoutSource = new(CommandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		bool hasStarted = false;
		bool processOwnershipTransferred = false;
		Task<string> standardOutputTask = Task.FromResult(string.Empty);
		Task<string> standardErrorTask = Task.FromResult(string.Empty);

		try
		{
			if (!process.Start())
			{
				throw new InvalidOperationException("無法啟動 Claude Code CLI。");
			}

			hasStarted = true;
			linkedSource.Token.ThrowIfCancellationRequested();
			process.StandardInput.Close();
			standardOutputTask = ReadBoundedStreamAsync(
				process.StandardOutput.BaseStream,
				MaximumStandardOutputBytes,
				() => TryKillProcess(process, operationTracker),
				linkedSource.Token);
			standardErrorTask = ReadBoundedStreamAsync(
				process.StandardError.BaseStream,
				MaximumStandardErrorBytes,
				() => TryKillProcess(process, operationTracker),
				linkedSource.Token);
			await process.WaitForExitAsync(linkedSource.Token);
			string standardOutput = await standardOutputTask.WaitAsync(linkedSource.Token);
			string standardError = await standardErrorTask.WaitAsync(linkedSource.Token);
			return new ProcessResult(process.ExitCode, standardOutput, standardError);
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (hasStarted)
			{
				processOwnershipTransferred = true;
				await CleanupProcessAsync(
					process,
					standardOutputTask,
					standardErrorTask,
					operationTracker);
			}

			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new TimeoutException("Claude Code CLI 執行逾時。");
		}
		catch
		{
			if (hasStarted)
			{
				processOwnershipTransferred = true;
				await CleanupProcessAsync(
					process,
					standardOutputTask,
					standardErrorTask,
					operationTracker);
			}

			throw;
		}
		finally
		{
			if (!processOwnershipTransferred)
			{
				process.Dispose();
			}
		}
	}

	private static async Task<string> ReadBoundedStreamAsync(
		Stream stream,
		int maximumBytes,
		Action terminateProcess,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[8192];
		using MemoryStream content = new(Math.Min(maximumBytes, buffer.Length));

		try
		{
			while (true)
			{
				int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

				if (bytesRead == 0)
				{
					break;
				}

				if ((content.Length + bytesRead) > maximumBytes)
				{
					throw new InvalidDataException("Claude CLI 輸出超過安全大小上限。");
				}

				await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
			}

			return StrictUtf8Encoding.GetString(content.GetBuffer(), 0, checked((int)content.Length));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			terminateProcess();
			throw;
		}
	}

	private static async Task CleanupProcessAsync(
		Process process,
		Task standardOutputTask,
		Task standardErrorTask,
		ProviderProcessOperationTracker operationTracker)
	{
		TryKillProcess(process, operationTracker);
		Task cleanupTask = CompleteProcessCleanupAsync(
			process,
			standardOutputTask,
			standardErrorTask,
			operationTracker);
		operationTracker.Track(cleanupTask);
		ProviderProcessExecution.ObserveFault(cleanupTask);

		try
		{
			await cleanupTask.WaitAsync(ProcessCleanupTimeout);
		}
		catch (TimeoutException)
		{
			// Tracker 會讓 executable lease 與 account gate 跟著 late cleanup 存活。
		}
	}

	private static async Task CompleteProcessCleanupAsync(
		Process process,
		Task standardOutputTask,
		Task standardErrorTask,
		ProviderProcessOperationTracker operationTracker)
	{
		try
		{
			await Task.WhenAll(
				ProviderProcessExecution.WaitForConfirmedExitAsync(
					process,
					operationTracker),
				ObserveTaskAsync(standardOutputTask),
				ObserveTaskAsync(standardErrorTask));
		}
		finally
		{
			process.Dispose();
		}
	}

	private static async Task ObserveTaskAsync(Task task)
	{
		try
		{
			await task;
		}
		catch
		{
			// Cleanup observes and intentionally discards partial-output failures.
		}
	}

	private static void TryKillProcess(
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
			operationTracker.MarkContainmentCompromised();
		}
	}

}
