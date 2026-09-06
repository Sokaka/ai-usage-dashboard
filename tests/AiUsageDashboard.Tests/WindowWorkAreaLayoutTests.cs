using AiUsageDashboard.App;

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

}
