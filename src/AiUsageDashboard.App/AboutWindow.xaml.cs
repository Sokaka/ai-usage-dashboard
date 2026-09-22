using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;

using FormsScreen = System.Windows.Forms.Screen;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

public partial class AboutWindow : Window
{
	private const double PreferredMinimumWidth = 320;

	internal event EventHandler? UpdateCheckRequested;

	public AboutWindow()
	{
		InitializeComponent();
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

	internal void UpdateUpdateStatus(
		string statusText,
		bool canCheck,
		bool isChecking)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
		bool didStatusChange = !string.Equals(
			UpdateStatusTextBlock.Text,
			statusText,
			StringComparison.Ordinal);
		UpdateStatusTextBlock.Text = statusText;
		CheckForUpdatesButton.IsEnabled = canCheck && !isChecking;
		CheckForUpdatesButton.Content = isChecking
			? "檢查中…"
			: "檢查更新";

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
			CopyStatusTextBlock.Text = "已複製版本。";
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
				$"無法複製 AI Usage 版本，剪貼簿可能正被其他程式使用。請稍後再試，或選取版本文字手動複製。\n\n{exception.Message}",
				"無法複製版本",
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
			"無法開啟使用說明",
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
				$"Windows 無法開啟支援頁面。請在瀏覽器開啟：\n{AboutLinkLauncher.GetUrl(link)}\n\n{exception.Message}",
				"無法開啟支援頁面",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
