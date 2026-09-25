using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using FormsScreen = System.Windows.Forms.Screen;
using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

public sealed partial class CodexWorkspacePromptWindow : Window
{
	private const double PreferredMinimumWidth = 380;

	public Guid? WorkspaceId { get; private set; }

	public CodexWorkspacePromptWindow()
	{
		InitializeComponent();
		Loaded += (_, _) => DefaultConnectionButton.Focus();
		DpiChanged += (_, _) => UpdateWorkAreaConstraints();
		LocationChanged += (_, _) => UpdateWorkAreaConstraints();
	}

	internal static bool TryNormalizeWorkspaceId(
		string? value,
		out Guid workspaceId)
	{
		return Guid.TryParse(value?.Trim(), out workspaceId) &&
			(workspaceId != Guid.Empty);
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		UpdateWorkAreaConstraints(preferOwner: true);
	}

	private void UpdateWorkAreaConstraints(bool preferOwner = false)
	{
		Window referenceWindow = this;
		IntPtr referenceHandle = new WindowInteropHelper(this).Handle;

		if (preferOwner && (Owner is Window owner))
		{
			IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
			if (ownerHandle != IntPtr.Zero)
			{
				referenceWindow = owner;
				referenceHandle = ownerHandle;
			}
		}

		if (referenceHandle == IntPtr.Zero)
		{
			return;
		}

		System.Drawing.Rectangle workArea =
			FormsScreen.FromHandle(referenceHandle).WorkingArea;
		DpiScale dpi = VisualTreeHelper.GetDpi(referenceWindow);
		double maximumWidth = WindowWorkAreaLayout.CalculateMaxWidth(
			PreferredMinimumWidth, workArea.Width, dpi.DpiScaleX);
		double targetMinimumWidth = Math.Min(
			PreferredMinimumWidth, maximumWidth);
		if (MaxWidth < targetMinimumWidth)
		{
			MaxWidth = maximumWidth;
		}

		MinWidth = targetMinimumWidth;
		MaxWidth = maximumWidth;
		MaxHeight = WindowWorkAreaLayout.CalculateMaxHeight(
			0, workArea.Height, dpi.DpiScaleY);
	}

	private void ContinueWithoutWorkspaceButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		WorkspaceId = null;
		DialogResult = true;
	}

	private void ContinueWithWorkspaceButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		TryContinueWithWorkspace();
	}

	private void WorkspaceIdTextBox_PreviewKeyDown(
		object sender,
		System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key != System.Windows.Input.Key.Enter)
		{
			return;
		}

		e.Handled = true;
		TryContinueWithWorkspace();
	}

	private void TryContinueWithWorkspace()
	{
		if (!TryNormalizeWorkspaceId(
				WorkspaceIdTextBox.Text,
				out Guid workspaceId))
		{
			WpfMessageBox.Show(
				this,
				"請輸入有效的 ChatGPT workspace ID（UUID 格式）。",
				"連接 Codex",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			WorkspaceIdTextBox.Focus();
			WorkspaceIdTextBox.SelectAll();
			return;
		}

		WorkspaceId = workspaceId;
		DialogResult = true;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
	}
}
