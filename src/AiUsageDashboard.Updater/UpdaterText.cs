using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class UpdaterText
{
	private static readonly UpdaterText English = new(UpdaterDisplayLanguage.English);
	private static readonly UpdaterText TraditionalChinese = new(UpdaterDisplayLanguage.TraditionalChinese);
	private readonly UpdaterDisplayLanguage _language;

	internal string DialogTitle => Select("AI Usage Updater", "AI Usage 更新程式");

	internal string UpdateCancelled => Select("The update was cancelled.", "更新已取消。");

	internal string AlreadyCurrent => Select("AI Usage is already up to date.", "AI Usage 已是最新版。");

	internal string AlreadyCurrentAndStarted => Select(
		"AI Usage is already up to date and has been started.",
		"AI Usage 已是最新版，並已啟動。");

	internal string DelegatedUpdateCompleted => Select(
		"The latest version check and update have completed.",
		"最新版檢查與更新已完成。");

	internal string DelegatedUpdateFailed => Select(
		"The newer updater could not complete the update. See the error details.",
		"新版 updater 未能完成更新，請查看錯誤訊息。");

	internal string CanonicalUpdaterNotCurrent => Select(
		"The App was updated, but the canonical maintenance updater is not the version specified by the signed feed.",
		"App 已更新，但 canonical maintenance updater 仍不是 signed feed 指定版本。");

	private UpdaterText(UpdaterDisplayLanguage language)
	{
		_language = language;
	}

	internal static UpdaterText ForLanguage(UpdaterDisplayLanguage language)
	{
		return language switch
		{
			UpdaterDisplayLanguage.English => English,
			UpdaterDisplayLanguage.TraditionalChinese => TraditionalChinese,
			_ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported updater display language.")
		};
	}

	internal string UpdateCompleted(string version)
	{
		return Select($"AI Usage {version} has been updated.", $"AI Usage {version} 已更新完成。");
	}

	internal string UpdateCompletedAndRestarted(string version)
	{
		return Select($"AI Usage {version} has been updated and restarted.", $"AI Usage {version} 已更新並重新啟動。");
	}

	internal string UpdateCouldNotStart(string details)
	{
		return Select("The update could not start: ", "無法開始更新：") + details;
	}

	internal string UpdateFailed(string details)
	{
		return Select("Update failed: ", "更新失敗：") + details;
	}

	internal string RestartFailed(string details)
	{
		return Select(
			"The update was applied, but AI Usage could not be restarted: ",
			"更新已完成，但無法重新啟動 AI Usage：") + details;
	}

	internal string CurrentAppStartFailed(string details)
	{
		return Select("AI Usage is current, but it could not be started: ", "AI Usage 已是最新版，但無法啟動：") + details;
	}

	internal string RegistrationFailed(string details)
	{
		return Select(
			"The App was updated, but it could not be registered in Windows installed apps: ",
			"App 已更新，但無法登錄 Windows 已安裝的應用程式：") + details;
	}

	internal string ParentIdentityUnavailable(string details)
	{
		return Select(
			"The delegated maintenance updater parent identity could not be verified: ",
			"無法確認 delegated maintenance updater 的 parent 身分：") + details;
	}

	internal string CanonicalUpdaterVerificationFailed(string details)
	{
		return Select(
			"The App was updated, but the canonical maintenance updater could not be verified: ",
			"App 已更新，但無法驗證 canonical maintenance updater：") + details;
	}

	internal string PromotionSchedulingFailed(string details)
	{
		return Select(
			"The App was updated, but delegated maintenance updater promotion could not be scheduled: ",
			"App 已更新，但無法安排 delegated maintenance updater 提升：") + details;
	}

	internal string PendingPromotionRetryFailed(string details)
	{
		return Select(
			"Pending maintenance updater promotion could not be retried: ",
			"無法重試待完成的 maintenance updater 提升：") + details;
	}

	internal string ShortcutCreationFailed(string shortcutPath, string details)
	{
		return Select(
			$"The Start menu shortcut '{shortcutPath}' could not be created; check the path and permissions, then run the updater again. ",
			$"無法建立開始選單捷徑「{shortcutPath}」；請檢查該路徑與權限，再重新執行更新程式。") + details;
	}

	internal string ShortcutConflict(string shortcutPath)
	{
		return Select(
			$"The Start menu shortcut '{shortcutPath}' does not match this installation and was preserved; check the shortcut and move the conflicting file before running the updater again.",
			$"開始選單捷徑「{shortcutPath}」的內容與此安裝不符，已保留；請檢查該捷徑，移開同名衝突後再重新執行更新程式。");
	}

	private string Select(string english, string traditionalChinese)
	{
		return _language switch
		{
			UpdaterDisplayLanguage.English => english,
			UpdaterDisplayLanguage.TraditionalChinese => traditionalChinese,
			_ => throw new ArgumentOutOfRangeException(nameof(_language), _language, "Unsupported updater display language.")
		};
	}
}
