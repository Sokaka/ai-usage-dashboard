using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.App.Providers;

internal sealed class GrokAccountLogin : IGrokAccountLogin
{
	private static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
	private readonly GrokAccountOperationGate _accountOperationGate;
	private readonly GrokProcessContainmentState _containmentState;
	private readonly IGrokCliExecutableValidator _executableValidator;
	private readonly Func<Guid, string> _getHomeDirectory;
	private readonly Func<Guid, string> _getWorkingDirectory;
	private readonly TimeSpan _loginTimeout;
	private readonly Action<string, string> _prepareDirectories;
	private readonly Func<ProcessStartInfo, GrokProcessContainmentState,
		CancellationToken, Task<int>> _processRunner;

	internal GrokAccountLogin(
		IGrokCliExecutableValidator? executableValidator = null,
		GrokAccountOperationGate? accountOperationGate = null,
		Func<Guid, string>? getHomeDirectory = null,
		Func<Guid, string>? getWorkingDirectory = null)
		: this(
			executableValidator ?? new GrokCliExecutableValidator(),
			accountOperationGate ?? GrokAccountOperationGate.Shared,
			GrokProcessContainmentState.Shared,
			getHomeDirectory ?? AppDataPaths.GetGrokHomeDirectory,
			getWorkingDirectory ?? AppDataPaths.GetGrokBlankWorkspaceDirectory,
			GrokAcpUsagePoller.PreparePrivateDirectories,
			RunProcessAsync,
			DefaultLoginTimeout)
	{
	}

	internal GrokAccountLogin(
		IGrokCliExecutableValidator executableValidator,
		GrokAccountOperationGate accountOperationGate,
		GrokProcessContainmentState containmentState,
		Func<Guid, string> getHomeDirectory,
		Func<Guid, string> getWorkingDirectory,
		Action<string, string> prepareDirectories,
		Func<ProcessStartInfo, GrokProcessContainmentState, CancellationToken, Task<int>>
			processRunner,
		TimeSpan loginTimeout)
	{
		_executableValidator = executableValidator ??
			throw new ArgumentNullException(nameof(executableValidator));
		_accountOperationGate = accountOperationGate ??
			throw new ArgumentNullException(nameof(accountOperationGate));
		_containmentState = containmentState ??
			throw new ArgumentNullException(nameof(containmentState));
		_getHomeDirectory = getHomeDirectory ??
			throw new ArgumentNullException(nameof(getHomeDirectory));
		_getWorkingDirectory = getWorkingDirectory ??
			throw new ArgumentNullException(nameof(getWorkingDirectory));
		_prepareDirectories = prepareDirectories ??
			throw new ArgumentNullException(nameof(prepareDirectories));
		_processRunner = processRunner ??
			throw new ArgumentNullException(nameof(processRunner));
		_loginTimeout = loginTimeout > TimeSpan.Zero
			? loginTimeout
			: throw new ArgumentOutOfRangeException(nameof(loginTimeout));
	}

	public async Task LoginAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Grok 帳號識別碼不可為空。", nameof(accountId));
		}

		_containmentState.ThrowIfCompromised();

		using CancellationTokenSource timeoutSource = new(_loginTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		ProviderProcessOperationTracker operationTracker = new();
		IDisposable? operationLease = null;
		IDisposable? executableLease = null;

		try
		{
			IDisposable rawOperationLease =
				await _accountOperationGate.EnterAsync(
				accountId,
				linkedSource.Token);
			operationLease = operationTracker.HoldLease(rawOperationLease);
			linkedSource.Token.ThrowIfCancellationRequested();

			string homeDirectory = Path.GetFullPath(_getHomeDirectory(accountId));
			string workingDirectory = Path.GetFullPath(
				_getWorkingDirectory(accountId));
			_prepareDirectories(
				homeDirectory,
				workingDirectory);
			_containmentState.ThrowIfCompromised();

			// Revalidate on every login attempt. The path is never accepted from PATH
			// and is shared with the background ACP probe's validator boundary.
			using GrokValidatedExecutable executable =
				await _executableValidator.ResolveAndValidateAsync(
					linkedSource.Token,
					operationTracker);
			WindowsOfficialCliExecutableLease? rawExecutableLease =
				executable.TakeExecutableLease();
			executableLease = rawExecutableLease is null
				? null
				: operationTracker.HoldLease(
					_containmentState.RetainExecutableLease(rawExecutableLease));
			_containmentState.ThrowIfCompromised();
			ProcessStartInfo startInfo = CreateStartInfo(
				executable.ExecutablePath,
				homeDirectory,
				workingDirectory);
			int exitCode = await ProviderProcessExecution.RunAsynchronousAsync(
				operationCancellationToken => _processRunner(
					startInfo,
					_containmentState,
					operationCancellationToken),
				linkedSource.Token,
				operationTracker);

			if (exitCode != 0)
			{
				throw new GrokAccountLoginException(
					"Grok 登入未完成，請再試一次。");
			}
		}
		catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw new GrokAccountLoginException(
				"Grok 登入逾時，請再試一次。");
		}
		catch (GrokAccountLoginException)
		{
			throw;
		}
		catch (GrokCliNotFoundException)
		{
			throw;
		}
		catch (GrokCliUntrustedException)
		{
			throw;
		}
		catch (GrokProcessContainmentException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new GrokAccountLoginException(
				"無法開啟或完成 Grok 登入，請再試一次。",
				exception);
		}
		finally
		{
			executableLease?.Dispose();
			operationLease?.Dispose();
		}
	}

	internal static ProcessStartInfo CreateStartInfo(
		string executablePath,
		string homeDirectory,
		string workingDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

		if (!Path.IsPathFullyQualified(executablePath) ||
			!Path.IsPathFullyQualified(homeDirectory) ||
			!Path.IsPathFullyQualified(workingDirectory))
		{
			throw new ArgumentException(
				"Grok login paths must be fully qualified.");
		}

		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = false,
			FileName = Path.GetFullPath(executablePath),
			RedirectStandardError = false,
			RedirectStandardInput = false,
			RedirectStandardOutput = false,
			UseShellExecute = false,
			WorkingDirectory = Path.GetFullPath(workingDirectory)
		};
		startInfo.ArgumentList.Add("--no-auto-update");
		startInfo.ArgumentList.Add("login");
		startInfo.ArgumentList.Add("--oauth");
		GrokProcessEnvironment.Apply(startInfo, homeDirectory);
		return startInfo;
	}

	internal static async Task<int> RunProcessAsync(
		ProcessStartInfo startInfo,
		GrokProcessContainmentState containmentState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(containmentState);
		cancellationToken.ThrowIfCancellationRequested();

		if (startInfo.RedirectStandardInput ||
			startInfo.RedirectStandardOutput ||
			startInfo.RedirectStandardError)
		{
			throw new InvalidOperationException(
				"Grok interactive login must inherit every console stream.");
		}

		using Process process = new()
		{
			StartInfo = startInfo
		};
		bool hasStarted = false;

		try
		{
			using (IDisposable transition =
				containmentState.EnterLaunchTransition())
			{
				if (!process.Start())
				{
					throw new InvalidOperationException(
						"Grok CLI process did not start.");
				}

				hasStarted = true;
			}

			cancellationToken.ThrowIfCancellationRequested();
			await process.WaitForExitAsync(cancellationToken);
			return process.ExitCode;
		}
		finally
		{
			if (hasStarted && !IsRootExitPositivelyConfirmed(process))
			{
				// The browser is an independently launched process. Cancel and
				// timeout terminate only the CLI root, never its process tree.
				TryKillRootProcess(process);

				if (!await WaitForPositiveRootExitAsync(process))
				{
					containmentState.MarkCompromised();
					throw new GrokProcessContainmentException(
						"Grok login process termination could not be positively confirmed. Restart AI Usage before launching Grok again.");
				}
			}
		}
	}

	private static bool IsRootExitPositivelyConfirmed(Process process)
	{
		try
		{
			return process.HasExited;
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or
				NotSupportedException or
				Win32Exception)
		{
			return false;
		}
	}

	private static void TryKillRootProcess(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: false);
			}
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or
				NotSupportedException or
				Win32Exception)
		{
			// The root already exited or cannot be terminated on this platform.
		}
	}

	private static async Task<bool> WaitForPositiveRootExitAsync(Process process)
	{
		try
		{
			await process.WaitForExitAsync(CancellationToken.None)
				.WaitAsync(ProcessCleanupTimeout);
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or
				NotSupportedException or
				Win32Exception or
				TimeoutException)
		{
			// The final process-state query below remains the positive oracle.
		}

		return IsRootExitPositivelyConfirmed(process);
	}
}
