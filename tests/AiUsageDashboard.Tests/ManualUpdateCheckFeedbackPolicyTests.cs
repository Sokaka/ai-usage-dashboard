using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.Tests;

[LegacyChineseUiTest]
public sealed class ManualUpdateCheckFeedbackPolicyTests
{
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		21,
		8,
		0,
		0,
		TimeSpan.Zero);

	[Fact]
	public void Create_WhenTrayCheckCompletesWhileWindowIsHidden_ShowsUpToDateFeedback()
	{
		UpdateCheckExecutionResult result = CreateResult(
			UpdateCheckExecutionOutcome.Completed,
			UpdatePresentationStatus.UpToDate);

		ManualUpdateCheckFeedback? feedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Tray,
				isWindowVisible: false,
				isWindowCollapsed: false,
				result);
		ManualUpdateCheckFeedback? expandedWindowFeedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Tray,
				isWindowVisible: true,
				isWindowCollapsed: false,
				result);

		Assert.NotNull(feedback);
		Assert.False(feedback.IsWarning);
		Assert.Contains("最新版", feedback.Message);
		Assert.NotNull(expandedWindowFeedback);
	}

	[Fact]
	public void Create_WhenTrayCheckFailsWhileWindowIsCollapsed_ShowsFailureFeedback()
	{
		UpdateCheckExecutionResult result = CreateResult(
			UpdateCheckExecutionOutcome.Failed,
			UpdatePresentationStatus.CheckFailed,
			"無法連線到更新服務，請稍後再試。");

		ManualUpdateCheckFeedback? feedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Tray,
				isWindowVisible: true,
				isWindowCollapsed: true,
				result);

		Assert.NotNull(feedback);
		Assert.True(feedback.IsWarning);
		Assert.Contains("無法連線到更新服務", feedback.Message);
	}

	[Fact]
	public void Create_WhenInlineSurfaceOwnsResult_DoesNotDuplicateFeedback()
	{
		UpdateCheckExecutionResult result = CreateResult(
			UpdateCheckExecutionOutcome.Completed,
			UpdatePresentationStatus.UpToDate);

		ManualUpdateCheckFeedback? feedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Inline,
				isWindowVisible: false,
				isWindowCollapsed: false,
				result);

		Assert.Null(feedback);
	}

	[Fact]
	public void Create_WhenTrayFailureIsAlreadyVisibleInline_DoesNotDuplicateFeedback()
	{
		UpdateCheckExecutionResult result = CreateResult(
			UpdateCheckExecutionOutcome.Failed,
			UpdatePresentationStatus.CheckFailed,
			"無法連線到更新服務，請稍後再試。");

		ManualUpdateCheckFeedback? feedback =
			ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Tray,
				isWindowVisible: true,
				isWindowCollapsed: false,
				result);

		Assert.Null(feedback);
	}

	[Theory]
	[InlineData("無法連線到更新服務，請稍後再試。", "Unable to connect to the update service. Try again later.")]
	[InlineData("更新資訊未通過驗證，已停止本次檢查。", "Update information failed validation. This check has been stopped.")]
	[InlineData("無法完成更新檢查，請稍後再試。", "The update check could not complete. Try again later.")]
	[InlineData("Remote diagnostic: 請保留原文 [raw_834]", "Remote diagnostic: 請保留原文 [raw_834]")]
	public void Create_WhenLanguageChanges_ProjectsTrayFailureWithoutChangingSource(
		string sourceMessage,
		string englishMessage)
	{
		UpdateCheckExecutionResult result = CreateResult(
			UpdateCheckExecutionOutcome.Failed,
			UpdatePresentationStatus.CheckFailed,
			sourceMessage);

		foreach (AppLanguage language in new[]
		{
			AppLanguage.English,
			AppLanguage.TraditionalChinese,
			AppLanguage.English
		})
		{
			using IDisposable languageScope = UiText.UseLanguage(language);
			ManualUpdateCheckFeedback? feedback = ManualUpdateCheckFeedbackPolicy.Create(
				ManualUpdateCheckInvocationSurface.Tray,
				isWindowVisible: false,
				isWindowCollapsed: false,
				result);
			string expectedMessage = language == AppLanguage.English ? englishMessage : sourceMessage;
			string expectedGuidance = language == AppLanguage.English
				? "The last verified update action is still available from the tray."
				: "上次確認的更新操作仍可從 tray 使用。";

			Assert.NotNull(feedback);
			Assert.True(feedback.IsWarning);
			Assert.Equal(language == AppLanguage.English ? "Unable to check for updates" : "無法檢查更新",
				feedback.Title);
			Assert.Equal($"{expectedMessage}\n\n{expectedGuidance}", feedback.Message);
			Assert.Equal(sourceMessage, result.State.FailureMessage);
		}
	}

	private static UpdateCheckExecutionResult CreateResult(
		UpdateCheckExecutionOutcome outcome,
		UpdatePresentationStatus status,
		string? failureMessage = null)
	{
		bool hasKnownUpdate = status != UpdatePresentationStatus.UpToDate;
		string availableVersion = hasKnownUpdate ? "1.0.4" : "1.0.3";
		UpdateKnownResult knownResult = new(
			"1.0.3",
			availableVersion,
			1018,
			IsUpdateAvailable: hasKnownUpdate);
		UpdatePresentationState state = new(
			status,
			IsAutoCheckEnabled: true,
			HasShownAutomaticCheckNotice: true,
			CurrentVersion: "1.0.3",
			availableVersion,
			ReleaseSequence: 1018,
			LastKnownResult: knownResult,
			LastSuccessfulCheckUtc: TestNow,
			LastFailureUtc: failureMessage is null
				? null
				: TestNow,
			failureMessage,
			IsManualCheck: true,
			IsSnoozed: false,
			SnoozedUntilUtc: null);
		return new UpdateCheckExecutionResult(
			outcome,
			state,
			Failure: null,
			IsInteractive: true);
	}
}
