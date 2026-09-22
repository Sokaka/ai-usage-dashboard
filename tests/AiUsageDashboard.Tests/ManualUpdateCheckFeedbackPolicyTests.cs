using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

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
