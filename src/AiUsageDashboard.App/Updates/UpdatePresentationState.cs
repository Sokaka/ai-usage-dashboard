namespace AiUsageDashboard.App.Updates;

internal enum UpdatePresentationStatus
{
	UnavailableInThisBuild,
	DisabledByUser,
	IdleStale,
	Checking,
	UpToDate,
	UpdateAvailable,
	UnknownCurrentVersion,
	CheckFailed
}

internal sealed record UpdatePresentationState(
	UpdatePresentationStatus Status,
	bool IsAutoCheckEnabled,
	bool HasShownAutomaticCheckNotice,
	string? CurrentVersion,
	string? AvailableVersion,
	long? ReleaseSequence,
	UpdateKnownResult? LastKnownResult,
	DateTimeOffset? LastSuccessfulCheckUtc,
	DateTimeOffset? LastFailureUtc,
	string? FailureMessage,
	bool IsManualCheck,
	bool IsSnoozed,
	DateTimeOffset? SnoozedUntilUtc)
{
	internal static UpdatePresentationState CreateInitial(
		bool isFeatureAvailableInBuild,
		string? currentVersion)
	{
		return new UpdatePresentationState(
			isFeatureAvailableInBuild
				? UpdatePresentationStatus.IdleStale
				: UpdatePresentationStatus.UnavailableInThisBuild,
			IsAutoCheckEnabled: true,
			HasShownAutomaticCheckNotice: false,
			currentVersion,
			AvailableVersion: null,
			ReleaseSequence: null,
			LastKnownResult: null,
			LastSuccessfulCheckUtc: null,
			LastFailureUtc: null,
			FailureMessage: null,
			IsManualCheck: false,
			IsSnoozed: false,
			SnoozedUntilUtc: null);
	}
}

internal sealed class UpdatePresentationStateChangedEventArgs : EventArgs
{
	internal UpdatePresentationState State { get; }

	internal UpdatePresentationStateChangedEventArgs(
		UpdatePresentationState state)
	{
		State = state ?? throw new ArgumentNullException(nameof(state));
	}
}
