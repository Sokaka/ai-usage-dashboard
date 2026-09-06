using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Persistence;

public sealed record AccountProfileLoadResult(
	IReadOnlyList<AccountProfile> Accounts,
	AccountProfileLoadStatus Status,
	bool CanSave,
	string? Message = null,
	string? RecoveryPath = null);
