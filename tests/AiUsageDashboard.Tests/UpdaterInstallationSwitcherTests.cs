using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterInstallationSwitcherTests
{
	[Fact]
	public void Switch_WithNoCurrentInstallation_CommitsFirstInstallBySingleRename()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "first");
		transaction.TransitionTo(UpdateTransactionState.Staged);
		transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
		transaction.TransitionTo(UpdateTransactionState.AppExited);
		List<(string Source, string Destination)> moves = [];
		UpdateInstallationSwitcher switcher = new((sourcePath, destinationPath) =>
		{
			moves.Add((sourcePath, destinationPath));
			Directory.Move(sourcePath, destinationPath);
		});

		switcher.Switch(transaction);

		Assert.Single(moves);
		Assert.Equal(UpdateTransactionState.Committed, transaction.State);
		Assert.Equal(
			"first",
			File.ReadAllText(Path.Combine(
				transaction.CurrentDirectoryPath,
				"version.txt")));
		Assert.False(Directory.Exists(transaction.PreviousDirectoryPath));
	}

	[Fact]
	public void Switch_WithPreparedLayout_CommitsByRenameAndPreservesPrevious()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = CreateReadyTransaction(temporaryDirectory);

		new UpdateInstallationSwitcher().Switch(transaction);

		Assert.Equal(UpdateTransactionState.Committed, transaction.State);
		Assert.Equal(
			"new",
			File.ReadAllText(Path.Combine(
				transaction.CurrentDirectoryPath,
				"version.txt")));
		Assert.Equal(
			"old",
			File.ReadAllText(Path.Combine(
				transaction.PreviousDirectoryPath,
				"version.txt")));
		Assert.False(Directory.Exists(transaction.StagingDirectoryPath));
	}

	[Fact]
	public void Switch_WhenSecondRenameFails_RestoresPreviousInstallation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = CreateReadyTransaction(temporaryDirectory);
		int moveCount = 0;
		UpdateInstallationSwitcher switcher = new((sourcePath, destinationPath) =>
		{
			moveCount++;

			if (moveCount == 2)
			{
				throw new IOException("Synthetic second rename failure.");
			}

			Directory.Move(sourcePath, destinationPath);
		});

		UpdateInstallationSwitchException exception = Assert.Throws<
			UpdateInstallationSwitchException>(() => switcher.Switch(transaction));

		Assert.Contains("restored", exception.Message, StringComparison.Ordinal);
		Assert.Equal(UpdateTransactionState.RolledBack, transaction.State);
		Assert.Equal(
			"old",
			File.ReadAllText(Path.Combine(
				transaction.CurrentDirectoryPath,
				"version.txt")));
		Assert.Equal(
			"new",
			File.ReadAllText(Path.Combine(
				transaction.StagingDirectoryPath,
				"version.txt")));
		Assert.False(Directory.Exists(transaction.PreviousDirectoryPath));
	}

	[Fact]
	public void Switch_WhenRollbackRenameFails_PreservesEvidenceAndMarksFailed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = CreateReadyTransaction(temporaryDirectory);
		int moveCount = 0;
		UpdateInstallationSwitcher switcher = new((sourcePath, destinationPath) =>
		{
			moveCount++;

			if (moveCount >= 2)
			{
				throw new IOException("Synthetic rename failure.");
			}

			Directory.Move(sourcePath, destinationPath);
		});

		UpdateInstallationSwitchException exception = Assert.Throws<
			UpdateInstallationSwitchException>(() => switcher.Switch(transaction));

		Assert.Contains(
			"both failed",
			exception.Message,
			StringComparison.Ordinal);
		Assert.Equal(UpdateTransactionState.Failed, transaction.State);
		Assert.True(Directory.Exists(transaction.PreviousDirectoryPath));
		Assert.False(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.True(Directory.Exists(transaction.StagingDirectoryPath));
	}

	[Fact]
	public void Switch_WhenPreviousDestinationExists_RejectsBeforeChangingLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = CreateReadyTransaction(temporaryDirectory);
		Directory.CreateDirectory(transaction.PreviousDirectoryPath);

		Assert.Throws<IOException>(() =>
			new UpdateInstallationSwitcher().Switch(transaction));

		Assert.Equal(UpdateTransactionState.AppExited, transaction.State);
		Assert.True(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.True(Directory.Exists(transaction.StagingDirectoryPath));
	}

	[Fact]
	public void Switch_BeforeApplicationExit_RejectsBeforeChangingLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		Directory.CreateDirectory(transaction.CurrentDirectoryPath);
		Directory.CreateDirectory(transaction.StagingDirectoryPath);

		Assert.Throws<InvalidOperationException>(() =>
			new UpdateInstallationSwitcher().Switch(transaction));

		Assert.Equal(UpdateTransactionState.Prepared, transaction.State);
		Assert.True(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.True(Directory.Exists(transaction.StagingDirectoryPath));
	}

	private static UpdateTransaction CreateReadyTransaction(
		TemporaryDirectory temporaryDirectory)
	{
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		UpdateRecoveryTestPayload.Write(transaction.CurrentDirectoryPath, "old");
		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "new");
		transaction.TransitionTo(UpdateTransactionState.Staged);
		transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
		transaction.TransitionTo(UpdateTransactionState.AppExited);
		return transaction;
	}
}
