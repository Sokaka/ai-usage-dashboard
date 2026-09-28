using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using AiUsageDashboard.App;
using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.Tests;

public sealed class WindowWorkAreaLayoutTests
{
	[Fact]
	public void CalculateMaxWidth_ConvertsPhysicalWorkAreaUsingMonitorDpi()
	{
		Assert.Equal(
			1264,
			WindowWorkAreaLayout.CalculateMaxWidth(
				preferredMinWidth: 760,
				physicalWorkAreaWidth: 1920,
				dpiScaleX: 1.5));
	}

	[Fact]
	public void CalculateMaxWidth_WhenAvailableAreaIsSmaller_AllowsNarrowLayout()
	{
		Assert.Equal(
			650,
			WindowWorkAreaLayout.CalculateMaxWidth(
				preferredMinWidth: 760,
				physicalWorkAreaWidth: 1000,
				dpiScaleX: 1.5));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(double.NaN)]
	public void CalculateMaxWidth_WithInvalidDpi_FailsSafeToPreferredMinimum(
		double dpiScaleX)
	{
		Assert.Equal(
			760,
			WindowWorkAreaLayout.CalculateMaxWidth(
				preferredMinWidth: 760,
				physicalWorkAreaWidth: 1920,
				dpiScaleX: dpiScaleX));
	}

	[Fact]
	public void CalculateMaxHeight_ConvertsPhysicalWorkAreaUsingMonitorDpi()
	{
		Assert.Equal(
			496,
			WindowWorkAreaLayout.CalculateMaxHeight(
				preferredMinHeight: 360,
				physicalWorkAreaHeight: 768,
				dpiScaleY: 1.5));
	}

	[Fact]
	public void CalculateMaxHeight_WhenAvailableAreaIsSmaller_AllowsShortLayout()
	{
		Assert.Equal(
			304,
			WindowWorkAreaLayout.CalculateMaxHeight(
				preferredMinHeight: 360,
				physicalWorkAreaHeight: 480,
				dpiScaleY: 1.5));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(double.NaN)]
	public void CalculateMaxHeight_WithInvalidDpi_FailsSafeToMinimum(
		double dpiScaleY)
	{
		Assert.Equal(
			360,
			WindowWorkAreaLayout.CalculateMaxHeight(
				preferredMinHeight: 360,
				physicalWorkAreaHeight: 768,
				dpiScaleY: dpiScaleY));
	}

	[Theory]
	[InlineData(640, 480, 2, 304, 224, AppLanguage.English)]
	[InlineData(1920, 1080, 3, 624, 344, AppLanguage.English)]
	[InlineData(832, 832, 2, 400, 400, AppLanguage.English)]
	[InlineData(640, 480, 2, 304, 224, AppLanguage.TraditionalChinese)]
	[InlineData(1920, 1080, 3, 624, 344, AppLanguage.TraditionalChinese)]
	[InlineData(832, 832, 2, 400, 400, AppLanguage.TraditionalChinese)]
	public async Task AutomaticSortRulesWindow_WithLimitedHighDpiWorkArea_KeepsRulesScrollableAndCloseVisible(
		int physicalWidth,
		int physicalHeight,
		double dpiScale,
		double expectedMaximumWidth,
		double expectedMaximumHeight,
		AppLanguage language)
	{
		await RunOnStaThreadAsync(() =>
		{
			using IDisposable languageScope = UiText.UseLanguage(language);
			AutomaticSortRulesWindow window = new(CreateWindowResources());
			try
			{
				window.ApplyWorkAreaConstraints(
					new System.Drawing.Rectangle(0, 0, physicalWidth, physicalHeight),
					new DpiScale(dpiScale, dpiScale));
				Assert.Equal(Math.Min(320, expectedMaximumWidth), window.MinWidth);
				Assert.Equal(expectedMaximumWidth, window.MaxWidth);
				Assert.Equal(expectedMaximumHeight, window.MaxHeight);

				Grid root = Assert.IsType<Grid>(window.Content);
				double contentWidth = Math.Min(window.Width, window.MaxWidth);
				root.Measure(new Size(contentWidth, window.MaxHeight));
				root.Arrange(new Rect(0, 0, contentWidth, window.MaxHeight));
				root.UpdateLayout();
				ScrollViewer rules = Assert.IsType<ScrollViewer>(root.Children[0]);
				Button close = Assert.IsType<Button>(root.Children[1]);
				Point closePosition = close.TranslatePoint(new Point(), root);
				StackPanel content = Assert.IsType<StackPanel>(rules.Content);
				foreach (TextBlock label in content.Children.OfType<TextBlock>())
				{
					Assert.False(string.IsNullOrWhiteSpace(label.Text));
					if (label.TextWrapping == TextWrapping.NoWrap)
					{
						FormattedText text = new(
							label.Text,
							UiText.Culture,
							label.FlowDirection,
							new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
							label.FontSize,
							label.Foreground,
							VisualTreeHelper.GetDpi(label).PixelsPerDip);
						Assert.True(
							text.WidthIncludingTrailingWhitespace <= label.ActualWidth + 1,
							$"Rule heading '{label.Text}' requires {text.WidthIncludingTrailingWhitespace:F2} DIP, but has {label.ActualWidth:F2} DIP.");
					}
				}

				Assert.True(rules.ScrollableHeight > 0);
				Assert.True(rules.ViewportHeight > 0);
				Assert.Equal(Visibility.Visible, rules.ComputedVerticalScrollBarVisibility);
				Assert.Equal(Visibility.Visible, close.Visibility);
				Assert.Equal(UiText.Get("Windows.SortRules.GotIt"), close.Content);
				Assert.True(close.ActualWidth > 0);
				Assert.True(close.ActualHeight > 0);
				Assert.True(closePosition.Y >= 0);
				Assert.True(closePosition.Y + close.ActualHeight <= root.ActualHeight);
			}
			finally
			{
				window.Close();
			}
		});
	}

	[Fact]
	public async Task AutomaticSortRulesWindow_WhenReturningToLargerMonitor_RestoresMinimumWidth()
	{
		await RunOnStaThreadAsync(() =>
		{
			AutomaticSortRulesWindow window = new(CreateWindowResources());
			try
			{
				window.ApplyWorkAreaConstraints(
					new System.Drawing.Rectangle(0, 0, 640, 480),
					new DpiScale(2, 2));
				Assert.Equal(304, window.MinWidth);
				Assert.Equal(304, window.MaxWidth);

				window.ApplyWorkAreaConstraints(
					new System.Drawing.Rectangle(0, 0, 1920, 1080),
					new DpiScale(1, 1));

				Assert.Equal(320, window.MinWidth);
				Assert.Equal(1904, window.MaxWidth);
				Assert.Equal(1064, window.MaxHeight);
			}
			finally
			{
				window.Close();
			}
		});
	}

	[Fact]
	public void AccountEditorWindow_KeepsFooterOutsideScrollableForm()
	{
		System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Load(
			Path.Combine(
				RepositoryTestPaths.Root,
				"src",
				"AiUsageDashboard.App",
				"AccountEditorWindow.xaml"));
		System.Xml.Linq.XNamespace presentation =
			"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		System.Xml.Linq.XNamespace x =
			"http://schemas.microsoft.com/winfx/2006/xaml";
		System.Xml.Linq.XElement rootGrid = Assert.Single(
			document.Root!.Elements(),
			element => element.Name == presentation + "Grid");
		System.Xml.Linq.XElement formScrollViewer = Assert.Single(
			rootGrid.Elements(),
			element =>
				(string?)element.Attribute(x + "Name") == "FormScrollViewer");
		System.Xml.Linq.XElement footerActionsPanel = Assert.Single(
			rootGrid.Elements(),
			element =>
				(string?)element.Attribute(x + "Name") == "FooterActionsPanel");

		Assert.Equal(presentation + "ScrollViewer", formScrollViewer.Name);
		Assert.Equal("0", (string?)formScrollViewer.Attribute("Grid.Row"));
		Assert.Equal("1", (string?)footerActionsPanel.Attribute("Grid.Row"));
		Assert.DoesNotContain(
			formScrollViewer.Descendants(),
			element =>
				(string?)element.Attribute(x + "Name") == "FooterActionsPanel");
	}

	[Theory]
	[InlineData(0x001A, true)]
	[InlineData(0x007E, true)]
	[InlineData(0x02E0, true)]
	[InlineData(0x0003, false)]
	[InlineData(0x0005, false)]
	[InlineData(0x000F, false)]
	public void RequiresRefreshForWindowMessage_MatchesDisplayAndDpiChanges(
		int message,
		bool expected)
	{
		Assert.Equal(
			expected,
			WindowWorkAreaLayout.RequiresRefreshForWindowMessage(message));
	}

	[Fact]
	public void ShouldAnnounceInlineStatus_WhenRecentVmStatusMatches_SuppressesDuplicate()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;

		Assert.False(
			LiveRegionAnnouncementPolicy.ShouldAnnounceInlineStatus(
				"更新完成",
				"更新完成",
				now - TimeSpan.FromSeconds(1),
				now));
	}

	[Fact]
	public void ShouldAnnounceInlineStatus_WhenMessageDiffersOrIsOld_AllowsAnnouncement()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;

		Assert.True(
			LiveRegionAnnouncementPolicy.ShouldAnnounceInlineStatus(
				"連接完成",
				"更新完成",
				now,
				now));
		Assert.True(
			LiveRegionAnnouncementPolicy.ShouldAnnounceInlineStatus(
				"更新完成",
				"更新完成",
				now - TimeSpan.FromSeconds(3),
				now));
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void ShouldReplaceInlineStatus_PreservesPersistentGuidance(
		bool hasPersistentInlineStatus,
		bool expected)
	{
		Assert.Equal(
			expected,
			LiveRegionAnnouncementPolicy.ShouldReplaceInlineStatus(
				hasPersistentInlineStatus));
	}

	private static ResourceDictionary CreateWindowResources()
	{
		ResourceDictionary resources = new();
		resources.MergedDictionaries.Add(
			(ResourceDictionary)Application.LoadComponent(new Uri(
				"/AiUsageDashboard.App;component/Themes/Palette.xaml",
				UriKind.Relative)));
		resources.MergedDictionaries.Add(
			(ResourceDictionary)Application.LoadComponent(new Uri(
				"/AiUsageDashboard.App;component/Themes/Controls.xaml",
				UriKind.Relative)));
		foreach ((string key, string text) in UiText.GetResources())
		{
			resources[key] = text;
		}
		return resources;
	}

	private static Task RunOnStaThreadAsync(Action operation)
	{
		TaskCompletionSource<bool> completion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			try
			{
				operation();
				completion.TrySetResult(true);
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		})
		{
			IsBackground = true
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}
}
