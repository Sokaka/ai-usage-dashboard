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
		int left = corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.BottomLeft
			? workingArea.Left + margin
			: workingArea.Right - windowSize.Width - margin;
		int top = corner is FloatingWidgetCorner.TopLeft or
			FloatingWidgetCorner.TopRight
			? workingArea.Top + margin
			: workingArea.Bottom - windowSize.Height - margin;
		return ClampTopLeft(
			new Point(left, top),
			windowSize,
			workingArea,
			margin);
	}

	internal static Point ClampTopLeft(
		Point desiredTopLeft,
		Size windowSize,
		Rectangle workingArea,
		int margin)
	{
		(int minX, int maxX, int minY, int maxY) =
			GetAllowedTopLeftRange(windowSize, workingArea, margin);
		return new Point(
			Math.Clamp(desiredTopLeft.X, minX, maxX),
			Math.Clamp(desiredTopLeft.Y, minY, maxY));
	}

	internal static Point GetTopLeftFromRatios(
		Size windowSize,
		Rectangle workingArea,
		int margin,
		double xRatio,
		double yRatio)
	{
		ValidateRatio(xRatio, nameof(xRatio));
		ValidateRatio(yRatio, nameof(yRatio));
		(int minX, int maxX, int minY, int maxY) =
			GetAllowedTopLeftRange(windowSize, workingArea, margin);
		return new Point(
			minX + (int)Math.Round((maxX - minX) * xRatio),
			minY + (int)Math.Round((maxY - minY) * yRatio));
	}

	internal static (double XRatio, double YRatio) GetPositionRatios(
		Point topLeft,
		Size windowSize,
		Rectangle workingArea,
		int margin)
	{
		(int minX, int maxX, int minY, int maxY) =
			GetAllowedTopLeftRange(windowSize, workingArea, margin);
		Point clampedTopLeft = new(
			Math.Clamp(topLeft.X, minX, maxX),
			Math.Clamp(topLeft.Y, minY, maxY));
		return (
			maxX == minX ? 0 : (clampedTopLeft.X - minX) / (double)(maxX - minX),
			maxY == minY ? 0 : (clampedTopLeft.Y - minY) / (double)(maxY - minY));
	}

	private static (int MinX, int MaxX, int MinY, int MaxY) GetAllowedTopLeftRange(
		Size windowSize,
		Rectangle workingArea,
		int margin)
	{
		if (margin < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(margin),
				"吸附邊距不可小於零。");
		}

		int minX = workingArea.Left + margin;
		int maxX = workingArea.Right - windowSize.Width - margin;
		int minY = workingArea.Top + margin;
		int maxY = workingArea.Bottom - windowSize.Height - margin;
		if (maxX < minX)
		{
			minX = workingArea.Left;
			maxX = Math.Max(minX, workingArea.Right - windowSize.Width);
		}

		if (maxY < minY)
		{
			minY = workingArea.Top;
			maxY = Math.Max(minY, workingArea.Bottom - windowSize.Height);
		}

		return (minX, maxX, minY, maxY);
	}

	private static void ValidateRatio(double ratio, string parameterName)
	{
		if (!double.IsFinite(ratio) || (ratio < 0) || (ratio > 1))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}
}
