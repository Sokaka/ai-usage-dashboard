using System.IO.Pipes;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class SingleInstanceActivationChannelTests
{
	private static readonly SemaphoreSlim PipeTestGate = new(1, 1);

	[Fact]
	public void CreateUserScopedName_IsDeterministicAndSeparatesUsers()
	{
		string first = SingleInstanceActivationChannel.CreateUserScopedName(
			"AiUsageDashboard.Test",
			"DOMAIN\\user-a");
		string repeated = SingleInstanceActivationChannel.CreateUserScopedName(
			"AiUsageDashboard.Test",
			"DOMAIN\\user-a");
		string otherUser = SingleInstanceActivationChannel.CreateUserScopedName(
			"AiUsageDashboard.Test",
			"DOMAIN\\user-b");

		Assert.Equal(first, repeated);
		Assert.NotEqual(first, otherUser);
		Assert.StartsWith("AiUsageDashboard.Test.", first, StringComparison.Ordinal);
		Assert.DoesNotContain("user-a", first, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_NotifiesListeningInstanceAndReturnsSuccess()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.ActivationResult.{Guid.NewGuid():N}";
		TaskCompletionSource<SingleInstanceActivationRequest> dispatched = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using SingleInstanceActivationChannel channel = new(
			pipeName,
			_ => true,
			request => dispatched.TrySetResult(request));
		channel.Start();

		SingleInstanceActivationResult result =
			await SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.ShowFloatingWidget,
				TimeSpan.FromSeconds(2));
		Assert.True(result.WasSent, result.Failure.ToString());
		Assert.Equal(SingleInstanceActivationFailure.None, result.Failure);
		Assert.Equal(
			SingleInstanceActivationRequest.ShowFloatingWidget,
			await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_WithEnsureAccountRequest_ForwardsKnownRequest()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.EnsureAccount.{Guid.NewGuid():N}";
		TaskCompletionSource<SingleInstanceActivationRequest> dispatched = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using SingleInstanceActivationChannel channel = new(
			pipeName,
			_ => true,
			request => dispatched.TrySetResult(request));
		channel.Start();

		SingleInstanceActivationResult result =
			await SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.EnsureAntigravityAccount,
				TimeSpan.FromSeconds(2));
		Assert.True(result.WasSent);
		Assert.Equal(SingleInstanceActivationFailure.None, result.Failure);
		Assert.Equal(
			SingleInstanceActivationRequest.EnsureAntigravityAccount,
			await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Listener_WhenAcknowledgedClientStaysConnected_DispatchesAndAcceptsNextRequest()
	{
		await PipeTestGate.WaitAsync();
		try
		{
			string pipeName =
				$"AiUsageDashboard.Tests.HeldAfterAck.{Guid.NewGuid():N}";
			TaskCompletionSource<SingleInstanceActivationRequest> firstDispatched = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource<SingleInstanceActivationRequest> secondDispatched = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			int dispatchCount = 0;
			using SingleInstanceActivationChannel channel = new(
				pipeName,
				_ => true,
				request =>
				{
					if (Interlocked.Increment(ref dispatchCount) == 1)
					{
						firstDispatched.TrySetResult(request);
						return;
					}

					secondDispatched.TrySetResult(request);
				});
			channel.Start();
			using NamedPipeClientStream heldClient = new(
				".",
				pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await heldClient.ConnectAsync(2000);
			await heldClient.WriteAsync(
				new[] { (byte)SingleInstanceActivationRequest.ShowFloatingWidget });
			await heldClient.FlushAsync();
			byte[] acknowledgement = new byte[1];
			int bytesRead = await heldClient.ReadAsync(acknowledgement);

			Assert.Equal(1, bytesRead);
			Assert.Equal(
				SingleInstanceActivationChannel.ActivationAcknowledgement,
				acknowledgement[0]);
			Assert.Equal(
				SingleInstanceActivationRequest.ShowFloatingWidget,
				await firstDispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));

			SingleInstanceActivationResult secondResult =
				await SingleInstanceActivationChannel.RequestActivationAsync(
					pipeName,
					SingleInstanceActivationRequest.EnsureAntigravityAccount,
					TimeSpan.FromSeconds(2));

			Assert.True(secondResult.WasSent, secondResult.Failure.ToString());
			Assert.Equal(
				SingleInstanceActivationRequest.EnsureAntigravityAccount,
				await secondDispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Listener_WhenClientSendsDataAfterAcknowledgement_DoesNotDispatchIt()
	{
		await PipeTestGate.WaitAsync();
		try
		{
			string pipeName =
				$"AiUsageDashboard.Tests.DataAfterAck.{Guid.NewGuid():N}";
			TaskCompletionSource<SingleInstanceActivationRequest> dispatched = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			int dispatchCount = 0;
			using SingleInstanceActivationChannel channel = new(
				pipeName,
				_ => true,
				request =>
				{
					Interlocked.Increment(ref dispatchCount);
					dispatched.TrySetResult(request);
				});
			channel.Start();

			using (NamedPipeClientStream malformedClient = new(
				".",
				pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
			{
				await malformedClient.ConnectAsync(2000);
				await malformedClient.WriteAsync(
					new[] { (byte)SingleInstanceActivationRequest.ShowFloatingWidget });
				await malformedClient.FlushAsync();
				byte[] acknowledgement = new byte[1];
				int bytesRead = await malformedClient.ReadAsync(acknowledgement);
				Assert.Equal(1, bytesRead);
				Assert.Equal(
					SingleInstanceActivationChannel.ActivationAcknowledgement,
					acknowledgement[0]);
				await malformedClient.WriteAsync(new byte[] { 0x7f });
				await malformedClient.FlushAsync();
			}

			SingleInstanceActivationResult validResult =
				await SingleInstanceActivationChannel.RequestActivationAsync(
					pipeName,
					SingleInstanceActivationRequest.EnsureAntigravityAccount,
					TimeSpan.FromSeconds(2));

			Assert.True(validResult.WasSent, validResult.Failure.ToString());
			Assert.Equal(
				SingleInstanceActivationRequest.EnsureAntigravityAccount,
				await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
			Assert.Equal(1, Volatile.Read(ref dispatchCount));
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ShutdownForUpdate_WhenClientDisconnectsAfterReservation_StillDispatches()
	{
		await PipeTestGate.WaitAsync();
		try
		{
			string pipeName =
				$"AiUsageDashboard.Tests.ShutdownDisconnect.{Guid.NewGuid():N}";
			TaskCompletionSource<SingleInstanceActivationRequest> dispatched = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
			using SingleInstanceActivationChannel channel = new(
				pipeName,
				_ => true,
				request => dispatched.TrySetResult(request));
			channel.Start();

			using (NamedPipeClientStream client = new(
				".",
				pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
			{
				await client.ConnectAsync(2000);
				await client.WriteAsync(
					new[]
					{
						(byte)SingleInstanceActivationRequest.ShutdownForUpdate
					});
				await client.FlushAsync();
			}

			Assert.Equal(
				SingleInstanceActivationRequest.ShutdownForUpdate,
				await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_WithShutdownForUpdateRequest_AcknowledgesBeforeDispatch()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.ShutdownForUpdate.{Guid.NewGuid():N}";
		TaskCompletionSource<SingleInstanceActivationRequest> dispatched = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseHandler = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using SingleInstanceActivationChannel channel = new(
			pipeName,
			_ => true,
			request =>
			{
				dispatched.TrySetResult(request);
				releaseHandler.Task.GetAwaiter().GetResult();
			});
		channel.Start();

		Task<SingleInstanceActivationResult> requestTask =
			SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.ShutdownForUpdate,
				TimeSpan.FromSeconds(2));
		SingleInstanceActivationRequest request;
		SingleInstanceActivationResult result;

		try
		{
			request = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
			result = await requestTask.WaitAsync(TimeSpan.FromSeconds(2));
		}
		finally
		{
			releaseHandler.TrySetResult();
		}
		Assert.True(result.WasSent);
		Assert.Equal(SingleInstanceActivationFailure.None, result.Failure);
		Assert.Equal(SingleInstanceActivationRequest.ShutdownForUpdate, request);
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_WhenPrimaryRejectsRequest_DoesNotReportSuccess()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.Rejected.{Guid.NewGuid():N}";
		using SingleInstanceActivationChannel channel = new(
			pipeName,
			_ => false);
		channel.Start();

		SingleInstanceActivationResult result =
			await SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.ShowFloatingWidget,
				TimeSpan.FromSeconds(2));

		Assert.False(result.WasSent);
		Assert.Equal(
			SingleInstanceActivationFailure.Unavailable,
			result.Failure);
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	public async Task RequestActivationAsync_WithUnknownRequest_FailsClosed()
	{
		string pipeName =
			$"AiUsageDashboard.Tests.UnknownRequest.{Guid.NewGuid():N}";

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				(SingleInstanceActivationRequest)byte.MaxValue,
				TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void ActivationCoordinator_BeforeReady_DrainsQueuedEnsureWhenMarkedReady()
	{
		SingleInstanceActivationCoordinator coordinator = new();

		SingleInstanceActivationWork beforeReady = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);
		SingleInstanceActivationWork ready = coordinator.MarkReady();

		Assert.False(beforeReady.ShouldShowFloatingWidget);
		Assert.False(beforeReady.ShouldEnsureAntigravityAccount);
		Assert.True(ready.ShouldShowFloatingWidget);
		Assert.True(ready.ShouldEnsureAntigravityAccount);
		Assert.True(coordinator.IsEnsureAntigravityAccountRunning);
	}

	[Fact]
	public void ActivationCoordinator_ShutdownSupersedesPendingWork()
	{
		SingleInstanceActivationCoordinator coordinator = new();
		_ = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);
		SingleInstanceActivationWork beforeReady = coordinator.Enqueue(
			SingleInstanceActivationRequest.ShutdownForUpdate);

		SingleInstanceActivationWork ready = coordinator.MarkReady();

		Assert.False(beforeReady.ShouldShutdownForUpdate);
		Assert.True(ready.ShouldShutdownForUpdate);
		Assert.False(ready.ShouldShowFloatingWidget);
		Assert.False(ready.ShouldEnsureAntigravityAccount);
		Assert.False(coordinator.IsEnsureAntigravityAccountRunning);
	}

	[Fact]
	public async Task ActivationCoordinator_WhileEnsureRuns_CoalescesPendingEnsure()
	{
		SingleInstanceActivationCoordinator coordinator = new();
		_ = coordinator.MarkReady();
		SingleInstanceActivationWork initial = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);
		TaskCompletionSource completionSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task<SingleInstanceActivationWork> running =
			coordinator.RunEnsureAntigravityAccountAsync(
				() => completionSource.Task,
				_ => Assert.Fail("The ensure operation should not fail."));

		SingleInstanceActivationWork duplicate = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);
		completionSource.SetResult();
		SingleInstanceActivationWork afterCompletion = await running;

		Assert.True(initial.ShouldShowFloatingWidget);
		Assert.True(initial.ShouldEnsureAntigravityAccount);
		Assert.True(duplicate.ShouldShowFloatingWidget);
		Assert.False(duplicate.ShouldEnsureAntigravityAccount);
		Assert.False(afterCompletion.ShouldShowFloatingWidget);
		Assert.True(afterCompletion.ShouldEnsureAntigravityAccount);
		Assert.True(coordinator.IsEnsureAntigravityAccountRunning);
	}

	[Fact]
	public async Task ActivationCoordinator_AfterCompletion_AllowsLaterEnsure()
	{
		SingleInstanceActivationCoordinator coordinator = new();
		_ = coordinator.MarkReady();
		_ = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);

		SingleInstanceActivationWork afterCompletion =
			await coordinator.RunEnsureAntigravityAccountAsync(
				() => Task.CompletedTask,
				_ => Assert.Fail("The ensure operation should not fail."));
		SingleInstanceActivationWork later = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);

		Assert.False(afterCompletion.ShouldEnsureAntigravityAccount);
		Assert.True(later.ShouldEnsureAntigravityAccount);
		Assert.True(coordinator.IsEnsureAntigravityAccountRunning);
	}

	[Fact]
	public async Task ActivationCoordinator_WhenEnsureThrows_CleansStateAndReportsFailure()
	{
		SingleInstanceActivationCoordinator coordinator = new();
		_ = coordinator.MarkReady();
		_ = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);
		InvalidOperationException expected = new("unexpected failure");
		Exception? observed = null;

		SingleInstanceActivationWork afterFailure =
			await coordinator.RunEnsureAntigravityAccountAsync(
				() => Task.FromException(expected),
				exception => observed = exception);
		SingleInstanceActivationWork later = coordinator.Enqueue(
			SingleInstanceActivationRequest.EnsureAntigravityAccount);

		Assert.Same(expected, observed);
		Assert.False(afterFailure.ShouldEnsureAntigravityAccount);
		Assert.True(later.ShouldEnsureAntigravityAccount);
		Assert.True(coordinator.IsEnsureAntigravityAccountRunning);
	}

	[Fact]
	public void ShutdownScheduler_UpdateDuringOperation_StartsAfterCompletion()
	{
		ShutdownOperationScheduler scheduler = new();

		bool initialStarted = scheduler.TryBeginOperation(
			isUpdateShutdown: false,
			isQuitting: false);
		bool updateStartedImmediately = scheduler.TryBeginOperation(
			isUpdateShutdown: true,
			isQuitting: false);
		bool updateStartedAfterCompletion =
			scheduler.CompleteOperationAndTryBeginPendingUpdate(
				isQuitting: false);

		Assert.True(initialStarted);
		Assert.False(updateStartedImmediately);
		Assert.True(updateStartedAfterCompletion);
		Assert.False(
			scheduler.CompleteOperationAndTryBeginPendingUpdate(
				isQuitting: false));
	}

	[Fact]
	public void ShutdownScheduler_UpdateDuringCommittedShutdown_DoesNotStartTwice()
	{
		ShutdownOperationScheduler scheduler = new();
		Assert.True(scheduler.TryBeginOperation(
			isUpdateShutdown: false,
			isQuitting: false));
		Assert.False(scheduler.TryBeginOperation(
			isUpdateShutdown: true,
			isQuitting: false));

		bool updateStartedAfterCompletion =
			scheduler.CompleteOperationAndTryBeginPendingUpdate(
				isQuitting: true);

		Assert.False(updateStartedAfterCompletion);
		Assert.False(scheduler.TryBeginOperation(
			isUpdateShutdown: true,
			isQuitting: true));
	}

	[Fact]
	public void ShutdownScheduler_NonUpdateDuringOperation_RemainsBestEffort()
	{
		ShutdownOperationScheduler scheduler = new();
		Assert.True(scheduler.TryBeginOperation(
			isUpdateShutdown: false,
			isQuitting: false));

		bool duplicateStarted = scheduler.TryBeginOperation(
			isUpdateShutdown: false,
			isQuitting: false);
		bool pendingUpdateStarted =
			scheduler.CompleteOperationAndTryBeginPendingUpdate(
				isQuitting: false);

		Assert.False(duplicateStarted);
		Assert.False(pendingUpdateStarted);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_WhenNoServer_ReturnsTimedOut()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.MissingResult.{Guid.NewGuid():N}";

		SingleInstanceActivationResult result =
			await SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.ShowFloatingWidget,
				TimeSpan.FromMilliseconds(100));

		Assert.False(result.WasSent);
		Assert.Equal(SingleInstanceActivationFailure.TimedOut, result.Failure);
		}
		finally
		{
			PipeTestGate.Release();
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task RequestActivationAsync_WhenListenerDoesNotAcknowledge_TimesOut()
	{
		await PipeTestGate.WaitAsync();
		try
		{
		string pipeName =
			$"AiUsageDashboard.Tests.MissingAck.{Guid.NewGuid():N}";
		TaskCompletionSource requestReceived = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseServer = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using NamedPipeServerStream server = new(
			pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		Task serverTask = Task.Run(async () =>
		{
			await server.WaitForConnectionAsync();
			byte[] request = new byte[1];
			_ = await server.ReadAsync(request);
			requestReceived.TrySetResult();
			await releaseServer.Task;
		});

		Task<SingleInstanceActivationResult> requestTask =
			SingleInstanceActivationChannel.RequestActivationAsync(
				pipeName,
				SingleInstanceActivationRequest.ShowFloatingWidget,
				TimeSpan.FromMilliseconds(100));
		await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));

		SingleInstanceActivationResult result = await requestTask;
		releaseServer.TrySetResult();

		Assert.False(result.WasSent);
		Assert.Equal(SingleInstanceActivationFailure.TimedOut, result.Failure);
		await serverTask;
		}
		finally
		{
			PipeTestGate.Release();
		}
	}
}
