using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterTransactionRecoveryTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void Recover_AfterEachUncommittedRename_RestoresOldPayloadAndPreservesUnknownFiles(int completedRenames)
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		string unknownRootPath = Path.Combine(transaction.InstallRootPath, "user-notes.txt");
		File.WriteAllText(unknownRootPath, "user root contents");
		File.WriteAllText(Path.Combine(transaction.CurrentDirectoryPath, "user-file.txt"), "old user contents");
		File.WriteAllText(Path.Combine(transaction.StagingDirectoryPath, "extra-file.txt"), "new extra contents");
		CompleteRenames(transaction, completedRenames);

		RecoverPending(transaction.InstallRootPath);

		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
		Assert.Equal("user root contents", File.ReadAllText(unknownRootPath));
		Assert.Equal("old user contents", File.ReadAllText(Path.Combine(transaction.CurrentDirectoryPath, "user-file.txt")));
		Assert.Equal("new extra contents", File.ReadAllText(Path.Combine(transaction.StagingDirectoryPath, "extra-file.txt")));
		Assert.False(Directory.Exists(transaction.PreviousDirectoryPath));
		Assert.Equal(UpdateTransactionState.RolledBack, Reload(transaction).State);
		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Recover_FirstInstallBeforeCommit_PreservesVerifiedStaging(bool renamed)
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory, hasOriginal: false);
		if (renamed)
		{
			Directory.Move(transaction.StagingDirectoryPath, transaction.CurrentDirectoryPath);
		}

		RecoverPending(transaction.InstallRootPath);

		Assert.False(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
		Assert.Equal(UpdateTransactionState.RolledBack, Reload(transaction).State);
	}

	[Theory]
	[InlineData(1, false)]
	[InlineData(1, true)]
	[InlineData(2, false)]
	[InlineData(2, true)]
	public void Recover_InterruptedAgainAtEachRollbackRename_CanResume(int failingRename, bool failAfterMove)
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 2);
		int moveCount = 0;
		UpdateTransactionRecovery recovery = new((source, destination) =>
		{
			moveCount++;
			if ((moveCount == failingRename) && !failAfterMove)
			{
				throw new IOException("Injected process interruption before recovery rename.");
			}

			Directory.Move(source, destination);
			if (moveCount == failingRename)
			{
				throw new IOException("Injected process interruption after recovery rename.");
			}
		});

		Assert.Throws<IOException>(() => recovery.Recover(transaction));
		RecoverPending(transaction.InstallRootPath);

		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
	}

	[Fact]
	public void Recover_AfterRollbackRenameButBeforeReceiptCommit_FinalizesWithoutAnotherRename()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 1);
		Directory.Move(transaction.PreviousDirectoryPath, transaction.CurrentDirectoryPath);
		UpdateTransactionRecovery recovery = new((_, _) => throw new InvalidOperationException("Unexpected additional rename."));

		recovery.Recover(transaction);

		Assert.Equal(UpdateTransactionState.RolledBack, Reload(transaction).State);
		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
	}

	[Fact]
	public void FindPending_AfterDurableCommit_DoesNotRollBackOrCleanPrevious()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 2);
		transaction.TransitionTo(UpdateTransactionState.Committed);

		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
		new UpdateTransactionRecovery().Recover(transaction);

		Assert.Equal("new", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("old", ReadVersion(transaction.PreviousDirectoryPath));
	}

	[Fact]
	public void Recover_AbandonedStaging_PreservesPartialPayloadAndUncommittedReceipt()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(Path.Combine(directory.Path, "install"));
		Directory.CreateDirectory(transaction.StagingDirectoryPath);
		File.WriteAllText(Path.Combine(transaction.StagingDirectoryPath, "partial.bin"), "partial download");
		string uncommittedPath = Path.Combine(transaction.TransactionDirectoryPath, "receipt-uncommitted.tmp");
		File.WriteAllText(uncommittedPath, "truncated JSON");

		RecoverPending(transaction.InstallRootPath);

		Assert.Equal(UpdateTransactionState.Failed, Reload(transaction).State);
		Assert.Equal("partial download", File.ReadAllText(Path.Combine(transaction.StagingDirectoryPath, "partial.bin")));
		Assert.Equal("truncated JSON", File.ReadAllText(uncommittedPath));
		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
	}

	[Fact]
	public void Recover_WhenApplicationIdentityChanged_RejectsBeforeMovingAnything()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 2);
		File.WriteAllText(Path.Combine(transaction.CurrentDirectoryPath, "app", "AiUsageDashboard.App.exe"), "tampered");

		Assert.Throws<InvalidDataException>(() => new UpdateTransactionRecovery().Recover(transaction));

		Assert.Equal("old", ReadVersion(transaction.PreviousDirectoryPath));
		Assert.True(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.False(Directory.Exists(transaction.StagingDirectoryPath));
		Assert.Equal(UpdateTransactionState.Switching, Reload(transaction).State);
	}

	[Fact]
	public void Recover_WhenAllThreeDirectoriesExist_RejectsAndPreservesUnknownDirectory()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 1);
		Directory.CreateDirectory(transaction.CurrentDirectoryPath);
		File.WriteAllText(Path.Combine(transaction.CurrentDirectoryPath, "unknown.txt"), "preserve");

		Assert.Throws<InvalidDataException>(() => new UpdateTransactionRecovery().Recover(transaction));

		Assert.Equal("preserve", File.ReadAllText(Path.Combine(transaction.CurrentDirectoryPath, "unknown.txt")));
		Assert.Equal("old", ReadVersion(transaction.PreviousDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
	}

	[Theory]
	[InlineData("transactionId", "other-transaction")]
	[InlineData("installRoot", "C:/unrelated-install")]
	[InlineData("packageId", "DifferentApplication")]
	[InlineData("state", "UnrecognizedState")]
	public void FindPending_WithTamperedReceipt_RejectsWithoutRenaming(string property, string value)
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 1);
		JsonObject receipt = JsonNode.Parse(File.ReadAllText(transaction.StateFilePath))!.AsObject();
		receipt[property] = value;
		File.WriteAllText(transaction.StateFilePath, receipt.ToJsonString());

		Assert.Throws<InvalidDataException>(() => UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));

		Assert.False(Directory.Exists(transaction.CurrentDirectoryPath));
		Assert.Equal("old", ReadVersion(transaction.PreviousDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
	}

	[Fact]
	public void FindPending_WithMultipleInterruptedSwitches_RejectsBeforeRecovery()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction first = CreateSwitchingTransaction(directory);
		UpdateTransaction second = UpdateTransaction.Create(first.InstallRootPath);
		UpdateRecoveryTestPayload.Write(second.StagingDirectoryPath, "another-target");
		PrepareSwitch(second);

		Assert.Throws<InvalidDataException>(() => UpdateTransactionRecovery.FindPending(first.InstallRootPath));

		Assert.Equal("old", ReadVersion(first.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(first.StagingDirectoryPath));
		Assert.Equal("another-target", ReadVersion(second.StagingDirectoryPath));
	}

	[Fact]
	public void FindPending_WithLegacyCommittedReceipt_PreservesHistory()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = UpdateTransaction.Create(Path.Combine(directory.Path, "install"));
		string legacyReceipt = JsonSerializer.Serialize(new
		{
			transactionId = transaction.TransactionId,
			state = "Committed",
			updatedAtUtc = DateTimeOffset.UnixEpoch
		});
		File.WriteAllText(transaction.StateFilePath, legacyReceipt);

		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
		Assert.Equal(legacyReceipt, File.ReadAllText(transaction.StateFilePath));
	}

	[Fact]
	public void FindPending_WithLegacyInterruptedReceipt_FailsClosedAndPreservesPrevious()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		CompleteRenames(transaction, 1);
		File.WriteAllText(transaction.StateFilePath, JsonSerializer.Serialize(new
		{
			transactionId = transaction.TransactionId,
			state = "Switching",
			updatedAtUtc = DateTimeOffset.UnixEpoch
		}));

		Assert.Throws<InvalidDataException>(() => UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));

		Assert.Equal("old", ReadVersion(transaction.PreviousDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
	}

	[Fact]
	public void FindPending_WithUnownedDirectory_PreservesItWithoutParsingItsName()
	{
		using TemporaryDirectory directory = new();
		string installRoot = Path.Combine(directory.Path, "install");
		string userDirectory = Path.Combine(installRoot, "transactions", "user notes");
		Directory.CreateDirectory(userDirectory);
		File.WriteAllText(Path.Combine(userDirectory, "notes.txt"), "user notes");

		Assert.Empty(UpdateTransactionRecovery.FindPending(installRoot));
		Assert.Equal("user notes", File.ReadAllText(Path.Combine(userDirectory, "notes.txt")));
	}

	[Fact]
	public void Switch_WhenDurableIntentWriteFails_DoesNotRenameAndRetainsPriorReceipt()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = UpdateTransaction.CreateCore(Path.Combine(directory.Path, "install"), null,
			(source, destination) =>
			{
				using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(source));
				if (receipt.RootElement.GetProperty("state").GetString() == "Switching")
				{
					throw new IOException("Injected disk-full receipt persistence failure.");
				}

				File.Move(source, destination, overwrite: true);
			});
		UpdateRecoveryTestPayload.Write(transaction.CurrentDirectoryPath, "old");
		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "new");
		transaction.TransitionTo(UpdateTransactionState.Staged);
		transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
		transaction.TransitionTo(UpdateTransactionState.AppExited);

		Assert.Throws<IOException>(() => new UpdateInstallationSwitcher().Switch(transaction));

		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
		Assert.Equal(UpdateTransactionState.AppExited, Reload(transaction).State);
		Assert.NotEmpty(Directory.EnumerateFiles(transaction.TransactionDirectoryPath, "receipt-*.tmp"));
		RecoverPending(transaction.InstallRootPath);
		Assert.Equal(UpdateTransactionState.Failed, Reload(transaction).State);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Recover_WhenPreviousWasReplacedByJunction_FailsClosed()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		string unrelatedDirectory = Path.Combine(directory.Path, "unrelated");
		UpdateRecoveryTestPayload.Write(unrelatedDirectory, "outside");
		await JunctionTestHelper.CreateAsync(transaction.PreviousDirectoryPath, unrelatedDirectory);
		try
		{
			Assert.Throws<InvalidDataException>(() => new UpdateTransactionRecovery().Recover(transaction));
			Assert.Equal("outside", ReadVersion(unrelatedDirectory));
			Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		}
		finally
		{
			JunctionTestHelper.Delete(transaction.PreviousDirectoryPath);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Switch_WhenCommitPersistenceFails_RollbackRemainsRecoverable(bool failRollbackReceipt)
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = UpdateTransaction.CreateCore(Path.Combine(directory.Path, "install"), null,
			(source, destination) =>
			{
				using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(source));
				string? state = receipt.RootElement.GetProperty("state").GetString();
				if ((state == "Committed") || (failRollbackReceipt && (state == "RolledBack")))
				{
					throw new IOException("Injected disk-full receipt persistence failure.");
				}

				File.Move(source, destination, overwrite: true);
			});
		UpdateRecoveryTestPayload.Write(transaction.CurrentDirectoryPath, "old");
		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "new");
		transaction.TransitionTo(UpdateTransactionState.Staged);
		transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
		transaction.TransitionTo(UpdateTransactionState.AppExited);

		Assert.Throws<UpdateInstallationSwitchException>(() => new UpdateInstallationSwitcher().Switch(transaction));

		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
		Assert.Equal(failRollbackReceipt ? UpdateTransactionState.Failed : UpdateTransactionState.RolledBack, Reload(transaction).State);
		RecoverPending(transaction.InstallRootPath);
		Assert.Equal(UpdateTransactionState.RolledBack, Reload(transaction).State);
		Assert.Empty(UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));
	}

	[Fact]
	public void FindPending_WhenInitialReceiptWasNotCommitted_PreservesOrphanWithoutClaimingOwnership()
	{
		using TemporaryDirectory directory = new();
		string installRoot = Path.Combine(directory.Path, "install");
		Assert.Throws<IOException>(() => UpdateTransaction.CreateCore(installRoot, "interrupted-create", (_, _) =>
			throw new IOException("Injected disk-full initial receipt failure.")));
		string transactionDirectory = Path.Combine(installRoot, "transactions", "interrupted-create");
		string temporaryReceipt = Assert.Single(Directory.EnumerateFiles(transactionDirectory));
		byte[] originalBytes = File.ReadAllBytes(temporaryReceipt);

		Assert.Empty(UpdateTransactionRecovery.FindPending(installRoot));

		Assert.Equal(originalBytes, File.ReadAllBytes(temporaryReceipt));
		Assert.False(File.Exists(Path.Combine(transactionDirectory, "transaction.json")));
	}

	[Fact]
	public void FindPending_WhenReceiptContainsDuplicateState_RejectsAmbiguousEvidence()
	{
		using TemporaryDirectory directory = new();
		UpdateTransaction transaction = CreateSwitchingTransaction(directory);
		string receipt = File.ReadAllText(transaction.StateFilePath);
		receipt = receipt.Insert(receipt.IndexOf('{') + 1, "\"state\":\"Committed\",");
		File.WriteAllText(transaction.StateFilePath, receipt);

		Assert.Throws<InvalidDataException>(() => UpdateTransactionRecovery.FindPending(transaction.InstallRootPath));

		Assert.Equal("old", ReadVersion(transaction.CurrentDirectoryPath));
		Assert.Equal("new", ReadVersion(transaction.StagingDirectoryPath));
	}

	private static UpdateTransaction CreateSwitchingTransaction(TemporaryDirectory directory, bool hasOriginal = true)
	{
		UpdateTransaction transaction = UpdateTransaction.Create(Path.Combine(directory.Path, "install"));
		if (hasOriginal)
		{
			UpdateRecoveryTestPayload.Write(transaction.CurrentDirectoryPath, "old");
		}

		UpdateRecoveryTestPayload.Write(transaction.StagingDirectoryPath, "new");
		PrepareSwitch(transaction);
		return transaction;
	}

	private static void PrepareSwitch(UpdateTransaction transaction)
	{
		transaction.TransitionTo(UpdateTransactionState.Staged);
		transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
		transaction.TransitionTo(UpdateTransactionState.AppExited);
		transaction.BeginSwitch();
	}

	private static void CompleteRenames(UpdateTransaction transaction, int completedRenames)
	{
		if (completedRenames >= 1)
		{
			Directory.Move(transaction.CurrentDirectoryPath, transaction.PreviousDirectoryPath);
		}

		if (completedRenames == 2)
		{
			Directory.Move(transaction.StagingDirectoryPath, transaction.CurrentDirectoryPath);
		}
	}

	private static void RecoverPending(string installRoot)
	{
		foreach (UpdateTransaction transaction in UpdateTransactionRecovery.FindPending(installRoot))
		{
			new UpdateTransactionRecovery().Recover(transaction);
		}
	}

	private static UpdateTransaction Reload(UpdateTransaction transaction)
	{
		return UpdateTransaction.Load(transaction.InstallRootPath, transaction.TransactionId);
	}

	private static string ReadVersion(string directoryPath)
	{
		return File.ReadAllText(Path.Combine(directoryPath, "version.txt"));
	}
}
