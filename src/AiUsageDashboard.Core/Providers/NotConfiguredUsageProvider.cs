using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Providers;

public sealed class NotConfiguredUsageProvider : IUsageProvider
{
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider { get; }

	public TimeSpan MinimumRefreshInterval => TimeSpan.FromMinutes(1);

	public NotConfiguredUsageProvider(
		ProviderKind provider,
		TimeProvider? timeProvider = null)
	{
		Provider = provider;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);
		cancellationToken.ThrowIfCancellationRequested();
		UsageSnapshot snapshot = new(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			_timeProvider.GetUtcNow(),
			Error: "此服務尚未連接。");

		return Task.FromResult(snapshot);
	}
}
