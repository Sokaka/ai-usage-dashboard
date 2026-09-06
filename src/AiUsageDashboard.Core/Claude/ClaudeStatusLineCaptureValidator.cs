namespace AiUsageDashboard.Core.Claude;

internal static class ClaudeStatusLineCaptureValidator
{
	internal static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

	internal static void Validate(
		ClaudeStatusLineCapture capture,
		DateTimeOffset utcNow)
	{
		ArgumentNullException.ThrowIfNull(capture);

		if (capture.SchemaVersion != ClaudeStatusLineCapture.CurrentSchemaVersion)
		{
			throw new InvalidDataException(
				$"Unsupported Claude status line capture schema version: {capture.SchemaVersion}.");
		}

		if (capture.CapturedAt == default)
		{
			throw new InvalidDataException("Claude status line capture time is required.");
		}

		if ((capture.CapturedAt - utcNow) > MaximumFutureClockSkew)
		{
			throw new InvalidDataException(
				"Claude status line capture time is too far in the future.");
		}

		if ((capture.Model is not null) &&
			string.IsNullOrWhiteSpace(capture.Model.Id) &&
			string.IsNullOrWhiteSpace(capture.Model.DisplayName))
		{
			throw new InvalidDataException("Claude model identity cannot be empty.");
		}

		ValidateWindow(capture.FiveHour);
		ValidateWindow(capture.SevenDay);
	}

	internal static bool IsValidWindow(ClaudeRateLimitWindow? window)
	{
		return (window is not null) &&
			double.IsFinite(window.UsedPercentage) &&
			(window.UsedPercentage >= 0) &&
			(window.UsedPercentage <= 100) &&
			((window.ResetsAt is null) || (window.ResetsAt != default));
	}

	private static void ValidateWindow(ClaudeRateLimitWindow? window)
	{
		if ((window is not null) && !IsValidWindow(window))
		{
			throw new InvalidDataException("Claude rate limit window is invalid.");
		}
	}
}
