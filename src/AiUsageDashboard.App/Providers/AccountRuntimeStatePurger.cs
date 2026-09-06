using System.IO;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed class AccountRuntimeStatePurger : IAccountRuntimeStatePurger
{
	private readonly ClaudeAccountOperationGate _claudeAccountOperationGate;
	private readonly IClaudeAccountBindingStore? _claudeAccountBindingStore;
	private readonly ClaudeBindingCommitGate? _claudeBindingCommitGate;
	private readonly ClaudeCliUsagePoller _claudeUsagePoller;
	private readonly ClaudeUsageProvider _claudeUsageProvider;
	private readonly CodexAccountOperationGate _codexAccountOperationGate;
	private readonly ICodexWorkspaceBindingStore? _codexWorkspaceBindingStore;
	private readonly CodexBindingCommitGate? _codexBindingCommitGate;
	private readonly CopilotAccountOperationGate? _copilotAccountOperationGate;
	private readonly Action<Guid>? _deleteCopilotCredential;
	private readonly Action<Guid> _deleteCopilotPrivateAccountDirectory;
	private readonly GrokAccountOperationGate? _grokAccountOperationGate;
	private readonly IGrokAccountBindingStore? _grokAccountBindingStore;
	private readonly IGrokConnectionPendingStore? _grokConnectionPendingStore;
	private readonly Action<Guid> _deleteGrokPrivateAccountDirectory;
	private readonly AntigravityUsageProvider? _antigravityUsageProvider;
	private readonly IAntigravityVerifiedAccountBindingStore?
		_antigravityVerifiedAccountBindingStore;

	internal AccountRuntimeStatePurger(
		ClaudeAccountOperationGate claudeAccountOperationGate,
		ClaudeCliUsagePoller claudeUsagePoller,
		ClaudeUsageProvider claudeUsageProvider,
		CodexAccountOperationGate codexAccountOperationGate,
		AntigravityUsageProvider? antigravityUsageProvider = null,
		IAntigravityVerifiedAccountBindingStore?
			antigravityVerifiedAccountBindingStore = null,
		GrokAccountOperationGate? grokAccountOperationGate = null,
		IGrokAccountBindingStore? grokAccountBindingStore = null,
		IGrokConnectionPendingStore? grokConnectionPendingStore = null,
		Action<Guid>? deleteGrokPrivateAccountDirectory = null,
		IClaudeAccountBindingStore? claudeAccountBindingStore = null,
		ClaudeBindingCommitGate? claudeBindingCommitGate = null,
		ICodexWorkspaceBindingStore? codexWorkspaceBindingStore = null,
		CodexBindingCommitGate? codexBindingCommitGate = null,
		CopilotAccountOperationGate? copilotAccountOperationGate = null,
		Action<Guid>? deleteCopilotCredential = null,
		Action<Guid>? deleteCopilotPrivateAccountDirectory = null)
	{
		_claudeAccountOperationGate = claudeAccountOperationGate ??
			throw new ArgumentNullException(nameof(claudeAccountOperationGate));
		_claudeUsagePoller = claudeUsagePoller ??
			throw new ArgumentNullException(nameof(claudeUsagePoller));
		_claudeUsageProvider = claudeUsageProvider ??
			throw new ArgumentNullException(nameof(claudeUsageProvider));
		_claudeAccountBindingStore = claudeAccountBindingStore;
		_claudeBindingCommitGate = claudeAccountBindingStore is null
			? null
			: claudeBindingCommitGate ??
				throw new ArgumentNullException(nameof(claudeBindingCommitGate));
		_codexAccountOperationGate = codexAccountOperationGate ??
			throw new ArgumentNullException(nameof(codexAccountOperationGate));
		_codexWorkspaceBindingStore = codexWorkspaceBindingStore;
		_codexBindingCommitGate = codexWorkspaceBindingStore is null
			? null
			: codexBindingCommitGate ??
				throw new ArgumentNullException(nameof(codexBindingCommitGate));
		_copilotAccountOperationGate = copilotAccountOperationGate;
		_deleteCopilotCredential = deleteCopilotCredential;
		_deleteCopilotPrivateAccountDirectory =
			deleteCopilotPrivateAccountDirectory ??
			DeleteCopilotPrivateAccountDirectory;
		_antigravityUsageProvider = antigravityUsageProvider;
		_antigravityVerifiedAccountBindingStore =
			antigravityVerifiedAccountBindingStore;
		_grokAccountOperationGate = grokAccountOperationGate;
		_grokAccountBindingStore = grokAccountBindingStore;
		_grokConnectionPendingStore = grokConnectionPendingStore;
		_deleteGrokPrivateAccountDirectory =
			deleteGrokPrivateAccountDirectory ??
			DeleteGrokPrivateAccountDirectory;
	}

	public async Task PurgeAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		switch (provider)
		{
			case ProviderKind.Claude:
				using (IDisposable? bindingLease =
					_claudeBindingCommitGate is null
						? null
						: await _claudeBindingCommitGate.EnterAsync(
							cancellationToken))
				using (IDisposable operationLease =
					await _claudeAccountOperationGate.EnterAsync(
					accountId,
					cancellationToken))
				{
					try
					{
						await _claudeUsagePoller.PurgeAccountSafetyStateAsync(
							accountId,
							ClaudeUsageSafetyStateClearPurpose.AccountRemoval,
							cancellationToken);
						if (_claudeAccountBindingStore is not null)
						{
							await _claudeAccountBindingStore.DeleteAsync(
								accountId,
								cancellationToken);
							ClaudeAccountBinding? deletedBinding =
								await _claudeAccountBindingStore.LoadAsync(
									accountId,
									cancellationToken);
							if (deletedBinding is not null)
							{
								throw new IOException(
									"Claude private binding 清理未通過 read-back 驗證。");
							}
						}
					}
					finally
					{
						// Durable cleanup can remain pending, but a removed card must
						// never retain account identity or keyed-gate state in this process.
						_claudeUsageProvider.PurgeAccountState(accountId);
						_claudeAccountOperationGate.RequestPurge(accountId);
					}
				}
				break;
			case ProviderKind.Codex:
				using (IDisposable? bindingLease =
					_codexBindingCommitGate is null
						? null
						: await _codexBindingCommitGate.EnterAsync(
							cancellationToken))
				using (IDisposable operationLease =
					await _codexAccountOperationGate.EnterAsync(
						accountId,
						cancellationToken))
				{
					try
					{
						if (_codexWorkspaceBindingStore is not null)
						{
							await _codexWorkspaceBindingStore.DeleteAsync(
								accountId,
								cancellationToken);
							CodexWorkspaceBinding? deletedBinding =
								await _codexWorkspaceBindingStore.LoadAsync(
									accountId,
									cancellationToken);
							if (deletedBinding is not null)
							{
								throw new IOException(
									"Codex private binding 清理未通過 read-back 驗證。");
							}
						}
					}
					finally
					{
						// 移除卡片只撤銷 private binding；不刪使用者的 Codex home、config 或 auth。
						_codexAccountOperationGate.RequestPurge(accountId);
					}
				}
				break;
			case ProviderKind.Copilot:
				if ((_copilotAccountOperationGate is null) ||
					(_deleteCopilotCredential is null))
				{
					throw new InvalidOperationException(
						"Copilot 執行狀態清理服務尚未設定。");
				}

				using (IDisposable lease =
					await _copilotAccountOperationGate.EnterAsync(
						accountId,
						cancellationToken))
				{
					try
					{
						await Task.Run(() =>
						{
							_deleteCopilotCredential(accountId);
							_deleteCopilotPrivateAccountDirectory(accountId);
						})
							.ConfigureAwait(false);
					}
					finally
					{
						_copilotAccountOperationGate.RequestPurge(accountId);
					}
				}

				break;
			case ProviderKind.Antigravity:
				if (_antigravityUsageProvider is null)
				{
					throw new InvalidOperationException(
						"Antigravity 執行狀態清理服務尚未設定。");
				}

				try
				{
					if (_antigravityVerifiedAccountBindingStore is not null)
					{
						await _antigravityVerifiedAccountBindingStore.DeleteAsync(
							accountId,
							cancellationToken);
					}
					else
					{
						cancellationToken.ThrowIfCancellationRequested();
					}
				}
				finally
				{
					// The durable binding cleanup remains journal-pending on
					// failure, but the removed card must not retain one-shot
					// revalidation or automatic-repair state in this process.
					_antigravityUsageProvider.PurgeAccountState(accountId);
				}

				break;
			case ProviderKind.Grok:
				if ((_grokAccountOperationGate is null) ||
					(_grokAccountBindingStore is null))
				{
					throw new InvalidOperationException(
						"Grok 執行狀態清理服務尚未設定。");
				}

				using (IDisposable lease =
					await _grokAccountOperationGate.EnterAsync(
						accountId,
						cancellationToken))
				{
					try
					{
						await _grokAccountBindingStore.DeleteAsync(
							accountId,
							cancellationToken);
						await RemoveGrokConnectionPendingWorkAsync(
							accountId,
							cancellationToken);
						await Task.Run(
							() => _deleteGrokPrivateAccountDirectory(accountId))
							.ConfigureAwait(false);
					}
					finally
					{
						_grokAccountOperationGate.RequestPurge(accountId);
					}
				}

				break;
		}
	}

	private async Task RemoveGrokConnectionPendingWorkAsync(
		Guid accountId,
		CancellationToken cancellationToken)
	{
		if (_grokConnectionPendingStore is null)
		{
			return;
		}

		IReadOnlyList<GrokConnectionPendingWork> pending =
			await _grokConnectionPendingStore.LoadAsync(cancellationToken);

		foreach (GrokConnectionPendingWork work in pending.Where(
			work => work.AccountId == accountId))
		{
			if (!await _grokConnectionPendingStore.TryRemoveAsync(
					accountId,
					work.AttemptId,
					cancellationToken))
			{
				throw new IOException("無法清除 Grok 連接續做狀態。");
			}
		}
	}

	private static void DeleteGrokPrivateAccountDirectory(Guid accountId)
	{
		DeletePrivateAccountDirectory(
			accountId,
			AppDataPaths.GetGrokHomeDirectory,
			"Grok");
	}

	private static void DeleteCopilotPrivateAccountDirectory(Guid accountId)
	{
		DeletePrivateAccountDirectory(
			accountId,
			AppDataPaths.GetCopilotHomeDirectory,
			"Copilot");
	}

	private static void DeletePrivateAccountDirectory(
		Guid accountId,
		Func<Guid, string> homeDirectoryResolver,
		string providerName)
	{
		string homeDirectory = Path.GetFullPath(
			homeDirectoryResolver(accountId));
		string? accountDirectory = Path.GetDirectoryName(homeDirectory);
		string? providerDirectory = accountDirectory is null
			? null
			: Path.GetDirectoryName(accountDirectory);

		if ((accountDirectory is null) ||
			(providerDirectory is null) ||
			!string.Equals(
				accountDirectory,
				Path.Combine(providerDirectory, accountId.ToString("N")),
				StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException($"{providerName} private account 路徑無效。");
		}

		if (!Directory.Exists(accountDirectory))
		{
			return;
		}

		DirectoryInfo provider = new(providerDirectory);
		DirectoryInfo account = new(accountDirectory);

		if (!provider.Exists ||
			((provider.Attributes & FileAttributes.ReparsePoint) != 0) ||
			((account.Attributes & FileAttributes.ReparsePoint) != 0))
		{
			throw new IOException(
				$"{providerName} private account 路徑含不安全的 reparse point。");
		}

		Stack<DirectoryInfo> directories = new();
		directories.Push(account);

		while (directories.TryPop(out DirectoryInfo? directory))
		{
			foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
			{
				if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException(
						$"{providerName} private account 路徑含不安全的 reparse point。");
				}

				if (item is DirectoryInfo childDirectory)
				{
					directories.Push(childDirectory);
				}
			}
		}

		Directory.Delete(accountDirectory, recursive: true);
	}

	public async Task ResetUsageSafetyStateAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		switch (provider)
		{
			case ProviderKind.Claude:
				using (IDisposable lease = await _claudeAccountOperationGate.EnterAsync(
					accountId,
					cancellationToken))
				{
					await _claudeUsagePoller.PurgeAccountSafetyStateAsync(
						accountId,
						ClaudeUsageSafetyStateClearPurpose.UserConfirmedReset,
						cancellationToken);
					_claudeUsageProvider.Invalidate(accountId);
				}

				break;
			case ProviderKind.Antigravity:
				cancellationToken.ThrowIfCancellationRequested();
				if (_antigravityUsageProvider is null)
				{
					throw new InvalidOperationException(
						"Antigravity 用量安全狀態重設服務尚未設定。");
				}

				_antigravityUsageProvider.ArmOfficialSafetyRevalidation(accountId);
				break;
			default:
				throw new ArgumentException(
					"只有 Claude 或 Antigravity 帳號具有可重設的用量安全狀態。",
					nameof(provider));
		}
	}
}
