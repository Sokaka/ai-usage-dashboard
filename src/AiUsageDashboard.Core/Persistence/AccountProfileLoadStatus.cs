namespace AiUsageDashboard.Core.Persistence;

public enum AccountProfileLoadStatus
{
	Missing,
	Loaded,
	RecoveredCorruptFile,
	UnsupportedVersion,
	Unavailable
}
