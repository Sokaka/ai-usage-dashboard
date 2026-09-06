namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityLiveR1SectionConsent(
	bool IsExplicitlyGranted);

internal sealed record AntigravityLiveR1SectionReport(
	bool IsCaptureSuccessful,
	bool IsR1Go,
	AntigravityLiveR0Report SafetyReport,
	AntigravityUsageR1SectionSemanticCapture? UsageSemanticCapture);

internal sealed class AntigravityLiveR1SectionRunResult
{
	internal AntigravityLiveR1SectionReport Report { get; }

	internal AntigravityUsageR1SectionPage? Page { get; }

	internal AntigravityLiveR1SectionRunResult(
		AntigravityLiveR1SectionReport report,
		AntigravityUsageR1SectionPage? page)
	{
		Report = report;
		Page = page;
	}
}
