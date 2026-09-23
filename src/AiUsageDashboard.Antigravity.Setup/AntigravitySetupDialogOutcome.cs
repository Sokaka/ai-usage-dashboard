namespace AiUsageDashboard.Antigravity.Setup;

internal enum AntigravitySetupDialogOutcome
{
	CompletedOfficialPrint,
	CompletionUnknown,
	Cancelled,
	Failed
}

internal sealed record AntigravitySetupDialogResult(
	AntigravitySetupDialogOutcome Outcome,
	Exception? Failure = null);
