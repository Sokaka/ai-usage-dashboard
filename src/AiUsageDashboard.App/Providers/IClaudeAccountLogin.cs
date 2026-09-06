namespace AiUsageDashboard.App.Providers;

internal sealed record ClaudeAccountLoginResult(
	string AccountIdentity,
	ClaudeSubscriptionContext? SubscriptionContext = null,
	CliVersionEvidence? VersionEvidence = null);

internal sealed class ClaudeAccountLoginException : InvalidOperationException
{
	public ClaudeAccountLoginException(string message)
		: base(message)
	{
	}

	public ClaudeAccountLoginException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

internal interface IClaudeAccountLogin
{
	Task<ClaudeAccountLoginResult> LoginAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
