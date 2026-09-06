using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Providers;

public sealed class UnsupportedUsageProvider : IUsageProvider
{
	public UnsupportedUsageProvider(ProviderKind provider)
	{
		Provider = provider;
	}

	public ProviderKind Provider { get; }

	public TimeSpan MinimumRefreshInterval => TimeSpan.FromMinutes(1);

	public Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		UsageSnapshot snapshot = new(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Unsupported,
			DateTimeOffset.UtcNow,
			Error: "此服務目前尚未支援用量顯示。");

		return Task.FromResult(snapshot);
	}
}
