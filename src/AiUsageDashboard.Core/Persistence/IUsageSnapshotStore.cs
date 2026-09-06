using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Persistence;

public interface IUsageSnapshotStore
{
	Task<UsageSnapshot?> LoadAsync(
		AccountProfile account,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		UsageSnapshot snapshot,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);
}
