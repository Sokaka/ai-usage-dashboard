using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterInstanceMutexTests
{
	[Fact]
	public void UserScopedMutexName_IsDeterministicAndDoesNotExposeIdentity()
	{
		const string identity = @"EXAMPLE\alice";

		string first = UpdateInstanceNames.CreateUserScopedSingleInstanceMutexName(
			identity);
		string second = UpdateInstanceNames.CreateUserScopedSingleInstanceMutexName(
			identity);

		Assert.Equal(first, second);
		Assert.StartsWith(
			UpdateInstanceNames.SingleInstanceMutexNamePrefix + ".",
			first,
			StringComparison.Ordinal);
		Assert.DoesNotContain(identity, first, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Acquire_WhenAnotherThreadOwnsMutex_FailsClosedAfterTimeout()
	{
		string mutexName =
			$@"Local\AiUsageDashboard.Tests.{Guid.NewGuid():N}";
		TaskCompletionSource<bool> acquired = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource completed = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim releaseOwner = new(initialState: false);
		Thread ownerThread = new(() =>
		{
			try
			{
				using Mutex owner = new(
					initiallyOwned: true,
					mutexName,
					out bool createdNew);
				acquired.SetResult(createdNew);
				releaseOwner.Wait();
				owner.ReleaseMutex();
			}
			finally
			{
				completed.SetResult();
			}
		});
		ownerThread.Start();
		Assert.True(await acquired.Task);

		try
		{
			RunningPayloadProcessGateException exception = await Task.Run(() =>
				Assert.Throws<RunningPayloadProcessGateException>(() =>
					AppSingleInstanceMutexLease.Acquire(
						mutexName,
						TimeSpan.FromMilliseconds(50))));
			Assert.Contains("single-instance mutex", exception.Message);
		}
		finally
		{
			releaseOwner.Set();
			await completed.Task;
		}
	}
}
