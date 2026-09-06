using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Providers;

public interface IUsageProvider
{
	ProviderKind Provider { get; }

	TimeSpan MinimumRefreshInterval { get; }

	Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken);
}
