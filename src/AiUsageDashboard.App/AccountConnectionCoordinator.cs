using System.Diagnostics;
using System.IO;
using System.Windows;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

internal enum CodexLoginRefreshOutcome
{
	PendingConfirmation,
	NotConfigured,
	Error,
	Stale,
	Ready
}

internal enum ClaudeLegacyCacheDisposition
{
	Retain,
	Clear,
	Separate
}

internal sealed record ClaudeLegacyCacheConfirmation(
	string AccountIdentity,
	string SubscriptionScopeDisplayName,
	string PlanTier);

internal sealed class AccountConnectionCoordinator : IDisposable
{
	private enum GrokConflictCleanupResult
	{
		Succeeded,
		PendingRetryable,
		BindingRestoreFailed
	}

	private readonly record struct GrokConflictReconciliationResult(
		bool WasProfileDisconnectPersisted,
		GrokConflictCleanupResult CleanupResult);

	internal const string AntigravityPreflightMessage =
		"請先登入 Antigravity。AI Usage 會確認這台電腦上的 Antigravity CLI 與既有連接，再讀取目前帳號的四項用量。" +
		"新連接只接受支援官方唯讀 /usage 的版本；舊版相容讀取只會沿用這台電腦上已存在且可驗證的連接。" +
		"這張卡會跟隨這台電腦目前的 Antigravity 登入；AI Usage 無法確認或固定企業專案範圍。這項操作不會讀取登入憑證。";
	private static readonly TimeSpan DefaultAntigravityRefreshQuiesceTimeout =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan[] AuthenticatedBindingUsageRetryDelays =
	[
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromMilliseconds(750)
	];
	private readonly Dictionary<Guid, CancellationTokenSource>
		_activeAccountConnectionCancellationSources = new();
	private readonly HashSet<TaskCompletionSource>
		_activeConnectionCompletions = new();
	private readonly object _activeConnectionCompletionsLock = new();
	private readonly HashSet<Guid> _accountConnectionsWithCommitStarted = new();
	private readonly HashSet<Guid> _activeAntigravitySetups = new();
	private readonly HashSet<Guid> _activeClaudeLogins = new();
	private readonly HashSet<Guid> _activeCodexLogins = new();
	private readonly HashSet<Guid> _activeCopilotLogins = new();
	private readonly HashSet<Guid> _activeGrokLogins = new();
	private readonly IAntigravityAccountSetupLauncher
		_antigravityAccountSetupLauncher;
	private readonly TimeSpan _antigravityRefreshQuiesceTimeout;
	private readonly IClaudeAccountBindingStore? _claudeAccountBindingStore;
	private readonly IClaudeAccountLogin _claudeAccountLogin;
	private readonly ClaudeBindingCommitGate _claudeBindingCommitGate;
	private readonly IClaudeSubscriptionContextProbe?
		_claudeSubscriptionContextProbe;
	private readonly ICodexAccountLogin _codexAccountLogin;
	private readonly ICodexWorkspaceBindingStore? _codexWorkspaceBindingStore;
	private readonly CodexBindingCommitGate _codexBindingCommitGate;
	private readonly ICopilotAccountConnector? _copilotAccountConnector;
	private readonly IGrokAccountBindingStore? _grokAccountBindingStore;
	private readonly IGrokAccountLogin? _grokAccountLogin;
	private readonly IGrokConnectionPendingStore? _grokConnectionPendingStore;
	private readonly IGrokUsagePoller? _grokUsagePoller;
	private readonly GrokBindingCommitGate _grokBindingCommitGate;
	private readonly Action<string, string, Exception?> _reportDiagnostic;
	private readonly CancellationTokenSource _lifetimeSource = new();
	private bool _isConnectionShutdownStarted;
	private bool _isUpdateShutdownReservationActive;
	private bool _isDisposed;

	internal bool IsAntigravitySetupInProgress
	{
		get
		{
			lock (_activeConnectionCompletionsLock)
			{
				return _activeAntigravitySetups.Count > 0;
			}
		}
	}

	internal void ReserveShutdown()
	{
		lock (_activeConnectionCompletionsLock)
		{
			_isConnectionShutdownStarted = true;
			_isUpdateShutdownReservationActive = false;
		}
	}

	internal bool TryReserveShutdownForUpdate()
	{
		lock (_activeConnectionCompletionsLock)
		{
			if (_isConnectionShutdownStarted ||
				(_activeAntigravitySetups.Count > 0))
			{
				return false;
			}

			_isConnectionShutdownStarted = true;
			_isUpdateShutdownReservationActive = true;
			return true;
		}
	}

	internal void RollbackShutdownForUpdateReservation()
	{
		lock (_activeConnectionCompletionsLock)
		{
			if (!_isUpdateShutdownReservationActive)
			{
				return;
			}

			_isUpdateShutdownReservationActive = false;
			_isConnectionShutdownStarted = false;
		}
	}

	internal static CodexLoginRefreshOutcome GetCodexLoginRefreshOutcome(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.CurrentSnapshot is not UsageSnapshot snapshot)
		{
			return CodexLoginRefreshOutcome.PendingConfirmation;
		}

		return snapshot.Status switch
		{
			SnapshotStatus.NotConfigured => CodexLoginRefreshOutcome.NotConfigured,
			SnapshotStatus.Error => CodexLoginRefreshOutcome.Error,
			SnapshotStatus.Stale when account.HasProviderAccountIdentity =>
				CodexLoginRefreshOutcome.Stale,
			SnapshotStatus.Ready when account.HasProviderAccountIdentity =>
				CodexLoginRefreshOutcome.Ready,
			_ => CodexLoginRefreshOutcome.PendingConfirmation
		};
	}

	internal bool IsAccountConnectionInProgress(Guid accountId)
	{
		lock (_activeConnectionCompletionsLock)
		{
			return _activeAccountConnectionCancellationSources.ContainsKey(
				accountId);
		}
	}

	internal bool TryCancelAccountConnection(Guid accountId)
	{
		CancellationTokenSource? cancellationSource;

		lock (_activeConnectionCompletionsLock)
		{
			if (!_activeAccountConnectionCancellationSources.TryGetValue(
					accountId,
					out cancellationSource) ||
				_accountConnectionsWithCommitStarted.Contains(accountId))
			{
				return false;
			}

		}

		try
		{
			if (!cancellationSource.IsCancellationRequested)
			{
				cancellationSource.Cancel();
			}

			return true;
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	public AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		Action<string, string, Exception?>? reportDiagnostic = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			DefaultAntigravityRefreshQuiesceTimeout,
			reportDiagnostic)
	{
	}

	public AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		IGrokAccountLogin grokAccountLogin,
		IGrokUsagePoller grokUsagePoller,
		IGrokAccountBindingStore grokAccountBindingStore,
		IGrokConnectionPendingStore grokConnectionPendingStore,
		Action<string, string, Exception?>? reportDiagnostic = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			grokAccountLogin ??
				throw new ArgumentNullException(nameof(grokAccountLogin)),
			grokUsagePoller ??
				throw new ArgumentNullException(nameof(grokUsagePoller)),
			grokAccountBindingStore ??
				throw new ArgumentNullException(nameof(grokAccountBindingStore)),
			grokConnectionPendingStore ??
				throw new ArgumentNullException(nameof(grokConnectionPendingStore)),
			DefaultAntigravityRefreshQuiesceTimeout,
			reportDiagnostic)
	{
	}

	internal AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		IGrokAccountLogin grokAccountLogin,
		IGrokUsagePoller grokUsagePoller,
		IGrokAccountBindingStore grokAccountBindingStore,
		IGrokConnectionPendingStore grokConnectionPendingStore,
		GrokBindingCommitGate grokBindingCommitGate,
		Action<string, string, Exception?>? reportDiagnostic = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			grokAccountLogin ??
				throw new ArgumentNullException(nameof(grokAccountLogin)),
			grokUsagePoller ??
				throw new ArgumentNullException(nameof(grokUsagePoller)),
			grokAccountBindingStore ??
				throw new ArgumentNullException(nameof(grokAccountBindingStore)),
			grokConnectionPendingStore ??
				throw new ArgumentNullException(nameof(grokConnectionPendingStore)),
			DefaultAntigravityRefreshQuiesceTimeout,
			reportDiagnostic,
			grokBindingCommitGate ??
				throw new ArgumentNullException(nameof(grokBindingCommitGate)))
	{
	}

	internal AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		ICodexWorkspaceBindingStore codexWorkspaceBindingStore,
		CodexBindingCommitGate codexBindingCommitGate,
		Action<string, string, Exception?>? reportDiagnostic = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			grokAccountLogin: null,
			grokUsagePoller: null,
			grokAccountBindingStore: null,
			grokConnectionPendingStore: null,
			DefaultAntigravityRefreshQuiesceTimeout,
			reportDiagnostic,
			grokBindingCommitGate: null,
			claudeAccountBindingStore: null,
			claudeBindingCommitGate: null,
			claudeSubscriptionContextProbe: null,
			codexWorkspaceBindingStore ??
				throw new ArgumentNullException(nameof(codexWorkspaceBindingStore)),
			codexBindingCommitGate ??
				throw new ArgumentNullException(nameof(codexBindingCommitGate)))
	{
	}

	internal AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		IGrokAccountLogin grokAccountLogin,
		IGrokUsagePoller grokUsagePoller,
		IGrokAccountBindingStore grokAccountBindingStore,
		IGrokConnectionPendingStore grokConnectionPendingStore,
		GrokBindingCommitGate grokBindingCommitGate,
		IClaudeAccountBindingStore claudeAccountBindingStore,
		ClaudeBindingCommitGate claudeBindingCommitGate,
		Action<string, string, Exception?>? reportDiagnostic = null,
		IClaudeSubscriptionContextProbe?
			claudeSubscriptionContextProbe = null,
		ICodexWorkspaceBindingStore? codexWorkspaceBindingStore = null,
		CodexBindingCommitGate? codexBindingCommitGate = null,
		ICopilotAccountConnector? copilotAccountConnector = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			grokAccountLogin ??
				throw new ArgumentNullException(nameof(grokAccountLogin)),
			grokUsagePoller ??
				throw new ArgumentNullException(nameof(grokUsagePoller)),
			grokAccountBindingStore ??
				throw new ArgumentNullException(nameof(grokAccountBindingStore)),
			grokConnectionPendingStore ??
				throw new ArgumentNullException(nameof(grokConnectionPendingStore)),
			DefaultAntigravityRefreshQuiesceTimeout,
			reportDiagnostic,
			grokBindingCommitGate ??
				throw new ArgumentNullException(nameof(grokBindingCommitGate)),
			claudeAccountBindingStore ??
				throw new ArgumentNullException(nameof(claudeAccountBindingStore)),
			claudeBindingCommitGate ??
				throw new ArgumentNullException(nameof(claudeBindingCommitGate)),
			claudeSubscriptionContextProbe,
			codexWorkspaceBindingStore,
			codexBindingCommitGate,
			copilotAccountConnector)
	{
	}

	internal AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		TimeSpan antigravityRefreshQuiesceTimeout,
		Action<string, string, Exception?>? reportDiagnostic = null)
		: this(
			claudeAccountLogin,
			codexAccountLogin,
			antigravityAccountSetupLauncher,
			grokAccountLogin: null,
			grokUsagePoller: null,
			grokAccountBindingStore: null,
			grokConnectionPendingStore: null,
			antigravityRefreshQuiesceTimeout,
			reportDiagnostic)
	{
	}

	internal AccountConnectionCoordinator(
		IClaudeAccountLogin claudeAccountLogin,
		ICodexAccountLogin codexAccountLogin,
		IAntigravityAccountSetupLauncher antigravityAccountSetupLauncher,
		IGrokAccountLogin? grokAccountLogin,
		IGrokUsagePoller? grokUsagePoller,
		IGrokAccountBindingStore? grokAccountBindingStore,
		IGrokConnectionPendingStore? grokConnectionPendingStore,
		TimeSpan antigravityRefreshQuiesceTimeout,
		Action<string, string, Exception?>? reportDiagnostic = null,
		GrokBindingCommitGate? grokBindingCommitGate = null,
		IClaudeAccountBindingStore? claudeAccountBindingStore = null,
		ClaudeBindingCommitGate? claudeBindingCommitGate = null,
		IClaudeSubscriptionContextProbe?
			claudeSubscriptionContextProbe = null,
		ICodexWorkspaceBindingStore? codexWorkspaceBindingStore = null,
		CodexBindingCommitGate? codexBindingCommitGate = null,
		ICopilotAccountConnector? copilotAccountConnector = null)
	{
		if (antigravityRefreshQuiesceTimeout < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(antigravityRefreshQuiesceTimeout));
		}

		bool hasAnyGrokDependency = (grokAccountLogin is not null) ||
			(grokUsagePoller is not null) ||
			(grokAccountBindingStore is not null) ||
			(grokConnectionPendingStore is not null);
		bool hasEveryGrokDependency = (grokAccountLogin is not null) &&
			(grokUsagePoller is not null) &&
			(grokAccountBindingStore is not null) &&
			(grokConnectionPendingStore is not null);
		if (hasAnyGrokDependency && !hasEveryGrokDependency)
		{
			throw new ArgumentException(
				"Grok account connection dependencies must be configured together.");
		}
		if ((claudeAccountBindingStore is not null) &&
			(claudeSubscriptionContextProbe is null))
		{
			throw new ArgumentException(
				"Claude binding persistence requires a subscription context probe.",
				nameof(claudeSubscriptionContextProbe));
		}
		if ((claudeAccountBindingStore is not null) &&
			(claudeBindingCommitGate is null))
		{
			throw new ArgumentNullException(nameof(claudeBindingCommitGate));
		}
		if ((codexWorkspaceBindingStore is null) !=
			(codexBindingCommitGate is null))
		{
			throw new ArgumentException(
				"Codex workspace binding dependencies must be configured together.");
		}

		_claudeAccountLogin = claudeAccountLogin ??
			throw new ArgumentNullException(nameof(claudeAccountLogin));
		_claudeAccountBindingStore = claudeAccountBindingStore;
		_claudeBindingCommitGate = claudeBindingCommitGate ??
			new ClaudeBindingCommitGate();
		_claudeSubscriptionContextProbe = claudeSubscriptionContextProbe;
		_codexAccountLogin = codexAccountLogin ??
			throw new ArgumentNullException(nameof(codexAccountLogin));
		_codexWorkspaceBindingStore = codexWorkspaceBindingStore;
		_codexBindingCommitGate = codexBindingCommitGate ??
			new CodexBindingCommitGate();
		_copilotAccountConnector = copilotAccountConnector;
		_grokAccountLogin = grokAccountLogin;
		_grokUsagePoller = grokUsagePoller;
		_grokAccountBindingStore = grokAccountBindingStore;
		_grokConnectionPendingStore = grokConnectionPendingStore;
		_grokBindingCommitGate = grokBindingCommitGate ?? new GrokBindingCommitGate();
		_antigravityAccountSetupLauncher =
			antigravityAccountSetupLauncher ??
			throw new ArgumentNullException(
				nameof(antigravityAccountSetupLauncher));
		_antigravityRefreshQuiesceTimeout =
			antigravityRefreshQuiesceTimeout;
		_reportDiagnostic = reportDiagnostic ?? ((_, _, _) => { });
	}

	public async Task ConnectAntigravityAsync(
		Window owner,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null,
		bool hasConfirmedPreflight = false)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		if (!CanStartAntigravitySetup(viewModel, account) ||
			IsAntigravitySetupActive(account.Id))
		{
			return;
		}

		if (!hasConfirmedPreflight &&
			(WpfMessageBox.Show(
				owner,
				AntigravityPreflightMessage,
				account.AntigravityAccountActionText,
				MessageBoxButton.YesNo,
				MessageBoxImage.Information,
				MessageBoxResult.No) != MessageBoxResult.Yes))
		{
			return;
		}

		await RunAntigravitySetupAsync(
			account,
			viewModel,
			reportStatus,
			(message, caption, image) =>
				WpfMessageBox.Show(
					owner,
					message,
					caption,
					MessageBoxButton.OK,
					image));
	}

	internal async Task RunAntigravitySetupAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null,
		Action<string, string, MessageBoxImage>? reportNotice = null)
	{
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		if (!CanStartAntigravitySetup(viewModel, account) ||
			!TryBeginAntigravitySetup(account.Id))
		{
			return;
		}

		CancellationToken lifetimeToken = _lifetimeSource.Token;
		if (!viewModel.TryBeginProviderAccountChange(account))
		{
			EndAntigravitySetup(account.Id);
			return;
		}

		TaskCompletionSource? setupCompletion = TryRegisterActiveConnection();

		if (setupCompletion is null)
		{
			account.AbortProviderAccountChange();
			EndAntigravitySetup(account.Id);
			return;
		}

		Guid setupAttemptId = Guid.Empty;
		bool didCreateSetupIntent = false;
		bool shouldRetainSetupIntent = false;
		try
		{
			reportStatus?.Invoke(
				$"正在連接「{account.AccountName}」的 Antigravity 帳號…");
			await viewModel
				.QuiesceAccountRefreshAsync(account.Id, lifetimeToken)
				.WaitAsync(
					_antigravityRefreshQuiesceTimeout,
					lifetimeToken);
			setupAttemptId = Guid.NewGuid();
			if (!await viewModel.TryBeginOfficialAntigravitySetupIntentAsync(
					account,
					setupAttemptId,
					lifetimeToken))
			{
				reportStatus?.Invoke(
					"Antigravity 連接尚未開始；暫時無法記錄連接進度。AI Usage 稍後會自動再試。");
				return;
			}
			didCreateSetupIntent = true;
			shouldRetainSetupIntent = true;

			viewModel.NotifyAntigravityAccountSetupStarting();
			AntigravityAccountSetupOutcome outcome =
				await _antigravityAccountSetupLauncher.RunAsync(
					setupAttemptId,
					lifetimeToken);
			ReportDiagnostic(
				"antigravity-account-connection",
				$"stage=helper-exit;outcome={outcome}");

			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			switch (outcome)
			{
				case AntigravityAccountSetupOutcome.CompletedOfficialPrint:
					if (TryBeginAntigravitySetupCommit(
							account,
							reportStatus))
					{
						await CompleteDurableAntigravitySetupAsync(
							account,
							viewModel,
							setupAttemptId,
							AntigravityMachineSetupSourceKind.OfficialPrint,
							reportStatus,
							lifetimeToken);
					}
					break;
				case AntigravityAccountSetupOutcome.Completed:
					if (TryBeginAntigravitySetupCommit(
							account,
							reportStatus))
					{
						await CompleteDurableAntigravitySetupAsync(
							account,
							viewModel,
							setupAttemptId,
							AntigravityMachineSetupSourceKind.ReviewedConPty,
							reportStatus,
							lifetimeToken);
					}
					break;
				case AntigravityAccountSetupOutcome.Cancelled:
					shouldRetainSetupIntent = false;
					reportStatus?.Invoke(
						$"已取消「{account.AccountName}」的 Antigravity 帳號連接。");
					break;
				case AntigravityAccountSetupOutcome.Unavailable:
					shouldRetainSetupIntent = false;
					reportNotice?.Invoke(
						"這份 AI Usage 安裝缺少 Antigravity 連接元件。Claude 與 Codex 不受影響；請重新下載 AI Usage 安裝包並完整解壓後再試。",
						"無法開啟 Antigravity 連接",
						MessageBoxImage.Warning);
					break;
				case AntigravityAccountSetupOutcome.Failed:
					shouldRetainSetupIntent = false;
					reportStatus?.Invoke(
						$"「{account.AccountName}」的 Antigravity 帳號連接未完成。Antigravity 連接視窗已顯示這次失敗的具體原因與處理方式；AI Usage 已保留原本顯示的用量。");
					break;
				case AntigravityAccountSetupOutcome.CompletionUnknown:
					reportStatus?.Invoke(
						"Antigravity 連接視窗尚未回報完成。AI Usage 已記錄連接進度，稍後會自動確認，重新啟動後也會繼續。");
					break;
				case AntigravityAccountSetupOutcome.LaunchFailed:
				default:
					shouldRetainSetupIntent = false;
					reportNotice?.Invoke(
						"Antigravity 連接視窗無法正常開啟或完成。請重新下載 AI Usage 安裝包並完整解壓，確認 Windows 或防毒軟體未封鎖檔案，再試一次。AI Usage 已保留原本顯示的用量。",
						"Antigravity 連接視窗未完成",
						MessageBoxImage.Warning);
					break;
			}
		}
		catch (TimeoutException)
		{
			reportNotice?.Invoke(
				"AI Usage 尚未完成目前的 Antigravity 用量檢查，因此沒有啟動帳號連接。請稍後再試。",
				"Antigravity 仍在使用中",
				MessageBoxImage.Warning);
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
		{
			// The application is closing; no user-facing error is needed.
		}
		catch (Exception exception) when (lifetimeToken.IsCancellationRequested)
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=shutdown-cleanup;result=failed",
				exception);
		}
		catch (Exception exception) when (
			!IsAttachedAndEnabled(viewModel, account))
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=detached-cleanup;result=failed",
				exception);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=coordinator;result=failed",
				exception);
			reportNotice?.Invoke(
				"無法完成 Antigravity 帳號連接。原本的用量仍會保留，請稍後再試。",
				"Antigravity 連接失敗",
				MessageBoxImage.Error);
		}
		finally
		{
			if (didCreateSetupIntent && !shouldRetainSetupIntent)
			{
				bool wasSetupIntentRemoved =
					await viewModel.CancelOfficialAntigravitySetupIntentAsync(
						account.Id,
						setupAttemptId,
						CancellationToken.None);
				shouldRetainSetupIntent = !wasSetupIntentRemoved;
			}

			account.AbortProviderAccountChange();
			if (didCreateSetupIntent && shouldRetainSetupIntent)
			{
				viewModel.NotifyAntigravitySetupIntentRetained(account.Id);
			}
			EndAntigravitySetup(account.Id);
			CompleteActiveConnection(setupCompletion);
		}
	}

	public Task ConnectClaudeAsync(
		Window owner,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null,
		bool hasConfirmedQuotaRisk = false)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		return ConnectClaudeAsync(
			account,
			viewModel,
			() => WpfMessageBox.Show(
				owner,
				"Claude /usage 查詢狀態時也可能計入少量用量。AI Usage 只會採用未產生模型請求、token 或費用的結果，但這些項目只能在執行後確認，已產生的用量或費用無法撤回。\n\n是否開始檢查？",
				"確認 Claude 用量讀取",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning,
				MessageBoxResult.No),
			reportStatus,
			(message, caption, image) =>
				WpfMessageBox.Show(
					owner,
					message,
					caption,
					MessageBoxButton.OK,
					image),
			hasConfirmedQuotaRisk,
			confirmation =>
			{
				MessageBoxResult result = WpfMessageBox.Show(
					owner,
					$"這張卡片有舊版 Claude 用量資料。請確認新觀察到的訂閱範圍：\n\n" +
					$"帳號：{confirmation.AccountIdentity}\n" +
					$"組織：{confirmation.SubscriptionScopeDisplayName}\n" +
					$"方案：{confirmation.PlanTier}\n\n" +
					"選「是」會把舊用量保留為這個訂閱範圍的歷史資料；選「否」會清除舊用量；選「取消」會保留舊資料並停止這次連接，讓你另建卡片。",
					"確認 Claude 訂閱範圍",
					MessageBoxButton.YesNoCancel,
					MessageBoxImage.Question,
					MessageBoxResult.Cancel);

				return result switch
				{
					MessageBoxResult.Yes =>
						ClaudeLegacyCacheDisposition.Retain,
					MessageBoxResult.No =>
						ClaudeLegacyCacheDisposition.Clear,
					_ => ClaudeLegacyCacheDisposition.Separate
				};
			});
	}

	internal async Task ConnectClaudeAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Func<MessageBoxResult> confirmQuotaRisk,
		Action<string>? reportStatus = null,
		Action<string, string, MessageBoxImage>? reportNotice = null,
		bool hasConfirmedQuotaRisk = false,
		Func<ClaudeLegacyCacheConfirmation, ClaudeLegacyCacheDisposition>?
			confirmLegacyCache = null)
	{
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);
		ArgumentNullException.ThrowIfNull(confirmQuotaRisk);

		if (!account.IsClaude ||
			!account.CanConnectProviderAccount ||
			viewModel.IsManagingAccounts ||
			viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) ||
			!IsAttachedAndEnabled(viewModel, account) ||
			!_activeClaudeLogins.Add(account.Id))
		{
			return;
		}

		CancellationToken lifetimeToken = _lifetimeSource.Token;
		TaskCompletionSource? loginCompletion = null;
		CancellationTokenSource? connectionCancellationSource = null;
		CancellationTokenSource? operationSource = null;
		CancellationTokenRegistration accountChangeCancellationRegistration =
			default;
		bool didAcceptQuotaRisk = false;
		bool didPersistAuthenticatedBinding = false;

		try
		{
			loginCompletion = TryRegisterActiveAccountConnection(
				account.Id,
				out connectionCancellationSource);

			if ((loginCompletion is null) ||
				(connectionCancellationSource is null))
			{
				return;
			}

			operationSource = CancellationTokenSource.CreateLinkedTokenSource(
				lifetimeToken,
				connectionCancellationSource.Token);
			CancellationToken operationToken = operationSource.Token;
			accountChangeCancellationRegistration = operationToken.Register(
				() => CancelProviderAccountChangeIfCancellable(
					account.Id,
					connectionCancellationSource,
					account));

			if (!account.Profile.HasAcceptedClaudeQuotaRisk)
			{
				if (!hasConfirmedQuotaRisk &&
					(confirmQuotaRisk() != MessageBoxResult.Yes))
				{
					return;
				}

				if (!IsAttachedAndEnabled(viewModel, account) ||
					viewModel.IsManagingAccounts ||
					!await viewModel.AcceptClaudeQuotaRiskAsync(
						account,
						operationToken) ||
					!IsAttachedAndEnabled(viewModel, account) ||
					!account.Profile.HasAcceptedClaudeQuotaRisk)
				{
					return;
				}

				didAcceptQuotaRisk = true;
			}

			operationToken.ThrowIfCancellationRequested();

			if (didAcceptQuotaRisk)
			{
				UsageSnapshot? snapshotBeforeRefresh = account.CurrentSnapshot;
				reportStatus?.Invoke(
					$"正在確認「{account.AccountName}」原本的 Claude 帳號…");
				await viewModel.RefreshAccountUsageAsync(
					account.Id,
					operationToken);
				operationToken.ThrowIfCancellationRequested();

				if (!IsAttachedAndEnabled(viewModel, account))
				{
					return;
				}

				bool requiresLogin =
					!ReferenceEquals(snapshotBeforeRefresh, account.CurrentSnapshot) &&
					(account.CurrentSnapshot?.Status == SnapshotStatus.NotConfigured) &&
					(account.RecoveryAction is UsageRecoveryAction.ConnectAccount or
						UsageRecoveryAction.ConfirmSubscription);

				if (!requiresLogin)
				{
					string resumeMessage = account.HasConfirmedProviderAccountBinding &&
						(account.CurrentSnapshot?.Status == SnapshotStatus.Ready)
						? $"「{account.AccountName}」已沿用原本的 Claude 帳號，並已重新讀取用量。"
						: account.RecoveryAction == UsageRecoveryAction.Retry
							? $"「{account.AccountName}」已保留原本的 Claude 帳號，AI Usage 稍後會自動再檢查用量。"
							: account.RecoveryAction == UsageRecoveryAction.RevalidateUsage
								? $"「{account.AccountName}」已保留原本的 Claude 帳號；上次用量檢查未完成。請按卡片上的「重新檢查 Claude 用量」。"
								: $"「{account.AccountName}」已保留原本的 Claude 帳號；請依卡片提示處理用量檢查。";
					reportStatus?.Invoke(resumeMessage);
					return;
				}
			}

			account.BeginProviderAccountChange();

			if (!account.IsProviderAccountChangeInProgress)
			{
				return;
			}

			reportStatus?.Invoke(
				$"正在連接「{account.AccountName}」的 Claude 帳號…");
			ClaudeAccountLoginResult loginResult = await _claudeAccountLogin.LoginAsync(
				account.Id,
				operationToken);
			accountChangeCancellationRegistration.Dispose();
			ClaudeAccountBinding? subscriptionBinding = null;
			UsageSnapshot? retainedLegacySnapshot = null;
			bool didConfirmLegacyContext = false;
			string providerBindingIdentity = loginResult.AccountIdentity;

			if (_claudeAccountBindingStore is not null)
			{
				if (loginResult.SubscriptionContext is not
					ClaudeSubscriptionContext subscriptionContext)
				{
					throw new ClaudeAccountLoginException(
						"Claude 登入完成，但無法確認訂閱範圍。");
				}

				bool isLegacyProfile =
					!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
						account.Profile.ProviderAccountIdentity,
						out _);
				UsageSnapshot? legacySnapshot = isLegacyProfile
					? await viewModel.LoadClaudeLegacyCachedSnapshotAsync(
						account,
						operationToken)
					: null;

				if (legacySnapshot is not null)
				{
					if (confirmLegacyCache is null)
					{
						throw new ClaudeAccountLoginException(
							"這張卡片有舊版 Claude 用量，必須先確認訂閱範圍與舊資料處理方式。");
					}

					ClaudeLegacyCacheDisposition disposition =
						confirmLegacyCache(new ClaudeLegacyCacheConfirmation(
							subscriptionContext.AccountIdentity,
							subscriptionContext.SubscriptionScopeDisplayName ??
								"已確認的組織（未提供名稱）",
							subscriptionContext.PlanTier));

					if (disposition == ClaudeLegacyCacheDisposition.Separate)
					{
						account.ApplySnapshot(legacySnapshot with
						{
							Status = SnapshotStatus.Stale,
							Error =
								"目前顯示尚未連接到訂閱範圍的舊版歷史用量；本次連接未完成。",
							RecoveryAction = UsageRecoveryAction.ConfirmSubscription,
							SubscriptionVerificationState =
								SubscriptionVerificationState.Unverified
						});
						reportStatus?.Invoke(
							$"已保留「{account.AccountName}」的舊版 Claude 用量，且未更改連接；請另建卡片連接新的訂閱範圍。");
						return;
					}

					didConfirmLegacyContext = true;

					if (disposition == ClaudeLegacyCacheDisposition.Retain)
					{
						retainedLegacySnapshot = legacySnapshot;
					}
				}

				subscriptionBinding = ClaudeAccountBinding.Create(
					account.Id,
					subscriptionContext);
				providerBindingIdentity =
					ClaudeAccountBinding.CreatePublicBindingIdentity(
						subscriptionBinding.PublicBindingId);
			}

			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			if (!TryBeginAccountConnectionCommitAfterExternalSuccess(
					account.Id,
					connectionCancellationSource,
					account,
					providerBindingIdentity))
			{
				return;
			}

			AuthenticatedProviderBindingCommitResult bindingResult =
				subscriptionBinding is null
					? await viewModel.TryPersistAuthenticatedProviderBindingAsync(
						account,
						providerBindingIdentity,
						CancellationToken.None)
					: await viewModel
						.ExecuteAuthenticatedProviderBindingMutationAsync(
							() => TryPersistClaudeSubscriptionBindingAsync(
								account,
								viewModel,
								subscriptionBinding,
								loginResult.SubscriptionContext!,
								retainedLegacySnapshot,
								didConfirmLegacyContext,
								CancellationToken.None),
							CancellationToken.None);
			if (bindingResult !=
				AuthenticatedProviderBindingCommitResult.Succeeded)
			{
				reportStatus?.Invoke(
					GetAuthenticatedProviderBindingCommitFailureMessage(
						account,
						bindingResult,
						$"「{account.AccountName}」無法連接：這個 Claude 訂閱範圍已連接到另一張帳號卡片。"));
				return;
			}
			didPersistAuthenticatedBinding = true;

			if (operationToken.IsCancellationRequested)
			{
				if (!lifetimeToken.IsCancellationRequested)
				{
					reportStatus?.Invoke(
						GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
				}

				return;
			}

			await RefreshAuthenticatedProviderUsageAfterBindingAsync(
				account,
				viewModel,
				operationToken);
			operationToken.ThrowIfCancellationRequested();

			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			account.CompleteProviderAccountChange();
			string message = GetClaudeLoginCompletionNotice(account).Message;
			reportStatus?.Invoke(message);
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
		{
			// The application is closing; no user-facing error is needed.
		}
		catch (Exception exception) when (lifetimeToken.IsCancellationRequested)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=shutdown-cleanup;result=failed",
				exception);
		}
		catch (Exception exception) when (didPersistAuthenticatedBinding)
		{
			// The provider switch is already durable. Any remaining refresh or
			// cleanup failure is recoverable without asking the user to reconnect.
			ReportDiagnostic(
				"claude-account-connection",
				"stage=committed-refresh;result=deferred",
				exception);
			reportStatus?.Invoke(
				GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
		}
		catch (OperationCanceledException) when (
			connectionCancellationSource?.IsCancellationRequested == true)
		{
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Claude 帳號連接。");
		}
		catch (Exception exception) when (
			connectionCancellationSource?.IsCancellationRequested == true)
		{
			// Process or browser cleanup can surface a secondary failure after cancel.
			ReportDiagnostic(
				"claude-account-connection",
				"stage=cancel-cleanup;result=failed",
				exception);
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Claude 帳號連接。");
		}
		catch (Exception exception) when (
			!IsAttachedAndEnabled(viewModel, account))
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=detached-cleanup;result=failed",
				exception);
		}
		catch (ClaudeAccountLoginException exception)
		{
			(string message, string caption, MessageBoxImage image) =
				GetClaudeLoginFailureNotice(exception);
			reportNotice?.Invoke(
				message,
				caption,
				image);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=coordinator;result=failed",
				exception);
			reportNotice?.Invoke(
				"無法完成 Claude 登入或重新讀取用量。原本的用量仍會保留，請稍後再試。",
				"Claude 連接失敗",
				MessageBoxImage.Error);
		}
		finally
		{
			accountChangeCancellationRegistration.Dispose();
			account.AbortProviderAccountChange();
			_activeClaudeLogins.Remove(account.Id);
			operationSource?.Dispose();

			if ((loginCompletion is not null) &&
				(connectionCancellationSource is not null))
			{
				CompleteActiveAccountConnection(
					account.Id,
					connectionCancellationSource,
					loginCompletion);
			}
		}
	}

	public Task ConnectCodexAsync(
		Window owner,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);
		if (!account.IsCodex ||
			!account.CanConnectProviderAccount ||
			viewModel.IsManagingAccounts ||
			viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) ||
			!IsAttachedAndEnabled(viewModel, account) ||
			_activeCodexLogins.Contains(account.Id))
		{
			return Task.CompletedTask;
		}

		CodexWorkspacePromptWindow prompt = new()
		{
			Owner = owner
		};
		if (prompt.ShowDialog() != true)
		{
			return Task.CompletedTask;
		}

		return ConnectCodexAsync(
			account,
			viewModel,
			prompt.WorkspaceId,
			reportStatus,
			LaunchAuthorizationUri,
			(message, caption, image) =>
				WpfMessageBox.Show(
					owner,
					message,
					caption,
					MessageBoxButton.OK,
					image),
			authorizationUri =>
				ConfirmAuthorizationUri(owner, authorizationUri));
	}

	internal async Task ConnectCodexAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Guid? workspaceId,
		Action<string>? reportStatus,
		Action<Uri> launchAuthorizationUri,
		Action<string, string, MessageBoxImage>? reportNotice = null,
		Func<Uri, bool>? confirmAuthorizationUri = null)
	{
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);
		ArgumentNullException.ThrowIfNull(launchAuthorizationUri);
		if (workspaceId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex workspace ID 不可為空。",
				nameof(workspaceId));
		}

		if (!account.IsCodex ||
			!account.CanConnectProviderAccount ||
			viewModel.IsManagingAccounts ||
			viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) ||
			!IsAttachedAndEnabled(viewModel, account) ||
			!_activeCodexLogins.Add(account.Id))
		{
			return;
		}

		if ((workspaceId is not null) &&
			(_codexWorkspaceBindingStore is null))
		{
			_activeCodexLogins.Remove(account.Id);
			reportNotice?.Invoke(
				"這份 AI Usage 尚未包含完整的 Codex workspace 綁定元件，請更新後再試。",
				"Codex 連接元件不可用",
				MessageBoxImage.Warning);
			return;
		}

		CancellationToken lifetimeToken = _lifetimeSource.Token;
		bool didExternalLoginSucceed = false;
		bool didReleaseLoginSession = false;
		bool didPersistAuthenticatedBinding = false;
		CodexWorkspaceBinding? pendingRestartQuarantine = null;
		account.BeginProviderAccountChange();
		TaskCompletionSource? loginCompletion =
			TryRegisterActiveAccountConnection(
				account.Id,
				out CancellationTokenSource? connectionCancellationSource);

		if ((loginCompletion is null) ||
			(connectionCancellationSource is null))
		{
			account.AbortProviderAccountChange();
			_activeCodexLogins.Remove(account.Id);
			return;
		}

		using CancellationTokenSource operationSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				lifetimeToken,
				connectionCancellationSource.Token);
		CancellationToken operationToken = operationSource.Token;
		using CancellationTokenRegistration accountChangeCancellationRegistration =
			operationToken.Register(
				() => CancelProviderAccountChangeIfCancellable(
					account.Id,
					connectionCancellationSource,
					account));

		try
		{
			reportStatus?.Invoke(
				$"正在連接「{account.AccountName}」的 Codex 帳號…");
			CodexAccountLoginResult loginResult;
			bool didBeginCommit;
			ICodexWorkspaceConfigurationTransaction?
				workspaceConfigurationTransaction = null;
			try
			{
				Task<ICodexAccountLoginSession> startLoginTask =
				workspaceId is Guid expectedWorkspaceId
					? _codexAccountLogin.StartAsync(
						account.Id,
						expectedWorkspaceId,
						operationToken)
					: _codexAccountLogin.StartAsync(
						account.Id,
						operationToken);
				await using (ICodexAccountLoginSession session = await startLoginTask)
				{
					operationToken.ThrowIfCancellationRequested();
					if ((confirmAuthorizationUri is not null) &&
						!confirmAuthorizationUri(session.AuthorizationUri))
					{
						reportStatus?.Invoke(
							$"已取消「{account.AccountName}」的 Codex 帳號連接。");
						return;
					}

					operationToken.ThrowIfCancellationRequested();
					launchAuthorizationUri(session.AuthorizationUri);
					loginResult = await session.WaitForCompletionAsync(operationToken);
					didExternalLoginSucceed = true;
					if (loginResult.WorkspaceId != workspaceId)
					{
						throw new CodexAccountLoginException(
							"Codex 回傳的 workspace 與這次連接不符。請重新連接。");
					}
					accountChangeCancellationRegistration.Dispose();
					didBeginCommit =
						TryBeginAccountConnectionCommitAfterExternalSuccess(
							account.Id,
							connectionCancellationSource,
							account);
					if (didBeginCommit)
					{
						workspaceConfigurationTransaction =
							session.TakeWorkspaceConfigurationTransaction();
					}
				}
				didReleaseLoginSession = true;

				if (!didBeginCommit || !IsAttachedAndEnabled(viewModel, account))
				{
					return;
				}

				AuthenticatedProviderBindingCommitResult bindingResult =
					workspaceId is null
						? await TryPersistCodexAccountBindingAsync(
							account,
							viewModel,
							loginResult,
							CancellationToken.None,
							restartQuarantine =>
								pendingRestartQuarantine = restartQuarantine)
						: await TryPersistCodexWorkspaceBindingAsync(
							account,
							viewModel,
							loginResult,
							CancellationToken.None,
							restartQuarantine =>
								pendingRestartQuarantine = restartQuarantine);
				if (bindingResult !=
					AuthenticatedProviderBindingCommitResult.Succeeded)
				{
					reportStatus?.Invoke(
						GetAuthenticatedProviderBindingCommitFailureMessage(
							account,
							bindingResult,
							workspaceId is null
								? $"「{account.AccountName}」無法連接。這個 Codex 帳號已連到另一張卡片。若要用多張卡片分別顯示不同 workspace，每張卡片（包含原本第一張）都要用自己的 workspace ID 重新連接。"
								: $"「{account.AccountName}」無法連接。這個 Codex 帳號已有一般連接，或這個 workspace 已連到另一張卡片。若要用多張卡片分別顯示不同 workspace，每張卡片（包含原本第一張）都要填自己的 workspace ID。"));
					return;
				}

				(workspaceConfigurationTransaction ??
					throw new InvalidOperationException(
						"Codex workspace 設定 transaction 未建立。"))
					.Commit();
				didPersistAuthenticatedBinding = true;
			}
			finally
			{
				if (workspaceConfigurationTransaction is not null)
				{
					await workspaceConfigurationTransaction.DisposeAsync();
				}
			}

			if (operationToken.IsCancellationRequested)
			{
				if (!lifetimeToken.IsCancellationRequested)
				{
					reportStatus?.Invoke(
						GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
				}

				return;
			}

			await RefreshAuthenticatedProviderUsageAfterBindingAsync(
				account,
				viewModel,
				operationToken);
			operationToken.ThrowIfCancellationRequested();

			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			account.CompleteProviderAccountChange();

			reportStatus?.Invoke(
				GetCodexLoginCompletionMessage(account));
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
		{
			// The application is closing; no user-facing error is needed.
		}
		catch (Exception exception) when (lifetimeToken.IsCancellationRequested)
		{
			ReportDiagnostic(
				"codex-account-connection",
				"stage=shutdown-cleanup;result=failed",
				exception);
		}
		catch (Exception exception) when (didPersistAuthenticatedBinding)
		{
			// The provider switch is already durable. Any remaining refresh or
			// cleanup failure is recoverable without asking the user to reconnect.
			ReportDiagnostic(
				"codex-account-connection",
				"stage=committed-refresh;result=deferred",
				exception);
			reportStatus?.Invoke(
				GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
		}
		catch (OperationCanceledException) when (
			connectionCancellationSource.IsCancellationRequested)
		{
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Codex 帳號連接。");
		}
		catch (Exception exception) when (
			connectionCancellationSource.IsCancellationRequested)
		{
			// Process or browser cleanup can surface a secondary failure after cancel.
			ReportDiagnostic(
				"codex-account-connection",
				"stage=cancel-cleanup;result=failed",
				exception);
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Codex 帳號連接。");
		}
		catch (Exception exception) when (
			!IsAttachedAndEnabled(viewModel, account))
		{
			ReportDiagnostic(
				"codex-account-connection",
				"stage=detached-cleanup;result=failed",
				exception);
		}
		catch (CodexAccountLoginException exception)
		{
			(string message, string caption, MessageBoxImage image) =
				GetCodexLoginFailureNotice(exception);
			reportNotice?.Invoke(message, caption, image);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"codex-account-connection",
				"stage=coordinator;result=failed",
				exception);
			reportNotice?.Invoke(
				"無法開啟或完成 Codex 登入，請再試一次。",
				"Codex 登入失敗",
				MessageBoxImage.Error);
		}
		finally
		{
			if (didExternalLoginSucceed &&
				!didPersistAuthenticatedBinding)
			{
				await TryFailClosedCodexConnectionAsync(
					account,
					viewModel,
					didReleaseLoginSession,
					pendingRestartQuarantine);
			}

			account.AbortProviderAccountChange();
			_activeCodexLogins.Remove(account.Id);
			CompleteActiveAccountConnection(
				account.Id,
				connectionCancellationSource,
				loginCompletion);
		}
	}

	private async Task<AuthenticatedProviderBindingCommitResult>
		TryPersistCodexAccountBindingAsync(
			AccountUsageViewModel account,
			DashboardViewModel viewModel,
			CodexAccountLoginResult loginResult,
			CancellationToken cancellationToken,
			Action<CodexWorkspaceBinding?> reportPendingRestartQuarantine)
	{
		if (!ProviderAccountIdentityRules.TryNormalize(
				loginResult.AccountIdentity,
				out string? normalizedAccountIdentity) ||
			(normalizedAccountIdentity is null) ||
			(loginResult.WorkspaceId is not null))
		{
			throw new InvalidDataException(
				"Codex 帳號層級登入結果格式無效。");
		}

		if (_codexWorkspaceBindingStore is null)
		{
			return await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				normalizedAccountIdentity,
				cancellationToken);
		}

		ICodexWorkspaceBindingStore bindingStore = _codexWorkspaceBindingStore;
		return await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
			async () =>
			{
				using IDisposable bindingLease =
					await _codexBindingCommitGate.EnterAsync(cancellationToken);
				CodexWorkspaceBinding? restartQuarantine = null;

				try
				{
					restartQuarantine =
						CodexWorkspaceBinding.CreateRestartQuarantine(account.Id);
					CodexWorkspaceBinding? previousBinding =
						await LoadCurrentCodexWorkspaceBindingForReconnectAsync(
							bindingStore,
							account.Id,
							cancellationToken);
					restartQuarantine = previousBinding is
					{ IsRestartQuarantine: true }
						? previousBinding
						: restartQuarantine;
					IReadOnlyList<CodexWorkspaceBinding> allBindings =
						await bindingStore.LoadAllAsync(cancellationToken);
					foreach (CodexWorkspaceBinding existingBinding in allBindings)
					{
						if (existingBinding.AccountId == account.Id)
						{
							continue;
						}

						AccountUsageViewModel? owner = viewModel.Accounts
							.FirstOrDefault(candidate =>
								(candidate.Id == existingBinding.AccountId) &&
								candidate.IsCodex);
						if ((owner is not null) &&
							existingBinding.IsRestartQuarantine)
						{
							continue;
						}

						if ((owner is null) ||
							!existingBinding.MatchesPublicProfile(owner.Profile))
						{
							await DeleteCodexWorkspaceBindingAndVerifyAsync(
								bindingStore,
								existingBinding.AccountId,
								cancellationToken);
							continue;
						}

						if (existingBinding.MatchesAccount(normalizedAccountIdentity))
						{
							bool wasFailClosed = await TryClearCodexBindingCoreAsync(
								bindingStore,
								account,
								viewModel,
								cancellationToken,
								isIdentityConflict: true,
								restartQuarantine: restartQuarantine,
								reportPendingRestartQuarantine:
									reportPendingRestartQuarantine);
							return wasFailClosed
								? AuthenticatedProviderBindingCommitResult.IdentityConflict
								: AuthenticatedProviderBindingCommitResult
									.IdentityConflictRuntimeFailClosed;
						}
					}

					await DeleteCodexWorkspaceBindingAndVerifyAsync(
						bindingStore,
						account.Id,
						cancellationToken);
					AuthenticatedProviderBindingCommitResult result =
						await viewModel.TryPersistAuthenticatedProviderBindingAsync(
							account,
							normalizedAccountIdentity,
							cancellationToken,
							isAccountMutationGateHeld: true);
					if (result != AuthenticatedProviderBindingCommitResult.Succeeded)
					{
						await TryClearCodexBindingCoreAsync(
							bindingStore,
							account,
							viewModel,
							CancellationToken.None,
							isIdentityConflict:
								(result == AuthenticatedProviderBindingCommitResult
									.IdentityConflict) ||
								(result == AuthenticatedProviderBindingCommitResult
									.IdentityConflictRuntimeFailClosed),
							restartQuarantine: restartQuarantine,
							precomputedPublicBindingClearResult: result switch
							{
								AuthenticatedProviderBindingCommitResult.IdentityConflict =>
									true,
								AuthenticatedProviderBindingCommitResult
									.IdentityConflictRuntimeFailClosed => false,
								_ => null
							},
							reportPendingRestartQuarantine:
								reportPendingRestartQuarantine);
					}

					return result;
				}
				catch
				{
					await TryClearCodexBindingCoreAsync(
						bindingStore,
						account,
						viewModel,
						CancellationToken.None,
						isIdentityConflict: false,
						restartQuarantine: restartQuarantine,
						reportPendingRestartQuarantine:
							reportPendingRestartQuarantine);
					throw;
				}
			},
			cancellationToken);
	}

	private async Task<AuthenticatedProviderBindingCommitResult>
		TryPersistCodexWorkspaceBindingAsync(
			AccountUsageViewModel account,
			DashboardViewModel viewModel,
			CodexAccountLoginResult loginResult,
			CancellationToken cancellationToken,
			Action<CodexWorkspaceBinding?> reportPendingRestartQuarantine)
	{
		ICodexWorkspaceBindingStore bindingStore =
			_codexWorkspaceBindingStore ??
			throw new InvalidOperationException(
				"Codex workspace binding store is not configured.");

		return await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
			async () =>
			{
				using IDisposable bindingLease =
					await _codexBindingCommitGate.EnterAsync(cancellationToken);
				CodexWorkspaceBinding? restartQuarantine = null;

				try
				{
					if (!ProviderAccountIdentityRules.TryNormalize(
							loginResult.AccountIdentity,
							out string? normalizedAccountIdentity) ||
						(normalizedAccountIdentity is null) ||
						(loginResult.WorkspaceId is not Guid workspaceId) ||
						(workspaceId == Guid.Empty))
					{
						throw new InvalidDataException(
							"Codex 帳號或 workspace 格式無效。");
					}

					restartQuarantine =
						CodexWorkspaceBinding.CreateRestartQuarantine(account.Id);
					CodexWorkspaceBinding? previousBinding =
						await LoadCurrentCodexWorkspaceBindingForReconnectAsync(
							bindingStore,
							account.Id,
							cancellationToken);
					restartQuarantine = previousBinding is
					{ IsRestartQuarantine: true }
						? previousBinding
						: restartQuarantine;
					IReadOnlyList<CodexWorkspaceBinding> allBindings =
						await bindingStore.LoadAllAsync(cancellationToken);
					List<CodexWorkspaceBinding> pairedBindings = new();
					foreach (CodexWorkspaceBinding existingBinding in allBindings)
					{
						if (existingBinding.AccountId == account.Id)
						{
							continue;
						}

						AccountUsageViewModel? owner = viewModel.Accounts
							.FirstOrDefault(candidate =>
								(candidate.Id == existingBinding.AccountId) &&
								candidate.IsCodex);
						if ((owner is not null) &&
							existingBinding.IsRestartQuarantine)
						{
							continue;
						}

						if ((owner is null) ||
							!existingBinding.MatchesPublicProfile(owner.Profile))
						{
							await DeleteCodexWorkspaceBindingAndVerifyAsync(
								bindingStore,
								existingBinding.AccountId,
								cancellationToken);
							continue;
						}

						pairedBindings.Add(existingBinding);
					}

					bool hasLegacyOwner = viewModel.Accounts.Any(candidate =>
						(candidate.Id != account.Id) &&
						candidate.IsCodex &&
						!CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
							candidate.ProviderAccountOwnershipIdentity,
							out _) &&
						ProviderAccountIdentityRules.TryNormalize(
							candidate.ProviderAccountOwnershipIdentity,
							out string? legacyIdentity) &&
						ProviderAccountIdentityRules.Comparer.Equals(
							legacyIdentity,
							normalizedAccountIdentity));
					bool hasEntitlementOwner = pairedBindings.Any(binding =>
						binding.MatchesEntitlement(
							normalizedAccountIdentity,
							workspaceId));
					if (hasLegacyOwner || hasEntitlementOwner)
					{
						bool wasFailClosed =
							await TryClearCodexBindingCoreAsync(
								bindingStore,
								account,
								viewModel,
								cancellationToken,
								isIdentityConflict: true,
								restartQuarantine: restartQuarantine,
								reportPendingRestartQuarantine:
									reportPendingRestartQuarantine);
						return wasFailClosed
							? AuthenticatedProviderBindingCommitResult.IdentityConflict
							: AuthenticatedProviderBindingCommitResult
								.IdentityConflictRuntimeFailClosed;
					}

					Guid publicBindingId = (previousBinding is not null) &&
						previousBinding.MatchesEntitlement(
							normalizedAccountIdentity,
							workspaceId)
							? previousBinding.PublicBindingId
							: Guid.NewGuid();
					CodexWorkspaceBinding binding =
						CodexWorkspaceBinding.Create(
							account.Id,
							publicBindingId,
							normalizedAccountIdentity,
							workspaceId);
					await bindingStore.SaveAsync(binding, cancellationToken);
					CodexWorkspaceBinding? persistedBinding =
						await bindingStore.LoadAsync(account.Id, cancellationToken);
					if (!Equals(binding, persistedBinding))
					{
						throw new IOException(
							"Codex workspace binding 未通過持久化 read-back 驗證。");
					}

					string publicBindingIdentity =
						CodexWorkspaceBinding.CreatePublicBindingIdentity(
							binding.PublicBindingId);
					AuthenticatedProviderBindingCommitResult result =
						await viewModel.TryPersistAuthenticatedProviderBindingAsync(
							account,
							publicBindingIdentity,
							cancellationToken,
							isAccountMutationGateHeld: true);
					if (result !=
						AuthenticatedProviderBindingCommitResult.Succeeded)
					{
						await TryClearCodexBindingCoreAsync(
							bindingStore,
							account,
							viewModel,
							CancellationToken.None,
							isIdentityConflict:
								(result == AuthenticatedProviderBindingCommitResult
									.IdentityConflict) ||
								(result == AuthenticatedProviderBindingCommitResult
									.IdentityConflictRuntimeFailClosed),
							restartQuarantine: restartQuarantine,
							precomputedPublicBindingClearResult: result switch
							{
								AuthenticatedProviderBindingCommitResult.IdentityConflict =>
									true,
								AuthenticatedProviderBindingCommitResult
									.IdentityConflictRuntimeFailClosed => false,
								_ => null
							},
							reportPendingRestartQuarantine:
								reportPendingRestartQuarantine);
					}

					return result;
				}
				catch
				{
					await TryClearCodexBindingCoreAsync(
						bindingStore,
						account,
						viewModel,
						CancellationToken.None,
						isIdentityConflict: false,
						restartQuarantine: restartQuarantine,
						reportPendingRestartQuarantine:
							reportPendingRestartQuarantine);
					throw;
				}
			},
			cancellationToken);
	}

	private static async Task<CodexWorkspaceBinding?>
		LoadCurrentCodexWorkspaceBindingForReconnectAsync(
			ICodexWorkspaceBindingStore bindingStore,
			Guid accountId,
			CancellationToken cancellationToken)
	{
		try
		{
			return await bindingStore.LoadAsync(accountId, cancellationToken);
		}
		catch (Exception exception) when (
			exception is InvalidDataException or
				IOException or
				UnauthorizedAccessException)
		{
			await DeleteCodexWorkspaceBindingAndVerifyAsync(
				bindingStore,
				accountId,
				cancellationToken);
			return CodexWorkspaceBinding.CreateRestartQuarantine(accountId);
		}
	}

	private async Task TryFailClosedCodexConnectionAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		bool canDeletePrivateBinding,
		CodexWorkspaceBinding? pendingRestartQuarantine)
	{
		if (!account.IsProviderAccountChangeInProgress &&
			(pendingRestartQuarantine is null))
		{
			return;
		}

		try
		{
			if (pendingRestartQuarantine is not null)
			{
				ICodexWorkspaceBindingStore bindingStore =
					_codexWorkspaceBindingStore ??
					throw new InvalidOperationException(
						"Codex workspace binding store is not configured.");
				await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
					async () =>
					{
						using IDisposable bindingLease =
							await _codexBindingCommitGate.EnterAsync(
								CancellationToken.None);
						await SaveCodexRestartQuarantineAndVerifyAsync(
							bindingStore,
							pendingRestartQuarantine,
							CancellationToken.None);
						return true;
					},
					CancellationToken.None);
				return;
			}

			if (canDeletePrivateBinding &&
				(_codexWorkspaceBindingStore is not null))
			{
				await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
					async () =>
					{
						using IDisposable bindingLease =
							await _codexBindingCommitGate.EnterAsync(
								CancellationToken.None);
						CodexWorkspaceBinding? restartQuarantine =
							CodexWorkspaceBinding.CreateRestartQuarantine(account.Id);
						try
						{
							CodexWorkspaceBinding? currentBinding =
								await LoadCurrentCodexWorkspaceBindingForReconnectAsync(
									_codexWorkspaceBindingStore,
									account.Id,
									CancellationToken.None);
							restartQuarantine = currentBinding is
								{ IsRestartQuarantine: true }
									? currentBinding
									: restartQuarantine;
						}
						catch (Exception exception) when (
							exception is InvalidDataException or
								IOException or
								UnauthorizedAccessException)
						{
							// 讀取狀態不明時，cleanup 必須保留可持久化的 quarantine。
						}

						return await TryClearCodexBindingCoreAsync(
							_codexWorkspaceBindingStore,
							account,
							viewModel,
							CancellationToken.None,
							isIdentityConflict: false,
							restartQuarantine: restartQuarantine);
					},
					CancellationToken.None);
				return;
			}

			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account,
				CancellationToken.None,
				isIdentityConflict: false);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"codex-account-connection",
				"stage=fail-closed-cleanup;result=failed",
				exception);
		}
	}

	private static async Task<bool> TryClearCodexBindingCoreAsync(
		ICodexWorkspaceBindingStore bindingStore,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		CancellationToken cancellationToken,
		bool isIdentityConflict,
		CodexWorkspaceBinding? restartQuarantine = null,
		bool? precomputedPublicBindingClearResult = null,
		Action<CodexWorkspaceBinding?>? reportPendingRestartQuarantine = null)
	{
		if ((restartQuarantine is not null) &&
			(!restartQuarantine.IsRestartQuarantine ||
			(restartQuarantine.AccountId != account.Id)))
		{
			throw new ArgumentException(
				"Codex restart quarantine 與目標帳號不符。",
				nameof(restartQuarantine));
		}

		bool wasPrivateBindingDeleted = false;
		try
		{
			await DeleteCodexWorkspaceBindingAndVerifyAsync(
				bindingStore,
				account.Id,
				cancellationToken);
			wasPrivateBindingDeleted = true;
		}
		catch (Exception exception) when (
			exception is IOException or
				UnauthorizedAccessException or
				InvalidDataException or
				OperationCanceledException)
		{
			// 清除 public profile/cache 後，殘留的 private binding 也不再有效。
		}

		bool wasPublicBindingCleared = precomputedPublicBindingClearResult ??
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account,
				CancellationToken.None,
				isAccountMutationGateHeld: true,
				isIdentityConflict: isIdentityConflict);
		if (wasPublicBindingCleared)
		{
			reportPendingRestartQuarantine?.Invoke(null);
		}
		else if (restartQuarantine is not null)
		{
			reportPendingRestartQuarantine?.Invoke(restartQuarantine);
			await SaveCodexRestartQuarantineAndVerifyAsync(
				bindingStore,
				restartQuarantine,
				CancellationToken.None);
			reportPendingRestartQuarantine?.Invoke(null);
		}

		return wasPrivateBindingDeleted && wasPublicBindingCleared;
	}

	private static async Task SaveCodexRestartQuarantineAndVerifyAsync(
		ICodexWorkspaceBindingStore bindingStore,
		CodexWorkspaceBinding restartQuarantine,
		CancellationToken cancellationToken)
	{
		await bindingStore.SaveAsync(restartQuarantine, cancellationToken);
		CodexWorkspaceBinding? persistedBinding = await bindingStore.LoadAsync(
			restartQuarantine.AccountId,
			cancellationToken);
		if (!Equals(restartQuarantine, persistedBinding))
		{
			throw new IOException(
				"Codex restart quarantine 未通過持久化 read-back 驗證。");
		}
	}

	private static async Task DeleteCodexWorkspaceBindingAndVerifyAsync(
		ICodexWorkspaceBindingStore bindingStore,
		Guid accountId,
		CancellationToken cancellationToken)
	{
		await bindingStore.DeleteAsync(accountId, cancellationToken);
		if (await bindingStore.LoadAsync(accountId, cancellationToken) is not null)
		{
			throw new IOException(
				"Codex workspace binding 刪除未通過 read-back 驗證。");
		}
	}

	public Task ConnectCopilotAsync(
		Window owner,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null,
		bool allowAccountSwitch = false)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		return ConnectCopilotAsync(
			account,
			viewModel,
			allowAccountSwitch,
			reportStatus,
			(message, caption, image) =>
				WpfMessageBox.Show(
					owner,
					message,
					caption,
					MessageBoxButton.OK,
					image),
			usageReport => ConfirmCopilotAccount(owner, usageReport));
	}

	internal async Task ConnectCopilotAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		bool allowAccountSwitch,
		Action<string>? reportStatus = null,
		Action<string, string, MessageBoxImage>? reportNotice = null,
		Func<CopilotUsageReport, bool>? confirmNewAccount = null)
	{
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		if (!account.IsCopilot ||
			!account.CanConnectProviderAccount ||
			viewModel.IsManagingAccounts ||
			viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) ||
			!IsAttachedAndEnabled(viewModel, account) ||
			!_activeCopilotLogins.Add(account.Id))
		{
			return;
		}

		if (_copilotAccountConnector is null)
		{
			_activeCopilotLogins.Remove(account.Id);
			reportNotice?.Invoke(
				"Copilot 元件尚未就緒，請更新或修復 AI Usage。",
				"Copilot 無法使用",
				MessageBoxImage.Information);
			return;
		}

		string? expectedProviderAccountIdentity =
			account.Profile.ProviderAccountIdentity;
		CancellationToken lifetimeToken = _lifetimeSource.Token;
		TaskCompletionSource? loginCompletion = null;
		CancellationTokenSource? connectionCancellationSource = null;
		CancellationTokenSource? operationSource = null;
		CancellationTokenRegistration accountChangeCancellationRegistration =
			default;
		bool didCommitCredential = false;

		try
		{
			loginCompletion = TryRegisterActiveAccountConnection(
				account.Id,
				out connectionCancellationSource);
			if ((loginCompletion is null) ||
				(connectionCancellationSource is null))
			{
				return;
			}

			operationSource = CancellationTokenSource.CreateLinkedTokenSource(
				lifetimeToken,
				connectionCancellationSource.Token);
			CancellationToken operationToken = operationSource.Token;
			accountChangeCancellationRegistration = operationToken.Register(
				() => CancelProviderAccountChangeIfCancellable(
					account.Id,
					connectionCancellationSource,
					account));

			account.BeginProviderAccountChange();
			if (!account.IsProviderAccountChangeInProgress)
			{
				return;
			}

			reportStatus?.Invoke(
				$"正在登入「{account.AccountName}」的 Copilot 帳號…");
			await using ICopilotConnectionCandidate candidate =
				await _copilotAccountConnector.BeginConnectAsync(
					account.Id,
					expectedProviderAccountIdentity,
					allowAccountSwitch,
					operationToken);
			operationToken.ThrowIfCancellationRequested();

			CopilotUsageReport usageReport = candidate.UsageReport ??
				throw new CopilotClientException(
					CopilotFailureKind.InvalidResponse,
					"Copilot 未回傳可驗證的帳號資訊。");
			string providerAccountIdentity = CopilotAccountIdentityRules.Create(
				usageReport.Account);
			bool isNewAccount = string.IsNullOrWhiteSpace(
				expectedProviderAccountIdentity);
			if (isNewAccount &&
				((confirmNewAccount is null) || !confirmNewAccount(usageReport)))
			{
				reportStatus?.Invoke(
					$"已取消「{account.AccountName}」的 Copilot 帳號連接。");
				return;
			}

			operationToken.ThrowIfCancellationRequested();
			accountChangeCancellationRegistration.Dispose();
			if (!IsAttachedAndEnabled(viewModel, account) ||
				!TryBeginAccountConnectionCommitAfterExternalSuccess(
					account.Id,
					connectionCancellationSource,
					account,
					providerAccountIdentity))
			{
				return;
			}

			AuthenticatedProviderBindingCommitResult bindingResult =
				await viewModel.TryPersistAuthenticatedProviderBindingAsync(
					account,
					providerAccountIdentity,
					CancellationToken.None,
					preserveExistingBindingOnIdentityConflict: true);
			if (bindingResult !=
				AuthenticatedProviderBindingCommitResult.Succeeded)
			{
				reportStatus?.Invoke(
					GetAuthenticatedProviderBindingCommitFailureMessage(
						account,
						bindingResult,
						$"「{account.AccountName}」無法連接。這個 GitHub 帳號已連到另一張 Copilot 卡片；原本的卡片連接不受影響。"));
				return;
			}

			try
			{
				await candidate.CommitAsync(CancellationToken.None);
				didCommitCredential = true;
			}
			catch (Exception exception)
			{
				ReportDiagnostic(
					"copilot-account-connection",
					"stage=credential-promote;result=failed",
					exception);
				bool didFailClosed = false;

				try
				{
					didFailClosed =
						await viewModel
							.FailClosedAuthenticatedProviderBindingConflictAsync(
								account,
								CancellationToken.None,
								isIdentityConflict: false);
				}
				catch (Exception cleanupException)
				{
					ReportDiagnostic(
						"copilot-account-connection",
						"stage=credential-promote-fail-closed;result=failed",
						cleanupException);
				}

				reportNotice?.Invoke(
					didFailClosed
						? "Copilot 登入已完成，但無法安全完成 credential 儲存。這張卡片已停止用量檢查，請重新連接。"
						: "Copilot credential 儲存未完成，且無法完整保存安全狀態。AI Usage 不會使用這次登入結果；請重新啟動後再連接。",
					"Copilot 連接未完成",
					MessageBoxImage.Error);
				return;
			}

			if (operationToken.IsCancellationRequested)
			{
				if (!lifetimeToken.IsCancellationRequested)
				{
					reportStatus?.Invoke(
						GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
				}

				return;
			}

			await RefreshAuthenticatedProviderUsageAfterBindingAsync(
				account,
				viewModel,
				operationToken);
			operationToken.ThrowIfCancellationRequested();
			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			account.CompleteProviderAccountChange();
			reportStatus?.Invoke(GetCopilotLoginCompletionMessage(account));
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
		{
			// App 關閉時不顯示額外錯誤。
		}
		catch (Exception exception) when (lifetimeToken.IsCancellationRequested)
		{
			ReportDiagnostic(
				"copilot-account-connection",
				"stage=shutdown-cleanup;result=failed",
				exception);
		}
		catch (Exception exception) when (didCommitCredential)
		{
			ReportDiagnostic(
				"copilot-account-connection",
				"stage=committed-refresh;result=deferred",
				exception);
			reportStatus?.Invoke(
				GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
		}
		catch (OperationCanceledException) when (
			connectionCancellationSource?.IsCancellationRequested == true)
		{
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Copilot 帳號連接。");
		}
		catch (CopilotAccountLoginException exception) when (
			exception.Kind == CopilotAccountLoginFailureKind.Cancelled)
		{
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Copilot 帳號連接。");
		}
		catch (CopilotClientException exception)
		{
			(string message, string caption, MessageBoxImage image) =
				GetCopilotLoginFailureNotice(exception);
			reportNotice?.Invoke(message, caption, image);
		}
		catch (CopilotAccountLoginException exception)
		{
			(string message, string caption, MessageBoxImage image) =
				GetCopilotLoginFailureNotice(exception);
			reportNotice?.Invoke(message, caption, image);
		}
		catch (Exception exception) when (
			!IsAttachedAndEnabled(viewModel, account))
		{
			ReportDiagnostic(
				"copilot-account-connection",
				"stage=detached-cleanup;result=failed",
				exception);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"copilot-account-connection",
				"stage=coordinator;result=failed",
				exception);
			reportNotice?.Invoke(
				"無法完成 Copilot 登入。原本的卡片連接不受影響，請稍後再試。",
				"Copilot 連接失敗",
				MessageBoxImage.Error);
		}
		finally
		{
			accountChangeCancellationRegistration.Dispose();
			account.AbortProviderAccountChange();
			_activeCopilotLogins.Remove(account.Id);
			operationSource?.Dispose();

			if ((loginCompletion is not null) &&
				(connectionCancellationSource is not null))
			{
				CompleteActiveAccountConnection(
					account.Id,
					connectionCancellationSource,
					loginCompletion);
			}
		}
	}

	public Task ConnectGrokAsync(
		Window owner,
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		return ConnectGrokAsync(
			account,
			viewModel,
			reportStatus,
			(message, caption, image) =>
				WpfMessageBox.Show(
					owner,
					message,
					caption,
					MessageBoxButton.OK,
					image));
	}

	internal async Task ConnectGrokAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Action<string>? reportStatus = null,
		Action<string, string, MessageBoxImage>? reportNotice = null)
	{
		ArgumentNullException.ThrowIfNull(account);
		ArgumentNullException.ThrowIfNull(viewModel);

		if (!account.IsGrok ||
			!account.CanConnectProviderAccount ||
			viewModel.IsManagingAccounts ||
			viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) ||
			!IsAttachedAndEnabled(viewModel, account) ||
			!_activeGrokLogins.Add(account.Id))
		{
			return;
		}

		if ((_grokAccountLogin is null) ||
			(_grokUsagePoller is null) ||
			(_grokAccountBindingStore is null) ||
			(_grokConnectionPendingStore is null))
		{
			_activeGrokLogins.Remove(account.Id);
			reportNotice?.Invoke(
				"這份 AI Usage 尚未包含完整的 Grok 連接元件，請更新後再試。",
				"Grok 連接元件不可用",
				MessageBoxImage.Warning);
			return;
		}

		CancellationToken lifetimeToken = _lifetimeSource.Token;
		bool didExternalLoginSucceed = false;
		bool didPersistAuthenticatedBinding = false;
		bool didBeginLoginJournal = false;
		bool didStartExternalLogin = false;
		bool shouldDiscardLoginStartedJournal = false;
		Guid attemptId = Guid.NewGuid();
		account.BeginProviderAccountChange();
		TaskCompletionSource? loginCompletion =
			TryRegisterActiveAccountConnection(
				account.Id,
				out CancellationTokenSource? connectionCancellationSource);

		if ((loginCompletion is null) ||
			(connectionCancellationSource is null))
		{
			account.AbortProviderAccountChange();
			_activeGrokLogins.Remove(account.Id);
			return;
		}

		using CancellationTokenSource operationSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				lifetimeToken,
				connectionCancellationSource.Token);
		CancellationToken operationToken = operationSource.Token;
		using CancellationTokenRegistration accountChangeCancellationRegistration =
			operationToken.Register(
				() => CancelProviderAccountChangeIfCancellable(
					account.Id,
					connectionCancellationSource,
					account));

		try
		{
			GrokConnectionBeginResult beginResult =
				await _grokConnectionPendingStore.BeginAsync(
				account.Id,
				attemptId,
				DateTimeOffset.UtcNow,
				operationToken);
			if (beginResult ==
				GrokConnectionBeginResult.BlockedByExistingRecoverableWork)
			{
				reportNotice?.Invoke(
					"這個帳號有尚未完成的 Grok 連接。為避免覆寫可能已切換的帳號，這次不會開始新的登入。請重新啟動 AI Usage，完成後再試。",
					"Grok 帳號連接仍待恢復",
					MessageBoxImage.Warning);
				return;
			}

			didBeginLoginJournal = true;
			if (beginResult ==
				GrokConnectionBeginResult.StartedAfterCorruptStateQuarantined)
			{
				reportNotice?.Invoke(
					"上次 Grok 連接進度已損壞。AI Usage 已保留原檔，這次會重新登入，不會套用無法確認的資料。",
					"已保留損壞的 Grok 連接資料",
					MessageBoxImage.Warning);
			}
			else if (beginResult ==
				GrokConnectionBeginResult.ReplacedIncompleteAttempt)
			{
				reportStatus?.Invoke(
					"先前尚未完成的 Grok 登入已由這次重新連接取代。");
			}

			reportStatus?.Invoke(
				$"已開啟「{account.AccountName}」的 Grok 登入終端機。請依終端機提示完成瀏覽器登入；若網頁要求複製內容，請把授權碼或完整回呼網址貼回終端機並按 Enter。");
			didStartExternalLogin = true;
			await _grokAccountLogin.LoginAsync(account.Id, operationToken);
			didExternalLoginSucceed = true;
			accountChangeCancellationRegistration.Dispose();
			if (!await _grokConnectionPendingStore.TryAdvanceAsync(
					account.Id,
					attemptId,
					GrokConnectionPendingStage.LoginCompleted,
					publicBindingId: null,
					DateTimeOffset.UtcNow,
					CancellationToken.None))
			{
				throw new InvalidOperationException(
					"Grok connection journal could not record login completion.");
			}

			if (!TryBeginAccountConnectionCommitAfterExternalSuccess(
					account.Id,
					connectionCancellationSource,
					account))
			{
				reportStatus?.Invoke(
					"Grok 登入已完成，但 AI Usage 暫時無法儲存連接資料。重新啟動後會自動再試。");
				return;
			}

			// Once the external credential switch succeeds, cancellation must not
			// create a binding/profile split. Every remaining durable step wins.
			GrokUsagePollResult usage = await _grokUsagePoller.PollAsync(
				account.Id,
				CancellationToken.None);
			if (!usage.CanCommitBinding)
			{
				throw new GrokUsageNotConfiguredException(
					"Grok current authentication could not be verified.");
			}

			bool wasBindingCommitCompleted =
				await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
					async () =>
					{
						using (await _grokBindingCommitGate.EnterAsync(
							CancellationToken.None))
						{
							await DeleteOrphanGrokBindingsAsync(
								viewModel,
								account.Id,
								CancellationToken.None);
							if (await IsGrokPrincipalBoundToAnotherAccountAsync(
									viewModel,
									account.Id,
									usage.Principal,
									CancellationToken.None))
							{
								GrokConflictReconciliationResult reconciliationResult =
									await TryReconcileConflictingGrokConnectionAsync(
										account,
										viewModel,
										attemptId,
										isAccountMutationGateHeld: true);
								if (reconciliationResult.CleanupResult !=
									GrokConflictCleanupResult.Succeeded)
								{
									viewModel.SetGrokConnectionRecoveryPending(isPending: true);
								}

								reportStatus?.Invoke(GetGrokConflictCleanupMessage(
									account,
									reconciliationResult.CleanupResult,
									"這個 Grok 帳號已連接到另一張帳號卡片",
									reconciliationResult.WasProfileDisconnectPersisted));
								return false;
							}

							GrokAccountBinding binding = GrokAccountBinding.Create(
								account.Id,
								attemptId,
								usage.Principal.PrincipalType,
								usage.Principal.PrincipalId);
							await _grokAccountBindingStore.SaveAsync(
								binding,
								CancellationToken.None);
							if (!await _grokConnectionPendingStore.TryAdvanceAsync(
									account.Id,
									attemptId,
									GrokConnectionPendingStage.BindingPersisted,
									binding.PublicBindingId,
									DateTimeOffset.UtcNow,
									CancellationToken.None))
							{
								throw new InvalidOperationException(
									"Grok connection journal could not record binding persistence.");
							}

							string publicBindingIdentity =
								GrokAccountBinding.CreatePublicBindingIdentity(
									binding.PublicBindingId);
							account.SeedProviderAccountIdentity(publicBindingIdentity);
							AuthenticatedProviderBindingCommitResult bindingResult =
								await viewModel.TryPersistAuthenticatedProviderBindingAsync(
									account,
									publicBindingIdentity,
									CancellationToken.None,
									isAccountMutationGateHeld: true);
							if (bindingResult !=
								AuthenticatedProviderBindingCommitResult.Succeeded)
							{
								if ((bindingResult ==
										AuthenticatedProviderBindingCommitResult.IdentityConflict) ||
									(bindingResult ==
										AuthenticatedProviderBindingCommitResult
											.IdentityConflictRuntimeFailClosed))
								{
									GrokConflictCleanupResult cleanupResult =
										await TryRemoveConflictingGrokBindingAndJournalAsync(
											account.Id,
											attemptId);
									if (cleanupResult != GrokConflictCleanupResult.Succeeded)
									{
										viewModel.SetGrokConnectionRecoveryPending(isPending: true);
									}

									reportStatus?.Invoke(GetGrokConflictCleanupMessage(
										account,
										cleanupResult,
										"這個 Grok 帳號已連接到另一張帳號卡片",
										bindingResult == AuthenticatedProviderBindingCommitResult
											.IdentityConflict));
									return false;
								}

								reportStatus?.Invoke(
									GetAuthenticatedProviderBindingCommitFailureMessage(
										account,
										bindingResult,
										$"「{account.AccountName}」無法連接：這個 Grok 帳號已連接到另一張帳號卡片。"));
								return false;
							}
							didPersistAuthenticatedBinding = true;

							if (!await _grokConnectionPendingStore.TryRemoveAsync(
									account.Id,
									attemptId,
									CancellationToken.None))
							{
								throw new InvalidOperationException(
									"Grok connection journal cleanup did not complete.");
							}

							return true;
						}
					},
					CancellationToken.None);
			if (!wasBindingCommitCompleted)
			{
				return;
			}

			if (operationToken.IsCancellationRequested)
			{
				if (!lifetimeToken.IsCancellationRequested)
				{
					reportStatus?.Invoke(
						GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
				}

				return;
			}

			await RefreshAuthenticatedProviderUsageAfterBindingAsync(
				account,
				viewModel,
				operationToken);
			operationToken.ThrowIfCancellationRequested();

			if (!IsAttachedAndEnabled(viewModel, account))
			{
				return;
			}

			account.CompleteProviderAccountChange();
			reportStatus?.Invoke(GetGrokLoginCompletionMessage(account));
		}
		catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
		{
			// Application shutdown does not interrupt the post-login commit barrier.
		}
		catch (Exception exception) when (lifetimeToken.IsCancellationRequested)
		{
			shouldDiscardLoginStartedJournal |=
				!didExternalLoginSucceed &&
				IsKnownGrokPreflightFailure(exception);
			ReportDiagnostic(
				"grok-account-connection",
				"stage=shutdown-cleanup;result=failed",
				exception);
		}
		catch (Exception exception) when (didPersistAuthenticatedBinding)
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=committed-refresh;result=deferred",
				exception);
			reportStatus?.Invoke(
				GetAuthenticatedProviderBindingRefreshDeferredMessage(account));
		}
		catch (OperationCanceledException) when (
			connectionCancellationSource.IsCancellationRequested)
		{
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Grok 帳號連接。");
		}
		catch (GrokProcessContainmentException exception)
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=process-containment;result=failed",
				exception);
			reportNotice?.Invoke(
				"AI Usage 無法確認先前的 Grok 程序是否已結束，因此這次不會再嘗試連接。請重新啟動 AI Usage 後再連接這個帳號。",
				"必須重新啟動 AI Usage",
				MessageBoxImage.Error);
		}
		catch (Exception exception) when (
			connectionCancellationSource.IsCancellationRequested)
		{
			shouldDiscardLoginStartedJournal |=
				!didExternalLoginSucceed &&
				IsKnownGrokPreflightFailure(exception);
			ReportDiagnostic(
				"grok-account-connection",
				"stage=cancel-cleanup;result=failed",
				exception);
			reportStatus?.Invoke(
				$"已取消「{account.AccountName}」的 Grok 帳號連接。");
		}
		catch (Exception exception) when (
			!IsAttachedAndEnabled(viewModel, account))
		{
			shouldDiscardLoginStartedJournal |=
				!didExternalLoginSucceed &&
				IsKnownGrokPreflightFailure(exception);
			ReportDiagnostic(
				"grok-account-connection",
				"stage=detached-cleanup;result=failed",
				exception);
		}
		catch (GrokCliNotFoundException)
		{
			if (!didExternalLoginSucceed)
			{
				shouldDiscardLoginStartedJournal = true;
			}

			reportNotice?.Invoke(
				"找不到官方 Grok Build CLI。請從 xAI 官方來源安裝或更新後再重新連接。",
				"需要安裝或更新 Grok Build CLI",
				MessageBoxImage.Warning);
		}
		catch (GrokCliUntrustedException)
		{
			if (!didExternalLoginSucceed)
			{
				shouldDiscardLoginStartedJournal = true;
			}

			reportNotice?.Invoke(
				"目前的 Grok Build CLI 不是受支援的官方版本，或安裝位置不符合要求。請從 xAI 官方來源重新安裝或更新後再試。",
				"需要安裝或更新 Grok Build CLI",
				MessageBoxImage.Warning);
		}
		catch (GrokAcpFailureException exception) when (
			exception.Category == GrokAcpFailureCategory.Authentication)
		{
			reportNotice?.Invoke(
				"Grok 登入程序已結束，但仍無法確認官方帳號。AI Usage 已記錄進度，重新啟動後會再確認；若仍顯示此訊息，請重新連接。",
				"Grok 帳號尚未驗證",
				MessageBoxImage.Warning);
		}
		catch (GrokUsageNotConfiguredException)
		{
			reportNotice?.Invoke(
				"Grok 登入程序已結束，但尚未確認可讀取用量的帳號。AI Usage 已記錄進度，重新啟動後會再確認；若仍顯示此訊息，請重新連接。",
				"Grok 帳號尚未驗證",
				MessageBoxImage.Warning);
		}
		catch (GrokAccountLoginException exception)
		{
			(string message, string caption, MessageBoxImage image) =
				GetGrokLoginFailureNotice(exception);
			reportNotice?.Invoke(message, caption, image);
		}
		catch (Exception exception)
		{
			if (!didStartExternalLogin)
			{
				shouldDiscardLoginStartedJournal = true;
			}
			ReportDiagnostic(
				"grok-account-connection",
				"stage=coordinator;result=failed",
				exception);

			reportNotice?.Invoke(
				didExternalLoginSucceed
					? "Grok 登入可能已完成，但 AI Usage 尚未確認帳號連接。AI Usage 已記錄進度，重新啟動後會自動繼續處理；若仍顯示此訊息，請重新連接。"
					: "無法開啟或完成 Grok 登入，請再試一次。",
				"Grok 帳號連接未完成",
				MessageBoxImage.Error);
		}
		finally
		{
			if (didBeginLoginJournal &&
				shouldDiscardLoginStartedJournal &&
				!didExternalLoginSucceed)
			{
				await TryDiscardKnownPreflightGrokJournalAsync(
					account.Id,
					attemptId);
			}

			account.AbortProviderAccountChange();
			_activeGrokLogins.Remove(account.Id);
			CompleteActiveAccountConnection(
				account.Id,
				connectionCancellationSource,
				loginCompletion);
		}
	}

	private async Task TryDiscardKnownPreflightGrokJournalAsync(
		Guid accountId,
		Guid attemptId)
	{
		try
		{
			if ((_grokConnectionPendingStore is null) ||
				!await _grokConnectionPendingStore.TryRemoveAsync(
					accountId,
					attemptId,
					CancellationToken.None))
			{
				ReportDiagnostic(
					"grok-account-connection",
					$"stage=preflight-journal-cleanup;account_id={accountId:N};result=not-removed");
			}
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"grok-account-connection",
				$"stage=preflight-journal-cleanup;account_id={accountId:N};result=failed",
				exception);
		}
	}

	private static bool IsKnownGrokPreflightFailure(Exception exception)
	{
		return exception is GrokCliNotFoundException or
			GrokCliUntrustedException;
	}

	internal async Task<bool> CancelAndDrainAccountConnectionsAsync(
		TimeSpan timeout)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Task[] activeConnections;

		lock (_activeConnectionCompletionsLock)
		{
			_isConnectionShutdownStarted = true;
			_isUpdateShutdownReservationActive = false;
			activeConnections = _activeConnectionCompletions
				.Select(completion => completion.Task)
				.ToArray();
		}

		if (!_lifetimeSource.IsCancellationRequested)
		{
			_lifetimeSource.Cancel();
		}

		if (activeConnections.Length == 0)
		{
			return true;
		}

		try
		{
			await Task.WhenAll(activeConnections).WaitAsync(timeout);
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;

		lock (_activeConnectionCompletionsLock)
		{
			_isConnectionShutdownStarted = true;
			_isUpdateShutdownReservationActive = false;
		}

		if (!_lifetimeSource.IsCancellationRequested)
		{
			_lifetimeSource.Cancel();
		}

		_lifetimeSource.Dispose();
	}

	private TaskCompletionSource? TryRegisterActiveConnection()
	{
		lock (_activeConnectionCompletionsLock)
		{
			if (_isConnectionShutdownStarted)
			{
				return null;
			}

			TaskCompletionSource completion = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			_activeConnectionCompletions.Add(completion);
			return completion;
		}
	}

	private bool IsAntigravitySetupActive(Guid accountId)
	{
		lock (_activeConnectionCompletionsLock)
		{
			return _activeAntigravitySetups.Contains(accountId);
		}
	}

	private bool TryBeginAntigravitySetup(Guid accountId)
	{
		lock (_activeConnectionCompletionsLock)
		{
			return !_isConnectionShutdownStarted &&
				_activeAntigravitySetups.Add(accountId);
		}
	}

	private void EndAntigravitySetup(Guid accountId)
	{
		lock (_activeConnectionCompletionsLock)
		{
			_activeAntigravitySetups.Remove(accountId);
		}
	}

	private TaskCompletionSource? TryRegisterActiveAccountConnection(
		Guid accountId,
		out CancellationTokenSource? cancellationSource)
	{
		lock (_activeConnectionCompletionsLock)
		{
			if (_isConnectionShutdownStarted ||
				_activeAccountConnectionCancellationSources.ContainsKey(accountId))
			{
				cancellationSource = null;
				return null;
			}

			cancellationSource = new CancellationTokenSource();
			TaskCompletionSource completion = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			_activeAccountConnectionCancellationSources.Add(
				accountId,
				cancellationSource);
			_activeConnectionCompletions.Add(completion);
			return completion;
		}
	}

	private void CompleteActiveConnection(TaskCompletionSource completion)
	{
		lock (_activeConnectionCompletionsLock)
		{
			completion.TrySetResult();
			_activeConnectionCompletions.Remove(completion);
		}
	}

	private void CompleteActiveAccountConnection(
		Guid accountId,
		CancellationTokenSource cancellationSource,
		TaskCompletionSource completion)
	{
		lock (_activeConnectionCompletionsLock)
		{
			if (_activeAccountConnectionCancellationSources.TryGetValue(
					accountId,
					out CancellationTokenSource? registeredSource) &&
				ReferenceEquals(registeredSource, cancellationSource))
			{
				_activeAccountConnectionCancellationSources.Remove(accountId);
			}

			_accountConnectionsWithCommitStarted.Remove(accountId);

			completion.TrySetResult();
			_activeConnectionCompletions.Remove(completion);
		}

		cancellationSource.Dispose();
	}

	private void CancelProviderAccountChangeIfCancellable(
		Guid accountId,
		CancellationTokenSource cancellationSource,
		AccountUsageViewModel account)
	{
		bool canCancel;

		lock (_activeConnectionCompletionsLock)
		{
			canCancel =
				_activeAccountConnectionCancellationSources.TryGetValue(
					accountId,
					out CancellationTokenSource? registeredSource) &&
				ReferenceEquals(registeredSource, cancellationSource) &&
				!_accountConnectionsWithCommitStarted.Contains(accountId);
		}

		if (canCancel)
		{
			account.CancelProviderAccountChange();
		}
	}

	private bool TryBeginAccountConnectionCommitAfterExternalSuccess(
		Guid accountId,
		CancellationTokenSource cancellationSource,
		AccountUsageViewModel account,
		string accountIdentity)
	{
		bool didBeginCommit = false;

		lock (_activeConnectionCompletionsLock)
		{
			if (!_activeAccountConnectionCancellationSources.TryGetValue(
					accountId,
					out CancellationTokenSource? registeredSource) ||
				!ReferenceEquals(registeredSource, cancellationSource) ||
				!_accountConnectionsWithCommitStarted.Add(accountId))
			{
				return false;
			}
		}

		try
		{
			// A successful provider result means the external credentials may
			// already be switched. Reopen local change state if a cancellation
			// callback won just before success, then make the durable commit win.
			if (!account.IsProviderAccountChangeInProgress)
			{
				account.BeginProviderAccountChange();
			}

			if (!account.IsProviderAccountChangeInProgress)
			{
				return false;
			}

			account.SeedProviderAccountIdentity(accountIdentity);
			didBeginCommit = account.TryBeginProviderAccountChangeCommit();
			return didBeginCommit;
		}
		finally
		{
			if (!didBeginCommit)
			{
				lock (_activeConnectionCompletionsLock)
				{
					if (_activeAccountConnectionCancellationSources.TryGetValue(
							accountId,
							out CancellationTokenSource? registeredSource) &&
						ReferenceEquals(registeredSource, cancellationSource))
					{
						_accountConnectionsWithCommitStarted.Remove(accountId);
					}
				}
			}
		}
	}

	private bool TryBeginAccountConnectionCommitAfterExternalSuccess(
		Guid accountId,
		CancellationTokenSource cancellationSource,
		AccountUsageViewModel account)
	{
		bool didBeginCommit = false;

		lock (_activeConnectionCompletionsLock)
		{
			if (!_activeAccountConnectionCancellationSources.TryGetValue(
					accountId,
					out CancellationTokenSource? registeredSource) ||
				!ReferenceEquals(registeredSource, cancellationSource) ||
				!_accountConnectionsWithCommitStarted.Add(accountId))
			{
				return false;
			}
		}

		try
		{
			if (!account.IsProviderAccountChangeInProgress)
			{
				account.BeginProviderAccountChange();
			}

			if (!account.IsProviderAccountChangeInProgress)
			{
				return false;
			}

			didBeginCommit = account.TryBeginProviderAccountChangeCommit();
			return didBeginCommit;
		}
		finally
		{
			if (!didBeginCommit)
			{
				lock (_activeConnectionCompletionsLock)
				{
					if (_activeAccountConnectionCancellationSources.TryGetValue(
							accountId,
							out CancellationTokenSource? registeredSource) &&
						ReferenceEquals(registeredSource, cancellationSource))
					{
						_accountConnectionsWithCommitStarted.Remove(accountId);
					}
				}
			}
		}
	}

	private async Task<IReadOnlyDictionary<Guid, ClaudeSubscriptionContext>>
		RevalidatePotentialClaudeLegacyOwnersAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		ClaudeSubscriptionContext subscriptionContext,
		CancellationToken cancellationToken)
	{
		Dictionary<Guid, ClaudeSubscriptionContext> observations = new();

		foreach (AccountUsageViewModel candidate in viewModel.Accounts.Where(
			candidate => candidate.IsClaude && (candidate.Id != account.Id)))
		{
			if (ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
					candidate.Profile.ProviderAccountIdentity,
					out _))
			{
				continue;
			}

			string? legacyIdentity =
				candidate.Profile.ProviderAccountIdentity;
			bool hasLegacyOwnershipEvidence =
				!string.IsNullOrWhiteSpace(legacyIdentity);

			if (!ProviderAccountIdentityRules.Comparer.Equals(
					legacyIdentity,
					subscriptionContext.AccountIdentity))
			{
				UsageSnapshot? legacySnapshot =
					await viewModel.LoadClaudeLegacyCachedSnapshotAsync(
						candidate,
						cancellationToken);
				hasLegacyOwnershipEvidence |= legacySnapshot is not null;
				legacyIdentity = legacySnapshot?.ProviderAccountIdentity ??
					legacyIdentity;
			}

			if (!hasLegacyOwnershipEvidence ||
				(!string.IsNullOrWhiteSpace(legacyIdentity) &&
				!ProviderAccountIdentityRules.Comparer.Equals(
					legacyIdentity,
					subscriptionContext.AccountIdentity)))
			{
				continue;
			}

			if (_claudeSubscriptionContextProbe is null)
			{
				throw new ClaudeAccountLoginException(
						"另一張舊版 Claude 卡片可能屬於相同登入帳號，但目前無法安全重新確認它的訂閱範圍。");
			}

			try
			{
				ClaudeSubscriptionObservation observation =
					await _claudeSubscriptionContextProbe.ProbeAsync(
						candidate.Id,
						cancellationToken);
				if (observation.Context.VerificationState !=
					SubscriptionVerificationState.Verified)
				{
					throw new InvalidDataException(
						"Claude legacy subscription context probe 未回傳已驗證狀態。");
				}

				observations.Add(candidate.Id, observation.Context);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				ReportDiagnostic(
					"claude-account-connection",
					$"stage=legacy-owner-revalidation;account={candidate.Id:N};result=failed",
					exception);
				throw new ClaudeAccountLoginException(
					"另一張舊版 Claude 卡片可能屬於相同登入帳號，但暫時無法確認它的訂閱範圍。請稍後再試。");
			}
		}

		return observations;
	}

	private async Task<AuthenticatedProviderBindingCommitResult>
		TryPersistClaudeSubscriptionBindingAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		ClaudeAccountBinding candidateBinding,
		ClaudeSubscriptionContext loginSubscriptionContext,
		UsageSnapshot? retainedLegacySnapshot,
		bool didConfirmLegacyContext,
		CancellationToken cancellationToken)
	{
		if (_claudeAccountBindingStore is null)
		{
			return AuthenticatedProviderBindingCommitResult.InvalidState;
		}

		using IDisposable lease = await _claudeBindingCommitGate.EnterAsync(
			cancellationToken);
		ClaudeSubscriptionContext subscriptionContext;

		try
		{
			subscriptionContext = await ProbeCurrentClaudeSubscriptionContextAsync(
				account.Id,
				cancellationToken);
			if (!Equals(
					loginSubscriptionContext.ProviderDefinedEntitlementKey,
					subscriptionContext.ProviderDefinedEntitlementKey) ||
				(didConfirmLegacyContext &&
					!Equals(loginSubscriptionContext, subscriptionContext)))
			{
				throw new ClaudeAccountLoginException(
					"Claude 登入狀態在確認期間已改變；這次未儲存訂閱範圍，請重新連接並確認。");
			}

			candidateBinding = ClaudeAccountBinding.Create(
				account.Id,
				candidateBinding.PublicBindingId,
				subscriptionContext);
			IReadOnlyDictionary<Guid, ClaudeSubscriptionContext>
				revalidatedLegacyContexts =
					await RevalidatePotentialClaudeLegacyOwnersAsync(
						account,
						viewModel,
						subscriptionContext,
						cancellationToken);
			IReadOnlyList<ClaudeAccountBinding> bindings =
				await _claudeAccountBindingStore.LoadAllAsync(cancellationToken);
			IReadOnlyList<ClaudeAccountBinding> activeBindings =
				await DeleteUnpairedClaudeBindingsAsync(
					viewModel,
					bindings,
					cancellationToken);

			if (activeBindings.Any(binding =>
					(binding.AccountId != account.Id) &&
					binding.MatchesEntitlement(subscriptionContext)))
			{
				return await FailClosedClaudeSubscriptionConflictAsync(
					account,
					viewModel,
					cancellationToken);
			}

			foreach (AccountUsageViewModel candidate in viewModel.Accounts.Where(
				candidate => candidate.IsClaude && (candidate.Id != account.Id)))
			{
				bool hasPrivateBinding = activeBindings.Any(binding =>
					binding.AccountId == candidate.Id);

				if (hasPrivateBinding)
				{
					continue;
				}

				if (ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
						candidate.Profile.ProviderAccountIdentity,
						out _))
				{
					return await FailClosedClaudeSubscriptionConflictAsync(
						account,
						viewModel,
						cancellationToken);
				}

				bool hasRevalidatedLegacyContext =
					revalidatedLegacyContexts.TryGetValue(
						candidate.Id,
						out ClaudeSubscriptionContext? legacyContext);
				bool hasMatchingLegacyAccountIdentity =
					ProviderAccountIdentityRules.Comparer.Equals(
						candidate.Profile.ProviderAccountIdentity,
						subscriptionContext.AccountIdentity);

				if ((hasRevalidatedLegacyContext &&
					Equals(
						legacyContext!.ProviderDefinedEntitlementKey,
						subscriptionContext.ProviderDefinedEntitlementKey)) ||
					(hasMatchingLegacyAccountIdentity &&
						!hasRevalidatedLegacyContext))
				{
					return await FailClosedClaudeSubscriptionConflictAsync(
						account,
						viewModel,
						cancellationToken);
				}
			}
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (ClaudeAccountLoginException)
		{
			throw;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=entitlement-owner-scan;result=failed",
				exception);
			return AuthenticatedProviderBindingCommitResult.PersistenceFailed;
		}

		ClaudeAccountBinding? previousBinding;

		try
		{
			previousBinding = await _claudeAccountBindingStore.LoadAsync(
				account.Id,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=private-binding-load;result=failed",
				exception);
			return AuthenticatedProviderBindingCommitResult.PersistenceFailed;
		}

		try
		{
			await _claudeAccountBindingStore.SaveAsync(
				candidateBinding,
				cancellationToken);
			ClaudeAccountBinding? persistedBinding =
				await _claudeAccountBindingStore.LoadAsync(
					account.Id,
					cancellationToken);
			if (!Equals(candidateBinding, persistedBinding))
			{
				throw new IOException(
					"Claude private binding 未通過持久化 read-back 驗證。");
			}
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=private-binding-save;result=failed",
				exception);
			if (await TryRollbackClaudeAccountBindingAsync(
					account.Id,
					previousBinding))
			{
				return AuthenticatedProviderBindingCommitResult.PersistenceFailed;
			}

			await FailClosedClaudeSubscriptionConflictAsync(
				account,
				viewModel,
				CancellationToken.None);
			return AuthenticatedProviderBindingCommitResult
				.PrivateBindingRollbackFailed;
		}

		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				candidateBinding.PublicBindingId);
		AuthenticatedProviderBindingCommitResult result =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				publicBindingIdentity,
				cancellationToken,
				retainedLegacySnapshot,
				subscriptionContext,
				isAccountMutationGateHeld: true);

		if (result == AuthenticatedProviderBindingCommitResult.Succeeded)
		{
			return result;
		}

		if (await TryRollbackClaudeAccountBindingAsync(
				account.Id,
				previousBinding))
		{
			return result;
		}

		await FailClosedClaudeSubscriptionConflictAsync(
			account,
			viewModel,
			CancellationToken.None);
		return AuthenticatedProviderBindingCommitResult
			.PrivateBindingRollbackFailed;
	}

	private async Task<IReadOnlyList<ClaudeAccountBinding>>
		DeleteUnpairedClaudeBindingsAsync(
		DashboardViewModel viewModel,
		IReadOnlyList<ClaudeAccountBinding> bindings,
		CancellationToken cancellationToken)
	{
		List<ClaudeAccountBinding> activeBindings = new(bindings.Count);

		foreach (ClaudeAccountBinding binding in bindings)
		{
			if (viewModel.Accounts.Any(account =>
					binding.MatchesPublicProfile(account.Profile)))
			{
				activeBindings.Add(binding);
				continue;
			}

			await _claudeAccountBindingStore!.DeleteAsync(
				binding.AccountId,
				cancellationToken);
			ClaudeAccountBinding? deletedBinding =
				await _claudeAccountBindingStore.LoadAsync(
					binding.AccountId,
					cancellationToken);
			if (deletedBinding is not null)
			{
				throw new IOException(
					"未配對的 Claude private binding 清理未通過 read-back 驗證。");
			}
		}

		return activeBindings;
	}

	private async Task<bool> TryRollbackClaudeAccountBindingAsync(
		Guid accountId,
		ClaudeAccountBinding? previousBinding)
	{
		try
		{
			if (previousBinding is null)
			{
				await _claudeAccountBindingStore!.DeleteAsync(
					accountId,
					CancellationToken.None);
				ClaudeAccountBinding? deletedBinding =
					await _claudeAccountBindingStore.LoadAsync(
						accountId,
						CancellationToken.None);
				if (deletedBinding is not null)
				{
					throw new IOException(
						"Claude private binding rollback delete 未通過 read-back 驗證。");
				}

				return true;
			}

			await _claudeAccountBindingStore!.SaveAsync(
				previousBinding,
				CancellationToken.None);
			ClaudeAccountBinding? restoredBinding =
				await _claudeAccountBindingStore.LoadAsync(
					accountId,
					CancellationToken.None);
			if (!Equals(previousBinding, restoredBinding))
			{
				throw new IOException(
					"Claude private binding rollback restore 未通過 read-back 驗證。");
			}

			return true;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				"stage=private-binding-rollback;result=failed",
				exception);
		}

		if (previousBinding is not null)
		{
			try
			{
				await _claudeAccountBindingStore!.DeleteAsync(
					accountId,
					CancellationToken.None);
				ClaudeAccountBinding? deletedBinding =
					await _claudeAccountBindingStore.LoadAsync(
						accountId,
						CancellationToken.None);
				if (deletedBinding is not null)
				{
					throw new IOException(
						"Claude private binding fallback delete 未通過 read-back 驗證。");
				}
			}
			catch (Exception exception)
			{
				ReportDiagnostic(
					"claude-account-connection",
					"stage=private-binding-rollback-delete;result=failed",
					exception);
			}
		}

		return false;
	}

	private async Task<ClaudeSubscriptionContext>
		ProbeCurrentClaudeSubscriptionContextAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		try
		{
			ClaudeSubscriptionObservation observation =
				await _claudeSubscriptionContextProbe!.ProbeAsync(
					accountId,
					cancellationToken);
			if (observation.Context.VerificationState !=
				SubscriptionVerificationState.Verified)
			{
				throw new InvalidDataException(
					"Claude subscription context probe 未回傳已驗證狀態。");
			}

			return observation.Context;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"claude-account-connection",
				$"stage=target-context-revalidation;account={accountId:N};result=failed",
				exception);
			throw new ClaudeAccountLoginException(
				"Claude 登入完成後暫時無法再次確認訂閱範圍；這次未儲存連接，請稍後再試。",
				exception);
		}
	}

	private async Task<AuthenticatedProviderBindingCommitResult>
		FailClosedClaudeSubscriptionConflictAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		CancellationToken cancellationToken)
	{
		bool wasProfileDisconnectPersisted =
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account,
				cancellationToken,
				isAccountMutationGateHeld: true);
		bool wasPrivateBindingDeleted = true;

		try
		{
			await _claudeAccountBindingStore!.DeleteAsync(
				account.Id,
				CancellationToken.None);
			ClaudeAccountBinding? deletedBinding =
				await _claudeAccountBindingStore.LoadAsync(
					account.Id,
					CancellationToken.None);
			if (deletedBinding is not null)
			{
				throw new IOException(
					"Claude private binding conflict cleanup 未通過 read-back 驗證。");
			}
		}
		catch (Exception exception)
		{
			wasPrivateBindingDeleted = false;
			ReportDiagnostic(
				"claude-account-connection",
				"stage=identity-conflict-cleanup;result=binding-delete-failed",
				exception);
		}

		return wasProfileDisconnectPersisted && wasPrivateBindingDeleted
			? AuthenticatedProviderBindingCommitResult.IdentityConflict
			: AuthenticatedProviderBindingCommitResult
				.IdentityConflictRuntimeFailClosed;
	}

	private async Task DeleteOrphanGrokBindingsAsync(
		DashboardViewModel viewModel,
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (_grokAccountBindingStore is null)
		{
			throw new InvalidOperationException(
				"Grok account binding store is unavailable.");
		}

		IReadOnlyList<GrokAccountBinding> bindings =
			await _grokAccountBindingStore.LoadAllAsync(cancellationToken);
		foreach (GrokAccountBinding binding in bindings.Where(
			binding => binding.AccountId != accountId))
		{
			AccountUsageViewModel? candidate = viewModel.Accounts.FirstOrDefault(
				candidate =>
					candidate.IsGrok &&
					(candidate.Id == binding.AccountId));
			if (candidate is null)
			{
				await _grokAccountBindingStore.DeleteAsync(
					binding.AccountId,
					cancellationToken);
				GrokAccountBinding? deletedBinding =
					await _grokAccountBindingStore.LoadAsync(
						binding.AccountId,
						cancellationToken);
				if (deletedBinding is not null)
				{
					throw new IOException(
						"Grok orphan private binding 清理未通過 read-back 驗證。");
				}
			}
		}
	}

	private async Task<bool> IsGrokPrincipalBoundToAnotherAccountAsync(
		DashboardViewModel viewModel,
		Guid accountId,
		GrokPrincipal principal,
		CancellationToken cancellationToken)
	{
		if (_grokAccountBindingStore is null)
		{
			throw new InvalidOperationException(
				"Grok account binding store is unavailable.");
		}

		foreach (AccountUsageViewModel candidate in viewModel.Accounts.Where(
			candidate => candidate.IsGrok && (candidate.Id != accountId)))
		{
			GrokAccountBinding? binding = await _grokAccountBindingStore.LoadAsync(
				candidate.Id,
				cancellationToken);
			if ((binding is not null) &&
				binding.MatchesPrincipal(
					principal.PrincipalType,
					principal.PrincipalId))
			{
				return true;
			}
		}

		return false;
	}

	private async Task<GrokConflictReconciliationResult>
		TryReconcileConflictingGrokConnectionAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Guid attemptId,
		bool isAccountMutationGateHeld)
	{
		bool wasProfileDisconnectPersisted =
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account,
				CancellationToken.None,
				isAccountMutationGateHeld);

		GrokConflictCleanupResult cleanupResult =
			await TryRemoveConflictingGrokBindingAndJournalAsync(
			account.Id,
			attemptId);
		return new GrokConflictReconciliationResult(
			wasProfileDisconnectPersisted,
			cleanupResult);
	}

	private async Task<GrokConflictCleanupResult>
		TryRemoveConflictingGrokBindingAndJournalAsync(
		Guid accountId,
		Guid attemptId)
	{
		if ((_grokAccountBindingStore is null) ||
			(_grokConnectionPendingStore is null))
		{
			return GrokConflictCleanupResult.PendingRetryable;
		}

		GrokAccountBinding? previousBinding;
		try
		{
			previousBinding = await _grokAccountBindingStore.LoadAsync(
				accountId,
				CancellationToken.None);
			await _grokAccountBindingStore.DeleteAsync(
				accountId,
				CancellationToken.None);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=identity-conflict-cleanup;result=binding-delete-failed",
				exception);
			return GrokConflictCleanupResult.PendingRetryable;
		}

		try
		{
			bool wasJournalRemoved =
				await _grokConnectionPendingStore.TryRemoveAsync(
				accountId,
				attemptId,
				CancellationToken.None);
			if (wasJournalRemoved)
			{
				return GrokConflictCleanupResult.Succeeded;
			}
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=identity-conflict-cleanup;result=journal-remove-failed",
				exception);
			// Restore the binding below so a retained BindingPersisted journal
			// remains correlated and startup recovery can retry safely.
		}

		if ((previousBinding is null) ||
			(previousBinding.PublicBindingId != attemptId))
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=identity-conflict-cleanup;result=journal-remove-pending");
			return GrokConflictCleanupResult.PendingRetryable;
		}

		try
		{
			await _grokAccountBindingStore.SaveAsync(
				previousBinding,
				CancellationToken.None);
			ReportDiagnostic(
				"grok-account-connection",
				"stage=identity-conflict-cleanup;result=binding-restored");
			return GrokConflictCleanupResult.PendingRetryable;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"grok-account-connection",
				"stage=identity-conflict-cleanup;result=binding-restore-failed",
				exception);
			return GrokConflictCleanupResult.BindingRestoreFailed;
		}
	}

	private static string GetAuthenticatedProviderBindingCommitFailureMessage(
		AccountUsageViewModel account,
		AuthenticatedProviderBindingCommitResult result,
		string identityConflictMessage)
	{
		return result switch
		{
			AuthenticatedProviderBindingCommitResult.IdentityConflict =>
				identityConflictMessage,
			AuthenticatedProviderBindingCommitResult
				.IdentityConflictRuntimeFailClosed =>
				GetAuthenticatedProviderConflictRuntimeFailClosedMessage(account),
			AuthenticatedProviderBindingCommitResult
				.PrivateBindingRollbackFailed =>
				GetClaudePrivateBindingRollbackFailureMessage(account),
			_ when account.IsCodex =>
				GetCodexBindingPersistenceFailureMessage(account),
			_ => GetAuthenticatedProviderBindingPersistenceFailureMessage(account)
		};
	}

	private static string GetCodexBindingPersistenceFailureMessage(
		AccountUsageViewModel account)
	{
		return $"「{account.AccountName}」的 Codex 登入已完成，但 AI Usage 無法儲存 Codex 帳號連接資料。這次已停止檢查這張卡片，也不會顯示上次用量；請確認 AI Usage 設定資料夾可寫入，再重新連接。";
	}

	private static string GetClaudePrivateBindingRollbackFailureMessage(
		AccountUsageViewModel account)
	{
		return $"「{account.AccountName}」的 Claude 登入未完成，而且 AI Usage 無法安全還原本機訂閱連接資料。這次不會更改連接或顯示新的用量；請確認 AI Usage 設定資料夾可寫入，再重新連接原卡片。";
	}

	private static string GetAuthenticatedProviderConflictRuntimeFailClosedMessage(
		AccountUsageViewModel account)
	{
		string providerName = account.IsClaude
			? "Claude"
			: account.IsCodex
				? "Codex"
				: account.IsCopilot
					? "Copilot"
					: "Grok";
		return $"「{account.AccountName}」的 {providerName} 登入已完成，但 AI Usage 無法儲存帳號衝突後的狀態。這次已停止檢查這個帳號，也不會顯示上次用量；重新啟動後可能再次顯示原本的連接資料。請確認 AI Usage 設定資料夾可寫入，再重新連接帳號。";
	}

	private static string GetGrokConflictCleanupMessage(
		AccountUsageViewModel account,
		GrokConflictCleanupResult result,
		string conflictReason,
		bool wasProfileDisconnectPersisted)
	{
		if (!wasProfileDisconnectPersisted)
		{
			string cleanupStatus = result switch
			{
				GrokConflictCleanupResult.Succeeded =>
					"Grok 帳號衝突與未完成的連接資料已清除。",
				GrokConflictCleanupResult.BindingRestoreFailed =>
					"也無法還原原本的 Grok 連接資料。AI Usage 已保留處理紀錄；重新啟動後若仍未恢復，請保留設定資料並聯絡維護人員。",
				_ =>
					"Grok 本機清理仍待完成，請重新啟動 AI Usage 後再試。"
			};
			return
				$"{GetAuthenticatedProviderConflictRuntimeFailClosedMessage(account)} {cleanupStatus}";
		}

		return result switch
		{
			GrokConflictCleanupResult.Succeeded =>
				$"「{account.AccountName}」無法連接：{conflictReason}。",
			GrokConflictCleanupResult.BindingRestoreFailed =>
				$"「{account.AccountName}」無法連接：{conflictReason}；也無法還原原本的 Grok 連接資料。AI Usage 已保留處理紀錄；重新啟動後若仍未恢復，請保留設定資料並聯絡維護人員。",
			_ =>
				$"「{account.AccountName}」無法連接：{conflictReason}；本機清理仍待完成，請重新啟動 AI Usage 後再試。"
		};
	}

	private static void LaunchAuthorizationUri(Uri authorizationUri)
	{
		_ = Process.Start(new ProcessStartInfo
		{
			FileName = authorizationUri.AbsoluteUri,
			UseShellExecute = true
		});
	}

	private static bool ConfirmAuthorizationUri(
		Window owner,
		Uri authorizationUri)
	{
		string authorizationOrigin = authorizationUri.GetComponents(
			UriComponents.SchemeAndServer,
			UriFormat.UriEscaped);
		MessageBoxResult result = WpfMessageBox.Show(
			owner,
			"Codex 要求在瀏覽器開啟以下登入網站：\n\n" +
			$"{authorizationOrigin}\n\n" +
			"請確認這是你預期的網站，再選擇「是」。",
			"確認 Codex 登入網站",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question,
			MessageBoxResult.No);
		return result == MessageBoxResult.Yes;
	}

	private static bool ConfirmCopilotAccount(
		Window owner,
		CopilotUsageReport usageReport)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(usageReport);
		CopilotAccountIdentity identity = usageReport.Account ??
			throw new ArgumentException(
				"Copilot 未回傳可驗證的帳號資訊。",
				nameof(usageReport));
		if (!CopilotAccountIdentityRules.TryNormalizeHost(
				identity.Host,
				out string normalizedHost) ||
			string.IsNullOrWhiteSpace(identity.Login))
		{
			throw new ArgumentException(
				"Copilot 未回傳可驗證的帳號資訊。",
				nameof(usageReport));
		}

		MessageBoxResult result = WpfMessageBox.Show(
			owner,
			"即將把這張卡片連接到以下 GitHub 帳號：\n\n" +
			$"@{identity.Login.Trim()}\n{normalizedHost}\n\n" +
			"確認要使用這個帳號嗎？",
			"確認 Copilot 帳號",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question,
			MessageBoxResult.No);
		return result == MessageBoxResult.Yes;
	}

	internal static string
		GetAuthenticatedProviderBindingPersistenceFailureMessage(
			AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);
		string providerName = account.IsClaude
			? "Claude"
			: account.IsCodex
				? "Codex"
				: account.IsCopilot
					? "Copilot"
					: account.IsGrok
						? "Grok"
						: "服務";
		if (account.IsCopilot)
		{
			return $"「{account.AccountName}」的 Copilot 登入已完成，但 AI Usage 無法儲存卡片連接資料。這次 staged credential 未套用，原本的卡片連接不受影響；請確認 AI Usage 設定資料夾可寫入，再重新連接。";
		}

		return $"「{account.AccountName}」的 {providerName} 登入已完成，但多次嘗試後仍無法儲存帳號連接。{providerName} 可能已切換到新帳號，但 AI Usage 仍保留原本的連接資料。請確認 AI Usage 設定資料夾可寫入，再重新連接。";
	}

	private static string GetAuthenticatedProviderBindingRefreshDeferredMessage(
		AccountUsageViewModel account)
	{
		string providerName = account.IsClaude
			? "Claude"
			: account.IsCodex
				? "Codex"
				: account.IsCopilot
					? "Copilot"
					: "Grok";
		return $"「{account.AccountName}」的 {providerName} 帳號已連接；稍後會自動再檢查用量。";
	}

	private static async Task RefreshAuthenticatedProviderUsageAfterBindingAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		CancellationToken cancellationToken)
	{
		for (int attempt = 0; ; attempt++)
		{
			await viewModel.RefreshUsageAfterInvalidationAsync(
				account.Id,
				cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();

			bool shouldRetryTransitionalNotConfigured =
				IsAttachedAndEnabled(viewModel, account) &&
				!string.IsNullOrWhiteSpace(
					account.Profile.ProviderAccountIdentity) &&
				(account.CurrentSnapshot?.Status ==
					SnapshotStatus.NotConfigured) &&
				(account.RecoveryAction == UsageRecoveryAction.ConnectAccount);
			if (!shouldRetryTransitionalNotConfigured ||
				(attempt >= AuthenticatedBindingUsageRetryDelays.Length))
			{
				return;
			}

			await Task.Delay(
				AuthenticatedBindingUsageRetryDelays[attempt],
				cancellationToken);
		}
	}

	private bool TryBeginAntigravitySetupCommit(
		AccountUsageViewModel account,
		Action<string>? reportStatus)
	{
		if (!account.TryBeginProviderAccountChangeCommit())
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=official-commit-gate;result=rejected");
			reportStatus?.Invoke(
				"Antigravity 帳號已確認，但暫時無法儲存連接資料。稍後會自動再試。");
			return false;
		}

		return true;
	}

	private async Task CompleteDurableAntigravitySetupAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		Guid setupAttemptId,
		AntigravityMachineSetupSourceKind sourceKind,
		Action<string>? reportStatus,
		CancellationToken cancellationToken)
	{
		ReportDiagnostic(
			"antigravity-account-connection",
			$"stage=durable-persist;source={sourceKind};result=started");
		bool wasPersisted =
			await viewModel.TryPersistAntigravityConnectionAsync(
				account,
				setupAttemptId,
				sourceKind,
				CancellationToken.None);
		ReportDiagnostic(
			"antigravity-account-connection",
			wasPersisted
				? $"stage=durable-persist;source={sourceKind};result=succeeded"
				: $"stage=durable-persist;source={sourceKind};result=failed");

		if (!wasPersisted)
		{
			string persistenceFailure = string.IsNullOrWhiteSpace(
				viewModel.AccountSettingsHealthMessage)
				? "多次嘗試後仍無法更新帳號的連接資料。"
				: viewModel.AccountSettingsHealthMessage;
			reportStatus?.Invoke(
				$"Antigravity 帳號已完成驗證，但連接尚未套用。{persistenceFailure}");
			return;
		}

		account.PrepareAntigravityConnectionRefresh();

		try
		{
			await viewModel.RefreshTargetUsageAfterInvalidationAsync(
				account.Id,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=durable-refresh;result=failed",
				exception);
			// The prepared Retry snapshot below schedules one bounded backoff.
		}

		if (!IsAttachedAndEnabled(viewModel, account))
		{
			ReportDiagnostic(
				"antigravity-account-connection",
				"stage=durable-refresh;result=account-detached");
			return;
		}

		bool hasFreshUsage = HasFreshConfirmedUsage(account);
		if (!hasFreshUsage &&
			(account.RecoveryAction == UsageRecoveryAction.Retry))
		{
			viewModel.ReportRefreshFailure();
		}

		account.CompleteAntigravityConnection();
		string refreshOutcome = hasFreshUsage
			? "ready"
			: account.RecoveryAction == UsageRecoveryAction.Retry
				? "automatic-retry"
				: "manual-recovery";
		ReportDiagnostic(
			"antigravity-account-connection",
			$"stage=durable-complete;source={sourceKind};refresh={refreshOutcome}");
		reportStatus?.Invoke(
			GetAntigravityConnectionCompletionMessage(account, hasFreshUsage));
	}

	private static bool HasFreshConfirmedUsage(AccountUsageViewModel account)
	{
		return account.HasConfirmedProviderAccountBinding &&
			(account.CurrentSnapshot is UsageSnapshot currentSnapshot) &&
			(currentSnapshot.Status == SnapshotStatus.Ready) &&
			string.IsNullOrWhiteSpace(currentSnapshot.Error);
	}

	internal static string GetAntigravityConnectionCompletionMessage(
		AccountUsageViewModel account,
		bool hasFreshUsage)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (hasFreshUsage)
		{
			return "Antigravity 帳號已連接，並已重新讀取用量。";
		}

		return account.RecoveryAction switch
		{
			UsageRecoveryAction.Retry =>
				"Antigravity 帳號已連接；目前暫時無法讀取用量，稍後會自動再試。",
			UsageRecoveryAction.RevalidateUsage =>
				"Antigravity 帳號已連接；上次用量檢查未完成。請按卡片上的「重新檢查 Antigravity 用量」。",
			UsageRecoveryAction.UpdateApplication =>
				"Antigravity 帳號已連接；AI Usage 尚未支援目前的用量格式。請更新 AI Usage。既有連接通常可沿用；若卡片後續要求，請重新確認連接。",
			UsageRecoveryAction.InstallOrUpdate =>
				"Antigravity 帳號已連接；找不到支援的 Antigravity CLI。請確認安裝與版本。既有連接通常可沿用；若卡片後續要求，請重新確認連接。",
			UsageRecoveryAction.ReconfigureUsageSource =>
				"Antigravity 帳號已連接；需要重新確認用量讀取。請依卡片提示操作，既有登入不受影響。",
			UsageRecoveryAction.ConnectAccount or
				UsageRecoveryAction.SwitchAccount =>
				"Antigravity 帳號連接尚未完成；請依卡片提示確認帳號。",
			_ =>
				"Antigravity 帳號已連接；目前沒有可顯示的新用量，請查看卡片狀態。"
		};
	}

	private void ReportDiagnostic(
		string operation,
		string summary,
		Exception? exception = null)
	{
		try
		{
			_reportDiagnostic(operation, summary, exception);
		}
		catch
		{
			// Diagnostics must never alter an account connection outcome.
		}
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetClaudeLoginFailureNotice(ClaudeAccountLoginException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return (
			BuildTypedLoginFailureMessage(exception.Message),
			"Claude 登入失敗",
			MessageBoxImage.Warning);
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetCodexLoginFailureNotice(CodexAccountLoginException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return (
			BuildTypedLoginFailureMessage(exception.Message),
			"Codex 登入失敗",
			MessageBoxImage.Warning);
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetCopilotLoginFailureNotice(CopilotClientException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return exception.Kind switch
		{
			CopilotFailureKind.AuthenticationRequired => (
				"Copilot 尚未登入，請連接帳號後再試。",
				"Copilot 尚未登入",
				MessageBoxImage.Information),
			CopilotFailureKind.AccountMismatch => (
				"登入的 GitHub 帳號與這張 Copilot 卡片不同。這次結果未儲存，請確認後再試。",
				"Copilot 帳號不同",
				MessageBoxImage.Warning),
			CopilotFailureKind.PermissionDenied => (
				"GitHub 已登入，但此帳號目前不允許讀取 Copilot quota。請確認方案或改用其他帳號。",
				"Copilot 權限不足",
				MessageBoxImage.Warning),
			CopilotFailureKind.RateLimited => (
				"GitHub 暫時限制 Copilot quota 查詢，請稍後再試。",
				"GitHub 暫時限制請求",
				MessageBoxImage.Warning),
			CopilotFailureKind.RuntimeUnavailable => (
				"Copilot 元件無法啟動，請更新或修復 AI Usage。",
				"Copilot 無法使用",
				MessageBoxImage.Warning),
			CopilotFailureKind.InvalidResponse => (
				"無法辨識 Copilot quota 資料，請更新 AI Usage 後再試。",
				"Copilot 用量無法讀取",
				MessageBoxImage.Warning),
			_ => (
				"暫時無法讀取 Copilot quota，原本的卡片連接不受影響。",
				"Copilot 連接失敗",
				MessageBoxImage.Warning)
		};
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetCopilotLoginFailureNotice(CopilotAccountLoginException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return (
			$"{exception.Message}\n\n原本的卡片連接不受影響。",
			"Copilot 登入失敗",
			MessageBoxImage.Warning);
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetGrokLoginFailureNotice(GrokAccountLoginException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return (
			BuildTypedLoginFailureMessage(exception.Message),
			"Grok 登入失敗",
			MessageBoxImage.Warning);
	}

	internal static (string Message, string Caption, MessageBoxImage Image)
		GetClaudeLoginCompletionNotice(AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		switch (account.RecoveryAction)
		{
			case UsageRecoveryAction.RestartApplication:
				return (
					"Claude 帳號已連接，但用量檢查已暫停。請重新啟動 AI Usage 後再試。",
					"Claude 用量檢查已暫停",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.RevalidateUsage:
				return (
					"Claude 帳號已連接，但上次用量檢查未完成。請按卡片上的「重新檢查 Claude 用量」。",
					"Claude 用量檢查已暫停",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.InstallOrUpdate:
				return (
					"Claude 登入已完成，但目前找不到可用的 Claude Code CLI；請先安裝或更新 Claude Code，再重新檢查。",
					"Claude Code CLI 需要更新",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.ConfirmSubscription:
				return (
					"Claude 登入已完成，但訂閱確認尚未完成。請使用原本的帳號，確認組織與訂閱方案。",
					"需要確認 Claude 訂閱",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.ConnectAccount:
				return (
					"Claude 登入已完成，但多次重新檢查後仍無法確認可用的訂閱帳號；請確認登入的是支援的 Claude 訂閱帳號，再重新連接。",
					"Claude 狀態待確認",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.SwitchAccount:
				return (
					"Claude 登入已完成，但目前的帳號或登入方式無法讀取用量；請切換至支援的 Claude 訂閱帳號。",
					"Claude 帳號需要切換",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.ReconfigureUsageSource:
				return (
					"Claude 帳號已連接，但需要重新確認用量讀取；帳號不必重新登入。",
					"需要確認 Claude 用量讀取",
					MessageBoxImage.Warning);
			case UsageRecoveryAction.Retry:
			case UsageRecoveryAction.None:
				break;
			default:
				return (
					"Claude 登入已完成，但目前無法繼續檢查用量。請重新啟動 AI Usage 後再試。",
					"Claude 用量待確認",
					MessageBoxImage.Warning);
		}

		if (!account.HasProviderAccountIdentity ||
			(account.CurrentSnapshot is null) ||
			(account.CurrentSnapshot.Status == SnapshotStatus.NotConfigured))
		{
			return (
				"Claude 登入已完成，但尚未能確認最新帳號狀態；稍後會自動再試。",
				"Claude 狀態待確認",
				MessageBoxImage.Warning);
		}

		if (account.CurrentSnapshot.SubscriptionVerificationState ==
			SubscriptionVerificationState.UsageUnavailable)
		{
			return (
				"Claude 訂閱範圍已連接並確認，但這個方案的 /usage 格式尚未經驗證；AI Usage 不會猜測或顯示額度。",
				"Claude 訂閱範圍已確認",
				MessageBoxImage.Warning);
		}

		return account.CurrentSnapshot.Status switch
		{
			SnapshotStatus.Error => (
				"Claude 帳號已連接；目前無法讀取用量，稍後會自動再試。",
				"Claude 登入完成",
				MessageBoxImage.Warning),
			SnapshotStatus.Stale => (
				"Claude 帳號已連接；目前顯示上次確認的用量，稍後將自動更新。",
				"Claude 登入完成",
				MessageBoxImage.Warning),
			_ => (
				"Claude 帳號已連接，並已重新讀取用量。",
				"Claude 登入完成",
				MessageBoxImage.Information)
		};
	}

	internal static string GetCodexLoginCompletionMessage(
		CodexLoginRefreshOutcome outcome)
	{
		return outcome switch
		{
			CodexLoginRefreshOutcome.Ready =>
				"Codex 帳號已連接，並已重新讀取用量。",
			CodexLoginRefreshOutcome.Stale =>
				"Codex 帳號已連接；目前顯示上次確認的用量，稍後將自動更新。",
			CodexLoginRefreshOutcome.Error =>
				"Codex 登入已完成，但目前無法確認帳號用量；稍後會自動再試。",
			CodexLoginRefreshOutcome.NotConfigured =>
				"Codex 登入已完成，但尚未確認可用的 ChatGPT 訂閱帳號；稍後會自動再試。",
			_ =>
				"Codex 登入已完成，但尚未能確認目前登入的帳號；稍後會自動再試。"
		};
	}

	internal static string GetCodexLoginCompletionMessage(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		return account.RecoveryAction switch
		{
			UsageRecoveryAction.ConnectAccount =>
				"Codex 登入已完成，但多次重新檢查後仍無法確認可用的 ChatGPT 訂閱帳號。請依卡片提示重新連接。",
			UsageRecoveryAction.SwitchAccount =>
				"Codex 帳號已連接，但這次用量檢查取得了不同帳號。已忽略結果，請依卡片提示確認帳號。",
			UsageRecoveryAction.InstallOrUpdate =>
				"Codex 帳號已連接，但 Codex CLI 需要安裝或更新；完成後 AI Usage 會繼續檢查。",
			UsageRecoveryAction.ReconfigureUsageSource =>
				"Codex 帳號已連接，但需要重新確認用量讀取；帳號不必重新登入。",
			UsageRecoveryAction.RevalidateUsage =>
				"Codex 帳號已連接，但用量檢查需要重新確認。請依卡片提示處理。",
			_ => GetCodexLoginCompletionMessage(
				GetCodexLoginRefreshOutcome(account))
		};
	}

	internal static string GetCopilotLoginCompletionMessage(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		return account.RecoveryAction switch
		{
			UsageRecoveryAction.ConnectAccount =>
				"Copilot 登入已完成，但尚未確認可讀取 quota 的 GitHub 帳號。請依卡片提示重新連接。",
			UsageRecoveryAction.SwitchAccount =>
				"Copilot 帳號已連接，但這次 quota 檢查取得不同帳號。已忽略結果，請依卡片提示確認帳號。",
			UsageRecoveryAction.InstallOrUpdate =>
				"Copilot 帳號已連接，但元件需要更新或修復；完成後 AI Usage 會繼續檢查。",
			UsageRecoveryAction.Retry =>
				"Copilot 帳號已連接；目前暫時無法讀取 quota，稍後會自動再試。",
			_ when (account.CurrentSnapshot?.Status == SnapshotStatus.Unsupported) =>
				"Copilot 帳號已連接並確認方案，但目前沒有可顯示的 quota。",
			_ when (account.CurrentSnapshot?.Status == SnapshotStatus.Stale) =>
				"Copilot 帳號已連接；目前顯示上次確認的用量，稍後將自動更新。",
			_ when (account.CurrentSnapshot?.Status == SnapshotStatus.Ready) =>
				"Copilot 帳號已連接，並已重新讀取用量。",
			_ =>
				"Copilot 帳號已連接；稍後會自動確認最新用量。"
		};
	}

	internal static string GetGrokLoginCompletionMessage(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		return account.RecoveryAction switch
		{
			UsageRecoveryAction.ConnectAccount =>
				"Grok 登入已完成，但尚未確認可讀取用量的帳號。請依卡片提示重新連接。",
			UsageRecoveryAction.SwitchAccount =>
				"Grok 帳號已連接，但目前無法讀取這個帳號的週用量。請依卡片提示確認或切換帳號。",
			UsageRecoveryAction.InstallOrUpdate =>
				"Grok 帳號已連接，但 Grok Build CLI 需要安裝或更新；完成後 AI Usage 會繼續檢查。",
			UsageRecoveryAction.Retry =>
				"Grok 帳號已連接；目前暫時無法讀取用量，稍後會自動再試。",
			_ when (account.CurrentSnapshot?.Status == SnapshotStatus.Stale) =>
				"Grok 帳號已連接；目前顯示上次確認的用量，稍後將自動更新。",
			_ when (account.CurrentSnapshot?.Status == SnapshotStatus.Ready) =>
				"Grok 帳號已連接，並已重新讀取用量。",
			_ =>
				"Grok 帳號已連接；稍後會自動確認最新用量。"
		};
	}

	private static bool CanStartAntigravitySetup(
		DashboardViewModel viewModel,
		AccountUsageViewModel account)
	{
		return account.IsAntigravity &&
			account.CanConnectProviderAccount &&
			!viewModel.IsManagingAccounts &&
			!viewModel.IsAccountCleanupBlocked(account.Id, account.Provider) &&
			IsAttachedAndEnabled(viewModel, account) &&
			viewModel.IsPrimaryEnabledAntigravityAccount(account.Id);
	}

	private static bool IsAttachedAndEnabled(
		DashboardViewModel viewModel,
		AccountUsageViewModel account)
	{
		return account.IsEnabled && viewModel.Accounts.Contains(account);
	}

	private static string BuildTypedLoginFailureMessage(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return $"{reason}\n\n原本顯示的用量不會被清除；依照上方說明處理後可重新連接。";
	}
}
