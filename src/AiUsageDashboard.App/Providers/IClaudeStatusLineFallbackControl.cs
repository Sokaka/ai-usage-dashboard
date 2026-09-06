namespace AiUsageDashboard.App.Providers;

internal interface IClaudeStatusLineFallbackControl
{
	Task DisableAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
