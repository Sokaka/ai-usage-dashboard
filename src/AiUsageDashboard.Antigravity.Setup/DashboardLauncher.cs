using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Antigravity.Setup;

internal static class DashboardLauncher
{
	internal static bool TryOpen()
	{
		return TryOpen(
			AppContext.BaseDirectory,
			startInfo => Process.Start(startInfo));
	}

	internal static bool TryOpen(
		string setupDirectory,
		Func<ProcessStartInfo, Process?> startProcess)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(setupDirectory);
		ArgumentNullException.ThrowIfNull(startProcess);

		try
		{
			DirectoryInfo sharedRuntimeDirectory = new(
				Path.GetFullPath(setupDirectory));
			sharedRuntimeDirectory.Refresh();

			if (!sharedRuntimeDirectory.Exists ||
				((sharedRuntimeDirectory.Attributes &
					FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			string sharedExecutablePath = Path.GetFullPath(Path.Combine(
				sharedRuntimeDirectory.FullName,
				"AiUsageDashboard.App.exe"));
			FileInfo sharedExecutable = new(sharedExecutablePath);
			sharedExecutable.Refresh();

			if (sharedExecutable.Exists)
			{
				return TryStart(
					sharedExecutable,
					sharedRuntimeDirectory.FullName,
					startProcess);
			}

			if (Directory.Exists(sharedExecutablePath))
			{
				return false;
			}

			string legacyExecutablePath = Path.GetFullPath(Path.Combine(
				sharedRuntimeDirectory.FullName,
				"..",
				"app",
				"AiUsageDashboard.App.exe"));
			FileInfo legacyExecutable = new(legacyExecutablePath);
			legacyExecutable.Refresh();
			return TryStart(
				legacyExecutable,
				legacyExecutable.DirectoryName ??
					sharedRuntimeDirectory.FullName,
				startProcess);
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				Win32Exception or
				InvalidOperationException or
				IOException or
				UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool TryStart(
		FileInfo executable,
		string workingDirectory,
		Func<ProcessStartInfo, Process?> startProcess)
	{
		if (!executable.Exists ||
			((executable.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			return false;
		}

		ProcessStartInfo startInfo = new(executable.FullName)
		{
			UseShellExecute = false,
			WorkingDirectory = workingDirectory
		};
		startInfo.ArgumentList.Add(
			AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount);
		using Process? process = startProcess(startInfo);
		return process is not null;
	}
}
