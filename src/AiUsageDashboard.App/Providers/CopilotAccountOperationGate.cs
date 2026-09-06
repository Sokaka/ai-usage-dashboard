using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Providers;

internal sealed class CopilotAccountOperationGate
{
	private readonly KeyedAsyncGate<Guid> _accountGates = new();

	internal int CachedAccountCount => _accountGates.Count;

	public async ValueTask<IDisposable> EnterAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		cancellationToken.ThrowIfCancellationRequested();

		return await _accountGates.EnterAsync(accountId, cancellationToken);
	}

	internal void RequestPurge(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		_accountGates.RequestEviction(accountId);
	}
}
