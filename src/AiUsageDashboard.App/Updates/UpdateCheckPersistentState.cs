using System.Globalization;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal sealed record UpdateKnownResult(
	string? CheckedAgainstVersion,
	string AvailableVersion,
	long ReleaseSequence,
	bool IsUpdateAvailable)
{
	internal UpdateAvailabilityStatus Status => CheckedAgainstVersion is null
		? UpdateAvailabilityStatus.UnknownCurrentVersion
		: IsUpdateAvailable
			? UpdateAvailabilityStatus.UpdateAvailable
			: UpdateAvailabilityStatus.UpToDate;

	internal UpdateNotificationKey NotificationKey => new(
		AvailableVersion,
		ReleaseSequence);
}

internal sealed record UpdateSnoozeState(
	string Version,
	long ReleaseSequence,
	DateTimeOffset SnoozedUntilUtc)
{
	internal UpdateNotificationKey NotificationKey => new(
		Version,
		ReleaseSequence);
}

internal sealed record UpdateCheckPersistentState(
	bool IsAutoCheckEnabled,
	bool HasShownAutomaticCheckNotice,
	DateTimeOffset? LastAttemptUtc,
	DateTimeOffset? LastSuccessfulCheckUtc,
	int ConsecutiveFailureCount,
	long? HighestObservedReleaseSequence,
	UpdateKnownResult? LastKnownResult,
	string? LastBalloonAttemptKey,
	UpdateSnoozeState? Snooze)
{
	internal static UpdateCheckPersistentState Default { get; } = new(
		IsAutoCheckEnabled: true,
		HasShownAutomaticCheckNotice: false,
		LastAttemptUtc: null,
		LastSuccessfulCheckUtc: null,
		ConsecutiveFailureCount: 0,
		HighestObservedReleaseSequence: null,
		LastKnownResult: null,
		LastBalloonAttemptKey: null,
		Snooze: null);
}

internal readonly record struct UpdateNotificationKey(
	string Version,
	long ReleaseSequence)
{
	private const char Separator = '|';

	internal static bool TryParse(
		string? value,
		out UpdateNotificationKey key)
	{
		key = default;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		int separatorIndex = value.LastIndexOf(Separator);
		if ((separatorIndex <= 0) ||
			(separatorIndex == value.Length - 1) ||
			!long.TryParse(
				value.AsSpan(separatorIndex + 1),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out long releaseSequence) ||
			(releaseSequence <= 0))
		{
			return false;
		}

		string version = value[..separatorIndex];
		if (!ReleaseVersion.TryParse(version, out _))
		{
			return false;
		}

		key = new UpdateNotificationKey(version, releaseSequence);
		return true;
	}

	public override string ToString()
	{
		return string.Concat(
			Version,
			Separator,
			ReleaseSequence.ToString(CultureInfo.InvariantCulture));
	}
}
