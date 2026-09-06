using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;

using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.Tests;

public sealed class PortableSettingsImportTransactionTests
{
	[Fact]
	public async Task ExecuteAsync_WhenOperationSucceeds_CommitsEveryTargetAndCleansJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(firstPath, "old-accounts");
		await File.WriteAllTextAsync(secondPath, "old-preferences");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[firstPath, secondPath]);

		await transaction.ExecuteAsync(async cancellationToken =>
		{
			await File.WriteAllTextAsync(
				firstPath,
				"new-accounts",
				cancellationToken);
			await File.WriteAllTextAsync(
				secondPath,
				"new-preferences",
				cancellationToken);
		});

		Assert.Equal("new-accounts", await File.ReadAllTextAsync(firstPath));
		Assert.Equal("new-preferences", await File.ReadAllTextAsync(secondPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenCrashOccursBeforeCommitCallback_RunsCallbackFromCommittedJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int callbackCount = 0;
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				throw new IOException("Synthetic callback failure before authorization.");
			});

		await Assert.ThrowsAsync<PortableSettingsImportCommittedException>(
			() => interruptedTransaction.ExecuteAsync(cancellationToken =>
				File.WriteAllTextAsync(
					targetPath,
					"new-accounts",
					cancellationToken)));

		Assert.Equal(1, callbackCount);
		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(2, callbackCount);
		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenCrashOccursAfterCommitCallbackBeforeReceipt_RunsCallbackIdempotently()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int durableAuthorizationCount = 0;
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				durableAuthorizationCount++;
				return Task.CompletedTask;
			},
			flushCommitCallbackCompletedMarkerToDisk: _ =>
				throw new IOException("Synthetic receipt flush failure."));

		await Assert.ThrowsAsync<PortableSettingsImportCommittedException>(
			() => interruptedTransaction.ExecuteAsync(cancellationToken =>
				File.WriteAllTextAsync(
					targetPath,
					"new-accounts",
					cancellationToken)));

		Assert.Equal(1, durableAuthorizationCount);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				durableAuthorizationCount++;
				return Task.CompletedTask;
			});
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(2, durableAuthorizationCount);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenCrashOccursAfterCommitReceipt_SkipsCallbackAndOnlyCleansJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int callbackCount = 0;
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			},
			deleteTransactionFile: filePath =>
			{
				if (string.Equals(
						Path.GetFileName(filePath),
						"committed",
						StringComparison.Ordinal))
				{
					throw new IOException(
						"Synthetic committed marker cleanup failure.");
				}

				File.Delete(filePath);
			});

		await interruptedTransaction.ExecuteAsync(cancellationToken =>
			File.WriteAllTextAsync(
				targetPath,
				"new-accounts",
				cancellationToken));

		Assert.Equal(1, callbackCount);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.True(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, callbackCount);
		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenOldCommittedCleanupIsPartial_DoesNotAuthorizeNewPendingGeneration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string cleanupPath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v2.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile retainedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Retained Copilot");
		JsonAccountProfileStore accountStore = new(accountPath);
		await accountStore.LoadAsync();
		await accountStore.SaveAsync([retainedCopilot]);
		JsonAccountCleanupPendingStore cleanupStore = new(cleanupPath);
		var cleanupKey = (retainedCopilot.Id, retainedCopilot.Provider);
		await cleanupStore.UpsertAsync([cleanupKey]);
		int callbackCount = 0;
		PortableSettingsImportTransaction oldTransaction = new(
			transactionPath,
			[accountPath],
			onTargetsCommitted: async cancellationToken =>
			{
				callbackCount++;
				await AiUsageDashboard.App.App
					.AuthorizeCommittedPortableCopilotCleanupAsync(
						accountStore,
						cleanupStore,
						cancellationToken);
			},
			deleteTransactionFile: filePath =>
			{
				if (string.Equals(
						Path.GetFileName(filePath),
						"committed",
						StringComparison.Ordinal))
				{
					throw new IOException(
						"Synthetic committed marker cleanup failure.");
				}

				File.Delete(filePath);
			});

		await oldTransaction.ExecuteAsync(_ => Task.CompletedTask);

		Assert.True(Assert.Single(await cleanupStore.LoadAsync())
			.AllowRetainedPrivateStateDeletion);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.True(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		await cleanupStore.MarkCacheCompletedAsync(
			cleanupKey.Id,
			cleanupKey.Provider);
		await cleanupStore.MarkRuntimeCompletedAsync(
			cleanupKey.Id,
			cleanupKey.Provider);
		Assert.Empty(await cleanupStore.LoadAsync());
		await cleanupStore.UpsertAsync([cleanupKey]);

		PortableSettingsImportTransaction nextTransaction = new(
			transactionPath,
			[accountPath],
			onTargetsCommitted: async cancellationToken =>
			{
				callbackCount++;
				await AiUsageDashboard.App.App
					.AuthorizeCommittedPortableCopilotCleanupAsync(
						accountStore,
						cleanupStore,
						cancellationToken);
			});

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => nextTransaction.ExecuteAsync(_ =>
				throw new InvalidOperationException(
					"Synthetic next-generation pre-commit failure.")));

		Assert.Equal(1, callbackCount);
		AccountCleanupPendingWork nextGeneration = Assert.Single(
			await cleanupStore.LoadAsync());
		Assert.False(nextGeneration.AllowRetainedPrivateStateDeletion);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOnlyCommitReceiptRemains_CleansWithoutReplayingCallback()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int callbackCount = 0;
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			},
			deleteTransactionFile: filePath =>
			{
				if (string.Equals(
						Path.GetFileName(filePath),
						"commit-callback-completed",
						StringComparison.Ordinal))
				{
					throw new IOException(
						"Synthetic receipt cleanup failure.");
				}

				File.Delete(filePath);
			});

		await interruptedTransaction.ExecuteAsync(cancellationToken =>
			File.WriteAllTextAsync(
				targetPath,
				"new-accounts",
				cancellationToken));

		Assert.Equal(1, callbackCount);
		Assert.False(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.True(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, callbackCount);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenCleanupFails_PreservesCommitReceiptUntilRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		List<string> deletionAttempts = [];
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			deleteTransactionFile: filePath =>
			{
				string fileName = Path.GetFileName(filePath);
				deletionAttempts.Add(fileName);

				if (string.Equals(
					fileName,
					"prepared",
					StringComparison.Ordinal))
				{
					throw new IOException("Synthetic cleanup failure.");
				}

				File.Delete(filePath);
			});

		await transaction.ExecuteAsync(cancellationToken =>
			File.WriteAllTextAsync(
				targetPath,
				"new-accounts",
				cancellationToken));

		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		Assert.DoesNotContain("commit-callback-completed", deletionAttempts);

		PortableSettingsImportTransaction retryingTransaction = new(
			transactionPath,
			[targetPath]);
		await retryingTransaction.RecoverInterruptedImportAsync();

		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenBothTerminalMarkersExist_FailsClosedAndPreservesJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "committed"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "restored"),
			[]);
		await File.WriteAllTextAsync(targetPath, "current-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath]);

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(
				() => transaction.RecoverInterruptedImportAsync());

		Assert.Contains("互相衝突", exception.Message, StringComparison.Ordinal);
		Assert.Equal("current-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.True(File.Exists(Path.Combine(transactionPath, "restored")));
	}

	[Fact]
	public async Task ExecuteAsync_WhenExistingTargetExceedsLimit_RejectsBeforeCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "oversized-preferences");
		bool operationWasInvoked = false;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			maximumTargetFileSizes: [8]);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => transaction.ExecuteAsync(_ =>
			{
				operationWasInvoked = true;
				return Task.CompletedTask;
			}));

		Assert.Contains("8 位元組", exception.Message, StringComparison.Ordinal);
		Assert.False(operationWasInvoked);
		Assert.Equal(
			"oversized-preferences",
			await File.ReadAllTextAsync(targetPath));
		Assert.False(File.Exists(Path.Combine(transactionPath, "target-0.backup")));

		await transaction.RecoverInterruptedImportAsync();
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenCurrentTargetExceedsLimit_RejectsBeforeHash()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		const string oversizedContents = "oversized-current-preferences";
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.operation-baseline.backup"),
			"old");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-started"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		string ownedHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(oversizedContents)))
			.ToLowerInvariant();
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, $"target-0.owned-{ownedHash}"),
			[]);
		await File.WriteAllTextAsync(targetPath, oversizedContents);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			maximumTargetFileSizes: [8]);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => transaction.RecoverInterruptedImportAsync());

		Assert.Contains("8 位元組", exception.Message, StringComparison.Ordinal);
		Assert.Equal(oversizedContents, await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.Empty(Directory.GetFiles(
			temporaryDirectory.Path,
			"*.rollback.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task ExecuteAsync_WhenSecondWriteFails_RestoresAllOriginalFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(firstPath, "old-accounts");
		await File.WriteAllTextAsync(secondPath, "old-preferences");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[firstPath, secondPath]);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => transaction.ExecuteAsync(async cancellationToken =>
			{
				await File.WriteAllTextAsync(
					firstPath,
					"new-accounts",
					cancellationToken);
				throw new InvalidOperationException("second write failed");
			}));

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(firstPath));
		Assert.Equal("old-preferences", await File.ReadAllTextAsync(secondPath));
		Assert.False(Directory.Exists(transactionPath));
		Assert.False(File.Exists(transactionPath + ".lock"));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenPrepared_RestoresPresentAndAbsentTargets()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "target-1.absent"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(firstPath, "partially-imported-accounts");
		await File.WriteAllTextAsync(secondPath, "partially-imported-preferences");
		PortableSettingsImportRestoreResult? restoreResult = null;
		int commitCallbackCount = 0;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[firstPath, secondPath],
			onTargetsRestoredWithResult: (result, _) =>
			{
				restoreResult = result;
				return Task.CompletedTask;
			},
			onTargetsCommitted: _ =>
			{
				commitCallbackCount++;
				return Task.CompletedTask;
			});

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(firstPath));
		Assert.False(File.Exists(secondPath));
		Assert.NotNull(restoreResult);
		Assert.True(restoreResult.GetTargetResult(firstPath)
			.DoesFinalTargetMatchRollbackBaseline);
		Assert.True(restoreResult.GetTargetResult(secondPath)
			.DoesFinalTargetMatchRollbackBaseline);
		Assert.Equal(0, commitCallbackCount);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOperationWasNotStarted_PreservesLaterTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "later-external-accounts");
		PortableSettingsImportRestoreResult? restoreResult = null;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			onTargetsRestoredWithResult: (result, _) =>
			{
				restoreResult = result;
				return Task.CompletedTask;
			});

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal(
			"later-external-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.NotNull(restoreResult);
		PortableSettingsImportTargetRestoreResult targetResult =
			restoreResult.GetTargetResult(targetPath);
		Assert.False(targetResult.DoesFinalTargetMatchRollbackBaseline);
		Assert.Equal(
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old-accounts"))),
			targetResult.RollbackBaselineState?.ContentFingerprint,
			ignoreCase: true);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOperationWasNotStartedAndPrepareBaselineIsMissing_PreservesJournalWithoutRestoredMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "later-external-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath]);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => transaction.RecoverInterruptedImportAsync());

		Assert.Equal(
			"later-external-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.False(File.Exists(Path.Combine(transactionPath, "restored")));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOperationWasStarted_UsesOperationBaseline()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllTextAsync(
			Path.Combine(
				transactionPath,
				"target-0.operation-baseline.backup"),
			"external-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-started"),
			[]);
		await File.WriteAllTextAsync(targetPath, "partially-imported-accounts");
		string ownedHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes("partially-imported-accounts")))
			.ToLowerInvariant();
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, $"target-0.owned-{ownedHash}"),
			[]);
		PortableSettingsImportRestoreResult? restoreResult = null;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			onTargetsRestoredWithResult: (result, _) =>
			{
				restoreResult = result;
				return Task.CompletedTask;
			});

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal(
			"external-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.NotNull(restoreResult);
		PortableSettingsImportTargetRestoreResult targetResult =
			restoreResult.GetTargetResult(targetPath);
		Assert.True(targetResult.DoesFinalTargetMatchRollbackBaseline);
		Assert.Equal(
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("external-accounts"))),
			targetResult.RollbackBaselineState?.ContentFingerprint,
			ignoreCase: true);
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenUnownedTargetLacksOperationBaseline_PreservesJournalWithoutRestoredMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-started"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "later-external-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			writeTracker: new PortableSettingsImportWriteTracker());

		await Assert.ThrowsAsync<InvalidDataException>(
			() => transaction.RecoverInterruptedImportAsync());

		Assert.Equal(
			"later-external-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.False(File.Exists(Path.Combine(transactionPath, "restored")));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOwnedTargetWasReplacedExternally_PreservesLaterWriter()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllTextAsync(
			Path.Combine(
				transactionPath,
				"target-0.operation-baseline.backup"),
			"operation-baseline-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-baseline-v1"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "operation-started"),
			[]);
		string ownedHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes("partially-imported-accounts")))
			.ToLowerInvariant();
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, $"target-0.owned-{ownedHash}"),
			[]);
		await File.WriteAllTextAsync(targetPath, "later-external-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath]);

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal(
			"later-external-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenCommitted_KeepsImportedTargets()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "committed"),
			[]);
		await File.WriteAllTextAsync(targetPath, "new-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath]);

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenRollbackCleanupWasBlocked_DoesNotReplayOldBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		string unexpectedPath = Path.Combine(transactionPath, "do-not-delete.txt");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath]);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => interruptedTransaction.ExecuteAsync(async cancellationToken =>
			{
				await File.WriteAllTextAsync(
					targetPath,
					"partially-imported-accounts",
					cancellationToken);
				await File.WriteAllTextAsync(
					unexpectedPath,
					"keep the journal directory",
					cancellationToken);
				throw new InvalidOperationException("Synthetic import failure.");
			}));

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "restored")));
		Assert.True(Directory.Exists(transactionPath));

		File.Delete(unexpectedPath);
		await File.WriteAllTextAsync(targetPath, "later-legitimate-accounts");
		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath]);

		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(
			"later-legitimate-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenOriginallyAbsentTargetIsWrittenThenOperationFails_RemovesIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath]);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => transaction.ExecuteAsync(async cancellationToken =>
			{
				await File.WriteAllTextAsync(
					targetPath,
					"new-accounts",
					cancellationToken);
				throw new InvalidOperationException("import failed");
			}));

		Assert.False(File.Exists(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenAtomicReplaceIsDenied_RestoresInPlace()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "new-accounts");
		int replaceAttempts = 0;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			(_, _) =>
			{
				replaceAttempts++;
				throw new UnauthorizedAccessException(
					"Synthetic restricted-token replacement denial.");
			});

		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, replaceAttempts);
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenTrackedAtomicRestoreIsDenied_KeepsOwnedTargetAndJournalForRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonDashboardPreferencesStore store = new(
			targetPath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		await store.SaveUsagePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		byte[] originalContents = await File.ReadAllBytesAsync(targetPath);
		int deniedReplaceAttempts = 0;
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			replaceFileAtomically: (_, _) =>
			{
				deniedReplaceAttempts++;
				throw new UnauthorizedAccessException(
					"Synthetic tracked atomic replacement denial.");
			},
			persistenceGate: persistenceGate,
			writeTracker: writeTracker);

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => interruptedTransaction.ExecuteAsync(
					async cancellationToken =>
					{
						await store.SaveUsagePreferencesAsync(
							UsageSortMode.Automatic,
							UsageDisplayMode.Remaining,
							cancellationToken);
						throw new IOException(
							"Synthetic post-write import failure.");
					}));

		AggregateException aggregate = Assert.IsType<AggregateException>(
			exception.InnerException);
		Assert.Contains(
			aggregate.InnerExceptions,
			inner => inner is UnauthorizedAccessException);
		Assert.Equal(1, deniedReplaceAttempts);
		Assert.NotEqual(
			originalContents,
			await File.ReadAllBytesAsync(targetPath));
		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.True(File.Exists(
			Path.Combine(transactionPath, "operation-started")));
		Assert.False(File.Exists(Path.Combine(transactionPath, "restored")));
		Assert.Single(Directory.GetFiles(transactionPath, "target-0.owned-*"));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath],
			persistenceGate: persistenceGate);
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(originalContents, await File.ReadAllBytesAsync(targetPath));
		Assert.Equal(
			UsageSortMode.Manual,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Used,
			await store.LoadUsageDisplayModeAsync());
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenCommittedMarkerFlushFails_RollsBackBeforePublishingFinalMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		string committedMarkerPath = Path.Combine(transactionPath, "committed");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		string? temporaryMarkerPath = null;
		bool finalMarkerWasVisibleDuringFlush = false;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			flushCommittedMarkerToDisk: stream =>
			{
				temporaryMarkerPath = stream.Name;
				finalMarkerWasVisibleDuringFlush = File.Exists(
					committedMarkerPath);
				throw new IOException("Synthetic committed-marker flush failure.");
			});

		await Assert.ThrowsAsync<IOException>(
			() => transaction.ExecuteAsync(async cancellationToken =>
			{
				await File.WriteAllTextAsync(
					targetPath,
					"new-accounts",
					cancellationToken);
			}));

		Assert.NotNull(temporaryMarkerPath);
		Assert.StartsWith(
			"committed.",
			Path.GetFileName(temporaryMarkerPath),
			StringComparison.Ordinal);
		Assert.EndsWith(
			".tmp",
			Path.GetFileName(temporaryMarkerPath),
			StringComparison.Ordinal);
		Assert.False(finalMarkerWasVisibleDuringFlush);
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(File.Exists(committedMarkerPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenRestoredMarkerFlushFails_KeepsPreparedJournalForRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			flushRestoredMarkerToDisk: _ => throw new IOException(
				"Synthetic restored-marker flush failure."));

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => interruptedTransaction.ExecuteAsync(
					async cancellationToken =>
					{
						await File.WriteAllTextAsync(
							targetPath,
							"partially-imported-accounts",
							cancellationToken);
						throw new InvalidOperationException(
							"Synthetic import failure.");
					}));

		Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.False(File.Exists(Path.Combine(transactionPath, "restored")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath]);
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenMarkerFlushAndRollbackFail_RestartRestoresPreparedTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		PortableSettingsImportTransaction interruptedTransaction = new(
			transactionPath,
			[targetPath],
			replaceFileAtomically: (_, _) => throw new IOException(
				"Synthetic rollback interruption."),
			flushCommittedMarkerToDisk: _ => throw new IOException(
				"Synthetic committed-marker flush failure."));

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => interruptedTransaction.ExecuteAsync(
					async cancellationToken =>
					{
						await File.WriteAllTextAsync(
							targetPath,
							"new-accounts",
							cancellationToken);
					}));

		Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.False(File.Exists(Path.Combine(transactionPath, "committed")));

		PortableSettingsImportTransaction restartedTransaction = new(
			transactionPath,
			[targetPath]);
		await restartedTransaction.RecoverInterruptedImportAsync();

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenPublisherThrowsAfterRename_DoesNotRollBackCommittedTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			publishCommittedMarkerAtomically: (source, destination) =>
			{
				File.Move(source, destination);
				throw new IOException("Synthetic post-rename failure.");
			});

		await transaction.ExecuteAsync(async cancellationToken =>
		{
			await File.WriteAllTextAsync(
				targetPath,
				"new-accounts",
				cancellationToken);
		});

		Assert.Equal("new-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenAccountSaveIsRolledBack_SynchronizesStoreForNextSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile originalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Original");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile finalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Final");
		JsonAccountProfileStore store = new(accountPath);
		await store.LoadAsync();
		await store.SaveAsync([originalAccount]);
		byte[] originalPrimary = await File.ReadAllBytesAsync(accountPath);
		byte[] originalBackup = await File.ReadAllBytesAsync($"{accountPath}.bak");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak"],
			flushCommittedMarkerToDisk: _ => throw new IOException(
				"Synthetic commit marker failure."),
			onTargetsRestored:
				store.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting: store.BeginPortableImportTransactionAsync,
			onTransactionFinished: store.CompletePortableImportTransaction);

		await Assert.ThrowsAsync<IOException>(
			() => transaction.ExecuteAsync(
				cancellationToken => store.SaveAsync(
					[importedAccount],
					cancellationToken)))
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(originalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(originalBackup, await File.ReadAllBytesAsync($"{accountPath}.bak"));
		await store.SaveAsync([finalAccount]).WaitAsync(TimeSpan.FromSeconds(5));
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(accountPath).LoadAsync();
		Assert.Equal(finalAccount, Assert.Single(result.Accounts));
	}

	[Fact]
	public async Task ExecuteAsync_WhenAccountFileWasAlreadyChangedExternally_DoesNotClearStaleWriterProtection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile originalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Original");
		AccountProfile externalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"External");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported",
			HasAcceptedClaudeQuotaRisk: true);
		JsonAccountProfileStore staleStore = new(accountPath);
		await staleStore.LoadAsync();
		await staleStore.SaveAsync([originalAccount]);
		JsonAccountProfileStore externalWriter = new(accountPath);
		await externalWriter.LoadAsync();
		await externalWriter.SaveAsync([externalAccount]);
		byte[] externalPrimary = await File.ReadAllBytesAsync(accountPath);
		byte[] externalBackup = await File.ReadAllBytesAsync($"{accountPath}.bak");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak"],
			onTargetsRestored:
				staleStore.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting: staleStore.BeginPortableImportTransactionAsync,
			onTransactionFinished: staleStore.CompletePortableImportTransaction);

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => transaction.ExecuteAsync(
				cancellationToken => staleStore.SaveAsync(
					[importedAccount],
					cancellationToken)));

		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(externalBackup, await File.ReadAllBytesAsync($"{accountPath}.bak"));
		AccountProfileStoreException nextSaveException =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => staleStore.SaveAsync([importedAccount]));
		Assert.Contains(
			"其他程序變更",
			nextSaveException.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WhenExternalAccountWriteRacesOperation_RollsBackOnlyOwnedPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string preferencesPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile originalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Original");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported",
			HasAcceptedClaudeQuotaRisk: true);
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonAccountProfileStore staleStore = new(accountPath, writeTracker);
		await staleStore.LoadAsync();
		await staleStore.SaveAsync([originalAccount]);
		JsonDashboardPreferencesStore preferencesStore = new(
			preferencesPath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		await preferencesStore.SaveUsagePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		byte[] originalPreferences = await File.ReadAllBytesAsync(preferencesPath);
		byte[] externalPrimary = "externally-written-primary"u8.ToArray();
		byte[] externalBackup = "externally-written-backup"u8.ToArray();
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak", preferencesPath],
			persistenceGate: persistenceGate,
			onTargetsRestored:
				staleStore.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting: staleStore.BeginPortableImportTransactionAsync,
			onTransactionFinished: staleStore.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => transaction.ExecuteAsync(async cancellationToken =>
			{
				await preferencesStore.SaveUsagePreferencesAsync(
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					cancellationToken);
				Assert.True(File.Exists(
					Path.Combine(transactionPath, "operation-started")));
				await File.WriteAllBytesAsync(
					accountPath,
					externalPrimary,
					cancellationToken);
				await File.WriteAllBytesAsync(
					$"{accountPath}.bak",
					externalBackup,
					cancellationToken);
				await staleStore.SaveAsync([importedAccount], cancellationToken);
			}));

		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(
			externalBackup,
			await File.ReadAllBytesAsync($"{accountPath}.bak"));
		Assert.Equal(
			originalPreferences,
			await File.ReadAllBytesAsync(preferencesPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenCommittedAccountIsReplacedExternally_PreservesStaleWriterProtection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile nextAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Next");
		byte[] externalPrimary = "later-external-primary"u8.ToArray();
		byte[] externalBackup = "later-external-backup"u8.ToArray();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonAccountProfileStore store = new(accountPath, writeTracker);
		await store.LoadAsync();
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak"],
			onTargetsRestored:
				store.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting: store.BeginPortableImportTransactionAsync,
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => transaction.ExecuteAsync(async cancellationToken =>
			{
				await store.SaveAsync([importedAccount], cancellationToken);
				await File.WriteAllBytesAsync(
					accountPath,
					externalPrimary,
					cancellationToken);
				await File.WriteAllBytesAsync(
					$"{accountPath}.bak",
					externalBackup,
					cancellationToken);
				throw new InvalidOperationException("Later import step failed.");
			}));

		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(
			externalBackup,
			await File.ReadAllBytesAsync($"{accountPath}.bak"));
		AccountProfileStoreException staleException =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => store.SaveAsync([nextAccount]));
		Assert.Contains(
			"其他程序變更",
			staleException.Message,
			StringComparison.Ordinal);
		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(
			externalBackup,
			await File.ReadAllBytesAsync($"{accountPath}.bak"));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_WhenOriginallyAbsentAccountFilesAreRolledBack_AllowsLaterSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported");
		AccountProfile finalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Final");
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonAccountProfileStore store = new(accountPath, writeTracker);
		await store.LoadAsync();
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak"],
			flushCommittedMarkerToDisk: _ => throw new IOException(
				"Synthetic commit marker failure."),
			onTargetsRestored:
				store.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting: store.BeginPortableImportTransactionAsync,
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<IOException>(
			() => transaction.ExecuteAsync(
				cancellationToken => store.SaveAsync(
					[importedAccount],
					cancellationToken)));

		Assert.False(File.Exists(accountPath));
		Assert.False(File.Exists($"{accountPath}.bak"));
		await store.SaveAsync([finalAccount]).WaitAsync(TimeSpan.FromSeconds(5));
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(accountPath).LoadAsync();
		Assert.Equal(finalAccount, Assert.Single(result.Accounts));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenRestoreCallbackFails_RetriesCallbackWithoutReplayingBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "new-accounts");
		int callbackCount = 0;
		PortableSettingsImportTransaction failingTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsRestored: _ =>
			{
				callbackCount++;
				throw new IOException("Synthetic synchronization failure.");
			});

		await Assert.ThrowsAsync<IOException>(
			() => failingTransaction.RecoverInterruptedImportAsync());

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.Equal(1, callbackCount);
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));
		Assert.True(File.Exists(Path.Combine(transactionPath, "restored")));
		Assert.False(File.Exists(transactionPath + ".lock"));
		await File.WriteAllTextAsync(targetPath, "later-legitimate-accounts");
		PortableSettingsImportTransaction retryingTransaction = new(
			transactionPath,
			[targetPath],
			onTargetsRestored: _ =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});

		await retryingTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(2, callbackCount);
		Assert.Equal(
			"later-legitimate-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenRestoredCleanupPartiallyFails_RetryDoesNotNeedDeletedBaseline()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string secondTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-1.backup"),
			"old-preferences");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "restored"),
			[]);
		await File.WriteAllTextAsync(firstTargetPath, "old-accounts");
		await File.WriteAllTextAsync(secondTargetPath, "old-preferences");
		int callbackCount = 0;
		PortableSettingsImportTransaction restoredDeletionFailingTransaction = new(
			transactionPath,
			[firstTargetPath, secondTargetPath],
			deleteTransactionFile: filePath =>
			{
				if (string.Equals(
					Path.GetFileName(filePath),
					"restored",
					StringComparison.Ordinal))
				{
					throw new IOException("Synthetic restored marker deletion failure.");
				}

				File.Delete(filePath);
			},
			onTargetsRestoredWithResult: (restoreResult, _) =>
			{
				callbackCount++;
				Assert.True(restoreResult.GetTargetResult(firstTargetPath)
					.DoesFinalTargetMatchRollbackBaseline);
				Assert.True(restoreResult.GetTargetResult(secondTargetPath)
					.DoesFinalTargetMatchRollbackBaseline);
				return Task.CompletedTask;
			});

		await restoredDeletionFailingTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, callbackCount);
		Assert.True(File.Exists(Path.Combine(transactionPath, "restored")));
		Assert.True(File.Exists(
			Path.Combine(transactionPath, "restore-callback-completed")));
		Assert.Equal(2, Directory.GetFiles(transactionPath, "target-*.backup").Length);

		int backupDeletionCount = 0;
		PortableSettingsImportTransaction baselineDeletionFailingTransaction = new(
			transactionPath,
			[firstTargetPath, secondTargetPath],
			deleteTransactionFile: filePath =>
			{
				if (Path.GetFileName(filePath).EndsWith(
					".backup",
					StringComparison.Ordinal))
				{
					backupDeletionCount++;

					if (backupDeletionCount == 2)
					{
						throw new IOException(
							"Synthetic baseline deletion failure.");
					}
				}

				File.Delete(filePath);
			},
			onTargetsRestoredWithResult: (_, _) =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});

		await baselineDeletionFailingTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, callbackCount);
		Assert.False(File.Exists(Path.Combine(transactionPath, "restored")));
		Assert.True(File.Exists(
			Path.Combine(transactionPath, "restore-callback-completed")));
		Assert.Single(Directory.GetFiles(transactionPath, "target-*.backup"));

		PortableSettingsImportTransaction retryingTransaction = new(
			transactionPath,
			[firstTargetPath, secondTargetPath],
			onTargetsRestoredWithResult: (_, _) =>
			{
				callbackCount++;
				return Task.CompletedTask;
			});

		await retryingTransaction.RecoverInterruptedImportAsync();

		Assert.Equal(1, callbackCount);
		Assert.False(Directory.Exists(transactionPath));
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(firstTargetPath));
		Assert.Equal(
			"old-preferences",
			await File.ReadAllTextAsync(secondTargetPath));
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenOldRestoredJournalLacksOneBaseline_ReportsUnverifiableAndCleansJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string secondTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-1.backup"),
			"old-preferences");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "restored"),
			[]);
		await File.WriteAllTextAsync(firstTargetPath, "later-accounts");
		await File.WriteAllTextAsync(secondTargetPath, "old-preferences");
		PortableSettingsImportRestoreResult? capturedRestoreResult = null;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[firstTargetPath, secondTargetPath],
			onTargetsRestoredWithResult: (restoreResult, _) =>
			{
				capturedRestoreResult = restoreResult;
				return Task.CompletedTask;
			});

		await transaction.RecoverInterruptedImportAsync();

		Assert.NotNull(capturedRestoreResult);
		PortableSettingsImportTargetRestoreResult unverifiableResult =
			capturedRestoreResult.GetTargetResult(firstTargetPath);
		Assert.False(unverifiableResult.RollbackBaselineState.HasValue);
		Assert.False(unverifiableResult.DoesFinalTargetMatchRollbackBaseline);
		PortableSettingsImportTargetRestoreResult verifiableResult =
			capturedRestoreResult.GetTargetResult(secondTargetPath);
		Assert.True(verifiableResult.RollbackBaselineState.HasValue);
		Assert.True(verifiableResult.DoesFinalTargetMatchRollbackBaseline);
		Assert.Equal(
			"later-accounts",
			await File.ReadAllTextAsync(firstTargetPath));
		Assert.Equal(
			"old-preferences",
			await File.ReadAllTextAsync(secondTargetPath));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ExecuteAsync_BlocksBackgroundPreferenceWriterUntilCommitCompletes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string preferencesPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		JsonDashboardPreferencesStore store = new(
			preferencesPath,
			persistenceGate);
		await store.SaveUsagePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[preferencesPath],
			persistenceGate: persistenceGate);
		TaskCompletionSource importPaused = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowImportToCommit = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task transactionTask = transaction.ExecuteAsync(
			async cancellationToken =>
			{
				await store.SaveUsagePreferencesAsync(
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					cancellationToken);
				importPaused.TrySetResult();
				await allowImportToCommit.Task.WaitAsync(cancellationToken);
			});
		await importPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

		TaskCompletionSource backgroundSaveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DashboardShellPreferences backgroundPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: "DISPLAY2",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		Task backgroundSaveTask;

		using (ExecutionContext.SuppressFlow())
		{
			backgroundSaveTask = Task.Run(async () =>
			{
				backgroundSaveStarted.TrySetResult();
				await store.SaveDashboardShellPreferencesAsync(
					backgroundPreferences);
			});
		}

		await backgroundSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task prematureCompletion = await Task.WhenAny(
			backgroundSaveTask,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(backgroundSaveTask, prematureCompletion);

		allowImportToCommit.TrySetResult();
		await transactionTask.WaitAsync(TimeSpan.FromSeconds(5));
		await backgroundSaveTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		Assert.Equal(
			backgroundPreferences,
			await store.LoadDashboardShellPreferencesAsync());
	}

	[Fact]
	public async Task RecoverInterruptedImportAsync_WhenAnotherProcessOwnsTransactionLock_FailsWithoutMutatingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		string lockFilePath = transactionPath + ".lock";
		Directory.CreateDirectory(transactionPath);
		await File.WriteAllTextAsync(
			Path.Combine(transactionPath, "target-0.backup"),
			"old-accounts");
		await File.WriteAllBytesAsync(
			Path.Combine(transactionPath, "prepared"),
			[]);
		await File.WriteAllTextAsync(targetPath, "partially-imported-accounts");
		await using FileStream externalLease = new(
			lockFilePath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			recoveryMutationLockWaitTimeout:
				TimeSpan.FromMilliseconds(100));

		IOException exception = await Assert.ThrowsAsync<IOException>(
			() => transaction.RecoverInterruptedImportAsync());

		Assert.Contains(
			"另一個 AI Usage 正在匯入或還原設定",
			exception.Message,
			StringComparison.Ordinal);
		Assert.Equal(
			"partially-imported-accounts",
			await File.ReadAllTextAsync(targetPath));
		Assert.True(File.Exists(Path.Combine(transactionPath, "prepared")));

		await externalLease.DisposeAsync();
		await transaction.RecoverInterruptedImportAsync();

		Assert.Equal("old-accounts", await File.ReadAllTextAsync(targetPath));
		Assert.False(Directory.Exists(transactionPath));
		Assert.False(File.Exists(lockFilePath));
	}

	[Fact]
	public async Task RunExclusiveAsync_LateInheritedTaskCannotBypassNewOwner()
	{
		SettingsPersistenceGate persistenceGate = new();
		TaskCompletionSource allowInheritedTask = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource inheritedTaskEnteredGate = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task? inheritedTask = null;

		await persistenceGate.RunExclusiveAsync(_ =>
		{
			inheritedTask = Task.Run(async () =>
			{
				await allowInheritedTask.Task;
				await persistenceGate.RunExclusiveAsync(__ =>
				{
					inheritedTaskEnteredGate.TrySetResult();
					return Task.CompletedTask;
				});
			});
			return Task.CompletedTask;
		});

		TaskCompletionSource newerOwnerEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseNewerOwner = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task newerOwnerTask = persistenceGate.RunExclusiveAsync(
			async cancellationToken =>
			{
				newerOwnerEntered.TrySetResult();
				await releaseNewerOwner.Task.WaitAsync(cancellationToken);
			});
		await newerOwnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		allowInheritedTask.TrySetResult();
		Task prematureEntry = await Task.WhenAny(
			inheritedTaskEnteredGate.Task,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(inheritedTaskEnteredGate.Task, prematureEntry);

		releaseNewerOwner.TrySetResult();
		await newerOwnerTask.WaitAsync(TimeSpan.FromSeconds(5));
		await inheritedTaskEnteredGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.IsAssignableFrom<Task>(inheritedTask)
			.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RunExclusiveAsync_NestedDifferentGateCanReenterOuterGate()
	{
		SettingsPersistenceGate outerGate = new();
		SettingsPersistenceGate innerGate = new();
		int reentryCount = 0;

		await outerGate.RunExclusiveAsync(
			_ => innerGate.RunExclusiveAsync(
				__ => outerGate.RunExclusiveAsync(
					___ =>
					{
						reentryCount++;
						return Task.CompletedTask;
					})))
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, reentryCount);
	}
}
