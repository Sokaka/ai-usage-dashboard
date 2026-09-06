namespace AiUsageDashboard.App.Providers;

internal sealed record AntigravityReportedAccount(
	string Email,
	DateTimeOffset CapturedAtUtc,
	string? PlanTier = null);

internal enum AntigravityReportedAccountObservationStatus
{
	Available,
	Unavailable
}

internal sealed record AntigravityReportedAccountObservation(
	long Generation,
	AntigravityReportedAccount? Account,
	bool HasFreshStatusLineInvocation = false,
	AntigravityReportedAccountObservationStatus Status =
		AntigravityReportedAccountObservationStatus.Available);

internal interface IAntigravityReportedAccountSource
{
	ValueTask<AntigravityReportedAccountObservation> ReadAsync(
		CancellationToken cancellationToken = default);

	ValueTask<bool> RemoveOwnedAsync(
		CancellationToken cancellationToken = default);
}
