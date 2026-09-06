using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.Tests;

public sealed class UnsupportedUsageProviderTests
{
	[Fact]
	public async Task GetUsageAsync_ReturnsExplicitUnsupportedSnapshot()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"GitHub Copilot");
		UnsupportedUsageProvider provider = new(ProviderKind.Copilot);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Unsupported, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Contains(
			"尚未支援",
			snapshot.Error,
			StringComparison.Ordinal);
	}
}
