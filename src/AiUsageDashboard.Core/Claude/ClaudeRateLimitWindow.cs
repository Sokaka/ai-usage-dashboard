using System.Text.Json.Serialization;

namespace AiUsageDashboard.Core.Claude;

public sealed record ClaudeRateLimitWindow(
	[property: JsonRequired] double UsedPercentage,
	DateTimeOffset? ResetsAt);
