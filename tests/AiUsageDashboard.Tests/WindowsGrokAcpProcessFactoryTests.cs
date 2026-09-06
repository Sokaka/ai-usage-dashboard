using System.IO;

using AiUsageDashboard.App.Providers;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.Tests;

public sealed class WindowsGrokAcpProcessFactoryTests
{
	[Fact]
	public void RetainExecutableLease_WhenContainmentRemainsHealthy_ReleasesLease()
	{
		GrokProcessContainmentState containmentState = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(
				"C:\\grok-retention-test.exe");

		using (IDisposable retention =
			containmentState.RetainExecutableLease(executableLease))
		{
			using WindowsOfficialCliExecutableLease activeLease =
				executableLease.Duplicate();
		}

		Assert.Equal(0, containmentState.QuarantinedExecutableLeaseCount);
		Assert.Throws<ObjectDisposedException>(() => executableLease.Duplicate());
	}

	[Fact]
	public void RetainExecutableLease_WhenContainmentIsCompromised_QuarantinesLease()
	{
		GrokProcessContainmentState containmentState = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(
				"C:\\grok-quarantine-test.exe");
		IDisposable retention =
			containmentState.RetainExecutableLease(executableLease);

		containmentState.MarkCompromised();
		retention.Dispose();

		Assert.Equal(1, containmentState.QuarantinedExecutableLeaseCount);
		using WindowsOfficialCliExecutableLease quarantinedLease =
			executableLease.Duplicate();
	}

	[Fact]
	public async Task RetainExecutableLease_WhenLateCleanupCompromisesContainment_QuarantinesLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"grok.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		GrokProcessContainmentState containmentState = new();
		ProviderProcessOperationTracker operationTracker = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		TaskCompletionSource lateCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		try
		{
			operationTracker.Track(lateCleanup.Task);
			IDisposable trackedRetention = operationTracker.HoldLease(
				containmentState.RetainExecutableLease(executableLease));

			trackedRetention.Dispose();
			containmentState.MarkCompromised();
			lateCleanup.TrySetResult();
			await lateCleanup.Task;

			Assert.Equal(1, containmentState.QuarantinedExecutableLeaseCount);
			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
				File.WriteAllText(executablePath, "changed"));
		}
		finally
		{
			lateCleanup.TrySetResult();
			executableLease.Dispose();
		}

		File.WriteAllText(executablePath, "changed");
	}

	[Fact]
	public async Task RetainExecutableLease_WhenLateCleanupCompletesHealthy_ReleasesLease()
	{
		GrokProcessContainmentState containmentState = new();
		ProviderProcessOperationTracker operationTracker = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(
				"C:\\grok-late-cleanup-release-test.exe");
		TaskCompletionSource lateCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		operationTracker.Track(lateCleanup.Task);
		IDisposable trackedRetention = operationTracker.HoldLease(
			containmentState.RetainExecutableLease(executableLease));

		trackedRetention.Dispose();
		using (WindowsOfficialCliExecutableLease activeLease =
			executableLease.Duplicate())
		{
			lateCleanup.TrySetResult();
			await lateCleanup.Task;
		}

		Assert.True(SpinWait.SpinUntil(
			() => IsDisposed(executableLease),
			TimeSpan.FromSeconds(1)));
		Assert.Equal(0, containmentState.QuarantinedExecutableLeaseCount);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CreateRedirectedPipe_OnWindows_UsesAsyncParentHandleAndTransfersBytes(
		bool parentReads)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		WindowsGrokAcpProcessFactory.CreateRedirectedPipe(
			out SafeFileHandle readHandle,
			out SafeFileHandle writeHandle,
			parentReads);
		SafeFileHandle parentHandle = parentReads ? readHandle : writeHandle;
		SafeFileHandle childHandle = parentReads ? writeHandle : readHandle;
		FileAccess parentAccess = parentReads ? FileAccess.Read : FileAccess.Write;
		FileAccess childAccess = parentReads ? FileAccess.Write : FileAccess.Read;
		await using FileStream parentStream = new(
			parentHandle,
			parentAccess,
			bufferSize: 4096,
			isAsync: true);
		await using FileStream childStream = new(
			childHandle,
			childAccess,
			bufferSize: 4096,
			isAsync: false);

		Assert.True(parentStream.IsAsync);
		byte[] expected = { 0x47, 0x52, 0x4F, 0x4B };
		byte[] actual = new byte[expected.Length];
		int bytesRead;

		if (parentReads)
		{
			childStream.Write(expected);
			childStream.Flush();
			bytesRead = await parentStream.ReadAsync(actual);
		}
		else
		{
			await parentStream.WriteAsync(expected);
			await parentStream.FlushAsync();
			bytesRead = childStream.Read(actual);
		}

		Assert.Equal(expected.Length, bytesRead);
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task EnterLaunchTransition_WhenMarkArrivesBehindActiveTransition_BlocksWaitingAndLaterTransitions()
	{
		GrokProcessContainmentState containmentState = new();
		using IDisposable activeTransition =
			containmentState.EnterLaunchTransition();
		using ManualResetEventSlim waitingTransitionStarted = new();
		bool waitingNativeTransitionExecuted = false;
		Task waitingTransition = Task.Run(() =>
		{
			waitingTransitionStarted.Set();
			using IDisposable transition =
				containmentState.EnterLaunchTransition();
			waitingNativeTransitionExecuted = true;
		});
		Assert.True(waitingTransitionStarted.Wait(TimeSpan.FromSeconds(5)));
		Task markTask = Task.Run(containmentState.MarkCompromised);
		Assert.True(SpinWait.SpinUntil(
			() => containmentState.IsCompromised,
			TimeSpan.FromSeconds(5)));
		Assert.False(markTask.IsCompleted);

		activeTransition.Dispose();

		await markTask;
		await Assert.ThrowsAsync<GrokProcessContainmentException>(async () =>
			await waitingTransition);
		Assert.False(waitingNativeTransitionExecuted);
		Assert.Throws<GrokProcessContainmentException>(() =>
			containmentState.EnterLaunchTransition());
	}

	[Fact]
	public async Task StartAsync_AfterContainmentIsCompromised_RejectsAllContainedCommandsBeforeLaunchValidation()
	{
		GrokProcessContainmentState containmentState = new();
		containmentState.MarkCompromised();
		WindowsGrokAcpProcessFactory factory = new(containmentState);

		foreach (GrokContainedCommand command in new[]
			{
				GrokContainedCommand.AcpStdio,
				GrokContainedCommand.Version
			})
		{
			GrokAcpLaunchOptions invalidOptions = new(
				"C:\\missing-grok.exe",
				"C:\\missing-home",
				"C:\\missing-workspace",
				command);

			GrokProcessContainmentException exception =
				await Assert.ThrowsAsync<GrokProcessContainmentException>(() =>
					factory.StartAsync(invalidOptions, CancellationToken.None));

			Assert.Contains(
				"Restart AI Usage",
				exception.Message,
				StringComparison.Ordinal);
		}

		Assert.True(containmentState.IsCompromised);
	}

	private static bool IsDisposed(
		WindowsOfficialCliExecutableLease executableLease)
	{
		try
		{
			using WindowsOfficialCliExecutableLease duplicate =
				executableLease.Duplicate();
			return false;
		}
		catch (ObjectDisposedException)
		{
			return true;
		}
	}
}
