using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AiUsageDashboard.App.Providers;

internal sealed class CodexAccountLogin : ICodexAccountLogin
{
	private sealed class WorkspaceConfigurationTransaction :
		ICodexWorkspaceConfigurationTransaction
	{
		private readonly ProviderProcessOperationTracker _operationTracker;
		private readonly object _sync = new();
		private IDisposable? _executableLease;
		private Func<Task>? _restoreWorkspaceConfigurationAsync;
		private bool _isCommitted;
		private bool _isDisposed;

		internal WorkspaceConfigurationTransaction(
			Func<Task> restoreWorkspaceConfigurationAsync,
			ProviderProcessOperationTracker operationTracker,
			IDisposable? executableLease)
		{
			_restoreWorkspaceConfigurationAsync =
				restoreWorkspaceConfigurationAsync ??
				throw new ArgumentNullException(
					nameof(restoreWorkspaceConfigurationAsync));
			_operationTracker = operationTracker ??
				throw new ArgumentNullException(nameof(operationTracker));
			_executableLease = executableLease;
		}

		public void Commit()
		{
			IDisposable? executableLease;

			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				if (_isCommitted)
				{
					return;
				}
				if (_operationTracker.IsContainmentCompromised)
				{
					throw new CodexAccountLoginException(
						CodexCliContainmentException.RestartRequiredMessage);
				}

				_isCommitted = true;
				_restoreWorkspaceConfigurationAsync = null;
				executableLease = _executableLease;
				_executableLease = null;
			}

			executableLease?.Dispose();
		}

		public async ValueTask DisposeAsync()
		{
			Func<Task>? restoreWorkspaceConfigurationAsync;
			IDisposable? executableLease;

			lock (_sync)
			{
				if (_isDisposed)
				{
					return;
				}

				_isDisposed = true;
				restoreWorkspaceConfigurationAsync = _isCommitted
					? null
					: _restoreWorkspaceConfigurationAsync;
				_restoreWorkspaceConfigurationAsync = null;
				executableLease = _executableLease;
				_executableLease = null;
			}

			try
			{
				if (restoreWorkspaceConfigurationAsync is not null)
				{
					if (_operationTracker.IsContainmentCompromised)
					{
						throw new CodexCliContainmentException(
							CodexCliContainmentException.RestartRequiredMessage);
					}

					await restoreWorkspaceConfigurationAsync();
				}
			}
			catch (CodexCliContainmentException exception)
			{
				throw new CodexAccountLoginException(
					CodexCliContainmentException.RestartRequiredMessage,
					exception);
			}
			catch (Exception exception)
			{
				throw new CodexAccountLoginException(
					"Codex 連接未完成，且無法復原這張卡片原本的 workspace 設定。請重新連接。",
					exception);
			}
			finally
			{
				executableLease?.Dispose();
			}
		}
	}

	private sealed class LoginSession : ICodexAccountLoginSession
	{
		private const int AccountIdentityReadAttempts = 4;
		private const int AccountIdentityReadRequestId = 11;
		private const int PostLoginConfigReadRequestId = 15;
		private const string LoginCompletedMethod = "account/login/completed";
		private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
		private static readonly TimeSpan[] AccountIdentityReadRetryDelays =
		[
			TimeSpan.FromMilliseconds(200),
			TimeSpan.FromMilliseconds(600),
			TimeSpan.FromMilliseconds(1200)
		];
		private readonly string _loginId;
		private readonly ProviderProcessOperationTracker _operationTracker;
		private readonly object _workspaceConfigurationSync = new();
		private readonly Guid? _workspaceId;
		private readonly ICodexAppServerTransport _transport;
		private IDisposable? _executableLease;
		private IDisposable? _operationLease;
		private Func<Task>? _restoreAfterSessionAsync;
		private Func<Task>? _restoreWithinSessionAsync;
		private bool _isLoginCompleted;
		private bool _isWorkspaceConfigurationTransferred;
		private int _isDisposed;
		private int _waitStarted;

		public Uri AuthorizationUri { get; }

		internal LoginSession(
			string loginId,
			Uri authorizationUri,
			Guid? workspaceId,
			ICodexAppServerTransport transport,
			ProviderProcessOperationTracker operationTracker,
			Func<Task> restoreWithinSessionAsync,
			Func<Task> restoreAfterSessionAsync,
			IDisposable operationLease,
			IDisposable? executableLease)
		{
			_loginId = loginId;
			AuthorizationUri = authorizationUri;
			_workspaceId = workspaceId;
			_transport = transport;
			_operationTracker = operationTracker ??
				throw new ArgumentNullException(nameof(operationTracker));
			_restoreWithinSessionAsync = restoreWithinSessionAsync ??
				throw new ArgumentNullException(
					nameof(restoreWithinSessionAsync));
			_restoreAfterSessionAsync = restoreAfterSessionAsync ??
				throw new ArgumentNullException(
					nameof(restoreAfterSessionAsync));
			_operationLease = operationLease;
			_executableLease = executableLease;
		}

		public async Task<CodexAccountLoginResult> WaitForCompletionAsync(
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Exchange(ref _waitStarted, 1) != 0)
			{
				throw new InvalidOperationException("Codex 登入完成狀態只能等待一次。");
			}

			using CancellationTokenSource timeoutSource = new(LoginTimeout);
			using CancellationTokenSource linkedSource =
				CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken,
					timeoutSource.Token);

			try
			{
				JsonElement parameters =
					await CodexAppServerUsagePoller.ReadNotificationAsync(
						_transport,
						LoginCompletedMethod,
						linkedSource.Token);
				ParseCompletion(parameters, _loginId);
				string accountIdentity = await ReadAccountIdentityWithRetryAsync(
					linkedSource.Token);
				if (_workspaceId is Guid expectedWorkspaceId)
				{
					Guid configuredWorkspaceId =
						await CodexAppServerUsagePoller.ReadConfiguredWorkspaceIdAsync(
							_transport,
							PostLoginConfigReadRequestId,
							linkedSource.Token);
					EnsureExpectedWorkspace(
						configuredWorkspaceId,
						expectedWorkspaceId);
				}
				else
				{
					JsonElement? configuredWorkspace =
						await CodexAppServerUsagePoller
							.ReadConfiguredWorkspaceValueAsync(
								_transport,
								PostLoginConfigReadRequestId,
								linkedSource.Token);
					EnsureWorkspaceIsUnset(configuredWorkspace);
				}
				lock (_workspaceConfigurationSync)
				{
					_isLoginCompleted = true;
				}
				return new CodexAccountLoginResult(accountIdentity, _workspaceId);
			}
			catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
			{
				_transport.Abort();

				if (cancellationToken.IsCancellationRequested)
				{
					cancellationToken.ThrowIfCancellationRequested();
				}

				throw new CodexAccountLoginException("Codex 登入逾時，請再試一次。");
			}
			catch
			{
				_transport.Abort();
				throw;
			}
		}

		public ICodexWorkspaceConfigurationTransaction
			TakeWorkspaceConfigurationTransaction()
		{
			lock (_workspaceConfigurationSync)
			{
				ObjectDisposedException.ThrowIf(
					Volatile.Read(ref _isDisposed) != 0,
					this);
				if (!_isLoginCompleted)
				{
					throw new InvalidOperationException(
						"Codex 登入尚未完成，不能提交 workspace 設定。");
				}
				if (_isWorkspaceConfigurationTransferred)
				{
					throw new InvalidOperationException(
						"Codex workspace 設定 transaction 只能取得一次。");
				}

				Func<Task> restoreAfterSessionAsync =
					_restoreAfterSessionAsync ??
					throw new InvalidOperationException(
						"Codex workspace 設定 transaction 已不可用。");
				_isWorkspaceConfigurationTransferred = true;
				_restoreWithinSessionAsync = null;
				_restoreAfterSessionAsync = null;
				IDisposable? executableLease = _executableLease;
				_executableLease = null;
				return new WorkspaceConfigurationTransaction(
					restoreAfterSessionAsync,
					_operationTracker,
					executableLease);
			}
		}

		private async Task<string> ReadAccountIdentityWithRetryAsync(
			CancellationToken cancellationToken)
		{
			Exception? lastException = null;

			for (int attempt = 0; attempt < AccountIdentityReadAttempts; attempt++)
			{
				try
				{
					return await CodexAppServerUsagePoller.ReadAccountIdentityAsync(
						_transport,
						AccountIdentityReadRequestId + attempt,
						cancellationToken);
				}
				catch (OperationCanceledException) when (
					cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (CodexUsageAccountActionRequiredException exception)
				{
					throw new CodexAccountLoginException(
						exception.Message,
						exception);
				}
				catch (Exception exception)
				{
					lastException = exception;

					if (attempt < AccountIdentityReadAttempts - 1)
					{
						await Task.Delay(
							AccountIdentityReadRetryDelays[attempt],
							cancellationToken);
					}
				}
			}

			throw new CodexAccountLoginException(
				"Codex 已登入，但 AI Usage 暫時無法確認目前登入的帳號。請稍後重新連接。",
				lastException ?? new InvalidOperationException(
					"Codex 帳號識別讀取失敗。"));
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
			{
				return;
			}

			Func<Task>? restoreWorkspaceConfigurationAsync;
			IDisposable? executableLease;
			bool shouldRestoreWorkspaceConfiguration;
			lock (_workspaceConfigurationSync)
			{
				shouldRestoreWorkspaceConfiguration =
					!_isWorkspaceConfigurationTransferred;
				restoreWorkspaceConfigurationAsync =
					shouldRestoreWorkspaceConfiguration
						? _restoreWithinSessionAsync
						: null;
				_restoreWithinSessionAsync = null;
				_restoreAfterSessionAsync = null;
				executableLease = _executableLease;
				_executableLease = null;
			}
			if (shouldRestoreWorkspaceConfiguration)
			{
				_transport.Abort();
			}

			Exception? transportCleanupException = null;

			try
			{
				try
				{
					await _transport.DisposeAsync();
				}
				catch (Exception exception)
				{
					transportCleanupException = exception;
				}
				if (_operationTracker.IsContainmentCompromised)
				{
					throw new CodexAccountLoginException(
						CodexCliContainmentException.RestartRequiredMessage);
				}

				if (restoreWorkspaceConfigurationAsync is not null)
				{
					try
					{
						await restoreWorkspaceConfigurationAsync();
					}
					catch (CodexCliContainmentException exception)
					{
						throw new CodexAccountLoginException(
							CodexCliContainmentException.RestartRequiredMessage,
							exception);
					}
					catch (Exception exception)
					{
						throw new CodexAccountLoginException(
							"Codex 連接未完成，且無法復原這張卡片原本的 workspace 設定。請重新連接。",
							exception);
					}
				}

				if (transportCleanupException is not null)
				{
					throw new CodexAccountLoginException(
						"Codex 登入程序清理失敗，請再試一次。",
						transportCleanupException);
				}
			}
			finally
			{
				executableLease?.Dispose();
				IDisposable? operationLease = Interlocked.Exchange(
					ref _operationLease,
					null);
				operationLease?.Dispose();
			}
		}

		private static void ParseCompletion(
			JsonElement parameters,
			string expectedLoginId)
		{
			if (parameters.ValueKind != JsonValueKind.Object)
			{
				throw new CodexAccountLoginException(
					UnrecognizedLoginDataMessage);
			}

			if (!parameters.TryGetProperty(
					"loginId",
					out JsonElement loginIdElement) ||
				(loginIdElement.ValueKind != JsonValueKind.String) ||
				string.IsNullOrWhiteSpace(loginIdElement.GetString()))
			{
				throw new CodexAccountLoginException(
					UnrecognizedLoginDataMessage);
			}

			if (!string.Equals(
					loginIdElement.GetString(),
					expectedLoginId,
					StringComparison.Ordinal))
			{
				throw new CodexAccountLoginException(
					"Codex 回傳的登入狀態與這次操作不符。請重新連接。");
			}

			if (!parameters.TryGetProperty("success", out JsonElement successElement) ||
				((successElement.ValueKind != JsonValueKind.True) &&
					(successElement.ValueKind != JsonValueKind.False)))
			{
				throw new CodexAccountLoginException(
					UnrecognizedLoginDataMessage);
			}

			if (parameters.TryGetProperty("error", out JsonElement errorElement) &&
				(errorElement.ValueKind != JsonValueKind.Null) &&
				(errorElement.ValueKind != JsonValueKind.String))
			{
				throw new CodexAccountLoginException(
					UnrecognizedLoginDataMessage);
			}

			if (!successElement.GetBoolean())
			{
				throw new CodexAccountLoginException(
					"Codex 登入未完成，請再試一次。");
			}
		}
	}

	private sealed record LoginStartResult(
		string LoginId,
		Uri AuthorizationUri);
	private sealed record LaunchContext(
		ProcessStartInfo StartInfo,
		WindowsOfficialCliExecutableLease? ExecutableLease);

	private const int LoginStartRequestId = 10;
	private const int SnapshotConfigReadRequestId = 7;
	private const int ConfigWriteRequestId = 8;
	private const int PreLoginConfigReadRequestId = 9;
	private const string UnrecognizedLoginDataMessage =
		"Codex 回傳的登入資料無法辨識。請更新 Codex CLI 後再重新連接。";
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
	private readonly Func<CodexCliExecutableResolution?> _executableResolver;
	private readonly Func<Guid, string> _homeDirectoryResolver;
	private readonly CodexAccountOperationGate _operationGate;
	private readonly Func<
		ProcessStartInfo,
		ProviderProcessOperationTracker,
		ICodexAppServerTransport> _transportFactory;
	private readonly TimeSpan _commandTimeout;

	public CodexAccountLogin(Func<Guid, string> homeDirectoryResolver)
		: this(
			homeDirectoryResolver,
			CodexAccountOperationGate.Shared)
	{
	}

	public CodexAccountLogin(
		Func<Guid, string> homeDirectoryResolver,
		CodexAccountOperationGate operationGate)
		: this(
			homeDirectoryResolver,
			CodexAppServerUsagePoller.ResolveExecutable,
			(startInfo, operationTracker) =>
				new CodexAppServerUsagePoller.ProcessTransport(
					startInfo,
					operationTracker),
			operationGate,
			CommandTimeout)
	{
	}

	internal CodexAccountLogin(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory)
		: this(
			homeDirectoryResolver,
			executableResolver,
			transportFactory,
			new CodexAccountOperationGate(),
			CommandTimeout)
	{
	}

	internal CodexAccountLogin(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		CodexAccountOperationGate operationGate)
		: this(
			homeDirectoryResolver,
			executableResolver,
			transportFactory,
			operationGate,
			CommandTimeout)
	{
	}

	internal CodexAccountLogin(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		CodexAccountOperationGate operationGate,
		TimeSpan commandTimeout,
		Func<CodexCliExecutableResolution?>? protectedExecutableResolver = null)
		: this(
			homeDirectoryResolver,
			protectedExecutableResolver ?? WrapExecutableResolver(executableResolver),
			WrapTransportFactory(transportFactory),
			operationGate,
			commandTimeout)
	{
		ArgumentNullException.ThrowIfNull(executableResolver);
	}

	internal CodexAccountLogin(
		Func<Guid, string> homeDirectoryResolver,
		Func<CodexCliExecutableResolution?> executableResolver,
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker,
			ICodexAppServerTransport> transportFactory,
		CodexAccountOperationGate operationGate,
		TimeSpan commandTimeout)
	{
		_homeDirectoryResolver = homeDirectoryResolver ??
			throw new ArgumentNullException(nameof(homeDirectoryResolver));
		_executableResolver = executableResolver ??
			throw new ArgumentNullException(nameof(executableResolver));
		_operationGate = operationGate ??
			throw new ArgumentNullException(nameof(operationGate));
		_transportFactory = transportFactory ??
			throw new ArgumentNullException(nameof(transportFactory));
		_commandTimeout = commandTimeout > TimeSpan.Zero
			? commandTimeout
			: throw new ArgumentOutOfRangeException(nameof(commandTimeout));
	}

	public Task<ICodexAccountLoginSession> StartAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return StartCoreAsync(accountId, workspaceId: null, cancellationToken);
	}

	public Task<ICodexAccountLoginSession> StartAsync(
		Guid accountId,
		Guid workspaceId,
		CancellationToken cancellationToken = default)
	{
		if (workspaceId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex workspace ID 不可為空。",
				nameof(workspaceId));
		}

		return StartCoreAsync(accountId, workspaceId, cancellationToken);
	}

	private async Task<ICodexAccountLoginSession> StartCoreAsync(
		Guid accountId,
		Guid? workspaceId,
		CancellationToken cancellationToken)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Codex 帳號識別碼不可為空。", nameof(accountId));
		}

		cancellationToken.ThrowIfCancellationRequested();
		using CancellationTokenSource gateTimeoutSource = new(_commandTimeout);
		using CancellationTokenSource gateLinkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				gateTimeoutSource.Token);
		IDisposable rawOperationLease;

		try
		{
			rawOperationLease = await _operationGate.EnterAsync(
				accountId,
				gateLinkedSource.Token);
		}
		catch (CodexCliContainmentException exception)
		{
			throw new CodexAccountLoginException(
				CodexCliContainmentException.RestartRequiredMessage,
				exception);
		}
		catch (OperationCanceledException) when (gateLinkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			ThrowIfAccountContainmentCompromised(accountId);

			throw new CodexAccountLoginException(
				"等待前一個 Codex 帳號作業逾時，請再試一次。");
		}

		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		IDisposable? operationLease =
			operationTracker.HoldLease(rawOperationLease);
		IDisposable? executableLease = null;

		try
		{
			using CancellationTokenSource timeoutSource = new(_commandTimeout);
			using CancellationTokenSource linkedSource =
				CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken,
					timeoutSource.Token);
			ICodexAppServerTransport? transport = null;
			LaunchContext? launchContext = null;
			JsonElement? previousWorkspaceConfiguration = null;
			bool didCaptureWorkspaceConfiguration = false;
			bool didAttemptWorkspaceConfigurationChange = false;
			bool didTransferWorkspaceConfigurationChange = false;
			bool isWorkspaceRestoreSafe = true;

			try
			{
				launchContext =
					await ProviderProcessExecution.RunSynchronousAsync(
						() => CreateStartInfoForAccount(accountId),
						linkedSource.Token,
						operationTracker,
						static context =>
						{
							context.ExecutableLease?.Dispose();
							return Task.CompletedTask;
						});
				executableLease = launchContext.ExecutableLease is null
					? null
					: operationTracker.HoldLease(launchContext.ExecutableLease);
				transport = await CodexAppServerUsagePoller.CreateTransportAsync(
					_transportFactory,
					launchContext.StartInfo,
					linkedSource.Token,
					operationTracker);
				await InitializeTransportAsync(transport, linkedSource.Token);
				previousWorkspaceConfiguration =
					await CodexAppServerUsagePoller
						.ReadConfiguredWorkspaceValueAsync(
							transport,
							SnapshotConfigReadRequestId,
							linkedSource.Token);
				didCaptureWorkspaceConfiguration = true;
				JsonElement? requestedWorkspaceConfiguration =
					CreateWorkspaceConfigurationValue(workspaceId);
				didAttemptWorkspaceConfigurationChange = true;
				JsonElement writeResult =
					await CodexAppServerUsagePoller.SendRequestAsync(
						transport,
						ConfigWriteRequestId,
						CreateConfigWriteRequest(requestedWorkspaceConfiguration),
						"config/value/write",
						linkedSource.Token);
				ValidateConfigWriteResult(
					writeResult,
					Path.Combine(
						launchContext.StartInfo.WorkingDirectory,
						"config.toml"));
				await transport.DisposeAsync();
				transport = null;
				if (operationTracker.IsContainmentCompromised)
				{
					throw new CodexCliContainmentException(
						CodexCliContainmentException.RestartRequiredMessage);
				}

				transport = await CodexAppServerUsagePoller.CreateTransportAsync(
					_transportFactory,
					launchContext.StartInfo,
					linkedSource.Token,
					operationTracker);
				await InitializeTransportAsync(transport, linkedSource.Token);
				if (workspaceId is Guid expectedWorkspaceId)
				{
					Guid configuredWorkspaceId =
						await CodexAppServerUsagePoller.ReadConfiguredWorkspaceIdAsync(
							transport,
							PreLoginConfigReadRequestId,
							linkedSource.Token);
					EnsureExpectedWorkspace(
						configuredWorkspaceId,
						expectedWorkspaceId);
				}
				else
				{
					JsonElement? configuredWorkspace =
						await CodexAppServerUsagePoller
							.ReadConfiguredWorkspaceValueAsync(
								transport,
								PreLoginConfigReadRequestId,
								linkedSource.Token);
					EnsureWorkspaceIsUnset(configuredWorkspace);
				}
				JsonElement loginResult =
					await CodexAppServerUsagePoller.SendRequestAsync(
						transport,
						LoginStartRequestId,
						CreateLoginStartRequest(),
						"account/login/start",
						linkedSource.Token);
				LoginStartResult result = ParseLoginStartResult(loginResult);
				LoginSession session = new(
					result.LoginId,
					result.AuthorizationUri,
					workspaceId,
					transport,
					operationTracker,
					() => RestoreWorkspaceConfigurationAsync(
						launchContext.StartInfo,
						operationTracker,
						previousWorkspaceConfiguration),
					() => RestoreWorkspaceConfigurationAfterSessionAsync(
						accountId,
						launchContext.StartInfo,
						previousWorkspaceConfiguration),
					operationLease!,
					executableLease);
				didTransferWorkspaceConfigurationChange = true;
				operationLease = null;
				executableLease = null;
				return session;
			}
			catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
			{
				if (transport is not null)
				{
					Task cleanupTask = AbortAndDisposeAsync(transport);
					operationTracker.Track(cleanupTask);
					if (didAttemptWorkspaceConfigurationChange)
					{
						try
						{
							await cleanupTask.WaitAsync(_commandTimeout);
						}
						catch (TimeoutException)
						{
							isWorkspaceRestoreSafe = false;
							operationTracker.MarkContainmentCompromised();
						}
					}
				}
				if (operationTracker.IsContainmentCompromised)
				{
					isWorkspaceRestoreSafe = false;
					throw new CodexCliContainmentException(
						CodexCliContainmentException.RestartRequiredMessage);
				}

				if (cancellationToken.IsCancellationRequested)
				{
					cancellationToken.ThrowIfCancellationRequested();
				}

				throw new CodexAccountLoginException(
					"Codex 登入啟動逾時，請再試一次。");
			}
			catch (CodexAccountLoginException)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw;
			}
			catch (CodexWorkspaceConfigurationException)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw;
			}
			catch (CodexWorkspaceMismatchException)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw;
			}
			catch (CodexCliContainmentException exception)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw new CodexAccountLoginException(
					CodexCliContainmentException.RestartRequiredMessage,
					exception);
			}
			catch (CodexCliProbeException exception)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw new CodexAccountLoginException(
					"暫時無法確認 Codex CLI 版本，請稍後再試。",
					exception);
			}
			catch (CodexCliUntrustedException exception)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw new CodexAccountLoginException(
					"Codex CLI 未通過官方簽章、路徑或版本驗證；請從 OpenAI 官方來源重新安裝或更新。",
					exception);
			}
			catch (Exception exception)
			{
				if (transport is not null)
				{
					await AbortAndDisposeAsync(transport);
				}

				throw new CodexAccountLoginException(
					"無法啟動 Codex 登入，請再試一次。",
					exception);
			}
			finally
			{
				if (didCaptureWorkspaceConfiguration &&
					didAttemptWorkspaceConfigurationChange &&
					isWorkspaceRestoreSafe &&
					!operationTracker.IsContainmentCompromised &&
					!didTransferWorkspaceConfigurationChange &&
					(launchContext is not null))
				{
					try
					{
						await RestoreWorkspaceConfigurationAsync(
							launchContext.StartInfo,
							operationTracker,
							previousWorkspaceConfiguration);
					}
					catch (Exception exception)
					{
						throw new CodexAccountLoginException(
							"Codex 登入未啟動，且無法復原這張卡片原本的 workspace 設定。請重新連接。",
							exception);
					}
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (
			operationTracker.IsContainmentCompromised)
		{
			throw new CodexAccountLoginException(
				CodexCliContainmentException.RestartRequiredMessage,
				exception);
		}
		finally
		{
			executableLease?.Dispose();
			operationLease?.Dispose();
		}
	}

	private void ThrowIfAccountContainmentCompromised(Guid accountId)
	{
		try
		{
			_operationGate.ThrowIfContainmentCompromised(accountId);
		}
		catch (CodexCliContainmentException exception)
		{
			throw new CodexAccountLoginException(
				CodexCliContainmentException.RestartRequiredMessage,
				exception);
		}
	}

	private LaunchContext CreateStartInfoForAccount(Guid accountId)
	{
		CodexCliExecutableResolution? resolution = _executableResolver();
		string? executablePath = resolution?.ExecutablePath;

		try
		{
			if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
					executablePath,
					out string normalizedExecutablePath) ||
				!File.Exists(normalizedExecutablePath))
			{
				throw new CodexAccountLoginException(
					"找不到 Codex CLI，請先安裝或更新 Codex。");
			}

			string homeDirectory = Path.GetFullPath(_homeDirectoryResolver(accountId));
			Directory.CreateDirectory(homeDirectory);
			return new LaunchContext(
				CodexAppServerUsagePoller.CreateStartInfo(
					normalizedExecutablePath,
					homeDirectory),
				resolution?.ExecutableLease);
		}
		catch
		{
			resolution?.ExecutableLease?.Dispose();
			throw;
		}
	}

	private static Func<CodexCliExecutableResolution?> WrapExecutableResolver(
		Func<string?> executableResolver)
	{
		return () =>
		{
			string? executablePath = executableResolver();
			return string.IsNullOrWhiteSpace(executablePath)
				? null
				: new CodexCliExecutableResolution(
					executablePath,
					VersionEvidence: null);
		};
	}

	private static Func<
		ProcessStartInfo,
		ProviderProcessOperationTracker,
		ICodexAppServerTransport> WrapTransportFactory(
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory)
	{
		ArgumentNullException.ThrowIfNull(transportFactory);
		return (startInfo, _) => transportFactory(startInfo);
	}

	private static Task AbortAndDisposeAsync(
		ICodexAppServerTransport transport)
	{
		try
		{
			transport.Abort();
		}
		catch
		{
			// 原始登入失敗比清理失敗更能協助排除問題。
		}

		return Task.Run(async () =>
		{
			try
			{
				await transport.DisposeAsync().ConfigureAwait(false);
			}
			catch
			{
				// 原始登入失敗比清理失敗更能協助排除問題。
			}
		});
	}

	private async Task RestoreWorkspaceConfigurationAfterSessionAsync(
		Guid accountId,
		ProcessStartInfo startInfo,
		JsonElement? workspaceConfiguration)
	{
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		IDisposable rawOperationLease;

		try
		{
			rawOperationLease = await _operationGate.EnterAsync(
				accountId,
				timeoutSource.Token);
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
		{
			ThrowIfAccountContainmentCompromised(accountId);
			throw new CodexWorkspaceConfigurationException(
				"等待復原 Codex workspace 設定逾時。");
		}

		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		using IDisposable operationLease =
			operationTracker.HoldLease(rawOperationLease);
		await RestoreWorkspaceConfigurationAsync(
			startInfo,
			operationTracker,
			workspaceConfiguration);
	}

	private async Task RestoreWorkspaceConfigurationAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		JsonElement? workspaceConfiguration)
	{
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		ICodexAppServerTransport? transport = null;

		try
		{
			transport = await CodexAppServerUsagePoller.CreateTransportAsync(
				_transportFactory,
				startInfo,
				timeoutSource.Token,
				operationTracker);
			await InitializeTransportAsync(transport, timeoutSource.Token);
			JsonElement writeResult =
				await CodexAppServerUsagePoller.SendRequestAsync(
					transport,
					ConfigWriteRequestId,
					CreateConfigWriteRequest(workspaceConfiguration),
					"config/value/write",
					timeoutSource.Token);
			ValidateConfigWriteResult(
				writeResult,
				Path.Combine(startInfo.WorkingDirectory, "config.toml"));
			await transport.DisposeAsync();
			transport = null;
			if (operationTracker.IsContainmentCompromised)
			{
				throw new CodexCliContainmentException(
					CodexCliContainmentException.RestartRequiredMessage);
			}

			transport = await CodexAppServerUsagePoller.CreateTransportAsync(
				_transportFactory,
				startInfo,
				timeoutSource.Token,
				operationTracker);
			await InitializeTransportAsync(transport, timeoutSource.Token);
			JsonElement? restoredWorkspaceConfiguration =
				await CodexAppServerUsagePoller.ReadConfiguredWorkspaceValueAsync(
					transport,
					PreLoginConfigReadRequestId,
					timeoutSource.Token);
			EnsureSameWorkspaceConfiguration(
				restoredWorkspaceConfiguration,
				workspaceConfiguration);
			await transport.DisposeAsync();
			transport = null;
			if (operationTracker.IsContainmentCompromised)
			{
				throw new CodexCliContainmentException(
					CodexCliContainmentException.RestartRequiredMessage);
			}
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
		{
			throw new CodexWorkspaceConfigurationException(
				"復原 Codex workspace 設定逾時。");
		}
		catch (CodexCliContainmentException)
		{
			throw;
		}
		catch (CodexWorkspaceConfigurationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new CodexWorkspaceConfigurationException(
				"無法復原 Codex workspace 設定。",
				exception);
		}
		finally
		{
			if (transport is not null)
			{
				await AbortAndDisposeAsync(transport);
			}
		}
	}

	private static async Task InitializeTransportAsync(
		ICodexAppServerTransport transport,
		CancellationToken cancellationToken)
	{
		JsonElement initializeResult =
			await CodexAppServerUsagePoller.SendRequestAsync(
				transport,
				1,
				CodexAppServerUsagePoller.CreateInitializeRequest(),
				"initialize",
				cancellationToken);

		if (initializeResult.ValueKind != JsonValueKind.Object)
		{
			throw new CodexAccountLoginException(
				UnrecognizedLoginDataMessage);
		}

		await transport.WriteLineAsync(
			CodexAppServerUsagePoller.CreateInitializedNotification(),
			cancellationToken);
	}

	private static JsonElement? CreateWorkspaceConfigurationValue(Guid? workspaceId)
	{
		return workspaceId is Guid value
			? JsonSerializer.SerializeToElement(value.ToString("D"))
			: null;
	}

	private static string CreateConfigWriteRequest(
		JsonElement? workspaceConfiguration)
	{
		return JsonSerializer.Serialize(new
		{
			method = "config/value/write",
			id = ConfigWriteRequestId,
			@params = new
			{
				keyPath = "forced_chatgpt_workspace_id",
				value = workspaceConfiguration,
				mergeStrategy = "replace"
			}
		});
	}

	private static void ValidateConfigWriteResult(
		JsonElement result,
		string expectedConfigPath)
	{
		if (result.ValueKind != JsonValueKind.Object ||
			!TryGetNonEmptyString(result, "status", out string? status) ||
			!TryGetNonEmptyString(result, "version", out _) ||
			!TryGetNonEmptyString(result, "filePath", out string? filePath) ||
			(!string.Equals(status, "ok", StringComparison.Ordinal) &&
				!string.Equals(status, "okOverridden", StringComparison.Ordinal)))
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex workspace 設定寫入結果無法辨識。");
		}

		try
		{
			if (!Path.IsPathFullyQualified(filePath!) ||
				!string.Equals(
					Path.GetFullPath(filePath!),
					Path.GetFullPath(expectedConfigPath),
					StringComparison.OrdinalIgnoreCase))
			{
				throw new CodexWorkspaceConfigurationException(
					"Codex workspace 設定未寫入帳號卡片的 private home。");
			}
		}
		catch (CodexWorkspaceConfigurationException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex workspace 設定回傳了無效路徑。",
				exception);
		}
	}

	private static bool TryGetNonEmptyString(
		JsonElement element,
		string propertyName,
		out string? value)
	{
		value = null;

		if (!element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind != JsonValueKind.String) ||
			string.IsNullOrWhiteSpace(property.GetString()))
		{
			return false;
		}

		value = property.GetString();
		return true;
	}

	private static void EnsureExpectedWorkspace(
		Guid configuredWorkspaceId,
		Guid expectedWorkspaceId)
	{
		if (configuredWorkspaceId != expectedWorkspaceId)
		{
			throw new CodexWorkspaceMismatchException();
		}
	}

	private static void EnsureWorkspaceIsUnset(JsonElement? workspaceConfiguration)
	{
		if (workspaceConfiguration is not null)
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex private home 仍套用 forced ChatGPT workspace 設定。");
		}
	}

	private static void EnsureSameWorkspaceConfiguration(
		JsonElement? actual,
		JsonElement? expected)
	{
		if ((actual is null) && (expected is null))
		{
			return;
		}

		if ((actual is null) ||
			(expected is null) ||
			!string.Equals(
				actual.Value.GetRawText(),
				expected.Value.GetRawText(),
				StringComparison.Ordinal))
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex workspace 設定復原後的內容與原設定不符。");
		}
	}

	private static string CreateLoginStartRequest()
	{
		return JsonSerializer.Serialize(new
		{
			method = "account/login/start",
			id = LoginStartRequestId,
			@params = new
			{
				type = "chatgpt"
			}
		});
	}

	private static LoginStartResult ParseLoginStartResult(JsonElement result)
	{
		if (result.ValueKind != JsonValueKind.Object)
		{
			throw new CodexAccountLoginException(
				UnrecognizedLoginDataMessage);
		}

		string type = GetRequiredString(result, "type");

		if (!string.Equals(type, "chatgpt", StringComparison.Ordinal))
		{
			throw new CodexAccountLoginException(
				"Codex 沒有提供可用的 ChatGPT 登入方式。請更新 Codex CLI 後再重新連接。");
		}

		string loginId = GetRequiredString(result, "loginId");
		string authorizationUrl = GetRequiredString(result, "authUrl");

		if (!Uri.TryCreate(
			authorizationUrl,
			UriKind.Absolute,
			out Uri? authorizationUri) ||
			!string.Equals(
				authorizationUri.Scheme,
				Uri.UriSchemeHttps,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new CodexAccountLoginException(
				"Codex 回傳的登入網址無效。");
		}

		return new LoginStartResult(loginId, authorizationUri);
	}

	private static string GetRequiredString(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind != JsonValueKind.String) ||
			string.IsNullOrWhiteSpace(property.GetString()))
		{
			throw new CodexAccountLoginException(
				UnrecognizedLoginDataMessage);
		}

		return property.GetString()!;
	}
}
