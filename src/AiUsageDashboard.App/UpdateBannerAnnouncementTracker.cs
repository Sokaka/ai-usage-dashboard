using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.App;

internal enum UpdateBannerAnnouncementKind
{
	AutomaticCheckNotice,
	UpdateAvailable
}

internal readonly record struct UpdateBannerAnnouncementKey(
	UpdateBannerAnnouncementKind Kind,
	string? AvailableVersion,
	long? ReleaseSequence)
{
	internal static UpdateBannerAnnouncementKey AutomaticCheckNotice { get; } =
		new(
			UpdateBannerAnnouncementKind.AutomaticCheckNotice,
			AvailableVersion: null,
			ReleaseSequence: null);

	internal static UpdateBannerAnnouncementKey CreateUpdateAvailable(
		string availableVersion,
		long? releaseSequence)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(availableVersion);
		return new UpdateBannerAnnouncementKey(
			UpdateBannerAnnouncementKind.UpdateAvailable,
			availableVersion,
			releaseSequence);
	}
}

internal readonly record struct UpdateBannerAnnouncementDispatch(
	long Generation,
	UpdateBannerAnnouncementKey Key);

internal static class UpdateBannerAnnouncementPolicy
{
	internal static UpdateBannerAnnouncementKey? CreateKey(
		UpdateUiPresentation presentation,
		long? releaseSequence,
		bool shouldShowAutomaticCheckNotice)
	{
		ArgumentNullException.ThrowIfNull(presentation);
		if (!presentation.IsBannerVisible ||
			string.IsNullOrWhiteSpace(presentation.BannerText))
		{
			return null;
		}

		if (shouldShowAutomaticCheckNotice)
		{
			return UpdateBannerAnnouncementKey.AutomaticCheckNotice;
		}

		if (!presentation.HasUpdateBadge ||
			string.IsNullOrWhiteSpace(presentation.AvailableVersion))
		{
			return null;
		}

		return UpdateBannerAnnouncementKey.CreateUpdateAvailable(
			presentation.AvailableVersion,
			releaseSequence);
	}
}

internal sealed class UpdateBannerAnnouncementTracker
{
	private UpdateBannerAnnouncementKey? _currentKey;
	private long? _scheduledGeneration;
	private long _generation;
	private bool _hasPendingAnnouncement;

	internal void Observe(UpdateBannerAnnouncementKey? key)
	{
		if (_currentKey == key)
		{
			return;
		}

		_currentKey = key;
		_generation++;
		_hasPendingAnnouncement = key is not null;
	}

	internal bool TryBeginDispatch(
		bool canAnnounce,
		out UpdateBannerAnnouncementDispatch dispatch)
	{
		if (!canAnnounce ||
			!_hasPendingAnnouncement ||
			(_currentKey is not UpdateBannerAnnouncementKey key) ||
			(_scheduledGeneration == _generation))
		{
			dispatch = default;
			return false;
		}

		_scheduledGeneration = _generation;
		dispatch = new UpdateBannerAnnouncementDispatch(_generation, key);
		return true;
	}

	internal bool IsCurrent(UpdateBannerAnnouncementDispatch dispatch)
	{
		return _hasPendingAnnouncement &&
			(_generation == dispatch.Generation) &&
			(_currentKey == dispatch.Key);
	}

	internal void CompleteDispatch(
		UpdateBannerAnnouncementDispatch dispatch,
		bool wasRaised)
	{
		if (_scheduledGeneration == dispatch.Generation)
		{
			_scheduledGeneration = null;
		}

		if (wasRaised && IsCurrent(dispatch))
		{
			_hasPendingAnnouncement = false;
		}
	}
}
