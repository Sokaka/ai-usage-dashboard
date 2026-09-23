using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;

using AiUsageDashboard.Core.Models;

using FormsScreen = System.Windows.Forms.Screen;
using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

public partial class AccountEditorWindow : Window
{
	internal sealed record ProviderOption(
		ProviderKind Provider,
		string DisplayName,
		bool IsEnabled = true,
		string? UnavailableReason = null)
	{
		public string SearchText => IsEnabled ? DisplayName : string.Empty;
	}

	private const string ExistingAntigravityCardMessage =
		"這個 Windows 帳號已有 Antigravity 卡片，不能再新增。";
	private static readonly IReadOnlyList<ProviderOption> ProviderOptions =
		new ProviderOption[]
		{
			new(ProviderKind.Claude, "Claude"),
			new(ProviderKind.Codex, "Codex"),
			new(ProviderKind.Antigravity, "Antigravity"),
			new(ProviderKind.Grok, "Grok"),
			new(ProviderKind.Copilot, "GitHub Copilot")
		};
	private readonly Guid _accountId;
	private readonly bool _canConnectCurrentProviderAccount;
	private readonly string? _currentProviderAccountActionText;
	private readonly string? _currentProviderAccountDisplayText;
	private readonly bool _hasCurrentProviderAccountIdentity;
	private readonly bool _hasAcceptedClaudeQuotaRisk;
	private readonly bool _isEditing;
	private readonly double _preferredMinHeight;
	private readonly double _preferredMinWidth;
	private readonly string? _providerAccountIdentity;
	private readonly bool _showSubscriptionContext;
	private HwndSource? _windowSource;
	private bool _isWorkAreaRefreshQueued;

	public bool ConnectAfterSave { get; private set; }

	public AccountProfile? EditedProfile { get; private set; }

	public AccountEditorWindow(
		AccountProfile? profile = null,
		bool canAddAntigravity = true,
		string? currentProviderAccountDisplayText = null,
		bool? hasCurrentProviderAccountIdentity = null,
		string? currentProviderAccountActionText = null,
		bool canConnectCurrentProviderAccount = true)
	{
		InitializeComponent();
		_preferredMinHeight = MinHeight;
		_preferredMinWidth = MinWidth;

		_isEditing = profile is not null;
		_accountId = profile?.Id ?? Guid.NewGuid();
		_canConnectCurrentProviderAccount =
			canConnectCurrentProviderAccount;
		_currentProviderAccountActionText =
			currentProviderAccountActionText;
		_currentProviderAccountDisplayText =
			currentProviderAccountDisplayText;
		_hasCurrentProviderAccountIdentity =
			hasCurrentProviderAccountIdentity ??
			!string.IsNullOrWhiteSpace(profile?.ProviderAccountIdentity);
		_hasAcceptedClaudeQuotaRisk =
			profile?.HasAcceptedClaudeQuotaRisk == true;
		_providerAccountIdentity = profile?.ProviderAccountIdentity;
		_showSubscriptionContext = profile?.ShowSubscriptionContext == true;
		ProviderComboBox.ItemsSource = _isEditing
			? ProviderOptions
			: GetProviderOptionsForNewAccount(canAddAntigravity);
		ProviderComboBox.SelectedValue = profile?.Provider ?? ProviderKind.Claude;
		ProviderComboBox.IsEnabled = !_isEditing;
		DisplayNameTextBox.Text = profile?.DisplayName ?? string.Empty;
		EnabledCheckBox.IsChecked = profile?.IsEnabled ?? true;

		if (_isEditing)
		{
			Title = "帳號設定";
			TitleTextBlock.Text = "帳號設定";
			SubtitleTextBlock.Text =
				"可以改暱稱、開關用量檢查，或重新連接帳號。";
			ProviderHintTextBlock.Text =
				"建立後不能更改服務。若要更換，請先移除這個帳號，再重新新增。";
			DisplayNameHintTextBlock.Visibility = Visibility.Collapsed;
		}
		else
		{
			SubtitleTextBlock.Text = "選擇服務並設定帳號。";
		}

		UpdateProviderPresentation();

		Loaded += (_, _) =>
		{
			QueueWorkAreaRefresh();

			if (_isEditing)
			{
				DisplayNameTextBox.Focus();
				DisplayNameTextBox.SelectAll();
			}
			else
			{
				ProviderComboBox.Focus();
			}
		};
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);

		IntPtr windowHandle = new WindowInteropHelper(this).Handle;
		_windowSource = HwndSource.FromHwnd(windowHandle);
		_windowSource?.AddHook(WindowMessageHook);
		LocationChanged += AccountEditorWindow_LocationChanged;
		UpdateWorkAreaConstraints(preferOwner: true);
	}

	protected override void OnClosed(EventArgs e)
	{
		LocationChanged -= AccountEditorWindow_LocationChanged;
		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		base.OnClosed(e);
	}

	private void UpdateWorkAreaConstraints(bool preferOwner = false)
	{
		Window dpiReference = this;
		IntPtr referenceHandle = new WindowInteropHelper(this).Handle;

		if (preferOwner && (Owner is Window owner))
		{
			IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;

			if (ownerHandle != IntPtr.Zero)
			{
				dpiReference = owner;
				referenceHandle = ownerHandle;
			}
		}

		if (referenceHandle == IntPtr.Zero)
		{
			return;
		}

		System.Drawing.Rectangle workingArea =
			FormsScreen.FromHandle(referenceHandle).WorkingArea;
		DpiScale dpi = VisualTreeHelper.GetDpi(dpiReference);
		double maxWidth = WindowWorkAreaLayout.CalculateMaxWidth(
			_preferredMinWidth,
			workingArea.Width,
			dpi.DpiScaleX);
		ApplyWidthConstraints(maxWidth);
		double maxHeight = WindowWorkAreaLayout.CalculateMaxHeight(
			_preferredMinHeight,
			workingArea.Height,
			dpi.DpiScaleY);
		ApplyHeightConstraints(maxHeight);
	}

	private void ApplyHeightConstraints(double maxHeight)
	{
		double targetMinHeight = Math.Min(_preferredMinHeight, maxHeight);

		if (MaxHeight < targetMinHeight)
		{
			MaxHeight = maxHeight;
		}

		MinHeight = targetMinHeight;
		MaxHeight = maxHeight;

		if (double.IsFinite(Height) && (Height > MaxHeight))
		{
			Height = MaxHeight;
		}
		else if (ActualHeight > MaxHeight)
		{
			SizeToContent = System.Windows.SizeToContent.Manual;
			Height = MaxHeight;
		}
	}

	private void ApplyWidthConstraints(double maxWidth)
	{
		double targetMinWidth = Math.Min(_preferredMinWidth, maxWidth);

		if (MaxWidth < targetMinWidth)
		{
			MaxWidth = maxWidth;
		}

		MinWidth = targetMinWidth;
		MaxWidth = maxWidth;

		if (double.IsFinite(Width) && (Width > MaxWidth))
		{
			Width = MaxWidth;
		}
		else if (ActualWidth > MaxWidth)
		{
			Width = MaxWidth;
		}
	}

	private void AccountEditorWindow_LocationChanged(
		object? sender,
		EventArgs e)
	{
		QueueWorkAreaRefresh();
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

	private void QueueWorkAreaRefresh()
	{
		if (_isWorkAreaRefreshQueued)
		{
			return;
		}

		_isWorkAreaRefreshQueued = true;
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				_isWorkAreaRefreshQueued = false;
				UpdateWorkAreaConstraints();
			},
			System.Windows.Threading.DispatcherPriority.ContextIdle);
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
	}

	private void ProviderComboBox_SelectionChanged(
		object sender,
		System.Windows.Controls.SelectionChangedEventArgs e)
	{
		UpdateProviderPresentation();
		RaiseProviderPresentationChanged();
	}

	private void RaiseProviderPresentationChanged(
		bool announceProviderHint = true,
		bool announceConnectionHint = false)
	{
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				if (!IsLoaded ||
					!IsVisible ||
					!IsActive)
				{
					return;
				}

				if (announceProviderHint &&
					!string.IsNullOrWhiteSpace(ProviderHintTextBlock.Text))
				{
					RaiseLiveRegionChanged(ProviderHintTextBlock);
				}

				if ((ProviderNoticePanel.Visibility == Visibility.Visible) &&
					!string.IsNullOrWhiteSpace(ProviderNoticeTextBlock.Text))
				{
					RaiseLiveRegionChanged(ProviderNoticeTextBlock);
				}

				if (announceConnectionHint &&
					(AccountConnectionPanel.Visibility == Visibility.Visible) &&
					!string.IsNullOrWhiteSpace(AccountConnectionHintTextBlock.Text))
				{
					RaiseLiveRegionChanged(AccountConnectionHintTextBlock);
				}
			},
			System.Windows.Threading.DispatcherPriority.Loaded);
	}

	private static void RaiseLiveRegionChanged(UIElement element)
	{
		AutomationPeer? peer =
			UIElementAutomationPeer.FromElement(element) ??
			UIElementAutomationPeer.CreatePeerForElement(element);
		peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
	}

	private void SaveAndConnectButton_Click(object sender, RoutedEventArgs e)
	{
		Save(connectAfterSave: !_isEditing && CanConnectAfterSave());
	}

	private void SaveOnlyButton_Click(object sender, RoutedEventArgs e)
	{
		Save(connectAfterSave: false);
	}

	private void ConnectAccountButton_Click(object sender, RoutedEventArgs e)
	{
		if (!_isEditing || !CanConnectAfterSave())
		{
			return;
		}

		Save(connectAfterSave: true);
	}

	private void Save(bool connectAfterSave)
	{
		if (!TryGetEnabledProvider(
			ProviderComboBox.SelectedItem,
			out ProviderKind provider))
		{
			string message = ProviderComboBox.SelectedItem is ProviderOption option
				? option.UnavailableReason ?? "這個服務目前不能新增。"
				: "請選擇服務。";
			WpfMessageBox.Show(
				this,
				message,
				"帳號設定",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			ProviderComboBox.Focus();
			return;
		}

		string displayName = DisplayNameTextBox.Text.Trim();

		if (displayName.Any(char.IsControl))
		{
			WpfMessageBox.Show(
				this,
				"暱稱不可包含換行、Tab 或其他控制字元。",
				"帳號設定",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			DisplayNameTextBox.Focus();
			DisplayNameTextBox.SelectAll();
			return;
		}

		EditedProfile = new AccountProfile(
			_accountId,
			provider,
			displayName,
			EnabledCheckBox.IsChecked == true,
			_providerAccountIdentity,
			_hasAcceptedClaudeQuotaRisk,
			_showSubscriptionContext);
		ConnectAfterSave = connectAfterSave &&
			(EnabledCheckBox.IsChecked == true);
		DialogResult = true;
	}

	internal static IReadOnlyList<ProviderOption> GetProviderOptionsForNewAccount(
		bool canAddAntigravity)
	{
		return ProviderOptions
			.Select(option =>
				(!canAddAntigravity) &&
				(option.Provider == ProviderKind.Antigravity)
					? option with
					{
						IsEnabled = false,
						UnavailableReason = ExistingAntigravityCardMessage
					}
					: option)
			.ToArray();
	}

	internal static bool TryGetEnabledProvider(
		object? selectedItem,
		out ProviderKind provider)
	{
		if (selectedItem is ProviderOption { IsEnabled: true } option)
		{
			provider = option.Provider;
			return true;
		}

		provider = default;
		return false;
	}

	internal static bool SupportsPostSaveAction(ProviderKind provider)
	{
		return provider is
			ProviderKind.Claude or
			ProviderKind.Codex or
			ProviderKind.Copilot or
			ProviderKind.Antigravity or
			ProviderKind.Grok;
	}

	internal static string GetPrimaryActionText(
		ProviderKind provider,
		bool isEnabled)
	{
		if (!isEnabled || !SupportsPostSaveAction(provider))
		{
			return "儲存";
		}

		return "儲存並連接";
	}

	internal static string GetConnectionActionText(
		ProviderKind provider,
		bool hasProviderAccountIdentity)
	{
		string providerName = provider switch
		{
			ProviderKind.Claude => "Claude",
			ProviderKind.Codex => "Codex",
			ProviderKind.Copilot => "Copilot",
			ProviderKind.Antigravity => "Antigravity",
			ProviderKind.Grok => "Grok",
			_ => string.Empty
		};
		string action = hasProviderAccountIdentity
			? provider == ProviderKind.Antigravity
				? "重新連接"
				: "切換"
			: "連接";

		return string.IsNullOrWhiteSpace(providerName)
			? "連接帳號"
			: $"{action} {providerName} 帳號";
	}

	internal static string GetConnectionHintText(
		ProviderKind provider,
		bool isEnabled,
		bool requiresClaudeQuotaRiskConsent = false,
		bool canConnectProviderAccount = true)
	{
		if (!isEnabled)
		{
			return "請先勾選「開始檢查用量」，才能連接帳號。";
		}

		if (!canConnectProviderAccount)
		{
			return "目前無法連接。請先處理卡片上的提示。";
		}

		if ((provider == ProviderKind.Claude) &&
			requiresClaudeQuotaRiskConsent)
		{
			return "儲存後會先確認是否讀取 Claude 用量。需要登入時才會開啟 Claude Code CLI。";
		}

		return provider switch
		{
			ProviderKind.Claude =>
				"儲存後會開啟 Claude Code CLI，讓你登入或切換帳號。",
			ProviderKind.Codex =>
				"儲存後先選「一般帳號連接」或「用 workspace ID 連接」，再用 Codex CLI 登入。",
			ProviderKind.Copilot =>
				"儲存後會開啟 GitHub 官方授權頁面，讓你登入或切換 Copilot 帳號。",
			ProviderKind.Antigravity => JoinParagraphs(
				"儲存後會沿用這台電腦目前登入的 Antigravity 帳號，不會另開登入畫面。",
				"使用企業帳號時，AI Usage 無法確認或指定專案。"),
			ProviderKind.Grok =>
				"儲存後會開啟 Grok Build CLI，讓你登入或切換帳號。",
			_ => string.Empty
		};
	}

	internal static string GetProviderNoticeTitleText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => "連接 Claude 前",
			ProviderKind.Codex => "連接 Codex 前",
			ProviderKind.Copilot => "連接 Copilot 前",
			ProviderKind.Antigravity => "連接 Antigravity 前",
			ProviderKind.Grok => "連接 Grok 前",
			_ => "連接前注意事項"
		};
	}

	internal static string? GetProviderNoticeText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => JoinParagraphs(
				"第一次讀取會執行 Claude /usage，可能產生少量用量。",
				"連接前會再請你確認。已產生的用量或費用不能取消。"),
			ProviderKind.Codex => JoinParagraphs(
				"一般帳號連接不需要 workspace ID。",
				"同一個 ChatGPT 帳號若要顯示多個 workspace，每張卡片都要填入對應的 workspace ID，原本的第一張也一樣。"),
			ProviderKind.Copilot => JoinParagraphs(
				"請在 GitHub 官方頁面完成授權。連接後，每張卡片的 credential 會獨立儲存在 Windows Credential Manager。",
				"同一個 GitHub 帳號不能重複連接到多張卡片。organization 或 subscription 不會拆成多張額度卡。"),
			ProviderKind.Antigravity => JoinParagraphs(
				"請先在這台電腦登入 Antigravity。AI Usage 不會另開登入畫面。",
				"請使用支援官方唯讀 /usage 的 Antigravity 版本。",
				"使用企業帳號時，AI Usage 無法確認或指定專案。"),
			ProviderKind.Grok => JoinParagraphs(
				"登入會在終端機中完成。",
				"登入時若網頁顯示授權碼或完整回呼網址，請貼回終端機。",
				"帳號沒有電子郵件時，可以用暱稱區分卡片。"),
			_ => null
		};
	}

	internal static string GetProviderStoredDataText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => JoinParagraphs(
				"Claude Code CLI 負責登入。AI Usage 會確認目前的帳號與 Claude organization 是否和這張卡片原本的一樣。",
				"這台電腦的上次用量資料會保留電子郵件、方案和 organization 名稱，但不會跟著設定匯出。",
				"organization ID 本身不會直接儲存。這張卡片也會記住你是否同意讀取 Claude 用量。"),
			ProviderKind.Codex => JoinParagraphs(
				"Codex CLI 負責登入。AI Usage 會確認目前的 ChatGPT 帳號與 workspace 是否和這張卡片原本的一樣。",
				"這台電腦的上次用量資料會保留電子郵件和方案，但不會跟著設定匯出。",
				"workspace ID 只會寫進這張卡片的 Codex 設定；重新連接時要再輸入。"),
			ProviderKind.Antigravity => JoinParagraphs(
				"Antigravity CLI 負責登入。AI Usage 不會讀取密碼或其他登入憑證。",
				"這台電腦會保留核准的 CLI 來源、四項用量和讀取時間。官方 /usage 不提供電子郵件或方案，AI Usage 不會為此安裝 status-line helper。"),
			ProviderKind.Grok => JoinParagraphs(
				"Grok Build CLI 會把每張卡片的登入資料分開儲存。",
				"移除卡片或匯入設定時，相關登入資料也會刪除。"),
			ProviderKind.Copilot => JoinParagraphs(
				"AI Usage 會把每張卡片的 GitHub credential 分開儲存在 Windows Credential Manager。",
				"credential 不會寫入帳號設定、上次用量、診斷紀錄或匯出的設定；移除卡片時也會刪除。"),
			_ => "AI Usage 只會儲存卡片設定和上次用量。"
		};
	}

	internal static bool ShouldAnnounceProviderNotice(
		bool isLoaded,
		bool wasVisible,
		bool isVisible)
	{
		return isLoaded && !wasVisible && isVisible;
	}

	private static string JoinParagraphs(params string[] paragraphs)
	{
		return string.Join(
			Environment.NewLine + Environment.NewLine,
			paragraphs);
	}

	private bool CanConnectAfterSave()
	{
		return
			(ProviderComboBox.SelectedValue is ProviderKind provider) &&
			SupportsPostSaveAction(provider) &&
			(EnabledCheckBox.IsChecked == true) &&
			(!_isEditing || _canConnectCurrentProviderAccount);
	}

	private string GetCurrentConnectionActionText(ProviderKind provider)
	{
		if ((provider == ProviderKind.Claude) &&
			!_hasAcceptedClaudeQuotaRisk)
		{
			return "確認 Claude 用量讀取";
		}

		return string.IsNullOrWhiteSpace(_currentProviderAccountActionText)
			? GetConnectionActionText(
				provider,
				_hasCurrentProviderAccountIdentity)
			: _currentProviderAccountActionText;
	}

	private void EnabledCheckBox_CheckStateChanged(
		object sender,
		RoutedEventArgs e)
	{
		bool wasProviderNoticeVisible =
			ProviderNoticePanel?.Visibility == Visibility.Visible;
		UpdateProviderPresentation();

		if (_isEditing)
		{
			RaiseProviderPresentationChanged(
				announceProviderHint: false,
				announceConnectionHint: true);
			return;
		}

		if (ShouldAnnounceProviderNotice(
				IsLoaded,
				wasProviderNoticeVisible,
				ProviderNoticePanel?.Visibility == Visibility.Visible))
		{
			RaiseProviderPresentationChanged(announceProviderHint: false);
		}
	}

	private void UpdateProviderPresentation()
	{
		if ((ProviderComboBox is null) ||
			(ProviderHintTextBlock is null) ||
			(ProviderNoticePanel is null) ||
			(ProviderNoticeTitleTextBlock is null) ||
			(ProviderNoticeTextBlock is null) ||
			(ProviderStoredDataTextBlock is null) ||
			(AccountConnectionPanel is null) ||
			(AccountTextBlock is null) ||
			(AccountConnectionHintTextBlock is null) ||
			(ConnectAccountButton is null) ||
			(SaveOnlyButton is null) ||
			(SaveAndConnectButton is null) ||
			(ProviderComboBox.SelectedValue is not ProviderKind provider))
		{
			return;
		}

		ProviderStoredDataTextBlock.Text =
			GetProviderStoredDataText(provider);

		if (_isEditing)
		{
			bool isUsageCheckEnabled = EnabledCheckBox.IsChecked == true;
			bool supportsConnection = SupportsPostSaveAction(provider);
			ProviderNoticePanel.Visibility = Visibility.Collapsed;
			AccountConnectionPanel.Visibility = supportsConnection
				? Visibility.Visible
				: Visibility.Collapsed;
			AccountTextBlock.Text = _hasCurrentProviderAccountIdentity
				? string.IsNullOrWhiteSpace(_currentProviderAccountDisplayText)
					? "帳號：已連接"
					: $"帳號：{_currentProviderAccountDisplayText}"
				: "帳號：尚未連接";
			AccountConnectionHintTextBlock.Text =
				GetConnectionHintText(
					provider,
					isUsageCheckEnabled,
					requiresClaudeQuotaRiskConsent:
						(provider == ProviderKind.Claude) &&
						!_hasAcceptedClaudeQuotaRisk,
					canConnectProviderAccount:
						_canConnectCurrentProviderAccount);
			ConnectAccountButton.Content =
				GetCurrentConnectionActionText(provider);
			ConnectAccountButton.IsEnabled =
				isUsageCheckEnabled &&
				_canConnectCurrentProviderAccount;
			SaveOnlyButton.Visibility = Visibility.Collapsed;
			SaveAndConnectButton.Content = "儲存";
			SaveAndConnectButton.IsEnabled = true;
			return;
		}

		AccountConnectionPanel.Visibility = Visibility.Collapsed;
		ProviderHintTextBlock.Text = provider switch
		{
			ProviderKind.Claude =>
				"使用 Claude Code CLI 登入或切換帳號。",
			ProviderKind.Codex =>
				"使用 Codex CLI 登入。",
			ProviderKind.Copilot =>
				"使用 GitHub 官方授權連接不同的 Copilot 帳號。",
			ProviderKind.Antigravity =>
				"使用這台電腦目前登入的 Antigravity 帳號。",
			ProviderKind.Grok =>
				"使用 Grok Build CLI 登入或切換帳號。",
			_ => "目前不支援用 CLI 連接或讀取用量。"
		};

		bool isEnabled = EnabledCheckBox.IsChecked == true;
		bool supportsPostSaveAction = SupportsPostSaveAction(provider);
		string? providerNotice = GetProviderNoticeText(provider);
		ProviderNoticeTitleTextBlock.Text = GetProviderNoticeTitleText(provider);
		ProviderNoticeTextBlock.Text = providerNotice ?? string.Empty;
		ProviderNoticePanel.Visibility =
			isEnabled && !string.IsNullOrWhiteSpace(providerNotice)
			? Visibility.Visible
			: Visibility.Collapsed;
		SaveOnlyButton.Visibility = supportsPostSaveAction && isEnabled
			? Visibility.Visible
			: Visibility.Collapsed;
		SaveAndConnectButton.Content = GetPrimaryActionText(provider, isEnabled);
		SaveAndConnectButton.IsEnabled = true;
	}
}
