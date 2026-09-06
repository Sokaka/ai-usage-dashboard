using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Persistence;

public interface IAccountProfileStore
{
	Task<AccountProfileLoadResult> LoadAsync(
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		IReadOnlyList<AccountProfile> accounts,
		CancellationToken cancellationToken = default);
}
