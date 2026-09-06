namespace AiUsageDashboard.Core.Models;

public sealed record UsageSnapshot(
	AccountProfile Account,
	IReadOnlyList<UsageMetric> Metrics,
	SourceTrust SourceTrust,
	SnapshotStatus Status,
	DateTimeOffset FetchedAt,
	DateTimeOffset? ObservedAt = null,
	DateTimeOffset? StaleAfter = null,
	string? Error = null,
	string? ProviderAccountIdentity = null,
	UsageRecoveryAction RecoveryAction = UsageRecoveryAction.None,
	DateTimeOffset? RetryNotBefore = null,
	string? ProviderAccountDisplayIdentity = null,
	string? SubscriptionScopeDisplayName = null,
	string? PlanTier = null,
	SubscriptionVerificationState SubscriptionVerificationState =
		SubscriptionVerificationState.Unverified)
{
	public bool IsStale => IsStaleAt(DateTimeOffset.UtcNow);

	public bool IsStaleAt(DateTimeOffset now)
	{
		return (Status == SnapshotStatus.Stale) ||
			((StaleAfter is not null) && (now >= StaleAfter.Value));
	}
}
