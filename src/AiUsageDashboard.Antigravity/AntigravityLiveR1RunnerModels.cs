namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityLiveR1Consent(bool IsExplicitlyGranted);

internal sealed record AntigravityLiveR1Report(
	bool IsCaptureSuccessful,
	bool IsR1Go,
	AntigravityLiveR0Report SafetyReport,
	AntigravityUsageR1SemanticCapture? UsageSemanticCapture);

internal sealed class AntigravityLiveR1RunResult
{
	internal AntigravityLiveR1Report Report { get; }

	internal AntigravityUsageResult? Usage { get; }

	internal AntigravityLiveR1RunResult(
		AntigravityLiveR1Report report,
		AntigravityUsageResult? usage)
	{
		Report = report;
		Usage = usage;
	}
}
