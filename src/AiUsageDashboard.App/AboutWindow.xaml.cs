using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;

using AiUsageDashboard.Core.Localization;

using FormsScreen = System.Windows.Forms.Screen;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

public partial class AboutWindow : Window
{
	private const double PreferredMinimumWidth = 320;
	private Func<string>? _updateStatusTextProvider;
	private bool _canCheck;
	private bool _isChecking;

	internal event EventHandler? UpdateCheckRequested;

	public AboutWindow()
	{
		InitializeComponent();
		UiText.LanguageChanged += LanguageChanged;
		VersionTextBox.Text = AppVersionInfo.GetDisplayVersion(typeof(App).Assembly);
		Loaded += (_, _) => CopyVersionButton.Focus();
		DpiChanged += (_, _) => UpdateWorkAreaConstraints();
		LocationChanged += (_, _) => UpdateWorkAreaConstraints();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		UpdateWorkAreaConstraints();
	}

	protected override void OnClosed(EventArgs e)
	{
		UiText.LanguageChanged -= LanguageChanged;
		base.OnClosed(e);
	}

	internal void UpdateUpdateStatus(
		Func<string> statusTextProvider,
		bool canCheck,
		bool isChecking)
	{
		ArgumentNullException.ThrowIfNull(statusTextProvider);
		UpdateUpdateStatus(statusTextProvider(), canCheck, isChecking);
		_updateStatusTextProvider = statusTextProvider;
	}

	internal void UpdateUpdateStatus(
		string statusText,
		bool canCheck,
		bool isChecking)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
		_updateStatusTextProvider = null;
		_canCheck = canCheck;
		_isChecking = isChecking;
		bool didStatusChange = !string.Equals(
			UpdateStatusTextBlock.Text,
			statusText,
			StringComparison.Ordinal);
		UpdateStatusTextBlock.Text = statusText;
		CheckForUpdatesButton.IsEnabled = canCheck && !isChecking;
		CheckForUpdatesButton.SetResourceReference(
			System.Windows.Controls.ContentControl.ContentProperty,
			isChecking ? "Windows.Common.Checking" : "Windows.About.CheckForUpdates");

		if (didStatusChange && IsVisible)
		{
			_ = Dispatcher.BeginInvoke(
				() =>
				{
					AutomationPeer? peer = UIElementAutomationPeer.FromElement(
						UpdateStatusTextBlock) ??
						UIElementAutomationPeer.CreatePeerForElement(
							UpdateStatusTextBlock);
					peer?.RaiseAutomationEvent(
						AutomationEvents.LiveRegionChanged);
				},
				System.Windows.Threading.DispatcherPriority.Loaded);
		}
	}

	private void LanguageChanged(object? sender, EventArgs e)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(() => LanguageChanged(sender, e));
			return;
		}

		if (_updateStatusTextProvider is Func<string> provider)
		{
			UpdateUpdateStatus(provider, _canCheck, _isChecking);
		}
	}

	private void UpdateWorkAreaConstraints()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;

		if (handle == IntPtr.Zero)
		{
			return;
		}

		System.Drawing.Rectangle workArea = FormsScreen.FromHandle(handle).WorkingArea;
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		double maximumWidth = WindowWorkAreaLayout.CalculateMaxWidth(
			PreferredMinimumWidth, workArea.Width, dpi.DpiScaleX);
		MinWidth = Math.Min(PreferredMinimumWidth, maximumWidth);
		MaxWidth = maximumWidth;
		MaxHeight = WindowWorkAreaLayout.CalculateMaxHeight(
			0, workArea.Height, dpi.DpiScaleY);
	}

	private void CopyVersionButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			WpfClipboard.SetText($"AI Usage {VersionTextBox.Text}");
			CopyStatusTextBlock.SetResourceReference(
				System.Windows.Controls.TextBlock.TextProperty,
				"Windows.About.VersionCopied");
			AutomationPeer? peer =
				UIElementAutomationPeer.FromElement(CopyStatusTextBlock) ??
				UIElementAutomationPeer.CreatePeerForElement(CopyStatusTextBlock);
			peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
		}
		catch (ExternalException exception)
		{
			CopyStatusTextBlock.Text = string.Empty;
			WpfMessageBox.Show(
				this,
				UiText.Format("Windows.About.CouldNotCopyTheAIUsageVersionAnother", exception.Message),
				UiText.Get("Windows.About.CouldNotCopyVersion"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	private void UserGuideButton_Click(object sender, RoutedEventArgs e)
	{
		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open();

		if (result == LocalUserGuideOpenResult.Opened)
		{
			return;
		}

		WpfMessageBox.Show(
			this,
			LocalUserGuideLauncher.GetFailureMessage(result),
			UiText.Get("Windows.About.CouldNotOpenUserGuide"),
			MessageBoxButton.OK,
			MessageBoxImage.Warning);
	}

	private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateCheckRequested?.Invoke(this, EventArgs.Empty);
	}

	private void ReleasesButton_Click(object sender, RoutedEventArgs e)
	{
		OpenLink(AboutLink.Releases);
	}

	private void ReportIssueButton_Click(object sender, RoutedEventArgs e)
	{
		OpenLink(AboutLink.ReportIssue);
	}

	private void OpenLink(AboutLink link)
	{
		try
		{
			AboutLinkLauncher.Open(link);
		}
		catch (Exception exception) when (
			(exception is Win32Exception) ||
			(exception is InvalidOperationException))
		{
			WpfMessageBox.Show(
				this,
				UiText.Format("Windows.About.WindowsCouldNotOpenTheSupportPageOpen", AboutLinkLauncher.GetUrl(link), exception.Message),
				UiText.Get("Windows.About.CouldNotOpenSupportPage"),
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
