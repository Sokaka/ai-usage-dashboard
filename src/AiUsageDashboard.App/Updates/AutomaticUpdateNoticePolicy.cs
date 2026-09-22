namespace AiUsageDashboard.App.Updates;

internal enum AutomaticUpdateNoticePresentationAction
{
	None,
	ShowInline,
	ShowBalloon
}

internal enum AutomaticUpdateCheckGateAction
{
	Allow,
	ShowInlineNotice,
	ShowBalloonNotice,
	WaitForDelay,
	RestartDelay,
	MarkNoticeShownAndAllow
}

internal static class AutomaticUpdateNoticePolicy
{
	internal static AutomaticUpdateNoticePresentationAction
		GetPresentationAction(
			bool shouldShowNotice,
			bool isWindowVisible,
			bool isWindowCollapsed,
			bool hasPresentedNotice)
	{
		if (!shouldShowNotice)
		{
			return AutomaticUpdateNoticePresentationAction.None;
		}

		if (isWindowVisible && !isWindowCollapsed)
		{
			return AutomaticUpdateNoticePresentationAction.ShowInline;
		}

		return hasPresentedNotice
			? AutomaticUpdateNoticePresentationAction.None
			: AutomaticUpdateNoticePresentationAction.ShowBalloon;
	}

	internal static AutomaticUpdateCheckGateAction GetAutomaticCheckAction(
		bool shouldShowNotice,
		bool isWindowVisible,
		bool isWindowCollapsed,
		DateTimeOffset? presentedAtUtc,
		DateTimeOffset utcNow)
	{
		if (!shouldShowNotice)
		{
			return AutomaticUpdateCheckGateAction.Allow;
		}

		if (presentedAtUtc is DateTimeOffset presentationTimeUtc)
		{
			TimeSpan elapsed = utcNow.ToUniversalTime() -
				presentationTimeUtc.ToUniversalTime();
			if (elapsed < TimeSpan.Zero)
			{
				return AutomaticUpdateCheckGateAction.RestartDelay;
			}

			return elapsed >= UpdateCheckScheduler.AutomaticCheckNoticeDelay
				? AutomaticUpdateCheckGateAction.MarkNoticeShownAndAllow
				: AutomaticUpdateCheckGateAction.WaitForDelay;
		}

		return isWindowVisible && !isWindowCollapsed
			? AutomaticUpdateCheckGateAction.ShowInlineNotice
			: AutomaticUpdateCheckGateAction.ShowBalloonNotice;
	}
}
