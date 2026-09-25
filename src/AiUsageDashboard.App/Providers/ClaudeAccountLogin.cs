using System.Diagnostics;
using System.IO;
using System.Text;

using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeAccountLogin : IClaudeAccountLogin
{
	private sealed record CommandContext(
		string ExecutablePath,
		string ConfigDirectory,
		WindowsOfficialCliExecutableLease ExecutableLease);

	internal sealed record ProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);

	private const int MaximumStandardErrorBytes = 64 * 1024;
	private const int MaximumStandardOutputBytes = 1024 * 1024;
	private static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan DefaultStatusTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);
	private readonly Func<Guid, string> _configDirectoryResolver;
	private readonly Func<WindowsOfficialCliExecutableLease?> _executableResolver;
	private readonly Func<Guid, string, CancellationToken, Task> _loginCompleted;
	private readonly TimeSpan _loginTimeout;
	private readonly ClaudeAccountOperationGate _operationGate;
	private readonly Func<
		ProcessStartInfo,
		ProviderProcessOperationTracker,
		CancellationToken,
		Task<ProcessResult>> _processRunner;
	private readonly TimeSpan _statusTimeout;
	private readonly TimeProvider _timeProvider;

	public ClaudeAccountLogin(Func<Guid, string> configDirectoryResolver)
		: this(
			configDirectoryResolver,
			ClaudeCliUsagePoller.ResolveExecutablePath,
			RunProcessAsync,
			ClaudeAccountOperationGate.Shared,
			DefaultLoginTimeout,
			DefaultStatusTimeout,
			CompleteWithoutInvalidationAsync,
			protectedExecutableResolver: ClaudeCliUsagePoller.ResolveExecutable,
			trackedProcessRunner: RunProcessAsync)
	{
	}

	public ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		ClaudeAccountOperationGate operationGate)
		: this(
			configDirectoryResolver,
			ClaudeCliUsagePoller.ResolveExecutablePath,
			RunProcessAsync,
			operationGate,
			DefaultLoginTimeout,
			DefaultStatusTimeout,
			CompleteWithoutInvalidationAsync,
			protectedExecutableResolver: ClaudeCliUsagePoller.ResolveExecutable,
			trackedProcessRunner: RunProcessAsync)
	{
	}

	public ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		ClaudeAccountOperationGate operationGate,
		Func<Guid, string, CancellationToken, Task> loginCompleted)
		: this(
			configDirectoryResolver,
			ClaudeCliUsagePoller.ResolveExecutablePath,
			RunProcessAsync,
			operationGate,
			DefaultLoginTimeout,
			DefaultStatusTimeout,
			loginCompleted,
			protectedExecutableResolver: ClaudeCliUsagePoller.ResolveExecutable,
			trackedProcessRunner: RunProcessAsync)
	{
	}

	internal ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		ClaudeAccountOperationGate operationGate)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			operationGate,
			DefaultLoginTimeout,
			DefaultStatusTimeout,
			CompleteWithoutInvalidationAsync)
	{
	}

	internal ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		ClaudeAccountOperationGate operationGate,
		Func<Guid, string, CancellationToken, Task> loginCompleted)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			operationGate,
			DefaultLoginTimeout,
			DefaultStatusTimeout,
			loginCompleted)
	{
	}

	internal ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		ClaudeAccountOperationGate operationGate,
		TimeSpan loginTimeout,
		TimeSpan statusTimeout)
		: this(
			configDirectoryResolver,
			executableResolver,
			processRunner,
			operationGate,
			loginTimeout,
			statusTimeout,
			CompleteWithoutInvalidationAsync)
	{
	}

	internal ClaudeAccountLogin(
		Func<Guid, string> configDirectoryResolver,
		Func<string?> executableResolver,
		Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
		ClaudeAccountOperationGate operationGate,
		TimeSpan loginTimeout,
		TimeSpan statusTimeout,
		Func<Guid, string, CancellationToken, Task> loginCompleted,
		Func<WindowsOfficialCliExecutableLease?>? protectedExecutableResolver = null,
		Func<
			ProcessStartInfo,
			ProviderProcessOperationTracker,
			CancellationToken,
			Task<ProcessResult>>? trackedProcessRunner = null,
		TimeProvider? timeProvider = null)
	{
		_configDirectoryResolver = configDirectoryResolver ??
			throw new ArgumentNullException(nameof(configDirectoryResolver));
		ArgumentNullException.ThrowIfNull(executableResolver);
		_executableResolver = protectedExecutableResolver ??
			WrapExecutableResolver(executableResolver);
		ArgumentNullException.ThrowIfNull(processRunner);
		_processRunner = trackedProcessRunner ??
			((startInfo, _, token) => processRunner(startInfo, token));
		_loginCompleted = loginCompleted ??
			throw new ArgumentNullException(nameof(loginCompleted));
		_operationGate = operationGate ??
			throw new ArgumentNullException(nameof(operationGate));

		if (loginTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(loginTimeout));
		}

		if (statusTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(statusTimeout));
		}

		_loginTimeout = loginTimeout;
		_statusTimeout = statusTimeout;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<ClaudeAccountLoginResult> LoginAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		using CancellationTokenSource gateTimeoutSource = new(_statusTimeout, _timeProvider);
		using CancellationTokenSource gateLinkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				gateTimeoutSource.Token);
		IDisposable rawOperation;

		try
		{
			rawOperation = await _operationGate.EnterAsync(
				accountId,
				gateLinkedSource.Token);
		}
		catch (ClaudeCliContainmentException exception)
		{
			throw new ClaudeAccountLoginException(
				exception.Message,
				exception);
		}
		catch (OperationCanceledException) when (gateLinkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			ThrowIfAccountContainmentCompromised(accountId);

			throw new ClaudeAccountLoginException(
				"等待前一個 Claude 帳號作業逾時，請再試一次。");
		}

		ProviderProcessOperationTracker operationTracker = new(
			() => _operationGate.MarkContainmentCompromised(accountId));
		using IDisposable operation = operationTracker.HoldLease(rawOperation);
		cancellationToken.ThrowIfCancellationRequested();
		CommandContext commandContext = await ResolveCommandContextAsync(
				accountId,
				operationTracker,
				cancellationToken)
				.ConfigureAwait(false);
		using IDisposable executableLease = operationTracker.HoldLease(
			commandContext.ExecutableLease);
		string fullExecutablePath = commandContext.ExecutablePath;
		string configDirectory = commandContext.ConfigDirectory;

		ProcessResult loginResult = await RunCommandAsync(
			CreateLoginStartInfo(fullExecutablePath, configDirectory),
			_loginTimeout,
			"Claude 登入逾時，請再試一次。",
			"無法啟動 Claude 登入，請再試一次。",
			operationTracker,
			cancellationToken);

		if (loginResult.ExitCode != 0)
		{
			throw new ClaudeAccountLoginException(
				"Claude 登入未完成，請再試一次。");
		}

		ProcessResult versionResult = await RunCommandAsync(
			ClaudeCliUsagePoller.CreateVersionStartInfo(
				fullExecutablePath,
				configDirectory),
			_statusTimeout,
			"確認 Claude Code 版本逾時，請再試一次。",
			"無法確認 Claude Code 版本，請再試一次。",
			operationTracker,
			cancellationToken);

		if (versionResult.ExitCode != 0)
		{
			throw new ClaudeAccountLoginException(
				"無法確認 Claude Code 版本，請先更新後再試一次。");
		}

		CliVersionEvidence versionEvidence;

		try
		{
			Version validatedVersion =
				ClaudeCliUsagePoller.EnsureSupportedVersion(
					versionResult.StandardOutput);
			versionEvidence = CliVersionPolicies.AssessClaude(validatedVersion);
		}
		catch
		{
			throw new ClaudeAccountLoginException(
				"Claude Code 版本不支援安全的訂閱範圍確認；請先更新後再試一次。");
		}

		ProcessResult statusResult = await RunCommandAsync(
			CreateAuthStatusStartInfo(fullExecutablePath, configDirectory),
			_statusTimeout,
			"確認 Claude 登入狀態逾時，請再試一次。",
			"無法確認 Claude 登入狀態，請再試一次。",
			operationTracker,
			cancellationToken);

		if (statusResult.ExitCode != 0)
		{
			throw new ClaudeAccountLoginException(
				"無法確認 Claude 登入狀態，請再試一次。");
		}

		ClaudeSubscriptionContext subscriptionContext;

		try
		{
			subscriptionContext = ClaudeCliUsagePoller.ParseSubscriptionContext(
				statusResult.StandardOutput);
		}
		catch (ClaudeUsageNotConfiguredException exception) when (
			exception.IsAccountIdentityUnavailable)
		{
			throw new ClaudeAccountLoginException(
				"Claude 登入完成，但無法確認目前登入的帳號。");
		}
		catch (ClaudeUsageNotConfiguredException exception) when (
			exception.RecoveryAction == UsageRecoveryAction.ConfirmSubscription)
		{
			throw new ClaudeAccountLoginException(
				exception.Message,
				exception);
		}
		catch (InvalidDataException exception) when (
			ClaudeCliUsagePoller.IsAccountIdentityUnavailable(exception))
		{
			throw new ClaudeAccountLoginException(
				"Claude 登入完成，但無法確認目前登入的帳號。");
		}
		catch
		{
			throw new ClaudeAccountLoginException(
				"無法確認 Claude 登入，請確認使用 claude.ai 訂閱帳號。");
		}

		if (string.IsNullOrWhiteSpace(subscriptionContext.AccountIdentity))
		{
			throw new ClaudeAccountLoginException(
				"Claude 登入完成，但無法確認目前登入的帳號。");
		}

		try
		{
			await _loginCompleted(
				accountId,
				subscriptionContext.AccountIdentity,
				cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			throw new ClaudeAccountLoginException(
				"Claude 已登入，但 AI Usage 無法完成帳號連接。請再試一次。");
		}

		return new ClaudeAccountLoginResult(
			subscriptionContext.AccountIdentity,
			subscriptionContext,
			versionEvidence);
	}

	private async Task<CommandContext> ResolveCommandContextAsync(
			Guid accountId,
			ProviderProcessOperationTracker operationTracker,
			CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(_statusTimeout, _timeProvider);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			return await ProviderProcessExecution.RunSynchronousAsync(
				() =>
				{
					WindowsOfficialCliExecutableLease? executableLease =
						_executableResolver();

					try
					{
						if ((executableLease is null) ||
							!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
								executableLease.ExecutablePath,
								out string normalizedExecutablePath) ||
							!File.Exists(normalizedExecutablePath))
						{
							throw new ClaudeAccountLoginException(
								"找不到 Claude Code CLI，請先安裝或更新 Claude Code。");
						}

						string configDirectory = Path.GetFullPath(
							_configDirectoryResolver(accountId));
						Directory.CreateDirectory(configDirectory);
						return new CommandContext(
							normalizedExecutablePath,
							configDirectory,
							executableLease);
					}
					catch
					{
						executableLease?.Dispose();
						throw;
					}
				},
				linkedSource.Token,
				operationTracker,
				static commandContext =>
				{
					commandContext.ExecutableLease.Dispose();
					return Task.CompletedTask;
				});
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			ThrowIfProcessContainmentCompromised(
				operationTracker,
				new TimeoutException("Claude command context resolution timed out."));

			throw new ClaudeAccountLoginException(
				"準備 Claude 登入環境逾時，請再試一次。");
		}
		catch (ClaudeAccountLoginException)
		{
			throw;
		}
		catch (ClaudeCliUntrustedException)
		{
			throw new ClaudeAccountLoginException(
				"Claude Code CLI 未通過官方簽章、路徑或版本驗證；請從 Anthropic 官方來源重新安裝或更新。");
		}
		catch (ClaudeCliContainmentException exception)
		{
			throw new ClaudeAccountLoginException(
				exception.Message,
				exception);
		}
		catch
		{
			throw new ClaudeAccountLoginException(
				"無法準備 Claude 帳號的獨立設定目錄。");
		}
	}

	private static Task CompleteWithoutInvalidationAsync(
		Guid accountId,
		string accountIdentity,
		CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	private static Func<WindowsOfficialCliExecutableLease?> WrapExecutableResolver(
		Func<string?> executableResolver)
	{
		return () =>
		{
			string? executablePath = executableResolver();
			return string.IsNullOrWhiteSpace(executablePath)
				? null
				: WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		};
	}

	internal static ProcessStartInfo CreateAuthStatusStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateCapturedStartInfo(
			executablePath,
			configDirectory);
		startInfo.ArgumentList.Add("auth");
		startInfo.ArgumentList.Add("status");
		startInfo.ArgumentList.Add("--json");
		return startInfo;
	}

	internal static ProcessStartInfo CreateLoginStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateInteractiveStartInfo(
			executablePath,
			configDirectory);
		startInfo.ArgumentList.Add("auth");
		startInfo.ArgumentList.Add("login");
		startInfo.ArgumentList.Add("--claudeai");
		return startInfo;
	}

	private static async Task CleanupInteractiveProcessAsync(
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		TryKillProcess(process, operationTracker);
		Task cleanupTask = CompleteInteractiveProcessCleanupAsync(
			process,
			operationTracker);
		operationTracker.Track(cleanupTask);
		ProviderProcessExecution.ObserveFault(cleanupTask);

		try
		{
			await cleanupTask.WaitAsync(ProcessCleanupTimeout);
		}
		catch (TimeoutException)
		{
			// Tracker 會讓 executable lease 與 account gate 跟著 late cleanup 存活。
		}
	}

	private static async Task CleanupProcessAsync(
		Process process,
		Task standardOutputTask,
		Task standardErrorTask,
		ProviderProcessOperationTracker operationTracker)
	{
		TryKillProcess(process, operationTracker);
		Task cleanupTask = CompleteProcessCleanupAsync(
			process,
			standardOutputTask,
			standardErrorTask,
			operationTracker);
		operationTracker.Track(cleanupTask);
		ProviderProcessExecution.ObserveFault(cleanupTask);

		try
		{
			await cleanupTask.WaitAsync(ProcessCleanupTimeout);
		}
		catch (TimeoutException)
		{
			// Tracker 會讓 executable lease 與 account gate 跟著 late cleanup 存活。
		}
	}

	private static async Task CompleteInteractiveProcessCleanupAsync(
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		try
		{
			await ProviderProcessExecution.WaitForConfirmedExitAsync(
				process,
				operationTracker);
		}
		finally
		{
			process.Dispose();
		}
	}

	private static async Task CompleteProcessCleanupAsync(
		Process process,
		Task standardOutputTask,
		Task standardErrorTask,
		ProviderProcessOperationTracker operationTracker)
	{
		try
		{
			await Task.WhenAll(
				ProviderProcessExecution.WaitForConfirmedExitAsync(
					process,
					operationTracker),
				ObserveTaskAsync(standardOutputTask),
				ObserveTaskAsync(standardErrorTask));
		}
		finally
		{
			process.Dispose();
		}
	}

	private static ProcessStartInfo CreateCapturedStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateCommonStartInfo(
			executablePath,
			configDirectory);
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardError = true;
		startInfo.RedirectStandardInput = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.StandardErrorEncoding = StrictUtf8Encoding;
		startInfo.StandardOutputEncoding = StrictUtf8Encoding;
		return startInfo;
	}

	private static ProcessStartInfo CreateCommonStartInfo(
		string executablePath,
		string configDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string normalizedExecutablePath) ||
			!Path.IsPathFullyQualified(configDirectory))
		{
			throw new ArgumentException(
				"Claude CLI 執行檔必須是本機絕對路徑，設定目錄必須使用絕對路徑。");
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = normalizedExecutablePath,
			UseShellExecute = false,
			WorkingDirectory = configDirectory
		};
		ClaudeProcessEnvironment.Apply(startInfo, configDirectory);
		return startInfo;
	}

	private static ProcessStartInfo CreateInteractiveStartInfo(
		string executablePath,
		string configDirectory)
	{
		ProcessStartInfo startInfo = CreateCommonStartInfo(
			executablePath,
			configDirectory);
		startInfo.CreateNoWindow = false;
		startInfo.RedirectStandardError = false;
		startInfo.RedirectStandardInput = false;
		startInfo.RedirectStandardOutput = false;
		return startInfo;
	}

	private static async Task ObserveTaskAsync(Task task)
	{
		try
		{
			await task;
		}
		catch
		{
			// Cleanup intentionally discards partial output and process failures.
		}
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
					throw new InvalidDataException(
						"Claude CLI 輸出超過安全大小上限。");
				}

				await content.WriteAsync(
					buffer.AsMemory(0, bytesRead),
					cancellationToken);
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

	private static Task<ProcessResult> RunProcessAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken)
	{
		return RunProcessAsync(
			startInfo,
			new ProviderProcessOperationTracker(),
			cancellationToken);
	}

	private static async Task<ProcessResult> RunProcessAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		bool capturesEveryStream = startInfo.RedirectStandardInput &&
			startInfo.RedirectStandardOutput &&
			startInfo.RedirectStandardError;
		bool capturesNoStreams = !startInfo.RedirectStandardInput &&
			!startInfo.RedirectStandardOutput &&
			!startInfo.RedirectStandardError;

		if (capturesEveryStream)
		{
			return await RunCapturedProcessAsync(
				startInfo,
				operationTracker,
				cancellationToken);
		}

		if (capturesNoStreams)
		{
			return await RunInteractiveProcessAsync(
				startInfo,
				operationTracker,
				cancellationToken);
		}

		throw new InvalidOperationException(
			"Claude CLI process stream 設定不符合預期。");
	}

	private static async Task<ProcessResult> RunCapturedProcessAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Process process = new()
		{
			StartInfo = startInfo
		};
		bool hasStarted = false;
		bool processOwnershipTransferred = false;
		Task<string> standardOutputTask = Task.FromResult(string.Empty);
		Task<string> standardErrorTask = Task.FromResult(string.Empty);

		try
		{
			if (!process.Start())
			{
				throw new InvalidOperationException("無法啟動 Claude Code CLI。");
			}

			hasStarted = true;
			cancellationToken.ThrowIfCancellationRequested();
			process.StandardInput.Close();
			standardOutputTask = ReadBoundedStreamAsync(
				process.StandardOutput.BaseStream,
				MaximumStandardOutputBytes,
				() => TryKillProcess(process, operationTracker),
				cancellationToken);
			standardErrorTask = ReadBoundedStreamAsync(
				process.StandardError.BaseStream,
				MaximumStandardErrorBytes,
				() => TryKillProcess(process, operationTracker),
				cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			string standardOutput = await standardOutputTask.WaitAsync(cancellationToken);
			string standardError = await standardErrorTask.WaitAsync(cancellationToken);
			return new ProcessResult(process.ExitCode, standardOutput, standardError);
		}
		catch
		{
			if (hasStarted)
			{
				processOwnershipTransferred = true;
				await CleanupProcessAsync(
					process,
					standardOutputTask,
					standardErrorTask,
					operationTracker);
			}

			throw;
		}
		finally
		{
			if (!processOwnershipTransferred)
			{
				process.Dispose();
			}
		}
	}

	private static async Task<ProcessResult> RunInteractiveProcessAsync(
		ProcessStartInfo startInfo,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Process process = new()
		{
			StartInfo = startInfo
		};
		bool hasStarted = false;
		bool processOwnershipTransferred = false;

		try
		{
			if (!process.Start())
			{
				throw new InvalidOperationException("無法啟動 Claude Code CLI。");
			}

			hasStarted = true;
			cancellationToken.ThrowIfCancellationRequested();
			await process.WaitForExitAsync(cancellationToken);
			return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
		}
		catch
		{
			if (hasStarted)
			{
				processOwnershipTransferred = true;
				await CleanupInteractiveProcessAsync(
					process,
					operationTracker);
			}

			throw;
		}
		finally
		{
			if (!processOwnershipTransferred)
			{
				process.Dispose();
			}
		}
	}

	private async Task<ProcessResult> RunCommandAsync(
		ProcessStartInfo startInfo,
		TimeSpan timeout,
		string timeoutMessage,
		string failureMessage,
		ProviderProcessOperationTracker operationTracker,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		ProcessResult result;

		try
		{
			result = await ProviderProcessExecution.RunAsynchronousAsync(
				token => _processRunner(
					startInfo,
					operationTracker,
					token),
				linkedSource.Token,
				operationTracker);
		}
		catch (OperationCanceledException exception) when (
			linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			ThrowIfProcessContainmentCompromised(
				operationTracker,
				exception);

			throw new ClaudeAccountLoginException(timeoutMessage);
		}
		catch (Exception exception)
		{
			ThrowIfProcessContainmentCompromised(
				operationTracker,
				exception);
			throw new ClaudeAccountLoginException(failureMessage);
		}

		ThrowIfProcessContainmentCompromised(operationTracker);
		return result;
	}

	private void ThrowIfAccountContainmentCompromised(Guid accountId)
	{
		try
		{
			_operationGate.ThrowIfContainmentCompromised(accountId);
		}
		catch (ClaudeCliContainmentException exception)
		{
			throw new ClaudeAccountLoginException(
				exception.Message,
				exception);
		}
	}

	private static void ThrowIfProcessContainmentCompromised(
		ProviderProcessOperationTracker operationTracker,
		Exception? innerException = null)
	{
		if (!operationTracker.IsContainmentCompromised)
		{
			return;
		}

		if (innerException is null)
		{
			throw new ClaudeAccountLoginException(
				ClaudeCliContainmentException.RestartRequiredMessage);
		}

		throw new ClaudeAccountLoginException(
			ClaudeCliContainmentException.RestartRequiredMessage,
			innerException);
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
