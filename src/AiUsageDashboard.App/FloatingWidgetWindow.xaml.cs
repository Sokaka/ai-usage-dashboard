using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Presentation;

using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using FormsScreen = System.Windows.Forms.Screen;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AiUsageDashboard.App;

public partial class FloatingWidgetWindow : Window
{
	internal enum FocusTransitionMode
	{
		PreserveCurrent,
		Release,
		Transfer
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRectangle
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private const double CollapsedSize = 56;
	private const double CornerMargin = 18;
	private const double CornerToggleContentInset = 42;
	private const double CornerToggleFooterHeight = 44;
	private const double ExpandedDefaultHeight = 720;
	private const double ExpandedWidth = 390;
	private const uint SetWindowPositionNoActivate = 0x0010;
	private const uint SetWindowPositionNoMove = 0x0002;
	private const uint SetWindowPositionNoSize = 0x0001;
	private const uint SetWindowPositionNoZOrder = 0x0004;
	private const uint SetWindowPositionHideWindow = 0x0080;
	private const uint SetWindowPositionShowWindow = 0x0040;
	private const int ShowWindowWithoutActivation = 4;
	private static readonly TimeSpan CompactRefreshStatusDisplayDuration =
		TimeSpan.FromSeconds(5);
	private readonly AccountConnectionCoordinator _accountConnectionCoordinator;
	private readonly AccountStatusAnnouncementBridge
		_accountStatusAnnouncementBridge;
	private readonly PortableSettingsJsonService _portableSettingsJsonService = new();
	private readonly SemaphoreSlim _portableSettingsOperationGate = new(1, 1);
	private readonly CancellationTokenSource _portableSettingsLifetime = new();
	private readonly VerticalDragScrollSession _accountScrollDragSession = new();
	private readonly UpdateBannerAnnouncementTracker
		_updateBannerAnnouncementTracker = new();
	private readonly DispatcherTimer _compactRefreshStatusTimer;
	private readonly DispatcherTimer _inlineStatusTimer;
	private DrawingPoint _collapsedDragStart;
	private FloatingWidgetCorner _corner = FloatingWidgetCorner.BottomRight;
	private string? _lastViewModelAnnouncement;
	private string _inlineStatusSource = string.Empty;
	private string? _updateAvailableVersion;
	private PortableWidgetPreferences?
		_portableWidgetPreferencesBeforeLastImport;
	private string _screenDeviceName = string.Empty;
	private DateTimeOffset _lastViewModelAnnouncementAt;
	private double _collapsedDragOffsetXRatio;
	private double _collapsedDragOffsetYRatio;
	private double? _collapsedPositionXRatio;
	private double? _collapsedPositionYRatio;
	private int _focusInteractionGeneration;
	private bool _hasPlacement;
	private bool _hasPersistentInlineStatus;
	private bool _hasCollapsedDragFailure;
	private bool _hasShownExpandedView;
	private bool _isShellPreferencesFailureInlineStatus;
	private bool _isApplyingPlacement;
	private bool _isApplyingPortableWidgetPreferences;
	private bool _isCollapsed;
	private bool _isCollapsedDragPending;
	private bool _isCollapsedDragging;
	private bool _isExpandedDragging;
	private bool _isHeightFollowingCardCount;
	private bool _isPlacementCorrectionPending;
	private bool _isPlacementPending;
	private bool _isPortableSettingsGateReservedForUpdateShutdown;
	private bool _isWorkAreaRefreshPending;
	private bool _hasUpdateBadge;
	private AppTheme _theme = AppTheme.ClassicBlue;
	private AppLanguage _language = AppLanguage.English;
	private HwndSource? _windowSource;

	internal event EventHandler? PreferencesChanged;

	internal event EventHandler? AutomaticUpdateChecksDisableRequested;

	internal event EventHandler? UpdatePrimaryActionRequested;

	internal event EventHandler? UpdateSnoozeRequested;

	internal bool IsCollapsed => _isCollapsed;

	internal FloatingWidgetCorner Corner => _corner;

	internal AppTheme Theme => _theme;

	internal AppLanguage SelectedLanguage => _language;

	private static string PortableSettingsReconnectNotice =>
		UiText.Get("Shell.ReconnectNotice");

	internal void ApplyUpdatePresentation(
		UpdateUiPresentation presentation,
		UpdateBannerAnnouncementKey? announcementKey)
	{
		ArgumentNullException.ThrowIfNull(presentation);
		_updateBannerAnnouncementTracker.Observe(
			presentation.IsBannerVisible ? announcementKey : null);

		UpdateBannerTextBlock.Text = presentation.BannerText;
		UpdateBanner.Visibility = presentation.IsBannerVisible
			? Visibility.Visible
			: Visibility.Collapsed;
		UpdatePrimaryActionButton.Content = presentation.PrimaryActionText;
		UpdatePrimaryActionButton.IsEnabled =
			presentation.IsPrimaryActionEnabled;
		UpdatePrimaryActionButton.Visibility =
			presentation.PrimaryAction == UpdatePrimaryActionKind.None
				? Visibility.Collapsed
				: Visibility.Visible;
		UpdateReleaseHistoryButton.Visibility =
			presentation.IsReleaseHistoryVisible
				? Visibility.Visible
				: Visibility.Collapsed;
		UpdateSnoozeButton.Visibility = presentation.IsSnoozeVisible
			? Visibility.Visible
			: Visibility.Collapsed;
		DisableAutomaticUpdateChecksButton.Visibility =
			presentation.IsDisableAutomaticChecksVisible
				? Visibility.Visible
				: Visibility.Collapsed;
		_hasUpdateBadge = presentation.HasUpdateBadge;
		_updateAvailableVersion = presentation.AvailableVersion;
		UpdateCornerDependentVisuals();
		TryAnnouncePendingUpdateBanner();
	}

	internal FloatingWidgetWindow(
		DashboardViewModel viewModel,
		AccountConnectionCoordinator accountConnectionCoordinator,
		DashboardShellPreferences preferences)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		_accountConnectionCoordinator = accountConnectionCoordinator ??
			throw new ArgumentNullException(nameof(accountConnectionCoordinator));
		ArgumentNullException.ThrowIfNull(preferences);
		InitializeComponent();
		_compactRefreshStatusTimer = new DispatcherTimer
		{
			Interval = CompactRefreshStatusDisplayDuration
		};
		_compactRefreshStatusTimer.Tick += CompactRefreshStatusTimer_Tick;
		_inlineStatusTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(12)
		};
		_inlineStatusTimer.Tick += InlineStatusTimer_Tick;
		_screenDeviceName = preferences.MonitorDeviceName;
		_corner = preferences.Corner;
		_collapsedPositionXRatio = preferences.CollapsedPositionXRatio;
		_collapsedPositionYRatio = preferences.CollapsedPositionYRatio;
		_theme = preferences.Theme;
		_language = preferences.Language;
		_isHeightFollowingCardCount = preferences.IsHeightFollowingCardCount;
		_hasPlacement = true;
		_isCollapsed = !preferences.IsCollapsed;
		SetCollapsed(preferences.IsCollapsed, notifyPreferences: false);
		Topmost = preferences.IsTopmost;
		PinButton.IsChecked = preferences.IsTopmost;
		UpdateCornerDependentVisuals();
		UpdateHeightFollowsCardCountMenuItem();
		UpdateThemeMenuItems();
		UpdateLanguageMenuItems();
		DataContext = viewModel;
		IsVisibleChanged += FloatingWidgetWindow_IsVisibleChanged;
		_accountStatusAnnouncementBridge =
			new AccountStatusAnnouncementBridge(
				viewModel,
				Dispatcher,
				ReportAccountStatusAnnouncement);
		viewModel.PropertyChanged += ViewModel_PropertyChanged;
		UpdateCompactRefreshStatus(viewModel);
		InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
	}

	internal bool TryReservePortableSettingsForUpdateShutdown()
	{
		if (_isPortableSettingsGateReservedForUpdateShutdown ||
			!_portableSettingsOperationGate.Wait(0))
		{
			return false;
		}

		_isPortableSettingsGateReservedForUpdateShutdown = true;
		return true;
	}

	internal void RollbackPortableSettingsUpdateShutdownReservation()
	{
		if (!_isPortableSettingsGateReservedForUpdateShutdown)
		{
			return;
		}

		_isPortableSettingsGateReservedForUpdateShutdown = false;
		_portableSettingsOperationGate.Release();
	}

	internal DashboardShellPreferences CreatePreferences(bool isVisible)
	{
		return new DashboardShellPreferences(
			isVisible,
			_isCollapsed,
			Topmost,
			_screenDeviceName,
			_corner,
			isVisible
				? DashboardStartupSurface.Widget
				: DashboardStartupSurface.Tray,
			_theme,
			_isHeightFollowingCardCount,
			_collapsedPositionXRatio,
			_collapsedPositionYRatio,
			_language);
	}

	internal static PortableWidgetPreferences CreatePortableWidgetPreferencesSnapshot(
		bool isWidgetVisible,
		bool isCollapsed,
		bool isTopmost,
		FloatingWidgetCorner corner,
		AppTheme theme,
		bool isHeightFollowingCardCount = false,
		AppLanguage language = AppLanguage.English)
	{
		return new PortableWidgetPreferences(
			isWidgetVisible,
			isCollapsed,
			isTopmost,
			corner,
			theme,
			isHeightFollowingCardCount,
			language);
	}

	private PortableWidgetPreferences CapturePortableWidgetPreferences()
	{
		return CreatePortableWidgetPreferencesSnapshot(
			IsVisible,
			_isCollapsed,
			Topmost,
			_corner,
			_theme,
			_isHeightFollowingCardCount,
			_language);
	}

	internal static PortableWidgetPreferences?
		CreatePortableWidgetPreferencesRestorePoint(
			PortableWidgetPreferences? current,
			PortableWidgetPreferences? imported)
	{
		if ((current is null) || (imported is null))
		{
			return null;
		}

		return current with
		{
			Theme = imported.Theme is null ? null : current.Theme,
			IsHeightFollowingCardCount =
				imported.IsHeightFollowingCardCount is null
					? null
					: current.IsHeightFollowingCardCount,
			Language = imported.Language is null ? null : current.Language
		};
	}

	internal static FocusTransitionMode ResolveAsyncFocusTransition(
		FocusTransitionMode initialMode,
		int capturedInteractionGeneration,
		int currentInteractionGeneration)
	{
		return capturedInteractionGeneration == currentInteractionGeneration
			? initialMode
			: FocusTransitionMode.PreserveCurrent;
	}

	internal void ApplyRecoveredPreferences(
		DashboardShellPreferences preferences)
	{
		ArgumentNullException.ThrowIfNull(preferences);
		_isApplyingPortableWidgetPreferences = true;

		try
		{
			_screenDeviceName = preferences.MonitorDeviceName;
			_collapsedPositionXRatio = preferences.CollapsedPositionXRatio;
			_collapsedPositionYRatio = preferences.CollapsedPositionYRatio;
			_theme = preferences.Theme;
			ApplyLanguage(preferences.Language);
			SetHeightFollowingCardCount(
				preferences.IsHeightFollowingCardCount,
				notifyPreferences: false);
			SetCollapsed(preferences.IsCollapsed, notifyPreferences: false);
			SetCorner(preferences.Corner, notifyPreferences: false);
			Topmost = preferences.IsTopmost;
			PinButton.IsChecked = preferences.IsTopmost;
			_hasPlacement = true;
			UpdateHeightFollowsCardCountMenuItem();
			UpdateThemeMenuItems();

			if (preferences.IsWidgetVisible)
			{
				ShowNearWorkArea();
			}
			else
			{
				Hide();
			}
		}
		finally
		{
			_isApplyingPortableWidgetPreferences = false;
		}

		NotifyPortableWidgetPreferencesChanged();
	}

	private void ApplyPortableWidgetPreferences(
		PortableWidgetPreferences? preferences,
		FocusTransitionMode focusTransitionMode,
		DependencyObject? pointerFocusOwner)
	{
		if (preferences is null)
		{
			QueuePointerFocusRelease(
				focusTransitionMode,
				pointerFocusOwner,
				_isCollapsed ? CollapsedButton : AnchorToggleButton);
			return;
		}

		bool isCollapsedStateChanging =
			_isCollapsed != preferences.IsCollapsed;
		_isApplyingPortableWidgetPreferences = true;

		try
		{
			if (preferences.Theme is AppTheme theme)
			{
				_theme = theme;
				UpdateThemeMenuItems();
			}

			if (preferences.Language is AppLanguage language)
			{
				ApplyLanguage(language);
			}

			if (preferences.IsHeightFollowingCardCount is bool
				isHeightFollowingCardCount)
			{
				SetHeightFollowingCardCount(
					isHeightFollowingCardCount,
					notifyPreferences: false);
			}

			SetCollapsed(
				preferences.IsCollapsed,
				notifyPreferences: false,
				focusTransitionMode: focusTransitionMode,
				pointerFocusOwner: pointerFocusOwner);
			SetCorner(preferences.Corner, notifyPreferences: false);
			Topmost = preferences.IsTopmost;
			PinButton.IsChecked = preferences.IsTopmost;
			_hasPlacement = true;

			if (preferences.IsWidgetVisible)
			{
				ShowNearWorkArea();
			}
			else
			{
				Hide();
			}
		}
		finally
		{
			_isApplyingPortableWidgetPreferences = false;
		}

		if (!isCollapsedStateChanging)
		{
			QueuePointerFocusRelease(
				focusTransitionMode,
				pointerFocusOwner,
				_isCollapsed ? CollapsedButton : AnchorToggleButton);
		}

		NotifyPreferencesChanged();
	}

	internal void ToggleTopmost()
	{
		PinButton.IsChecked = PinButton.IsChecked != true;
	}

	internal static string CreateShellPreferencesSaveFailureInlineText(
		string failureReason)
	{
		return
			UiText.Get("Shell.Widget106") +
			App.CreateShellPreferencesSaveFailureNotificationText(failureReason);
	}

	internal void ReportShellPreferencesSaveFailure(string failureReason)
	{
		ReportInlineStatus(
			CreateShellPreferencesSaveFailureInlineText(failureReason),
			autoDismiss: false,
			preserveAgainstAsyncUpdates: true);
		_isShellPreferencesFailureInlineStatus = true;
	}

	internal void ReportShellPreferencesSaveRecovered()
	{
		if (_isShellPreferencesFailureInlineStatus)
		{
			ReportInlineStatus(UiText.Get("Shell.Widget107"));
			return;
		}

		ReportAsyncStatus(UiText.Get("Shell.Widget107"));
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		ResetAccountScrollDrag(releaseMouseCapture: true);

		if ((System.Windows.Application.Current is App app) && !app.IsQuitting)
		{
			e.Cancel = true;
			Hide();
		}

		base.OnClosing(e);
	}

	protected override void OnActivated(EventArgs e)
	{
		base.OnActivated(e);
		TryAnnouncePendingUpdateBanner();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);

		IntPtr windowHandle = new WindowInteropHelper(this).Handle;
		_windowSource = HwndSource.FromHwnd(windowHandle);
		_windowSource?.AddHook(WindowMessageHook);
	}

	protected override void OnClosed(EventArgs e)
	{
		ResetAccountScrollDrag(releaseMouseCapture: true);
		_portableSettingsLifetime.Cancel();
		_accountStatusAnnouncementBridge.Dispose();

		if (DataContext is DashboardViewModel viewModel)
		{
			viewModel.PropertyChanged -= ViewModel_PropertyChanged;
		}

		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		_compactRefreshStatusTimer.Stop();
		_inlineStatusTimer.Stop();
		IsVisibleChanged -= FloatingWidgetWindow_IsVisibleChanged;
		InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
		base.OnClosed(e);
	}

	protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
	{
		if ((e.Key == Key.Escape) && !_isCollapsed)
		{
			SetCollapsed(true);
			e.Handled = true;
			return;
		}

		base.OnPreviewKeyDown(e);
	}

	public void ShowNearWorkArea()
	{
		Show();
		UpdateLayout();

		if (!_hasShownExpandedView)
		{
			_hasShownExpandedView = true;
			AccountScrollViewer.ScrollToTop();
		}

		ApplyCurrentPlacement();
	}

	internal void ShowExpandedNearWorkArea()
	{
		SetCollapsed(false);
		ShowNearWorkArea();
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetPhysicalCursorPos(out NativePoint point);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(
		IntPtr windowHandle,
		out NativeRectangle rectangle);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(
		IntPtr windowHandle,
		IntPtr insertAfter,
		int x,
		int y,
		int width,
		int height,
		uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(
		IntPtr windowHandle,
		int command);

	private static bool TryGetWindowBounds(
		IntPtr windowHandle,
		out DrawingRectangle windowBounds)
	{
		if ((windowHandle == IntPtr.Zero) ||
			!GetWindowRect(windowHandle, out NativeRectangle nativeRectangle))
		{
			windowBounds = DrawingRectangle.Empty;
			return false;
		}

		windowBounds = DrawingRectangle.FromLTRB(
			nativeRectangle.Left,
			nativeRectangle.Top,
			nativeRectangle.Right,
			nativeRectangle.Bottom);
		return true;
	}

	private async void AddAccountMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if ((DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement)
		{
			return;
		}

		ReportInlineStatus(string.Empty);
		AccountEditorWindow editorWindow = new(
			canAddAntigravity: !viewModel.Accounts.Any(
				account => account.Provider == ProviderKind.Antigravity))
		{
			Owner = this
		};

		if ((editorWindow.ShowDialog() != true) ||
			(editorWindow.EditedProfile is not AccountProfile profile))
		{
			return;
		}

		bool wasAdded = await ExecuteAccountChangeAsync(
			() => viewModel.AddAccountAsync(profile));

		if (wasAdded &&
			editorWindow.ConnectAfterSave &&
			viewModel.Accounts.FirstOrDefault(account => account.Id == profile.Id) is
				AccountUsageViewModel addedAccount)
		{
			await ConnectAccountAsync(
				addedAccount,
				viewModel,
				hasConfirmedClaudeQuotaRisk:
					profile.Provider == ProviderKind.Claude,
				hasConfirmedAntigravityPreflight:
					profile.Provider == ProviderKind.Antigravity);
		}
	}

	private void ExportSettingsMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		BeginPortableSettingsExport();
	}

	internal void BeginPortableSettingsExport()
	{
		PortableWidgetPreferences widgetPreferences =
			CapturePortableWidgetPreferences();
		_ = ExportPortableSettingsAsync(widgetPreferences);
	}

	private async Task ExportPortableSettingsAsync(
		PortableWidgetPreferences widgetPreferences)
	{
		if ((DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement)
		{
			return;
		}

		if (!_portableSettingsOperationGate.Wait(0))
		{
			ReportInlineStatus(UiText.Get("Shell.Widget108"));
			return;
		}

		try
		{
			MessageBoxResult confirmation = ShowPortableSettingsMessageBox(
				UiText.Get("Shell.Widget109") +
				UiText.Get("Shell.Widget110"),
				UiText.Get("Shell.Widget111"),
				MessageBoxButton.YesNo,
				MessageBoxImage.Information,
				MessageBoxResult.No);

			if (confirmation != MessageBoxResult.Yes)
			{
				return;
			}

			WpfSaveFileDialog dialog = new()
			{
				AddExtension = true,
				DefaultExt = ".aiusage.json",
				FileName =
					$"ai-usage-settings-{DateTimeOffset.Now:yyyyMMdd}.aiusage.json",
				Filter =
					UiText.Get("Shell.Widget112"),
				OverwritePrompt = true,
				Title = UiText.Get("Shell.Widget113")
			};

			bool? dialogResult = IsVisible
				? dialog.ShowDialog(this)
				: dialog.ShowDialog();

			if (dialogResult != true)
			{
				return;
			}

			try
			{
				PortableSettingsSnapshot snapshot =
					await viewModel.CreatePortableSettingsSnapshotAsync(
						widgetPreferences,
						_portableSettingsLifetime.Token);
				await _portableSettingsJsonService.ExportAsync(
					dialog.FileName,
					snapshot,
					_portableSettingsLifetime.Token);
				ReportInlineStatus(
					UiText.Format("Shell.Widget114", snapshot.Accounts.Count));
			}
			catch (OperationCanceledException)
			{
				// Closing the app or cancelling the operation leaves the destination unchanged.
			}
			catch (Exception exception)
			{
				ReportPortableSettingsFailure("export", exception);
			}
		}
		catch (OperationCanceledException)
		{
			// App shutdown cancels any operation that has not committed.
		}
		catch (Exception exception)
		{
			ReportPortableSettingsFailure("export", exception);
		}
		finally
		{
			_portableSettingsOperationGate.Release();
		}
	}

	private async void ImportSettingsMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		FocusTransitionMode focusTransitionMode =
			PointerFocusRelease.WasInvokedByPointer(sender)
				? FocusTransitionMode.Release
				: FocusTransitionMode.Transfer;
		DependencyObject? pointerFocusOwner =
			focusTransitionMode == FocusTransitionMode.Release
				? PointerFocusRelease.GetPointerFocusOwner(sender)
				: null;
		int focusInteractionGeneration = _focusInteractionGeneration;

		if ((DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement)
		{
			return;
		}

		if (!_portableSettingsOperationGate.Wait(0))
		{
			ReportInlineStatus(UiText.Get("Shell.Widget108"));
			return;
		}

		try
		{
			WpfOpenFileDialog dialog = new()
			{
				CheckFileExists = true,
				DefaultExt = ".aiusage.json",
				Filter =
					UiText.Get("Shell.Widget112"),
				Multiselect = false,
				Title = UiText.Get("Shell.Widget116")
			};

			if (dialog.ShowDialog(this) != true)
			{
				return;
			}

			PortableSettingsSnapshot snapshot;

			try
			{
				snapshot = await _portableSettingsJsonService.ImportAsync(
					dialog.FileName,
					_portableSettingsLifetime.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception exception)
			{
				ReportPortableSettingsFailure("import", exception);
				return;
			}

			PortableSettingsSnapshot currentSnapshot =
				await viewModel.CreatePortableSettingsSnapshotAsync(
					CapturePortableWidgetPreferences(),
					_portableSettingsLifetime.Token);
			string preview = CreatePortableSettingsImportPreview(
				currentSnapshot,
				snapshot);
			int claudeQuotaRiskConfirmationCount = snapshot.Accounts.Count(
				account => account.Provider == ProviderKind.Claude);
			string claudeQuotaRiskNotice =
				CreateClaudeQuotaRiskReconfirmationNotice(
					claudeQuotaRiskConfirmationCount);
			string replacementDescription = UiText.Get("Shell.ImportReplaceDescription");

			MessageBoxResult confirmation = WpfMessageBox.Show(
				this,
				$"{preview}\n\n" +
				UiText.Format("Shell.Widget123", replacementDescription) +
				$"{claudeQuotaRiskNotice}\n\n" +
				UiText.Format("Shell.Widget124", PortableSettingsReconnectNotice),
				UiText.Get("Shell.Widget125"),
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning,
				MessageBoxResult.No);

			if (confirmation != MessageBoxResult.Yes)
			{
				return;
			}

			try
			{
				ReportInlineStatus(
					UiText.Get("Shell.Widget126"),
					autoDismiss: false,
					preserveAgainstAsyncUpdates: true);
				await viewModel.ReplacePortableSettingsAsync(
					snapshot,
					CreatePreferences(IsVisible),
					_portableSettingsLifetime.Token);
				_portableWidgetPreferencesBeforeLastImport =
					CreatePortableWidgetPreferencesRestorePoint(
						currentSnapshot.WidgetPreferences,
						snapshot.WidgetPreferences);
				ReportPortableSettingsImportCompleted(
					snapshot.Accounts.Count,
					claudeQuotaRiskConfirmationCount,
					viewModel.AccountSettingsMessage);
				ApplyPortableWidgetPreferences(
					snapshot.WidgetPreferences,
					ResolveAsyncFocusTransition(
						focusTransitionMode,
						focusInteractionGeneration,
						_focusInteractionGeneration),
					pointerFocusOwner);
			}
			catch (AccountProfileStoreException exception) when (
				exception.HasCommittedChanges)
			{
				string message = CreatePortableSettingsImportCompletedMessage(
					snapshot.Accounts.Count,
					claudeQuotaRiskConfirmationCount,
					viewModel.AccountSettingsMessage);
				string warningDetails = exception.Message.Trim();

				if (!string.IsNullOrWhiteSpace(viewModel.AccountSettingsMessage) &&
					warningDetails.StartsWith(
						viewModel.AccountSettingsMessage,
						StringComparison.Ordinal))
				{
					warningDetails = warningDetails[
						viewModel.AccountSettingsMessage.Length..].Trim();
				}

				if (!string.IsNullOrWhiteSpace(warningDetails))
				{
					message += $"\n\n{warningDetails}";
				}

				ReportInlineStatus(message);
				WpfMessageBox.Show(
					this,
					message,
					UiText.Get("Shell.Widget127"),
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
				_portableWidgetPreferencesBeforeLastImport =
					CreatePortableWidgetPreferencesRestorePoint(
						currentSnapshot.WidgetPreferences,
						snapshot.WidgetPreferences);
				ApplyPortableWidgetPreferences(
					snapshot.WidgetPreferences,
					ResolveAsyncFocusTransition(
						focusTransitionMode,
						focusInteractionGeneration,
						_focusInteractionGeneration),
					pointerFocusOwner);
			}
			catch (OperationCanceledException)
			{
				// Cancellation before commit leaves the current settings unchanged.
			}
			catch (Exception exception)
			{
				ReportPortableSettingsFailure("import", exception);
			}
		}
		catch (OperationCanceledException)
		{
			// App shutdown cancels any import that has not committed.
		}
		catch (Exception exception)
		{
			ReportPortableSettingsFailure("import", exception);
		}
		finally
		{
			_portableSettingsOperationGate.Release();
		}
	}

	private void InputManager_PreProcessInput(
		object sender,
		PreProcessInputEventArgs e)
	{
		InputEventArgs input = e.StagingItem.Input;

		if (((input is System.Windows.Input.KeyEventArgs) &&
			ReferenceEquals(input.RoutedEvent, Keyboard.PreviewKeyDownEvent)) ||
			((input is MouseButtonEventArgs) &&
			ReferenceEquals(input.RoutedEvent, Mouse.PreviewMouseDownEvent)))
		{
			_focusInteractionGeneration++;
		}
	}

	private void UndoSettingsImportMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		FocusTransitionMode focusTransitionMode =
			PointerFocusRelease.WasInvokedByPointer(sender)
				? FocusTransitionMode.Release
				: FocusTransitionMode.Transfer;
		BeginPortableSettingsImportUndo(
			focusTransitionMode,
			focusTransitionMode == FocusTransitionMode.Release
				? PointerFocusRelease.GetPointerFocusOwner(sender)
				: null);
	}

	internal void BeginPortableSettingsImportUndo()
	{
		BeginPortableSettingsImportUndo(
			FocusTransitionMode.PreserveCurrent,
			pointerFocusOwner: null);
	}

	private void BeginPortableSettingsImportUndo(
		FocusTransitionMode focusTransitionMode,
		DependencyObject? pointerFocusOwner)
	{
		DashboardShellPreferences currentShellPreferences =
			CreatePreferences(IsVisible);
		_ = UndoLastPortableSettingsImportAsync(
			currentShellPreferences,
			focusTransitionMode,
			pointerFocusOwner,
			_focusInteractionGeneration);
	}

	private async Task UndoLastPortableSettingsImportAsync(
		DashboardShellPreferences currentShellPreferences,
		FocusTransitionMode focusTransitionMode,
		DependencyObject? pointerFocusOwner,
		int focusInteractionGeneration)
	{
		if ((DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanUndoLastPortableSettingsImport)
		{
			return;
		}

		if (!_portableSettingsOperationGate.Wait(0))
		{
			ReportInlineStatus(UiText.Get("Shell.Widget108"));
			return;
		}

		try
		{
			ReportInlineStatus(
				UiText.Get("Shell.Widget128"),
				autoDismiss: false,
				preserveAgainstAsyncUpdates: true);
			PortableWidgetPreferences? widgetPreferencesToRestore =
				_portableWidgetPreferencesBeforeLastImport;
			await viewModel.UndoLastPortableSettingsImportAsync(
				currentShellPreferences,
				_portableSettingsLifetime.Token);
			ReportInlineStatus(viewModel.AccountSettingsMessage);
			ApplyPortableWidgetPreferences(
				widgetPreferencesToRestore,
				ResolveAsyncFocusTransition(
					focusTransitionMode,
					focusInteractionGeneration,
					_focusInteractionGeneration),
				pointerFocusOwner);
			_portableWidgetPreferencesBeforeLastImport = null;
		}
		catch (AccountProfileStoreException exception) when (
			exception.HasCommittedChanges)
		{
			string message = string.IsNullOrWhiteSpace(
				viewModel.AccountSettingsMessage)
					? exception.Message
					: viewModel.AccountSettingsMessage;
			ReportInlineStatus(message);
			WpfMessageBox.Show(
				this,
				message,
				UiText.Get("Shell.Widget129"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			ApplyPortableWidgetPreferences(
				_portableWidgetPreferencesBeforeLastImport,
				ResolveAsyncFocusTransition(
					focusTransitionMode,
					focusInteractionGeneration,
					_focusInteractionGeneration),
				pointerFocusOwner);
			_portableWidgetPreferencesBeforeLastImport = null;
		}
		catch (OperationCanceledException)
		{
			// App shutdown cancels an undo that has not committed.
		}
		catch (Exception exception)
		{
			ReportPortableSettingsFailure("undo", exception);
		}
		finally
		{
			_portableSettingsOperationGate.Release();
		}
	}

	private MessageBoxResult ShowPortableSettingsMessageBox(
		string message,
		string caption,
		MessageBoxButton buttons,
		MessageBoxImage image,
		MessageBoxResult defaultResult)
	{
		return IsVisible
			? WpfMessageBox.Show(
				this,
				message,
				caption,
				buttons,
				image,
				defaultResult)
			: WpfMessageBox.Show(
				message,
				caption,
				buttons,
				image,
				defaultResult);
	}

	internal static string CreatePortableSettingsImportPreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(imported);
		Dictionary<(Guid Id, ProviderKind Provider), AccountProfile> currentAccounts =
			current.Accounts.ToDictionary(account => (account.Id, account.Provider));
		Dictionary<(Guid Id, ProviderKind Provider), AccountProfile> importedAccounts =
			imported.Accounts.ToDictionary(account => (account.Id, account.Provider));
		int addedCount = importedAccounts.Keys.Count(key =>
			!currentAccounts.ContainsKey(key));
		int removedCount = currentAccounts.Keys.Count(key =>
			!importedAccounts.ContainsKey(key));
		int updatedCount = importedAccounts.Count(pair =>
			currentAccounts.TryGetValue(pair.Key, out AccountProfile? existing) &&
			!ArePortableAccountFieldsEqual(existing, pair.Value));
		int claudeQuotaRiskConfirmationCount = imported.Accounts.Count(
			account => account.Provider == ProviderKind.Claude);
		bool orderChanged = !current.Accounts
			.Select(account => (account.Id, account.Provider))
			.SequenceEqual(imported.Accounts.Select(account =>
				(account.Id, account.Provider)));
		string providerCounts = string.Join(
			UiText.Get("Shell.ListSeparator"),
			imported.Accounts
				.GroupBy(account => account.Provider)
				.OrderBy(group => group.Key)
				.Select(group => $"{GetProviderDisplayName(group.Key)} {group.Count()}")
				.DefaultIfEmpty(UiText.Get("Shell.Widget131")));

		return UiText.Get("Shell.Widget132") +
			UiText.Format("Shell.Widget133", current.Accounts.Count, imported.Accounts.Count) +
			UiText.Format("Shell.Widget134", addedCount, removedCount, updatedCount) +
			UiText.Format("Shell.Widget137", (orderChanged ? UiText.Get("Shell.Widget135") : UiText.Get("Shell.Widget136"))) +
			UiText.Format("Shell.Widget138", GetUsageSortModeText(current.UsageSortMode)) +
			$"{GetUsageSortModeText(imported.UsageSortMode)}\n" +
			UiText.Format("Shell.Widget139", GetUsageDisplayModeText(current.UsageDisplayMode)) +
			$"{GetUsageDisplayModeText(imported.UsageDisplayMode)}\n" +
			UiText.Format("Shell.Widget140", GetPortableWidgetPreferencesPreview(current, imported)) +
			UiText.Format("Shell.Widget141", GetPortableThemePreview(current, imported)) +
			UiText.Format("Shell.LanguagePreview", GetPortableLanguagePreview(current, imported)) +
			UiText.Format("Shell.Widget142", claudeQuotaRiskConfirmationCount) +
			UiText.Format("Shell.Widget143", providerCounts);
	}

	private static string CreateClaudeQuotaRiskReconfirmationNotice(
		int claudeAccountCount)
	{
		return UiText.Format("Shell.Widget144", claudeAccountCount);
	}

	internal static string GetPortableLanguagePreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		if (imported.WidgetPreferences?.Language is not AppLanguage importedLanguage)
		{
			return UiText.Get("Shell.KeepCurrentLanguage");
		}

		return $"{GetLanguageDisplayName(current.WidgetPreferences?.Language ?? UiText.CurrentLanguage)} → " +
			GetLanguageDisplayName(importedLanguage);
	}

	private static string GetLanguageDisplayName(AppLanguage language)
	{
		return language switch
		{
			AppLanguage.English => UiText.Get("Shell.LanguageEnglish"),
			AppLanguage.TraditionalChinese => UiText.Get("Shell.LanguageTraditionalChinese"),
			_ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported App language.")
		};
	}

	private static bool ArePortableAccountFieldsEqual(
		AccountProfile first,
		AccountProfile second)
	{
		return string.Equals(
				first.DisplayName,
				second.DisplayName,
				StringComparison.Ordinal) &&
			(first.IsEnabled == second.IsEnabled) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				first.ProviderAccountIdentity,
				second.ProviderAccountIdentity);
	}

	private static string GetUsageSortModeText(UsageSortMode mode)
	{
		return mode switch
		{
			UsageSortMode.Manual => UiText.Get("Shell.Widget145"),
			UsageSortMode.Automatic => UiText.Get("Shell.Widget146"),
			_ => UiText.Get("Shell.Widget147")
		};
	}

	private static string GetProviderDisplayName(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => "Claude",
			ProviderKind.Codex => "Codex",
			ProviderKind.Copilot => "GitHub Copilot",
			ProviderKind.Antigravity => "Antigravity",
			ProviderKind.Grok => "Grok",
			_ => UiText.Get("Shell.Widget148")
		};
	}

	private static string GetUsageDisplayModeText(UsageDisplayMode mode)
	{
		return mode switch
		{
			UsageDisplayMode.Used => UiText.Get("Shell.Widget149"),
			UsageDisplayMode.Remaining => UiText.Get("Shell.Widget150"),
			_ => UiText.Get("Shell.Widget147")
		};
	}

	private static string GetPortableWidgetPreferencesPreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		if (imported.WidgetPreferences is null)
		{
			return UiText.Get("Shell.Widget151");
		}

		string importedText = GetPortableWidgetPreferencesText(
			imported.WidgetPreferences);

		return current.WidgetPreferences is null
			? importedText
			: $"{GetPortableWidgetPreferencesText(current.WidgetPreferences)} → " +
				importedText;
	}

	private static string GetPortableWidgetPreferencesText(
		PortableWidgetPreferences preferences)
	{
		string visibility = preferences.IsWidgetVisible ? UiText.Get("Shell.Widget152") : UiText.Get("Shell.Widget153");
		string collapsed = preferences.IsCollapsed ? UiText.Get("Shell.Widget154") : UiText.Get("Shell.Widget155");
		string topmost = preferences.IsTopmost ? UiText.Get("Shell.App096") : UiText.Get("Shell.Widget156");
		string height = preferences.IsHeightFollowingCardCount switch
		{
			true => UiText.Get("Shell.Widget157"),
			false => UiText.Get("Shell.Widget158"),
			null => UiText.Get("Shell.Widget159")
		};
		string corner = preferences.Corner switch
		{
			FloatingWidgetCorner.TopLeft => UiText.Get("Shell.Widget160"),
			FloatingWidgetCorner.TopRight => UiText.Get("Shell.Widget161"),
			FloatingWidgetCorner.BottomLeft => UiText.Get("Shell.Widget162"),
			FloatingWidgetCorner.BottomRight => UiText.Get("Shell.Widget163"),
			_ => UiText.Get("Shell.Widget164")
		};
		return string.Join(UiText.Get("Shell.ListSeparator"), visibility, collapsed, topmost, corner, height);
	}

	private static string GetPortableThemePreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		AppTheme? importedTheme = imported.WidgetPreferences?.Theme;

		if (importedTheme is null)
		{
			return UiText.Get("Shell.Widget165");
		}

		AppTheme? currentTheme = current.WidgetPreferences?.Theme;
		string importedText = GetPortableThemeText(importedTheme.Value);

		return currentTheme is null
			? importedText
			: $"{GetPortableThemeText(currentTheme.Value)} → {importedText}";
	}

	private static string GetPortableThemeText(AppTheme theme)
	{
		return theme switch
		{
			AppTheme.ClassicBlue => UiText.Get("Shell.Widget166"),
			AppTheme.Midnight => UiText.Get("Shell.Widget167"),
			AppTheme.Light => UiText.Get("Shell.Widget168"),
			AppTheme.Sakura => UiText.Get("Shell.Widget169"),
			_ => UiText.Get("Shell.Widget147")
		};
	}

	private void ReportPortableSettingsImportCompleted(
		int accountCount,
		int claudeQuotaRiskConfirmationCount,
		string accountSettingsMessage)
	{
		string message = CreatePortableSettingsImportCompletedMessage(
			accountCount,
			claudeQuotaRiskConfirmationCount,
			accountSettingsMessage);
		ReportInlineStatus(message);
		WpfMessageBox.Show(
			this,
			message,
			UiText.Get("Shell.Widget170"),
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private static string CreatePortableSettingsImportCompletedMessage(
		int accountCount,
		int claudeQuotaRiskConfirmationCount,
		string accountSettingsMessage)
	{
		string appliedStatus = string.IsNullOrWhiteSpace(accountSettingsMessage)
			? UiText.Format("Shell.Widget171", accountCount)
			: accountSettingsMessage.Trim();

		return $"{appliedStatus}\n\n" +
			$"{CreateClaudeQuotaRiskReconfirmationNotice(claudeQuotaRiskConfirmationCount)}\n\n" +
			$"{PortableSettingsReconnectNotice}\n\n" +
			UiText.Get("Shell.Widget172");
	}

	private void ReportPortableSettingsFailure(
		string diagnosticOperation,
		Exception exception)
	{
		string operation = diagnosticOperation switch
		{
			"import" => UiText.Get("Shell.Widget117"),
			"export" => UiText.Get("Shell.Widget115"),
			"undo" => UiText.Get("Shell.Widget130"),
			_ => throw new ArgumentOutOfRangeException(
				nameof(diagnosticOperation), diagnosticOperation, "Unknown settings operation.")
		};
		string reason = exception switch
		{
			PortableSettingsException => exception.Message,
			ArgumentException argumentException =>
				GetPortableSettingsArgumentFailureReason(argumentException),
			InvalidOperationException => exception.Message,
			_ => AppDiagnostics.GetUserFacingFailureReason(
				exception,
				UiText.Format("Shell.Widget173", operation))
		};
		AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
			$"portable-settings-{diagnosticOperation}",
			reason,
			exception);
		string diagnosticHint = diagnostic.WasWritten
			? UiText.Format("Shell.Widget174", diagnostic.FilePath)
			: string.Empty;
		string displayReason = UiText.Translate(reason);
		ReportInlineStatus(UiText.Format("Shell.Widget175", operation, displayReason));
		WpfMessageBox.Show(
			this,
			UiText.Format("Shell.Widget176", operation, displayReason, diagnosticHint),
			UiText.Format("Shell.Widget177", operation),
			MessageBoxButton.OK,
			MessageBoxImage.Error);
	}

	internal static string GetPortableSettingsArgumentFailureReason(
		ArgumentException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		string message = exception.Message.Trim();

		if (string.IsNullOrWhiteSpace(exception.ParamName))
		{
			return message;
		}

		int parameterNameIndex = message.LastIndexOf(
			exception.ParamName,
			StringComparison.Ordinal);

		if (parameterNameIndex < 0)
		{
			return message;
		}

		int parenthesisStart = message.LastIndexOf('(', parameterNameIndex);
		int lineStart = message.LastIndexOf('\n', parameterNameIndex);
		int technicalDetailsStart = Math.Max(parenthesisStart, lineStart);

		if (technicalDetailsStart < 0)
		{
			return message;
		}

		string userMessage = message[..technicalDetailsStart].TrimEnd();
		return string.IsNullOrWhiteSpace(userMessage)
			? message
			: userMessage;
	}

	internal static string GetAccountChangeFailureReason(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		return exception switch
		{
			AccountProfileStoreException => exception.Message,
			ArgumentException argumentException =>
				GetPortableSettingsArgumentFailureReason(argumentException),
			InvalidOperationException => exception.Message,
			_ => AppDiagnostics.GetUserFacingFailureReason(
				exception,
				UiText.Get("Shell.Widget178"))
		};
	}

	private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender is not System.Windows.Controls.Button button) ||
			(button.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.CanOpenWidgetAccountMenu ||
			(button.ContextMenu is null))
		{
			return;
		}

		button.ContextMenu.PlacementTarget = button;
		button.ContextMenu.Placement = PlacementMode.Bottom;
		button.ContextMenu.IsOpen = true;
		e.Handled = true;
	}

	private async void ConnectClaudeAccountButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		if (account.CanCancelProviderAccountConnection)
		{
			if (_accountConnectionCoordinator.TryCancelAccountConnection(account.Id))
			{
				ReportInlineStatus(
					UiText.Format("Shell.Widget179", account.AccountName),
					autoDismiss: false);
			}

			return;
		}

		if (!account.CanConnectProviderAccount)
		{
			return;
		}

		await _accountConnectionCoordinator.ConnectClaudeAsync(
			this,
			account,
			viewModel,
			ReportConnectionStatus);
	}

	private async void ConnectCodexAccountButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		if (account.CanCancelProviderAccountConnection)
		{
			if (_accountConnectionCoordinator.TryCancelAccountConnection(account.Id))
			{
				ReportInlineStatus(
					UiText.Format("Shell.Widget180", account.AccountName),
					autoDismiss: false);
			}

			return;
		}

		if (!account.CanConnectProviderAccount)
		{
			return;
		}

		await _accountConnectionCoordinator.ConnectCodexAsync(
			this,
			account,
			viewModel,
			ReportConnectionStatus);
	}

	private async void ConnectCopilotAccountButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		if (account.CanCancelProviderAccountConnection)
		{
			if (_accountConnectionCoordinator.TryCancelAccountConnection(account.Id))
			{
				ReportInlineStatus(
					UiText.Format("Shell.Widget181", account.AccountName),
					autoDismiss: false);
			}

			return;
		}

		if (!account.CanConnectProviderAccount)
		{
			return;
		}

		await _accountConnectionCoordinator.ConnectCopilotAsync(
			this,
			account,
			viewModel,
			ReportConnectionStatus,
			allowAccountSwitch: account.HasProviderAccountIdentity);
	}

	private async void ConnectGrokAccountButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		if (account.CanCancelProviderAccountConnection)
		{
			if (_accountConnectionCoordinator.TryCancelAccountConnection(account.Id))
			{
				ReportInlineStatus(
					UiText.Format("Shell.Widget182", account.AccountName),
					autoDismiss: false);
			}

			return;
		}

		if (!account.CanConnectProviderAccount)
		{
			return;
		}

		await _accountConnectionCoordinator.ConnectGrokAsync(
			this,
			account,
			viewModel,
			ReportConnectionStatus);
	}

	private async void ConnectAntigravityAccountButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.CanConnectProviderAccount)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		await _accountConnectionCoordinator.ConnectAntigravityAsync(
			this,
			account,
			viewModel,
			ReportConnectionStatus);
	}

	private async Task ConnectAccountAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel,
		bool hasConfirmedClaudeQuotaRisk,
		bool hasConfirmedAntigravityPreflight)
	{
		if (account.IsClaude)
		{
			await _accountConnectionCoordinator.ConnectClaudeAsync(
				this,
				account,
				viewModel,
				ReportConnectionStatus,
				hasConfirmedQuotaRisk: hasConfirmedClaudeQuotaRisk);
		}
		else if (account.IsCodex)
		{
			await _accountConnectionCoordinator.ConnectCodexAsync(
				this,
				account,
				viewModel,
				ReportConnectionStatus);
		}
		else if (account.IsCopilot)
		{
			await _accountConnectionCoordinator.ConnectCopilotAsync(
				this,
				account,
				viewModel,
				ReportConnectionStatus,
				allowAccountSwitch: account.HasProviderAccountIdentity);
		}
		else if (account.IsGrok)
		{
			await _accountConnectionCoordinator.ConnectGrokAsync(
				this,
				account,
				viewModel,
				ReportConnectionStatus);
		}
		else if (account.IsAntigravity)
		{
			await _accountConnectionCoordinator.ConnectAntigravityAsync(
				this,
				account,
				viewModel,
				ReportConnectionStatus,
				hasConfirmedPreflight:
					hasConfirmedAntigravityPreflight);
		}
	}

	private async void DeleteAccountMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement ||
			!account.CanChangeAccountSettings)
		{
			return;
		}

		ReportInlineStatus(string.Empty);
		MessageBoxResult result = WpfMessageBox.Show(
			this,
			account.AccountDeletionConfirmationText,
			UiText.Get("Shell.Widget183"),
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No);

		if (result == MessageBoxResult.Yes)
		{
			await ExecuteAccountChangeAsync(
				() => viewModel.RemoveAccountAsync(account.Id));
		}
	}

	private async void EditAccountMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement ||
			!account.CanChangeAccountSettings)
		{
			return;
		}

		ReportInlineStatus(string.Empty);
		AccountEditorWindow editorWindow = new(
			account.Profile,
			currentProviderAccountDisplayTextProvider:
				() => account.ProviderAccountDisplayText,
			hasCurrentProviderAccountIdentity:
				account.HasProviderAccountIdentity,
			currentProviderAccountActionTextProvider: () => account.Provider switch
			{
				ProviderKind.Claude => account.ClaudeAccountActionText,
				ProviderKind.Codex => account.CodexAccountActionText,
				ProviderKind.Copilot => account.CopilotAccountActionText,
				ProviderKind.Antigravity =>
					account.AntigravityAccountActionText,
				ProviderKind.Grok => account.GrokAccountActionText,
				_ => string.Empty
			},
			canConnectCurrentProviderAccount:
				account.CanConnectProviderAccountAfterSave &&
				!viewModel.IsAccountCleanupBlocked(
					account.Id,
					account.Provider))
		{
			Owner = this
		};

		if ((editorWindow.ShowDialog() != true) ||
			(editorWindow.EditedProfile is not AccountProfile editedProfile))
		{
			return;
		}

		bool wasUpdated = await ExecuteAccountChangeAsync(
			() => viewModel.UpdateAccountAsync(editedProfile));

		if (wasUpdated &&
			editorWindow.ConnectAfterSave &&
			viewModel.Accounts.FirstOrDefault(
				candidate => candidate.Id == editedProfile.Id) is
				AccountUsageViewModel updatedAccount)
		{
			await ConnectAccountAsync(
				updatedAccount,
				viewModel,
				hasConfirmedClaudeQuotaRisk: false,
				hasConfirmedAntigravityPreflight: false);
		}
	}

	private async Task<bool> ExecuteAccountChangeAsync(Func<Task> operation)
	{
		try
		{
			await operation();

			if (DataContext is DashboardViewModel viewModel)
			{
				ReportInlineStatus(viewModel.AccountSettingsMessage);
			}

			return true;
		}
		catch (AccountProfileStoreException exception) when (exception.HasCommittedChanges)
		{
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"floating-window-account-change",
				"result=committed-with-warning",
				exception);
			string message =
				(DataContext is DashboardViewModel viewModel) &&
				!string.IsNullOrWhiteSpace(viewModel.AccountSettingsMessage)
					? viewModel.AccountSettingsMessage
					: exception.Message;
			string diagnosticHint = diagnostic.WasWritten
				? UiText.Format("Shell.Widget174", diagnostic.FilePath)
				: string.Empty;
			ReportInlineStatus(message);
			WpfMessageBox.Show(
				this,
				$"{message}{diagnosticHint}",
				UiText.Get("Shell.Widget184"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			return true;
		}
		catch (Exception exception)
		{
			string reason = GetAccountChangeFailureReason(exception);
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"floating-window-account-change",
				"result=failed",
				exception);
			string diagnosticHint = diagnostic.WasWritten
				? UiText.Format("Shell.Widget174", diagnostic.FilePath)
				: string.Empty;
			ReportInlineStatus(UiText.Format("Shell.Widget185", reason));
			WpfMessageBox.Show(
				this,
				UiText.Format("Shell.Widget186", reason, diagnosticHint),
				UiText.Get("Shell.Widget187"),
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			return false;
		}
	}

	private async Task ExecutePreferenceChangeAsync(Func<Task> operation)
	{
		try
		{
			await operation();

			if (DataContext is DashboardViewModel viewModel)
			{
				ReportInlineStatus(viewModel.AccountSettingsMessage);
			}
		}
		catch (Exception exception)
		{
			string reason = AppDiagnostics.GetUserFacingFailureReason(
				exception,
				UiText.Get("Shell.Widget188"));
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"floating-window-preference-change",
				reason,
				exception);
			string diagnosticHint = diagnostic.WasWritten
				? UiText.Format("Shell.Widget174", diagnostic.FilePath)
				: string.Empty;
			WpfMessageBox.Show(
				this,
				UiText.Format("Shell.Widget189", reason, diagnosticHint),
				UiText.Get("Shell.Widget190"),
				MessageBoxButton.OK,
				MessageBoxImage.Error);
		}
	}

	private async void RecoverUsageButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.CanInvokeRecoveryAction)
		{
			return;
		}

		ReportInlineStatus(string.Empty);

		if (account.CanCancelProviderAccountConnection)
		{
			if (_accountConnectionCoordinator.TryCancelAccountConnection(account.Id))
			{
				ReportInlineStatus(
					UiText.Format("Shell.Widget191", account.AccountName, account.ProviderName),
					autoDismiss: false);
			}

			return;
		}

		switch (account.RecoveryAction)
		{
			case UsageRecoveryAction.Retry:
				await RefreshAccountUsageWithFeedbackAsync(account, viewModel);
				break;
			case UsageRecoveryAction.ReconfigureUsageSource:
				if (account.IsAntigravity)
				{
					await _accountConnectionCoordinator.ConnectAntigravityAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus);
				}
				else
				{
					ShowRecoveryGuidance(account);
				}

				break;
			case UsageRecoveryAction.ConfirmSubscription:
			case UsageRecoveryAction.ConnectAccount:
			case UsageRecoveryAction.SwitchAccount:
				if (account.IsClaude)
				{
					await _accountConnectionCoordinator.ConnectClaudeAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus);
				}
				else if (account.IsCodex)
				{
					await _accountConnectionCoordinator.ConnectCodexAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus);
				}
				else if (account.IsCopilot)
				{
					await _accountConnectionCoordinator.ConnectCopilotAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus,
						allowAccountSwitch:
							account.RecoveryAction ==
								UsageRecoveryAction.SwitchAccount);
				}
				else if (account.IsGrok)
				{
					await _accountConnectionCoordinator.ConnectGrokAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus);
				}
				else if (account.IsAntigravity)
				{
					await _accountConnectionCoordinator.ConnectAntigravityAsync(
						this,
						account,
						viewModel,
						ReportConnectionStatus);
				}

				break;
			case UsageRecoveryAction.RestartApplication:
				if (System.Windows.Application.Current is App app)
				{
					app.Restart();
				}

				break;
			case UsageRecoveryAction.RevalidateUsage:
				await RevalidateUsageWithFeedbackAsync(account, viewModel);
				break;
			case UsageRecoveryAction.InstallOrUpdate:
			case UsageRecoveryAction.UpdateApplication:
				ShowRecoveryGuidance(account);
				break;
			case UsageRecoveryAction.None:
			default:
				break;
		}
	}

	private void CopyRecoveryGuidanceButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account))
		{
			return;
		}

		string guidance = CreateRecoveryGuidance(account);
		ReportInlineStatus(TryCopyRecoveryGuidance(guidance)
			? UiText.Get("Shell.Widget192")
			: UiText.Format("Shell.Widget193", guidance));
	}

	private async void RetryRecoveryButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.CanExecuteRecoveryAction)
		{
			return;
		}

		await RefreshAccountUsageWithFeedbackAsync(account, viewModel);
	}

	private void ShowRecoveryGuidance(AccountUsageViewModel account)
	{
		string guidance = CreateRecoveryGuidance(account);
		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open();

		if (result == LocalUserGuideOpenResult.Opened)
		{
			ReportInlineStatus(UiText.Get("Shell.Widget194"));
			return;
		}

		ReportInlineStatus(
			UiText.Format("Shell.Widget195", LocalUserGuideLauncher.GetFailureMessage(result), guidance),
			autoDismiss: false,
			preserveAgainstAsyncUpdates: true);
	}

	private void ReportConnectionStatus(string message)
	{
		ReportAsyncStatus(message);
	}

	private void ReportAsyncStatus(string message, bool autoDismiss = true)
	{
		if (!LiveRegionAnnouncementPolicy.ShouldReplaceInlineStatus(
				_hasPersistentInlineStatus))
		{
			if (!string.IsNullOrWhiteSpace(message))
			{
				RaiseNotification(message);
			}

			return;
		}

		ReportInlineStatus(message, autoDismiss);
	}

	private void ReportAccountStatusAnnouncement(string message)
	{
		if (!CanAnnounceLiveRegion())
		{
			return;
		}

		if (!LiveRegionAnnouncementPolicy.ShouldReplaceInlineStatus(
				_hasPersistentInlineStatus))
		{
			RaiseNotification(message);
			return;
		}

		ReportInlineStatus(message);
	}

	private void ReportInlineStatus(
		string message,
		bool autoDismiss = true,
		bool preserveAgainstAsyncUpdates = false)
	{
		_inlineStatusTimer.Stop();
		_isShellPreferencesFailureInlineStatus = false;
		_hasPersistentInlineStatus =
			!string.IsNullOrWhiteSpace(message) && preserveAgainstAsyncUpdates;
		_inlineStatusSource = message;
		InlineStatusTextBlock.Text = UiText.Translate(message);
		InlineStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(message)
			? Visibility.Collapsed
			: Visibility.Visible;
		UpdateExpandedFooterVisibility();

		if (!string.IsNullOrWhiteSpace(message))
		{
			if (LiveRegionAnnouncementPolicy.ShouldAnnounceInlineStatus(
				message,
				_lastViewModelAnnouncement,
				_lastViewModelAnnouncementAt,
				DateTimeOffset.UtcNow))
			{
				RaiseLiveRegionChanged(InlineStatusTextBlock);
			}

			if (autoDismiss)
			{
				_inlineStatusTimer.Start();
			}
		}
	}

	private void ViewModel_PropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (sender is not DashboardViewModel viewModel)
		{
			return;
		}

		if ((e.PropertyName == nameof(DashboardViewModel.CompactRefreshStatus)) ||
			(e.PropertyName == nameof(DashboardViewModel.IsRefreshing)))
		{
			UpdateCompactRefreshStatus(viewModel);
		}

		if (e.PropertyName == nameof(DashboardViewModel.IsRefreshing))
		{
			TryAnnouncePendingUpdateBanner();
		}

		if (!CanAnnounceLiveRegion())
		{
			return;
		}

		if (e.PropertyName == nameof(DashboardViewModel.CompactRefreshStatus))
		{
			_lastViewModelAnnouncement = viewModel.LastRefreshText;
			_lastViewModelAnnouncementAt = DateTimeOffset.UtcNow;
			RaiseNotification(
				viewModel.LastRefreshText,
				"RefreshStatusAnnouncement");
			return;
		}

		if ((e.PropertyName == nameof(DashboardViewModel.IsRefreshing)) &&
			viewModel.IsRefreshing)
		{
			_lastViewModelAnnouncement = UiText.Get("Shell.Widget196");
			_lastViewModelAnnouncementAt = DateTimeOffset.UtcNow;
			RaiseNotification(UiText.Get("Shell.Widget196"), "RefreshStatusAnnouncement");
			return;
		}

		(FrameworkElement? LiveRegion, string? Message) announcement =
			e.PropertyName switch
			{
				nameof(DashboardViewModel.AccountSettingsHealthMessage) =>
					(
						AccountSettingsHealthTextBlock,
						viewModel.AccountSettingsHealthMessage),
				_ => (null, null)
			};

		if ((announcement.LiveRegion is not null) &&
			!string.IsNullOrWhiteSpace(announcement.Message))
		{
			_lastViewModelAnnouncement = announcement.Message;
			_lastViewModelAnnouncementAt = DateTimeOffset.UtcNow;
			RaiseLiveRegionChanged(announcement.LiveRegion);
		}
	}

	private bool CanAnnounceLiveRegion()
	{
		return IsVisible && IsActive;
	}

	private void RaiseLiveRegionChanged(FrameworkElement liveRegion)
	{
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				if (!liveRegion.IsVisible ||
					!CanAnnounceLiveRegion())
				{
					return;
				}

				AutomationPeer? peer =
					UIElementAutomationPeer.FromElement(liveRegion) ??
					UIElementAutomationPeer.CreatePeerForElement(liveRegion);
				peer?.RaiseAutomationEvent(
					AutomationEvents.LiveRegionChanged);
			},
			DispatcherPriority.Loaded);
	}

	private void RaiseNotification(
		string message,
		string activityId = "AccountStatusAnnouncement")
	{
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				if (!CanAnnounceLiveRegion())
				{
					return;
				}

				AutomationPeer? peer =
					UIElementAutomationPeer.FromElement(this) ??
					UIElementAutomationPeer.CreatePeerForElement(this);
				peer?.RaiseNotificationEvent(
					AutomationNotificationKind.Other,
					AutomationNotificationProcessing.MostRecent,
					message,
					activityId);
			},
			DispatcherPriority.Loaded);
	}

	private void CompactRefreshStatusTimer_Tick(object? sender, EventArgs e)
	{
		_compactRefreshStatusTimer.Stop();
		CompactRefreshTextBlock.Visibility = Visibility.Collapsed;
		UpdateExpandedFooterVisibility();
	}

	private void InlineStatusTimer_Tick(object? sender, EventArgs e)
	{
		if (_hasPersistentInlineStatus)
		{
			return;
		}

		ReportInlineStatus(string.Empty);
	}

	private void UpdateCompactRefreshStatus(DashboardViewModel viewModel)
	{
		_compactRefreshStatusTimer.Stop();
		CompactRefreshStatus status = viewModel.CompactRefreshStatus;
		bool shouldShow =
			!viewModel.IsRefreshing &&
			!string.IsNullOrWhiteSpace(status.Text);
		CompactRefreshTextBlock.Visibility = shouldShow
			? Visibility.Visible
			: Visibility.Collapsed;

		if (shouldShow && status.IsTransient)
		{
			_compactRefreshStatusTimer.Start();
		}

		UpdateExpandedFooterVisibility();
	}

	private void UpdateExpandedFooterVisibility()
	{
		bool isRefreshing =
			(DataContext is DashboardViewModel viewModel) &&
			viewModel.IsRefreshing;
		ExpandedFooter.Visibility =
			isRefreshing ||
			(CompactRefreshTextBlock.Visibility == Visibility.Visible) ||
			(InlineStatusTextBlock.Visibility == Visibility.Visible)
				? Visibility.Visible
				: Visibility.Collapsed;
	}

	private static string CreateRecoveryGuidance(AccountUsageViewModel account)
	{
		if (account.IsAntigravity &&
			(account.RecoveryAction == UsageRecoveryAction.UpdateApplication))
		{
			return UiText.Get("Shell.Widget197");
		}

		if (account.IsAntigravity &&
			(account.RecoveryAction == UsageRecoveryAction.InstallOrUpdate))
		{
			return UiText.Get("Shell.Widget198");
		}

		if (account.IsAntigravity &&
			(account.RecoveryAction ==
				UsageRecoveryAction.ReconfigureUsageSource))
		{
			return UiText.Format("Shell.Widget199", account.AntigravityAccountActionText);
		}

		return account.Provider switch
		{
			ProviderKind.Claude =>
				UiText.Get("Shell.Widget200"),
			ProviderKind.Codex =>
				UiText.Get("Shell.Widget201"),
			ProviderKind.Copilot =>
				UiText.Get("Shell.Widget202"),
			ProviderKind.Grok =>
				UiText.Get("Shell.Widget203"),
			ProviderKind.Antigravity =>
				UiText.Get("Shell.Widget204"),
			_ =>
				UiText.Get("Shell.Widget205")
		};
	}

	private static bool TryCopyRecoveryGuidance(string guidance)
	{
		try
		{
			WpfClipboard.SetText(guidance);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void GlobalMenuButton_Click(object sender, RoutedEventArgs e)
	{
		if ((sender is not System.Windows.Controls.Button button) ||
			(button.ContextMenu is null))
		{
			return;
		}

		button.ContextMenu.PlacementTarget = button;
		button.ContextMenu.Placement = PlacementMode.Bottom;
		button.ContextMenu.IsOpen = true;
		e.Handled = true;
	}

	private void ApplyCurrentPlacement(bool queueDpiCorrection = true)
	{
		if (!_hasPlacement ||
			_isApplyingPlacement ||
			_isCollapsedDragPending ||
			_isCollapsedDragging ||
			_isExpandedDragging ||
			!IsVisible)
		{
			return;
		}

		IntPtr windowHandle = new WindowInteropHelper(this).Handle;
		(DrawingRectangle workingArea, int margin, DpiScale dpi) =
			GetPlacementLayoutContext(windowHandle);
		UpdateExpandedBounds(workingArea, margin, dpi);

		if (!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds))
		{
			return;
		}

		DrawingPoint topLeft = GetCurrentTopLeft(
			windowBounds.Size,
			workingArea,
			margin);

		_isApplyingPlacement = true;

		bool wasPositioned;

		try
		{
			wasPositioned = SetWindowPos(
				windowHandle,
				IntPtr.Zero,
				topLeft.X,
				topLeft.Y,
				0,
				0,
				SetWindowPositionNoActivate |
				SetWindowPositionNoSize |
				SetWindowPositionNoZOrder);
		}
		finally
		{
			_isApplyingPlacement = false;
		}

		if (wasPositioned && queueDpiCorrection)
		{
			QueueDpiPlacementCorrection();
		}
	}

	private DrawingPoint GetCurrentTopLeft(
		DrawingSize windowSize,
		DrawingRectangle workingArea,
		int margin)
	{
		if (_isCollapsed &&
			(_collapsedPositionXRatio is double xRatio) &&
			(_collapsedPositionYRatio is double yRatio))
		{
			return FloatingWidgetPlacement.GetTopLeftFromRatios(
				windowSize,
				workingArea,
				margin,
				xRatio,
				yRatio);
		}

		return FloatingWidgetPlacement.GetTopLeft(
			windowSize,
			workingArea,
			_corner,
			margin);
	}

	private void CollapseButton_Click(object sender, RoutedEventArgs e)
	{
		bool wasInvokedByPointer =
			PointerFocusRelease.WasInvokedByPointer(sender);
		SetCollapsed(
			true,
			focusTransitionMode: wasInvokedByPointer
				? FocusTransitionMode.Release
				: FocusTransitionMode.Transfer,
			pointerFocusOwner: wasInvokedByPointer
				? PointerFocusRelease.GetPointerFocusOwner(sender)
				: null);
	}

	private void AccountScrollViewer_LostMouseCapture(
		object sender,
		System.Windows.Input.MouseEventArgs e)
	{
		if (!_accountScrollDragSession.IsDragging ||
			!ReferenceEquals(e.OriginalSource, AccountScrollViewer))
		{
			return;
		}

		ResetAccountScrollDrag(releaseMouseCapture: false);
	}

	private void AccountScrollViewer_PreviewMouseLeftButtonDown(
		object sender,
		MouseButtonEventArgs e)
	{
		if ((e.LeftButton != MouseButtonState.Pressed) ||
			(e.StylusDevice is not null) ||
			(AccountScrollViewer.ScrollableHeight <= 0) ||
			IsScrollBarInputSource(e.OriginalSource as DependencyObject))
		{
			return;
		}

		System.Windows.Point pointer = e.GetPosition(AccountScrollViewer);
		_accountScrollDragSession.Begin(
			pointer.Y,
			AccountScrollViewer.VerticalOffset);
	}

	private void AccountScrollViewer_PreviewMouseLeftButtonUp(
		object sender,
		MouseButtonEventArgs e)
	{
		if (!_accountScrollDragSession.IsActive)
		{
			return;
		}

		bool wasDragging = _accountScrollDragSession.End();
		ResetAccountScrollDrag(releaseMouseCapture: true);

		if (wasDragging)
		{
			e.Handled = true;
		}
	}

	private void AccountScrollViewer_PreviewMouseMove(
		object sender,
		System.Windows.Input.MouseEventArgs e)
	{
		if (!_accountScrollDragSession.IsActive)
		{
			return;
		}

		System.Windows.Point pointer = e.GetPosition(AccountScrollViewer);
		VerticalDragScrollUpdate update = _accountScrollDragSession.Move(
			pointer.Y,
			e.LeftButton == MouseButtonState.Pressed,
			SystemParameters.MinimumVerticalDragDistance,
			AccountScrollViewer.ScrollableHeight);

		if (!_accountScrollDragSession.IsActive)
		{
			ResetAccountScrollDrag(releaseMouseCapture: true);
			return;
		}

		if (!update.ShouldScroll)
		{
			return;
		}

		if (update.ShouldCaptureMouse &&
			!AccountScrollViewer.CaptureMouse())
		{
			ResetAccountScrollDrag(releaseMouseCapture: false);
			return;
		}

		AccountScrollViewer.Cursor = System.Windows.Input.Cursors.ScrollNS;
		AccountScrollViewer.ScrollToVerticalOffset(update.VerticalOffset);
		e.Handled = true;
	}

	private static DependencyObject? GetInputParent(DependencyObject element)
	{
		return element is Visual
			? VisualTreeHelper.GetParent(element)
			: LogicalTreeHelper.GetParent(element);
	}

	private bool IsScrollBarInputSource(DependencyObject? source)
	{
		DependencyObject? current = source;

		while ((current is not null) &&
			!ReferenceEquals(current, AccountScrollViewer))
		{
			if (current is System.Windows.Controls.Primitives.ScrollBar)
			{
				return true;
			}

			current = GetInputParent(current);
		}

		return false;
	}

	private void ResetAccountScrollDrag(bool releaseMouseCapture)
	{
		_accountScrollDragSession.Cancel();
		AccountScrollViewer.ClearValue(FrameworkElement.CursorProperty);

		if (releaseMouseCapture &&
			ReferenceEquals(Mouse.Captured, AccountScrollViewer))
		{
			AccountScrollViewer.ReleaseMouseCapture();
		}
	}

	private void CornerMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.Tag is not string cornerName) ||
			!Enum.TryParse(cornerName, ignoreCase: false, out FloatingWidgetCorner corner))
		{
			return;
		}

		bool didPlacementChange = (_corner != corner) ||
			_collapsedPositionXRatio.HasValue;
		_collapsedPositionXRatio = null;
		_collapsedPositionYRatio = null;
		SetCorner(corner, notifyPreferences: false);
		_hasPlacement = true;
		ApplyCurrentPlacement();
		if (didPlacementChange)
		{
			NotifyPreferencesChanged();
		}
	}

	private void HeightFollowsCardCountMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem menuItem)
		{
			return;
		}

		SetHeightFollowingCardCount(menuItem.IsChecked);
	}

	private void SetHeightFollowingCardCount(
		bool isHeightFollowingCardCount,
		bool notifyPreferences = true)
	{
		if (_isHeightFollowingCardCount == isHeightFollowingCardCount)
		{
			UpdateHeightFollowsCardCountMenuItem();
			return;
		}

		_isHeightFollowingCardCount = isHeightFollowingCardCount;
		UpdateHeightFollowsCardCountMenuItem();

		if (!_isCollapsed)
		{
			if (IsVisible)
			{
				ApplyCurrentPlacement();
			}
			else
			{
				double targetWidth = double.IsNaN(Width) || (Width <= 0)
					? ExpandedWidth
					: Width;
				double maximumHeight =
					!double.IsPositiveInfinity(MaxHeight) && (MaxHeight > 0)
						? MaxHeight
						: !double.IsNaN(Height) && (Height > 0)
							? Height
							: ExpandedDefaultHeight;
				UpdateExpandedSize(targetWidth, maximumHeight);
			}
		}

		if (notifyPreferences)
		{
			NotifyPreferencesChanged();
		}
	}

	private void UpdateHeightFollowsCardCountMenuItem()
	{
		HeightFollowsCardCountMenuItem.IsChecked =
			_isHeightFollowingCardCount;
	}

	private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.Tag is not string themeName) ||
			!Enum.TryParse(themeName, ignoreCase: false, out AppTheme theme) ||
			!Enum.IsDefined(theme))
		{
			return;
		}

		if (_theme == theme)
		{
			UpdateThemeMenuItems();
			return;
		}

		_theme = theme;
		UpdateThemeMenuItems();
		NotifyPreferencesChanged();
	}

	private void UpdateThemeMenuItems()
	{
		ClassicBlueThemeMenuItem.IsChecked = _theme == AppTheme.ClassicBlue;
		MidnightThemeMenuItem.IsChecked = _theme == AppTheme.Midnight;
		LightThemeMenuItem.IsChecked = _theme == AppTheme.Light;
		SakuraThemeMenuItem.IsChecked = _theme == AppTheme.Sakura;
	}

	private void CollapsedButton_Click(object sender, RoutedEventArgs e)
	{
		bool wasInvokedByPointer =
			PointerFocusRelease.WasInvokedByPointer(sender);
		SetCollapsed(
			false,
			focusTransitionMode: wasInvokedByPointer
				? FocusTransitionMode.Release
				: FocusTransitionMode.Transfer,
			pointerFocusOwner: wasInvokedByPointer
				? PointerFocusRelease.GetPointerFocusOwner(sender)
				: null);
	}

	private void CollapsedButton_PreviewMouseLeftButtonDown(
		object sender,
		MouseButtonEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}

		IntPtr windowHandle = new WindowInteropHelper(this).Handle;

		if (!GetPhysicalCursorPos(out NativePoint cursorPosition) ||
			!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds) ||
			(windowBounds.Width <= 0) ||
			(windowBounds.Height <= 0))
		{
			return;
		}

		_collapsedDragStart = new DrawingPoint(
			cursorPosition.X,
			cursorPosition.Y);
		_collapsedDragOffsetXRatio = Math.Clamp(
			(cursorPosition.X - windowBounds.Left) / (double)windowBounds.Width,
			0,
			1);
		_collapsedDragOffsetYRatio = Math.Clamp(
			(cursorPosition.Y - windowBounds.Top) / (double)windowBounds.Height,
			0,
			1);
		_isCollapsedDragPending = true;
		_isCollapsedDragging = false;
		_hasCollapsedDragFailure = false;

		if (!CollapsedButton.CaptureMouse())
		{
			_isCollapsedDragPending = false;
			return;
		}

		e.Handled = true;
	}

	private void CollapsedButton_PreviewMouseLeftButtonUp(
		object sender,
		MouseButtonEventArgs e)
	{
		if (!_isCollapsedDragPending && !_isCollapsedDragging)
		{
			return;
		}

		bool shouldExpand = _isCollapsedDragPending;
		bool didDrag = _isCollapsedDragging;

		if (didDrag && GetPhysicalCursorPos(out NativePoint cursorPosition))
		{
			MoveCollapsedWindowToCursor(cursorPosition);
		}

		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;
		CollapsedButton.ReleaseMouseCapture();
		e.Handled = true;

		if (didDrag)
		{
			SaveCollapsedPosition();
		}
		else if (shouldExpand)
		{
			SetCollapsed(
				false,
				focusTransitionMode: FocusTransitionMode.Release,
				pointerFocusOwner: CollapsedButton);
		}
	}

	private void CollapsedButton_PreviewMouseMove(
		object sender,
		System.Windows.Input.MouseEventArgs e)
	{
		if ((!_isCollapsedDragPending && !_isCollapsedDragging) ||
			(e.LeftButton != MouseButtonState.Pressed) ||
			!GetPhysicalCursorPos(out NativePoint cursorPosition))
		{
			return;
		}

		if (_isCollapsedDragPending)
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			double horizontalThreshold =
				SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX;
			double verticalThreshold =
				SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
			bool hasExceededDragThreshold =
				(Math.Abs(cursorPosition.X - _collapsedDragStart.X) >=
					horizontalThreshold) ||
				(Math.Abs(cursorPosition.Y - _collapsedDragStart.Y) >=
					verticalThreshold);

			if (!hasExceededDragThreshold)
			{
				return;
			}

			_isCollapsedDragPending = false;
			_isCollapsedDragging = true;
		}

		MoveCollapsedWindowToCursor(cursorPosition);
		e.Handled = true;
	}

	private void MoveCollapsedWindowToCursor(NativePoint cursorPosition)
	{
		IntPtr windowHandle = new WindowInteropHelper(this).Handle;

		if (!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds))
		{
			ReportCollapsedDragFailure(windowHandle, UiText.Get("Shell.Widget206"));
			return;
		}

		int x = cursorPosition.X - (int)Math.Round(
			windowBounds.Width * _collapsedDragOffsetXRatio);
		int y = cursorPosition.Y - (int)Math.Round(
			windowBounds.Height * _collapsedDragOffsetYRatio);
		FormsScreen targetScreen = FormsScreen.FromPoint(
			new DrawingPoint(cursorPosition.X, cursorPosition.Y));
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		int margin = Math.Max(
			0,
			(int)Math.Round(CornerMargin * dpi.DpiScaleX));
		DrawingPoint topLeft = FloatingWidgetPlacement.ClampTopLeft(
			new DrawingPoint(x, y),
			windowBounds.Size,
			targetScreen.WorkingArea,
			margin);
		if (!SetWindowPos(
			windowHandle,
			IntPtr.Zero,
			topLeft.X,
			topLeft.Y,
			0,
			0,
			SetWindowPositionNoActivate |
			SetWindowPositionNoSize |
			SetWindowPositionNoZOrder))
		{
			ReportCollapsedDragFailure(windowHandle, UiText.Get("Shell.Widget207"));
		}
	}

	private void SaveCollapsedPosition()
	{
		IntPtr windowHandle = new WindowInteropHelper(this).Handle;
		if (!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds))
		{
			ReportCollapsedDragFailure(windowHandle, UiText.Get("Shell.Widget208"));
			return;
		}

		FormsScreen targetScreen = FormsScreen.FromHandle(windowHandle);
		DrawingRectangle workingArea = targetScreen.WorkingArea;
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		int margin = Math.Max(
			0,
			(int)Math.Round(CornerMargin * dpi.DpiScaleX));
		DrawingPoint topLeft = FloatingWidgetPlacement.ClampTopLeft(
			windowBounds.Location,
			windowBounds.Size,
			workingArea,
			margin);
		if ((topLeft != windowBounds.Location) &&
			!SetWindowPos(
				windowHandle,
				IntPtr.Zero,
				topLeft.X,
				topLeft.Y,
				0,
				0,
				SetWindowPositionNoActivate |
				SetWindowPositionNoSize |
				SetWindowPositionNoZOrder))
		{
			ReportCollapsedDragFailure(windowHandle, UiText.Get("Shell.Widget209"));
			return;
		}

		(_collapsedPositionXRatio, _collapsedPositionYRatio) =
			FloatingWidgetPlacement.GetPositionRatios(
				topLeft,
				windowBounds.Size,
				workingArea,
				margin);
		_screenDeviceName = targetScreen.DeviceName;
		_corner = FloatingWidgetPlacement.GetNearestCorner(
			new DrawingRectangle(topLeft, windowBounds.Size),
			workingArea);
		UpdateCornerDependentVisuals();
		_hasPlacement = true;
		NotifyPreferencesChanged();
		ApplyCurrentPlacement();
	}

	private void ReportCollapsedDragFailure(
		IntPtr windowHandle,
		string operation)
	{
		if (_hasCollapsedDragFailure)
		{
			return;
		}

		_hasCollapsedDragFailure = true;
		AppDiagnostics.TryWrite(
			"floating-icon-position",
			UiText.Format("Shell.Widget210", operation, windowHandle),
			new Win32Exception(Marshal.GetLastWin32Error()));
	}

	private void CollapsedButton_LostMouseCapture(
		object sender,
		System.Windows.Input.MouseEventArgs e)
	{
		if (!_isCollapsedDragPending && !_isCollapsedDragging)
		{
			return;
		}

		bool didDrag = _isCollapsedDragging;
		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;

		if (didDrag)
		{
			SaveCollapsedPosition();
		}
	}

	private void ExpandedHeader_MouseLeftButtonDown(
		object sender,
		MouseButtonEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}

		_isExpandedDragging = true;

		try
		{
			DragMove();
		}
		catch (InvalidOperationException)
		{
			return;
		}
		finally
		{
			_isExpandedDragging = false;
		}

		SnapToNearestCorner(preserveVerticalCorner: true);
	}

	private DrawingRectangle GetTargetWorkingArea(IntPtr windowHandle)
	{
		FormsScreen? targetScreen = FormsScreen.AllScreens.FirstOrDefault(
			screen => string.Equals(
				screen.DeviceName,
				_screenDeviceName,
				StringComparison.OrdinalIgnoreCase));

		if (targetScreen is null)
		{
			targetScreen = FormsScreen.FromHandle(windowHandle);

			if (!string.Equals(
				_screenDeviceName,
				targetScreen.DeviceName,
				StringComparison.OrdinalIgnoreCase))
			{
				_screenDeviceName = targetScreen.DeviceName;
				NotifyPreferencesChanged();
			}
		}

		return targetScreen.WorkingArea;
	}

	private void HideButton_Click(object sender, RoutedEventArgs e)
	{
		Hide();
	}

	private async void MoveAccountDownButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		await MoveAccountAsync(sender, 1);
	}

	private async void MoveAccountUpButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		await MoveAccountAsync(sender, -1);
	}

	private async Task MoveAccountAsync(object sender, int offset)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel))
		{
			return;
		}

		bool wasMoved = await ExecuteAccountChangeAsync(
			() => viewModel.MoveAccountAsync(account.Id, offset));

		if (!wasMoved)
		{
			return;
		}

		_ = Dispatcher.BeginInvoke(
			() =>
			{
				if (AccountItemsControl.ItemContainerGenerator.ContainerFromItem(account) is
					FrameworkElement accountContainer)
				{
					accountContainer.BringIntoView();
				}
			},
			DispatcherPriority.Loaded);
	}

	private void OpenAboutMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (System.Windows.Application.Current is App app)
		{
			app.ShowAboutWindow();
		}
	}

	private void TryAnnouncePendingUpdateBanner()
	{
		if (!_updateBannerAnnouncementTracker.TryBeginDispatch(
				CanAnnounceUpdateBanner(),
				out UpdateBannerAnnouncementDispatch dispatch))
		{
			return;
		}

		_ = Dispatcher.BeginInvoke(
			() => CompleteUpdateBannerAnnouncement(dispatch),
			DispatcherPriority.Loaded);
	}

	private bool CanAnnounceUpdateBanner()
	{
		return !_isCollapsed &&
			(UpdateBanner.Visibility == Visibility.Visible) &&
			(DataContext is DashboardViewModel viewModel) &&
			!viewModel.IsRefreshing &&
			CanAnnounceLiveRegion();
	}

	private void CompleteUpdateBannerAnnouncement(
		UpdateBannerAnnouncementDispatch dispatch)
	{
		bool isCurrent =
			_updateBannerAnnouncementTracker.IsCurrent(dispatch);
		bool wasRaised = isCurrent && TryRaiseUpdateBannerLiveRegion();
		_updateBannerAnnouncementTracker.CompleteDispatch(
			dispatch,
			wasRaised);
		if (!isCurrent)
		{
			TryAnnouncePendingUpdateBanner();
		}
	}

	private bool TryRaiseUpdateBannerLiveRegion()
	{
		if (!CanAnnounceUpdateBanner() || !UpdateBannerTextBlock.IsVisible)
		{
			return false;
		}

		AutomationPeer? peer =
			UIElementAutomationPeer.FromElement(UpdateBannerTextBlock) ??
			UIElementAutomationPeer.CreatePeerForElement(
				UpdateBannerTextBlock);
		if (peer is null)
		{
			return false;
		}

		peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
		return true;
	}

	private void OpenUserGuideMenuItem_Click(object sender, RoutedEventArgs e)
	{
		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open();

		if (result == LocalUserGuideOpenResult.Opened)
		{
			ReportInlineStatus(UiText.Get("Shell.Widget194"));
			return;
		}

		ReportInlineStatus(
			LocalUserGuideLauncher.GetFailureMessage(result),
			autoDismiss: false,
			preserveAgainstAsyncUpdates: true);
	}

	private void PinButton_Checked(object sender, RoutedEventArgs e)
	{
		Topmost = true;
		NotifyPreferencesChanged();
	}

	private void PinButton_Unchecked(object sender, RoutedEventArgs e)
	{
		Topmost = false;
		NotifyPreferencesChanged();
	}

	private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element)
			|| (element.Tag is not string languageName)
			|| !Enum.TryParse(languageName, ignoreCase: false, out AppLanguage language)
			|| !Enum.IsDefined(language))
		{
			return;
		}

		ChangeLanguage(language);
	}

	internal void ChangeLanguage(AppLanguage language)
	{
		if ((_language == language)
			|| ((DataContext is DashboardViewModel viewModel) && !viewModel.CanChangeUsageDisplayMode))
		{
			UpdateLanguageMenuItems();
			return;
		}

		ApplyLanguage(language);
		NotifyPreferencesChanged();
	}

	private void ApplyLanguage(AppLanguage language)
	{
		_language = language;
		if (System.Windows.Application.Current is App app)
		{
			app.ApplyLanguage(language);
		}
		else
		{
			UiText.SetLanguage(language);
		}

		RefreshLocalizedPresentation();
	}

	internal void RefreshLocalizedPresentation()
	{
		UpdateLanguageMenuItems();
		UpdateCornerDependentVisuals();
		InlineStatusTextBlock.Text = UiText.Translate(_inlineStatusSource);
		if (DataContext is DashboardViewModel viewModel)
		{
			UpdateCompactRefreshStatus(viewModel);
		}
	}

	private void UpdateLanguageMenuItems()
	{
		EnglishLanguageMenuItem.IsChecked = _language == AppLanguage.English;
		TraditionalChineseLanguageMenuItem.IsChecked = _language == AppLanguage.TraditionalChinese;
	}

	private void UpdatePrimaryActionButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		UpdatePrimaryActionRequested?.Invoke(this, EventArgs.Empty);
	}

	private void UpdateReleaseHistoryButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		try
		{
			AboutLinkLauncher.Open(AboutLink.Releases);
		}
		catch (Exception exception) when (
			exception is Win32Exception or InvalidOperationException)
		{
			ReportInlineStatus(
				UiText.Format("Shell.Widget211", exception.Message),
				autoDismiss: false,
				preserveAgainstAsyncUpdates: true);
		}
	}

	private void UpdateSnoozeButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		UpdateSnoozeRequested?.Invoke(this, EventArgs.Empty);
	}

	private void DisableAutomaticUpdateChecksButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		AutomaticUpdateChecksDisableRequested?.Invoke(this, EventArgs.Empty);
	}

	private void FloatingWidgetWindow_IsVisibleChanged(
		object sender,
		DependencyPropertyChangedEventArgs e)
	{
		SetInformationalWindowsVisible(IsVisible && !_isCollapsed);

		if (!_isApplyingPortableWidgetPreferences)
		{
			NotifyPortableWidgetPreferencesChanged();
		}

		TryAnnouncePendingUpdateBanner();
	}

	private async void RefreshAccountMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.CanRefreshUsage)
		{
			return;
		}

		await RefreshAccountUsageWithFeedbackAsync(account, viewModel);
	}

	private void ViewCodexResetCreditsMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			viewModel.IsManagingAccounts ||
			!account.IsCodex ||
			!account.CanOpenWidgetAccountMenu ||
			!viewModel.Accounts.Contains(account))
		{
			return;
		}

		CodexResetCreditsWindow? existingWindow = OwnedWindows
			.OfType<CodexResetCreditsWindow>()
			.FirstOrDefault(window => ReferenceEquals(window.Account, account));
		if (existingWindow is not null)
		{
			ShowExistingInformationalWindow(existingWindow);
			return;
		}

		CodexResetCreditsWindow window = new(account, viewModel.Accounts)
		{
			Owner = this
		};
		window.Show();
	}

	private async void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not DashboardViewModel viewModel)
		{
			return;
		}

		try
		{
			await viewModel.RefreshUsageAsync();
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"manual-usage-refresh",
				"scope=all;stage=window-boundary;result=failed",
				exception);
			viewModel.ReportRefreshFailure();
		}
	}

	private async Task RefreshAccountUsageWithFeedbackAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel)
	{
		try
		{
			await viewModel.RefreshAccountUsageAsync(
				account.Id,
				progress =>
				{
					string message = progress == AccountRefreshProgress.Queued
						? UiText.Format("Shell.Widget212", account.AccountName)
						: UiText.Format("Shell.Widget213", account.AccountName);
					ReportInlineStatus(
						message,
						autoDismiss: false,
						preserveAgainstAsyncUpdates: true);
				});
			ReportInlineStatus(string.Empty);
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"manual-usage-refresh",
				"scope=account;stage=window-boundary;result=failed",
				exception);
			viewModel.ReportRefreshFailure();
			ReportInlineStatus(
				UiText.Format("Shell.Widget214", account.AccountName));
		}
	}

	private async Task RevalidateUsageWithFeedbackAsync(
		AccountUsageViewModel account,
		DashboardViewModel viewModel)
	{
		try
		{
			string providerName = account.IsAntigravity ? "Antigravity" : "Claude";
			ReportInlineStatus(
				UiText.Format("Shell.Widget215", account.AccountName, providerName),
				autoDismiss: false);
			await viewModel.RevalidateUsageAsync(account.Id);

			if (account.RecoveryAction == UsageRecoveryAction.RevalidateUsage)
			{
				ReportAsyncStatus(
					UiText.Format("Shell.Widget216", account.AccountName),
					autoDismiss: false);
			}
			else if (account.RecoveryAction == UsageRecoveryAction.Retry)
			{
				ReportAsyncStatus(
					UiText.Format("Shell.Widget217", account.AccountName),
					autoDismiss: false);
			}
			else
			{
				ReportAsyncStatus(string.Empty);
			}
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"manual-usage-safety-revalidation",
				"stage=window-boundary;result=failed",
				exception);
			viewModel.ReportRefreshFailure();
			ReportAsyncStatus(
				UiText.Format("Shell.Widget218", account.AccountName),
				autoDismiss: false);
		}
	}

	private async void ToggleAccountEnabledMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement ||
			!account.CanChangeAccountSettings)
		{
			return;
		}

		AccountProfile updatedProfile = account.Profile with
		{
			IsEnabled = !account.IsEnabled
		};
		await ExecuteAccountChangeAsync(
			() => viewModel.UpdateAccountAsync(updatedProfile));
	}

	private async void ToggleSubscriptionContextMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if ((sender is not FrameworkElement element) ||
			(element.DataContext is not AccountUsageViewModel account) ||
			(DataContext is not DashboardViewModel viewModel) ||
			!viewModel.CanStartAccountManagement ||
			!account.CanChangeAccountSettings ||
			!account.SupportsSubscriptionContext)
		{
			return;
		}

		AccountProfile updatedProfile = account.Profile with
		{
			ShowSubscriptionContext = !account.ShowSubscriptionContext
		};
		bool wasUpdated = await ExecuteAccountChangeAsync(
			() => viewModel.UpdateAccountAsync(updatedProfile));
		if (!wasUpdated &&
			(sender is System.Windows.Controls.MenuItem menuItem))
		{
			menuItem.GetBindingExpression(
				System.Windows.Controls.MenuItem.IsCheckedProperty)?.UpdateTarget();
		}
	}

	private async void ToggleSortModeMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if (DataContext is DashboardViewModel viewModel)
		{
			await ExecutePreferenceChangeAsync(
				() => viewModel.ToggleUsageSortModeAsync());
		}
	}

	private void ShowAutomaticSortRulesMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		AutomaticSortRulesWindow? existingWindow = OwnedWindows
			.OfType<AutomaticSortRulesWindow>()
			.FirstOrDefault();
		if (existingWindow is not null)
		{
			ShowExistingInformationalWindow(existingWindow);
			return;
		}

		AutomaticSortRulesWindow rulesWindow = new() { Owner = this };
		rulesWindow.Show();
	}

	private async void ToggleUsageDisplayModeMenuItem_Click(
		object sender,
		RoutedEventArgs e)
	{
		if (DataContext is DashboardViewModel viewModel)
		{
			await ExecutePreferenceChangeAsync(
				() => viewModel.ToggleUsageDisplayModeAsync());
		}
	}

	private void QueueCurrentPlacement()
	{
		if (!_hasPlacement ||
			_isApplyingPlacement ||
			_isCollapsedDragPending ||
			_isCollapsedDragging ||
			_isPlacementPending ||
			!IsVisible)
		{
			return;
		}

		_isPlacementPending = true;
		Dispatcher.BeginInvoke(
			() =>
			{
				_isPlacementPending = false;
				ApplyCurrentPlacement();
			},
			DispatcherPriority.Loaded);
	}

	private void QueueDpiPlacementCorrection()
	{
		if (_isPlacementCorrectionPending ||
			_isCollapsedDragPending ||
			_isCollapsedDragging ||
			!IsVisible)
		{
			return;
		}

		_isPlacementCorrectionPending = true;
		Dispatcher.BeginInvoke(
			() =>
			{
				_isPlacementCorrectionPending = false;
				ApplyCurrentPlacement(queueDpiCorrection: false);
			},
			DispatcherPriority.ContextIdle);
	}

	private void QueueWorkAreaRefresh()
	{
		if (_isWorkAreaRefreshPending ||
			_isApplyingPlacement ||
			_isCollapsedDragPending ||
			_isCollapsedDragging ||
			_isExpandedDragging ||
			!IsVisible)
		{
			return;
		}

		_isWorkAreaRefreshPending = true;
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				_isWorkAreaRefreshPending = false;
				ApplyCurrentPlacement();
			},
			DispatcherPriority.ContextIdle);
	}

	private void SetCollapsed(
		bool isCollapsed,
		bool notifyPreferences = true,
		FocusTransitionMode focusTransitionMode =
			FocusTransitionMode.Transfer,
		DependencyObject? pointerFocusOwner = null)
	{
		if (_isCollapsed == isCollapsed)
		{
			return;
		}

		bool shouldTransferFocus = IsKeyboardFocusWithin;
		ResetAccountScrollDrag(releaseMouseCapture: true);
		if (isCollapsed)
		{
			SetInformationalWindowsVisible(false);
		}

		bool wasNativeWindowHidden = TryHideNativeWindowForLayoutTransition(
			out IntPtr windowHandle);
		_isCollapsed = isCollapsed;
		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;
		CollapsedButton.ReleaseMouseCapture();

		try
		{
			if (isCollapsed)
			{
				SizeToContent = SizeToContent.Manual;
				MaxHeight = double.PositiveInfinity;
				ExpandedView.Visibility = Visibility.Collapsed;
				CollapsedButton.Visibility = Visibility.Visible;
				Width = CollapsedSize;
				Height = CollapsedSize;
			}
			else
			{
				CollapsedButton.Visibility = Visibility.Collapsed;
				ExpandedView.Visibility = Visibility.Visible;
				UpdateExpandedSizeForLayoutTransition(windowHandle);
			}

			UpdateLayout();
			ApplyCurrentPlacement();
		}
		finally
		{
			if (wasNativeWindowHidden)
			{
				ShowNativeWindowAfterLayoutTransition(windowHandle);
			}
		}

		if (!isCollapsed)
		{
			SetInformationalWindowsVisible(IsVisible);
		}

		TryAnnouncePendingUpdateBanner();

		FrameworkElement focusTarget = isCollapsed
			? CollapsedButton
			: AnchorToggleButton;

		if (shouldTransferFocus &&
			(focusTransitionMode == FocusTransitionMode.Transfer))
		{
			_ = Dispatcher.BeginInvoke(
				() => focusTarget.Focus(),
				DispatcherPriority.Input);
		}
		else if (focusTransitionMode == FocusTransitionMode.Release)
		{
			QueuePointerFocusRelease(
				focusTransitionMode,
				pointerFocusOwner,
				focusTarget);
		}

		if (notifyPreferences)
		{
			NotifyPreferencesChanged();
		}
	}

	private static void ShowExistingInformationalWindow(Window window)
	{
		if (window.WindowState == WindowState.Minimized)
		{
			window.WindowState = WindowState.Normal;
		}

		window.Show();
		window.Activate();
	}

	private void SetInformationalWindowsVisible(bool isVisible)
	{
		foreach (Window window in OwnedWindows
			.OfType<Window>()
			.Where(window =>
				window is CodexResetCreditsWindow or
					AutomaticSortRulesWindow or AboutWindow)
			.ToArray())
		{
			if (window.IsVisible == isVisible)
			{
				continue;
			}

			if (isVisible)
			{
				window.Show();
			}
			else
			{
				window.Hide();
			}
		}
	}

	private void QueuePointerFocusRelease(
		FocusTransitionMode focusTransitionMode,
		DependencyObject? pointerFocusOwner,
		FrameworkElement fallbackFocusOwner)
	{
		if (focusTransitionMode != FocusTransitionMode.Release)
		{
			return;
		}

		DependencyObject focusOwner =
			pointerFocusOwner ?? fallbackFocusOwner;
		_ = Dispatcher.BeginInvoke(
			() => PointerFocusRelease.ReleaseFocusIfStillOwned(
				focusOwner,
				fallbackFocusOwner),
			DispatcherPriority.Input);
	}

	private void SetCorner(
		FloatingWidgetCorner corner,
		bool notifyPreferences = true)
	{
		if (_corner == corner)
		{
			UpdateCornerDependentVisuals();
			return;
		}

		_corner = corner;
		UpdateCornerDependentVisuals();

		if (notifyPreferences)
		{
			NotifyPreferencesChanged();
		}
	}

	private void SnapToNearestCorner(bool preserveVerticalCorner = false)
	{
		IntPtr windowHandle = new WindowInteropHelper(this).Handle;

		if (!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds))
		{
			return;
		}

		FormsScreen targetScreen = FormsScreen.FromHandle(windowHandle);
		DrawingRectangle workingArea = targetScreen.WorkingArea;
		bool didScreenChange = !string.Equals(
			_screenDeviceName,
			targetScreen.DeviceName,
			StringComparison.OrdinalIgnoreCase);
		_screenDeviceName = targetScreen.DeviceName;
		FloatingWidgetCorner nearestCorner = FloatingWidgetPlacement.GetNearestCorner(
			windowBounds,
			workingArea);

		if (preserveVerticalCorner)
		{
			bool isLeft = nearestCorner is FloatingWidgetCorner.TopLeft or
				FloatingWidgetCorner.BottomLeft;
			nearestCorner = FloatingWidgetPlacement.GetCornerOnHorizontalSide(
				_corner,
				isLeft);
		}

		FloatingWidgetCorner previousCorner = _corner;
		SetCorner(nearestCorner, notifyPreferences: false);
		_hasPlacement = true;
		ApplyCurrentPlacement();
		if (didScreenChange ||
			(previousCorner != nearestCorner))
		{
			NotifyPreferencesChanged();
		}
	}

	private void UpdateCornerDependentVisuals()
	{
		bool isLeft = _corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.BottomLeft;
		bool isTop = _corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.TopRight;
		string cornerText = _corner switch
		{
			FloatingWidgetCorner.TopLeft => UiText.Get("Shell.Widget160"),
			FloatingWidgetCorner.TopRight => UiText.Get("Shell.Widget161"),
			FloatingWidgetCorner.BottomLeft => UiText.Get("Shell.Widget162"),
			_ => UiText.Get("Shell.Widget163")
		};

		AnchorToggleButton.HorizontalAlignment = isLeft
			? System.Windows.HorizontalAlignment.Left
			: System.Windows.HorizontalAlignment.Right;
		AnchorToggleButton.VerticalAlignment = isTop
			? System.Windows.VerticalAlignment.Top
			: System.Windows.VerticalAlignment.Bottom;
		AnchorToggleButton.Content = _corner switch
		{
			FloatingWidgetCorner.TopLeft => "↖",
			FloatingWidgetCorner.TopRight => "↗",
			FloatingWidgetCorner.BottomLeft => "↙",
			_ => "↘"
		};

		string collapseText = _collapsedPositionXRatio.HasValue
			? UiText.Get("Shell.Widget219")
			: UiText.Format("Shell.Widget220", cornerText);
		AutomationProperties.SetName(AnchorToggleButton, collapseText);
		AnchorToggleButton.ToolTip = collapseText;
		UpdateCollapsedButtonPresentation(cornerText);
		TopLeftCornerMenuItem.IsChecked =
			_corner == FloatingWidgetCorner.TopLeft;
		TopRightCornerMenuItem.IsChecked =
			_corner == FloatingWidgetCorner.TopRight;
		BottomLeftCornerMenuItem.IsChecked =
			_corner == FloatingWidgetCorner.BottomLeft;
		BottomRightCornerMenuItem.IsChecked =
			_corner == FloatingWidgetCorner.BottomRight;

		if (isTop)
		{
			ExpandedHeader.Margin = isLeft
				? new Thickness(CornerToggleContentInset, 0, 2, 10)
				: new Thickness(2, 0, CornerToggleContentInset, 10);
			ExpandedFooterRow.Height = GridLength.Auto;
			ExpandedFooter.Margin = new Thickness(2, 7, 2, 0);
			return;
		}

		ExpandedHeader.Margin = new Thickness(2, 0, 2, 10);
		ExpandedFooterRow.Height = new GridLength(CornerToggleFooterHeight);
		ExpandedFooter.Margin = isLeft
			? new Thickness(CornerToggleContentInset, 0, 2, 0)
			: new Thickness(2, 0, CornerToggleContentInset, 0);
	}

	private void UpdateCollapsedButtonPresentation(string cornerText)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cornerText);
		string updateHint = _hasUpdateBadge &&
			!string.IsNullOrWhiteSpace(_updateAvailableVersion)
				? UiText.Format("Shell.Widget221", _updateAvailableVersion)
				: string.Empty;
		string positionText = _collapsedPositionXRatio.HasValue
			? UiText.Get("Shell.Widget222")
			: UiText.Format("Shell.Widget223", cornerText);
		AutomationProperties.SetName(
			CollapsedButton,
			UiText.Format("Shell.Widget224", positionText, updateHint));
		string expandHelpText =
			UiText.Format("Shell.Widget225", positionText, updateHint);
		AutomationProperties.SetHelpText(CollapsedButton, expandHelpText);
		CollapsedButton.ToolTip = expandHelpText;
		CollapsedUpdateBadge.Visibility = _hasUpdateBadge
			? Visibility.Visible
			: Visibility.Collapsed;
	}

	private (DrawingRectangle WorkingArea, int Margin, DpiScale Dpi)
		GetPlacementLayoutContext(IntPtr windowHandle)
	{
		DrawingRectangle workingArea = GetTargetWorkingArea(windowHandle);
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		int margin = Math.Max(
			0,
			(int)Math.Round(CornerMargin * dpi.DpiScaleX));
		return (workingArea, margin, dpi);
	}

	private void UpdateExpandedSizeForLayoutTransition(IntPtr windowHandle)
	{
		if (windowHandle == IntPtr.Zero)
		{
			UpdateExpandedSize(ExpandedWidth, ExpandedDefaultHeight);
			return;
		}

		(DrawingRectangle workingArea, int margin, DpiScale dpi) =
			GetPlacementLayoutContext(windowHandle);
		UpdateExpandedBounds(workingArea, margin, dpi);
	}

	private void UpdateExpandedBounds(
		DrawingRectangle workingArea,
		int margin,
		DpiScale dpi)
	{
		if (_isCollapsed)
		{
			return;
		}

		(double targetWidth, double targetHeight) =
			FloatingWidgetPlacement.GetExpandedSize(
				workingArea.Size,
				margin,
				dpi.DpiScaleX,
				dpi.DpiScaleY,
				ExpandedWidth,
				ExpandedDefaultHeight);

		UpdateExpandedSize(targetWidth, targetHeight);
	}

	private void UpdateExpandedSize(double targetWidth, double maximumHeight)
	{
		if (_isHeightFollowingCardCount)
		{
			if ((Math.Abs(Width - targetWidth) < 0.5) &&
				(Math.Abs(MaxHeight - maximumHeight) < 0.5) &&
				double.IsNaN(Height) &&
				(SizeToContent == SizeToContent.Height))
			{
				return;
			}

			Width = targetWidth;
			MaxHeight = maximumHeight;
			Height = double.NaN;
			SizeToContent = SizeToContent.Height;
			UpdateLayout();
			return;
		}

		if ((Math.Abs(Width - targetWidth) < 0.5) &&
			(Math.Abs(Height - maximumHeight) < 0.5) &&
			double.IsPositiveInfinity(MaxHeight) &&
			(SizeToContent == SizeToContent.Manual))
		{
			return;
		}

		SizeToContent = SizeToContent.Manual;
		MaxHeight = double.PositiveInfinity;
		Width = targetWidth;
		Height = maximumHeight;
		UpdateLayout();
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		QueueCurrentPlacement();
	}

	private bool TryHideNativeWindowForLayoutTransition(
		out IntPtr windowHandle)
	{
		windowHandle = IntPtr.Zero;

		if (!IsVisible)
		{
			return false;
		}

		windowHandle = new WindowInteropHelper(this).Handle;

		if (windowHandle == IntPtr.Zero)
		{
			return false;
		}

		bool wasHidden = SetWindowPos(
			windowHandle,
			IntPtr.Zero,
			0,
			0,
			0,
			0,
			SetWindowPositionNoActivate |
			SetWindowPositionNoMove |
			SetWindowPositionNoSize |
			SetWindowPositionNoZOrder |
			SetWindowPositionHideWindow);

		if (!wasHidden)
		{
			int errorCode = Marshal.GetLastWin32Error();
			AppDiagnostics.TryWrite(
				"floating-window-layout-transition",
				UiText.Format("Shell.Widget226", windowHandle),
				new Win32Exception(errorCode));
		}

		return wasHidden;
	}

	private void ShowNativeWindowAfterLayoutTransition(IntPtr windowHandle)
	{
		try
		{
			(DrawingRectangle workingArea, int margin, DpiScale dpi) =
				GetPlacementLayoutContext(windowHandle);
			UpdateExpandedBounds(workingArea, margin, dpi);
			UpdateLayout();

			if (!TryGetWindowBounds(
					windowHandle,
					out DrawingRectangle windowBounds))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					UiText.Format("Shell.Widget227", windowHandle));
			}

			DrawingPoint topLeft = GetCurrentTopLeft(
				windowBounds.Size,
				workingArea,
				margin);
			_isApplyingPlacement = true;

			bool wasShown;

			try
			{
				wasShown = SetWindowPos(
					windowHandle,
					IntPtr.Zero,
					topLeft.X,
					topLeft.Y,
					windowBounds.Width,
					windowBounds.Height,
					SetWindowPositionNoActivate |
					SetWindowPositionNoZOrder |
					SetWindowPositionShowWindow);
			}
			finally
			{
				_isApplyingPlacement = false;
			}

			if (!wasShown)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					UiText.Format("Shell.Widget228", windowHandle));
			}
		}
		catch (Exception exception)
		{
			_ = ShowWindow(windowHandle, ShowWindowWithoutActivation);
			AppDiagnostics.TryWrite(
				"floating-window-layout-transition",
				UiText.Format("Shell.Widget229", windowHandle),
				exception);
		}
	}

	private IntPtr WindowMessageHook(
		IntPtr hwnd,
		int message,
		IntPtr wParam,
		IntPtr lParam,
		ref bool handled)
	{
		if (WindowWorkAreaLayout.RequiresRefreshForWindowMessage(message))
		{
			QueueWorkAreaRefresh();
		}

		return IntPtr.Zero;
	}

	private void NotifyPreferencesChanged()
	{
		if (_isApplyingPortableWidgetPreferences)
		{
			return;
		}

		NotifyPortableWidgetPreferencesChanged();
		PreferencesChanged?.Invoke(this, EventArgs.Empty);
	}

	private void NotifyPortableWidgetPreferencesChanged()
	{
		if (DataContext is not DashboardViewModel viewModel)
		{
			return;
		}

		viewModel.NotifyPortableWidgetPreferencesChanged(
			CapturePortableWidgetPreferences());

		if (!viewModel.CanUndoLastPortableSettingsImport)
		{
			_portableWidgetPreferencesBeforeLastImport = null;
		}
	}
}
