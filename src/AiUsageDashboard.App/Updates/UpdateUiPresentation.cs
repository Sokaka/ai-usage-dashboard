using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.App.Updates;

internal enum UpdatePrimaryActionKind
{
	None,
	CheckNow,
	OpenReleases,
	LaunchUpdater
}

internal sealed record UpdateUiPresentation(
	string AboutStatusText,
	bool CanCheckManually,
	bool IsChecking,
	bool IsBannerVisible,
	string BannerText,
	UpdatePrimaryActionKind PrimaryAction,
	string PrimaryActionText,
	bool IsPrimaryActionEnabled,
	bool IsReleaseHistoryVisible,
	bool IsSnoozeVisible,
	bool IsDisableAutomaticChecksVisible,
	bool HasUpdateBadge,
	string? AvailableVersion,
	string? TrayUpdateActionText);

internal static class UpdateUiPresentationFactory
{
	internal static UpdateUiPresentation Create(
		UpdatePresentationState state,
		bool shouldShowAutomaticCheckNotice,
		AppInstallationKind installationKind,
		bool canLaunchUpdater,
		bool isUpdaterRunning,
		DateTimeOffset localNow)
	{
		ArgumentNullException.ThrowIfNull(state);
		localNow = localNow.ToLocalTime();

		if (shouldShowAutomaticCheckNotice)
		{
			return CreateAutomaticCheckNotice(state);
		}

		return state.Status switch
		{
			UpdatePresentationStatus.UnavailableInThisBuild =>
				CreateUnavailable(state),
			UpdatePresentationStatus.DisabledByUser =>
				CreateIdle(state, UiText.Translate("自動檢查更新已關閉；仍可手動檢查。")),
			UpdatePresentationStatus.IdleStale =>
				CreateIdle(state, UiText.Translate("尚未檢查更新。")),
			UpdatePresentationStatus.Checking =>
				CreateChecking(
					state,
					installationKind,
					canLaunchUpdater,
					isUpdaterRunning,
					localNow),
			UpdatePresentationStatus.UpToDate =>
				CreateUpToDate(state, localNow),
			UpdatePresentationStatus.UpdateAvailable =>
				CreateUpdateAvailable(
					state,
					installationKind,
					canLaunchUpdater,
					isUpdaterRunning,
					localNow),
			UpdatePresentationStatus.UnknownCurrentVersion =>
				CreateUnknownCurrentVersion(state, localNow),
			UpdatePresentationStatus.CheckFailed =>
				CreateCheckFailed(
					state,
					installationKind,
					canLaunchUpdater,
					isUpdaterRunning,
					localNow),
			_ => throw new ArgumentOutOfRangeException(
				nameof(state),
				state.Status,
				UiText.Translate("未知的更新顯示狀態。"))
		};
	}

	private static UpdateUiPresentation CreateAutomaticCheckNotice(
		UpdatePresentationState state)
	{
		return new UpdateUiPresentation(
			UiText.Translate("AI Usage 會定期連線檢查已簽署的穩定版更新；成功後 24 小時內不再自動檢查，失敗時會依 15 分鐘至 24 小時的間隔重試。"),
			CanCheckManually: true,
			IsChecking: false,
			IsBannerVisible: true,
			UiText.Translate("AI Usage 會定期連線檢查已簽署的穩定版更新，不會自動下載或安裝。成功後 24 小時內不再自動檢查；失敗時會依 15 分鐘至 24 小時的間隔重試。你可以在這裡或 tray 選單關閉自動檢查。"),
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: state.IsAutoCheckEnabled,
			HasUpdateBadge: false,
			AvailableVersion: null,
			TrayUpdateActionText: null);
	}

	private static UpdateUiPresentation CreateUnavailable(
		UpdatePresentationState state)
	{
		return new UpdateUiPresentation(
			UiText.Translate("此開發版本未設定更新來源。正式發佈版本才會提供更新檢查。"),
			CanCheckManually: false,
			IsChecking: false,
			IsBannerVisible: false,
			BannerText: string.Empty,
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: false,
			AvailableVersion: state.AvailableVersion,
			TrayUpdateActionText: null);
	}

	private static UpdateUiPresentation CreateIdle(
		UpdatePresentationState state,
		string aboutStatusText)
	{
		return new UpdateUiPresentation(
			aboutStatusText,
			CanCheckManually: true,
			IsChecking: false,
			IsBannerVisible: false,
			BannerText: string.Empty,
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: false,
			AvailableVersion: state.AvailableVersion,
			TrayUpdateActionText: null);
	}

	private static UpdateUiPresentation CreateChecking(
		UpdatePresentationState state,
		AppInstallationKind installationKind,
		bool canLaunchUpdater,
		bool isUpdaterRunning,
		DateTimeOffset localNow)
	{
		UpdateUiPresentation? knownPresentation =
			CreateKnownResultPresentation(
				state,
				installationKind,
				canLaunchUpdater,
				isUpdaterRunning,
				localNow);
		if (knownPresentation is not null)
		{
			return knownPresentation with
			{
				AboutStatusText =
					UiText.Translate($"正在檢查更新… {knownPresentation.AboutStatusText}"),
				CanCheckManually = false,
				IsChecking = true,
				IsBannerVisible = state.IsManualCheck ||
					knownPresentation.IsBannerVisible,
				BannerText = state.IsManualCheck
					? UiText.Translate("正在檢查更新…")
					: knownPresentation.BannerText,
				IsPrimaryActionEnabled = false,
				IsSnoozeVisible = false
			};
		}

		bool isBannerVisible = state.IsManualCheck;
		return new UpdateUiPresentation(
			UiText.Translate("正在檢查更新…"),
			CanCheckManually: false,
			IsChecking: true,
			isBannerVisible,
			isBannerVisible ? UiText.Translate("正在檢查更新…") : string.Empty,
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: HasKnownUpdate(state),
			AvailableVersion: GetKnownAvailableVersion(state),
			TrayUpdateActionText: null);
	}

	private static UpdateUiPresentation CreateUpToDate(
		UpdatePresentationState state,
		DateTimeOffset localNow)
	{
		string checkTime = FormatCheckTime(state.LastSuccessfulCheckUtc, localNow);
		return new UpdateUiPresentation(
			UiText.Translate($"上次檢查時已是最新版{checkTime}。"),
			CanCheckManually: true,
			IsChecking: false,
			IsBannerVisible: false,
			BannerText: string.Empty,
			UpdatePrimaryActionKind.None,
			PrimaryActionText: string.Empty,
			IsPrimaryActionEnabled: false,
			IsReleaseHistoryVisible: false,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: false,
			AvailableVersion: state.AvailableVersion,
			TrayUpdateActionText: null);
	}

	private static UpdateUiPresentation CreateUpdateAvailable(
		UpdatePresentationState state,
		AppInstallationKind installationKind,
		bool canLaunchUpdater,
		bool isUpdaterRunning,
		DateTimeOffset localNow)
	{
		string version = GetRequiredAvailableVersion(state);
		UpdatePrimaryActionKind action = canLaunchUpdater
			? UpdatePrimaryActionKind.LaunchUpdater
			: UpdatePrimaryActionKind.OpenReleases;
		string actionText = canLaunchUpdater
			? UiText.Translate("更新並重新啟動")
			: UiText.Translate("開啟下載頁");
		string guidance = GetInstallationGuidance(
			installationKind,
			canLaunchUpdater);
		string bannerText = state.IsSnoozed
			? string.Empty
			: UiText.Translate($"AI Usage {version} 已可使用。{guidance}");
		string snoozeText = state.IsSnoozed &&
			(state.SnoozedUntilUtc is DateTimeOffset snoozedUntilUtc)
				? UiText.Translate($"；已稍後提醒至 {FormatDateTime(snoozedUntilUtc, localNow)}")
				: string.Empty;
		string checkTime = FormatCheckTime(
			state.LastSuccessfulCheckUtc,
			localNow);

		return new UpdateUiPresentation(
			UiText.Translate($"有新版 {version} 可用{snoozeText}{checkTime}。{guidance}"),
			CanCheckManually: !isUpdaterRunning,
			IsChecking: false,
			IsBannerVisible: !state.IsSnoozed,
			bannerText,
			action,
			actionText,
			IsPrimaryActionEnabled: !isUpdaterRunning,
			IsReleaseHistoryVisible: true,
			IsSnoozeVisible: !state.IsSnoozed && !isUpdaterRunning,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: true,
			AvailableVersion: version,
			TrayUpdateActionText: canLaunchUpdater
				? UiText.Translate($"更新並重新啟動 {version}")
				: UiText.Translate("開啟下載頁"));
	}

	private static UpdateUiPresentation CreateUnknownCurrentVersion(
		UpdatePresentationState state,
		DateTimeOffset localNow)
	{
		string version = GetRequiredAvailableVersion(state);
		string checkTime = FormatCheckTime(
			state.LastSuccessfulCheckUtc,
			localNow);
		return new UpdateUiPresentation(
			UiText.Translate($"已找到版本 {version}{checkTime}，但無法可靠判斷目前執行版本。請到下載頁確認。"),
			CanCheckManually: true,
			IsChecking: false,
			IsBannerVisible: true,
			UiText.Translate($"已找到版本 {version}，但無法可靠判斷目前版本。"),
			UpdatePrimaryActionKind.OpenReleases,
			UiText.Translate("開啟下載頁"),
			IsPrimaryActionEnabled: true,
			IsReleaseHistoryVisible: true,
			IsSnoozeVisible: false,
			IsDisableAutomaticChecksVisible: false,
			HasUpdateBadge: false,
			AvailableVersion: version,
			TrayUpdateActionText: UiText.Translate("開啟下載頁"));
	}

	private static UpdateUiPresentation CreateCheckFailed(
		UpdatePresentationState state,
		AppInstallationKind installationKind,
		bool canLaunchUpdater,
		bool isUpdaterRunning,
		DateTimeOffset localNow)
	{
		if (HasKnownUpdate(state))
		{
			UpdatePresentationState knownUpdateState = state with
			{
				Status = UpdatePresentationStatus.UpdateAvailable,
				AvailableVersion = state.LastKnownResult?.AvailableVersion,
				ReleaseSequence = state.LastKnownResult?.ReleaseSequence,
				IsManualCheck = false
			};
			UpdateUiPresentation knownUpdate = CreateUpdateAvailable(
				knownUpdateState,
				installationKind,
				canLaunchUpdater,
				isUpdaterRunning,
				localNow);
			string manualFailureText = CreateManualFailureText(state, localNow);
			UpdateUiPresentation failurePresentation = state.IsManualCheck
				? knownUpdate with
				{
					AboutStatusText = manualFailureText,
					IsBannerVisible = true,
					BannerText =
						UiText.Translate($"{manualFailureText} 仍可使用上次確認的更新；可從 tray 或「關於 AI Usage」重新檢查。")
				}
				: knownUpdate with
				{
					AboutStatusText = CreateAutomaticFailureAboutStatus(
						state,
						knownUpdate.AboutStatusText,
						localNow)
				};
			return failurePresentation with { IsSnoozeVisible = false };
		}

		if (!state.IsManualCheck &&
			(state.LastKnownResult is
				{ Status: UpdateAvailabilityStatus.UnknownCurrentVersion }
				unknownVersionResult))
		{
			UpdatePresentationState unknownVersionState = state with
			{
				Status = UpdatePresentationStatus.UnknownCurrentVersion,
				AvailableVersion = unknownVersionResult.AvailableVersion,
				ReleaseSequence = unknownVersionResult.ReleaseSequence
			};
			UpdateUiPresentation unknownVersionPresentation =
				CreateUnknownCurrentVersion(
				unknownVersionState,
				localNow);
			return unknownVersionPresentation with
			{
				AboutStatusText = CreateAutomaticFailureAboutStatus(
					state,
					unknownVersionPresentation.AboutStatusText,
					localNow)
			};
		}

		if (state.IsManualCheck)
		{
			string failureText = CreateManualFailureText(state, localNow);
			return new UpdateUiPresentation(
				failureText,
				CanCheckManually: true,
				IsChecking: false,
				IsBannerVisible: true,
				failureText,
				UpdatePrimaryActionKind.CheckNow,
				UiText.Translate("重新檢查"),
				IsPrimaryActionEnabled: true,
				IsReleaseHistoryVisible: true,
				IsSnoozeVisible: false,
				IsDisableAutomaticChecksVisible: false,
				HasUpdateBadge: false,
				AvailableVersion: state.AvailableVersion,
				TrayUpdateActionText: null);
		}

		string previousStatus = state.LastKnownResult is
			{ Status: UpdateAvailabilityStatus.UpToDate }
				? UiText.Translate($"上次成功檢查時沒有新版{FormatCheckTime(state.LastSuccessfulCheckUtc, localNow)}。")
				: UiText.Translate("尚未有成功的更新檢查結果。");
		string aboutStatus = CreateAutomaticFailureAboutStatus(
			state,
			previousStatus,
			localNow);
		return CreateIdle(state, aboutStatus);
	}

	private static string CreateAutomaticFailureAboutStatus(
		UpdatePresentationState state,
		string previousStatus,
		DateTimeOffset localNow)
	{
		string failureTime = state.LastFailureUtc is DateTimeOffset failedAtUtc
			? $"（{FormatDateTime(failedAtUtc, localNow)}）"
			: string.Empty;
		string retryText = state.IsAutoCheckEnabled
			? UiText.Translate("，將依排程重試")
			: string.Empty;
		return UiText.Translate($"最近一次自動檢查失敗{failureTime}{retryText}；{previousStatus}");
	}

	private static UpdateUiPresentation? CreateKnownResultPresentation(
		UpdatePresentationState state,
		AppInstallationKind installationKind,
		bool canLaunchUpdater,
		bool isUpdaterRunning,
		DateTimeOffset localNow)
	{
		if (HasKnownUpdate(state))
		{
			return CreateUpdateAvailable(
				state with
				{
					Status = UpdatePresentationStatus.UpdateAvailable,
					AvailableVersion = state.LastKnownResult?.AvailableVersion,
					ReleaseSequence = state.LastKnownResult?.ReleaseSequence
				},
				installationKind,
				canLaunchUpdater,
				isUpdaterRunning,
				localNow);
		}

		if (state.LastKnownResult is
			{ Status: UpdateAvailabilityStatus.UnknownCurrentVersion }
			unknownVersionResult)
		{
			return CreateUnknownCurrentVersion(
				state with
				{
					Status = UpdatePresentationStatus.UnknownCurrentVersion,
					AvailableVersion = unknownVersionResult.AvailableVersion,
					ReleaseSequence = unknownVersionResult.ReleaseSequence
				},
				localNow);
		}

		return null;
	}

	private static string CreateManualFailureText(
		UpdatePresentationState state,
		DateTimeOffset localNow)
	{
		string failureText = (string.IsNullOrWhiteSpace(state.FailureMessage)
			? UiText.Translate("無法完成更新檢查，請稍後再試。")
			: UiText.Translate(state.FailureMessage)).TrimEnd('。', '.');
		if (state.LastSuccessfulCheckUtc is not DateTimeOffset)
		{
			return failureText;
		}

		string previousResult = state.LastKnownResult switch
		{
			{ Status: UpdateAvailabilityStatus.UpdateAvailable } knownResult =>
				UiText.Translate($"；上次成功檢查找到版本 {knownResult.AvailableVersion}"),
			{ Status: UpdateAvailabilityStatus.UpToDate } =>
				UiText.Translate("；上次成功檢查時沒有新版"),
			{ Status: UpdateAvailabilityStatus.UnknownCurrentVersion } =>
				UiText.Translate("；上次成功取得新版資訊，但無法判斷目前版本"),
			null => UiText.Translate("；上次成功檢查已有結果"),
			_ => throw new InvalidOperationException(UiText.Translate("未知的更新結果狀態。"))
		};
		return $"{failureText}{previousResult}{FormatCheckTime(state.LastSuccessfulCheckUtc, localNow)}。";
	}

	private static string GetInstallationGuidance(
		AppInstallationKind installationKind,
		bool canLaunchUpdater)
	{
		return installationKind switch
		{
			AppInstallationKind.CanonicalManaged when canLaunchUpdater =>
				UiText.Translate("可使用既有維護 Updater 安裝。"),
			AppInstallationKind.CanonicalManaged =>
				UiText.Translate("目前無法啟動維護 Updater，請改用下載頁。"),
			AppInstallationKind.CustomManaged =>
				UiText.Translate("自訂安裝位置不會原地更新；下載頁的 Updater 可能建立標準安裝。"),
			AppInstallationKind.Unmanaged =>
				UiText.Translate("Portable 版請下載完整 ZIP、解壓到新資料夾，不要覆蓋目前資料夾。"),
			_ => throw new ArgumentOutOfRangeException(
				nameof(installationKind),
				installationKind,
				UiText.Translate("未知的安裝型態。"))
		};
	}

	private static bool HasKnownUpdate(UpdatePresentationState state)
	{
		return state.Status == UpdatePresentationStatus.UpdateAvailable ||
			(state.LastKnownResult?.IsUpdateAvailable == true);
	}

	private static string? GetKnownAvailableVersion(
		UpdatePresentationState state)
	{
		return state.AvailableVersion ??
			state.LastKnownResult?.AvailableVersion;
	}

	private static string GetRequiredAvailableVersion(
		UpdatePresentationState state)
	{
		return GetKnownAvailableVersion(state) ??
			throw new InvalidOperationException(
				UiText.Translate("更新顯示狀態缺少可用版本。"));
	}

	private static string FormatCheckTime(
		DateTimeOffset? checkUtc,
		DateTimeOffset localNow)
	{
		return checkUtc is DateTimeOffset value
			? UiText.Translate($"（上次檢查：{FormatDateTime(value, localNow)}）")
			: string.Empty;
	}

	private static string FormatDateTime(
		DateTimeOffset value,
		DateTimeOffset localNow)
	{
		return value.ToOffset(localNow.Offset).ToString(
			"g",
			UiText.Culture);
	}
}
