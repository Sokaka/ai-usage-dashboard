namespace AiUsageDashboard.App.Providers;

internal sealed class GrokAccountLoginException : InvalidOperationException
{
	public GrokAccountLoginException(string message)
		: base(message)
	{
	}

	public GrokAccountLoginException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

internal interface IGrokAccountLogin
{
	Task LoginAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
