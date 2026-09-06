namespace AiUsageDashboard.Core.Models;

public sealed record AccountProfile(
	Guid Id,
	ProviderKind Provider,
	string DisplayName,
	bool IsEnabled = true,
	string? ProviderAccountIdentity = null,
	bool HasAcceptedClaudeQuotaRisk = false,
	bool ShowSubscriptionContext = false);
