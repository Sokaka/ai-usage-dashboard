using System.Windows;
using System.Windows.Controls;

using AiUsageDashboard.Licensing;

using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace AiUsageDashboard.LegalUi;

internal static class LegalTermsDialog
{
	public static bool EnsureAccepted(LegalCatalog catalog, LegalAcceptanceStore store)
	{
		if (store.IsAccepted(catalog))
		{
			return true;
		}
		if (!Show(catalog, allowAcceptance: true))
		{
			return false;
		}
		store.Accept(catalog, catalog.Digest);
		return true;
	}

	public static bool Show(LegalCatalog catalog, bool allowAcceptance)
	{
		Window window = new()
		{
			Title = "AI Usage Dashboard 授權與第三方條款",
			Width = 820,
			Height = 680,
			MinWidth = 520,
			MinHeight = 360,
			WindowStartupLocation = WindowStartupLocation.CenterScreen
		};
		window.SetResourceReference(Window.BackgroundProperty,
			"WindowBackgroundBrush");
		window.SetResourceReference(Window.IconProperty,
			"ThemeWindowIcon");
		DockPanel panel = new() { Margin = new Thickness(16) };
		TextBlock explanation = new()
		{
			Text = allowAcceptance
				? "首次使用前，請閱讀適用的第三方條款。接受後會在這個 Windows 使用者的本機保存版本與範圍；背景查詢不會重複詢問。"
				: "以下是這個程式適用的授權與第三方條款。查看條款不會建立接受紀錄。",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 12)
		};
		explanation.SetResourceReference(TextBlock.ForegroundProperty,
			"SecondaryTextBrush");
		DockPanel.SetDock(explanation, Dock.Top);
		panel.Children.Add(explanation);
		StackPanel buttons = new()
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 12, 0, 0)
		};
		Button close = new()
		{
			Content = allowAcceptance ? "不接受並離開" : "關閉",
			IsCancel = true
		};
		close.SetResourceReference(FrameworkElement.StyleProperty,
			"SecondaryButtonStyle");
		close.Click += (_, _) => window.DialogResult = false;
		buttons.Children.Add(close);
		if (allowAcceptance)
		{
			Button accept = new()
			{
				Content = "接受並繼續",
				Margin = new Thickness(12, 0, 0, 0)
			};
			accept.SetResourceReference(FrameworkElement.StyleProperty,
				"PrimaryButtonStyle");
			accept.Click += (_, _) => window.DialogResult = true;
			buttons.Children.Add(accept);
		}
		DockPanel.SetDock(buttons, Dock.Bottom);
		panel.Children.Add(buttons);
		TextBox text = new()
		{
			Text = catalog.GetReadableText(),
			IsReadOnly = true,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(10),
			FontSize = 13
		};
		panel.Children.Add(text);
		window.Content = panel;
		return window.ShowDialog() == true;
	}
}
