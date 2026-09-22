using System.Text.RegularExpressions;
using System.Xml.Linq;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class XamlUsabilityRegressionTests
{
	private static readonly XNamespace Presentation =
		"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Xaml =
		"http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void HighContrastUsageProgress_UsesDistinctTrackAndIndicatorColors()
	{
		XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
		XElement usageStyle = GetKeyedElement(
			controls,
			"Style",
			"UsageMetricProgressBarStyle");
		XElement highContrastTrigger = Assert.Single(
			usageStyle.Descendants(Presentation + "DataTrigger"),
			element =>
				((string?)element.Attribute("Binding"))?.Contains(
					"Binding Tag",
					StringComparison.Ordinal) == true);

		AssertSetter(
			highContrastTrigger,
			"Background",
			"{DynamicResource ControlBackgroundBrush}");
		AssertSetter(
			highContrastTrigger,
			"Foreground",
			"{DynamicResource AccentBrush}");

		XDocument palette = LoadAppXaml(
			"Themes",
			"HighContrastPalette.xaml");
		string trackColor = GetColorMarkup(
			palette,
			"ControlBackgroundColor");
		string indicatorColor = GetColorMarkup(palette, "AccentColor");

		Assert.Contains("ControlColor", trackColor, StringComparison.Ordinal);
		Assert.Contains("HighlightColor", indicatorColor, StringComparison.Ordinal);
		Assert.NotEqual(trackColor, indicatorColor);
	}

	[Fact]
	public void FloatingAccountManagement_UsesExistingMenusWithoutSeparateMode()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement root = Assert.IsType<XElement>(window.Root);
		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");

		Assert.DoesNotContain(
			root.DescendantsAndSelf(),
			element => element.Attributes().Any(
				attribute =>
					attribute.Value.Contains(
						"IsManagementMode",
						StringComparison.Ordinal) ||
					attribute.Value.Contains(
						"ManagementModeButton",
						StringComparison.Ordinal) ||
					attribute.Value.Contains(
						"ManagementActionsStyle",
						StringComparison.Ordinal)));
		Assert.DoesNotContain(
			"IsManagementMode",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ManagementModeButton_Click",
			source,
			StringComparison.Ordinal);

		XElement globalMenu = Assert.Single(
			window.Descendants(Presentation + "ContextMenu"),
			element => element
				.Elements(Presentation + "MenuItem")
				.Any(item => string.Equals(
					(string?)item.Attribute("Click"),
					"AddAccountMenuItem_Click",
					StringComparison.Ordinal)));
		Assert.Contains(
			globalMenu.Elements(Presentation + "MenuItem"),
			item => string.Equals(
				(string?)item.Attribute("Click"),
				"ToggleSortModeMenuItem_Click",
				StringComparison.Ordinal));

		XElement accountMenu = Assert.Single(
			window.Descendants(Presentation + "ContextMenu"),
			element => element
				.Elements(Presentation + "MenuItem")
				.Any(item => string.Equals(
					(string?)item.Attribute("Click"),
					"DeleteAccountMenuItem_Click",
					StringComparison.Ordinal)));
		string[] requiredAccountActions =
		[
			"RefreshAccountMenuItem_Click",
			"EditAccountMenuItem_Click",
			"ToggleAccountEnabledMenuItem_Click",
			"MoveAccountUpButton_Click",
			"MoveAccountDownButton_Click",
			"DeleteAccountMenuItem_Click"
		];

		foreach (string clickHandler in requiredAccountActions)
		{
			Assert.Contains(
				accountMenu.Elements(Presentation + "MenuItem"),
				item => string.Equals(
					(string?)item.Attribute("Click"),
					clickHandler,
					StringComparison.Ordinal));
		}

		string[] connectionActionHandlers =
		[
			"ConnectClaudeAccountButton_Click",
			"ConnectCodexAccountButton_Click",
			"ConnectGrokAccountButton_Click",
			"ConnectAntigravityAccountButton_Click"
		];

		foreach (string clickHandler in connectionActionHandlers)
		{
			Assert.DoesNotContain(
				accountMenu.Elements(Presentation + "MenuItem"),
				item => string.Equals(
					(string?)item.Attribute("Click"),
					clickHandler,
					StringComparison.Ordinal));
		}
	}

	[Fact]
	public void FloatingHeader_UsesWholeHeaderAsDragSurfaceWithoutGripGlyph()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement header = GetNamedElement(window, "Grid", "ExpandedHeader");

		Assert.Equal("SizeAll", (string?)header.Attribute("Cursor"));
		Assert.Equal(
			"ExpandedHeader_MouseLeftButtonDown",
			(string?)header.Attribute("MouseLeftButtonDown"));
		Assert.Contains(
			"拖曳此區域可移動浮窗",
			(string?)header.Attribute("AutomationProperties.HelpText"),
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			header.Descendants(Presentation + "TextBlock"),
			element =>
				string.Equals(
					(string?)element.Attribute("Text"),
					"⠿",
					StringComparison.Ordinal) ||
				string.Equals(
					(string?)element.Attribute("AutomationProperties.Name"),
					"拖曳浮窗",
					StringComparison.Ordinal));
	}

	[Fact]
	public void FloatingAccountList_SupportsMouseDragScrolling()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement accountScrollViewer = GetNamedElement(
			window,
			"ScrollViewer",
			"AccountScrollViewer");

		Assert.Equal(
			"VerticalOnly",
			(string?)accountScrollViewer.Attribute("PanningMode"));
		Assert.Equal(
			"AccountScrollViewer_PreviewMouseLeftButtonDown",
			(string?)accountScrollViewer.Attribute(
				"PreviewMouseLeftButtonDown"));
		Assert.Equal(
			"AccountScrollViewer_PreviewMouseMove",
			(string?)accountScrollViewer.Attribute("PreviewMouseMove"));
		Assert.Equal(
			"AccountScrollViewer_PreviewMouseLeftButtonUp",
			(string?)accountScrollViewer.Attribute(
				"PreviewMouseLeftButtonUp"));
		Assert.Equal(
			"AccountScrollViewer_LostMouseCapture",
			(string?)accountScrollViewer.Attribute("LostMouseCapture"));
		Assert.Contains(
			"按住帳號卡片上下拖曳來捲動畫面",
			(string?)accountScrollViewer.Attribute(
				"AutomationProperties.HelpText"),
			StringComparison.Ordinal);
	}

	[Fact]
	public void FloatingUpdateBanner_UsesDedicatedRowAndKeyboardActionOrder()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement expandedView = GetNamedElement(
			window,
			"Border",
			"ExpandedView");
		XElement contentGrid = Assert.Single(
			expandedView.Elements(Presentation + "Grid"));
		XElement rowDefinitions = Assert.IsType<XElement>(
			contentGrid.Element(Presentation + "Grid.RowDefinitions"));
		XElement[] rows = rowDefinitions
			.Elements(Presentation + "RowDefinition")
			.ToArray();

		Assert.Collection(
			rows,
			row => Assert.Equal("Auto", (string?)row.Attribute("Height")),
			row => Assert.Equal("Auto", (string?)row.Attribute("Height")),
			row => Assert.Equal("*", (string?)row.Attribute("Height")),
			row =>
			{
				Assert.Equal("ExpandedFooterRow", (string?)row.Attribute(Xaml + "Name"));
				Assert.Equal("Auto", (string?)row.Attribute("Height"));
			});

		XElement header = GetNamedElement(window, "Grid", "ExpandedHeader");
		XElement updateBanner = GetNamedElement(window, "Border", "UpdateBanner");
		XElement accountScrollViewer = GetNamedElement(
			window,
			"ScrollViewer",
			"AccountScrollViewer");
		XElement footer = GetNamedElement(window, "Grid", "ExpandedFooter");

		Assert.Same(contentGrid, header.Parent);
		Assert.Null(header.Attribute("Grid.Row"));
		Assert.Same(contentGrid, updateBanner.Parent);
		Assert.Equal("1", (string?)updateBanner.Attribute("Grid.Row"));
		Assert.Same(contentGrid, accountScrollViewer.Parent);
		Assert.Equal("2", (string?)accountScrollViewer.Attribute("Grid.Row"));
		Assert.Same(contentGrid, footer.Parent);
		Assert.Equal("3", (string?)footer.Attribute("Grid.Row"));
		Assert.DoesNotContain(updateBanner, accountScrollViewer.Descendants());

		XElement bannerText = GetNamedElement(
			window,
			"TextBlock",
			"UpdateBannerTextBlock");
		Assert.Contains(updateBanner, bannerText.Ancestors());
		Assert.Equal(
			"Polite",
			(string?)bannerText.Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("Wrap", (string?)bannerText.Attribute("TextWrapping"));

		XElement actionPanel = Assert.Single(
			updateBanner.Descendants(Presentation + "WrapPanel"));
		Assert.Equal(
			"Local",
			(string?)actionPanel.Attribute("KeyboardNavigation.TabNavigation"));
		string[] actionOrder = actionPanel
			.Elements(Presentation + "Button")
			.Select(button => (string?)button.Attribute(Xaml + "Name"))
			.OfType<string>()
			.ToArray();
		Assert.Equal(
			[
				"UpdatePrimaryActionButton",
				"UpdateReleaseHistoryButton",
				"UpdateSnoozeButton",
				"DisableAutomaticUpdateChecksButton"
			],
			actionOrder);
	}

	[Fact]
	public void CollapsedUpdateBadge_UsesDedicatedPaletteVectorAndExpandOnlyAction()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement collapsedButton = GetNamedElement(
			window,
			"Button",
			"CollapsedButton");
		XElement badge = GetNamedElement(
			window,
			"Border",
			"CollapsedUpdateBadge");
		XElement glyph = Assert.Single(
			badge.Elements(Presentation + "Path"));

		Assert.Contains(collapsedButton, badge.Ancestors());
		Assert.Equal(
			"CollapsedButton_Click",
			(string?)collapsedButton.Attribute("Click"));
		Assert.Equal("False", (string?)badge.Attribute("IsHitTestVisible"));
		Assert.Equal("20", (string?)badge.Attribute("Width"));
		Assert.Equal("20", (string?)badge.Attribute("Height"));
		Assert.Equal("0,-2,-2,0", (string?)badge.Attribute("Margin"));
		Assert.Equal("10", (string?)badge.Attribute("CornerRadius"));
		Assert.Equal("2", (string?)badge.Attribute("BorderThickness"));
		Assert.Equal(
			"{DynamicResource UpdateBadgeBrush}",
			(string?)badge.Attribute("Background"));
		Assert.Equal(
			"{DynamicResource UpdateBadgeForegroundBrush}",
			(string?)badge.Attribute("BorderBrush"));
		Assert.Empty(badge.Elements(Presentation + "TextBlock"));
		Assert.Equal("10", (string?)glyph.Attribute("Width"));
		Assert.Equal("11", (string?)glyph.Attribute("Height"));
		Assert.Equal(
			"M 1,5 L 5,1 L 9,5 M 5,1 L 5,10",
			(string?)glyph.Attribute("Data"));
		Assert.Equal(
			"{DynamicResource UpdateBadgeForegroundBrush}",
			(string?)glyph.Attribute("Stroke"));
		Assert.Equal("2", (string?)glyph.Attribute("StrokeThickness"));
		Assert.Equal("Round", (string?)glyph.Attribute("StrokeStartLineCap"));
		Assert.Equal("Round", (string?)glyph.Attribute("StrokeEndLineCap"));
		Assert.Equal("Round", (string?)glyph.Attribute("StrokeLineJoin"));

		XDocument highContrastPalette = LoadAppXaml(
			"Themes",
			"HighContrastPalette.xaml");
		string backgroundColor = GetColorMarkup(
			highContrastPalette,
			"UpdateBadgeForegroundColor");
		string indicatorColor = GetColorMarkup(
			highContrastPalette,
			"UpdateBadgeColor");
		Assert.Contains("WindowColor", backgroundColor, StringComparison.Ordinal);
		Assert.Contains("WindowTextColor", indicatorColor, StringComparison.Ordinal);
		Assert.NotEqual(backgroundColor, indicatorColor);

		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string clickHandler = GetMethodSource(
			source,
			"private void CollapsedButton_Click(",
			"private void CollapsedButton_PreviewMouseLeftButtonDown(");
		Assert.Matches(
			new Regex(
				@"SetCollapsed\(\s*false,",
				RegexOptions.CultureInvariant),
			clickHandler);
		Assert.Contains(
			"PointerFocusRelease.WasInvokedByPointer(sender)",
			clickHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"FocusTransitionMode.Release",
			clickHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"FocusTransitionMode.Transfer",
			clickHandler,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"UpdatePrimaryActionRequested",
			clickHandler,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AboutLinkLauncher",
			clickHandler,
			StringComparison.Ordinal);

		string pointerUpHandler = GetMethodSource(
			source,
			"private void CollapsedButton_PreviewMouseLeftButtonUp(",
			"private void CollapsedButton_PreviewMouseMove(");
		Assert.Contains(
			"else if (shouldExpand)",
			pointerUpHandler,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				@"else if \(shouldExpand\)[\s\S]*?SetCollapsed\(\s*false,",
				RegexOptions.CultureInvariant),
			pointerUpHandler);
		Assert.Contains(
			"focusTransitionMode: FocusTransitionMode.Release",
			pointerUpHandler,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"UpdatePrimaryActionRequested",
			pointerUpHandler,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AboutLinkLauncher",
			pointerUpHandler,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AboutWindow_ExposesPoliteUpdateStatusAndManualCheckAction()
	{
		XDocument window = LoadAppXaml("AboutWindow.xaml");
		XElement status = GetNamedElement(
			window,
			"TextBlock",
			"UpdateStatusTextBlock");
		XElement checkButton = GetNamedElement(
			window,
			"Button",
			"CheckForUpdatesButton");
		XElement releasesButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			button => string.Equals(
				(string?)button.Attribute("Click"),
				"ReleasesButton_Click",
				StringComparison.Ordinal));

		Assert.Equal(
			"AboutUpdateStatus",
			(string?)status.Attribute("AutomationProperties.AutomationId"));
		Assert.Equal(
			"Polite",
			(string?)status.Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("Wrap", (string?)status.Attribute("TextWrapping"));
		Assert.Equal("尚未檢查更新。", (string?)status.Attribute("Text"));
		Assert.Equal(
			"CheckForUpdates",
			(string?)checkButton.Attribute("AutomationProperties.AutomationId"));
		Assert.Equal(
			"CheckForUpdatesButton_Click",
			(string?)checkButton.Attribute("Click"));
		Assert.Equal("檢查更新", (string?)checkButton.Attribute("Content"));
		Assert.Equal(
			"{StaticResource SecondaryButtonStyle}",
			(string?)checkButton.Attribute("Style"));
		Assert.Equal(
			"下載與版本紀錄",
			(string?)releasesButton.Attribute("Content"));

		string source = LoadAppSource("AboutWindow.xaml.cs");
		string statusUpdater = GetMethodSource(
			source,
			"internal void UpdateUpdateStatus(",
			"private void UpdateWorkAreaConstraints()");
		Assert.Contains(
			"UpdateStatusTextBlock.Text = statusText;",
			statusUpdater,
			StringComparison.Ordinal);
		Assert.Contains(
			"CheckForUpdatesButton.IsEnabled = canCheck && !isChecking;",
			statusUpdater,
			StringComparison.Ordinal);
		Assert.Contains("檢查中…", statusUpdater, StringComparison.Ordinal);

		string clickHandler = GetMethodSource(
			source,
			"private void CheckForUpdatesButton_Click(",
			"private void ReleasesButton_Click(");
		Assert.Contains(
			"UpdateCheckRequested?.Invoke(this, EventArgs.Empty);",
			clickHandler,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ButtonFocusVisual_UsesKeyboardOnlyFocusAdorner()
	{
		const string FocusVisual =
			"{StaticResource ButtonKeyboardFocusVisualStyle}";
		const string CheckBoxFocusVisual =
			"{StaticResource CheckBoxKeyboardFocusVisualStyle}";
		const string CircularFocusVisual =
			"{StaticResource CircularButtonKeyboardFocusVisualStyle}";
		XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
		XElement focusStyle = GetKeyedElement(
			controls,
			"Style",
			"ButtonKeyboardFocusVisualStyle");
		XElement defaultButtonStyle = GetKeyedElement(
			controls,
			"Style",
			"DefaultButtonStyle");
		XElement checkBoxFocusStyle = GetKeyedElement(
			controls,
			"Style",
			"CheckBoxKeyboardFocusVisualStyle");
		XElement checkBoxStyle = Assert.Single(
			controls.Descendants(Presentation + "Style"),
			element =>
				(element.Attribute(Xaml + "Key") is null) &&
				string.Equals(
					(string?)element.Attribute("TargetType"),
					"{x:Type CheckBox}",
					StringComparison.Ordinal));

		Assert.NotEmpty(focusStyle.Descendants(Presentation + "Border"));
		AssertSetter(defaultButtonStyle, "FocusVisualStyle", FocusVisual);
		AssertDoesNotUsePersistentFocusTrigger(defaultButtonStyle);
		Assert.NotEmpty(
			checkBoxFocusStyle.Descendants(Presentation + "Border"));
		AssertSetter(
			checkBoxStyle,
			"FocusVisualStyle",
			CheckBoxFocusVisual);
		AssertDoesNotUsePersistentFocusTrigger(checkBoxStyle);

		XDocument floatingWindow = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement pinButtonStyle = GetKeyedElement(
			floatingWindow,
			"Style",
			"PinButtonStyle");
		XElement circularFocusStyle = GetKeyedElement(
			floatingWindow,
			"Style",
			"CircularButtonKeyboardFocusVisualStyle");
		XElement collapsedButton = GetNamedElement(
			floatingWindow,
			"Button",
			"CollapsedButton");

		AssertSetter(pinButtonStyle, "FocusVisualStyle", FocusVisual);
		AssertDoesNotUsePersistentFocusTrigger(pinButtonStyle);
		Assert.NotEmpty(
			circularFocusStyle.Descendants(Presentation + "Ellipse"));
		Assert.Equal(
			CircularFocusVisual,
			(string?)collapsedButton.Attribute("FocusVisualStyle"));
		AssertDoesNotUsePersistentFocusTrigger(collapsedButton);

		const string SetupFocusVisual =
			"{StaticResource SetupKeyboardFocusVisualStyle}";
		XDocument setupControls = LoadSetupXaml(
			"Themes",
			"SetupControls.xaml");
		XElement setupFocusStyle = GetKeyedElement(
			setupControls,
			"Style",
			"SetupKeyboardFocusVisualStyle");
		XElement setupPrimaryButtonStyle = GetKeyedElement(
			setupControls,
			"Style",
			"SetupPrimaryButtonStyle");
		XElement setupGhostButtonStyle = GetKeyedElement(
			setupControls,
			"Style",
			"SetupGhostButtonStyle");

		Assert.NotEmpty(
			setupFocusStyle.Descendants(Presentation + "Border"));
		Assert.Contains(
			setupFocusStyle.Descendants(Presentation + "Border"),
			border => string.Equals(
				(string?)border.Attribute("BorderBrush"),
				"{DynamicResource FocusBrush}",
				StringComparison.Ordinal));
		Assert.Contains(
			setupFocusStyle.Descendants(Presentation + "Border"),
			border => string.Equals(
				(string?)border.Attribute("BorderBrush"),
				"{DynamicResource AccentFocusBrush}",
				StringComparison.Ordinal));
		AssertSetter(
			setupPrimaryButtonStyle,
			"FocusVisualStyle",
			SetupFocusVisual);
		AssertSetter(
			setupGhostButtonStyle,
			"FocusVisualStyle",
			SetupFocusVisual);
		AssertDoesNotUsePersistentFocusTrigger(setupPrimaryButtonStyle);
		AssertDoesNotUsePersistentFocusTrigger(setupGhostButtonStyle);
	}

	[Fact]
	public void SetupProgressFocus_UsesVisibleCancelActionAndKeepsLiveRegion()
	{
		const string SetupFocusVisual =
			"{StaticResource SetupKeyboardFocusVisualStyle}";
		XDocument window = LoadSetupXaml("SetupWindow.xaml");
		string source = LoadSetupSource("SetupWindow.xaml.cs");
		XElement progressHeading = GetNamedElement(
			window,
			"TextBlock",
			"ProgressHeadingTextBlock");
		XElement progressStatus = GetNamedElement(
			window,
			"TextBlock",
			"ProgressTextBlock");
		XElement progressPanel = GetNamedElement(
			window,
			"Border",
			"ProgressPanel");
		XElement progressIndicator = GetNamedElement(
			window,
			"ProgressBar",
			"ProgressIndicator");
		XElement cancelButton = GetNamedElement(
			window,
			"Button",
			"CancelButton");

		Assert.Null(progressHeading.Attribute("Focusable"));
		Assert.Equal(
			"Polite",
			(string?)progressStatus.Attribute(
				"AutomationProperties.LiveSetting"));
		Assert.Equal(
			"{StaticResource SetupGhostButtonStyle}",
			(string?)cancelButton.Attribute("Style"));
		Assert.Equal(
			"{StaticResource SetupPanelStyle}",
			(string?)progressPanel.Attribute("Style"));
		Assert.Equal("True", (string?)progressIndicator.Attribute("Focusable"));
		Assert.Equal(
			"False",
			(string?)progressIndicator.Attribute("IsTabStop"));
		Assert.Equal(
			"{Binding Text, ElementName=ProgressTextBlock}",
			(string?)progressIndicator.Attribute(
				"AutomationProperties.Name"));
		Assert.Equal(
			SetupFocusVisual,
			(string?)progressIndicator.Attribute("FocusVisualStyle"));
		Assert.Matches(
			new Regex(
				@"ReferenceEquals\(visiblePanel,\s*ProgressPanel\)\s*\?\s*" +
					@"CancelButton\.IsEnabled\s*\?\s*CancelButton\s*:\s*ProgressIndicator",
				RegexOptions.CultureInvariant),
			source);
		Assert.Matches(
			new Regex(
				@"ProgressTextBlock\.Text\s*=\s*cancellationStatus;\s*" +
					@"FooterStatusTextBlock\.Text\s*=\s*cancellationStatus;\s*" +
					@"CancelButton\.IsEnabled\s*=\s*false;\s*" +
					@"FocusProgressIndicator\(\);",
				RegexOptions.CultureInvariant),
			source);
		Assert.Contains(
			"ProgressIndicator.Focus();",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"Keyboard.Focus(ProgressIndicator);",
			source,
			StringComparison.Ordinal);
		int showOnlyStart = source.IndexOf(
			"private void ShowOnly(",
			StringComparison.Ordinal);
		Assert.True(showOnlyStart >= 0);
		int queueAnnouncementStart = source.IndexOf(
			"private void QueueLiveRegionAnnouncement(",
			showOnlyStart,
			StringComparison.Ordinal);
		Assert.True(queueAnnouncementStart > showOnlyStart);
		string showOnlySource = source[showOnlyStart..queueAnnouncementStart];
		Assert.True(
			showOnlySource.IndexOf(
				"FrameworkElement? focusTarget",
				StringComparison.Ordinal) >
			showOnlySource.IndexOf(
				"Dispatcher.BeginInvoke",
				StringComparison.Ordinal),
			"The progress focus target must be resolved inside the queued callback.");
		Assert.Contains("focusTarget is not null", source, StringComparison.Ordinal);
		Assert.Contains("focusTarget.IsVisible", source, StringComparison.Ordinal);
		Assert.Contains("focusTarget.IsEnabled", source, StringComparison.Ordinal);
		Assert.Contains(
			"if (!_shouldTransferOperationFocus)",
			source,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				@"if \(!_shouldTransferOperationFocus\)\s*\{\s*" +
					@"return ReferenceEquals\(visiblePanel, ProgressPanel\)\s*" +
					@"\? ProgressIndicator\s*:\s*null;",
				RegexOptions.CultureInvariant),
			source);
	}

	[Fact]
	public void ScrollBar_UsesWideHitAreaAndThinVisualThumb()
	{
		XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
		XElement scrollBarStyle = Assert.Single(
			controls.Descendants(Presentation + "Style"),
			element =>
				(element.Attribute(Xaml + "Key") is null) &&
				string.Equals(
					(string?)element.Attribute("TargetType"),
					"{x:Type ScrollBar}",
					StringComparison.Ordinal));

		AssertSetter(scrollBarStyle, "Width", "18");
		XElement horizontalTrigger = Assert.Single(
			scrollBarStyle.Descendants(Presentation + "Trigger"),
			element =>
				string.Equals(
					(string?)element.Attribute("Property"),
					"Orientation",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)element.Attribute("Value"),
					"Horizontal",
					StringComparison.Ordinal));
		AssertSetter(horizontalTrigger, "Height", "18");

		XElement thumbStyle = GetKeyedElement(
			controls,
			"Style",
			"ScrollBarThumbStyle");
		AssertSetter(thumbStyle, "Background", "{DynamicResource ScrollBarThumbBrush}");
		XElement chrome = Assert.Single(thumbStyle.Descendants(Presentation + "Border"));
		Assert.Equal("{TemplateBinding Background}", (string?)chrome.Attribute("Background"));
		Assert.Null(chrome.Attribute("BorderBrush"));
		Assert.Null(chrome.Attribute("BorderThickness"));
		XElement[] orientationTriggers = thumbStyle
			.Descendants(Presentation + "DataTrigger")
			.Where(element =>
				((string?)element.Attribute("Binding"))?.Contains(
					"Orientation",
					StringComparison.Ordinal) == true)
			.ToArray();

		Assert.Contains(
			orientationTriggers,
			trigger => HasSetter(trigger, "Width", "6"));
		Assert.Contains(
			orientationTriggers,
			trigger => HasSetter(trigger, "Height", "6"));
	}

	[Theory]
	[InlineData("FloatingWidgetWindow.xaml")]
	public void AccountRemovalMenu_DescribesAccountRemoval(string fileName)
	{
		string xaml = File.ReadAllText(GetAppXamlPath(fileName));

		Assert.Contains(
			"Header=\"從 AI Usage 移除帳號\"",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Header=\"移除帳號卡\"", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("Header=\"刪除帳號\"", xaml, StringComparison.Ordinal);
	}

	[Fact]
	public void FloatingAccountMenu_UsesConciseActionLabels()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement refresh = GetMenuItemByClick(
			window,
			"RefreshAccountMenuItem_Click");
		XElement edit = GetMenuItemByClick(
			window,
			"EditAccountMenuItem_Click");
		XElement moveUp = GetMenuItemByClick(
			window,
			"MoveAccountUpButton_Click");
		XElement moveDown = GetMenuItemByClick(
			window,
			"MoveAccountDownButton_Click");
		XElement toggleEnabled = GetMenuItemByClick(
			window,
			"ToggleAccountEnabledMenuItem_Click");
		XElement toggleSubscriptionContext = GetMenuItemByClick(
			window,
			"ToggleSubscriptionContextMenuItem_Click");
		XElement accountMenu = Assert.IsType<XElement>(toggleEnabled.Parent);
		XElement[] menuItems = accountMenu
			.Elements(Presentation + "MenuItem")
			.ToArray();

		Assert.Equal("檢查此帳號用量", (string?)refresh.Attribute("Header"));
		Assert.Equal("帳號設定", (string?)edit.Attribute("Header"));
		Assert.Equal(
			"{Binding SubscriptionContextMenuText}",
			(string?)toggleSubscriptionContext.Attribute("Header"));
		Assert.Equal(
			"True",
			(string?)toggleSubscriptionContext.Attribute("IsCheckable"));
		Assert.Equal(
			"{Binding ShowSubscriptionContext, Mode=OneWay}",
			(string?)toggleSubscriptionContext.Attribute("IsChecked"));
		Assert.Equal(
			"{Binding SupportsSubscriptionContext, Converter={StaticResource BooleanToVisibilityConverter}}",
			(string?)toggleSubscriptionContext.Attribute("Visibility"));
		Assert.Equal("向上移動", (string?)moveUp.Attribute("Header"));
		Assert.Equal("向下移動", (string?)moveDown.Attribute("Header"));
		Assert.Contains(
			"向上移動",
			(string?)moveUp.Attribute("AutomationProperties.Name"),
			StringComparison.Ordinal);
		Assert.Contains(
			"向下移動",
			(string?)moveDown.Attribute("AutomationProperties.Name"),
			StringComparison.Ordinal);
		Assert.Contains(
			toggleEnabled.Descendants(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					"Header",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"開始檢查用量",
					StringComparison.Ordinal));
		Assert.Contains(
			toggleEnabled.Descendants(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					"Header",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"停止檢查用量",
					StringComparison.Ordinal));
		Assert.True(
			Array.IndexOf(menuItems, toggleEnabled) <
			Array.IndexOf(menuItems, moveUp));
		Assert.True(
			Array.IndexOf(menuItems, toggleEnabled) <
			Array.IndexOf(menuItems, moveDown));
		Assert.Equal(
			Presentation + "Separator",
			toggleEnabled.ElementsAfterSelf().First().Name);
	}

	[Fact]
	public void FloatingGlobalMenu_OffersThemeAndDockPosition()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement globalMenu = Assert.Single(
			window.Descendants(Presentation + "ContextMenu"),
			element => element
				.Elements(Presentation + "MenuItem")
				.Any(item => string.Equals(
					(string?)item.Attribute("Click"),
					"AddAccountMenuItem_Click",
					StringComparison.Ordinal)));
		XElement[] menuItems = globalMenu
			.Elements(Presentation + "MenuItem")
			.ToArray();
		XElement dockPosition = Assert.Single(
			menuItems,
			item => string.Equals(
				(string?)item.Attribute("Header"),
				"停靠位置",
				StringComparison.Ordinal));
		XElement theme = Assert.Single(
			menuItems,
			item => string.Equals(
				(string?)item.Attribute("Header"),
				"主題配色",
				StringComparison.Ordinal));
		XElement heightFollowingCardCount = Assert.Single(
			menuItems,
			item => string.Equals(
				(string?)item.Attribute("Click"),
				"HeightFollowsCardCountMenuItem_Click",
				StringComparison.Ordinal));
		Assert.Equal(3, Array.IndexOf(menuItems, theme));
		Assert.Equal(4, Array.IndexOf(menuItems, dockPosition));
		Assert.Equal(5, Array.IndexOf(menuItems, heightFollowingCardCount));
		Assert.Equal(
			"ToggleUsageDisplayModeMenuItem_Click",
			(string?)menuItems[2].Attribute("Click"));
		XElement[] themeChoices = theme
			.Elements(Presentation + "MenuItem")
			.ToArray();
		Assert.Equal(
			new[] { "ClassicBlue", "Midnight", "Light", "Sakura" },
			themeChoices
				.Select(item => (string?)item.Attribute("Tag"))
				.ToArray());
		Assert.All(themeChoices, item =>
		{
			Assert.Equal("True", (string?)item.Attribute("IsCheckable"));
			Assert.Equal(
				"ThemeMenuItem_Click",
				(string?)item.Attribute("Click"));
		});
		Assert.Equal(
			"True",
			(string?)heightFollowingCardCount.Attribute("IsCheckable"));
		Assert.Equal(
			"高度隨卡片數量調整",
			(string?)heightFollowingCardCount.Attribute("Header"));
		Assert.Equal(
			Presentation + "Separator",
			heightFollowingCardCount.ElementsAfterSelf().First().Name);
		XElement exportSettings = GetMenuItemByClick(
			window,
			"ExportSettingsMenuItem_Click");
		XElement importSettings = GetMenuItemByClick(
			window,
			"ImportSettingsMenuItem_Click");
		XElement undoSettingsImport = GetMenuItemByClick(
			window,
			"UndoSettingsImportMenuItem_Click");

		Assert.Equal(
			"ExportPortableSettings",
			(string?)exportSettings.Attribute(
				"AutomationProperties.AutomationId"));
		Assert.Equal(
			"ImportPortableSettings",
			(string?)importSettings.Attribute(
				"AutomationProperties.AutomationId"));
		Assert.Equal(
			"UndoPortableSettingsImport",
			(string?)undoSettingsImport.Attribute(
				"AutomationProperties.AutomationId"));
		Assert.Equal(
			"還原匯入前設定",
			(string?)undoSettingsImport.Attribute("Header"));
		Assert.Equal(
			"只還原這次匯入取代的帳號、順序與設定。關閉程式後便無法還原。",
			(string?)undoSettingsImport.Attribute("ToolTip"));
		Assert.Equal(
			"匯入或匯出設定",
			(string?)Assert.IsType<XElement>(exportSettings.Parent)
				.Attribute("Header"));
		Assert.Equal(
			"{Binding CanUndoLastPortableSettingsImport}",
			(string?)undoSettingsImport.Attribute("IsEnabled"));
		Assert.Same(exportSettings.Parent, importSettings.Parent);
		Assert.Same(importSettings.Parent, undoSettingsImport.Parent);
	}

	[Fact]
	public void FloatingWidget_HeightFollowingCards_UsesContentHeightWithWorkAreaCap()
	{
		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");

		Assert.Contains(
			"SizeToContent = SizeToContent.Height;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"MaxHeight = maximumHeight;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"Height = double.NaN;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"UpdateExpandedSize(targetWidth, targetHeight);",
			source,
			StringComparison.Ordinal);
	}

	[Fact]
	public void PortableSettingsImport_DisclosesReplacementAndPreservesDetailedStatus()
	{
		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");

		Assert.Contains(
			"套用後可在關閉程式前選擇「還原匯入前設定」",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"AI Usage 為現有 Grok 帳號另存的登入資料會清除",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"還原後部分用量可能需要重新檢查",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"用量顯示方式、浮窗設定與主題",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"舊版檔案沒有主題設定，因此會保留目前主題",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"舊版檔案沒有視窗高度設定，因此會保留目前高度設定",
			source,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				"ReportPortableSettingsImportCompleted\\s*\\(\\s*" +
				"snapshot\\.Accounts\\.Count,\\s*" +
				"claudeQuotaRiskConfirmationCount,\\s*" +
				"viewModel\\.AccountSettingsMessage\\s*\\)",
				RegexOptions.CultureInvariant),
			source);
		Assert.Contains(
			"匯入後有 {claudeAccountCount} 個 Claude 帳號需要重新確認用量讀取",
			source,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				"正在匯入設定.*?preserveAgainstAsyncUpdates:\\s*true.*?" +
				"ReplacePortableSettingsAsync",
				RegexOptions.CultureInvariant | RegexOptions.Singleline),
			source);
		Assert.Matches(
			new Regex(
				"正在還原匯入前設定.*?preserveAgainstAsyncUpdates:\\s*true.*?" +
				"UndoLastPortableSettingsImportAsync",
				RegexOptions.CultureInvariant | RegexOptions.Singleline),
			source);
		Assert.Matches(
			new Regex(
				"string claudeQuotaRiskNotice\\s*=.*?" +
				"MessageBoxResult confirmation\\s*=.*?" +
				"claudeQuotaRiskNotice",
				RegexOptions.CultureInvariant | RegexOptions.Singleline),
			source);
		Assert.Matches(
			new Regex(
				"CreatePortableSettingsImportCompletedMessage\\s*\\(.*?" +
				"return.*?CreateClaudeQuotaRiskReconfirmationNotice" +
				"\\(claudeQuotaRiskConfirmationCount\\)",
				RegexOptions.CultureInvariant | RegexOptions.Singleline),
			source);
		Assert.Contains(
			"匯入檔不含登入憑證。匯入後，Antigravity 與 Grok 必須逐一重新連接",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"匯入後，Antigravity 與 Grok 必須逐一重新連接",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"CreatePortableSettingsImportPreview",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"_portableSettingsOperationGate.Wait(0)",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"ReportInlineStatus($\"無法{operation}設定：{reason}\");",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"UndoLastPortableSettingsImportAsync",
			source,
			StringComparison.Ordinal);
	}

	[Fact]
	public void RecoveredPreferences_RevalidatePortableImportUndoWithoutSaving()
	{
		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string method = GetMethodSource(
			source,
			"internal void ApplyRecoveredPreferences(",
			"private void ApplyPortableWidgetPreferences(");
		int applyCompletedIndex = method.LastIndexOf(
			"_isApplyingPortableWidgetPreferences = false;",
			StringComparison.Ordinal);
		int revalidateUndoIndex = method.IndexOf(
			"NotifyPortableWidgetPreferencesChanged();",
			StringComparison.Ordinal);

		Assert.True(applyCompletedIndex >= 0);
		Assert.True(revalidateUndoIndex > applyCompletedIndex);
		Assert.DoesNotContain(
			"NotifyPreferencesChanged();",
			method,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"PreferencesChanged?.Invoke",
			method,
			StringComparison.Ordinal);
	}

	[Fact]
	public void PortableWidgetPreferencesSnapshot_PreservesHiddenAndCollapsedState()
	{
		PortableWidgetPreferences snapshot =
			FloatingWidgetWindow.CreatePortableWidgetPreferencesSnapshot(
				isWidgetVisible: false,
				isCollapsed: true,
				isTopmost: false,
				FloatingWidgetCorner.TopLeft,
				AppTheme.Midnight,
				isHeightFollowingCardCount: true);

		Assert.False(snapshot.IsWidgetVisible);
		Assert.True(snapshot.IsCollapsed);
		Assert.False(snapshot.IsTopmost);
		Assert.Equal(FloatingWidgetCorner.TopLeft, snapshot.Corner);
		Assert.Equal(AppTheme.Midnight, snapshot.Theme);
		Assert.True(snapshot.IsHeightFollowingCardCount);
	}

	[Fact]
	public void PortableWidgetPreferencesRestorePoint_OnlyOwnsCurrentSchemaFields()
	{
		PortableWidgetPreferences current = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			Corner: FloatingWidgetCorner.BottomRight,
			Theme: AppTheme.Midnight,
			IsHeightFollowingCardCount: true);

		PortableWidgetPreferences? legacyRestorePoint =
			FloatingWidgetWindow.CreatePortableWidgetPreferencesRestorePoint(
				current,
				current with
				{
					Theme = null,
					IsHeightFollowingCardCount = null
				});
		PortableWidgetPreferences? currentRestorePoint =
			FloatingWidgetWindow.CreatePortableWidgetPreferencesRestorePoint(
				current,
				current with { Theme = AppTheme.Light });

		Assert.NotNull(legacyRestorePoint);
		Assert.Null(legacyRestorePoint.Theme);
		Assert.Null(legacyRestorePoint.IsHeightFollowingCardCount);
		Assert.Equal(current, currentRestorePoint);
	}

	[Fact]
	public void PortableSettingsArgumentFailure_HidesTechnicalParameterDetails()
	{
		ArgumentOutOfRangeException exception = new(
			"profiles",
			257,
			"匯入檔的帳號卡數量超過上限。");

		string message =
			FloatingWidgetWindow.GetPortableSettingsArgumentFailureReason(
				exception);

		Assert.Equal("匯入檔的帳號卡數量超過上限。", message);
		Assert.DoesNotContain("profiles", message, StringComparison.Ordinal);
		Assert.DoesNotContain("257", message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		"FloatingWidgetWindow.xaml",
		"MenuItem",
		"Header",
		"OpenUserGuideMenuItem_Click")]
	public void UserGuideAction_IsAlwaysAvailable(
		string fileName,
		string elementName,
		string labelAttribute,
		string clickHandler)
	{
		XDocument window = LoadAppXaml(fileName);
		XElement action = Assert.Single(
			window.Descendants(Presentation + elementName),
			element => string.Equals(
				(string?)element.Attribute(
					"AutomationProperties.AutomationId"),
				"OpenUserGuide",
				StringComparison.Ordinal));

		Assert.Equal("使用說明", (string?)action.Attribute(labelAttribute));
		Assert.Equal(clickHandler, (string?)action.Attribute("Click"));
	}

	[Fact]
	public void TrayMenu_ProvidesUserGuideAction()
	{
		string source = LoadAppSource("App.xaml.cs");

		Assert.Matches(
			new Regex(
				@"menu\.Items\.Add\(\s*""使用說明"",\s*null,\s*\(_, _\) => Dispatcher\.Invoke\(OpenUserGuide\)\);",
				RegexOptions.CultureInvariant),
				source);
	}

	[Fact]
	public void TrayMenu_ProvidesPermanentAndContextualUpdateActions()
	{
		string source = LoadAppSource("App.xaml.cs");
		string initializer = GetMethodSource(
			source,
			"private void InitializeNotifyIcon()",
			"internal void ShowAboutWindow()");
		int contextualActionStart = initializer.IndexOf(
			"_updateAvailableMenuItem =",
			StringComparison.Ordinal);
		int manualActionStart = initializer.IndexOf(
			"_checkForUpdatesMenuItem =",
			StringComparison.Ordinal);
		int automaticActionStart = initializer.IndexOf(
			"_automaticUpdateChecksMenuItem =",
			StringComparison.Ordinal);
		int logonStartupActionStart = initializer.IndexOf(
			"_logonStartupMenuItem =",
			StringComparison.Ordinal);

		Assert.True(contextualActionStart >= 0);
		Assert.True(manualActionStart > contextualActionStart);
		Assert.True(automaticActionStart > manualActionStart);
		Assert.True(logonStartupActionStart > automaticActionStart);

		string contextualAction = initializer[
			contextualActionStart..manualActionStart];
		Assert.Contains(
			"Visible = false",
			contextualAction,
			StringComparison.Ordinal);
		Assert.Contains(
			"ExecutePrimaryUpdateActionAsync",
			contextualAction,
			StringComparison.Ordinal);
		Assert.Contains(
			"menu.Items.Add(_updateAvailableMenuItem);",
			contextualAction,
			StringComparison.Ordinal);

		string manualAction = initializer[
			manualActionStart..automaticActionStart];
		Assert.Contains("檢查更新", manualAction, StringComparison.Ordinal);
		Assert.Contains(
			"CheckForUpdatesManuallyAsync",
			manualAction,
			StringComparison.Ordinal);
		Assert.Contains(
			"menu.Items.Add(_checkForUpdatesMenuItem);",
			manualAction,
			StringComparison.Ordinal);

		string automaticAction = initializer[
			automaticActionStart..logonStartupActionStart];
		Assert.Contains("自動檢查更新", automaticAction, StringComparison.Ordinal);
		Assert.Contains(
			"SetAutomaticUpdateChecksEnabledAsync(",
			automaticAction,
			StringComparison.Ordinal);
		Assert.Contains(
			"!_updatePresentationState.IsAutoCheckEnabled",
			automaticAction,
			StringComparison.Ordinal);
		Assert.Contains(
			"menu.Items.Add(_automaticUpdateChecksMenuItem);",
			automaticAction,
			StringComparison.Ordinal);

		string presentationUpdater = GetMethodSource(
			source,
			"private void UpdateUpdateTrayMenuItems(",
			"private void InitializeUpdateCheckTimer()");
		Assert.Contains(
			"presentation.CanCheckManually",
			presentationUpdater,
			StringComparison.Ordinal);
		Assert.Contains(
			"_updatePresentationState.IsAutoCheckEnabled",
			presentationUpdater,
			StringComparison.Ordinal);
		Assert.Contains(
			"!string.IsNullOrWhiteSpace(presentation.TrayUpdateActionText)",
			presentationUpdater,
			StringComparison.Ordinal);
		Assert.Contains(
			"presentation.IsPrimaryActionEnabled",
			presentationUpdater,
			StringComparison.Ordinal);
		Assert.Contains(
			"presentation.TrayUpdateActionText ?? \"開啟下載頁\"",
			presentationUpdater,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TrayMenu_PortableActionsCaptureStateWithoutShowingOrExpandingWidget()
	{
		string appSource = LoadAppSource("App.xaml.cs");
		string windowSource = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string userGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"使用說明.md"));

		Assert.Matches(
			new Regex(
				@"_exportPortableSettingsMenuItem\.Click\s*\+=\s*" +
				@"\(_, _\) => Dispatcher\.Invoke\(\s*" +
				@"\(\) => _floatingWidgetWindow\?\.BeginPortableSettingsExport\(\)\);",
				RegexOptions.CultureInvariant),
			appSource);
		Assert.Matches(
			new Regex(
				@"_undoPortableSettingsImportMenuItem\.Click\s*\+=\s*" +
				@"\(_, _\) => Dispatcher\.Invoke\(\s*" +
				@"\(\) => _floatingWidgetWindow\?\.BeginPortableSettingsImportUndo\(\)\);",
				RegexOptions.CultureInvariant),
			appSource);
		Assert.Matches(
			new Regex(
				@"_undoPortableSettingsImportMenuItem\.Enabled\s*=\s*" +
				@"_dashboardViewModel\?\.CanUndoLastPortableSettingsImport == true;",
				RegexOptions.CultureInvariant),
			appSource);
		string notifyIconInitializer = GetMethodSource(
			appSource,
			"private void InitializeNotifyIcon()",
			"internal void ShowAboutWindow()");
		int openingHandlerStart = notifyIconInitializer.IndexOf(
			"menu.Opening +=",
			StringComparison.Ordinal);
		int firstMenuItemDefinition = notifyIconInitializer.IndexOf(
			"_widgetVisibilityMenuItem =",
			StringComparison.Ordinal);
		Assert.True(openingHandlerStart >= 0);
		Assert.True(firstMenuItemDefinition > openingHandlerStart);
		string openingHandler = notifyIconInitializer[
			openingHandlerStart..firstMenuItemDefinition];
		Assert.Contains(
			"UpdatePortableSettingsMenuItems();",
			openingHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"UpdateLogonStartupMenuItem();",
			openingHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"UpdateUpdateTrayMenuItems(presentation);",
			openingHandler,
			StringComparison.Ordinal);

		string exportEntry = GetMethodSource(
			windowSource,
			"internal void BeginPortableSettingsExport()",
			"private async Task ExportPortableSettingsAsync(");
		Assert.True(
			exportEntry.IndexOf(
				"CapturePortableWidgetPreferences()",
				StringComparison.Ordinal) <
			exportEntry.IndexOf(
				"ExportPortableSettingsAsync(widgetPreferences)",
				StringComparison.Ordinal));
		Assert.DoesNotContain("Show", exportEntry, StringComparison.Ordinal);
		Assert.DoesNotContain("SetCollapsed", exportEntry, StringComparison.Ordinal);

		string undoEntry = GetMethodSource(
			windowSource,
			"internal void BeginPortableSettingsImportUndo()",
			"private async Task UndoLastPortableSettingsImportAsync(");
		Assert.True(
			undoEntry.IndexOf(
				"CreatePreferences(IsVisible)",
				StringComparison.Ordinal) <
			undoEntry.IndexOf(
				"UndoLastPortableSettingsImportAsync(",
				StringComparison.Ordinal));
		Assert.DoesNotContain("Show", undoEntry, StringComparison.Ordinal);
		Assert.DoesNotContain("SetCollapsed", undoEntry, StringComparison.Ordinal);
		Assert.Contains(
			"單純顯示或隱藏浮窗不會關閉還原功能",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"請直接從系統匣選擇 **還原匯入前設定**，不要先展開浮窗",
			userGuide,
			StringComparison.Ordinal);
	}

	[Fact]
	public void LogonStartup_DisablesFirstWindowActivationBeforeSurfaceRestore()
	{
		string source = LoadAppSource("App.xaml.cs");
		int windowCreationIndex = source.IndexOf(
			"_floatingWidgetWindow = new FloatingWidgetWindow(",
			StringComparison.Ordinal);
		int suppressActivationIndex = source.IndexOf(
			"_floatingWidgetWindow.ShowActivated = false;",
			windowCreationIndex,
			StringComparison.Ordinal);
		int restoreSurfaceIndex = source.IndexOf(
			"RestoreStartupSurface(",
			suppressActivationIndex,
			StringComparison.Ordinal);

		Assert.True(windowCreationIndex >= 0);
		Assert.True(suppressActivationIndex > windowCreationIndex);
		Assert.True(restoreSurfaceIndex > suppressActivationIndex);
		Assert.Contains(
			"ShouldSuppressStartupWindowActivation(launchIntent)",
			source[windowCreationIndex..restoreSurfaceIndex],
			StringComparison.Ordinal);
		Assert.Contains(
			"_floatingWidgetWindow.Activate();",
			source,
			StringComparison.Ordinal);
	}

	[Fact]
	public void FloatingAccountCard_UsesCardSpecificAccountText()
	{
		string xaml = File.ReadAllText(
			GetAppXamlPath("FloatingWidgetWindow.xaml"));

		Assert.Contains(
			"Text=\"{Binding ProviderName, Mode=OneWay}\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"Text=\"{Binding AccountHeaderSuffixText, Mode=OneWay}\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"ToolTip=\"{Binding AccountHeaderText}\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"Text=\"{Binding AccountCardDisplayText}\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"ToolTip=\"{Binding AccountCardDisplayText}\"",
			xaml,
			StringComparison.Ordinal);
	}

	[Fact]
	public void FloatingUsageMetric_ExpandsValueWhenResetTextIsAbsent()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement metricValue = GetNamedElement(
			window,
			"TextBlock",
			"MetricValueTextBlock");
		XElement style = Assert.Single(
			metricValue.Elements(Presentation + "TextBlock.Style"))
			.Element(Presentation + "Style")!;
		XElement trigger = Assert.Single(
			style
				.Element(Presentation + "Style.Triggers")!
				.Elements(Presentation + "DataTrigger"),
			element =>
				string.Equals(
					(string?)element.Attribute("Binding"),
					"{Binding HasResetText}",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)element.Attribute("Value"),
					"False",
					StringComparison.Ordinal));

		AssertSetter(style, "MaxWidth", "90");
		AssertSetter(trigger, "MaxWidth", "210");
		Assert.Equal(
			"{StaticResource UsageMetricValueTextStyle}",
			(string?)style.Attribute("BasedOn"));
		Assert.Equal(
			"CharacterEllipsis",
			(string?)metricValue.Attribute("TextTrimming"));
		Assert.Equal(
			"{Binding ToolTipValue}",
			(string?)metricValue.Attribute("ToolTip"));
	}

	[Fact]
	public void FloatingAccountHeader_DoesNotShowProviderUsageStatus()
	{
		string xaml = File.ReadAllText(
			GetAppXamlPath("FloatingWidgetWindow.xaml"));

		Assert.DoesNotContain("UsageStatusBadgeHost", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("UsageStatusBadge", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("UsageStatusMetric", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("HasUsageStatus", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("UsageStatusText", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("UsageStatusToolTip", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"Text=\"{Binding DisplayStatusText}\"",
			xaml,
			StringComparison.Ordinal);
	}

	[Fact]
	public void SubscriptionContextPreference_IsPerAccountInsteadOfGlobal()
	{
		string source = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string shellPreferences = LoadAppSource(
			Path.Combine("Persistence", "IDashboardPreferencesStore.cs"));
		string editor = LoadAppSource("AccountEditorWindow.xaml.cs");

		Assert.Contains(
			"ShowSubscriptionContext = !account.ShowSubscriptionContext",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ShowClaudeOrganization",
			source,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ShowClaudeOrganization",
			shellPreferences,
			StringComparison.Ordinal);
		Assert.Contains(
			"_showSubscriptionContext = profile?.ShowSubscriptionContext == true",
			editor,
			StringComparison.Ordinal);
		Assert.Contains(
			"_showSubscriptionContext);",
			editor,
			StringComparison.Ordinal);
		Assert.Contains(
			"UpdateTarget();",
			source,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TrayMenu_UsesFloatingWidgetAsOnlySurface()
	{
		string source = LoadAppSource("App.xaml.cs");
		int methodStart = source.IndexOf(
			"private void InitializeNotifyIcon()",
			StringComparison.Ordinal);
		Assert.True(methodStart >= 0);
		int methodEnd = source.IndexOf(
			"private void OpenUserGuide()",
			methodStart,
			StringComparison.Ordinal);
		Assert.True(methodEnd > methodStart);
		string method = source[methodStart..methodEnd];

		int visibilityIndex = method.IndexOf(
			"menu.Items.Add(_widgetVisibilityMenuItem);",
			StringComparison.Ordinal);
		int topmostIndex = method.IndexOf(
			"menu.Items.Add(_widgetTopmostMenuItem);",
			StringComparison.Ordinal);
		int topmostDefinitionIndex = method.IndexOf(
			"new FormsToolStripMenuItem(\"置頂\")",
			StringComparison.Ordinal);
		int contextualUpdateIndex = method.IndexOf(
			"menu.Items.Add(_updateAvailableMenuItem);",
			StringComparison.Ordinal);
		int manualUpdateIndex = method.IndexOf(
			"menu.Items.Add(_checkForUpdatesMenuItem);",
			StringComparison.Ordinal);
		int automaticUpdateIndex = method.IndexOf(
			"menu.Items.Add(_automaticUpdateChecksMenuItem);",
			StringComparison.Ordinal);
		int logonStartupIndex = method.IndexOf(
			"menu.Items.Add(_logonStartupMenuItem);",
			StringComparison.Ordinal);
		int startupSettingsIndex = method.IndexOf(
			"\"開啟 Windows 啟動應用程式設定\"",
			StringComparison.Ordinal);
		int exportIndex = method.IndexOf(
			"menu.Items.Add(_exportPortableSettingsMenuItem);",
			StringComparison.Ordinal);
		int undoIndex = method.IndexOf(
			"menu.Items.Add(_undoPortableSettingsImportMenuItem);",
			StringComparison.Ordinal);
		int userGuideIndex = method.IndexOf(
			"\"使用說明\"",
			StringComparison.Ordinal);
		int updateSeparatorIndex = method.IndexOf(
			"menu.Items.Add(\"-\");",
			topmostIndex,
			StringComparison.Ordinal);
		int startupSeparatorIndex = method.IndexOf(
			"menu.Items.Add(\"-\");",
			updateSeparatorIndex + 1,
			StringComparison.Ordinal);
		int settingsSeparatorIndex = method.IndexOf(
			"menu.Items.Add(\"-\");",
			startupSeparatorIndex + 1,
			StringComparison.Ordinal);
		int userGuideSeparatorIndex = method.IndexOf(
			"menu.Items.Add(\"-\");",
			settingsSeparatorIndex + 1,
			StringComparison.Ordinal);
		int exitSeparatorIndex = method.IndexOf(
			"menu.Items.Add(\"-\");",
			userGuideSeparatorIndex + 1,
			StringComparison.Ordinal);

		Assert.True(visibilityIndex >= 0);
		Assert.True(topmostDefinitionIndex > visibilityIndex);
		Assert.True(topmostIndex > visibilityIndex);
		Assert.True(updateSeparatorIndex > topmostIndex);
		Assert.True(contextualUpdateIndex > updateSeparatorIndex);
		Assert.True(manualUpdateIndex > contextualUpdateIndex);
		Assert.True(automaticUpdateIndex > manualUpdateIndex);
		Assert.True(startupSeparatorIndex > automaticUpdateIndex);
		Assert.True(logonStartupIndex > startupSeparatorIndex);
		Assert.True(startupSettingsIndex > logonStartupIndex);
		Assert.True(settingsSeparatorIndex > startupSettingsIndex);
		Assert.True(exportIndex > settingsSeparatorIndex);
		Assert.True(undoIndex > exportIndex);
		Assert.True(userGuideSeparatorIndex > undoIndex);
		Assert.True(userGuideIndex > userGuideSeparatorIndex);
		Assert.True(exitSeparatorIndex > userGuideIndex);
		Assert.Contains(
			"new FormsToolStripMenuItem(\"置頂\")",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"CheckOnClick = false",
			method[topmostDefinitionIndex..topmostIndex],
			StringComparison.Ordinal);
		Assert.Contains(
			"Dispatcher.Invoke(ToggleFloatingWidgetTopmost)",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"Dispatcher.Invoke(ShowFloatingWidget)",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"結束 AI Usage\"",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"Text = \"AI Usage\"",
			method,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AI Usage Dashboard",
			method,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"開啟 Dashboard",
			method,
			StringComparison.Ordinal);
		Assert.Contains(
			"_widgetTopmostMenuItem.Checked =",
			source,
			StringComparison.Ordinal);
		string preferencesChangedHandler = GetMethodSource(
			source,
			"private void FloatingWidgetWindow_PreferencesChanged(",
			"private void QueueShellPreferencesSave()");
		int themeUpdateIndex = preferencesChangedHandler.IndexOf(
			"ApplyThemePalette();",
			StringComparison.Ordinal);
		int topmostUpdateIndex = preferencesChangedHandler.IndexOf(
			"UpdateWidgetTopmostMenuItem();",
			StringComparison.Ordinal);
		int saveIndex = preferencesChangedHandler.IndexOf(
			"QueueShellPreferencesSave();",
			StringComparison.Ordinal);

		Assert.True(themeUpdateIndex >= 0);
		Assert.True(topmostUpdateIndex > themeUpdateIndex);
		Assert.True(saveIndex > topmostUpdateIndex);

		string floatingSource = LoadAppSource(
			"FloatingWidgetWindow.xaml.cs");
		Assert.Matches(
			new Regex(
				@"internal void ToggleTopmost\(\)\s*\{\s*PinButton\.IsChecked = PinButton\.IsChecked != true;\s*\}",
				RegexOptions.CultureInvariant),
			floatingSource);
	}

	[Fact]
	public void FloatingWidget_IsTheOnlyPrimarySurface()
	{
		string appSource = LoadAppSource("App.xaml.cs");
		string floatingSource = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string floatingXaml = File.ReadAllText(
			GetAppXamlPath("FloatingWidgetWindow.xaml"));

		Assert.Contains(
			"MainWindow = _floatingWidgetWindow;",
			appSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"ShowExpandedNearWorkArea()",
			appSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ShowDashboard",
			appSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"OpenDashboardMenuItem_Click",
			floatingSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"開啟完整 Dashboard",
			floatingXaml,
			StringComparison.Ordinal);
		Assert.False(File.Exists(GetAppXamlPath("MainWindow.xaml")));
	}

	[Fact]
	public void AutomaticRetryNotice_DoesNotOfferManualRecoveryControls()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement retryButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Content"),
				"完成後再檢查",
				StringComparison.Ordinal));
		XElement actionPanel = Assert.IsType<XElement>(retryButton.Parent);

		Assert.Equal(
			"{Binding HasExecutableRecoveryAction, Converter={StaticResource BooleanToVisibilityConverter}}",
			(string?)actionPanel.Attribute("Visibility"));
		Assert.Contains(
			actionPanel.Elements(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Content"),
				"複製處理步驟",
				StringComparison.Ordinal));
	}

	[Fact]
	public void FloatingEmptyState_KeepsSingleAddAction()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement emptyStateButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element =>
				string.Equals(
					(string?)element.Attribute("Content"),
					"新增帳號",
					StringComparison.Ordinal));

		Assert.Equal(
			"{StaticResource PrimaryButtonStyle}",
			(string?)emptyStateButton.Attribute("Style"));
	}

	[Fact]
	public void FloatingRefreshFooter_UsesCompactVisualStatusAndKeepsFullDetails()
	{
		XDocument window = LoadAppXaml("FloatingWidgetWindow.xaml");
		XElement footer = GetNamedElement(window, "Grid", "ExpandedFooter");
		XElement compactStatus = GetNamedElement(
			window,
			"TextBlock",
			"CompactRefreshTextBlock");
		XElement refreshButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Click"),
				"RefreshButton_Click",
				StringComparison.Ordinal));
		XElement globalMenuButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Click"),
				"GlobalMenuButton_Click",
				StringComparison.Ordinal));

		Assert.Equal("Collapsed", (string?)footer.Attribute("Visibility"));
		Assert.Equal(
			"{Binding CompactRefreshStatus.Text}",
			(string?)compactStatus.Attribute("Text"));
		Assert.Equal(
			"{Binding LastRefreshText}",
			(string?)compactStatus.Attribute("ToolTip"));
		Assert.Equal(
			"{Binding LastRefreshText}",
			(string?)compactStatus.Attribute("AutomationProperties.Name"));
		Assert.Equal(
			"{Binding LastRefreshText}",
			(string?)refreshButton.Attribute("AutomationProperties.HelpText"));
		Assert.Equal(
			"檢查各服務的最新用量",
			(string?)refreshButton.Attribute("ToolTip"));
		Assert.Equal(
			"開啟更多操作",
			(string?)globalMenuButton.Attribute("AutomationProperties.Name"));
	}

	[Theory]
	[InlineData("FloatingWidgetWindow.xaml")]
	public void ProviderConnectionActions_RemainAvailableForCancellation(
		string fileName)
	{
		XDocument window = LoadAppXaml(fileName);
		string xaml = window.ToString();
		string[] connectionActionHandlers =
		[
			"ConnectClaudeAccountButton_Click",
			"ConnectCodexAccountButton_Click",
			"ConnectGrokAccountButton_Click",
			"ConnectAntigravityAccountButton_Click"
		];

		Assert.Contains(
			"CanInvokeProviderAccountAction",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"CanInvokeRecoveryAction",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"CodexAccountActionText",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"連接／切換 Codex",
			xaml,
			StringComparison.Ordinal);

		foreach (string clickHandler in connectionActionHandlers)
		{
			Assert.Contains(
				window.Descendants(Presentation + "Button"),
				button => string.Equals(
					(string?)button.Attribute("Click"),
					clickHandler,
					StringComparison.Ordinal));
			Assert.DoesNotContain(
				window.Descendants(Presentation + "MenuItem"),
				menuItem => string.Equals(
					(string?)menuItem.Attribute("Click"),
					clickHandler,
					StringComparison.Ordinal));
		}
	}

	[Fact]
	public void AccountEditorDisclosure_ListsStoredDataAndCredentialExclusion()
	{
		string xaml = File.ReadAllText(GetAppXamlPath("AccountEditorWindow.xaml"));
		string source = LoadAppSource("AccountEditorWindow.xaml.cs");
		string claudeStoredData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Claude);
		string codexStoredData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Codex);
		string antigravityStoredData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Antigravity);
		string copilotStoredData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Copilot);
		string grokStoredData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Grok);
		string paragraphBreak = Environment.NewLine + Environment.NewLine;

		Assert.Contains("儲存的資料", xaml, StringComparison.Ordinal);
		Assert.Contains("帳號設定", xaml, StringComparison.Ordinal);
		Assert.Contains("Title = \"帳號設定\";", source, StringComparison.Ordinal);
		Assert.Contains(
			"TitleTextBlock.Text = \"帳號設定\";",
			source,
			StringComparison.Ordinal);
		Assert.Contains("帳號資訊", xaml, StringComparison.Ordinal);
		Assert.Contains("上次用量", xaml, StringComparison.Ordinal);
		Assert.Contains("不會儲存密碼", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("ProviderStoredDataTitleTextBlock", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("GetProviderStoredDataTitleText", source, StringComparison.Ordinal);
		Assert.Contains("ProviderStoredDataTextBlock", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"ProviderStoredDataTextBlock.Text =",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"GetProviderStoredDataText(provider);",
			source,
			StringComparison.Ordinal);

		Assert.Contains(
			"是否同意讀取 Claude 用量",
			claudeStoredData,
			StringComparison.Ordinal);
		Assert.Contains("Claude Code CLI", claudeStoredData, StringComparison.Ordinal);
		Assert.Contains(
			"確認目前的帳號與 Claude organization 是否和這張卡片原本的一樣",
			claudeStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"這台電腦的上次用量資料會保留電子郵件、方案和 organization 名稱",
			claudeStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"organization ID 本身不會直接儲存",
			claudeStoredData,
			StringComparison.Ordinal);
		Assert.Contains("不會跟著設定匯出", claudeStoredData, StringComparison.Ordinal);

		Assert.Contains(
			"workspace ID 只會寫進這張卡片的 Codex 設定",
			codexStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"確認目前的 ChatGPT 帳號與 workspace 是否和這張卡片原本的一樣",
			codexStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"這台電腦的上次用量資料會保留電子郵件和方案",
			codexStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"重新連接時要再輸入",
			codexStoredData,
			StringComparison.Ordinal);
		Assert.Contains("不會跟著設定匯出", codexStoredData, StringComparison.Ordinal);

		Assert.Contains("Antigravity CLI", antigravityStoredData, StringComparison.Ordinal);
		Assert.Contains("電子郵件或方案", antigravityStoredData, StringComparison.Ordinal);
		Assert.Contains(
			"顯示資料和讀取時間",
			antigravityStoredData,
			StringComparison.Ordinal);
		Assert.Contains(
			"不會讀取密碼或其他登入憑證",
			antigravityStoredData,
			StringComparison.Ordinal);

		Assert.Contains("每張卡片的登入資料分開儲存", grokStoredData, StringComparison.Ordinal);
		Assert.Contains("移除卡片或匯入設定時", grokStoredData, StringComparison.Ordinal);
		Assert.Contains("也會刪除", grokStoredData, StringComparison.Ordinal);
		Assert.Contains("Windows Credential Manager", copilotStoredData, StringComparison.Ordinal);
		Assert.Contains("匯出的設定", copilotStoredData, StringComparison.Ordinal);
		Assert.Contains("移除卡片時也會刪除", copilotStoredData, StringComparison.Ordinal);

		Assert.Contains(paragraphBreak, claudeStoredData, StringComparison.Ordinal);
		Assert.Contains(paragraphBreak, codexStoredData, StringComparison.Ordinal);
		Assert.Contains(paragraphBreak, antigravityStoredData, StringComparison.Ordinal);
		Assert.Contains(paragraphBreak, copilotStoredData, StringComparison.Ordinal);
		Assert.Contains(paragraphBreak, grokStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("workspace ID", claudeStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("Grok Build CLI", claudeStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("Claude organization", codexStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("Antigravity CLI", codexStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("Grok Build CLI", antigravityStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("workspace ID", grokStoredData, StringComparison.Ordinal);
		Assert.DoesNotContain("連接識別", source, StringComparison.Ordinal);
		Assert.DoesNotContain("加鹽指紋", source, StringComparison.Ordinal);
		Assert.DoesNotContain("雜湊值", source, StringComparison.Ordinal);
		Assert.DoesNotContain("可攜設定", source, StringComparison.Ordinal);
		Assert.Contains("帳號連接", xaml, StringComparison.Ordinal);
		Assert.Contains("Content=\"儲存\"", xaml, StringComparison.Ordinal);
		Assert.Contains("Content=\"儲存並連接\"", xaml, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorActions_DescribeSavingAndUsageChecks()
	{
		string xaml = LoadAppSource("AccountEditorWindow.xaml");
		string source = LoadAppSource("AccountEditorWindow.xaml.cs");
		string coordinatorSource =
			LoadAppSource("AccountConnectionCoordinator.cs");
		string widgetSource = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string combined = xaml + source;
		string claudeNotice =
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Claude)!;

		Assert.Contains(
			"Content=\"開始檢查用量\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"連接前會再請你確認",
			claudeNotice,
			StringComparison.Ordinal);
		Assert.Contains(
			"可能產生少量用量",
			claudeNotice,
			StringComparison.Ordinal);
		Assert.Contains(
			"用量或費用不能取消",
			claudeNotice,
			StringComparison.Ordinal);
		Assert.Contains(
			"查詢狀態時也可能計入少量用量",
			coordinatorSource,
			StringComparison.Ordinal);
		Assert.Contains("\"儲存並連接\"", source, StringComparison.Ordinal);
		Assert.Contains("return \"儲存\";", source, StringComparison.Ordinal);
		Assert.DoesNotContain("了解並連接", combined, StringComparison.Ordinal);
		Assert.Contains(
			"hasConfirmedClaudeQuotaRisk:",
			widgetSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"profile.Provider == ProviderKind.Claude",
			widgetSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"profile.Provider == ProviderKind.Antigravity",
			widgetSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain("新增但不連接", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("新增並連接", combined, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Content=\"啟用這個帳號\"",
			xaml,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProviderKind.Claude, true, "儲存並連接")]
	[InlineData(ProviderKind.Codex, true, "儲存並連接")]
	[InlineData(ProviderKind.Antigravity, true, "儲存並連接")]
	[InlineData(ProviderKind.Grok, true, "儲存並連接")]
	[InlineData(ProviderKind.Copilot, true, "儲存並連接")]
	[InlineData(ProviderKind.Claude, false, "儲存")]
	public void AccountEditorPrimaryAction_MatchesProviderAndUsageCheckState(
		ProviderKind provider,
		bool isEnabled,
		string expected)
	{
		Assert.Equal(
			expected,
			AccountEditorWindow.GetPrimaryActionText(provider, isEnabled));
	}

	[Theory]
	[InlineData(ProviderKind.Claude, false, "連接 Claude 帳號")]
	[InlineData(ProviderKind.Claude, true, "切換 Claude 帳號")]
	[InlineData(ProviderKind.Codex, true, "切換 Codex 帳號")]
	[InlineData(ProviderKind.Grok, true, "切換 Grok 帳號")]
	[InlineData(
		ProviderKind.Antigravity,
		true,
		"重新連接 Antigravity 帳號")]
	public void AccountEditorConnectionAction_MatchesProviderAndIdentity(
		ProviderKind provider,
		bool hasProviderAccountIdentity,
		string expected)
	{
		Assert.Equal(
			expected,
			AccountEditorWindow.GetConnectionActionText(
				provider,
				hasProviderAccountIdentity));
	}

	[Theory]
	[InlineData(ProviderKind.Claude, true, false, true, "Claude Code CLI")]
	[InlineData(ProviderKind.Codex, true, false, true, "Codex CLI")]
	[InlineData(ProviderKind.Grok, true, false, true, "Grok Build CLI")]
	[InlineData(
		ProviderKind.Antigravity,
		true,
		false,
		true,
		"不會另開登入畫面")]
	[InlineData(
		ProviderKind.Claude,
		false,
		false,
		true,
		"請先勾選「開始檢查用量」")]
	[InlineData(
		ProviderKind.Claude,
		true,
		true,
		true,
		"先確認是否讀取 Claude 用量")]
	[InlineData(
		ProviderKind.Antigravity,
		true,
		false,
		false,
		"請先處理卡片上的提示")]
	public void AccountEditorConnectionHint_ExplainsNextStep(
		ProviderKind provider,
		bool isEnabled,
		bool requiresClaudeQuotaRiskConsent,
		bool canConnectProviderAccount,
		string expected)
	{
		Assert.Contains(
			expected,
			AccountEditorWindow.GetConnectionHintText(
				provider,
				isEnabled,
				requiresClaudeQuotaRiskConsent,
				canConnectProviderAccount),
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorExistingAccountConnection_SavesBeforeCliDispatch()
	{
		XDocument window = LoadAppXaml("AccountEditorWindow.xaml");
		XElement connectionPanel = GetNamedElement(
			window,
			"Border",
			"AccountConnectionPanel");
		XElement connectionButton = GetNamedElement(
			window,
			"Button",
			"ConnectAccountButton");
		XElement saveButton = GetNamedElement(
			window,
			"Button",
			"SaveAndConnectButton");
		string editorSource = LoadAppSource("AccountEditorWindow.xaml.cs");
		string widgetSource = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string connectButtonHandler = GetMethodSource(
			editorSource,
			"private void ConnectAccountButton_Click(",
			"private void Save(bool connectAfterSave)");
		string editHandler = GetMethodSource(
			widgetSource,
			"private async void EditAccountMenuItem_Click(",
			"private async Task<bool> ExecuteAccountChangeAsync(");
		string connectionDispatcher = GetMethodSource(
			widgetSource,
			"private async Task ConnectAccountAsync(",
			"private async void DeleteAccountMenuItem_Click(");

		Assert.Equal("Collapsed", (string?)connectionPanel.Attribute("Visibility"));
		Assert.Equal(
			"ConnectAccountButton_Click",
			(string?)connectionButton.Attribute("Click"));
		Assert.Null(connectionButton.Attribute("IsDefault"));
		Assert.Equal("True", (string?)saveButton.Attribute("IsDefault"));
		Assert.Contains(
			"Save(connectAfterSave: true);",
			connectButtonHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"_canConnectCurrentProviderAccount",
			editorSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"請先勾選「開始檢查用量」，才能連接帳號。",
			editorSource,
			StringComparison.Ordinal);
		int updateIndex = editHandler.IndexOf(
			"ExecuteAccountChangeAsync(",
			StringComparison.Ordinal);
		int connectIndex = editHandler.IndexOf(
			"ConnectAccountAsync(",
			StringComparison.Ordinal);
		Assert.True(updateIndex >= 0);
		Assert.True(connectIndex >= 0);
		Assert.True(updateIndex < connectIndex);
		Assert.Matches(
			@"if\s*\(\s*wasUpdated\s*&&\s*editorWindow\.ConnectAfterSave\s*&&",
			editHandler);
		Assert.Contains("\"帳號：", editorSource, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"\"目前帳號：",
			editorSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"editorWindow.ConnectAfterSave",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"account.ProviderAccountDisplayText",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"account.HasProviderAccountIdentity",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"account.CanConnectProviderAccountAfterSave",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"account.ClaudeAccountActionText",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"hasConfirmedClaudeQuotaRisk: false",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"hasConfirmedAntigravityPreflight: false",
			editHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"hasConfirmedPreflight:",
			connectionDispatcher,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"hasConfirmedPreflight: true",
			connectionDispatcher,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorNickname_IsOptionalAndDoesNotPromiseAutomaticNaming()
	{
		string xaml = File.ReadAllText(GetAppXamlPath("AccountEditorWindow.xaml"));

		Assert.Contains("Content=\"暱稱（選填）\"", xaml, StringComparison.Ordinal);
		Assert.Contains("留白時不顯示暱稱", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("自動命名", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("Claude 2", xaml, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorProviderDescriptions_UseConsistentCliStructure()
	{
		string xaml = LoadAppSource("AccountEditorWindow.xaml");
		string source = LoadAppSource("AccountEditorWindow.xaml.cs");
		string combined = xaml + source;
		string paragraphBreak = Environment.NewLine + Environment.NewLine;

		Assert.Contains(
			"使用 Claude Code CLI 登入或切換帳號。",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"使用 Codex CLI 登入。",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"同一個 ChatGPT 帳號若要顯示多個 workspace，每張卡片都要填入對應的 workspace ID，原本的第一張也一樣。",
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Codex)!,
			StringComparison.Ordinal);
		Assert.Contains(
			"使用這台電腦目前登入的 Antigravity 帳號。",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"使用 Grok Build CLI 登入或切換帳號。",
			source,
			StringComparison.Ordinal);
		Assert.Contains("ProviderNoticeTitleTextBlock", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"ProviderNoticeTitleTextBlock.Text = GetProviderNoticeTitleText(provider);",
			source,
			StringComparison.Ordinal);
		Assert.Equal(
			"連接 Claude 前",
			AccountEditorWindow.GetProviderNoticeTitleText(ProviderKind.Claude));
		Assert.Equal(
			"連接 Codex 前",
			AccountEditorWindow.GetProviderNoticeTitleText(ProviderKind.Codex));
		Assert.Equal(
			"連接 Antigravity 前",
			AccountEditorWindow.GetProviderNoticeTitleText(ProviderKind.Antigravity));
		Assert.Equal(
			"連接 Grok 前",
			AccountEditorWindow.GetProviderNoticeTitleText(ProviderKind.Grok));
		Assert.Contains(
			paragraphBreak,
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Claude)!,
			StringComparison.Ordinal);
		Assert.Contains(
			paragraphBreak,
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Codex)!,
			StringComparison.Ordinal);
		Assert.Contains(
			paragraphBreak,
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Antigravity)!,
			StringComparison.Ordinal);
		Assert.Contains(
			paragraphBreak,
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Grok)!,
			StringComparison.Ordinal);
		Assert.DoesNotContain("AGY", combined, StringComparison.Ordinal);
	}

	[Fact]
	public void CodexWorkspacePrompt_DefaultsToAccountLevelAndKeepsWorkspaceIdOptional()
	{
		XDocument window = LoadAppXaml("CodexWorkspacePromptWindow.xaml");
		string source = LoadAppSource("CodexWorkspacePromptWindow.xaml.cs");
		string coordinatorSource =
			LoadAppSource("AccountConnectionCoordinator.cs");
		XElement workspaceTextBox = GetNamedElement(
			window,
			"TextBox",
			"WorkspaceIdTextBox");
		XElement defaultButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Click"),
				"ContinueWithoutWorkspaceButton_Click",
				StringComparison.Ordinal));
		XElement workspaceButton = Assert.Single(
			window.Descendants(Presentation + "Button"),
			element => string.Equals(
				(string?)element.Attribute("Click"),
				"ContinueWithWorkspaceButton_Click",
				StringComparison.Ordinal));

		Assert.Contains(
			"只需要一張 Codex 卡片時，選「一般帳號連接」",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"同一個 ChatGPT 帳號若要用多張卡片顯示不同 workspace",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"每張卡片都要填入對應的 workspace ID，原本的第一張也一樣",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"每個 workspace 各一張卡片",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"不是電腦資料夾",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"workspace 是 ChatGPT 裡的工作空間",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"不是訂閱方案",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"AI Usage 無法從電子郵件、ChatGPT 帳號或方案查出",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"ID 會寫入這張卡片的 Codex 設定",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"用 ChatGPT 登入 Codex 時，只能使用指定的 workspace",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"重新連接時要再輸入一次",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"不會放進匯出的設定檔",
			window.ToString(),
			StringComparison.Ordinal);
		Assert.Contains(
			"ChatGPT workspace 管理者提供的 ID（UUID 格式）",
			(string?)workspaceTextBox.Attribute("AutomationProperties.HelpText"),
			StringComparison.Ordinal);
		Assert.DoesNotContain("workspace UUID", window.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("連接識別", window.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("加鹽指紋", window.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("可攜設定", window.ToString(), StringComparison.Ordinal);
		Assert.Contains(
			"請輸入有效的 ChatGPT workspace ID（UUID 格式）。",
			source,
			StringComparison.Ordinal);
		Assert.Equal("True", (string?)defaultButton.Attribute("IsDefault"));
		Assert.Null(workspaceButton.Attribute("IsDefault"));
		Assert.Equal(
			"WorkspaceIdTextBox_PreviewKeyDown",
			(string?)workspaceTextBox.Attribute("PreviewKeyDown"));
		Assert.Equal(
			"{StaticResource SecondaryButtonStyle}",
			(string?)workspaceButton.Attribute("Style"));
		Assert.Equal(
			"{DynamicResource AccentSurfaceBrush}",
			(string?)workspaceButton.Attribute("Background"));
		Assert.Equal(
			"{DynamicResource AccentBrush}",
			(string?)workspaceButton.Attribute("BorderBrush"));
		Assert.Contains("WorkspaceId = null", source, StringComparison.Ordinal);
		Assert.Contains(
			"private void WorkspaceIdTextBox_PreviewKeyDown",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"e.Key != System.Windows.Input.Key.Enter",
			source,
			StringComparison.Ordinal);
		Assert.Contains("e.Handled = true", source, StringComparison.Ordinal);
		Assert.Contains("TryContinueWithWorkspace();", source, StringComparison.Ordinal);
		Assert.Contains("Guid.TryParse", source, StringComparison.Ordinal);
		Assert.Contains("workspaceId != Guid.Empty", source, StringComparison.Ordinal);
		Assert.DoesNotContain("SaveAsync", source, StringComparison.Ordinal);
		Assert.DoesNotContain("ProviderAccountIdentity", source, StringComparison.Ordinal);
		Assert.Contains(
			"CodexWorkspacePromptWindow prompt = new()",
			coordinatorSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"prompt.WorkspaceId",
			coordinatorSource,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", true)]
	[InlineData("  aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa  ", true)]
	[InlineData("00000000-0000-0000-0000-000000000000", false)]
	[InlineData("not-a-workspace", false)]
	[InlineData(null, false)]
	public void CodexWorkspacePromptValidation_AcceptsOnlyNonEmptyUuid(
		string? value,
		bool expected)
	{
		Assert.Equal(
			expected,
			CodexWorkspacePromptWindow.TryNormalizeWorkspaceId(
				value,
				out Guid workspaceId));

		if (!expected)
		{
			Assert.Equal(Guid.Empty, workspaceId);
		}
	}

	[Fact]
	public void AccountEditorProviderNotice_IsAnnouncedAsLiveRegion()
	{
		XDocument window = LoadAppXaml("AccountEditorWindow.xaml");
		XElement providerNotice = Assert.Single(
			window.Descendants(Presentation + "TextBlock"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"ProviderNoticeTextBlock",
				StringComparison.Ordinal));
		XElement connectionHint = GetNamedElement(
			window,
			"TextBlock",
			"AccountConnectionHintTextBlock");
		string source = LoadAppSource("AccountEditorWindow.xaml.cs");
		string enabledStateHandler = GetMethodSource(
			source,
			"private void EnabledCheckBox_CheckStateChanged(",
			"private void UpdateProviderPresentation()");

		Assert.Equal(
			"Polite",
			(string?)providerNotice.Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal(
			"Polite",
			(string?)connectionHint.Attribute("AutomationProperties.LiveSetting"));
		Assert.Contains(
			"RaiseLiveRegionChanged(ProviderNoticeTextBlock);",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"announceProviderHint: false",
			enabledStateHandler,
			StringComparison.Ordinal);
		Assert.Contains(
			"announceConnectionHint: true",
			enabledStateHandler,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(true, false, true, true)]
	[InlineData(true, true, true, false)]
	[InlineData(true, true, false, false)]
	[InlineData(false, false, true, false)]
	public void AccountEditorProviderNoticeAnnouncement_OnlyOccursAfterLoadedHiddenToVisibleTransition(
		bool isLoaded,
		bool wasVisible,
		bool isVisible,
		bool expected)
	{
		Assert.Equal(
			expected,
			AccountEditorWindow.ShouldAnnounceProviderNotice(
				isLoaded,
				wasVisible,
				isVisible));
	}

	[Fact]
	public void CheckBoxChrome_HasLeftInsetToAvoidClipping()
	{
		XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
		XElement checkChrome = Assert.Single(
			controls.Descendants(Presentation + "Border"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"CheckChrome",
				StringComparison.Ordinal));
		XElement checkBoxGlyph = Assert.IsType<XElement>(checkChrome.Parent);

		Assert.Equal("1,0,0,0", (string?)checkBoxGlyph.Attribute("Margin"));
	}

	[Fact]
	public void AntigravityDisclosures_ExplainCliValidationWithoutOverpromising()
	{
		string accountEditorDisclosure =
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Antigravity)!;
		string setupDisclosure = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup",
			"SetupWindow.xaml"));

		Assert.Contains(
			"請先在這台電腦登入 Antigravity",
			accountEditorDisclosure,
			StringComparison.Ordinal);
		Assert.Contains(
			"支援官方唯讀 /usage 的 Antigravity 版本",
			accountEditorDisclosure,
			StringComparison.Ordinal);
		Assert.Contains(
			"不會另開登入畫面",
			accountEditorDisclosure,
			StringComparison.Ordinal);
		Assert.Contains(
			"確認這台電腦上的 Antigravity CLI 與既有連接",
			setupDisclosure,
			StringComparison.Ordinal);
		Assert.Contains(
			"新連接只接受支援官方唯讀 /usage 的版本",
			setupDisclosure,
			StringComparison.Ordinal);
		Assert.Contains(
			"舊版相容讀取只會沿用",
			setupDisclosure,
			StringComparison.Ordinal);

		string[] disclosures =
		{
			accountEditorDisclosure,
			setupDisclosure
		};

		Assert.All(disclosures, xaml =>
		{
			Assert.DoesNotContain("只會讀取", xaml, StringComparison.Ordinal);
			Assert.DoesNotContain("不會開啟對話", xaml, StringComparison.Ordinal);
			Assert.DoesNotContain("送出模型請求", xaml, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"關閉其他 Antigravity 視窗",
				xaml,
				StringComparison.Ordinal);
			Assert.DoesNotContain("讀取帳號識別", xaml, StringComparison.Ordinal);
			Assert.DoesNotContain("AGY", xaml, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void SetupFailure_DiagnosticsUseCollapsedAccessibleDisclosure()
	{
		XDocument window = LoadSetupXaml("SetupWindow.xaml");
		string source = LoadSetupSource("SetupWindow.xaml.cs");
		XElement failurePanel = GetNamedElement(
			window,
			"Border",
			"FailurePanel");
		XElement disclosureButton = GetNamedElement(
			window,
			"Button",
			"FailureDiagnosticDisclosureButton");
		XElement detailsPanel = GetNamedElement(
			window,
			"StackPanel",
			"FailureDiagnosticDetailsPanel");
		XElement diagnosticText = GetNamedElement(
			window,
			"TextBlock",
			"FailureDiagnosticTextBlock");
		XElement copyButton = GetNamedElement(
			window,
			"Button",
			"CopyDiagnosticButton");
		XElement retryButton = GetNamedElement(
			window,
			"Button",
			"RetryButton");

		Assert.Contains(failurePanel, disclosureButton.Ancestors());
		Assert.Equal("Collapsed", (string?)detailsPanel.Attribute("Visibility"));
		Assert.Equal(
			"顯示技術資訊",
			(string?)disclosureButton.Attribute("Content"));
		Assert.Equal(
			"Polite",
			(string?)disclosureButton.Attribute(
				"AutomationProperties.LiveSetting"));
		Assert.Equal(
			"{StaticResource SetupGhostButtonStyle}",
			(string?)disclosureButton.Attribute("Style"));
		Assert.Contains(detailsPanel, diagnosticText.Ancestors());
		Assert.Contains(detailsPanel, copyButton.Ancestors());
		Assert.DoesNotContain(detailsPanel, retryButton.Ancestors());
		Assert.Contains(
			"FailureDiagnosticDetailsPanel.Visibility = Visibility.Collapsed;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"FailureDiagnosticDetailsPanel.Visibility = showDetails",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"QueueLiveRegionAnnouncement(FailureDiagnosticDisclosureButton);",
			source,
			StringComparison.Ordinal);
	}

	private static void AssertSetter(
		XElement scope,
		string property,
		string value)
	{
		Assert.True(
			HasSetter(scope, property, value),
			$"Expected setter {property}={value} was not found.");
	}

	private static void AssertDoesNotUsePersistentFocusTrigger(XElement scope)
	{
		Assert.DoesNotContain(
			scope.Descendants(Presentation + "Trigger"),
			trigger => string.Equals(
				(string?)trigger.Attribute("Property"),
				"IsKeyboardFocused",
				StringComparison.Ordinal));
	}

	private static XElement GetKeyedElement(
		XDocument document,
		string elementName,
		string key)
	{
		return Assert.Single(
			document.Descendants(Presentation + elementName),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Key"),
				key,
				StringComparison.Ordinal));
	}

	private static XElement GetNamedElement(
		XDocument document,
		string elementName,
		string name)
	{
		return Assert.Single(
			document.Descendants(Presentation + elementName),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				name,
				StringComparison.Ordinal));
	}

	private static XElement GetMenuItemByClick(
		XDocument document,
		string clickHandler)
	{
		return Assert.Single(
			document.Descendants(Presentation + "MenuItem"),
			element => string.Equals(
				(string?)element.Attribute("Click"),
				clickHandler,
				StringComparison.Ordinal));
	}

	private static string GetMethodSource(
		string source,
		string startMarker,
		string endMarker)
	{
		int start = source.IndexOf(startMarker, StringComparison.Ordinal);
		Assert.True(start >= 0, $"Method marker not found: {startMarker}");
		int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
		Assert.True(end > start, $"Method end marker not found: {endMarker}");
		return source[start..end];
	}

	private static string GetColorMarkup(
		XDocument palette,
		string key)
	{
		XElement color = Assert.Single(
			palette.Root!.Elements(),
			element => (string?)element.Attribute(Xaml + "Key") == key);
		return (string?)color.Attribute("Member") ?? color.Value.Trim();
	}

	private static bool HasSetter(
		XElement scope,
		string property,
		string value)
	{
		return scope
			.Elements(Presentation + "Setter")
			.Any(setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					property,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					value,
					StringComparison.Ordinal));
	}

	private static XDocument LoadAppXaml(params string[] pathParts)
	{
		return XDocument.Load(GetAppXamlPath(pathParts));
	}

	private static string LoadAppSource(params string[] pathParts)
	{
		return File.ReadAllText(GetAppXamlPath(pathParts));
	}

	private static string GetAppXamlPath(params string[] pathParts)
	{
		return Path.Combine(
			new[]
			{
				RepositoryTestPaths.Root,
				"src",
				"AiUsageDashboard.App"
			}.Concat(pathParts).ToArray());
	}

	private static XDocument LoadSetupXaml(params string[] pathParts)
	{
		return XDocument.Load(GetSetupXamlPath(pathParts));
	}

	private static string LoadSetupSource(params string[] pathParts)
	{
		return File.ReadAllText(GetSetupXamlPath(pathParts));
	}

	private static string GetSetupXamlPath(params string[] pathParts)
	{
		return Path.Combine(
			new[]
			{
				RepositoryTestPaths.Root,
				"src",
				"AiUsageDashboard.Antigravity.Setup"
			}.Concat(pathParts).ToArray());
	}
}
