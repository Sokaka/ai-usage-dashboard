using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Providers;

internal sealed class GrokAccountOperationGate
{
	private readonly KeyedAsyncGate<Guid> _accountGates = new();

	internal static GrokAccountOperationGate Shared { get; } = new();

	internal int CachedAccountCount => _accountGates.Count;

	public async ValueTask<IDisposable> EnterAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Grok 帳號識別碼不可為空。", nameof(accountId));
		}

		return await _accountGates.EnterAsync(accountId, cancellationToken);
	}

	internal async Task PurgeAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		using IDisposable lease = await EnterAsync(accountId, cancellationToken);
		RequestPurge(accountId);
	}

	internal void RequestPurge(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Grok 帳號識別碼不可為空。", nameof(accountId));
		}

		_accountGates.RequestEviction(accountId);
	}
}
