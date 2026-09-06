using System.Drawing;

namespace AiUsageDashboard.App;

internal enum FloatingWidgetCorner
{
	TopLeft,
	TopRight,
	BottomRight,
	BottomLeft
}

internal static class FloatingWidgetPlacement
{
	internal static (double Width, double Height) GetExpandedSize(
		Size workingAreaSize,
		int margin,
		double dpiScaleX,
		double dpiScaleY,
		double defaultWidth,
		double defaultHeight)
	{
		if (margin < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(margin));
		}

		if ((dpiScaleX <= 0) || (dpiScaleY <= 0))
		{
			throw new ArgumentOutOfRangeException(
				nameof(dpiScaleX),
				"DPI scale 必須大於零。");
		}

		double availableWidth = Math.Max(
			1,
			(workingAreaSize.Width - (margin * 2d)) / dpiScaleX);
		double availableHeight = Math.Max(
			1,
			(workingAreaSize.Height - (margin * 2d)) / dpiScaleY);
		return (
			Math.Min(defaultWidth, availableWidth),
			availableHeight);
	}

	internal static FloatingWidgetCorner GetCornerOnHorizontalSide(
		FloatingWidgetCorner currentCorner,
		bool isLeft)
	{
		bool isTop = currentCorner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.TopRight;

		if (isTop)
		{
			return isLeft
				? FloatingWidgetCorner.TopLeft
				: FloatingWidgetCorner.TopRight;
		}

		return isLeft
			? FloatingWidgetCorner.BottomLeft
			: FloatingWidgetCorner.BottomRight;
	}

	internal static FloatingWidgetCorner GetNearestCorner(
		Rectangle windowBounds,
		Rectangle workingArea)
	{
		double windowCenterX = windowBounds.Left + (windowBounds.Width / 2d);
		double windowCenterY = windowBounds.Top + (windowBounds.Height / 2d);
		double workingAreaCenterX = workingArea.Left + (workingArea.Width / 2d);
		double workingAreaCenterY = workingArea.Top + (workingArea.Height / 2d);
		bool isLeft = windowCenterX < workingAreaCenterX;
		bool isTop = windowCenterY < workingAreaCenterY;

		if (isTop)
		{
			return isLeft
				? FloatingWidgetCorner.TopLeft
				: FloatingWidgetCorner.TopRight;
		}

		return isLeft
			? FloatingWidgetCorner.BottomLeft
			: FloatingWidgetCorner.BottomRight;
	}

	internal static Point GetTopLeft(
		Size windowSize,
		Rectangle workingArea,
		FloatingWidgetCorner corner,
		int margin)
	{
		if (margin < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(margin),
				"吸附邊距不可小於零。");
		}

		int left = corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.BottomLeft
			? workingArea.Left + margin
			: workingArea.Right - windowSize.Width - margin;
		int top = corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.TopRight
			? workingArea.Top + margin
			: workingArea.Bottom - windowSize.Height - margin;
		int maximumLeft = Math.Max(
			workingArea.Left,
			workingArea.Right - windowSize.Width);
		int maximumTop = Math.Max(
			workingArea.Top,
			workingArea.Bottom - windowSize.Height);

		return new Point(
			Math.Clamp(left, workingArea.Left, maximumLeft),
			Math.Clamp(top, workingArea.Top, maximumTop));
	}
}
