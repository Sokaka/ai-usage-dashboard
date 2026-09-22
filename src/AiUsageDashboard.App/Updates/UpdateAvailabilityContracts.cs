namespace AiUsageDashboard.App.Updates;

internal enum UpdateAvailabilityStatus
{
	UpToDate,
	UpdateAvailable,
	UnknownCurrentVersion
}

internal sealed record UpdateAvailabilityCheckResult(
	UpdateAvailabilityStatus Status,
	string AvailableVersion,
	long ReleaseSequence);

internal sealed record UpdateAvailabilityCheckRequest(
	AppInstallationContext InstallationContext,
	string? CurrentVersion,
	long? HighestObservedReleaseSequence);

internal interface IUpdateAvailabilityChecker
{
	Task<UpdateAvailabilityCheckResult> CheckAsync(
		UpdateAvailabilityCheckRequest request,
		CancellationToken cancellationToken);
}
