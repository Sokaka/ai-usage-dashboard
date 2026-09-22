namespace AiUsageDashboard.App.Updates;

internal enum UpdateCheckExecutionOutcome
{
	Completed,
	Failed,
	Cancelled,
	SkippedNotDue,
	SkippedDisabled,
	UnavailableInThisBuild
}

internal sealed record UpdateCheckExecutionResult(
	UpdateCheckExecutionOutcome Outcome,
	UpdatePresentationState State,
	Exception? Failure = null,
	bool IsInteractive = false);

internal readonly record struct UpdateWindowActivity(
	bool IsVisible,
	bool IsCollapsed,
	bool IsActive)
{
	internal bool AllowsBalloonAttempt =>
		!IsVisible || IsCollapsed || !IsActive;
}
