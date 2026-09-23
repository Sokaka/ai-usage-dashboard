using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravitySetupAttemptStateStoreTests
{
	[Fact]
	public async Task ReadAsync_WithPersistedOfficialWireValue_LoadsOfficialPrint()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string stateDirectory = Path.Combine(
			temporaryDirectory.Path,
			"states");
		Directory.CreateDirectory(stateDirectory);
		Guid attemptId = Guid.NewGuid();
		string document =
			$"{{\"schemaVersion\":1,\"attemptId\":\"{attemptId}\",\"phase\":3,\"processId\":1,\"processStartTimeUtcTicks\":1,\"createdAtUtcTicks\":1,\"sourceKind\":1,\"targetIdentityFingerprint\":\"{new string('A', 64)}\"}}";
		await File.WriteAllTextAsync(
			Path.Combine(stateDirectory, $"{attemptId:N}.json"),
			document);
		AntigravitySetupAttemptStateStore store = new(stateDirectory);

		AntigravitySetupAttemptState state = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			state.SourceKind);
	}

	[Fact]
	public async Task ReadAsync_WithPersistedLegacyWireValue_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string stateDirectory = Path.Combine(
			temporaryDirectory.Path,
			"states");
		Directory.CreateDirectory(stateDirectory);
		Guid attemptId = Guid.NewGuid();
		string document =
			$"{{\"schemaVersion\":1,\"attemptId\":\"{attemptId}\",\"phase\":3,\"processId\":1,\"processStartTimeUtcTicks\":1,\"createdAtUtcTicks\":1,\"sourceKind\":0,\"targetIdentityFingerprint\":\"{new string('A', 64)}\"}}";
		await File.WriteAllTextAsync(
			Path.Combine(stateDirectory, $"{attemptId:N}.json"),
			document);
		AntigravitySetupAttemptStateStore store = new(stateDirectory);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task StateTransitions_AreDurableAndMonotonic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;

		await store.BeginLaunchAsync(attemptId, process, createdAtUtc);
		AntigravitySetupAttemptState launching = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));
		Assert.Equal(AntigravitySetupAttemptPhase.Launching, launching.Phase);
		Assert.Equal(process.ProcessId, launching.ProcessId);

		await store.MarkActiveAsync(attemptId, process);
		AntigravitySetupAttemptState active = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));
		Assert.Equal(AntigravitySetupAttemptPhase.Active, active.Phase);

		await store.MarkApprovalRequestedAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"agy-user@example.com");
		AntigravitySetupAttemptState requested = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));
		Assert.Equal(
			AntigravitySetupAttemptPhase.ApprovalRequested,
			requested.Phase);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			requested.SourceKind);
		Assert.Equal(
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint("AGY-USER@example.com"),
			requested.TargetIdentityFingerprint);
		Assert.Equal(launching.CreatedAtUtcTicks, requested.CreatedAtUtcTicks);

		await store.MarkActiveAsync(attemptId, process);
		Assert.Equal(requested, await store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task ApprovalRequest_WithConflictingTarget_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginLaunchAsync(
			attemptId,
			process,
			DateTimeOffset.UtcNow);
		await store.MarkActiveAsync(
			attemptId,
			process);
		await store.MarkApprovalRequestedAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.MarkApprovalRequestedAsync(
				attemptId,
				AntigravityMachineSetupSourceKind.OfficialPrint,
				"other@example.com"));
	}

	[Fact]
	public async Task TryRemoveLaunchingAsync_WhenRemovalWins_BlocksLateHelper()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity parentProcess =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginLaunchAsync(
			attemptId,
			parentProcess,
			DateTimeOffset.UtcNow);
		AntigravitySetupAttemptState launching = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));

		Assert.True(await store.TryRemoveLaunchingAsync(attemptId, launching));
		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.MarkActiveAsync(attemptId, parentProcess));
		Assert.Null(await store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task TryRemoveLaunchingAsync_WhenHelperWins_PreservesActiveState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginLaunchAsync(
			attemptId,
			process,
			DateTimeOffset.UtcNow);
		AntigravitySetupAttemptState launching = Assert.IsType<
			AntigravitySetupAttemptState>(await store.ReadAsync(attemptId));

		await store.MarkActiveAsync(attemptId, process);

		Assert.False(await store.TryRemoveLaunchingAsync(attemptId, launching));
		Assert.Equal(
			AntigravitySetupAttemptPhase.Active,
			Assert.IsType<AntigravitySetupAttemptState>(
				await store.ReadAsync(attemptId)).Phase);
	}

	[Fact]
	public async Task MissingLaunchState_CannotBeRecreatedByHelper()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.MarkActiveAsync(
				attemptId,
				AntigravitySetupProcessIdentity.CaptureCurrent()));
		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.MarkApprovalRequestedAsync(
				attemptId,
				AntigravityMachineSetupSourceKind.OfficialPrint,
				"agy-user@example.com"));
		Assert.Null(await store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task ReadAsync_WithUnknownProperty_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string directoryPath = Path.Combine(
			temporaryDirectory.Path,
			"states");
		Directory.CreateDirectory(directoryPath);
		Guid attemptId = Guid.NewGuid();
		string json =
			$"{{\"schemaVersion\":1,\"attemptId\":\"{attemptId}\",\"phase\":2,\"processId\":1,\"processStartTimeUtcTicks\":1,\"createdAtUtcTicks\":1,\"sourceKind\":null,\"targetIdentityFingerprint\":null,\"extra\":true}}";
		await File.WriteAllTextAsync(
			Path.Combine(directoryPath, $"{attemptId:N}.json"),
			json);
		AntigravitySetupAttemptStateStore store = new(directoryPath);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task ReadAsync_WithInvalidTimestamp_FailsAsInvalidData()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string directoryPath = Path.Combine(
			temporaryDirectory.Path,
			"states");
		Directory.CreateDirectory(directoryPath);
		Guid attemptId = Guid.NewGuid();
		string json =
			$"{{\"schemaVersion\":1,\"attemptId\":\"{attemptId}\",\"phase\":2,\"processId\":1,\"processStartTimeUtcTicks\":0,\"createdAtUtcTicks\":1,\"sourceKind\":null,\"targetIdentityFingerprint\":null}}";
		await File.WriteAllTextAsync(
			Path.Combine(directoryPath, $"{attemptId:N}.json"),
			json);
		AntigravitySetupAttemptStateStore store = new(directoryPath);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task RemoveAsync_IsIdempotent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupAttemptStateStore store = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		Guid attemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginLaunchAsync(
			attemptId,
			process,
			DateTimeOffset.UtcNow);
		await store.MarkActiveAsync(
			attemptId,
			process);

		await store.RemoveAsync(attemptId);
		await store.RemoveAsync(attemptId);

		Assert.Null(await store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task RemoveUnreferencedAsync_RemovesOnlyCanonicalOrphans()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string stateDirectory = Path.Combine(
			temporaryDirectory.Path,
			"states");
		AntigravitySetupAttemptStateStore store = new(stateDirectory);
		Guid activeAttemptId = Guid.NewGuid();
		Guid orphanedAttemptId = Guid.NewGuid();
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		await store.BeginLaunchAsync(
			activeAttemptId,
			process,
			DateTimeOffset.UtcNow);
		await store.BeginLaunchAsync(
			orphanedAttemptId,
			process,
			DateTimeOffset.UtcNow);
		string unknownFilePath = Path.Combine(
			stateDirectory,
			"not-owned.json");
		await File.WriteAllTextAsync(unknownFilePath, "{}");

		await store.RemoveUnreferencedAsync(
			new HashSet<Guid> { activeAttemptId });

		Assert.NotNull(await store.ReadAsync(activeAttemptId));
		Assert.Null(await store.ReadAsync(orphanedAttemptId));
		Assert.True(File.Exists(unknownFilePath));
		Assert.True(File.Exists(Path.Combine(stateDirectory, ".store.lock")));
	}
}
