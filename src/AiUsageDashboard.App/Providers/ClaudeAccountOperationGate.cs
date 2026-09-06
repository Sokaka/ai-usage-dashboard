using System.Collections.Concurrent;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeAccountOperationGate
{
	private readonly KeyedAsyncGate<Guid> _accountGates = new();
	private readonly ConcurrentDictionary<Guid, byte>
		_containmentCompromisedAccounts = new();

	internal static ClaudeAccountOperationGate Shared { get; } = new();

	internal int CachedAccountCount => _accountGates.Count;

	public async ValueTask<IDisposable> EnterAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		ThrowIfContainmentCompromised(accountId);

		return await _accountGates.EnterAsync(
			accountId,
			cancellationToken);
	}

	internal void MarkContainmentCompromised(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		_containmentCompromisedAccounts.TryAdd(accountId, 0);
	}

	internal void ThrowIfContainmentCompromised(Guid accountId)
	{
		if (_containmentCompromisedAccounts.ContainsKey(accountId))
		{
			throw new ClaudeCliContainmentException();
		}
	}

	internal void RequestPurge(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		_accountGates.RequestEviction(accountId);
	}
}
