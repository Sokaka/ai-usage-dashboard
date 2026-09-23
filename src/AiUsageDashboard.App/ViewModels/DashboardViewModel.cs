using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.App.ViewModels;

internal enum AuthenticatedProviderBindingCommitResult
{
	Succeeded,
	IdentityConflict,
	IdentityConflictRuntimeFailClosed,
	PersistenceFailed,
	PrivateBindingRollbackFailed,
	InvalidState
}

internal enum AccountRefreshProgress
{
	Queued,
	Started
}

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
	private enum RetainedPrivateAccountCleanupDisposition
	{
		DestructiveCleanupAllowed,
		CompleteWithoutDestruction,
		RetryWithoutDestruction
	}

	private enum RefreshRequestScope
	{
		None,
		All,
		Account,
		ProviderInvalidation,
		UsageSafetyRevalidation
	}

	private enum AntigravitySetupProcessActivity
	{
		Inactive,
		Active,
		Unknown
	}

	private enum AntigravitySetupIntentResolution
	{
		Pending,
		ReadyToCommit,
		Discarded
	}

	private sealed record AntigravityPendingTargetResolution(
		AntigravitySetupIntentResolution Resolution,
		string? TargetProviderAccountIdentity = null);

	private sealed class AccountRefreshActivity
	{
		internal int Count { get; set; }

		internal TaskCompletionSource<bool> IdleCompletion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed record PortableSettingsImportUndoState(
		PortableSettingsSnapshot PreviousSettings,
		PortableSettingsSnapshot ImportedSettings,
		long ImportedSettingsRevision);

	private sealed record PrivateAccountCleanupIntentRollbackState(
		Guid AccountId,
		ProviderKind Provider,
		bool WasPreviouslyBlocked,
		AccountCleanupPendingWork? PreviousPendingWork);

	private sealed record AntigravityReportedAccountRefreshAssociation(
		long BaselineObservationGeneration,
		DateTimeOffset RefreshStartedAtUtc,
		bool IsBaselineAvailable);

	public event PropertyChangedEventHandler? PropertyChanged;

	private const string InactiveAntigravityAccountMessage =
		"Antigravity 目前一次只能檢查一個帳號的用量。請停止其他 Antigravity 帳號的用量檢查或移除帳號；不必重新登入。";
	private const string ProviderAccountIdentityConflictMessage =
		"這個登入帳號已連接到另一張帳號卡片。已忽略結果；請改用其他帳號。";
	private const string ProviderAccountIdentityPersistenceFailureMessage =
		"無法儲存這次連接的帳號資訊。已忽略結果；稍後會自動再試。";
	private const string AntigravitySetupApprovalMismatchMessage =
		"這次用量檢查的登入帳號與剛確認的 Antigravity 帳號不同。已忽略結果；請重新連接正確帳號。";
	private const string AuthenticatedProviderBindingPersistenceFailureMessage =
		"多次嘗試後仍無法儲存這次帳號連接。請確認 AI Usage 設定資料夾可寫入，再重新連接帳號。";
	private const string AuthenticatedProviderConflictRuntimeFailClosedMessage =
		"無法儲存帳號衝突後的狀態。這次已停止檢查這個帳號，也不會顯示上次用量；重新啟動後可能再次顯示原本的連接資料。請確認 AI Usage 設定資料夾可寫入，再重新連接帳號。";
	private const string GrokConnectionRecoveryPendingMessage =
		"Grok 帳號連接尚未完成。重新啟動 AI Usage 後會繼續；若仍顯示此訊息，請重新連接該帳號。";
	private const string OfficialAntigravityCacheCleanupFailureMessage =
		"Antigravity 帳號已確認，但切換前的上次用量資料仍待清除。稍後會自動再試，重新啟動後也會繼續；不需要重新連接。";
	private const string OfficialAntigravityConnectionPendingMessage =
		"Antigravity 帳號已確認，但連接資料仍待儲存。稍後會自動再試，重新啟動後也會繼續；不需要重新連接。";
	private const string OfficialAntigravityConnectionPendingInvalidDataMessage =
		"儲存的 Antigravity 連接進度已損壞或由不支援的 AI Usage 版本建立。為避免連接錯誤帳號，AI Usage 已暫停 Antigravity 用量檢查與新連接。請先更新 AI Usage；若仍未恢復，請保留設定資料並聯絡維護人員。";
	private const string OfficialAntigravityConnectionJournalUnavailableMessage =
		"暫時無法讀寫 Antigravity 連接進度。AI Usage 已暫停新的連接，稍後會自動再試；若持續發生，請確認設定資料夾可寫入。";
	private const string OfficialAntigravitySetupCompletionPendingMessage =
		"AI Usage 正在確認先前的 Antigravity 連接是否已完成。稍後會自動再試，重新啟動後也會繼續；不需要重新操作。";
	private const string ProviderAccountCacheCleanupFailureMessage =
		"無法清除切換前的上次用量資料；這些資料不會顯示在目前帳號。";
	private const string ClaudeLegacyCacheRetentionFailureMessage =
		"Claude 訂閱範圍已連接，但無法持久保留你確認歸屬於此訂閱範圍的舊版用量；本次執行期間仍會保留作為暫態失敗的備援。";
	private const string ClaudeBindingInventoryCleanupFailureMessage =
		"無法確認或清理未配對的 Claude 訂閱連接資料；本次啟動不會顯示 Claude 上次用量，重新連接前也會持續安全檢查。請確認 AI Usage 設定資料夾可寫入。";
	private const string CodexBindingInventoryCleanupFailureMessage =
		"無法確認或清理未配對的 Codex workspace 連接資料；本次啟動不會顯示 Codex 上次用量，重新連接前也會持續安全檢查。請確認 AI Usage 設定資料夾可寫入。";
	private const string AccountCleanupPendingFailureMessage =
		"部分帳號的舊資料仍待清理。稍後會自動再試，重新啟動後也會繼續；完成前不會檢查這些帳號的用量。";
	private const string AccountCleanupPendingInvalidDataFailureMessage =
		"儲存的帳號清理進度已損壞或由不支援的 AI Usage 版本建立。所有帳號的用量檢查已暫停，且無法自動修復。請更新 AI Usage；若仍未恢復，請保留設定資料並聯絡維護人員。";
	private const int MaximumAccountCount = 256;
	private const int MaximumDisplayNameLength = 80;
	private const int MaximumAntigravityReportedAccountRemovalAttempts = 3;
	private const int MaximumAccountProfileSaveAttempts = 3;
	private const int MaximumUsageCacheDeleteAttempts = 3;
	private static readonly TimeSpan DefaultAccountRefreshQuiesceTimeout =
		TimeSpan.FromSeconds(3);
	private static readonly TimeSpan AntigravitySetupLaunchRegistrationGrace =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan AntigravitySetupLaunchClockSkewTolerance =
		TimeSpan.FromMinutes(1);
	private static readonly TimeSpan AntigravityReportedAccountAssociationWindow =
		AntigravityVerifiedAccountBinding.MaximumSourceQuotaAssociationLag;
	private static readonly TimeSpan AntigravityReportedAccountFreshness =
		TimeSpan.FromMinutes(15);
	private static readonly TimeSpan AntigravityReportedAccountFutureTolerance =
		TimeSpan.FromMinutes(5);
	private static readonly TimeSpan[] UsageCacheDeleteRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private static readonly TimeSpan[] AccountProfileSaveRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private static readonly TimeSpan[] BackgroundPersistenceRetryDelays =
	[
		TimeSpan.FromMinutes(1),
		TimeSpan.FromMinutes(2),
		TimeSpan.FromMinutes(4),
		TimeSpan.FromMinutes(8),
		TimeSpan.FromMinutes(15)
	];
	internal static readonly TimeSpan MaximumAntigravityReportedAccountReadDuration =
		TimeSpan.FromSeconds(10);
	private const string NoRefreshTargetsMessage = "沒有可檢查用量的帳號";
	private readonly Dictionary<Guid, AccountRefreshActivity>
		_accountRefreshActivities = new();
	private readonly List<AccountProfile> _accountProfiles = new();
	private readonly SemaphoreSlim _accountMutationGate = new(1, 1);
	private readonly HashSet<Guid>
		_providerAccountCacheCleanupFailureAccountIds = new();
	private readonly object _providerAccountCacheCleanupWarningSync = new();
	private readonly HashSet<Guid> _runtimeOnlyCachedProviderAccountIdentityAccountIds =
		new();
	private readonly Dictionary<
		Guid,
		(long LifecycleRevision, long OperationId, Task Task)>
		_usageSafetyRevalidations = new();
	private readonly IAccountProfileStore _accountProfileStore;
	private readonly IAccountCleanupPendingStore? _accountCleanupPendingStore;
	private readonly IAntigravityConnectionPendingStore?
		_antigravityConnectionPendingStore;
	private readonly IGrokConnectionPendingStore? _grokConnectionPendingStore;
	private readonly IAccountRuntimeStatePurger? _accountRuntimeStatePurger;
	private readonly IAntigravityReportedAccountSource?
		_antigravityReportedAccountSource;
	private readonly IAntigravityVerifiedAccountBindingStore?
		_antigravityVerifiedAccountBindingStore;
	private readonly IClaudeAccountBindingStore? _claudeAccountBindingStore;
	private readonly ClaudeBindingCommitGate? _claudeBindingCommitGate;
	private readonly ICodexWorkspaceBindingStore? _codexWorkspaceBindingStore;
	private readonly CodexBindingCommitGate? _codexBindingCommitGate;
	private readonly IGrokAccountBindingStore? _grokAccountBindingStore;
	private readonly GrokBindingCommitGate? _grokBindingCommitGate;
	private readonly IDashboardPreferencesStore? _dashboardPreferencesStore;
	private readonly IPortableSettingsImportTransaction?
		_portableSettingsImportTransaction;
	private readonly IUsageSnapshotStore? _usageSnapshotStore;
	private readonly IUsageRefreshCoordinator _usageRefreshCoordinator;
	private readonly Action<string, string, Exception?> _reportDiagnostic;
	private readonly TimeSpan _accountRefreshQuiesceTimeout;
	private readonly TimeProvider _timeProvider;
	private readonly object _refreshSync = new();
	private readonly object _cleanupStateSync = new();
	private readonly object _backgroundPersistenceRetrySync = new();
	private readonly HashSet<(Guid AccountId, ProviderKind Provider)>
		_cleanupBlockedAccountKeys = new();
	private readonly HashSet<(Guid AccountId, ProviderKind Provider)>
		_retainedPrivateStateDeletionAuthorizedAccountKeys = new();
	private readonly HashSet<Guid>
		_grokConnectionPendingCleanupProtectedAccountIds = new();
	private readonly Dictionary<Guid, AntigravityConnectionPendingWork>
		_pendingAntigravityConnectionWork = new();
	private readonly HashSet<Guid>
		_unjournaledAntigravityConnectionWorkAccountIds = new();
	private Guid? _activeRefreshAccountId;
	private long _activeRefreshLifecycleRevision = -1;
	private RefreshRequestScope _activeRefreshScope;
	private Task? _activeRefreshTask;
	private int _activeRefreshOperationCount;
	private bool _canManageAccounts = true;
	private bool _blockAllAccountsForCleanup;
	private int _accountCleanupRetryFailureCount;
	private DateTimeOffset _accountCleanupRetryNotBeforeUtc =
		DateTimeOffset.MinValue;
	private int _providerIdentityPersistenceRetryFailureCount;
	private DateTimeOffset _providerIdentityPersistenceRetryNotBeforeUtc =
		DateTimeOffset.MinValue;
	private int _antigravityConnectionRetryFailureCount;
	private DateTimeOffset _antigravityConnectionRetryNotBeforeUtc =
		DateTimeOffset.MinValue;
	private bool _isManagingAccounts;
	private bool _isRefreshing;
	private bool _isRefreshStopped;
	private volatile bool _isUpdateShutdownReserved;
	private bool _isAccountMutationGateHeldForUpdateShutdown;
	private bool _wereStoppedRefreshesInvalidated;
	private bool _isAntigravityReportedAccountRemovalComplete;
	private bool _isAntigravityConnectionPendingWorkHydrated;
	private bool _isAntigravityConnectionPendingStoreHydrationBlocked;
	private bool _isAntigravitySetupArtifactCleanupPending = true;
	private int _antigravityReportedAccountRemovalAttemptCount;
	private DateTimeOffset _nextAntigravityReportedAccountRemovalAttemptUtc =
		DateTimeOffset.MinValue;
	private bool _shouldClearAccountSettingsHealthAfterSuccessfulSave;
	private long _portableSettingsRevision;
	private long _nextUsageSafetyRevalidationOperationId;
	private PortableSettingsImportUndoState? _lastPortableSettingsImportUndoState;
	private string _accountSettingsHealthMessage = string.Empty;
	private string _accountSettingsMessage = "正在載入帳號設定…";
	private CompactRefreshStatus _compactRefreshStatus =
		CompactRefreshStatus.Hidden;
	private string _lastRefreshText = "尚未檢查";
	private UsageDisplayMode _usageDisplayMode = UsageDisplayMode.Used;
	private UsageSortMode _sortMode = UsageSortMode.Manual;
	private bool _isDisposed;

	public ObservableCollection<AccountUsageViewModel> Accounts { get; }

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		(_antigravityReportedAccountSource as IDisposable)?.Dispose();
	}

	public bool CanManageAccounts
	{
		get => _canManageAccounts;
		private set
		{
			if (_canManageAccounts == value)
			{
				return;
			}

			_canManageAccounts = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanStartAccountManagement));
			OnPropertyChanged(nameof(CanUndoLastPortableSettingsImport));
			OnPropertyChanged(nameof(EmptyAccountsTitle));
			OnPropertyChanged(nameof(EmptyAccountsMessage));
			UpdateAccountMoveAvailability();
		}
	}

	public bool CanStartAccountManagement => CanManageAccounts &&
		!IsManagingAccounts &&
		!_isUpdateShutdownReserved;

	public bool CanUndoLastPortableSettingsImport =>
		CanStartAccountManagement &&
		(_lastPortableSettingsImportUndoState is
			PortableSettingsImportUndoState undoState) &&
		(_portableSettingsRevision == undoState.ImportedSettingsRevision) &&
		IsCurrentPortableSettingsState(
			undoState.ImportedSettings,
			undoState.ImportedSettings.WidgetPreferences);

	public bool CanChangeSortMode => !IsManagingAccounts &&
		!_isUpdateShutdownReserved;

	public bool CanChangeUsageDisplayMode => !IsManagingAccounts &&
		!_isUpdateShutdownReserved;

	public string EmptyAccountsTitle => CanManageAccounts
		? "尚未新增帳號"
		: "帳號設定目前無法載入";

	public string EmptyAccountsMessage => CanManageAccounts
		? "選擇「新增帳號」開始設定。"
		: "設定檔可能由較新版本建立或目前無法讀取；原檔不會被覆寫。";

	public bool HasAccounts => Accounts.Count > 0;

	public bool HasNoAccounts => !HasAccounts;

	public bool CanRefresh => !_isRefreshStopped &&
		!_isUpdateShutdownReserved &&
		!IsRefreshing &&
		!IsManagingAccounts &&
		Accounts.Any(account =>
			account.CanRefreshUsage &&
			!IsAccountCleanupBlocked(account.Id, account.Provider));

	public bool IsRefreshing
	{
		get => _isRefreshing;
		private set
		{
			if (_isRefreshing == value)
			{
				return;
			}

			_isRefreshing = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanRefresh));
			OnPropertyChanged(nameof(RefreshActionText));
		}
	}

	public bool IsAutomaticUsageSorting => SortMode == UsageSortMode.Automatic;

	public bool IsShowingRemainingUsage =>
		DisplayMode == UsageDisplayMode.Remaining;

	public bool IsManagingAccounts
	{
		get => _isManagingAccounts;
		private set
		{
			if (_isManagingAccounts == value)
			{
				return;
			}

			_isManagingAccounts = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanStartAccountManagement));
			OnPropertyChanged(nameof(CanUndoLastPortableSettingsImport));
			OnPropertyChanged(nameof(CanChangeSortMode));
			OnPropertyChanged(nameof(CanChangeUsageDisplayMode));
			OnPropertyChanged(nameof(CanRefresh));
			UpdateAccountMoveAvailability();
		}
	}

	public string AccountSettingsMessage
	{
		get => _accountSettingsMessage;
		private set => SetField(ref _accountSettingsMessage, value);
	}

	public string AccountSettingsHealthMessage
	{
		get => _accountSettingsHealthMessage;
		private set
		{
			if (_accountSettingsHealthMessage == value)
			{
				return;
			}

			_accountSettingsHealthMessage = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasAccountSettingsHealthMessage));
		}
	}

	public bool HasAccountSettingsHealthMessage =>
		!string.IsNullOrWhiteSpace(AccountSettingsHealthMessage);

	public CompactRefreshStatus CompactRefreshStatus
	{
		get => _compactRefreshStatus;
		private set
		{
			_compactRefreshStatus = value;
			// A repeated completion must restart the floating widget's timer.
			OnPropertyChanged();
		}
	}

	public string LastRefreshText
	{
		get => _lastRefreshText;
		private set => SetField(ref _lastRefreshText, value);
	}

	public string RefreshActionText => IsRefreshing ? "正在檢查…" : "檢查用量";

	public UsageSortMode SortMode => _sortMode;

	public string SortModeActionText => IsAutomaticUsageSorting
		? "切換為手動排序"
		: "切換為自動排序";

	public string SortModeText => IsAutomaticUsageSorting
		? "排序：自動"
		: "排序：手動";

	public string SortModeToolTip => IsAutomaticUsageSorting
		? "依服務、主要用量週期與重置時間排序，不依用量百分比排序。按一下切換為手動。"
		: "保留自訂帳號順序。按一下切換為自動。";

	public UsageDisplayMode DisplayMode => _usageDisplayMode;

	public string UsageDisplayModeActionText => IsShowingRemainingUsage
		? "改為顯示已使用量"
		: "改為顯示剩餘用量";

	public string UsageDisplayModeText => IsShowingRemainingUsage
		? "顯示：剩餘"
		: "顯示：已使用";

	public string UsageDisplayModeToolTip => IsShowingRemainingUsage
		? "目前顯示剩餘用量。按一下改為已使用。"
		: "目前顯示已使用量。按一下改為剩餘。";

	public DashboardViewModel(
		IAccountProfileStore accountProfileStore,
		IUsageRefreshCoordinator usageRefreshCoordinator,
		IUsageSnapshotStore? usageSnapshotStore = null,
		IDashboardPreferencesStore? dashboardPreferencesStore = null)
		: this(
			accountProfileStore,
			usageRefreshCoordinator,
			usageSnapshotStore,
			dashboardPreferencesStore,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: null,
			timeProvider: null)
	{
	}

	internal DashboardViewModel(
		IAccountProfileStore accountProfileStore,
		IUsageRefreshCoordinator usageRefreshCoordinator,
		IUsageSnapshotStore? usageSnapshotStore,
		IDashboardPreferencesStore? dashboardPreferencesStore,
		IAccountRuntimeStatePurger? accountRuntimeStatePurger,
		IAntigravityReportedAccountSource?
			antigravityReportedAccountSource = null,
		TimeProvider? timeProvider = null,
		IPortableSettingsImportTransaction?
			portableSettingsImportTransaction = null,
		TimeSpan? accountRefreshQuiesceTimeout = null,
		IAntigravityVerifiedAccountBindingStore?
			antigravityVerifiedAccountBindingStore = null,
		IAccountCleanupPendingStore? accountCleanupPendingStore = null,
		IAntigravityConnectionPendingStore?
			antigravityConnectionPendingStore = null,
		IGrokConnectionPendingStore? grokConnectionPendingStore = null,
		Action<string, string, Exception?>? reportDiagnostic = null,
		IClaudeAccountBindingStore? claudeAccountBindingStore = null,
		ClaudeBindingCommitGate? claudeBindingCommitGate = null,
		ICodexWorkspaceBindingStore? codexWorkspaceBindingStore = null,
		CodexBindingCommitGate? codexBindingCommitGate = null,
		IGrokAccountBindingStore? grokAccountBindingStore = null,
		GrokBindingCommitGate? grokBindingCommitGate = null)
	{
		_accountProfileStore = accountProfileStore ??
			throw new ArgumentNullException(nameof(accountProfileStore));
		_usageRefreshCoordinator = usageRefreshCoordinator ??
			throw new ArgumentNullException(nameof(usageRefreshCoordinator));
		_reportDiagnostic = reportDiagnostic ?? ((_, _, _) => { });
		_usageSnapshotStore = usageSnapshotStore;
		_dashboardPreferencesStore = dashboardPreferencesStore;
		_accountRuntimeStatePurger = accountRuntimeStatePurger;
		_accountCleanupPendingStore = accountCleanupPendingStore;
		_antigravityConnectionPendingStore =
			antigravityConnectionPendingStore;
		_grokConnectionPendingStore = grokConnectionPendingStore;
		_claudeAccountBindingStore = claudeAccountBindingStore;
		_claudeBindingCommitGate = claudeAccountBindingStore is null
			? null
			: claudeBindingCommitGate ??
				throw new ArgumentNullException(nameof(claudeBindingCommitGate));
		_codexWorkspaceBindingStore = codexWorkspaceBindingStore;
		_codexBindingCommitGate = codexWorkspaceBindingStore is null
			? null
			: codexBindingCommitGate ??
				throw new ArgumentNullException(nameof(codexBindingCommitGate));
		_grokAccountBindingStore = grokAccountBindingStore;
		_grokBindingCommitGate = grokAccountBindingStore is null
			? null
			: grokBindingCommitGate ??
				throw new ArgumentNullException(nameof(grokBindingCommitGate));
		_isAntigravityConnectionPendingStoreHydrationBlocked =
			_antigravityConnectionPendingStore is not null;
		_antigravityReportedAccountSource =
			antigravityReportedAccountSource;
		_antigravityVerifiedAccountBindingStore =
			antigravityVerifiedAccountBindingStore;
		_timeProvider = timeProvider ?? TimeProvider.System;
		_portableSettingsImportTransaction =
			portableSettingsImportTransaction;
		_accountRefreshQuiesceTimeout = accountRefreshQuiesceTimeout ??
			DefaultAccountRefreshQuiesceTimeout;

		if (_accountRefreshQuiesceTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(accountRefreshQuiesceTimeout));
		}
		Accounts = new ObservableCollection<AccountUsageViewModel>();
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			UsageSortMode sortMode = _dashboardPreferencesStore is null
				? UsageSortMode.Manual
				: await _dashboardPreferencesStore.LoadUsageSortModeAsync(
					cancellationToken);
			UsageDisplayMode displayMode = _dashboardPreferencesStore is null
				? UsageDisplayMode.Used
				: await _dashboardPreferencesStore.LoadUsageDisplayModeAsync(
					cancellationToken);
			SetSortMode(sortMode);
			SetUsageDisplayMode(displayMode);
			AccountProfileLoadResult loadResult = await _accountProfileStore.LoadAsync(
				cancellationToken);
			CanManageAccounts = loadResult.CanSave;
			ApplyProfiles(loadResult.Accounts);
			AccountSettingsHealthMessage =
				CreateAccountSettingsHealthMessage(loadResult);
			bool isAccountProfileInventoryAuthoritative =
				(loadResult.Status is AccountProfileLoadStatus.Loaded or
					AccountProfileLoadStatus.Missing) ||
				((loadResult.Status ==
					AccountProfileLoadStatus.RecoveredCorruptFile) &&
					loadResult.Accounts.Count > 0);
			bool isClaudeBindingInventoryHealthy =
				isAccountProfileInventoryAuthoritative &&
				await CleanupUnpairedClaudeBindingsAsync(cancellationToken);
			bool isCodexBindingInventoryHealthy =
				isAccountProfileInventoryAuthoritative &&
				await CleanupUnpairedCodexBindingsAsync(cancellationToken);
			await RecoverPendingAccountCleanupsAsync(
				cancellationToken,
				deferDestructiveGrokCleanup: true);
			await RecoverPendingAntigravityConnectionsAsync(
				cancellationToken,
				allowProviderRefresh: false);
			await RestoreCachedSnapshotsAsync(
				Accounts.Where(account => account.IsEnabled &&
					(isClaudeBindingInventoryHealthy || !account.IsClaude) &&
					(isCodexBindingInventoryHealthy || !account.IsCodex)).ToArray(),
				cancellationToken);
			_shouldClearAccountSettingsHealthAfterSuccessfulSave =
				(loadResult.Status == AccountProfileLoadStatus.Loaded) &&
				loadResult.CanSave &&
				!string.IsNullOrWhiteSpace(loadResult.Message);
			AccountSettingsMessage = string.Empty;
			UpdateRefreshAvailabilityText();
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<bool> TryBeginProviderAccountRecoveryAsync(
		AccountUsageViewModel account,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);

		await _accountMutationGate.WaitAsync(cancellationToken);
		try
		{
			if (_isUpdateShutdownReserved ||
				!account.IsGrok ||
				!Accounts.Contains(account) ||
				!_accountProfiles.Any(profile =>
					(profile.Id == account.Id) &&
					(profile.Provider == account.Provider)) ||
				!account.IsEnabled ||
				account.IsProviderAccountChangeInProgress ||
				IsAccountCleanupBlocked(account.Id, account.Provider))
			{
				return false;
			}

			account.BeginProviderAccountChange();
			return account.IsProviderAccountChangeInProgress;
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal bool TryBeginProviderAccountChange(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		lock (_refreshSync)
		{
			if (_isUpdateShutdownReserved)
			{
				return false;
			}

			account.BeginProviderAccountChange();
			return account.IsProviderAccountChangeInProgress;
		}
	}

	internal async Task RestoreDeferredStartupDisplayStateAsync(
		CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			await RestoreAntigravityReportedAccountDisplayAsync(
				cancellationToken);
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<bool> CompleteDeferredStartupAccountCleanupsAsync(
		IReadOnlySet<Guid>? protectedGrokAccountIds = null,
		CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			if (protectedGrokAccountIds is not null)
			{
				_grokConnectionPendingCleanupProtectedAccountIds.Clear();
				_grokConnectionPendingCleanupProtectedAccountIds.UnionWith(
					protectedGrokAccountIds);
			}

			return await RecoverPendingAccountCleanupsAsync(
				cancellationToken,
				requireRefreshQuiescence: true,
				includeUnjournaledBlockedAccounts: true);
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal Task<PortableSettingsSnapshot> CreatePortableSettingsSnapshotAsync(
		CancellationToken cancellationToken = default)
	{
		return CreatePortableSettingsSnapshotCoreAsync(
			widgetPreferences: null,
			cancellationToken);
	}

	internal Task<PortableSettingsSnapshot> CreatePortableSettingsSnapshotAsync(
		PortableWidgetPreferences widgetPreferences,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(widgetPreferences);
		ValidatePortableWidgetPreferences(widgetPreferences);
		return CreatePortableSettingsSnapshotCoreAsync(
			widgetPreferences,
			cancellationToken);
	}

	private async Task<PortableSettingsSnapshot>
		CreatePortableSettingsSnapshotCoreAsync(
		PortableWidgetPreferences? widgetPreferences,
		CancellationToken cancellationToken)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			EnsureAccountManagementAvailable();
			return new PortableSettingsSnapshot(
				_accountProfiles.ToArray(),
				SortMode,
				DisplayMode,
				widgetPreferences);
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal void NotifyPortableWidgetPreferencesChanged(
		PortableWidgetPreferences widgetPreferences)
	{
		ArgumentNullException.ThrowIfNull(widgetPreferences);
		ValidatePortableWidgetPreferences(widgetPreferences);
		PortableWidgetPreferences? importedWidgetPreferences =
			_lastPortableSettingsImportUndoState?
				.ImportedSettings.WidgetPreferences;

		if ((importedWidgetPreferences is not null) &&
			!ArePortableWidgetPreferencesEquivalentForUndo(
				importedWidgetPreferences,
				widgetPreferences))
		{
			SetLastPortableSettingsImportUndoState(null);
		}
	}

	internal async Task<DashboardPreferencesRecoveryStatus>
		RecoverDashboardPreferencesAsync(
			IDashboardPreferencesRecoveryStore recoveryStore,
			Func<DashboardShellPreferences> captureShellPreferences,
			Action<DashboardShellPreferences> applyShellPreferences,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(recoveryStore);
		ArgumentNullException.ThrowIfNull(captureShellPreferences);
		ArgumentNullException.ThrowIfNull(applyShellPreferences);
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			DashboardPreferencesSnapshot currentPreferences = new(
				SortMode,
				DisplayMode,
				captureShellPreferences());
			DashboardPreferencesRecoveryPrepareResult prepareResult =
				await recoveryStore.PrepareRecoveryAsync(
					currentPreferences,
					cancellationToken);

			if (prepareResult.Status != DashboardPreferencesRecoveryStatus.Ready)
			{
				return prepareResult.Status;
			}

			long generation = prepareResult.Generation;

			while (true)
			{
				currentPreferences = new DashboardPreferencesSnapshot(
					SortMode,
					DisplayMode,
					captureShellPreferences());
				DashboardPreferencesRecoveryCommitResult commitResult =
					await recoveryStore.CommitRecoveryAsync(
						generation,
						currentPreferences,
						cancellationToken);

				if (commitResult.Status != DashboardPreferencesRecoveryStatus.Ready)
				{
					return commitResult.Status;
				}

				DashboardPreferencesSnapshot recoveredPreferences =
					commitResult.Preferences ??
					throw new InvalidOperationException(
						"沒有可還原的顯示設定。");
				DashboardPreferencesSnapshot latestPreferences = new(
					SortMode,
					DisplayMode,
					captureShellPreferences());

				if (latestPreferences != currentPreferences)
				{
					generation = commitResult.Generation;
					continue;
				}

				SetSortMode(recoveredPreferences.UsageSortMode);
				SetUsageDisplayMode(recoveredPreferences.UsageDisplayMode);
				ApplyAccountDisplayOrder();
				applyShellPreferences(recoveredPreferences.ShellPreferences);

				return await recoveryStore.CompleteRecoveryAsync(
					commitResult.Generation,
					cancellationToken)
					? DashboardPreferencesRecoveryStatus.Ready
					: DashboardPreferencesRecoveryStatus.Pending;
			}
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal Task ReplacePortableSettingsAsync(
		PortableSettingsSnapshot settings,
		CancellationToken cancellationToken = default)
	{
		return ReplacePortableSettingsCoreAsync(
			settings,
			currentShellPreferences: null,
			cancellationToken);
	}

	internal Task ReplacePortableSettingsAsync(
		PortableSettingsSnapshot settings,
		DashboardShellPreferences currentShellPreferences,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(currentShellPreferences);
		return ReplacePortableSettingsCoreAsync(
			settings,
			currentShellPreferences,
			cancellationToken);
	}

	private async Task ReplacePortableSettingsCoreAsync(
		PortableSettingsSnapshot settings,
		DashboardShellPreferences? currentShellPreferences,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(settings);
		IReadOnlyList<AccountProfile> importedProfiles =
			NormalizeImportedProfiles(settings.Accounts);
		AccountProfile[] importedMachineLocalProfiles = importedProfiles
			.Where(profile =>
				(profile.Provider == ProviderKind.Claude) ||
				(profile.Provider == ProviderKind.Codex) ||
				(profile.Provider == ProviderKind.Copilot) ||
				(profile.Provider == ProviderKind.Antigravity) ||
				(profile.Provider == ProviderKind.Grok))
			.ToArray();
		HashSet<(Guid Id, ProviderKind Provider)> importedMachineLocalKeys =
			importedMachineLocalProfiles
				.Select(profile => (profile.Id, profile.Provider))
				.ToHashSet();

		if (!Enum.IsDefined(settings.UsageSortMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(settings),
				settings.UsageSortMode,
				"匯入檔包含未知的用量排序方式。");
		}

		if (!Enum.IsDefined(settings.UsageDisplayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(settings),
				settings.UsageDisplayMode,
				"匯入檔包含未知的用量顯示方式。");
		}

		DashboardShellPreferences? importedShellPreferences = null;
		DashboardShellPreferences? previousShellPreferences = null;
		PortableWidgetPreferences? previousWidgetPreferences = null;

		if (settings.WidgetPreferences is not null)
		{
			ValidatePortableWidgetPreferences(settings.WidgetPreferences);

			if ((currentShellPreferences is null) ||
				(_dashboardPreferencesStore is not IWidgetPreferencesStore))
			{
				throw new InvalidOperationException(
					"目前無法儲存浮窗設定，因此未匯入設定。");
			}

			previousWidgetPreferences = CreatePortableWidgetPreferences(
				currentShellPreferences) with
			{
				Theme = settings.WidgetPreferences.Theme is null
					? null
					: currentShellPreferences.Theme,
				IsHeightFollowingCardCount =
					settings.WidgetPreferences.IsHeightFollowingCardCount is null
						? null
						: currentShellPreferences.IsHeightFollowingCardCount
			};
			previousShellPreferences = currentShellPreferences;
			importedShellPreferences = ApplyPortableWidgetPreferences(
				currentShellPreferences,
				settings.WidgetPreferences);
		}

		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();

			if (IsAntigravityConnectionJournalUnavailable() &&
				(importedProfiles.Any(profile =>
					profile.Provider == ProviderKind.Antigravity) ||
					_accountProfiles.Any(profile =>
					profile.Provider == ProviderKind.Antigravity) ||
					(_pendingAntigravityConnectionWork.Count > 0)))
			{
				throw new InvalidOperationException(
					OfficialAntigravityConnectionJournalUnavailableMessage);
			}

			if (Accounts.Any(account =>
					account.IsProviderAccountChangeInProgress))
			{
				throw new InvalidOperationException(
					"有帳號正在連接中；請先完成或取消連接，再匯入設定。");
			}

			EnsureImportedProfilesDoNotRebindExistingCards(importedProfiles);
			if (importedProfiles.Any(profile =>
					(profile.Provider == ProviderKind.Antigravity) &&
					_pendingAntigravityConnectionWork.ContainsKey(profile.Id)))
			{
				throw new InvalidOperationException(
					"匯入的 Antigravity 帳號仍在取消先前的連接。AI Usage 會自動完成，完成後即可再次匯入。");
			}

			AccountProfile[] previousProfiles = _accountProfiles.ToArray();
			HashSet<(Guid AccountId, ProviderKind Provider)>
				previousAccountKeys = previousProfiles
					.Select(profile => (profile.Id, profile.Provider))
					.ToHashSet();
			UsageSortMode previousSortMode = SortMode;
			UsageDisplayMode previousDisplayMode = DisplayMode;
			AccountProfileStoreException? committedWarning = null;
			PortableSettingsImportCommittedException?
				committedCleanupReceiptWarning = null;
			HashSet<(Guid Id, ProviderKind Provider)> importedAccountKeys =
				importedProfiles
					.Select(profile => (profile.Id, profile.Provider))
					.ToHashSet();
			AccountProfile[] removedProfiles = previousProfiles
				.Where(profile => !importedAccountKeys.Contains(
					(profile.Id, profile.Provider)))
				.ToArray();
			AccountProfile[] localStateCleanupProfiles = removedProfiles
				.Concat(importedMachineLocalProfiles)
				.DistinctBy(profile => (profile.Id, profile.Provider))
				.ToArray();

			async Task PersistImportedSettingsAsync(
				CancellationToken persistenceToken)
			{
				bool preferencesWereSaved = false;

				if (_dashboardPreferencesStore is not null)
				{
					await SavePortablePreferencesAsync(
						settings.UsageSortMode,
						settings.UsageDisplayMode,
						importedShellPreferences,
						persistenceToken);
					preferencesWereSaved = true;
				}

				try
				{
					await _accountProfileStore.SaveAsync(
						importedProfiles,
						persistenceToken);
				}
				catch (AccountProfileStoreException exception) when (
					exception.HasCommittedChanges &&
					(_portableSettingsImportTransaction is null))
				{
					committedWarning = exception;
				}
				catch (Exception importException)
				{
					if (preferencesWereSaved &&
						(_portableSettingsImportTransaction is null))
					{
						try
						{
							await SavePortablePreferencesAsync(
								previousSortMode,
								previousDisplayMode,
								previousShellPreferences,
								CancellationToken.None);
						}
						catch (Exception rollbackException)
						{
							throw new InvalidOperationException(
								"帳號設定未匯入，但無法還原顯示設定。請重新啟動 AI Usage 後確認設定。",
								new AggregateException(
									importException,
									rollbackException));
						}
					}

					throw;
				}
			}

			IReadOnlyList<PrivateAccountCleanupIntentRollbackState>
				privateCleanupRollbackStates = await PersistAccountCleanupIntentAsync(
				localStateCleanupProfiles,
				cancellationToken);

			try
			{
				if (_portableSettingsImportTransaction is null)
				{
					await PersistImportedSettingsAsync(cancellationToken);
				}
				else
				{
					try
					{
						await _portableSettingsImportTransaction.ExecuteAsync(
							PersistImportedSettingsAsync,
							cancellationToken);
					}
					catch (PortableSettingsImportCommittedException exception)
					{
						committedCleanupReceiptWarning = exception;
						AppendAccountSettingsHealthWarning(
							AccountCleanupPendingFailureMessage);
					}
					catch (AccountProfileStoreException exception) when (
						exception.HasCommittedChanges)
					{
						// The account store committed its primary file, but the
						// surrounding portable-settings transaction rolled every
						// target back. Do not let the UI report this as a successful
						// import merely because the inner exception says "committed".
						throw new InvalidOperationException(
							"設定匯入未完成；已還原匯入前的帳號與顯示設定。",
							exception);
					}
				}
			}
			catch
			{
				await RollbackFailedPrivateAccountCleanupIntentAsync(
					privateCleanupRollbackStates,
					previousAccountKeys);
				throw;
			}

			if (committedCleanupReceiptWarning is null)
			{
				await AuthorizeCommittedPortableCopilotCleanupSafelyAsync(
					importedMachineLocalProfiles,
					CancellationToken.None);
			}

			IReadOnlyDictionary<Guid, bool> refreshQuiesceResults =
				await QuiesceAccountRefreshesSafelyAsync(
					localStateCleanupProfiles);

			SetSortMode(settings.UsageSortMode);
			SetUsageDisplayMode(settings.UsageDisplayMode);
			ApplyProfiles(importedProfiles);

			foreach (AccountProfile machineLocalProfile in
				importedMachineLocalProfiles)
			{
				AccountUsageViewModel? account = Accounts.FirstOrDefault(
					candidate =>
						(candidate.Id == machineLocalProfile.Id) &&
						(candidate.Provider == machineLocalProfile.Provider));
				account?.ResetImportedMachineLocalConnectionState();

				lock (_refreshSync)
				{
					_runtimeOnlyCachedProviderAccountIdentityAccountIds.Remove(
						machineLocalProfile.Id);
				}
			}

			SetLastPortableSettingsImportUndoState(
				new PortableSettingsImportUndoState(
					new PortableSettingsSnapshot(
						previousProfiles,
						previousSortMode,
						previousDisplayMode,
						previousWidgetPreferences),
					new PortableSettingsSnapshot(
						importedProfiles.ToArray(),
						settings.UsageSortMode,
						settings.UsageDisplayMode,
						settings.WidgetPreferences),
					_portableSettingsRevision));
			AccountSettingsMessage = settings.WidgetPreferences switch
			{
				null => $"已匯入 {importedProfiles.Count} 個帳號、排序與顯示設定。",
				{ Theme: null } =>
					$"已匯入 {importedProfiles.Count} 個帳號、排序、用量顯示與浮窗設定。",
				_ =>
					$"已匯入 {importedProfiles.Count} 個帳號、排序、用量顯示、浮窗設定與主題。"
			};

			bool wereCachedSnapshotsDeleted =
				committedCleanupReceiptWarning is null;
			bool wereRuntimeStatesPurged =
				committedCleanupReceiptWarning is null;
			bool wereRefreshesQuiesced = true;

			foreach (AccountProfile cleanupProfile in localStateCleanupProfiles)
			{
				if (committedCleanupReceiptWarning is not null)
				{
					continue;
				}

				await DeleteAntigravityVerifiedAccountBindingSafelyAsync(
					cleanupProfile.Id,
					cleanupProfile.Provider,
					CancellationToken.None);
				bool wasRefreshQuiesced = refreshQuiesceResults[cleanupProfile.Id];

				if (!wasRefreshQuiesced)
				{
					wereRefreshesQuiesced = false;
				}

				(bool wasCachedSnapshotDeleted, bool wasRuntimeStatePurged) =
					await AttemptAccountCleanupAsync(
					cleanupProfile.Id,
					cleanupProfile.Provider,
					wasRefreshQuiesced,
					cachePending: true,
					runtimePending: true,
					CancellationToken.None,
					allowRetainedPrivateStateDeletion:
						cleanupProfile.Provider == ProviderKind.Copilot);
				wereCachedSnapshotsDeleted &= wasCachedSnapshotDeleted;
				wereRuntimeStatesPurged &= wasRuntimeStatePurged;
			}

			await RestoreCachedSnapshotsAsync(
				Accounts.Where(account =>
					account.IsEnabled &&
					!importedMachineLocalKeys.Contains(
						(account.Id, account.Provider)))
					.ToArray(),
				CancellationToken.None);
			await RefreshAntigravityReportedAccountDisplayAsync(
				CancellationToken.None);

			if (importedMachineLocalProfiles.Any(
				profile => profile.Provider == ProviderKind.Claude))
			{
				AccountSettingsMessage +=
				" Claude 訂閱連接資料不會隨設定匯入，已重設；請逐一重新連接 Claude 帳號。";
			}

			if (importedMachineLocalProfiles.Any(
				profile => profile.Provider == ProviderKind.Codex))
			{
				AccountSettingsMessage +=
					" Codex 連接不會隨設定匯入，已重設。請逐一重新連接 Codex 帳號。使用 workspace 連接時，要重新輸入 workspace ID。";
			}

			if (importedMachineLocalProfiles.Any(
				profile => profile.Provider == ProviderKind.Copilot))
			{
				AccountSettingsMessage +=
					" Copilot credential 不會隨設定匯入，卡片連接已重設；請逐一重新連接 Copilot 帳號。";
			}

			if (importedMachineLocalProfiles.Any(
				profile => profile.Provider == ProviderKind.Antigravity))
			{
				AccountSettingsMessage +=
					" Antigravity 連接屬於這台電腦，已重設；請在這台電腦重新連接 Antigravity 帳號。";
			}

			if (importedMachineLocalProfiles.Any(
				profile => profile.Provider == ProviderKind.Grok))
			{
				AccountSettingsMessage +=
					" Grok 連接資料不會隨設定匯入，已重設；請逐一重新連接 Grok 帳號。";
			}

			if (!wereCachedSnapshotsDeleted)
			{
				AccountSettingsMessage +=
					" 部分帳號的上次用量無法清除；資料可能仍保留在這台電腦。";
			}

			if (!wereRuntimeStatesPurged)
			{
				AccountSettingsMessage +=
					" 多次嘗試後仍無法清除部分帳號的舊資料；資料可能仍保留在這台電腦。";
			}

			if (!wereRefreshesQuiesced)
			{
				AccountSettingsMessage +=
					" 部分舊帳號的用量檢查尚未停止。設定已套用，稍後會自動完成，重新啟動後也會繼續。";
			}

			if ((committedWarning is null) &&
				(committedCleanupReceiptWarning is null) &&
				wereCachedSnapshotsDeleted &&
				wereRuntimeStatesPurged &&
				wereRefreshesQuiesced &&
				!HasBlockedAccountCleanups())
			{
				AccountSettingsHealthMessage = string.Empty;
				_shouldClearAccountSettingsHealthAfterSuccessfulSave = false;
			}

			if (committedCleanupReceiptWarning is not null)
			{
				AccountSettingsMessage +=
					" 匯入後的 private state 清理授權尚未完成；清理工作與交易紀錄已保留，重新啟動後會自動再試。";
				throw committedCleanupReceiptWarning;
			}

			ThrowCommittedSaveWarning(
				committedWarning,
				$"{AccountSettingsMessage} 帳號設定已套用，但備份更新失敗。");
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	internal Task UndoLastPortableSettingsImportAsync(
		CancellationToken cancellationToken = default)
	{
		return UndoLastPortableSettingsImportCoreAsync(
			currentShellPreferences: null,
			cancellationToken);
	}

	internal Task UndoLastPortableSettingsImportAsync(
		DashboardShellPreferences currentShellPreferences,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(currentShellPreferences);
		return UndoLastPortableSettingsImportCoreAsync(
			currentShellPreferences,
			cancellationToken);
	}

	private async Task UndoLastPortableSettingsImportCoreAsync(
		DashboardShellPreferences? currentShellPreferences,
		CancellationToken cancellationToken)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();

			PortableSettingsImportUndoState? undoState =
				_lastPortableSettingsImportUndoState;

			PortableWidgetPreferences? currentWidgetPreferences =
				currentShellPreferences is null
					? null
					: CreatePortableWidgetPreferences(currentShellPreferences);

			if ((undoState is null) ||
				(_portableSettingsRevision != undoState.ImportedSettingsRevision) ||
				!IsCurrentPortableSettingsState(
					undoState.ImportedSettings,
					currentWidgetPreferences))
			{
				SetLastPortableSettingsImportUndoState(null);
				throw new InvalidOperationException(
					"上次匯入後設定已變更，無法再還原。請改用先前匯出的設定檔。");
			}

			if (Accounts.Any(account =>
					account.IsProviderAccountChangeInProgress))
			{
				throw new InvalidOperationException(
					"有帳號正在連接中；請先完成或取消連接，再還原匯入前設定。");
			}

			PortableSettingsSnapshot previousSettings = undoState.PreviousSettings;
			PortableSettingsSnapshot importedSettings = undoState.ImportedSettings;
			DashboardShellPreferences? previousShellPreferences =
				CreateEffectiveShellPreferences(
					currentShellPreferences,
					previousSettings.WidgetPreferences);
			DashboardShellPreferences? importedShellPreferences =
				CreateEffectiveShellPreferences(
					currentShellPreferences,
					importedSettings.WidgetPreferences);
			HashSet<(Guid AccountId, ProviderKind Provider)>
				originalAccountKeys = _accountProfiles
					.Select(profile => (profile.Id, profile.Provider))
					.ToHashSet();
			AccountProfile[] restoredPreviousAccounts = previousSettings.Accounts
				.Select(profile => profile.Provider is
					ProviderKind.Claude or ProviderKind.Codex or
						ProviderKind.Copilot or ProviderKind.Grok
					? profile with { ProviderAccountIdentity = null }
					: profile)
				.ToArray();
			bool didResetRestoredClaudeConnection = previousSettings.Accounts.Any(
				profile => profile.Provider == ProviderKind.Claude);
			bool didResetRestoredCodexConnection = previousSettings.Accounts.Any(
				profile => profile.Provider == ProviderKind.Codex);
			bool didResetRestoredCopilotConnection = previousSettings.Accounts.Any(
				profile => profile.Provider == ProviderKind.Copilot);
			bool didResetRestoredGrokConnection = previousSettings.Accounts.Any(
				profile => profile.Provider == ProviderKind.Grok);
			AccountProfileStoreException? committedWarning = null;
			PortableSettingsImportCommittedException?
				committedCleanupReceiptWarning = null;
			Dictionary<(Guid Id, ProviderKind Provider), AccountProfile>
				previousProfilesByKey = previousSettings.Accounts.ToDictionary(
					profile => (profile.Id, profile.Provider));
			AccountProfile[] localStateCleanupProfiles = importedSettings.Accounts
				.Where(importedProfile =>
					!previousProfilesByKey.TryGetValue(
						(importedProfile.Id, importedProfile.Provider),
						out AccountProfile? previousProfile) ||
					!ProviderAccountIdentityRules.Comparer.Equals(
						importedProfile.ProviderAccountIdentity,
						previousProfile.ProviderAccountIdentity))
				.Concat(previousSettings.Accounts.Where(profile =>
					profile.Provider is ProviderKind.Claude or ProviderKind.Codex or
						ProviderKind.Copilot))
				.DistinctBy(profile => (profile.Id, profile.Provider))
				.ToArray();

			async Task PersistPreviousSettingsAsync(
				CancellationToken persistenceToken)
			{
				bool preferencesWereSaved = false;

				if (_dashboardPreferencesStore is not null)
				{
					await SavePortablePreferencesAsync(
						previousSettings.UsageSortMode,
						previousSettings.UsageDisplayMode,
						previousShellPreferences,
						persistenceToken);
					preferencesWereSaved = true;
				}

				try
				{
					// This is an in-session restore of trusted pre-import records.
					// Do not run portable-import normalization here: doing so would
					// discard the machine's previous bindings and risk consent.
					await _accountProfileStore.SaveAsync(
						restoredPreviousAccounts,
						persistenceToken);
				}
				catch (AccountProfileStoreException exception) when (
					exception.HasCommittedChanges &&
					(_portableSettingsImportTransaction is null))
				{
					committedWarning = exception;
				}
				catch (Exception undoException)
				{
					if (preferencesWereSaved &&
						(_portableSettingsImportTransaction is null))
					{
						try
						{
							await SavePortablePreferencesAsync(
								importedSettings.UsageSortMode,
								importedSettings.UsageDisplayMode,
								importedShellPreferences,
								CancellationToken.None);
						}
						catch (Exception rollbackException)
						{
							throw new InvalidOperationException(
								"帳號設定尚未還原，且無法把顯示設定恢復為匯入後狀態。請重新啟動 AI Usage 後確認設定。",
								new AggregateException(
									undoException,
									rollbackException));
						}
					}

					throw;
				}
			}

			IReadOnlyList<PrivateAccountCleanupIntentRollbackState>
				privateCleanupRollbackStates = await PersistAccountCleanupIntentAsync(
				localStateCleanupProfiles,
				cancellationToken);

			try
			{
				if (_portableSettingsImportTransaction is null)
				{
					await PersistPreviousSettingsAsync(cancellationToken);
				}
				else
				{
					try
					{
						await _portableSettingsImportTransaction.ExecuteAsync(
							PersistPreviousSettingsAsync,
							cancellationToken);
					}
					catch (PortableSettingsImportCommittedException exception)
					{
						committedCleanupReceiptWarning = exception;
						AppendAccountSettingsHealthWarning(
							AccountCleanupPendingFailureMessage);
					}
					catch (AccountProfileStoreException exception) when (
						exception.HasCommittedChanges)
					{
						throw new InvalidOperationException(
							"設定還原未完成；已保留匯入後的帳號與顯示設定。",
							exception);
					}
				}
			}
			catch
			{
				await RollbackFailedPrivateAccountCleanupIntentAsync(
					privateCleanupRollbackStates,
					originalAccountKeys);
				throw;
			}

			if (committedCleanupReceiptWarning is null)
			{
				await AuthorizeCommittedPortableCopilotCleanupSafelyAsync(
					restoredPreviousAccounts,
					CancellationToken.None);
			}

			bool wereCachedSnapshotsDeleted =
				committedCleanupReceiptWarning is null;
			bool wereRuntimeStatesPurged =
				committedCleanupReceiptWarning is null;
			bool wereRefreshesQuiesced = true;
			IReadOnlyDictionary<Guid, bool> refreshQuiesceResults =
				await QuiesceAccountRefreshesSafelyAsync(
					localStateCleanupProfiles);

			SetSortMode(previousSettings.UsageSortMode);
			SetUsageDisplayMode(previousSettings.UsageDisplayMode);
			ApplyProfiles(restoredPreviousAccounts);
			SetLastPortableSettingsImportUndoState(null);
			string restoredSettingsMessage =
				previousSettings.WidgetPreferences switch
				{
					null => "已還原匯入前設定：帳號、順序、排序與用量顯示設定已還原。",
					{ Theme: null } =>
						"已還原匯入前設定：帳號、順序、排序、用量顯示與浮窗設定已還原。",
					_ =>
						"已還原匯入前設定：帳號、順序、排序、用量顯示、浮窗設定與主題已還原。"
				};
			AccountSettingsMessage = restoredSettingsMessage +
				" 為避免顯示匯入後的錯誤帳號資料，部分用量可能需要重新檢查。";
			if (didResetRestoredClaudeConnection)
			{
				AccountSettingsMessage +=
				" Claude 訂閱連接資料已在匯入時重設；請逐一重新連接需要使用的 Claude 帳號。";
			}

			if (didResetRestoredCodexConnection)
			{
				AccountSettingsMessage +=
					" Codex 連接已在匯入時重設。請逐一重新連接需要使用的 Codex 帳號。使用 workspace 連接時，要重新輸入 workspace ID。";
			}

			if (didResetRestoredCopilotConnection)
			{
				AccountSettingsMessage +=
					" Copilot 卡片連接已在匯入時重設；請逐一重新連接需要使用的 Copilot 帳號。";
			}

			if (didResetRestoredGrokConnection)
			{
				AccountSettingsMessage +=
					" Grok 連接資料已在匯入時重設；請逐一重新連接需要使用的 Grok 帳號。";
			}

			foreach (AccountProfile cleanupProfile in localStateCleanupProfiles)
			{
				if (committedCleanupReceiptWarning is not null)
				{
					continue;
				}

				await DeleteAntigravityVerifiedAccountBindingSafelyAsync(
					cleanupProfile.Id,
					cleanupProfile.Provider,
					CancellationToken.None);
				bool wasRefreshQuiesced = refreshQuiesceResults[cleanupProfile.Id];

				if (!wasRefreshQuiesced)
				{
					wereRefreshesQuiesced = false;
				}

				(bool wasCachedSnapshotDeleted, bool wasRuntimeStatePurged) =
					await AttemptAccountCleanupAsync(
					cleanupProfile.Id,
					cleanupProfile.Provider,
					wasRefreshQuiesced,
					cachePending: true,
					runtimePending: true,
					CancellationToken.None,
					allowRetainedPrivateStateDeletion:
						cleanupProfile.Provider == ProviderKind.Copilot);
				wereCachedSnapshotsDeleted &= wasCachedSnapshotDeleted;
				wereRuntimeStatesPurged &= wasRuntimeStatePurged;

				lock (_refreshSync)
				{
					_runtimeOnlyCachedProviderAccountIdentityAccountIds.Remove(
						cleanupProfile.Id);
				}
			}

			await RestoreCachedSnapshotsAsync(
				Accounts.Where(account => account.IsEnabled).ToArray(),
				CancellationToken.None);
			await RefreshAntigravityReportedAccountDisplayAsync(
				CancellationToken.None);

			if (!wereCachedSnapshotsDeleted)
			{
				AccountSettingsMessage +=
					" 部分匯入後的上次用量無法清除，建議重新啟動 AI Usage。";
			}

			if (!wereRuntimeStatesPurged)
			{
				AccountSettingsMessage +=
					" 多次嘗試後仍無法清除部分匯入後的舊資料；資料可能仍保留在這台電腦。";
			}

			if (!wereRefreshesQuiesced)
			{
				AccountSettingsMessage +=
					" 部分匯入後的用量檢查尚未停止。設定已還原，稍後會自動完成，重新啟動後也會繼續。";
			}

			if ((committedWarning is null) &&
				(committedCleanupReceiptWarning is null) &&
				wereCachedSnapshotsDeleted &&
				wereRuntimeStatesPurged &&
				wereRefreshesQuiesced &&
				!HasBlockedAccountCleanups())
			{
				AccountSettingsHealthMessage = string.Empty;
				_shouldClearAccountSettingsHealthAfterSuccessfulSave = false;
			}

			if (committedCleanupReceiptWarning is not null)
			{
				AccountSettingsMessage +=
					" 還原後的 private state 清理授權尚未完成；清理工作與交易紀錄已保留，重新啟動後會自動再試。";
				throw committedCleanupReceiptWarning;
			}

			ThrowCommittedSaveWarning(
				committedWarning,
				$"{AccountSettingsMessage} 帳號設定已還原，但備份更新失敗。");
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public async Task AddAccountAsync(
		AccountProfile profile,
		CancellationToken cancellationToken = default)
	{
		AccountProfile normalizedProfile = NormalizeProfile(profile) with
		{
			ProviderAccountIdentity = null,
			HasAcceptedClaudeQuotaRisk = false
		};
		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();

			if ((normalizedProfile.Provider == ProviderKind.Antigravity) &&
				IsAntigravityConnectionJournalUnavailable())
			{
				throw new InvalidOperationException(
					OfficialAntigravityConnectionJournalUnavailableMessage);
			}

			if (_accountProfiles.Any(account => account.Id == normalizedProfile.Id))
			{
				throw new ArgumentException("帳號識別碼已存在。", nameof(profile));
			}

			if ((normalizedProfile.Provider == ProviderKind.Antigravity) &&
				_pendingAntigravityConnectionWork.ContainsKey(
					normalizedProfile.Id))
			{
				throw new InvalidOperationException(
					"先前的 Antigravity 連接仍在取消中。AI Usage 會自動完成，完成後即可重新新增帳號。");
			}

			if ((normalizedProfile.Provider == ProviderKind.Antigravity) &&
				_accountProfiles.Any(account =>
					account.Provider == ProviderKind.Antigravity))
			{
				throw new InvalidOperationException(
					"Antigravity 目前只能建立一個帳號。");
			}

			List<AccountProfile> candidateProfiles = new(_accountProfiles);
			int insertionIndex = GetDefaultAccountInsertionIndex(
				candidateProfiles,
				normalizedProfile.Provider);
			candidateProfiles.Insert(insertionIndex, normalizedProfile);
			AccountProfileStoreException? committedWarning =
				await PersistAndPublishAsync(candidateProfiles, cancellationToken);
			AccountSettingsMessage =
				$"已新增帳號「{GetAccountLabel(normalizedProfile)}」。";
			await RefreshAntigravityReportedAccountDisplayAsync(
				CancellationToken.None);
			ThrowCommittedSaveWarning(committedWarning);
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public async Task UpdateAccountAsync(
		AccountProfile profile,
		CancellationToken cancellationToken = default)
	{
		AccountProfile normalizedProfile = NormalizeProfile(profile);
		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();

			int accountIndex = _accountProfiles.FindIndex(
				account => account.Id == normalizedProfile.Id);

			if (accountIndex < 0)
			{
				throw new ArgumentException("找不到要編輯的帳號。", nameof(profile));
			}

			EnsureAccountMutationAllowed(normalizedProfile.Id);

			AccountProfile existingProfile = _accountProfiles[accountIndex];

			if (existingProfile.Provider != normalizedProfile.Provider)
			{
				throw new ArgumentException(
					"既有帳號不可變更服務；請刪除後重新新增。",
					nameof(profile));
			}

			normalizedProfile = normalizedProfile with
			{
				ProviderAccountIdentity =
					existingProfile.ProviderAccountIdentity,
				HasAcceptedClaudeQuotaRisk =
					existingProfile.HasAcceptedClaudeQuotaRisk
			};
			bool isUnboundAuthenticatedProviderBeingReEnabled =
				!existingProfile.IsEnabled &&
				normalizedProfile.IsEnabled &&
				string.IsNullOrWhiteSpace(
					existingProfile.ProviderAccountIdentity) &&
				((existingProfile.Provider == ProviderKind.Claude) ||
					(existingProfile.Provider == ProviderKind.Codex) ||
					(existingProfile.Provider == ProviderKind.Copilot) ||
					(existingProfile.Provider == ProviderKind.Grok));
			if (isUnboundAuthenticatedProviderBeingReEnabled)
			{
				_usageRefreshCoordinator.Invalidate(existingProfile.Id);
				if (!await DeleteCachedSnapshotSafelyAsync(
						existingProfile.Id,
						existingProfile.Provider,
						cancellationToken,
						retryTransientFailures: true))
				{
					RecordProviderAccountCacheCleanupFailure(
						existingProfile.Id);
					throw new InvalidOperationException(
						$"無法清除「{GetAccountLabel(existingProfile)}」先前的用量資料，因此仍會停止檢查。請確認 AI Usage 設定資料夾可寫入後再試。");
				}

				ResolveProviderAccountCacheCleanupFailure(existingProfile.Id);
			}

			List<AccountProfile> candidateProfiles = new(_accountProfiles);
			candidateProfiles[accountIndex] = normalizedProfile;
			AccountProfileStoreException? committedWarning =
				await PersistAndPublishAsync(candidateProfiles, cancellationToken);
			AccountSettingsMessage =
				$"已更新帳號「{GetAccountLabel(normalizedProfile)}」。";
			await RefreshAntigravityReportedAccountDisplayAsync(
				CancellationToken.None);
			ThrowCommittedSaveWarning(committedWarning);
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	internal async Task<bool> AcceptClaudeQuotaRiskAsync(
		AccountUsageViewModel account,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			if (_isUpdateShutdownReserved ||
				!CanManageAccounts ||
				!account.IsClaude ||
				!account.IsEnabled ||
				account.IsProviderAccountChangeInProgress ||
				!Accounts.Contains(account))
			{
				return false;
			}

			int accountIndex = _accountProfiles.FindIndex(
				profile =>
					(profile.Id == account.Id) &&
					(profile.Provider == ProviderKind.Claude));

			if ((accountIndex < 0) ||
				!ReferenceEquals(
					Accounts.FirstOrDefault(candidate => candidate.Id == account.Id),
					account))
			{
				return false;
			}

			AccountProfile existingProfile = _accountProfiles[accountIndex];

			if (existingProfile.HasAcceptedClaudeQuotaRisk)
			{
				return true;
			}

			string? cachedProviderAccountIdentity =
				GetRuntimeOnlyCachedProviderAccountIdentity(account);

			List<AccountProfile> candidateProfiles = new(_accountProfiles);
			candidateProfiles[accountIndex] = existingProfile with
			{
				HasAcceptedClaudeQuotaRisk = true
			};
			AccountProfileStoreException? committedWarning =
				await PersistAndPublishAsync(candidateProfiles, cancellationToken);
			RestoreRuntimeOnlyCachedProviderAccountIdentity(
				account,
				cachedProviderAccountIdentity);
			ThrowCommittedSaveWarning(
				committedWarning,
				"Claude 用量讀取確認已儲存，但備份更新失敗。請再儲存一次帳號設定。");
			return account.IsEnabled &&
				account.Profile.HasAcceptedClaudeQuotaRisk &&
				Accounts.Contains(account);
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	public async Task RemoveAccountAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();
			AccountProfile? accountToRemove = _accountProfiles.Find(
				account => account.Id == accountId);

			if (accountToRemove is null)
			{
				throw new ArgumentException("找不到要刪除的帳號。", nameof(accountId));
			}

			EnsureAccountMutationAllowed(accountId);

			List<AccountProfile> candidateProfiles = _accountProfiles
				.Where(account => account.Id != accountId)
				.ToList();
			HashSet<(Guid AccountId, ProviderKind Provider)> originalAccountKeys =
				_accountProfiles
					.Select(profile => (profile.Id, profile.Provider))
					.ToHashSet();
			IReadOnlyList<PrivateAccountCleanupIntentRollbackState>
				privateCleanupRollbackStates = await PersistAccountCleanupIntentAsync(
				new[] { accountToRemove },
				cancellationToken);
			AccountProfileStoreException? committedWarning;
			try
			{
				committedWarning = await PersistAndPublishAsync(
					candidateProfiles,
					cancellationToken);
			}
			catch
			{
				await RollbackFailedPrivateAccountCleanupIntentAsync(
					privateCleanupRollbackStates,
					originalAccountKeys);
				throw;
			}
			if ((accountToRemove.Provider == ProviderKind.Antigravity) &&
				_pendingAntigravityConnectionWork.ContainsKey(
					accountToRemove.Id) &&
				!await RemovePendingAntigravityConnectionWorkAsync(
					accountToRemove.Id,
					CancellationToken.None))
			{
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityConnectionPendingMessage);
			}
			await DeleteAntigravityVerifiedAccountBindingSafelyAsync(
				accountToRemove.Id,
				accountToRemove.Provider,
				CancellationToken.None);
			bool wasRefreshQuiesced =
				await QuiesceRemovedAccountRefreshSafelyAsync(accountToRemove.Id);
			(bool wasCachedSnapshotDeleted, bool wasRuntimeStatePurged) =
				await AttemptAccountCleanupAsync(
					accountToRemove.Id,
					accountToRemove.Provider,
					wasRefreshQuiesced,
					cachePending: true,
					runtimePending: true,
					CancellationToken.None);
			string accountLabel = GetAccountLabel(accountToRemove);
			string removalMessage = wasCachedSnapshotDeleted
				? $"已從 AI Usage 移除帳號「{accountLabel}」，並清除上次用量。"
				: $"已從 AI Usage 移除帳號「{accountLabel}」，但無法清除上次用量；資料可能仍保留在這台電腦。";

			if (!wasRuntimeStatePurged)
			{
				removalMessage +=
					" 多次嘗試後仍無法清除部分舊資料；資料可能仍保留在這台電腦。";
			}

			if (!wasRefreshQuiesced)
			{
				removalMessage +=
					" 舊用量檢查尚未停止。帳號已移除，稍後會自動完成，重新啟動後也會繼續。";
			}

			AccountSettingsMessage = removalMessage;
			await RefreshAntigravityReportedAccountDisplayAsync(
				CancellationToken.None);
			ThrowCommittedSaveWarning(
				committedWarning,
				wasCachedSnapshotDeleted && wasRuntimeStatePurged
					? null
					: $"{committedWarning?.Message}\n\n{removalMessage}");
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public async Task MoveAccountAsync(
		Guid accountId,
		int offset,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		if ((offset != -1) && (offset != 1))
		{
			throw new ArgumentOutOfRangeException(
				nameof(offset),
				"帳號一次只能向上或向下移動一格。");
		}

		await _accountMutationGate.WaitAsync(cancellationToken);
		IsManagingAccounts = true;

		try
		{
			EnsureAccountManagementAvailable();

			if (IsAutomaticUsageSorting)
			{
				throw new InvalidOperationException(
					"自動排序時無法手動移動。請先切換為手動排序。");
			}

			int accountIndex = _accountProfiles.FindIndex(
				account => account.Id == accountId);

			if (accountIndex < 0)
			{
				throw new ArgumentException("找不到要移動的帳號。", nameof(accountId));
			}

			int targetIndex = accountIndex + offset;

			if ((targetIndex < 0) || (targetIndex >= _accountProfiles.Count))
			{
				return;
			}

			List<AccountProfile> candidateProfiles = new(_accountProfiles);
			AccountProfile accountToMove = candidateProfiles[accountIndex];
			candidateProfiles.RemoveAt(accountIndex);
			candidateProfiles.Insert(targetIndex, accountToMove);
			AccountProfileStoreException? committedWarning =
				await PersistAndPublishAsync(candidateProfiles, cancellationToken);
			string direction = offset < 0 ? "上" : "下";
			AccountSettingsMessage =
				$"已將帳號「{GetAccountLabel(accountToMove)}」向{direction}移動。";
			ThrowCommittedSaveWarning(committedWarning);
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public async Task ToggleUsageSortModeAsync(
		CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			EnsureUpdateShutdownNotReserved();
			IsManagingAccounts = true;
			UsageSortMode nextMode = IsAutomaticUsageSorting
				? UsageSortMode.Manual
				: UsageSortMode.Automatic;

			if (_dashboardPreferencesStore is not null)
			{
				await _dashboardPreferencesStore.SaveUsageSortModeAsync(
					nextMode,
					cancellationToken);
			}

			SetSortMode(nextMode);
			ApplyAccountDisplayOrder();
			AccountSettingsMessage = IsAutomaticUsageSorting
				? "已切換為自動排序；手動帳號順序仍保留在本機。"
				: "已切換為手動排序；已恢復自訂帳號順序。";
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public async Task ToggleUsageDisplayModeAsync(
		CancellationToken cancellationToken = default)
	{
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			EnsureUpdateShutdownNotReserved();
			IsManagingAccounts = true;
			UsageDisplayMode nextMode = IsShowingRemainingUsage
				? UsageDisplayMode.Used
				: UsageDisplayMode.Remaining;

			if (_dashboardPreferencesStore is not null)
			{
				await _dashboardPreferencesStore.SaveUsageDisplayModeAsync(
					nextMode,
					cancellationToken);
			}

			SetUsageDisplayMode(nextMode);
			AccountSettingsMessage = IsShowingRemainingUsage
				? "已改為顯示剩餘用量。"
				: "已改為顯示已使用量。";
		}
		finally
		{
			IsManagingAccounts = false;
			_accountMutationGate.Release();
		}
	}

	public Task RefreshUsageAsync(CancellationToken cancellationToken = default)
	{
		return QueueRefreshUsageAsync(cancellationToken);
	}

	public async Task RefreshUsageInBackgroundAsync(
		CancellationToken cancellationToken = default)
	{
		if (_isUpdateShutdownReserved)
		{
			return;
		}

		Dictionary<Guid, UsageSnapshot?> cleanupBlockedAccounts = Accounts
			.Where(account => IsAccountCleanupBlocked(
				account.Id,
				account.Provider))
			.ToDictionary(account => account.Id, account => account.CurrentSnapshot);
		Task<bool> antigravityConnectionTask =
			RetryPendingAntigravityConnectionsNowAsync(cancellationToken);
		Task<bool> cleanupTask =
			RetryPendingAccountCleanupsNowAsync(cancellationToken);
		Task refreshTask = QueueRefreshUsageAsync(cancellationToken);
		await Task.WhenAll(
			antigravityConnectionTask,
			cleanupTask,
			refreshTask);
		bool cleanupCompleted = await cleanupTask;

		if (!cleanupCompleted)
		{
			return;
		}

		foreach ((Guid accountId, UsageSnapshot? previousSnapshot) in
			cleanupBlockedAccounts)
		{
			AccountUsageViewModel? account = Accounts.FirstOrDefault(
				candidate => candidate.Id == accountId);
			if ((account is not null) &&
				!IsAccountCleanupBlocked(account.Id, account.Provider) &&
				ReferenceEquals(account.CurrentSnapshot, previousSnapshot))
			{
				await RefreshAccountUsageAsync(account.Id, cancellationToken);
			}
		}
	}

	public Task RefreshAccountUsageAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return RefreshAccountUsageAsync(
			accountId,
			reportProgress: null,
			cancellationToken);
	}

	internal Task RefreshAccountUsageAsync(
		Guid accountId,
		Action<AccountRefreshProgress>? reportProgress,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		long lifecycleRevision = Accounts.FirstOrDefault(
			account => account.Id == accountId)?.LifecycleRevision ?? -1;
		Task refreshTask;
		AccountRefreshProgress? coalescedProgress = null;

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return Task.CompletedTask;
			}

			if ((_activeRefreshTask is not null) &&
				!_activeRefreshTask.IsCompleted &&
				(_activeRefreshScope == RefreshRequestScope.Account) &&
				(_activeRefreshAccountId == accountId) &&
				(_activeRefreshLifecycleRevision == lifecycleRevision))
			{
				refreshTask = _activeRefreshTask;
				AccountUsageViewModel? activeAccount = Accounts.FirstOrDefault(
					account => account.Id == accountId);
				coalescedProgress = activeAccount?.StatusKind ==
					AccountStatusKind.Refreshing
						? AccountRefreshProgress.Started
						: AccountRefreshProgress.Queued;
			}
			else
			{
				Task previousRefresh = _activeRefreshTask ?? Task.CompletedTask;
				refreshTask = RefreshAccountUsageCoreAsync(
					previousRefresh,
					accountId,
					lifecycleRevision,
					reportProgress,
					cancellationToken);
				_activeRefreshTask = refreshTask;
				_activeRefreshAccountId = accountId;
				_activeRefreshLifecycleRevision = lifecycleRevision;
				_activeRefreshScope = RefreshRequestScope.Account;
			}
		}

		if (coalescedProgress.HasValue)
		{
			ReportAccountRefreshProgress(
				reportProgress,
				cancellationToken,
				coalescedProgress.Value);
		}

		return refreshTask.WaitAsync(cancellationToken);
	}

	internal Task RevalidateClaudeUsageAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return RevalidateUsageAsync(accountId, cancellationToken);
	}

	internal Task RevalidateUsageAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		long lifecycleRevision = Accounts.FirstOrDefault(
			account => account.Id == accountId)?.LifecycleRevision ?? -1;
		Task refreshTask;
		bool shouldObserveCompletion = false;
		long operationId = 0;

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return Task.CompletedTask;
			}

			if (_usageSafetyRevalidations.TryGetValue(
					accountId,
					out var activeRevalidation) &&
				!activeRevalidation.Task.IsCompleted &&
				(activeRevalidation.LifecycleRevision == lifecycleRevision))
			{
				refreshTask = activeRevalidation.Task;
			}
			else
			{
				Task previousRefresh = _activeRefreshTask ?? Task.CompletedTask;
				AccountUsageViewModel? account = Accounts.FirstOrDefault(
					candidate => candidate.Id == accountId);
				operationId = Interlocked.Increment(
					ref _nextUsageSafetyRevalidationOperationId);
				if (operationId <= 0)
				{
					throw new InvalidOperationException(
						"目前無法安排新的用量檢查。請重新啟動 AI Usage。");
				}

				account?.BeginUsageSafetyRevalidation(operationId);
				refreshTask = RevalidateUsageAfterPreviousAsync(
					previousRefresh,
					accountId,
					lifecycleRevision);
				_usageSafetyRevalidations[accountId] =
					(lifecycleRevision, operationId, refreshTask);
				_activeRefreshTask = refreshTask;
				_activeRefreshAccountId = accountId;
				_activeRefreshLifecycleRevision = lifecycleRevision;
				_activeRefreshScope = RefreshRequestScope.UsageSafetyRevalidation;
				shouldObserveCompletion = true;
			}
		}

		if (shouldObserveCompletion)
		{
			_ = ObserveUsageSafetyRevalidationCompletionAsync(
				accountId,
				lifecycleRevision,
				operationId,
				refreshTask);
		}

		return refreshTask.WaitAsync(cancellationToken);
	}

	public void StopRefreshing()
	{
		Guid[] accountIds = StopSchedulingRefreshes();
		InvalidateStoppedRefreshes(accountIds);
	}

	internal bool TryReserveAccountMutationsForUpdateShutdown()
	{
		if (_isUpdateShutdownReserved ||
			!_accountMutationGate.Wait(0))
		{
			return false;
		}

		try
		{
			if (_isUpdateShutdownReserved)
			{
				return false;
			}

			_isUpdateShutdownReserved = true;
		}
		finally
		{
			_accountMutationGate.Release();
		}

		NotifyUpdateShutdownAvailabilityChanged();
		return true;
	}

	internal async Task<bool>
		RollbackAccountMutationsUpdateShutdownReservationAsync()
	{
		if (!_isUpdateShutdownReserved)
		{
			return true;
		}

		if (_isAccountMutationGateHeldForUpdateShutdown)
		{
			return false;
		}

		await _accountMutationGate.WaitAsync();
		try
		{
			if (_isAccountMutationGateHeldForUpdateShutdown)
			{
				return false;
			}

			if (!_isUpdateShutdownReserved)
			{
				return true;
			}

			_isUpdateShutdownReserved = false;
		}
		finally
		{
			_accountMutationGate.Release();
		}

		NotifyUpdateShutdownAvailabilityChanged();
		return true;
	}

	internal async Task<bool> LockAccountMutationsForUpdateShutdownAsync(
		TimeSpan timeout)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		if (!_isUpdateShutdownReserved)
		{
			throw new InvalidOperationException(
				"尚未預約更新關閉，不能鎖定帳號寫入。");
		}

		if (_isAccountMutationGateHeldForUpdateShutdown)
		{
			return true;
		}

		using CancellationTokenSource timeoutSource = new(timeout);
		try
		{
			await _accountMutationGate.WaitAsync(timeoutSource.Token);
		}
		catch (OperationCanceledException) when (
			timeoutSource.IsCancellationRequested)
		{
			return false;
		}

		if (!_isUpdateShutdownReserved)
		{
			_accountMutationGate.Release();
			return false;
		}

		_isAccountMutationGateHeldForUpdateShutdown = true;
		return true;
	}

	internal async Task<bool> StopAndDrainRefreshingAsync(TimeSpan timeout)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Guid[] accountIds = StopSchedulingRefreshes();
		using CancellationTokenSource timeoutSource = new(timeout);

		while (true)
		{
			Task activeRefreshTask;
			Task[] accountRefreshIdleTasks;
			bool isDrained;

			lock (_refreshSync)
			{
				isDrained = ((_activeRefreshTask is null) ||
						_activeRefreshTask.IsCompleted) &&
					(_accountRefreshActivities.Count == 0);

				activeRefreshTask = _activeRefreshTask ?? Task.CompletedTask;
				accountRefreshIdleTasks = _accountRefreshActivities.Values
					.Select(activity => activity.IdleCompletion.Task)
					.ToArray();
			}

			if (isDrained)
			{
				InvalidateStoppedRefreshes(accountIds);
				return true;
			}

			Task[] drainTasks = accountRefreshIdleTasks
				.Append(ObserveRefreshCompletionAsync(activeRefreshTask))
				.ToArray();

			try
			{
				await Task.WhenAll(drainTasks).WaitAsync(timeoutSource.Token);
			}
			catch (OperationCanceledException) when (
				timeoutSource.IsCancellationRequested)
			{
				InvalidateStoppedRefreshes(accountIds);
				return false;
			}
		}
	}

	private Guid[] StopSchedulingRefreshes()
	{
		bool didStop;
		Guid[] accountIds;

		lock (_refreshSync)
		{
			didStop = !_isRefreshStopped;
			_isRefreshStopped = true;
			accountIds = Accounts.Select(account => account.Id).ToArray();
		}

		if (didStop)
		{
			OnPropertyChanged(nameof(CanRefresh));
		}

		return accountIds;
	}

	private void InvalidateStoppedRefreshes(
		IReadOnlyCollection<Guid> accountIds)
	{
		lock (_refreshSync)
		{
			if (_wereStoppedRefreshesInvalidated)
			{
				return;
			}

			_wereStoppedRefreshesInvalidated = true;
		}

		foreach (Guid accountId in accountIds)
		{
			_usageRefreshCoordinator.Invalidate(accountId);
		}
	}

	internal Task RefreshUsageAfterInvalidationAsync(
		Guid accountId,
		CancellationToken cancellationToken = default,
		Func<bool>? tryBeginProviderAccountCommit = null)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		Task refreshTask;

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return Task.CompletedTask;
			}

			Task previousRefresh = _activeRefreshTask ?? Task.CompletedTask;
			_activeRefreshTask = RefreshUsageAfterInvalidationCoreAsync(
				previousRefresh,
				accountId,
				tryBeginProviderAccountCommit);
			_activeRefreshAccountId = accountId;
			_activeRefreshLifecycleRevision = -1;
			_activeRefreshScope = RefreshRequestScope.ProviderInvalidation;
			refreshTask = _activeRefreshTask;
		}

		return refreshTask.WaitAsync(cancellationToken);
	}

	internal Task RefreshTargetUsageAfterInvalidationAsync(
		Guid accountId,
		CancellationToken cancellationToken = default,
		Func<bool>? tryBeginProviderAccountCommit = null)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		Task refreshTask;

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return Task.CompletedTask;
			}

			Task previousRefresh = _activeRefreshTask ?? Task.CompletedTask;
			refreshTask = RefreshUsageAfterInvalidationCoreAsync(
				Task.CompletedTask,
				accountId,
				tryBeginProviderAccountCommit,
				onlyAccountId: accountId);
			_activeRefreshTask = Task.WhenAll(
				ObserveRefreshCompletionAsync(previousRefresh),
				ObserveRefreshCompletionAsync(refreshTask));
			_activeRefreshAccountId = accountId;
			_activeRefreshLifecycleRevision = -1;
			_activeRefreshScope = RefreshRequestScope.ProviderInvalidation;
		}

		return refreshTask.WaitAsync(cancellationToken);
	}

	internal Task QuiesceAccountRefreshAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		_usageRefreshCoordinator.Invalidate(accountId);
		Task accountRefreshIdleTask;

		lock (_refreshSync)
		{
			accountRefreshIdleTask = _accountRefreshActivities.TryGetValue(
				accountId,
				out AccountRefreshActivity? activity)
				? activity.IdleCompletion.Task
				: Task.CompletedTask;
		}

		return accountRefreshIdleTask.WaitAsync(cancellationToken);
	}

	internal bool IsPrimaryEnabledAntigravityAccount(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			return false;
		}

		AccountProfile? activeAccount = _accountProfiles.FirstOrDefault(
			profile =>
				profile.IsEnabled &&
				(profile.Provider == ProviderKind.Antigravity));
		return activeAccount?.Id == accountId;
	}

	internal void NotifyAntigravityAccountSetupStarting()
	{
		ResetAntigravityReportedAccountRemovalState();
	}

	internal void NotifyAntigravitySetupIntentRetained(Guid accountId)
	{
		if (_pendingAntigravityConnectionWork.ContainsKey(accountId))
		{
			MarkPendingAntigravityConnection(accountId);
		}
	}

	internal async Task<bool> TryBeginOfficialAntigravitySetupIntentAsync(
		AccountUsageViewModel account,
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			if (_isUpdateShutdownReserved ||
				!CanManageAccounts ||
				!account.CanManage ||
				!account.IsEnabled ||
				!account.IsAntigravity ||
				!account.IsProviderAccountChangeInProgress ||
				account.IsProviderAccountChangeCommitInProgress ||
				!Accounts.Contains(account) ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				IsAntigravityConnectionJournalUnavailable() ||
				_pendingAntigravityConnectionWork.ContainsKey(account.Id))
			{
				return false;
			}

			AntigravityConnectionPendingWork setupIntent = new(
				account.Id,
				CreateAntigravityPendingProfileIdentityFingerprint(
					account.Profile.ProviderAccountIdentity),
				CachePending: false,
				ProfilePending: false,
				setupAttemptId);
			_pendingAntigravityConnectionWork[account.Id] = setupIntent;

			if (_antigravityConnectionPendingStore is not null)
			{
				ResetAntigravityConnectionRetryBackoff();
				_unjournaledAntigravityConnectionWorkAccountIds.Add(account.Id);

				try
				{
					await _antigravityConnectionPendingStore.BeginSetupAsync(
						account.Id,
						setupIntent.ExpectedProfileIdentityFingerprint,
						setupAttemptId,
						cancellationToken);
					_unjournaledAntigravityConnectionWorkAccountIds.Remove(
						account.Id);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					throw;
				}
				catch
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					RecordAntigravityConnectionRetryResult(completed: false);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					return false;
				}
			}

			return true;
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<bool> CancelOfficialAntigravitySetupIntentAsync(
		Guid accountId,
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			if (!_pendingAntigravityConnectionWork.TryGetValue(
					accountId,
					out AntigravityConnectionPendingWork? work) ||
				!work.IsSetupPending ||
				(work.SetupAttemptId != setupAttemptId))
			{
				return true;
			}

			if (_antigravityConnectionPendingStore is not null)
			{
				try
				{
					AntigravitySetupApprovalReceipt? approval =
						await _antigravityConnectionPendingStore
							.LoadSetupApprovalAsync(
								setupAttemptId,
								cancellationToken);
					AntigravitySetupAttemptState? attemptState =
						await _antigravityConnectionPendingStore
							.LoadSetupAttemptStateAsync(
								setupAttemptId,
								cancellationToken);
					if ((approval is not null) ||
						(attemptState?.Phase ==
							AntigravitySetupAttemptPhase.ApprovalRequested))
					{
						MarkPendingAntigravityConnection(accountId);
						return false;
					}
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
					MarkPendingAntigravityConnection(accountId);
					RecordAntigravityConnectionRetryResult(completed: false);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					return false;
				}
			}

			bool wasRemoved =
				await RemovePendingAntigravityConnectionWorkAsync(
					accountId,
					cancellationToken);
			if (!wasRemoved)
			{
				MarkPendingAntigravityConnection(accountId);
				AppendAccountSettingsHealthWarning(
					OfficialAntigravitySetupCompletionPendingMessage);
				RecordAntigravityConnectionRetryResult(completed: false);
				return false;
			}

			if (!_pendingAntigravityConnectionWork.Values.Any(
					pending => pending.IsSetupPending))
			{
				ClearAccountSettingsHealthWarning(
					OfficialAntigravitySetupCompletionPendingMessage);
			}

			return true;
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal Task<bool> CancelOfficialAntigravitySetupIntentAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		Guid? setupAttemptId =
			_pendingAntigravityConnectionWork.TryGetValue(
				accountId,
				out AntigravityConnectionPendingWork? work)
				? work.SetupAttemptId
				: null;
		return setupAttemptId is Guid attemptId
			? CancelOfficialAntigravitySetupIntentAsync(
				accountId,
				attemptId,
				cancellationToken)
			: Task.FromResult(true);
	}

	internal void ReportRefreshFailure()
	{
		SetRefreshStatus(
			"檢查失敗，稍後會自動再試",
			new CompactRefreshStatus("檢查失敗，稍後自動再試", false));
	}

	internal void SetGrokConnectionRecoveryPending(bool isPending)
	{
		if (isPending)
		{
			AppendAccountSettingsHealthWarning(
				GrokConnectionRecoveryPendingMessage);
			return;
		}

		ClearAccountSettingsHealthWarning(
			GrokConnectionRecoveryPendingMessage);
	}

	private Task QueueRefreshUsageAsync(CancellationToken cancellationToken)
	{
		Task refreshTask;

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return Task.CompletedTask;
			}

			if ((_activeRefreshTask is null) ||
				_activeRefreshTask.IsCompleted ||
				(_activeRefreshScope != RefreshRequestScope.All))
			{
				Task previousRefresh = _activeRefreshTask ?? Task.CompletedTask;
				_activeRefreshTask = RefreshUsageAfterPreviousAsync(
					previousRefresh);
				_activeRefreshAccountId = null;
				_activeRefreshLifecycleRevision = -1;
				_activeRefreshScope = RefreshRequestScope.All;
			}

			refreshTask = _activeRefreshTask;
		}

		return refreshTask.WaitAsync(cancellationToken);
	}

	private async Task RefreshUsageAfterPreviousAsync(Task previousRefresh)
	{
		try
		{
			await previousRefresh;
		}
		catch
		{
			// A refresh-all request still gets one attempt after an earlier failure.
		}

		lock (_refreshSync)
		{
			if (_isRefreshStopped || _isUpdateShutdownReserved)
			{
				return;
			}
		}

		await RefreshUsageCoreAsync();
	}

	private async Task RefreshUsageCoreAsync(
		Guid? providerAccountChangeId = null,
		Func<bool>? tryBeginProviderAccountCommit = null,
		Guid? onlyAccountId = null)
	{
		BeginRefreshOperation();
		(AccountUsageViewModel Account, long LifecycleRevision)[] refreshTargets =
			Array.Empty<(AccountUsageViewModel, long)>();
		bool[] refreshReservationsTransferred = Array.Empty<bool>();

		try
		{
			lock (_refreshSync)
			{
				if (_isRefreshStopped || _isUpdateShutdownReserved)
				{
					return;
				}

				refreshTargets =
					Accounts
						.Where(account =>
						{
							bool isExplicitProviderAccountChange =
								account.IsProviderAccountChangeInProgress &&
								(account.Id == providerAccountChangeId);
							return ((onlyAccountId is null) ||
									(account.Id == onlyAccountId)) &&
								account.IsEnabled &&
								!IsAccountCleanupBlocked(account.Id, account.Provider) &&
								(isExplicitProviderAccountChange ||
									account.CanQueryUsage) &&
								(!account.IsProviderAccountChangeInProgress ||
									isExplicitProviderAccountChange);
						})
						.OrderByDescending(account =>
							account.IsAntigravity &&
							!IsInactiveAntigravityAccount(account))
						.Select(account => (account, account.LifecycleRevision))
					.ToArray();
				// Reserve every captured target before setup can mark the account as
				// changing or the AGY metadata prelude can yield.
				refreshReservationsTransferred = new bool[refreshTargets.Length];
				foreach ((AccountUsageViewModel account, _) in refreshTargets)
				{
					BeginAccountRefresh(account.Id);
				}
			}

			int coolingDownCount = refreshTargets.Count(target =>
				GetRemainingRefreshCooldown(
					target.Account.Profile) is TimeSpan remaining &&
				(remaining > TimeSpan.Zero));
			Guid[] liveAntigravityRefreshTargetIds = refreshTargets
				.Where(target =>
					target.Account.IsAntigravity &&
					!IsInactiveAntigravityAccount(target.Account) &&
					!(GetRemainingRefreshCooldown(target.Account.Profile) is
						TimeSpan remaining &&
						(remaining > TimeSpan.Zero)))
				.Select(target => target.Account.Id)
				.ToArray();
			bool hasLiveAntigravityRefreshTarget =
				liveAntigravityRefreshTargetIds.Length > 0;
			AntigravityReportedAccountRefreshAssociation?
				antigravityReportedAccountAssociation =
					hasLiveAntigravityRefreshTarget
						? await BeginAntigravityReportedAccountRefreshAssociationAsync()
						: null;

			foreach ((AccountUsageViewModel account, _) in refreshTargets)
			{
				account.MarkRefreshing();
			}

			Task<UsageSnapshot>[] refreshTasks =
				new Task<UsageSnapshot>[refreshTargets.Length];
			for (int index = 0; index < refreshTargets.Length; index++)
			{
				refreshTasks[index] = RefreshReservedAccountSafelyAsync(
					refreshTargets[index].Account);
				refreshReservationsTransferred[index] = true;
			}

			UsageSnapshot[] snapshots = await Task.WhenAll(refreshTasks);

			if (IsRefreshStopped())
			{
				return;
			}

			if ((providerAccountChangeId is not null) &&
				(tryBeginProviderAccountCommit is not null) &&
				!tryBeginProviderAccountCommit())
			{
				return;
			}

			Dictionary<Guid, string> identityConflicts =
				FindProviderAccountIdentityConflicts(
					refreshTargets.Select(target => target.Account).ToArray(),
					snapshots,
					refreshTargets
						.Select(target => target.LifecycleRevision)
						.ToArray());

			for (int index = 0; index < refreshTargets.Length; index++)
			{
				(AccountUsageViewModel account, long lifecycleRevision) =
					refreshTargets[index];

				if (!account.IsEnabled ||
					!Accounts.Contains(account) ||
					(account.LifecycleRevision != lifecycleRevision))
				{
					continue;
				}

				if (identityConflicts.TryGetValue(
						account.Id,
						out string? identityConflict))
				{
					account.RejectProviderAccountSnapshot(
						snapshots[index],
						identityConflict,
						UsageRecoveryAction.SwitchAccount);
				}
				else
				{
					account.ApplyLiveSnapshot(snapshots[index]);
					ReleaseRuntimeOnlyCachedProviderAccountIdentityIfVerified(
						account,
						snapshots[index]);
				}

				await DeleteDefiniteScopeMismatchCacheAsync(account);
			}

			await PersistProviderAccountIdentityBindingsAsync();

			if (hasLiveAntigravityRefreshTarget)
			{
				AccountUsageViewModel[] successfulAntigravityTargets = Accounts
					.Where(account =>
						liveAntigravityRefreshTargetIds.Contains(account.Id) &&
						IsSuccessfulAntigravityQuotaSnapshot(
							account.CurrentSnapshot))
					.ToArray();
				MarkAntigravityReportedAccountsStale(
					liveAntigravityRefreshTargetIds,
					successfulAntigravityTargets);

				if (successfulAntigravityTargets.Length > 0)
				{
					await RefreshAntigravityReportedAccountDisplayAsync(
						antigravityReportedAccountAssociation,
						successfulAntigravityTargets);
				}
			}
			else if (!HasConnectedEnabledAntigravityAccount())
			{
				// Keep retrying cleanup for disconnected AGY cards without
				// treating a connected account's normal cooldown as new identity
				// evidence.
				await RefreshAntigravityReportedAccountDisplayAsync();
			}
			List<UsageSnapshot> appliedSnapshots = new(refreshTargets.Length);
			List<UsageSnapshot> applicableSnapshots = new(refreshTargets.Length);

			foreach ((AccountUsageViewModel account, long lifecycleRevision) in
				refreshTargets)
			{
				if (!account.IsEnabled ||
					!Accounts.Contains(account) ||
					(account.LifecycleRevision != lifecycleRevision))
				{
					continue;
				}

				if (account.CurrentSnapshot is UsageSnapshot currentSnapshot)
				{
					appliedSnapshots.Add(currentSnapshot);
				}

				if (!account.DidRejectProviderAccountSnapshot &&
					(account.CurrentSnapshot is UsageSnapshot appliedSnapshot) &&
					account.CanPersistCurrentSnapshot)
				{
					applicableSnapshots.Add(appliedSnapshot);
				}
			}

			ApplyAccountDisplayOrder();

			await PersistUsableSnapshotsAsync(applicableSnapshots);

			ReportRefreshResult(appliedSnapshots, coolingDownCount);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"usage-refresh",
				"scope=all;stage=orchestration;result=failed",
				exception);
			ReportRefreshFailure();
		}
		finally
		{
			for (int index = 0;
				index < refreshReservationsTransferred.Length;
				index++)
			{
				if (!refreshReservationsTransferred[index])
				{
					EndAccountRefresh(refreshTargets[index].Account.Id);
				}
			}

			EndRefreshOperation();
		}
	}

	private async Task RefreshAccountUsageCoreAsync(
		Task previousRefresh,
		Guid accountId,
		long lifecycleRevision,
		Action<AccountRefreshProgress>? reportProgress,
		CancellationToken progressCancellationToken)
	{
		AccountUsageViewModel? queuedAccount = Accounts.FirstOrDefault(
			candidate => candidate.Id == accountId);

		if ((queuedAccount is null) ||
			!queuedAccount.IsEnabled ||
			IsAccountCleanupBlocked(
				queuedAccount.Id,
				queuedAccount.Provider) ||
			!queuedAccount.CanQueryUsage ||
			queuedAccount.IsProviderAccountChangeInProgress ||
			(queuedAccount.LifecycleRevision != lifecycleRevision))
		{
			return;
		}

		if (!previousRefresh.IsCompleted)
		{
			ReportAccountRefreshProgress(
				reportProgress,
				progressCancellationToken,
				AccountRefreshProgress.Queued);
		}

		try
		{
			await previousRefresh;
		}
		catch
		{
			// A manual refresh still gets one new attempt after an earlier failure.
		}

		AccountUsageViewModel? account = Accounts.FirstOrDefault(
			candidate => candidate.Id == accountId);

		lock (_refreshSync)
		{
			if (_isRefreshStopped ||
				_isUpdateShutdownReserved ||
				(account is null) ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				!account.CanQueryUsage ||
				account.IsProviderAccountChangeInProgress ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevision))
			{
				return;
			}
		}

		UsageSnapshot? fallbackSnapshot = GetManualRefreshFallbackSnapshot(account);
		TimeSpan? remainingCooldown =
			GetRemainingRefreshCooldown(account.Profile);

		if ((remainingCooldown is not null) &&
			(remainingCooldown.Value > TimeSpan.Zero))
		{
			UsageSnapshot? cooldownSnapshot =
				PrepareManualRefreshFallbackSnapshot(
					account.CurrentSnapshot,
					account.Profile,
					_timeProvider.GetUtcNow(),
					allowEmptyMetrics: true);
			if ((cooldownSnapshot is not null) &&
				(account.CurrentSnapshot is UsageSnapshot currentSnapshot) &&
				(cooldownSnapshot.Metrics.Count != currentSnapshot.Metrics.Count))
			{
				account.ApplySnapshot(cooldownSnapshot);
			}

			ReportRefreshCooldown(
				remainingCooldown.Value,
				fallbackSnapshot is not null,
				account.RecoveryAction == UsageRecoveryAction.Retry);
			return;
		}

		// Recheck and reserve atomically with provider-account setup. This keeps a
		// setup quiesce from observing idle before the provider call is registered.
		lock (_refreshSync)
		{
			if (_isRefreshStopped ||
				_isUpdateShutdownReserved ||
				(account is null) ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				!account.CanQueryUsage ||
				account.IsProviderAccountChangeInProgress ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevision))
			{
				return;
			}

			BeginAccountRefresh(account.Id);
		}

		// Manual AGY refresh has the same metadata prelude as refresh-all.
		bool wasAccountRefreshReservationTransferred = false;
		BeginRefreshOperation();

		try
		{
			ReportAccountRefreshProgress(
				reportProgress,
				progressCancellationToken,
				AccountRefreshProgress.Started);
			AntigravityReportedAccountRefreshAssociation?
				antigravityReportedAccountAssociation = account.IsAntigravity
					? await BeginAntigravityReportedAccountRefreshAssociationAsync()
					: null;

			_usageRefreshCoordinator.Invalidate(accountId);

			if (fallbackSnapshot is not null)
			{
				_usageRefreshCoordinator.SeedLastKnownGood(fallbackSnapshot);
			}

			account.MarkRefreshing();
			Task<UsageSnapshot> refreshTask =
				RefreshReservedAccountSafelyAsync(account);
			wasAccountRefreshReservationTransferred = true;
			UsageSnapshot snapshot = await refreshTask;

			if (IsRefreshStopped() ||
				!account.IsEnabled ||
				account.IsProviderAccountChangeInProgress ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevision))
			{
				return;
			}

			if ((snapshot.Status == SnapshotStatus.Error) &&
				!IsTerminalIdentityRecovery(snapshot.RecoveryAction))
			{
				fallbackSnapshot = PrepareManualRefreshFallbackSnapshot(
					fallbackSnapshot,
					account.Profile,
					_timeProvider.GetUtcNow());

				if (fallbackSnapshot is not null)
				{
					snapshot = fallbackSnapshot with
					{
						Status = SnapshotStatus.Stale,
						Error = snapshot.Error,
						RecoveryAction = snapshot.RecoveryAction
					};
				}
			}

			Dictionary<Guid, string> identityConflicts =
				FindProviderAccountIdentityConflicts(
					new[] { account },
					new[] { snapshot },
					new[] { lifecycleRevision });

			if (identityConflicts.TryGetValue(
					account.Id,
					out string? identityConflict))
			{
				account.RejectProviderAccountSnapshot(
					snapshot,
					identityConflict,
					UsageRecoveryAction.SwitchAccount);
			}
			else
			{
				account.ApplyLiveSnapshot(snapshot);
				ReleaseRuntimeOnlyCachedProviderAccountIdentityIfVerified(
					account,
					snapshot);
			}

			await DeleteDefiniteScopeMismatchCacheAsync(account);

			await PersistProviderAccountIdentityBindingsAsync();

			if (account.IsAntigravity &&
				IsSuccessfulAntigravityQuotaSnapshot(account.CurrentSnapshot))
			{
				await RefreshAntigravityReportedAccountDisplayAsync(
					antigravityReportedAccountAssociation,
					new[] { account });
			}
			else if (account.IsAntigravity)
			{
				account.MarkAntigravityReportedAccountEmailStale();
			}
			ApplyAccountDisplayOrder();

			if (!account.DidRejectProviderAccountSnapshot &&
				(account.CurrentSnapshot is UsageSnapshot appliedSnapshot) &&
				account.CanPersistCurrentSnapshot)
			{
				await PersistUsableSnapshotsAsync(new[] { appliedSnapshot });
			}

			ReportRefreshResult(new[] { account.CurrentSnapshot ?? snapshot }, 0);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"usage-refresh",
				"scope=account;stage=orchestration;result=failed",
				exception);
			ReportRefreshFailure();
		}
		finally
		{
			if (!wasAccountRefreshReservationTransferred)
			{
				EndAccountRefresh(account.Id);
			}

			EndRefreshOperation();
		}
	}

	private async Task RevalidateUsageAfterPreviousAsync(
		Task previousRefresh,
		Guid accountId,
		long lifecycleRevision)
	{
		try
		{
			await previousRefresh;
		}
		catch
		{
			// A manual safety revalidation still gets one attempt after an earlier failure.
		}

		AccountUsageViewModel? account = Accounts.FirstOrDefault(
			candidate => candidate.Id == accountId);

		lock (_refreshSync)
		{
			if (_isRefreshStopped ||
				_isUpdateShutdownReserved ||
				(account is null) ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				(!account.IsClaude && !account.IsAntigravity) ||
				account.IsProviderAccountChangeInProgress ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevision) ||
				(account.RecoveryAction != UsageRecoveryAction.RevalidateUsage))
			{
				return;
			}
		}

		if (_accountRuntimeStatePurger is null)
		{
			ReportRefreshFailure();
			return;
		}

		try
		{
			_usageRefreshCoordinator.Invalidate(accountId);
			await _accountRuntimeStatePurger.ResetUsageSafetyStateAsync(
				accountId,
				account.Profile.Provider,
				CancellationToken.None);
		}
		catch (Exception exception)
		{
			ReportDiagnostic(
				"usage-safety-revalidation",
				"stage=runtime-state-reset;result=failed",
				exception);
			ReportRefreshFailure();
			return;
		}

		if (!account.IsEnabled ||
			!Accounts.Contains(account) ||
			(account.LifecycleRevision != lifecycleRevision))
		{
			return;
		}

		await RefreshAccountUsageCoreAsync(
			Task.CompletedTask,
			accountId,
			lifecycleRevision,
			reportProgress: null,
			progressCancellationToken: CancellationToken.None);
	}

	private async Task ObserveUsageSafetyRevalidationCompletionAsync(
		Guid accountId,
		long lifecycleRevision,
		long operationId,
		Task revalidationTask)
	{
		try
		{
			await revalidationTask;
		}
		catch
		{
			// The original caller observes the failure; this observer only removes the entry.
		}
		finally
		{
			bool didRemoveRevalidation = false;

			lock (_refreshSync)
			{
				if (_usageSafetyRevalidations.TryGetValue(
						accountId,
						out var activeRevalidation) &&
					(activeRevalidation.LifecycleRevision == lifecycleRevision) &&
					(activeRevalidation.OperationId == operationId) &&
					ReferenceEquals(activeRevalidation.Task, revalidationTask))
				{
					_usageSafetyRevalidations.Remove(accountId);
					didRemoveRevalidation = true;
				}
			}

			if (didRemoveRevalidation)
			{
				AccountUsageViewModel? account = Accounts.FirstOrDefault(
					candidate => candidate.Id == accountId);
				account?.CompleteUsageSafetyRevalidation(operationId);
			}
		}
	}

	private static void ReportAccountRefreshProgress(
		Action<AccountRefreshProgress>? reportProgress,
		CancellationToken cancellationToken,
		AccountRefreshProgress progress)
	{
		if ((reportProgress is null) || cancellationToken.IsCancellationRequested)
		{
			return;
		}

		try
		{
			reportProgress(progress);
		}
		catch
		{
			// Status feedback must not change or strand the refresh operation.
		}
	}

	private void ReportDiagnostic(
		string operation,
		string summary,
		Exception exception)
	{
		try
		{
			_reportDiagnostic(operation, summary, exception);
		}
		catch
		{
			// Diagnostics must never alter a refresh outcome.
		}
	}

	private async Task RefreshUsageAfterInvalidationCoreAsync(
		Task previousRefresh,
		Guid accountId,
		Func<bool>? tryBeginProviderAccountCommit,
		Guid? onlyAccountId = null)
	{
		bool previousRefreshWasActive = !previousRefresh.IsCompleted;
		AccountUsageViewModel? account = Accounts.FirstOrDefault(
			candidate => candidate.Id == accountId);
		if ((account is null) ||
			IsAccountCleanupBlocked(account.Id, account.Provider))
		{
			return;
		}
		long lifecycleRevision = account?.LifecycleRevision ?? -1;
		UsageSnapshot? fallbackSnapshot =
			account?.GetProviderAccountChangeFallbackSnapshot();
		bool shouldPreserveConfirmedClaudeHistory =
			(account?.IsClaude == true) &&
			(fallbackSnapshot is not null) &&
			(fallbackSnapshot.SubscriptionVerificationState ==
				SubscriptionVerificationState.Verified);

		_usageRefreshCoordinator.Invalidate(accountId);

		bool wasCachedSnapshotDeleted = false;
		if ((account is not null) && !shouldPreserveConfirmedClaudeHistory)
		{
			wasCachedSnapshotDeleted = await DeleteCachedSnapshotSafelyAsync(
				account.Id,
				account.Provider,
				CancellationToken.None);
		}

		try
		{
			await previousRefresh;
		}
		catch
		{
			// A forced refresh still gets one new attempt after an earlier failure.
		}

		if (previousRefreshWasActive &&
			(account is not null) &&
			!shouldPreserveConfirmedClaudeHistory)
		{
			wasCachedSnapshotDeleted = await DeleteCachedSnapshotSafelyAsync(
				account.Id,
				account.Provider,
				CancellationToken.None);
		}

		if (wasCachedSnapshotDeleted)
		{
			ResolveProviderAccountCacheCleanupFailure(account!.Id);
		}

		Task refreshTask;

		lock (_refreshSync)
		{
			if (_isRefreshStopped ||
				_isUpdateShutdownReserved ||
				(account is null) ||
				(!account.CanQueryUsage &&
					!account.IsProviderAccountChangeInProgress) ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevision))
			{
				return;
			}

			if (fallbackSnapshot is not null)
			{
				_usageRefreshCoordinator.SeedLastKnownGood(
					fallbackSnapshot with { Account = account.Profile });
			}

			refreshTask = RefreshUsageCoreAsync(
				accountId,
				tryBeginProviderAccountCommit:
					tryBeginProviderAccountCommit,
				onlyAccountId: onlyAccountId);
		}

		await refreshTask;
	}

	private async Task<UsageSnapshot> RefreshReservedAccountSafelyAsync(
		AccountUsageViewModel account)
	{
		try
		{
			if (IsInactiveAntigravityAccount(account))
			{
				return CreateInactiveAntigravitySnapshot(account);
			}

			try
			{
				AccountProfile refreshProfile = account.Profile;

				if (string.IsNullOrWhiteSpace(
						refreshProfile.ProviderAccountIdentity) &&
					IsRuntimeOnlyCachedProviderAccountIdentity(account.Id) &&
					(GetRuntimeOnlyCachedProviderAccountIdentity(account) is
						string runtimeProviderAccountIdentity))
				{
					refreshProfile = refreshProfile with
					{
						ProviderAccountIdentity = runtimeProviderAccountIdentity
					};
				}

				return await _usageRefreshCoordinator.RefreshAsync(refreshProfile);
			}
			catch (Exception)
			{
				return new UsageSnapshot(
					account.Profile,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					DateTimeOffset.UtcNow,
					Error: "用量檢查失敗，稍後會自動再試。",
					RecoveryAction: UsageRecoveryAction.Retry);
			}
		}
		finally
		{
			EndAccountRefresh(account.Id);
		}
	}

	private async ValueTask<AntigravityReportedAccountRefreshAssociation?>
		BeginAntigravityReportedAccountRefreshAssociationAsync(
			CancellationToken cancellationToken = default)
	{
		if (_antigravityReportedAccountSource is null)
		{
			return null;
		}

		try
		{
			AntigravityReportedAccountObservation baseline =
				await ReadAntigravityReportedAccountWithinBudgetAsync(
					cancellationToken);
			return new AntigravityReportedAccountRefreshAssociation(
				baseline.Generation,
				_timeProvider.GetUtcNow(),
				baseline.Status ==
					AntigravityReportedAccountObservationStatus.Available);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Display-only metadata must never make usage refresh fail.
			return new AntigravityReportedAccountRefreshAssociation(
				BaselineObservationGeneration: 0,
				RefreshStartedAtUtc: _timeProvider.GetUtcNow(),
				IsBaselineAvailable: false);
		}
	}

	private async ValueTask RefreshAntigravityReportedAccountDisplayAsync(
		CancellationToken cancellationToken = default)
	{
		await RefreshAntigravityReportedAccountDisplayAsync(
			association: null,
			requireFreshAssociation: false,
			cancellationToken: cancellationToken);
	}

	private async ValueTask RestoreAntigravityReportedAccountDisplayAsync(
		CancellationToken cancellationToken = default)
	{
		await RefreshAntigravityReportedAccountDisplayAsync(
			association: null,
			requireFreshAssociation: false,
			cancellationToken: cancellationToken,
			reconciliationTargets: null,
			allowCachedAssociation: true);
	}

	private async ValueTask RefreshAntigravityReportedAccountDisplayAsync(
		AntigravityReportedAccountRefreshAssociation? association,
		IReadOnlyCollection<AccountUsageViewModel> reconciliationTargets,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(reconciliationTargets);
		await RefreshAntigravityReportedAccountDisplayAsync(
			association,
			requireFreshAssociation: true,
			cancellationToken,
			reconciliationTargets);
	}

	private async ValueTask RefreshAntigravityReportedAccountDisplayAsync(
		AntigravityReportedAccountRefreshAssociation? association,
		bool requireFreshAssociation,
		CancellationToken cancellationToken,
		IReadOnlyCollection<AccountUsageViewModel>? reconciliationTargets = null,
		bool allowCachedAssociation = false)
	{
		if (requireFreshAssociation && allowCachedAssociation)
		{
			throw new ArgumentException(
				"Fresh and cached Antigravity account association cannot run together.",
				nameof(allowCachedAssociation));
		}

		if (requireFreshAssociation &&
			((reconciliationTargets is null) ||
				(reconciliationTargets.Count == 0)))
		{
			throw new ArgumentException(
				"Fresh Antigravity account reconciliation requires at least one target.",
				nameof(reconciliationTargets));
		}

		if (_antigravityReportedAccountSource is null)
		{
			if (requireFreshAssociation)
			{
				ClearAntigravityReportedAccountDisplay(
					reconciliationTargets);
			}
			else if (HasEnabledAntigravityAccountChangeInProgress())
			{
				MarkAntigravityReportedAccountDisplayStale(
					Accounts
						.Where(candidate => candidate.IsAntigravity)
						.ToArray());
			}
			else
			{
				ClearAntigravityReportedAccountDisplay();
			}

			return;
		}

		if (!HasConnectedEnabledAntigravityAccount() &&
			!HasEnabledAntigravityAccountCommitInProgress())
		{
			if (HasEnabledAntigravityAccountChangeInProgress())
			{
				MarkAntigravityReportedAccountDisplayStale(
					Accounts
						.Where(candidate => candidate.IsAntigravity)
						.ToArray());
				return;
			}

			ClearAntigravityReportedAccountDisplay();

			await TryRemoveAntigravityReportedAccountArtifactsAsync(
				cancellationToken);
			return;
		}

		ResetAntigravityReportedAccountRemovalState();
		AntigravityReportedAccountObservation observation;

		try
		{
			observation = await ReadAntigravityReportedAccountWithinBudgetAsync(
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			if (requireFreshAssociation)
			{
				MarkAntigravityReportedAccountDisplayStale(
					reconciliationTargets);
			}
			else
			{
				MarkAntigravityReportedAccountDisplayStale(
					Accounts
						.Where(candidate => candidate.IsAntigravity)
						.ToArray());
			}

			// Display-only metadata must never make usage refresh fail.
			return;
		}

		if (observation.Status ==
			AntigravityReportedAccountObservationStatus.Unavailable)
		{
			MarkAntigravityReportedAccountDisplayStale(
				requireFreshAssociation
					? reconciliationTargets
					: Accounts
						.Where(candidate => candidate.IsAntigravity)
						.ToArray());
			return;
		}

		AntigravityReportedAccount? reportedAccount = observation.Account;
		bool hasFreshStatusLineInvocation =
			observation.HasFreshStatusLineInvocation;

		if (!requireFreshAssociation)
		{
			// Initialization and account-setting changes may perform source
			// maintenance, including one-time legacy cleanup. They did not run a
			// new quota query, so missing fresh metadata cannot invalidate a
			// previously verified display by itself.
			if (allowCachedAssociation)
			{
				await RestoreAntigravityReportedAccountDisplayAsync(
					reportedAccount,
					cancellationToken);
			}

			return;
		}

		if ((association is not null) && !association.IsBaselineAvailable)
		{
			// A post-refresh observation cannot be associated safely when the
			// pre-refresh boundary was unavailable. Keep the old verified display
			// stale; a later refresh with an available baseline may restore or
			// switch it using a complete evidence pair.
			MarkAntigravityReportedAccountDisplayStale(reconciliationTargets);
			return;
		}

		if ((association is null) ||
			(observation.Generation <= association.BaselineObservationGeneration) ||
			(reportedAccount is null) ||
			(!hasFreshStatusLineInvocation &&
				(reportedAccount.CapturedAtUtc <
					association.RefreshStartedAtUtc)))
		{
			ClearAntigravityReportedAccountDisplay(reconciliationTargets);
			return;
		}

		// A same-account observation may be intentionally left unchanged for up
		// to 30 seconds. A source-specific freshness signal proves it was seen
		// after the refresh baseline. Use the refresh start as a conservative
		// lower-bound timestamp for association with the quota snapshot.
		DateTimeOffset accountObservedAtUtc = hasFreshStatusLineInvocation
			? association.RefreshStartedAtUtc
			: reportedAccount.CapturedAtUtc;
		DateTimeOffset now = _timeProvider.GetUtcNow();

		if (accountObservedAtUtc >
			now + AntigravityReportedAccountFutureTolerance)
		{
			ClearAntigravityReportedAccountDisplay(reconciliationTargets);
			return;
		}

		bool isStale =
			now - accountObservedAtUtc >
			AntigravityReportedAccountFreshness;

		(AccountUsageViewModel Account, TimeSpan AssociationLag)[]
			associationCandidates = reconciliationTargets!
				.Where(account =>
					account.IsAntigravity && Accounts.Contains(account))
				.Select(account =>
				{
					bool canAssociate = account
						.TryGetAntigravityReportedAccountAssociationLag(
							accountObservedAtUtc,
							AntigravityReportedAccountAssociationWindow,
							out TimeSpan associationLag);
					return (Account: account, CanAssociate: canAssociate,
						AssociationLag: associationLag);
				})
				.Where(candidate => candidate.CanAssociate)
				.OrderBy(candidate => candidate.AssociationLag)
				.Select(candidate => (
					candidate.Account,
					candidate.AssociationLag))
				.ToArray();
		AccountUsageViewModel? associatedAccount =
			associationCandidates.Length == 0 ||
			((associationCandidates.Length > 1) &&
				(associationCandidates[0].AssociationLag ==
					associationCandidates[1].AssociationLag))
				? null
				: associationCandidates[0].Account;

		foreach (AccountUsageViewModel account in reconciliationTargets!.Where(
			candidate => candidate.IsAntigravity && Accounts.Contains(candidate)))
		{
			if (ReferenceEquals(account, associatedAccount))
			{
				if (!account.TrySetAntigravityReportedAccountEmail(
					reportedAccount.Email,
					accountObservedAtUtc,
					association.RefreshStartedAtUtc,
					AntigravityReportedAccountAssociationWindow,
					isStale,
					planTier: reportedAccount.PlanTier))
				{
					account.ClearAntigravityReportedAccountEmail();
				}
				else
				{
					await TrySaveAntigravityVerifiedAccountBindingAsync(
						account,
						reportedAccount,
						accountObservedAtUtc,
						cancellationToken);
				}
			}
			else
			{
				account.ClearAntigravityReportedAccountEmail();
			}
		}
	}

	private async ValueTask<AntigravityReportedAccountObservation>
		ReadAntigravityReportedAccountWithinBudgetAsync(
			CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(
			MaximumAntigravityReportedAccountReadDuration);

		try
		{
			return await _antigravityReportedAccountSource!.ReadAsync(
				timeoutSource.Token).AsTask().WaitAsync(
					MaximumAntigravityReportedAccountReadDuration,
					cancellationToken);
		}
		catch (OperationCanceledException) when (
			!cancellationToken.IsCancellationRequested &&
			timeoutSource.IsCancellationRequested)
		{
			throw new TimeoutException(
				"Antigravity account-display metadata exceeded its read budget.");
		}
	}

	private async ValueTask RestoreAntigravityReportedAccountDisplayAsync(
		AntigravityReportedAccount? reportedAccount,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = _timeProvider.GetUtcNow();
		if (reportedAccount is null ||
			!AntigravityAccountMetadataValidator.IsValidAsciiEmail(
				reportedAccount.Email) ||
			(reportedAccount.CapturedAtUtc >
				now + AntigravityReportedAccountFutureTolerance))
		{
			return;
		}

		AccountUsageViewModel[] connectedAccounts = Accounts
			.Where(account =>
				account.IsAntigravity &&
				account.IsEnabled &&
				IsPrimaryEnabledAntigravityAccount(account.Id) &&
				string.Equals(
					account.ProviderAccountIdentity,
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
					StringComparison.OrdinalIgnoreCase))
			.ToArray();

		AntigravityVerifiedAccountBinding? existingBinding =
			connectedAccounts.Length == 1
				? await LoadAntigravityVerifiedAccountBindingAsync(
					connectedAccounts[0].Id,
					cancellationToken)
				: null;

		if ((connectedAccounts.Length == 1) && (existingBinding is not null))
		{
			UsageSnapshot? currentSnapshot =
				connectedAccounts[0].CurrentSnapshot;
			DateTimeOffset? currentQuotaObservedAtUtc = currentSnapshot is null
				? null
				: currentSnapshot.ObservedAt ?? currentSnapshot.FetchedAt;
			bool hasUnambiguousEvidenceOrder =
				existingBinding.SourceCapturedAtUtc <=
					existingBinding.QuotaObservedAtUtc &&
				existingBinding.QuotaObservedAtUtc -
					existingBinding.SourceCapturedAtUtc <=
					AntigravityReportedAccountAssociationWindow &&
				existingBinding.VerifiedAtUtc >=
					existingBinding.QuotaObservedAtUtc;
			bool matchesCurrentQuota = hasUnambiguousEvidenceOrder &&
				currentQuotaObservedAtUtc == existingBinding.QuotaObservedAtUtc;

			if (matchesCurrentQuota && string.Equals(
				existingBinding.NormalizedEmailSha256,
				AntigravityVerifiedAccountBinding
					.ComputeNormalizedEmailSha256(reportedAccount.Email),
				StringComparison.Ordinal))
			{
				connectedAccounts[0]
					.TryRestoreVerifiedAntigravityReportedAccountEmail(
						reportedAccount.Email,
						reportedAccount.PlanTier);
			}

			// A hash mismatch means the raw capture belongs to a different login.
			// A quota timestamp mismatch means a later quota was not verified with
			// this binding. Neither case may restore the old email or overwrite the
			// binding through the legacy cache-migration path below.
			return;
		}

		if (_antigravityVerifiedAccountBindingStore is not null)
		{
			// A missing or unreadable binding is not evidence that the raw capture
			// belongs to the cached quota. Wait for the next live quota/status-line
			// pair instead of recreating an identity from ambiguous legacy data.
			return;
		}

		bool isFreshRawCapture =
			now - reportedAccount.CapturedAtUtc <=
			AntigravityReportedAccountFreshness;
		if (!isFreshRawCapture)
		{
			return;
		}

		// Store-less callers can still show a fresh, correlated capture for this
		// process. They cannot persist it as a verified restart boundary.

		(AccountUsageViewModel Account, TimeSpan AssociationLag)[] candidates =
			Accounts
				.Where(account =>
					account.IsAntigravity &&
					account.IsEnabled &&
					IsPrimaryEnabledAntigravityAccount(account.Id))
				.Select(account =>
				{
					bool canAssociate = account
						.TryGetAntigravityReportedAccountAssociationLag(
							reportedAccount.CapturedAtUtc,
							AntigravityReportedAccountAssociationWindow,
							out TimeSpan associationLag,
							allowStaleSnapshot: true);
					return (Account: account, CanAssociate: canAssociate,
						AssociationLag: associationLag);
				})
				.Where(candidate => candidate.CanAssociate)
				.OrderBy(candidate => candidate.AssociationLag)
				.Select(candidate => (
					candidate.Account,
					candidate.AssociationLag))
				.ToArray();
		AccountUsageViewModel? associatedAccount =
			candidates.Length == 0 ||
			((candidates.Length > 1) &&
				(candidates[0].AssociationLag ==
					candidates[1].AssociationLag))
				? null
				: candidates[0].Account;

		if (associatedAccount?.TrySetAntigravityReportedAccountEmail(
			reportedAccount.Email,
			reportedAccount.CapturedAtUtc,
			DateTimeOffset.MinValue,
			AntigravityReportedAccountAssociationWindow,
			isStale: true,
			allowStaleSnapshot: true,
			planTier: reportedAccount.PlanTier) == true)
		{
			await TrySaveAntigravityVerifiedAccountBindingAsync(
				associatedAccount,
				reportedAccount,
				reportedAccount.CapturedAtUtc,
				cancellationToken,
				allowStaleQuotaSnapshot: true);
		}
	}

	private async ValueTask<AntigravityVerifiedAccountBinding?>
		LoadAntigravityVerifiedAccountBindingAsync(
			Guid accountId,
			CancellationToken cancellationToken)
	{
		if (_antigravityVerifiedAccountBindingStore is null)
		{
			return null;
		}

		try
		{
			return await _antigravityVerifiedAccountBindingStore.LoadAsync(
				accountId,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Display-only metadata must never make initialization fail.
			return null;
		}
	}

	private async ValueTask TrySaveAntigravityVerifiedAccountBindingAsync(
		AccountUsageViewModel account,
		AntigravityReportedAccount reportedAccount,
		DateTimeOffset sourceObservedAtUtc,
		CancellationToken cancellationToken,
		bool allowStaleQuotaSnapshot = false)
	{
		UsageSnapshot? snapshot = account.CurrentSnapshot;
		if ((_antigravityVerifiedAccountBindingStore is null) ||
			!IsUsableAntigravityQuotaSnapshotForVerifiedBinding(
				snapshot,
				allowStaleQuotaSnapshot))
		{
			return;
		}

		try
		{
			DateTimeOffset quotaObservedAtUtc =
				snapshot!.ObservedAt ?? snapshot.FetchedAt;
			DateTimeOffset verifiedAtUtc = _timeProvider.GetUtcNow();
			DateTimeOffset latestEvidenceAtUtc =
				sourceObservedAtUtc >= quotaObservedAtUtc
					? sourceObservedAtUtc
					: quotaObservedAtUtc;
			if (verifiedAtUtc < latestEvidenceAtUtc)
			{
				verifiedAtUtc = latestEvidenceAtUtc;
			}
			await _antigravityVerifiedAccountBindingStore.SaveAsync(
				new AntigravityVerifiedAccountBinding(
					account.Id,
					AntigravityVerifiedAccountBinding
						.ComputeNormalizedEmailSha256(reportedAccount.Email),
					sourceObservedAtUtc,
					quotaObservedAtUtc,
					verifiedAtUtc),
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Display continuity is best effort and must not fail usage refresh.
		}
	}

	private static bool IsUsableAntigravityQuotaSnapshotForVerifiedBinding(
		UsageSnapshot? snapshot,
		bool allowStaleQuotaSnapshot)
	{
		return IsSuccessfulAntigravityQuotaSnapshot(snapshot) ||
			(allowStaleQuotaSnapshot &&
			 (snapshot is not null) &&
			 (snapshot.Account.Provider == ProviderKind.Antigravity) &&
			 (snapshot.Status == SnapshotStatus.Stale) &&
			 (snapshot.SourceTrust == SourceTrust.OfficialExperimental) &&
			 (snapshot.Metrics.Count > 0) &&
			 string.Equals(
				snapshot.ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase));
	}

	private bool HasConnectedEnabledAntigravityAccount()
	{
		return Accounts.Any(account =>
			account.IsAntigravity &&
			account.IsEnabled &&
			string.Equals(
				account.ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase));
	}

	private bool HasEnabledAntigravityAccountCommitInProgress()
	{
		return Accounts.Any(account =>
			account.IsAntigravity &&
			account.IsEnabled &&
			account.IsProviderAccountChangeCommitInProgress &&
			account.HasConfirmedProviderAccountBinding);
	}

	private bool HasEnabledAntigravityAccountChangeInProgress()
	{
		return Accounts.Any(account =>
			account.IsAntigravity &&
			account.IsEnabled &&
			account.IsProviderAccountChangeInProgress);
	}

	private async ValueTask TryRemoveAntigravityReportedAccountArtifactsAsync(
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = _timeProvider.GetUtcNow();

		if (_isAntigravityReportedAccountRemovalComplete ||
			(_antigravityReportedAccountRemovalAttemptCount >=
				MaximumAntigravityReportedAccountRemovalAttempts) ||
			(now < _nextAntigravityReportedAccountRemovalAttemptUtc))
		{
			return;
		}

		bool removed = false;

		try
		{
			removed = await _antigravityReportedAccountSource!.RemoveOwnedAsync(
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Lifecycle cleanup is best-effort and must not fail account UI.
		}

		_antigravityReportedAccountRemovalAttemptCount++;

		if (removed)
		{
			_isAntigravityReportedAccountRemovalComplete = true;
			return;
		}

		if (_antigravityReportedAccountRemovalAttemptCount <
			MaximumAntigravityReportedAccountRemovalAttempts)
		{
			TimeSpan retryDelay = _antigravityReportedAccountRemovalAttemptCount == 1
				? TimeSpan.FromMinutes(1)
				: TimeSpan.FromMinutes(2);
			_nextAntigravityReportedAccountRemovalAttemptUtc = now + retryDelay;
		}
	}

	private void ResetAntigravityReportedAccountRemovalState()
	{
		_isAntigravityReportedAccountRemovalComplete = false;
		_antigravityReportedAccountRemovalAttemptCount = 0;
		_nextAntigravityReportedAccountRemovalAttemptUtc =
			DateTimeOffset.MinValue;
	}

	private void ClearAntigravityReportedAccountDisplay()
	{
		ClearAntigravityReportedAccountDisplay(
			Accounts.Where(candidate => candidate.IsAntigravity).ToArray());
	}

	private static void ClearAntigravityReportedAccountDisplay(
		IReadOnlyCollection<AccountUsageViewModel>? accounts)
	{
		foreach (AccountUsageViewModel account in accounts ??
			Array.Empty<AccountUsageViewModel>())
		{
			account.ClearAntigravityReportedAccountEmail();
		}
	}

	private static void MarkAntigravityReportedAccountDisplayStale(
		IReadOnlyCollection<AccountUsageViewModel>? accounts)
	{
		foreach (AccountUsageViewModel account in accounts ??
			Array.Empty<AccountUsageViewModel>())
		{
			account.MarkAntigravityReportedAccountEmailStale();
		}
	}

	private static bool IsSuccessfulAntigravityQuotaSnapshot(
		UsageSnapshot? snapshot)
	{
		return (snapshot is not null) &&
			(snapshot.Account.Provider == ProviderKind.Antigravity) &&
			(snapshot.Status == SnapshotStatus.Ready) &&
			(snapshot.SourceTrust == SourceTrust.OfficialExperimental) &&
			string.IsNullOrWhiteSpace(snapshot.Error) &&
			(snapshot.Metrics.Count > 0) &&
			string.Equals(
				snapshot.ProviderAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase);
	}

	private void MarkAntigravityReportedAccountsStale(
		IReadOnlyCollection<Guid> liveTargetIds,
		IReadOnlyCollection<AccountUsageViewModel> successfulTargets)
	{
		HashSet<Guid> successfulTargetIds = successfulTargets
			.Select(account => account.Id)
			.ToHashSet();

		foreach (AccountUsageViewModel account in Accounts.Where(account =>
			liveTargetIds.Contains(account.Id) &&
			!successfulTargetIds.Contains(account.Id)))
		{
			account.MarkAntigravityReportedAccountEmailStale();
		}
	}

	private void BeginAccountRefresh(Guid accountId)
	{
		lock (_refreshSync)
		{
			if (!_accountRefreshActivities.TryGetValue(
				accountId,
				out AccountRefreshActivity? activity))
			{
				activity = new AccountRefreshActivity();
				_accountRefreshActivities.Add(accountId, activity);
			}

			activity.Count++;
		}
	}

	private void EndAccountRefresh(Guid accountId)
	{
		AccountRefreshActivity? completedActivity = null;

		lock (_refreshSync)
		{
			if (!_accountRefreshActivities.TryGetValue(
				accountId,
				out AccountRefreshActivity? activity))
			{
				return;
			}

			activity.Count--;

			if (activity.Count == 0)
			{
				_accountRefreshActivities.Remove(accountId);
				completedActivity = activity;
			}
		}

		completedActivity?.IdleCompletion.TrySetResult(true);
	}

	private UsageSnapshot? GetManualRefreshFallbackSnapshot(
		AccountUsageViewModel account)
	{
		UsageSnapshot? snapshot = account.CurrentSnapshot;

		if ((snapshot is null) ||
			!account.CanPersistCurrentSnapshot)
		{
			return null;
		}

		return PrepareManualRefreshFallbackSnapshot(
			snapshot,
			account.Profile,
			_timeProvider.GetUtcNow());
	}

	private static UsageSnapshot? PrepareManualRefreshFallbackSnapshot(
		UsageSnapshot? snapshot,
		AccountProfile account,
		DateTimeOffset now,
		bool allowEmptyMetrics = false)
	{
		if ((snapshot is null) ||
			((snapshot.Status != SnapshotStatus.Ready) &&
				(snapshot.Status != SnapshotStatus.Stale)) ||
			IsTerminalIdentityRecovery(snapshot.RecoveryAction))
		{
			return null;
		}

		UsageMetric[] currentMetrics = snapshot.Metrics
			.Where(metric =>
				(metric.ResetsAt is null) || (metric.ResetsAt.Value > now))
			.ToArray();

		return (currentMetrics.Length == 0) && !allowEmptyMetrics
			? null
			: snapshot with
			{
				Account = account,
				Metrics = currentMetrics
			};
	}

	private static bool IsTerminalIdentityRecovery(
		UsageRecoveryAction recoveryAction)
	{
		return (recoveryAction == UsageRecoveryAction.ConnectAccount) ||
			(recoveryAction == UsageRecoveryAction.SwitchAccount) ||
			(recoveryAction == UsageRecoveryAction.ConfirmSubscription);
	}

	private static async Task ObserveRefreshCompletionAsync(Task refreshTask)
	{
		try
		{
			await refreshTask;
		}
		catch
		{
			// Shutdown drain only waits for provider cleanup; refresh failures are
			// already projected to account state by the normal refresh pipeline.
		}
	}

	private TimeSpan? GetRemainingRefreshCooldown(AccountProfile account)
	{
		try
		{
			return _usageRefreshCoordinator.GetRemainingCooldown(account);
		}
		catch
		{
			return null;
		}
	}

	private UsageSnapshot CreateInactiveAntigravitySnapshot(
		AccountUsageViewModel account)
	{
		return new UsageSnapshot(
			account.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: InactiveAntigravityAccountMessage,
			RecoveryAction: UsageRecoveryAction.None);
	}

	private bool IsInactiveAntigravityAccount(
		AccountUsageViewModel account)
	{
		if (!account.IsEnabled ||
			(account.Provider != ProviderKind.Antigravity))
		{
			return false;
		}

		return !IsPrimaryEnabledAntigravityAccount(account.Id);
	}

	private void ReportRefreshCooldown(
		TimeSpan remaining,
		bool hasDisplayedSnapshot,
		bool isAutomaticRetry)
	{
		double totalSeconds = Math.Max(1, Math.Ceiling(remaining.TotalSeconds));
		string waitText = totalSeconds >= 60
			? $"{Math.Ceiling(totalSeconds / 60)} 分鐘"
			: $"{totalSeconds:0} 秒";
		string dataStatus = hasDisplayedSnapshot
			? "目前仍顯示上次資料。"
			: "目前尚無可顯示的用量資料。";
		if (isAutomaticRetry)
		{
			SetRefreshStatus(
				$"AI Usage 已排定自動重試，將在 {waitText}後再次檢查。{dataStatus}",
				new CompactRefreshStatus("稍後自動再試", true));
			return;
		}

		SetRefreshStatus(
			$"更新過於頻繁，請在 {waitText}後再試。{dataStatus}",
			new CompactRefreshStatus("稍後可更新", true));
	}

	private void ReportRefreshResult(
		IReadOnlyCollection<UsageSnapshot> snapshots,
		int coolingDownCount)
	{
		if (snapshots.Count == 0)
		{
			SetRefreshStatus(
				NoRefreshTargetsMessage,
				CompactRefreshStatus.Hidden);
			return;
		}

		int readyCount = snapshots.Count(
			snapshot => snapshot.Status == SnapshotStatus.Ready);
		int staleCount = snapshots.Count(
			snapshot => snapshot.Status == SnapshotStatus.Stale);
		int failureCount = snapshots.Count(
			snapshot => snapshot.Status == SnapshotStatus.Error);
		int notConfiguredCount = snapshots.Count(
			snapshot => snapshot.Status == SnapshotStatus.NotConfigured);
		int unsupportedCount = snapshots.Count(
			snapshot => snapshot.Status == SnapshotStatus.Unsupported);
		List<string> statusParts = new();

		AddRefreshStatusPart(statusParts, "可用", readyCount);
		AddRefreshStatusPart(statusParts, "舊資料", staleCount);
		AddRefreshStatusPart(statusParts, "失敗", failureCount);
		AddRefreshStatusPart(statusParts, "待設定", notConfiguredCount);
		AddRefreshStatusPart(statusParts, "不支援", unsupportedCount);

		int otherCount = snapshots.Count -
			readyCount -
			staleCount -
			failureCount -
			notConfiguredCount -
			unsupportedCount;
		AddRefreshStatusPart(statusParts, "其他", otherCount);

		if (coolingDownCount > 0)
		{
			statusParts.Add($"稍後可更新 {coolingDownCount}");
		}

		DateTimeOffset? oldestDataAt = snapshots
			.Where(snapshot =>
				((snapshot.Status == SnapshotStatus.Ready) ||
					(snapshot.Status == SnapshotStatus.Stale)) &&
				(snapshot.Metrics.Count > 0))
			.Select(snapshot => (DateTimeOffset?)(
				snapshot.ObservedAt ?? snapshot.FetchedAt))
			.Min();
		string dataTimeText = oldestDataAt is null
			? string.Empty
			: $"；最舊資料 {oldestDataAt.Value.ToLocalTime():MM/dd HH:mm:ss}";
		SetRefreshStatus(
			$"檢查完成：{string.Join("、", statusParts)}{dataTimeText}",
			CompactRefreshStatusPolicy.CreateResult(
				snapshots,
				coolingDownCount,
				TimeProvider.System.GetLocalNow()));
	}

	private static void AddRefreshStatusPart(
		ICollection<string> parts,
		string label,
		int count)
	{
		if (count > 0)
		{
			parts.Add($"{label} {count}");
		}
	}

	private void SetRefreshStatus(
		string fullText,
		CompactRefreshStatus compactStatus)
	{
		LastRefreshText = fullText;
		CompactRefreshStatus = compactStatus;
	}

	internal bool IsAccountCleanupBlocked(
		Guid accountId,
		ProviderKind provider)
	{
		lock (_cleanupStateSync)
		{
			return _blockAllAccountsForCleanup ||
				_cleanupBlockedAccountKeys.Contains((accountId, provider));
		}
	}

	private void BeginRefreshOperation()
	{
		lock (_refreshSync)
		{
			_activeRefreshOperationCount++;
			if (_activeRefreshOperationCount == 1)
			{
				IsRefreshing = true;
			}
		}
	}

	private void EndRefreshOperation()
	{
		lock (_refreshSync)
		{
			if (_activeRefreshOperationCount <= 0)
			{
				throw new InvalidOperationException(
					"用量更新作業計數不可小於零。");
			}

			_activeRefreshOperationCount--;
			if (_activeRefreshOperationCount == 0)
			{
				IsRefreshing = false;
			}
		}
	}

	private void BlockAccountCleanups(
		IEnumerable<(Guid AccountId, ProviderKind Provider)> accountKeys)
	{
		bool changed = false;
		lock (_cleanupStateSync)
		{
			foreach (var accountKey in accountKeys)
			{
				changed |= _cleanupBlockedAccountKeys.Add(accountKey);
			}
		}

		if (changed)
		{
			OnPropertyChanged(nameof(CanRefresh));
		}
	}

	private void UnblockAccountCleanup(
		Guid accountId,
		ProviderKind provider)
	{
		bool changed;
		lock (_cleanupStateSync)
		{
			changed = _cleanupBlockedAccountKeys.Remove((accountId, provider));
		}

		if (changed)
		{
			OnPropertyChanged(nameof(CanRefresh));
		}
	}

	private void BlockAllAccountCleanups()
	{
		bool changed;
		lock (_cleanupStateSync)
		{
			changed = !_blockAllAccountsForCleanup;
			_blockAllAccountsForCleanup = true;
		}

		if (changed)
		{
			OnPropertyChanged(nameof(CanRefresh));
		}
	}

	private void ClearBlockAllAccountCleanups()
	{
		bool changed;
		lock (_cleanupStateSync)
		{
			changed = _blockAllAccountsForCleanup;
			_blockAllAccountsForCleanup = false;
		}

		if (changed)
		{
			OnPropertyChanged(nameof(CanRefresh));
		}
	}

	private (Guid AccountId, ProviderKind Provider)[]
		GetBlockedAccountCleanupKeys()
	{
		lock (_cleanupStateSync)
		{
			return _cleanupBlockedAccountKeys.ToArray();
		}
	}

	private bool HasBlockedAccountCleanups()
	{
		lock (_cleanupStateSync)
		{
			return _blockAllAccountsForCleanup ||
				(_cleanupBlockedAccountKeys.Count > 0);
		}
	}

	private bool IsAccountCleanupRetryDue()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			return _timeProvider.GetUtcNow().ToUniversalTime() >=
				_accountCleanupRetryNotBeforeUtc;
		}
	}

	private void RecordAccountCleanupRetryResult(bool completed)
	{
		if (completed)
		{
			ResetAccountCleanupRetryBackoff();
			return;
		}

		lock (_backgroundPersistenceRetrySync)
		{
			_accountCleanupRetryFailureCount = Math.Min(
				BackgroundPersistenceRetryDelays.Length,
				_accountCleanupRetryFailureCount + 1);
			_accountCleanupRetryNotBeforeUtc =
				_timeProvider.GetUtcNow().ToUniversalTime() +
				BackgroundPersistenceRetryDelays[
					_accountCleanupRetryFailureCount - 1];
		}
	}

	private void ResetAccountCleanupRetryBackoff()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			_accountCleanupRetryFailureCount = 0;
			_accountCleanupRetryNotBeforeUtc = DateTimeOffset.MinValue;
		}
	}

	private bool IsProviderIdentityPersistenceRetryDue()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			return _timeProvider.GetUtcNow().ToUniversalTime() >=
				_providerIdentityPersistenceRetryNotBeforeUtc;
		}
	}

	private void RecordProviderIdentityPersistenceFailure()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			_providerIdentityPersistenceRetryFailureCount = Math.Min(
				BackgroundPersistenceRetryDelays.Length,
				_providerIdentityPersistenceRetryFailureCount + 1);
			_providerIdentityPersistenceRetryNotBeforeUtc =
				_timeProvider.GetUtcNow().ToUniversalTime() +
				BackgroundPersistenceRetryDelays[
					_providerIdentityPersistenceRetryFailureCount - 1];
		}
	}

	private void ResetProviderIdentityPersistenceRetryBackoff()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			_providerIdentityPersistenceRetryFailureCount = 0;
			_providerIdentityPersistenceRetryNotBeforeUtc =
				DateTimeOffset.MinValue;
		}
	}

	private async Task AuthorizeCommittedPortableCopilotCleanupSafelyAsync(
		IReadOnlyCollection<AccountProfile> retainedProfiles,
		CancellationToken cancellationToken)
	{
		(Guid AccountId, ProviderKind Provider)[] accountKeys = retainedProfiles
			.Where(profile => profile.Provider == ProviderKind.Copilot)
			.Select(profile => (profile.Id, profile.Provider))
			.Distinct()
			.ToArray();
		if (accountKeys.Length == 0)
		{
			return;
		}

		await AuthorizeRetainedPrivateStateDeletionSafelyAsync(
			accountKeys,
			cancellationToken);
	}

	private async Task<bool> AuthorizeRetainedPrivateStateDeletionSafelyAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken)
	{
		if (accountKeys.Count == 0)
		{
			return true;
		}

		lock (_cleanupStateSync)
		{
			_retainedPrivateStateDeletionAuthorizedAccountKeys.UnionWith(
				accountKeys);
		}

		if (_accountCleanupPendingStore is null)
		{
			return true;
		}

		try
		{
			await _accountCleanupPendingStore
				.AuthorizeRetainedPrivateStateDeletionAsync(
					accountKeys,
					cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// 已完成的匯入仍可在本次執行清理；未持久化授權時，重啟後會保留 retained credential。
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return false;
		}
	}

	private async Task EnsurePortableImportRecoveryBeforeCleanupIntentAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken)
	{
		if (_portableSettingsImportTransaction is null)
		{
			return;
		}

		try
		{
			await _portableSettingsImportTransaction
				.RecoverInterruptedImportAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			throw new InvalidOperationException(
				"先前設定變更的帳號清理尚未完成。請先重試清理或重新啟動 AI Usage，再變更帳號設定。",
				exception);
		}

		if (_accountCleanupPendingStore is null)
		{
			return;
		}

		HashSet<(Guid AccountId, ProviderKind Provider)> cleanupKeys =
			accountKeys.ToHashSet();

		bool HasOverlappingAuthorizedWork(
			IEnumerable<AccountCleanupPendingWork> pendingWork)
		{
			return pendingWork.Any(work =>
				work.RuntimePending &&
				work.AllowRetainedPrivateStateDeletion &&
				cleanupKeys.Contains((work.AccountId, work.Provider)));
		}

		bool hasOverlappingAuthorizedWork;
		try
		{
			hasOverlappingAuthorizedWork = HasOverlappingAuthorizedWork(
				await _accountCleanupPendingStore.LoadAsync(cancellationToken));
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			throw new InvalidOperationException(
				"無法確認先前設定變更的帳號清理狀態，因此尚未變更帳號設定。",
				exception);
		}

		if (!hasOverlappingAuthorizedWork)
		{
			return;
		}

		await RecoverPendingAccountCleanupsAsync(
			cancellationToken,
			requireRefreshQuiescence: true,
			includeUnjournaledBlockedAccounts: true);

		try
		{
			hasOverlappingAuthorizedWork = HasOverlappingAuthorizedWork(
				await _accountCleanupPendingStore.LoadAsync(cancellationToken));
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			throw new InvalidOperationException(
				"無法確認先前設定變更的帳號清理狀態，因此尚未變更帳號設定。",
				exception);
		}

		if (hasOverlappingAuthorizedWork)
		{
			throw new InvalidOperationException(
				"先前設定變更的帳號清理尚未完成。請先重試清理或重新啟動 AI Usage，再變更帳號設定。");
		}
	}

	private async Task<IReadOnlyList<PrivateAccountCleanupIntentRollbackState>>
		PersistAccountCleanupIntentAsync(
		IReadOnlyCollection<AccountProfile> profiles,
		CancellationToken cancellationToken)
	{
		(Guid AccountId, ProviderKind Provider)[] accountKeys = profiles
			.Select(profile => (profile.Id, profile.Provider))
			.Distinct()
			.ToArray();
		if (accountKeys.Length == 0)
		{
			return Array.Empty<PrivateAccountCleanupIntentRollbackState>();
		}

		await EnsurePortableImportRecoveryBeforeCleanupIntentAsync(
			accountKeys,
			cancellationToken);

		Dictionary<(Guid AccountId, ProviderKind Provider), bool>
			previouslyBlockedPrivateAccountKeys = accountKeys
			.Where(accountKey =>
				HasDestructivePrivateAccountState(accountKey.Provider))
			.ToDictionary(
				accountKey => accountKey,
				accountKey => IsAccountCleanupBlocked(
					accountKey.AccountId,
					accountKey.Provider));
		HashSet<(Guid AccountId, ProviderKind Provider)>
			previouslyExplicitlyBlockedKeys = GetBlockedAccountCleanupKeys()
				.ToHashSet();
		BlockAccountCleanups(accountKeys);
		if (_accountCleanupPendingStore is null)
		{
			return previouslyBlockedPrivateAccountKeys
				.Select(item => new PrivateAccountCleanupIntentRollbackState(
					item.Key.AccountId,
					item.Key.Provider,
					item.Value,
					PreviousPendingWork: null))
				.ToArray();
		}

		try
		{
			Dictionary<(Guid AccountId, ProviderKind Provider),
				AccountCleanupPendingWork> previousPendingPrivateAccountWork =
				previouslyBlockedPrivateAccountKeys.Count == 0
					? new()
					: (await _accountCleanupPendingStore.LoadAsync(
						cancellationToken))
						.Where(work =>
							HasDestructivePrivateAccountState(work.Provider))
						.ToDictionary(
							work => (work.AccountId, work.Provider));
			PrivateAccountCleanupIntentRollbackState[] rollbackStates =
				previouslyBlockedPrivateAccountKeys.Select(item =>
				{
					previousPendingPrivateAccountWork.TryGetValue(
						item.Key,
						out AccountCleanupPendingWork? previousPendingWork);
					return new PrivateAccountCleanupIntentRollbackState(
						item.Key.AccountId,
						item.Key.Provider,
						item.Value,
						previousPendingWork);
				}).ToArray();
			await _accountCleanupPendingStore.UpsertAsync(
				accountKeys,
				cancellationToken);
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return rollbackStates;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			RestoreNewAccountCleanupBlocks(
				accountKeys,
				previouslyExplicitlyBlockedKeys);
			throw;
		}
		catch (Exception exception)
		{
			RestoreNewAccountCleanupBlocks(
				accountKeys,
				previouslyExplicitlyBlockedKeys);
			if (HasBlockedAccountCleanups())
			{
				AppendAccountSettingsHealthWarning(
					AccountCleanupPendingFailureMessage);
			}
			else
			{
				ClearAccountSettingsHealthWarning(
					AccountCleanupPendingFailureMessage);
			}
			throw new InvalidOperationException(
				"無法記錄帳號清理進度，因此帳號設定尚未變更。請確認 AI Usage 設定資料夾可寫入後再試一次。",
				exception);
		}
	}

	private void RestoreNewAccountCleanupBlocks(
		IEnumerable<(Guid AccountId, ProviderKind Provider)> accountKeys,
		IReadOnlySet<(Guid AccountId, ProviderKind Provider)>
			previouslyExplicitlyBlockedKeys)
	{
		foreach (var accountKey in accountKeys)
		{
			if (!previouslyExplicitlyBlockedKeys.Contains(accountKey))
			{
				UnblockAccountCleanup(accountKey.AccountId, accountKey.Provider);
			}
		}
	}

	private async Task RollbackFailedPrivateAccountCleanupIntentAsync(
		IReadOnlyCollection<PrivateAccountCleanupIntentRollbackState> rollbackStates,
		IReadOnlySet<(Guid AccountId, ProviderKind Provider)> originalAccountKeys)
	{
		foreach (PrivateAccountCleanupIntentRollbackState state in rollbackStates)
		{
			var accountKey = (state.AccountId, state.Provider);
			if (!HasDestructivePrivateAccountState(state.Provider) ||
				!originalAccountKeys.Contains(accountKey))
			{
				continue;
			}

			bool cacheRestored =
				(state.PreviousPendingWork?.CachePending == true) ||
				await MarkAccountCleanupComponentCompletedSafelyAsync(
					state.AccountId,
					state.Provider,
					isCacheComponent: true,
					CancellationToken.None);
			bool runtimeRestored =
				(state.PreviousPendingWork?.RuntimePending == true) ||
				await MarkAccountCleanupComponentCompletedSafelyAsync(
					state.AccountId,
					state.Provider,
					isCacheComponent: false,
					CancellationToken.None);
			bool retainedDeletionAuthorizationRestored =
				(state.PreviousPendingWork?
					.AllowRetainedPrivateStateDeletion != true) ||
				(runtimeRestored &&
					await AuthorizeRetainedPrivateStateDeletionSafelyAsync(
						[accountKey],
						CancellationToken.None));

			if (cacheRestored &&
				runtimeRestored &&
				retainedDeletionAuthorizationRestored &&
				(state.PreviousPendingWork is null) &&
				!state.WasPreviouslyBlocked)
			{
				UnblockAccountCleanup(state.AccountId, state.Provider);
			}
		}

		if (!HasBlockedAccountCleanups())
		{
			ClearAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}
	}

	internal async Task ScheduleAccountCleanupRetryAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"帳號識別碼不可為空。",
				nameof(accountId));
		}

		if (!Enum.IsDefined(provider))
		{
			throw new ArgumentOutOfRangeException(nameof(provider));
		}

		(Guid AccountId, ProviderKind Provider)[] accountKeys =
			[(accountId, provider)];
		BlockAccountCleanups(accountKeys);
		ResetAccountCleanupRetryBackoff();

		if (_accountCleanupPendingStore is null)
		{
			return;
		}

		try
		{
			await _accountCleanupPendingStore.UpsertAsync(
				accountKeys,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// The in-memory block remains. The next background cleanup pass
			// includes unjournaled blocked keys and retries this durable write.
		}

		AppendAccountSettingsHealthWarning(
			AccountCleanupPendingFailureMessage);
	}

	internal async Task<bool> RetryPendingAccountCleanupsNowAsync(
		CancellationToken cancellationToken = default)
	{
		if (!HasBlockedAccountCleanups())
		{
			ResetAccountCleanupRetryBackoff();
			return true;
		}

		if (!IsAccountCleanupRetryDue())
		{
			return false;
		}

		await _accountMutationGate.WaitAsync(cancellationToken);
		try
		{
			if (!HasBlockedAccountCleanups())
			{
				return true;
			}

			bool completed;
			if (_accountCleanupPendingStore is not null)
			{
				completed = await RecoverPendingAccountCleanupsAsync(
					cancellationToken,
					requireRefreshQuiescence: true,
					includeUnjournaledBlockedAccounts: true);
			}
			else
			{
				foreach (var accountKey in GetBlockedAccountCleanupKeys())
				{
					bool wasRefreshQuiesced =
						await QuiesceRemovedAccountRefreshSafelyAsync(
							accountKey.AccountId);
					await AttemptAccountCleanupAsync(
						accountKey.AccountId,
						accountKey.Provider,
						wasRefreshQuiesced,
						cachePending: true,
						runtimePending: true,
						cancellationToken);
				}

				completed = !HasBlockedAccountCleanups();
			}

			RecordAccountCleanupRetryResult(completed);
			return completed;
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	private async Task<bool> RecoverPendingAccountCleanupsAsync(
		CancellationToken cancellationToken,
		bool requireRefreshQuiescence = false,
		bool includeUnjournaledBlockedAccounts = false,
		bool deferDestructiveGrokCleanup = false)
	{
		if (_portableSettingsImportTransaction is not null)
		{
			try
			{
				await _portableSettingsImportTransaction
					.RecoverInterruptedImportAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				AppendAccountSettingsHealthWarning(
					AccountCleanupPendingFailureMessage);
				return false;
			}
		}

		if (_accountCleanupPendingStore is null)
		{
			return !HasBlockedAccountCleanups();
		}

		List<AccountCleanupPendingWork> pendingWork;
		try
		{
			pendingWork = (await _accountCleanupPendingStore.LoadAsync(
				cancellationToken)).ToList();
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException)
		{
			BlockAllAccountCleanups();
			ClearAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingInvalidDataFailureMessage);
			return false;
		}
		catch
		{
			BlockAllAccountCleanups();
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return false;
		}

		ClearAccountSettingsHealthWarning(
			AccountCleanupPendingInvalidDataFailureMessage);
		BlockAccountCleanups(pendingWork.Select(work =>
			(work.AccountId, work.Provider)));
		ClearBlockAllAccountCleanups();

		if (includeUnjournaledBlockedAccounts)
		{
			HashSet<(Guid AccountId, ProviderKind Provider)> persistedKeys =
				pendingWork
					.Select(work => (work.AccountId, work.Provider))
					.ToHashSet();
			(Guid AccountId, ProviderKind Provider)[] unjournaledKeys =
				GetBlockedAccountCleanupKeys()
					.Where(accountKey => !persistedKeys.Contains(accountKey))
					.ToArray();

			if (unjournaledKeys.Length > 0)
			{
				try
				{
					await _accountCleanupPendingStore.UpsertAsync(
						unjournaledKeys,
						cancellationToken);
					pendingWork.AddRange(unjournaledKeys.Select(accountKey =>
						new AccountCleanupPendingWork(
							accountKey.AccountId,
							accountKey.Provider,
							CachePending: true,
							RuntimePending: true)));
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
					AppendAccountSettingsHealthWarning(
						AccountCleanupPendingFailureMessage);
					return false;
				}
			}
		}

		foreach (AccountCleanupPendingWork work in pendingWork)
		{
			bool isProtectedPendingGrokProfile =
				work.RuntimePending &&
				(work.Provider == ProviderKind.Grok) &&
				_grokConnectionPendingCleanupProtectedAccountIds.Contains(
					work.AccountId) &&
				_accountProfiles.Any(profile =>
					(profile.Id == work.AccountId) &&
					(profile.Provider == ProviderKind.Grok));
			if (deferDestructiveGrokCleanup &&
				work.RuntimePending &&
				(work.Provider == ProviderKind.Grok) &&
				!IsRetainedConnectedGrokProfile(work.AccountId, work.Provider))
			{
				if (_accountProfiles.Any(profile =>
						(profile.Id == work.AccountId) &&
						(profile.Provider == ProviderKind.Grok)))
				{
					_grokConnectionPendingCleanupProtectedAccountIds.Add(
						work.AccountId);
				}

				continue;
			}
			if (isProtectedPendingGrokProfile)
			{
				continue;
			}

			bool wasRefreshQuiesced = !requireRefreshQuiescence ||
				await QuiesceRemovedAccountRefreshSafelyAsync(work.AccountId);
			await AttemptAccountCleanupAsync(
				work.AccountId,
				work.Provider,
				wasRefreshQuiesced,
				work.CachePending,
				work.RuntimePending,
				cancellationToken,
				work.AllowRetainedPrivateStateDeletion);
		}

		if (!HasBlockedAccountCleanups())
		{
			ClearAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}

		return !HasBlockedAccountCleanups();
	}

	private async Task<(bool CacheCompleted, bool RuntimeCompleted)>
		AttemptAccountCleanupAsync(
			Guid accountId,
			ProviderKind provider,
			bool wasRefreshQuiesced,
			bool cachePending,
			bool runtimePending,
			CancellationToken cancellationToken,
			bool allowRetainedPrivateStateDeletion = false)
	{
		RetainedPrivateAccountCleanupDisposition cleanupDisposition = runtimePending
			? await GetRetainedPrivateAccountCleanupDispositionAsync(
				accountId,
				provider,
				cancellationToken,
				allowRetainedPrivateStateDeletion)
			: RetainedPrivateAccountCleanupDisposition.DestructiveCleanupAllowed;
		if (cleanupDisposition ==
			RetainedPrivateAccountCleanupDisposition.CompleteWithoutDestruction)
		{
			return await CompleteRetainedPrivateAccountCleanupIntentAsync(
				accountId,
				provider,
				cachePending,
				runtimePending,
				cancellationToken);
		}
		if (cleanupDisposition ==
			RetainedPrivateAccountCleanupDisposition.RetryWithoutDestruction)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return (!cachePending, !runtimePending);
		}

		bool cacheCompleted = !cachePending;
		bool runtimeCompleted = !runtimePending;
		if (!wasRefreshQuiesced)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return (cacheCompleted, runtimeCompleted);
		}

		if (cachePending && await DeleteCachedSnapshotSafelyAsync(
				accountId,
				provider,
				cancellationToken,
				retryTransientFailures: true))
		{
			cacheCompleted = await MarkAccountCleanupComponentCompletedSafelyAsync(
				accountId,
				provider,
				isCacheComponent: true,
				cancellationToken);
		}

		if (runtimePending && await PurgeAccountRuntimeStateSafelyAsync(
				accountId,
				provider,
				cancellationToken))
		{
			runtimeCompleted = await MarkAccountCleanupComponentCompletedSafelyAsync(
				accountId,
				provider,
				isCacheComponent: false,
				cancellationToken);
		}
		if (runtimeCompleted)
		{
			RevokeRetainedPrivateStateDeletionAuthorization(accountId, provider);
		}

		if (cacheCompleted && runtimeCompleted)
		{
			UnblockAccountCleanup(accountId, provider);
			if (!HasBlockedAccountCleanups())
			{
				ClearAccountSettingsHealthWarning(
					AccountCleanupPendingFailureMessage);
			}
		}
		else
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}

		return (cacheCompleted, runtimeCompleted);
	}

	private async Task<(bool CacheCompleted, bool RuntimeCompleted)>
		CompleteRetainedPrivateAccountCleanupIntentAsync(
			Guid accountId,
			ProviderKind provider,
			bool cachePending,
			bool runtimePending,
			CancellationToken cancellationToken)
	{
		bool cacheCompleted = !cachePending ||
			await MarkAccountCleanupComponentCompletedSafelyAsync(
				accountId,
				provider,
				isCacheComponent: true,
				cancellationToken);
		bool runtimeCompleted = !runtimePending ||
			await MarkAccountCleanupComponentCompletedSafelyAsync(
				accountId,
				provider,
				isCacheComponent: false,
				cancellationToken);

		if (cacheCompleted && runtimeCompleted)
		{
			UnblockAccountCleanup(accountId, provider);
			if (!HasBlockedAccountCleanups())
			{
				ClearAccountSettingsHealthWarning(
					AccountCleanupPendingFailureMessage);
			}
		}
		else
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}

		return (cacheCompleted, runtimeCompleted);
	}

	private bool IsRetainedConnectedGrokProfile(
		Guid accountId,
		ProviderKind provider)
	{
		return (provider == ProviderKind.Grok) &&
			_accountProfiles.Any(profile =>
				(profile.Id == accountId) &&
				(profile.Provider == provider) &&
				(profile.ProviderAccountIdentity is not null));
	}

	private async Task<RetainedPrivateAccountCleanupDisposition>
		GetRetainedPrivateAccountCleanupDispositionAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken,
		bool allowRetainedPrivateStateDeletion)
	{
		if (!HasDestructivePrivateAccountState(provider))
		{
			return RetainedPrivateAccountCleanupDisposition.DestructiveCleanupAllowed;
		}

		AccountProfile? retainedProfile = _accountProfiles.FirstOrDefault(
			profile =>
				(profile.Id == accountId) &&
				(profile.Provider == provider));
		AccountUsageViewModel? retainedAccount = Accounts.FirstOrDefault(
			account =>
				(account.Id == accountId) &&
				(account.Provider == provider));
		if (retainedAccount?.IsProviderAccountChangeInProgress == true)
		{
			return RetainedPrivateAccountCleanupDisposition.RetryWithoutDestruction;
		}
		if ((provider == ProviderKind.Copilot) && (retainedProfile is not null))
		{
			if (allowRetainedPrivateStateDeletion ||
				IsRetainedPrivateStateDeletionAuthorized(accountId, provider))
			{
				return RetainedPrivateAccountCleanupDisposition
					.DestructiveCleanupAllowed;
			}

			return RetainedPrivateAccountCleanupDisposition.CompleteWithoutDestruction;
		}
		if (retainedProfile?.ProviderAccountIdentity is not null)
		{
			return RetainedPrivateAccountCleanupDisposition.CompleteWithoutDestruction;
		}

		if (_grokConnectionPendingStore is null)
		{
			return retainedProfile is null
				? RetainedPrivateAccountCleanupDisposition.DestructiveCleanupAllowed
				: RetainedPrivateAccountCleanupDisposition.RetryWithoutDestruction;
		}

		IReadOnlyList<GrokConnectionPendingWork> pendingConnections;
		try
		{
			pendingConnections = await _grokConnectionPendingStore.LoadAsync(
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return retainedProfile is null
				? RetainedPrivateAccountCleanupDisposition.DestructiveCleanupAllowed
				: RetainedPrivateAccountCleanupDisposition.RetryWithoutDestruction;
		}

		return (retainedProfile is not null) &&
			pendingConnections.Any(work => work.AccountId == accountId)
			? RetainedPrivateAccountCleanupDisposition.CompleteWithoutDestruction
			: RetainedPrivateAccountCleanupDisposition.DestructiveCleanupAllowed;
	}

	private static bool HasDestructivePrivateAccountState(ProviderKind provider)
	{
		return (provider == ProviderKind.Copilot) ||
			(provider == ProviderKind.Grok);
	}

	private bool IsRetainedPrivateStateDeletionAuthorized(
		Guid accountId,
		ProviderKind provider)
	{
		lock (_cleanupStateSync)
		{
			return _retainedPrivateStateDeletionAuthorizedAccountKeys.Contains(
				(accountId, provider));
		}
	}

	private void RevokeRetainedPrivateStateDeletionAuthorization(
		Guid accountId,
		ProviderKind provider)
	{
		lock (_cleanupStateSync)
		{
			_retainedPrivateStateDeletionAuthorizedAccountKeys.Remove(
				(accountId, provider));
		}
	}

	private async Task<bool> MarkAccountCleanupComponentCompletedSafelyAsync(
		Guid accountId,
		ProviderKind provider,
		bool isCacheComponent,
		CancellationToken cancellationToken)
	{
		if (_accountCleanupPendingStore is null)
		{
			return true;
		}

		try
		{
			if (isCacheComponent)
			{
				await _accountCleanupPendingStore.MarkCacheCompletedAsync(
					accountId,
					provider,
					cancellationToken);
			}
			else
			{
				await _accountCleanupPendingStore.MarkRuntimeCompletedAsync(
					accountId,
					provider,
					cancellationToken);
			}

			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
			return false;
		}
	}

	private async Task<bool> DeleteCachedSnapshotSafelyAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken,
		bool retryTransientFailures = false)
	{
		if (_usageSnapshotStore is null)
		{
			return true;
		}

		int maximumAttempts = retryTransientFailures
			? MaximumUsageCacheDeleteAttempts
			: 1;
		for (int attempt = 1; attempt <= maximumAttempts; attempt++)
		{
			try
			{
				await _usageSnapshotStore.DeleteAsync(
					accountId,
					provider,
					cancellationToken);
				return true;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (
				(attempt < maximumAttempts) &&
				(exception is IOException or UnauthorizedAccessException))
			{
				// File locks and antivirus scans are commonly transient. Back off before
				// retrying so one temporary cache failure cannot abort account setup.
				await Task.Delay(
					UsageCacheDeleteRetryDelays[attempt - 1],
					cancellationToken);
			}
			catch
			{
				// Cache cleanup remains best effort after the bounded retry budget.
				return false;
			}
		}

		return false;
	}

	private async Task DeleteDefiniteScopeMismatchCacheAsync(
		AccountUsageViewModel account)
	{
		if (account.CurrentSnapshot?.SubscriptionVerificationState !=
			SubscriptionVerificationState.DefiniteScopeMismatch)
		{
			return;
		}

		AccountCleanupPendingWork? cacheCleanupIntent =
			await TryPersistProviderAccountCacheCleanupIntentAsync(
				account.Id,
				account.Provider,
				CancellationToken.None);
		bool wasCacheCleanupIntentPersisted = cacheCleanupIntent is not null;
		bool wasCachedSnapshotDeleted = await DeleteCachedSnapshotSafelyAsync(
			account.Id,
			account.Provider,
			CancellationToken.None,
			retryTransientFailures: true);
		bool wasCacheCleanupIntentCompleted = !wasCacheCleanupIntentPersisted;
		if (!wasCacheCleanupIntentPersisted && !wasCachedSnapshotDeleted)
		{
			await PersistDefiniteScopeMismatchFailClosedProfileAsync(account);
		}

		if (wasCacheCleanupIntentPersisted && wasCachedSnapshotDeleted)
		{
			bool wasCacheComponentCompleted =
				await MarkAccountCleanupComponentCompletedSafelyAsync(
					account.Id,
					account.Provider,
					isCacheComponent: true,
					CancellationToken.None);
			wasCacheCleanupIntentCompleted = wasCacheComponentCompleted &&
				(cacheCleanupIntent!.RuntimePending == false);
			if (wasCacheCleanupIntentCompleted)
			{
				UnblockAccountCleanup(account.Id, account.Provider);
				if (!HasBlockedAccountCleanups())
				{
					ClearAccountSettingsHealthWarning(
						AccountCleanupPendingFailureMessage);
				}
			}
		}

		if (wasCachedSnapshotDeleted)
		{
			ResolveProviderAccountCacheCleanupFailure(account.Id);
		}
		else
		{
			RecordProviderAccountCacheCleanupFailure(account.Id);
		}

		if (!wasCacheCleanupIntentCompleted)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}
	}

	private async Task PersistDefiniteScopeMismatchFailClosedProfileAsync(
		AccountUsageViewModel account)
	{
		await _accountMutationGate.WaitAsync(CancellationToken.None);

		try
		{
			int profileIndex = _accountProfiles.FindIndex(profile =>
				(profile.Id == account.Id) &&
				(profile.Provider == account.Provider));
			if ((profileIndex < 0) ||
				!Accounts.Contains(account) ||
				!account.IsEnabled ||
				(account.CurrentSnapshot?.SubscriptionVerificationState !=
					SubscriptionVerificationState.DefiniteScopeMismatch))
			{
				return;
			}

			AccountProfile currentProfile = _accountProfiles[profileIndex];
			List<AccountProfile> candidateProfiles = _accountProfiles.ToList();
			candidateProfiles[profileIndex] = currentProfile with
			{
				IsEnabled = false,
				ProviderAccountIdentity = null
			};
			string? committedWarning = null;
			bool wasFailClosedProfilePersisted = false;

			try
			{
				await SaveAccountProfilesWithTransientRetryAsync(
					candidateProfiles,
					CancellationToken.None);
				wasFailClosedProfilePersisted = true;
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				committedWarning = exception.Message;
				wasFailClosedProfilePersisted = true;
			}
			catch
			{
				// 持久化 fail-closed 失敗時，本次 process 仍維持停用。
			}

			if (!wasFailClosedProfilePersisted)
			{
				await TryPersistPrivateBindingRestartQuarantineAsync(
					currentProfile);
			}

			ApplyProfiles(candidateProfiles);
			if (committedWarning is not null)
			{
				AccountSettingsHealthMessage = committedWarning;
			}
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	private async Task TryPersistPrivateBindingRestartQuarantineAsync(
		AccountProfile profile)
	{
		try
		{
			switch (profile.Provider)
			{
				case ProviderKind.Claude when _claudeAccountBindingStore is not null:
				{
					using IDisposable lease =
						await _claudeBindingCommitGate!.EnterAsync(CancellationToken.None);
					await _claudeAccountBindingStore.DeleteAsync(
						profile.Id,
						CancellationToken.None);
					ClaudeAccountBinding? deletedBinding =
						await _claudeAccountBindingStore.LoadAsync(
							profile.Id,
						CancellationToken.None);
					if (deletedBinding is not null)
					{
						throw new IOException(
							"Claude restart fail-closed binding 清理未通過 read-back 驗證。");
					}

					break;
				}

				case ProviderKind.Codex when _codexWorkspaceBindingStore is not null:
				{
					using IDisposable lease =
						await _codexBindingCommitGate!.EnterAsync(CancellationToken.None);
					if (CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
							profile.ProviderAccountIdentity,
							out _))
					{
						await _codexWorkspaceBindingStore.DeleteAsync(
							profile.Id,
							CancellationToken.None);
						CodexWorkspaceBinding? deletedBinding =
							await _codexWorkspaceBindingStore.LoadAsync(
								profile.Id,
								CancellationToken.None);
						if (deletedBinding is not null)
						{
							throw new IOException(
								"Codex restart fail-closed binding 清理未通過 read-back 驗證。");
						}
					}
					else
					{
						CodexWorkspaceBinding quarantine =
							CodexWorkspaceBinding.CreateRestartQuarantine(profile.Id);
						await _codexWorkspaceBindingStore.SaveAsync(
							quarantine,
							CancellationToken.None);
						CodexWorkspaceBinding? persistedQuarantine =
							await _codexWorkspaceBindingStore.LoadAsync(
								profile.Id,
								CancellationToken.None);
						if (!Equals(quarantine, persistedQuarantine))
						{
							throw new IOException(
								"Codex restart fail-closed quarantine 未通過 read-back 驗證。");
						}
					}

					break;
				}

				case ProviderKind.Grok when _grokAccountBindingStore is not null:
				{
					using IDisposable lease =
						await _grokBindingCommitGate!.EnterAsync(CancellationToken.None);
					await _grokAccountBindingStore.DeleteAsync(
						profile.Id,
						CancellationToken.None);
					GrokAccountBinding? deletedBinding =
						await _grokAccountBindingStore.LoadAsync(
							profile.Id,
							CancellationToken.None);
					if (deletedBinding is not null)
					{
						throw new IOException(
							"Grok restart fail-closed binding 清理未通過 read-back 驗證。");
					}

					break;
				}
			}
		}
		catch
		{
			// 無法留下任何 durable 保護時，本次 process 仍維持停用。
		}
	}

	private async Task SaveAccountProfilesWithTransientRetryAsync(
		IReadOnlyList<AccountProfile> profiles,
		CancellationToken cancellationToken)
	{
		for (int attempt = 1; attempt <= MaximumAccountProfileSaveAttempts; attempt++)
		{
			try
			{
				await _accountProfileStore.SaveAsync(profiles, cancellationToken);
				return;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (
				(attempt < MaximumAccountProfileSaveAttempts) &&
				IsTransientAccountProfileSaveFailure(exception))
			{
				await Task.Delay(
					AccountProfileSaveRetryDelays[attempt - 1],
					cancellationToken);
			}
		}
	}

	private static bool IsTransientAccountProfileSaveFailure(Exception exception)
	{
		if (exception is AccountProfileStoreException storeException)
		{
			return !storeException.HasCommittedChanges &&
				(storeException.InnerException is IOException or
					UnauthorizedAccessException);
		}

		return exception is IOException or UnauthorizedAccessException;
	}

	private void AppendAccountSettingsHealthWarning(string warning)
	{
		if (AccountSettingsHealthMessage.Contains(warning, StringComparison.Ordinal))
		{
			return;
		}

		AccountSettingsHealthMessage = string.IsNullOrWhiteSpace(
			AccountSettingsHealthMessage)
			? warning
			: $"{AccountSettingsHealthMessage} {warning}";
	}

	private void ClearAccountSettingsHealthWarning(string warning)
	{
		if (!AccountSettingsHealthMessage.Contains(warning, StringComparison.Ordinal))
		{
			return;
		}

		AccountSettingsHealthMessage = AccountSettingsHealthMessage
			.Replace(warning, string.Empty, StringComparison.Ordinal)
			.Trim();
	}

	private void RecordProviderAccountCacheCleanupFailure(Guid accountId)
	{
		lock (_providerAccountCacheCleanupWarningSync)
		{
			_providerAccountCacheCleanupFailureAccountIds.Add(accountId);
			AppendAccountSettingsHealthWarning(
				ProviderAccountCacheCleanupFailureMessage);
		}
	}

	private void ResolveProviderAccountCacheCleanupFailure(Guid accountId)
	{
		lock (_providerAccountCacheCleanupWarningSync)
		{
			_providerAccountCacheCleanupFailureAccountIds.Remove(accountId);
			if (_providerAccountCacheCleanupFailureAccountIds.Count == 0)
			{
				ClearAccountSettingsHealthWarning(
					ProviderAccountCacheCleanupFailureMessage);
			}
		}
	}

	private void ReconcileProviderAccountCacheCleanupFailures(
		IReadOnlySet<Guid> currentAccountIds)
	{
		lock (_providerAccountCacheCleanupWarningSync)
		{
			_providerAccountCacheCleanupFailureAccountIds.RemoveWhere(
				accountId => !currentAccountIds.Contains(accountId));
			if (_providerAccountCacheCleanupFailureAccountIds.Count == 0)
			{
				ClearAccountSettingsHealthWarning(
					ProviderAccountCacheCleanupFailureMessage);
			}
		}
	}

	private async Task<bool> QuiesceRemovedAccountRefreshSafelyAsync(
		Guid accountId)
	{
		try
		{
			_usageRefreshCoordinator.Invalidate(accountId);
		}
		catch
		{
			// Continue waiting for any provider call already in progress.
		}

		Task accountRefreshIdleTask;

		lock (_refreshSync)
		{
			accountRefreshIdleTask = _accountRefreshActivities.TryGetValue(
				accountId,
				out AccountRefreshActivity? activity)
				? activity.IdleCompletion.Task
				: Task.CompletedTask;
		}

		try
		{
			await accountRefreshIdleTask.WaitAsync(
				_accountRefreshQuiesceTimeout);
			return true;
		}
		catch (TimeoutException)
		{
			// The settings change is already committed. Do not let a provider
			// that ignores cancellation keep account management locked forever.
			return false;
		}
		catch
		{
			// Provider failures are already converted into error snapshots.
			return true;
		}
	}

	private async Task<IReadOnlyDictionary<Guid, bool>>
		QuiesceAccountRefreshesSafelyAsync(
			IReadOnlyCollection<AccountProfile> profiles)
	{
		Task<(Guid AccountId, bool WasQuiesced)>[] quiesceTasks = profiles
			.Select(profile => profile.Id)
			.Distinct()
			.Select(async accountId => (
				accountId,
				await QuiesceRemovedAccountRefreshSafelyAsync(accountId)))
			.ToArray();
		(Guid AccountId, bool WasQuiesced)[] results =
			await Task.WhenAll(quiesceTasks);
		return results.ToDictionary(
			result => result.AccountId,
			result => result.WasQuiesced);
	}

	private async Task<bool> PurgeAccountRuntimeStateSafelyAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken)
	{
		if (_accountRuntimeStatePurger is null)
		{
			return true;
		}

		try
		{
			await _accountRuntimeStatePurger.PurgeAsync(
				accountId,
				provider,
				cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// This purge can include durable Claude safety state. The provider already
			// exhausted its bounded transient retries, so callers must surface the error.
			return false;
		}
	}

	private async Task PersistUsableSnapshotsAsync(
		IReadOnlyCollection<UsageSnapshot> snapshots)
	{
		if (_usageSnapshotStore is null)
		{
			return;
		}

		foreach (UsageSnapshot snapshot in snapshots)
		{
			if (((snapshot.Status != SnapshotStatus.Ready) &&
					(snapshot.Status != SnapshotStatus.Stale)) ||
				(snapshot.SourceTrust == SourceTrust.Unavailable) ||
				(snapshot.Metrics.Count == 0) ||
				((snapshot.Account.Provider == ProviderKind.Claude) &&
					(snapshot.SubscriptionVerificationState ==
						SubscriptionVerificationState.UsageUnavailable)) ||
				!_accountProfiles.Any(profile =>
					(profile.Id == snapshot.Account.Id) &&
					(profile.Provider == snapshot.Account.Provider) &&
					profile.IsEnabled))
			{
				continue;
			}

			try
			{
				await _usageSnapshotStore.SaveAsync(snapshot);

				if (!_accountProfiles.Any(profile =>
					(profile.Id == snapshot.Account.Id) &&
					(profile.Provider == snapshot.Account.Provider)))
				{
					await DeleteCachedSnapshotSafelyAsync(
						snapshot.Account.Id,
						snapshot.Account.Provider,
						CancellationToken.None);
				}
			}
			catch
			{
				// A cache write failure must not turn a successful refresh into an error.
			}
		}
	}

	private async Task<bool> CleanupUnpairedClaudeBindingsAsync(
		CancellationToken cancellationToken)
	{
		if (_claudeAccountBindingStore is null)
		{
			return true;
		}

		try
		{
			using IDisposable lease =
				await _claudeBindingCommitGate!.EnterAsync(cancellationToken);
			IReadOnlyList<ClaudeAccountBinding> bindings =
				await _claudeAccountBindingStore.LoadAllAsync(cancellationToken);

			foreach (ClaudeAccountBinding binding in bindings)
			{
				if (Accounts.Any(account =>
						binding.MatchesPublicProfile(account.Profile)))
				{
					continue;
				}

				await _claudeAccountBindingStore.DeleteAsync(
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

			ClearAccountSettingsHealthWarning(
				ClaudeBindingInventoryCleanupFailureMessage);
			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_reportDiagnostic(
				"claude-binding-inventory-cleanup",
				"stage=startup;result=failed",
				exception);
			AppendAccountSettingsHealthWarning(
				ClaudeBindingInventoryCleanupFailureMessage);
			return false;
		}
	}

	private async Task<bool> CleanupUnpairedCodexBindingsAsync(
		CancellationToken cancellationToken)
	{
		if (_codexWorkspaceBindingStore is null)
		{
			return true;
		}

		try
		{
			using IDisposable lease =
				await _codexBindingCommitGate!.EnterAsync(cancellationToken);
			IReadOnlyList<CodexWorkspaceBinding> bindings =
				await _codexWorkspaceBindingStore.LoadAllAsync(cancellationToken);

			foreach (CodexWorkspaceBinding binding in bindings)
			{
				if (Accounts.Any(account =>
						binding.MatchesPublicProfile(account.Profile) ||
						(binding.IsRestartQuarantine &&
							account.IsCodex &&
							(account.Id == binding.AccountId))))
				{
					continue;
				}

				await _codexWorkspaceBindingStore.DeleteAsync(
					binding.AccountId,
					cancellationToken);
				CodexWorkspaceBinding? deletedBinding =
					await _codexWorkspaceBindingStore.LoadAsync(
						binding.AccountId,
						cancellationToken);
				if (deletedBinding is not null)
				{
					throw new IOException(
						"未配對的 Codex private binding 清理未通過 read-back 驗證。");
				}
			}

			ClearAccountSettingsHealthWarning(
				CodexBindingInventoryCleanupFailureMessage);
			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_reportDiagnostic(
				"codex-binding-inventory-cleanup",
				"stage=startup;result=failed",
				exception);
			AppendAccountSettingsHealthWarning(
				CodexBindingInventoryCleanupFailureMessage);
			return false;
		}
	}

	private async Task RestoreCachedSnapshotsAsync(
		IReadOnlyCollection<AccountUsageViewModel> candidateAccounts,
		CancellationToken cancellationToken)
	{
		if (_usageSnapshotStore is null)
		{
			return;
		}

		Dictionary<ProviderKind, Dictionary<string, Guid>> identityOwners =
			CreateProviderAccountIdentityOwners();
		List<(
			AccountUsageViewModel Account,
			long LifecycleRevision,
			UsageSnapshot Snapshot,
			string ProviderAccountIdentity,
			bool IsProfileIdentityUnbound)> cachedCandidates = new();
		Dictionary<ProviderKind, Dictionary<string, int>> candidateIdentityCounts =
			new();

		foreach (AccountUsageViewModel account in candidateAccounts)
		{
			if (!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				!Accounts.Contains(account) ||
				(account.CurrentSnapshot is not null))
			{
				continue;
			}

			long lifecycleRevision = account.LifecycleRevision;

			UsageSnapshot? snapshot;

			try
			{
				snapshot = await _usageSnapshotStore.LoadAsync(
					account.Profile,
					cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				continue;
			}

			if ((snapshot is null) ||
				(snapshot.Account.Id != account.Id) ||
				(snapshot.Account.Provider != account.Provider) ||
				(snapshot.Metrics.Count == 0) ||
				!Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) ||
				(snapshot.SourceTrust == SourceTrust.Unavailable) ||
				((snapshot.Status != SnapshotStatus.Ready) &&
					(snapshot.Status != SnapshotStatus.Stale)))
			{
				continue;
			}

			if (!await CanRestoreClaudeCachedSnapshotAsync(
					account,
					snapshot,
					cancellationToken))
			{
				continue;
			}

			if (!await CanRestoreCodexCachedSnapshotAsync(
					account,
					snapshot,
					cancellationToken))
			{
				continue;
			}

			if (!await CanRestoreGrokCachedSnapshotAsync(
					account,
					snapshot,
					cancellationToken))
			{
				continue;
			}

			if (account.IsAntigravity &&
				string.IsNullOrWhiteSpace(
					account.Profile.ProviderAccountIdentity) &&
				string.Equals(
					snapshot.ProviderAccountIdentity,
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
					StringComparison.OrdinalIgnoreCase))
			{
				// Official AGY usage is bound to this machine's active login. An
				// unbound card (including a portable import) must reconnect instead
				// of inheriting an old machine-local cache.
				continue;
			}

			UsageSnapshot staleSnapshot = snapshot with
			{
				Account = account.Profile,
				Status = SnapshotStatus.Stale,
				Error = snapshot.Error
			};

			if (!TryGetUsableProviderAccountIdentity(
					staleSnapshot,
					out string? providerAccountIdentity) ||
				(providerAccountIdentity is null) ||
				!account.CanAcceptProviderAccountSnapshot(staleSnapshot))
			{
				continue;
			}

			cachedCandidates.Add((
				account,
				lifecycleRevision,
				staleSnapshot,
				providerAccountIdentity,
				string.IsNullOrWhiteSpace(
					account.Profile.ProviderAccountIdentity)));

			if (!candidateIdentityCounts.TryGetValue(
					account.Provider,
					out Dictionary<string, int>? providerIdentityCounts))
			{
				providerIdentityCounts = new Dictionary<string, int>(
					ProviderAccountIdentityRules.Comparer);
				candidateIdentityCounts.Add(
					account.Provider,
					providerIdentityCounts);
			}

			providerIdentityCounts[providerAccountIdentity] =
				providerIdentityCounts.GetValueOrDefault(providerAccountIdentity) + 1;
		}

		foreach ((
			AccountUsageViewModel account,
			long lifecycleRevision,
			UsageSnapshot staleSnapshot,
			string providerAccountIdentity,
			bool isProfileIdentityUnbound) in cachedCandidates)
		{
			if (isProfileIdentityUnbound &&
				(!candidateIdentityCounts.TryGetValue(
					account.Provider,
					out Dictionary<string, int>? providerIdentityCounts) ||
				!providerIdentityCounts.TryGetValue(
					providerAccountIdentity,
					out int identityCandidateCount) ||
				(identityCandidateCount != 1)))
			{
				continue;
			}

			if (!TryClaimProviderAccountIdentity(
					account,
					staleSnapshot,
					identityOwners))
			{
				account.RejectProviderAccountSnapshot(
					staleSnapshot,
					ProviderAccountIdentityConflictMessage,
					UsageRecoveryAction.SwitchAccount);
				continue;
			}

			lock (_refreshSync)
			{
				if (_isRefreshStopped ||
					_isUpdateShutdownReserved ||
					!account.IsEnabled ||
					!Accounts.Contains(account) ||
					(account.LifecycleRevision != lifecycleRevision) ||
					(account.CurrentSnapshot is not null))
				{
					continue;
				}

				if (_usageRefreshCoordinator.SeedLastKnownGood(staleSnapshot))
				{
					if (isProfileIdentityUnbound)
					{
						account.SeedProviderAccountIdentity(
							providerAccountIdentity);
					}

					account.ApplySnapshot(staleSnapshot);

					if (isProfileIdentityUnbound)
					{
						_runtimeOnlyCachedProviderAccountIdentityAccountIds.Add(
							account.Id);
					}
				}
			}
		}

		ApplyAccountDisplayOrder();
	}

	private async Task<bool> CanRestoreClaudeCachedSnapshotAsync(
		AccountUsageViewModel account,
		UsageSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		if (!account.IsClaude)
		{
			return true;
		}

		if ((_claudeAccountBindingStore is null) ||
			((snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Verified) &&
				(snapshot.SubscriptionVerificationState !=
					SubscriptionVerificationState.TransientProbeError)) ||
			!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				account.Profile.ProviderAccountIdentity,
				out string? profileIdentity) ||
			(profileIdentity is null) ||
			!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				snapshotIdentity))
		{
			return false;
		}

		ClaudeAccountBinding? binding;

		try
		{
			binding = await _claudeAccountBindingStore.LoadAsync(
				account.Id,
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

		return (binding is not null) &&
			(binding.AccountId == account.Id) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				ClaudeAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId));
	}

	private async Task<bool> CanRestoreCodexCachedSnapshotAsync(
		AccountUsageViewModel account,
		UsageSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		if (!account.IsCodex)
		{
			return true;
		}

		CodexWorkspaceBinding? binding = null;
		if (_codexWorkspaceBindingStore is not null)
		{
			try
			{
				using IDisposable lease =
					await _codexBindingCommitGate!.EnterAsync(cancellationToken);
				binding = await _codexWorkspaceBindingStore.LoadAsync(
					account.Id,
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

		bool isWorkspaceBound =
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				account.Profile.ProviderAccountIdentity,
				out string? profileIdentity) &&
			(profileIdentity is not null);
		if (!isWorkspaceBound)
		{
			if (binding is not null)
			{
				return false;
			}

			return IsUsableCodexAccountLevelSnapshot(account, snapshot);
		}

		if ((_codexWorkspaceBindingStore is null) ||
			((snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Verified) &&
				(snapshot.SubscriptionVerificationState !=
					SubscriptionVerificationState.TransientProbeError)) ||
			!CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				snapshotIdentity))
		{
			return false;
		}

		return (binding is not null) &&
			!binding.IsRestartQuarantine &&
			(binding.AccountId == account.Id) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId));
	}

	private static bool IsUsableCodexAccountLevelSnapshot(
		AccountUsageViewModel account,
		UsageSnapshot snapshot)
	{
		if ((snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Unverified) ||
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				snapshot.ProviderAccountIdentity,
				out _) ||
			!ProviderAccountIdentityRules.TryNormalize(
				account.Profile.ProviderAccountIdentity,
				out string? profileIdentity) ||
			!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			Guid.TryParse(snapshotIdentity, out _) ||
			((profileIdentity is not null) && Guid.TryParse(profileIdentity, out _)))
		{
			return false;
		}

		return (profileIdentity is null) ||
			ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				snapshotIdentity);
	}

	private async Task<bool> CanRestoreGrokCachedSnapshotAsync(
		AccountUsageViewModel account,
		UsageSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		if (!account.IsGrok)
		{
			return true;
		}

		if ((_grokAccountBindingStore is null) ||
			((snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Verified) &&
				(snapshot.SubscriptionVerificationState !=
					SubscriptionVerificationState.TransientProbeError)) ||
			!GrokAccountBinding.TryNormalizePublicBindingIdentity(
				account.Profile.ProviderAccountIdentity,
				out string? profileIdentity) ||
			(profileIdentity is null) ||
			!GrokAccountBinding.TryNormalizePublicBindingIdentity(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				snapshotIdentity))
		{
			return false;
		}

		GrokAccountBinding? binding;

		try
		{
			using IDisposable lease =
				await _grokBindingCommitGate!.EnterAsync(cancellationToken);
			binding = await _grokAccountBindingStore.LoadAsync(
				account.Id,
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

		return (binding is not null) &&
			(binding.AccountId == account.Id) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				GrokAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId));
	}

	internal async Task<UsageSnapshot?> LoadClaudeLegacyCachedSnapshotAsync(
		AccountUsageViewModel account,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (!account.IsClaude)
		{
			throw new ArgumentException(
				"只有 Claude 帳號可讀取舊版 subscription cache。",
				nameof(account));
		}

		if (ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				account.Profile.ProviderAccountIdentity,
				out _))
		{
			return null;
		}

		if (IsUsableClaudeLegacySnapshot(account, account.CurrentSnapshot))
		{
			return account.CurrentSnapshot;
		}

		if (_usageSnapshotStore is null)
		{
			return null;
		}

		UsageSnapshot? snapshot;

		try
		{
			snapshot = await _usageSnapshotStore.LoadAsync(
				account.Profile,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new InvalidOperationException(
				"無法安全確認 Claude 舊版用量資料。",
				exception);
		}

		return IsUsableClaudeLegacySnapshot(account, snapshot)
			? snapshot
			: null;
	}

	private static bool IsUsableClaudeLegacySnapshot(
		AccountUsageViewModel account,
		UsageSnapshot? snapshot)
	{
		if ((snapshot is null) ||
			(snapshot.Account.Id != account.Id) ||
			(snapshot.Account.Provider != ProviderKind.Claude) ||
			(snapshot.Metrics.Count == 0) ||
			!Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) ||
			(snapshot.SourceTrust == SourceTrust.Unavailable) ||
			((snapshot.Status != SnapshotStatus.Ready) &&
				(snapshot.Status != SnapshotStatus.Stale)) ||
			(snapshot.SubscriptionVerificationState !=
				SubscriptionVerificationState.Unverified) ||
			ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				snapshot.ProviderAccountIdentity,
				out _))
		{
			return false;
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				account.Profile.ProviderAccountIdentity,
				out string? profileIdentity) ||
			!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? snapshotIdentity))
		{
			return false;
		}

		return (profileIdentity is null) ||
			(snapshotIdentity is null) ||
			ProviderAccountIdentityRules.Comparer.Equals(
				profileIdentity,
				snapshotIdentity);
	}

	private static string CreateAccountSettingsHealthMessage(
		AccountProfileLoadResult loadResult)
	{
		if (string.IsNullOrWhiteSpace(loadResult.Message))
		{
			return string.Empty;
		}

		string message = loadResult.Message.Trim();
		return string.IsNullOrWhiteSpace(loadResult.RecoveryPath)
			? message
			: $"{message}\n已保留原設定檔：{loadResult.RecoveryPath}";
	}

	private Dictionary<ProviderKind, Dictionary<string, Guid>>
		CreateProviderAccountIdentityOwners()
	{
		Dictionary<ProviderKind, Dictionary<string, Guid>> owners = new();

		foreach (AccountProfile profile in _accountProfiles)
		{
			if (IsInactiveAntigravityLocalSessionOwner(
					profile.Id,
					profile.Provider,
					profile.ProviderAccountIdentity))
			{
				continue;
			}

			TryAddProviderAccountIdentityOwner(
				owners,
				profile.Provider,
				profile.Id,
				profile.ProviderAccountIdentity);
		}

		foreach (AccountUsageViewModel account in Accounts)
		{
			if (IsInactiveAntigravityLocalSessionOwner(
					account.Id,
					account.Provider,
					account.ProviderAccountIdentity))
			{
				continue;
			}

			TryAddProviderAccountIdentityOwner(
				owners,
				account.Provider,
				account.Id,
				account.IsCodex
					? account.ProviderAccountOwnershipIdentity
					: account.ProviderAccountIdentity);
		}

		return owners;
	}

	private async Task<bool> DeleteAntigravityVerifiedAccountBindingSafelyAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken)
	{
		if ((provider != ProviderKind.Antigravity) ||
			(_antigravityVerifiedAccountBindingStore is null))
		{
			return true;
		}

		try
		{
			await _antigravityVerifiedAccountBindingStore.DeleteAsync(
				accountId,
				cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Optional display metadata must never block committed account cleanup.
			return false;
		}
	}

	private bool IsInactiveAntigravityLocalSessionOwner(
		Guid accountId,
		ProviderKind provider,
		string? providerAccountIdentity)
	{
		return (provider == ProviderKind.Antigravity) &&
			string.Equals(
				providerAccountIdentity,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				StringComparison.OrdinalIgnoreCase) &&
			!IsPrimaryEnabledAntigravityAccount(accountId);
	}

	private Dictionary<Guid, string> FindProviderAccountIdentityConflicts(
		IReadOnlyList<AccountUsageViewModel> accounts,
		IReadOnlyList<UsageSnapshot> snapshots,
		IReadOnlyList<long> lifecycleRevisions)
	{
		if ((accounts.Count != snapshots.Count) ||
			(accounts.Count != lifecycleRevisions.Count))
		{
			throw new ArgumentException(
				"帳號、用量快照與 lifecycle 數量必須一致。",
				nameof(snapshots));
		}

		Dictionary<ProviderKind, Dictionary<string, Guid>> owners =
			CreateProviderAccountIdentityOwners();
		Dictionary<Guid, string> conflicts = new();

		for (int index = 0; index < accounts.Count; index++)
		{
			AccountUsageViewModel account = accounts[index];
			UsageSnapshot snapshot = snapshots[index];

			if (!account.IsEnabled ||
				!Accounts.Contains(account) ||
				(account.LifecycleRevision != lifecycleRevisions[index]) ||
				!account.CanAcceptLiveProviderAccountSnapshot(snapshot))
			{
				continue;
			}

			if (!TryClaimProviderAccountIdentity(
					account,
					snapshot,
					owners))
			{
				conflicts[account.Id] =
					ProviderAccountIdentityConflictMessage;
			}
		}

		return conflicts;
	}

	private async Task PersistProviderAccountIdentityBindingsAsync()
	{
		if (!HasPendingProviderAccountIdentityBindings())
		{
			ResetProviderIdentityPersistenceRetryBackoff();
			return;
		}

		await _accountMutationGate.WaitAsync();

		try
		{
			List<(AccountUsageViewModel Account, UsageSnapshot Snapshot, string Identity)>
				bindings = Accounts
					.Select(account =>
					{
						UsageSnapshot? snapshot = account.CurrentSnapshot;
						string? identity = null;
						bool hasIdentity = (snapshot is not null) &&
							!IsRuntimeOnlyCachedProviderAccountIdentity(account.Id) &&
							account.CanPersistCurrentSnapshot &&
							!account.DidRejectProviderAccountSnapshot &&
							TryGetUsableProviderAccountIdentity(
								snapshot,
								out identity);
						return (
							Account: account,
							Snapshot: snapshot,
							Identity: identity,
							HasIdentity: hasIdentity);
					})
					.Where(item =>
						item.HasIdentity &&
						(item.Snapshot is not null) &&
						(item.Identity is not null) &&
						!ProviderAccountIdentityRules.Comparer.Equals(
							item.Account.Profile.ProviderAccountIdentity,
							item.Identity))
					.Select(item => (
						item.Account,
						item.Snapshot!,
						item.Identity!))
					.ToList();

			if (bindings.Count == 0)
			{
				ResetProviderIdentityPersistenceRetryBackoff();
				return;
			}

			if (!IsProviderIdentityPersistenceRetryDue())
			{
				RejectUnpersistedProviderAccountIdentityBindings(bindings);
				return;
			}

			if (!CanManageAccounts)
			{
				RecordProviderIdentityPersistenceFailure();
				RejectUnpersistedProviderAccountIdentityBindings(bindings);
				return;
			}

			Dictionary<Guid, string> identitiesByAccountId = bindings
				.ToDictionary(binding => binding.Account.Id, binding => binding.Identity);
			List<AccountProfile> candidateProfiles = _accountProfiles
				.Select(profile => identitiesByAccountId.TryGetValue(
						profile.Id,
						out string? providerAccountIdentity)
					? profile with
					{
						ProviderAccountIdentity = providerAccountIdentity
					}
					: profile)
				.ToList();

			try
			{
				await SaveAccountProfilesWithTransientRetryAsync(
					candidateProfiles,
					CancellationToken.None);
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				ResetProviderIdentityPersistenceRetryBackoff();
				ApplyProfiles(candidateProfiles);
				AccountSettingsHealthMessage = exception.Message;
				return;
			}
			catch
			{
				RecordProviderIdentityPersistenceFailure();
				RejectUnpersistedProviderAccountIdentityBindings(bindings);
				return;
			}

			ResetProviderIdentityPersistenceRetryBackoff();
			ApplyProfiles(candidateProfiles);

			if (string.Equals(
					AccountSettingsHealthMessage,
					ProviderAccountIdentityPersistenceFailureMessage,
					StringComparison.Ordinal))
			{
				AccountSettingsHealthMessage = string.Empty;
			}
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<bool> RetryPendingAntigravityConnectionsNowAsync(
		CancellationToken cancellationToken = default)
	{
		if (!IsAntigravityConnectionRetryDue())
		{
			return false;
		}

		await _accountMutationGate.WaitAsync(cancellationToken);
		try
		{
			return await RecoverPendingAntigravityConnectionsAsync(
				cancellationToken,
				allowProviderRefresh: true);
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	private async Task<bool> RecoverPendingAntigravityConnectionsAsync(
		CancellationToken cancellationToken,
		bool allowProviderRefresh)
	{
		if ((_antigravityConnectionPendingStore is not null) &&
			!_isAntigravityConnectionPendingWorkHydrated)
		{
			IReadOnlyList<AntigravityConnectionPendingWork> persistedWork;
			try
			{
				persistedWork =
					await _antigravityConnectionPendingStore.LoadAsync(
						cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (InvalidDataException)
			{
				BlockAntigravityConnectionJournalHydration();
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityConnectionPendingInvalidDataMessage);
				RecordAntigravityConnectionRetryResult(completed: false);
				return false;
			}
			catch
			{
				BlockAntigravityConnectionJournalHydration();
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityConnectionJournalUnavailableMessage);
				RecordAntigravityConnectionRetryResult(completed: false);
				return false;
			}

			ClearAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			HashSet<Guid> persistedAccountIds = persistedWork
				.Select(work => work.AccountId)
				.ToHashSet();
			foreach (AntigravityConnectionPendingWork work in persistedWork)
			{
				if (_unjournaledAntigravityConnectionWorkAccountIds.Contains(
						work.AccountId))
				{
					continue;
				}

				_pendingAntigravityConnectionWork[work.AccountId] = work;
			}

			AntigravityConnectionPendingWork[] workNeedingJournal =
				_pendingAntigravityConnectionWork.Values
					.Where(work =>
						_unjournaledAntigravityConnectionWorkAccountIds.Contains(
							work.AccountId) ||
						!persistedAccountIds.Contains(work.AccountId))
					.ToArray();
			foreach (AntigravityConnectionPendingWork work in workNeedingJournal)
			{
				try
				{
					if (work.IsSetupPending)
					{
						if (work.SetupAttemptId is not Guid setupAttemptId)
						{
							throw new InvalidDataException(
								"Antigravity setup intent 缺少 attempt ID。");
						}

						await _antigravityConnectionPendingStore.BeginSetupAsync(
							work.AccountId,
							work.ExpectedProfileIdentityFingerprint,
							setupAttemptId,
							cancellationToken);
					}
					else
					{
						await _antigravityConnectionPendingStore.UpsertAsync(
							work.AccountId,
							work.ExpectedProfileIdentityFingerprint,
							cancellationToken);
						_pendingAntigravityConnectionWork[work.AccountId] =
							work with
							{
								CachePending = true,
								ProfilePending = true
							};
					}

					_unjournaledAntigravityConnectionWorkAccountIds.Remove(
						work.AccountId);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					throw;
				}
				catch
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					MarkPendingAntigravityConnection(work.AccountId);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					RecordAntigravityConnectionRetryResult(completed: false);
					return false;
				}
			}

			_isAntigravityConnectionPendingWorkHydrated = true;
			ClearAntigravityConnectionJournalHydrationBlock();
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityConnectionJournalUnavailableMessage);
		}
		else if (_antigravityConnectionPendingStore is null)
		{
			_isAntigravityConnectionPendingWorkHydrated = true;
			_unjournaledAntigravityConnectionWorkAccountIds.Clear();
			ClearAntigravityConnectionJournalHydrationBlock();
		}

		if (_pendingAntigravityConnectionWork.Count == 0)
		{
			bool cleanupCompletedWithNoPendingConnection =
				await TryCleanupOrphanedAntigravitySetupArtifactsAsync(
					cancellationToken);
			if (cleanupCompletedWithNoPendingConnection)
			{
				ResetAntigravityConnectionRetryBackoff();
			}
			else
			{
				RecordAntigravityConnectionRetryResult(completed: false);
			}
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingMessage);
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityConnectionJournalUnavailableMessage);
			ClearAccountSettingsHealthWarning(
				OfficialAntigravitySetupCompletionPendingMessage);
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityCacheCleanupFailureMessage);
			return cleanupCompletedWithNoPendingConnection;
		}

		bool completedAll = true;
		foreach (AntigravityConnectionPendingWork initialWork in
			_pendingAntigravityConnectionWork.Values.ToArray())
		{
			int profileIndex = _accountProfiles.FindIndex(profile =>
				(profile.Id == initialWork.AccountId) &&
				(profile.Provider == ProviderKind.Antigravity));
			AccountUsageViewModel? account = Accounts.FirstOrDefault(candidate =>
				(candidate.Id == initialWork.AccountId) &&
				candidate.IsAntigravity);
			if ((profileIndex < 0) || (account is null))
			{
				completedAll &= await RemovePendingAntigravityConnectionWorkAsync(
					initialWork.AccountId,
					cancellationToken);
				continue;
			}

			AccountProfile profile = _accountProfiles[profileIndex];
			bool doesProfileMatchPendingWork =
				DoesProfileMatchAntigravityPendingWork(profile, initialWork);
			if (initialWork.IsSetupPending && !doesProfileMatchPendingWork)
			{
				completedAll &= await RemovePendingAntigravityConnectionWorkAsync(
					initialWork.AccountId,
					cancellationToken);
				continue;
			}

			if (!CanManageAccounts ||
				!account.CanManage ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider))
			{
				MarkPendingAntigravityConnection(account.Id);
				completedAll = false;
				continue;
			}

			MarkPendingAntigravityConnection(account.Id);
			AntigravityConnectionPendingWork work =
				_pendingAntigravityConnectionWork[account.Id];
			string? targetProviderAccountIdentity = null;
			if (work.IsSetupPending)
			{
				AntigravityPendingTargetResolution setupResolution =
					await ResolvePendingAntigravitySetupIntentAsync(
						account,
						work,
						allowProviderRefresh,
						cancellationToken);
				if (setupResolution.Resolution ==
					AntigravitySetupIntentResolution.Pending)
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravitySetupCompletionPendingMessage);
					completedAll = false;
					continue;
				}

				if (setupResolution.Resolution ==
					AntigravitySetupIntentResolution.Discarded)
				{
					continue;
				}

				work = _pendingAntigravityConnectionWork[account.Id];
				targetProviderAccountIdentity =
					setupResolution.TargetProviderAccountIdentity;
			}

			if (targetProviderAccountIdentity is null)
			{
				AntigravityPendingTargetResolution targetResolution =
					await ResolvePendingAntigravityTargetAsync(
						account,
						profile,
						work,
						allowProviderRefresh,
						cancellationToken);
				if (targetResolution.Resolution ==
					AntigravitySetupIntentResolution.Pending)
				{
					completedAll = false;
					continue;
				}

				if (targetResolution.Resolution ==
					AntigravitySetupIntentResolution.Discarded)
				{
					continue;
				}

				targetProviderAccountIdentity =
					targetResolution.TargetProviderAccountIdentity;
			}

			if (string.IsNullOrWhiteSpace(targetProviderAccountIdentity))
			{
				completedAll = false;
				continue;
			}

			profile = _accountProfiles[profileIndex];
			bool isProfileAlreadyCommitted =
				ProviderAccountIdentityRules.Comparer.Equals(
					profile.ProviderAccountIdentity,
					targetProviderAccountIdentity);
			doesProfileMatchPendingWork =
				DoesProfileMatchAntigravityPendingWork(profile, work);
			if (!isProfileAlreadyCommitted && !doesProfileMatchPendingWork)
			{
				completedAll &= await RemovePendingAntigravityConnectionWorkAsync(
					work.AccountId,
					cancellationToken);
				continue;
			}

			if (work.CachePending)
			{
				_usageRefreshCoordinator.Invalidate(account.Id);
				if (!await DeleteCachedSnapshotSafelyAsync(
						account.Id,
						account.Provider,
						cancellationToken,
						retryTransientFailures: true))
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityCacheCleanupFailureMessage);
					completedAll = false;
					continue;
				}

				if (!await MarkPendingAntigravityCacheCompletedAsync(
						account.Id,
						cancellationToken))
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingMessage);
					completedAll = false;
					continue;
				}

				ClearAccountSettingsHealthWarning(
					OfficialAntigravityCacheCleanupFailureMessage);
				work = _pendingAntigravityConnectionWork[account.Id];
			}

			if (!work.ProfilePending)
			{
				continue;
			}

			string? committedWarning = null;
			List<AccountProfile> candidateProfiles = _accountProfiles.ToList();
			candidateProfiles[profileIndex] =
				candidateProfiles[profileIndex] with
				{
					ProviderAccountIdentity =
						targetProviderAccountIdentity
				};
			if (!ProviderAccountIdentityRules.Comparer.Equals(
					profile.ProviderAccountIdentity,
					targetProviderAccountIdentity))
			{
				try
				{
					await SaveAccountProfilesWithTransientRetryAsync(
						candidateProfiles,
						cancellationToken);
				}
				catch (AccountProfileStoreException exception) when (
					exception.HasCommittedChanges)
				{
					committedWarning = exception.Message;
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingMessage);
					completedAll = false;
					continue;
				}

				ApplyProfiles(candidateProfiles);
			}

			if (committedWarning is not null)
			{
				AccountSettingsHealthMessage = committedWarning;
			}

			if (!await MarkPendingAntigravityProfileCompletedAsync(
					account.Id,
					cancellationToken))
			{
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityConnectionPendingMessage);
				completedAll = false;
			}
		}

		bool artifactCleanupCompleted =
			await TryCleanupOrphanedAntigravitySetupArtifactsAsync(
				cancellationToken);
		completedAll &= artifactCleanupCompleted;
		completedAll &= _pendingAntigravityConnectionWork.Count == 0;
		if (!artifactCleanupCompleted || allowProviderRefresh || completedAll)
		{
			RecordAntigravityConnectionRetryResult(completedAll);
		}
		if (completedAll)
		{
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingMessage);
			ClearAccountSettingsHealthWarning(
				OfficialAntigravitySetupCompletionPendingMessage);
			ClearAccountSettingsHealthWarning(
				OfficialAntigravityCacheCleanupFailureMessage);
		}

		return completedAll;
	}

	private async Task<AntigravityPendingTargetResolution>
		ResolvePendingAntigravitySetupIntentAsync(
			AccountUsageViewModel account,
			AntigravityConnectionPendingWork setupIntent,
			bool allowProviderRefresh,
			CancellationToken cancellationToken)
	{
		if (account.IsProviderAccountChangeInProgress)
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if ((setupIntent.SetupAttemptId is not Guid setupAttemptId) ||
			(_antigravityConnectionPendingStore is null))
		{
			return await DiscardPendingAntigravitySetupIntentAsync(
				account,
				verificationSnapshot: null,
				cancellationToken);
		}

		AntigravitySetupApprovalReceipt? approval;
		AntigravitySetupAttemptState? attemptState;
		try
		{
			approval = await _antigravityConnectionPendingStore
				.LoadSetupApprovalAsync(
					setupAttemptId,
					cancellationToken);
			attemptState = await _antigravityConnectionPendingStore
				.LoadSetupAttemptStateAsync(
					setupAttemptId,
					cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException)
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}
		catch
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionJournalUnavailableMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if ((approval is not null) &&
			(attemptState?.Phase ==
				AntigravitySetupAttemptPhase.ApprovalRequested) &&
			!DoesAntigravityAttemptStateMatchApproval(
				attemptState,
				approval))
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (approval is null &&
			(attemptState?.Phase ==
				AntigravitySetupAttemptPhase.ApprovalRequested))
		{
			AntigravitySetupProcessActivity activity =
				GetAntigravitySetupProcessActivity(attemptState);
			if (ShouldWaitForSetupProcessCompletion(
				activity == AntigravitySetupProcessActivity.Inactive,
				IsCurrentApplicationProcess(attemptState)))
			{
				return new(AntigravitySetupIntentResolution.Pending);
			}

			// In-process setup uses the dashboard process identity. The account-change
			// gate above proves its dialog has already returned, even though the App
			// itself is still running.
			approval = CreateApprovalFromAttemptState(attemptState);
		}

		if (approval is null && attemptState is not null)
		{
			if (attemptState.Phase ==
				AntigravitySetupAttemptPhase.Launching)
			{
				// Launching records the parent dashboard process, not the helper.
				// Parent liveness therefore cannot prove that setup is still active.
				if (IsAntigravitySetupLaunchWithinRegistrationGrace(attemptState))
				{
					return new(AntigravitySetupIntentResolution.Pending);
				}

				bool wasRemoved;
				try
				{
					wasRemoved = await _antigravityConnectionPendingStore
						.TryRemoveLaunchingSetupAttemptAsync(
							setupAttemptId,
							attemptState,
							cancellationToken);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (InvalidDataException)
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingInvalidDataMessage);
					return new(AntigravitySetupIntentResolution.Pending);
				}
				catch
				{
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					return new(AntigravitySetupIntentResolution.Pending);
				}

				if (!wasRemoved)
				{
					// The helper advanced the same attempt after our read. Keep the
					// durable intent so the next pass observes its newer state.
					return new(AntigravitySetupIntentResolution.Pending);
				}

			}
			else
			{
				AntigravitySetupProcessActivity activity =
					GetAntigravitySetupProcessActivity(attemptState);
				if (ShouldWaitForSetupProcessCompletion(
					activity == AntigravitySetupProcessActivity.Inactive,
					IsCurrentApplicationProcess(attemptState)))
				{
					return new(AntigravitySetupIntentResolution.Pending);
				}
			}
		}

		if (approval is null)
		{
			return await DiscardPendingAntigravitySetupIntentAsync(
				account,
				verificationSnapshot: null,
				cancellationToken);
		}

		if (!allowProviderRefresh)
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		UsageSnapshot verificationSnapshot;
		try
		{
			_usageRefreshCoordinator.Invalidate(account.Id);
			verificationSnapshot =
				await _usageRefreshCoordinator.RefreshAsync(
					account.Profile,
					cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (TryGetApprovedAntigravityTargetIdentity(
				approval,
				verificationSnapshot,
				out string? targetProviderAccountIdentity) &&
			(targetProviderAccountIdentity is not null))
		{
			AntigravityConnectionPendingWork completedWork =
				setupIntent with
				{
					CachePending = true,
					ProfilePending = true
				};
			_pendingAntigravityConnectionWork[account.Id] = completedWork;

			if (_antigravityConnectionPendingStore is not null)
			{
				_unjournaledAntigravityConnectionWorkAccountIds.Add(account.Id);
				try
				{
					await _antigravityConnectionPendingStore.UpsertAsync(
						account.Id,
						completedWork.ExpectedProfileIdentityFingerprint,
						cancellationToken);
					_unjournaledAntigravityConnectionWorkAccountIds.Remove(
						account.Id);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					throw;
				}
				catch
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					BlockAntigravityConnectionJournalHydration();
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					return new(AntigravitySetupIntentResolution.Pending);
				}
			}

			account.ClearAntigravityConnectionJournalCheckPending();
			account.MarkOfficialAntigravityConnectionCommitPending();
			if (!_pendingAntigravityConnectionWork.Values.Any(
					work => work.IsSetupPending))
			{
				ClearAccountSettingsHealthWarning(
					OfficialAntigravitySetupCompletionPendingMessage);
			}

			return new(
				AntigravitySetupIntentResolution.ReadyToCommit,
				targetProviderAccountIdentity);
		}

		if ((verificationSnapshot.RecoveryAction ==
				UsageRecoveryAction.Retry) ||
			(verificationSnapshot.Status == SnapshotStatus.Refreshing))
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		return await DiscardPendingAntigravitySetupIntentAsync(
			account,
			verificationSnapshot,
			cancellationToken);
	}

	private async Task<AntigravityPendingTargetResolution>
		ResolvePendingAntigravityTargetAsync(
			AccountUsageViewModel account,
			AccountProfile profile,
			AntigravityConnectionPendingWork work,
			bool allowProviderRefresh,
			CancellationToken cancellationToken)
	{
		if (work.IsSetupPending)
		{
			throw new InvalidOperationException(
				"Antigravity setup intent 尚未確認完成。");
		}

		if (work.SetupAttemptId is not Guid setupAttemptId)
		{
			return new(
				AntigravitySetupIntentResolution.ReadyToCommit,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		}

		if (_antigravityConnectionPendingStore is null)
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		AntigravitySetupApprovalReceipt? approval;
		AntigravitySetupAttemptState? attemptState;
		try
		{
			approval = await _antigravityConnectionPendingStore
				.LoadSetupApprovalAsync(
					setupAttemptId,
					cancellationToken);
			attemptState = await _antigravityConnectionPendingStore
				.LoadSetupAttemptStateAsync(
					setupAttemptId,
					cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException)
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}
		catch
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionJournalUnavailableMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if ((approval is not null) &&
			(attemptState?.Phase ==
				AntigravitySetupAttemptPhase.ApprovalRequested) &&
			!DoesAntigravityAttemptStateMatchApproval(
				attemptState,
				approval))
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (approval is null &&
			(attemptState?.Phase ==
				AntigravitySetupAttemptPhase.ApprovalRequested))
		{
			approval = CreateApprovalFromAttemptState(attemptState);
		}

		if (approval is null)
		{
			AppendAccountSettingsHealthWarning(
				OfficialAntigravityConnectionPendingInvalidDataMessage);
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (ProviderAccountIdentityRules.TryNormalize(
				profile.ProviderAccountIdentity,
				out string? committedIdentity) &&
			(committedIdentity is not null) &&
			DoesAntigravityIdentityMatchApproval(
				committedIdentity,
				approval))
		{
			return new(
				AntigravitySetupIntentResolution.ReadyToCommit,
				committedIdentity);
		}

		if (!DoesProfileMatchAntigravityPendingWork(profile, work))
		{
			return await DiscardPendingAntigravitySetupIntentAsync(
				account,
				verificationSnapshot: null,
				cancellationToken);
		}

		if (!allowProviderRefresh)
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		UsageSnapshot verificationSnapshot;
		try
		{
			_usageRefreshCoordinator.Invalidate(account.Id);
			verificationSnapshot =
				await _usageRefreshCoordinator.RefreshAsync(
					account.Profile,
					cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (TryGetApprovedAntigravityTargetIdentity(
				approval,
				verificationSnapshot,
				out string? targetProviderAccountIdentity) &&
			(targetProviderAccountIdentity is not null))
		{
			return new(
				AntigravitySetupIntentResolution.ReadyToCommit,
				targetProviderAccountIdentity);
		}

		if (IsPendingAntigravitySetupVerification(verificationSnapshot))
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		return await DiscardPendingAntigravitySetupIntentAsync(
			account,
			verificationSnapshot,
			cancellationToken);
	}

	private async Task<AntigravityPendingTargetResolution>
		DiscardPendingAntigravitySetupIntentAsync(
			AccountUsageViewModel account,
			UsageSnapshot? verificationSnapshot,
			CancellationToken cancellationToken)
	{
		if (!await RemovePendingAntigravityConnectionWorkAsync(
				account.Id,
				cancellationToken))
		{
			return new(AntigravitySetupIntentResolution.Pending);
		}

		if (verificationSnapshot is not null)
		{
			if ((verificationSnapshot.Status == SnapshotStatus.Ready) &&
				(verificationSnapshot.Metrics.Count > 0))
			{
				UsageRecoveryAction recoveryAction =
					ProviderAccountIdentityRules.TryNormalize(
						account.Profile.ProviderAccountIdentity,
						out string? persistedIdentity) &&
					(persistedIdentity is not null)
						? UsageRecoveryAction.SwitchAccount
						: UsageRecoveryAction.ConnectAccount;
				account.RejectProviderAccountSnapshot(
					verificationSnapshot,
					AntigravitySetupApprovalMismatchMessage,
					recoveryAction);
			}
			else
			{
				account.ApplyLiveSnapshot(verificationSnapshot);
			}
		}

		if (!_pendingAntigravityConnectionWork.Values.Any(
				work => work.IsSetupPending))
		{
			ClearAccountSettingsHealthWarning(
				OfficialAntigravitySetupCompletionPendingMessage);
		}

		return new(AntigravitySetupIntentResolution.Discarded);
	}

	private static bool DoesProfileMatchAntigravityPendingWork(
		AccountProfile profile,
		AntigravityConnectionPendingWork work)
	{
		return string.Equals(
			CreateAntigravityPendingProfileIdentityFingerprint(
				profile.ProviderAccountIdentity),
			work.ExpectedProfileIdentityFingerprint,
			StringComparison.Ordinal);
	}

	private static AntigravitySetupApprovalReceipt
		CreateApprovalFromAttemptState(
			AntigravitySetupAttemptState state)
	{
		if ((state.Phase !=
				AntigravitySetupAttemptPhase.ApprovalRequested) ||
			(state.SourceKind is not AntigravityMachineSetupSourceKind sourceKind) ||
			string.IsNullOrWhiteSpace(state.TargetIdentityFingerprint))
		{
			throw new InvalidDataException(
				"Antigravity setup approval request 內容無效。");
		}

		return new AntigravitySetupApprovalReceipt(
			state.AttemptId,
			sourceKind,
			state.TargetIdentityFingerprint);
	}

	private static bool DoesAntigravityAttemptStateMatchApproval(
		AntigravitySetupAttemptState state,
		AntigravitySetupApprovalReceipt approval)
	{
		return (state.AttemptId == approval.AttemptId) &&
			(state.SourceKind == approval.SourceKind) &&
			string.Equals(
				state.TargetIdentityFingerprint,
				approval.TargetIdentityFingerprint,
				StringComparison.Ordinal);
	}

	private bool IsAntigravitySetupLaunchWithinRegistrationGrace(
		AntigravitySetupAttemptState state)
	{
		long ageTicks =
			_timeProvider.GetUtcNow().ToUniversalTime().UtcDateTime.Ticks -
			state.CreatedAtUtcTicks;
		return (ageTicks >= -AntigravitySetupLaunchClockSkewTolerance.Ticks) &&
			(ageTicks <= AntigravitySetupLaunchRegistrationGrace.Ticks);
	}

	private static AntigravitySetupProcessActivity
		GetAntigravitySetupProcessActivity(
			AntigravitySetupAttemptState state)
	{
		try
		{
			using Process process = Process.GetProcessById(state.ProcessId);
			if (process.HasExited)
			{
				return AntigravitySetupProcessActivity.Inactive;
			}

			return process.StartTime.ToUniversalTime().Ticks ==
				state.ProcessStartTimeUtcTicks
					? AntigravitySetupProcessActivity.Active
					: AntigravitySetupProcessActivity.Inactive;
		}
		catch (ArgumentException)
		{
			return AntigravitySetupProcessActivity.Inactive;
		}
		catch (InvalidOperationException)
		{
			return AntigravitySetupProcessActivity.Inactive;
		}
		catch (Win32Exception)
		{
			return AntigravitySetupProcessActivity.Unknown;
		}
		catch (NotSupportedException)
		{
			return AntigravitySetupProcessActivity.Unknown;
		}
	}

	private static bool IsCurrentApplicationProcess(
		AntigravitySetupAttemptState state)
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			return (process.Id == state.ProcessId) &&
				(process.StartTime.ToUniversalTime().Ticks ==
					state.ProcessStartTimeUtcTicks);
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		catch (Win32Exception)
		{
			return false;
		}
		catch (NotSupportedException)
		{
			return false;
		}
	}

	internal static bool ShouldWaitForSetupProcessCompletion(
		bool isProcessInactive,
		bool isCurrentApplicationProcess)
	{
		return !isProcessInactive && !isCurrentApplicationProcess;
	}

	private static bool TryGetApprovedAntigravityTargetIdentity(
		AntigravitySetupApprovalReceipt approval,
		UsageSnapshot snapshot,
		out string? targetProviderAccountIdentity)
	{
		if (!TryGetAntigravityTargetIdentity(
				approval.SourceKind,
				snapshot,
				out targetProviderAccountIdentity) ||
			(targetProviderAccountIdentity is null) ||
			!DoesAntigravityIdentityMatchApproval(
				targetProviderAccountIdentity,
				approval))
		{
			targetProviderAccountIdentity = null;
			return false;
		}

		return true;
	}

	private static bool TryGetAntigravityTargetIdentity(
		AntigravityMachineSetupSourceKind sourceKind,
		UsageSnapshot snapshot,
		out string? targetProviderAccountIdentity)
	{
		targetProviderAccountIdentity = null;
		SourceTrust expectedSourceTrust = sourceKind switch
		{
			AntigravityMachineSetupSourceKind.OfficialPrint =>
				SourceTrust.OfficialExperimental,
			_ => SourceTrust.Unavailable
		};

		if ((snapshot.Account.Provider != ProviderKind.Antigravity) ||
			(snapshot.Status != SnapshotStatus.Ready) ||
			(snapshot.SourceTrust != expectedSourceTrust) ||
			!string.IsNullOrWhiteSpace(snapshot.Error) ||
			(snapshot.Metrics.Count == 0) ||
			!ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null) ||
			((sourceKind == AntigravityMachineSetupSourceKind.OfficialPrint) &&
				!string.Equals(
					normalizedIdentity,
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
					StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		targetProviderAccountIdentity = normalizedIdentity;
		return true;
	}

	private static bool DoesAntigravityIdentityMatchApproval(
		string providerAccountIdentity,
		AntigravitySetupApprovalReceipt approval)
	{
		return string.Equals(
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(providerAccountIdentity),
			approval.TargetIdentityFingerprint,
			StringComparison.Ordinal);
	}

	private static bool IsPendingAntigravitySetupVerification(
		UsageSnapshot snapshot)
	{
		return (snapshot.RecoveryAction == UsageRecoveryAction.Retry) ||
			(snapshot.Status == SnapshotStatus.Refreshing);
	}

	private bool IsAntigravityConnectionJournalUnavailable()
	{
		return (_antigravityConnectionPendingStore is not null) &&
			(!_isAntigravityConnectionPendingWorkHydrated ||
				_isAntigravityConnectionPendingStoreHydrationBlocked);
	}

	private void BlockAntigravityConnectionJournalHydration()
	{
		if (_antigravityConnectionPendingStore is null)
		{
			return;
		}

		_isAntigravityConnectionPendingStoreHydrationBlocked = true;
		foreach (AccountUsageViewModel account in Accounts.Where(
			account => account.IsAntigravity))
		{
			account.MarkAntigravityConnectionJournalCheckPending();
		}
	}

	private void ClearAntigravityConnectionJournalHydrationBlock()
	{
		if (!_isAntigravityConnectionPendingStoreHydrationBlocked)
		{
			return;
		}

		_isAntigravityConnectionPendingStoreHydrationBlocked = false;
		foreach (AccountUsageViewModel account in Accounts.Where(
			account => account.IsAntigravity))
		{
			account.ClearAntigravityConnectionJournalCheckPending();
		}
	}

	private void MarkPendingAntigravityConnection(Guid accountId)
	{
		AccountUsageViewModel? account = Accounts.FirstOrDefault(account =>
			(account.Id == accountId) &&
			account.IsAntigravity);
		if (account is null)
		{
			return;
		}

		if (_pendingAntigravityConnectionWork.TryGetValue(
				accountId,
				out AntigravityConnectionPendingWork? work) &&
			work.IsSetupPending)
		{
			if (!account.IsProviderAccountChangeInProgress)
			{
				account.MarkAntigravityConnectionJournalCheckPending();
			}
		}
		else
		{
			account.MarkOfficialAntigravityConnectionCommitPending();
		}
	}

	internal static string CreateAntigravityPendingProfileIdentityFingerprint(
		string? providerAccountIdentity)
	{
		if (!ProviderAccountIdentityRules.TryNormalize(
				providerAccountIdentity,
				out string? normalizedIdentity))
		{
			throw new InvalidDataException(
				"Antigravity 帳號連接資料格式無效。");
		}

		string fingerprintInput = normalizedIdentity is null
			? "AiUsageDashboard.AntigravityPending.v1\nunbound"
			: $"AiUsageDashboard.AntigravityPending.v1\nbound\n{normalizedIdentity.ToUpperInvariant()}";
		return Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
	}

	private async Task<bool> MarkPendingAntigravityCacheCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (_antigravityConnectionPendingStore is not null)
		{
			try
			{
				await _antigravityConnectionPendingStore.MarkCacheCompletedAsync(
					accountId,
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

		_pendingAntigravityConnectionWork[accountId] =
			_pendingAntigravityConnectionWork[accountId] with
			{
				CachePending = false
			};
		return true;
	}

	private async Task<bool> MarkPendingAntigravityProfileCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		Guid? setupAttemptId =
			_pendingAntigravityConnectionWork.TryGetValue(
				accountId,
				out AntigravityConnectionPendingWork? pendingWork)
				? pendingWork.SetupAttemptId
				: null;
		if (_antigravityConnectionPendingStore is not null)
		{
			try
			{
				await _antigravityConnectionPendingStore.MarkProfileCompletedAsync(
					accountId,
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

		_pendingAntigravityConnectionWork.Remove(accountId);
		_unjournaledAntigravityConnectionWorkAccountIds.Remove(accountId);
		Accounts.FirstOrDefault(account =>
			(account.Id == accountId) &&
			account.IsAntigravity)
			?.ClearOfficialAntigravityConnectionCommitPending();
		Accounts.FirstOrDefault(account =>
			(account.Id == accountId) &&
			account.IsAntigravity)
			?.ClearAntigravityConnectionJournalCheckPending();
		await TryRemoveAntigravitySetupApprovalAsync(setupAttemptId);
		return true;
	}

	private async Task<bool> RemovePendingAntigravityConnectionWorkAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		Guid? setupAttemptId =
			_pendingAntigravityConnectionWork.TryGetValue(
				accountId,
				out AntigravityConnectionPendingWork? pendingWork)
				? pendingWork.SetupAttemptId
				: null;
		if (_antigravityConnectionPendingStore is not null)
		{
			try
			{
				await _antigravityConnectionPendingStore.RemoveAsync(
					accountId,
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

		_pendingAntigravityConnectionWork.Remove(accountId);
		_unjournaledAntigravityConnectionWorkAccountIds.Remove(accountId);
		Accounts.FirstOrDefault(account =>
			(account.Id == accountId) &&
			account.IsAntigravity)
			?.ClearOfficialAntigravityConnectionCommitPending();
		Accounts.FirstOrDefault(account =>
			(account.Id == accountId) &&
			account.IsAntigravity)
			?.ClearAntigravityConnectionJournalCheckPending();
		await TryRemoveAntigravitySetupApprovalAsync(setupAttemptId);
		return true;
	}

	private async Task<bool>
		TryCleanupOrphanedAntigravitySetupArtifactsAsync(
			CancellationToken cancellationToken)
	{
		if (!_isAntigravitySetupArtifactCleanupPending)
		{
			return true;
		}

		if (_antigravityConnectionPendingStore is null)
		{
			_isAntigravitySetupArtifactCleanupPending = false;
			return true;
		}

		if (_pendingAntigravityConnectionWork.Values.Any(work =>
				work.IsSetupPending && (work.SetupAttemptId is null)))
		{
			// A legacy setup intent has no attempt ID, so the journal cannot
			// prove which attempt-scoped files are still active.
			return true;
		}

		HashSet<Guid> activeAttemptIds = _pendingAntigravityConnectionWork
			.Values
			.Where(work => work.SetupAttemptId.HasValue)
			.Select(work => work.SetupAttemptId!.Value)
			.ToHashSet();
		try
		{
			await _antigravityConnectionPendingStore
				.CleanupOrphanedSetupArtifactsAsync(
					activeAttemptIds,
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

		_isAntigravitySetupArtifactCleanupPending = false;
		return true;
	}

	private async Task TryRemoveAntigravitySetupApprovalAsync(
		Guid? setupAttemptId)
	{
		if ((_antigravityConnectionPendingStore is null) ||
			(setupAttemptId is not Guid attemptId))
		{
			return;
		}

		try
		{
			await _antigravityConnectionPendingStore.RemoveSetupApprovalAsync(
				attemptId,
				CancellationToken.None);
		}
		catch
		{
			_isAntigravitySetupArtifactCleanupPending = true;
			// An orphaned attempt-scoped receipt cannot promote work after the
			// matching journal entry has been removed.
		}

		try
		{
			await _antigravityConnectionPendingStore.RemoveSetupAttemptStateAsync(
				attemptId,
				CancellationToken.None);
		}
		catch
		{
			_isAntigravitySetupArtifactCleanupPending = true;
			// The account-scoped journal is the authority. Orphaned attempt
			// state is inert and can be cleaned by a later best-effort pass.
		}
	}

	private bool IsAntigravityConnectionRetryDue()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			return _timeProvider.GetUtcNow().ToUniversalTime() >=
				_antigravityConnectionRetryNotBeforeUtc;
		}
	}

	private void RecordAntigravityConnectionRetryResult(bool completed)
	{
		if (completed)
		{
			ResetAntigravityConnectionRetryBackoff();
			return;
		}

		lock (_backgroundPersistenceRetrySync)
		{
			_antigravityConnectionRetryFailureCount = Math.Min(
				BackgroundPersistenceRetryDelays.Length,
				_antigravityConnectionRetryFailureCount + 1);
			_antigravityConnectionRetryNotBeforeUtc =
				_timeProvider.GetUtcNow().ToUniversalTime() +
				BackgroundPersistenceRetryDelays[
					_antigravityConnectionRetryFailureCount - 1];
		}
	}

	private void ResetAntigravityConnectionRetryBackoff()
	{
		lock (_backgroundPersistenceRetrySync)
		{
			_antigravityConnectionRetryFailureCount = 0;
			_antigravityConnectionRetryNotBeforeUtc =
				DateTimeOffset.MinValue;
		}
	}

	internal async Task<bool> TryPersistOfficialAntigravityConnectionAsync(
		AccountUsageViewModel account,
		CancellationToken cancellationToken = default)
	{
		return await TryPersistAntigravityConnectionCoreAsync(
			account,
			setupAttemptId: null,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			cancellationToken);
	}

	internal async Task<bool> TryPersistAntigravityConnectionAsync(
		AccountUsageViewModel account,
		Guid setupAttemptId,
		AntigravityMachineSetupSourceKind sourceKind,
		CancellationToken cancellationToken = default)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		return await TryPersistAntigravityConnectionCoreAsync(
			account,
			setupAttemptId,
			sourceKind,
			cancellationToken);
	}

	private async Task<bool> TryPersistAntigravityConnectionCoreAsync(
		AccountUsageViewModel account,
		Guid? setupAttemptId,
		AntigravityMachineSetupSourceKind sourceKind,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);
		if (sourceKind != AntigravityMachineSetupSourceKind.OfficialPrint)
		{
			throw new ArgumentOutOfRangeException(nameof(sourceKind));
		}

		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			if (!CanManageAccounts ||
				!account.CanManage ||
				!account.IsEnabled ||
				IsAntigravityConnectionJournalUnavailable() ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				!account.IsAntigravity ||
				!account.IsProviderAccountChangeInProgress ||
				!account.IsProviderAccountChangeCommitInProgress ||
				!Accounts.Contains(account))
			{
				return false;
			}

			int profileIndex = _accountProfiles.FindIndex(profile =>
				(profile.Id == account.Id) &&
				(profile.Provider == ProviderKind.Antigravity));
			if (profileIndex < 0)
			{
				return false;
			}

			string providerAccountIdentity;
			AntigravityConnectionPendingWork? setupIntent = null;
			if (setupAttemptId is Guid expectedAttemptId)
			{
				if (!_pendingAntigravityConnectionWork.TryGetValue(
						account.Id,
						out setupIntent) ||
					!setupIntent.IsSetupPending ||
					(setupIntent.SetupAttemptId != expectedAttemptId))
				{
					return false;
				}

				AntigravitySetupApprovalReceipt? approval = null;
				if (_antigravityConnectionPendingStore is not null)
				{
					try
					{
						approval = await _antigravityConnectionPendingStore
							.LoadSetupApprovalAsync(
								expectedAttemptId,
								cancellationToken);
					}
					catch (OperationCanceledException) when (
						cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch (InvalidDataException)
					{
						AppendAccountSettingsHealthWarning(
							OfficialAntigravityConnectionPendingInvalidDataMessage);
						return false;
					}
					catch
					{
						AppendAccountSettingsHealthWarning(
							OfficialAntigravityConnectionJournalUnavailableMessage);
						return false;
					}

					if ((approval is null) ||
						(approval.SourceKind != sourceKind))
					{
						return false;
					}
				}
				else if (sourceKind ==
					AntigravityMachineSetupSourceKind.OfficialPrint)
				{
					providerAccountIdentity =
						AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
					goto TargetIdentityVerified;
				}

				UsageSnapshot verificationSnapshot;
				try
				{
					_usageRefreshCoordinator.Invalidate(account.Id);
					verificationSnapshot =
						await _usageRefreshCoordinator.RefreshAsync(
							account.Profile,
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

				bool isVerified = approval is not null
					? TryGetApprovedAntigravityTargetIdentity(
						approval,
						verificationSnapshot,
						out string? verifiedIdentity)
					: TryGetAntigravityTargetIdentity(
						sourceKind,
						verificationSnapshot,
						out verifiedIdentity);
				if (!isVerified || string.IsNullOrWhiteSpace(verifiedIdentity))
				{
					return false;
				}

				providerAccountIdentity = verifiedIdentity;

			TargetIdentityVerified:;
			}
			else
			{
				providerAccountIdentity =
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
			}

			AntigravityConnectionPendingWork pendingWork = setupIntent is null
				? new AntigravityConnectionPendingWork(
					account.Id,
					CreateAntigravityPendingProfileIdentityFingerprint(
						_accountProfiles[profileIndex].ProviderAccountIdentity),
					CachePending: true,
					ProfilePending: true)
				: setupIntent with
				{
					CachePending = true,
					ProfilePending = true
				};
			_pendingAntigravityConnectionWork[account.Id] = pendingWork;
			bool usesPendingJournal =
				_antigravityConnectionPendingStore is not null;
			if (usesPendingJournal)
			{
				ResetAntigravityConnectionRetryBackoff();
				_unjournaledAntigravityConnectionWorkAccountIds.Add(
					account.Id);

				try
				{
					await _antigravityConnectionPendingStore!.UpsertAsync(
						account.Id,
						pendingWork.ExpectedProfileIdentityFingerprint,
						cancellationToken);
					_unjournaledAntigravityConnectionWorkAccountIds.Remove(
						account.Id);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					account.MarkOfficialAntigravityConnectionCommitPending();
					BlockAntigravityConnectionJournalHydration();
					throw;
				}
				catch
				{
					_isAntigravityConnectionPendingWorkHydrated = false;
					account.MarkOfficialAntigravityConnectionCommitPending();
					BlockAntigravityConnectionJournalHydration();
					RecordAntigravityConnectionRetryResult(completed: false);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionJournalUnavailableMessage);
					return false;
				}
			}

			account.ClearAntigravityConnectionJournalCheckPending();
			account.MarkOfficialAntigravityConnectionCommitPending();
			_usageRefreshCoordinator.Invalidate(account.Id);
			if (!await DeleteCachedSnapshotSafelyAsync(
					account.Id,
					account.Provider,
					cancellationToken,
					retryTransientFailures: true))
			{
				account.MarkOfficialAntigravityConnectionCommitPending();
				RecordAntigravityConnectionRetryResult(completed: false);
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityCacheCleanupFailureMessage);
				return false;
			}

			if (usesPendingJournal)
			{
				try
				{
					await _antigravityConnectionPendingStore!
						.MarkCacheCompletedAsync(
							account.Id,
							cancellationToken);
					_pendingAntigravityConnectionWork[account.Id] =
						_pendingAntigravityConnectionWork[account.Id] with
						{
							CachePending = false
						};
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
					account.MarkOfficialAntigravityConnectionCommitPending();
					RecordAntigravityConnectionRetryResult(completed: false);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingMessage);
					return false;
				}
			}
			else
			{
				_pendingAntigravityConnectionWork[account.Id] =
					pendingWork with { CachePending = false };
			}

			ClearAccountSettingsHealthWarning(
				OfficialAntigravityCacheCleanupFailureMessage);

			List<AccountProfile> candidateProfiles = _accountProfiles.ToList();
			candidateProfiles[profileIndex] =
				candidateProfiles[profileIndex] with
				{
					ProviderAccountIdentity = providerAccountIdentity
				};
			string? committedWarning = null;

			try
			{
				await SaveAccountProfilesWithTransientRetryAsync(
					candidateProfiles,
					cancellationToken);
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				committedWarning = exception.Message;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				account.MarkOfficialAntigravityConnectionCommitPending();
				RecordAntigravityConnectionRetryResult(completed: false);
				AppendAccountSettingsHealthWarning(
					OfficialAntigravityConnectionPendingMessage);
				return false;
			}

			ApplyProfiles(candidateProfiles);
			account.SeedProviderAccountIdentity(providerAccountIdentity);

			if (committedWarning is not null)
			{
				AccountSettingsHealthMessage = committedWarning;
			}
			else if (string.Equals(
					AccountSettingsHealthMessage,
					AuthenticatedProviderBindingPersistenceFailureMessage,
					StringComparison.Ordinal))
			{
				AccountSettingsHealthMessage = string.Empty;
			}

			if (usesPendingJournal)
			{
				if (await MarkPendingAntigravityProfileCompletedAsync(
						account.Id,
						CancellationToken.None))
				{
					ResetAntigravityConnectionRetryBackoff();
					ClearAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingMessage);
				}
				else
				{
					RecordAntigravityConnectionRetryResult(completed: false);
					AppendAccountSettingsHealthWarning(
						OfficialAntigravityConnectionPendingMessage);
				}
			}
			else
			{
				_pendingAntigravityConnectionWork.Remove(account.Id);
				_unjournaledAntigravityConnectionWorkAccountIds.Remove(
					account.Id);
				account.ClearOfficialAntigravityConnectionCommitPending();
				ResetAntigravityConnectionRetryBackoff();
			}

			return true;
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<TResult> ExecuteAuthenticatedProviderBindingMutationAsync<TResult>(
		Func<Task<TResult>> mutation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(mutation);
		await _accountMutationGate.WaitAsync(cancellationToken);

		try
		{
			return await mutation();
		}
		finally
		{
			_accountMutationGate.Release();
		}
	}

	internal async Task<AuthenticatedProviderBindingCommitResult>
		TryPersistAuthenticatedProviderBindingAsync(
			AccountUsageViewModel account,
			string providerAccountIdentity,
			CancellationToken cancellationToken = default,
			UsageSnapshot? retainedClaudeLegacySnapshot = null,
			ClaudeSubscriptionContext? claudeSubscriptionContext = null,
			bool isAccountMutationGateHeld = false,
			bool preserveExistingBindingOnIdentityConflict = false)
	{
		ArgumentNullException.ThrowIfNull(account);

		if ((claudeSubscriptionContext is not null) &&
			(!account.IsClaude ||
			(claudeSubscriptionContext.VerificationState !=
				SubscriptionVerificationState.Verified)))
		{
			throw new ArgumentException(
				"Claude 訂閱範圍必須是已驗證狀態。",
				nameof(claudeSubscriptionContext));
		}

		if ((retainedClaudeLegacySnapshot is not null) &&
			((claudeSubscriptionContext is null) ||
			(_usageSnapshotStore is null) ||
			!IsUsableClaudeLegacySnapshot(
				account,
				retainedClaudeLegacySnapshot)))
		{
			throw new ArgumentException(
				"保留的 Claude 舊版用量資料無法安全遷移。",
				nameof(retainedClaudeLegacySnapshot));
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				providerAccountIdentity,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null))
		{
			throw new ArgumentException(
				"帳號資訊格式無效。",
				nameof(providerAccountIdentity));
		}

		if (account.IsGrok)
		{
			if (!GrokAccountBinding.TryNormalizePublicBindingIdentity(
					normalizedIdentity,
					out string? normalizedGrokIdentity) ||
				(normalizedGrokIdentity is null))
			{
				throw new ArgumentException(
					"Grok 帳號連接資料格式無效。",
					nameof(providerAccountIdentity));
			}

			normalizedIdentity = normalizedGrokIdentity;
		}
		else if (account.IsCopilot)
		{
			if (!CopilotAccountIdentityRules.TryParse(
					normalizedIdentity,
					out string? normalizedCopilotIdentity) ||
				(normalizedCopilotIdentity is null))
			{
				throw new ArgumentException(
					"Copilot 帳號連接資料格式無效。",
					nameof(providerAccountIdentity));
			}

			normalizedIdentity = normalizedCopilotIdentity;
		}
		else if ((claudeSubscriptionContext is not null) &&
			(!ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				normalizedIdentity,
				out string? normalizedClaudeIdentity) ||
			(normalizedClaudeIdentity is null)))
		{
			throw new ArgumentException(
				"Claude 訂閱連接資料格式無效。",
				nameof(providerAccountIdentity));
		}

		if (!isAccountMutationGateHeld)
		{
			await _accountMutationGate.WaitAsync(cancellationToken);
		}

		try
		{
			if (!CanManageAccounts ||
				!account.CanManage ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				(!account.IsClaude && !account.IsCodex && !account.IsCopilot &&
					!account.IsGrok) ||
				!account.IsProviderAccountChangeInProgress ||
				!account.IsProviderAccountChangeCommitInProgress ||
				!Accounts.Contains(account))
			{
				return AuthenticatedProviderBindingCommitResult.InvalidState;
			}

			int profileIndex = _accountProfiles.FindIndex(profile =>
				(profile.Id == account.Id) &&
				(profile.Provider == account.Provider));
			if (profileIndex < 0)
			{
				return AuthenticatedProviderBindingCommitResult.InvalidState;
			}

			Dictionary<ProviderKind, Dictionary<string, Guid>> owners =
				CreateProviderAccountIdentityOwners();

			if (owners.TryGetValue(
					account.Provider,
					out Dictionary<string, Guid>? providerOwners) &&
				providerOwners.TryGetValue(normalizedIdentity, out Guid ownerAccountId) &&
				(ownerAccountId != account.Id))
			{
				if (preserveExistingBindingOnIdentityConflict)
				{
					return AuthenticatedProviderBindingCommitResult.IdentityConflict;
				}

				return await FailClosedAuthenticatedProviderBindingConflictCoreAsync(
						account,
						profileIndex,
						cancellationToken)
					? AuthenticatedProviderBindingCommitResult.IdentityConflict
					: AuthenticatedProviderBindingCommitResult
						.IdentityConflictRuntimeFailClosed;
			}

			List<AccountProfile> candidateProfiles = _accountProfiles.ToList();
			candidateProfiles[profileIndex] = candidateProfiles[profileIndex] with
			{
				ProviderAccountIdentity = normalizedIdentity
			};
			string? committedWarning = null;

			try
			{
				await SaveAccountProfilesWithTransientRetryAsync(
					candidateProfiles,
					cancellationToken);
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				committedWarning = exception.Message;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				AccountSettingsHealthMessage =
					AuthenticatedProviderBindingPersistenceFailureMessage;
				return AuthenticatedProviderBindingCommitResult.PersistenceFailed;
			}

			ApplyProfiles(candidateProfiles);
			account.SeedProviderAccountIdentity(normalizedIdentity);
			UsageSnapshot? retainedClaudeSnapshot =
				retainedClaudeLegacySnapshot is null
					? null
					: retainedClaudeLegacySnapshot with
					{
						Account = candidateProfiles[profileIndex],
						Status = SnapshotStatus.Stale,
						Error =
				"目前顯示你已確認歸屬於此 Claude 訂閱範圍的舊版歷史用量。",
						ProviderAccountIdentity = normalizedIdentity,
						RecoveryAction = UsageRecoveryAction.Retry,
						ProviderAccountDisplayIdentity =
							claudeSubscriptionContext!.AccountIdentity,
						SubscriptionScopeDisplayName =
							claudeSubscriptionContext.SubscriptionScopeDisplayName,
						PlanTier = claudeSubscriptionContext.PlanTier,
						SubscriptionVerificationState =
							SubscriptionVerificationState.Verified
					};
			account.PrepareAuthenticatedProviderConnectionRefresh(
				retainedClaudeSnapshot);

			if (committedWarning is not null)
			{
				AccountSettingsHealthMessage = committedWarning;
			}
			else if (string.Equals(
					AccountSettingsHealthMessage,
					AuthenticatedProviderBindingPersistenceFailureMessage,
					StringComparison.Ordinal))
			{
				AccountSettingsHealthMessage = string.Empty;
			}

			_usageRefreshCoordinator.Invalidate(account.Id);
			if (retainedClaudeSnapshot is not null)
			{
				try
				{
					await _usageSnapshotStore!.SaveAsync(
						retainedClaudeSnapshot,
						CancellationToken.None);
					UsageSnapshot? persistedRetainedSnapshot =
						await _usageSnapshotStore.LoadAsync(
							retainedClaudeSnapshot.Account,
							CancellationToken.None);
					if (!IsSameRetainedClaudeSnapshot(
							retainedClaudeSnapshot,
							persistedRetainedSnapshot))
					{
						throw new IOException(
							"Claude 舊版歷史用量未通過持久化 read-back 驗證。");
					}

					ClearAccountSettingsHealthWarning(
						ClaudeLegacyCacheRetentionFailureMessage);
					ResolveProviderAccountCacheCleanupFailure(account.Id);
				}
				catch
				{
					AppendAccountSettingsHealthWarning(
						ClaudeLegacyCacheRetentionFailureMessage);
				}
			}
			else if (!await DeleteCachedSnapshotSafelyAsync(
					account.Id,
					account.Provider,
					CancellationToken.None,
					retryTransientFailures: true))
			{
				RecordProviderAccountCacheCleanupFailure(account.Id);
			}
			else
			{
				ResolveProviderAccountCacheCleanupFailure(account.Id);
			}

			return AuthenticatedProviderBindingCommitResult.Succeeded;
		}
		finally
		{
			if (!isAccountMutationGateHeld)
			{
				_accountMutationGate.Release();
			}
		}
	}

	private static bool IsSameRetainedClaudeSnapshot(
		UsageSnapshot expected,
		UsageSnapshot? actual)
	{
		return (actual is not null) &&
			(actual.Account.Id == expected.Account.Id) &&
			(actual.Account.Provider == expected.Account.Provider) &&
			(actual.SourceTrust == expected.SourceTrust) &&
			(actual.Status == expected.Status) &&
			(actual.FetchedAt == expected.FetchedAt) &&
			(actual.ObservedAt == expected.ObservedAt) &&
			(actual.StaleAfter == expected.StaleAfter) &&
			string.Equals(actual.Error, expected.Error, StringComparison.Ordinal) &&
			string.Equals(
				actual.ProviderAccountIdentity,
				expected.ProviderAccountIdentity,
				StringComparison.Ordinal) &&
			(actual.RecoveryAction == expected.RecoveryAction) &&
			string.Equals(
				actual.ProviderAccountDisplayIdentity,
				expected.ProviderAccountDisplayIdentity,
				StringComparison.Ordinal) &&
			string.Equals(
				actual.SubscriptionScopeDisplayName,
				expected.SubscriptionScopeDisplayName,
				StringComparison.Ordinal) &&
			string.Equals(actual.PlanTier, expected.PlanTier, StringComparison.Ordinal) &&
			(actual.SubscriptionVerificationState ==
				expected.SubscriptionVerificationState) &&
			actual.Metrics.SequenceEqual(expected.Metrics);
	}

	internal async Task<bool> FailClosedAuthenticatedProviderBindingConflictAsync(
		AccountUsageViewModel account,
		CancellationToken cancellationToken = default,
		bool isAccountMutationGateHeld = false,
		bool isIdentityConflict = true)
	{
		ArgumentNullException.ThrowIfNull(account);
		if (!isAccountMutationGateHeld)
		{
			await _accountMutationGate.WaitAsync(cancellationToken);
		}

		try
		{
			if (!CanManageAccounts ||
				!account.CanManage ||
				!account.IsEnabled ||
				IsAccountCleanupBlocked(account.Id, account.Provider) ||
				(!account.IsClaude && !account.IsCodex && !account.IsCopilot &&
					!account.IsGrok) ||
				!account.IsProviderAccountChangeInProgress ||
				!account.IsProviderAccountChangeCommitInProgress ||
				!Accounts.Contains(account))
			{
				return false;
			}

			int profileIndex = _accountProfiles.FindIndex(profile =>
				(profile.Id == account.Id) &&
				(profile.Provider == account.Provider));
			if (profileIndex < 0)
			{
				return false;
			}

			return await FailClosedAuthenticatedProviderBindingConflictCoreAsync(
				account,
				profileIndex,
				cancellationToken,
				isIdentityConflict);
		}
		finally
		{
			if (!isAccountMutationGateHeld)
			{
				_accountMutationGate.Release();
			}
		}
	}

	private async Task<bool>
		FailClosedAuthenticatedProviderBindingConflictCoreAsync(
			AccountUsageViewModel account,
			int profileIndex,
			CancellationToken cancellationToken,
			bool isIdentityConflict = true)
	{
		AccountProfile currentProfile = _accountProfiles[profileIndex];
		AccountCleanupPendingWork? cacheCleanupIntent =
			await TryPersistProviderAccountCacheCleanupIntentAsync(
				account.Id,
				account.Provider,
				cancellationToken);
		bool wasCacheCleanupIntentPersisted = cacheCleanupIntent is not null;
		_usageRefreshCoordinator.Invalidate(account.Id);
		bool wasCachedSnapshotDeleted =
			await DeleteCachedSnapshotSafelyAsync(
				account.Id,
				account.Provider,
				CancellationToken.None,
				retryTransientFailures: true);
		bool wasCacheCleanupIntentCompleted =
			!wasCacheCleanupIntentPersisted;
		if (wasCacheCleanupIntentPersisted && wasCachedSnapshotDeleted)
		{
			bool wasCacheComponentCompleted =
				await MarkAccountCleanupComponentCompletedSafelyAsync(
					account.Id,
					account.Provider,
					isCacheComponent: true,
					CancellationToken.None);
			wasCacheCleanupIntentCompleted =
				wasCacheComponentCompleted &&
				(cacheCleanupIntent?.RuntimePending == false);
			if (wasCacheCleanupIntentCompleted)
			{
				UnblockAccountCleanup(account.Id, account.Provider);
				if (!HasBlockedAccountCleanups())
				{
					ClearAccountSettingsHealthWarning(
						AccountCleanupPendingFailureMessage);
				}
			}
		}

		bool hasDurableCacheProtection =
			wasCachedSnapshotDeleted || wasCacheCleanupIntentPersisted;
		string? committedWarning = null;
		bool wasConflictDisconnectPersisted = true;
		bool shouldPersistProfile =
			!string.IsNullOrWhiteSpace(
				currentProfile.ProviderAccountIdentity) ||
			(!hasDurableCacheProtection && currentProfile.IsEnabled);

		if (shouldPersistProfile)
		{
			List<AccountProfile> candidateProfiles = _accountProfiles.ToList();
			candidateProfiles[profileIndex] = currentProfile with
			{
				IsEnabled = hasDurableCacheProtection &&
					currentProfile.IsEnabled,
				ProviderAccountIdentity = null
			};

			try
			{
				await SaveAccountProfilesWithTransientRetryAsync(
					candidateProfiles,
					cancellationToken);
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				committedWarning = exception.Message;
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				wasConflictDisconnectPersisted = false;
				AccountSettingsHealthMessage =
					isIdentityConflict
						? AuthenticatedProviderConflictRuntimeFailClosedMessage
						: AuthenticatedProviderBindingPersistenceFailureMessage;
				candidateProfiles[profileIndex] = currentProfile with
				{
					IsEnabled = false,
					ProviderAccountIdentity = null
				};
			}

			ApplyProfiles(candidateProfiles);
		}

		account.CompleteProviderAccountConflictDisconnect();

		if (committedWarning is not null)
		{
			AccountSettingsHealthMessage = committedWarning;
		}
		else if (wasConflictDisconnectPersisted && string.Equals(
				AccountSettingsHealthMessage,
				AuthenticatedProviderBindingPersistenceFailureMessage,
				StringComparison.Ordinal))
		{
			AccountSettingsHealthMessage = string.Empty;
		}

		if (!wasCachedSnapshotDeleted)
		{
			RecordProviderAccountCacheCleanupFailure(account.Id);
		}
		else
		{
			ResolveProviderAccountCacheCleanupFailure(account.Id);
		}
		if (!wasCacheCleanupIntentCompleted)
		{
			AppendAccountSettingsHealthWarning(
				AccountCleanupPendingFailureMessage);
		}

		return wasConflictDisconnectPersisted;
	}

	private async Task<AccountCleanupPendingWork?>
		TryPersistProviderAccountCacheCleanupIntentAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken)
	{
		if ((_usageSnapshotStore is null) ||
			(_accountCleanupPendingStore is null))
		{
			return null;
		}

		try
		{
			AccountCleanupPendingWork pendingWork =
				await _accountCleanupPendingStore.UpsertCacheCleanupAsync(
				accountId,
				provider,
				cancellationToken);
			BlockAccountCleanups(new[] { (accountId, provider) });
			return pendingWork;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	private bool HasPendingProviderAccountIdentityBindings()
	{
		foreach (AccountUsageViewModel account in Accounts)
		{
			UsageSnapshot? snapshot = account.CurrentSnapshot;

			if ((snapshot is null) ||
				IsRuntimeOnlyCachedProviderAccountIdentity(account.Id) ||
				!account.CanPersistCurrentSnapshot ||
				account.DidRejectProviderAccountSnapshot ||
				!TryGetUsableProviderAccountIdentity(
					snapshot,
					out string? identity) ||
				(identity is null))
			{
				continue;
			}

			if (!ProviderAccountIdentityRules.Comparer.Equals(
					account.Profile.ProviderAccountIdentity,
					identity))
			{
				return true;
			}
		}

		return false;
	}

	private bool IsRuntimeOnlyCachedProviderAccountIdentity(Guid accountId)
	{
		lock (_refreshSync)
		{
			return _runtimeOnlyCachedProviderAccountIdentityAccountIds.Contains(
				accountId);
		}
	}

	private string? GetRuntimeOnlyCachedProviderAccountIdentity(
		AccountUsageViewModel account)
	{
		if (!IsRuntimeOnlyCachedProviderAccountIdentity(account.Id) ||
			(account.CurrentSnapshot is not UsageSnapshot snapshot) ||
			!TryGetUsableProviderAccountIdentity(
				snapshot,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				account.ProviderAccountIdentity,
				snapshotIdentity))
		{
			return null;
		}

		return snapshotIdentity;
	}

	private void RestoreRuntimeOnlyCachedProviderAccountIdentity(
		AccountUsageViewModel account,
		string? cachedProviderAccountIdentity)
	{
		if ((cachedProviderAccountIdentity is null) ||
			!IsRuntimeOnlyCachedProviderAccountIdentity(account.Id) ||
			!account.IsEnabled ||
			!Accounts.Contains(account) ||
			!string.IsNullOrWhiteSpace(
				account.Profile.ProviderAccountIdentity) ||
			(account.CurrentSnapshot is not UsageSnapshot snapshot) ||
			!TryGetUsableProviderAccountIdentity(
				snapshot,
				out string? snapshotIdentity) ||
			(snapshotIdentity is null) ||
			!ProviderAccountIdentityRules.Comparer.Equals(
				cachedProviderAccountIdentity,
				snapshotIdentity))
		{
			return;
		}

		account.SeedProviderAccountIdentity(cachedProviderAccountIdentity);
	}

	private void ReleaseRuntimeOnlyCachedProviderAccountIdentityIfVerified(
		AccountUsageViewModel account,
		UsageSnapshot snapshot)
	{
		if ((snapshot.Status != SnapshotStatus.Ready) ||
			!string.IsNullOrWhiteSpace(snapshot.Error) ||
			!TryGetUsableProviderAccountIdentity(snapshot, out _) ||
			!account.CanAcceptLiveProviderAccountSnapshot(snapshot))
		{
			return;
		}

		lock (_refreshSync)
		{
			_runtimeOnlyCachedProviderAccountIdentityAccountIds.Remove(account.Id);
		}
	}

	private void RejectUnpersistedProviderAccountIdentityBindings(
		IReadOnlyCollection<(
			AccountUsageViewModel Account,
			UsageSnapshot Snapshot,
			string Identity)> bindings)
	{
		foreach ((AccountUsageViewModel account, UsageSnapshot snapshot, _) in bindings)
		{
			if (Accounts.Contains(account))
			{
				account.RejectProviderAccountSnapshot(
					snapshot,
					ProviderAccountIdentityPersistenceFailureMessage,
					UsageRecoveryAction.Retry);
			}
		}

		AccountSettingsHealthMessage =
			ProviderAccountIdentityPersistenceFailureMessage;
	}

	private static bool TryClaimProviderAccountIdentity(
		AccountUsageViewModel account,
		UsageSnapshot snapshot,
		Dictionary<ProviderKind, Dictionary<string, Guid>> owners)
	{
		if (!TryGetUsableProviderAccountIdentity(
				snapshot,
				out string? providerAccountIdentity))
		{
			return true;
		}

		if (!owners.TryGetValue(
				account.Provider,
				out Dictionary<string, Guid>? providerOwners))
		{
			providerOwners = new Dictionary<string, Guid>(
				ProviderAccountIdentityRules.Comparer);
			owners.Add(account.Provider, providerOwners);
		}

		if (providerOwners.TryGetValue(
				providerAccountIdentity!,
				out Guid ownerAccountId))
		{
			return ownerAccountId == account.Id;
		}

		providerOwners.Add(providerAccountIdentity!, account.Id);
		return true;
	}

	private static bool TryGetUsableProviderAccountIdentity(
		UsageSnapshot snapshot,
		out string? providerAccountIdentity)
	{
		providerAccountIdentity = null;
		return ((snapshot.Status == SnapshotStatus.Ready) ||
				(snapshot.Status == SnapshotStatus.Stale)) &&
			Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) &&
			(snapshot.SourceTrust != SourceTrust.Unavailable) &&
			(snapshot.Metrics.Count > 0) &&
			ProviderAccountIdentityRules.TryNormalize(
				snapshot.ProviderAccountIdentity,
				out providerAccountIdentity) &&
			(providerAccountIdentity is not null);
	}

	private static void TryAddProviderAccountIdentityOwner(
		Dictionary<ProviderKind, Dictionary<string, Guid>> owners,
		ProviderKind provider,
		Guid accountId,
		string? providerAccountIdentity)
	{
		if (!ProviderAccountIdentityRules.TryNormalize(
				providerAccountIdentity,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null))
		{
			return;
		}

		if (!owners.TryGetValue(
				provider,
				out Dictionary<string, Guid>? providerOwners))
		{
			providerOwners = new Dictionary<string, Guid>(
				ProviderAccountIdentityRules.Comparer);
			owners.Add(provider, providerOwners);
		}

		providerOwners.TryAdd(normalizedIdentity, accountId);
	}

	private static AccountProfile NormalizeProfile(AccountProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);

		if (profile.Id == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(profile));
		}

		if (!Enum.IsDefined(typeof(ProviderKind), profile.Provider))
		{
			throw new ArgumentException("帳號包含不支援的服務。", nameof(profile));
		}

		string displayName = profile.DisplayName?.Trim() ?? string.Empty;

		if (profile.DisplayName is null)
		{
			throw new ArgumentException("帳號暱稱不可為 null。", nameof(profile));
		}

		if (displayName.Length > MaximumDisplayNameLength)
		{
			throw new ArgumentException(
				$"帳號暱稱不可超過 {MaximumDisplayNameLength} 個字元。",
				nameof(profile));
		}

		if (displayName.Any(char.IsControl))
		{
			throw new ArgumentException(
				"帳號暱稱不可包含控制字元。",
				nameof(profile));
		}

		return profile with { DisplayName = displayName };
	}

	private static IReadOnlyList<AccountProfile> NormalizeImportedProfiles(
		IReadOnlyList<AccountProfile> profiles)
	{
		ArgumentNullException.ThrowIfNull(profiles);

		if (profiles.Count > MaximumAccountCount)
		{
			throw new ArgumentException(
				$"匯入檔的帳號數量不可超過 {MaximumAccountCount} 個。",
				nameof(profiles));
		}

		HashSet<Guid> accountIds = new();
		Dictionary<ProviderKind, HashSet<string>> identitiesByProvider = new();
		List<AccountProfile> normalizedProfiles = new(profiles.Count);

		foreach (AccountProfile? profile in profiles)
		{
			if (profile is null)
			{
				throw new ArgumentException(
					"匯入檔的帳號項目不可為 null。",
					nameof(profiles));
			}

			AccountProfile normalizedProfile = NormalizeProfile(profile);

			if (!accountIds.Add(normalizedProfile.Id))
			{
				throw new ArgumentException(
					"匯入檔的帳號識別碼不可重複。",
					nameof(profiles));
			}

			if (!ProviderAccountIdentityRules.TryNormalize(
					normalizedProfile.ProviderAccountIdentity,
					out string? providerAccountIdentity))
			{
				throw new ArgumentException(
					"匯入檔包含格式無效的帳號資訊。",
					nameof(profiles));
			}

			string? portableProviderAccountIdentity =
				(normalizedProfile.Provider == ProviderKind.Claude) ||
				(normalizedProfile.Provider == ProviderKind.Codex) ||
				(normalizedProfile.Provider == ProviderKind.Copilot) ||
				(normalizedProfile.Provider == ProviderKind.Antigravity) ||
				(normalizedProfile.Provider == ProviderKind.Grok)
					? null
					: providerAccountIdentity;

			if (portableProviderAccountIdentity is not null)
			{
				if (!identitiesByProvider.TryGetValue(
						normalizedProfile.Provider,
						out HashSet<string>? identities))
				{
					identities = new HashSet<string>(
						ProviderAccountIdentityRules.Comparer);
					identitiesByProvider.Add(
						normalizedProfile.Provider,
						identities);
				}

				if (!identities.Add(portableProviderAccountIdentity))
				{
					throw new ArgumentException(
						"匯入檔把同一個登入帳號連接到多張帳號卡片。",
						nameof(profiles));
				}
			}

			normalizedProfiles.Add(normalizedProfile with
			{
				ProviderAccountIdentity = portableProviderAccountIdentity,
				HasAcceptedClaudeQuotaRisk = false
			});
		}

		return normalizedProfiles;
	}

	private void EnsureImportedProfilesDoNotRebindExistingCards(
		IReadOnlyList<AccountProfile> importedProfiles)
	{
		Dictionary<Guid, AccountProfile> existingProfiles = _accountProfiles
			.ToDictionary(profile => profile.Id);

		foreach (AccountProfile importedProfile in importedProfiles)
		{
			if (!existingProfiles.TryGetValue(
					importedProfile.Id,
					out AccountProfile? existingProfile) ||
				(existingProfile.Provider != importedProfile.Provider) ||
				(((importedProfile.Provider == ProviderKind.Claude) ||
					(importedProfile.Provider == ProviderKind.Codex) ||
					(importedProfile.Provider == ProviderKind.Copilot) ||
					(importedProfile.Provider == ProviderKind.Antigravity) ||
					(importedProfile.Provider == ProviderKind.Grok)) &&
					string.IsNullOrWhiteSpace(
						importedProfile.ProviderAccountIdentity)) ||
				ProviderAccountIdentityRules.Comparer.Equals(
					existingProfile.ProviderAccountIdentity,
					importedProfile.ProviderAccountIdentity))
			{
				continue;
			}

			throw new InvalidOperationException(
				"匯入檔中的登入帳號與這台電腦的其他帳號衝突；原設定未變更。");
		}
	}

	private void ApplyProfiles(IEnumerable<AccountProfile> profiles)
	{
		HashSet<AccountUsageViewModel> previouslyTrackedAccounts =
			Accounts.ToHashSet();
		Dictionary<Guid, AccountUsageViewModel> existingAccounts = Accounts
			.ToDictionary(account => account.Id);
		_accountProfiles.Clear();
		_accountProfiles.AddRange(profiles);
		ReconcileProviderAccountCacheCleanupFailures(
			_accountProfiles.Select(profile => profile.Id).ToHashSet());
		List<AccountUsageViewModel> desiredAccounts = new(_accountProfiles.Count);

		foreach (AccountProfile profile in _accountProfiles)
		{
			if (existingAccounts.TryGetValue(profile.Id, out AccountUsageViewModel? existingAccount) &&
				(existingAccount.Provider == profile.Provider))
			{
				existingAccount.ApplyProfile(profile, CanManageAccounts);
				desiredAccounts.Add(existingAccount);
			}
			else
			{
				desiredAccounts.Add(new AccountUsageViewModel(
					profile,
					CanManageAccounts,
					DisplayMode));
			}
		}

		for (int index = 0; index < desiredAccounts.Count; index++)
		{
			AccountUsageViewModel desiredAccount = desiredAccounts[index];

			if ((index < Accounts.Count) && ReferenceEquals(Accounts[index], desiredAccount))
			{
				continue;
			}

			int existingIndex = Accounts.IndexOf(desiredAccount);

			if (existingIndex >= 0)
			{
				Accounts.Move(existingIndex, index);
			}
			else
			{
				Accounts.Insert(index, desiredAccount);
			}
		}

		while (Accounts.Count > desiredAccounts.Count)
		{
			Accounts.RemoveAt(Accounts.Count - 1);
		}

		foreach (AccountUsageViewModel removedAccount in
			previouslyTrackedAccounts.Except(desiredAccounts))
		{
			removedAccount.PropertyChanged -= Account_PropertyChanged;
		}

		foreach (AccountUsageViewModel addedAccount in
			desiredAccounts.Except(previouslyTrackedAccounts))
		{
			addedAccount.PropertyChanged += Account_PropertyChanged;
		}

		HashSet<Guid> enabledAccountIds = _accountProfiles
			.Where(profile => profile.IsEnabled)
			.Select(profile => profile.Id)
			.ToHashSet();

		lock (_refreshSync)
		{
			_runtimeOnlyCachedProviderAccountIdentityAccountIds.RemoveWhere(
				accountId => !enabledAccountIds.Contains(accountId));
		}

		foreach (Guid existingAccountId in existingAccounts.Keys)
		{
			if (!enabledAccountIds.Contains(existingAccountId))
			{
				_usageRefreshCoordinator.Invalidate(existingAccountId);
			}
		}

		ApplyAntigravityAccountConstraints();
		foreach (Guid accountId in
			_pendingAntigravityConnectionWork.Keys.ToArray())
		{
			MarkPendingAntigravityConnection(accountId);
		}

		if (_isAntigravityConnectionPendingStoreHydrationBlocked)
		{
			BlockAntigravityConnectionJournalHydration();
		}

		OnPropertyChanged(nameof(HasAccounts));
		OnPropertyChanged(nameof(HasNoAccounts));
		OnPropertyChanged(nameof(CanRefresh));
		UpdateRefreshAvailabilityText();
		ApplyAccountDisplayOrder();
		AdvancePortableSettingsRevision();
	}

	private void Account_PropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (string.IsNullOrEmpty(e.PropertyName) ||
			string.Equals(
				e.PropertyName,
				nameof(AccountUsageViewModel.CanRefreshUsage),
				StringComparison.Ordinal))
		{
			OnPropertyChanged(nameof(CanRefresh));
		}
	}

	private void UpdateRefreshAvailabilityText()
	{
		if (!Accounts.Any(account => account.IsEnabled))
		{
			SetRefreshStatus(
				NoRefreshTargetsMessage,
				CompactRefreshStatus.Hidden);
		}
		else if (LastRefreshText == NoRefreshTargetsMessage)
		{
			SetRefreshStatus(
				"尚未檢查",
				CompactRefreshStatus.Hidden);
		}
	}

	private void ApplyAntigravityAccountConstraints()
	{
		foreach (AccountUsageViewModel account in Accounts)
		{
			account.SetAntigravityAccountSetupAvailability(
				IsPrimaryEnabledAntigravityAccount(account.Id));

			if (!IsInactiveAntigravityAccount(account))
			{
				continue;
			}

			account.ClearAntigravityReportedAccountEmail();
			_usageRefreshCoordinator.Invalidate(account.Id);
			account.ApplySnapshot(CreateInactiveAntigravitySnapshot(account));
		}
	}

	private static int GetProviderSortOrder(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => 0,
			ProviderKind.Codex => 1,
			ProviderKind.Antigravity => 2,
			ProviderKind.Grok => 3,
			ProviderKind.Copilot => 4,
			_ => int.MaxValue
		};
	}

	private static int GetDefaultAccountInsertionIndex(
		IReadOnlyList<AccountProfile> profiles,
		ProviderKind provider)
	{
		for (int index = profiles.Count - 1; index >= 0; index--)
		{
			if (profiles[index].Provider == provider)
			{
				return index + 1;
			}
		}

		int providerSortOrder = GetProviderSortOrder(provider);

		for (int index = 0; index < profiles.Count; index++)
		{
			if (GetProviderSortOrder(profiles[index].Provider) > providerSortOrder)
			{
				return index;
			}
		}

		return profiles.Count;
	}

	private void ApplyAccountDisplayOrder()
	{
		DateTimeOffset now = TimeProvider.System.GetUtcNow();
		Dictionary<Guid, int> manualIndexes = _accountProfiles
			.Select((profile, index) => (profile.Id, Index: index))
			.ToDictionary(item => item.Id, item => item.Index);
		IEnumerable<AccountUsageViewModel> desiredAccounts;

		if (IsAutomaticUsageSorting)
		{
			desiredAccounts = Accounts
				.Select(account => new
				{
					Account = account,
					LeadingMetric = account.CurrentSnapshot is UsageSnapshot snapshot
						? UsageMetricPresentation.OrderMetrics(snapshot.Metrics, now)
							.FirstOrDefault()
						: null,
					ManualIndex = manualIndexes.GetValueOrDefault(account.Id, int.MaxValue)
				})
				.OrderBy(item => GetProviderSortOrder(item.Account.Provider))
				.ThenBy(item => item.LeadingMetric is null
					? int.MaxValue
					: UsageMetricPresentation.GetWindowSortOrder(item.LeadingMetric))
				.ThenBy(item => item.LeadingMetric is null
					? DateTimeOffset.MaxValue
					: UsageMetricPresentation.GetResetSortValue(item.LeadingMetric, now))
				.ThenBy(item => item.ManualIndex)
				.Select(item => item.Account)
				.ToArray();
		}
		else
		{
			desiredAccounts = _accountProfiles
				.Select(profile => Accounts.First(account => account.Id == profile.Id))
				.ToArray();
		}

		int desiredIndex = 0;

		foreach (AccountUsageViewModel desiredAccount in desiredAccounts)
		{
			int currentIndex = Accounts.IndexOf(desiredAccount);

			if ((currentIndex >= 0) && (currentIndex != desiredIndex))
			{
				Accounts.Move(currentIndex, desiredIndex);
			}

			desiredIndex++;
		}

		UpdateAccountMoveAvailability();
	}

	private void SetSortMode(UsageSortMode value)
	{
		if (!Enum.IsDefined(value))
		{
			throw new ArgumentOutOfRangeException(
				nameof(value),
				value,
				"未知的用量排序方式。");
		}

		if (_sortMode == value)
		{
			return;
		}

		_sortMode = value;
		AdvancePortableSettingsRevision();
		OnPropertyChanged(nameof(SortMode));
		OnPropertyChanged(nameof(IsAutomaticUsageSorting));
		OnPropertyChanged(nameof(SortModeActionText));
		OnPropertyChanged(nameof(SortModeText));
		OnPropertyChanged(nameof(SortModeToolTip));
		UpdateAccountMoveAvailability();
	}

	private void SetUsageDisplayMode(UsageDisplayMode value)
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
		AdvancePortableSettingsRevision();
		OnPropertyChanged(nameof(DisplayMode));
		OnPropertyChanged(nameof(IsShowingRemainingUsage));
		OnPropertyChanged(nameof(UsageDisplayModeActionText));
		OnPropertyChanged(nameof(UsageDisplayModeText));
		OnPropertyChanged(nameof(UsageDisplayModeToolTip));

		foreach (AccountUsageViewModel account in Accounts)
		{
			account.SetUsageDisplayMode(value);
		}
	}

	private void UpdateAccountMoveAvailability()
	{
		bool canMoveAccounts = CanManageAccounts &&
			!IsManagingAccounts &&
			!_isUpdateShutdownReserved &&
			!IsAutomaticUsageSorting;

		for (int index = 0; index < Accounts.Count; index++)
		{
			Accounts[index].SetMoveAvailability(
				canMoveAccounts && (index > 0),
				canMoveAccounts && (index < (Accounts.Count - 1)));
		}
	}

	private bool IsRefreshStopped()
	{
		lock (_refreshSync)
		{
			return _isRefreshStopped || _isUpdateShutdownReserved;
		}
	}

	private void NotifyUpdateShutdownAvailabilityChanged()
	{
		OnPropertyChanged(nameof(CanStartAccountManagement));
		OnPropertyChanged(nameof(CanUndoLastPortableSettingsImport));
		OnPropertyChanged(nameof(CanChangeSortMode));
		OnPropertyChanged(nameof(CanChangeUsageDisplayMode));
		OnPropertyChanged(nameof(CanRefresh));
		UpdateAccountMoveAvailability();
	}

	private void EnsureAccountManagementAvailable()
	{
		EnsureUpdateShutdownNotReserved();

		if (!CanManageAccounts)
		{
			throw new AccountProfileStoreException(
				"目前無法寫入帳號設定。請先處理畫面上的設定檔警告。");
		}
	}

	private void EnsureUpdateShutdownNotReserved()
	{
		if (_isUpdateShutdownReserved)
		{
			throw new InvalidOperationException(
				"AI Usage 正在安全關閉以進行更新，不能再修改設定。");
		}
	}

	private void EnsureAccountMutationAllowed(Guid accountId)
	{
		AccountUsageViewModel? account = Accounts.FirstOrDefault(
			candidate => candidate.Id == accountId);

		if (account?.IsProviderAccountChangeInProgress == true)
		{
			throw new InvalidOperationException(
				"帳號正在連接中。請等待連接完成後再修改或移除帳號。");
		}
	}

	private static DashboardShellPreferences ApplyPortableWidgetPreferences(
		DashboardShellPreferences currentShellPreferences,
		PortableWidgetPreferences widgetPreferences)
	{
		return currentShellPreferences with
		{
			IsWidgetVisible = widgetPreferences.IsWidgetVisible,
			IsCollapsed = widgetPreferences.IsCollapsed,
			IsTopmost = widgetPreferences.IsTopmost,
			Corner = widgetPreferences.Corner,
			StartupSurface = widgetPreferences.IsWidgetVisible
				? DashboardStartupSurface.Widget
				: DashboardStartupSurface.Tray,
			Theme = widgetPreferences.Theme ?? currentShellPreferences.Theme,
			IsHeightFollowingCardCount =
				widgetPreferences.IsHeightFollowingCardCount ??
				currentShellPreferences.IsHeightFollowingCardCount
		};
	}

	private static bool ArePortableWidgetPreferencesEquivalentForUndo(
		PortableWidgetPreferences first,
		PortableWidgetPreferences second)
	{
		return (first.IsCollapsed == second.IsCollapsed) &&
			(first.IsTopmost == second.IsTopmost) &&
			(first.Corner == second.Corner) &&
			((first.Theme is null) || (first.Theme == second.Theme)) &&
			((first.IsHeightFollowingCardCount is null) ||
				(first.IsHeightFollowingCardCount ==
					second.IsHeightFollowingCardCount));
	}

	private static PortableWidgetPreferences CreatePortableWidgetPreferences(
		DashboardShellPreferences shellPreferences)
	{
		return new PortableWidgetPreferences(
			shellPreferences.IsWidgetVisible,
			shellPreferences.IsCollapsed,
			shellPreferences.IsTopmost,
			shellPreferences.Corner,
			shellPreferences.Theme,
			shellPreferences.IsHeightFollowingCardCount);
	}

	private static DashboardShellPreferences? CreateEffectiveShellPreferences(
		DashboardShellPreferences? currentShellPreferences,
		PortableWidgetPreferences? widgetPreferences)
	{
		if (widgetPreferences is null)
		{
			return null;
		}

		if (currentShellPreferences is null)
		{
			throw new InvalidOperationException(
				"目前無法確認浮窗設定，因此不能還原這次匯入。");
		}

		return ApplyPortableWidgetPreferences(
			currentShellPreferences,
			widgetPreferences);
	}

	private bool IsCurrentPortableSettingsState(
		PortableSettingsSnapshot settings,
		PortableWidgetPreferences? currentWidgetPreferences)
	{
		return (SortMode == settings.UsageSortMode) &&
			(DisplayMode == settings.UsageDisplayMode) &&
			_accountProfiles.SequenceEqual(settings.Accounts) &&
			((settings.WidgetPreferences is null) ||
				((currentWidgetPreferences is not null) &&
					ArePortableWidgetPreferencesEquivalentForUndo(
						settings.WidgetPreferences,
						currentWidgetPreferences)));
	}

	private async Task SavePortablePreferencesAsync(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		DashboardShellPreferences? shellPreferences,
		CancellationToken cancellationToken)
	{
		if (_dashboardPreferencesStore is null)
		{
			if (shellPreferences is not null)
			{
				throw new InvalidOperationException(
					"目前無法儲存浮窗設定。");
			}

			return;
		}

		if (shellPreferences is null)
		{
			await _dashboardPreferencesStore.SaveUsagePreferencesAsync(
				sortMode,
				displayMode,
				cancellationToken);
			return;
		}

		if (_dashboardPreferencesStore is not IWidgetPreferencesStore widgetStore)
		{
			throw new InvalidOperationException(
				"目前無法儲存浮窗設定。");
		}

		await widgetStore.SavePortablePreferencesAsync(
			sortMode,
			displayMode,
			shellPreferences,
			cancellationToken);
	}

	private static void ValidatePortableWidgetPreferences(
		PortableWidgetPreferences widgetPreferences)
	{
		if (!Enum.IsDefined(widgetPreferences.Corner))
		{
			throw new ArgumentOutOfRangeException(
				nameof(widgetPreferences),
				widgetPreferences.Corner,
				"匯入檔包含未知的浮窗停靠位置。");
		}

		if ((widgetPreferences.Theme is AppTheme theme) &&
			!Enum.IsDefined(theme))
		{
			throw new ArgumentOutOfRangeException(
				nameof(widgetPreferences),
				theme,
				"匯入檔包含未知的主題設定。");
		}
	}

	private void SetLastPortableSettingsImportUndoState(
		PortableSettingsImportUndoState? undoState)
	{
		if (ReferenceEquals(_lastPortableSettingsImportUndoState, undoState))
		{
			return;
		}

		_lastPortableSettingsImportUndoState = undoState;
		OnPropertyChanged(nameof(CanUndoLastPortableSettingsImport));
	}

	private void AdvancePortableSettingsRevision()
	{
		unchecked
		{
			_portableSettingsRevision++;
		}

		OnPropertyChanged(nameof(CanUndoLastPortableSettingsImport));
	}

	private static string GetAccountLabel(AccountProfile profile)
	{
		if (!string.IsNullOrWhiteSpace(profile.DisplayName))
		{
			return profile.DisplayName;
		}

		return profile.Provider switch
		{
			ProviderKind.Claude => "Claude",
			ProviderKind.Codex => "Codex",
			ProviderKind.Copilot => "GitHub Copilot",
			ProviderKind.Antigravity => "Antigravity",
			ProviderKind.Grok => "Grok",
			_ => profile.Provider.ToString()
		};
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	private async Task<AccountProfileStoreException?> PersistAndPublishAsync(
		IReadOnlyList<AccountProfile> profiles,
		CancellationToken cancellationToken)
	{
		HashSet<Guid> reEnabledAccountIds = profiles
			.Where(profile => profile.IsEnabled &&
				_accountProfiles.Any(existingProfile =>
					(existingProfile.Id == profile.Id) &&
					(existingProfile.Provider == profile.Provider) &&
					!existingProfile.IsEnabled))
			.Select(profile => profile.Id)
			.ToHashSet();
		AccountProfileStoreException? committedWarning = null;

		try
		{
			await _accountProfileStore.SaveAsync(profiles, cancellationToken);
		}
		catch (AccountProfileStoreException exception) when (exception.HasCommittedChanges)
		{
			committedWarning = exception;
		}

		ApplyProfiles(profiles);
		await RestoreCachedSnapshotsAsync(
			Accounts.Where(account => reEnabledAccountIds.Contains(account.Id)).ToArray(),
			committedWarning is null ? cancellationToken : CancellationToken.None);

		if ((committedWarning is null) &&
			_shouldClearAccountSettingsHealthAfterSuccessfulSave &&
			!HasBlockedAccountCleanups())
		{
			AccountSettingsHealthMessage = string.Empty;
			_shouldClearAccountSettingsHealthAfterSuccessfulSave = false;
		}

		return committedWarning;
	}

	private void ThrowCommittedSaveWarning(
		AccountProfileStoreException? committedWarning,
		string? statusMessage = null)
	{
		if (committedWarning is null)
		{
			return;
		}

		AccountSettingsMessage = statusMessage ?? committedWarning.Message;
		throw committedWarning;
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
