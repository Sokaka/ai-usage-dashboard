namespace AiUsageDashboard.UpdaterProcessFixture;

internal sealed record ParentObservation(
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string ExecutablePath,
	int ChildProcessId,
	long ChildProcessStartTimeUtcTicks,
	int ChildExitCode);

internal sealed record DelegatedChildObservation(
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string ExecutablePath,
	int ParentProcessId,
	long ParentProcessStartTimeUtcTicks,
	string ParentExecutablePath,
	bool ShouldRefreshUpdater,
	bool ShouldRegisterInstalledApp,
	bool ShouldPromptForLicenses,
	bool WasPromotionScheduled,
	int PromotionParentProcessId,
	long PromotionParentProcessStartTimeUtcTicks,
	string ExpectedCanonicalSha256,
	string SourceSha256);

internal sealed record PromoterObservation(
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string ExecutablePath,
	string[] Arguments);
