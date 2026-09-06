using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CopilotAccountOperationGateTests
{
	[Fact]
	public async Task EnterAsync_ForDifferentAccounts_DoesNotShareGate()
	{
		CopilotAccountOperationGate gate = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		IDisposable firstLease = await gate.EnterAsync(
			firstAccountId,
			CancellationToken.None);
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
		IDisposable secondLease = await gate.EnterAsync(
			secondAccountId,
			timeout.Token);

		Assert.Equal(2, gate.CachedAccountCount);

		gate.RequestPurge(firstAccountId);
		gate.RequestPurge(secondAccountId);
		firstLease.Dispose();
		secondLease.Dispose();

		Assert.Equal(0, gate.CachedAccountCount);
	}

	[Fact]
	public async Task RequestPurge_WaitsUntilSameAccountHolderAndWaiterRelease()
	{
		CopilotAccountOperationGate gate = new();
		Guid accountId = Guid.NewGuid();
		IDisposable firstLease = await gate.EnterAsync(
			accountId,
			CancellationToken.None);
		Task<IDisposable> waitingLeaseTask = gate.EnterAsync(
			accountId,
			CancellationToken.None).AsTask();

		gate.RequestPurge(accountId);

		Assert.Equal(1, gate.CachedAccountCount);
		Assert.False(waitingLeaseTask.IsCompleted);

		firstLease.Dispose();
		IDisposable waitingLease = await waitingLeaseTask;
		Assert.Equal(1, gate.CachedAccountCount);
		waitingLease.Dispose();

		Assert.Equal(0, gate.CachedAccountCount);
	}

	[Fact]
	public async Task EmptyAccountId_IsRejected()
	{
		CopilotAccountOperationGate gate = new();

		await Assert.ThrowsAsync<ArgumentException>(() =>
			gate.EnterAsync(Guid.Empty, CancellationToken.None).AsTask());
		Assert.Throws<ArgumentException>(() => gate.RequestPurge(Guid.Empty));
	}
}
