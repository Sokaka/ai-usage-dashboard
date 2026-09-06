using System.ComponentModel;
using System.Diagnostics;

namespace AiUsageDashboard.Updater;

internal sealed record RunningPayloadProcess(
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string ExecutablePath);

internal sealed record ObservedUpdateProcessIdentity(
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string PayloadGenerationId);

internal sealed record UpdateShutdownQueryResult(
	bool IsObserved,
	ObservedUpdateProcessIdentity? Identity,
	string? Failure);

internal sealed record UpdateShutdownReservationResult(
	bool IsAccepted,
	ObservedUpdateProcessIdentity? Identity,
	string? Failure);

internal interface IRunningPayloadProcessProbe
{
	IReadOnlyList<RunningPayloadProcess> Capture(string currentPayloadRoot);

	Task<bool> WaitForNaturalExitAsync(
		RunningPayloadProcess process,
		TimeSpan timeout,
		CancellationToken cancellationToken);
}

internal interface IUpdateShutdownProtocolClient
{
	Task<UpdateShutdownQueryResult> QueryAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken);

	Task<UpdateShutdownReservationResult> ReserveAsync(
		ObservedUpdateProcessIdentity identity,
		TimeSpan timeout,
		CancellationToken cancellationToken);
}

internal interface IManagedPayloadShutdown
{
	Task EnsureQuiescentAsync(
		string currentPayloadRoot,
		string expectedPayloadGenerationId,
		CancellationToken cancellationToken = default);

	void EnsureNoPayloadProcesses(string currentPayloadRoot);
}

internal sealed class RunningPayloadProcessGateException : Exception
{
	internal RunningPayloadProcessGateException(string message)
		: base(message)
	{
	}

	internal RunningPayloadProcessGateException(
		string message,
		Exception innerException)
		: base(message, innerException)
	{
	}
}

internal sealed class RunningPayloadProcessGate : IManagedPayloadShutdown
{
	private const string DashboardExecutableRelativePath =
		@"app\AiUsageDashboard.App.exe";
	private readonly IRunningPayloadProcessProbe _processProbe;
	private readonly IUpdateShutdownProtocolClient _shutdownClient;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _protocolTimeout;
	private readonly TimeSpan _processExitTimeout;

	internal RunningPayloadProcessGate(
		IRunningPayloadProcessProbe processProbe,
		IUpdateShutdownProtocolClient shutdownClient,
		TimeSpan protocolTimeout,
		TimeSpan processExitTimeout,
		TimeProvider? timeProvider = null)
	{
		_processProbe = processProbe ??
			throw new ArgumentNullException(nameof(processProbe));
		_shutdownClient = shutdownClient ??
			throw new ArgumentNullException(nameof(shutdownClient));

		if (protocolTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(protocolTimeout));
		}

		if (processExitTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(processExitTimeout));
		}

		_protocolTimeout = protocolTimeout;
		_processExitTimeout = processExitTimeout;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task EnsureQuiescentAsync(
		string currentPayloadRoot,
		string expectedPayloadGenerationId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(currentPayloadRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedPayloadGenerationId);
		string resolvedPayloadRoot = Path.GetFullPath(currentPayloadRoot);
		IReadOnlyList<RunningPayloadProcess> initialProcesses =
			_processProbe.Capture(resolvedPayloadRoot);

		if (initialProcesses.Count == 0)
		{
			return;
		}

		RunningPayloadProcess dashboardProcess = FindDashboardProcess(
			resolvedPayloadRoot,
			initialProcesses);
		UpdateShutdownQueryResult query = await _shutdownClient.QueryAsync(
			_protocolTimeout,
			cancellationToken);

		if (!query.IsObserved || (query.Identity is null))
		{
			throw new RunningPayloadProcessGateException(
				"The running dashboard did not return an observed update identity" +
				FormatFailure(query.Failure));
		}

		ObservedUpdateProcessIdentity observedIdentity = query.Identity;
		AssertIdentityMatches(
			dashboardProcess,
			observedIdentity,
			expectedPayloadGenerationId,
			"query");

		IReadOnlyList<RunningPayloadProcess> beforeReservationProcesses =
			_processProbe.Capture(resolvedPayloadRoot);
		RunningPayloadProcess beforeReservationDashboard = FindDashboardProcess(
			resolvedPayloadRoot,
			beforeReservationProcesses);
		AssertIdentityMatches(
			beforeReservationDashboard,
			observedIdentity,
			expectedPayloadGenerationId,
			"pre-reservation recheck");

		UpdateShutdownReservationResult reservation =
			await _shutdownClient.ReserveAsync(
				observedIdentity,
				_protocolTimeout,
				cancellationToken);

		if (!reservation.IsAccepted || (reservation.Identity is null))
		{
			throw new RunningPayloadProcessGateException(
				"The running dashboard did not accept the exact update shutdown " +
				"reservation" + FormatFailure(reservation.Failure));
		}

		AssertSameProtocolIdentity(
			observedIdentity,
			reservation.Identity,
			"reservation response");
		await WaitForNaturalExitAsync(
			beforeReservationProcesses,
			cancellationToken);

		IReadOnlyList<RunningPayloadProcess> remainingProcesses =
			_processProbe.Capture(resolvedPayloadRoot);
		if (remainingProcesses.Count != 0)
		{
			throw new RunningPayloadProcessGateException(
				"One or more processes are still running from the current payload.");
		}
	}

	public void EnsureNoPayloadProcesses(string currentPayloadRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(currentPayloadRoot);
		string resolvedPayloadRoot = Path.GetFullPath(currentPayloadRoot);
		IReadOnlyList<RunningPayloadProcess> remainingProcesses =
			_processProbe.Capture(resolvedPayloadRoot);

		if (remainingProcesses.Count != 0)
		{
			throw new RunningPayloadProcessGateException(
				"One or more processes are still running from the current payload.");
		}
	}

	private static RunningPayloadProcess FindDashboardProcess(
		string currentPayloadRoot,
		IReadOnlyList<RunningPayloadProcess> processes)
	{
		string expectedPath = Path.GetFullPath(Path.Combine(
			currentPayloadRoot,
			DashboardExecutableRelativePath));
		RunningPayloadProcess[] matchingProcesses = processes
			.Where(process => string.Equals(
				process.ExecutablePath,
				expectedPath,
				StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (matchingProcesses.Length != 1)
		{
			throw new RunningPayloadProcessGateException(
				"Running current-payload processes exist, but exactly one " +
				"dashboard process could not be proven.");
		}

		return matchingProcesses[0];
	}

	private static void AssertIdentityMatches(
		RunningPayloadProcess process,
		ObservedUpdateProcessIdentity identity,
		string expectedPayloadGenerationId,
		string operation)
	{
		if ((process.ProcessId != identity.ProcessId) ||
			(process.ProcessStartTimeUtcTicks !=
				identity.ProcessStartTimeUtcTicks) ||
			!string.Equals(
				identity.PayloadGenerationId,
				expectedPayloadGenerationId,
				StringComparison.Ordinal))
		{
			throw new RunningPayloadProcessGateException(
				$"The {operation} identity does not exactly match the current " +
				"payload process.");
		}
	}

	private static void AssertSameProtocolIdentity(
		ObservedUpdateProcessIdentity expected,
		ObservedUpdateProcessIdentity actual,
		string operation)
	{
		if ((expected.ProcessId != actual.ProcessId) ||
			(expected.ProcessStartTimeUtcTicks !=
				actual.ProcessStartTimeUtcTicks) ||
			!string.Equals(
				expected.PayloadGenerationId,
				actual.PayloadGenerationId,
				StringComparison.Ordinal))
		{
			throw new RunningPayloadProcessGateException(
				$"The {operation} changed the observed process identity.");
		}
	}

	private static string FormatFailure(string? failure)
	{
		return string.IsNullOrWhiteSpace(failure)
			? "."
			: $": {failure}";
	}

	private async Task WaitForNaturalExitAsync(
		IReadOnlyList<RunningPayloadProcess> processes,
		CancellationToken cancellationToken)
	{
		DateTimeOffset deadline =
			_timeProvider.GetUtcNow() + _processExitTimeout;

		foreach (RunningPayloadProcess process in processes)
		{
			TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
			if ((remaining <= TimeSpan.Zero) ||
				!await _processProbe.WaitForNaturalExitAsync(
					process,
					remaining,
					cancellationToken))
			{
				throw new RunningPayloadProcessGateException(
					$"Process {process.ProcessId} did not exit naturally before " +
					"the update deadline.");
			}
		}
	}
}

internal sealed class SystemRunningPayloadProcessProbe :
	IRunningPayloadProcessProbe
{
	private const string DashboardProcessName = "AiUsageDashboard.App";
	private const string DashboardExecutableRelativePath =
		@"app\AiUsageDashboard.App.exe";

	public IReadOnlyList<RunningPayloadProcess> Capture(string currentPayloadRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(currentPayloadRoot);
		string resolvedRoot = Path.GetFullPath(currentPayloadRoot);
		string expectedDashboardPath = Path.GetFullPath(Path.Combine(
			resolvedRoot,
			DashboardExecutableRelativePath));
		Dictionary<int, RunningPayloadProcess> matches = [];
		CaptureDashboardProcesses(expectedDashboardPath, matches);

		if (!Directory.Exists(resolvedRoot))
		{
			return matches.Values
				.OrderBy(process => process.ProcessId)
				.ToArray();
		}

		IReadOnlyDictionary<string, HashSet<string>> packagedExecutables =
			EnumeratePackagedExecutables(resolvedRoot);

		foreach ((string processName, HashSet<string> executablePaths) in
			packagedExecutables)
		{
			if (string.Equals(
					processName,
					DashboardProcessName,
					StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (Process candidate in GetProcessesByName(processName))
			{
				using (candidate)
				{
					CapturePackagedCandidate(
						candidate,
						executablePaths,
						matches);
				}
			}
		}

		return matches.Values
			.OrderBy(process => process.ProcessId)
			.ToArray();
	}

	public async Task<bool> WaitForNaturalExitAsync(
		RunningPayloadProcess process,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(process);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Process liveProcess;

		try
		{
			liveProcess = Process.GetProcessById(process.ProcessId);
		}
		catch (ArgumentException)
		{
			return true;
		}

		using (liveProcess)
		{
			try
			{
				if (liveProcess.StartTime.ToUniversalTime().Ticks !=
					process.ProcessStartTimeUtcTicks)
				{
					return true;
				}

				await liveProcess.WaitForExitAsync(cancellationToken)
					.WaitAsync(timeout, cancellationToken);
				return true;
			}
			catch (TimeoutException)
			{
				return false;
			}
			catch (InvalidOperationException)
			{
				return TryProveExactProcessExited(process);
			}
			catch (Win32Exception exception)
			{
				throw new RunningPayloadProcessGateException(
					$"Unable to wait for process {process.ProcessId} to exit.",
					exception);
			}
		}
	}

	internal static void EnsureDashboardExecutablePathMatches(
		string expectedExecutablePath,
		string actualExecutablePath,
		int processId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(actualExecutablePath);
		string resolvedExpectedPath = Path.GetFullPath(expectedExecutablePath);
		string resolvedActualPath = Path.GetFullPath(actualExecutablePath);

		if (!string.Equals(
				resolvedActualPath,
				resolvedExpectedPath,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new RunningPayloadProcessGateException(
				$"Process {processId} is named '{DashboardProcessName}', but its " +
				"executable is not the exact current dashboard payload.");
		}
	}

	private static void CaptureDashboardProcesses(
		string expectedDashboardPath,
		Dictionary<int, RunningPayloadProcess> matches)
	{
		foreach (Process candidate in GetProcessesByName(DashboardProcessName))
		{
			using (candidate)
			{
				try
				{
					string executablePath = GetResolvedExecutablePath(candidate);
					EnsureDashboardExecutablePathMatches(
						expectedDashboardPath,
						executablePath,
						candidate.Id);
					matches[candidate.Id] = CreateRunningProcess(
						candidate,
						executablePath);
				}
				catch (RunningPayloadProcessGateException)
				{
					throw;
				}
				catch (Exception exception) when (
					exception is ArgumentException or
						InvalidOperationException or
						NotSupportedException or
						Win32Exception)
				{
					throw CreateInspectionException(candidate, exception);
				}
			}
		}
	}

	private static void CapturePackagedCandidate(
		Process candidate,
		HashSet<string> packagedExecutablePaths,
		Dictionary<int, RunningPayloadProcess> matches)
	{
		try
		{
			string resolvedExecutablePath = GetResolvedExecutablePath(candidate);
			if (!packagedExecutablePaths.Contains(resolvedExecutablePath))
			{
				return;
			}

			matches[candidate.Id] = CreateRunningProcess(
				candidate,
				resolvedExecutablePath);
		}
		catch (RunningPayloadProcessGateException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				InvalidOperationException or
				NotSupportedException or
				Win32Exception)
		{
			throw CreateInspectionException(candidate, exception);
		}
	}

	private static RunningPayloadProcessGateException CreateInspectionException(
		Process candidate,
		Exception exception)
	{
		return new RunningPayloadProcessGateException(
			$"Unable to inspect process {candidate.Id} exactly.",
			exception);
	}

	private static RunningPayloadProcess CreateRunningProcess(
		Process candidate,
		string resolvedExecutablePath)
	{
		return new RunningPayloadProcess(
			candidate.Id,
			candidate.StartTime.ToUniversalTime().Ticks,
			resolvedExecutablePath);
	}

	private static Process[] GetProcessesByName(string processName)
	{
		try
		{
			return Process.GetProcessesByName(processName);
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or Win32Exception)
		{
			throw new RunningPayloadProcessGateException(
				$"Unable to enumerate processes named '{processName}'.",
				exception);
		}
	}

	private static string GetResolvedExecutablePath(Process candidate)
	{
		string? executablePath = candidate.MainModule?.FileName;

		if (string.IsNullOrWhiteSpace(executablePath))
		{
			throw new RunningPayloadProcessGateException(
				$"Unable to prove the executable path for process {candidate.Id}.");
		}

		return Path.GetFullPath(executablePath);
	}

	private static IReadOnlyDictionary<string, HashSet<string>>
		EnumeratePackagedExecutables(string resolvedRoot)
	{
		Dictionary<string, HashSet<string>> result =
			new(StringComparer.OrdinalIgnoreCase);
		Stack<DirectoryInfo> pendingDirectories = new();
		pendingDirectories.Push(new DirectoryInfo(resolvedRoot));

		while (pendingDirectories.TryPop(out DirectoryInfo? directory))
		{
			directory.Refresh();
			if (!directory.Exists ||
				((directory.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				throw new RunningPayloadProcessGateException(
					$"The current payload directory cannot be inspected safely: " +
					$"{directory.FullName}");
			}

			foreach (DirectoryInfo child in directory.EnumerateDirectories())
			{
				child.Refresh();
				if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new RunningPayloadProcessGateException(
						$"The current payload contains a reparse-point directory: " +
						$"{child.FullName}");
				}

				pendingDirectories.Push(child);
			}

			foreach (FileInfo file in directory.EnumerateFiles("*.exe"))
			{
				file.Refresh();
				if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new RunningPayloadProcessGateException(
						$"The current payload contains a reparse-point executable: " +
						$"{file.FullName}");
				}

				string processName = Path.GetFileNameWithoutExtension(file.Name);
				if (!result.TryGetValue(
						processName,
						out HashSet<string>? executablePaths))
				{
					executablePaths = new HashSet<string>(
						StringComparer.OrdinalIgnoreCase);
					result.Add(processName, executablePaths);
				}

				executablePaths.Add(Path.GetFullPath(file.FullName));
			}
		}

		return result;
	}

	private static bool TryProveExactProcessExited(
		RunningPayloadProcess expectedProcess)
	{
		try
		{
			using Process candidate = Process.GetProcessById(
				expectedProcess.ProcessId);
			return candidate.StartTime.ToUniversalTime().Ticks !=
				expectedProcess.ProcessStartTimeUtcTicks;
		}
		catch (ArgumentException)
		{
			return true;
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or Win32Exception)
		{
			throw new RunningPayloadProcessGateException(
				$"Unable to prove process {expectedProcess.ProcessId} exited.",
				exception);
		}
	}
}
