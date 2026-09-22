using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.Updater;

internal sealed record ProcessLineage(
	string CurrentExecutablePath,
	ExactProcessIdentity CurrentIdentity,
	string ParentExecutablePath,
	ExactProcessIdentity ParentIdentity);

internal interface IProcessLineageProbe
{
	ProcessLineage CaptureCurrent();
}

internal sealed class SystemProcessLineageProbe : IProcessLineageProbe
{
	private const int ErrorNoMoreFiles = 18;
	private const int MaximumExecutablePathLength = 32768;
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const uint StillActive = 259;
	private const uint ToolhelpSnapshotProcesses = 0x00000002;

	private static class NativeMethods
	{
		[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		internal static extern SafeFileHandle CreateToolhelp32Snapshot(
			uint flags,
			uint processId);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "Process32FirstW",
			CharSet = CharSet.Unicode,
			ExactSpelling = true,
			SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool Process32First(
			SafeFileHandle snapshot,
			ref ProcessEntry entry);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "Process32NextW",
			CharSet = CharSet.Unicode,
			ExactSpelling = true,
			SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool Process32Next(
			SafeFileHandle snapshot,
			ref ProcessEntry entry);

		[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		internal static extern SafeProcessHandle OpenProcess(
			uint desiredAccess,
			[MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
			int processId);

		[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetExitCodeProcess(
			SafeProcessHandle process,
			out uint exitCode);

		[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetProcessTimes(
			SafeProcessHandle process,
			out FileTime creationTime,
			out FileTime exitTime,
			out FileTime kernelTime,
			out FileTime userTime);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "QueryFullProcessImageNameW",
			CharSet = CharSet.Unicode,
			ExactSpelling = true,
			SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool QueryFullProcessImageName(
			SafeProcessHandle process,
			uint flags,
			StringBuilder executablePath,
			ref uint characterCount);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileTime
	{
		internal uint LowDateTime;
		internal uint HighDateTime;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry
	{
		internal uint Size;
		internal uint Usage;
		internal uint ProcessId;
		internal UIntPtr DefaultHeapId;
		internal uint ModuleId;
		internal uint Threads;
		internal uint ParentProcessId;
		internal int BasePriority;
		internal uint Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		internal string? ExecutableFile;
	}

	public ProcessLineage CaptureCurrent()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Process lineage inspection is only supported on Windows.");
		}

		int currentProcessId = Environment.ProcessId;
		int parentProcessId = GetParentProcessId(currentProcessId);
		ProcessIdentitySnapshot current = CaptureProcess(currentProcessId);
		ProcessIdentitySnapshot parent = CaptureProcess(parentProcessId);
		return new ProcessLineage(
			current.ExecutablePath,
			current.Identity,
			parent.ExecutablePath,
			parent.Identity);
	}

	private static ProcessIdentitySnapshot CaptureProcess(int processId)
	{
		using SafeProcessHandle process = NativeMethods.OpenProcess(
			ProcessQueryLimitedInformation,
			inheritHandle: false,
			processId);

		if (process.IsInvalid)
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to open process {processId} while capturing updater lineage.");
		}

		EnsureProcessIsRunning(process, processId);
		StringBuilder executablePath = new(MaximumExecutablePathLength);
		uint characterCount = MaximumExecutablePathLength;

		if (!NativeMethods.QueryFullProcessImageName(
				process,
				flags: 0,
				executablePath,
				ref characterCount))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to read the executable path for process {processId}.");
		}

		if (!NativeMethods.GetProcessTimes(
				process,
				out FileTime creationTime,
				out _,
				out _,
				out _))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to read the start time for process {processId}.");
		}

		EnsureProcessIsRunning(process, processId);
		long creationFileTime = ((long)creationTime.HighDateTime << 32) |
			creationTime.LowDateTime;
		long startTimeUtcTicks = DateTime.FromFileTimeUtc(creationFileTime).Ticks;
		return new ProcessIdentitySnapshot(
			Path.GetFullPath(executablePath.ToString()),
			new ExactProcessIdentity(processId, startTimeUtcTicks));
	}

	private static void EnsureProcessIsRunning(
		SafeProcessHandle process,
		int processId)
	{
		if (!NativeMethods.GetExitCodeProcess(process, out uint exitCode))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to inspect process {processId} while capturing updater lineage.");
		}

		if (exitCode != StillActive)
		{
			throw new InvalidOperationException(
				$"Process {processId} exited while updater lineage was captured.");
		}
	}

	private static int GetParentProcessId(int currentProcessId)
	{
		using SafeFileHandle snapshot = NativeMethods.CreateToolhelp32Snapshot(
			ToolhelpSnapshotProcesses,
			processId: 0);

		if (snapshot.IsInvalid)
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to enumerate processes while capturing updater lineage.");
		}

		ProcessEntry entry = new()
		{
			Size = (uint)Marshal.SizeOf<ProcessEntry>()
		};

		if (!NativeMethods.Process32First(snapshot, ref entry))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to read the process snapshot while capturing updater lineage.");
		}

		do
		{
			if (entry.ProcessId == (uint)currentProcessId)
			{
				if ((entry.ParentProcessId == 0) ||
					(entry.ParentProcessId > int.MaxValue))
				{
					throw new InvalidOperationException(
						$"Process {currentProcessId} has no valid direct parent.");
				}

				return (int)entry.ParentProcessId;
			}
		}
		while (NativeMethods.Process32Next(snapshot, ref entry));

		int error = Marshal.GetLastWin32Error();

		if (error != ErrorNoMoreFiles)
		{
			throw new Win32Exception(
				error,
				"Process enumeration failed while capturing updater lineage.");
		}

		throw new InvalidOperationException(
			$"Process {currentProcessId} was absent from the process snapshot.");
	}

	private sealed record ProcessIdentitySnapshot(
		string ExecutablePath,
		ExactProcessIdentity Identity);
}
