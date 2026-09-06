namespace AiUsageDashboard.Core.Models;

public sealed record UsageMetric(
	string Key,
	string Label,
	double? UsedPercent,
	string DisplayValue,
	DateTimeOffset? ResetsAt = null,
	string? ResetDisplayValue = null);
