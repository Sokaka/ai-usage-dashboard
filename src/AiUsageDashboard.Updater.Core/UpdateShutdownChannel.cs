using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.Updater.Core;

internal enum UpdateShutdownClientFailure
{
	None,
	TimedOut,
	AccessDenied,
	Unavailable,
	InvalidResponse
}

internal readonly record struct UpdateShutdownClientResult(
	UpdateShutdownResponse? Response,
	UpdateShutdownClientFailure Failure);

internal sealed class UpdateShutdownChannel : IDisposable
{
	internal const string PipeNamePrefix =
		"AiUsageDashboard.UpdateShutdown.v1";
	private static readonly TimeSpan DefaultConnectedClientTimeout =
		TimeSpan.FromSeconds(5);

	private readonly TimeSpan _connectedClientTimeout;
	private readonly Action _dispatchAcceptedShutdown;
	private readonly string _pipeName;
	private readonly Func<UpdateShutdownOutcome> _reserveShutdown;
	private readonly Action _rollbackAcceptedShutdown;
	private readonly CancellationTokenSource _shutdownTokenSource = new();
	private readonly UpdateProcessIdentity _targetIdentity;
	private bool _isDisposed;
	private Task? _listenerTask;

	internal UpdateShutdownChannel(
		string pipeName,
		UpdateProcessIdentity targetIdentity,
		Func<UpdateShutdownOutcome> reserveShutdown,
		Action rollbackAcceptedShutdown,
		Action dispatchAcceptedShutdown)
		: this(
			pipeName,
			targetIdentity,
			reserveShutdown,
			rollbackAcceptedShutdown,
			dispatchAcceptedShutdown,
			DefaultConnectedClientTimeout)
	{
	}

	internal UpdateShutdownChannel(
		string pipeName,
		UpdateProcessIdentity targetIdentity,
		Func<UpdateShutdownOutcome> reserveShutdown,
		Action rollbackAcceptedShutdown,
		Action dispatchAcceptedShutdown,
		TimeSpan connectedClientTimeout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

		if ((connectedClientTimeout <= TimeSpan.Zero) ||
			(connectedClientTimeout.TotalMilliseconds > uint.MaxValue - 1))
		{
			throw new ArgumentOutOfRangeException(
				nameof(connectedClientTimeout));
		}

		_pipeName = pipeName;
		_targetIdentity = targetIdentity;
		_reserveShutdown = reserveShutdown ??
			throw new ArgumentNullException(nameof(reserveShutdown));
		_rollbackAcceptedShutdown = rollbackAcceptedShutdown ??
			throw new ArgumentNullException(nameof(rollbackAcceptedShutdown));
		_dispatchAcceptedShutdown = dispatchAcceptedShutdown ??
			throw new ArgumentNullException(nameof(dispatchAcceptedShutdown));
		_connectedClientTimeout = connectedClientTimeout;
	}

	internal static string CreateCurrentUserPipeName()
	{
		return CreateUserScopedPipeName(
			$"{Environment.UserDomainName}\\{Environment.UserName}");
	}

	internal static string CreateUserScopedPipeName(string userIdentity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);
		byte[] identityHash = SHA256.HashData(
			Encoding.UTF8.GetBytes(userIdentity));
		return $"{PipeNamePrefix}." +
			Convert.ToHexString(identityHash.AsSpan(0, 12));
	}

	internal static async Task<UpdateShutdownClientResult> RequestAsync(
		string pipeName,
		UpdateShutdownRequest request,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		ArgumentNullException.ThrowIfNull(request);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		using CancellationTokenSource timeoutTokenSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutTokenSource.CancelAfter(timeout);
		using NamedPipeClientStream client = new(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		bool wasConnected = false;

		try
		{
			await client.ConnectAsync(timeoutTokenSource.Token).ConfigureAwait(false);
			wasConnected = true;
			client.ReadMode = PipeTransmissionMode.Message;
			byte[] requestFrame = UpdateShutdownProtocol.EncodeRequest(request);
			await client.WriteAsync(
				requestFrame,
				timeoutTokenSource.Token).ConfigureAwait(false);
			await client.FlushAsync(timeoutTokenSource.Token).ConfigureAwait(false);
			byte[] responseFrame = new byte[UpdateShutdownProtocol.FrameSize];
			int bytesRead = await ReadFrameAsync(
				client,
				responseFrame,
				timeoutTokenSource.Token).ConfigureAwait(false);

			if ((bytesRead != responseFrame.Length) ||
				!client.IsMessageComplete ||
				!UpdateShutdownProtocol.TryDecodeResponse(
					responseFrame,
					out UpdateShutdownResponse? response) ||
				(response is null) ||
				(response.RequestId != request.RequestId))
			{
				return new UpdateShutdownClientResult(
					Response: null,
					UpdateShutdownClientFailure.InvalidResponse);
			}

			return new UpdateShutdownClientResult(
				response,
				UpdateShutdownClientFailure.None);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new UpdateShutdownClientResult(
				Response: null,
				UpdateShutdownClientFailure.TimedOut);
		}
		catch (TimeoutException)
		{
			return new UpdateShutdownClientResult(
				Response: null,
				UpdateShutdownClientFailure.TimedOut);
		}
		catch (UnauthorizedAccessException)
		{
			return new UpdateShutdownClientResult(
				Response: null,
				wasConnected
					? UpdateShutdownClientFailure.Unavailable
					: UpdateShutdownClientFailure.AccessDenied);
		}
		catch (Exception exception) when (
			exception is IOException or InvalidOperationException)
		{
			return new UpdateShutdownClientResult(
				Response: null,
				UpdateShutdownClientFailure.Unavailable);
		}
	}

	internal void Start()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);

		if (_listenerTask is null)
		{
			_listenerTask = ListenAsync(_shutdownTokenSource.Token);
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		_shutdownTokenSource.Cancel();
		_shutdownTokenSource.Dispose();
	}

	private static async Task<int> ReadFrameAsync(
		Stream stream,
		Memory<byte> frame,
		CancellationToken cancellationToken)
	{
		int totalBytesRead = 0;

		while (totalBytesRead < frame.Length)
		{
			int bytesRead = await stream.ReadAsync(
				frame[totalBytesRead..],
				cancellationToken).ConfigureAwait(false);

			if (bytesRead == 0)
			{
				break;
			}

			totalBytesRead += bytesRead;
		}

		return totalBytesRead;
	}

	private async Task ListenAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			bool hasAcceptedReservation = false;
			bool responseWasSent = false;
			bool shouldDispatchAcceptedShutdown = false;
			bool shouldDelayBeforeRetry = false;
			bool shouldStopListening = false;

			try
			{
				using NamedPipeServerStream server = new(
					_pipeName,
					PipeDirection.InOut,
					maxNumberOfServerInstances: 1,
					PipeTransmissionMode.Message,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				await server.WaitForConnectionAsync(cancellationToken)
					.ConfigureAwait(false);
				using CancellationTokenSource requestTimeoutTokenSource =
					CancellationTokenSource.CreateLinkedTokenSource(
						cancellationToken);
				requestTimeoutTokenSource.CancelAfter(
					_connectedClientTimeout);
				CancellationToken requestCancellationToken =
					requestTimeoutTokenSource.Token;
				byte[] requestFrame = new byte[UpdateShutdownProtocol.FrameSize];
				int bytesRead = await ReadRequestFrameAsync(
					server,
					requestFrame,
					requestCancellationToken).ConfigureAwait(false);
				bool wasOversized = !server.IsMessageComplete;

				if (wasOversized &&
					!await TryDrainOversizedMessageAsync(
						server,
						requestCancellationToken)
						.ConfigureAwait(false))
				{
					continue;
				}

				bool isCompleteFrame = server.IsMessageComplete;
				Guid requestId = Guid.Empty;
				UpdateShutdownOutcome rejectionOutcome =
					UpdateShutdownOutcome.MalformedRequest;
				UpdateShutdownRequest? request = null;
				UpdateShutdownResponse response;
				bool wasDecoded = UpdateShutdownProtocol.TryDecodeRequest(
					requestFrame.AsSpan(0, bytesRead),
					out request,
					out requestId,
					out rejectionOutcome);

				if (!wasDecoded || wasOversized || !isCompleteFrame ||
					(request is null))
				{
					response = new UpdateShutdownResponse(
						requestId,
						rejectionOutcome,
						targetIdentity: null);
				}
				else
				{
					UpdateShutdownOutcome outcome = HandleRequest(request);
					hasAcceptedReservation =
						outcome == UpdateShutdownOutcome.Accepted;
					response = new UpdateShutdownResponse(
						request.RequestId,
						outcome,
						_targetIdentity);
				}

				byte[] responseFrame =
					UpdateShutdownProtocol.EncodeResponse(response);
				await server.WriteAsync(
					responseFrame,
					requestCancellationToken)
					.ConfigureAwait(false);
				await server.FlushAsync(requestCancellationToken)
					.ConfigureAwait(false);
				responseWasSent = true;
				shouldDispatchAcceptedShutdown = hasAcceptedReservation;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				shouldStopListening = true;
			}
			catch (OperationCanceledException)
			{
				shouldDelayBeforeRetry = true;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException or
					InvalidOperationException or ObjectDisposedException or
					TimeoutException)
			{
				shouldDelayBeforeRetry = true;
			}

			if (hasAcceptedReservation && !responseWasSent)
			{
				TryRollbackAcceptedShutdown();
			}

			if (shouldDispatchAcceptedShutdown)
			{
				try
				{
					_dispatchAcceptedShutdown();
				}
				catch
				{
					// Reservation 已成立，callback failure 不可讓 pipe listener 終止。
				}
			}

			if (shouldStopListening)
			{
				return;
			}

			if (shouldDelayBeforeRetry)
			{
				try
				{
					await Task.Delay(
						TimeSpan.FromMilliseconds(50),
						cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}
	}

	private void TryRollbackAcceptedShutdown()
	{
		try
		{
			_rollbackAcceptedShutdown();
		}
		catch
		{
			// Rollback callback failure 不可讓 pipe listener 終止。
		}
	}

	private static async Task<int> ReadRequestFrameAsync(
		NamedPipeServerStream server,
		Memory<byte> frame,
		CancellationToken cancellationToken)
	{
		int totalBytesRead = 0;

		do
		{
			int bytesRead = await server.ReadAsync(
				frame[totalBytesRead..],
				cancellationToken).ConfigureAwait(false);

			if (bytesRead == 0)
			{
				break;
			}

			totalBytesRead += bytesRead;
		}
		while ((totalBytesRead < frame.Length) && !server.IsMessageComplete);

		return totalBytesRead;
	}

	private static async Task<bool> TryDrainOversizedMessageAsync(
		NamedPipeServerStream server,
		CancellationToken cancellationToken)
	{
		byte[] overflowBuffer = new byte[UpdateShutdownProtocol.FrameSize];
		int totalBytesRead = 0;

		while (!server.IsMessageComplete &&
			(totalBytesRead < overflowBuffer.Length))
		{
			int bytesRead = await server.ReadAsync(
				overflowBuffer.AsMemory(totalBytesRead),
				cancellationToken).ConfigureAwait(false);

			if (bytesRead == 0)
			{
				return false;
			}

			totalBytesRead += bytesRead;
		}

		return server.IsMessageComplete;
	}

	private UpdateShutdownOutcome HandleRequest(UpdateShutdownRequest request)
	{
		if (request.Operation == UpdateShutdownOperation.Query)
		{
			return UpdateShutdownOutcome.Observed;
		}

		if ((request.ExpectedIdentity is not UpdateProcessIdentity expectedIdentity) ||
			(expectedIdentity != _targetIdentity))
		{
			return UpdateShutdownOutcome.IdentityMismatch;
		}

		UpdateShutdownOutcome outcome = _reserveShutdown();
		return outcome is UpdateShutdownOutcome.Accepted or
			UpdateShutdownOutcome.Busy or
			UpdateShutdownOutcome.AlreadyShuttingDown
			? outcome
			: UpdateShutdownOutcome.Busy;
	}
}
