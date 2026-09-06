using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class JsonAntigravityConnectionPendingStoreTests
{
	[Fact]
	public async Task RoundTrip_PreservesPhasesAndRemovesCompletedWork()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity-connection-pending-v1.json");
		JsonAntigravityConnectionPendingStore store = new(filePath);
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string firstFingerprint = new('A', 64);
		string secondFingerprint = new('B', 64);

		await store.UpsertAsync(firstAccountId, firstFingerprint);
		await store.UpsertAsync(secondAccountId, secondFingerprint);
		await store.MarkCacheCompletedAsync(firstAccountId);

		Assert.Collection(
			await store.LoadAsync(),
			first =>
			{
				Assert.Equal(
					new[] { firstAccountId, secondAccountId }.Order().First(),
					first.AccountId);
			},
			second =>
			{
				Assert.Equal(
					new[] { firstAccountId, secondAccountId }.Order().Last(),
					second.AccountId);
			});
		AntigravityConnectionPendingWork firstWork =
			Assert.Single(
				await store.LoadAsync(),
				work => work.AccountId == firstAccountId);
		Assert.False(firstWork.CachePending);
		Assert.True(firstWork.ProfilePending);
		Assert.Equal(firstFingerprint, firstWork.ExpectedProfileIdentityFingerprint);

		await store.MarkProfileCompletedAsync(firstAccountId);
		Assert.DoesNotContain(
			await store.LoadAsync(),
			work => work.AccountId == firstAccountId);

		await store.RemoveAsync(secondAccountId);
		Assert.Empty(await store.LoadAsync());
	}

	[Fact]
	public async Task UpsertExistingWork_ResetsBothPhases()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityConnectionPendingStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"pending.json"));
		Guid accountId = Guid.NewGuid();
		string firstFingerprint = new('A', 64);
		string replacementFingerprint = new('C', 64);
		await store.UpsertAsync(accountId, firstFingerprint);
		await store.MarkCacheCompletedAsync(accountId);

		await store.UpsertAsync(accountId, replacementFingerprint);

		AntigravityConnectionPendingWork work =
			Assert.Single(await store.LoadAsync());
		Assert.True(work.CachePending);
		Assert.True(work.ProfilePending);
		Assert.Equal(
			replacementFingerprint,
			work.ExpectedProfileIdentityFingerprint);
	}

	[Fact]
	public async Task BeginSetup_PersistsIntentUntilPromotedOrRemoved()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityConnectionPendingStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"pending.json"));
		Guid accountId = Guid.NewGuid();
		Guid setupAttemptId = Guid.NewGuid();
		string fingerprint = new('D', 64);

		await store.BeginSetupAsync(
			accountId,
			fingerprint,
			setupAttemptId);

		AntigravityConnectionPendingWork setupIntent =
			Assert.Single(await store.LoadAsync());
		Assert.True(setupIntent.IsSetupPending);
		Assert.False(setupIntent.CachePending);
		Assert.False(setupIntent.ProfilePending);
		Assert.Equal(setupAttemptId, setupIntent.SetupAttemptId);

		await store.UpsertAsync(accountId, fingerprint);

		AntigravityConnectionPendingWork completedSetup =
			Assert.Single(await store.LoadAsync());
		Assert.False(completedSetup.IsSetupPending);
		Assert.True(completedSetup.CachePending);
		Assert.True(completedSetup.ProfilePending);
		Assert.Equal(setupAttemptId, completedSetup.SetupAttemptId);
	}

	[Fact]
	public async Task CleanupOrphanedSetupArtifactsAsync_PreservesActiveAndCleansAfterRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string pendingFilePath = Path.Combine(
			temporaryDirectory.Path,
			"pending.json");
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore store = new(
			pendingFilePath,
			receiptStore,
			attemptStateStore);
		Guid accountId = Guid.NewGuid();
		Guid activeAttemptId = Guid.NewGuid();
		Guid orphanedAttemptId = Guid.NewGuid();
		string fingerprint = new('D', 64);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginSetupAsync(
			accountId,
			fingerprint,
			activeAttemptId);
		foreach (Guid attemptId in new[] { activeAttemptId, orphanedAttemptId })
		{
			await attemptStateStore.BeginLaunchAsync(
				attemptId,
				process,
				DateTimeOffset.UtcNow);
			await receiptStore.WriteAsync(
				attemptId,
				AntigravityMachineSetupSourceKind.OfficialPrint,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		}

		await store.CleanupOrphanedSetupArtifactsAsync(
			new HashSet<Guid> { activeAttemptId });

		Assert.NotNull(await receiptStore.ReadAsync(activeAttemptId));
		Assert.NotNull(await attemptStateStore.ReadAsync(activeAttemptId));
		Assert.Null(await receiptStore.ReadAsync(orphanedAttemptId));
		Assert.Null(await attemptStateStore.ReadAsync(orphanedAttemptId));

		await store.RemoveAsync(accountId);
		JsonAntigravityConnectionPendingStore restartedStore = new(
			pendingFilePath,
			receiptStore,
			attemptStateStore);
		await restartedStore.CleanupOrphanedSetupArtifactsAsync(
			new HashSet<Guid>());

		Assert.Null(await receiptStore.ReadAsync(activeAttemptId));
		Assert.Null(await attemptStateStore.ReadAsync(activeAttemptId));
	}

	[Fact]
	public async Task LoadAsync_LegacySetupIntent_LoadsWithoutInventingAttemptId()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "pending.json");
		Guid accountId = Guid.NewGuid();
		string fingerprint = new('A', 64);
		string json =
			$"{{\"schemaVersion\":1,\"pendingConnections\":[{{\"accountId\":\"{accountId}\",\"expectedProfileIdentityFingerprint\":\"{fingerprint}\",\"cachePending\":false,\"profilePending\":false}}]}}";
		await File.WriteAllTextAsync(filePath, json);
		JsonAntigravityConnectionPendingStore store = new(filePath);

		AntigravityConnectionPendingWork work =
			Assert.Single(await store.LoadAsync());

		Assert.True(work.IsSetupPending);
		Assert.Null(work.SetupAttemptId);
	}

	[Fact]
	public async Task LoadAsync_LegacyCommittedWork_LoadsWithoutInventingAttemptId()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "pending.json");
		Guid accountId = Guid.NewGuid();
		string fingerprint = new('B', 64);
		string json =
			$"{{\"schemaVersion\":1,\"pendingConnections\":[{{\"accountId\":\"{accountId}\",\"expectedProfileIdentityFingerprint\":\"{fingerprint}\",\"cachePending\":true,\"profilePending\":true}}]}}";
		await File.WriteAllTextAsync(filePath, json);
		JsonAntigravityConnectionPendingStore store = new(filePath);

		AntigravityConnectionPendingWork work =
			Assert.Single(await store.LoadAsync());

		Assert.False(work.IsSetupPending);
		Assert.True(work.CachePending);
		Assert.True(work.ProfilePending);
		Assert.Null(work.SetupAttemptId);
	}

	[Theory]
	[InlineData(
		"{\"schemaVersion\":1,\"pendingConnections\":[],\"unknown\":true}")]
	[InlineData(
		"{\"schemaVersion\":99,\"pendingConnections\":[]}")]
	[InlineData(
		"{\"schemaVersion\":1,\"pendingConnections\":[{\"accountId\":\"00000000-0000-0000-0000-000000000000\",\"cachePending\":true,\"profilePending\":true}]}")]
	[InlineData(
		"{\"schemaVersion\":1,\"pendingConnections\":[{\"accountId\":\"11111111-1111-1111-1111-111111111111\",\"cachePending\":true,\"profilePending\":true},{\"accountId\":\"11111111-1111-1111-1111-111111111111\",\"cachePending\":true,\"profilePending\":true}]}")]
	[InlineData(
		"{\"schemaVersion\":1,\"pendingConnections\":[{\"accountId\":\"11111111-1111-1111-1111-111111111111\",\"cachePending\":true,\"expectedProfileIdentityFingerprint\":\"invalid\",\"profilePending\":true}]}")]
	public async Task LoadAsync_WithInvalidDocument_FailsClosed(string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"pending.json");
		await File.WriteAllTextAsync(filePath, json);
		JsonAntigravityConnectionPendingStore store = new(filePath);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync());
	}

	[Fact]
	public async Task PublicOperations_RejectEmptyAccountId()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityConnectionPendingStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"pending.json"));

		await Assert.ThrowsAsync<ArgumentException>(
			() => store.UpsertAsync(Guid.Empty, new string('A', 64)));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.BeginSetupAsync(
				Guid.Empty,
				new string('A', 64),
				Guid.NewGuid()));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.UpsertAsync(Guid.NewGuid(), "invalid"));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.MarkCacheCompletedAsync(Guid.Empty));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.MarkProfileCompletedAsync(Guid.Empty));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.RemoveAsync(Guid.Empty));
	}
}
