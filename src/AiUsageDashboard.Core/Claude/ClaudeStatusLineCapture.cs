namespace AiUsageDashboard.Core.Claude;

public sealed record ClaudeStatusLineCapture(
	int SchemaVersion,
	DateTimeOffset CapturedAt,
	string? ClaudeVersion,
	ClaudeStatusLineModel? Model,
	bool HasCurrentUsage,
	ClaudeRateLimitWindow? FiveHour,
	ClaudeRateLimitWindow? SevenDay)
{
	public const int CurrentSchemaVersion = 1;
}
