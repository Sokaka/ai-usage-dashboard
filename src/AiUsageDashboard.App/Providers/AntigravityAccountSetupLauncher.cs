using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal enum AntigravityAccountSetupOutcome
{
	Completed,
	CompletedOfficialPrint,
	CompletionUnknown,
	Cancelled,
	Unavailable,
	Failed,
	LaunchFailed
}

internal interface IAntigravityAccountSetupLauncher
{
	Task<AntigravityAccountSetupOutcome> RunAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken);
}

internal sealed class AntigravityAccountSetupLauncher :
	IAntigravityAccountSetupLauncher
{
	private const string AppDirectoryName = "app";
	private const string SetupDirectoryName = "setup";
	private const string SetupExecutableName =
		"AiUsageDashboard.Antigravity.Setup.exe";
	private const int CompletionUnknownProcessResult = int.MinValue;
	private const int CancelledBeforeLaunchProcessResult = int.MinValue + 1;
	private static readonly TimeSpan ProcessCloseGracePeriod =
		TimeSpan.FromSeconds(3);
	private readonly string _appBaseDirectory;
	private readonly Func<ProcessStartInfo, CancellationToken, Task<int?>>
		_runProcessAsync;
	private readonly AntigravitySetupAttemptStateStore? _attemptStateStore;

	internal AntigravityAccountSetupLauncher()
	{
		_appBaseDirectory = AppContext.BaseDirectory;
		_attemptStateStore = AntigravitySetupAttemptStateStore.CreateDefault();
		_runProcessAsync = (startInfo, cancellationToken) => RunProcessAsync(
			startInfo,
			_attemptStateStore,
			cancellationToken);
	}

	internal AntigravityAccountSetupLauncher(
		string appBaseDirectory,
		Func<ProcessStartInfo, CancellationToken, Task<int?>> runProcessAsync)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
		_appBaseDirectory = appBaseDirectory;
		_runProcessAsync = runProcessAsync ??
			throw new ArgumentNullException(nameof(runProcessAsync));
		_attemptStateStore = null;
	}

	public async Task<AntigravityAccountSetupOutcome> RunAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return AntigravityAccountSetupOutcome.Cancelled;
		}

		ProcessStartInfo? startInfo;

		try
		{
			if (!TryCreateStartInfo(_appBaseDirectory, out startInfo) ||
				(startInfo is null))
			{
				return AntigravityAccountSetupOutcome.Unavailable;
			}

			startInfo.Environment[
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedSetupAttemptIdEnvironmentVariable] =
				setupAttemptId.ToString("N");

			if (_attemptStateStore is not null)
			{
				await _attemptStateStore.BeginLaunchAsync(
					setupAttemptId,
					AntigravitySetupProcessIdentity.CaptureCurrent(),
					DateTimeOffset.UtcNow,
					CancellationToken.None);
			}

			if (cancellationToken.IsCancellationRequested)
			{
				return AntigravityAccountSetupOutcome.Cancelled;
			}

			int? exitCode = await _runProcessAsync(
				startInfo,
				cancellationToken);
			return exitCode switch
			{
				CancelledBeforeLaunchProcessResult =>
					AntigravityAccountSetupOutcome.Cancelled,
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedOfficialPrintSuccessExitCode =>
					AntigravityAccountSetupOutcome.CompletedOfficialPrint,
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedSuccessExitCode =>
					AntigravityAccountSetupOutcome.Completed,
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedCancelledExitCode =>
					AntigravityAccountSetupOutcome.Cancelled,
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedFailedExitCode =>
					AntigravityAccountSetupOutcome.Failed,
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedCompletionUnknownExitCode =>
					AntigravityAccountSetupOutcome.CompletionUnknown,
				null => AntigravityAccountSetupOutcome.LaunchFailed,
				_ => AntigravityAccountSetupOutcome.CompletionUnknown
			};
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			return AntigravityAccountSetupOutcome.CompletionUnknown;
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				IOException or
				UnauthorizedAccessException or
				NotSupportedException or
				Win32Exception or
				InvalidOperationException)
		{
			return AntigravityAccountSetupOutcome.LaunchFailed;
		}
	}

	internal Task<AntigravityAccountSetupOutcome> RunAsync(
		CancellationToken cancellationToken)
	{
		return RunAsync(Guid.NewGuid(), cancellationToken);
	}

	internal static bool TryCreateStartInfo(
		string appBaseDirectory,
		out ProcessStartInfo? startInfo)
	{
		startInfo = null;
		ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

		DirectoryInfo appDirectory = new(
			Path.GetFullPath(appBaseDirectory));
		if (!string.Equals(
				appDirectory.Name,
				AppDirectoryName,
				StringComparison.OrdinalIgnoreCase) ||
			(appDirectory.Parent is not DirectoryInfo packageDirectory) ||
			!appDirectory.Exists ||
			((appDirectory.Attributes & FileAttributes.ReparsePoint) != 0))
		{
			return false;
		}

		string sharedExecutablePath = Path.GetFullPath(
			Path.Combine(appDirectory.FullName, SetupExecutableName));
		FileInfo sharedExecutable = new(sharedExecutablePath);
		sharedExecutable.Refresh();

		if (sharedExecutable.Exists)
		{
			if ((sharedExecutable.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			startInfo = CreateStartInfo(
				sharedExecutable.FullName,
				appDirectory.FullName);
			return true;
		}

		if (Directory.Exists(sharedExecutablePath))
		{
			return false;
		}

		string setupDirectoryPath = Path.GetFullPath(
			Path.Combine(packageDirectory.FullName, SetupDirectoryName));
		DirectoryInfo setupDirectory = new(setupDirectoryPath);
		string executablePath = Path.GetFullPath(
			Path.Combine(setupDirectory.FullName, SetupExecutableName));

		if (!Directory.Exists(setupDirectory.FullName) ||
			!File.Exists(executablePath))
		{
			return false;
		}

		FileAttributes setupDirectoryAttributes =
			File.GetAttributes(setupDirectory.FullName);
		FileAttributes executableAttributes = File.GetAttributes(executablePath);
		if (((setupDirectoryAttributes & FileAttributes.ReparsePoint) != 0) ||
			((executableAttributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			return false;
		}

		startInfo = CreateStartInfo(
			executablePath,
			setupDirectory.FullName);
		return true;
	}

	private static ProcessStartInfo CreateStartInfo(
		string executablePath,
		string workingDirectory)
	{
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = workingDirectory
		};
		startInfo.ArgumentList.Add(
			AntigravityMachineSetupLaunchArguments.DashboardManaged);
		startInfo.Environment[
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedResultProtocolEnvironmentVariable] =
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSourceKindExitProtocol;
		return startInfo;
	}

	private static async Task<int?> RunProcessAsync(
		ProcessStartInfo startInfo,
		AntigravitySetupAttemptStateStore attemptStateStore,
		CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return CancelledBeforeLaunchProcessResult;
		}

		using Process? process = Process.Start(startInfo);
		if (process is null)
		{
			return null;
		}

		string? rawAttemptId = startInfo.Environment[
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSetupAttemptIdEnvironmentVariable];
		if (!Guid.TryParseExact(rawAttemptId, "N", out Guid setupAttemptId) ||
			(setupAttemptId == Guid.Empty))
		{
			return CompletionUnknownProcessResult;
		}

		bool wasRegistered = await TryRegisterActiveProcessAsync(
			() => attemptStateStore.MarkActiveAsync(
				setupAttemptId,
				new AntigravitySetupProcessIdentity(
					process.Id,
					process.StartTime.ToUniversalTime().Ticks),
				CancellationToken.None));
		if (!wasRegistered)
		{
			// The helper is already running. Its own startup path also registers
			// the attempt, so preserve the durable intent and let restart recovery
			// observe either that state or the later approval receipt.
			return CompletionUnknownProcessResult;
		}

		try
		{
			bool hasConfirmedExit = await WaitForExitOrTerminateAsync(
				process.WaitForExitAsync,
				() => process.HasExited,
				() => process.CloseMainWindow(),
				ProcessCloseGracePeriod,
				cancellationToken);
			return hasConfirmedExit
				? process.ExitCode
				: CompletionUnknownProcessResult;
		}
		catch (Exception exception) when (
			IsExpectedProcessCleanupException(exception))
		{
			return CompletionUnknownProcessResult;
		}
	}

	internal static async Task<bool> TryRegisterActiveProcessAsync(
		Func<Task> registerAsync)
	{
		ArgumentNullException.ThrowIfNull(registerAsync);

		try
		{
			await registerAsync();
			return true;
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				IOException or
				UnauthorizedAccessException or
				NotSupportedException or
				Win32Exception or
				InvalidOperationException)
		{
			return false;
		}
	}

	internal static async Task<bool> WaitForExitOrTerminateAsync(
		Func<CancellationToken, Task> waitForExitAsync,
		Func<bool> hasExited,
		Action requestCooperativeClose,
		TimeSpan terminationTimeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(waitForExitAsync);
		ArgumentNullException.ThrowIfNull(hasExited);
		ArgumentNullException.ThrowIfNull(requestCooperativeClose);

		if (terminationTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(terminationTimeout));
		}

		try
		{
			await waitForExitAsync(cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			try
			{
				if (hasExited())
				{
					return true;
				}

				requestCooperativeClose();
				await waitForExitAsync(CancellationToken.None)
					.WaitAsync(terminationTimeout);
				return true;
			}
			catch (Exception exception) when (
				IsExpectedProcessCleanupException(exception))
			{
				// Leave an unresponsive helper alive so it can finish an external
				// commit and fsync its receipt. The durable intent remains pending.
				return false;
			}
		}
	}

	private static bool IsExpectedProcessCleanupException(Exception exception)
	{
		return exception is TimeoutException or
			InvalidOperationException or
			NotSupportedException or
			Win32Exception;
	}
}
