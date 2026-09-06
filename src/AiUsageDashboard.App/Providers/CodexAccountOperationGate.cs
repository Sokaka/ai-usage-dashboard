using System.Collections.Concurrent;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Providers;

internal sealed class CodexAccountOperationGate
{
	private readonly KeyedAsyncGate<Guid> _accountGates = new();
	private readonly ConcurrentDictionary<Guid, byte>
		_containmentCompromisedAccounts = new();

	internal static CodexAccountOperationGate Shared { get; } = new();

	internal int CachedAccountCount => _accountGates.Count;

	public async ValueTask<IDisposable> EnterAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Codex 帳號識別碼不可為空。", nameof(accountId));
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
			throw new ArgumentException("Codex 帳號識別碼不可為空。", nameof(accountId));
		}

		_containmentCompromisedAccounts.TryAdd(accountId, 0);
	}

	internal void ThrowIfContainmentCompromised(Guid accountId)
	{
		if (_containmentCompromisedAccounts.ContainsKey(accountId))
		{
			throw new CodexCliContainmentException(
				CodexCliContainmentException.RestartRequiredMessage);
		}
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
			throw new ArgumentException("Codex 帳號識別碼不可為空。", nameof(accountId));
		}

		_accountGates.RequestEviction(accountId);
	}
}
