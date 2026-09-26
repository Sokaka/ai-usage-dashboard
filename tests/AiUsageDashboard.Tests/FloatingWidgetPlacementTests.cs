using System.Drawing;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class FloatingWidgetPlacementTests
{
	[Theory]
	[InlineData(1920, 1040, 18, 1, 390, 1004)]
	[InlineData(800, 600, 18, 1, 390, 564)]
	[InlineData(3840, 2160, 36, 2, 390, 1044)]
	[InlineData(2560, 1400, 27, 1.5, 390, 897.3333333333334)]
	public void GetExpandedSize_UsesFullAvailableWorkAreaHeight(
		int workingWidth,
		int workingHeight,
		int margin,
		double dpiScale,
		double expectedWidth,
		double expectedHeight)
	{
		(double width, double height) = FloatingWidgetPlacement.GetExpandedSize(
			new Size(workingWidth, workingHeight),
			margin,
			dpiScale,
			dpiScale,
			defaultWidth: 390,
			defaultHeight: 720);

		Assert.Equal(expectedWidth, width);
		Assert.Equal(expectedHeight, height);
	}

	[Theory]
	[InlineData((int)FloatingWidgetCorner.TopLeft, true, (int)FloatingWidgetCorner.TopLeft)]
	[InlineData((int)FloatingWidgetCorner.TopLeft, false, (int)FloatingWidgetCorner.TopRight)]
	[InlineData((int)FloatingWidgetCorner.TopRight, true, (int)FloatingWidgetCorner.TopLeft)]
	[InlineData((int)FloatingWidgetCorner.TopRight, false, (int)FloatingWidgetCorner.TopRight)]
	[InlineData((int)FloatingWidgetCorner.BottomRight, true, (int)FloatingWidgetCorner.BottomLeft)]
	[InlineData((int)FloatingWidgetCorner.BottomRight, false, (int)FloatingWidgetCorner.BottomRight)]
	[InlineData((int)FloatingWidgetCorner.BottomLeft, true, (int)FloatingWidgetCorner.BottomLeft)]
	[InlineData((int)FloatingWidgetCorner.BottomLeft, false, (int)FloatingWidgetCorner.BottomRight)]
	public void GetCornerOnHorizontalSide_PreservesVerticalAnchor(
		int currentCorner,
		bool isLeft,
		int expected)
	{
		FloatingWidgetCorner actual =
			FloatingWidgetPlacement.GetCornerOnHorizontalSide(
				(FloatingWidgetCorner)currentCorner,
				isLeft);

		Assert.Equal((FloatingWidgetCorner)expected, actual);
	}

	[Theory]
	[InlineData(120, 90, (int)FloatingWidgetCorner.TopLeft)]
	[InlineData(1500, 90, (int)FloatingWidgetCorner.TopRight)]
	[InlineData(1500, 820, (int)FloatingWidgetCorner.BottomRight)]
	[InlineData(120, 820, (int)FloatingWidgetCorner.BottomLeft)]
	public void GetNearestCorner_ReturnsCornerInSameQuadrant(
		int left,
		int top,
		int expected)
	{
		Rectangle workingArea = new(0, 0, 1920, 1040);
		Rectangle windowBounds = new(left, top, 320, 180);

		FloatingWidgetCorner actual = FloatingWidgetPlacement.GetNearestCorner(
			windowBounds,
			workingArea);

		Assert.Equal((FloatingWidgetCorner)expected, actual);
	}

	[Fact]
	public void GetNearestCorner_SupportsMonitorsWithNegativeCoordinates()
	{
		Rectangle workingArea = new(-1920, 0, 1920, 1040);
		Rectangle windowBounds = new(-1860, 760, 320, 180);

		FloatingWidgetCorner actual = FloatingWidgetPlacement.GetNearestCorner(
			windowBounds,
			workingArea);

		Assert.Equal(FloatingWidgetCorner.BottomLeft, actual);
	}

	[Theory]
	[InlineData((int)FloatingWidgetCorner.TopLeft, -1902, 18)]
	[InlineData((int)FloatingWidgetCorner.TopRight, -338, 18)]
	[InlineData((int)FloatingWidgetCorner.BottomRight, -338, 842)]
	[InlineData((int)FloatingWidgetCorner.BottomLeft, -1902, 842)]
	public void GetTopLeft_AppliesMarginInsideWorkingArea(
		int corner,
		int expectedX,
		int expectedY)
	{
		Rectangle workingArea = new(-1920, 0, 1920, 1040);
		Size windowSize = new(320, 180);

		Point actual = FloatingWidgetPlacement.GetTopLeft(
			windowSize,
			workingArea,
			(FloatingWidgetCorner)corner,
			18);

		Assert.Equal(new Point(expectedX, expectedY), actual);
	}

	[Theory]
	[InlineData((int)FloatingWidgetCorner.TopLeft, 118, 218, 118, 218)]
	[InlineData((int)FloatingWidgetCorner.TopRight, 762, 218, 762, 218)]
	[InlineData((int)FloatingWidgetCorner.BottomRight, 762, 482, 762, 782)]
	[InlineData((int)FloatingWidgetCorner.BottomLeft, 118, 482, 118, 782)]
	public void GetTopLeft_WhenHeightShrinks_PreservesAnchoredEdges(
		int corner,
		int expectedTallX,
		int expectedTallY,
		int expectedShortX,
		int expectedShortY)
	{
		Rectangle workingArea = new(100, 200, 1000, 800);
		FloatingWidgetCorner anchor = (FloatingWidgetCorner)corner;

		Point tall = FloatingWidgetPlacement.GetTopLeft(
			new Size(320, 500),
			workingArea,
			anchor,
			18);
		Point @short = FloatingWidgetPlacement.GetTopLeft(
			new Size(320, 200),
			workingArea,
			anchor,
			18);

		Assert.Equal(new Point(expectedTallX, expectedTallY), tall);
		Assert.Equal(new Point(expectedShortX, expectedShortY), @short);
	}

	[Fact]
	public void GetTopLeft_ClampsOversizedWindowToWorkingAreaOrigin()
	{
		Rectangle workingArea = new(100, 200, 300, 240);
		Size windowSize = new(500, 400);

		Point actual = FloatingWidgetPlacement.GetTopLeft(
			windowSize,
			workingArea,
			FloatingWidgetCorner.BottomRight,
			18);

		Assert.Equal(new Point(100, 200), actual);
	}

	[Theory]
	[InlineData(-2000, -100, -1902, 18)]
	[InlineData(0, 1100, -74, 966)]
	[InlineData(-800, 500, -800, 500)]
	public void ClampTopLeft_KeepsCollapsedIconInsideInsetWorkArea(
		int desiredX,
		int desiredY,
		int expectedX,
		int expectedY)
	{
		Point actual = FloatingWidgetPlacement.ClampTopLeft(
			new Point(desiredX, desiredY),
			new Size(56, 56),
			new Rectangle(-1920, 0, 1920, 1040),
			18);

		Assert.Equal(new Point(expectedX, expectedY), actual);
	}

	[Fact]
	public void PositionRatios_PreserveRelativeLocationWhenWorkAreaChanges()
	{
		Rectangle originalArea = new(-1920, 0, 1920, 1040);
		Size iconSize = new(56, 56);
		Point original = new(-988, 492);
		(double xRatio, double yRatio) =
			FloatingWidgetPlacement.GetPositionRatios(
				original,
				iconSize,
				originalArea,
				18);

		Point restored = FloatingWidgetPlacement.GetTopLeftFromRatios(
			iconSize,
			new Rectangle(100, 200, 1280, 800),
			18,
			xRatio,
			yRatio);

		Assert.Equal(original, FloatingWidgetPlacement.GetTopLeftFromRatios(
			iconSize,
			originalArea,
			18,
			xRatio,
			yRatio));
		Assert.Equal(new Point(712, 572), restored);
	}

	[Fact]
	public void ClampTopLeft_UsesAvailableWorkAreaWhenInsetCannotFit()
	{
		Point actual = FloatingWidgetPlacement.ClampTopLeft(
			new Point(1000, 1000),
			new Size(56, 56),
			new Rectangle(100, 200, 70, 70),
			18);

		Assert.Equal(new Point(114, 214), actual);
	}
}
