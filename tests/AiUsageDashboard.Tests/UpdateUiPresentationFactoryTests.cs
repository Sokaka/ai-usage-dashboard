using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class UpdateUiPresentationFactoryTests
{
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		6,
		0,
		0,
		TimeSpan.Zero);

	[Fact]
	public void Create_WhenFirstAutomaticNoticeIsPending_ShowsPrivacyNoticeOnly()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.IdleStale),
			shouldShowAutomaticCheckNotice: true,
			AppInstallationKind.CanonicalManaged,
			canLaunchUpdater: true,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.IsDisableAutomaticChecksVisible);
		Assert.Equal(UpdatePrimaryActionKind.None, presentation.PrimaryAction);
		Assert.False(presentation.HasUpdateBadge);
		Assert.Contains("不會自動下載或安裝", presentation.BannerText);
		Assert.Contains("24 小時", presentation.BannerText);
		Assert.Contains("15 分鐘至 24 小時", presentation.BannerText);
	}

	[Fact]
	public void Create_WhenCanonicalUpdateIsAvailable_OffersUpdaterAndBadge()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.UpdateAvailable),
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.CanonicalManaged,
			canLaunchUpdater: true,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.True(presentation.IsSnoozeVisible);
		Assert.Equal(
			UpdatePrimaryActionKind.LaunchUpdater,
			presentation.PrimaryAction);
		Assert.Equal("更新並重新啟動", presentation.PrimaryActionText);
		Assert.Equal(
			"更新並重新啟動 1.0.4",
			presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenMaintenanceUpdaterIsRunning_DisablesDuplicateActions()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.UpdateAvailable),
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.CanonicalManaged,
			canLaunchUpdater: true,
			isUpdaterRunning: true,
			TestNow);

		Assert.Equal(
			UpdatePrimaryActionKind.LaunchUpdater,
			presentation.PrimaryAction);
		Assert.False(presentation.IsPrimaryActionEnabled);
		Assert.False(presentation.CanCheckManually);
		Assert.False(presentation.IsSnoozeVisible);
		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.Equal(
			"更新並重新啟動 1.0.4",
			presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenPortableUpdateIsAvailable_UsesReleasePageAction()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.UpdateAvailable),
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			presentation.PrimaryAction);
		Assert.Equal("開啟下載頁", presentation.PrimaryActionText);
		Assert.True(presentation.HasUpdateBadge);
		Assert.Contains("解壓到新資料夾", presentation.BannerText);
		Assert.Equal("開啟下載頁", presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenCustomManagedUpdateIsAvailable_ExplainsInstallBoundary()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.UpdateAvailable),
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.CustomManaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			presentation.PrimaryAction);
		Assert.Contains("不會原地更新", presentation.BannerText);
		Assert.Contains("可能建立標準安裝", presentation.BannerText);
	}

	[Fact]
	public void Create_WhenUpdateIsSnoozed_HidesBannerButKeepsBadgeAndTrayAction()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.UpdateAvailable) with
			{
				IsSnoozed = true,
				SnoozedUntilUtc = TestNow + TimeSpan.FromHours(24)
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.False(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.False(presentation.IsSnoozeVisible);
		Assert.NotNull(presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenManualCheckFails_ShowsRetryWithoutUpdateBadge()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				AvailableVersion = null,
				ReleaseSequence = null,
				LastKnownResult = null,
				FailureMessage = "目前無法連線檢查更新。",
				IsManualCheck = true
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.Equal(UpdatePrimaryActionKind.CheckNow, presentation.PrimaryAction);
		Assert.False(presentation.HasUpdateBadge);
		Assert.Contains("目前無法連線檢查更新", presentation.BannerText);
		Assert.Contains("上次成功檢查", presentation.AboutStatusText);
	}

	[Fact]
	public void Create_WhenAutomaticCheckFailsWithoutKnownResult_RemainsNonModal()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				AvailableVersion = null,
				ReleaseSequence = null,
				LastKnownResult = null,
				FailureMessage = "目前無法連線檢查更新。",
				IsManualCheck = false
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.False(presentation.IsBannerVisible);
		Assert.Equal(UpdatePrimaryActionKind.None, presentation.PrimaryAction);
		Assert.False(presentation.HasUpdateBadge);
		Assert.Contains("最近一次自動檢查失敗", presentation.AboutStatusText);
		Assert.Contains("將依排程重試", presentation.AboutStatusText);
	}

	[Fact]
	public void Create_WhenAutomaticCheckFailsWhileDisabled_DoesNotPromiseRetry()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				IsAutoCheckEnabled = false,
				AvailableVersion = null,
				ReleaseSequence = null,
				LastKnownResult = null,
				LastFailureUtc = TestNow,
				FailureMessage = null,
				IsManualCheck = false
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.Contains("最近一次自動檢查失敗", presentation.AboutStatusText);
		Assert.DoesNotContain("重試", presentation.AboutStatusText);
	}

	[Fact]
	public void Create_WhenAutomaticCheckFailsAfterKnownUpdate_KeepsActionsAndReportsFailure()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				LastFailureUtc = TestNow,
				FailureMessage = null,
				IsManualCheck = false
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.CanonicalManaged,
			canLaunchUpdater: true,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.False(presentation.IsSnoozeVisible);
		Assert.Equal(
			UpdatePrimaryActionKind.LaunchUpdater,
			presentation.PrimaryAction);
		Assert.Contains("最近一次自動檢查失敗", presentation.AboutStatusText);
		Assert.Contains("有新版 1.0.4", presentation.AboutStatusText);
	}

	[Fact]
	public void Create_WhenManualCheckFailsAfterKnownUpdate_KeepsReliableActionsAndShowsRetryGuidance()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				FailureMessage = "目前無法連線檢查更新。",
				IsManualCheck = true,
				IsSnoozed = true,
				SnoozedUntilUtc = TestNow + TimeSpan.FromHours(1)
			};

		UpdateUiPresentation managedPresentation =
			UpdateUiPresentationFactory.Create(
				state,
				shouldShowAutomaticCheckNotice: false,
				AppInstallationKind.CanonicalManaged,
				canLaunchUpdater: true,
				isUpdaterRunning: false,
				TestNow);
		UpdateUiPresentation portablePresentation =
			UpdateUiPresentationFactory.Create(
				state,
				shouldShowAutomaticCheckNotice: false,
				AppInstallationKind.Unmanaged,
				canLaunchUpdater: false,
				isUpdaterRunning: false,
				TestNow);

		Assert.Equal(
			UpdatePrimaryActionKind.LaunchUpdater,
			managedPresentation.PrimaryAction);
		Assert.Equal(
			"更新並重新啟動 1.0.4",
			managedPresentation.TrayUpdateActionText);
		Assert.True(managedPresentation.IsPrimaryActionEnabled);
		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			portablePresentation.PrimaryAction);
		Assert.Equal("開啟下載頁", portablePresentation.TrayUpdateActionText);
		Assert.True(portablePresentation.IsPrimaryActionEnabled);
		Assert.Contains("目前無法連線檢查更新", managedPresentation.BannerText);
		Assert.Contains("重新檢查", managedPresentation.BannerText);
		Assert.True(managedPresentation.IsBannerVisible);
		Assert.True(managedPresentation.HasUpdateBadge);
	}

	[Fact]
	public void Create_WhenAutomaticCheckRunsWithKnownUpdate_KeepsReliableSurfaces()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.Checking) with
			{
				IsManualCheck = false
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.CanonicalManaged,
			canLaunchUpdater: true,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsChecking);
		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.False(presentation.IsPrimaryActionEnabled);
		Assert.False(presentation.IsSnoozeVisible);
		Assert.Equal(
			"更新並重新啟動 1.0.4",
			presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenAutomaticCheckFailsAfterUnknownVersion_KeepsReleasePrompt()
	{
		UpdatePresentationState state =
			CreateState(UpdatePresentationStatus.CheckFailed) with
			{
				LastKnownResult = new UpdateKnownResult(
					CheckedAgainstVersion: null,
					AvailableVersion: "1.0.4",
					ReleaseSequence: 1018,
					IsUpdateAvailable: false),
				FailureMessage = "目前無法連線檢查更新。",
				IsManualCheck = false
			};

		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: false,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			presentation.PrimaryAction);
		Assert.False(presentation.HasUpdateBadge);
		Assert.Contains("無法可靠判斷", presentation.BannerText);
		Assert.Contains("最近一次自動檢查失敗", presentation.AboutStatusText);
		Assert.Equal("開啟下載頁", presentation.TrayUpdateActionText);
	}

	[Fact]
	public void Create_WhenCurrentVersionIsUnknown_UsesPersistentReleasePagePrompt()
	{
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			CreateState(UpdatePresentationStatus.UnknownCurrentVersion),
			shouldShowAutomaticCheckNotice: false,
			AppInstallationKind.Unmanaged,
			canLaunchUpdater: true,
			isUpdaterRunning: false,
			TestNow);

		Assert.True(presentation.IsBannerVisible);
		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			presentation.PrimaryAction);
		Assert.False(presentation.HasUpdateBadge);
		Assert.Contains("無法可靠判斷", presentation.BannerText);
		Assert.Equal("開啟下載頁", presentation.TrayUpdateActionText);
	}

	private static UpdatePresentationState CreateState(
		UpdatePresentationStatus status)
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			1018,
			IsUpdateAvailable: true);
		return new UpdatePresentationState(
			status,
			IsAutoCheckEnabled: true,
			HasShownAutomaticCheckNotice: true,
			CurrentVersion: "1.0.3",
			AvailableVersion: "1.0.4",
			ReleaseSequence: 1018,
			LastKnownResult: knownResult,
			LastSuccessfulCheckUtc: TestNow,
			LastFailureUtc: null,
			FailureMessage: null,
			IsManualCheck: false,
			IsSnoozed: false,
			SnoozedUntilUtc: null);
	}
}
