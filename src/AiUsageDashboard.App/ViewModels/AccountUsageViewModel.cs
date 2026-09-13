using System.ComponentModel;
using System.Runtime.CompilerServices;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.ViewModels;

public enum AccountStatusKind
{
	Ready,
	Refreshing,
	Stale,
	Error,
	NotConfigured,
	Disabled,
	Unsupported,
	NotConnected
}

public enum AccountStatusSeverity
{
	Neutral,
	Positive,
	Informational,
	Warning,
	Critical
}

public sealed class AccountUsageViewModel : INotifyPropertyChanged
{
	private const string OfficialAntigravityConnectionCommitPendingMessage =
		"Antigravity 帳號已確認；AI Usage 正在儲存連接資料，不需要重新連接。";
	private const string AntigravityConnectionJournalCheckPendingMessage =
		"AI Usage 正在確認上次 Antigravity 連接是否完成；目前不需要重新連接。";
	private const string InlineAutomaticRetryGuidance =
		"AI Usage 稍後會自動再試，不需要操作。";
	private static readonly string[] RemovableAutomaticRetryGuidance =
	[
		InlineAutomaticRetryGuidance,
		"AI Usage 稍後會自動再試，並保留上次成功讀取的資料。",
		"AI Usage 稍後會自動再試，並保留現有帳號與用量資料。",
		"AI Usage 稍後會自動再試。",
		"稍後會自動再試。",
		"稍後會自動重試。",
		"稍後自動再試。",
		"稍後會自動重新檢查；"
	];
	private static readonly UsageMetric[] AntigravityFiveHourRevalidationPlaceholders =
	{
		new(
			"agy.gemini.rolling-5h",
			"Gemini rolling 5h",
			UsedPercent: null,
			DisplayValue: "等待重新檢查"),
		new(
			"agy.claude.rolling-5h",
			"Claude + GPT rolling 5h",
			UsedPercent: null,
			DisplayValue: "等待重新檢查")
	};

	public event PropertyChangedEventHandler? PropertyChanged;

	private AccountProfile _profile;
	private bool _canManage;
	private bool _canMoveDown;
	private bool _canMoveUp;
	private bool _didRejectProviderAccountSnapshot;
	private bool _hasPendingConnectionRefresh;
	private bool _isPrimaryEnabledAntigravityAccount;
	private bool _isProviderAccountChangeInProgress;
	private bool _isProviderAccountChangeCommitInProgress;
	private bool _isOfficialAntigravityConnectionCommitPending;
	private bool _isAntigravityConnectionJournalCheckPending;
	private long _usageSafetyRevalidationOperationId;
	private bool _isProviderAccountChangeFallbackIdentityConfirmed;
	private bool _isUsingProviderAccountChangeFallback;
	private long _lifecycleRevision;
	private string _noticeText = string.Empty;
	private string _primaryText = string.Empty;
	private string? _expectedProviderAccountIdentity;
	private string? _providerAccountChangeFallbackIdentity;
	private string? _providerAccountIdentity;
	private string? _antigravityReportedAccountEmail;
	private bool _isAntigravityReportedAccountEmailStale;
	private UsageSnapshot? _providerAccountChangeFallbackSnapshot;
	private string _secondaryText = string.Empty;
	private AccountStatusKind _statusKind = AccountStatusKind.NotConnected;
	private UsageDisplayMode _usageDisplayMode = UsageDisplayMode.Used;
	private IReadOnlyList<UsageMetricViewModel> _usageMetrics =
		Array.Empty<UsageMetricViewModel>();
	private double _usedPercent;
	private UsageSnapshot? _currentSnapshot;

	public AccountProfile Profile => _profile;

	public Guid Id => Profile.Id;

	public ProviderKind Provider => Profile.Provider;

	public string AccountName => HasAccountNickname
		? AccountNickname
		: HasProviderAccountIdentity
			? $"{ProviderName} · {GetProviderAccountCardDisplayText(ProviderAccountIdentity)}"
			: ProviderName;

	public string AccountNickname => Profile.DisplayName ?? string.Empty;

	public string AccountCardTitle => HasAccountNickname
		? AccountNickname
		: ProviderName;

	public string AccountTitle => HasAccountNickname
		? $"{ProviderName} · {AccountNickname}"
		: ProviderName;

	public string AccountHeaderText => $"{ProviderName}{AccountHeaderSuffixText}";

	public string AccountHeaderSuffixText
	{
		get
		{
			string planTier = SubscriptionPlanDisplayText;
			string planSuffix = string.IsNullOrEmpty(planTier)
				? string.Empty
				: $" · {planTier}";
			return HasAccountNickname
				? $"{planSuffix} · {AccountNickname}"
				: planSuffix;
		}
	}

	public string SubscriptionPlanDisplayText
	{
		get
		{
			if (_isProviderAccountChangeInProgress ||
				(!IsClaude && !IsCodex && !IsCopilot && !IsAntigravity) ||
				(CurrentSnapshot is not UsageSnapshot currentSnapshot) ||
				string.IsNullOrWhiteSpace(currentSnapshot.PlanTier))
			{
				return string.Empty;
			}

			bool canDisplayPlan =
				TryGetPrivateBindingAccountDisplayIdentity(
					ProviderAccountIdentity,
					" · ",
					includeSubscriptionContext: false,
					includePlanTier: false,
					out _) ||
				CanDisplayStandardCodexSubscriptionPlan(currentSnapshot) ||
				CanDisplayCopilotSubscriptionPlan(currentSnapshot) ||
				CanDisplayAntigravitySubscriptionPlan(currentSnapshot);
			if (!canDisplayPlan)
			{
				return string.Empty;
			}

			return currentSnapshot.PlanTier.Trim();
		}
	}

	public string AccountCardDisplayText => GetAccountDisplayText(
		ShowSubscriptionContext ? Environment.NewLine : " · ",
		includeSubscriptionContext: ShowSubscriptionContext,
		includePlanTier: false);

	public string AccountDisplayText => GetAccountDisplayText(
		" · ",
		includeSubscriptionContext: true,
		includePlanTier: true);

	public string AccountAliasText => HasAccountNickname
		? $"暱稱 · {AccountNickname}"
		: string.Empty;

	public string ClaudeAccountActionText
	{
		get
		{
			if (IsProviderAccountChangeCommitInProgress)
			{
				return "正在完成 Claude 帳號連接";
			}

			if (CanCancelProviderAccountConnection)
			{
				return "取消 Claude 帳號連接";
			}

			if (RequiresClaudeQuotaRiskConsent)
			{
				return "確認 Claude 用量讀取";
			}

			if (RecoveryAction == UsageRecoveryAction.ConfirmSubscription)
			{
				return "確認 Claude 訂閱";
			}

			return HasProviderAccountIdentity
				? "切換 Claude 帳號"
				: "連接 Claude 帳號";
		}
	}

	public string CodexAccountActionText =>
		IsProviderAccountChangeCommitInProgress
			? "正在完成 Codex 帳號連接"
			: CanCancelProviderAccountConnection
				? "取消 Codex 帳號連接"
				: HasProviderAccountIdentity
					? "切換 Codex 帳號"
					: "連接 Codex 帳號";

	public string CopilotAccountActionText =>
		IsProviderAccountChangeCommitInProgress
			? "正在完成 Copilot 帳號連接"
			: CanCancelProviderAccountConnection
				? "取消 Copilot 帳號連接"
				: HasProviderAccountIdentity
					? "切換 Copilot 帳號"
					: "連接 Copilot 帳號";

	public string GrokAccountActionText =>
		IsProviderAccountChangeCommitInProgress
			? "正在完成 Grok 帳號連接"
			: CanCancelProviderAccountConnection
				? "取消 Grok 帳號連接"
				: HasProviderAccountIdentity
					? "切換 Grok 帳號"
					: "連接 Grok 帳號";

	public string AntigravityAccountActionText => HasProviderAccountIdentity
		? "重新連接 Antigravity 帳號"
		: "連接 Antigravity 帳號";

	public bool HasAccountAlias =>
		HasAccountNickname &&
		(!HasProviderAccountIdentity ||
			!string.Equals(
			ProviderAccountIdentity,
			AccountNickname,
			StringComparison.OrdinalIgnoreCase));

	public bool HasAccountNickname =>
		!string.IsNullOrWhiteSpace(AccountNickname);

	public bool HasProviderAccountIdentity =>
		!string.IsNullOrWhiteSpace(ProviderAccountIdentity);

	internal string ProviderAccountDisplayText => AccountDisplayText;

	internal string? ProviderAccountOwnershipIdentity =>
		ProviderAccountIdentity ??
		(_isProviderAccountChangeInProgress
			? _providerAccountChangeFallbackIdentity
			: null);

	public string? ProviderAccountIdentity => _providerAccountIdentity;

	public bool IsEnabled => Profile.IsEnabled;

	public bool IsProviderAccountChangeInProgress =>
		_isProviderAccountChangeInProgress;

	public bool IsUsageSafetyRevalidationInProgress =>
		Volatile.Read(ref _usageSafetyRevalidationOperationId) != 0;

	internal bool IsAntigravityReportedAccountEmailStale =>
		_isAntigravityReportedAccountEmailStale;

	public bool IsClaude => Provider == ProviderKind.Claude;

	public bool RequiresClaudeQuotaRiskConsent =>
		IsEnabled &&
		IsClaude &&
		!Profile.HasAcceptedClaudeQuotaRisk;

	public bool IsCodex => Provider == ProviderKind.Codex;

	public bool IsCopilot => Provider == ProviderKind.Copilot;

	public bool IsGrok => Provider == ProviderKind.Grok;

	public bool SupportsSubscriptionContext => IsClaude || IsCodex;

	public bool ShowSubscriptionContext =>
		SupportsSubscriptionContext && Profile.ShowSubscriptionContext;

	public string SubscriptionContextMenuText => Provider switch
	{
		ProviderKind.Claude => "顯示組織",
		ProviderKind.Codex => "顯示 workspace",
		_ => string.Empty
	};

	public string SubscriptionContextToolTip => Provider switch
	{
		ProviderKind.Claude => "在這張卡片顯示組織名稱",
		ProviderKind.Codex => "在這張卡片顯示 workspace 連接資訊",
		_ => string.Empty
	};

	public bool IsAntigravity => Provider == ProviderKind.Antigravity;

	public bool CanConfigureAntigravityAccount =>
		IsAntigravity &&
		IsEnabled &&
		_isPrimaryEnabledAntigravityAccount;

	public UsageSnapshot? CurrentSnapshot
	{
		get => _currentSnapshot;
		private set => SetField(ref _currentSnapshot, value);
	}

	public bool CanManage
	{
		get => _canManage;
		private set
		{
			if (_canManage == value)
			{
				return;
			}

			_canManage = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanChangeAccountSettings));
			OnPropertyChanged(nameof(CanOpenMainAccountMenu));
			OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		}
	}

	public bool CanChangeAccountSettings =>
		CanManage && !IsProviderAccountChangeInProgress;

	public bool CanConnectProviderAccount =>
		IsEnabled &&
		!IsProviderAccountChangeInProgress &&
		!_isOfficialAntigravityConnectionCommitPending &&
		!_isAntigravityConnectionJournalCheckPending &&
		(!IsAntigravity || CanConfigureAntigravityAccount);

	internal bool CanConnectProviderAccountAfterSave =>
		!IsProviderAccountChangeInProgress &&
		!_isOfficialAntigravityConnectionCommitPending &&
		!_isAntigravityConnectionJournalCheckPending &&
		(!IsAntigravity || !IsEnabled || CanConfigureAntigravityAccount);

	public bool CanCancelProviderAccountConnection =>
		IsProviderAccountChangeInProgress &&
		!IsProviderAccountChangeCommitInProgress &&
		(IsClaude || IsCodex || IsCopilot || IsGrok);

	public bool CanInvokeProviderAccountAction =>
		CanConnectProviderAccount || CanCancelProviderAccountConnection;

	internal bool CanQueryUsage =>
		IsEnabled &&
		!RequiresClaudeQuotaRiskConsent &&
		!_isOfficialAntigravityConnectionCommitPending &&
		!_isAntigravityConnectionJournalCheckPending &&
		(!IsAntigravity || HasProviderAccountIdentity);

	public bool CanRefreshUsage =>
		CanQueryUsage &&
		!IsProviderAccountChangeInProgress &&
		!IsUsageSafetyRevalidationInProgress;

	public bool CanOpenMainAccountMenu => CanChangeAccountSettings;

	public bool CanOpenWidgetAccountMenu =>
		CanOpenMainAccountMenu || CanRefreshUsage;

	public UsageRecoveryAction RecoveryAction =>
		CurrentSnapshot?.RecoveryAction ?? UsageRecoveryAction.None;

	public bool HasRecoveryAction =>
		RecoveryAction != UsageRecoveryAction.None;

	public bool ShowRecoveryActionNotice =>
		HasRecoveryAction &&
		!_hasPendingConnectionRefresh &&
		(CurrentSnapshot?.Status != SnapshotStatus.Refreshing) &&
		(!IsAntigravity || (RecoveryAction != UsageRecoveryAction.Retry)) &&
		!HasInlineAutomaticRetryGuidance(CurrentSnapshot);

	public string RecoveryPanelTitle => RecoveryAction switch
	{
		UsageRecoveryAction.ConnectAccount when
			IsClaude && RequiresClaudeQuotaRiskConsent => "需要確認用量讀取",
		UsageRecoveryAction.ConnectAccount => "尚未連接",
		UsageRecoveryAction.SwitchAccount => "需要切換帳號",
		UsageRecoveryAction.ConfirmSubscription => "需要確認 Claude 訂閱",
		UsageRecoveryAction.ReconfigureUsageSource => "需要確認用量讀取",
		UsageRecoveryAction.RestartApplication => "需要重新啟動",
		UsageRecoveryAction.InstallOrUpdate => "需要安裝或更新",
		UsageRecoveryAction.RevalidateUsage => "需要重新檢查",
		UsageRecoveryAction.UpdateApplication => "需要更新 AI Usage",
		UsageRecoveryAction.Retry => "稍後會自動再試",
		UsageRecoveryAction.None => "用量狀態",
		_ => "用量需要確認"
	};

	public bool HasExecutableRecoveryAction => RecoveryAction switch
	{
		UsageRecoveryAction.ConnectAccount =>
			IsClaude || IsCodex || IsCopilot || IsGrok ||
				CanConfigureAntigravityAccount,
		UsageRecoveryAction.SwitchAccount =>
			IsClaude || IsCodex || IsCopilot || IsGrok ||
				CanConfigureAntigravityAccount,
		UsageRecoveryAction.ConfirmSubscription => IsClaude,
		UsageRecoveryAction.ReconfigureUsageSource =>
			!IsAntigravity || CanConfigureAntigravityAccount,
		UsageRecoveryAction.RestartApplication => true,
		UsageRecoveryAction.InstallOrUpdate => true,
		UsageRecoveryAction.RevalidateUsage => IsClaude || IsAntigravity,
		UsageRecoveryAction.UpdateApplication => true,
		_ => false
	};

	public bool CanExecuteRecoveryAction =>
		IsEnabled &&
		!IsProviderAccountChangeInProgress &&
		!IsUsageSafetyRevalidationInProgress &&
		!_isOfficialAntigravityConnectionCommitPending &&
		!_isAntigravityConnectionJournalCheckPending &&
		HasExecutableRecoveryAction;

	public bool CanInvokeRecoveryAction =>
		CanExecuteRecoveryAction ||
		(CanCancelProviderAccountConnection && HasExecutableRecoveryAction);

	public bool ShowClaudeDefaultConnectionAction =>
		IsClaude &&
		((CanCancelProviderAccountConnection &&
			!HasExecutableRecoveryAction) ||
			(!HasRecoveryAction &&
				(RequiresClaudeQuotaRiskConsent ||
					(StatusKind == AccountStatusKind.NotConnected))));

	public bool ShowCodexDefaultConnectionAction =>
		IsCodex &&
		((CanCancelProviderAccountConnection &&
			!HasExecutableRecoveryAction) ||
			(!HasRecoveryAction &&
				((StatusKind == AccountStatusKind.NotConnected) ||
					(StatusKind == AccountStatusKind.NotConfigured) ||
					(StatusKind == AccountStatusKind.Error))));

	public bool ShowCopilotDefaultConnectionAction =>
		IsCopilot &&
		((CanCancelProviderAccountConnection &&
			!HasExecutableRecoveryAction) ||
			(!HasRecoveryAction &&
				((StatusKind == AccountStatusKind.NotConnected) ||
					(StatusKind == AccountStatusKind.NotConfigured) ||
					(StatusKind == AccountStatusKind.Error))));

	public bool ShowGrokDefaultConnectionAction =>
		IsGrok &&
		((CanCancelProviderAccountConnection &&
			!HasExecutableRecoveryAction) ||
			(!HasRecoveryAction &&
				((StatusKind == AccountStatusKind.NotConnected) ||
					(StatusKind == AccountStatusKind.NotConfigured) ||
					(StatusKind == AccountStatusKind.Error))));

	public bool ShowAntigravityDefaultConnectionAction =>
		CanConnectProviderAccount &&
		CanConfigureAntigravityAccount &&
		!HasProviderAccountIdentity &&
		!HasRecoveryAction &&
		((StatusKind == AccountStatusKind.NotConnected) ||
			(StatusKind == AccountStatusKind.NotConfigured) ||
			(StatusKind == AccountStatusKind.Error));

	public string RecoveryActionText =>
		(IsProviderAccountChangeInProgress &&
			(IsClaude || IsCodex || IsCopilot || IsGrok))
			? IsClaude
				? ClaudeAccountActionText
				: IsCodex
					? CodexAccountActionText
					: IsCopilot
						? CopilotAccountActionText
						: GrokAccountActionText
			: RecoveryAction switch
		{
			UsageRecoveryAction.Retry => string.Empty,
			UsageRecoveryAction.ConnectAccount when
				IsClaude && RequiresClaudeQuotaRiskConsent =>
				"確認 Claude 用量讀取",
			UsageRecoveryAction.ConnectAccount => $"連接 {ProviderName} 帳號",
			UsageRecoveryAction.SwitchAccount => $"切換 {ProviderName} 帳號",
			UsageRecoveryAction.ConfirmSubscription => "確認 Claude 訂閱",
			UsageRecoveryAction.ReconfigureUsageSource when IsAntigravity =>
				AntigravityAccountActionText,
			UsageRecoveryAction.ReconfigureUsageSource => "開啟設定說明",
			UsageRecoveryAction.RestartApplication => "重新啟動 AI Usage",
			UsageRecoveryAction.UpdateApplication => "查看 AI Usage 更新方式",
			UsageRecoveryAction.InstallOrUpdate when IsAntigravity =>
				"查看 Antigravity 處理方式",
			UsageRecoveryAction.InstallOrUpdate when IsGrok =>
				"查看 Grok Build CLI 處理方式",
			UsageRecoveryAction.InstallOrUpdate when IsCopilot =>
				"查看 Copilot 處理方式",
			UsageRecoveryAction.InstallOrUpdate =>
				$"查看 {ProviderName} CLI 處理方式",
			UsageRecoveryAction.RevalidateUsage when IsAntigravity =>
				"重新檢查 Antigravity 用量",
			UsageRecoveryAction.RevalidateUsage => "重新檢查 Claude 用量",
			_ => string.Empty
		};

	public string RecoveryActionDescription => RecoveryAction switch
	{
		UsageRecoveryAction.Retry =>
			CreateAutomaticRetryDescription(CurrentSnapshot),
		UsageRecoveryAction.ConnectAccount when
			IsClaude && RequiresClaudeQuotaRiskConsent =>
			"Claude /usage 查詢狀態時也可能計入少量用量。確認一次後會自動更新；需要登入時會開啟 Claude 官方登入。",
		UsageRecoveryAction.ConnectAccount =>
			$"尚未連接 {ProviderName} 帳號。連接後即可讀取用量。",
		UsageRecoveryAction.SwitchAccount =>
			$"目前的 {ProviderName} 帳號或登入方式無法讀取用量，請切換帳號。",
		UsageRecoveryAction.ConfirmSubscription =>
			"請使用這張卡片原本的 Claude 帳號，確認組織與訂閱方案。完成後即可重新檢查用量。",
		UsageRecoveryAction.ReconfigureUsageSource when
			IsAntigravity && HasProviderAccountIdentity =>
			"AI Usage 會重新確認目前的 Antigravity 帳號與用量讀取；既有 Antigravity 登入不受影響。",
		UsageRecoveryAction.ReconfigureUsageSource when IsAntigravity =>
			"完成 Antigravity 帳號連接後，AI Usage 會自動更新用量。",
		UsageRecoveryAction.ReconfigureUsageSource =>
			"請依卡片提示重新確認用量讀取，再檢查一次。帳號不必重新登入。",
		UsageRecoveryAction.RestartApplication =>
			"用量檢查已暫停，以避免重複檢查。重新啟動 AI Usage 後才會再試。",
		UsageRecoveryAction.UpdateApplication when IsAntigravity =>
			"目前的 AI Usage 尚未支援這個 Antigravity 用量格式。更新 AI Usage 後會自動重新檢查。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。",
		UsageRecoveryAction.UpdateApplication =>
			"目前的 AI Usage 尚未支援這個用量格式。更新後會自動重新檢查；不必重新連接帳號。",
		UsageRecoveryAction.InstallOrUpdate when IsAntigravity =>
			"找不到可用的 Antigravity CLI，或目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請確認 Antigravity 已安裝並更新 Antigravity 或 AI Usage。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。",
		UsageRecoveryAction.InstallOrUpdate when IsGrok =>
			"目前找不到可用的 Grok Build CLI，或版本不支援用量檢查。請從 xAI 官方來源安裝或更新；完成後不必重新連接帳號。",
		UsageRecoveryAction.InstallOrUpdate when IsCopilot =>
			"本機 GitHub Copilot CLI 目前無法使用。請安裝或更新官方 CLI；完成後不必重新連接帳號。",
		UsageRecoveryAction.InstallOrUpdate =>
			$"目前的 {ProviderName} CLI 無法使用。安裝、更新或修復後不必重新連接帳號。",
		UsageRecoveryAction.RevalidateUsage when IsAntigravity =>
			"上次 Antigravity 用量檢查未完成。請再檢查一次；這不會重新登入 Antigravity。",
		UsageRecoveryAction.RevalidateUsage =>
			"上次 Claude 用量檢查未完成。按下後只會重新檢查這個帳號一次，不會重新啟動 AI Usage 或登入 Claude。",
		_ => string.Empty
	};

	public bool CanMoveDown
	{
		get => _canMoveDown;
		private set => SetField(ref _canMoveDown, value);
	}

	public bool CanMoveUp
	{
		get => _canMoveUp;
		private set => SetField(ref _canMoveUp, value);
	}

	public string ProviderName => Provider switch
	{
		ProviderKind.Claude => "Claude",
		ProviderKind.Codex => "Codex",
		ProviderKind.Copilot => "GitHub Copilot",
		ProviderKind.Antigravity => "Antigravity",
		ProviderKind.Grok => "Grok",
		_ => Provider.ToString()
	};

	public string AccountDeletionConfirmationText
	{
		get
		{
			string identity = HasProviderAccountIdentity
				? GetProviderAccountIdentityDisplayText(
					ProviderAccountIdentity,
					" · ")
				: "尚未確認";
			string retainedDataMessage = Provider switch
			{
				ProviderKind.Claude =>
					"不會登出 Claude Code，也不會刪除 Claude 的登入憑證。",
				ProviderKind.Codex =>
					"不會登出 Codex CLI，也不會刪除 Codex 的登入憑證。",
				ProviderKind.Copilot =>
					"會刪除 AI Usage 為這張卡片儲存在 Windows Credential Manager 的 GitHub credential；其他 Copilot 卡片不受影響。",
				ProviderKind.Antigravity =>
					"不會登出 Antigravity，也不會刪除登入資料。若移除後已沒有會檢查用量的 Antigravity 帳號，AI Usage 也會移除自己建立的用量顯示設定；其他 Antigravity 設定不受影響。",
				ProviderKind.Grok =>
					"會刪除 AI Usage 為這個帳號另外儲存的 Grok Build CLI 登入資料；不會影響 Grok Build CLI 預設的登入狀態。",
				_ =>
					"不會登出服務，也不會刪除服務的登入憑證。"
			};

			return
				$"確定要從 AI Usage 移除這個帳號嗎？\n\n" +
				(HasAccountNickname
					? $"暱稱：{AccountNickname}\n"
					: string.Empty) +
				$"服務：{ProviderName}\n" +
				$"帳號：{identity}\n\n" +
				"會從 AI Usage 移除這個帳號，並嘗試清除上次用量。\n" +
				retainedDataMessage;
		}
	}

	public string NoticeText
	{
		get => _noticeText;
		private set
		{
			if (_noticeText == value)
			{
				return;
			}

			_noticeText = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasNotice));
		}
	}

	public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeText);

	public string PrimaryText
	{
		get => _primaryText;
		private set => SetField(ref _primaryText, value);
	}

	public string SecondaryText
	{
		get => _secondaryText;
		private set => SetField(ref _secondaryText, value);
	}

	public AccountStatusKind StatusKind
	{
		get => _statusKind;
		private set
		{
			if (_statusKind == value)
			{
				return;
			}

			_statusKind = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(StatusSeverity));
			OnPropertyChanged(nameof(StatusText));
			OnPropertyChanged(nameof(DisplayStatusSeverity));
			OnPropertyChanged(nameof(DisplayStatusText));
			OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
			OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
			OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
			OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
			OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
		}
	}

	public AccountStatusSeverity DisplayStatusSeverity =>
		IsProviderAccountChangeInProgress
			? AccountStatusSeverity.Informational
			: RequiresClaudeQuotaRiskConsent
				? AccountStatusSeverity.Warning
				: StatusSeverity;

	public string DisplayStatusText => IsProviderAccountChangeInProgress
		? "連接中"
		: RequiresClaudeQuotaRiskConsent
			? "等待確認"
			: StatusText;

	public AccountStatusSeverity StatusSeverity => StatusKind switch
	{
		AccountStatusKind.Ready => AccountStatusSeverity.Positive,
		AccountStatusKind.Refreshing => AccountStatusSeverity.Informational,
		AccountStatusKind.Stale => AccountStatusSeverity.Warning,
		AccountStatusKind.Error => AccountStatusSeverity.Critical,
		AccountStatusKind.NotConfigured => AccountStatusSeverity.Warning,
		AccountStatusKind.Disabled => AccountStatusSeverity.Neutral,
		AccountStatusKind.Unsupported => AccountStatusSeverity.Neutral,
		AccountStatusKind.NotConnected => AccountStatusSeverity.Neutral,
		_ => AccountStatusSeverity.Neutral
	};

	public string StatusText => StatusKind switch
	{
		AccountStatusKind.Ready => "可用",
		AccountStatusKind.Refreshing => "檢查中",
		AccountStatusKind.Stale => "顯示上次資料",
		AccountStatusKind.Error => "讀取失敗",
		AccountStatusKind.NotConfigured => "尚未設定",
		AccountStatusKind.Disabled => "已停止檢查",
		AccountStatusKind.Unsupported => "暫不支援",
		AccountStatusKind.NotConnected => "尚未連接",
		_ => "未知狀態"
	};

	public IReadOnlyList<UsageMetricViewModel> UsageMetrics
	{
		get => _usageMetrics;
		private set
		{
			_usageMetrics = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasUsageMetrics));
			OnPropertyChanged(nameof(HasNoUsageMetrics));
		}
	}

	public bool HasUsageMetrics => UsageMetrics.Count > 0;

	public bool HasNoUsageMetrics => !HasUsageMetrics;

	public double UsedPercent
	{
		get => _usedPercent;
		private set => SetField(ref _usedPercent, value);
	}

	internal bool CanPersistCurrentSnapshot =>
		!_isUsingProviderAccountChangeFallback ||
		_isProviderAccountChangeFallbackIdentityConfirmed;

	internal bool DidRejectProviderAccountSnapshot =>
		_didRejectProviderAccountSnapshot;

	internal bool HasConfirmedProviderAccountBinding
	{
		get
		{
			if (_didRejectProviderAccountSnapshot ||
				(CurrentSnapshot is not UsageSnapshot snapshot) ||
				!HasUsableUsage(snapshot) ||
				!CanPersistCurrentSnapshot ||
				!ProviderAccountIdentityRules.TryNormalize(
					Profile.ProviderAccountIdentity,
					out string? boundIdentity) ||
				(boundIdentity is null) ||
				!ProviderAccountIdentityRules.TryNormalize(
					snapshot.ProviderAccountIdentity,
					out string? snapshotIdentity) ||
				(snapshotIdentity is null))
			{
				return false;
			}

			return ProviderAccountIdentityRules.Comparer.Equals(
				boundIdentity,
				snapshotIdentity);
		}
	}

	public bool IsProviderAccountChangeCommitInProgress =>
		_isProviderAccountChangeCommitInProgress;

	internal long LifecycleRevision => _lifecycleRevision;

	public AccountUsageViewModel(
		AccountProfile profile,
		bool canManage,
		UsageDisplayMode usageDisplayMode = UsageDisplayMode.Used)
	{
		ArgumentNullException.ThrowIfNull(profile);

		if (!ProviderAccountIdentityRules.TryNormalize(
				profile.ProviderAccountIdentity,
				out string? providerAccountIdentity))
		{
			throw new ArgumentException(
				"帳號資訊格式無效。",
				nameof(profile));
		}

		_profile = profile with
		{
			ProviderAccountIdentity = providerAccountIdentity
		};
		_providerAccountIdentity = providerAccountIdentity;
		_isPrimaryEnabledAntigravityAccount = IsAntigravity && IsEnabled;

		if (!Enum.IsDefined(usageDisplayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(usageDisplayMode),
				usageDisplayMode,
				"未知的用量顯示方式。");
		}

		CanManage = canManage;
		_usageDisplayMode = usageDisplayMode;
		ResetLocalState();
	}

	public void SeedProviderAccountIdentity(string accountIdentity)
	{
		string? identity = NormalizeProviderAccountIdentity(accountIdentity);

		if (identity is null)
		{
			throw new ArgumentException(
				"帳號資訊格式無效。",
				nameof(accountIdentity));
		}

		if (_isProviderAccountChangeInProgress)
		{
			_expectedProviderAccountIdentity = identity;
		}

		SetProviderAccountIdentity(identity);
	}

	public void BeginProviderAccountChange()
	{
		if (!IsEnabled || _isProviderAccountChangeInProgress)
		{
			return;
		}

		_providerAccountChangeFallbackSnapshot = CreateProviderAccountChangeFallback();
		_providerAccountChangeFallbackIdentity = ProviderAccountIdentity;
		_expectedProviderAccountIdentity = null;
		_didRejectProviderAccountSnapshot = false;
		_hasPendingConnectionRefresh = false;
		_isProviderAccountChangeFallbackIdentityConfirmed = false;
		_isUsingProviderAccountChangeFallback = false;
		_lifecycleRevision++;
		SetProviderAccountChangeInProgress(true);
		SetProviderAccountIdentity(null);
	}

	public void EndProviderAccountChange()
	{
		if (!_isProviderAccountChangeInProgress)
		{
			return;
		}

		FinalizePendingConnectionRefresh();
		SetProviderAccountChangeInProgress(false);
		_providerAccountChangeFallbackIdentity = null;
	}

	public void AbortProviderAccountChange()
	{
		if (!_isProviderAccountChangeInProgress)
		{
			return;
		}

		string? fallbackIdentity =
			Profile.ProviderAccountIdentity ??
			_providerAccountChangeFallbackIdentity;
		_expectedProviderAccountIdentity = null;
		_didRejectProviderAccountSnapshot = false;
		ClearProviderAccountChangeFallback();
		SetProviderAccountIdentity(fallbackIdentity);
		EndProviderAccountChange();
	}

	public void CancelProviderAccountChange()
	{
		if (!_isProviderAccountChangeInProgress)
		{
			return;
		}

		_lifecycleRevision++;
		AbortProviderAccountChange();
	}

	public bool TryBeginProviderAccountChangeCommit()
	{
		if (!_isProviderAccountChangeInProgress ||
			_isProviderAccountChangeCommitInProgress)
		{
			return false;
		}

		_isProviderAccountChangeCommitInProgress = true;
		OnPropertyChanged(nameof(IsProviderAccountChangeCommitInProgress));
		OnPropertyChanged(nameof(CanCancelProviderAccountConnection));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
		OnPropertyChanged(nameof(ClaudeAccountActionText));
		OnPropertyChanged(nameof(CodexAccountActionText));
		OnPropertyChanged(nameof(CopilotAccountActionText));
		OnPropertyChanged(nameof(GrokAccountActionText));
		OnPropertyChanged(nameof(RecoveryActionText));
		return true;
	}

	public void CompleteProviderAccountChange()
	{
		if (!_isProviderAccountChangeInProgress)
		{
			return;
		}

		string? providerAccountIdentity = _isUsingProviderAccountChangeFallback
			? null
			: CurrentSnapshot?.ProviderAccountIdentity;

		if (!string.IsNullOrWhiteSpace(providerAccountIdentity))
		{
			SetProviderAccountIdentity(providerAccountIdentity);
		}

		_expectedProviderAccountIdentity = null;
		EndProviderAccountChange();
	}

	internal void CompleteProviderAccountConflictDisconnect()
	{
		if ((!IsClaude && !IsCodex && !IsCopilot && !IsGrok) ||
			!string.IsNullOrWhiteSpace(Profile.ProviderAccountIdentity))
		{
			throw new InvalidOperationException(
				"只有已清除舊連接資料的帳號可以完成衝突處理。");
		}

		_lifecycleRevision++;
		SetProviderAccountIdentity(null);
		ResetLocalState();
	}

	internal void PrepareAuthenticatedProviderConnectionRefresh(
		UsageSnapshot? retainedClaudeLegacySnapshot = null)
	{
		if ((!IsClaude && !IsCodex && !IsCopilot && !IsGrok) ||
			!_isProviderAccountChangeInProgress ||
			!_isProviderAccountChangeCommitInProgress ||
			string.IsNullOrWhiteSpace(Profile.ProviderAccountIdentity) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				Profile.ProviderAccountIdentity,
				_expectedProviderAccountIdentity))
		{
			throw new InvalidOperationException(
				"帳號連接尚未儲存完成，無法重新讀取用量。");
		}

		if ((retainedClaudeLegacySnapshot is not null) &&
			(!IsClaude ||
			(retainedClaudeLegacySnapshot.Account.Id != Id) ||
			(retainedClaudeLegacySnapshot.Account.Provider != Provider) ||
			(retainedClaudeLegacySnapshot.Metrics.Count == 0) ||
			((retainedClaudeLegacySnapshot.Status != SnapshotStatus.Ready) &&
				(retainedClaudeLegacySnapshot.Status != SnapshotStatus.Stale)) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				Profile.ProviderAccountIdentity,
				retainedClaudeLegacySnapshot.ProviderAccountIdentity)))
		{
			throw new ArgumentException(
				"保留的 Claude 舊版用量資料與目前訂閱連接資料不符。",
				nameof(retainedClaudeLegacySnapshot));
		}

		ClearProviderAccountChangeFallback();
		_providerAccountChangeFallbackSnapshot =
			retainedClaudeLegacySnapshot is null
				? null
				: retainedClaudeLegacySnapshot with
				{
					Account = Profile,
					Status = SnapshotStatus.Stale
				};
		string refreshMessage = CreateConnectionRefreshMessage();
		ApplySnapshot(new UsageSnapshot(
			Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Refreshing,
			DateTimeOffset.UtcNow,
			Error: refreshMessage,
			ProviderAccountIdentity: Profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));
		_hasPendingConnectionRefresh = true;
		PrimaryText = "正在讀取用量";
		SecondaryText = refreshMessage;
		UsedPercent = 0;
	}

	internal void PrepareAntigravityConnectionRefresh()
	{
		if (!IsAntigravity ||
			!_isProviderAccountChangeInProgress ||
			!_isProviderAccountChangeCommitInProgress ||
			string.IsNullOrWhiteSpace(Profile.ProviderAccountIdentity))
		{
			throw new InvalidOperationException(
				"Antigravity 連接尚未完成持久化，無法準備重新讀取用量。");
		}

		ClearProviderAccountChangeFallback();
		ClearAntigravityReportedAccountEmail();
		string refreshMessage = CreateConnectionRefreshMessage();
		ApplySnapshot(new UsageSnapshot(
			Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Refreshing,
			DateTimeOffset.UtcNow,
			Error: refreshMessage,
			ProviderAccountIdentity: Profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));
		_hasPendingConnectionRefresh = true;
		PrimaryText = "正在讀取用量";
		SecondaryText = refreshMessage;
		UsedPercent = 0;
	}

	internal void MarkOfficialAntigravityConnectionCommitPending()
	{
		if (!IsAntigravity)
		{
			return;
		}

		if (!_isOfficialAntigravityConnectionCommitPending)
		{
			_isOfficialAntigravityConnectionCommitPending = true;
			OnPropertyChanged(nameof(CanConnectProviderAccount));
			OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
			OnPropertyChanged(nameof(CanRefreshUsage));
			OnPropertyChanged(nameof(CanExecuteRecoveryAction));
			OnPropertyChanged(nameof(CanInvokeRecoveryAction));
			OnPropertyChanged(nameof(CanOpenMainAccountMenu));
			OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
			OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
		}

		if (!IsEnabled)
		{
			return;
		}

		ApplySnapshot(new UsageSnapshot(
			Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: OfficialAntigravityConnectionCommitPendingMessage,
			ProviderAccountIdentity: Profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));
	}

	internal void ClearOfficialAntigravityConnectionCommitPending()
	{
		if (!_isOfficialAntigravityConnectionCommitPending)
		{
			return;
		}

		bool shouldResetCommitPendingSnapshot =
			IsEnabled &&
			!_isProviderAccountChangeInProgress &&
			!_isAntigravityConnectionJournalCheckPending &&
			CurrentSnapshot is UsageSnapshot snapshot &&
			(snapshot.SourceTrust == SourceTrust.Unavailable) &&
			(snapshot.Status == SnapshotStatus.Error) &&
			(snapshot.RecoveryAction == UsageRecoveryAction.Retry) &&
			(snapshot.Metrics.Count == 0) &&
			string.Equals(
				snapshot.Error,
				OfficialAntigravityConnectionCommitPendingMessage,
				StringComparison.Ordinal);
		_isOfficialAntigravityConnectionCommitPending = false;
		OnPropertyChanged(nameof(CanConnectProviderAccount));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanRefreshUsage));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));

		if (shouldResetCommitPendingSnapshot)
		{
			ResetLocalState();
		}
	}

	internal void MarkAntigravityConnectionJournalCheckPending()
	{
		if (!IsAntigravity)
		{
			return;
		}

		if (!_isAntigravityConnectionJournalCheckPending)
		{
			_isAntigravityConnectionJournalCheckPending = true;
			OnPropertyChanged(nameof(CanConnectProviderAccount));
			OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
			OnPropertyChanged(nameof(CanRefreshUsage));
			OnPropertyChanged(nameof(CanExecuteRecoveryAction));
			OnPropertyChanged(nameof(CanInvokeRecoveryAction));
			OnPropertyChanged(nameof(CanOpenMainAccountMenu));
			OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
			OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
		}

		if (!IsEnabled)
		{
			return;
		}

		ApplySnapshot(new UsageSnapshot(
			Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: AntigravityConnectionJournalCheckPendingMessage,
			ProviderAccountIdentity: Profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));
	}

	internal void ClearAntigravityConnectionJournalCheckPending()
	{
		if (!_isAntigravityConnectionJournalCheckPending)
		{
			return;
		}

		bool shouldResetJournalCheckSnapshot =
			IsEnabled &&
			!_isOfficialAntigravityConnectionCommitPending &&
			string.Equals(
				CurrentSnapshot?.Error,
				AntigravityConnectionJournalCheckPendingMessage,
				StringComparison.Ordinal);
		_isAntigravityConnectionJournalCheckPending = false;
		OnPropertyChanged(nameof(CanConnectProviderAccount));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanRefreshUsage));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));

		if (shouldResetJournalCheckSnapshot)
		{
			ResetLocalState();
		}
	}

	internal void CompleteAntigravityConnection()
	{
		if (!IsAntigravity ||
			!_isProviderAccountChangeInProgress ||
			!_isProviderAccountChangeCommitInProgress ||
			string.IsNullOrWhiteSpace(Profile.ProviderAccountIdentity))
		{
			throw new InvalidOperationException(
				"Antigravity 連接尚未完成持久化，無法完成帳號連接。");
		}

		SetProviderAccountIdentity(Profile.ProviderAccountIdentity);
		_expectedProviderAccountIdentity = null;
		_didRejectProviderAccountSnapshot = false;
		ClearProviderAccountChangeFallback();
		EndProviderAccountChange();
	}

	public void ApplyProfile(AccountProfile profile, bool canManage)
	{
		ArgumentNullException.ThrowIfNull(profile);

		if ((profile.Id != Id) || (profile.Provider != Provider))
		{
			throw new ArgumentException(
				"帳號更新必須保留原本的帳號識別碼與服務。",
				nameof(profile));
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				profile.ProviderAccountIdentity,
				out string? providerAccountIdentity))
		{
			throw new ArgumentException(
				"帳號資訊格式無效。",
				nameof(profile));
		}

		bool wasEnabled = IsEnabled;
		bool didRevokeClaudeQuotaRiskConsent =
			IsClaude &&
			Profile.HasAcceptedClaudeQuotaRisk &&
			!profile.HasAcceptedClaudeQuotaRisk;
		_profile = profile with
		{
			ProviderAccountIdentity = providerAccountIdentity
		};
		CanManage = canManage;
		SetProviderAccountIdentity(providerAccountIdentity);
		if (IsAntigravity &&
			string.Equals(
				providerAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase))
		{
			ClearOfficialAntigravityConnectionCommitPending();
		}

		if (!IsEnabled)
		{
			SetAntigravityAccountSetupAvailability(false);
		}

		if (CurrentSnapshot is not null)
		{
			CurrentSnapshot = CurrentSnapshot with { Account = profile };
		}

		RevalidateAntigravityReportedAccountEmail();

		OnPropertyChanged(nameof(Profile));
		OnPropertyChanged(nameof(AccountName));
		OnPropertyChanged(nameof(AccountNickname));
		OnPropertyChanged(nameof(AccountCardTitle));
		OnPropertyChanged(nameof(AccountTitle));
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		OnPropertyChanged(nameof(AccountCardDisplayText));
		OnPropertyChanged(nameof(AccountDisplayText));
		OnPropertyChanged(nameof(ShowSubscriptionContext));
		OnPropertyChanged(nameof(AccountAliasText));
		OnPropertyChanged(nameof(HasAccountAlias));
		OnPropertyChanged(nameof(HasAccountNickname));
		OnPropertyChanged(nameof(IsEnabled));
		OnPropertyChanged(nameof(RequiresClaudeQuotaRiskConsent));
		OnPropertyChanged(nameof(DisplayStatusSeverity));
		OnPropertyChanged(nameof(DisplayStatusText));
		OnPropertyChanged(nameof(ClaudeAccountActionText));
		OnPropertyChanged(nameof(CodexAccountActionText));
		OnPropertyChanged(nameof(CopilotAccountActionText));
		OnPropertyChanged(nameof(GrokAccountActionText));
		OnPropertyChanged(nameof(CanChangeAccountSettings));
		OnPropertyChanged(nameof(CanConnectProviderAccount));
		OnPropertyChanged(nameof(CanCancelProviderAccountConnection));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanRefreshUsage));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(RecoveryPanelTitle));
		OnPropertyChanged(nameof(RecoveryActionText));
		OnPropertyChanged(nameof(RecoveryActionDescription));
		OnPropertyChanged(nameof(AccountDeletionConfirmationText));

		if ((wasEnabled != IsEnabled) || didRevokeClaudeQuotaRiskConsent)
		{
			_lifecycleRevision++;

			if (!IsEnabled)
			{
				AbortProviderAccountChange();
			}

			ResetLocalState();
			if (IsEnabled && _isOfficialAntigravityConnectionCommitPending)
			{
				MarkOfficialAntigravityConnectionCommitPending();
			}

			if (IsEnabled && _isAntigravityConnectionJournalCheckPending)
			{
				MarkAntigravityConnectionJournalCheckPending();
			}
		}
	}

	internal void SetAntigravityAccountSetupAvailability(bool isAvailable)
	{
		bool normalizedValue = IsAntigravity && IsEnabled && isAvailable;

		if (_isPrimaryEnabledAntigravityAccount == normalizedValue)
		{
			return;
		}

		_isPrimaryEnabledAntigravityAccount = normalizedValue;
		OnPropertyChanged(nameof(CanConfigureAntigravityAccount));
		OnPropertyChanged(nameof(CanConnectProviderAccount));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(HasExecutableRecoveryAction));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
	}

	public void ApplySnapshot(UsageSnapshot snapshot)
	{
		ApplySnapshot(snapshot, allowLiveOfficialLocalSessionMigration: false);
	}

	internal bool TrySetAntigravityReportedAccountEmail(
		string email,
		DateTimeOffset capturedAtUtc,
		TimeSpan maximumAssociationLag,
		bool isStale,
		string? planTier = null)
	{
		return TrySetAntigravityReportedAccountEmail(
			email,
			capturedAtUtc,
			DateTimeOffset.MinValue,
			maximumAssociationLag,
			isStale,
			planTier: planTier);
	}

	internal bool TrySetAntigravityReportedAccountEmail(
		string email,
		DateTimeOffset capturedAtUtc,
		DateTimeOffset minimumCapturedAtUtc,
		TimeSpan maximumAssociationLag,
		bool isStale,
		bool allowStaleSnapshot = false,
		string? planTier = null)
	{
		_ = AntigravityStatusLineCapture.TryNormalizePlanTier(
			planTier,
			out string? normalizedPlanTier);
		if (string.IsNullOrWhiteSpace(email) ||
			(capturedAtUtc < minimumCapturedAtUtc) ||
			!TryGetAntigravityReportedAccountAssociationLag(
				capturedAtUtc,
				maximumAssociationLag,
				out _,
				allowStaleSnapshot))
		{
			return false;
		}

		string normalizedEmail = email.Trim();
		bool didDisplayChange = !string.Equals(
			_antigravityReportedAccountEmail,
			normalizedEmail,
			StringComparison.Ordinal) ||
			!string.Equals(CurrentSnapshot?.PlanTier,
				normalizedPlanTier,
				StringComparison.Ordinal) ||
			(_isAntigravityReportedAccountEmailStale != isStale);
		_antigravityReportedAccountEmail = normalizedEmail;
		_isAntigravityReportedAccountEmailStale = isStale;
		SetAntigravityReportedPlanTier(normalizedPlanTier);

		if (didDisplayChange)
		{
			NotifyAntigravityReportedAccountDisplayChanged();
		}

		return true;
	}

	internal bool TryRestoreVerifiedAntigravityReportedAccountEmail(
		string email,
		string? planTier = null)
	{
		_ = AntigravityStatusLineCapture.TryNormalizePlanTier(
			planTier,
			out string? normalizedPlanTier);
		if (!IsAntigravity ||
			!IsEnabled ||
			!string.Equals(
				ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrWhiteSpace(email))
		{
			return false;
		}

		string normalizedEmail = email.Trim();
		bool didDisplayChange = !string.Equals(
			_antigravityReportedAccountEmail,
			normalizedEmail,
			StringComparison.Ordinal) ||
			!string.Equals(CurrentSnapshot?.PlanTier,
				normalizedPlanTier,
				StringComparison.Ordinal) ||
			!_isAntigravityReportedAccountEmailStale;
		_antigravityReportedAccountEmail = normalizedEmail;
		_isAntigravityReportedAccountEmailStale = true;
		SetAntigravityReportedPlanTier(normalizedPlanTier);

		if (didDisplayChange)
		{
			NotifyAntigravityReportedAccountDisplayChanged();
		}

		return true;
	}

	internal bool TryGetAntigravityReportedAccountAssociationLag(
		DateTimeOffset capturedAtUtc,
		TimeSpan maximumAssociationLag,
		out TimeSpan associationLag,
		bool allowStaleSnapshot = false)
	{
		associationLag = default;
		bool hasCommittedLocalSessionBinding = string.Equals(
			ProviderAccountIdentity,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			StringComparison.OrdinalIgnoreCase) ||
			(_isProviderAccountChangeCommitInProgress &&
				string.IsNullOrWhiteSpace(ProviderAccountIdentity));
		if (!IsAntigravity ||
			(maximumAssociationLag < TimeSpan.Zero) ||
			_didRejectProviderAccountSnapshot ||
			!CanPersistCurrentSnapshot ||
			(CurrentSnapshot is not UsageSnapshot snapshot) ||
			((snapshot.Status != SnapshotStatus.Ready) &&
				(!allowStaleSnapshot ||
					(snapshot.Status != SnapshotStatus.Stale))) ||
			!string.IsNullOrWhiteSpace(snapshot.Error) ||
			(snapshot.Metrics.Count == 0) ||
			(snapshot.SourceTrust != SourceTrust.OfficialExperimental) ||
			!string.Equals(
				snapshot.ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase) ||
			!hasCommittedLocalSessionBinding)
		{
			return false;
		}

		DateTimeOffset observedAt = snapshot.ObservedAt ?? snapshot.FetchedAt;
		if (observedAt >= capturedAtUtc)
		{
			associationLag = observedAt - capturedAtUtc;
			return associationLag <= maximumAssociationLag;
		}

		// A capture made after the quota snapshot cannot prove which login
		// produced that quota. Keep verified AGY bindings strictly
		// capture-before-quota, including legacy cache migration.
		associationLag = observedAt - capturedAtUtc;
		return false;
	}

	internal void ClearAntigravityReportedAccountEmail()
	{
		bool didDisplayChange =
			!string.IsNullOrWhiteSpace(_antigravityReportedAccountEmail) ||
			_isAntigravityReportedAccountEmailStale;
		_antigravityReportedAccountEmail = null;
		_isAntigravityReportedAccountEmailStale = false;

		if (didDisplayChange)
		{
			NotifyAntigravityReportedAccountDisplayChanged();
		}
	}

	internal void MarkAntigravityReportedAccountEmailStale()
	{
		if (string.IsNullOrWhiteSpace(_antigravityReportedAccountEmail) ||
			_isAntigravityReportedAccountEmailStale)
		{
			return;
		}

		_isAntigravityReportedAccountEmailStale = true;
		NotifyAntigravityReportedAccountDisplayChanged();
	}

	internal void ResetImportedMachineLocalConnectionState()
	{
		if (!IsClaude && !IsCodex && !IsCopilot && !IsAntigravity && !IsGrok)
		{
			throw new InvalidOperationException(
				"只有 Claude、Codex、Copilot、Antigravity 或 Grok 帳號具有需要重設的本機連接狀態。");
		}

		_lifecycleRevision++;
		_profile = Profile with { ProviderAccountIdentity = null };
		SetProviderAccountIdentity(null);
		ResetLocalState();
		OnPropertyChanged(nameof(Profile));
	}

	private void NotifyAntigravityReportedAccountDisplayChanged()
	{
		OnPropertyChanged(nameof(AccountName));
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		OnPropertyChanged(nameof(AccountCardDisplayText));
		OnPropertyChanged(nameof(AccountDisplayText));
		OnPropertyChanged(nameof(HasAccountAlias));
		OnPropertyChanged(nameof(AccountDeletionConfirmationText));
	}

	private void SetAntigravityReportedPlanTier(string? planTier)
	{
		if ((CurrentSnapshot is not UsageSnapshot snapshot) ||
			string.Equals(
				snapshot.PlanTier,
				planTier,
				StringComparison.Ordinal))
		{
			return;
		}

		CurrentSnapshot = snapshot with { PlanTier = planTier };
	}

	internal void ApplyLiveSnapshot(UsageSnapshot snapshot)
	{
		ApplySnapshot(snapshot, allowLiveOfficialLocalSessionMigration: true);
	}

	private void ApplySnapshot(
		UsageSnapshot snapshot,
		bool allowLiveOfficialLocalSessionMigration)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if ((snapshot.Account.Id != Id) || (snapshot.Account.Provider != Provider))
		{
			throw new ArgumentException(
				"用量資料必須屬於目前帳號與服務。",
				nameof(snapshot));
		}

		if (!IsEnabled)
		{
			ResetLocalState();
			return;
		}

		snapshot = snapshot with { Account = Profile };

		if (CanSkipSnapshotProjection(snapshot))
		{
			return;
		}

		_didRejectProviderAccountSnapshot = false;

		if (HasProviderAccountIdentityMismatch(
				snapshot,
				allowLiveOfficialLocalSessionMigration))
		{
			_didRejectProviderAccountSnapshot = true;
			snapshot = new UsageSnapshot(
				Profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				snapshot.FetchedAt,
				Error: CreateProviderAccountIdentityRejectionMessage(snapshot),
				ProviderAccountIdentity: ProviderAccountIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount);
		}
		else if (HasUsableUsage(snapshot) &&
			!string.IsNullOrWhiteSpace(_expectedProviderAccountIdentity))
		{
			_expectedProviderAccountIdentity = null;
		}
		else if (((snapshot.Status == SnapshotStatus.NotConfigured) ||
				(snapshot.Status == SnapshotStatus.Unsupported)) &&
			!MustPreserveExpectedAuthenticatedProviderAccountIdentity())
		{
			_expectedProviderAccountIdentity = null;
		}

		bool isProviderAccountChangeFallback =
			IsProviderAccountChangeFallback(snapshot);

		if (isProviderAccountChangeFallback)
		{
			snapshot = AddProviderAccountChangeFallbackNotice(snapshot);
		}
		else if (HasUsableUsage(snapshot) ||
			(snapshot.Status == SnapshotStatus.NotConfigured) ||
			(snapshot.Status == SnapshotStatus.Unsupported))
		{
			ClearProviderAccountChangeFallback();
		}

		_isUsingProviderAccountChangeFallback = isProviderAccountChangeFallback;
		CurrentSnapshot = snapshot;
		NotifyRecoveryActionChanged();

		if (!_isProviderAccountChangeInProgress &&
			!_isUsingProviderAccountChangeFallback)
		{
			if (HasUsableUsage(snapshot) &&
				ProviderAccountIdentityRules.TryNormalize(
					Profile.ProviderAccountIdentity,
					out string? persistedIdentity) &&
				(persistedIdentity is not null) &&
				ProviderAccountIdentityRules.TryNormalize(
					snapshot.ProviderAccountIdentity,
					out string? snapshotIdentity) &&
				(snapshotIdentity is not null) &&
				ProviderAccountIdentityRules.Comparer.Equals(
					persistedIdentity,
					snapshotIdentity))
			{
				SetProviderAccountIdentity(snapshotIdentity);
			}
			else if (ShouldClearProviderAccountIdentity(snapshot))
			{
				SetProviderAccountIdentity(Profile.ProviderAccountIdentity);
			}
		}

		OnPropertyChanged(nameof(AccountName));
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		OnPropertyChanged(nameof(AccountCardDisplayText));
		OnPropertyChanged(nameof(AccountDisplayText));
		OnPropertyChanged(nameof(AccountDeletionConfirmationText));
		if (_hasPendingConnectionRefresh)
		{
			RevalidateAntigravityReportedAccountEmail();
			return;
		}

		ProjectSnapshot(snapshot);
	}

	private void ProjectSnapshot(UsageSnapshot snapshot)
	{
		ProjectUsageMetrics(snapshot);
		NoticeText = (snapshot.Status == SnapshotStatus.Stale) &&
			(snapshot.RecoveryAction != UsageRecoveryAction.Retry) &&
			!string.IsNullOrWhiteSpace(snapshot.Error)
			? CreateStaleNoticeText(snapshot)
			: string.Empty;

		switch (snapshot.Status)
		{
			case SnapshotStatus.Ready:
				ApplyMetrics(snapshot);
				StatusKind = AccountStatusKind.Ready;
				break;
			case SnapshotStatus.Stale:
				ApplyMetrics(snapshot);
				StatusKind = AccountStatusKind.Stale;
				break;
			case SnapshotStatus.Refreshing:
				StatusKind = AccountStatusKind.Refreshing;
				break;
			case SnapshotStatus.NotConfigured:
				PrimaryText = GetNotConfiguredPrimaryText(snapshot.RecoveryAction);
				SecondaryText = snapshot.Error ?? "等待確認用量讀取";
				StatusKind = AccountStatusKind.NotConfigured;
				UsedPercent = 0;
				break;
			case SnapshotStatus.Unsupported:
				PrimaryText = "暫不支援";
				SecondaryText = snapshot.Error ?? "此服務目前不支援用量顯示";
				StatusKind = AccountStatusKind.Unsupported;
				UsedPercent = 0;
				break;
			case SnapshotStatus.Error:
				PrimaryText = "讀取失敗";
				SecondaryText = snapshot.Error ?? "暫時無法讀取用量，稍後會自動再試。";
				StatusKind = AccountStatusKind.Error;
				UsedPercent = 0;
				break;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(snapshot),
					snapshot.Status,
					"未知的用量快照狀態。");
		}

		RevalidateAntigravityReportedAccountEmail();
	}

	private void RevalidateAntigravityReportedAccountEmail()
	{
		if (string.IsNullOrWhiteSpace(_antigravityReportedAccountEmail))
		{
			return;
		}

		if (IsAntigravity &&
			IsEnabled &&
			_isProviderAccountChangeInProgress)
		{
			// Keep the last verified display identity until setup either reports a
			// replacement or rolls back. The provider binding is intentionally
			// cleared while a new login is being verified, but that temporary state
			// is not evidence that the previously reported email became invalid.
			return;
		}

		// The email is display metadata confirmed alongside an earlier quota
		// observation. A replacement snapshot or profile-only clone is not, by
		// itself, evidence that the local AGY login changed. The dashboard
		// reconciles this metadata after every real AGY quota query. Missing or
		// delayed display metadata only marks the last verified email stale; an
		// explicit disconnect or machine-local import reset clears it.
		if (!IsAntigravity ||
			!IsEnabled ||
			_didRejectProviderAccountSnapshot ||
			!string.Equals(
				ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase))
		{
			ClearAntigravityReportedAccountEmail();
		}
	}

	internal bool CanAcceptProviderAccountSnapshot(UsageSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		return !HasProviderAccountIdentityMismatch(
			snapshot,
			allowLiveOfficialLocalSessionMigration: false);
	}

	internal bool CanAcceptLiveProviderAccountSnapshot(UsageSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		return !HasProviderAccountIdentityMismatch(
			snapshot,
			allowLiveOfficialLocalSessionMigration: true);
	}

	internal void RejectProviderAccountSnapshot(
		UsageSnapshot snapshot,
		string error,
		UsageRecoveryAction recoveryAction)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(error);
		string? persistedProviderAccountIdentity =
			Profile.ProviderAccountIdentity;
		ApplySnapshot(new UsageSnapshot(
			Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			snapshot.FetchedAt,
			Error: error,
			ProviderAccountIdentity: persistedProviderAccountIdentity,
			RecoveryAction: recoveryAction));
		SetProviderAccountIdentity(persistedProviderAccountIdentity);
		_didRejectProviderAccountSnapshot = true;
	}

	public void MarkRefreshing()
	{
		if (!IsEnabled)
		{
			ResetLocalState();
			return;
		}

		if (CurrentSnapshot is null)
		{
			StatusKind = AccountStatusKind.Refreshing;
			PrimaryText = "正在讀取用量";
			SecondaryText = "正在等待用量資料";
		}
	}

	internal void SetMoveAvailability(bool canMoveUp, bool canMoveDown)
	{
		CanMoveUp = canMoveUp;
		CanMoveDown = canMoveDown;
	}

	internal void SetUsageDisplayMode(UsageDisplayMode value)
	{
		if (!Enum.IsDefined(value))
		{
			throw new ArgumentOutOfRangeException(
				nameof(value),
				value,
				"未知的用量顯示方式。");
		}

		if (_usageDisplayMode == value)
		{
			return;
		}

		_usageDisplayMode = value;

		if ((CurrentSnapshot is not null) &&
			!_hasPendingConnectionRefresh)
		{
			ProjectUsageMetrics(CurrentSnapshot);
		}
	}

	internal UsageSnapshot? GetProviderAccountChangeFallbackSnapshot()
	{
		UsageSnapshot? fallbackSnapshot = _providerAccountChangeFallbackSnapshot;

		if (fallbackSnapshot is null)
		{
			return null;
		}

		string? targetIdentity = NormalizeProviderAccountIdentity(
			ProviderAccountIdentity);
		string? fallbackIdentity = NormalizeProviderAccountIdentity(
			fallbackSnapshot.ProviderAccountIdentity);

		if (targetIdentity is not null)
		{
			if ((fallbackIdentity is null) ||
				!string.Equals(
					fallbackIdentity,
					targetIdentity,
					StringComparison.OrdinalIgnoreCase))
			{
				ClearProviderAccountChangeFallback();
				return null;
			}

			_isProviderAccountChangeFallbackIdentityConfirmed = true;
		}
		else
		{
			_isProviderAccountChangeFallbackIdentityConfirmed = false;
		}

		return fallbackSnapshot with { Account = Profile };
	}

	private void ApplyMetrics(UsageSnapshot snapshot)
	{
		UsageMetric? primaryMetric = snapshot.Metrics.FirstOrDefault();

		if (primaryMetric is null)
		{
			PrimaryText = "已連接";
			SecondaryText = CreateObservationText(snapshot);
			UsedPercent = 0;
			return;
		}

		PrimaryText = primaryMetric.DisplayValue;
		List<string> details = new()
		{
			primaryMetric.Label
		};

		if (primaryMetric.ResetsAt is not null)
		{
			details.Add($"重置 {primaryMetric.ResetsAt.Value.ToLocalTime():MM/dd HH:mm}");
		}

		UsageMetric? secondaryMetric = snapshot.Metrics.Skip(1).FirstOrDefault();

		if (secondaryMetric is not null)
		{
			details.Add($"{secondaryMetric.Label} {secondaryMetric.DisplayValue}");
		}

		SecondaryText = string.Join(" · ", details);
		UsedPercent = Math.Clamp(primaryMetric.UsedPercent ?? 0, 0, 100);
	}

	private void ProjectUsageMetrics(UsageSnapshot snapshot)
	{
		IEnumerable<UsageMetric> metrics = snapshot.Metrics;

		if (IsAntigravity &&
			(snapshot.RecoveryAction == UsageRecoveryAction.RevalidateUsage))
		{
			HashSet<string> existingKeys = snapshot.Metrics
				.Select(metric => metric.Key)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			metrics = snapshot.Metrics.Concat(
				AntigravityFiveHourRevalidationPlaceholders.Where(
					placeholder => !existingKeys.Contains(placeholder.Key)));
		}

		UsageMetrics = UsageMetricPresentation.OrderMetrics(metrics)
			.Select(metric => new UsageMetricViewModel(
				metric,
				_usageDisplayMode,
				showResetText: !IsCopilot,
				usePercentageDisplayValue: IsCopilot))
			.ToArray();
	}

	private static string CreateStaleNoticeText(UsageSnapshot snapshot)
	{
		string dataAgeText = snapshot.ObservedAt is null
			? "目前顯示上次成功讀取的資料（時間不明）。"
			: $"目前顯示 {snapshot.ObservedAt.Value.ToLocalTime():yyyy/MM/dd HH:mm} 成功讀取的資料。";
		return $"{dataAgeText}{snapshot.Error}";
	}

	private static string CreateAutomaticRetryDescription(UsageSnapshot? snapshot)
	{
		if (snapshot?.Status != SnapshotStatus.Stale)
		{
			return "這次未取得新用量，稍後會自動再試，不需要手動操作。";
		}

		string dataAgeText = snapshot.ObservedAt is null
			? "目前顯示上次成功讀取的資料"
			: $"目前顯示 {snapshot.ObservedAt.Value.ToLocalTime():yyyy/MM/dd HH:mm} 成功讀取的資料";
		string? retryReason = GetAutomaticRetryReasonDetail(snapshot.Error);

		if (retryReason is not null)
		{
			return $"{dataAgeText}。{retryReason} AI Usage 稍後會自動再試，不需要手動操作。";
		}

		return $"{dataAgeText}，稍後會自動再試，不需要手動操作。";
	}

	private static string? GetAutomaticRetryReasonDetail(string? error)
	{
		if (string.IsNullOrWhiteSpace(error))
		{
			return null;
		}

		string reason = error.Trim();

		foreach (string guidance in RemovableAutomaticRetryGuidance)
		{
			int guidanceIndex = reason.IndexOf(
				guidance,
				StringComparison.Ordinal);

			if (guidanceIndex >= 0)
			{
				return RemoveAutomaticRetryGuidance(
					reason,
					guidanceIndex,
					guidance.Length);
			}
		}

		return ContainsAutomaticRetryGuidance(reason)
			? null
			: reason;
	}

	private static string? RemoveAutomaticRetryGuidance(
		string reason,
		int guidanceIndex,
		int guidanceLength)
	{
		string prefix = reason[..guidanceIndex]
			.TrimEnd()
			.TrimEnd('；', '，', '。');
		string suffix = reason[(guidanceIndex + guidanceLength)..]
			.TrimStart();

		if (string.IsNullOrWhiteSpace(prefix))
		{
			return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
		}

		return string.IsNullOrWhiteSpace(suffix)
			? $"{prefix}。"
			: $"{prefix}。 {suffix}";
	}

	private static bool ContainsAutomaticRetryGuidance(string? error)
	{
		return !string.IsNullOrWhiteSpace(error) &&
			(error.Contains("稍後會自動", StringComparison.Ordinal) ||
				error.Contains("稍後自動", StringComparison.Ordinal) ||
				error.Contains("自動重試", StringComparison.Ordinal));
	}

	private static bool HasInlineAutomaticRetryGuidance(UsageSnapshot? snapshot)
	{
		return (snapshot?.Status == SnapshotStatus.Error) &&
			(snapshot.RecoveryAction == UsageRecoveryAction.Retry) &&
			(snapshot.Metrics.Count == 0) &&
			ContainsAutomaticRetryGuidance(snapshot.Error);
	}

	private string CreateConnectionRefreshMessage()
	{
		return IsAntigravity
			? "Antigravity 帳號已連接，正在重新讀取用量；若暫時失敗，稍後會自動再試。"
			: $"{ProviderName} 帳號已連接，正在重新檢查用量；若暫時失敗，稍後會自動再試。";
	}

	private void FinalizePendingConnectionRefresh()
	{
		if (!_hasPendingConnectionRefresh)
		{
			return;
		}

		UsageSnapshot? snapshot = CurrentSnapshot;
		_hasPendingConnectionRefresh = false;

		if (snapshot is null)
		{
			OnPropertyChanged(nameof(ShowRecoveryActionNotice));
			return;
		}

		if ((snapshot.Status == SnapshotStatus.Refreshing) &&
			(snapshot.RecoveryAction == UsageRecoveryAction.Retry))
		{
			ApplySnapshot(snapshot with
			{
				Status = SnapshotStatus.Error,
				Error =
					$"{ProviderName} 帳號已連接，但這次未取得新用量；" +
					InlineAutomaticRetryGuidance
			});
			return;
		}

		ProjectSnapshot(snapshot);
		OnPropertyChanged(nameof(ShowRecoveryActionNotice));
	}

	private UsageSnapshot AddProviderAccountChangeFallbackNotice(
		UsageSnapshot snapshot)
	{
		string fallbackMessage;

		if (_isProviderAccountChangeFallbackIdentityConfirmed)
		{
			fallbackMessage =
				"最新用量暫時無法讀取；目前顯示這個帳號上次成功取得的資料。";
		}
		else if (!string.IsNullOrWhiteSpace(snapshot.ProviderAccountIdentity))
		{
			string identity = GetProviderAccountIdentityDisplayText(
				snapshot.ProviderAccountIdentity,
				" · ");
			fallbackMessage =
				$"新登入帳號尚未確認；目前顯示上次確認帳號 {identity} 的用量。";
		}
		else
		{
			fallbackMessage =
				"新登入帳號尚未確認；目前顯示切換前最後一次成功取得的用量。";
		}

		return snapshot with
		{
			Error = string.IsNullOrWhiteSpace(snapshot.Error)
				? fallbackMessage
				: $"{fallbackMessage}{snapshot.Error}"
		};
	}

	private bool CanSkipSnapshotProjection(UsageSnapshot snapshot)
	{
		return !_isProviderAccountChangeInProgress &&
			!_isUsingProviderAccountChangeFallback &&
			!_isProviderAccountChangeFallbackIdentityConfirmed &&
			(_providerAccountChangeFallbackSnapshot is null) &&
			string.IsNullOrWhiteSpace(_expectedProviderAccountIdentity) &&
			!_didRejectProviderAccountSnapshot &&
			Equals(CurrentSnapshot, snapshot);
	}

	private void ClearProviderAccountChangeFallback()
	{
		_providerAccountChangeFallbackSnapshot = null;
		_isProviderAccountChangeFallbackIdentityConfirmed = false;
		_isUsingProviderAccountChangeFallback = false;
	}

	private UsageSnapshot? CreateProviderAccountChangeFallback()
	{
		if ((CurrentSnapshot is not UsageSnapshot snapshot) ||
			!HasUsableUsage(snapshot))
		{
			return null;
		}

		return snapshot with
		{
			Account = Profile,
			ProviderAccountIdentity = snapshot.ProviderAccountIdentity ??
				ProviderAccountIdentity
		};
	}

	private bool IsProviderAccountChangeFallback(UsageSnapshot snapshot)
	{
		UsageSnapshot? fallbackSnapshot = _providerAccountChangeFallbackSnapshot;

		return (fallbackSnapshot is not null) &&
			(snapshot.Status == SnapshotStatus.Stale) &&
			(snapshot.SourceTrust == fallbackSnapshot.SourceTrust) &&
			(snapshot.FetchedAt == fallbackSnapshot.FetchedAt) &&
			(snapshot.ObservedAt == fallbackSnapshot.ObservedAt) &&
			(snapshot.StaleAfter == fallbackSnapshot.StaleAfter) &&
			string.Equals(
				snapshot.ProviderAccountIdentity,
				fallbackSnapshot.ProviderAccountIdentity,
				StringComparison.OrdinalIgnoreCase) &&
			snapshot.Metrics.SequenceEqual(fallbackSnapshot.Metrics);
	}

	private static bool HasUsableUsage(UsageSnapshot snapshot)
	{
		return ((snapshot.Status == SnapshotStatus.Ready) ||
			(snapshot.Status == SnapshotStatus.Stale)) &&
			(snapshot.Metrics.Count > 0);
	}

	private string GetNotConfiguredPrimaryText(
		UsageRecoveryAction recoveryAction)
	{
		return recoveryAction switch
		{
			UsageRecoveryAction.ConnectAccount when
				IsClaude && RequiresClaudeQuotaRiskConsent =>
				"等待確認用量讀取",
			UsageRecoveryAction.InstallOrUpdate => "CLI 待安裝或更新",
			UsageRecoveryAction.ReconfigureUsageSource => "需要確認用量讀取",
			UsageRecoveryAction.SwitchAccount => "帳號需要切換",
			UsageRecoveryAction.ConfirmSubscription => "需要確認訂閱",
			_ => "尚未連接"
		};
	}

	private static bool ShouldClearProviderAccountIdentity(
		UsageSnapshot snapshot)
	{
		return (snapshot.Status == SnapshotStatus.NotConfigured) &&
			(snapshot.RecoveryAction is UsageRecoveryAction.None or
				UsageRecoveryAction.ConnectAccount or
				UsageRecoveryAction.SwitchAccount or
				UsageRecoveryAction.ConfirmSubscription);
	}

	private bool HasProviderAccountIdentityMismatch(
		UsageSnapshot snapshot,
		bool allowLiveOfficialLocalSessionMigration)
	{
		if (!HasUsableUsage(snapshot))
		{
			return false;
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null))
		{
			return true;
		}

		string? boundIdentity = _isProviderAccountChangeInProgress
			? _expectedProviderAccountIdentity
			: Profile.ProviderAccountIdentity ?? ProviderAccountIdentity;
		if ((boundIdentity is null) ||
			ProviderAccountIdentityRules.Comparer.Equals(
				boundIdentity,
				snapshotIdentity))
		{
			return false;
		}

		return !allowLiveOfficialLocalSessionMigration ||
			!IsLiveOfficialLocalSessionMigration(snapshot, snapshotIdentity);
	}

	private bool MustPreserveExpectedAuthenticatedProviderAccountIdentity()
	{
		return (IsClaude || IsCodex || IsCopilot || IsGrok) &&
			_isProviderAccountChangeInProgress &&
			_isProviderAccountChangeCommitInProgress &&
			!string.IsNullOrWhiteSpace(_expectedProviderAccountIdentity) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				Profile.ProviderAccountIdentity,
				_expectedProviderAccountIdentity);
	}

	internal void BeginUsageSafetyRevalidation(long operationId)
	{
		if (operationId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(operationId));
		}

		long previousOperationId = Interlocked.Exchange(
			ref _usageSafetyRevalidationOperationId,
			operationId);

		if (previousOperationId != 0)
		{
			return;
		}

		NotifyUsageSafetyRevalidationChanged();
	}

	internal void CompleteUsageSafetyRevalidation(long operationId)
	{
		if ((operationId <= 0) ||
			(Interlocked.CompareExchange(
				ref _usageSafetyRevalidationOperationId,
				0,
				operationId) != operationId))
		{
			return;
		}

		NotifyUsageSafetyRevalidationChanged();
	}

	private void NotifyUsageSafetyRevalidationChanged()
	{
		OnPropertyChanged(nameof(IsUsageSafetyRevalidationInProgress));
		OnPropertyChanged(nameof(CanRefreshUsage));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
	}

	private bool IsLiveOfficialLocalSessionMigration(
		UsageSnapshot snapshot,
		string snapshotIdentity)
	{
		return IsAntigravity &&
			(snapshot.Account.Id == Id) &&
			(snapshot.Account.Provider == ProviderKind.Antigravity) &&
			(snapshot.Status == SnapshotStatus.Ready) &&
			(snapshot.SourceTrust == SourceTrust.OfficialExperimental) &&
			string.IsNullOrWhiteSpace(snapshot.Error) &&
			string.Equals(
				snapshotIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.Ordinal);
	}

	private bool HasUnpersistedOfficialLocalSessionBinding()
	{
		return (CurrentSnapshot is UsageSnapshot snapshot) &&
			ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) &&
			(snapshotIdentity is not null) &&
			IsLiveOfficialLocalSessionMigration(snapshot, snapshotIdentity) &&
			!ProviderAccountIdentityRules.Comparer.Equals(
				Profile.ProviderAccountIdentity,
				snapshotIdentity);
	}

	private static string CreateProviderAccountIdentityRejectionMessage(
		UsageSnapshot snapshot)
	{
		return string.IsNullOrWhiteSpace(snapshot.ProviderAccountIdentity)
			? "這次檢查無法確認登入帳號，因此已忽略結果。請重新連接帳號。"
			: "這次檢查取得的登入帳號與這個帳號不同，因此已忽略結果。請重新連接正確帳號。";
	}

	private static string CreateObservationText(UsageSnapshot snapshot)
	{
		DateTimeOffset observedAt = snapshot.ObservedAt ?? snapshot.FetchedAt;
		return $"資料時間 {observedAt.ToLocalTime():MM/dd HH:mm:ss}";
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	private void ResetLocalState()
	{
		CurrentSnapshot = null;
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		ClearAntigravityReportedAccountEmail();
		NotifyRecoveryActionChanged();
		_expectedProviderAccountIdentity = null;
		_didRejectProviderAccountSnapshot = false;
		ClearProviderAccountChangeFallback();
		EndProviderAccountChange();
		UsageMetrics = Array.Empty<UsageMetricViewModel>();
		NoticeText = string.Empty;

		if (!IsEnabled)
		{
			PrimaryText = "已停止檢查";
			SecondaryText = "不會檢查這個帳號的用量";
			StatusKind = AccountStatusKind.Disabled;
		}
		else if (Provider == ProviderKind.Antigravity)
		{
			PrimaryText = HasProviderAccountIdentity
				? "正在檢查用量"
				: "尚未取得用量";
			SecondaryText = "等待本機 Antigravity CLI 檢查完成";
			StatusKind = HasProviderAccountIdentity
				? AccountStatusKind.Refreshing
				: AccountStatusKind.NotConnected;
		}
		else
		{
			PrimaryText = "尚未取得用量";
			SecondaryText = "帳號尚未完成連接";
			StatusKind = AccountStatusKind.NotConnected;
		}

		UsedPercent = 0;
	}

	private void SetProviderAccountIdentity(string? value)
	{
		string? identity = NormalizeProviderAccountIdentity(value);

		if (string.Equals(
			_providerAccountIdentity,
			identity,
			StringComparison.Ordinal))
		{
			return;
		}

		_providerAccountIdentity = identity;
		OnPropertyChanged(nameof(ProviderAccountIdentity));
		OnPropertyChanged(nameof(HasProviderAccountIdentity));
		OnPropertyChanged(nameof(AccountName));
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		OnPropertyChanged(nameof(AccountCardDisplayText));
		OnPropertyChanged(nameof(AccountDisplayText));
		OnPropertyChanged(nameof(HasAccountAlias));
		OnPropertyChanged(nameof(ClaudeAccountActionText));
		OnPropertyChanged(nameof(CodexAccountActionText));
		OnPropertyChanged(nameof(CopilotAccountActionText));
		OnPropertyChanged(nameof(GrokAccountActionText));
		OnPropertyChanged(nameof(AntigravityAccountActionText));
		OnPropertyChanged(nameof(AccountDeletionConfirmationText));
		OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
	}

	private void SetProviderAccountChangeInProgress(bool value)
	{
		if (_isProviderAccountChangeInProgress == value)
		{
			return;
		}

		_isProviderAccountChangeInProgress = value;

		if (!value)
		{
			_isProviderAccountChangeCommitInProgress = false;
		}

		OnPropertyChanged(nameof(IsProviderAccountChangeInProgress));
		OnPropertyChanged(nameof(IsProviderAccountChangeCommitInProgress));
		OnPropertyChanged(nameof(CanChangeAccountSettings));
		OnPropertyChanged(nameof(CanConnectProviderAccount));
		OnPropertyChanged(nameof(CanCancelProviderAccountConnection));
		OnPropertyChanged(nameof(CanInvokeProviderAccountAction));
		OnPropertyChanged(nameof(CanRefreshUsage));
		OnPropertyChanged(nameof(CanOpenMainAccountMenu));
		OnPropertyChanged(nameof(CanOpenWidgetAccountMenu));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
		OnPropertyChanged(nameof(AccountHeaderText));
		OnPropertyChanged(nameof(AccountHeaderSuffixText));
		OnPropertyChanged(nameof(SubscriptionPlanDisplayText));
		OnPropertyChanged(nameof(AccountCardDisplayText));
		OnPropertyChanged(nameof(AccountDisplayText));
		OnPropertyChanged(nameof(ClaudeAccountActionText));
		OnPropertyChanged(nameof(CodexAccountActionText));
		OnPropertyChanged(nameof(CopilotAccountActionText));
		OnPropertyChanged(nameof(GrokAccountActionText));
		OnPropertyChanged(nameof(RecoveryActionText));
		OnPropertyChanged(nameof(DisplayStatusSeverity));
		OnPropertyChanged(nameof(DisplayStatusText));
	}

	private void NotifyRecoveryActionChanged()
	{
		OnPropertyChanged(nameof(RecoveryAction));
		OnPropertyChanged(nameof(ClaudeAccountActionText));
		OnPropertyChanged(nameof(HasRecoveryAction));
		OnPropertyChanged(nameof(ShowRecoveryActionNotice));
		OnPropertyChanged(nameof(RecoveryPanelTitle));
		OnPropertyChanged(nameof(HasExecutableRecoveryAction));
		OnPropertyChanged(nameof(CanExecuteRecoveryAction));
		OnPropertyChanged(nameof(CanInvokeRecoveryAction));
		OnPropertyChanged(nameof(ShowClaudeDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCodexDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowCopilotDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowGrokDefaultConnectionAction));
		OnPropertyChanged(nameof(ShowAntigravityDefaultConnectionAction));
		OnPropertyChanged(nameof(RecoveryActionText));
		OnPropertyChanged(nameof(RecoveryActionDescription));
	}

	private static string? NormalizeProviderAccountIdentity(string? value)
	{
		return ProviderAccountIdentityRules.TryNormalize(
			value,
			out string? normalizedIdentity)
			? normalizedIdentity
			: null;
	}

	private string GetAccountDisplayText(
		string subscriptionContextSeparator,
		bool includeSubscriptionContext,
		bool includePlanTier)
	{
		if (_isProviderAccountChangeInProgress)
		{
			return "連接中";
		}

		if (!HasProviderAccountIdentity)
		{
			return IsEnabled
				? "尚未確認"
				: "未檢查（已停止檢查）";
		}

		string identity = GetProviderAccountCardDisplayText(
			ProviderAccountIdentity,
			subscriptionContextSeparator,
			includeSubscriptionContext,
			includePlanTier);

		if (IsUsingAntigravityReportedAccountEmail())
		{
			return identity;
		}

		bool isLastConfirmedIdentity =
			HasUnpersistedOfficialLocalSessionBinding() ||
			!IsEnabled ||
			(CurrentSnapshot is null) ||
			((CurrentSnapshot.Status != SnapshotStatus.Ready) &&
				(CurrentSnapshot.SubscriptionVerificationState is not
					SubscriptionVerificationState.Verified and not
					SubscriptionVerificationState.UsageUnavailable));
		return isLastConfirmedIdentity
			? $"{identity}（上次確認）"
			: identity;
	}

	private string GetProviderAccountIdentityDisplayText(
		string? identity,
		string subscriptionContextSeparator,
		bool includeSubscriptionContext = true,
		bool includePlanTier = true)
	{
		if (TryGetPrivateBindingAccountDisplayIdentity(
				identity,
				subscriptionContextSeparator,
				includeSubscriptionContext,
				includePlanTier,
				out string? displayIdentity))
		{
			return displayIdentity!;
		}

		if (IsGrok && !string.IsNullOrWhiteSpace(identity))
		{
			return "已連接的 Grok 帳號";
		}

		if (IsCopilot && !string.IsNullOrWhiteSpace(identity))
		{
			return "已連接的 GitHub 帳號";
		}

		if (IsClaude &&
			ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				identity,
				out _))
		{
			return "已連接的 Claude 訂閱範圍";
		}

		if (IsCodex &&
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				identity,
				out _))
		{
			return "已連接的 Codex workspace";
		}

		return IsAntigravity &&
			string.Equals(
				identity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase)
			? "目前登入的 Antigravity 帳號"
			: identity ?? string.Empty;
	}

	private string GetProviderAccountCardDisplayText(
		string? identity,
		string subscriptionContextSeparator = " · ",
		bool includeSubscriptionContext = true,
		bool includePlanTier = true)
	{
		if (IsAntigravity &&
			!string.IsNullOrWhiteSpace(_antigravityReportedAccountEmail) &&
			string.Equals(
				identity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase))
		{
			return _isAntigravityReportedAccountEmailStale
				? $"{_antigravityReportedAccountEmail}（上次確認）"
				: _antigravityReportedAccountEmail;
		}

		return GetProviderAccountIdentityDisplayText(
			identity,
			subscriptionContextSeparator,
			includeSubscriptionContext,
			includePlanTier);
	}

	private bool TryGetPrivateBindingAccountDisplayIdentity(
		string? identity,
		string subscriptionContextSeparator,
		bool includeSubscriptionContext,
		bool includePlanTier,
		out string? displayIdentity)
	{
		displayIdentity = null;

		if ((!IsClaude && !IsCodex && !IsCopilot && !IsGrok) ||
			(CurrentSnapshot is not UsageSnapshot currentSnapshot) ||
			!CanUsePrivateBindingDisplay(currentSnapshot))
		{
			return false;
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				identity,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null) ||
			!ProviderAccountIdentityRules.TryNormalize(
				currentSnapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				normalizedIdentity,
				snapshotIdentity) ||
			!ProviderAccountIdentityRules.TryNormalize(
				currentSnapshot.ProviderAccountDisplayIdentity,
				out displayIdentity) ||
			(displayIdentity is null))
		{
			return false;
		}

		if (IsClaude &&
			ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				normalizedIdentity,
				out string? publicBindingIdentity) &&
			(publicBindingIdentity is not null))
		{
			if (includeSubscriptionContext)
			{
				string scopeDisplayName = string.IsNullOrWhiteSpace(
					currentSnapshot.SubscriptionScopeDisplayName)
					? $"範圍 {publicBindingIdentity[..8].ToUpperInvariant()}"
					: currentSnapshot.SubscriptionScopeDisplayName;
				displayIdentity =
					$"{displayIdentity}{subscriptionContextSeparator}{scopeDisplayName}";
			}

			if (includePlanTier &&
				!string.IsNullOrWhiteSpace(currentSnapshot.PlanTier))
			{
				displayIdentity =
					$"{displayIdentity} · 方案 {currentSnapshot.PlanTier}";
			}
		}
		else if (IsCodex &&
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				normalizedIdentity,
				out string? codexPublicBindingIdentity) &&
			(codexPublicBindingIdentity is not null))
		{
			if (includeSubscriptionContext)
			{
				displayIdentity =
					$"{displayIdentity}{subscriptionContextSeparator}進階 workspace · 本機連接碼 {codexPublicBindingIdentity[..8].ToUpperInvariant()}";
			}

			if (includePlanTier &&
				!string.IsNullOrWhiteSpace(currentSnapshot.PlanTier))
			{
				displayIdentity =
					$"{displayIdentity} · 方案 {currentSnapshot.PlanTier}";
			}
		}
		else if (IsCopilot &&
			includePlanTier &&
			!string.IsNullOrWhiteSpace(currentSnapshot.PlanTier))
		{
			displayIdentity =
				$"{displayIdentity} · 方案 {currentSnapshot.PlanTier}";
		}

		return true;
	}

	private bool CanUsePrivateBindingDisplay(UsageSnapshot snapshot)
	{
		return IsGrok
			? HasUsableUsage(snapshot) ||
				snapshot.SubscriptionVerificationState is
					SubscriptionVerificationState.Verified or
					SubscriptionVerificationState.TransientProbeError or
					SubscriptionVerificationState.UsageUnavailable
			: snapshot.SubscriptionVerificationState is
				SubscriptionVerificationState.Verified or
				SubscriptionVerificationState.TransientProbeError or
				SubscriptionVerificationState.UsageUnavailable;
	}

	private bool CanDisplayStandardCodexSubscriptionPlan(
		UsageSnapshot snapshot)
	{
		if (!IsCodex ||
			!HasUsableUsage(snapshot) ||
			((snapshot.SourceTrust != SourceTrust.Official) &&
				(snapshot.SourceTrust != SourceTrust.OfficialExperimental)) ||
			(snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Unverified) ||
			!ProviderAccountIdentityRules.TryNormalize(
				ProviderAccountIdentity,
				out string? accountIdentity) ||
			(accountIdentity is null) ||
			Guid.TryParse(accountIdentity, out _) ||
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				accountIdentity,
				out _) ||
			!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountDisplayIdentity,
				out string? displayIdentity) ||
			(displayIdentity is null))
		{
			return false;
		}

		return ProviderAccountIdentityRules.Comparer.Equals(
				accountIdentity,
				snapshotIdentity) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				accountIdentity,
				displayIdentity);
	}

	private bool CanDisplayCopilotSubscriptionPlan(UsageSnapshot snapshot)
	{
		bool hasUsablePlanEvidence =
			HasUsableUsage(snapshot) &&
			((snapshot.SourceTrust == SourceTrust.Official) ||
				(snapshot.SourceTrust == SourceTrust.OfficialExperimental)) &&
			(snapshot.SubscriptionVerificationState ==
				SubscriptionVerificationState.Verified);
		bool hasUnavailableQuotaPlanEvidence =
			(snapshot.Status == SnapshotStatus.Unsupported) &&
			(snapshot.Metrics.Count == 0) &&
			(snapshot.SourceTrust == SourceTrust.Unavailable) &&
			(snapshot.SubscriptionVerificationState ==
				SubscriptionVerificationState.UsageUnavailable);

		return IsCopilot &&
			(hasUsablePlanEvidence || hasUnavailableQuotaPlanEvidence) &&
			(CopilotPlanTierRules.CreateDisplayText(snapshot.PlanTier) is not null) &&
			CopilotAccountIdentityRules.TryParse(
				ProviderAccountIdentity,
				out string? accountIdentity) &&
			CopilotAccountIdentityRules.TryParse(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) &&
			CopilotAccountIdentityRules.AreEquivalent(
				accountIdentity,
				snapshotIdentity) &&
			!string.IsNullOrWhiteSpace(
				snapshot.ProviderAccountDisplayIdentity);
	}

	private bool CanDisplayAntigravitySubscriptionPlan(
		UsageSnapshot snapshot)
	{
		return IsAntigravity &&
			(snapshot.Status == SnapshotStatus.Ready) &&
			(snapshot.Metrics.Count > 0) &&
			(snapshot.SourceTrust == SourceTrust.OfficialExperimental) &&
			CanPersistCurrentSnapshot &&
			IsUsingAntigravityReportedAccountEmail() &&
			!_isAntigravityReportedAccountEmailStale &&
			string.Equals(
				snapshot.ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase) &&
			AntigravityStatusLineCapture.TryNormalizePlanTier(
				snapshot.PlanTier,
				out string? normalizedPlanTier) &&
			(normalizedPlanTier is not null);
	}

	private bool IsUsingAntigravityReportedAccountEmail()
	{
		return IsAntigravity &&
			!string.IsNullOrWhiteSpace(_antigravityReportedAccountEmail) &&
			string.Equals(
				ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase);
	}

	private void SetField<T>(
		ref T field,
		T value,
		[CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return;
		}

		field = value;
		OnPropertyChanged(propertyName);
	}
}
