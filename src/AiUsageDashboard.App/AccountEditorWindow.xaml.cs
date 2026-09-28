using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;

using AiUsageDashboard.Core.Localization;
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
	private readonly bool _canAddAntigravity;
	private readonly bool _canConnectCurrentProviderAccount;
	private readonly string? _currentProviderAccountActionText;
	private readonly Func<string>? _currentProviderAccountActionTextProvider;
	private readonly string? _currentProviderAccountDisplayText;
	private readonly Func<string>? _currentProviderAccountDisplayTextProvider;
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

	private static string ExistingAntigravityCardMessage =>
		UiText.Get("Windows.Editor.ThisWindowsUserAlreadyHasAnAntigravityCard");

	public AccountEditorWindow(
		AccountProfile? profile = null,
		bool canAddAntigravity = true,
		string? currentProviderAccountDisplayText = null,
		bool? hasCurrentProviderAccountIdentity = null,
		string? currentProviderAccountActionText = null,
		bool canConnectCurrentProviderAccount = true,
		Func<string>? currentProviderAccountActionTextProvider = null,
		Func<string>? currentProviderAccountDisplayTextProvider = null,
		ResourceDictionary? windowResources = null)
	{
		if (windowResources is not null)
		{
			Resources = windowResources;
		}
		InitializeComponent();
		_preferredMinHeight = MinHeight;
		_preferredMinWidth = MinWidth;

		_isEditing = profile is not null;
		_accountId = profile?.Id ?? Guid.NewGuid();
		_canAddAntigravity = canAddAntigravity;
		_canConnectCurrentProviderAccount =
			canConnectCurrentProviderAccount;
		_currentProviderAccountActionText =
			currentProviderAccountActionText;
		_currentProviderAccountActionTextProvider = currentProviderAccountActionTextProvider;
		_currentProviderAccountDisplayText =
			currentProviderAccountDisplayText;
		_currentProviderAccountDisplayTextProvider = currentProviderAccountDisplayTextProvider;
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

		RefreshLocalizedPresentation();
		UiText.LanguageChanged += LanguageChanged;

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
		UiText.LanguageChanged -= LanguageChanged;
		LocationChanged -= AccountEditorWindow_LocationChanged;
		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		base.OnClosed(e);
	}

	private void LanguageChanged(object? sender, EventArgs e)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(RefreshLocalizedPresentation);
			return;
		}

		RefreshLocalizedPresentation();
	}

	internal void RefreshLocalizedPresentation()
	{
		if (!_isEditing)
		{
			object? selectedProvider = ProviderComboBox.SelectedValue;
			ProviderComboBox.ItemsSource = GetProviderOptionsForNewAccount(_canAddAntigravity);
			ProviderComboBox.SelectedValue = selectedProvider;
		}

		Title = UiText.Get("Windows.Editor.AccountSettings");
		TitleTextBlock.Text = UiText.Get(_isEditing
			? "Windows.Editor.AccountSettings"
			: "Windows.Editor.AddAccount");
		SubtitleTextBlock.Text = UiText.Get(_isEditing
			? "Windows.Editor.ChangeTheNicknameTurnUsageChecksOnOr"
			: "Windows.Editor.ChooseAServiceAndSetUpTheAccount");
		if (_isEditing)
		{
			ProviderHintTextBlock.Text = UiText.Get("Windows.Editor.TheServiceCannotBeChangedAfterCreationRemove");
			DisplayNameHintTextBlock.Visibility = Visibility.Collapsed;
		}

		UpdateProviderPresentation();
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
				? option.UnavailableReason ?? UiText.Get("Windows.Editor.ThisServiceCannotBeAddedRightNow")
				: UiText.Get("Windows.Editor.ChooseAService");
			WpfMessageBox.Show(
				this,
				message,
				UiText.Get("Windows.Editor.AccountSettings"),
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
				UiText.Get("Windows.Editor.TheNicknameCannotContainLineBreaksTabsOr"),
				UiText.Get("Windows.Editor.AccountSettings"),
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
			return UiText.Get("Windows.Common.Save");
		}

		return UiText.Get("Windows.Common.SaveAndConnect");
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
				? UiText.Get("Windows.Editor.Reconnect")
				: UiText.Get("Windows.Editor.Switch")
			: UiText.Get("Windows.Editor.Connect");

		return string.IsNullOrWhiteSpace(providerName)
			? UiText.Get("Windows.Editor.ConnectAccount")
			: UiText.Format("Windows.Editor.01Account", action, providerName);
	}

	internal static string GetConnectionHintText(
		ProviderKind provider,
		bool isEnabled,
		bool requiresClaudeQuotaRiskConsent = false,
		bool canConnectProviderAccount = true)
	{
		if (!isEnabled)
		{
			return UiText.Get("Windows.Editor.SelectStartUsageChecksBeforeConnectingTheAccount");
		}

		if (!canConnectProviderAccount)
		{
			return UiText.Get("Windows.Editor.ConnectionIsUnavailableFollowTheNoticeOnThe");
		}

		if ((provider == ProviderKind.Claude) &&
			requiresClaudeQuotaRiskConsent)
		{
			return UiText.Get("Windows.Editor.AfterSavingYouWillBeAskedToConfirm");
		}

		return provider switch
		{
			ProviderKind.Claude =>
				UiText.Get("Windows.Editor.AfterSavingClaudeCodeCLIOpensSoYou"),
			ProviderKind.Codex =>
				UiText.Get("Windows.Editor.AfterSavingChooseStandardAccountConnectionOrConnect"),
			ProviderKind.Copilot =>
				UiText.Get("Windows.Editor.AfterSavingTheOfficialGitHubAuthorizationPageOpens"),
			ProviderKind.Antigravity => JoinParagraphs(
				UiText.Get("Windows.Editor.AfterSavingTheCurrentAntigravitySignInOn"),
				UiText.Get("Windows.Editor.ForEnterpriseAccountsAIUsageCannotVerifyOr")),
			ProviderKind.Grok =>
				UiText.Get("Windows.Editor.AfterSavingGrokBuildCLIOpensSoYou"),
			_ => string.Empty
		};
	}

	internal static string GetProviderNoticeTitleText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => UiText.Get("Windows.Editor.BeforeConnectingClaude"),
			ProviderKind.Codex => UiText.Get("Windows.Editor.BeforeConnectingCodex"),
			ProviderKind.Copilot => UiText.Get("Windows.Editor.BeforeConnectingCopilot"),
			ProviderKind.Antigravity => UiText.Get("Windows.Editor.BeforeConnectingAntigravity"),
			ProviderKind.Grok => UiText.Get("Windows.Editor.BeforeConnectingGrok"),
			_ => UiText.Get("Windows.Editor.BeforeConnecting")
		};
	}

	internal static string? GetProviderNoticeText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => JoinParagraphs(
				UiText.Get("Windows.Editor.TheFirstCheckRunsClaudeUsageAndMay"),
				UiText.Get("Windows.Editor.AIUsageCanOnlyDetermineAfterExecutionWhether"),
				UiText.Get("Windows.Editor.ByChoosingSaveAndConnectYouAcceptThese")),
			ProviderKind.Codex => JoinParagraphs(
				UiText.Get("Windows.Editor.AStandardAccountConnectionDoesNotNeedA"),
				UiText.Get("Windows.Editor.ToDisplayMultipleWorkspacesFromOneChatGPTAccount")),
			ProviderKind.Copilot => JoinParagraphs(
				UiText.Get("Windows.Editor.CompleteAuthorizationOnTheOfficialGitHubPageEach"),
				UiText.Get("Windows.Editor.AGitHubAccountCannotBeConnectedToMore")),
			ProviderKind.Antigravity => JoinParagraphs(
				UiText.Get("Windows.Editor.SignInToAntigravityOnThisComputerFirst"),
				UiText.Get("Windows.Editor.UseAnAntigravityVersionThatSupportsTheOfficial"),
				UiText.Get("Windows.Editor.ForEnterpriseAccountsAIUsageCannotVerifyOr")),
			ProviderKind.Grok => JoinParagraphs(
				UiText.Get("Windows.Editor.SignInTakesPlaceInTheTerminal"),
				UiText.Get("Windows.Editor.IfTheSignInPageShowsAnAuthorization"),
				UiText.Get("Windows.Editor.IfTheAccountHasNoEmailAddressUse")),
			_ => null
		};
	}

	internal static string GetProviderStoredDataText(ProviderKind provider)
	{
		return provider switch
		{
			ProviderKind.Claude => JoinParagraphs(
				UiText.Get("Windows.Editor.ClaudeCodeCLIHandlesSignInAIUsage"),
				UiText.Get("Windows.Editor.TheLastUsageResultOnThisComputerRetains"),
				UiText.Get("Windows.Editor.TheOrganizationIDItselfIsNotStoredDirectly")),
			ProviderKind.Codex => JoinParagraphs(
				UiText.Get("Windows.Editor.CodexCLIHandlesSignInAIUsageChecks"),
				UiText.Get("Windows.Editor.TheLastUsageResultOnThisComputerRetains182"),
				UiText.Get("Windows.Editor.TheWorkspaceIDIsWrittenOnlyToThis")),
			ProviderKind.Antigravity => JoinParagraphs(
				UiText.Get("Windows.Editor.AntigravityCLIHandlesSignInAIUsageDoes"),
				UiText.Get("Windows.Editor.ThisComputerRetainsTheApprovedCLISourceFour")),
			ProviderKind.Grok => JoinParagraphs(
				UiText.Get("Windows.Editor.GrokBuildCLIStoresEachCardSSign"),
				UiText.Get("Windows.Editor.TheAssociatedSignInDataIsDeletedWhen")),
			ProviderKind.Copilot => JoinParagraphs(
				UiText.Get("Windows.Editor.AIUsageStoresEachCardSGitHubCredential"),
				UiText.Get("Windows.Editor.CredentialsAreNotWrittenToAccountSettingsUsage")),
			_ => UiText.Get("Windows.Editor.AIUsageStoresOnlyCardSettingsAndThe")
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
			return UiText.Get("Windows.Editor.ConfirmClaudeUsageCheck");
		}

		string? actionText = _currentProviderAccountActionTextProvider?.Invoke()
			?? _currentProviderAccountActionText;
		return string.IsNullOrWhiteSpace(actionText)
			? GetConnectionActionText(
				provider,
				_hasCurrentProviderAccountIdentity)
			: actionText;
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
			string? accountDisplayText = _currentProviderAccountDisplayTextProvider?.Invoke()
				?? _currentProviderAccountDisplayText;
			AccountTextBlock.Text = _hasCurrentProviderAccountIdentity
				? string.IsNullOrWhiteSpace(accountDisplayText)
					? UiText.Get("Windows.Editor.AccountConnected")
					: UiText.Format("Windows.Editor.Account0", accountDisplayText)
				: UiText.Get("Windows.Editor.AccountNotConnected");
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
			SaveAndConnectButton.Content = UiText.Get("Windows.Common.Save");
			SaveAndConnectButton.IsEnabled = true;
			return;
		}

		AccountConnectionPanel.Visibility = Visibility.Collapsed;
		ProviderHintTextBlock.Text = provider switch
		{
			ProviderKind.Claude =>
				UiText.Get("Windows.Editor.SignInOrSwitchAccountsWithClaudeCode"),
			ProviderKind.Codex =>
				UiText.Get("Windows.Editor.SignInWithCodexCLI"),
			ProviderKind.Copilot =>
				UiText.Get("Windows.Editor.UseOfficialGitHubAuthorizationToConnectDifferentCopilot"),
			ProviderKind.Antigravity =>
				UiText.Get("Windows.Editor.UseTheCurrentAntigravitySignInOnThis"),
			ProviderKind.Grok =>
				UiText.Get("Windows.Editor.SignInOrSwitchAccountsWithGrokBuild"),
			_ => UiText.Get("Windows.Editor.CLIConnectionAndUsageChecksAreNotCurrently")
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
