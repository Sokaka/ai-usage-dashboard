using System.Windows;

namespace AiUsageDashboard.App;

public partial class AutomaticSortRulesWindow : Window
{
	public AutomaticSortRulesWindow()
	{
		InitializeComponent();
		Loaded += (_, _) => CloseButton.Focus();
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
