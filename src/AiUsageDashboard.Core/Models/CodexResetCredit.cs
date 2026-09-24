namespace AiUsageDashboard.Core.Models;

public sealed record CodexResetCredit(
	string Status,
	string? ResetType,
	DateTimeOffset? GrantedAt,
	DateTimeOffset? ExpiresAt,
	string? Title,
	string? Description,
	bool HasExpiresAtField = false)
{
	public const string AvailableStatus = "available";
}
