using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal enum MaintenanceUpdaterLaunchFailure
{
	None,
	NotCanonicalInstallation,
	ShutdownChannelUnavailable,
	ExecutableUnavailable,
	AlreadyRunning,
	StartFailed
}

internal sealed record MaintenanceUpdaterLaunchResult(
	bool WasStarted,
	int? ExitCode,
	MaintenanceUpdaterLaunchFailure Failure,
	Exception? Exception)
{
	internal static MaintenanceUpdaterLaunchResult Rejected(
		MaintenanceUpdaterLaunchFailure failure,
		Exception? exception = null)
	{
		return new MaintenanceUpdaterLaunchResult(
			WasStarted: false,
			ExitCode: null,
			failure,
			exception);
	}
}

internal sealed class MaintenanceUpdaterLauncher
{
	private const string ExecutablePathDescription =
		"The canonical maintenance updater executable path";
	private readonly Func<string> _getExecutablePath;
	private readonly Func<ProcessStartInfo, Task<int>> _runProcessAsync;
	private int _isRunning;

	internal MaintenanceUpdaterLauncher()
		: this(
			MaintenanceUpdaterPathContract.GetCanonicalExecutablePath,
			RunProcessAsync)
	{
	}

	internal MaintenanceUpdaterLauncher(
		Func<string> getExecutablePath,
		Func<ProcessStartInfo, Task<int>> runProcessAsync)
	{
		_getExecutablePath = getExecutablePath ??
			throw new ArgumentNullException(nameof(getExecutablePath));
		_runProcessAsync = runProcessAsync ??
			throw new ArgumentNullException(nameof(runProcessAsync));
	}

	internal bool IsRunning => Volatile.Read(ref _isRunning) != 0;

	internal bool CanLaunch(
		AppInstallationContext installationContext,
		bool isShutdownChannelReady)
	{
		ArgumentNullException.ThrowIfNull(installationContext);

		if ((installationContext.Kind !=
				AppInstallationKind.CanonicalManaged) ||
			!isShutdownChannelReady)
		{
			return false;
		}

		try
		{
			string executablePath = Path.GetFullPath(_getExecutablePath());
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				executablePath,
				ExecutablePathDescription);
			return File.Exists(executablePath);
		}
		catch (Exception exception) when (
			exception is ArgumentException or IOException or SecurityException or
				InvalidOperationException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	internal async Task<MaintenanceUpdaterLaunchResult> LaunchAsync(
		AppInstallationContext installationContext,
		bool isShutdownChannelReady)
	{
		ArgumentNullException.ThrowIfNull(installationContext);

		if (installationContext.Kind != AppInstallationKind.CanonicalManaged)
		{
			return MaintenanceUpdaterLaunchResult.Rejected(
				MaintenanceUpdaterLaunchFailure.NotCanonicalInstallation);
		}

		if (!isShutdownChannelReady)
		{
			return MaintenanceUpdaterLaunchResult.Rejected(
				MaintenanceUpdaterLaunchFailure.ShutdownChannelUnavailable);
		}

		if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
		{
			return MaintenanceUpdaterLaunchResult.Rejected(
				MaintenanceUpdaterLaunchFailure.AlreadyRunning);
		}

		try
		{
			string executablePath = Path.GetFullPath(_getExecutablePath());
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				executablePath,
				ExecutablePathDescription);

			if (!File.Exists(executablePath))
			{
				return MaintenanceUpdaterLaunchResult.Rejected(
					MaintenanceUpdaterLaunchFailure.ExecutableUnavailable);
			}

			ProcessStartInfo startInfo = CreateStartInfo(executablePath);
			int exitCode = await _runProcessAsync(startInfo).ConfigureAwait(false);
			return new MaintenanceUpdaterLaunchResult(
				WasStarted: true,
				exitCode,
				MaintenanceUpdaterLaunchFailure.None,
				Exception: null);
		}
		catch (Exception exception) when (
			exception is ArgumentException or Win32Exception or IOException or
				InvalidOperationException or SecurityException or
				UnauthorizedAccessException)
		{
			return MaintenanceUpdaterLaunchResult.Rejected(
				MaintenanceUpdaterLaunchFailure.StartFailed,
				exception);
		}
		finally
		{
			Volatile.Write(ref _isRunning, 0);
		}
	}

	internal static ProcessStartInfo CreateStartInfo(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		string fullPath = Path.GetFullPath(executablePath);
		string? workingDirectory = Path.GetDirectoryName(fullPath);

		if (string.IsNullOrEmpty(workingDirectory))
		{
			throw new ArgumentException(
				"The maintenance updater path must have a parent directory.",
				nameof(executablePath));
		}

		return new ProcessStartInfo(fullPath)
		{
			CreateNoWindow = false,
			UseShellExecute = true,
			WindowStyle = ProcessWindowStyle.Normal,
			WorkingDirectory = workingDirectory
		};
	}

	private static async Task<int> RunProcessAsync(ProcessStartInfo startInfo)
	{
		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"Windows did not start the canonical maintenance updater.");
		await process.WaitForExitAsync().ConfigureAwait(false);
		return process.ExitCode;
	}
}
