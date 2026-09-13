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
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Presentation;

using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
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
	private const uint SetWindowPositionNoSize = 0x0001;
	private const uint SetWindowPositionNoZOrder = 0x0004;
	private static readonly TimeSpan CompactRefreshStatusDisplayDuration =
		TimeSpan.FromSeconds(5);
	private readonly AccountConnectionCoordinator _accountConnectionCoordinator;
	private readonly AccountStatusAnnouncementBridge
		_accountStatusAnnouncementBridge;
	private readonly PortableSettingsJsonService _portableSettingsJsonService = new();
	private readonly SemaphoreSlim _portableSettingsOperationGate = new(1, 1);
	private readonly CancellationTokenSource _portableSettingsLifetime = new();
	private readonly VerticalDragScrollSession _accountScrollDragSession = new();
	private readonly DispatcherTimer _compactRefreshStatusTimer;
	private readonly DispatcherTimer _inlineStatusTimer;
	private DrawingPoint _collapsedDragStart;
	private FloatingWidgetCorner _corner = FloatingWidgetCorner.BottomRight;
	private string? _lastViewModelAnnouncement;
	private PortableWidgetPreferences?
		_portableWidgetPreferencesBeforeLastImport;
	private string _screenDeviceName = string.Empty;
	private DateTimeOffset _lastViewModelAnnouncementAt;
	private double _collapsedDragOffsetXRatio;
	private double _collapsedDragOffsetYRatio;
	private int _focusInteractionGeneration;
	private bool _hasPlacement;
	private bool _hasPersistentInlineStatus;
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
	private AppTheme _theme = AppTheme.ClassicBlue;
	private HwndSource? _windowSource;

	internal event EventHandler? PreferencesChanged;

	internal bool IsCollapsed => _isCollapsed;

	internal FloatingWidgetCorner Corner => _corner;

	internal AppTheme Theme => _theme;

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
		_theme = preferences.Theme;
		_isHeightFollowingCardCount = preferences.IsHeightFollowingCardCount;
		_hasPlacement = true;
		_isCollapsed = !preferences.IsCollapsed;
		SetCollapsed(preferences.IsCollapsed, notifyPreferences: false);
		Topmost = preferences.IsTopmost;
		PinButton.IsChecked = preferences.IsTopmost;
		UpdateCornerDependentVisuals();
		UpdateHeightFollowsCardCountMenuItem();
		UpdateThemeMenuItems();
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
			_isHeightFollowingCardCount);
	}

	internal static PortableWidgetPreferences CreatePortableWidgetPreferencesSnapshot(
		bool isWidgetVisible,
		bool isCollapsed,
		bool isTopmost,
		FloatingWidgetCorner corner,
		AppTheme theme,
		bool isHeightFollowingCardCount = false)
	{
		return new PortableWidgetPreferences(
			isWidgetVisible,
			isCollapsed,
			isTopmost,
			corner,
			theme,
			isHeightFollowingCardCount);
	}

	private PortableWidgetPreferences CapturePortableWidgetPreferences()
	{
		return CreatePortableWidgetPreferencesSnapshot(
			IsVisible,
			_isCollapsed,
			Topmost,
			_corner,
			_theme,
			_isHeightFollowingCardCount);
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
					: current.IsHeightFollowingCardCount
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
			_theme = preferences.Theme;
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
			"無法儲存浮窗位置與顯示設定；" +
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
			ReportInlineStatus("浮窗位置與顯示設定已恢復儲存。");
			return;
		}

		ReportAsyncStatus("浮窗位置與顯示設定已恢復儲存。");
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
			ReportInlineStatus("已有設定匯入、匯出或還原正在進行。");
			return;
		}

		try
		{
			MessageBoxResult confirmation = ShowPortableSettingsMessageBox(
				"匯出檔是未加密的 JSON，包含帳號暱稱、連接後顯示的帳號（例如電子郵件）、帳號順序、用量顯示方式、浮窗設定與主題。\n\n" +
				"檔案不包含密碼或登入憑證。請只存放在可信任的位置。\n\n要繼續匯出嗎？",
				"匯出設定",
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
					"AI Usage 設定 (*.aiusage.json)|*.aiusage.json|JSON 檔案 (*.json)|*.json",
				OverwritePrompt = true,
				Title = "匯出 AI Usage 設定"
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
					$"已匯出 {snapshot.Accounts.Count} 個帳號與設定；檔案不含登入憑證。");
			}
			catch (OperationCanceledException)
			{
				// Closing the app or cancelling the operation leaves the destination unchanged.
			}
			catch (Exception exception)
			{
				ReportPortableSettingsFailure("匯出", exception);
			}
		}
		catch (OperationCanceledException)
		{
			// App shutdown cancels any operation that has not committed.
		}
		catch (Exception exception)
		{
			ReportPortableSettingsFailure("匯出", exception);
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
			ReportInlineStatus("已有設定匯入、匯出或還原正在進行。");
			return;
		}

		try
		{
			WpfOpenFileDialog dialog = new()
			{
				CheckFileExists = true,
				DefaultExt = ".aiusage.json",
				Filter =
					"AI Usage 設定 (*.aiusage.json)|*.aiusage.json|JSON 檔案 (*.json)|*.json",
				Multiselect = false,
				Title = "匯入 AI Usage 設定"
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
				ReportPortableSettingsFailure("匯入", exception);
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
			string replacementDescription = snapshot.WidgetPreferences switch
			{
				null =>
					"繼續會取代目前的帳號、手動順序、排序方式與用量顯示方式；這份舊版檔案沒有浮窗、主題與視窗高度設定，因此會保留目前設定。 ",
				{ Theme: null, IsHeightFollowingCardCount: null } =>
					"繼續會取代目前的帳號、手動順序、排序方式、用量顯示方式與浮窗設定；這份舊版檔案沒有主題與視窗高度設定，因此會保留目前主題與高度設定。 ",
				{ Theme: null } =>
					"繼續會取代目前的帳號、手動順序、排序方式、用量顯示方式與浮窗設定；這份舊版檔案沒有主題設定，因此會保留目前主題。 ",
				{ IsHeightFollowingCardCount: null } =>
					"繼續會取代目前的帳號、手動順序、排序方式、用量顯示方式、浮窗設定與主題；這份舊版檔案沒有視窗高度設定，因此會保留目前高度設定。 ",
				_ =>
					"繼續會取代目前的帳號、手動順序、排序方式、用量顯示方式、浮窗設定與主題。 "
			};

			MessageBoxResult confirmation = WpfMessageBox.Show(
				this,
				$"{preview}\n\n" +
				$"{replacementDescription}Claude、Codex、Antigravity 與 Grok Build CLI 的預設登入資料不會被刪除；AI Usage 為現有 Grok 帳號另存的登入資料會清除。套用後可在關閉程式前選擇「還原匯入前設定」；若重新啟動後仍需還原，請先取消並匯出目前設定。\n\n" +
				$"{claudeQuotaRiskNotice}\n\n" +
				"匯入檔不含登入憑證。匯入後，Antigravity 與 Grok 必須逐一重新連接；若這台電腦沒有對應的 Claude／Codex 登入，也需重新連接。\n\n要套用這份設定嗎？",
				"取代目前設定",
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
					"正在匯入設定；如有進行中的用量檢查，會先等待它結束…",
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
					"設定已匯入，但備份更新失敗",
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
				ReportPortableSettingsFailure("匯入", exception);
			}
		}
		catch (OperationCanceledException)
		{
			// App shutdown cancels any import that has not committed.
		}
		catch (Exception exception)
		{
			ReportPortableSettingsFailure("匯入", exception);
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
			ReportInlineStatus("已有設定匯入、匯出或還原正在進行。");
			return;
		}

		try
		{
			ReportInlineStatus(
				"正在還原匯入前設定；如有進行中的用量檢查，會先等待它結束…",
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
				"設定已還原，但備份更新失敗",
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
			ReportPortableSettingsFailure("還原", exception);
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
			"、",
			imported.Accounts
				.GroupBy(account => account.Provider)
				.OrderBy(group => group.Key)
				.Select(group => $"{GetProviderDisplayName(group.Key)} {group.Count()}")
				.DefaultIfEmpty("無帳號"));

		return "變更預覽：\n" +
			$"• 帳號：{current.Accounts.Count} → {imported.Accounts.Count}" +
			$"（新增 {addedCount}、移除 {removedCount}、更新 {updatedCount}）\n" +
			$"• 順序：{(orderChanged ? "會變更" : "不變")}\n" +
			$"• 排序方式：{GetUsageSortModeText(current.UsageSortMode)} → " +
			$"{GetUsageSortModeText(imported.UsageSortMode)}\n" +
			$"• 顯示方式：{GetUsageDisplayModeText(current.UsageDisplayMode)} → " +
			$"{GetUsageDisplayModeText(imported.UsageDisplayMode)}\n" +
			$"• 浮窗設定：{GetPortableWidgetPreferencesPreview(current, imported)}\n" +
			$"• 主題：{GetPortableThemePreview(current, imported)}\n" +
			$"• 需重新確認 Claude 用量讀取：{claudeQuotaRiskConfirmationCount} 個帳號\n" +
			$"• 帳號所屬服務：{providerCounts}";
	}

	private static string CreateClaudeQuotaRiskReconfirmationNotice(
		int claudeAccountCount)
	{
		return $"匯入後有 {claudeAccountCount} 個 Claude 帳號需要重新確認用量讀取；確認前不會檢查這些帳號的用量。";
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
			UsageSortMode.Manual => "手動",
			UsageSortMode.Automatic => "自動",
			_ => "未知"
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
			_ => "未知服務"
		};
	}

	private static string GetUsageDisplayModeText(UsageDisplayMode mode)
	{
		return mode switch
		{
			UsageDisplayMode.Used => "已使用",
			UsageDisplayMode.Remaining => "剩餘",
			_ => "未知"
		};
	}

	private static string GetPortableWidgetPreferencesPreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		if (imported.WidgetPreferences is null)
		{
			return "保留目前設定";
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
		string visibility = preferences.IsWidgetVisible ? "顯示" : "隱藏";
		string collapsed = preferences.IsCollapsed ? "收合" : "展開";
		string topmost = preferences.IsTopmost ? "置頂" : "不置頂";
		string height = preferences.IsHeightFollowingCardCount switch
		{
			true => "高度隨卡片數量",
			false => "固定可用高度",
			null => "高度維持目前設定"
		};
		string corner = preferences.Corner switch
		{
			FloatingWidgetCorner.TopLeft => "左上角",
			FloatingWidgetCorner.TopRight => "右上角",
			FloatingWidgetCorner.BottomLeft => "左下角",
			FloatingWidgetCorner.BottomRight => "右下角",
			_ => "未知角落"
		};
		return $"{visibility}、{collapsed}、{topmost}、{corner}、{height}";
	}

	private static string GetPortableThemePreview(
		PortableSettingsSnapshot current,
		PortableSettingsSnapshot imported)
	{
		AppTheme? importedTheme = imported.WidgetPreferences?.Theme;

		if (importedTheme is null)
		{
			return "保留目前主題";
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
			AppTheme.ClassicBlue => "經典藍",
			AppTheme.Midnight => "曜石黑",
			AppTheme.Light => "柔霧灰",
			AppTheme.Sakura => "櫻花粉",
			_ => "未知"
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
			"設定匯入完成",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private static string CreatePortableSettingsImportCompletedMessage(
		int accountCount,
		int claudeQuotaRiskConfirmationCount,
		string accountSettingsMessage)
	{
		string appliedStatus = string.IsNullOrWhiteSpace(accountSettingsMessage)
			? $"已匯入 {accountCount} 個帳號，排序、用量顯示、浮窗設定與主題已套用。"
			: accountSettingsMessage.Trim();

		return $"{appliedStatus}\n\n" +
			$"{CreateClaudeQuotaRiskReconfirmationNotice(claudeQuotaRiskConfirmationCount)}\n\n" +
			"匯入檔不含登入憑證。若這台電腦沒有對應的登入資料，請依帳號卡片提示重新連接 Claude 或 Codex。匯入後，Antigravity 與 Grok 必須逐一重新連接。\n\n" +
			"如需還原，請在關閉程式前選擇「匯入或匯出設定」→「還原匯入前設定」；還原後部分用量可能需要重新檢查。";
	}

	private void ReportPortableSettingsFailure(
		string operation,
		Exception exception)
	{
		string reason = exception switch
		{
			PortableSettingsException => exception.Message,
			ArgumentException argumentException =>
				GetPortableSettingsArgumentFailureReason(argumentException),
			InvalidOperationException => exception.Message,
			_ => AppDiagnostics.GetUserFacingFailureReason(
				exception,
				$"{operation}設定時發生錯誤。")
		};
		string diagnosticOperation = operation switch
		{
			"匯入" => "import",
			"匯出" => "export",
			"還原" => "undo",
			_ => "operation"
		};
		AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
			$"portable-settings-{diagnosticOperation}",
			reason,
			exception);
		string diagnosticHint = diagnostic.WasWritten
			? $"\n\n診斷紀錄：{diagnostic.FilePath}"
			: string.Empty;
		ReportInlineStatus($"無法{operation}設定：{reason}");
		WpfMessageBox.Show(
			this,
			$"無法{operation}設定。\n\n{reason}{diagnosticHint}",
			$"設定{operation}失敗",
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
				"更新帳號設定時發生錯誤。")
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
					$"正在取消「{account.AccountName}」的 Claude 帳號連接…",
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
					$"正在取消「{account.AccountName}」的 Codex 帳號連接…",
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
					$"正在取消「{account.AccountName}」的 Copilot 帳號連接…",
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
					$"正在取消「{account.AccountName}」的 Grok 帳號連接…",
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
			"從 AI Usage 移除帳號",
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
			currentProviderAccountDisplayText:
				account.ProviderAccountDisplayText,
			hasCurrentProviderAccountIdentity:
				account.HasProviderAccountIdentity,
			currentProviderAccountActionText: account.Provider switch
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
				? $"\n\n診斷紀錄：{diagnostic.FilePath}"
				: string.Empty;
			ReportInlineStatus(message);
			WpfMessageBox.Show(
				this,
				$"{message}{diagnosticHint}",
				"帳號備份更新失敗",
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
				? $"\n\n診斷紀錄：{diagnostic.FilePath}"
				: string.Empty;
			ReportInlineStatus($"無法更新帳號設定：{reason}");
			WpfMessageBox.Show(
				this,
				$"無法更新帳號設定；原設定未變更。\n\n{reason}{diagnosticHint}",
				"帳號設定錯誤",
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
				"更新顯示設定時發生錯誤。");
			AppDiagnosticWriteResult diagnostic = AppDiagnostics.TryWrite(
				"floating-window-preference-change",
				reason,
				exception);
			string diagnosticHint = diagnostic.WasWritten
				? $"\n\n診斷紀錄：{diagnostic.FilePath}"
				: string.Empty;
			WpfMessageBox.Show(
				this,
				$"無法更新顯示設定；原設定未變更。\n\n{reason}{diagnosticHint}",
				"顯示設定錯誤",
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
					$"正在取消「{account.AccountName}」的 {account.ProviderName} 帳號連接…",
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
			? "處理步驟已複製到剪貼簿。"
			: $"目前無法存取剪貼簿。處理步驟如下：{guidance}");
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
			ReportInlineStatus("已開啟使用說明。");
			return;
		}

		ReportInlineStatus(
			$"{LocalUserGuideLauncher.GetFailureMessage(result)} 設定步驟：{guidance}",
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
		InlineStatusTextBlock.Text = message;
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
			_lastViewModelAnnouncement = "檢查中…";
			_lastViewModelAnnouncementAt = DateTimeOffset.UtcNow;
			RaiseNotification("檢查中…", "RefreshStatusAnnouncement");
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
			return "Antigravity：目前版本的 AI Usage 尚未支援這個用量格式。請更新 AI Usage；更新後會自動重新檢查。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。";
		}

		if (account.IsAntigravity &&
			(account.RecoveryAction == UsageRecoveryAction.InstallOrUpdate))
		{
			return "Antigravity：請確認 Antigravity CLI 已安裝且可正常啟動，並更新 Antigravity 或 AI Usage。完成後會自動重新檢查。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。";
		}

		if (account.IsAntigravity &&
			(account.RecoveryAction ==
				UsageRecoveryAction.ReconfigureUsageSource))
		{
			return $"Antigravity：請按「{account.AntigravityAccountActionText}」，確認帳號與用量。完成後 AI Usage 會自動重新檢查。";
		}

		return account.Provider switch
		{
			ProviderKind.Claude =>
				"Claude：請從官方來源安裝或更新 Claude Code。這不會刪除既有帳號設定或登入資料。完成後回到浮窗按「完成後再檢查」。",
			ProviderKind.Codex =>
				"Codex：請從官方來源安裝或更新 Codex CLI。這不會刪除既有帳號設定或登入資料。完成後回到浮窗按「完成後再檢查」。",
			ProviderKind.Copilot =>
				"Copilot：請安裝或更新本機官方 GitHub Copilot CLI（正式版 1.0.79 以上且低於 2.0.0）。安裝說明：https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli 。原有卡片 credential 會保留；完成後回到浮窗按「完成後再檢查」。",
			ProviderKind.Grok =>
				"Grok：請從 xAI 官方來源安裝或更新 Grok Build CLI。這不會影響 AI Usage 已儲存的帳號設定。完成後回到浮窗按「完成後再檢查」。",
			ProviderKind.Antigravity =>
				"Antigravity：請依卡片提示處理。完成後 AI Usage 會自動重新檢查。",
			_ =>
				"請從服務的官方來源安裝或更新 CLI。完成後回到浮窗按「完成後再檢查」。"
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
		DrawingRectangle workingArea = GetTargetWorkingArea(windowHandle);
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		int margin = Math.Max(
			0,
			(int)Math.Round(CornerMargin * dpi.DpiScaleX));
		UpdateExpandedBounds(workingArea, margin, dpi);

		if (!TryGetWindowBounds(windowHandle, out DrawingRectangle windowBounds))
		{
			return;
		}

		DrawingPoint topLeft = FloatingWidgetPlacement.GetTopLeft(
			windowBounds.Size,
			workingArea,
			_corner,
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

		SetCorner(corner);
		_hasPlacement = true;
		ApplyCurrentPlacement();
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
		bool shouldSnap = _isCollapsedDragging;

		if (shouldSnap && GetPhysicalCursorPos(out NativePoint cursorPosition))
		{
			MoveCollapsedWindowToCursor(cursorPosition);
		}

		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;
		CollapsedButton.ReleaseMouseCapture();
		e.Handled = true;

		if (shouldSnap)
		{
			SnapToNearestCorner();
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
			return;
		}

		int x = cursorPosition.X - (int)Math.Round(
			windowBounds.Width * _collapsedDragOffsetXRatio);
		int y = cursorPosition.Y - (int)Math.Round(
			windowBounds.Height * _collapsedDragOffsetYRatio);
		SetWindowPos(
			windowHandle,
			IntPtr.Zero,
			x,
			y,
			0,
			0,
			SetWindowPositionNoActivate |
			SetWindowPositionNoSize |
			SetWindowPositionNoZOrder);
	}

	private void CollapsedButton_LostMouseCapture(
		object sender,
		System.Windows.Input.MouseEventArgs e)
	{
		if (!_isCollapsedDragPending && !_isCollapsedDragging)
		{
			return;
		}

		bool shouldSnap = _isCollapsedDragging;
		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;

		if (shouldSnap)
		{
			SnapToNearestCorner();
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

	private void OpenUserGuideMenuItem_Click(object sender, RoutedEventArgs e)
	{
		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open();

		if (result == LocalUserGuideOpenResult.Opened)
		{
			ReportInlineStatus("已開啟使用說明。");
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

	private void FloatingWidgetWindow_IsVisibleChanged(
		object sender,
		DependencyPropertyChangedEventArgs e)
	{
		if (!_isApplyingPortableWidgetPreferences)
		{
			NotifyPortableWidgetPreferencesChanged();
		}
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
						? $"「{account.AccountName}」的用量檢查已排隊；目前的檢查完成後會自動開始。"
						: $"正在檢查「{account.AccountName}」的用量…";
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
				$"目前無法檢查「{account.AccountName}」；已保留上次可用資料，稍後會自動再試。");
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
				$"正在重新檢查「{account.AccountName}」的 {providerName} 用量…",
				autoDismiss: false);
			await viewModel.RevalidateUsageAsync(account.Id);

			if (account.RecoveryAction == UsageRecoveryAction.RevalidateUsage)
			{
				ReportAsyncStatus(
					$"「{account.AccountName}」這次仍未完成檢查；自動檢查維持暫停。",
					autoDismiss: false);
			}
			else if (account.RecoveryAction == UsageRecoveryAction.Retry)
			{
				ReportAsyncStatus(
					$"「{account.AccountName}」這次仍未完成；稍後會自動再試。",
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
				$"目前無法重新檢查「{account.AccountName}」；其他帳號不受影響。",
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
		_isCollapsed = isCollapsed;
		_isCollapsedDragPending = false;
		_isCollapsedDragging = false;
		CollapsedButton.ReleaseMouseCapture();

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
			UpdateExpandedSize(ExpandedWidth, ExpandedDefaultHeight);
		}

		UpdateLayout();

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

		QueueCurrentPlacement();

		if (notifyPreferences)
		{
			NotifyPreferencesChanged();
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
		SetCorner(nearestCorner);

		if (didScreenChange && (previousCorner == nearestCorner))
		{
			NotifyPreferencesChanged();
		}
		_hasPlacement = true;
		ApplyCurrentPlacement();
	}

	private void UpdateCornerDependentVisuals()
	{
		bool isLeft = _corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.BottomLeft;
		bool isTop = _corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.TopRight;
		string cornerText = _corner switch
		{
			FloatingWidgetCorner.TopLeft => "左上角",
			FloatingWidgetCorner.TopRight => "右上角",
			FloatingWidgetCorner.BottomLeft => "左下角",
			_ => "右下角"
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

		string collapseText = $"收合 AI Usage 到{cornerText}";
		AutomationProperties.SetName(AnchorToggleButton, collapseText);
		AnchorToggleButton.ToolTip = collapseText;
		AutomationProperties.SetName(
			CollapsedButton,
			$"展開 AI Usage 浮窗，目前停靠在{cornerText}");
		string expandHelpText =
			$"按一下展開 AI Usage；拖曳可變更停靠角落。目前停靠在{cornerText}。";
		AutomationProperties.SetHelpText(CollapsedButton, expandHelpText);
		CollapsedButton.ToolTip = expandHelpText;
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
