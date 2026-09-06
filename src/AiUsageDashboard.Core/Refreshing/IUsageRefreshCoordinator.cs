using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Refreshing;

public interface IUsageRefreshCoordinator
{
	TimeSpan? GetRemainingCooldown(AccountProfile account);

	Task<UsageSnapshot> RefreshAsync(
		AccountProfile account,
		CancellationToken cancellationToken = default);

	bool SeedLastKnownGood(UsageSnapshot snapshot);

	void Invalidate(Guid accountId);
}
