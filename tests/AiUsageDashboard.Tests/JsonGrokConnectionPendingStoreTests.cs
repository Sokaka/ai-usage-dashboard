using System.Text.Json;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class JsonGrokConnectionPendingStoreTests
{
	private static readonly Guid AccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid AttemptId =
		Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly DateTimeOffset StartedAtUtc =
		new(2026, 8, 18, 1, 2, 3, TimeSpan.Zero);

	[Fact]
	public async Task BeginAndAdvanceAsync_RoundTripsSafeRecoveryMetadata()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);
		Guid publicBindingId =
			Guid.Parse("33333333-3333-3333-3333-333333333333");

		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await store.BeginAsync(AccountId, AttemptId, StartedAtUtc));
		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null,
			StartedAtUtc.AddSeconds(1)));
		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.BindingPersisted,
			publicBindingId,
			StartedAtUtc.AddSeconds(2)));

		GrokConnectionPendingWork work = Assert.Single(
			await new JsonGrokConnectionPendingStore(filePath).LoadAsync());
		Assert.Equal(AccountId, work.AccountId);
		Assert.Equal(AttemptId, work.AttemptId);
		Assert.Equal(StartedAtUtc, work.StartedAtUtc);
		Assert.Equal(StartedAtUtc.AddSeconds(2), work.UpdatedAtUtc);
		Assert.Equal(GrokConnectionPendingStage.BindingPersisted, work.Stage);
		Assert.Equal(publicBindingId, work.PublicBindingId);

		string json = await File.ReadAllTextAsync(filePath);
		Assert.DoesNotContain("@", json, StringComparison.Ordinal);
		Assert.DoesNotContain("rawIdentity", json, StringComparison.Ordinal);
		Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task TryAdvanceAsync_WhenWallClockMovesBackward_AdvancesWithMonotonicTimestamp()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);
		DateTimeOffset loginCompletedAtUtc = StartedAtUtc.AddMinutes(5);
		Guid publicBindingId =
			Guid.Parse("33333333-3333-3333-3333-333333333333");

		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await store.BeginAsync(AccountId, AttemptId, StartedAtUtc));
		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null,
			loginCompletedAtUtc));

		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.BindingPersisted,
			publicBindingId,
			StartedAtUtc.AddMinutes(1)));

		GrokConnectionPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.Equal(GrokConnectionPendingStage.BindingPersisted, work.Stage);
		Assert.Equal(loginCompletedAtUtc, work.UpdatedAtUtc);
		Assert.Equal(publicBindingId, work.PublicBindingId);
	}

	[Fact]
	public async Task BeginAsync_ForTwoAccounts_PreservesIndependentPendingWork()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);
		Guid secondAccountId =
			Guid.Parse("33333333-3333-3333-3333-333333333333");
		Guid secondAttemptId =
			Guid.Parse("44444444-4444-4444-4444-444444444444");

		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await store.BeginAsync(AccountId, AttemptId, StartedAtUtc));
		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await store.BeginAsync(
			secondAccountId,
			secondAttemptId,
			StartedAtUtc.AddSeconds(1)));
		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null,
			StartedAtUtc.AddSeconds(2)));

		IReadOnlyList<GrokConnectionPendingWork> pending =
			await store.LoadAsync();
		Assert.Equal(2, pending.Count);
		Assert.Contains(
			pending,
			work => (work.AccountId == AccountId) &&
				(work.Stage == GrokConnectionPendingStage.LoginCompleted));
		Assert.Contains(
			pending,
			work => (work.AccountId == secondAccountId) &&
				(work.AttemptId == secondAttemptId) &&
				(work.Stage == GrokConnectionPendingStage.LoginStarted));
	}

	[Fact]
	public async Task StaleAttempt_CannotAdvanceOrRemoveReplacementAttempt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);
		Guid replacementAttemptId =
			Guid.Parse("44444444-4444-4444-4444-444444444444");

		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await store.BeginAsync(AccountId, AttemptId, StartedAtUtc));
		Assert.Equal(
			GrokConnectionBeginResult.ReplacedIncompleteAttempt,
			await store.BeginAsync(
				AccountId,
				replacementAttemptId,
				StartedAtUtc.AddSeconds(1)));

		Assert.False(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null,
			StartedAtUtc.AddSeconds(2)));
		Assert.False(await store.TryRemoveAsync(AccountId, AttemptId));
		Assert.Equal(
			replacementAttemptId,
			Assert.Single(await store.LoadAsync()).AttemptId);
		Assert.True(await store.TryRemoveAsync(AccountId, replacementAttemptId));
		Assert.Empty(await store.LoadAsync());
	}

	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	public async Task BeginAsync_WithRecoverableExistingAttempt_DoesNotOverwrite(
		int existingStageValue)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);
		Guid replacementAttemptId =
			Guid.Parse("44444444-4444-4444-4444-444444444444");
		GrokConnectionPendingStage existingStage =
			(GrokConnectionPendingStage)existingStageValue;
		Guid? publicBindingId = existingStage ==
			GrokConnectionPendingStage.BindingPersisted
			? Guid.Parse("33333333-3333-3333-3333-333333333333")
			: null;

		await store.BeginAsync(AccountId, AttemptId, StartedAtUtc);
		Assert.True(await store.TryAdvanceAsync(
			AccountId,
			AttemptId,
			existingStage,
			publicBindingId,
			StartedAtUtc.AddSeconds(1)));

		GrokConnectionBeginResult result = await store.BeginAsync(
			AccountId,
			replacementAttemptId,
			StartedAtUtc.AddSeconds(2));

		Assert.Equal(
			GrokConnectionBeginResult.BlockedByExistingRecoverableWork,
			result);
		GrokConnectionPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.Equal(AttemptId, work.AttemptId);
		Assert.Equal(existingStage, work.Stage);
		Assert.Equal(publicBindingId, work.PublicBindingId);
	}

	[Fact]
	public async Task BeginAsync_WithCorruptDocument_QuarantinesEvidenceAndStartsFresh()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		const string CorruptContents = "{ not-valid-json";
		await File.WriteAllTextAsync(filePath, CorruptContents);
		JsonGrokConnectionPendingStore store = new(filePath);

		GrokConnectionBeginResult result = await store.BeginAsync(
			AccountId,
			AttemptId,
			StartedAtUtc);

		Assert.Equal(
			GrokConnectionBeginResult.StartedAfterCorruptStateQuarantined,
			result);
		string quarantineFilePath = string.Concat(
			filePath,
			".corrupt-",
			AttemptId.ToString("N"));
		Assert.Equal(
			CorruptContents,
			await File.ReadAllTextAsync(quarantineFilePath));
		GrokConnectionPendingWork work = Assert.Single(await store.LoadAsync());
		Assert.Equal(AccountId, work.AccountId);
		Assert.Equal(AttemptId, work.AttemptId);
		Assert.Equal(GrokConnectionPendingStage.LoginStarted, work.Stage);
	}

	[Fact]
	public async Task LoadAsync_WithRawIdentityField_RejectsDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			pendingConnections = new[]
			{
				new
				{
					accountId = AccountId,
					attemptId = AttemptId,
					startedAtUtc = StartedAtUtc,
					updatedAtUtc = StartedAtUtc,
					stage = "LoginStarted",
					publicBindingId = (Guid?)null,
					rawIdentity = "person@example.com"
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new JsonGrokConnectionPendingStore(filePath).LoadAsync());
		Assert.True(File.Exists(filePath));
		Assert.Empty(Directory.GetFiles(
			temporaryDirectory.Path,
			"*.corrupt-*"));
	}

	[Fact]
	public async Task BeginAsync_WithNonUtcTimestamp_RejectsWithoutWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"grok-connection-pending-v1.json");
		JsonGrokConnectionPendingStore store = new(filePath);

		await Assert.ThrowsAsync<ArgumentException>(() => store.BeginAsync(
			AccountId,
			AttemptId,
			new DateTimeOffset(2026, 8, 18, 9, 2, 3, TimeSpan.FromHours(8))));

		Assert.False(File.Exists(filePath));
	}
}
