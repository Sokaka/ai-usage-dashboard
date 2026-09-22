using AiUsageDashboard.App;
using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class UpdateBannerAnnouncementTrackerTests
{
	[Fact]
	public void FirstAutomaticCheckNotice_IsAnnouncedOncePerVisibleCycle()
	{
		UpdateUiPresentation presentation = CreatePresentation(
			isBannerVisible: true,
			hasUpdateBadge: false,
			availableVersion: null);
		UpdateBannerAnnouncementKey? key =
			UpdateBannerAnnouncementPolicy.CreateKey(
				presentation,
				releaseSequence: null,
				shouldShowAutomaticCheckNotice: true);
		UpdateBannerAnnouncementTracker tracker = new();

		tracker.Observe(key);

		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch dispatch));
		Assert.Equal(
			UpdateBannerAnnouncementKind.AutomaticCheckNotice,
			dispatch.Key.Kind);
		tracker.CompleteDispatch(dispatch, wasRaised: true);
		tracker.Observe(key);
		Assert.False(tracker.TryBeginDispatch(
			canAnnounce: true,
			out _));
	}

	[Fact]
	public void UpdateDetectedDuringRefresh_IsHeldUntilAnnouncementIsAllowed()
	{
		UpdateBannerAnnouncementTracker tracker = new();
		UpdateBannerAnnouncementKey key = CreateUpdateKey("1.0.4", 104);

		tracker.Observe(key);

		Assert.False(tracker.TryBeginDispatch(
			canAnnounce: false,
			out _));
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch dispatch));
		tracker.CompleteDispatch(dispatch, wasRaised: true);
		Assert.False(tracker.TryBeginDispatch(
			canAnnounce: true,
			out _));
	}

	[Fact]
	public void DifferentVersionOrReleaseSequence_QueuesNewAnnouncement()
	{
		UpdateBannerAnnouncementTracker tracker = new();
		UpdateBannerAnnouncementKey first = CreateUpdateKey("1.0.4", 104);
		Announce(tracker, first);

		tracker.Observe(first);
		Assert.False(tracker.TryBeginDispatch(
			canAnnounce: true,
			out _));

		UpdateBannerAnnouncementKey newSequence =
			CreateUpdateKey("1.0.4", 105);
		Announce(tracker, newSequence);

		UpdateBannerAnnouncementKey newVersion =
			CreateUpdateKey("1.0.5", 106);
		tracker.Observe(newVersion);
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch dispatch));
		Assert.Equal("1.0.5", dispatch.Key.AvailableVersion);
	}

	[Fact]
	public void SnoozeExpiry_RequeuesSameReleaseAfterBannerReappears()
	{
		UpdateUiPresentation visiblePresentation = CreatePresentation(
			isBannerVisible: true,
			hasUpdateBadge: true,
			availableVersion: "1.0.4");
		UpdateBannerAnnouncementKey? visibleKey =
			UpdateBannerAnnouncementPolicy.CreateKey(
				visiblePresentation,
				releaseSequence: 104,
				shouldShowAutomaticCheckNotice: false);
		UpdateBannerAnnouncementTracker tracker = new();
		tracker.Observe(visibleKey);
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch firstDispatch));
		tracker.CompleteDispatch(firstDispatch, wasRaised: true);

		UpdateUiPresentation snoozedPresentation =
			visiblePresentation with
			{
				IsBannerVisible = false,
				BannerText = string.Empty
			};
		tracker.Observe(UpdateBannerAnnouncementPolicy.CreateKey(
			snoozedPresentation,
			releaseSequence: 104,
			shouldShowAutomaticCheckNotice: false));
		Assert.False(tracker.TryBeginDispatch(
			canAnnounce: true,
			out _));

		tracker.Observe(visibleKey);
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch resumedDispatch));
		Assert.Equal(firstDispatch.Key, resumedDispatch.Key);
		Assert.NotEqual(firstDispatch.Generation, resumedDispatch.Generation);
	}

	[Fact]
	public void SupersededDispatch_CannotConsumeNewPendingAnnouncement()
	{
		UpdateBannerAnnouncementTracker tracker = new();
		UpdateBannerAnnouncementKey first = CreateUpdateKey("1.0.4", 104);
		UpdateBannerAnnouncementKey second = CreateUpdateKey("1.0.5", 105);
		tracker.Observe(first);
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch firstDispatch));

		tracker.Observe(second);
		Assert.False(tracker.IsCurrent(firstDispatch));
		tracker.CompleteDispatch(firstDispatch, wasRaised: true);

		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch secondDispatch));
		Assert.Equal(second, secondDispatch.Key);
	}

	private static void Announce(
		UpdateBannerAnnouncementTracker tracker,
		UpdateBannerAnnouncementKey key)
	{
		tracker.Observe(key);
		Assert.True(tracker.TryBeginDispatch(
			canAnnounce: true,
			out UpdateBannerAnnouncementDispatch dispatch));
		tracker.CompleteDispatch(dispatch, wasRaised: true);
	}

	private static UpdateBannerAnnouncementKey CreateUpdateKey(
		string availableVersion,
		long releaseSequence)
	{
		UpdateBannerAnnouncementKey? key =
			UpdateBannerAnnouncementPolicy.CreateKey(
				CreatePresentation(
					isBannerVisible: true,
					hasUpdateBadge: true,
					availableVersion),
				releaseSequence,
				shouldShowAutomaticCheckNotice: false);
		Assert.True(key.HasValue);
		return key.Value;
	}

	private static UpdateUiPresentation CreatePresentation(
		bool isBannerVisible,
		bool hasUpdateBadge,
		string? availableVersion)
	{
		return new UpdateUiPresentation(
			AboutStatusText: string.Empty,
			CanCheckManually: true,
			IsChecking: false,
			isBannerVisible,
			BannerText: isBannerVisible ? "更新提示" : string.Empty,
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			hasUpdateBadge,
			availableVersion,
			TrayUpdateActionText: null);
	}
}
