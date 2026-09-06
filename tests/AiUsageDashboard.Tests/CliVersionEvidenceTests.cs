using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CliVersionEvidenceTests
{
	[Fact]
	public void AppendFailureDiagnostic_WithReferenceVersion_LeavesMessageUnchanged()
	{
		CliVersionEvidence evidence = CliVersionPolicies.AssessCodex(
			new Version(0, 144, 1));

		string message = evidence.AppendFailureDiagnostic("原始錯誤。");

		Assert.Equal("原始錯誤。", message);
	}

	[Fact]
	public void AppendFailureDiagnostic_WithUnverifiedVersion_AddsNonCausalContext()
	{
		CliVersionEvidence evidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));

		string message = evidence.AppendFailureDiagnostic("原始錯誤。");

		Assert.StartsWith("原始錯誤。", message, StringComparison.Ordinal);
		Assert.Contains("偵測到 Codex CLI 0.145.0", message, StringComparison.Ordinal);
		Assert.Contains("相容性基準為 0.144.1", message, StringComparison.Ordinal);
		Assert.Contains("不代表不支援", message, StringComparison.Ordinal);
		Assert.Contains("可能是原因之一", message, StringComparison.Ordinal);
	}

	[Fact]
	public void AppendFailureDiagnostic_WithUnknownAllowedVersion_ReportsUnknownVersion()
	{
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(version: null);

		string message = evidence.AppendFailureDiagnostic("原始錯誤。");

		Assert.Contains("無法辨識 Grok Build CLI 版號", message, StringComparison.Ordinal);
		Assert.Contains("相容性基準為 1.0.3", message, StringComparison.Ordinal);
	}

	[Fact]
	public void AppendVersionDiagnostic_FindsEvidenceOnInnerException()
	{
		InvalidDataException inner = new("schema failure");
		inner.AttachVersionEvidence(CliVersionPolicies.AssessClaude(
			new Version(2, 1, 239)));
		InvalidOperationException outer = new("wrapper", inner);

		string message = outer.AppendVersionDiagnostic("原始錯誤。");

		Assert.Contains("Claude Code 2.1.239", message, StringComparison.Ordinal);
	}
}
