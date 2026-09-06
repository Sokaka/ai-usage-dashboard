namespace AiUsageDashboard.App;

internal static class WindowWorkAreaLayout
{
	private const double WorkAreaPaddingDips = 16;

	internal static double CalculateMaxWidth(
		double preferredMinWidth,
		int physicalWorkAreaWidth,
		double dpiScaleX)
	{
		if (!double.IsFinite(preferredMinWidth) ||
			(preferredMinWidth < 0))
		{
			throw new ArgumentOutOfRangeException(nameof(preferredMinWidth));
		}

		if ((physicalWorkAreaWidth <= 0) ||
			!double.IsFinite(dpiScaleX) ||
			(dpiScaleX <= 0))
		{
			return preferredMinWidth;
		}

		double availableWidth =
			(physicalWorkAreaWidth / dpiScaleX) -
			WorkAreaPaddingDips;
		return Math.Max(1, Math.Floor(availableWidth));
	}

	internal static double CalculateMaxHeight(
		double preferredMinHeight,
		int physicalWorkAreaHeight,
		double dpiScaleY)
	{
		if (!double.IsFinite(preferredMinHeight) || (preferredMinHeight < 0))
		{
			throw new ArgumentOutOfRangeException(nameof(preferredMinHeight));
		}

		if ((physicalWorkAreaHeight <= 0) ||
			!double.IsFinite(dpiScaleY) ||
			(dpiScaleY <= 0))
		{
			return preferredMinHeight;
		}

		double availableHeight =
			(physicalWorkAreaHeight / dpiScaleY) -
			WorkAreaPaddingDips;
		return Math.Max(1, Math.Floor(availableHeight));
	}

	internal static bool RequiresRefreshForWindowMessage(int message)
	{
		return message is 0x001A or 0x007E or 0x02E0;
	}
}

internal static class LiveRegionAnnouncementPolicy
{
	private static readonly TimeSpan RecentStatusWindow =
		TimeSpan.FromSeconds(2);

	internal static bool ShouldAnnounceInlineStatus(
		string message,
		string? recentViewModelStatus,
		DateTimeOffset recentViewModelStatusAt,
		DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return false;
		}

		if (!string.Equals(
			message,
			recentViewModelStatus,
			StringComparison.Ordinal))
		{
			return true;
		}

		TimeSpan elapsed = now - recentViewModelStatusAt;
		return (elapsed < TimeSpan.Zero) ||
			(elapsed > RecentStatusWindow);
	}

	internal static bool ShouldReplaceInlineStatus(
		bool hasPersistentInlineStatus)
	{
		return !hasPersistentInlineStatus;
	}
}
