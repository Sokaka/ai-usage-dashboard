#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

using GitHub.Copilot;

namespace AiUsageDashboard.App.Providers;

internal interface ICopilotLoginProcess : IDisposable
{
	bool HasExited { get; }

	int ExitCode { get; }

	Task WaitForExitAsync(CancellationToken cancellationToken);

	void KillEntireProcessTree();
}

internal sealed class CopilotBootstrapCredential
{
	internal string Host { get; }

	internal string Login { get; }

	internal string Token { get; }

	internal CopilotBootstrapCredential(
		string host,
		string login,
		string token)
	{
		Host = host;
		Login = login;
		Token = token;
	}

	public override string ToString()
	{
		return nameof(CopilotBootstrapCredential);
	}
}

internal interface ICopilotBootstrapClient : IAsyncDisposable
{
	Task StartAsync(CancellationToken cancellationToken);

	Task<CopilotBootstrapCredential> GetSelectedCredentialAsync(
		CancellationToken cancellationToken);

	Task StopAsync();

	Task ForceStopAsync();
}

internal sealed class CopilotCliAccountConnector : ICopilotAccountConnector
{
	private sealed record BootstrapTree(
		IReadOnlyList<FileInfo> Files,
		IReadOnlyList<DirectoryInfo> Directories,
		IReadOnlyList<DirectoryInfo> InternetCacheLinks);

	private sealed record BootstrapReadOutcome(
		CopilotBootstrapCredential? Credential,
		Exception? Failure,
		Task LifecycleCompletion);

	private sealed class LateInteractiveLoginException : Exception
	{
		internal Task ExitCompletion { get; }

		internal ICopilotLoginProcess Process { get; }

		internal CopilotAccountLoginException UserFacingFailure { get; }

		internal LateInteractiveLoginException(
			ICopilotLoginProcess process,
			Task exitCompletion,
			CopilotAccountLoginException userFacingFailure)
			: base(userFacingFailure.Message, userFacingFailure)
		{
			Process = process;
			ExitCompletion = exitCompletion;
			UserFacingFailure = userFacingFailure;
		}
	}

	private sealed class LateInteractiveLoginStartException : Exception
	{
		internal Task ContainmentCompletion { get; }

		internal CopilotAccountLoginException UserFacingFailure { get; }

		internal LateInteractiveLoginStartException(
			Task containmentCompletion,
			CopilotAccountLoginException userFacingFailure)
			: base(userFacingFailure.Message, userFacingFailure)
		{
			ContainmentCompletion = containmentCompletion;
			UserFacingFailure = userFacingFailure;
		}
	}

	private sealed class LateCleanupLeaseTracker
	{
		private readonly Task _lifecycleCompletion;
		private readonly long _trackerId;
		private IDisposable? _accountLease;
		private IDisposable? _globalLease;

		internal LateCleanupLeaseTracker(
			long trackerId,
			IDisposable accountLease,
			IDisposable globalLease,
			Task lifecycleCompletion)
		{
			_trackerId = trackerId;
			_accountLease = accountLease;
			_globalLease = globalLease;
			_lifecycleCompletion = lifecycleCompletion;
		}

		internal void Start()
		{
			_ = ReleaseLeasesAsync();
		}

		private async Task ReleaseLeasesAsync()
		{
			try
			{
				await _lifecycleCompletion.ConfigureAwait(false);
			}
			catch
			{
				BlockInteractiveLoginUntilRestart();
				return;
			}

			try
			{
				Interlocked.Exchange(ref _accountLease, null)?.Dispose();
			}
			finally
			{
				Interlocked.Exchange(ref _globalLease, null)?.Dispose();
				ActiveLateCleanupLeaseTrackers.TryRemove(_trackerId, out _);
			}
		}
	}

	internal sealed class BootstrapClient : ICopilotBootstrapClient
	{
		private sealed class BootstrapRuntimeCleanupRequestedException :
			InvalidOperationException
		{
			internal BootstrapRuntimeCleanupRequestedException()
				: base("Copilot bootstrap runtime cleanup was requested.")
			{
			}
		}

		private readonly string _connectionToken;
		private readonly string _executablePath;
		private readonly string _homeDirectory;
		private readonly Func<
			ProcessStartInfo,
			Action,
			WindowsJobContainedProcess> _runtimeProcessStarter;
		private readonly TaskCompletionSource<bool> _forceStopSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly object _sync = new();
		private Task? _cleanupTask;
		private CopilotClient? _client;
		private bool _cleanupRequested;
		private Task _standardErrorDrainTask = Task.CompletedTask;
		private Task _standardOutputDrainTask = Task.CompletedTask;
		private bool _startWasInvoked;
		private Task _unconfirmedLaunchContainmentCompletion =
			Task.CompletedTask;
		private WindowsJobContainedProcess? _runtimeProcess;

		internal BootstrapClient(string homeDirectory)
			: this(
				ResolveExecutablePath() ??
					throw new FileNotFoundException(
						"找不到 AI Usage 隨附的 GitHub Copilot CLI runtime。"),
				homeDirectory,
				WindowsJobContainedProcess.Start)
		{
		}

		internal BootstrapClient(
			string executablePath,
			string homeDirectory,
			Func<
				ProcessStartInfo,
				Action,
				WindowsJobContainedProcess> runtimeProcessStarter)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
			ArgumentNullException.ThrowIfNull(runtimeProcessStarter);
			_executablePath = Path.GetFullPath(executablePath);
			_homeDirectory = Path.GetFullPath(homeDirectory);
			_connectionToken = Guid.NewGuid().ToString("N");
			_runtimeProcessStarter = runtimeProcessStarter;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			lock (_sync)
			{
				if (_startWasInvoked)
				{
					throw new InvalidOperationException(
						"Copilot bootstrap runtime 已啟動過。");
				}

				ThrowIfCleanupRequested();
				_startWasInvoked = true;
			}

			return StartCoreAsync(cancellationToken);
		}

		public async Task<CopilotBootstrapCredential>
			GetSelectedCredentialAsync(CancellationToken cancellationToken)
		{
			CopilotClient client;
			lock (_sync)
			{
				client = _client ??
					throw new InvalidOperationException(
						"Copilot bootstrap runtime 尚未連線。");
			}

			GetAuthStatusResponse status =
				await client.GetAuthStatusAsync(cancellationToken);
			GitHub.Copilot.Rpc.AccountGetCurrentAuthResult current =
				await client.Rpc.Account.GetCurrentAuthAsync(cancellationToken);
			IList<GitHub.Copilot.Rpc.AccountAllUsers> users =
				await client.Rpc.Account.GetAllUsersAsync(cancellationToken);

			if (!status.IsAuthenticated ||
				(current.AuthInfo is not GitHub.Copilot.Rpc.AuthInfoUser selected) ||
				!TryNormalizeAccount(
					selected.Host,
					selected.Login,
					out string selectedHost,
					out string selectedLogin) ||
				!TryNormalizeAccount(
					status.Host,
					status.Login,
					out string statusHost,
					out string statusLogin) ||
				!string.Equals(
					selectedHost,
					statusHost,
					StringComparison.Ordinal) ||
				!string.Equals(
					selectedLogin,
					statusLogin,
					StringComparison.Ordinal))
			{
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.AuthenticationFailed,
					"無法唯一確認剛登入的 Copilot 帳號，請再試一次。");
			}

			GitHub.Copilot.Rpc.AccountAllUsers[] matches = users
				.Where(user =>
				{
					return (user.AuthInfo is
						GitHub.Copilot.Rpc.AuthInfoUser candidate) &&
						TryNormalizeAccount(
							candidate.Host,
							candidate.Login,
							out string candidateHost,
							out string candidateLogin) &&
						string.Equals(
							candidateHost,
							selectedHost,
							StringComparison.Ordinal) &&
						string.Equals(
							candidateLogin,
							selectedLogin,
							StringComparison.Ordinal);
				})
				.ToArray();
			string? matchedToken = matches.Length == 1
				? matches[0].Token
				: null;
			if (string.IsNullOrWhiteSpace(matchedToken) ||
				(matchedToken.Length > MaximumBootstrapCredentialCharacters) ||
				matchedToken.Any(character =>
					char.IsControl(character) || char.IsWhiteSpace(character)))
			{
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.AuthenticationFailed,
					"Copilot CLI 未提供可安全隔離的帳號 credential，請再試一次。");
			}

			return new CopilotBootstrapCredential(
				selectedHost,
				selected.Login.Trim(),
				matchedToken);
		}

		public Task StopAsync()
		{
			return RequestCleanup(forceStop: false);
		}

		public Task ForceStopAsync()
		{
			WindowsJobContainedProcess? runtimeProcess;
			lock (_sync)
			{
				_cleanupRequested = true;
				runtimeProcess = _runtimeProcess;
			}

			Exception? terminationFailure = null;
			try
			{
				runtimeProcess?.TerminateTree();
			}
			catch (Exception exception)
			{
				terminationFailure = exception;
			}
			finally
			{
				_forceStopSource.TrySetResult(true);
			}

			Task cleanupTask = RequestCleanup(forceStop: true);
			return terminationFailure is null
				? cleanupTask
				: CompleteForceStopWithFailureAsync(
					cleanupTask,
					terminationFailure);
		}

		public ValueTask DisposeAsync()
		{
			return new ValueTask(ForceStopAsync());
		}

		private async Task StartCoreAsync(CancellationToken cancellationToken)
		{
			ProcessStartInfo startInfo = CreateBootstrapRuntimeStartInfo(
				_executablePath,
				_homeDirectory,
				_connectionToken);
			WindowsJobContainedProcess runtimeProcess;

			lock (_sync)
			{
				ThrowIfCleanupRequested();
				try
				{
					runtimeProcess = _runtimeProcessStarter(
						startInfo,
						ThrowIfRuntimeLaunchBlocked);
				}
				catch (WindowsJobContainedProcessLaunchException exception)
				{
					if (!exception.IsTreeEmptyConfirmed)
					{
						_unconfirmedLaunchContainmentCompletion =
							exception.ContainmentCompletion;
						BlockInteractiveLoginUntilRestart();
						throw CreateInteractiveLoginBlockedException();
					}

					if (exception.InnerException is
						CopilotAccountLoginException loginException)
					{
						throw loginException;
					}

					throw new InvalidOperationException(
						"GitHub Copilot bootstrap runtime 未能在受驗證的 Job Object 中啟動。",
						exception);
				}

				_runtimeProcess = runtimeProcess;
				_standardErrorDrainTask = DrainStreamAsync(
					runtimeProcess.StandardError);
			}

			using CancellationTokenSource startupSource =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			startupSource.CancelAfter(BootstrapRuntimeStartupTimeout);
			int port;
			try
			{
				port = await ReadBootstrapRuntimePortAsync(
					runtimeProcess.StandardOutput,
					startupSource.Token);
			}
			catch (OperationCanceledException) when (
				!cancellationToken.IsCancellationRequested &&
				startupSource.IsCancellationRequested)
			{
				throw new TimeoutException(
					"Copilot bootstrap runtime 未在期限內提供安全的 loopback endpoint。");
			}

			lock (_sync)
			{
				_standardOutputDrainTask = DrainStreamAsync(
					runtimeProcess.StandardOutput);
				ThrowIfCleanupRequested();
			}

			CopilotClient client = new(new CopilotClientOptions
			{
				Connection = RuntimeConnection.ForUri(
					$"http://127.0.0.1:{port}",
					_connectionToken),
				LogLevel = CopilotLogLevel.None
			});
			bool didAcceptClient;
			lock (_sync)
			{
				didAcceptClient = !_cleanupRequested;
				if (didAcceptClient)
				{
					_client = client;
				}
			}

			if (!didAcceptClient)
			{
				await client.DisposeAsync();
				throw new BootstrapRuntimeCleanupRequestedException();
			}

			try
			{
				await client.StartAsync(startupSource.Token);
			}
			catch (OperationCanceledException) when (
				!cancellationToken.IsCancellationRequested &&
				startupSource.IsCancellationRequested)
			{
				throw new TimeoutException(
					"Copilot bootstrap runtime 連線逾時。");
			}
		}

		private Task RequestCleanup(bool forceStop)
		{
			lock (_sync)
			{
				_cleanupRequested = true;
				if (forceStop)
				{
					_forceStopSource.TrySetResult(true);
				}

				return _cleanupTask ??= CleanupCoreAsync();
			}
		}

		private async Task CleanupCoreAsync()
		{
			CopilotClient? client;
			WindowsJobContainedProcess? runtimeProcess;
			Task standardErrorDrainTask;
			Task standardOutputDrainTask;
			Task unconfirmedLaunchContainmentCompletion;
			lock (_sync)
			{
				client = _client;
				runtimeProcess = _runtimeProcess;
				standardErrorDrainTask = _standardErrorDrainTask;
				standardOutputDrainTask = _standardOutputDrainTask;
				unconfirmedLaunchContainmentCompletion =
					_unconfirmedLaunchContainmentCompletion;
			}

			List<Exception> failures = new();
			if (client is not null)
			{
				try
				{
					if (_forceStopSource.Task.IsCompleted)
					{
						await client.ForceStopAsync();
					}
					else
					{
						Task stopTask = client.StopAsync();
						Task firstCompletion = await Task.WhenAny(
							stopTask,
							_forceStopSource.Task);
						if (firstCompletion == _forceStopSource.Task)
						{
							await client.ForceStopAsync();
						}

						await stopTask;
					}
				}
				catch (Exception exception)
				{
					failures.Add(exception);
				}
			}

			await AwaitContainmentCompletionFailClosedAsync(
				unconfirmedLaunchContainmentCompletion);

			if (runtimeProcess is not null)
			{
				await ConfirmBootstrapRuntimeTreeEmptyAsync(runtimeProcess);
			}

			try
			{
				await Task.WhenAll(
					standardOutputDrainTask,
					standardErrorDrainTask).WaitAsync(
					BootstrapRuntimeDrainTimeout);
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}

			if (client is not null)
			{
				try
				{
					await client.DisposeAsync();
				}
				catch (Exception exception)
				{
					failures.Add(exception);
				}
			}

			try
			{
				runtimeProcess?.Dispose();
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}

			lock (_sync)
			{
				_client = null;
				_runtimeProcess = null;
			}

			if (failures.Count == 1)
			{
				ExceptionDispatchInfo.Capture(failures[0]).Throw();
			}

			if (failures.Count > 1)
			{
				throw new AggregateException(failures);
			}
		}

		private void ThrowIfRuntimeLaunchBlocked()
		{
			ThrowIfInteractiveLoginBlocked();
			ThrowIfCleanupRequested();
		}

		private void ThrowIfCleanupRequested()
		{
			if (_cleanupRequested)
			{
				throw new BootstrapRuntimeCleanupRequestedException();
			}
		}

		private static async Task CompleteForceStopWithFailureAsync(
			Task cleanupTask,
			Exception terminationFailure)
		{
			try
			{
				await cleanupTask;
			}
			catch (Exception cleanupFailure)
			{
				throw new AggregateException(
					terminationFailure,
					cleanupFailure);
			}

			ExceptionDispatchInfo.Capture(terminationFailure).Throw();
		}

		private static async Task AwaitContainmentCompletionFailClosedAsync(
			Task containmentCompletion)
		{
			try
			{
				await containmentCompletion.ConfigureAwait(false);
			}
			catch
			{
				await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
			}
		}
	}

	private sealed class ConnectionCandidate : ICopilotConnectionCandidate
	{
		private readonly Guid _accountId;
		private readonly ICopilotCredentialStore _credentialStore;
		private readonly string _providerAccountIdentity;
		private IDisposable? _accountLease;
		private IDisposable? _globalLease;
		private bool _isCommitted;
		private bool _isDisposed;

		public CopilotUsageReport UsageReport { get; }

		internal ConnectionCandidate(
			Guid accountId,
			string providerAccountIdentity,
			CopilotUsageReport usageReport,
			ICopilotCredentialStore credentialStore,
			IDisposable accountLease,
			IDisposable globalLease)
		{
			_accountId = accountId;
			_providerAccountIdentity = providerAccountIdentity;
			UsageReport = usageReport;
			_credentialStore = credentialStore;
			_accountLease = accountLease;
			_globalLease = globalLease;
		}

		public Task CommitAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			cancellationToken.ThrowIfCancellationRequested();
			if (_isCommitted)
			{
				return Task.CompletedTask;
			}

			if (!_credentialStore.CommitStaged(
					_accountId,
					_providerAccountIdentity))
			{
				throw new CopilotClientException(
					CopilotFailureKind.AuthenticationRequired,
					"找不到待提交的 Copilot credential。");
			}

			_isCommitted = true;
			ReleaseLeases();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			if (_isDisposed)
			{
				return ValueTask.CompletedTask;
			}

			_isDisposed = true;
			try
			{
				if (!_isCommitted)
				{
					_credentialStore.DiscardStaged(_accountId);
				}
			}
			finally
			{
				ReleaseLeases();
			}

			return ValueTask.CompletedTask;
		}

		private void ReleaseLeases()
		{
			try
			{
				Interlocked.Exchange(ref _accountLease, null)?.Dispose();
			}
			finally
			{
				Interlocked.Exchange(ref _globalLease, null)?.Dispose();
			}
		}
	}

	private sealed class LoginProcess : ICopilotLoginProcess
	{
		private readonly WindowsJobContainedProcess _process;
		private int? _exitCode;

		public bool HasExited
		{
			get
			{
				if (_exitCode.HasValue)
				{
					return true;
				}

				if (!_process.TryGetExitCode(out int exitCode))
				{
					return false;
				}

				_exitCode = exitCode;
				return true;
			}
		}

		public int ExitCode => _exitCode ??
			throw new InvalidOperationException(
				"Copilot login process 尚未結束。");

		internal LoginProcess(WindowsJobContainedProcess process)
		{
			_process = process ?? throw new ArgumentNullException(nameof(process));
		}

		public async Task WaitForExitAsync(CancellationToken cancellationToken)
		{
			_exitCode = await _process.WaitForExitAsync(cancellationToken);
		}

		public void KillEntireProcessTree()
		{
			_process.TerminateTree();
		}

		public void Dispose()
		{
			_process.Dispose();
		}
	}

	internal const long MaximumBootstrapFileBytes = 256L * 1024 * 1024;
	internal const int MaximumBootstrapTreeEntryCount = 4096;
	internal const long MaximumBootstrapTotalBytes = 512L * 1024 * 1024;
	private const int BootstrapFileScanBufferBytes = 64 * 1024;
	private const int MaximumBootstrapCredentialCharacters = 4096;
	private const int MaximumBootstrapRootEntryCount = 512;
	private const int MaximumBootstrapRuntimeAnnouncementBytes = 256;
	private const int MaximumBootstrapRuntimeStartupBytes = 64 * 1024;
	private const string BootstrapRuntimePortAnnouncementPrefix =
		"CLI server listening on port ";
	private const string InteractiveLoginRestartMessage =
		"Copilot 登入程序的安全狀態無法確認。請重新啟動 AI Usage 後再試。";
	private static readonly string InternetCacheJunctionRelativePath = Path.Combine(
		"AppData",
		"Local",
		"Microsoft",
		"Windows",
		"INetCache",
		"Content.IE5");
	private static readonly string InternetCacheTargetRelativePath = Path.Combine(
		"AppData",
		"Local",
		"Microsoft",
		"Windows",
		"INetCache",
		"IE");
	private static readonly ConcurrentDictionary<long, LateCleanupLeaseTracker>
		ActiveLateCleanupLeaseTrackers = new();
	private static readonly TimeSpan BootstrapCleanupTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan BootstrapRuntimeDrainTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan BootstrapRuntimeStartupTimeout =
		TimeSpan.FromSeconds(30);
	private static readonly TimeSpan BootstrapRuntimeTerminationTimeout =
		TimeSpan.FromSeconds(10);
	private static readonly TimeSpan LockRetryDelay =
		TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan DefaultProcessTerminationTimeout =
		TimeSpan.FromSeconds(10);
	private static string? _lateCleanupFailureMessage;
	private static long _nextLateCleanupLeaseTrackerId;
	private readonly CopilotAccountOperationGate _accountOperationGate;
	private readonly Func<string, ICopilotBootstrapClient>
		_bootstrapClientFactory;
	private readonly ICopilotCredentialStore _credentialStore;
	private readonly Func<string?> _executableResolver;
	private readonly Func<Guid, string> _homeDirectoryResolver;
	private readonly Func<ProcessStartInfo, ICopilotLoginProcess>
		_processStarter;
	private readonly TimeSpan _processTerminationTimeout;
	private readonly CopilotSdkQuotaClient _quotaClient;

	internal int ActiveLateCleanupCount =>
		ActiveLateCleanupLeaseTrackers.Count;

	internal CopilotCliAccountConnector(
		Func<Guid, string> homeDirectoryResolver,
		CopilotAccountOperationGate accountOperationGate,
		ICopilotCredentialStore credentialStore,
		CopilotSdkQuotaClient quotaClient)
		: this(
			homeDirectoryResolver,
			accountOperationGate,
			credentialStore,
			quotaClient,
			ResolveExecutablePath,
			StartProcess,
			static homeDirectory => new BootstrapClient(homeDirectory))
	{
	}

	internal CopilotCliAccountConnector(
		Func<Guid, string> homeDirectoryResolver,
		CopilotAccountOperationGate accountOperationGate,
		ICopilotCredentialStore credentialStore,
		CopilotSdkQuotaClient quotaClient,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICopilotLoginProcess> processStarter,
		Func<string, ICopilotBootstrapClient> bootstrapClientFactory,
		TimeSpan? processTerminationTimeout = null)
	{
		_homeDirectoryResolver = homeDirectoryResolver ??
			throw new ArgumentNullException(nameof(homeDirectoryResolver));
		_accountOperationGate = accountOperationGate ??
			throw new ArgumentNullException(nameof(accountOperationGate));
		_credentialStore = credentialStore ??
			throw new ArgumentNullException(nameof(credentialStore));
		_quotaClient = quotaClient ??
			throw new ArgumentNullException(nameof(quotaClient));
		_executableResolver = executableResolver ??
			throw new ArgumentNullException(nameof(executableResolver));
		_processStarter = processStarter ??
			throw new ArgumentNullException(nameof(processStarter));
		_bootstrapClientFactory = bootstrapClientFactory ??
			throw new ArgumentNullException(nameof(bootstrapClientFactory));
		_processTerminationTimeout = processTerminationTimeout ??
			DefaultProcessTerminationTimeout;
		if (_processTerminationTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(processTerminationTimeout));
		}
	}

	public async Task<ICopilotConnectionCandidate> BeginConnectAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		bool allowAccountSwitch,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		if (cancellationToken.IsCancellationRequested)
		{
			throw CreateCancellationException();
		}

		string activeHomeDirectory = ResolveHomeDirectory(accountId);
		string bootstrapRoot = ResolveBootstrapRoot(activeHomeDirectory);
		string bootstrapDirectory = Path.Combine(
			bootstrapRoot,
			Guid.NewGuid().ToString("N"));
		IDisposable? globalLease = null;
		IDisposable? accountLease = null;
		bool didTransferBootstrapCleanup = false;
		bool didStageCredential = false;

		try
		{
			globalLease = await AcquireGlobalLockAsync(
				ResolveGlobalLockPath(activeHomeDirectory),
				cancellationToken);
			try
			{
				await SweepOrphanedBootstrapContextsAsync(
					bootstrapRoot,
					cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.ProcessFailed,
					"無法安全清理上一次 Copilot 登入資料，因此未啟動新的登入。請重新啟動 AI Usage 後再試。");
			}

			accountLease = await _accountOperationGate.EnterAsync(
				accountId,
				cancellationToken);
			_credentialStore.RecoverStaged(
				accountId,
				expectedProviderAccountIdentity);
			Directory.CreateDirectory(bootstrapDirectory);

			try
			{
				await RunInteractiveLoginAsync(
					bootstrapDirectory,
					cancellationToken);
			}
			catch (LateInteractiveLoginException exception)
			{
				Task lifecycleCompletion =
					CompleteLateInteractiveLoginCleanupAsync(
						exception.Process,
						exception.ExitCompletion,
						bootstrapDirectory,
						bootstrapRoot);
				RetainLateCleanupLeases(
					accountLease,
					globalLease,
					lifecycleCompletion);
				accountLease = null;
				globalLease = null;
				didTransferBootstrapCleanup = true;
				throw exception.UserFacingFailure;
			}
			catch (LateInteractiveLoginStartException exception)
			{
				Task lifecycleCompletion =
					CompleteLateInteractiveLoginStartCleanupAsync(
						exception.ContainmentCompletion,
						bootstrapDirectory,
						bootstrapRoot);
				RetainLateCleanupLeases(
					accountLease,
					globalLease,
					lifecycleCompletion);
				accountLease = null;
				globalLease = null;
				didTransferBootstrapCleanup = true;
				throw exception.UserFacingFailure;
			}
			BootstrapReadOutcome bootstrapOutcome =
				await ReadBootstrapCredentialWithLifecycleAsync(
					bootstrapDirectory,
					cancellationToken);
			bool didTransferBootstrapLeases = false;
			if (!await WaitForLifecycleCompletionAsync(
					bootstrapOutcome.LifecycleCompletion))
			{
				Task lifecycleCompletion =
					CompleteLateBootstrapSdkCleanupAsync(
						bootstrapOutcome.LifecycleCompletion,
						bootstrapDirectory,
						bootstrapRoot,
						bootstrapOutcome.Credential?.Token);
				RetainLateCleanupLeases(
					accountLease,
					globalLease,
					lifecycleCompletion);
				accountLease = null;
				globalLease = null;
				didTransferBootstrapCleanup = true;
				didTransferBootstrapLeases = true;
			}

			if (bootstrapOutcome.Failure is not null)
			{
				if (bootstrapOutcome.Failure is
					OperationCanceledException or CopilotAccountLoginException)
				{
					ExceptionDispatchInfo.Capture(
						bootstrapOutcome.Failure).Throw();
				}

				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.ProcessFailed,
					"無法安全驗證 Copilot 登入結果，請再試一次。");
			}

			if (didTransferBootstrapLeases)
			{
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.ProcessFailed,
					"Copilot 登入驗證程序尚未安全結束，請稍後再試。");
			}

			CopilotBootstrapCredential bootstrapCredential =
				bootstrapOutcome.Credential ??
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.AuthenticationFailed,
					"Copilot CLI 未提供可驗證的帳號 credential。");
			await EnsureTokenWasNotPersistedAsync(
				bootstrapDirectory,
				bootstrapCredential.Token,
				cancellationToken);
			DeleteBootstrapDirectory(bootstrapDirectory, bootstrapRoot);

			(
				CopilotUsageReport? probeReport,
				Exception? probeFailure,
				Task probeLifecycleCompletion) =
				await _quotaClient.ProbeTokenWithLifecycleAsync(
					accountId,
					bootstrapCredential.Token,
					bootstrapCredential.Host,
					cancellationToken);
			bool didTransferProbeLeases = false;
			if (!await WaitForLifecycleCompletionAsync(probeLifecycleCompletion))
			{
				RetainLateCleanupLeases(
					accountLease,
					globalLease,
					probeLifecycleCompletion);
				accountLease = null;
				globalLease = null;
				didTransferProbeLeases = true;
			}

			if (probeFailure is not null)
			{
				CopilotSdkQuotaClient.ThrowNormalized(probeFailure);
			}

			if (didTransferProbeLeases)
			{
				throw new CopilotClientException(
					CopilotFailureKind.Transient,
					"GitHub Copilot quota 程序尚未安全結束，請稍後再試。");
			}

			CopilotUsageReport usageReport = probeReport ??
				throw new CopilotClientException(
					CopilotFailureKind.InvalidResponse,
					"GitHub Copilot 未回傳用量資料。");
			string observedIdentity = CopilotAccountIdentityRules.Create(
				usageReport.Account);
			if (!allowAccountSwitch &&
				CopilotAccountIdentityRules.TryParse(
					expectedProviderAccountIdentity,
					out string? expectedIdentity) &&
				!CopilotAccountIdentityRules.AreEquivalent(
					expectedIdentity,
					observedIdentity))
			{
				throw new CopilotClientException(
					CopilotFailureKind.AccountMismatch,
					"剛登入的 GitHub 帳號與這張卡片不同。");
			}

			_credentialStore.Stage(
				accountId,
				new CopilotStoredCredential(
					observedIdentity,
					bootstrapCredential.Token));
			didStageCredential = true;
			IDisposable retainedAccountLease = accountLease ??
				throw new InvalidOperationException(
					"Copilot connection candidate 缺少 account lease。");
			IDisposable retainedGlobalLease = globalLease ??
				throw new InvalidOperationException(
					"Copilot connection candidate 缺少 global login lease。");
			ConnectionCandidate candidate = new(
				accountId,
				observedIdentity,
				usageReport,
				_credentialStore,
				retainedAccountLease,
				retainedGlobalLease);
			accountLease = null;
			globalLease = null;
			return candidate;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw CreateCancellationException();
		}
		finally
		{
			if (didStageCredential && (accountLease is not null))
			{
				try
				{
					_credentialStore.DiscardStaged(accountId);
				}
				catch
				{
				}
			}

			try
			{
				if (!didTransferBootstrapCleanup)
				{
					await DeleteBootstrapDirectoryWithRetryAsync(
						bootstrapDirectory,
						bootstrapRoot);
				}
			}
			finally
			{
				accountLease?.Dispose();
				globalLease?.Dispose();
			}
		}
	}

	internal static ProcessStartInfo CreateBootstrapRuntimeStartInfo(
		string executablePath,
		string homeDirectory,
		string connectionToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionToken);
		if (connectionToken.Any(character =>
				char.IsControl(character) || char.IsWhiteSpace(character)))
		{
			throw new ArgumentException(
				"Copilot runtime connection token 不可包含空白或控制字元。",
				nameof(connectionToken));
		}

		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedExecutablePath) ||
			!string.Equals(
				Path.GetExtension(normalizedExecutablePath),
				".exe",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new CopilotAccountLoginException(
				CopilotAccountLoginFailureKind.RuntimeUnavailable,
				"Copilot CLI 路徑無效，請更新或修復 AI Usage。");
		}

		string normalizedHomeDirectory = Path.GetFullPath(homeDirectory);
		Directory.CreateDirectory(normalizedHomeDirectory);
		ProcessStartInfo startInfo = new()
		{
			FileName = normalizedExecutablePath,
			WorkingDirectory = normalizedHomeDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardInput = false,
			RedirectStandardOutput = true
		};
		startInfo.ArgumentList.Add("--headless");
		startInfo.ArgumentList.Add("--no-auto-update");
		startInfo.ArgumentList.Add("--log-level");
		startInfo.ArgumentList.Add("none");
		CopilotProcessEnvironment.Apply(startInfo, normalizedHomeDirectory);
		startInfo.Environment["COPILOT_CONNECTION_TOKEN"] = connectionToken;
		return startInfo;
	}

	internal static bool TryParseBootstrapRuntimePortAnnouncement(
		string? announcement,
		out int port)
	{
		port = default;
		if ((announcement is null) ||
			!announcement.StartsWith(
				BootstrapRuntimePortAnnouncementPrefix,
				StringComparison.Ordinal))
		{
			return false;
		}

		ReadOnlySpan<char> portText = announcement.AsSpan(
			BootstrapRuntimePortAnnouncementPrefix.Length);
		if (portText.IsEmpty)
		{
			return false;
		}

		foreach (char character in portText)
		{
			if (character is < '0' or > '9')
			{
				return false;
			}
		}

		if (!int.TryParse(
				portText,
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out port) ||
			(port is < 1 or > 65535))
		{
			port = default;
			return false;
		}

		return true;
	}

	internal static async Task<int> ReadBootstrapRuntimePortAsync(
		Stream standardOutput,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(standardOutput);
		byte[] readBuffer = new byte[4096];
		byte[] lineBuffer = new byte[MaximumBootstrapRuntimeAnnouncementBytes];
		int lineLength = 0;
		int totalBytes = 0;
		bool isLineOversized = false;

		while (true)
		{
			int bytesRead = await standardOutput.ReadAsync(
				readBuffer,
				cancellationToken);
			if (bytesRead == 0)
			{
				break;
			}

			totalBytes = checked(totalBytes + bytesRead);
			if (totalBytes > MaximumBootstrapRuntimeStartupBytes)
			{
				throw new InvalidDataException(
					"Copilot bootstrap runtime 啟動輸出超過安全上限。");
			}

			for (int index = 0; index < bytesRead; index++)
			{
				byte value = readBuffer[index];
				if (value != (byte)'\n')
				{
					if (lineLength < lineBuffer.Length)
					{
						lineBuffer[lineLength++] = value;
					}
					else
					{
						isLineOversized = true;
					}

					continue;
				}

				if (!isLineOversized &&
					TryParseBootstrapRuntimePortAnnouncement(
						DecodeBootstrapRuntimeAnnouncement(
							lineBuffer,
							lineLength),
						out int port))
				{
					return port;
				}

				lineLength = 0;
				isLineOversized = false;
			}
		}

		if (!isLineOversized &&
			(lineLength > 0) &&
			TryParseBootstrapRuntimePortAnnouncement(
				DecodeBootstrapRuntimeAnnouncement(lineBuffer, lineLength),
				out int finalPort))
		{
			return finalPort;
		}

		throw new InvalidDataException(
			"Copilot bootstrap runtime 未提供安全的 loopback endpoint。");
	}

	private static string DecodeBootstrapRuntimeAnnouncement(
		byte[] lineBuffer,
		int lineLength)
	{
		if ((lineLength > 0) &&
			(lineBuffer[lineLength - 1] == (byte)'\r'))
		{
			lineLength--;
		}

		return Encoding.ASCII.GetString(lineBuffer, 0, lineLength);
	}

	private static async Task DrainStreamAsync(Stream stream)
	{
		byte[] buffer = new byte[4096];
		while (await stream.ReadAsync(buffer) > 0)
		{
		}
	}

	private static async Task ConfirmBootstrapRuntimeTreeEmptyAsync(
		WindowsJobContainedProcess runtimeProcess)
	{
		TimeSpan retryDelay = TimeSpan.FromMilliseconds(100);
		while (true)
		{
			try
			{
				await runtimeProcess.TerminateTreeAndConfirmEmptyAsync(
					BootstrapRuntimeTerminationTimeout);
				return;
			}
			catch
			{
				await Task.Delay(retryDelay);
				retryDelay = TimeSpan.FromMilliseconds(Math.Min(
					retryDelay.TotalMilliseconds * 2,
					5000));
			}
		}
	}

	internal static ProcessStartInfo CreateLoginStartInfo(
		string executablePath,
		string homeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedExecutablePath) ||
			!string.Equals(
				Path.GetExtension(normalizedExecutablePath),
				".exe",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new CopilotAccountLoginException(
				CopilotAccountLoginFailureKind.RuntimeUnavailable,
				"Copilot CLI 路徑無效，請更新或修復 AI Usage。");
		}

		string normalizedHomeDirectory = Path.GetFullPath(homeDirectory);
		Directory.CreateDirectory(normalizedHomeDirectory);
		ProcessStartInfo startInfo = new()
		{
			FileName = normalizedExecutablePath,
			WorkingDirectory = normalizedHomeDirectory,
			UseShellExecute = false,
			CreateNoWindow = false,
			RedirectStandardError = false,
			RedirectStandardInput = false,
			RedirectStandardOutput = false
		};
		startInfo.ArgumentList.Add("--no-auto-update");
		startInfo.ArgumentList.Add("login");
		startInfo.ArgumentList.Add("--web-flow");
		CopilotProcessEnvironment.Apply(startInfo, normalizedHomeDirectory);
		return startInfo;
	}

	internal static string? ResolveExecutablePath(
		string baseDirectory,
		Architecture processArchitecture,
		Func<string, bool> fileExists)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
		ArgumentNullException.ThrowIfNull(fileExists);
		string? runtimeIdentifier = processArchitecture switch
		{
			Architecture.X64 => "win-x64",
			Architecture.Arm64 => "win-arm64",
			_ => null
		};
		if (runtimeIdentifier is null)
		{
			return null;
		}

		string bundledPath = Path.Combine(
			baseDirectory,
			"runtimes",
			runtimeIdentifier,
			"native",
			"copilot.exe");
		return TryResolveCandidate(
			bundledPath,
			fileExists,
			out string resolvedPath)
			? resolvedPath
			: null;
	}

	internal static string? ResolveExecutablePath()
	{
		return ResolveExecutablePath(
			AppContext.BaseDirectory,
			RuntimeInformation.ProcessArchitecture,
			File.Exists);
	}

	private static async Task<FileStream> AcquireGlobalLockAsync(
		string lockPath,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(lockPath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("Copilot login lock 路徑無效。");
		}

		Directory.CreateDirectory(directoryPath);
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ThrowIfInteractiveLoginBlocked();
			try
			{
				return new FileStream(
					lockPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.Asynchronous);
			}
			catch (IOException)
			{
				await Task.Delay(LockRetryDelay, cancellationToken);
			}
		}
	}

	private async Task<BootstrapReadOutcome>
		ReadBootstrapCredentialWithLifecycleAsync(
		string bootstrapDirectory,
		CancellationToken cancellationToken)
	{
		ICopilotBootstrapClient client =
			_bootstrapClientFactory(bootstrapDirectory);
		CopilotBootstrapCredential? credential = null;
		Exception? operationFailure = null;
		bool didStart = false;
		bool startWasInvoked = false;
		List<Task> lifecycleTasks = new();

		try
		{
			startWasInvoked = true;
			Task startTask = client.StartAsync(cancellationToken);
			lifecycleTasks.Add(startTask);
			await startTask.WaitAsync(cancellationToken);
			didStart = true;
			Task<CopilotBootstrapCredential> credentialTask =
				client.GetSelectedCredentialAsync(cancellationToken);
			lifecycleTasks.Add(credentialTask);
			credential = await credentialTask.WaitAsync(cancellationToken);
		}
		catch (Exception exception)
		{
			operationFailure = exception;
		}

		bool shouldAttemptStop = didStart ||
			(startWasInvoked &&
				(operationFailure is OperationCanceledException or TimeoutException));
		Exception? cleanupFailure = await CleanupBootstrapClientAsync(
			client,
			shouldAttemptStop,
			lifecycleTasks);
		Task lifecycleCompletion = ObserveTasksAsync(lifecycleTasks);
		return new BootstrapReadOutcome(
			credential,
			operationFailure ?? cleanupFailure,
			lifecycleCompletion);
	}

	private static async Task<Exception?> CleanupBootstrapClientAsync(
		ICopilotBootstrapClient client,
		bool shouldAttemptStop,
		ICollection<Task> lifecycleTasks)
	{
		List<Exception> failures = new();

		if (shouldAttemptStop)
		{
			try
			{
				Task stopTask = client.StopAsync();
				lifecycleTasks.Add(stopTask);
				await stopTask.WaitAsync(BootstrapCleanupTimeout);
			}
			catch (Exception stopException)
			{
				failures.Add(stopException);

				try
				{
					Task forceStopTask = client.ForceStopAsync();
					lifecycleTasks.Add(forceStopTask);
					await forceStopTask.WaitAsync(BootstrapCleanupTimeout);
				}
				catch (Exception forceStopException)
				{
					failures.Add(forceStopException);
				}
			}
		}

		try
		{
			Task disposeTask = client.DisposeAsync().AsTask();
			lifecycleTasks.Add(disposeTask);
			await disposeTask.WaitAsync(BootstrapCleanupTimeout);
		}
		catch (Exception disposeException)
		{
			failures.Add(disposeException);
		}

		return failures.Count switch
		{
			0 => null,
			1 => failures[0],
			_ => new AggregateException(failures)
		};
	}

	private static async Task<bool> WaitForLifecycleCompletionAsync(
		Task lifecycleCompletion)
	{
		ArgumentNullException.ThrowIfNull(lifecycleCompletion);
		if (lifecycleCompletion.IsCompletedSuccessfully)
		{
			return true;
		}

		try
		{
			await lifecycleCompletion.WaitAsync(BootstrapCleanupTimeout);
			return lifecycleCompletion.IsCompletedSuccessfully;
		}
		catch
		{
			return false;
		}
	}

	private static async Task ObserveTasksAsync(
		IReadOnlyCollection<Task> tasks)
	{
		foreach (Task task in tasks)
		{
			_ = task.ContinueWith(
				static faultedTask => _ = faultedTask.Exception,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously |
					TaskContinuationOptions.OnlyOnFaulted,
				TaskScheduler.Default);
		}

		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static void RetainLateCleanupLeases(
		IDisposable? accountLease,
		IDisposable? globalLease,
		Task lifecycleCompletion)
	{
		if ((accountLease is null) || (globalLease is null))
		{
			throw new InvalidOperationException(
				"Copilot late cleanup 缺少必要的 operation lease。");
		}

		long trackerId = Interlocked.Increment(
			ref _nextLateCleanupLeaseTrackerId);
		LateCleanupLeaseTracker tracker = new(
			trackerId,
			accountLease,
			globalLease,
			lifecycleCompletion);
		ActiveLateCleanupLeaseTrackers[trackerId] = tracker;
		tracker.Start();
	}

	private async Task RunInteractiveLoginAsync(
		string bootstrapDirectory,
		CancellationToken cancellationToken)
	{
		string executablePath;
		try
		{
			executablePath = _executableResolver() ??
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.RuntimeUnavailable,
					"找不到 Copilot CLI，請更新或修復 AI Usage。");
		}
		catch (CopilotAccountLoginException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new CopilotAccountLoginException(
				CopilotAccountLoginFailureKind.RuntimeUnavailable,
				"無法確認 Copilot CLI，請更新或修復 AI Usage。",
				innerException: exception);
		}

		ICopilotLoginProcess process;
		try
		{
			process = _processStarter(CreateLoginStartInfo(
				executablePath,
				bootstrapDirectory));
		}
		catch (LateInteractiveLoginStartException)
		{
			throw;
		}
		catch (CopilotAccountLoginException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new CopilotAccountLoginException(
				CopilotAccountLoginFailureKind.ProcessFailed,
				"無法開啟 Copilot 登入，請更新或修復 AI Usage。",
				innerException: exception);
		}

		bool ownsProcess = true;
		try
		{
			try
			{
				await process.WaitForExitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				if (await TryTerminateProcessAsync(process))
				{
					throw CreateCancellationException();
				}

				ownsProcess = false;
				throw new LateInteractiveLoginException(
					process,
					WaitForPositiveProcessExitAsync(process),
					new CopilotAccountLoginException(
						CopilotAccountLoginFailureKind.ProcessFailed,
						"無法確認 Copilot 登入已安全關閉；完成清理前不會啟動其他 Copilot 登入。"));
			}
			catch (Exception exception)
			{
				CopilotAccountLoginException processFailure = new(
					CopilotAccountLoginFailureKind.ProcessFailed,
					"無法確認 Copilot 登入程序狀態；完成清理前不會啟動其他 Copilot 登入。",
					innerException: exception);
				if (await TryTerminateProcessAsync(process))
				{
					throw processFailure;
				}

				ownsProcess = false;
				throw new LateInteractiveLoginException(
					process,
					WaitForPositiveProcessExitAsync(process),
					processFailure);
			}

			if (process.ExitCode != 0)
			{
				throw new CopilotAccountLoginException(
					CopilotAccountLoginFailureKind.AuthenticationFailed,
					$"Copilot 登入未完成（exit code: {process.ExitCode}），請再試一次。",
					process.ExitCode);
			}
		}
		finally
		{
			if (ownsProcess)
			{
				process.Dispose();
			}
		}
	}

	private static async Task EnsureTokenWasNotPersistedAsync(
		string bootstrapDirectory,
		string accessToken,
		CancellationToken cancellationToken)
	{
		if (accessToken.Length > MaximumBootstrapCredentialCharacters)
		{
			throw new IOException(
				"Copilot bootstrap credential 超過安全驗證大小。");
		}

		byte[] tokenBytes = Encoding.UTF8.GetBytes(accessToken);
		byte[] scanBuffer = new byte[checked(
			BootstrapFileScanBufferBytes + Math.Max(tokenBytes.Length - 1, 0))];
		try
		{
			BootstrapTree tree = InspectBootstrapTree(bootstrapDirectory);
			long scannedBytes = 0;
			foreach (FileInfo file in tree.Files)
			{
				(bool containsToken, long fileBytes) =
					await BootstrapFileContainsTokenAsync(
						file,
						tokenBytes,
						scanBuffer,
						MaximumBootstrapTotalBytes - scannedBytes,
						cancellationToken).ConfigureAwait(false);
				scannedBytes += fileBytes;
				if (containsToken)
				{
					throw new CopilotAccountLoginException(
						CopilotAccountLoginFailureKind.AuthenticationFailed,
						"Copilot CLI 未使用受保護的 credential store，已取消連接。");
				}
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(scanBuffer);
			CryptographicOperations.ZeroMemory(tokenBytes);
		}
	}

	private static async Task<(bool ContainsToken, long FileBytes)>
		BootstrapFileContainsTokenAsync(
		FileInfo file,
		byte[] tokenBytes,
		byte[] scanBuffer,
		long remainingTotalBytes,
		CancellationToken cancellationToken)
	{
		file.Refresh();
		if (!file.Exists ||
			((file.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			throw new IOException(
				"Copilot bootstrap 檔案無法安全驗證。");
		}

		using FileStream stream = new(
			file.FullName,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			BootstrapFileScanBufferBytes,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		long fileLength = stream.Length;
		if ((fileLength > MaximumBootstrapFileBytes) ||
			(fileLength > remainingTotalBytes))
		{
			throw new IOException(
				"Copilot bootstrap 內容超過安全驗證大小。");
		}

		long remainingBytes = fileLength;
		int overlapLength = Math.Max(tokenBytes.Length - 1, 0);
		int preservedBytes = 0;
		while (remainingBytes > 0)
		{
			int requestedBytes = (int)Math.Min(
				BootstrapFileScanBufferBytes,
				remainingBytes);
			int readBytes = await stream.ReadAsync(
				scanBuffer.AsMemory(preservedBytes, requestedBytes),
				cancellationToken).ConfigureAwait(false);
			if (readBytes == 0)
			{
				throw new IOException(
					"Copilot bootstrap 檔案在安全驗證期間發生變更。");
			}

			int availableBytes = preservedBytes + readBytes;
			if (scanBuffer.AsSpan(0, availableBytes).IndexOf(tokenBytes) >= 0)
			{
				return (true, fileLength);
			}

			remainingBytes -= readBytes;
			preservedBytes = Math.Min(overlapLength, availableBytes);
			if (preservedBytes > 0)
			{
				scanBuffer.AsSpan(
					availableBytes - preservedBytes,
					preservedBytes).CopyTo(scanBuffer);
			}
		}

		return (false, fileLength);
	}

	private string ResolveHomeDirectory(Guid accountId)
	{
		try
		{
			return Path.GetFullPath(_homeDirectoryResolver(accountId));
		}
		catch (Exception exception) when (
			exception is ArgumentException or
			IOException or
			NotSupportedException or
			UnauthorizedAccessException)
		{
			throw new CopilotAccountLoginException(
				CopilotAccountLoginFailureKind.RuntimeUnavailable,
				"無法確認 Copilot 登入資料夾，請確認 AI Usage 有寫入權限。",
				innerException: exception);
		}
	}

	private static string ResolveBootstrapRoot(string activeHomeDirectory)
	{
		string? accountDirectory = Path.GetDirectoryName(activeHomeDirectory);
		if (string.IsNullOrWhiteSpace(accountDirectory))
		{
			throw new InvalidOperationException("Copilot account 目錄無效。");
		}

		return Path.Combine(accountDirectory, "bootstrap");
	}

	private static string ResolveGlobalLockPath(string activeHomeDirectory)
	{
		string? accountDirectory = Path.GetDirectoryName(activeHomeDirectory);
		string? copilotDirectory = accountDirectory is null
			? null
			: Path.GetDirectoryName(accountDirectory);
		if (string.IsNullOrWhiteSpace(copilotDirectory))
		{
			throw new InvalidOperationException("Copilot data 目錄無效。");
		}

		return Path.Combine(copilotDirectory, "interactive-login-v1.lock");
	}

	private static void DeleteBootstrapDirectory(
		string bootstrapDirectory,
		string bootstrapRoot)
	{
		string normalizedDirectory = Path.GetFullPath(bootstrapDirectory);
		string normalizedRoot = Path.GetFullPath(bootstrapRoot).TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		if (!Directory.Exists(normalizedDirectory))
		{
			return;
		}

		DirectoryInfo root = new(normalizedRoot);
		DirectoryInfo attempt = new(normalizedDirectory);
		if (!root.Exists ||
			((root.Attributes & FileAttributes.ReparsePoint) != 0) ||
			!string.Equals(
				attempt.Parent?.FullName,
				root.FullName,
				StringComparison.OrdinalIgnoreCase) ||
			!Guid.TryParseExact(attempt.Name, "N", out _) ||
			!IsOrdinaryDirectoryChain(root.FullName, attempt.FullName))
		{
			throw new IOException(
				"Copilot bootstrap attempt 路徑無法安全清理。");
		}

		BootstrapTree tree = InspectBootstrapTree(normalizedDirectory);
		foreach (DirectoryInfo link in tree.InternetCacheLinks)
		{
			DeleteInternetCacheLink(normalizedDirectory, link);
		}

		foreach (FileInfo file in tree.Files)
		{
			DeleteBootstrapFile(normalizedDirectory, file);
		}

		foreach (DirectoryInfo childDirectory in tree.Directories
			.OrderByDescending(value => value.FullName.Length))
		{
			DeleteOrdinaryBootstrapDirectory(
				normalizedDirectory,
				childDirectory);
		}

		DeleteOrdinaryBootstrapDirectory(normalizedDirectory, attempt);
	}

	private static BootstrapTree InspectBootstrapTree(string directoryPath)
	{
		DirectoryInfo root = new(Path.GetFullPath(directoryPath));
		if (!root.Exists)
		{
			return new BootstrapTree([], [], []);
		}

		if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException("Copilot bootstrap 目錄不可為 reparse point。");
		}

		List<FileInfo> files = [];
		List<DirectoryInfo> directories = [];
		List<DirectoryInfo> internetCacheLinks = [];
		Stack<DirectoryInfo> pending = new();
		pending.Push(root);
		long totalBytes = 0;
		int entryCount = 0;

		while (pending.Count > 0)
		{
			DirectoryInfo current = pending.Pop();
			foreach (FileSystemInfo entry in current.EnumerateFileSystemInfos(
				"*",
				SearchOption.TopDirectoryOnly))
			{
				entryCount++;
				if (entryCount > MaximumBootstrapTreeEntryCount)
				{
					throw new IOException(
						"Copilot bootstrap 內容無法安全驗證。");
				}

				if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					if ((entry is not DirectoryInfo link) ||
						!IsExpectedInternetCacheLink(root.FullName, link))
					{
						throw new IOException(
							"Copilot bootstrap 內容無法安全驗證。");
					}

					internetCacheLinks.Add(link);
					continue;
				}

				if (entry is DirectoryInfo childDirectory)
				{
					directories.Add(childDirectory);
					pending.Push(childDirectory);
					continue;
				}

				if (entry is not FileInfo file)
				{
					throw new IOException(
						"Copilot bootstrap 內容類型無效。");
				}

				if (file.Length > MaximumBootstrapFileBytes)
				{
					throw new IOException(
						"Copilot bootstrap 檔案超過安全驗證大小。");
				}

				totalBytes = checked(totalBytes + file.Length);
				if (totalBytes > MaximumBootstrapTotalBytes)
				{
					throw new IOException(
						"Copilot bootstrap 內容超過安全驗證大小。");
				}

				files.Add(file);
			}
		}

		return new BootstrapTree(files, directories, internetCacheLinks);
	}

	private static bool IsExpectedInternetCacheLink(
		string bootstrapDirectory,
		DirectoryInfo link)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		string normalizedBootstrapDirectory = Path.GetFullPath(bootstrapDirectory);
		string relativeLinkPath = Path.GetRelativePath(
			normalizedBootstrapDirectory,
			link.FullName);
		if (!string.Equals(
				relativeLinkPath,
				InternetCacheJunctionRelativePath,
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		FileSystemInfo? immediateTarget = link.ResolveLinkTarget(
			returnFinalTarget: false);
		FileSystemInfo? finalTarget = link.ResolveLinkTarget(
			returnFinalTarget: true);
		if ((immediateTarget is not DirectoryInfo immediateTargetDirectory) ||
			(finalTarget is not DirectoryInfo finalTargetDirectory))
		{
			return false;
		}

		string expectedTarget = Path.GetFullPath(Path.Combine(
			normalizedBootstrapDirectory,
			InternetCacheTargetRelativePath));
		return string.Equals(
				Path.GetFullPath(immediateTargetDirectory.FullName),
				expectedTarget,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(
				Path.GetFullPath(finalTargetDirectory.FullName),
				expectedTarget,
				StringComparison.OrdinalIgnoreCase) &&
			IsOrdinaryDirectoryChain(
				normalizedBootstrapDirectory,
				expectedTarget);
	}

	private static bool IsOrdinaryDirectoryChain(
		string rootDirectory,
		string leafDirectory)
	{
		string normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		string currentPath = Path.GetFullPath(leafDirectory);
		string normalizedRootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
		if (!string.Equals(
				currentPath,
				normalizedRoot,
				StringComparison.OrdinalIgnoreCase) &&
			!currentPath.StartsWith(
				normalizedRootPrefix,
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		while (true)
		{
			DirectoryInfo current = new(currentPath);
			current.Refresh();
			if (!current.Exists ||
				((current.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			if (string.Equals(
					current.FullName,
					normalizedRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			string? parentPath = Path.GetDirectoryName(current.FullName);
			if (string.IsNullOrWhiteSpace(parentPath))
			{
				return false;
			}

			currentPath = parentPath;
		}
	}

	private static void DeleteBootstrapFile(
		string bootstrapDirectory,
		FileInfo file)
	{
		file.Refresh();
		if (!file.Exists)
		{
			return;
		}

		FileAttributes attributes = file.Attributes;
		string? parentPath = Path.GetDirectoryName(file.FullName);
		if (string.IsNullOrWhiteSpace(parentPath) ||
			!IsOrdinaryDirectoryChain(bootstrapDirectory, parentPath) ||
			((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			throw new IOException(
				"Copilot bootstrap 檔案無法安全清理。");
		}

		if ((attributes & FileAttributes.ReadOnly) != 0)
		{
			File.SetAttributes(
				file.FullName,
				attributes & ~FileAttributes.ReadOnly);
		}

		File.Delete(file.FullName);
	}

	private static void DeleteInternetCacheLink(
		string bootstrapDirectory,
		DirectoryInfo link)
	{
		link.Refresh();
		if (!link.Exists)
		{
			return;
		}

		if (((link.Attributes & FileAttributes.ReparsePoint) == 0) ||
			!IsExpectedInternetCacheLink(bootstrapDirectory, link))
		{
			throw new IOException(
				"Copilot bootstrap internet cache link 無法安全清理。");
		}

		Directory.Delete(link.FullName, recursive: false);
	}

	private static void DeleteOrdinaryBootstrapDirectory(
		string bootstrapDirectory,
		DirectoryInfo directory)
	{
		directory.Refresh();
		if (!directory.Exists)
		{
			return;
		}

		FileAttributes attributes = directory.Attributes;
		if (!IsOrdinaryDirectoryChain(
				bootstrapDirectory,
				directory.FullName) ||
			((attributes & FileAttributes.ReparsePoint) != 0))
		{
			throw new IOException(
				"Copilot bootstrap 目錄無法安全清理。");
		}

		if ((attributes & FileAttributes.ReadOnly) != 0)
		{
			File.SetAttributes(
				directory.FullName,
				attributes & ~FileAttributes.ReadOnly);
		}

		Directory.Delete(directory.FullName, recursive: false);
	}

	private static async Task DeleteBootstrapDirectoryWithRetryAsync(
		string bootstrapDirectory,
		string bootstrapRoot,
		CancellationToken cancellationToken = default)
	{
		Exception? lastFailure = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				DeleteBootstrapDirectory(bootstrapDirectory, bootstrapRoot);
				return;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				lastFailure = exception;
				if (attempt < 2)
				{
					await Task.Delay(
						TimeSpan.FromMilliseconds(100),
						cancellationToken);
				}
			}
		}

		throw new IOException(
			"無法清理 Copilot disposable login context。",
			lastFailure);
	}

	private static async Task SweepOrphanedBootstrapContextsAsync(
		string bootstrapRoot,
		CancellationToken cancellationToken)
	{
		DirectoryInfo root = new(Path.GetFullPath(bootstrapRoot));
		if (!root.Exists)
		{
			return;
		}

		if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException(
				"Copilot bootstrap root 不可為 reparse point。");
		}

		FileSystemInfo[] entries = root.EnumerateFileSystemInfos(
			"*",
			SearchOption.TopDirectoryOnly)
			.Take(MaximumBootstrapRootEntryCount + 1)
			.ToArray();
		if (entries.Length > MaximumBootstrapRootEntryCount)
		{
			throw new IOException(
				"Copilot bootstrap root 超過安全清理數量。");
		}

		foreach (FileSystemInfo entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ((entry is not DirectoryInfo directory) ||
				((entry.Attributes & FileAttributes.ReparsePoint) != 0) ||
				!Guid.TryParseExact(entry.Name, "N", out _))
			{
				throw new IOException(
					"Copilot bootstrap root 含有無法安全辨識的內容。");
			}

			await DeleteBootstrapDirectoryWithRetryAsync(
				directory.FullName,
				root.FullName,
				cancellationToken);
		}
	}

	private static ICopilotLoginProcess StartProcess(ProcessStartInfo startInfo)
	{
		return StartContainedInteractiveProcess(
			startInfo,
			WindowsJobContainedProcess.StartInteractive);
	}

	internal static ICopilotLoginProcess StartContainedInteractiveProcess(
		ProcessStartInfo startInfo,
		Func<
			ProcessStartInfo,
			Action,
			WindowsJobContainedProcess> containedProcessStarter)
	{
		ArgumentNullException.ThrowIfNull(containedProcessStarter);
		try
		{
			return new LoginProcess(
				containedProcessStarter(
					startInfo,
					ThrowIfInteractiveLoginBlocked));
		}
		catch (WindowsJobContainedProcessLaunchException exception)
		{
			if (!exception.IsTreeEmptyConfirmed)
			{
				BlockInteractiveLoginUntilRestart();
				throw new LateInteractiveLoginStartException(
					exception.ContainmentCompletion,
					CreateInteractiveLoginBlockedException());
			}

			if (exception.IsLaunchBlocked)
			{
				BlockInteractiveLoginUntilRestart();
				throw CreateInteractiveLoginBlockedException();
			}

			throw new InvalidOperationException(
				"GitHub Copilot CLI process did not start in a verified Job Object.",
				exception);
		}
	}

	private static void ThrowIfInteractiveLoginBlocked()
	{
		if (Volatile.Read(ref _lateCleanupFailureMessage) is not null)
		{
			throw CreateInteractiveLoginBlockedException();
		}
	}

	private static void BlockInteractiveLoginUntilRestart()
	{
		Volatile.Write(
			ref _lateCleanupFailureMessage,
			InteractiveLoginRestartMessage);
	}

	private static CopilotAccountLoginException
		CreateInteractiveLoginBlockedException()
	{
		return new CopilotAccountLoginException(
			CopilotAccountLoginFailureKind.ProcessFailed,
			InteractiveLoginRestartMessage);
	}

	private async Task<bool> TryTerminateProcessAsync(
		ICopilotLoginProcess process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.KillEntireProcessTree();
			}
		}
		catch
		{
		}

		try
		{
			await process.WaitForExitAsync(CancellationToken.None)
				.WaitAsync(_processTerminationTimeout);
		}
		catch
		{
		}

		try
		{
			return process.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private async Task WaitForPositiveProcessExitAsync(
		ICopilotLoginProcess process)
	{
		while (true)
		{
			try
			{
				if (process.HasExited)
				{
					return;
				}
			}
			catch
			{
			}

			try
			{
				await process.WaitForExitAsync(CancellationToken.None)
					.WaitAsync(_processTerminationTimeout);
			}
			catch
			{
			}

			await Task.Delay(LockRetryDelay);
		}
	}

	private static async Task CompleteLateInteractiveLoginCleanupAsync(
		ICopilotLoginProcess process,
		Task exitCompletion,
		string bootstrapDirectory,
		string bootstrapRoot)
	{
		await exitCompletion.ConfigureAwait(false);

		try
		{
			process.Dispose();
		}
		catch
		{
		}

		await DeleteBootstrapDirectoryUntilCleanAsync(
			bootstrapDirectory,
			bootstrapRoot);
	}

	private static async Task CompleteLateInteractiveLoginStartCleanupAsync(
		Task containmentCompletion,
		string bootstrapDirectory,
		string bootstrapRoot)
	{
		await containmentCompletion.ConfigureAwait(false);
		await DeleteBootstrapDirectoryUntilCleanAsync(
			bootstrapDirectory,
			bootstrapRoot);
	}

	private static async Task CompleteLateBootstrapSdkCleanupAsync(
		Task sdkLifecycleCompletion,
		string bootstrapDirectory,
		string bootstrapRoot,
		string? accessToken)
	{
		await sdkLifecycleCompletion.ConfigureAwait(false);

		if (!string.IsNullOrWhiteSpace(accessToken))
		{
			try
			{
				await EnsureTokenWasNotPersistedAsync(
					bootstrapDirectory,
					accessToken,
					CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				// 連接已失敗；仍須先清除 disposable context 才能釋放登入鎖。
			}
		}

		await DeleteBootstrapDirectoryUntilCleanAsync(
			bootstrapDirectory,
			bootstrapRoot);
	}

	private static async Task DeleteBootstrapDirectoryUntilCleanAsync(
		string bootstrapDirectory,
		string bootstrapRoot)
	{
		TimeSpan retryDelay = LockRetryDelay;

		while (true)
		{
			try
			{
				DeleteBootstrapDirectory(
					bootstrapDirectory,
					bootstrapRoot);
				return;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(retryDelay);
				retryDelay = TimeSpan.FromMilliseconds(Math.Min(
					retryDelay.TotalMilliseconds * 2,
					TimeSpan.FromSeconds(15).TotalMilliseconds));
			}
		}
	}

	private static bool TryNormalizeAccount(
		string? host,
		string? login,
		out string normalizedHost,
		out string normalizedLogin)
	{
		normalizedLogin = string.Empty;
		if (!CopilotAccountIdentityRules.TryNormalizeHost(
				host,
				out normalizedHost) ||
			string.IsNullOrWhiteSpace(login))
		{
			return false;
		}

		string candidate = login.Trim();
		if (candidate.Any(character =>
			char.IsControl(character) || char.IsWhiteSpace(character)))
		{
			return false;
		}

		normalizedLogin = candidate.ToLowerInvariant();
		return true;
	}

	private static CopilotAccountLoginException CreateCancellationException()
	{
		return new CopilotAccountLoginException(
			CopilotAccountLoginFailureKind.Cancelled,
			"已取消 Copilot 登入。");
	}

	private static bool TryResolveCandidate(
		string? candidate,
		Func<string, bool> fileExists,
		out string resolvedPath)
	{
		resolvedPath = string.Empty;
		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				candidate,
				out string normalizedPath) ||
			!string.Equals(
				Path.GetExtension(normalizedPath),
				".exe",
				StringComparison.OrdinalIgnoreCase) ||
			!fileExists(normalizedPath))
		{
			return false;
		}

		resolvedPath = normalizedPath;
		return true;
	}
}

#pragma warning restore GHCP001
