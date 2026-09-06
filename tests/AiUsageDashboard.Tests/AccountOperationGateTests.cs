using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class AccountOperationGateTests
{
	[Fact]
	public async Task ClaudeGate_RequestPurge_WaitsUntilHolderAndWaiterRelease()
	{
		ClaudeAccountOperationGate gate = new();
		Guid accountId = Guid.NewGuid();
		IDisposable firstLease = await gate.EnterAsync(
			accountId,
			CancellationToken.None);
		Task<IDisposable> waitingLeaseTask = gate.EnterAsync(
			accountId,
			CancellationToken.None).AsTask();

		gate.RequestPurge(accountId);
		Task<IDisposable> reusedAccountLeaseTask = gate.EnterAsync(
			accountId,
			CancellationToken.None).AsTask();

		Assert.Equal(1, gate.CachedAccountCount);
		Assert.False(waitingLeaseTask.IsCompleted);
		Assert.False(reusedAccountLeaseTask.IsCompleted);

		firstLease.Dispose();
		Task<IDisposable> firstCompletedTask = await Task.WhenAny(
			waitingLeaseTask,
			reusedAccountLeaseTask);
		Task<IDisposable> remainingTask = ReferenceEquals(
			firstCompletedTask,
			waitingLeaseTask)
				? reusedAccountLeaseTask
				: waitingLeaseTask;
		IDisposable nextLease = await firstCompletedTask;
		Assert.False(remainingTask.IsCompleted);
		nextLease.Dispose();
		IDisposable finalLease = await remainingTask;
		Assert.Equal(1, gate.CachedAccountCount);

		finalLease.Dispose();

		Assert.Equal(0, gate.CachedAccountCount);
	}

	[Fact]
	public async Task CodexGate_RequestPurge_WaitsUntilHolderAndWaiterRelease()
	{
		CodexAccountOperationGate gate = new();
		Guid accountId = Guid.NewGuid();
		IDisposable firstLease = await gate.EnterAsync(
			accountId,
			CancellationToken.None);
		Task<IDisposable> waitingLeaseTask = gate.EnterAsync(
			accountId,
			CancellationToken.None).AsTask();

		gate.RequestPurge(accountId);
		Task<IDisposable> reusedAccountLeaseTask = gate.EnterAsync(
			accountId,
			CancellationToken.None).AsTask();

		Assert.Equal(1, gate.CachedAccountCount);
		Assert.False(waitingLeaseTask.IsCompleted);
		Assert.False(reusedAccountLeaseTask.IsCompleted);

		firstLease.Dispose();
		Task<IDisposable> firstCompletedTask = await Task.WhenAny(
			waitingLeaseTask,
			reusedAccountLeaseTask);
		Task<IDisposable> remainingTask = ReferenceEquals(
			firstCompletedTask,
			waitingLeaseTask)
				? reusedAccountLeaseTask
				: waitingLeaseTask;
		IDisposable nextLease = await firstCompletedTask;
		Assert.False(remainingTask.IsCompleted);
		nextLease.Dispose();
		IDisposable finalLease = await remainingTask;
		Assert.Equal(1, gate.CachedAccountCount);

		finalLease.Dispose();

		Assert.Equal(0, gate.CachedAccountCount);
	}
}
