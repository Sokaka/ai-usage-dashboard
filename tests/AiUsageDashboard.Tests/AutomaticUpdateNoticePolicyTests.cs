using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class AutomaticUpdateNoticePolicyTests
{
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		6,
		0,
		0,
		TimeSpan.Zero);

	[Theory]
	[InlineData(false, false, false, (int)AutomaticUpdateNoticePresentationAction.ShowBalloon)]
	[InlineData(true, true, false, (int)AutomaticUpdateNoticePresentationAction.ShowBalloon)]
	[InlineData(true, false, false, (int)AutomaticUpdateNoticePresentationAction.ShowInline)]
	[InlineData(false, false, true, (int)AutomaticUpdateNoticePresentationAction.None)]
	[InlineData(true, true, true, (int)AutomaticUpdateNoticePresentationAction.None)]
	[InlineData(true, false, true, (int)AutomaticUpdateNoticePresentationAction.ShowInline)]
	public void GetPresentationAction_CoversSurfaceAndDeduplication(
		bool isWindowVisible,
		bool isWindowCollapsed,
		bool hasPresentedNotice,
		int expectedValue)
	{
		AutomaticUpdateNoticePresentationAction actual =
			AutomaticUpdateNoticePolicy.GetPresentationAction(
				shouldShowNotice: true,
				isWindowVisible,
				isWindowCollapsed,
				hasPresentedNotice);

		Assert.Equal((AutomaticUpdateNoticePresentationAction)expectedValue, actual);
	}

	[Fact]
	public void GetPresentationAction_WhenNoticeIsComplete_DoesNothing()
	{
		AutomaticUpdateNoticePresentationAction actual =
			AutomaticUpdateNoticePolicy.GetPresentationAction(
				shouldShowNotice: false,
				isWindowVisible: true,
				isWindowCollapsed: false,
				hasPresentedNotice: false);

		Assert.Equal(AutomaticUpdateNoticePresentationAction.None, actual);
	}

	[Theory]
	[InlineData(false, false, (int)AutomaticUpdateCheckGateAction.ShowBalloonNotice)]
	[InlineData(true, true, (int)AutomaticUpdateCheckGateAction.ShowBalloonNotice)]
	[InlineData(true, false, (int)AutomaticUpdateCheckGateAction.ShowInlineNotice)]
	public void GetAutomaticCheckAction_BeforePresentation_RequiresNotice(
		bool isWindowVisible,
		bool isWindowCollapsed,
		int expectedValue)
	{
		AutomaticUpdateCheckGateAction actual =
			AutomaticUpdateNoticePolicy.GetAutomaticCheckAction(
				shouldShowNotice: true,
				isWindowVisible,
				isWindowCollapsed,
				presentedAtUtc: null,
				TestNow);

		Assert.Equal((AutomaticUpdateCheckGateAction)expectedValue, actual);
	}

	[Theory]
	[InlineData(-1, (int)AutomaticUpdateCheckGateAction.RestartDelay)]
	[InlineData(0, (int)AutomaticUpdateCheckGateAction.WaitForDelay)]
	[InlineData(29999, (int)AutomaticUpdateCheckGateAction.WaitForDelay)]
	[InlineData(30000, (int)AutomaticUpdateCheckGateAction.MarkNoticeShownAndAllow)]
	public void GetAutomaticCheckAction_AfterPresentation_EnforcesDelay(
		int elapsedMilliseconds,
		int expectedValue)
	{
		AutomaticUpdateCheckGateAction actual =
			AutomaticUpdateNoticePolicy.GetAutomaticCheckAction(
				shouldShowNotice: true,
				isWindowVisible: false,
				isWindowCollapsed: true,
				TestNow,
				TestNow.AddMilliseconds(elapsedMilliseconds));

		Assert.Equal((AutomaticUpdateCheckGateAction)expectedValue, actual);
	}

	[Fact]
	public void GetAutomaticCheckAction_WhenNoticeIsComplete_AllowsCheck()
	{
		AutomaticUpdateCheckGateAction actual =
			AutomaticUpdateNoticePolicy.GetAutomaticCheckAction(
				shouldShowNotice: false,
				isWindowVisible: false,
				isWindowCollapsed: true,
				TestNow,
				TestNow.AddSeconds(1));

		Assert.Equal(AutomaticUpdateCheckGateAction.Allow, actual);
	}
}
