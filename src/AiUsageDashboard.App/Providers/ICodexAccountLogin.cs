namespace AiUsageDashboard.App.Providers;

internal sealed record CodexAccountLoginResult(
	string AccountIdentity,
	Guid? WorkspaceId = null);

internal sealed class CodexAccountLoginException : InvalidOperationException
{
	public CodexAccountLoginException(string message)
		: base(message)
	{
	}

	public CodexAccountLoginException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

internal interface ICodexWorkspaceConfigurationTransaction : IAsyncDisposable
{
	void Commit();
}

internal interface ICodexAccountLoginSession : IAsyncDisposable
{
	Uri AuthorizationUri { get; }

	Task<CodexAccountLoginResult> WaitForCompletionAsync(
		CancellationToken cancellationToken = default);

	ICodexWorkspaceConfigurationTransaction
		TakeWorkspaceConfigurationTransaction();
}

internal interface ICodexAccountLogin
{
	Task<ICodexAccountLoginSession> StartAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task<ICodexAccountLoginSession> StartAsync(
		Guid accountId,
		Guid workspaceId,
		CancellationToken cancellationToken = default);
}
