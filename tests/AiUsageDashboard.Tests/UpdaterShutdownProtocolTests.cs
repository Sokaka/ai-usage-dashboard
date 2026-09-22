using System.IO.Pipes;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterShutdownProtocolTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

	[Fact]
	public void CreateUserScopedPipeName_IsDeterministicAndDoesNotExposeIdentity()
	{
		string first = UpdateShutdownChannel.CreateUserScopedPipeName(
			"DOMAIN\\user-a");
		string repeated = UpdateShutdownChannel.CreateUserScopedPipeName(
			"DOMAIN\\user-a");
		string otherUser = UpdateShutdownChannel.CreateUserScopedPipeName(
			"DOMAIN\\user-b");

		Assert.Equal(first, repeated);
		Assert.NotEqual(first, otherUser);
		Assert.StartsWith(
			UpdateShutdownChannel.PipeNamePrefix + ".",
			first,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"user-a",
			first,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void RequestFrames_QueryAndReserve_RoundTripExactValues()
	{
		Guid queryId = Guid.NewGuid();
		Guid reserveId = Guid.NewGuid();
		UpdateProcessIdentity identity = CreateIdentity();

		byte[] queryFrame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateQuery(queryId));
		byte[] reserveFrame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateReserve(reserveId, identity));

		Assert.Equal(UpdateShutdownProtocol.FrameSize, queryFrame.Length);
		Assert.Equal(UpdateShutdownProtocol.FrameSize, reserveFrame.Length);
		Assert.True(UpdateShutdownProtocol.TryDecodeRequest(
			queryFrame,
			out UpdateShutdownRequest? query,
			out Guid decodedQueryId,
			out _));
		Assert.NotNull(query);
		Assert.Equal(queryId, decodedQueryId);
		Assert.Equal(UpdateShutdownOperation.Query, query.Operation);
		Assert.Null(query.ExpectedIdentity);
		Assert.True(UpdateShutdownProtocol.TryDecodeRequest(
			reserveFrame,
			out UpdateShutdownRequest? reserve,
			out Guid decodedReserveId,
			out _));
		Assert.NotNull(reserve);
		Assert.Equal(reserveId, decodedReserveId);
		Assert.Equal(UpdateShutdownOperation.Reserve, reserve.Operation);
		Assert.Equal(identity, reserve.ExpectedIdentity);
	}

	[Fact]
	public void ResponseFrame_RoundTripsRequestIdOutcomeAndIdentity()
	{
		Guid requestId = Guid.NewGuid();
		UpdateProcessIdentity identity = CreateIdentity();
		UpdateShutdownResponse expected = new(
			requestId,
			UpdateShutdownOutcome.Accepted,
			identity);

		byte[] frame = UpdateShutdownProtocol.EncodeResponse(expected);

		Assert.Equal(UpdateShutdownProtocol.FrameSize, frame.Length);
		Assert.True(UpdateShutdownProtocol.TryDecodeResponse(
			frame,
			out UpdateShutdownResponse? actual));
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void DecodeRequest_WithUnsupportedVersion_EchoesIdAndRejectsProtocol()
	{
		Guid requestId = Guid.NewGuid();
		byte[] frame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateQuery(requestId));
		frame[4] = checked((byte)(UpdateShutdownProtocol.CurrentVersion + 1));

		bool decoded = UpdateShutdownProtocol.TryDecodeRequest(
			frame,
			out UpdateShutdownRequest? request,
			out Guid decodedRequestId,
			out UpdateShutdownOutcome rejectionOutcome);

		Assert.False(decoded);
		Assert.Null(request);
		Assert.Equal(requestId, decodedRequestId);
		Assert.Equal(
			UpdateShutdownOutcome.UnsupportedProtocol,
			rejectionOutcome);
	}

	[Fact]
	public void DecodeRequest_WithTruncatedOversizedOrMalformedFrame_FailsClosed()
	{
		byte[] validFrame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateQuery(Guid.NewGuid()));
		byte[] malformedFrame = validFrame.ToArray();
		malformedFrame[0] ^= 0xff;
		byte[][] rejectedFrames =
		[
			validFrame[..^1],
			[.. validFrame, 0],
			malformedFrame
		];

		foreach (byte[] frame in rejectedFrames)
		{
			Assert.False(UpdateShutdownProtocol.TryDecodeRequest(
				frame,
				out UpdateShutdownRequest? request,
				out _,
				out UpdateShutdownOutcome rejectionOutcome));
			Assert.Null(request);
			Assert.Equal(
				UpdateShutdownOutcome.MalformedRequest,
				rejectionOutcome);
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task StartAsync_WhenPipeIsBusy_WaitsAndBecomesReadyAfterRetry()
	{
		string pipeName = CreatePipeName();
		using NamedPipeServerStream blocker = new(
			pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Message,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using UpdateShutdownChannel channel = new(
			pipeName,
			CreateIdentity(),
			() => UpdateShutdownOutcome.Accepted,
			() => { },
			() => { });
		using CancellationTokenSource blockedWaitSource = new(
			TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			channel.StartAsync(blockedWaitSource.Token));

		blocker.Dispose();
		using CancellationTokenSource readyWaitSource = new(TestTimeout);
		await channel.StartAsync(readyWaitSource.Token);

		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateQuery(Guid.NewGuid()),
				TestTimeout);
		Assert.Equal(UpdateShutdownClientFailure.None, result.Failure);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task StartAsync_WhenDisposedBeforeReady_ThrowsObjectDisposed()
	{
		string pipeName = CreatePipeName();
		using NamedPipeServerStream blocker = new(
			pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Message,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		UpdateShutdownChannel channel = new(
			pipeName,
			CreateIdentity(),
			() => UpdateShutdownOutcome.Accepted,
			() => { },
			() => { });
		Task startTask = channel.StartAsync();

		channel.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => startTask);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ListenerCompletion_WhenFaultedAfterReady_ExposesFailure()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() => throw new NotSupportedException("Injected listener fault."),
			() => { },
			() => { });
		await channel.StartAsync().WaitAsync(TestTimeout);

		UpdateShutdownClientResult requestResult =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateReserve(
					Guid.NewGuid(),
					targetIdentity),
				TestTimeout);
		NotSupportedException failure =
			await Assert.ThrowsAsync<NotSupportedException>(() =>
				channel.ListenerCompletion.WaitAsync(TestTimeout));

		Assert.NotEqual(UpdateShutdownClientFailure.None, requestResult.Failure);
		Assert.Equal("Injected listener fault.", failure.Message);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task QueryAsync_ReturnsObservedTargetIdentityWithoutReserving()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		int reservationCount = 0;
		int dispatchCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() =>
			{
				Interlocked.Increment(ref reservationCount);
				return UpdateShutdownOutcome.Accepted;
			},
			() => { },
			() => Interlocked.Increment(ref dispatchCount));
		await channel.StartAsync();
		Guid requestId = Guid.NewGuid();

		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateQuery(requestId),
				TestTimeout);

		Assert.Equal(UpdateShutdownClientFailure.None, result.Failure);
		UpdateShutdownResponse response = Assert.IsType<UpdateShutdownResponse>(
			result.Response);
		Assert.Equal(requestId, response.RequestId);
		Assert.Equal(UpdateShutdownOutcome.Observed, response.Outcome);
		Assert.Equal(targetIdentity, response.TargetIdentity);
		Assert.Equal(0, Volatile.Read(ref reservationCount));
		Assert.Equal(0, Volatile.Read(ref dispatchCount));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ReserveAsync_WithExactIdentity_ReturnsAcceptedBeforeDispatchCompletes()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		TaskCompletionSource dispatchStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseDispatch = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int rollbackCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() => UpdateShutdownOutcome.Accepted,
			() => Interlocked.Increment(ref rollbackCount),
			() =>
			{
				dispatchStarted.TrySetResult();
				releaseDispatch.Task.GetAwaiter().GetResult();
			});
		channel.Start();

		Task<UpdateShutdownClientResult> requestTask =
			UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateReserve(
					Guid.NewGuid(),
					targetIdentity),
				TestTimeout);

		try
		{
			await dispatchStarted.Task.WaitAsync(TestTimeout);
			UpdateShutdownClientResult result =
				await requestTask.WaitAsync(TestTimeout);
			Assert.Equal(UpdateShutdownClientFailure.None, result.Failure);
			Assert.Equal(
				UpdateShutdownOutcome.Accepted,
				Assert.IsType<UpdateShutdownResponse>(result.Response).Outcome);
			Assert.Equal(0, Volatile.Read(ref rollbackCount));
			Assert.False(releaseDispatch.Task.IsCompleted);
		}
		finally
		{
			releaseDispatch.TrySetResult();
		}
	}

	[Theory]
	[InlineData(true, (int)UpdateShutdownOutcome.IdentityMismatch)]
	[InlineData(false, (int)UpdateShutdownOutcome.Busy)]
	[InlineData(false, (int)UpdateShutdownOutcome.AlreadyShuttingDown)]
	[Trait("Category", "WindowsIntegration")]
	public async Task ReserveAsync_WhenRejected_ReturnsReasonWithoutDispatch(
		bool useMismatchedIdentity,
		int reservationOutcomeValue)
	{
		UpdateShutdownOutcome reservationOutcome =
			(UpdateShutdownOutcome)reservationOutcomeValue;
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		UpdateProcessIdentity requestedIdentity = useMismatchedIdentity
			? new UpdateProcessIdentity(
				targetIdentity.ProcessId,
				targetIdentity.ProcessStartTimeUtcTicks + 1,
				targetIdentity.PayloadGenerationId)
			: targetIdentity;
		int reservationCount = 0;
		int dispatchCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() =>
			{
				Interlocked.Increment(ref reservationCount);
				return reservationOutcome;
			},
			() => { },
			() => Interlocked.Increment(ref dispatchCount));
		channel.Start();

		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateReserve(
					Guid.NewGuid(),
					requestedIdentity),
				TestTimeout);

		Assert.Equal(UpdateShutdownClientFailure.None, result.Failure);
		UpdateShutdownOutcome expectedOutcome = useMismatchedIdentity
			? UpdateShutdownOutcome.IdentityMismatch
			: reservationOutcome;
		Assert.Equal(
			expectedOutcome,
			Assert.IsType<UpdateShutdownResponse>(result.Response).Outcome);
		Assert.Equal(
			useMismatchedIdentity ? 0 : 1,
			Volatile.Read(ref reservationCount));
		Assert.Equal(0, Volatile.Read(ref dispatchCount));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Channel_WithUnsupportedProtocol_ReturnsStructuredRejection()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() => UpdateShutdownOutcome.Accepted,
			() => { },
			() => { });
		channel.Start();
		Guid requestId = Guid.NewGuid();
		byte[] requestFrame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateQuery(requestId));
		requestFrame[4] = checked(
			(byte)(UpdateShutdownProtocol.CurrentVersion + 1));
		using NamedPipeClientStream client = new(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using CancellationTokenSource timeoutSource = new(TestTimeout);
		await client.ConnectAsync(timeoutSource.Token);
		await client.WriteAsync(requestFrame, timeoutSource.Token);
		await client.FlushAsync(timeoutSource.Token);
		byte[] responseFrame = new byte[UpdateShutdownProtocol.FrameSize];

		await client.ReadExactlyAsync(responseFrame, timeoutSource.Token);

		Assert.True(UpdateShutdownProtocol.TryDecodeResponse(
			responseFrame,
			out UpdateShutdownResponse? response));
		Assert.NotNull(response);
		Assert.Equal(requestId, response.RequestId);
		Assert.Equal(
			UpdateShutdownOutcome.UnsupportedProtocol,
			response.Outcome);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Channel_WithOversizedRequestMessage_ReturnsMalformedRequest()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		int reservationCount = 0;
		int dispatchCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() =>
			{
				Interlocked.Increment(ref reservationCount);
				return UpdateShutdownOutcome.Accepted;
			},
			() => { },
			() => Interlocked.Increment(ref dispatchCount));
		channel.Start();
		Guid requestId = Guid.NewGuid();
		byte[] validFrame = UpdateShutdownProtocol.EncodeRequest(
			UpdateShutdownRequest.CreateQuery(requestId));
		byte[] oversizedFrame = [.. validFrame, 0];
		using NamedPipeClientStream client = new(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using CancellationTokenSource timeoutSource = new(TestTimeout);
		await client.ConnectAsync(timeoutSource.Token);
		client.ReadMode = PipeTransmissionMode.Message;
		await client.WriteAsync(oversizedFrame, timeoutSource.Token);
		await client.FlushAsync(timeoutSource.Token);
		byte[] responseFrame = new byte[UpdateShutdownProtocol.FrameSize];

		await client.ReadExactlyAsync(responseFrame, timeoutSource.Token);

		Assert.True(client.IsMessageComplete);
		Assert.True(UpdateShutdownProtocol.TryDecodeResponse(
			responseFrame,
			out UpdateShutdownResponse? response));
		Assert.NotNull(response);
		Assert.Equal(requestId, response.RequestId);
		Assert.Equal(
			UpdateShutdownOutcome.MalformedRequest,
			response.Outcome);
		Assert.Equal(0, Volatile.Read(ref reservationCount));
		Assert.Equal(0, Volatile.Read(ref dispatchCount));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Channel_AfterIdleClientTimesOut_AcceptsNextRequest()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		int reservationCount = 0;
		int rollbackCount = 0;
		int dispatchCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() =>
			{
				Interlocked.Increment(ref reservationCount);
				return UpdateShutdownOutcome.Accepted;
			},
			() => Interlocked.Increment(ref rollbackCount),
			() => Interlocked.Increment(ref dispatchCount),
			TimeSpan.FromMilliseconds(100));
		channel.Start();
		using NamedPipeClientStream idleClient = new(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using CancellationTokenSource timeoutSource = new(TestTimeout);
		await idleClient.ConnectAsync(timeoutSource.Token);
		await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutSource.Token);
		Guid requestId = Guid.NewGuid();

		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateQuery(requestId),
				TestTimeout,
				timeoutSource.Token);

		Assert.Equal(UpdateShutdownClientFailure.None, result.Failure);
		UpdateShutdownResponse response = Assert.IsType<UpdateShutdownResponse>(
			result.Response);
		Assert.Equal(requestId, response.RequestId);
		Assert.Equal(UpdateShutdownOutcome.Observed, response.Outcome);
		Assert.Equal(0, Volatile.Read(ref reservationCount));
		Assert.Equal(0, Volatile.Read(ref rollbackCount));
		Assert.Equal(0, Volatile.Read(ref dispatchCount));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Reserve_WhenClientDisconnectsBeforeResponse_RollsBackWithoutDispatch()
	{
		string pipeName = CreatePipeName();
		UpdateProcessIdentity targetIdentity = CreateIdentity();
		TaskCompletionSource reservationStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseReservation = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource rollbackCompleted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int reservationCount = 0;
		int rollbackCount = 0;
		int dispatchCount = 0;
		using UpdateShutdownChannel channel = new(
			pipeName,
			targetIdentity,
			() =>
			{
				Interlocked.Increment(ref reservationCount);
				reservationStarted.TrySetResult();
				releaseReservation.Task.GetAwaiter().GetResult();
				return UpdateShutdownOutcome.Accepted;
			},
			() =>
			{
				Interlocked.Increment(ref rollbackCount);
				rollbackCompleted.TrySetResult();
			},
			() => Interlocked.Increment(ref dispatchCount));
		channel.Start();
		using CancellationTokenSource timeoutSource = new(TestTimeout);
		NamedPipeClientStream client = new(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

		try
		{
			await client.ConnectAsync(timeoutSource.Token);
			byte[] requestFrame = UpdateShutdownProtocol.EncodeRequest(
				UpdateShutdownRequest.CreateReserve(
					Guid.NewGuid(),
					targetIdentity));
			await client.WriteAsync(requestFrame, timeoutSource.Token);
			await client.FlushAsync(timeoutSource.Token);
			await reservationStarted.Task.WaitAsync(TestTimeout);
			client.Dispose();
		}
		finally
		{
			releaseReservation.TrySetResult();
			client.Dispose();
		}

		await rollbackCompleted.Task.WaitAsync(TestTimeout);
		Assert.Equal(1, Volatile.Read(ref reservationCount));
		Assert.Equal(1, Volatile.Read(ref rollbackCount));
		Assert.Equal(0, Volatile.Read(ref dispatchCount));

		UpdateShutdownClientResult recoveryResult =
			await UpdateShutdownChannel.RequestAsync(
				pipeName,
				UpdateShutdownRequest.CreateQuery(Guid.NewGuid()),
				TestTimeout,
				timeoutSource.Token);
		Assert.Equal(
			UpdateShutdownClientFailure.None,
			recoveryResult.Failure);
	}

	private static UpdateProcessIdentity CreateIdentity()
	{
		return new UpdateProcessIdentity(
			processId: 1234,
			processStartTimeUtcTicks: DateTime.UtcNow.Ticks,
			payloadGenerationId: new string('a', 64));
	}

	private static string CreatePipeName()
	{
		return $"AiUsageDashboard.Tests.UpdateShutdown.{Guid.NewGuid():N}";
	}
}
