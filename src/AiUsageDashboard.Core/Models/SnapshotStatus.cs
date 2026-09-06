namespace AiUsageDashboard.Core.Models;

public enum SnapshotStatus
{
	Ready,
	Refreshing,
	Stale,
	NotConfigured,
	Unsupported,
	Error
}
