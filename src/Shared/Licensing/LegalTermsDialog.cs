using System.Windows;
using System.Windows.Controls;

using AiUsageDashboard.Core.Localization;
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
			Width = 820,
			Height = 680,
			MinWidth = 520,
			MinHeight = 360,
			WindowStartupLocation = WindowStartupLocation.CenterScreen
		};
		window.SetResourceReference(Window.TitleProperty, "Windows.Legal.Title");
		window.SetResourceReference(Window.BackgroundProperty,
			"WindowBackgroundBrush");
		window.SetResourceReference(Window.IconProperty,
			"ThemeWindowIcon");
		DockPanel panel = new() { Margin = new Thickness(16) };
		TextBlock explanation = new()
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 12)
		};
		explanation.SetResourceReference(TextBlock.TextProperty, allowAcceptance
			? "Windows.Legal.AcceptanceExplanation" : "Windows.Legal.ViewExplanation");
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
			IsCancel = true
		};
		close.SetResourceReference(ContentControl.ContentProperty,
			allowAcceptance ? "Windows.Legal.Decline" : "Windows.Common.Close");
		close.SetResourceReference(FrameworkElement.StyleProperty,
			"SecondaryButtonStyle");
		close.Click += (_, _) => window.DialogResult = false;
		buttons.Children.Add(close);
		if (allowAcceptance)
		{
			Button accept = new()
			{
				Margin = new Thickness(12, 0, 0, 0)
			};
			accept.SetResourceReference(ContentControl.ContentProperty, "Windows.Legal.Accept");
			accept.SetResourceReference(FrameworkElement.StyleProperty,
				"PrimaryButtonStyle");
			accept.Click += (_, _) => window.DialogResult = true;
			buttons.Children.Add(accept);
		}
		DockPanel.SetDock(buttons, Dock.Bottom);
		panel.Children.Add(buttons);
		TextBox text = new()
		{
			Text = GetDisplayText(catalog),
			IsReadOnly = true,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(10),
			FontSize = 13
		};
		panel.Children.Add(text);
		window.Content = panel;
		EventHandler languageChanged = (_, _) => window.Dispatcher.Invoke(() =>
		{
			double scrollOffset = text.VerticalOffset;
			int selectionStart = text.SelectionStart;
			int selectionLength = text.SelectionLength;
			text.Text = GetDisplayText(catalog);
			text.Select(Math.Min(selectionStart, text.Text.Length),
				Math.Min(selectionLength, Math.Max(0, text.Text.Length - selectionStart)));
			text.ScrollToVerticalOffset(scrollOffset);
		});
		UiText.LanguageChanged += languageChanged;
		try
		{
			return window.ShowDialog() == true;
		}
		finally
		{
			UiText.LanguageChanged -= languageChanged;
		}
	}

	internal static string GetDisplayText(LegalCatalog catalog)
	{
		return catalog.GetReadableText(new LegalDisplayLabels(
			UiText.Get("Windows.Legal.Title"),
			UiText.Get("Windows.Legal.TermsVersion"),
			UiText.Get("Windows.Legal.Scope"),
			UiText.Get("Windows.Legal.InstallerScope"),
			UiText.Get("Windows.Legal.CodeAndAcceptance"),
			UiText.Get("Windows.Legal.Source")));
	}
}
