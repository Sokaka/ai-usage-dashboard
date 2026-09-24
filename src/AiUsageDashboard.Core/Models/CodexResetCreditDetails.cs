namespace AiUsageDashboard.Core.Models;

public sealed record CodexResetCreditDetails(
	long AvailableCount,
	IReadOnlyList<CodexResetCredit>? Credits,
	bool IsComplete,
	bool HasCountConflict = false);
