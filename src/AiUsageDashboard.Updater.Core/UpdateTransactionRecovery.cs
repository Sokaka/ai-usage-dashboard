namespace AiUsageDashboard.Updater.Core;

public sealed class UpdateTransactionRecovery
{
	private readonly Action<string, string> _moveDirectory;

	public UpdateTransactionRecovery()
		: this(Directory.Move)
	{
	}

	internal UpdateTransactionRecovery(Action<string, string> moveDirectory)
	{
		_moveDirectory = moveDirectory ?? throw new ArgumentNullException(nameof(moveDirectory));
	}

	public static IReadOnlyList<UpdateTransaction> FindPending(string installRoot)
	{
		string root = UpdateTransaction.NormalizeInstallRoot(installRoot);
		string transactionsRoot = Path.Combine(root, "transactions");
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(transactionsRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(transactionsRoot, mustExist: false);
		if (!Directory.Exists(transactionsRoot))
		{
			return [];
		}

		List<UpdateTransaction> pending = [];
		foreach (string directory in Directory.EnumerateDirectories(transactionsRoot).Order(StringComparer.Ordinal))
		{
			UpdateTransaction.ThrowIfNotOrdinaryDirectory(directory, mustExist: true);
			string receiptPath = Path.Combine(directory, "transaction.json");
			UpdateTransaction.ThrowIfNotOrdinaryFile(receiptPath, mustExist: false);
			if (!File.Exists(receiptPath))
			{
				if (Directory.Exists(Path.Combine(directory, "previous")) ||
					File.Exists(Path.Combine(directory, "previous")))
				{
					throw new InvalidDataException($"Previous payload has no committed recovery receipt: {directory}");
				}

				// 初次 receipt 提交前中斷，或使用者自放的目錄，均不授權接管或清除。
				continue;
			}

			UpdateTransaction transaction = UpdateTransaction.Load(root, Path.GetFileName(directory));
			if ((transaction.TargetPayload is null) &&
				(transaction.State is not UpdateTransactionState.Committed) &&
				(Directory.Exists(transaction.PreviousDirectoryPath) || File.Exists(transaction.PreviousDirectoryPath)))
			{
				throw new InvalidDataException($"Previous payload exists without a switch ownership record: {transaction.TransactionDirectoryPath}");
			}

			if (transaction.HasPendingSwitch ||
				(transaction.State is UpdateTransactionState.Prepared or UpdateTransactionState.Staged or
				UpdateTransactionState.ShutdownRequested or UpdateTransactionState.AppExited))
			{
				transaction.ValidateOwnedDirectoryChain();
				pending.Add(transaction);
			}
		}

		if (pending.Count(transaction => transaction.HasPendingSwitch) > 1)
		{
			throw new InvalidDataException($"Multiple interrupted switches require manual inspection before recovery: {transactionsRoot}");
		}

		return pending;
	}

	public void Recover(UpdateTransaction transaction)
	{
		// 呼叫端持有 install lock；搬移 payload 前還需 App mutex 與 process gate。
		ArgumentNullException.ThrowIfNull(transaction);
		UpdateTransaction persisted = UpdateTransaction.Load(transaction.InstallRootPath, transaction.TransactionId);
		if (persisted.HasPendingSwitch)
		{
			RestoreLayout(persisted);
			persisted.TransitionTo(UpdateTransactionState.RolledBack);
			return;
		}

		if (persisted.State is UpdateTransactionState.Prepared or UpdateTransactionState.Staged or
			UpdateTransactionState.ShutdownRequested or UpdateTransactionState.AppExited)
		{
			persisted.ValidateOwnedDirectoryChain();
			if (Directory.Exists(persisted.PreviousDirectoryPath))
			{
				throw new InvalidDataException($"Previous payload exists without a switch intent: {persisted.TransactionDirectoryPath}");
			}

			persisted.TransitionTo(UpdateTransactionState.Failed);
		}
	}

	internal void RestoreLayout(UpdateTransaction transaction)
	{
		transaction.ValidateOwnedDirectoryChain();
		UpdatePayloadIdentity target = transaction.TargetPayload ??
			throw new InvalidDataException($"Recovery has no target payload identity: {transaction.StateFilePath}");
		UpdatePayloadIdentity? original = transaction.OriginalPayload;
		bool currentExists = Directory.Exists(transaction.CurrentDirectoryPath);
		bool stagingExists = Directory.Exists(transaction.StagingDirectoryPath);
		bool previousExists = Directory.Exists(transaction.PreviousDirectoryPath);

		if (original is null)
		{
			if (previousExists || (currentExists == stagingExists))
			{
				throw InvalidLayout(transaction);
			}

			RequireIdentity(currentExists ? transaction.CurrentDirectoryPath : transaction.StagingDirectoryPath, target);
			if (currentExists)
			{
				Move(transaction, transaction.CurrentDirectoryPath, transaction.StagingDirectoryPath);
			}

			return;
		}

		if (!previousExists)
		{
			if (!currentExists || !stagingExists)
			{
				throw InvalidLayout(transaction);
			}

			RequireIdentity(transaction.CurrentDirectoryPath, original);
			RequireIdentity(transaction.StagingDirectoryPath, target);
			return;
		}

		if (currentExists == stagingExists)
		{
			throw InvalidLayout(transaction);
		}

		RequireIdentity(transaction.PreviousDirectoryPath, original);
		RequireIdentity(currentExists ? transaction.CurrentDirectoryPath : transaction.StagingDirectoryPath, target);
		if (currentExists)
		{
			Move(transaction, transaction.CurrentDirectoryPath, transaction.StagingDirectoryPath);
		}

		Move(transaction, transaction.PreviousDirectoryPath, transaction.CurrentDirectoryPath);
	}

	private static InvalidDataException InvalidLayout(UpdateTransaction transaction)
	{
		return new InvalidDataException($"Interrupted update layout is inconsistent; all files were preserved: {transaction.TransactionDirectoryPath}");
	}

	private static void RequireIdentity(string directoryPath, UpdatePayloadIdentity expected)
	{
		if (UpdatePayloadIdentity.Read(directoryPath) != expected)
		{
			throw new InvalidDataException($"Payload identity does not match the interrupted update receipt: {directoryPath}");
		}
	}

	private void Move(UpdateTransaction transaction, string sourcePath, string destinationPath)
	{
		transaction.ValidateOwnedDirectoryChain();
		_moveDirectory(sourcePath, destinationPath);
		transaction.ValidateOwnedDirectoryChain();
	}
}
