using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class CompactRefreshStatusPolicyTests
{
	[Fact]
	public void CreateResult_WithNoSnapshots_HidesStatus()
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			Array.Empty<UsageSnapshot>(),
			0,
			DateTimeOffset.Now);

		Assert.Equal(CompactRefreshStatus.Hidden, status);
	}

	[Fact]
	public void CreateResult_WhenAllAccountsAreReady_ShowsTransientUpdateTime()
	{
		DateTimeOffset completedAt = new(
			2026,
			8,
			3,
			14,
			5,
			0,
			TimeSpan.FromHours(8));

		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[] { CreateSnapshot(SnapshotStatus.Ready) },
			0,
			completedAt);

		Assert.Equal("已更新 14:05", status.Text);
		Assert.True(status.IsTransient);
	}

	[Fact]
	public void CreateResult_WhenUserActionAndRetryAreMixed_PrioritizesUserAction()
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[]
			{
				CreateSnapshot(SnapshotStatus.NotConfigured),
				CreateSnapshot(
					SnapshotStatus.Error,
					UsageRecoveryAction.Retry)
			},
			0,
			DateTimeOffset.Now);

		Assert.Equal("1 個帳號需處理", status.Text);
		Assert.False(status.IsTransient);
	}

	[Fact]
	public void CreateResult_WhenRetryAndCooldownAreMixed_AvoidsDoubleCounting()
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[]
			{
				CreateSnapshot(
					SnapshotStatus.Error,
					UsageRecoveryAction.Retry),
				CreateSnapshot(SnapshotStatus.Ready)
			},
			2,
			DateTimeOffset.Now);

		Assert.Equal("部分帳號稍後自動再試", status.Text);
		Assert.False(status.IsTransient);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.RevalidateUsage)]
	[InlineData(UsageRecoveryAction.ConfirmSubscription)]
	public void CreateResult_WhenClaudeUsageNeedsConfirmation_ShowsActionRequired(
		UsageRecoveryAction recoveryAction)
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[]
			{
				CreateSnapshot(
					SnapshotStatus.Error,
					recoveryAction)
			},
			0,
			DateTimeOffset.Now);

		Assert.Equal("1 個帳號需處理", status.Text);
		Assert.False(status.IsTransient);
	}

	[Fact]
	public void CreateResult_WhenOnlyCooldownApplies_ShowsDeferredCount()
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[] { CreateSnapshot(SnapshotStatus.Ready) },
			2,
			DateTimeOffset.Now);

		Assert.Equal("2 個帳號稍後更新", status.Text);
		Assert.False(status.IsTransient);
	}

	[Fact]
	public void CreateResult_WithUnclassifiedNonReadyStatuses_ShowsAttentionCount()
	{
		CompactRefreshStatus status = CompactRefreshStatusPolicy.CreateResult(
			new[]
			{
				CreateSnapshot(SnapshotStatus.Stale),
				CreateSnapshot(SnapshotStatus.Unsupported)
			},
			0,
			DateTimeOffset.Now);

		Assert.Equal("2 個帳號需留意", status.Text);
		Assert.False(status.IsTransient);
	}

	private static UsageSnapshot CreateSnapshot(
		SnapshotStatus status,
		UsageRecoveryAction recoveryAction = UsageRecoveryAction.None)
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"測試帳號");
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			DateTimeOffset.UtcNow,
			RecoveryAction: recoveryAction);
	}
}
