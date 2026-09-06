using System.Text.Json;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterTransactionTests
{
	[Fact]
	public void Create_CreatesSameRootLayoutAndPreparedReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRootPath = Path.Combine(temporaryDirectory.Path, "install");

		UpdateTransaction transaction = UpdateTransaction.Create(
			installRootPath,
			"transaction-1");

		Assert.Equal(Path.GetFullPath(installRootPath), transaction.InstallRootPath);
		Assert.Equal(
			Path.Combine(installRootPath, "current"),
			transaction.CurrentDirectoryPath);
		Assert.Equal(
			Path.Combine(installRootPath, "transactions", "transaction-1", "staging"),
			transaction.StagingDirectoryPath);
		Assert.Equal(
			Path.Combine(installRootPath, "transactions", "transaction-1", "previous"),
			transaction.PreviousDirectoryPath);
		Assert.Equal(UpdateTransactionState.Prepared, transaction.State);
		using JsonDocument receipt = JsonDocument.Parse(
			File.ReadAllText(transaction.StateFilePath));
		Assert.Equal(
			"Prepared",
			receipt.RootElement.GetProperty("state").GetString());
	}

	[Fact]
	public void TransitionTo_WithExpectedSequence_PersistsEveryState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "new");

		UpdateTransactionState[] states =
		[
			UpdateTransactionState.Staged,
			UpdateTransactionState.ShutdownRequested,
			UpdateTransactionState.AppExited,
			UpdateTransactionState.Switching,
			UpdateTransactionState.Committed
		];
		foreach (UpdateTransactionState state in states)
		{
			if (state == UpdateTransactionState.Switching)
			{
				transaction.BeginSwitch();
			}
			else
			{
				transaction.TransitionTo(state);
			}
			Assert.Equal(state, transaction.State);
			Assert.Contains(
				$"\"state\": \"{state}\"",
				File.ReadAllText(transaction.StateFilePath),
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void TransitionTo_WhenStateIsSkipped_RejectsWithoutChangingReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		string originalReceipt = File.ReadAllText(transaction.StateFilePath);

		Assert.Throws<InvalidOperationException>(() =>
			transaction.TransitionTo(UpdateTransactionState.AppExited));

		Assert.Equal(UpdateTransactionState.Prepared, transaction.State);
		Assert.Equal(originalReceipt, File.ReadAllText(transaction.StateFilePath));
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("nested/transaction")]
	[InlineData("transaction id")]
	public void Create_WithUnsafeTransactionId_Rejects(string transactionId)
	{
		using TemporaryDirectory temporaryDirectory = new();

		Assert.Throws<ArgumentException>(() => UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			transactionId));
	}

	[Fact]
	public void Create_WhenTransactionAlreadyExists_RejectsWithoutOverwritingReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRootPath = Path.Combine(temporaryDirectory.Path, "install");
		UpdateTransaction first = UpdateTransaction.Create(
			installRootPath,
			"existing");
		string originalReceipt = File.ReadAllText(first.StateFilePath);

		Assert.Throws<IOException>(() => UpdateTransaction.Create(
			installRootPath,
			"existing"));

		Assert.Equal(originalReceipt, File.ReadAllText(first.StateFilePath));
	}

	[Fact]
	public async Task Create_WhenTransactionsRootIsJunction_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRootPath = Path.Combine(temporaryDirectory.Path, "install");
		string junctionPath = Path.Combine(installRootPath, "transactions");
		string junctionTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"junction-target");
		Directory.CreateDirectory(installRootPath);
		Directory.CreateDirectory(junctionTargetPath);
		await JunctionTestHelper.CreateAsync(junctionPath, junctionTargetPath);

		try
		{
			Assert.Throws<InvalidDataException>(() =>
				UpdateTransaction.Create(installRootPath));
			Assert.Empty(Directory.EnumerateFileSystemEntries(junctionTargetPath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Create_WhenInstallRootAncestorIsJunction_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string junctionPath = Path.Combine(
			temporaryDirectory.Path,
			"junction-parent");
		string junctionTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"junction-target");
		Directory.CreateDirectory(junctionTargetPath);
		await JunctionTestHelper.CreateAsync(junctionPath, junctionTargetPath);

		try
		{
			Assert.Throws<InvalidDataException>(() =>
				UpdateTransaction.Create(Path.Combine(junctionPath, "install")));
			Assert.Empty(Directory.EnumerateFileSystemEntries(junctionTargetPath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}
}
