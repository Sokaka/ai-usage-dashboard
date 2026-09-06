using System.Globalization;

namespace AiUsageDashboard.Core.Claude;

public static class ClaudeStatusLineFormatter
{
	public static string Format(ClaudeStatusLineCapture capture)
	{
		ArgumentNullException.ThrowIfNull(capture);

		List<string> parts = new(2);
		AddWindow(parts, "5h", capture.FiveHour);
		AddWindow(parts, "7d", capture.SevenDay);

		if (parts.Count > 0)
		{
			return $"[Claude] {string.Join(" | ", parts)}";
		}

		return capture.HasCurrentUsage
			? "[Claude] subscription usage unavailable"
			: "[Claude] waiting for first response";
	}

	private static void AddWindow(
		ICollection<string> parts,
		string label,
		ClaudeRateLimitWindow? window)
	{
		if (!ClaudeStatusLineCaptureValidator.IsValidWindow(window))
		{
			return;
		}

		parts.Add(string.Create(
			CultureInfo.InvariantCulture,
			$"{label}: {window!.UsedPercentage:0.##}%"));
	}
}
