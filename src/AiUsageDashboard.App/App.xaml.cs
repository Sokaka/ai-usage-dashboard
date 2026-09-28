using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.Updates;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Licensing;
using AiUsageDashboard.LegalUi;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Core.Providers;
using AiUsageDashboard.Core.Refreshing;
using AiUsageDashboard.Updater.Core;

using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

internal readonly record struct AntigravityAccountEnsureFailureNotification(
	string Title,
	string Message);

internal readonly record struct LogonStartupMenuPresentation(
	string Text,
	bool IsChecked,
	bool IsEnabled);

internal enum AppLaunchIntent
{
	Interactive,
	LogonStartup,
	EnsureAntigravityAccount,
	ShutdownForUpdate
}

public partial class App : System.Windows.Application
{
	private const long MaximumAccountSettingsTransactionTargetSizeBytes =
		1024 * 1024;
	private const long MaximumDashboardPreferencesTransactionTargetSizeBytes =
		64 * 1024;
	private const string ApplicationUserModelId = "AiUsageDashboard.Desktop";
	private const string RestartAfterProcessArgument = "--restart-after-pid";
	private const string ShutdownForUpdateArgument = "--shutdown-for-update";
	private const string WindowsStartupAppsSettingsUri =
		"ms-settings:startupapps";
	private static readonly TimeSpan AccountConnectionShutdownTimeout =
		TimeSpan.FromSeconds(7);
	private static readonly TimeSpan AutomaticUpdateCheckTimerInterval =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ActivationRequestTimeout = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan ClaudeSafetyStateStartupRecoveryTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan DashboardPreferencesRecoveryRetryInterval =
		TimeSpan.FromSeconds(1);
	private static readonly TimeSpan GrokConnectionStartupRecoveryTimeout =
		TimeSpan.FromSeconds(45);
	private static readonly TimeSpan GrokAccountCleanupPostStartupBudget =
		TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PostStartupRecoveryShutdownTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan UsageRefreshShutdownTimeout =
		AntigravityOfficialPrintTiming.MaximumCaptureDuration +
		DashboardViewModel.MaximumAntigravityReportedAccountReadDuration +
		DashboardViewModel.MaximumAntigravityReportedAccountReadDuration +
		TimeSpan.FromSeconds(10);
	private static readonly TimeSpan UpdateMutationShutdownTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan UpdateHttpTimeout =
		TimeSpan.FromSeconds(20);
	private static readonly TimeSpan UpdateShutdownRecoveryRetryInterval =
		TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan RestartShutdownFinalizationAllowance =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan RestartWaitTimeout =
		PostStartupRecoveryShutdownTimeout +
		AccountConnectionShutdownTimeout +
		UsageRefreshShutdownTimeout +
		RestartShutdownFinalizationAllowance;
	private static readonly Uri DefaultPaletteUri = new(
		"Themes/Palette.xaml",
		UriKind.Relative);
	private static readonly Uri HighContrastPaletteUri = new(
		"Themes/HighContrastPalette.xaml",
		UriKind.Relative);
	private static readonly Uri LightPaletteUri = new(
		"Themes/LightPalette.xaml",
		UriKind.Relative);
	private static readonly Uri MidnightPaletteUri = new(
		"Themes/MidnightPalette.xaml",
		UriKind.Relative);
	private static readonly Uri SakuraPaletteUri = new(
		"Themes/SakuraPalette.xaml",
		UriKind.Relative);
	private readonly SingleInstanceActivationCoordinator
		_activationCoordinator = new();
	private readonly ICurrentUserLogonStartupRegistrationStore
		_logonStartupRegistrationStore =
			new CurrentUserLogonStartupRegistryStore();
	private readonly ShutdownOperationScheduler _shutdownOperationScheduler = new();
	private readonly SemaphoreSlim _shellPreferencesSaveGate = new(1, 1);
	private readonly MaintenanceUpdaterLauncher _maintenanceUpdaterLauncher = new();
	private readonly CancellationTokenSource _startupRecoverySource = new();
	private AccountConnectionCoordinator? _accountConnectionCoordinator;
	private AboutWindow? _aboutWindow;
	private SingleInstanceActivationChannel? _activationChannel;
	private UpdateShutdownChannel? _updateShutdownChannel;
	private Icon? _applicationIcon;
	private ThemeAppIcon? _themeAppIcon;
	private DashboardViewModel? _dashboardViewModel;
	private FloatingWidgetWindow? _floatingWidgetWindow;
	private AppInstallationContext _appInstallationContext =
		AppInstallationContext.CreateUnmanaged();
	private UpdatePresentationState _updatePresentationState =
		UpdatePresentationState.CreateInitial(
			isFeatureAvailableInBuild: false,
			currentVersion: null);
	private UpdateUiPresentation? _updateUiPresentation;
	private UpdateCheckCoordinator? _updateCheckCoordinator;
	private HttpClient? _updateHttpClient;
	private bool _hasReportedShellPreferencesSaveFailure;
	private bool _isDashboardPreferencesRecoveryRequested;
	private bool _isPostStartupRecoveryRunning;
	private bool _isRestoringShellPreferences = true;
	private bool _isUpdateAutomaticCheckRunning;
	private bool _isUpdateAutomaticNoticePresentationRunning;
	private bool _isUpdateBalloonAttemptRunning;
	private bool _isUpdatePowerModeSubscribed;
	private bool _isUpdateShutdownChannelReady;
	private bool _isUpdateShutdownReservationReady;
	private bool _isUpdateShutdownReserved;
	private int _dashboardPreferencesRecoveryRequestPendingAfterTransaction;
	private AppTheme _selectedTheme = AppTheme.ClassicBlue;
	private AppLanguage _selectedLanguage = AppLanguage.English;
	private FormsNotifyIcon? _notifyIcon;
	private Task _dashboardPreferencesRecoveryTask = Task.CompletedTask;
	private Task _postStartupRecoveryTask = Task.CompletedTask;
	private DispatcherTimer? _refreshTimer;
	private DispatcherTimer? _updateCheckTimer;
	private DateTimeOffset? _automaticUpdateNoticePresentedAtUtc;
	private Mutex? _singleInstanceMutex;
	private DashboardShellPreferences? _latestDashboardShellPreferences;
	private IDashboardPreferencesRecoveryStore?
		_dashboardPreferencesRecoveryStore;
	private IWidgetPreferencesStore? _widgetPreferencesStore;
	private FormsToolStripMenuItem? _exportPortableSettingsMenuItem;
	private FormsToolStripMenuItem? _automaticUpdateChecksMenuItem;
	private FormsToolStripMenuItem? _checkForUpdatesMenuItem;
	private FormsToolStripMenuItem? _logonStartupMenuItem;
	private FormsToolStripMenuItem? _updateAvailableMenuItem;
	private FormsToolStripMenuItem? _undoPortableSettingsImportMenuItem;
	private FormsToolStripMenuItem? _widgetTopmostMenuItem;
	private FormsToolStripMenuItem? _widgetVisibilityMenuItem;
	private FormsToolStripMenuItem? _englishLanguageMenuItem;
	private FormsToolStripMenuItem? _traditionalChineseLanguageMenuItem;

	public bool IsFloatingWidgetVisible =>
		_floatingWidgetWindow?.IsVisible == true;

	public bool IsQuitting { get; private set; }

	internal static TimeSpan RestartWaitBudget => RestartWaitTimeout;

	internal static TimeSpan UsageRefreshShutdownBudget =>
		UsageRefreshShutdownTimeout;

	internal static TimeSpan ShutdownDrainBudget =>
		PostStartupRecoveryShutdownTimeout +
		AccountConnectionShutdownTimeout +
		UsageRefreshShutdownTimeout;

	internal static string StableApplicationUserModelId => ApplicationUserModelId;

	internal static bool CanStartInteractiveShutdown(
		bool isUpdateShutdownReserved) => !isUpdateShutdownReserved;

	internal static bool CanShowInteractiveUpdateResult(
		bool isQuitting) => !isQuitting;

	internal static bool ShouldRunUpdateCheckTimer(
		bool isQuitting,
		bool hasCoordinator,
		bool isAutoCheckEnabled,
		bool isSnoozed)
	{
		return !isQuitting &&
			hasCoordinator &&
			(isAutoCheckEnabled || isSnoozed);
	}

	protected override async void OnStartup(StartupEventArgs e)
	{
		_ = SetCurrentProcessExplicitAppUserModelID(ApplicationUserModelId);
		base.OnStartup(e);

		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		ApplyThemePalette();
		ApplyLanguage(AppLanguage.English);
		SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
		try
		{
			if (LegalCommandLine.IsLegalCommand(e.Args))
			{
				LegalCommandLine.TryHandle(e.Args, () => LegalCatalog.Load(LegalProfile.App),
					LegalAcceptanceStore.CreateDefault, out int legalExitCode);
				Shutdown(legalExitCode);
				return;
			}

			if (!await WaitForRestartParentAsync(e.Args))
			{
				WpfMessageBox.Show(
					UiText.Get("Shell.App001"),
					UiText.Get("Shell.App002"),
					MessageBoxButton.OK,
					MessageBoxImage.Error);
				Shutdown(-1);
				return;
			}

			string mutexName =
				UpdateInstanceNames.CreateCurrentUserSingleInstanceMutexName();
			string pipeName =
				SingleInstanceActivationChannel.CreateCurrentUserScopedName(
					"AiUsageDashboard.Activation");
			_singleInstanceMutex = new Mutex(
				initiallyOwned: true,
				mutexName,
				out bool isFirstInstance);
			AppLaunchIntent launchIntent = CreateLaunchIntent(e.Args);

			if (!isFirstInstance)
			{
				_singleInstanceMutex.Dispose();
				_singleInstanceMutex = null;

				if (!TryCreateSingleInstanceActivationRequest(
					launchIntent,
					out SingleInstanceActivationRequest activationRequest))
				{
					Shutdown();
					return;
				}

				SingleInstanceActivationResult activationResult =
					await SingleInstanceActivationChannel.RequestActivationAsync(
						pipeName,
						activationRequest,
						ActivationRequestTimeout);

				if (!activationResult.WasSent &&
					(activationRequest !=
						SingleInstanceActivationRequest.ShutdownForUpdate))
				{
					string failureReason =
						GetActivationFailureReason(activationResult.Failure);
					AppDiagnosticWriteResult diagnostic =
						AppDiagnostics.TryWrite(
							"single-instance-activation",
							failureReason);
					WpfMessageBox.Show(
						CreateActivationFailureMessage(
							activationResult.Failure,
							diagnostic),
						UiText.Get("Shell.App003"),
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}

				Shutdown();
				return;
			}

			if (launchIntent == AppLaunchIntent.ShutdownForUpdate)
			{
				Shutdown();
				return;
			}

			LegalCatalog catalog = LegalCatalog.Load(LegalProfile.App);
			LegalAcceptanceStore acceptanceStore = LegalAcceptanceStore.CreateDefault();
			bool isAccepted = acceptanceStore.IsAccepted(catalog);
			if (!isAccepted && (launchIntent == AppLaunchIntent.Interactive))
			{
				JsonDashboardPreferencesStore themePreviewStore = new(
					AppDataPaths.GetDashboardPreferencesFilePath());
				DashboardShellPreferences themePreview =
					await themePreviewStore.LoadDashboardShellPreferencesAsync();
				_selectedTheme = themePreview.Theme;
				ApplyThemePalette();
				ApplyLanguage(themePreview.Language);
			}

			bool canStart = isAccepted ||
				((launchIntent == AppLaunchIntent.Interactive) &&
					LegalTermsDialog.EnsureAccepted(catalog, acceptanceStore));
			if (!canStart)
			{
				Shutdown(LegalCommandLine.AcceptanceRequiredExitCode);
				return;
			}

			_activationChannel = new SingleInstanceActivationChannel(
				pipeName,
				CanAcceptSingleInstanceActivation,
				AcceptSingleInstanceActivation);
			_activationChannel.Start();

			string accountSettingsFilePath =
				AppDataPaths.GetAccountSettingsFilePath();
			string dashboardPreferencesFilePath =
				AppDataPaths.GetDashboardPreferencesFilePath();
			SettingsPersistenceGate settingsPersistenceGate = new();
			PortableSettingsImportWriteTracker portableSettingsImportWriteTracker =
				new();
			JsonAccountProfileStore accountProfileStore = new(
				accountSettingsFilePath,
				portableSettingsImportWriteTracker,
				(operation, summary, exception) =>
				{
					AppDiagnostics.TryWrite(
						operation,
						summary,
						exception);
				});
			JsonAccountCleanupPendingStore accountCleanupPendingStore = new(
				AppDataPaths.GetAccountCleanupPendingFilePath());
			JsonDashboardPreferencesStore dashboardPreferencesStore = new(
				dashboardPreferencesFilePath,
				settingsPersistenceGate,
				portableSettingsImportWriteTracker:
					portableSettingsImportWriteTracker);
			dashboardPreferencesStore.RecoveryRequested +=
				RequestDashboardPreferencesRecovery;
			_dashboardPreferencesRecoveryStore = dashboardPreferencesStore;
			PortableSettingsImportTransaction portableSettingsImportTransaction = new(
				AppDataPaths.GetPortableSettingsImportTransactionDirectoryPath(),
				[
					accountSettingsFilePath,
					$"{accountSettingsFilePath}.bak",
					dashboardPreferencesFilePath
				],
				persistenceGate: settingsPersistenceGate,
				onTargetsRestoredWithResult: async (
					restoreResult,
					cancellationToken) =>
				{
					bool isDashboardPreferencesRecoveryActive =
						await dashboardPreferencesStore
						.SynchronizeAfterPortableImportRestoreAsync(
							restoreResult,
							cancellationToken);

					await accountProfileStore
						.SynchronizeLoadedFileStateAfterExternalRestoreAsync(
							cancellationToken);

					if (isDashboardPreferencesRecoveryActive)
					{
						Interlocked.Exchange(
							ref _dashboardPreferencesRecoveryRequestPendingAfterTransaction,
							1);
					}
				},
				onOperationStarting: async cancellationToken =>
				{
					Interlocked.Exchange(
						ref _dashboardPreferencesRecoveryRequestPendingAfterTransaction,
						0);

					DashboardViewModel? currentViewModel = _dashboardViewModel;
					DashboardShellPreferences? currentShellPreferences =
						Volatile.Read(ref _latestDashboardShellPreferences);

					if ((currentViewModel is null) ||
						(currentShellPreferences is null))
					{
						throw new InvalidOperationException(
							UiText.Get("Shell.App004"));
					}

					await dashboardPreferencesStore
						.BeginPortableImportTransactionAsync(
							new DashboardPreferencesSnapshot(
								currentViewModel.SortMode,
								currentViewModel.DisplayMode,
								currentShellPreferences),
							cancellationToken);
					await accountProfileStore.BeginPortableImportTransactionAsync(
						cancellationToken);
				},
				onTransactionFinished: () =>
				{
					try
					{
						accountProfileStore.CompletePortableImportTransaction();
					}
					finally
					{
						dashboardPreferencesStore
							.CompletePortableImportTransaction();
					}

					if (Interlocked.Exchange(
							ref _dashboardPreferencesRecoveryRequestPendingAfterTransaction,
							0) != 0)
					{
						RequestDashboardPreferencesRecovery();
					}
				},
				onTargetsCommitted: cancellationToken =>
					AuthorizeCommittedPortableCopilotCleanupAsync(
						accountProfileStore,
						accountCleanupPendingStore,
						cancellationToken),
				writeTracker: portableSettingsImportWriteTracker,
				maximumTargetFileSizes:
				[
					MaximumAccountSettingsTransactionTargetSizeBytes,
					MaximumAccountSettingsTransactionTargetSizeBytes,
					MaximumDashboardPreferencesTransactionTargetSizeBytes
				]);
			await portableSettingsImportTransaction
				.RecoverInterruptedImportAsync();
			_widgetPreferencesStore = dashboardPreferencesStore;
			DashboardShellPreferences shellPreferences =
				await dashboardPreferencesStore.LoadDashboardShellPreferencesAsync();
			_selectedTheme = shellPreferences.Theme;
			ApplyThemePalette();
			ApplyLanguage(shellPreferences.Language);
			Volatile.Write(
				ref _latestDashboardShellPreferences,
				shellPreferences);
			IUsageSnapshotStore usageSnapshotStore = new JsonUsageSnapshotStore(
				AppDataPaths.GetUsageSnapshotCacheFilePath,
				reportDiagnostic: (operation, summary, exception) =>
				{
					return AppDiagnostics.TryWrite(
						operation,
						summary,
						exception).WasWritten;
				});
			JsonAntigravityConnectionPendingStore
				antigravityConnectionPendingStore = new(
					AppDataPaths.GetAntigravityConnectionPendingFilePath());
			JsonGrokConnectionPendingStore grokConnectionPendingStore = new(
				AppDataPaths.GetGrokConnectionPendingFilePath());
			JsonGrokAccountBindingStore grokAccountBindingStore = new(
				AppDataPaths.GetGrokAccountBindingFilePath,
				AppDataPaths.GetGrokDataDirectoryPath);
			JsonClaudeAccountBindingStore claudeAccountBindingStore = new(
				AppDataPaths.GetClaudeAccountBindingFilePath,
				AppDataPaths.GetClaudeDataDirectoryPath);
			JsonCodexWorkspaceBindingStore codexWorkspaceBindingStore = new(
				AppDataPaths.GetCodexWorkspaceBindingFilePath,
				AppDataPaths.GetCodexDataDirectoryPath);
			ClaudeAccountOperationGate claudeAccountOperationGate = new();
			CodexAccountOperationGate codexAccountOperationGate = new();
			CopilotAccountOperationGate copilotAccountOperationGate = new();
			GrokAccountOperationGate grokAccountOperationGate = new();
			GrokBindingCommitGate grokBindingCommitGate = new();
			ClaudeBindingCommitGate claudeBindingCommitGate = new();
			CodexBindingCommitGate codexBindingCommitGate = new();
			CodexAccountLogin codexAccountLogin = new(
				AppDataPaths.GetCodexHomeDirectory,
				codexAccountOperationGate);
			CodexAppServerUsagePoller codexUsagePoller = new(
				AppDataPaths.GetCodexHomeDirectory,
				codexWorkspaceBindingStore,
				codexBindingCommitGate,
				codexAccountOperationGate);
			WindowsCopilotCredentialStore copilotCredentialStore = new();
			HttpClient copilotHttpClient = new()
			{
				Timeout = TimeSpan.FromSeconds(20)
			};
			CopilotGitHubUserClient copilotGitHubUserClient = new(
				copilotHttpClient);
			CopilotSdkQuotaClient copilotQuotaClient = new(
				AppDataPaths.GetCopilotHomeDirectory,
				copilotAccountOperationGate,
				copilotCredentialStore,
				copilotGitHubUserClient);
			CopilotCliAccountConnector copilotAccountConnector = new(
				AppDataPaths.GetCopilotHomeDirectory,
				copilotAccountOperationGate,
				copilotCredentialStore,
				copilotQuotaClient);
			CopilotUsageProvider copilotUsageProvider = new(
				copilotQuotaClient);
			ClaudeStatusLineUsageProvider claudeStatusLineProvider = new(
				AppDataPaths.GetClaudeStatusLineCaptureFilePath,
				AppDataPaths.GetClaudeStatusLineFallbackDisabledMarkerFilePath);
			JsonClaudeUsageSafetyStateStore claudeUsageSafetyStateStore = new(
				AppDataPaths.GetClaudeUsageSafetyStateFilePath);
			ClaudeCliUsagePoller claudeUsagePoller = new(
				AppDataPaths.GetClaudeConfigDirectory,
				claudeAccountOperationGate,
				claudeUsageSafetyStateStore,
				claudeAccountBindingStore,
				claudeBindingCommitGate);
			ClaudeUsageProvider claudeUsageProvider = new(
				claudeUsagePoller,
				claudeStatusLineProvider,
				bindingStore: claudeAccountBindingStore);
			ClaudeAccountLogin claudeAccountLogin = new(
				AppDataPaths.GetClaudeConfigDirectory,
				claudeAccountOperationGate);
			JsonAntigravityOfficialPrintSafetyStateStore
				antigravitySafetyStateStore = new(
					AppDataPaths
						.GetAntigravityOfficialPrintSafetyStateFilePath());
			JsonAntigravityVerifiedAccountBindingStore
				antigravityVerifiedAccountBindingStore = new(
					AppDataPaths.GetAntigravityVerifiedAccountBindingFilePath);
			AntigravityOfficialPrintUsageClient antigravityOfficialClient = new(
				antigravitySafetyStateStore);
			AntigravityUsageProvider antigravityUsageProvider = new(
				antigravityOfficialClient,
				new AntigravityOfficialExecutablePathResolver(),
				setupService: new AntigravityMachineSetupService(
					antigravityOfficialClient));
			WindowsGrokAcpProcessFactory grokProcessFactory = new();
			GrokCliExecutableValidator grokExecutableValidator = new(
				new GrokCliVersionProbe(grokProcessFactory));
			GrokAcpUsagePoller grokUsagePoller = new(
				new GrokAcpUsageClient(grokProcessFactory),
				grokExecutableValidator,
				grokAccountOperationGate);
			GrokUsageProvider grokUsageProvider = new(
				grokUsagePoller,
				grokAccountBindingStore,
				bindingCommitGate: grokBindingCommitGate);
			GrokAccountLogin grokAccountLogin = new(
				grokExecutableValidator,
				grokAccountOperationGate);
			IGrokStartupRecovery grokStartupRecovery = new GrokStartupRecovery(
				grokAccountBindingStore,
				grokConnectionPendingStore,
				grokUsagePoller,
				grokBindingCommitGate,
				GrokConnectionStartupRecoveryTimeout,
				GrokAccountCleanupPostStartupBudget);
			UsageProviderRegistry providerRegistry = new(new IUsageProvider[]
			{
				claudeUsageProvider,
				new CodexUsageProvider(codexUsagePoller),
				copilotUsageProvider,
				antigravityUsageProvider,
				grokUsageProvider
			});
			IUsageRefreshCoordinator usageRefreshCoordinator =
				new UsageRefreshCoordinator(
					providerRegistry,
					reportUnexpectedException: (provider, exception) =>
						AppDiagnostics.TryWrite(
							"usage_refresh_unexpected_exception",
							$"provider={provider}",
							exception));
			AccountRuntimeStatePurger accountRuntimeStatePurger = new(
				claudeAccountOperationGate,
				claudeUsagePoller,
				claudeUsageProvider,
				codexAccountOperationGate,
				antigravityUsageProvider,
				antigravityVerifiedAccountBindingStore,
				grokAccountOperationGate,
				grokAccountBindingStore,
				grokConnectionPendingStore,
				claudeAccountBindingStore: claudeAccountBindingStore,
				claudeBindingCommitGate: claudeBindingCommitGate,
				codexWorkspaceBindingStore: codexWorkspaceBindingStore,
				codexBindingCommitGate: codexBindingCommitGate,
				copilotAccountOperationGate: copilotAccountOperationGate,
				deleteCopilotCredential: copilotCredentialStore.Delete);
			DashboardViewModel viewModel = new(
				accountProfileStore,
				usageRefreshCoordinator,
				usageSnapshotStore,
				dashboardPreferencesStore,
				accountRuntimeStatePurger,
				new AntigravityReportedAccountSource(),
				timeProvider: null,
				portableSettingsImportTransaction:
					portableSettingsImportTransaction,
				antigravityVerifiedAccountBindingStore:
					antigravityVerifiedAccountBindingStore,
				accountCleanupPendingStore: accountCleanupPendingStore,
				antigravityConnectionPendingStore:
					antigravityConnectionPendingStore,
				grokConnectionPendingStore: grokConnectionPendingStore,
				reportDiagnostic: (operation, summary, exception) =>
				{
					AppDiagnostics.TryWrite(
						operation,
						summary,
						exception);
				},
				claudeAccountBindingStore: claudeAccountBindingStore,
				claudeBindingCommitGate: claudeBindingCommitGate,
				codexWorkspaceBindingStore: codexWorkspaceBindingStore,
				codexBindingCommitGate: codexBindingCommitGate,
				grokAccountBindingStore: grokAccountBindingStore,
				grokBindingCommitGate: grokBindingCommitGate);
			await viewModel.InitializeAsync();
			await EnsureAntigravityAccountAsync(viewModel, e.Args);
			_dashboardViewModel = viewModel;
			_accountConnectionCoordinator = new AccountConnectionCoordinator(
				claudeAccountLogin,
				codexAccountLogin,
				new AntigravityAccountSetupLauncher(),
				grokAccountLogin,
				grokUsagePoller,
				grokAccountBindingStore,
				grokConnectionPendingStore,
				grokBindingCommitGate,
				claudeAccountBindingStore,
				claudeBindingCommitGate,
				(operation, summary, exception) =>
				{
					AppDiagnostics.TryWrite(
						operation,
						summary,
						exception);
				},
				claudeSubscriptionContextProbe: claudeUsagePoller,
				codexWorkspaceBindingStore: codexWorkspaceBindingStore,
				codexBindingCommitGate: codexBindingCommitGate,
				copilotAccountConnector: copilotAccountConnector);

			if (_isUpdateShutdownReserved)
			{
				_accountConnectionCoordinator.ReserveShutdown();
			}
			await InitializeUpdateServicesAsync();
			_floatingWidgetWindow = new FloatingWidgetWindow(
				viewModel,
				_accountConnectionCoordinator,
				shellPreferences);
			if (ShouldSuppressStartupWindowActivation(launchIntent))
			{
				_floatingWidgetWindow.ShowActivated = false;
			}
			MainWindow = _floatingWidgetWindow;
			_floatingWidgetWindow.IsVisibleChanged += ShellWindow_IsVisibleChanged;
			_floatingWidgetWindow.Activated += ShellWindow_ActivityChanged;
			_floatingWidgetWindow.Deactivated += ShellWindow_ActivityChanged;
			_floatingWidgetWindow.AutomaticUpdateChecksDisableRequested +=
				FloatingWidgetWindow_AutomaticUpdateChecksDisableRequested;
			_floatingWidgetWindow.PreferencesChanged +=
				FloatingWidgetWindow_PreferencesChanged;
			_floatingWidgetWindow.UpdatePrimaryActionRequested +=
				FloatingWidgetWindow_UpdatePrimaryActionRequested;
			_floatingWidgetWindow.UpdateSnoozeRequested +=
				FloatingWidgetWindow_UpdateSnoozeRequested;

			InitializeNotifyIcon();
			InitializeRefreshTimer(viewModel);
			RestoreStartupSurface(
				shellPreferences,
				launchIntent);
			_isRestoringShellPreferences = false;
			ProcessSingleInstanceActivationWork(
				_activationCoordinator.MarkReady());
			UpdateWidgetVisibilityMenuItem();
			UpdateWidgetTopmostMenuItem();
			QueueShellPreferencesSave();
			QueueDashboardPreferencesRecovery();
			QueuePostStartupRecovery(
				viewModel,
				claudeUsageSafetyStateStore,
				accountRuntimeStatePurger,
				grokStartupRecovery);
			_isUpdateShutdownReservationReady = true;
			TryStartUpdateShutdownChannel();
			InitializeUpdateCheckTimer();
			RefreshUpdatePresentation();
			BeginAutomaticUpdateNoticePresentation();
			BeginUpdateBalloonAttempt();
		}
		catch (Exception exception)
		{
			string failureReason =
				AppDiagnostics.GetUserFacingFailureReason(exception);
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"startup",
				failureReason,
				exception);
			WpfMessageBox.Show(
				CreateStartupFailureMessage(exception, diagnostic),
				UiText.Get("Shell.App005"),
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			Shutdown(-1);
		}
	}

	internal static async Task AuthorizeCommittedPortableCopilotCleanupAsync(
		IAccountProfileStore accountProfileStore,
		IAccountCleanupPendingStore accountCleanupPendingStore,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(accountProfileStore);
		ArgumentNullException.ThrowIfNull(accountCleanupPendingStore);
		AccountProfileLoadResult committedProfiles =
			await accountProfileStore.LoadAsync(cancellationToken);
		if (committedProfiles.Status != AccountProfileLoadStatus.Loaded)
		{
			throw new InvalidDataException(
				UiText.Get("Shell.App006"));
		}

		(Guid AccountId, ProviderKind Provider)[] committedCopilotKeys =
			committedProfiles.Accounts
				.Where(profile => profile.Provider == ProviderKind.Copilot)
				.Select(profile => (profile.Id, profile.Provider))
				.Distinct()
				.ToArray();
		await accountCleanupPendingStore
			.AuthorizeRetainedPrivateStateDeletionForCommittedAccountsAsync(
				committedCopilotKeys,
				cancellationToken);
	}

	private void RequestDashboardPreferencesRecovery()
	{
		try
		{
			_ = Dispatcher.BeginInvoke(
				DispatcherPriority.Background,
				new Action(QueueDashboardPreferencesRecovery));
		}
		catch (InvalidOperationException) when (
			Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			// Shutdown owns cancellation and draining from this point onward.
		}
	}

	private void QueueDashboardPreferencesRecovery()
	{
		if (IsQuitting ||
			_startupRecoverySource.IsCancellationRequested ||
			(_dashboardViewModel is null) ||
			(_dashboardPreferencesRecoveryStore is null))
		{
			return;
		}

		_isDashboardPreferencesRecoveryRequested = true;

		if (!_dashboardPreferencesRecoveryTask.IsCompleted)
		{
			return;
		}

		_dashboardPreferencesRecoveryTask =
			RunQueuedDashboardPreferencesRecoveryAsync();
	}

	private async Task RunQueuedDashboardPreferencesRecoveryAsync()
	{
		do
		{
			_isDashboardPreferencesRecoveryRequested = false;
			DashboardViewModel? viewModel = _dashboardViewModel;
			IDashboardPreferencesRecoveryStore? recoveryStore =
				_dashboardPreferencesRecoveryStore;

			if ((viewModel is null) || (recoveryStore is null))
			{
				return;
			}

			await RecoverDashboardPreferencesUntilReadyAsync(
				viewModel,
				recoveryStore);
		}
		while (_isDashboardPreferencesRecoveryRequested &&
			!IsQuitting &&
			!_startupRecoverySource.IsCancellationRequested);
	}

	private async Task RecoverDashboardPreferencesUntilReadyAsync(
		DashboardViewModel viewModel,
		IDashboardPreferencesRecoveryStore recoveryStore)
	{
		CancellationToken cancellationToken = _startupRecoverySource.Token;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				DashboardPreferencesRecoveryStatus status;
				await _shellPreferencesSaveGate.WaitAsync(cancellationToken);

				try
				{
					status = await viewModel.RecoverDashboardPreferencesAsync(
						recoveryStore,
						() => _floatingWidgetWindow?.CreatePreferences(
								_floatingWidgetWindow.IsVisible) ??
							DashboardShellPreferences.Default,
						preferences =>
						{
							if (_floatingWidgetWindow is null)
							{
								return;
							}

							bool wasRestoring = _isRestoringShellPreferences;
							_isRestoringShellPreferences = true;

							try
							{
								DashboardShellPreferences effectivePreferences =
									preferences with
									{
										IsWidgetVisible =
											ShouldShowFloatingWidgetOnStartup(
												preferences)
									};
								_floatingWidgetWindow.ApplyRecoveredPreferences(
									effectivePreferences);
								_selectedTheme = effectivePreferences.Theme;
								ApplyThemePalette();
							}
							finally
							{
								_isRestoringShellPreferences = wasRestoring;
							}
						},
						cancellationToken);
				}
				finally
				{
					_shellPreferencesSaveGate.Release();
				}

				if (status != DashboardPreferencesRecoveryStatus.Pending)
				{
					UpdateWidgetVisibilityMenuItem();
					UpdateWidgetTopmostMenuItem();

					if (status == DashboardPreferencesRecoveryStatus.Ready)
					{
						bool hadReportedFailure =
							_hasReportedShellPreferencesSaveFailure;
						_hasReportedShellPreferencesSaveFailure = false;

						if (hadReportedFailure)
						{
							ReportShellPreferencesSaveRecovered();
						}

						QueueShellPreferencesSave();
					}

					return;
				}

				await Task.Delay(
					DashboardPreferencesRecoveryRetryInterval,
					cancellationToken);
			}
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			// App shutdown ends best-effort preference recovery.
		}
		catch (Exception exception)
		{
			string failureReason = GetShellPreferencesSaveFailureReason(exception);
			AppDiagnostics.TryWrite(
				"dashboard-preferences-recovery",
				failureReason,
				exception);
			ReportShellPreferencesSaveFailure(failureReason);
		}
	}

	private void QueuePostStartupRecovery(
		DashboardViewModel viewModel,
		JsonClaudeUsageSafetyStateStore claudeUsageSafetyStateStore,
		AccountRuntimeStatePurger accountRuntimeStatePurger,
		IGrokStartupRecovery grokStartupRecovery)
	{
		if (_isPostStartupRecoveryRunning || IsQuitting)
		{
			return;
		}

		_isPostStartupRecoveryRunning = true;
		_ = Dispatcher.BeginInvoke(
			DispatcherPriority.Background,
			new Action(() =>
			{
				if (IsQuitting ||
					_startupRecoverySource.IsCancellationRequested)
				{
					_isPostStartupRecoveryRunning = false;
					return;
				}

				_postStartupRecoveryTask = CompletePostStartupRecoveryAsync(
					viewModel,
					claudeUsageSafetyStateStore,
					accountRuntimeStatePurger,
					grokStartupRecovery);
			}));
	}

	private async Task CompletePostStartupRecoveryAsync(
		DashboardViewModel viewModel,
		JsonClaudeUsageSafetyStateStore claudeUsageSafetyStateStore,
		AccountRuntimeStatePurger accountRuntimeStatePurger,
		IGrokStartupRecovery grokStartupRecovery)
	{
		CancellationToken cancellationToken = _startupRecoverySource.Token;

		try
		{
			await viewModel.RestoreDeferredStartupDisplayStateAsync(
				cancellationToken);
			await grokStartupRecovery.RecoverAsync(
				viewModel,
				cancellationToken);

			var claudeSafetyRecovery =
				await RecoverClaudeUsageSafetyStateFilesAsync(
					claudeUsageSafetyStateStore,
					viewModel.Accounts
						.Where(account => account.IsClaude)
						.Select(account => account.Id),
					viewModel.CanManageAccounts,
					AppDataPaths.GetClaudeDataDirectoryPath(),
					ClaudeSafetyStateStartupRecoveryTimeout,
					(accountId, token) =>
						accountRuntimeStatePurger.PurgeAsync(
							accountId,
							ProviderKind.Claude,
							token),
					(accountId, token) =>
						viewModel.ScheduleAccountCleanupRetryAsync(
							accountId,
							ProviderKind.Claude,
							token),
					cancellationToken);
			if (claudeSafetyRecovery.FailedAccountIds.Count > 0 ||
				claudeSafetyRecovery.DidDiskDiscoveryFail)
			{
				AppDiagnostics.TryWrite(
					"claude_safety_state_startup_recovery_incomplete",
					$"account_count={claudeSafetyRecovery.FailedAccountIds.Count};" +
					$"disk_discovery_failed={claudeSafetyRecovery.DidDiskDiscoveryFail}");
			}

			cancellationToken.ThrowIfCancellationRequested();
			viewModel.ScheduleUsageRefreshInBackground();
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			// The application is closing; startup recovery no longer needs to finish.
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"post_startup_recovery",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			viewModel.ReportRefreshFailure();
		}
		finally
		{
			_isPostStartupRecoveryRunning = false;
		}
	}

	internal static async Task<(
		IReadOnlyList<Guid> FailedAccountIds,
		bool DidDiskDiscoveryFail)>
		RecoverClaudeUsageSafetyStateFilesAsync(
			JsonClaudeUsageSafetyStateStore safetyStateStore,
			IEnumerable<Guid> knownAccountIds,
			bool canIdentifyRemovedAccounts,
			string accountRootDirectoryPath,
			TimeSpan timeout,
			Func<Guid, CancellationToken, Task> purgeRemovedAccountState,
			Func<Guid, CancellationToken, Task> scheduleRemovedAccountCleanupRetry,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(safetyStateStore);
		ArgumentNullException.ThrowIfNull(knownAccountIds);
		ArgumentNullException.ThrowIfNull(purgeRemovedAccountState);
		ArgumentNullException.ThrowIfNull(scheduleRemovedAccountCleanupRetry);
		ArgumentException.ThrowIfNullOrWhiteSpace(accountRootDirectoryPath);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		HashSet<Guid> knownIds = knownAccountIds
			.Where(accountId => accountId != Guid.Empty)
			.ToHashSet();
		HashSet<Guid> recoveryAccountIds = new(knownIds);
		List<Guid> failures = new();
		bool didDiskDiscoveryFail = false;
		using CancellationTokenSource timeoutSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(timeout);
		try
		{
			IReadOnlyList<Guid> discoveredAccountIds = await Task.Run(
				() => JsonClaudeUsageSafetyStateStore.DiscoverAccountIdsForRecovery(
					accountRootDirectoryPath,
					timeoutSource.Token),
				timeoutSource.Token);
			recoveryAccountIds.UnionWith(discoveredAccountIds);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			didDiskDiscoveryFail = true;
		}

		Guid[] distinctAccountIds = recoveryAccountIds.ToArray();

		for (int index = 0; index < distinctAccountIds.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Guid accountId = distinctAccountIds[index];
			bool isRemovedAccount =
				!knownIds.Contains(accountId) && canIdentifyRemovedAccounts;

			try
			{
				if (!isRemovedAccount)
				{
					await safetyStateStore.RecoverPendingCleanupAsync(
						accountId,
						timeoutSource.Token);
				}
				else
				{
					// Route disk-only cleanup through the same quiescence-first purge
					// used by account removal. A failed recovery leaves its durable,
					// identity-free attempt marker for the next startup.
					await purgeRemovedAccountState(
						accountId,
						timeoutSource.Token);
				}
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException) when (
				timeoutSource.IsCancellationRequested)
			{
				Guid[] timedOutAccountIds = distinctAccountIds[index..];
				failures.AddRange(timedOutAccountIds);

				foreach (Guid timedOutAccountId in timedOutAccountIds)
				{
					if (knownIds.Contains(timedOutAccountId) ||
						!canIdentifyRemovedAccounts)
					{
						continue;
					}

					try
					{
						cancellationToken.ThrowIfCancellationRequested();
						await scheduleRemovedAccountCleanupRetry(
							timedOutAccountId,
							cancellationToken);
					}
					catch (OperationCanceledException) when (
						cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch
					{
						// The failed IDs remain in the diagnostic result.
					}
				}

				break;
			}
			catch
			{
				cancellationToken.ThrowIfCancellationRequested();
				failures.Add(accountId);

				if (isRemovedAccount)
				{
					try
					{
						await scheduleRemovedAccountCleanupRetry(
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
						// The failed account remains in the diagnostic result. The
						// scheduler owns any additional persistence diagnostics.
					}
				}
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		return (failures, didDiskDiscoveryFail);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
		if (!_startupRecoverySource.IsCancellationRequested)
		{
			_startupRecoverySource.Cancel();
		}

		_refreshTimer?.Stop();
		StopUpdateServicesOnExit();
		_dashboardViewModel?.StopRefreshing();
		_accountConnectionCoordinator?.Dispose();
		_dashboardViewModel?.Dispose();
		_activationChannel?.Dispose();
		_updateShutdownChannel?.Dispose();
		_notifyIcon?.Dispose();
		_themeAppIcon?.Dispose();
		_applicationIcon?.Dispose();
		_shellPreferencesSaveGate.Dispose();

		if (_singleInstanceMutex is not null)
		{
			try
			{
				_singleInstanceMutex.ReleaseMutex();
			}
			catch (ApplicationException)
			{
				// The mutex was not owned, so there is nothing to release.
			}

			_singleInstanceMutex.Dispose();
		}

		base.OnExit(e);
	}

	internal static bool ShouldEnsureAntigravityAccount(
		IEnumerable<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		return arguments.Any(argument => string.Equals(
			argument,
			AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount,
			StringComparison.Ordinal));
	}

	internal static AppLaunchIntent CreateLaunchIntent(
		IEnumerable<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		string[] argumentSnapshot = arguments.ToArray();

		if (argumentSnapshot.Any(argument => string.Equals(
			argument,
			ShutdownForUpdateArgument,
			StringComparison.Ordinal)))
		{
			return AppLaunchIntent.ShutdownForUpdate;
		}

		if (ShouldEnsureAntigravityAccount(argumentSnapshot))
		{
			return AppLaunchIntent.EnsureAntigravityAccount;
		}

		return argumentSnapshot.Any(argument => string.Equals(
			argument,
			WindowsLogonStartupRegistrationContract.StartupArgument,
			StringComparison.Ordinal))
			? AppLaunchIntent.LogonStartup
			: AppLaunchIntent.Interactive;
	}

	internal static bool ShouldSuppressStartupWindowActivation(
		AppLaunchIntent launchIntent) =>
		launchIntent == AppLaunchIntent.LogonStartup;

	internal static SingleInstanceActivationRequest CreateSingleInstanceActivationRequest(
		IEnumerable<string> arguments)
	{
		if (TryCreateSingleInstanceActivationRequest(
			CreateLaunchIntent(arguments),
			out SingleInstanceActivationRequest request))
		{
			return request;
		}

		throw new InvalidOperationException(
			"A logon startup launch must not activate an existing instance.");
	}

	internal static bool TryCreateSingleInstanceActivationRequest(
		AppLaunchIntent launchIntent,
		out SingleInstanceActivationRequest request)
	{
		switch (launchIntent)
		{
			case AppLaunchIntent.Interactive:
				request = SingleInstanceActivationRequest.ShowFloatingWidget;
				return true;
			case AppLaunchIntent.EnsureAntigravityAccount:
				request = SingleInstanceActivationRequest.EnsureAntigravityAccount;
				return true;
			case AppLaunchIntent.ShutdownForUpdate:
				request = SingleInstanceActivationRequest.ShutdownForUpdate;
				return true;
			case AppLaunchIntent.LogonStartup:
				request = default;
				return false;
			default:
				throw new ArgumentOutOfRangeException(nameof(launchIntent));
		}
	}

	internal static AccountProfile CreateDefaultAntigravityAccountProfile()
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			string.Empty);
	}

	internal static AntigravityAccountEnsureFailureNotification
		CreateAntigravityAccountEnsureFailureNotification(
			Exception exception,
			AppDiagnosticWriteResult diagnostic)
	{
		ArgumentNullException.ThrowIfNull(exception);

		if (exception is AccountProfileStoreException
			{
				HasCommittedChanges: true
			})
		{
			return new AntigravityAccountEnsureFailureNotification(
				UiText.Get("Shell.App007"),
				UiText.Get("Shell.App008") +
				CreateDiagnosticGuidance(diagnostic));
		}

		return new AntigravityAccountEnsureFailureNotification(
			UiText.Get("Shell.App009"),
			UiText.Get("Shell.App010") +
			CreateDiagnosticGuidance(diagnostic));
	}

	internal static AntigravityAccountEnsureFailureNotification
		CreateUnexpectedAntigravityAccountEnsureFailureNotification(
			AppDiagnosticWriteResult diagnostic)
	{
		return new AntigravityAccountEnsureFailureNotification(
			UiText.Get("Shell.App011"),
			UiText.Get("Shell.App012") +
			CreateDiagnosticGuidance(diagnostic));
	}

	private static async Task EnsureAntigravityAccountAsync(
		DashboardViewModel viewModel,
		IEnumerable<string> arguments)
	{
		if (!ShouldEnsureAntigravityAccount(arguments) ||
			viewModel.Accounts.Any(
				account => account.Provider == ProviderKind.Antigravity))
		{
			return;
		}

		try
		{
			await viewModel.AddAccountAsync(
				CreateDefaultAntigravityAccountProfile());
		}
		catch (Exception exception) when (
			exception is AccountProfileStoreException or
				InvalidOperationException)
		{
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"antigravity-setup-account",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			AntigravityAccountEnsureFailureNotification notification =
				CreateAntigravityAccountEnsureFailureNotification(
					exception,
					diagnostic);
			WpfMessageBox.Show(
				notification.Message,
				notification.Title,
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	private bool CanAcceptSingleInstanceActivation(
		SingleInstanceActivationRequest request)
	{
		if (request == SingleInstanceActivationRequest.ShutdownForUpdate)
		{
			return TryReserveStructuredUpdateShutdown() ==
				UpdateShutdownOutcome.Accepted;
		}

		if (!Dispatcher.CheckAccess())
		{
			try
			{
				return Dispatcher.Invoke(
					() => CanAcceptSingleInstanceActivation(request));
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or
					TaskCanceledException)
			{
				return false;
			}
		}

		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
			IsQuitting || _isUpdateShutdownReserved)
		{
			return false;
		}

		return true;
	}

	private async Task InitializeUpdateServicesAsync()
	{
		_appInstallationContext = await new AppInstallationContextDetector()
			.DetectAsync(Environment.ProcessPath);
		string? currentProductVersion = GetCurrentProductVersion();
		AppUpdateBuildDefaults? buildDefaults = AppUpdateBuildDefaults.Load();

		if (buildDefaults is null)
		{
			_updatePresentationState = UpdatePresentationState.CreateInitial(
				isFeatureAvailableInBuild: false,
				currentProductVersion);
			return;
		}

		HttpClient httpClient = new()
		{
			Timeout = UpdateHttpTimeout
		};
		UpdateCheckCoordinator coordinator = new(
			SignedFeedUpdateAvailabilityChecker.Create(
				httpClient,
				buildDefaults),
			new JsonUpdateCheckStateStore(
				AppDataPaths.GetUpdateCheckStateFilePath()),
			new UpdateCheckCoordinatorOptions(
				_appInstallationContext,
				currentProductVersion,
				IsFeatureAvailableInBuild: true,
				ReportDiagnostic: (summary, exception) =>
				{
					_ = AppDiagnostics.TryWrite(
						"update-check-coordinator",
						summary,
						exception);
				}));

		try
		{
			await coordinator.InitializeAsync();
		}
		catch
		{
			await coordinator.DisposeAsync();
			httpClient.Dispose();
			throw;
		}

		_updateHttpClient = httpClient;
		_updateCheckCoordinator = coordinator;
		_updatePresentationState = coordinator.CurrentState;
		coordinator.StateChanged += UpdateCheckCoordinator_StateChanged;
	}

	private static string? GetCurrentProductVersion()
	{
		string? executablePath = Environment.ProcessPath;

		if (string.IsNullOrWhiteSpace(executablePath))
		{
			return null;
		}

		try
		{
			return FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
		}
		catch (Exception exception) when (
			exception is ArgumentException or FileNotFoundException or
				SecurityException or Win32Exception)
		{
			_ = AppDiagnostics.TryWrite(
				"update-current-version",
				UiText.Get("Shell.App013"),
				exception);
			return null;
		}
	}

	private void TryStartUpdateShutdownChannel()
	{
		UpdateManifest? manifest = _appInstallationContext.InstalledManifest;

		if (manifest is null)
		{
			return;
		}

		try
		{
			UpdateProcessIdentity identity =
				UpdateProcessIdentity.CaptureCurrent(
					manifest.PayloadGenerationId);
			UpdateShutdownChannel channel = new(
				UpdateShutdownChannel.CreateCurrentUserPipeName(),
				identity,
				TryReserveStructuredUpdateShutdown,
				RollbackStructuredUpdateShutdownReservation,
				DispatchStructuredUpdateShutdown);
			_updateShutdownChannel = channel;
			Task readinessTask = channel.StartAsync();
			_ = ObserveUpdateShutdownChannelReadinessAsync(
				channel,
				readinessTask);
		}
		catch (Exception exception) when (
			exception is IOException or InvalidDataException or
				UnauthorizedAccessException or ArgumentException or
				InvalidOperationException)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-channel-startup",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
	}

	private async Task ObserveUpdateShutdownChannelReadinessAsync(
		UpdateShutdownChannel channel,
		Task readinessTask)
	{
		try
		{
			await readinessTask;
		}
		catch (Exception exception)
		{
			string summary = exception is ObjectDisposedException
				? UiText.Get("Shell.App014")
				: AppDiagnostics.GetUserFacingFailureReason(exception);
			ReportUpdateShutdownChannelStopped(
				channel,
				"update-shutdown-channel-readiness",
				summary,
				exception);
			return;
		}

		if (IsQuitting || !ReferenceEquals(_updateShutdownChannel, channel))
		{
			return;
		}

		_isUpdateShutdownChannelReady = true;
		TryRefreshUpdatePresentationForShutdownChannel();

		try
		{
			await channel.ListenerCompletion;
		}
		catch (Exception exception)
		{
			ReportUpdateShutdownChannelStopped(
				channel,
				"update-shutdown-channel-listener",
				UiText.Get("Shell.App015"),
				exception);
			return;
		}

		ReportUpdateShutdownChannelStopped(
			channel,
			"update-shutdown-channel-listener",
			UiText.Get("Shell.App015"));
	}

	private void ReportUpdateShutdownChannelStopped(
		UpdateShutdownChannel channel,
		string operation,
		string summary,
		Exception? exception = null)
	{
		if (IsQuitting ||
			Dispatcher.HasShutdownStarted ||
			Dispatcher.HasShutdownFinished ||
			!ReferenceEquals(_updateShutdownChannel, channel))
		{
			return;
		}

		_isUpdateShutdownChannelReady = false;
		_ = AppDiagnostics.TryWrite(
			operation,
			summary,
			exception);
		TryRefreshUpdatePresentationForShutdownChannel();
	}

	private void TryRefreshUpdatePresentationForShutdownChannel()
	{
		try
		{
			RefreshUpdatePresentation();
		}
		catch (Exception presentationException)
		{
			_ = AppDiagnostics.TryWrite(
				"update-shutdown-channel-presentation",
				UiText.Get("Shell.App016"),
				presentationException);
		}
	}

	private void UpdateCheckCoordinator_StateChanged(
		object? sender,
		UpdatePresentationStateChangedEventArgs e)
	{
		if (!Dispatcher.CheckAccess())
		{
			try
			{
				_ = Dispatcher.BeginInvoke(
					() => ApplyUpdatePresentationState(e.State),
					DispatcherPriority.Normal);
			}
			catch (InvalidOperationException) when (
				Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
			{
				// 從這裡開始由 App 關閉流程負責釋放更新檢查資源。
			}

			return;
		}

		ApplyUpdatePresentationState(e.State);
	}

	private void ApplyUpdatePresentationState(UpdatePresentationState state)
	{
		_updatePresentationState = state;
		RefreshUpdatePresentation();
		BeginUpdateBalloonAttempt();
	}

	private void RefreshUpdatePresentation()
	{
		if (!Dispatcher.CheckAccess())
		{
			try
			{
				_ = Dispatcher.BeginInvoke(
					RefreshUpdatePresentation,
					DispatcherPriority.Normal);
			}
			catch (InvalidOperationException) when (
				Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
			{
				// 從這裡開始由 App 關閉流程負責剩餘的 UI 工作。
			}

			return;
		}

		bool shouldShowAutomaticCheckNotice =
			_updateCheckCoordinator?.ShouldShowAutomaticCheckNotice == true;
		bool canLaunchUpdater = _maintenanceUpdaterLauncher.CanLaunch(
			_appInstallationContext,
			_isUpdateShutdownChannelReady);
		UpdateUiPresentation presentation =
			UpdateUiPresentationFactory.Create(
				_updatePresentationState,
				shouldShowAutomaticCheckNotice,
				_appInstallationContext.Kind,
				canLaunchUpdater,
				_maintenanceUpdaterLauncher.IsRunning,
				DateTimeOffset.Now);
		UpdateBannerAnnouncementKey? bannerAnnouncementKey =
			UpdateBannerAnnouncementPolicy.CreateKey(
				presentation,
				_updatePresentationState.ReleaseSequence,
				shouldShowAutomaticCheckNotice);
		_updateUiPresentation = presentation;
		UpdateAutomaticUpdateCheckTimerState();
		_floatingWidgetWindow?.ApplyUpdatePresentation(
			presentation,
			bannerAnnouncementKey);
		_aboutWindow?.UpdateUpdateStatus(
			() => _updateUiPresentation?.AboutStatusText ?? string.Empty,
			presentation.CanCheckManually,
			presentation.IsChecking);
		UpdateUpdateTrayMenuItems(presentation);

		// 只有提示介面全部套用成功後，才開始首次自動連線的 30 秒 gate。
		if (shouldShowAutomaticCheckNotice &&
			(_automaticUpdateNoticePresentedAtUtc is null) &&
			(_floatingWidgetWindow is FloatingWidgetWindow window) &&
			window.IsVisible &&
			!window.IsCollapsed)
		{
			_automaticUpdateNoticePresentedAtUtc = DateTimeOffset.UtcNow;
		}

		if (!shouldShowAutomaticCheckNotice)
		{
			_automaticUpdateNoticePresentedAtUtc = null;
		}
	}

	private void UpdateUpdateTrayMenuItems(UpdateUiPresentation presentation)
	{
		bool isFeatureAvailable = _updateCheckCoordinator is not null;

		if (_checkForUpdatesMenuItem is not null)
		{
			_checkForUpdatesMenuItem.Enabled =
				isFeatureAvailable && presentation.CanCheckManually;
			_checkForUpdatesMenuItem.Text = presentation.IsChecking
				? UiText.Get("Shell.App017")
				: UiText.Get("Shell.App018");
		}

		if (_automaticUpdateChecksMenuItem is not null)
		{
			_automaticUpdateChecksMenuItem.Enabled = isFeatureAvailable;
			_automaticUpdateChecksMenuItem.Checked =
				isFeatureAvailable &&
				_updatePresentationState.IsAutoCheckEnabled;
		}

		if (_updateAvailableMenuItem is not null)
		{
			_updateAvailableMenuItem.Visible =
				!string.IsNullOrWhiteSpace(presentation.TrayUpdateActionText);
			_updateAvailableMenuItem.Enabled =
				presentation.IsPrimaryActionEnabled;
			_updateAvailableMenuItem.Text =
				presentation.TrayUpdateActionText ?? UiText.Get("Shell.App019");
		}
	}

	private void InitializeUpdateCheckTimer()
	{
		if (_updateCheckCoordinator is null)
		{
			return;
		}

		_updateCheckTimer = new DispatcherTimer
		{
			Interval = AutomaticUpdateCheckTimerInterval
		};
		_updateCheckTimer.Tick += UpdateCheckTimer_Tick;
		UpdateAutomaticUpdateCheckTimerState();

		try
		{
			Microsoft.Win32.SystemEvents.PowerModeChanged +=
				SystemEvents_PowerModeChanged;
			_isUpdatePowerModeSubscribed = true;
		}
		catch (Exception exception) when (
			exception is ExternalException or InvalidOperationException)
		{
			_ = AppDiagnostics.TryWrite(
				"update-resume-monitor-startup",
				UiText.Get("Shell.App020"),
				exception);
		}
	}

	private void UpdateAutomaticUpdateCheckTimerState()
	{
		if (_updateCheckTimer is not DispatcherTimer timer)
		{
			return;
		}

		bool shouldRun = ShouldRunUpdateCheckTimer(
			IsQuitting,
			_updateCheckCoordinator is not null,
			_updatePresentationState.IsAutoCheckEnabled,
			_updatePresentationState.IsSnoozed);
		if (shouldRun && !timer.IsEnabled)
		{
			timer.Start();
		}
		else if (!shouldRun && timer.IsEnabled)
		{
			timer.Stop();
		}
	}

	private void UpdateCheckTimer_Tick(object? sender, EventArgs e)
	{
		BeginAutomaticUpdateCheck(isResume: false);
	}

	private void SystemEvents_PowerModeChanged(
		object sender,
		Microsoft.Win32.PowerModeChangedEventArgs e)
	{
		if (e.Mode != Microsoft.Win32.PowerModes.Resume)
		{
			return;
		}

		try
		{
			_ = Dispatcher.BeginInvoke(
				() => BeginAutomaticUpdateCheck(isResume: true),
				DispatcherPriority.Normal);
		}
		catch (InvalidOperationException) when (
			Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			// 從這裡開始由 App 關閉流程負責釋放更新檢查資源。
		}
	}

	private void BeginAutomaticUpdateCheck(bool isResume)
	{
		if (IsQuitting ||
			_isUpdateAutomaticCheckRunning ||
			(_updateCheckCoordinator is null))
		{
			return;
		}

		_isUpdateAutomaticCheckRunning = true;
		_ = CompleteAutomaticUpdateCheckAsync(isResume);
	}

	private async Task CompleteAutomaticUpdateCheckAsync(bool isResume)
	{
		try
		{
			UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
			if ((coordinator is null) ||
				!await CanStartAutomaticUpdateCheckAsync(coordinator))
			{
				return;
			}

			_ = isResume
				? await coordinator.HandleResumeAsync()
				: await coordinator.TryCheckAutomaticallyAsync();
		}
		catch (ObjectDisposedException) when (IsQuitting)
		{
			// 從這裡開始由 App 關閉流程負責取消作業。
		}
		catch (Exception exception)
		{
			_ = AppDiagnostics.TryWrite(
				"automatic-update-check",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
		finally
		{
			_isUpdateAutomaticCheckRunning = false;
		}
	}

	private async Task<bool> CanStartAutomaticUpdateCheckAsync(
		UpdateCheckCoordinator coordinator)
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		FloatingWidgetWindow? window = _floatingWidgetWindow;
		AutomaticUpdateCheckGateAction action =
			AutomaticUpdateNoticePolicy.GetAutomaticCheckAction(
				coordinator.ShouldShowAutomaticCheckNotice,
				window?.IsVisible == true,
				window?.IsCollapsed == true,
				_automaticUpdateNoticePresentedAtUtc,
				utcNow);

		switch (action)
		{
			case AutomaticUpdateCheckGateAction.Allow:
				return true;
			case AutomaticUpdateCheckGateAction.ShowInlineNotice:
				RefreshUpdatePresentation();
				return false;
			case AutomaticUpdateCheckGateAction.ShowBalloonNotice:
				if (TryShowAutomaticUpdateNoticeBalloon())
				{
					_automaticUpdateNoticePresentedAtUtc = DateTimeOffset.UtcNow;
				}

				return false;
			case AutomaticUpdateCheckGateAction.WaitForDelay:
				return false;
			case AutomaticUpdateCheckGateAction.RestartDelay:
				_automaticUpdateNoticePresentedAtUtc = utcNow;
				_ = AppDiagnostics.TryWrite(
					"automatic-update-notice-clock-rollback",
					UiText.Get("Shell.App021"));
				return false;
			case AutomaticUpdateCheckGateAction.MarkNoticeShownAndAllow:
				await coordinator.MarkAutomaticCheckNoticeShownAsync();
				return true;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(action),
					action,
					UiText.Get("Shell.App022"));
		}
	}

	private void PresentAutomaticUpdateNoticeIfNeeded()
	{
		UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
		if (coordinator is null)
		{
			return;
		}

		FloatingWidgetWindow? window = _floatingWidgetWindow;
		AutomaticUpdateNoticePresentationAction action =
			AutomaticUpdateNoticePolicy.GetPresentationAction(
				coordinator.ShouldShowAutomaticCheckNotice,
				window?.IsVisible == true,
				window?.IsCollapsed == true,
				_automaticUpdateNoticePresentedAtUtc is not null);

		switch (action)
		{
			case AutomaticUpdateNoticePresentationAction.None:
				return;
			case AutomaticUpdateNoticePresentationAction.ShowInline:
				RefreshUpdatePresentation();
				return;
			case AutomaticUpdateNoticePresentationAction.ShowBalloon:
				if (TryShowAutomaticUpdateNoticeBalloon())
				{
					_automaticUpdateNoticePresentedAtUtc = DateTimeOffset.UtcNow;
				}

				return;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(action),
					action,
					UiText.Get("Shell.App023"));
		}
	}

	private bool TryShowAutomaticUpdateNoticeBalloon()
	{
		if (_notifyIcon is null)
		{
			return false;
		}

		try
		{
			_notifyIcon.ShowBalloonTip(
				7000,
				UiText.Get("Shell.App024"),
				UiText.Get("Shell.App025"),
				System.Windows.Forms.ToolTipIcon.Info);
			return true;
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or SecurityException)
		{
			_ = AppDiagnostics.TryWrite(
				"automatic-update-check-notice",
				UiText.Get("Shell.App026"),
				exception);
			return false;
		}
	}

	private void BeginUpdateBalloonAttempt()
	{
		if (IsQuitting ||
			_isUpdateBalloonAttemptRunning ||
			(_notifyIcon is null) ||
			(_updateCheckCoordinator is null) ||
			_updateCheckCoordinator.ShouldShowAutomaticCheckNotice)
		{
			return;
		}

		_isUpdateBalloonAttemptRunning = true;
		_ = CompleteUpdateBalloonAttemptAsync();
	}

	private async Task CompleteUpdateBalloonAttemptAsync()
	{
		try
		{
			UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
			FormsNotifyIcon? notifyIcon = _notifyIcon;
			FloatingWidgetWindow? window = _floatingWidgetWindow;
			UpdateWindowActivity activity = new(
				window?.IsVisible == true,
				window?.IsCollapsed == true,
				window?.IsActive == true);
			if ((coordinator is null) ||
				(notifyIcon is null) ||
				!coordinator.TryGetBalloonAttemptKey(
					activity,
					out UpdateNotificationKey key))
			{
				return;
			}

			try
			{
				notifyIcon.ShowBalloonTip(
					7000,
					UiText.Get("Shell.App027"),
					UiText.Format("Shell.App028", key.Version),
					System.Windows.Forms.ToolTipIcon.Info);
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or SecurityException)
			{
				_ = AppDiagnostics.TryWrite(
					"update-availability-balloon",
					UiText.Get("Shell.App029"),
					exception);
				return;
			}

			_ = await coordinator.RecordBalloonAttemptAsync(key);
		}
		catch (ObjectDisposedException) when (IsQuitting)
		{
			// 從這裡開始由 App 關閉流程負責取消作業。
		}
		catch (Exception exception)
		{
			_ = AppDiagnostics.TryWrite(
				"update-availability-balloon",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
		finally
		{
			_isUpdateBalloonAttemptRunning = false;
		}
	}

	private void FloatingWidgetWindow_AutomaticUpdateChecksDisableRequested(
		object? sender,
		EventArgs e)
	{
		StartUpdateUiOperation(
			() => SetAutomaticUpdateChecksEnabledAsync(isEnabled: false),
			"disable-automatic-update-checks");
	}

	private void FloatingWidgetWindow_UpdatePrimaryActionRequested(
		object? sender,
		EventArgs e)
	{
		StartUpdateUiOperation(
			ExecutePrimaryUpdateActionAsync,
			"update-primary-action");
	}

	private void FloatingWidgetWindow_UpdateSnoozeRequested(
		object? sender,
		EventArgs e)
	{
		StartUpdateUiOperation(SnoozeUpdateAsync, "snooze-update");
	}

	private void AboutWindow_UpdateCheckRequested(
		object? sender,
		EventArgs e)
	{
		StartUpdateUiOperation(
			CheckForUpdatesManuallyAsync,
			"manual-update-check",
			sender as AboutWindow);
	}

	private void StartUpdateUiOperation(
		Func<Task> operation,
		string diagnosticOperation,
		Window? preferredOwner = null)
	{
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticOperation);
		_ = CompleteUpdateUiOperationAsync(
			operation,
			diagnosticOperation,
			preferredOwner);
	}

	private async Task CompleteUpdateUiOperationAsync(
		Func<Task> operation,
		string diagnosticOperation,
		Window? preferredOwner)
	{
		try
		{
			await operation();
		}
		catch (ObjectDisposedException) when (IsQuitting)
		{
			// 從這裡開始由 App 關閉流程負責取消作業。
		}
		catch (Exception exception)
		{
			string reason = AppDiagnostics.GetUserFacingFailureReason(exception);
			_ = AppDiagnostics.TryWrite(
				diagnosticOperation,
				reason,
				exception);
			if (!CanShowInteractiveUpdateResult(IsQuitting))
			{
				return;
			}

			ShowShellMessage(
				UiText.Format("Shell.App030", reason),
				UiText.Get("Shell.App031"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning,
				preferredOwner: preferredOwner);
		}
	}

	private Task CheckForUpdatesManuallyAsync()
	{
		return CheckForUpdatesManuallyAsync(
			ManualUpdateCheckInvocationSurface.Inline);
	}

	private async Task CheckForUpdatesManuallyAsync(
		ManualUpdateCheckInvocationSurface invocationSurface)
	{
		UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
		if (coordinator is null)
		{
			return;
		}

		if (coordinator.ShouldShowAutomaticCheckNotice)
		{
			await coordinator.MarkAutomaticCheckNoticeShownAsync();
		}

		UpdateCheckExecutionResult result =
			await coordinator.CheckManuallyAsync();
		ShowManualUpdateCheckFeedbackIfNeeded(invocationSurface, result);
	}

	private void ShowManualUpdateCheckFeedbackIfNeeded(
		ManualUpdateCheckInvocationSurface invocationSurface,
		UpdateCheckExecutionResult result)
	{
		if (!CanShowInteractiveUpdateResult(IsQuitting))
		{
			return;
		}

		FloatingWidgetWindow? window = _floatingWidgetWindow;
		ManualUpdateCheckFeedback? feedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				invocationSurface,
				window?.IsVisible == true,
				window?.IsCollapsed == true,
				result);
		if (feedback is null)
		{
			return;
		}

		ShowShellMessage(
			feedback.Message,
			feedback.Title,
			MessageBoxButton.OK,
			feedback.IsWarning
				? MessageBoxImage.Warning
				: MessageBoxImage.Information);
	}

	private async Task SetAutomaticUpdateChecksEnabledAsync(bool isEnabled)
	{
		UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
		if (coordinator is null)
		{
			return;
		}

		AutoCheckPreferenceChangeResult result =
			await coordinator.SetAutoCheckEnabledAsync(isEnabled);
		RefreshUpdatePresentation();
		if (!result.IsPersisted)
		{
			ShowShellMessage(
				result.PersistenceWarning ??
					UiText.Get("Shell.App032"),
				UiText.Get("Shell.App033"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}

		if (result.IsEnabled)
		{
			BeginAutomaticUpdateNoticePresentation();
		}
	}

	private async Task SnoozeUpdateAsync()
	{
		if (_updateCheckCoordinator is UpdateCheckCoordinator coordinator)
		{
			SnoozeUpdateResult result = await coordinator.SnoozeAsync();
			if (!result.IsPersisted)
			{
				ShowShellMessage(
					result.PersistenceWarning ??
						UiText.Get("Shell.App034"),
					UiText.Get("Shell.App033"),
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
		}
	}

	private async Task ExecutePrimaryUpdateActionAsync()
	{
		UpdateUiPresentation? presentation = _updateUiPresentation;
		if (presentation is null)
		{
			return;
		}

		switch (presentation.PrimaryAction)
		{
			case UpdatePrimaryActionKind.None:
				return;
			case UpdatePrimaryActionKind.CheckNow:
				await CheckForUpdatesManuallyAsync();
				return;
			case UpdatePrimaryActionKind.OpenReleases:
				OpenUpdateReleases();
				return;
			case UpdatePrimaryActionKind.LaunchUpdater:
				await LaunchMaintenanceUpdaterAsync();
				return;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(presentation),
					presentation.PrimaryAction,
					UiText.Get("Shell.App035"));
		}
	}

	private async Task LaunchMaintenanceUpdaterAsync()
	{
		Task<MaintenanceUpdaterLaunchResult> launchTask =
			_maintenanceUpdaterLauncher.LaunchAsync(
				_appInstallationContext,
				_isUpdateShutdownChannelReady);
		RefreshUpdatePresentation();
		MaintenanceUpdaterLaunchResult result = await launchTask;
		if (!CanShowInteractiveUpdateResult(IsQuitting))
		{
			if ((result.Failure != MaintenanceUpdaterLaunchFailure.None) ||
				(result.ExitCode is int shutdownExitCode &&
					(shutdownExitCode != 0)))
			{
				_ = AppDiagnostics.TryWrite(
					"maintenance-updater-completion-during-shutdown",
					UiText.Get("Shell.App036") +
					$"failure={result.Failure}, exitCode={result.ExitCode?.ToString() ?? "none"}。",
					result.Exception);
			}

			return;
		}

		RefreshUpdatePresentation();

		if (result.Failure is
			MaintenanceUpdaterLaunchFailure.NotCanonicalInstallation or
			MaintenanceUpdaterLaunchFailure.ShutdownChannelUnavailable or
			MaintenanceUpdaterLaunchFailure.ExecutableUnavailable)
		{
			OfferOpenUpdateReleases(
				UiText.Get("Shell.App037"),
				UiText.Get("Shell.App038"));
			return;
		}

		if (result.Failure == MaintenanceUpdaterLaunchFailure.AlreadyRunning)
		{
			return;
		}

		if (result.Failure == MaintenanceUpdaterLaunchFailure.StartFailed)
		{
			_ = AppDiagnostics.TryWrite(
				"maintenance-updater-launch",
				UiText.Get("Shell.App039"),
				result.Exception);
			OfferOpenUpdateReleases(
				UiText.Get("Shell.App039"),
				UiText.Get("Shell.App038"));
			return;
		}

		if (!result.WasStarted || (result.ExitCode is not int exitCode))
		{
			throw new InvalidOperationException(
				UiText.Get("Shell.App040"),
				result.Exception);
		}

		if (exitCode != 0)
		{
			OfferOpenUpdateReleases(
				UiText.Format("Shell.App041", exitCode),
				UiText.Get("Shell.App042"),
				defaultResult: MessageBoxResult.Yes);
		}
	}

	private void OfferOpenUpdateReleases(
		string reason,
		string title,
		MessageBoxResult defaultResult = MessageBoxResult.No)
	{
		MessageBoxResult result = ShowShellMessage(
			UiText.Format("Shell.App043", reason),
			title,
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			defaultResult);
		if (result == MessageBoxResult.Yes)
		{
			OpenUpdateReleases();
		}
	}

	private static void OpenUpdateReleases()
	{
		AboutLinkLauncher.Open(AboutLink.Releases);
	}

	private UpdateShutdownOutcome TryReserveStructuredUpdateShutdown()
	{
		if (!Dispatcher.CheckAccess())
		{
			try
			{
				return Dispatcher.Invoke(
					TryReserveStructuredUpdateShutdown,
					DispatcherPriority.Send,
					CancellationToken.None,
					ActivationRequestTimeout);
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or TaskCanceledException or
					TimeoutException)
			{
				return UpdateShutdownOutcome.AlreadyShuttingDown;
			}
		}

		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
			IsQuitting || _isUpdateShutdownReserved)
		{
			return UpdateShutdownOutcome.AlreadyShuttingDown;
		}

		if (!_isUpdateShutdownReservationReady ||
			(_floatingWidgetWindow is not FloatingWidgetWindow window) ||
			(_dashboardViewModel is not DashboardViewModel viewModel) ||
			(_accountConnectionCoordinator is not
				AccountConnectionCoordinator coordinator))
		{
			return UpdateShutdownOutcome.Busy;
		}

		_isUpdateShutdownReserved = true;

		try
		{
			if (!window.TryReservePortableSettingsForUpdateShutdown() ||
				!viewModel.TryReserveAccountMutationsForUpdateShutdown() ||
				!coordinator.TryReserveShutdownForUpdate())
			{
				BeginRollbackUpdateShutdownComponentReservations();
				return UpdateShutdownOutcome.Busy;
			}
		}
		catch (Exception exception)
		{
			BeginRollbackUpdateShutdownComponentReservations();
			AppDiagnostics.TryWrite(
				"update-shutdown-reservation",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return UpdateShutdownOutcome.Busy;
		}

		return UpdateShutdownOutcome.Accepted;
	}

	private void RollbackStructuredUpdateShutdownReservation()
	{
		if (!Dispatcher.CheckAccess())
		{
			try
			{
				Dispatcher.BeginInvoke(
					BeginRollbackUpdateShutdownComponentReservations,
					DispatcherPriority.Normal);
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or TaskCanceledException)
			{
				AppDiagnostics.TryWrite(
					"update-shutdown-reservation-rollback",
					AppDiagnostics.GetUserFacingFailureReason(exception),
					exception);
			}

			return;
		}

		BeginRollbackUpdateShutdownComponentReservations();
	}

	private void BeginRollbackUpdateShutdownComponentReservations()
	{
		if (!_isUpdateShutdownReserved || IsQuitting)
		{
			return;
		}

		_ = ObserveUpdateShutdownComponentReservationRollbackAsync();
	}

	private async Task ObserveUpdateShutdownComponentReservationRollbackAsync()
	{
		try
		{
			await RollbackUpdateShutdownComponentReservationsAsync();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-reservation-rollback",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
	}

	private async Task RollbackUpdateShutdownComponentReservationsAsync()
	{
		try
		{
			if ((_dashboardViewModel is DashboardViewModel viewModel) &&
				!await viewModel
					.RollbackAccountMutationsUpdateShutdownReservationAsync())
			{
				return;
			}
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-account-mutation-rollback",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return;
		}

		try
		{
			_accountConnectionCoordinator?
				.RollbackShutdownForUpdateReservation();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-coordinator-rollback",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return;
		}

		try
		{
			_floatingWidgetWindow?
				.RollbackPortableSettingsUpdateShutdownReservation();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-portable-settings-rollback",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return;
		}

		_isUpdateShutdownReserved = false;
	}

	private void DispatchStructuredUpdateShutdown()
	{
		AcceptSingleInstanceActivation(
			SingleInstanceActivationRequest.ShutdownForUpdate);
	}

	private void AcceptSingleInstanceActivation(
		SingleInstanceActivationRequest request)
	{
		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			return;
		}

		if (!Dispatcher.CheckAccess())
		{
			try
			{
				Dispatcher.BeginInvoke(
					() => AcceptSingleInstanceActivation(request));
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or
					TaskCanceledException)
			{
				return;
			}

			return;
		}

		if (IsQuitting ||
			Dispatcher.HasShutdownStarted ||
			Dispatcher.HasShutdownFinished)
		{
			return;
		}

		if ((request == SingleInstanceActivationRequest.ShutdownForUpdate) &&
			!_isUpdateShutdownReserved)
		{
			return;
		}

		ProcessSingleInstanceActivationWork(
			_activationCoordinator.Enqueue(request));
	}

	private void ProcessSingleInstanceActivationWork(
		SingleInstanceActivationWork work)
	{
		if (work.ShouldShutdownForUpdate)
		{
			StartShutdownOperation(
				CompleteUpdateShutdownAsync,
				"shutdown-for-update",
				isUpdateShutdown: true);
			return;
		}

		if (work.ShouldShowFloatingWidget)
		{
			ShowFloatingWidget();
		}

		if (work.ShouldEnsureAntigravityAccount)
		{
			_ = RunEnsureAntigravityAccountAsync();
		}
	}

	private async Task RunEnsureAntigravityAccountAsync()
	{
		try
		{
			SingleInstanceActivationWork pendingWork =
				await _activationCoordinator.RunEnsureAntigravityAccountAsync(
					async () =>
					{
						if (_dashboardViewModel is not null)
						{
							await EnsureAntigravityAccountAsync(
								_dashboardViewModel,
								new[]
								{
									AntigravityMachineSetupLaunchArguments
										.EnsureDashboardAccount
								});
						}
					},
					ReportUnexpectedAntigravityAccountEnsureFailure);
			ProcessSingleInstanceActivationWork(pendingWork);
		}
		catch (Exception exception)
		{
			ReportUnexpectedAntigravityAccountEnsureFailure(exception);

			try
			{
				ProcessSingleInstanceActivationWork(
				_activationCoordinator
					.CompleteEnsureAntigravityAccount());
			}
			catch (Exception recoveryException)
			{
				AppDiagnostics.TryWrite(
					"antigravity-setup-account-activation-recovery",
					AppDiagnostics.GetUserFacingFailureReason(
						recoveryException),
					recoveryException);
			}
		}
	}

	private void ReportUnexpectedAntigravityAccountEnsureFailure(
		Exception exception)
	{
		AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
			"antigravity-setup-account-activation",
			AppDiagnostics.GetUserFacingFailureReason(exception),
			exception);
		AntigravityAccountEnsureFailureNotification notification =
			CreateUnexpectedAntigravityAccountEnsureFailureNotification(
				diagnostic);

		try
		{
			ShowShellMessage(
				notification.Message,
				notification.Title,
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
		catch (Exception notificationException)
		{
			AppDiagnostics.TryWrite(
				"antigravity-setup-account-activation-notification",
				AppDiagnostics.GetUserFacingFailureReason(
					notificationException),
				notificationException);
		}
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr windowHandle);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(
		string applicationUserModelId);

	public void ShowFloatingWidget()
	{
		if (_floatingWidgetWindow is null)
		{
			return;
		}

		_floatingWidgetWindow.ShowExpandedNearWorkArea();
		_floatingWidgetWindow.Activate();
		_floatingWidgetWindow.Focus();
		IntPtr windowHandle = new WindowInteropHelper(
			_floatingWidgetWindow).Handle;

		if (windowHandle != IntPtr.Zero)
		{
			_ = SetForegroundWindow(windowHandle);
		}
	}

	public void ToggleFloatingWidget()
	{
		if (_floatingWidgetWindow is null)
		{
			return;
		}

		if (_floatingWidgetWindow.IsVisible)
		{
			_floatingWidgetWindow.Hide();
			return;
		}

		ShowFloatingWidget();
	}

	public void ToggleFloatingWidgetTopmost()
	{
		_floatingWidgetWindow?.ToggleTopmost();
	}

	public void Quit()
	{
		if (!CanStartInteractiveShutdown(_isUpdateShutdownReserved))
		{
			return;
		}

		StartShutdownOperation(QuitAsync, "quit");
	}

	private async Task QuitAsync()
	{
		if (IsQuitting ||
			!CanStartInteractiveShutdown(_isUpdateShutdownReserved))
		{
			return;
		}

		if (!ConfirmActiveAntigravitySetupCancellation(isRestart: false))
		{
			return;
		}

		await CompleteShutdownAsync();
	}

	public void Restart()
	{
		if (!CanStartInteractiveShutdown(_isUpdateShutdownReserved))
		{
			return;
		}

		StartShutdownOperation(RestartAsync, "restart");
	}

	private async Task RestartAsync()
	{
		if (IsQuitting ||
			!CanStartInteractiveShutdown(_isUpdateShutdownReserved))
		{
			return;
		}

		string executablePath = Environment.ProcessPath ?? string.Empty;

		if ((executablePath.Length == 0) ||
			!System.IO.File.Exists(executablePath))
		{
			ShowShellMessage(
				UiText.Get("Shell.App044"),
				UiText.Get("Shell.App002"),
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			return;
		}

		if (!ConfirmActiveAntigravitySetupCancellation(isRestart: true))
		{
			return;
		}

		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory
		};
		startInfo.ArgumentList.Add(RestartAfterProcessArgument);
		startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

		try
		{
			using Process? restartProcess = Process.Start(startInfo);

			if (restartProcess is null)
			{
				throw new InvalidOperationException(UiText.Get("Shell.App045"));
			}
		}
		catch (Exception)
		{
			ShowShellMessage(
				UiText.Get("Shell.App046"),
				UiText.Get("Shell.App002"),
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			return;
		}

		await CompleteShutdownAsync();
	}

	internal static string CreateActiveAntigravitySetupCancellationMessage(
		bool isRestart)
	{
		string action = isRestart ? UiText.Get("Shell.App047") : UiText.Get("Shell.App048");
		return
			UiText.Format("Shell.App049", action, action);
	}

	private bool ConfirmActiveAntigravitySetupCancellation(bool isRestart)
	{
		if (_accountConnectionCoordinator?.IsAntigravitySetupInProgress != true)
		{
			return true;
		}

		return ShowShellMessage(
			CreateActiveAntigravitySetupCancellationMessage(isRestart),
			UiText.Get("Shell.App050"),
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No) == MessageBoxResult.Yes;
	}

	private MessageBoxResult ShowShellMessage(
		string message,
		string title,
		MessageBoxButton buttons,
		MessageBoxImage image,
		MessageBoxResult defaultResult = MessageBoxResult.None,
		Window? preferredOwner = null)
	{
		Window? messageOwner = preferredOwner?.IsVisible == true
			? preferredOwner
			: _floatingWidgetWindow;
		if (messageOwner?.IsVisible == true)
		{
			return WpfMessageBox.Show(
				messageOwner,
				message,
				title,
				buttons,
				image,
				defaultResult);
		}

		return WpfMessageBox.Show(
			message,
			title,
			buttons,
			image,
			defaultResult);
	}

	private async Task CompleteShutdownAsync()
	{
		await CompleteShutdownAsync(requireConfirmedDrain: false);
	}

	private async Task CompleteUpdateShutdownAsync()
	{
		await CompleteShutdownAsync(requireConfirmedDrain: true);
	}

	private async Task CompleteShutdownAsync(bool requireConfirmedDrain)
	{
		if (IsQuitting ||
			(!requireConfirmedDrain && _isUpdateShutdownReserved))
		{
			return;
		}

		_accountConnectionCoordinator?.ReserveShutdown();
		IsQuitting = true;
		StopActivationListener();
		_refreshTimer?.Stop();
		await StopUpdateServicesAsync();
		bool wasStartupRecoveryDrained =
			await CancelAndDrainPostStartupRecoveryForShutdownAsync();
		bool wereUsageRefreshesDrained =
			await StopAndDrainUsageRefreshesForShutdownAsync();
		bool wereAccountConnectionsDrained =
			await CancelAccountConnectionsForShutdownAsync();
		bool wereAccountMutationsLocked = !requireConfirmedDrain ||
			(wasStartupRecoveryDrained &&
			wereUsageRefreshesDrained &&
			wereAccountConnectionsDrained &&
			await LockAccountMutationsForUpdateShutdownAsync());
		bool didDeferUpdateShutdown = requireConfirmedDrain &&
			(!wasStartupRecoveryDrained ||
			!wereUsageRefreshesDrained ||
			!wereAccountConnectionsDrained ||
			!wereAccountMutationsLocked);

		if (didDeferUpdateShutdown)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-deferred",
				UiText.Get("Shell.App051"));

			do
			{
				await Task.Delay(UpdateShutdownRecoveryRetryInterval);
				wasStartupRecoveryDrained =
					await CancelAndDrainPostStartupRecoveryForShutdownAsync();
				wereUsageRefreshesDrained =
					await StopAndDrainUsageRefreshesForShutdownAsync();
				wereAccountConnectionsDrained =
					await CancelAccountConnectionsForShutdownAsync();
				wereAccountMutationsLocked =
					wasStartupRecoveryDrained &&
					wereUsageRefreshesDrained &&
					wereAccountConnectionsDrained &&
					await LockAccountMutationsForUpdateShutdownAsync();
			}
			while (!wasStartupRecoveryDrained ||
				!wereUsageRefreshesDrained ||
				!wereAccountConnectionsDrained ||
				!wereAccountMutationsLocked);
		}

		await SaveShellPreferencesAsync();

		if (didDeferUpdateShutdown)
		{
			TryStartDeferredUpdateRecoveryRestart();
		}

		_notifyIcon?.Dispose();
		_floatingWidgetWindow?.Close();
		Shutdown();
	}

	private void StopUpdateServicesOnExit()
	{
		try
		{
			StopUpdateServicesAsync().GetAwaiter().GetResult();
		}
		catch (Exception exception)
		{
			_ = AppDiagnostics.TryWrite(
				"update-check-shutdown",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
	}

	private async Task StopUpdateServicesAsync()
	{
		DispatcherTimer? timer = _updateCheckTimer;
		_updateCheckTimer = null;
		if (timer is not null)
		{
			try
			{
				timer.Stop();
				timer.Tick -= UpdateCheckTimer_Tick;
			}
			catch (Exception exception)
			{
				ReportUpdateServiceShutdownFailure(
					"update-check-timer-shutdown",
					exception);
			}
		}

		if (_isUpdatePowerModeSubscribed)
		{
			_isUpdatePowerModeSubscribed = false;
			try
			{
				Microsoft.Win32.SystemEvents.PowerModeChanged -=
					SystemEvents_PowerModeChanged;
			}
			catch (Exception exception)
			{
				ReportUpdateServiceShutdownFailure(
					"update-resume-monitor-shutdown",
					exception);
			}
		}

		UpdateCheckCoordinator? coordinator = _updateCheckCoordinator;
		_updateCheckCoordinator = null;
		if (coordinator is not null)
		{
			try
			{
				coordinator.StateChanged -= UpdateCheckCoordinator_StateChanged;
				await coordinator.DisposeAsync();
			}
			catch (Exception exception)
			{
				ReportUpdateServiceShutdownFailure(
					"update-check-coordinator-shutdown",
					exception);
			}
		}

		HttpClient? httpClient = _updateHttpClient;
		_updateHttpClient = null;
		if (httpClient is not null)
		{
			try
			{
				httpClient.Dispose();
			}
			catch (Exception exception)
			{
				ReportUpdateServiceShutdownFailure(
					"update-http-client-shutdown",
					exception);
			}
		}
	}

	private static void ReportUpdateServiceShutdownFailure(
		string operation,
		Exception exception)
	{
		_ = AppDiagnostics.TryWrite(
			operation,
			AppDiagnostics.GetUserFacingFailureReason(exception),
			exception);
	}

	private async Task<bool> LockAccountMutationsForUpdateShutdownAsync()
	{
		DashboardViewModel? viewModel = _dashboardViewModel;

		if (viewModel is null)
		{
			return false;
		}

		try
		{
			return await viewModel.LockAccountMutationsForUpdateShutdownAsync(
				UpdateMutationShutdownTimeout);
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"account-mutation-update-shutdown",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return false;
		}
	}

	private void TryStartDeferredUpdateRecoveryRestart()
	{
		string executablePath = Environment.ProcessPath ?? string.Empty;

		if ((executablePath.Length == 0) || !File.Exists(executablePath))
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-restart",
				UiText.Get("Shell.App052"));
			return;
		}

		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory
		};
		startInfo.ArgumentList.Add(RestartAfterProcessArgument);
		startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

		try
		{
			using Process? restartProcess = Process.Start(startInfo);

			if (restartProcess is null)
			{
				throw new InvalidOperationException(UiText.Get("Shell.App045"));
			}
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"update-shutdown-restart",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
	}

	private void StartShutdownOperation(
		Func<Task> operation,
		string diagnosticOperation,
		bool isUpdateShutdown = false)
	{
		if (!_shutdownOperationScheduler.TryBeginOperation(
			isUpdateShutdown,
			IsQuitting))
		{
			return;
		}

		_ = ObserveShutdownOperationAsync(
			operation,
			diagnosticOperation,
			shutdownOnFailure: !isUpdateShutdown);
	}

	private async Task ObserveShutdownOperationAsync(
		Func<Task> operation,
		string diagnosticOperation,
		bool shutdownOnFailure = true)
	{
		try
		{
			await operation();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				diagnosticOperation,
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);

			if (IsQuitting && shutdownOnFailure)
			{
				try
				{
					Shutdown(-1);
				}
				catch (Exception shutdownException)
				{
					AppDiagnostics.TryWrite(
						$"{diagnosticOperation}-forced-shutdown",
						AppDiagnostics.GetUserFacingFailureReason(
							shutdownException),
						shutdownException);
				}
			}
		}
		finally
		{
			if (_shutdownOperationScheduler
				.CompleteOperationAndTryBeginPendingUpdate(IsQuitting))
			{
				_ = ObserveShutdownOperationAsync(
					CompleteUpdateShutdownAsync,
					"shutdown-for-update",
					shutdownOnFailure: false);
			}
		}
	}

	private async Task<bool> CancelAndDrainPostStartupRecoveryForShutdownAsync()
	{
		if (!_startupRecoverySource.IsCancellationRequested)
		{
			_startupRecoverySource.Cancel();
		}

		Task recoveryTask = Task.WhenAll(
			_postStartupRecoveryTask,
			_dashboardPreferencesRecoveryTask);

		try
		{
			await recoveryTask.WaitAsync(
				PostStartupRecoveryShutdownTimeout);
			return true;
		}
		catch (TimeoutException)
		{
			AppDiagnostics.TryWrite(
				"post-startup-recovery-shutdown-timeout",
				UiText.Get("Shell.App053"));
			return false;
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"post-startup-recovery-shutdown",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return recoveryTask.IsCompleted;
		}
	}

	private async Task<bool> StopAndDrainUsageRefreshesForShutdownAsync()
	{
		DashboardViewModel? viewModel = _dashboardViewModel;

		if (viewModel is null)
		{
			return true;
		}

		try
		{
			bool wasDrained = await viewModel.StopAndDrainRefreshingAsync(
				UsageRefreshShutdownTimeout);

			if (!wasDrained)
			{
				AppDiagnostics.TryWrite(
					"usage-refresh-shutdown-timeout",
					UiText.Get("Shell.App054"));
			}

			return wasDrained;
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"usage-refresh-shutdown",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return false;
		}
	}

	private async Task<bool> CancelAccountConnectionsForShutdownAsync()
	{
		AccountConnectionCoordinator? coordinator = _accountConnectionCoordinator;

		if (coordinator is null)
		{
			return true;
		}

		bool wasDrained = false;
		try
		{
			wasDrained = await coordinator
				.CancelAndDrainAccountConnectionsAsync(
					AccountConnectionShutdownTimeout);

			if (!wasDrained)
			{
				AppDiagnostics.TryWrite(
					"account-connection-shutdown-timeout",
					UiText.Get("Shell.App055"));
			}

			return wasDrained;
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"account-connection-shutdown",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return false;
		}
		finally
		{
			if (wasDrained)
			{
				coordinator.Dispose();

				if (ReferenceEquals(_accountConnectionCoordinator, coordinator))
				{
					_accountConnectionCoordinator = null;
				}
			}
		}
	}

	private void StopActivationListener()
	{
		_updateShutdownChannel?.Dispose();
		_updateShutdownChannel = null;
		_activationChannel?.Dispose();
		_activationChannel = null;
	}

	private static async Task<bool> WaitForRestartParentAsync(string[] arguments)
	{
		if ((arguments.Length == 0) ||
			!string.Equals(
				arguments[0],
				RestartAfterProcessArgument,
				StringComparison.Ordinal))
		{
			return true;
		}

		if ((arguments.Length != 2) ||
			!int.TryParse(arguments[1], out int parentProcessId) ||
			(parentProcessId <= 0) ||
			(parentProcessId == Environment.ProcessId))
		{
			return false;
		}

		try
		{
			using Process parentProcess = Process.GetProcessById(parentProcessId);
			await parentProcess.WaitForExitAsync().WaitAsync(RestartWaitTimeout);
			return true;
		}
		catch (ArgumentException)
		{
			// The old process already exited before the replacement could inspect it.
			return true;
		}
		catch (InvalidOperationException)
		{
			// The process exited between lookup and waiting.
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
	}

	private static Icon? LoadApplicationIcon()
	{
		string executablePath = Environment.ProcessPath ?? string.Empty;

		if ((executablePath.Length == 0) ||
			!System.IO.File.Exists(executablePath))
		{
			return null;
		}

		try
		{
			return Icon.ExtractAssociatedIcon(executablePath);
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal static Uri GetPaletteUri(
		AppTheme theme,
		bool isHighContrast)
	{
		if (isHighContrast)
		{
			return HighContrastPaletteUri;
		}

		return theme switch
		{
			AppTheme.Midnight => MidnightPaletteUri,
			AppTheme.Light => LightPaletteUri,
			AppTheme.Sakura => SakuraPaletteUri,
			_ => DefaultPaletteUri
		};
	}

	internal static void UpdatePaletteResources(
		ResourceDictionary currentPalette,
		ResourceDictionary targetPalette)
	{
		ArgumentNullException.ThrowIfNull(currentPalette);
		ArgumentNullException.ThrowIfNull(targetPalette);

		object[] keys = targetPalette.Keys.Cast<object>().ToArray();

		foreach (object key in keys)
		{
			object targetValue = targetPalette[key];

			if (targetValue is not SolidColorBrush)
			{
				currentPalette[key] = targetValue;
			}
		}

		foreach (object key in keys)
		{
			if (targetPalette[key] is not SolidColorBrush targetBrush)
			{
				continue;
			}

			WpfColor targetColor = GetTargetBrushColor(
				targetPalette,
				key,
				targetBrush);

			if ((currentPalette[key] is SolidColorBrush currentBrush) &&
				!currentBrush.IsFrozen)
			{
				currentBrush.Color = targetColor;
				currentBrush.Opacity = targetBrush.Opacity;
				continue;
			}

			SolidColorBrush replacementBrush =
				targetBrush.CloneCurrentValue();
			replacementBrush.Color = targetColor;
			replacementBrush.Opacity = targetBrush.Opacity;
			currentPalette[key] = replacementBrush;
		}
	}

	private static WpfColor GetTargetBrushColor(
		ResourceDictionary targetPalette,
		object brushKey,
		SolidColorBrush targetBrush)
	{
		if (brushKey is string resourceName &&
			resourceName.EndsWith(
				"Brush",
				StringComparison.Ordinal))
		{
			string colorResourceName =
				$"{resourceName[..^"Brush".Length]}Color";

			if (targetPalette[colorResourceName] is WpfColor targetColor)
			{
				return targetColor;
			}
		}

		return targetBrush.Color;
	}

	internal static string CreateActivationFailureMessage(
		SingleInstanceActivationFailure failure,
		AppDiagnosticWriteResult diagnostic)
	{
		return
			UiText.Get("Shell.App056") +
			UiText.Format("Shell.App057", GetActivationFailureReason(failure)) +
			UiText.Get("Shell.App058") +
			CreateDiagnosticGuidance(diagnostic);
	}

	internal static string CreateStartupFailureMessage(
		Exception exception,
		AppDiagnosticWriteResult diagnostic)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return
			UiText.Get("Shell.App059") +
			UiText.Format("Shell.App057", AppDiagnostics.GetUserFacingFailureReason(exception)) +
			UiText.Get("Shell.App060") +
			CreateDiagnosticGuidance(diagnostic);
	}

	internal static string GetShellPreferencesSaveFailureReason(
		Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		for (Exception? current = exception;
			current is not null;
			current = current.InnerException)
		{
			if (current is UnauthorizedAccessException or SecurityException)
			{
				return UiText.Get("Shell.App061");
			}
		}

		for (Exception? current = exception;
			current is not null;
			current = current.InnerException)
		{
			if (current is DashboardPreferencesSaveBlockedException blockedException)
			{
				return blockedException.Reason switch
				{
					DashboardPreferencesSaveBlockReason.NewerSchema =>
						UiText.Get("Shell.App062"),
					DashboardPreferencesSaveBlockReason.DocumentTooLarge =>
						UiText.Get("Shell.App063"),
					DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable =>
						UiText.Get("Shell.App064"),
					DashboardPreferencesSaveBlockReason.UnsafePath =>
						UiText.Get("Shell.App065"),
					_ => UiText.Get("Shell.App066")
				};
			}
		}

		return AppDiagnostics.GetUserFacingFailureReason(
			exception,
			UiText.Get("Shell.App066"));
	}

	internal static string CreateShellPreferencesSaveFailureNotificationText(
		string failureReason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
		return UiText.Format("Shell.App067", failureReason);
	}

	private void ApplyThemePalette()
	{
		Uri targetPaletteUri = GetPaletteUri(
			_selectedTheme,
			SystemParameters.HighContrast);
		IList<ResourceDictionary> dictionaries = Resources.MergedDictionaries;
		int paletteIndex = -1;

		for (int index = 0; index < dictionaries.Count; index++)
		{
			string source = dictionaries[index].Source?.OriginalString ??
				string.Empty;

			if (source.EndsWith(
				"Palette.xaml",
				StringComparison.OrdinalIgnoreCase))
			{
				paletteIndex = index;
				break;
			}
		}

		ResourceDictionary palette = new()
		{
			Source = targetPaletteUri
		};

		if (paletteIndex >= 0)
		{
			UpdatePaletteResources(dictionaries[paletteIndex], palette);
		}
		else
		{
			dictionaries.Insert(0, palette);
		}

		UpdateThemeAppIcon(paletteIndex >= 0
			? dictionaries[paletteIndex]
			: palette);
	}

	private void UpdateThemeAppIcon(ResourceDictionary palette)
	{
		try
		{
			if (Resources["ThemeLogoStyle"] is not Style logoStyle)
			{
				throw new InvalidOperationException(
					UiText.Get("Shell.App068"));
			}

			ThemeAppIcon? previous = _themeAppIcon;
			ImageSource previousWindowIcon =
				(ImageSource)Resources["ThemeWindowIcon"];
			Icon previousTrayIcon = _notifyIcon?.Icon ??
				previous?.TrayIcon ?? _applicationIcon ?? SystemIcons.Application;
			ThemeAppIcon replacement = ThemeAppIcon.Create(logoStyle, palette);
			bool keepReplacement = false;
			try
			{
				if (_notifyIcon is not null)
				{
					_notifyIcon.Icon = replacement.TrayIcon;
				}
				Resources["ThemeWindowIcon"] = replacement.WindowIcon;
				keepReplacement = true;
			}
			catch (Exception updateException)
			{
				keepReplacement = RestoreThemeAppIcon(
					previousTrayIcon, previousWindowIcon, replacement);
				throw new InvalidOperationException(
					UiText.Get("Shell.App069"),
					updateException);
			}
			finally
			{
				if (keepReplacement)
				{
					_themeAppIcon = replacement;
					previous?.Dispose();
				}
				else
				{
					replacement.Dispose();
				}
			}
		}
		catch (Exception exception)
		{
			_ = AppDiagnostics.TryWrite(
				"theme-app-icon",
				UiText.Get("Shell.App070"),
				exception);
		}
	}

	private bool RestoreThemeAppIcon(
		Icon previousTrayIcon,
		ImageSource previousWindowIcon,
		ThemeAppIcon replacement)
	{
		if (_notifyIcon is not null)
		{
			try
			{
				_notifyIcon.Icon = previousTrayIcon;
			}
			catch (Exception rollbackException)
			{
				_ = AppDiagnostics.TryWrite(
					"theme-app-icon-rollback",
					UiText.Get("Shell.App071"),
					rollbackException);
			}
		}

		bool keepReplacement = ReferenceEquals(
			_notifyIcon?.Icon, replacement.TrayIcon);
		try
		{
			Resources["ThemeWindowIcon"] = keepReplacement
				? replacement.WindowIcon
				: previousWindowIcon;
		}
		catch (Exception rollbackException)
		{
			_ = AppDiagnostics.TryWrite(
				"theme-app-icon-rollback",
				UiText.Get("Shell.App072"),
				rollbackException);
		}

		return keepReplacement;
	}

	internal static bool ShouldShowFloatingWidgetOnStartup(
		DashboardShellPreferences preferences,
		bool shouldForceShow = false)
	{
		ArgumentNullException.ThrowIfNull(preferences);
		return shouldForceShow ||
			preferences.IsWidgetVisible ||
			(preferences.StartupSurface != DashboardStartupSurface.Tray);
	}

	private void FloatingWidgetWindow_PreferencesChanged(
		object? sender,
		EventArgs e)
	{
		if ((_floatingWidgetWindow is not null) &&
			(_selectedTheme != _floatingWidgetWindow.Theme))
		{
			_selectedTheme = _floatingWidgetWindow.Theme;
			ApplyThemePalette();
		}

		if ((_floatingWidgetWindow is not null)
			&& (_selectedLanguage != _floatingWidgetWindow.SelectedLanguage))
		{
			ApplyLanguage(_floatingWidgetWindow.SelectedLanguage);
		}

		UpdateWidgetTopmostMenuItem();
		RefreshUpdatePresentation();
		BeginAutomaticUpdateNoticePresentation();
		BeginUpdateBalloonAttempt();
		QueueShellPreferencesSave();
	}

	internal void ApplyLanguage(AppLanguage language)
	{
		_selectedLanguage = language;
		UiText.SetLanguage(language);
		foreach (var (key, value) in UiText.GetResources())
		{
			Resources[key] = value;
		}

		_dashboardViewModel?.RefreshLocalizedPresentation();
		_floatingWidgetWindow?.RefreshLocalizedPresentation();
		if (_notifyIcon?.ContextMenuStrip is FormsContextMenuStrip menu)
		{
			RefreshTrayLanguage(menu.Items);
			UpdateWidgetVisibilityMenuItem();
			UpdateLogonStartupMenuItem();
			UpdateLanguageMenuItems();
		}

		if (_updateCheckCoordinator is not null)
		{
			RefreshUpdatePresentation();
		}
	}

	private static void RefreshTrayLanguage(System.Windows.Forms.ToolStripItemCollection items)
	{
		foreach (System.Windows.Forms.ToolStripItem item in items)
		{
			item.Text = UiText.Translate(item.Text ?? string.Empty);
			item.ToolTipText = UiText.Translate(item.ToolTipText ?? string.Empty);
			if (item is FormsToolStripMenuItem menuItem)
			{
				RefreshTrayLanguage(menuItem.DropDownItems);
			}
		}
	}

	private void UpdateLanguageMenuItems()
	{
		if (_englishLanguageMenuItem is not null)
		{
			_englishLanguageMenuItem.Checked = _selectedLanguage == AppLanguage.English;
			_englishLanguageMenuItem.Enabled = _dashboardViewModel?.CanChangeUsageDisplayMode == true;
		}

		if (_traditionalChineseLanguageMenuItem is not null)
		{
			_traditionalChineseLanguageMenuItem.Checked =
				_selectedLanguage == AppLanguage.TraditionalChinese;
			_traditionalChineseLanguageMenuItem.Enabled = _dashboardViewModel?.CanChangeUsageDisplayMode == true;
		}
	}

	private void ShellWindow_ActivityChanged(object? sender, EventArgs e)
	{
		RefreshUpdatePresentation();
		BeginUpdateBalloonAttempt();
	}

	private void BeginAutomaticUpdateNoticePresentation()
	{
		if (IsQuitting ||
			_isUpdateAutomaticNoticePresentationRunning ||
			(_updateCheckCoordinator is null))
		{
			return;
		}

		_isUpdateAutomaticNoticePresentationRunning = true;
		try
		{
			PresentAutomaticUpdateNoticeIfNeeded();
		}
		catch (ObjectDisposedException) when (IsQuitting)
		{
			// 從這裡開始由 App 關閉流程負責釋放更新檢查資源。
		}
		catch (Exception exception)
		{
			_ = AppDiagnostics.TryWrite(
				"automatic-update-check-notice",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
		}
		finally
		{
			_isUpdateAutomaticNoticePresentationRunning = false;
		}
	}

	private void QueueShellPreferencesSave()
	{
		if (_floatingWidgetWindow is not null)
		{
			Volatile.Write(
				ref _latestDashboardShellPreferences,
				_floatingWidgetWindow.CreatePreferences(
					_floatingWidgetWindow.IsVisible));
		}

		if (_isRestoringShellPreferences || IsQuitting)
		{
			return;
		}

		_ = SaveShellPreferencesAsync();
	}

	private void RestoreStartupSurface(
		DashboardShellPreferences preferences,
		AppLaunchIntent launchIntent)
	{
		if (_floatingWidgetWindow is null)
		{
			return;
		}

		bool shouldForceShow =
			launchIntent == AppLaunchIntent.EnsureAntigravityAccount;

		if (!ShouldShowFloatingWidgetOnStartup(preferences, shouldForceShow))
		{
			return;
		}

		if (shouldForceShow)
		{
			_floatingWidgetWindow.ShowExpandedNearWorkArea();
			return;
		}

		_floatingWidgetWindow.ShowNearWorkArea();
	}

	private async Task SaveShellPreferencesAsync()
	{
		if ((_widgetPreferencesStore is null) ||
			(_floatingWidgetWindow is null))
		{
			return;
		}

		await _shellPreferencesSaveGate.WaitAsync();
		bool shouldQueueDashboardPreferencesRecovery = false;

		try
		{
			DashboardShellPreferences preferences =
				_floatingWidgetWindow.CreatePreferences(
					_floatingWidgetWindow.IsVisible);
			Volatile.Write(ref _latestDashboardShellPreferences, preferences);
			bool hadReportedFailure = _hasReportedShellPreferencesSaveFailure;
			await _widgetPreferencesStore.SaveDashboardShellPreferencesAsync(
				preferences);
			_hasReportedShellPreferencesSaveFailure = false;

			if (hadReportedFailure)
			{
				ReportShellPreferencesSaveRecovered();
			}
		}
		catch (Exception exception)
		{
			// Preferences are best effort; a future schema is never overwritten.
			// Record only the first failure until a later save succeeds.
			if (!_hasReportedShellPreferencesSaveFailure)
			{
				string failureReason =
					GetShellPreferencesSaveFailureReason(exception);
				AppDiagnostics.TryWrite(
					"shell-preferences-save",
					failureReason,
					exception);
				_hasReportedShellPreferencesSaveFailure = true;
				ReportShellPreferencesSaveFailure(failureReason);
			}
		}
		finally
		{
			shouldQueueDashboardPreferencesRecovery =
				_dashboardPreferencesRecoveryStore?.IsRecoveryActive == true;
			_shellPreferencesSaveGate.Release();
		}

		if (shouldQueueDashboardPreferencesRecovery)
		{
			QueueDashboardPreferencesRecovery();
		}
	}

	private void ReportShellPreferencesSaveFailure(string failureReason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

		if (IsQuitting)
		{
			return;
		}

		if ((_floatingWidgetWindow is not null) &&
			_floatingWidgetWindow.IsVisible &&
			!_floatingWidgetWindow.IsCollapsed)
		{
			_floatingWidgetWindow.ReportShellPreferencesSaveFailure(failureReason);
			return;
		}

		try
		{
			_notifyIcon?.ShowBalloonTip(
				5000,
				UiText.Get("Shell.App073"),
				CreateShellPreferencesSaveFailureNotificationText(failureReason),
				System.Windows.Forms.ToolTipIcon.Warning);
		}
		catch (Exception)
		{
			// Failure reporting must not interrupt the running application.
		}
	}

	private void ReportShellPreferencesSaveRecovered()
	{
		if ((_floatingWidgetWindow is not null) &&
			_floatingWidgetWindow.IsVisible &&
			!_floatingWidgetWindow.IsCollapsed)
		{
			_floatingWidgetWindow.ReportShellPreferencesSaveRecovered();
		}
	}

	private static string CreateDiagnosticGuidance(
		AppDiagnosticWriteResult diagnostic)
	{
		if (diagnostic.WasWritten &&
			!string.IsNullOrWhiteSpace(diagnostic.FilePath))
		{
			return UiText.Format("Shell.App074", diagnostic.FilePath);
		}

		return UiText.Get("Shell.App075");
	}

	private static string GetActivationFailureReason(
		SingleInstanceActivationFailure failure)
	{
		return failure switch
		{
			SingleInstanceActivationFailure.TimedOut =>
				UiText.Get("Shell.App076"),
			SingleInstanceActivationFailure.AccessDenied =>
				UiText.Get("Shell.App077"),
			SingleInstanceActivationFailure.Unavailable =>
				UiText.Get("Shell.App078"),
			_ => UiText.Get("Shell.App079")
		};
	}

	private void ShellWindow_IsVisibleChanged(
		object sender,
		DependencyPropertyChangedEventArgs e)
	{
		UpdateWidgetVisibilityMenuItem();
		RefreshUpdatePresentation();
		BeginAutomaticUpdateNoticePresentation();
		BeginUpdateBalloonAttempt();

		QueueShellPreferencesSave();
	}

	private void SystemParameters_StaticPropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (string.Equals(
			e.PropertyName,
			nameof(SystemParameters.HighContrast),
			StringComparison.Ordinal))
		{
			ApplyThemePalette();
		}
	}

	private void UpdateWidgetVisibilityMenuItem()
	{
		if (_widgetVisibilityMenuItem is null)
		{
			return;
		}

		bool isVisible = IsFloatingWidgetVisible;
		_widgetVisibilityMenuItem.Checked = isVisible;
		_widgetVisibilityMenuItem.Text = isVisible
			? UiText.Get("Shell.App080")
			: UiText.Get("Shell.App081");
	}

	private void UpdateWidgetTopmostMenuItem()
	{
		if (_widgetTopmostMenuItem is null)
		{
			return;
		}

		_widgetTopmostMenuItem.Checked =
			_floatingWidgetWindow?.Topmost == true;
	}

	private void UpdatePortableSettingsMenuItems()
	{
		UpdateLanguageMenuItems();
		if (_exportPortableSettingsMenuItem is not null)
		{
			_exportPortableSettingsMenuItem.Enabled =
				_dashboardViewModel?.CanStartAccountManagement == true;
		}

		if (_undoPortableSettingsImportMenuItem is not null)
		{
			_undoPortableSettingsImportMenuItem.Enabled =
				_dashboardViewModel?.CanUndoLastPortableSettingsImport == true;
		}
	}

	internal static LogonStartupMenuPresentation
		CreateLogonStartupMenuPresentation(
			bool canManageRegistration,
			LogonStartupRegistrationState? state,
			bool canCreateExpectedCommand = true)
	{
		if (!canManageRegistration)
		{
			return new LogonStartupMenuPresentation(
				UiText.Get("Shell.App082"),
				IsChecked: false,
				IsEnabled: false);
		}

		if (!canCreateExpectedCommand)
		{
			return new LogonStartupMenuPresentation(
				UiText.Get("Shell.App083"),
				IsChecked: false,
				IsEnabled: false);
		}

		return state switch
		{
			LogonStartupRegistrationState.Absent =>
				new LogonStartupMenuPresentation(
					UiText.Get("Shell.App084"),
					IsChecked: false,
					IsEnabled: true),
			LogonStartupRegistrationState.ExactMatch =>
				new LogonStartupMenuPresentation(
					UiText.Get("Shell.App085"),
					IsChecked: true,
					IsEnabled: true),
			LogonStartupRegistrationState.Conflict =>
				new LogonStartupMenuPresentation(
					UiText.Get("Shell.App086"),
					IsChecked: false,
					IsEnabled: true),
			_ => new LogonStartupMenuPresentation(
				UiText.Get("Shell.App087"),
				IsChecked: false,
				IsEnabled: false)
		};
	}

	private void UpdateLogonStartupMenuItem()
	{
		if (_logonStartupMenuItem is null)
		{
			return;
		}

		LogonStartupMenuPresentation presentation;

		try
		{
			presentation = ReadLogonStartupMenuPresentation();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"logon-startup-registration-query",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			presentation = CreateLogonStartupMenuPresentation(
				canManageRegistration: true,
				state: null);
		}

		_logonStartupMenuItem.Text = presentation.Text;
		_logonStartupMenuItem.Checked = presentation.IsChecked;
		_logonStartupMenuItem.Enabled = presentation.IsEnabled;
	}

	private LogonStartupMenuPresentation ReadLogonStartupMenuPresentation()
	{
		bool canManageRegistration =
			WindowsLogonStartupRegistrationContract
				.IsCanonicalExecutablePath(Environment.ProcessPath);

		if (!canManageRegistration)
		{
			return CreateLogonStartupMenuPresentation(
				canManageRegistration: false,
				state: null);
		}

		string expectedCommand;

		try
		{
			expectedCommand = WindowsLogonStartupRegistrationContract
				.CreateExpectedCommand();
		}
		catch (ArgumentException exception)
		{
			AppDiagnostics.TryWrite(
				"logon-startup-command-create",
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception);
			return CreateLogonStartupMenuPresentation(
				canManageRegistration: true,
				state: null,
				canCreateExpectedCommand: false);
		}

		LogonStartupRegistrationState state =
			_logonStartupRegistrationStore.Query(expectedCommand);
		return CreateLogonStartupMenuPresentation(
			canManageRegistration: true,
			state);
	}

	private void ToggleLogonStartupRegistration()
	{
		try
		{
			if (!WindowsLogonStartupRegistrationContract
				.IsCanonicalExecutablePath(Environment.ProcessPath))
			{
				return;
			}

			string expectedCommand =
				WindowsLogonStartupRegistrationContract.CreateExpectedCommand();
			LogonStartupRegistrationState state =
				_logonStartupRegistrationStore.Query(expectedCommand);
			LogonStartupRegistrationState resultingState;

			switch (state)
			{
				case LogonStartupRegistrationState.Absent:
					resultingState =
						_logonStartupRegistrationStore.Enable(expectedCommand);
					EnsureLogonStartupMutationResult(
						resultingState,
						LogonStartupRegistrationState.ExactMatch);
					break;
				case LogonStartupRegistrationState.ExactMatch:
					resultingState = _logonStartupRegistrationStore
						.RemoveIfMatches(expectedCommand);
					EnsureLogonStartupMutationResult(
						resultingState,
						LogonStartupRegistrationState.Absent);
					break;
				case LogonStartupRegistrationState.Conflict:
					if (!ConfirmLogonStartupRegistrationReplacement())
					{
						return;
					}

					resultingState = _logonStartupRegistrationStore
						.Overwrite(expectedCommand);
					EnsureLogonStartupMutationResult(
						resultingState,
						LogonStartupRegistrationState.ExactMatch);
					break;
				default:
					throw new InvalidOperationException(
						UiText.Get("Shell.App088"));
			}
		}
		catch (Exception exception)
		{
			ReportLogonStartupRegistrationFailure(exception);
		}
		finally
		{
			UpdateLogonStartupMenuItem();
		}
	}

	private bool ConfirmLogonStartupRegistrationReplacement()
	{
		return ShowShellMessage(
			UiText.Get("Shell.App089"),
			UiText.Get("Shell.App090"),
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No) == MessageBoxResult.Yes;
	}

	private static void EnsureLogonStartupMutationResult(
		LogonStartupRegistrationState actual,
		LogonStartupRegistrationState expected)
	{
		if (actual != expected)
		{
			throw new InvalidOperationException(
				UiText.Get("Shell.App091"));
		}
	}

	private void ReportLogonStartupRegistrationFailure(Exception exception)
	{
		string failureReason =
			AppDiagnostics.GetUserFacingFailureReason(exception);
		AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
			"logon-startup-registration-update",
			failureReason,
			exception);
		ShowShellMessage(
			UiText.Format("Shell.App092", failureReason) +
				CreateDiagnosticGuidance(diagnostic),
			UiText.Get("Shell.App093"),
			MessageBoxButton.OK,
			MessageBoxImage.Warning);
	}

	internal static ProcessStartInfo CreateWindowsStartupAppsStartInfo()
	{
		return new ProcessStartInfo(WindowsStartupAppsSettingsUri)
		{
			UseShellExecute = true
		};
	}

	internal static void LaunchWindowsStartupAppsSettings(
		Func<ProcessStartInfo, Process?> startProcess)
	{
		ArgumentNullException.ThrowIfNull(startProcess);

		// Shell URI 成功交付後仍可能回傳 null；只有例外才代表啟動失敗。
		using (startProcess(CreateWindowsStartupAppsStartInfo()))
		{
		}
	}

	private void OpenWindowsStartupAppsSettings()
	{
		try
		{
			LaunchWindowsStartupAppsSettings(Process.Start);
		}
		catch (Exception exception)
		{
			string failureReason =
				AppDiagnostics.GetUserFacingFailureReason(exception);
			AppDiagnostics.TryWrite(
				"windows-startup-apps-settings",
				failureReason,
				exception);
			ShowShellMessage(
				UiText.Format("Shell.App094", failureReason),
				UiText.Get("Shell.App095"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	private void InitializeNotifyIcon()
	{
		FormsContextMenuStrip menu = new();
		menu.Opening += (_, _) => Dispatcher.Invoke(() =>
		{
			UpdatePortableSettingsMenuItems();
			UpdateLogonStartupMenuItem();
			if (_updateUiPresentation is UpdateUiPresentation presentation)
			{
				UpdateUpdateTrayMenuItems(presentation);
			}
		});
		_widgetVisibilityMenuItem = new FormsToolStripMenuItem(UiText.Get("Shell.App081"))
		{
			CheckOnClick = false
		};
		_widgetVisibilityMenuItem.Click +=
			(_, _) => Dispatcher.Invoke(ToggleFloatingWidget);
		menu.Items.Add(_widgetVisibilityMenuItem);
		_widgetTopmostMenuItem = new FormsToolStripMenuItem(UiText.Get("Shell.App096"))
		{
			CheckOnClick = false
		};
		_widgetTopmostMenuItem.Click +=
			(_, _) => Dispatcher.Invoke(ToggleFloatingWidgetTopmost);
		menu.Items.Add(_widgetTopmostMenuItem);
		var languageMenu = new FormsToolStripMenuItem(UiText.Get("Shell.Language"));
		_englishLanguageMenuItem = new FormsToolStripMenuItem(UiText.Get("Shell.LanguageEnglish"));
		_traditionalChineseLanguageMenuItem =
			new FormsToolStripMenuItem(UiText.Get("Shell.LanguageTraditionalChinese"));
		_englishLanguageMenuItem.Click += (_, _) => Dispatcher.Invoke(
			() => _floatingWidgetWindow?.ChangeLanguage(AppLanguage.English));
		_traditionalChineseLanguageMenuItem.Click += (_, _) => Dispatcher.Invoke(
			() => _floatingWidgetWindow?.ChangeLanguage(AppLanguage.TraditionalChinese));
		languageMenu.DropDownItems.Add(_englishLanguageMenuItem);
		languageMenu.DropDownItems.Add(_traditionalChineseLanguageMenuItem);
		menu.Items.Add(languageMenu);
		menu.Items.Add("-");
		_updateAvailableMenuItem = new FormsToolStripMenuItem(
			UiText.Get("Shell.App019"))
		{
			Visible = false
		};
		_updateAvailableMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
			StartUpdateUiOperation(
				ExecutePrimaryUpdateActionAsync,
				"tray-update-primary-action"));
		menu.Items.Add(_updateAvailableMenuItem);
		_checkForUpdatesMenuItem = new FormsToolStripMenuItem(UiText.Get("Shell.App018"));
		_checkForUpdatesMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
			StartUpdateUiOperation(
				() => CheckForUpdatesManuallyAsync(
					ManualUpdateCheckInvocationSurface.Tray),
				"tray-manual-update-check"));
		menu.Items.Add(_checkForUpdatesMenuItem);
		_automaticUpdateChecksMenuItem = new FormsToolStripMenuItem(
			UiText.Get("Shell.App097"))
		{
			CheckOnClick = false
		};
		_automaticUpdateChecksMenuItem.Click += (_, _) =>
			Dispatcher.Invoke(() => StartUpdateUiOperation(
				() => SetAutomaticUpdateChecksEnabledAsync(
					!_updatePresentationState.IsAutoCheckEnabled),
				"toggle-automatic-update-checks"));
		menu.Items.Add(_automaticUpdateChecksMenuItem);
		menu.Items.Add("-");
		_logonStartupMenuItem = new FormsToolStripMenuItem(
			UiText.Get("Shell.App087"))
		{
			CheckOnClick = false,
			ToolTipText =
				UiText.Get("Shell.App098")
		};
		_logonStartupMenuItem.Click +=
			(_, _) => Dispatcher.Invoke(ToggleLogonStartupRegistration);
		menu.Items.Add(_logonStartupMenuItem);
		menu.Items.Add(
			UiText.Get("Shell.App099"),
			null,
			(_, _) => Dispatcher.Invoke(OpenWindowsStartupAppsSettings));
		menu.Items.Add("-");
		_exportPortableSettingsMenuItem = new FormsToolStripMenuItem(
			UiText.Get("Shell.App100"));
		_exportPortableSettingsMenuItem.Click +=
			(_, _) => Dispatcher.Invoke(
				() => _floatingWidgetWindow?.BeginPortableSettingsExport());
		menu.Items.Add(_exportPortableSettingsMenuItem);
		_undoPortableSettingsImportMenuItem = new FormsToolStripMenuItem(
			UiText.Get("Shell.App101"));
		_undoPortableSettingsImportMenuItem.Click +=
			(_, _) => Dispatcher.Invoke(
				() => _floatingWidgetWindow?.BeginPortableSettingsImportUndo());
		menu.Items.Add(_undoPortableSettingsImportMenuItem);
		menu.Items.Add("-");
		menu.Items.Add(
			UiText.Get("Shell.App102"),
			null,
			(_, _) => Dispatcher.Invoke(OpenUserGuide));
		menu.Items.Add(
			UiText.Get("Shell.App103"),
			null,
			(_, _) => Dispatcher.Invoke(ShowAboutWindowFromTray));
		menu.Items.Add("-");
		menu.Items.Add(
			UiText.Get("Shell.App104"),
			null,
			(_, _) => Dispatcher.Invoke(Quit));

		_applicationIcon = LoadApplicationIcon();
		_notifyIcon = new FormsNotifyIcon
		{
			ContextMenuStrip = menu,
			Icon = _themeAppIcon?.TrayIcon ??
				_applicationIcon ?? SystemIcons.Application,
			Text = "AI Usage",
			Visible = true
		};

		_notifyIcon.DoubleClick +=
			(_, _) => Dispatcher.Invoke(ShowFloatingWidget);
		_notifyIcon.BalloonTipClicked +=
			(_, _) => Dispatcher.Invoke(ShowFloatingWidget);
		UpdateWidgetVisibilityMenuItem();
		UpdateWidgetTopmostMenuItem();
		UpdateLanguageMenuItems();
		UpdatePortableSettingsMenuItems();
		UpdateLogonStartupMenuItem();
		if (_updateUiPresentation is UpdateUiPresentation presentation)
		{
			UpdateUpdateTrayMenuItems(presentation);
		}
	}

	internal void ShowAboutWindow()
	{
		FloatingWidgetWindow? widget = _floatingWidgetWindow;
		ShowAboutWindowCore(
			(widget is not null) &&
			widget.IsVisible &&
			!widget.IsCollapsed
				? widget
				: null);
	}

	private void ShowAboutWindowFromTray()
	{
		ShowAboutWindowCore(owner: null);
	}

	private void ShowAboutWindowCore(FloatingWidgetWindow? owner)
	{
		if (IsQuitting)
		{
			return;
		}

		if (_aboutWindow is not null)
		{
			ConfigureAboutWindowOwnership(_aboutWindow, owner);
			if (_aboutWindow.WindowState == WindowState.Minimized)
			{
				_aboutWindow.WindowState = WindowState.Normal;
			}

			if (!_aboutWindow.IsVisible)
			{
				_aboutWindow.Show();
			}

			_aboutWindow.Activate();
			return;
		}

		AboutWindow aboutWindow = new();
		aboutWindow.UpdateCheckRequested += AboutWindow_UpdateCheckRequested;
		aboutWindow.Closed += AboutWindow_Closed;
		ConfigureAboutWindowOwnership(aboutWindow, owner);

		if (aboutWindow.Owner is null)
		{
			aboutWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}

		_aboutWindow = aboutWindow;
		if (_updateUiPresentation is UpdateUiPresentation presentation)
		{
			aboutWindow.UpdateUpdateStatus(
				() => _updateUiPresentation?.AboutStatusText ?? string.Empty,
				presentation.CanCheckManually,
				presentation.IsChecking);
		}

		try
		{
			aboutWindow.Show();
		}
		catch
		{
			ReleaseAboutWindow(aboutWindow);
			throw;
		}
	}

	private void AboutWindow_Closed(object? sender, EventArgs e)
	{
		if (sender is AboutWindow aboutWindow)
		{
			ReleaseAboutWindow(aboutWindow);
		}
	}

	private void ReleaseAboutWindow(AboutWindow aboutWindow)
	{
		aboutWindow.UpdateCheckRequested -= AboutWindow_UpdateCheckRequested;
		aboutWindow.Closed -= AboutWindow_Closed;
		if (ReferenceEquals(_aboutWindow, aboutWindow))
		{
			_aboutWindow = null;
		}
	}

	private static void ConfigureAboutWindowOwnership(
		AboutWindow aboutWindow,
		FloatingWidgetWindow? owner)
	{
		if (owner is not null)
		{
			if (!ReferenceEquals(aboutWindow.Owner, owner))
			{
				aboutWindow.Owner = owner;
			}

			aboutWindow.ShowInTaskbar = false;
			return;
		}

		if (aboutWindow.Owner is not null)
		{
			aboutWindow.Owner = null;
		}

		aboutWindow.ShowInTaskbar = true;
	}

	private void OpenUserGuide()
	{
		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open();

		if (result == LocalUserGuideOpenResult.Opened)
		{
			return;
		}

		ShowShellMessage(
			LocalUserGuideLauncher.GetFailureMessage(result),
			UiText.Get("Shell.App105"),
			MessageBoxButton.OK,
			MessageBoxImage.Warning);
	}

	private void InitializeRefreshTimer(DashboardViewModel viewModel)
	{
		_refreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(10)
		};
		_refreshTimer.Tick += (_, _) => BeginPeriodicRefresh(viewModel);
		_refreshTimer.Start();
	}

	private void BeginPeriodicRefresh(DashboardViewModel viewModel)
	{
		if (_isPostStartupRecoveryRunning || IsQuitting)
		{
			return;
		}

		try
		{
			viewModel.ScheduleUsageRefreshInBackground();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"periodic-usage-refresh",
				"stage=outer-boundary;result=failed",
				exception);
			viewModel.ReportRefreshFailure();
		}
	}
}
