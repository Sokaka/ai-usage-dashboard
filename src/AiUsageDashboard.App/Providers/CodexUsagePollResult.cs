namespace AiUsageDashboard.App.Providers;

internal sealed record CodexRateLimitWindow(
	int UsedPercent,
	long? WindowDurationMinutes,
	DateTimeOffset? ResetsAt);

internal sealed record CodexRateLimitBucket(
	string LimitId,
	string? LimitName,
	CodexRateLimitWindow? Primary,
	CodexRateLimitWindow? Secondary);

internal sealed record CodexUsagePollResult(
	string PlanType,
	IReadOnlyList<CodexRateLimitBucket> RateLimits,
	long? AvailableResetCredits,
	DateTimeOffset ObservedAt,
	string? AccountIdentity = null,
	CliVersionEvidence? VersionEvidence = null,
	string? PublicBindingIdentity = null,
	Guid WorkspaceId = default);
