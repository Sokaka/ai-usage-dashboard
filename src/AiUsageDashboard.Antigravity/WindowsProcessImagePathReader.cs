using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.AntigravitySpike;

internal static class WindowsProcessImagePathReader
{
	private static class NativeMethods
	{
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

	private const int ErrorInvalidParameter = 87;
	private const int MaximumExecutablePathLength = 32768;
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const uint ProcessImageNameWin32 = 0;
	private const uint StillActive = 259;

	// null 僅表示已確認退出；查詢失敗必須拋出，讓呼叫端保守阻擋。
	internal static string? Read(int processId)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
		// 只查 executable 路徑，不需 MainModule 所要求的 PROCESS_VM_READ。
		using SafeProcessHandle process = NativeMethods.OpenProcess(
			ProcessQueryLimitedInformation,
			inheritHandle: false,
			processId);

		if (process.IsInvalid)
		{
			int error = Marshal.GetLastWin32Error();

			if (error == ErrorInvalidParameter)
			{
				return null;
			}

			throw new Win32Exception(
				error,
				$"Unable to open process {processId} for a limited executable-path query.");
		}

		if (!NativeMethods.GetExitCodeProcess(process, out uint exitCode))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to check whether process {processId} exited before querying its executable path.");
		}

		if (exitCode != StillActive)
		{
			return null;
		}

		StringBuilder executablePath = new(MaximumExecutablePathLength);
		uint characterCount = MaximumExecutablePathLength;

		if (!NativeMethods.QueryFullProcessImageName(
			process,
			ProcessImageNameWin32,
			executablePath,
			ref characterCount))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				$"Unable to query the executable path of process {processId}.");
		}

		return executablePath.ToString();
	}
}
