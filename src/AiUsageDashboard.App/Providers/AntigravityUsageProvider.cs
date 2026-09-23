using System.Globalization;
using System.IO;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal sealed class AntigravityUsageProvider :
	IUsageProvider,
	IUsageProviderInvalidator
{
	private sealed class AutomaticRepairState
	{
		internal AutomaticRepairState(string expectedIdentity)
		{
			ExpectedIdentity = expectedIdentity;
		}

		internal string ExpectedIdentity { get; }

		internal AutomaticRepairFailurePresentation? Failure { get; set; }

		internal DateTimeOffset NextAttemptAt { get; set; } =
			DateTimeOffset.MinValue;
	}

	private sealed record WindowDescriptor(
		string StableWindowId,
		string Label);

	private sealed record AutomaticRepairFailurePresentation(
		string Error,
		UsageRecoveryAction RecoveryAction);

	private const string AutomaticRepairIdentityRejectedMessage =
		"Antigravity 回報的帳號與目前連接的帳號不同，已忽略這次用量。請重新連接正確的帳號。";
	private const string AutomaticRepairSetupRejectedMessage =
		"AI Usage 無法恢復 Antigravity 用量檢查。請重新連接 Antigravity 帳號；既有登入不受影響。";
	private const string AutomaticRepairCliUnavailableMessage =
		"找不到 Antigravity CLI 或執行所需檔案。請確認 Antigravity 已安裝且可正常啟動；既有登入不受影響。";
	private const string AutomaticRepairUnsupportedBuildMessage =
		"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。";

	private const string CaptureFailureMessage =
		"暫時無法讀取 Antigravity 用量，稍後會自動再試。";
	private const string CaptureRejectedMessage =
		"Antigravity 用量檢查未完成，稍後會自動再試。";
	private const string AutomaticRevalidationPendingMessage =
		"Antigravity 用量檢查尚未完成。AI Usage 稍後會自動再試，並保留現有帳號與用量資料。";
	private const string NotConfiguredMessage =
		"尚未連接 Antigravity 帳號。";
	private const string ProfileRejectedMessage =
		"AI Usage 無法確認 Antigravity 的用量讀取。請重新連接 Antigravity 帳號；既有登入不受影響。";
	private const string ProvenanceRejectedMessage =
		"AI Usage 無法確認這次 Antigravity 用量讀取是否仍符合安全條件，稍後會自動重新檢查；既有登入不受影響。";
	private const string UsageShapeRejectedMessage =
		"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。";
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan AutomaticRepairInterval =
		TimeSpan.FromMinutes(15);
	private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
	private static readonly WindowDescriptor[] ExpectedWindows =
	{
		new("agy.gemini.weekly", "Gemini weekly"),
		new("agy.gemini.rolling-5h", "Gemini rolling 5h"),
		new("agy.claude.weekly", "Claude + GPT weekly"),
		new("agy.claude.rolling-5h", "Claude + GPT rolling 5h")
	};
	private readonly Dictionary<Guid, AutomaticRepairState>
		_automaticRepairStates = new();
	private readonly object _automaticRepairSync = new();
	private readonly SemaphoreSlim _captureGate = new(1, 1);
	private readonly IAntigravityOfficialPrintUsageClient _officialClient;
	private readonly IAntigravityOfficialExecutablePathResolver
		_officialExecutablePathResolver;
	private readonly object _revalidationSync = new();
	private readonly IAntigravityMachineSetupService? _setupService;
	private readonly TimeProvider _timeProvider;
	private Guid? _officialSafetyRevalidationAccountId;

	public ProviderKind Provider => ProviderKind.Antigravity;

	public TimeSpan MinimumRefreshInterval => RefreshInterval;

	internal AntigravityUsageProvider(
		IAntigravityOfficialPrintUsageClient officialClient,
		IAntigravityOfficialExecutablePathResolver
			officialExecutablePathResolver,
		TimeProvider? timeProvider = null,
		IAntigravityMachineSetupService? setupService = null)
	{
		_officialClient = officialClient ??
			throw new ArgumentNullException(nameof(officialClient));
		_officialExecutablePathResolver = officialExecutablePathResolver ??
			throw new ArgumentNullException(
				nameof(officialExecutablePathResolver));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_setupService = setupService;
	}

	internal void ArmOfficialSafetyRevalidation(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity 帳號識別碼不可為空。",
				nameof(accountId));
		}

		lock (_revalidationSync)
		{
			_officialSafetyRevalidationAccountId = accountId;
		}
	}

	public void Invalidate(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity 帳號識別碼不可為空。",
				nameof(accountId));
		}

		ClearAutomaticRepairState(accountId);
	}

	internal void PurgeAccountState(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity 帳號識別碼不可為空。",
				nameof(accountId));
		}

		lock (_revalidationSync)
		{
			if (_officialSafetyRevalidationAccountId == accountId)
			{
				_officialSafetyRevalidationAccountId = null;
			}
		}

		ClearAutomaticRepairState(accountId);
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Antigravity)
		{
			throw new ArgumentException(
				"Antigravity provider 只能讀取 Antigravity 帳號。",
				nameof(account));
		}

		await _captureGate.WaitAsync(cancellationToken);

		try
		{
			string? officialExecutablePath;

			try
			{
				officialExecutablePath =
					_officialExecutablePathResolver.ResolveExecutablePath();
			}
			catch
			{
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					_timeProvider.GetUtcNow(),
					ProfileRejectedMessage,
					UsageRecoveryAction.ReconfigureUsageSource);
			}

			if (officialExecutablePath is null)
			{
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.NotConfigured,
					_timeProvider.GetUtcNow(),
					NotConfiguredMessage,
					UsageRecoveryAction.ReconfigureUsageSource);
			}

			AntigravityProductionUsageResult result;
			bool revalidateOfficialSafetyState = false;
			int didStartSafetyTrackedAttempt = 0;

			try
			{
				revalidateOfficialSafetyState =
					TryConsumeOfficialSafetyRevalidation(account.Id);
				result = revalidateOfficialSafetyState
					? await _officialClient.RevalidateAsync(
						officialExecutablePath,
						() => Interlocked.Exchange(
							ref didStartSafetyTrackedAttempt,
							1),
						cancellationToken)
					: await _officialClient.CaptureAsync(
						officialExecutablePath,
						cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				RestoreUnstartedOfficialSafetyRevalidation(
					account.Id,
					revalidateOfficialSafetyState,
					didStartSafetyTrackedAttempt);
				throw;
			}
			catch
			{
				RestoreUnstartedOfficialSafetyRevalidation(
					account.Id,
					revalidateOfficialSafetyState,
					didStartSafetyTrackedAttempt);
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					_timeProvider.GetUtcNow(),
					CaptureFailureMessage,
					UsageRecoveryAction.Retry);
			}

			RestoreUnstartedOfficialSafetyRevalidation(
				account.Id,
				revalidateOfficialSafetyState,
				didStartSafetyTrackedAttempt);

			DateTimeOffset observedNow = _timeProvider.GetUtcNow();

			if ((result is null) ||
				(!result.IsSuccessful &&
				 (result.FailureKind ==
					AntigravityProductionUsageFailureKind.None)))
			{
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					observedNow,
					CaptureFailureMessage,
					UsageRecoveryAction.Retry);
			}

			if (!result.IsSuccessful)
			{
				UsageSnapshot? repaired = await TryRepairAutomaticallyAsync(
					account,
					result.FailureKind,
					observedNow,
					cancellationToken);

				if (repaired is not null)
				{
					return repaired;
				}

				(string error, UsageRecoveryAction recoveryAction) =
					GetFailurePresentation(
						result.FailureKind,
						result.SafetyFailureReason,
						result.AutomaticRevalidationPending,
						useOfficialPrint: true);
				error = AppendOfficialPrintVersionDiagnostic(
					error,
					result,
					useOfficialPrint: true);
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					observedNow,
					error,
					recoveryAction);
			}

			if ((result.FailureKind !=
					AntigravityProductionUsageFailureKind.None) ||
				!TryCreateMetrics(
					result.Windows,
					observedNow,
					out IReadOnlyList<UsageMetric>? metrics) ||
				(metrics is null))
			{
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					observedNow,
					UsageShapeRejectedMessage,
					UsageRecoveryAction.UpdateApplication);
			}

			ClearAutomaticRepairState(account.Id);

			return new UsageSnapshot(
				account,
				metrics,
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				observedNow,
				observedNow,
				observedNow + StaleAfter,
				ProviderAccountIdentity: result.AccountIdentity);
		}
		finally
		{
			_captureGate.Release();
		}
	}

	private bool TryConsumeOfficialSafetyRevalidation(Guid accountId)
	{
		lock (_revalidationSync)
		{
			if (_officialSafetyRevalidationAccountId != accountId)
			{
				return false;
			}

			_officialSafetyRevalidationAccountId = null;
			return true;
		}
	}

	private void RestoreUnstartedOfficialSafetyRevalidation(
		Guid accountId,
		bool wasConsumed,
		int didStartSafetyTrackedAttempt)
	{
		if (!wasConsumed || (Volatile.Read(ref didStartSafetyTrackedAttempt) != 0))
		{
			return;
		}

		lock (_revalidationSync)
		{
			// Do not overwrite a newer explicit authorization that arrived while
			// the preflight was running.
			_officialSafetyRevalidationAccountId ??= accountId;
		}
	}

	private async Task<UsageSnapshot?> TryRepairAutomaticallyAsync(
		AccountProfile account,
		AntigravityProductionUsageFailureKind failureKind,
		DateTimeOffset observedNow,
		CancellationToken cancellationToken)
	{
		if ((failureKind !=
				AntigravityProductionUsageFailureKind.CaptureRejected) &&
			 (failureKind !=
				AntigravityProductionUsageFailureKind.ProvenanceRejected))
		{
			return null;
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				account.ProviderAccountIdentity,
				out string? expectedIdentity) ||
			(expectedIdentity is null))
		{
			ClearAutomaticRepairState(account.Id);
			return CreateFailureSnapshot(
				account,
				SnapshotStatus.NotConfigured,
				observedNow,
				NotConfiguredMessage,
				UsageRecoveryAction.ConnectAccount);
		}

		if (_setupService is null)
		{
			return null;
		}

		AutomaticRepairState repairState = GetAutomaticRepairState(
			account.Id,
			expectedIdentity);

		if (ShouldThrottleAutomaticRepair(repairState, observedNow))
		{
			return CreateRecordedAutomaticRepairFailureSnapshot(
				account,
				observedNow,
				repairState);
		}

		try
		{
			AntigravityMachineSetupPreparationResult? preparation =
				await _setupService.PrepareAsync(
					new AntigravityMachineSetupConsent(
						IsOfficialUsageReadApproved: true),
					progress: null,
					cancellationToken);

			if (preparation?.Candidate is not
				AntigravityMachineSetupCandidate candidate)
			{
				AutomaticRepairFailurePresentation? failure =
					GetAutomaticRepairFailurePresentation(
						preparation);
				return failure is null
					? CreateRecordedAutomaticRepairFailureSnapshot(
						account,
						_timeProvider.GetUtcNow(),
						repairState)
					: RecordAutomaticRepairFailure(
						account,
						_timeProvider.GetUtcNow(),
						repairState,
						failure);
			}

			await using (candidate)
			{
				if (!ProviderAccountIdentityRules.TryNormalize(
						candidate.AccountIdentity,
						out string? candidateIdentity) ||
					(candidateIdentity is null) ||
					!ProviderAccountIdentityRules.Comparer.Equals(
						candidateIdentity,
						expectedIdentity))
				{
					return RecordAutomaticRepairFailure(
						account,
						_timeProvider.GetUtcNow(),
						repairState,
						new AutomaticRepairFailurePresentation(
							AutomaticRepairIdentityRejectedMessage,
							UsageRecoveryAction.SwitchAccount));
				}

				DateTimeOffset repairedAt = _timeProvider.GetUtcNow();

				if (
					!TryCreateMetrics(
						candidate.Windows,
						repairedAt,
						out IReadOnlyList<UsageMetric>? metrics) ||
					(metrics is null))
				{
					return RecordAutomaticRepairFailure(
						account,
						repairedAt,
						repairState,
						new AutomaticRepairFailurePresentation(
							UsageShapeRejectedMessage,
							UsageRecoveryAction.UpdateApplication));
				}

				try
				{
					await candidate.ApproveAsync(cancellationToken);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					AutomaticRepairFailurePresentation? failure =
						GetAutomaticRepairApprovalFailurePresentation(
							exception);
					return failure is null
						? CreateRecordedAutomaticRepairFailureSnapshot(
							account,
							_timeProvider.GetUtcNow(),
							repairState)
						: RecordAutomaticRepairFailure(
							account,
							_timeProvider.GetUtcNow(),
							repairState,
							failure);
				}

				if (!candidate.IsApproved)
				{
					return CreateRecordedAutomaticRepairFailureSnapshot(
						account,
						_timeProvider.GetUtcNow(),
						repairState);
				}

				ClearAutomaticRepairState(account.Id, repairState);

				return new UsageSnapshot(
					account,
					metrics,
					GetSourceTrust(candidate.SourceKind),
					SnapshotStatus.Ready,
					repairedAt,
					repairedAt,
					repairedAt + StaleAfter,
					ProviderAccountIdentity: candidateIdentity);
			}
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return CreateRecordedAutomaticRepairFailureSnapshot(
				account,
				_timeProvider.GetUtcNow(),
				repairState);
		}
	}

	private UsageSnapshot? CreateRecordedAutomaticRepairFailureSnapshot(
		AccountProfile account,
		DateTimeOffset observedNow,
		AutomaticRepairState repairState)
	{
		AutomaticRepairFailurePresentation? failure;

		lock (_automaticRepairSync)
		{
			failure = repairState.Failure;
		}

		if (failure is null)
		{
			return null;
		}

		return CreateFailureSnapshot(
			account,
			SnapshotStatus.Error,
			observedNow,
			failure.Error,
			failure.RecoveryAction);
	}

	private UsageSnapshot RecordAutomaticRepairFailure(
		AccountProfile account,
		DateTimeOffset observedNow,
		AutomaticRepairState repairState,
		AutomaticRepairFailurePresentation failure)
	{
		lock (_automaticRepairSync)
		{
			repairState.Failure = failure;
		}

		return CreateFailureSnapshot(
			account,
			SnapshotStatus.Error,
			observedNow,
			failure.Error,
			failure.RecoveryAction);
	}

	private void ClearAutomaticRepairState(
		Guid accountId,
		AutomaticRepairState? expectedState = null)
	{
		lock (_automaticRepairSync)
		{
			if ((expectedState is null) ||
				(_automaticRepairStates.TryGetValue(
					accountId,
					out AutomaticRepairState? currentState) &&
				 ReferenceEquals(currentState, expectedState)))
			{
				_automaticRepairStates.Remove(accountId);
			}
		}
	}

	private AutomaticRepairState GetAutomaticRepairState(
		Guid accountId,
		string expectedIdentity)
	{
		lock (_automaticRepairSync)
		{
			if (_automaticRepairStates.TryGetValue(
					accountId,
					out AutomaticRepairState? state) &&
				ProviderAccountIdentityRules.Comparer.Equals(
					state.ExpectedIdentity,
					expectedIdentity))
			{
				return state;
			}

			state = new AutomaticRepairState(expectedIdentity);
			_automaticRepairStates[accountId] = state;
			return state;
		}
	}

	private static AutomaticRepairFailurePresentation?
		GetAutomaticRepairApprovalFailurePresentation(Exception exception)
	{
		return exception switch
		{
			FileNotFoundException or DirectoryNotFoundException =>
				new AutomaticRepairFailurePresentation(
					AutomaticRepairCliUnavailableMessage,
					UsageRecoveryAction.InstallOrUpdate),
			NotSupportedException => new AutomaticRepairFailurePresentation(
				AutomaticRepairUnsupportedBuildMessage,
				UsageRecoveryAction.InstallOrUpdate),
			_ => null
		};
	}

	private bool ShouldThrottleAutomaticRepair(
		AutomaticRepairState repairState,
		DateTimeOffset observedNow)
	{
		lock (_automaticRepairSync)
		{
			if (observedNow < repairState.NextAttemptAt)
			{
				return true;
			}

			repairState.NextAttemptAt =
				observedNow + AutomaticRepairInterval;
			repairState.Failure = null;
			return false;
		}
	}

	private static AutomaticRepairFailurePresentation?
		GetAutomaticRepairFailurePresentation(
			AntigravityMachineSetupPreparationResult? preparation)
	{
		if (preparation?.PreviewFailure is
			AntigravityMachineSetupPreviewFailure previewFailure)
		{
			(string error, UsageRecoveryAction recoveryAction) =
				GetFailurePresentation(
					previewFailure.FailureKind,
					previewFailure.SafetyFailureReason,
					previewFailure.AutomaticRevalidationPending,
					previewFailure.SourceKind ==
						AntigravityMachineSetupSourceKind.OfficialPrint);
			return new AutomaticRepairFailurePresentation(
				error,
				recoveryAction);
		}

		AntigravityMachineSetupFailureKind failureKind =
			preparation?.FailureKind ??
				AntigravityMachineSetupFailureKind.UnexpectedFailure;

		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.UnsupportedBuild => new(
				AutomaticRepairUnsupportedBuildMessage,
				UsageRecoveryAction.InstallOrUpdate),
			AntigravityMachineSetupFailureKind.UsageRejected => new(
				UsageShapeRejectedMessage,
				UsageRecoveryAction.UpdateApplication),
			AntigravityMachineSetupFailureKind.ConsentRequired or
			AntigravityMachineSetupFailureKind.AmbiguousExecutable or
			AntigravityMachineSetupFailureKind.ExistingProcessDetected or
			AntigravityMachineSetupFailureKind.SettingsRejected or
			AntigravityMachineSetupFailureKind.PromptRejected => new(
				AutomaticRepairSetupRejectedMessage,
				UsageRecoveryAction.ReconfigureUsageSource),
			_ => null
		};
	}

	private static SourceTrust GetSourceTrust(
		AntigravityMachineSetupSourceKind sourceKind)
	{
		return sourceKind switch
		{
			AntigravityMachineSetupSourceKind.OfficialPrint =>
				SourceTrust.OfficialExperimental,
			_ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
		};
	}

	private static UsageSnapshot CreateFailureSnapshot(
		AccountProfile account,
		SnapshotStatus status,
		DateTimeOffset fetchedAt,
		string error,
		UsageRecoveryAction recoveryAction)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			fetchedAt,
			Error: error,
			ProviderAccountIdentity: null,
			RecoveryAction: recoveryAction);
	}

	private static (string Error, UsageRecoveryAction RecoveryAction)
		GetFailurePresentation(
			AntigravityProductionUsageFailureKind failureKind,
			AntigravityUsageSafetyFailureReason safetyFailureReason,
			bool automaticRevalidationPending,
			bool useOfficialPrint)
	{
		return failureKind switch
		{
			AntigravityProductionUsageFailureKind.ProfileRejected => (
				ProfileRejectedMessage,
				UsageRecoveryAction.ReconfigureUsageSource),
			AntigravityProductionUsageFailureKind.ProvenanceRejected => (
				ProvenanceRejectedMessage,
				UsageRecoveryAction.Retry),
			AntigravityProductionUsageFailureKind.CaptureRejected => (
				CaptureRejectedMessage,
				UsageRecoveryAction.Retry),
			AntigravityProductionUsageFailureKind.ExecutionBusy => (
				CaptureRejectedMessage,
				UsageRecoveryAction.Retry),
			AntigravityProductionUsageFailureKind.UsageShapeRejected => (
				UsageShapeRejectedMessage,
				UsageRecoveryAction.UpdateApplication),
			AntigravityProductionUsageFailureKind.SafetyLatched
				when useOfficialPrint && automaticRevalidationPending => (
					AutomaticRevalidationPendingMessage,
					UsageRecoveryAction.Retry),
			AntigravityProductionUsageFailureKind.SafetyLatched => (
				CreateSafetyLatchedMessage(
					safetyFailureReason,
					useOfficialPrint),
				useOfficialPrint
					? UsageRecoveryAction.RevalidateUsage
					: UsageRecoveryAction.ReconfigureUsageSource),
			_ => (
				CaptureFailureMessage,
				UsageRecoveryAction.Retry)
		};
	}

	private static string AppendOfficialPrintVersionDiagnostic(
		string message,
		AntigravityProductionUsageResult result,
		bool useOfficialPrint)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		ArgumentNullException.ThrowIfNull(result);
		AntigravityOfficialPrintVersionAssessment? versionAssessment =
			result.OfficialPrintVersionAssessment;

		if (!useOfficialPrint ||
			(versionAssessment?.Kind !=
				AntigravityOfficialPrintVersionAssessmentKind
					.UnverifiedAllowed) ||
			!AntigravityProductionUsageResult
				.CanIncludeOfficialPrintVersionDiagnostic(
					result.FailureKind,
					result.SafetyFailureReason))
		{
			return message;
		}

		return new CliVersionEvidence(
			"Antigravity CLI",
			versionAssessment.DetectedVersion,
			AntigravityOfficialPrintCapabilityValidator.ReferenceVersion,
			CliVersionDisposition.UnverifiedAllowed)
			.AppendFailureDiagnostic(message);
	}

	private static string CreateSafetyLatchedMessage(
		AntigravityUsageSafetyFailureReason reason,
		bool useOfficialPrint)
	{
		string detail = reason switch
		{
			AntigravityUsageSafetyFailureReason.AttemptInterrupted =>
				"上次檢查在完成前中斷",
			AntigravityUsageSafetyFailureReason.TimedOut =>
				"上次檢查逾時",
			AntigravityUsageSafetyFailureReason.CanceledAfterStart =>
				"上次檢查在啟動 Antigravity 後被取消",
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart =>
				"Antigravity 指令啟動後沒有正常結束",
			AntigravityUsageSafetyFailureReason.NonZeroExit =>
				"Antigravity 指令回報錯誤",
			AntigravityUsageSafetyFailureReason.EmptyOutput =>
				"Antigravity 指令沒有回傳資料",
			AntigravityUsageSafetyFailureReason.StdoutTooLarge or
				AntigravityUsageSafetyFailureReason.StderrTooLarge =>
				"Antigravity 指令回傳的資料量太大，無法確認結果",
			AntigravityUsageSafetyFailureReason.UnverifiableOutput =>
				"無法確認 Antigravity 回傳的內容",
			AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid or
				AntigravityUsageSafetyFailureReason.OutputJsonInvalid or
				AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties or
				AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid or
				AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid or
				AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer =>
				"Antigravity 回傳的用量資料無法辨識",
			AntigravityUsageSafetyFailureReason.UsageActivityDetected =>
				"偵測到這次檢查可能產生用量",
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable =>
				"無法讀取或儲存上次檢查的狀態",
			_ => "無法確認上次檢查是否完成"
		};

		string nextStep = useOfficialPrint
			? "請按「重新檢查 Antigravity 用量」再試一次。"
			: "請重新連接 Antigravity 帳號。";
		return $"Antigravity 用量檢查已暫停：{detail}。AI Usage 不會自動再試。{nextStep}";
	}

	private static bool TryCreateMetric(
		AntigravityProductionUsageWindow window,
		WindowDescriptor descriptor,
		DateTimeOffset observedNow,
		out UsageMetric? metric)
	{
		metric = null;

		if ((window.RemainingPercent < 0m) ||
			(window.RemainingPercent > 100m) ||
			(window.IsAvailable && (window.ResetsIn is not null)) ||
			(!window.IsAvailable && (window.ResetsIn is null)) ||
			((window.ResetsIn is TimeSpan resetsIn) &&
				(resetsIn < TimeSpan.Zero)))
		{
			return false;
		}

		decimal usedPercent = 100m - window.RemainingPercent;
		DateTimeOffset? resetsAt;

		try
		{
			resetsAt = window.ResetsIn is TimeSpan duration
				? observedNow + duration
				: null;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}

		metric = new UsageMetric(
			descriptor.StableWindowId,
			descriptor.Label,
			(double)usedPercent,
			$"已使用 {usedPercent.ToString("0.##", CultureInfo.InvariantCulture)}%",
			resetsAt,
			ResetDisplayValue: window.IsAvailable ? "目前可用" : null);
		return true;
	}

	private static bool TryCreateMetrics(
		IReadOnlyList<AntigravityProductionUsageWindow>? windows,
		DateTimeOffset observedNow,
		out IReadOnlyList<UsageMetric>? metrics)
	{
		metrics = null;

		if ((windows is null) ||
			(windows.Count != ExpectedWindows.Length))
		{
			return false;
		}

		Dictionary<string, AntigravityProductionUsageWindow> windowsById =
			new(StringComparer.Ordinal);

		foreach (AntigravityProductionUsageWindow? window in windows)
		{
			if ((window is null) ||
				string.IsNullOrEmpty(window.StableWindowId) ||
				!windowsById.TryAdd(window.StableWindowId, window))
			{
				return false;
			}
		}

		List<UsageMetric> mapped = new(ExpectedWindows.Length);

		foreach (WindowDescriptor descriptor in ExpectedWindows)
		{
			if (!windowsById.TryGetValue(
					descriptor.StableWindowId,
					out AntigravityProductionUsageWindow? window) ||
				!TryCreateMetric(
					window,
					descriptor,
					observedNow,
					out UsageMetric? metric) ||
				(metric is null))
			{
				return false;
			}

			mapped.Add(metric);
		}

		metrics = mapped.AsReadOnly();
		return true;
	}
}
