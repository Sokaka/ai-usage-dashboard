using System.Windows;

using WpfMessageBox = System.Windows.MessageBox;

namespace AiUsageDashboard.App;

public sealed partial class CodexWorkspacePromptWindow : Window
{
	public Guid? WorkspaceId { get; private set; }

	public CodexWorkspacePromptWindow()
	{
		InitializeComponent();
		Loaded += (_, _) => DefaultConnectionButton.Focus();
	}

	internal static bool TryNormalizeWorkspaceId(
		string? value,
		out Guid workspaceId)
	{
		return Guid.TryParse(value?.Trim(), out workspaceId) &&
			(workspaceId != Guid.Empty);
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
