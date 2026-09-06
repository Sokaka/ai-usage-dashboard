using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.App;

internal enum SingleInstanceActivationFailure
{
	None,
	TimedOut,
	AccessDenied,
	Unavailable
}

internal enum SingleInstanceActivationRequest : byte
{
	ShowFloatingWidget = 1,
	EnsureAntigravityAccount = 2,
	ShutdownForUpdate = 3
}

internal readonly record struct SingleInstanceActivationResult(
	bool WasSent,
	SingleInstanceActivationFailure Failure);

internal readonly record struct SingleInstanceActivationWork(
	bool ShouldShowFloatingWidget,
	bool ShouldEnsureAntigravityAccount,
	bool ShouldShutdownForUpdate);

internal sealed class SingleInstanceActivationCoordinator
{
	private bool _isEnsureAntigravityAccountRunning;
	private bool _isReady;
	private bool _shouldShutdownForUpdate;
	private bool _shouldEnsureAntigravityAccount;
	private bool _shouldShowFloatingWidget;

	internal bool IsEnsureAntigravityAccountRunning =>
		_isEnsureAntigravityAccountRunning;

	internal SingleInstanceActivationWork Enqueue(
		SingleInstanceActivationRequest request)
	{
		switch (request)
		{
			case SingleInstanceActivationRequest.ShowFloatingWidget:
				_shouldShowFloatingWidget = true;
				break;
			case SingleInstanceActivationRequest.EnsureAntigravityAccount:
				_shouldEnsureAntigravityAccount = true;
				_shouldShowFloatingWidget = true;
				break;
			case SingleInstanceActivationRequest.ShutdownForUpdate:
				_shouldShutdownForUpdate = true;
				_shouldEnsureAntigravityAccount = false;
				_shouldShowFloatingWidget = false;
				break;
		}

		return TakeAvailableWork();
	}

	internal SingleInstanceActivationWork MarkReady()
	{
		_isReady = true;
		return TakeAvailableWork();
	}

	internal SingleInstanceActivationWork CompleteEnsureAntigravityAccount()
	{
		_isEnsureAntigravityAccountRunning = false;
		return TakeAvailableWork();
	}

	internal async Task<SingleInstanceActivationWork>
		RunEnsureAntigravityAccountAsync(
			Func<Task> ensureAction,
			Action<Exception> failureHandler)
	{
		ArgumentNullException.ThrowIfNull(ensureAction);
		ArgumentNullException.ThrowIfNull(failureHandler);

		if (!_isEnsureAntigravityAccountRunning)
		{
			throw new InvalidOperationException(
				"目前沒有可執行的 Antigravity 卡片確認要求。");
		}

		try
		{
			await ensureAction();
		}
		catch (Exception exception)
		{
			failureHandler(exception);
		}
		finally
		{
			_isEnsureAntigravityAccountRunning = false;
		}

		return TakeAvailableWork();
	}

	private SingleInstanceActivationWork TakeAvailableWork()
	{
		if (!_isReady)
		{
			return default;
		}

		if (_shouldShutdownForUpdate)
		{
			_shouldShutdownForUpdate = false;
			_shouldEnsureAntigravityAccount = false;
			_shouldShowFloatingWidget = false;
			return new SingleInstanceActivationWork(
				ShouldShowFloatingWidget: false,
				ShouldEnsureAntigravityAccount: false,
				ShouldShutdownForUpdate: true);
		}

		bool shouldShowFloatingWidget = _shouldShowFloatingWidget;
		_shouldShowFloatingWidget = false;
		bool shouldEnsureAntigravityAccount =
			_shouldEnsureAntigravityAccount &&
			!_isEnsureAntigravityAccountRunning;

		if (shouldEnsureAntigravityAccount)
		{
			_shouldEnsureAntigravityAccount = false;
			_isEnsureAntigravityAccountRunning = true;
		}

		return new SingleInstanceActivationWork(
			shouldShowFloatingWidget,
			shouldEnsureAntigravityAccount,
			ShouldShutdownForUpdate: false);
	}
}

internal sealed class ShutdownOperationScheduler
{
	private bool _isOperationRunning;
	private bool _isUpdateShutdownPending;

	internal bool TryBeginOperation(
		bool isUpdateShutdown,
		bool isQuitting)
	{
		if (isQuitting)
		{
			return false;
		}

		if (_isOperationRunning)
		{
			if (isUpdateShutdown)
			{
				_isUpdateShutdownPending = true;
			}

			return false;
		}

		_isOperationRunning = true;
		return true;
	}

	internal bool CompleteOperationAndTryBeginPendingUpdate(bool isQuitting)
	{
		if (!_isOperationRunning)
		{
			throw new InvalidOperationException(
				"目前沒有可完成的關閉操作。");
		}

		_isOperationRunning = false;

		if (isQuitting)
		{
			_isUpdateShutdownPending = false;
			return false;
		}

		if (!_isUpdateShutdownPending)
		{
			return false;
		}

		_isUpdateShutdownPending = false;
		_isOperationRunning = true;
		return true;
	}
}

internal sealed class SingleInstanceActivationChannel : IDisposable
{
	internal const byte ActivationAcknowledgement = 0x06;
	private static readonly TimeSpan PostAcknowledgementReceiptTimeout =
		TimeSpan.FromMilliseconds(250);

	private readonly Func<SingleInstanceActivationRequest, bool>
		_canAcceptActivation;
	private readonly Action<SingleInstanceActivationRequest>
		_activationAccepted;
	private readonly CancellationTokenSource _shutdownTokenSource = new();
	private readonly string _pipeName;
	private bool _isDisposed;
	private Task? _listenerTask;

	internal SingleInstanceActivationChannel(
		string pipeName,
		Func<SingleInstanceActivationRequest, bool> canAcceptActivation,
		Action<SingleInstanceActivationRequest>? activationAccepted = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		_canAcceptActivation = canAcceptActivation ??
			throw new ArgumentNullException(nameof(canAcceptActivation));
		_activationAccepted = activationAccepted ?? (_ => { });
		_pipeName = pipeName;
	}

	internal static string CreateCurrentUserScopedName(string prefix)
	{
		string userIdentity =
			$"{Environment.UserDomainName}\\{Environment.UserName}";
		return CreateUserScopedName(prefix, userIdentity);
	}

	internal static string CreateUserScopedName(
		string prefix,
		string userIdentity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
		ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);
		byte[] identityHash = SHA256.HashData(
			Encoding.UTF8.GetBytes(userIdentity));
		string suffix = Convert.ToHexString(identityHash.AsSpan(0, 12));
		return $"{prefix}.{suffix}";
	}

	internal static async Task<SingleInstanceActivationResult> RequestActivationAsync(
		string pipeName,
		SingleInstanceActivationRequest request,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

		if (!Enum.IsDefined(request))
		{
			throw new ArgumentOutOfRangeException(
				nameof(request),
				"不支援的啟用要求。");
		}

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(timeout),
				"連線逾時必須大於零。");
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
			await client.ConnectAsync(timeoutTokenSource.Token);
			wasConnected = true;
			await client.WriteAsync(
				new[] { (byte)request },
				timeoutTokenSource.Token);
			await client.FlushAsync(timeoutTokenSource.Token);
			byte[] acknowledgement = new byte[1];
			int bytesRead = await client.ReadAsync(
				acknowledgement,
				timeoutTokenSource.Token);

			if ((bytesRead != 1) ||
				(acknowledgement[0] != ActivationAcknowledgement))
			{
				return new SingleInstanceActivationResult(
					WasSent: false,
					SingleInstanceActivationFailure.Unavailable);
			}

			return new SingleInstanceActivationResult(
				WasSent: true,
				SingleInstanceActivationFailure.None);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new SingleInstanceActivationResult(
				WasSent: false,
				SingleInstanceActivationFailure.TimedOut);
		}
		catch (TimeoutException)
		{
			return new SingleInstanceActivationResult(
				WasSent: false,
				SingleInstanceActivationFailure.TimedOut);
		}
		catch (UnauthorizedAccessException)
		{
			return new SingleInstanceActivationResult(
				WasSent: false,
				wasConnected
					? SingleInstanceActivationFailure.Unavailable
					: SingleInstanceActivationFailure.AccessDenied);
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is InvalidOperationException))
		{
			return new SingleInstanceActivationResult(
				WasSent: false,
				SingleInstanceActivationFailure.Unavailable);
		}
	}

	internal void Start()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);

		if (_listenerTask is not null)
		{
			return;
		}

		_listenerTask = ListenAsync(_shutdownTokenSource.Token);
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

	private async Task ListenAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			SingleInstanceActivationRequest? requestToDispatch = null;
			bool shouldStopListening = false;

			try
			{
				using NamedPipeServerStream server = new(
					_pipeName,
					PipeDirection.InOut,
					maxNumberOfServerInstances: 1,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				await server.WaitForConnectionAsync(cancellationToken);
				byte[] message = new byte[1];
				int bytesRead = await server.ReadAsync(message, cancellationToken);

				if ((bytesRead == 1) &&
					TryParseRequest(
						message[0],
						out SingleInstanceActivationRequest request))
				{
					try
					{
						if (!_canAcceptActivation(request))
						{
							continue;
						}

						bool isCommittedShutdown = request ==
							SingleInstanceActivationRequest.ShutdownForUpdate;

						if (isCommittedShutdown)
						{
							// The callback reserved shutdown before acknowledgement. Once
							// reserved, dispatch it even if the client disconnects early so
							// the primary instance cannot be left frozen indefinitely.
							requestToDispatch = request;
						}

						await server.WriteAsync(
							new[] { ActivationAcknowledgement },
							cancellationToken);
						await server.FlushAsync(cancellationToken);
						byte[] unexpectedClientData = new byte[1];
						using CancellationTokenSource receiptTimeoutTokenSource =
							CancellationTokenSource.CreateLinkedTokenSource(
								cancellationToken);
						receiptTimeoutTokenSource.CancelAfter(
							PostAcknowledgementReceiptTimeout);

						try
						{
							int bytesAfterAcknowledgement = await server.ReadAsync(
								unexpectedClientData,
								receiptTimeoutTokenSource.Token);

							if ((bytesAfterAcknowledgement != 0) &&
								!isCommittedShutdown)
							{
								continue;
							}
						}
						catch (OperationCanceledException) when (
							!cancellationToken.IsCancellationRequested &&
							receiptTimeoutTokenSource.IsCancellationRequested)
						{
							// A client that received the acknowledgement may remain
							// connected. Bound the receipt wait so one client cannot
							// occupy the only server instance indefinitely.
						}
						catch (IOException)
						{
							// The client closes after reading the acknowledgement.
						}

						requestToDispatch = request;
					}
					catch (Exception)
					{
						// A UI callback failure must not stop future activation requests.
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				shouldStopListening = true;
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException) ||
				(exception is InvalidOperationException))
			{
				try
				{
					await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
				}
				catch (OperationCanceledException)
				{
					shouldStopListening = true;
				}
			}

			if (requestToDispatch is SingleInstanceActivationRequest acceptedRequest)
			{
				try
				{
					_activationAccepted(acceptedRequest);
				}
				catch
				{
					// A UI callback failure must not stop future activation requests.
				}
			}

			if (shouldStopListening)
			{
				return;
			}
		}
	}

	private static bool TryParseRequest(
		byte message,
		out SingleInstanceActivationRequest request)
	{
		switch ((SingleInstanceActivationRequest)message)
		{
			case SingleInstanceActivationRequest.ShowFloatingWidget:
				request = SingleInstanceActivationRequest.ShowFloatingWidget;
				return true;
			case SingleInstanceActivationRequest.EnsureAntigravityAccount:
				request = SingleInstanceActivationRequest.EnsureAntigravityAccount;
				return true;
			case SingleInstanceActivationRequest.ShutdownForUpdate:
				request = SingleInstanceActivationRequest.ShutdownForUpdate;
				return true;
			default:
				request = default;
				return false;
		}
	}
}
