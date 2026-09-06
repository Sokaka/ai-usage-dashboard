using System.Diagnostics;
using System.Runtime.CompilerServices;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ProviderProcessExecutionTests
{
	[Fact]
	public async Task RunSynchronousAsync_WhenAlreadyCancelled_DoesNotInvokeOperation()
	{
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		int invocationCount = 0;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			ProviderProcessExecution.RunSynchronousAsync(
				() => Interlocked.Increment(ref invocationCount),
				cancellationSource.Token));

		Assert.Equal(0, Volatile.Read(ref invocationCount));
	}

	[Fact]
	public async Task RunSynchronousAsync_WhenCancelledAfterStart_CleansUpLateResult()
	{
		TaskCompletionSource started = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<int> cleanedResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using CancellationTokenSource cancellationSource = new();
		Task<int> operationTask = ProviderProcessExecution.RunSynchronousAsync(
			() =>
			{
				started.TrySetResult();
				release.Task.GetAwaiter().GetResult();
				return 42;
			},
			cancellationSource.Token,
			lateResultCleanup: result =>
			{
				cleanedResult.TrySetResult(result);
				return Task.CompletedTask;
			});

		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cancellationSource.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => operationTask);
		}
		finally
		{
			release.TrySetResult();
		}

		Assert.Equal(
			42,
			await cleanedResult.Task.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task WaitForConfirmedCompletionAsync_WhenWaitFailsButFallbackConfirms_Completes()
	{
		bool isCompletionConfirmed =
			await ProviderProcessExecution.WaitForConfirmedCompletionAsync(
				() => Task.FromException(new AggregateException("wait failed")),
				() => true).WaitAsync(TimeSpan.FromSeconds(1));

		Assert.True(isCompletionConfirmed);
	}

	[Fact]
	public async Task WaitForConfirmedCompletionAsync_WhenNoPositiveEvidence_ReturnsFalse()
	{
		bool isCompletionConfirmed =
			await ProviderProcessExecution.WaitForConfirmedCompletionAsync(
				() => Task.FromException(new AggregateException("wait failed")),
				() => false).WaitAsync(TimeSpan.FromSeconds(1));

		Assert.False(isCompletionConfirmed);
	}

	[Fact]
	public async Task OperationTracker_WhenContainmentIsCompromised_QuarantinesTransferredLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"tracked.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		ProviderProcessOperationTracker operationTracker = new();
		TaskCompletionSource cleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		operationTracker.Track(cleanupCompletion.Task);
		IDisposable trackedLease = operationTracker.HoldLease(executableLease);

		try
		{
			trackedLease.Dispose();
			operationTracker.MarkContainmentCompromised();
			cleanupCompletion.TrySetResult();
			await Task.Delay(TimeSpan.FromMilliseconds(50));

			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});
		}
		finally
		{
			cleanupCompletion.TrySetResult();
			trackedLease.Dispose();
			executableLease.Dispose();
		}
	}

	[Fact]
	public void OperationTracker_WhenContainmentIsMarked_NotifiesCallbackExactlyOnce()
	{
		int callbackCount = 0;
		ProviderProcessOperationTracker operationTracker = new(
			() => Interlocked.Increment(ref callbackCount));

		operationTracker.MarkContainmentCompromised();
		operationTracker.MarkContainmentCompromised();

		Assert.True(operationTracker.IsContainmentCompromised);
		Assert.Equal(1, Volatile.Read(ref callbackCount));
	}

	[Fact]
	public async Task WaitForConfirmedExitAsync_WhenNoPositiveEvidence_QuarantinesLeaseAndRemainsPending()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"gc-retained.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		(Process Process, Task WaitTask, WeakReference<WindowsOfficialCliExecutableLease> LeaseReference)
			pendingCleanup = StartUnconfirmedWaitAndTransferLease(executablePath);
		Task timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(100));

		Assert.Same(
			timeoutTask,
			await Task.WhenAny(pendingCleanup.WaitTask, timeoutTask));

		CollectAllGarbage();

		Assert.True(pendingCleanup.LeaseReference.TryGetTarget(
			out WindowsOfficialCliExecutableLease? lease));

		try
		{
			Assert.True(lease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});
		}
		finally
		{
			lease.Dispose();
			pendingCleanup.Process.Dispose();
		}
	}

	[Theory]
	[InlineData(@"\\server\share\tool.exe")]
	[InlineData(@"\\?\UNC\server\share\tool.exe")]
	public void TryNormalizeLocalExecutablePath_WhenPathIsUnc_Rejects(
		string executablePath)
	{
		Assert.False(
			ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedPath));
		Assert.Equal(string.Empty, normalizedPath);
	}

	private static void CollectAllGarbage()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (
		Process Process,
		Task WaitTask,
		WeakReference<WindowsOfficialCliExecutableLease> LeaseReference)
		StartUnconfirmedWaitAndTransferLease(string executablePath)
	{
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		WeakReference<WindowsOfficialCliExecutableLease> leaseReference = new(
			executableLease);
		ProviderProcessOperationTracker operationTracker = new();
		IDisposable trackedLease = operationTracker.HoldLease(executableLease);
		Process process = new();
		Task waitTask = ProviderProcessExecution.WaitForConfirmedExitAsync(
			process,
			operationTracker);

		operationTracker.Track(waitTask);
		trackedLease.Dispose();

		return (process, waitTask, leaseReference);
	}
}
