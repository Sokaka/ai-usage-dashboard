using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.ViewModels;

public readonly record struct CompactRefreshStatus(
	string Text,
	bool IsTransient)
{
	internal static CompactRefreshStatus Hidden { get; } =
		new(string.Empty, false);
}

internal static class CompactRefreshStatusPolicy
{
	internal static CompactRefreshStatus CreateResult(
		IReadOnlyCollection<UsageSnapshot> snapshots,
		int coolingDownCount,
		DateTimeOffset completedAt)
	{
		ArgumentNullException.ThrowIfNull(snapshots);

		if (snapshots.Count == 0)
		{
			return CompactRefreshStatus.Hidden;
		}

		int actionRequiredCount = snapshots.Count(RequiresUserAction);

		if (actionRequiredCount > 0)
		{
			return new CompactRefreshStatus(
				$"{actionRequiredCount} 個帳號需處理",
				false);
		}

		int retryCount = snapshots.Count(
			snapshot => snapshot.RecoveryAction == UsageRecoveryAction.Retry);

		if ((retryCount > 0) && (coolingDownCount > 0))
		{
			return new CompactRefreshStatus("部分帳號稍後自動再試", false);
		}

		if (retryCount > 0)
		{
			return new CompactRefreshStatus(
				$"{retryCount} 個帳號稍後自動再試",
				false);
		}

		if (coolingDownCount > 0)
		{
			return new CompactRefreshStatus(
				$"{coolingDownCount} 個帳號稍後更新",
				false);
		}

		int attentionCount = snapshots.Count(
			snapshot => snapshot.Status != SnapshotStatus.Ready);

		if (attentionCount > 0)
		{
			return new CompactRefreshStatus(
				$"{attentionCount} 個帳號需留意",
				false);
		}

		return new CompactRefreshStatus(
			$"已更新 {completedAt:HH:mm}",
			true);
	}

	private static bool RequiresUserAction(UsageSnapshot snapshot)
	{
		return snapshot.RecoveryAction switch
		{
			UsageRecoveryAction.ConnectAccount => true,
			UsageRecoveryAction.SwitchAccount => true,
			UsageRecoveryAction.ConfirmSubscription => true,
			UsageRecoveryAction.RestartApplication => true,
			UsageRecoveryAction.InstallOrUpdate => true,
			UsageRecoveryAction.UpdateApplication => true,
			UsageRecoveryAction.ReconfigureUsageSource => true,
			UsageRecoveryAction.RevalidateUsage => true,
			_ => (snapshot.Status == SnapshotStatus.NotConfigured) &&
				(snapshot.RecoveryAction == UsageRecoveryAction.None)
		};
	}
}
