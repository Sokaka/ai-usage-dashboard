using System.IO;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class JsonAccountCleanupPendingStoreTests
{
	[Fact]
	public async Task ComponentProgress_RoundTripsAcrossStoreInstances()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		Guid accountId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);

		await store.UpsertAsync(new[]
		{
			(accountId, ProviderKind.Claude)
		});
		await store.MarkRuntimeCompletedAsync(
			accountId,
			ProviderKind.Claude);

		AccountCleanupPendingWork partial = Assert.Single(
			await new JsonAccountCleanupPendingStore(filePath).LoadAsync());
		Assert.Equal(accountId, partial.AccountId);
		Assert.Equal(ProviderKind.Claude, partial.Provider);
		Assert.True(partial.CachePending);
		Assert.False(partial.RuntimePending);

		await new JsonAccountCleanupPendingStore(filePath)
			.MarkCacheCompletedAsync(accountId, ProviderKind.Claude);

		Assert.Empty(await new JsonAccountCleanupPendingStore(filePath).LoadAsync());
		Assert.True(File.Exists(filePath));
	}

	[Fact]
	public async Task UpsertCacheCleanupAsync_NewWork_RoundTripsWithoutRuntimeCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		Guid accountId = Guid.NewGuid();

		await new JsonAccountCleanupPendingStore(filePath)
			.UpsertCacheCleanupAsync(accountId, ProviderKind.Grok);

		AccountCleanupPendingWork work = Assert.Single(
			await new JsonAccountCleanupPendingStore(filePath).LoadAsync());
		Assert.Equal(accountId, work.AccountId);
		Assert.Equal(ProviderKind.Grok, work.Provider);
		Assert.True(work.CachePending);
		Assert.False(work.RuntimePending);
	}

	[Fact]
	public async Task UpsertCacheCleanupAsync_ExistingRuntimeWork_PreservesRuntimeCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		Guid accountId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);
		await store.UpsertAsync(new[]
		{
			(accountId, ProviderKind.Claude)
		});
		await store.MarkCacheCompletedAsync(accountId, ProviderKind.Claude);

		await store.UpsertCacheCleanupAsync(accountId, ProviderKind.Claude);

		AccountCleanupPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.True(work.CachePending);
		Assert.True(work.RuntimePending);
		Assert.False(work.AllowRetainedPrivateStateDeletion);
	}

	[Fact]
	public async Task LoadAsync_DuplicateKeys_ConservativelyMergesPendingComponents()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		Guid accountId = Guid.NewGuid();
		await File.WriteAllTextAsync(
			filePath,
			$$"""
			{
			  "schemaVersion": 1,
			  "pendingCleanups": [
			    {
			      "accountId": "{{accountId}}",
			      "provider": "Claude",
			      "cachePending": true,
			      "runtimePending": false
			    },
			    {
			      "accountId": "{{accountId}}",
			      "provider": "Claude",
			      "cachePending": false,
			      "runtimePending": true
			    }
			  ]
			}
			""");

		AccountCleanupPendingWork work = Assert.Single(
			await new JsonAccountCleanupPendingStore(filePath).LoadAsync());

		Assert.True(work.CachePending);
		Assert.True(work.RuntimePending);
		Assert.False(work.AllowRetainedPrivateStateDeletion);
	}

	[Fact]
	public async Task LoadAsync_WithUnknownField_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 1,
			  "pendingCleanups": [],
			  "unexpected": true
			}
			""");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => new JsonAccountCleanupPendingStore(filePath).LoadAsync());
	}

	[Fact]
	public async Task LoadAsync_WithUnsupportedSchemaVersion_FailsClosedWithoutRewritingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		const string contents =
			"""
			{
			  "schemaVersion": 3,
			  "pendingCleanups": []
			}
			""";
		await File.WriteAllTextAsync(filePath, contents);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => new JsonAccountCleanupPendingStore(filePath).LoadAsync());

		Assert.Contains("版本或內容無效", exception.Message, StringComparison.Ordinal);
		Assert.Equal(contents, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task UpsertAsync_NewGeneration_RearmsBothComponents()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v1.json");
		Guid accountId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);
		var key = (accountId, ProviderKind.Antigravity);
		await store.UpsertAsync(new[] { key });
		await store.MarkCacheCompletedAsync(key.Item1, key.Item2);

		await store.UpsertAsync(new[] { key });

		AccountCleanupPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.True(work.CachePending);
		Assert.True(work.RuntimePending);
	}

	[Fact]
	public async Task AuthorizeRetainedPrivateStateDeletionAsync_ForCopilot_RoundTripsUntilRuntimeCompletes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v2.json");
		Guid accountId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);
		var key = (accountId, ProviderKind.Copilot);
		await store.UpsertAsync(new[] { key });

		await store.AuthorizeRetainedPrivateStateDeletionAsync(new[] { key });

		AccountCleanupPendingWork authorized = Assert.Single(
			await new JsonAccountCleanupPendingStore(filePath).LoadAsync());
		Assert.True(authorized.AllowRetainedPrivateStateDeletion);

		await store.MarkRuntimeCompletedAsync(accountId, ProviderKind.Copilot);

		AccountCleanupPendingWork cacheOnly = Assert.Single(
			await new JsonAccountCleanupPendingStore(filePath).LoadAsync());
		Assert.True(cacheOnly.CachePending);
		Assert.False(cacheOnly.RuntimePending);
		Assert.False(cacheOnly.AllowRetainedPrivateStateDeletion);
	}

	[Fact]
	public async Task UpsertAsync_AfterCopilotAuthorization_PreservesRetainedDeletionAuthorization()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v2.json");
		Guid accountId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);
		var key = (accountId, ProviderKind.Copilot);
		await store.UpsertAsync(new[] { key });
		await store.AuthorizeRetainedPrivateStateDeletionAsync(new[] { key });

		await store.UpsertAsync(new[] { key });

		AccountCleanupPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.True(work.CachePending);
		Assert.True(work.RuntimePending);
		Assert.True(work.AllowRetainedPrivateStateDeletion);
	}

	[Fact]
	public async Task CommittedPortableImportAuthorization_OnlyAuthorizesPendingRetainedCopilotIntersection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-cleanup-pending-v2.json");
		string accountFilePath = Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid retainedCopilotId = Guid.NewGuid();
		Guid removedCopilotId = Guid.NewGuid();
		JsonAccountCleanupPendingStore store = new(filePath);
		JsonAccountProfileStore accountProfileStore = new(accountFilePath);
		await accountProfileStore.LoadAsync();
		await accountProfileStore.SaveAsync(
		[
			new AccountProfile(
				retainedCopilotId,
				ProviderKind.Copilot,
				"Retained Copilot")
		]);
		await store.UpsertAsync(
		[
			(retainedCopilotId, ProviderKind.Copilot),
			(removedCopilotId, ProviderKind.Copilot)
		]);

		await AiUsageDashboard.App.App
			.AuthorizeCommittedPortableCopilotCleanupAsync(
			accountProfileStore,
			store);

		IReadOnlyDictionary<Guid, AccountCleanupPendingWork> workByAccountId =
			(await new JsonAccountCleanupPendingStore(filePath).LoadAsync())
				.ToDictionary(work => work.AccountId);
		Assert.True(workByAccountId[retainedCopilotId]
			.AllowRetainedPrivateStateDeletion);
		Assert.False(workByAccountId[removedCopilotId]
			.AllowRetainedPrivateStateDeletion);
	}
}
