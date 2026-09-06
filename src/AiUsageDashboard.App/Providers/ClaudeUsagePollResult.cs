namespace AiUsageDashboard.App.Providers;

internal sealed record ClaudeUsagePollResult(
	string Output,
	DateTimeOffset ObservedAt,
	string? AccountIdentity = null,
	CliVersionEvidence? VersionEvidence = null,
	ClaudeSubscriptionContext? SubscriptionContext = null);
