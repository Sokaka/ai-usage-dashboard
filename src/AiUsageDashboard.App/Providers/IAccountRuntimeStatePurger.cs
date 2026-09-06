using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal interface IAccountRuntimeStatePurger
{
	Task PurgeAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);

	Task ResetUsageSafetyStateAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);
}
