using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed class CodexAppServerUsagePoller : ICodexUsagePoller
{
	private sealed record AccountResult(
		string PlanType,
		string AccountIdentity);

	private sealed class BoundedUtf8LineReader
	{
		private readonly byte[] _buffer = new byte[8192];
		private readonly int _maximumBytes;
		private readonly Stream _stream;
		private int _bufferLength;
		private int _bufferPosition;

		internal BoundedUtf8LineReader(Stream stream, int maximumBytes)
		{
			_stream = stream ?? throw new ArgumentNullException(nameof(stream));
			_maximumBytes = maximumBytes > 0
				? maximumBytes
				: throw new ArgumentOutOfRangeException(nameof(maximumBytes));
		}

		internal async ValueTask<string?> ReadLineAsync(
			CancellationToken cancellationToken)
		{
			using MemoryStream content = new(Math.Min(_maximumBytes, _buffer.Length));

			while (true)
			{
				if (_bufferPosition >= _bufferLength)
				{
					_bufferLength = await _stream.ReadAsync(_buffer, cancellationToken);
					_bufferPosition = 0;

					if (_bufferLength == 0)
					{
						return content.Length == 0 ? null : Decode(content);
					}
				}

				int newlineIndex = Array.IndexOf(
					_buffer,
					(byte)'\n',
					_bufferPosition,
					_bufferLength - _bufferPosition);
				int bytesToCopy = newlineIndex >= 0
					? newlineIndex - _bufferPosition
					: _bufferLength - _bufferPosition;
				Append(content, _buffer.AsSpan(_bufferPosition, bytesToCopy));
				_bufferPosition += bytesToCopy;

				if (newlineIndex >= 0)
				{
					_bufferPosition++;
					return Decode(content);
				}
			}
		}

		private void Append(MemoryStream content, ReadOnlySpan<byte> bytes)
		{
			if ((content.Length + bytes.Length) > _maximumBytes)
			{
				throw new InvalidDataException("Codex app-server 回應超過允許大小。");
			}

			content.Write(bytes);
		}

		private static string Decode(MemoryStream content)
		{
			int length = checked((int)content.Length);
			byte[] buffer = content.GetBuffer();

			if ((length > 0) && (buffer[length - 1] == (byte)'\r'))
			{
				length--;
			}

			return StrictUtf8Encoding.GetString(buffer, 0, length);
		}
	}

	private sealed record LaunchContext(
		ProcessStartInfo StartInfo,
		CliVersionEvidence? VersionEvidence,
		WindowsOfficialCliExecutableLease? ExecutableLease);

	internal sealed class ProcessTransport : ICodexAppServerTransport
	{
		private readonly Action<Process> _closeStandardInput;
		private readonly ProviderProcessOperationTracker _operationTracker;
		private readonly Process _process;
		private readonly Task<string> _standardErrorTask;
		private readonly BoundedUtf8LineReader _standardOutputReader;
		private bool _isDisposed;

		public ProcessStartInfo StartInfo => _process.StartInfo;

		internal ProcessTransport(
			ProcessStartInfo startInfo,
			ProviderProcessOperationTracker operationTracker)
			: this(
				startInfo,
				operationTracker,
				process => TerminateAndDisposeProcessAsync(
					process,
					operationTracker))
		{
		}

		internal ProcessTransport(
			ProcessStartInfo startInfo,
			ProviderProcessOperationTracker operationTracker,
			Func<Process, Task> failedStartCleanup,
			Action<Process>? closeStandardInput = null,
			CodexVersionProbeContainmentState? containmentState = null)
		{
			ArgumentNullException.ThrowIfNull(startInfo);
			_operationTracker = operationTracker ??
				throw new ArgumentNullException(nameof(operationTracker));
			ArgumentNullException.ThrowIfNull(failedStartCleanup);
			_closeStandardInput = closeStandardInput ??
				(static process => process.StandardInput.Close());
			CodexVersionProbeContainmentState effectiveContainmentState =
				containmentState ?? CodexVersionProbeContainmentState.Shared;
			_process = new Process
			{
				StartInfo = startInfo
			};
			bool processStarted = false;

			try
			{
				using IDisposable transition =
					effectiveContainmentState.EnterLaunchTransition();
				effectiveContainmentState.ThrowIfCompromised();

				if (!_process.Start())
				{
					throw new IOException("無法啟動 Codex app-server。");
				}

				processStarted = true;

				_standardErrorTask = ReadBoundedStreamAsync(
					_process.StandardError.BaseStream,
					MaximumStandardErrorBytes,
					() => TryKillProcess(_process, _operationTracker),
					CancellationToken.None);
				_standardOutputReader = new BoundedUtf8LineReader(
					_process.StandardOutput.BaseStream,
					MaximumMessageBytes);
			}
			catch
			{
				if (processStarted)
				{
					try
					{
						Task cleanupTask = failedStartCleanup(_process);
						_operationTracker.Track(cleanupTask);
						ObserveFault(cleanupTask);
					}
					catch
					{
						_process.Dispose();
					}
				}
				else
				{
					_process.Dispose();
				}

				throw;
			}
		}

		public void Abort()
		{
			TryKillProcess(_process, _operationTracker);
		}

		public async ValueTask<string?> ReadLineAsync(
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();

			try
			{
				return await _standardOutputReader.ReadLineAsync(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				Abort();
				throw;
			}
		}

		public async ValueTask WriteLineAsync(
			string message,
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(message);
			await _process.StandardInput.WriteLineAsync(
				message.AsMemory(),
				cancellationToken);
			await _process.StandardInput.FlushAsync(cancellationToken);
		}

		public async ValueTask DisposeAsync()
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;

			try
			{
				_closeStandardInput(_process);
			}
			catch
			{
				// Pipe close 失敗不能跳過 terminate／wait／dispose。
			}

			Task outputDrainTask = DrainStandardOutputAsync(
				_standardOutputReader,
				_process,
				_operationTracker);
			Task processExitTask =
				ProviderProcessExecution.WaitForConfirmedExitAsync(
					_process,
					_operationTracker);
			Task errorObservationTask = ObserveTaskAsync(_standardErrorTask);
			Task cleanupTask = Task.WhenAll(
				outputDrainTask,
				processExitTask,
				errorObservationTask);

			await CompleteBoundedCleanupAsync(
				cleanupTask,
				() => TryKillProcess(_process, _operationTracker),
				_process,
				_operationTracker,
				ProcessCleanupTimeout);
		}

		internal static async Task CompleteBoundedCleanupAsync(
			Task cleanupTask,
			Action terminateProcess,
			IDisposable processResource,
			ProviderProcessOperationTracker operationTracker,
			TimeSpan cleanupTimeout)
		{
			ArgumentNullException.ThrowIfNull(cleanupTask);
			ArgumentNullException.ThrowIfNull(terminateProcess);
			ArgumentNullException.ThrowIfNull(processResource);
			ArgumentNullException.ThrowIfNull(operationTracker);

			if (cleanupTimeout <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
			}

			bool cleanupContinuesInBackground = false;

			try
			{
				await cleanupTask.WaitAsync(cleanupTimeout);
			}
			catch (TimeoutException)
			{
				try
				{
					terminateProcess();
				}
				catch
				{
					operationTracker.MarkContainmentCompromised();
				}

				try
				{
					await cleanupTask.WaitAsync(cleanupTimeout);
				}
				catch (TimeoutException)
				{
					operationTracker.MarkContainmentCompromised();
					Task deferredCleanupTask = DisposeAfterCleanupAsync(
						cleanupTask,
						processResource);
					operationTracker.Track(deferredCleanupTask);
					ObserveFault(deferredCleanupTask);
					cleanupContinuesInBackground = true;
				}
			}
			catch
			{
				// Cleanup is best effort and must not replace the polling result.
			}
			finally
			{
				if (!cleanupContinuesInBackground)
				{
					processResource.Dispose();
				}
			}
		}

		private static async Task DisposeAfterCleanupAsync(
			Task cleanupTask,
			IDisposable processResource)
		{
			try
			{
				await cleanupTask.ConfigureAwait(false);
			}
			finally
			{
				processResource.Dispose();
			}
		}

		private static async Task TerminateAndDisposeProcessAsync(
			Process process,
			ProviderProcessOperationTracker operationTracker)
		{
			try
			{
				TryKillProcess(process, operationTracker);
				await ProviderProcessExecution.WaitForConfirmedExitAsync(
					process,
					operationTracker)
					.ConfigureAwait(false);
			}
			finally
			{
				process.Dispose();
			}
		}

		private void ThrowIfDisposed()
		{
			if (_isDisposed)
			{
				throw new ObjectDisposedException(nameof(ProcessTransport));
			}
		}
	}

	private const int AccountReadRequestId = 2;
	private const int ConfigReadRequestId = 6;
	private const int InitializeRequestId = 1;
	private const long JsonRpcInternalErrorCode = -32603;
	private const long JsonRpcInvalidParamsCode = -32602;
	private const long JsonRpcInvalidRequestCode = -32600;
	private const long JsonRpcMethodNotFoundCode = -32601;
	private const long JsonRpcParseErrorCode = -32700;
	private const long JsonRpcServerOverloadedCode = -32001;
	private const int MaximumAccountIdentityLength = 320;
	private const int MaximumMetricCount = 64;
	private const int MaximumMessageBytes = 1024 * 1024;
	private const int MaximumRateLimitBucketCount = 32;
	private const int MaximumResetCreditDetailCount = 64;
	private const int MaximumResetCreditDescriptionLength = 512;
	private const int MaximumResetCreditTitleLength = 200;
	private const int MaximumResetCreditTypeLength = 128;
	// 184 leaves room for "codex:" + ":secondary"; 173 leaves room for " · " plus
	// the longest Int64 minute label. The provider rechecks the composed 200-char fields.
	private const int MaximumRateLimitIdLength = 184;
	private const int MaximumRateLimitNameLength = 173;
	private const int MaximumStandardErrorBytes = 64 * 1024;
	private const int MaximumStandardOutputCharacters = 2 * 1024 * 1024;
	private const string ExpectedSignerCommonName = "OpenAI OpCo, LLC";
	private const int RateLimitsReadRequestId = 3;
	private const int RefreshAccountReadRequestId = 4;
	private const int RetryRateLimitsReadRequestId = 5;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly string[] ScrubbedEnvironmentVariables =
	{
		"CODEX_ACCESS_TOKEN",
		"CODEX_API_KEY",
		"OPENAI_API_KEY"
	};
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);
	private readonly Func<Guid, string> _homeDirectoryResolver;
	private readonly ICodexWorkspaceBindingStore? _accountBindingStore;
	private readonly CodexBindingCommitGate? _bindingCommitGate;
	private readonly Func<CodexCliExecutableResolution?> _executableResolver;
	private readonly CodexAccountOperationGate _operationGate;
	private readonly TimeProvider _timeProvider;
	private readonly Func<
		ProcessStartInfo,
		ProviderProcessOperationTracker,
		ICodexAppServerTransport> _transportFactory;
	private readonly TimeSpan _commandTimeout;

	public CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		ICodexWorkspaceBindingStore accountBindingStore,
		CodexBindingCommitGate bindingCommitGate)
		: this(
			homeDirectoryResolver,
			accountBindingStore,
			bindingCommitGate,
			CodexAccountOperationGate.Shared)
	{
	}

	public CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		ICodexWorkspaceBindingStore accountBindingStore,
		CodexBindingCommitGate bindingCommitGate,
		CodexAccountOperationGate operationGate)
		: this(
			homeDirectoryResolver,
			ResolveExecutable,
			(startInfo, operationTracker) =>
				new ProcessTransport(startInfo, operationTracker),
			TimeProvider.System,
			operationGate,
			CommandTimeout,
			accountBindingStore ??
				throw new ArgumentNullException(nameof(accountBindingStore)),
			bindingCommitGate ??
				throw new ArgumentNullException(nameof(bindingCommitGate)))
	{
	}

	internal CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider)
		: this(
			homeDirectoryResolver,
			WrapExecutableResolver(executableResolver),
			WrapTransportFactory(transportFactory),
			timeProvider,
			new CodexAccountOperationGate(),
			CommandTimeout,
			accountBindingStore: null,
			bindingCommitGate: null)
	{
	}

	internal CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider,
		CodexAccountOperationGate operationGate,
		ICodexWorkspaceBindingStore accountBindingStore,
		CodexBindingCommitGate bindingCommitGate)
		: this(
			homeDirectoryResolver,
			WrapExecutableResolver(executableResolver),
			WrapTransportFactory(transportFactory),
			timeProvider,
			operationGate,
			CommandTimeout,
			accountBindingStore ??
				throw new ArgumentNullException(nameof(accountBindingStore)),
			bindingCommitGate ??
				throw new ArgumentNullException(nameof(bindingCommitGate)))
	{
	}

	internal CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider,
		CodexAccountOperationGate operationGate)
		: this(
			homeDirectoryResolver,
			WrapExecutableResolver(executableResolver),
			WrapTransportFactory(transportFactory),
			timeProvider,
			operationGate,
			CommandTimeout,
			accountBindingStore: null,
			bindingCommitGate: null)
	{
	}

	internal CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider,
		CodexAccountOperationGate operationGate,
		TimeSpan commandTimeout)
		: this(
			homeDirectoryResolver,
			WrapExecutableResolver(executableResolver),
			WrapTransportFactory(transportFactory),
			timeProvider,
			operationGate,
			commandTimeout,
			accountBindingStore: null,
			bindingCommitGate: null)
	{
	}

	internal CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		CodexCliExecutableResolution executableResolution,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider)
		: this(
			homeDirectoryResolver,
			() => executableResolution,
			WrapTransportFactory(transportFactory),
			timeProvider,
			new CodexAccountOperationGate(),
			CommandTimeout,
			accountBindingStore: null,
			bindingCommitGate: null)
	{
		ArgumentNullException.ThrowIfNull(executableResolution);
	}

	private CodexAppServerUsagePoller(
		Func<Guid, string> homeDirectoryResolver,
		Func<CodexCliExecutableResolution?> executableResolver,
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker,
			ICodexAppServerTransport> transportFactory,
		TimeProvider timeProvider,
		CodexAccountOperationGate operationGate,
		TimeSpan commandTimeout,
		ICodexWorkspaceBindingStore? accountBindingStore,
		CodexBindingCommitGate? bindingCommitGate)
	{
		_homeDirectoryResolver = homeDirectoryResolver ??
			throw new ArgumentNullException(nameof(homeDirectoryResolver));
		_accountBindingStore = accountBindingStore;
		_bindingCommitGate = bindingCommitGate;
		_executableResolver = executableResolver ??
			throw new ArgumentNullException(nameof(executableResolver));
		_operationGate = operationGate ??
			throw new ArgumentNullException(nameof(operationGate));
		_transportFactory = transportFactory ??
			throw new ArgumentNullException(nameof(transportFactory));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_commandTimeout = commandTimeout > TimeSpan.Zero
			? commandTimeout
			: throw new ArgumentOutOfRangeException(nameof(commandTimeout));
	}

	public async Task<CodexUsagePollResult> PollBoundAsync(
		Guid accountId,
		string expectedPublicBindingIdentity,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);

		if (!CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				expectedPublicBindingIdentity,
				out string? normalizedPublicBindingIdentity) ||
			(normalizedPublicBindingIdentity is null))
		{
			throw new ArgumentException(
				"Codex public binding identity 格式無效。",
				nameof(expectedPublicBindingIdentity));
		}

		if ((_accountBindingStore is null) || (_bindingCommitGate is null))
		{
			throw new InvalidOperationException(
				"Codex bound poll 缺少 private binding store 或 commit gate。");
		}

		cancellationToken.ThrowIfCancellationRequested();
		using CancellationTokenSource gateTimeoutSource = new(_commandTimeout);
		using CancellationTokenSource gateLinkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				gateTimeoutSource.Token);

		try
		{
			using IDisposable bindingLease =
				await _bindingCommitGate.EnterAsync(gateLinkedSource.Token);
			CodexWorkspaceBinding? binding;
			try
			{
				binding = await _accountBindingStore.LoadAsync(
					accountId,
					gateLinkedSource.Token).ConfigureAwait(false);
			}
			catch (InvalidDataException)
			{
				throw new CodexWorkspaceBindingInvalidException();
			}
			IReadOnlyList<CodexWorkspaceBinding> allBindings =
				await _accountBindingStore.LoadAllAsync(
					gateLinkedSource.Token).ConfigureAwait(false);
			ValidateBoundBinding(
				accountId,
				normalizedPublicBindingIdentity,
				binding,
				allBindings);

			return await PollAsyncCore(
				accountId,
				binding,
				allBindings,
				normalizedPublicBindingIdentity,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			gateLinkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new TimeoutException(
				"等待 Codex workspace binding 作業逾時。");
		}
	}

	public Task<CodexUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return PollAsyncCore(
			accountId,
			expectedBinding: null,
			allBindings: null,
			publicBindingIdentity: null,
			cancellationToken);
	}

	private async Task<CodexUsagePollResult> PollAsyncCore(
		Guid accountId,
		CodexWorkspaceBinding? expectedBinding,
		IReadOnlyList<CodexWorkspaceBinding>? allBindings,
		string? publicBindingIdentity,
		CancellationToken cancellationToken)
	{
		ValidateAccountId(accountId);

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
		catch (OperationCanceledException) when (gateLinkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			_operationGate.ThrowIfContainmentCompromised(accountId);

			throw new TimeoutException("等待前一個 Codex 帳號作業逾時。");
		}

		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		using IDisposable operationLease =
			operationTracker.HoldLease(rawOperationLease);
		using CancellationTokenSource timeoutSource = new(_commandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		LaunchContext? launchContext = null;

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
			using IDisposable? executableLease =
				launchContext.ExecutableLease is null
					? null
					: operationTracker.HoldLease(launchContext.ExecutableLease);
			CodexUsagePollResult result =
				await ProviderProcessExecution.RunAsynchronousAsync(
				token => PollCoreAsync(
					launchContext.StartInfo,
					operationTracker,
					expectedBinding,
					allBindings,
					publicBindingIdentity,
					token),
				linkedSource.Token,
				operationTracker);

			if (operationTracker.IsContainmentCompromised)
			{
				throw new CodexCliContainmentException(
					CodexCliContainmentException.RestartRequiredMessage);
			}

			return result with
			{
				VersionEvidence = launchContext.VersionEvidence
			};
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			if (operationTracker.IsContainmentCompromised)
			{
				throw new CodexCliContainmentException(
					CodexCliContainmentException.RestartRequiredMessage);
			}

			throw new TimeoutException("Codex app-server 回應逾時。");
		}
		catch (Exception exception) when (
			operationTracker.IsContainmentCompromised)
		{
			throw new CodexCliContainmentException(
				CodexCliContainmentException.RestartRequiredMessage,
				exception);
		}
		catch (Exception exception) when (IsVersionSensitiveFailure(exception))
		{
			AttachVersionEvidence(exception, launchContext?.VersionEvidence);
			throw;
		}
	}

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateBoundBinding(
		Guid accountId,
		string expectedPublicBindingIdentity,
		CodexWorkspaceBinding? binding,
		IReadOnlyList<CodexWorkspaceBinding>? allBindings)
	{
		if (binding is null)
		{
			throw new CodexWorkspaceBindingMissingException();
		}

		if (!IsValidBinding(binding) ||
			(binding.AccountId != accountId) ||
			!string.Equals(
				expectedPublicBindingIdentity,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId),
				StringComparison.Ordinal) ||
			(allBindings is null) ||
			allBindings.Any(candidate => !IsValidBinding(candidate)))
		{
			throw new CodexWorkspaceBindingInvalidException();
		}
	}

	private static bool IsValidBinding(CodexWorkspaceBinding? binding)
	{
		return (binding is not null) &&
			(binding.AccountId != Guid.Empty) &&
			(binding.PublicBindingId != Guid.Empty) &&
			CodexWorkspaceBinding.IsCanonicalSalt(binding.SaltBase64) &&
			CodexWorkspaceBinding.IsCanonicalFingerprint(
				binding.AccountFingerprintSha256) &&
			CodexWorkspaceBinding.IsCanonicalFingerprint(
				binding.WorkspaceFingerprintSha256) &&
			CodexWorkspaceBinding.IsCanonicalFingerprint(
				binding.EntitlementFingerprintSha256);
	}

	private static void ValidateObservedBinding(
		AccountResult account,
		Guid workspaceId,
		CodexWorkspaceBinding? expectedBinding,
		IReadOnlyList<CodexWorkspaceBinding>? allBindings)
	{
		if (expectedBinding is null)
		{
			return;
		}

		if ((workspaceId == Guid.Empty) ||
			!expectedBinding.MatchesAccount(account.AccountIdentity) ||
			!expectedBinding.MatchesWorkspace(workspaceId) ||
			!expectedBinding.MatchesEntitlement(
				account.AccountIdentity,
				workspaceId) ||
			(allBindings is null) ||
			allBindings.Any(candidate =>
				(candidate.AccountId != expectedBinding.AccountId) &&
				candidate.MatchesEntitlement(
					account.AccountIdentity,
					workspaceId)))
		{
			throw new CodexWorkspaceMismatchException();
		}
	}

	internal static ProcessStartInfo CreateStartInfo(
		string executablePath,
		string homeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedExecutablePath) ||
			!Path.IsPathFullyQualified(homeDirectory))
		{
			throw new ArgumentException(
				"Codex executable 必須是本機完整路徑，home 必須是完整路徑。");
		}

		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = true,
			FileName = normalizedExecutablePath,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = StrictUtf8Encoding,
			StandardInputEncoding = StrictUtf8Encoding,
			StandardOutputEncoding = StrictUtf8Encoding,
			UseShellExecute = false,
			WorkingDirectory = homeDirectory
		};
		startInfo.ArgumentList.Add("app-server");
		startInfo.ArgumentList.Add("--listen");
		startInfo.ArgumentList.Add("stdio://");

		foreach (string variableName in ScrubbedEnvironmentVariables)
		{
			startInfo.Environment.Remove(variableName);
		}

		startInfo.Environment["CODEX_HOME"] = homeDirectory;
		startInfo.Environment["CODEX_SQLITE_HOME"] = homeDirectory;
		startInfo.Environment["NO_COLOR"] = "1";
		return startInfo;
	}

	internal static string? ResolveExecutablePath()
	{
		CodexCliExecutableResolution? resolution = ResolveExecutable();

		try
		{
			return resolution?.ExecutablePath;
		}
		finally
		{
			resolution?.ExecutableLease?.Dispose();
		}
	}

	internal static CodexCliExecutableResolution? ResolveExecutable()
	{
		CliVersionEvidence? versionEvidence = null;
		string? versionEvidenceExecutablePath = null;
		WindowsOfficialCliExecutableValidator validator = new(
			ExpectedSignerCommonName,
			executablePath =>
			{
				if (!CodexCliVersionProbe.TryProbeVersion(
						executablePath,
						out Version detectedVersion))
				{
					return false;
				}

				versionEvidence = CliVersionPolicies.AssessCodex(detectedVersion);
				versionEvidenceExecutablePath = executablePath;
				return true;
			},
			CodexCliExecutableStager.IsDefaultStagedPathAclSafe,
			CodexVersionProbeContainmentState.Shared.RetainExecutableLease);
		WindowsOfficialCliExecutableLease? executableLease = ResolveExecutable(
			GetExecutableCandidates(),
			validator,
			CodexCliExecutableStager.Shared,
			CodexVersionProbeContainmentState.Shared);

		if (executableLease is null)
		{
			return null;
		}

		string executablePath = executableLease.ExecutablePath;

		if ((versionEvidence is null) ||
			!string.Equals(
				executablePath,
				versionEvidenceExecutablePath,
				StringComparison.OrdinalIgnoreCase))
		{
			executableLease.Dispose();
			throw new CodexCliProbeException(
				"Codex CLI 版本檢查未提供可用的診斷結果。");
		}

		return new CodexCliExecutableResolution(
			executablePath,
			versionEvidence,
			executableLease);
	}

	internal static string? ResolveExecutablePath(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator)
	{
		return ResolveExecutablePath(candidates, validator, stager: null);
	}

	internal static string? ResolveExecutablePath(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator,
		ICodexCliExecutableStager? stager)
	{
		return ResolveExecutablePath(
			candidates,
			validator,
			stager,
			containmentState: null);
	}

	internal static string? ResolveExecutablePath(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator,
		ICodexCliExecutableStager? stager,
		CodexVersionProbeContainmentState? containmentState)
	{
		using WindowsOfficialCliExecutableLease? executableLease =
			ResolveExecutable(candidates, validator, stager, containmentState);
		return executableLease?.ExecutablePath;
	}

	private static WindowsOfficialCliExecutableLease? ResolveExecutable(
		IEnumerable<string> candidates,
		WindowsOfficialCliExecutableValidator validator,
		ICodexCliExecutableStager? stager,
		CodexVersionProbeContainmentState? containmentState = null)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(validator);
		ThrowIfCodexContainmentCompromised(containmentState);
		HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
		OfficialCliExecutableValidationException? lastContainmentFailure = null;
		Exception? lastValidationFailure = null;
		OfficialCliExecutableValidationException? lastVersionProbeFailure = null;

		foreach (string candidate in candidates)
		{
			ThrowIfCodexContainmentCompromised(containmentState);

			if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				candidate,
				out string fullPath))
			{
				continue;
			}

			if (!visitedPaths.Add(fullPath))
			{
				continue;
			}

			if (!File.Exists(fullPath))
			{
				continue;
			}

			try
			{
				WindowsOfficialCliExecutableLease executableLease;

				if (stager is null)
				{
					string validationPath =
						CodexOfficialExecutablePathResolver.Resolve(fullPath);
					executableLease =
						WindowsOfficialCliExecutableLease.CreateUnprotected(
							validator.Validate(validationPath));
				}
				else
				{
					ThrowIfCodexContainmentCompromised(containmentState);
					string sourcePath = CodexOfficialExecutablePathResolver
						.ResolveStagingSource(fullPath);
					ThrowIfCodexContainmentCompromised(containmentState);
					executableLease = stager.Stage(sourcePath);

					try
					{
						_ = validator.ValidateStaged(executableLease);
					}
					catch
					{
						executableLease.Dispose();
						throw;
					}
				}

				return executableLease;
			}
			catch (OfficialCliExecutableValidationException exception)
			{
				if (exception.FailureReason ==
					OfficialCliExecutableValidationFailureReason.VersionProbeContainmentFailed)
				{
					lastContainmentFailure = exception;
				}
				else if (exception.FailureReason ==
					OfficialCliExecutableValidationFailureReason.VersionProbeFailed)
				{
					lastVersionProbeFailure = exception;
				}
				else
				{
					lastValidationFailure = exception;
				}
			}
			catch (CodexCliUntrustedException exception)
			{
				lastValidationFailure = exception;
			}
		}

		if (lastContainmentFailure is not null)
		{
			throw new CodexCliContainmentException(
				"無法確認 Codex CLI 的版本檢查程序是否已完全結束；請重新啟動 AI Usage 後再試。",
				lastContainmentFailure);
		}

		if (lastVersionProbeFailure is not null)
		{
			throw new CodexCliProbeException(
				"暫時無法確認 Codex CLI 版本，請稍後再試。",
				lastVersionProbeFailure);
		}

		if (lastValidationFailure is not null)
		{
			throw new CodexCliUntrustedException(
				"Codex CLI 未通過官方簽章、路徑或版本驗證；請從 OpenAI 官方來源重新安裝或更新。",
				lastValidationFailure);
		}

		return null;
	}

	private static void ThrowIfCodexContainmentCompromised(
		CodexVersionProbeContainmentState? containmentState)
	{
		if (containmentState is null)
		{
			return;
		}

		try
		{
			containmentState.ThrowIfCompromised();
		}
		catch (CodexCliVersionProbeContainmentException exception)
		{
			throw new CodexCliContainmentException(
				"無法確認 Codex CLI 的版本檢查程序是否已完全結束；請重新啟動 AI Usage 後再試。",
				exception);
		}
	}

	private async Task<CodexUsagePollResult> PollCoreAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CodexWorkspaceBinding? expectedBinding,
		IReadOnlyList<CodexWorkspaceBinding>? allBindings,
		string? publicBindingIdentity,
		CancellationToken cancellationToken)
	{
		await using ICodexAppServerTransport transport = await CreateTransportAsync(
			_transportFactory,
			startInfo,
			cancellationToken,
			operationTracker);

		try
		{
			JsonElement initializeResult = await SendRequestAsync(
				transport,
				InitializeRequestId,
				CreateInitializeRequest(),
				"initialize",
				cancellationToken);
			EnsureObject(initializeResult, "Codex initialize result");
			await transport.WriteLineAsync(CreateInitializedNotification(), cancellationToken);
			Guid workspaceId;
			if (expectedBinding is null)
			{
				JsonElement? configuredWorkspace =
					await ReadConfiguredWorkspaceValueAsync(
						transport,
						ConfigReadRequestId,
						cancellationToken);
				if (configuredWorkspace is not null)
				{
					throw new CodexWorkspaceMismatchException();
				}

				workspaceId = Guid.Empty;
			}
			else
			{
				workspaceId = await ReadConfiguredWorkspaceIdAsync(
					transport,
					ConfigReadRequestId,
					cancellationToken);
			}
			JsonElement accountResultElement = await SendRequestAsync(
				transport,
				AccountReadRequestId,
				CreateAccountReadRequest(),
				"account/read",
				cancellationToken);
			AccountResult accountResult = ParseAccount(accountResultElement);
			ValidateObservedBinding(
				accountResult,
				workspaceId,
				expectedBinding,
				allBindings);
			JsonElement rateLimitsResult;

			try
			{
				rateLimitsResult = await SendRequestAsync(
					transport,
					RateLimitsReadRequestId,
					CreateRateLimitsReadRequest(),
					"account/rateLimits/read",
					cancellationToken);
			}
			catch (CodexAppServerFailureException exception) when (
				IsExpiredAccessTokenFailure(exception.ServerMessage))
			{
				accountResultElement = await SendRequestAsync(
					transport,
					RefreshAccountReadRequestId,
					CreateAccountReadRequest(
						RefreshAccountReadRequestId,
						refreshToken: true),
					"account/read",
					cancellationToken);
				accountResult = ParseAccount(accountResultElement);
				ValidateObservedBinding(
					accountResult,
					workspaceId,
					expectedBinding,
					allBindings);
				rateLimitsResult = await SendRequestAsync(
					transport,
					RetryRateLimitsReadRequestId,
					CreateRateLimitsReadRequest(RetryRateLimitsReadRequestId),
					"account/rateLimits/read",
					cancellationToken);
			}
			IReadOnlyList<CodexRateLimitBucket> rateLimits = ParseRateLimits(rateLimitsResult);
			DateTimeOffset observedAt = _timeProvider.GetUtcNow();
			(long? availableResetCredits, DateTimeOffset? nextResetCreditExpiresAt,
				CodexResetCreditDetails? resetCreditDetails) =
				ParseResetCredits(rateLimitsResult, observedAt);
			EnsureMetricCapacity(rateLimits, availableResetCredits);
			return new CodexUsagePollResult(
				accountResult.PlanType,
				rateLimits,
				availableResetCredits,
				observedAt,
				accountResult.AccountIdentity,
				PublicBindingIdentity: publicBindingIdentity,
				WorkspaceId: workspaceId,
				NextResetCreditExpiresAt: nextResetCreditExpiresAt,
				ResetCreditDetails: resetCreditDetails);
		}
		catch
		{
			transport.Abort();
			throw;
		}
	}

	internal static async Task<ICodexAppServerTransport> CreateTransportAsync(
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory,
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null)
	{
		ArgumentNullException.ThrowIfNull(transportFactory);
		return await CreateTransportCoreAsync(
			(info, _) => transportFactory(info),
			startInfo,
			cancellationToken,
			operationTracker);
	}

	internal static async Task<ICodexAppServerTransport> CreateTransportAsync(
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker,
			ICodexAppServerTransport> transportFactory,
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker operationTracker)
	{
		ArgumentNullException.ThrowIfNull(transportFactory);
		ArgumentNullException.ThrowIfNull(operationTracker);
		return await CreateTransportCoreAsync(
			(info, tracker) => transportFactory(info, tracker!),
			startInfo,
			cancellationToken,
			operationTracker);
	}

	private static async Task<ICodexAppServerTransport> CreateTransportCoreAsync(
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker?,
			ICodexAppServerTransport> transportFactory,
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		cancellationToken.ThrowIfCancellationRequested();
		Task<ICodexAppServerTransport> creationTask = Task.Run(
			() => transportFactory(startInfo, operationTracker),
			cancellationToken);

		try
		{
			return await creationTask.WaitAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			Task cleanupTask = AbortAndDisposeLateTransportAsync(creationTask);
			operationTracker?.Track(cleanupTask);
			throw;
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
				throw new CodexCliNotFoundException("找不到 Codex CLI。");
			}

			string homeDirectory = Path.GetFullPath(
				_homeDirectoryResolver(accountId));

			if (!Directory.Exists(homeDirectory))
			{
				throw new DirectoryNotFoundException(
					"Codex 帳號設定目錄尚未建立。");
			}

			return new LaunchContext(
				CreateStartInfo(normalizedExecutablePath, homeDirectory),
				resolution?.VersionEvidence,
				resolution?.ExecutableLease);
		}
		catch
		{
			resolution?.ExecutableLease?.Dispose();
			throw;
		}
	}

	private static void AttachVersionEvidence(
		Exception exception,
		CliVersionEvidence? evidence)
	{
		if (evidence is null)
		{
			return;
		}

		try
		{
			exception.AttachVersionEvidence(evidence);
		}
		catch
		{
			// 診斷 metadata 不得取代原始 polling failure。
		}
	}

	private static bool IsVersionSensitiveFailure(Exception exception)
	{
		return (exception is DecoderFallbackException) ||
			(exception is InvalidDataException) ||
			(exception is JsonException) ||
			(exception is CodexAppServerFailureException
			{
				Category: CodexAppServerFailureCategory.Compatibility or
					CodexAppServerFailureCategory.Unknown
			});
	}

	private static Func<CodexCliExecutableResolution?> WrapExecutableResolver(
		Func<string?> executableResolver)
	{
		ArgumentNullException.ThrowIfNull(executableResolver);
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

	private static Task AbortAndDisposeLateTransportAsync(
		Task<ICodexAppServerTransport> creationTask)
	{
		return Task.Run(async () =>
		{
			try
			{
				ICodexAppServerTransport transport =
					await creationTask.ConfigureAwait(false);
				transport.Abort();
				await transport.DisposeAsync().ConfigureAwait(false);
			}
			catch
			{
				// Late creation and cleanup failures must remain observed.
			}
		});
	}

	private static string CreateAccountReadRequest(
		int requestId = AccountReadRequestId,
		bool refreshToken = false)
	{
		return JsonSerializer.Serialize(new
		{
			method = "account/read",
			id = requestId,
			@params = new
			{
				refreshToken
			}
		});
	}

	internal static async Task<string> ReadAccountIdentityAsync(
		ICodexAppServerTransport transport,
		int requestId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(transport);
		if (requestId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(requestId));
		}

		JsonElement accountResultElement = await SendRequestAsync(
			transport,
			requestId,
			CreateAccountReadRequest(requestId),
			"account/read",
			cancellationToken);
		AccountResult accountResult = ParseAccount(accountResultElement);
		if (string.IsNullOrWhiteSpace(accountResult.AccountIdentity))
		{
			throw new InvalidDataException(
				"Codex 已回報帳號資料，但 AI Usage 無法確認目前登入的帳號。");
		}

		return accountResult.AccountIdentity;
	}

	internal static async Task<Guid> ReadConfiguredWorkspaceIdAsync(
		ICodexAppServerTransport transport,
		int requestId,
		CancellationToken cancellationToken)
	{
		JsonElement? workspaceValue = await ReadConfiguredWorkspaceValueAsync(
			transport,
			requestId,
			cancellationToken);
		return ParseConfiguredWorkspaceId(workspaceValue);
	}

	internal static async Task<JsonElement?> ReadConfiguredWorkspaceValueAsync(
		ICodexAppServerTransport transport,
		int requestId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(transport);
		if (requestId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(requestId));
		}

		JsonElement configResult = await SendRequestAsync(
			transport,
			requestId,
			CreateConfigReadRequest(requestId),
			"config/read",
			cancellationToken);
		return ParseConfiguredWorkspaceValue(configResult);
	}

	private static string CreateConfigReadRequest(int requestId)
	{
		return JsonSerializer.Serialize(new
		{
			method = "config/read",
			id = requestId,
			@params = new
			{
				includeLayers = false
			}
		});
	}

	internal static string CreateInitializeRequest()
	{
		return JsonSerializer.Serialize(new
		{
			method = "initialize",
			id = InitializeRequestId,
			@params = new
			{
				clientInfo = new
				{
					name = "ai_usage_dashboard",
					title = "AI Usage",
					version = "0.1.0"
				}
			}
		});
	}

	internal static string CreateInitializedNotification()
	{
		return JsonSerializer.Serialize(new
		{
			method = "initialized",
			@params = new { }
		});
	}

	private static string CreateRateLimitsReadRequest(
		int requestId = RateLimitsReadRequestId)
	{
		return JsonSerializer.Serialize(new
		{
			method = "account/rateLimits/read",
			id = requestId
		});
	}

	internal static async Task<JsonElement> SendRequestAsync(
		ICodexAppServerTransport transport,
		int requestId,
		string request,
		string method,
		CancellationToken cancellationToken)
	{
		await transport.WriteLineAsync(request, cancellationToken);

		while (true)
		{
			string? line = await transport.ReadLineAsync(cancellationToken);

			if (line is null)
			{
				throw new IOException(
					$"Codex app-server 在回應 {method} 前結束。");
			}

			using JsonDocument document = ParseJson(line);
			JsonElement root = document.RootElement;
			EnsureNoDuplicateProperties(root);

			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException("Codex app-server 訊息必須是 JSON object。");
			}

			if (!root.TryGetProperty("id", out JsonElement idElement))
			{
				continue;
			}

			if ((idElement.ValueKind != JsonValueKind.Number) ||
				!idElement.TryGetInt32(out int responseId) ||
				(responseId != requestId))
			{
				throw new InvalidDataException("Codex app-server 回傳非預期的 response id。");
			}

			return GetResponseResult(root, method);
		}
	}

	internal static async Task<JsonElement> ReadNotificationAsync(
		ICodexAppServerTransport transport,
		string expectedMethod,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(transport);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedMethod);

		while (true)
		{
			string? line = await transport.ReadLineAsync(cancellationToken);

			if (line is null)
			{
				throw new IOException(
					$"Codex app-server 在送出 {expectedMethod} 前結束。");
			}

			using JsonDocument document = ParseJson(line);
			JsonElement root = document.RootElement;
			EnsureNoDuplicateProperties(root);
			EnsureObject(root, "Codex app-server notification");

			if (root.TryGetProperty("id", out _))
			{
				throw new InvalidDataException(
					"等待 Codex notification 時收到非預期的 request／response。");
			}

			if (!root.TryGetProperty("method", out JsonElement methodElement) ||
				(methodElement.ValueKind != JsonValueKind.String))
			{
				throw new InvalidDataException("Codex notification 缺少 method。");
			}

			if (!string.Equals(
				methodElement.GetString(),
				expectedMethod,
				StringComparison.Ordinal))
			{
				continue;
			}

			if (!root.TryGetProperty("params", out JsonElement paramsElement))
			{
				throw new InvalidDataException(
					$"Codex {expectedMethod} notification 缺少 params。");
			}

			return paramsElement.Clone();
		}
	}

	private static JsonElement GetResponseResult(
		JsonElement response,
		string method)
	{
		if (response.TryGetProperty("error", out JsonElement errorElement))
		{
			EnsureObject(errorElement, $"Codex app-server {method} error");

			if (!errorElement.TryGetProperty("code", out JsonElement codeElement) ||
				(codeElement.ValueKind != JsonValueKind.Number) ||
				!codeElement.TryGetInt64(out long errorCode))
			{
				throw new InvalidDataException(
					$"Codex app-server {method} error 缺少整數 code。");
			}

			string serverMessage = GetRequiredString(errorElement, "message");
			CodexAppServerFailureCategory category =
				ClassifyAppServerFailure(errorCode, serverMessage);
			throw new CodexAppServerFailureException(
				method,
				errorCode,
				serverMessage,
				category);
		}

		if (!response.TryGetProperty("result", out JsonElement resultElement))
		{
			throw new InvalidDataException(
				$"Codex app-server {method} response 缺少 result。");
		}

		return resultElement.Clone();
	}

	private static CodexAppServerFailureCategory ClassifyAppServerFailure(
		long errorCode,
		string serverMessage)
	{
		if (IsPermanentAuthenticationFailure(serverMessage) ||
			IsExpiredAccessTokenFailure(serverMessage))
		{
			return CodexAppServerFailureCategory.Authentication;
		}

		if (errorCode == JsonRpcMethodNotFoundCode)
		{
			return CodexAppServerFailureCategory.Compatibility;
		}

		if ((errorCode == JsonRpcServerOverloadedCode) ||
			(errorCode == JsonRpcInternalErrorCode) ||
			ContainsOrdinalIgnoreCase(serverMessage, "temporarily") ||
			ContainsOrdinalIgnoreCase(serverMessage, "timed out") ||
			ContainsOrdinalIgnoreCase(serverMessage, "timeout") ||
			ContainsOrdinalIgnoreCase(serverMessage, "overloaded") ||
			ContainsOrdinalIgnoreCase(serverMessage, "unavailable") ||
			ContainsOrdinalIgnoreCase(serverMessage, "try again"))
		{
			return CodexAppServerFailureCategory.Transient;
		}

		if ((errorCode == JsonRpcParseErrorCode) ||
			(errorCode == JsonRpcInvalidRequestCode) ||
			(errorCode == JsonRpcInvalidParamsCode))
		{
			return CodexAppServerFailureCategory.Compatibility;
		}

		return CodexAppServerFailureCategory.Unknown;
	}

	private static bool IsExpiredAccessTokenFailure(string message)
	{
		bool referencesRefreshToken =
			ContainsOrdinalIgnoreCase(message, "refresh token") ||
			ContainsOrdinalIgnoreCase(message, "refresh_token");

		return !referencesRefreshToken &&
			ContainsOrdinalIgnoreCase(message, "token_expired");
	}

	private static bool IsPermanentAuthenticationFailure(string message)
	{
		bool referencesRefreshToken =
			ContainsOrdinalIgnoreCase(message, "refresh token") ||
			ContainsOrdinalIgnoreCase(message, "refresh_token");
		bool refreshTokenIsInvalid =
			referencesRefreshToken &&
			(ContainsOrdinalIgnoreCase(message, "revoked") ||
				ContainsOrdinalIgnoreCase(message, "invalidated") ||
				ContainsOrdinalIgnoreCase(message, "already used"));

		return ContainsOrdinalIgnoreCase(message, "authentication required") ||
			ContainsOrdinalIgnoreCase(message, "login required") ||
			ContainsOrdinalIgnoreCase(message, "not logged in") ||
			ContainsOrdinalIgnoreCase(message, "sign in again") ||
			ContainsOrdinalIgnoreCase(message, "invalid_grant") ||
			ContainsInvalidRefreshTokenPhrase(message) ||
			refreshTokenIsInvalid;
	}

	private static bool ContainsInvalidRefreshTokenPhrase(string message)
	{
		return ContainsRefreshTokenStatePhrase(message, "refresh token") ||
			ContainsRefreshTokenStatePhrase(message, "refresh_token");
	}

	private static bool ContainsRefreshTokenStatePhrase(
		string message,
		string tokenPhrase)
	{
		return ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} is expired") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} was expired") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} has expired") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} expired") ||
			ContainsOrdinalIgnoreCase(message, $"expired {tokenPhrase}") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} is invalid") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} was invalid") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} has become invalid") ||
			ContainsOrdinalIgnoreCase(message, $"{tokenPhrase} invalid") ||
			ContainsOrdinalIgnoreCase(message, $"invalid {tokenPhrase}");
	}

	private static bool ContainsOrdinalIgnoreCase(string value, string searchValue)
	{
		return value.Contains(searchValue, StringComparison.OrdinalIgnoreCase);
	}

	private static AccountResult ParseAccount(JsonElement result)
	{
		EnsureObject(result, "Codex account/read result");

		if (!result.TryGetProperty(
			"requiresOpenaiAuth",
			out JsonElement requiresAuthElement) ||
			((requiresAuthElement.ValueKind != JsonValueKind.True) &&
				(requiresAuthElement.ValueKind != JsonValueKind.False)))
		{
			throw new InvalidDataException(
				"Codex account/read response 缺少 requiresOpenaiAuth。");
		}

		bool requiresOpenaiAuth =
			requiresAuthElement.ValueKind == JsonValueKind.True;

		if (!result.TryGetProperty("account", out JsonElement accountElement) ||
			(accountElement.ValueKind == JsonValueKind.Null))
		{
			if (requiresOpenaiAuth)
			{
				throw new CodexUsageNotConfiguredException(
					"Codex 帳號尚未登入 ChatGPT。");
			}

			throw new CodexUsageAccountActionRequiredException(
				"Codex 目前的登入方式不會使用 ChatGPT 帳號，因此沒有可讀取的 ChatGPT 訂閱用量。請在 Codex 切換成 ChatGPT 登入，再重新連接帳號。");
		}

		EnsureObject(accountElement, "Codex account");
		string accountType = GetRequiredString(accountElement, "type");

		if (!string.Equals(accountType, "chatgpt", StringComparison.Ordinal))
		{
			throw new CodexUsageNotConfiguredException(
				"Codex 目前不是以 ChatGPT 訂閱帳號登入。");
		}

		string planType = GetRequiredString(accountElement, "planType");
		string? accountIdentity = NormalizeOptionalAccountIdentity(
			GetOptionalString(accountElement, "email"));
		if (accountIdentity is null)
		{
			throw new CodexUsageAccountActionRequiredException(
				"Codex 已登入 ChatGPT，但 AI Usage 無法確認目前登入的帳號。請改用其他 ChatGPT 帳號重新連接。");
		}

		return new AccountResult(planType, accountIdentity);
	}

	private static JsonElement? ParseConfiguredWorkspaceValue(JsonElement result)
	{
		if (result.ValueKind != JsonValueKind.Object ||
			!result.TryGetProperty("config", out JsonElement configElement) ||
			(configElement.ValueKind != JsonValueKind.Object) ||
			!result.TryGetProperty("origins", out JsonElement originsElement) ||
			(originsElement.ValueKind != JsonValueKind.Object))
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex private home 的 workspace 設定資料無法辨識。");
		}

		if (!configElement.TryGetProperty(
				"forced_chatgpt_workspace_id",
				out JsonElement workspaceElement) ||
			(workspaceElement.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		if ((workspaceElement.ValueKind != JsonValueKind.String) &&
			(workspaceElement.ValueKind != JsonValueKind.Array))
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex forced ChatGPT workspace 必須是 UUID 或 UUID 清單。");
		}

		return workspaceElement.Clone();
	}

	private static Guid ParseConfiguredWorkspaceId(JsonElement? configuredValue)
	{
		if (configuredValue is not JsonElement workspaceElement)
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex private home 缺少有效的 forced ChatGPT workspace 設定。");
		}

		JsonElement workspaceValue;

		if (workspaceElement.ValueKind == JsonValueKind.String)
		{
			workspaceValue = workspaceElement;
		}
		else if (workspaceElement.ValueKind == JsonValueKind.Array)
		{
			JsonElement.ArrayEnumerator values = workspaceElement.EnumerateArray();

			if (!values.MoveNext())
			{
				throw new CodexWorkspaceConfigurationException(
					"Codex forced ChatGPT workspace 必須只有一個 UUID。");
			}

			workspaceValue = values.Current;

			if (values.MoveNext())
			{
				throw new CodexWorkspaceConfigurationException(
					"Codex forced ChatGPT workspace 必須只有一個 UUID。");
			}
		}
		else
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex forced ChatGPT workspace 必須是單一 UUID。");
		}

		if ((workspaceValue.ValueKind != JsonValueKind.String) ||
			!Guid.TryParse(workspaceValue.GetString(), out Guid workspaceId) ||
			(workspaceId == Guid.Empty))
		{
			throw new CodexWorkspaceConfigurationException(
				"Codex forced ChatGPT workspace 必須是單一 UUID。");
		}

		return workspaceId;
	}

	private static (long? AvailableCount, DateTimeOffset? NextExpiresAt,
		CodexResetCreditDetails? Details)
		ParseResetCredits(JsonElement result, DateTimeOffset observedAt)
	{
		if (!result.TryGetProperty(
			"rateLimitResetCredits",
			out JsonElement resetCreditsElement) ||
			(resetCreditsElement.ValueKind == JsonValueKind.Null))
		{
			return (null, null, null);
		}

		EnsureObject(resetCreditsElement, "Codex rateLimitResetCredits");

		if (!resetCreditsElement.TryGetProperty(
			"availableCount",
			out JsonElement availableCountElement) ||
			(availableCountElement.ValueKind != JsonValueKind.Number) ||
			!availableCountElement.TryGetInt64(out long availableCount) ||
			(availableCount < 0))
		{
			throw new InvalidDataException(
				"Codex rateLimitResetCredits.availableCount 必須是非負整數。");
		}

		if (!resetCreditsElement.TryGetProperty("credits", out JsonElement creditsElement) ||
			(creditsElement.ValueKind == JsonValueKind.Null))
		{
			return (availableCount, null,
				new CodexResetCreditDetails(availableCount, null, false));
		}

		if (creditsElement.ValueKind != JsonValueKind.Array)
		{
			return (availableCount, null,
				new CodexResetCreditDetails(availableCount, null, false));
		}

		(DateTimeOffset? nextExpiresAt, CodexResetCreditDetails details) =
			ParseResetCreditDetails(
				creditsElement,
				availableCount,
				observedAt);
		return (availableCount, nextExpiresAt, details);
	}

	private static (DateTimeOffset? NextExpiresAt, CodexResetCreditDetails Details)
		ParseResetCreditDetails(
		JsonElement creditsElement,
		long availableCount,
		DateTimeOffset observedAt)
	{
		DateTimeOffset? nextExpiresAt = null;
		long availableDetailCount = 0;
		HashSet<string> availableCreditIds = new(StringComparer.Ordinal);
		List<CodexResetCredit> details = new();
		bool hasValidExpiryDetails = true;
		bool hasValidDisplayDetails = true;

		foreach (JsonElement credit in creditsElement.EnumerateArray())
		{
			if ((credit.ValueKind != JsonValueKind.Object) ||
				!credit.TryGetProperty("status", out JsonElement statusElement) ||
				(statusElement.ValueKind != JsonValueKind.String))
			{
				hasValidExpiryDetails = false;
				hasValidDisplayDetails = false;
				continue;
			}

			string? status = statusElement.GetString();
			bool isAvailable = string.Equals(
				status,
				CodexResetCredit.AvailableStatus,
				StringComparison.Ordinal);
			if (!isAvailable)
			{
				continue;
			}

			availableDetailCount++;

			if (!credit.TryGetProperty("id", out JsonElement idElement) ||
				(idElement.ValueKind != JsonValueKind.String))
			{
				hasValidExpiryDetails = false;
				hasValidDisplayDetails = false;
				continue;
			}

			string? creditId = idElement.GetString();
			if (string.IsNullOrWhiteSpace(creditId))
			{
				hasValidExpiryDetails = false;
				hasValidDisplayDetails = false;
				continue;
			}

			if (!availableCreditIds.Add(creditId))
			{
				hasValidExpiryDetails = false;
				hasValidDisplayDetails = false;
				continue;
			}

			bool hasExpiresAtField = credit.TryGetProperty("expiresAt", out _);
			if (!TryReadResetCreditTime(
				credit, "expiresAt", out DateTimeOffset? expiresAt))
			{
				hasValidExpiryDetails = false;
				hasValidDisplayDetails = false;
				continue;
			}

			if (!hasExpiresAtField)
			{
				hasValidExpiryDetails = false;
			}
			else if (expiresAt is DateTimeOffset expiry)
			{
				if (expiry <= observedAt)
				{
					hasValidExpiryDetails = false;
					hasValidDisplayDetails = false;
				}
				else if ((nextExpiresAt is null) ||
					(expiry < nextExpiresAt.Value))
				{
					nextExpiresAt = expiry;
				}
			}

			bool hasValidGrantedAt = TryReadResetCreditTime(
				credit, "grantedAt", out DateTimeOffset? grantedAt);
			bool hasValidResetType = TryReadResetCreditText(
				credit, "resetType", MaximumResetCreditTypeLength,
				out string? resetType);
			bool hasValidTitle = TryReadResetCreditText(
				credit, "title", MaximumResetCreditTitleLength,
				out string? title);
			bool hasValidDescription = TryReadResetCreditText(
				credit, "description", MaximumResetCreditDescriptionLength,
				out string? description);
			if (!hasValidGrantedAt || !hasValidResetType ||
				!hasValidTitle || !hasValidDescription)
			{
				hasValidDisplayDetails = false;
			}

			if (details.Count < MaximumResetCreditDetailCount)
			{
				details.Add(new CodexResetCredit(
					CodexResetCredit.AvailableStatus,
					resetType, grantedAt, expiresAt,
					title, description,
					HasExpiresAtField: hasExpiresAtField));
			}
			else
			{
				hasValidDisplayDetails = false;
			}
		}

		if (availableDetailCount > availableCount)
		{
			return (null, new CodexResetCreditDetails(
				availableCount,
				Array.Empty<CodexResetCredit>(),
				false,
				HasCountConflict: true));
		}

		bool hasEveryAvailableCredit =
			availableDetailCount == availableCount;
		return (
			(hasValidExpiryDetails && (availableCount > 0) &&
				hasEveryAvailableCredit) ? nextExpiresAt : null,
			new CodexResetCreditDetails(
				availableCount,
				details.ToArray(),
				hasValidDisplayDetails && hasEveryAvailableCredit));
	}

	private static bool TryReadResetCreditTime(
		JsonElement credit,
		string propertyName,
		out DateTimeOffset? timestamp)
	{
		timestamp = null;
		if (!credit.TryGetProperty(propertyName, out JsonElement value) ||
			(value.ValueKind == JsonValueKind.Null))
		{
			return true;
		}

		if ((value.ValueKind != JsonValueKind.Number) ||
			!value.TryGetInt64(out long seconds))
		{
			return false;
		}

		try
		{
			timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}
	}

	private static bool TryReadResetCreditText(
		JsonElement credit,
		string propertyName,
		int maximumLength,
		out string? text)
	{
		text = null;
		if (!credit.TryGetProperty(propertyName, out JsonElement value) ||
			(value.ValueKind == JsonValueKind.Null))
		{
			return true;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		string rawText = value.GetString() ?? string.Empty;
		if (rawText.Any(char.IsControl))
		{
			return false;
		}

		string normalized = rawText.Trim();
		if (normalized.Length > maximumLength)
		{
			return false;
		}

		text = normalized.Length == 0 ? null : normalized;
		return true;
	}

	private static IReadOnlyList<CodexRateLimitBucket> ParseRateLimits(
		JsonElement result)
	{
		EnsureObject(result, "Codex account/rateLimits/read result");
		List<CodexRateLimitBucket> buckets = new();

		if (result.TryGetProperty(
			"rateLimitsByLimitId",
			out JsonElement bucketsElement) &&
			(bucketsElement.ValueKind != JsonValueKind.Null))
		{
			EnsureObject(bucketsElement, "Codex rateLimitsByLimitId");
			int bucketCount = 0;

			foreach (JsonProperty property in bucketsElement.EnumerateObject())
			{
				bucketCount++;

				if (bucketCount > MaximumRateLimitBucketCount)
				{
					throw new InvalidDataException(
						$"Codex rateLimitsByLimitId 不得超過 {MaximumRateLimitBucketCount} 個 bucket。");
				}

				CodexRateLimitBucket bucket = ParseRateLimitBucket(
					property.Value,
					property.Name,
					enforceLimitIdMatch: true);

				if ((bucket.Primary is not null) || (bucket.Secondary is not null))
				{
					buckets.Add(bucket);
				}
			}
		}

		if (buckets.Count == 0)
		{
			if (!result.TryGetProperty("rateLimits", out JsonElement legacyElement))
			{
				throw new InvalidDataException(
					"Codex rate-limit response 缺少 rateLimits。");
			}

			CodexRateLimitBucket legacyBucket = ParseRateLimitBucket(
				legacyElement,
				"codex",
				enforceLimitIdMatch: false);

			if ((legacyBucket.Primary is not null) || (legacyBucket.Secondary is not null))
			{
				buckets.Add(legacyBucket);
			}
		}

		if (buckets.Count == 0)
		{
			throw new InvalidDataException(
				"Codex rate-limit response 沒有可用的 window。");
		}

		return buckets;
	}

	private static CodexRateLimitBucket ParseRateLimitBucket(
		JsonElement element,
		string fallbackLimitId,
		bool enforceLimitIdMatch)
	{
		EnsureObject(element, "Codex rate-limit bucket");
		string? declaredLimitId = GetOptionalString(element, "limitId");
		string normalizedFallbackLimitId = NormalizeRequiredRateLimitText(
			fallbackLimitId,
			MaximumRateLimitIdLength,
			"rate-limit bucket key");
		string limitId = declaredLimitId is null
			? normalizedFallbackLimitId
			: NormalizeRequiredRateLimitText(
				declaredLimitId,
				MaximumRateLimitIdLength,
				"limitId");

		if (enforceLimitIdMatch &&
			(declaredLimitId is not null) &&
			!string.Equals(limitId, normalizedFallbackLimitId, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Codex rate-limit bucket key 與 limitId 不一致。");
		}

		return new CodexRateLimitBucket(
			limitId,
			NormalizeOptionalRateLimitText(
				GetOptionalString(element, "limitName"),
				MaximumRateLimitNameLength,
				"limitName"),
			ParseRateLimitWindow(element, "primary"),
			ParseRateLimitWindow(element, "secondary"));
	}

	private static void EnsureMetricCapacity(
		IReadOnlyList<CodexRateLimitBucket> rateLimits,
		long? availableResetCredits)
	{
		int metricCount = availableResetCredits is null ? 0 : 1;

		foreach (CodexRateLimitBucket bucket in rateLimits)
		{
			metricCount += bucket.Primary is null ? 0 : 1;
			metricCount += bucket.Secondary is null ? 0 : 1;
		}

		if (metricCount > MaximumMetricCount)
		{
			throw new InvalidDataException(
				$"Codex rate-limit 項目不得超過 {MaximumMetricCount} 個。");
		}
	}

	private static CodexRateLimitWindow? ParseRateLimitWindow(
		JsonElement bucket,
		string propertyName)
	{
		if (!bucket.TryGetProperty(propertyName, out JsonElement windowElement) ||
			(windowElement.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		EnsureObject(windowElement, $"Codex {propertyName} rate-limit window");

		if (!windowElement.TryGetProperty(
			"usedPercent",
			out JsonElement usedPercentElement) ||
			(usedPercentElement.ValueKind != JsonValueKind.Number) ||
			!usedPercentElement.TryGetInt32(out int usedPercent) ||
			(usedPercent < 0) ||
			(usedPercent > 100))
		{
			throw new InvalidDataException("Codex usedPercent 必須是 0 到 100 的整數。");
		}

		long? duration = GetOptionalInt64(windowElement, "windowDurationMins");

		if ((duration is not null) && (duration.Value <= 0))
		{
			throw new InvalidDataException("Codex windowDurationMins 必須大於 0。");
		}

		long? resetsAtSeconds = GetOptionalInt64(windowElement, "resetsAt");
		DateTimeOffset? resetsAt = null;

		if (resetsAtSeconds is not null)
		{
			try
			{
				resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds.Value);
			}
			catch (ArgumentOutOfRangeException exception)
			{
				throw new InvalidDataException("Codex resetsAt 超出 Unix timestamp 範圍。", exception);
			}
		}

		return new CodexRateLimitWindow(
			usedPercent,
			duration,
			resetsAt);
	}

	private static long? GetOptionalInt64(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		if ((property.ValueKind != JsonValueKind.Number) ||
			!property.TryGetInt64(out long value))
		{
			throw new InvalidDataException($"Codex {propertyName} 必須是整數或 null。");
		}

		return value;
	}

	private static string? GetOptionalString(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		if (property.ValueKind != JsonValueKind.String)
		{
			throw new InvalidDataException($"Codex {propertyName} 必須是 string 或 null。");
		}

		return property.GetString();
	}

	private static string GetRequiredString(
		JsonElement element,
		string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property) ||
			(property.ValueKind != JsonValueKind.String) ||
			string.IsNullOrWhiteSpace(property.GetString()))
		{
			throw new InvalidDataException($"Codex {propertyName} 必須是非空字串。");
		}

		return property.GetString()!;
	}

	private static string? NormalizeOptionalRateLimitText(
		string? value,
		int maximumLength,
		string fieldName)
	{
		if (value is null)
		{
			return null;
		}

		if (value.Any(char.IsControl))
		{
			throw new InvalidDataException($"Codex {fieldName} 不得包含控制字元。");
		}

		string normalizedValue = value.Trim();

		if (normalizedValue.Length == 0)
		{
			return null;
		}

		if (normalizedValue.Length > maximumLength)
		{
			throw new InvalidDataException(
				$"Codex {fieldName} 不得超過 {maximumLength} 個字元。");
		}

		return normalizedValue;
	}

	private static string NormalizeRequiredRateLimitText(
		string? value,
		int maximumLength,
		string fieldName)
	{
		return NormalizeOptionalRateLimitText(value, maximumLength, fieldName) ??
			throw new InvalidDataException($"Codex {fieldName} 必須是非空字串。");
	}

	private static string? NormalizeOptionalAccountIdentity(string? value)
	{
		string identity = value?.Trim() ?? string.Empty;

		return (identity.Length > 0) &&
			(identity.Length <= MaximumAccountIdentityLength) &&
			!identity.Any(char.IsControl)
			? identity.ToLowerInvariant()
			: null;
	}

	private static void EnsureObject(JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException($"{name} 必須是 JSON object。");
		}
	}

	private static void EnsureNoDuplicateProperties(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				HashSet<string> names = new(StringComparer.Ordinal);

				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (!names.Add(property.Name))
					{
						throw new InvalidDataException(
							$"Codex app-server JSON 包含重複欄位：{property.Name}。");
					}

					EnsureNoDuplicateProperties(property.Value);
				}

				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in element.EnumerateArray())
				{
					EnsureNoDuplicateProperties(item);
				}

				break;
		}
	}

	private static IEnumerable<string> GetExecutableCandidates()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (!string.IsNullOrWhiteSpace(localApplicationData))
		{
			yield return Path.Combine(
				localApplicationData,
				"Programs",
				"OpenAI",
				"Codex",
				"bin",
				"codex.exe");
		}

		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);

		if (!string.IsNullOrWhiteSpace(userProfile))
		{
			yield return Path.Combine(userProfile, ".local", "bin", "codex.exe");
		}

		string? pathValue = Environment.GetEnvironmentVariable("PATH");

		if (string.IsNullOrWhiteSpace(pathValue))
		{
			yield break;
		}

		foreach (string pathEntry in pathValue.Split(Path.PathSeparator))
		{
			string directory = pathEntry.Trim().Trim('"');

			if (!string.IsNullOrWhiteSpace(directory) &&
				Path.IsPathFullyQualified(directory))
			{
				yield return Path.Combine(directory, "codex.exe");
			}
		}
	}

	private static JsonDocument ParseJson(string json)
	{
		return JsonDocument.Parse(
			json,
			new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 32
			});
	}

	private static async Task<string> ReadBoundedStreamAsync(
		Stream stream,
		int maximumBytes,
		Action terminateProcess,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[8192];
		using MemoryStream content = new(Math.Min(maximumBytes, buffer.Length));

		try
		{
			while (true)
			{
				int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

				if (bytesRead == 0)
				{
					break;
				}

				if ((content.Length + bytesRead) > maximumBytes)
				{
					throw new InvalidDataException("Codex app-server stderr 超過允許大小。");
				}

				await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
			}

			return StrictUtf8Encoding.GetString(
				content.GetBuffer(),
				0,
				checked((int)content.Length));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			terminateProcess();
			throw;
		}
	}

	private static async Task DrainStandardOutputAsync(
		BoundedUtf8LineReader reader,
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		int totalCharacters = 0;

		try
		{
			while (true)
			{
				string? line = await reader.ReadLineAsync(CancellationToken.None);

				if (line is null)
				{
					return;
				}

				totalCharacters = checked(totalCharacters + line.Length);

				if (totalCharacters > MaximumStandardOutputCharacters)
				{
					TryKillProcess(process, operationTracker);
					return;
				}
			}
		}
		catch
		{
			// Cleanup only drains unread notifications and intentionally ignores failures.
		}
	}

	private static async Task ObserveTaskAsync(Task task)
	{
		try
		{
			await task;
		}
		catch
		{
			// Cleanup observes and intentionally discards diagnostic stream failures.
		}
	}

	private static void ObserveFault(Task task)
	{
		_ = task.ContinueWith(
			completedTask => _ = completedTask.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously |
				TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	private static void TryKillProcess(
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
			operationTracker.MarkContainmentCompromised();
		}
	}

}
