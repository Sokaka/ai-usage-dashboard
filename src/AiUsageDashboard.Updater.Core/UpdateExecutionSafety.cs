using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.Updater.Core;

public static class UpdateExecutionSafety
{
	private const uint TokenQuery = 0x0008;

	public static void EnsureNotElevated()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"The updater execution check requires Windows.");
		}

		if (!NativeMethods.OpenProcessToken(
				NativeMethods.GetCurrentProcess(),
				TokenQuery,
				out SafeAccessTokenHandle tokenHandle))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to inspect the updater process token.");
		}

		using (tokenHandle)
		{
			if (!NativeMethods.GetTokenInformation(
					tokenHandle,
					TokenInformationClass.TokenElevation,
					out TokenElevation elevation,
					Marshal.SizeOf<TokenElevation>(),
					out _))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to determine whether the updater is elevated.");
			}

			EnsureNotElevated(elevation.TokenIsElevated != 0);
		}
	}

	internal static void EnsureNotElevated(bool isElevated)
	{
		if (isElevated)
		{
			throw new InvalidOperationException(
				"The updater refuses elevated execution. Run it as the signed-in " +
				"Windows user without administrator elevation.");
		}
	}

	private enum TokenInformationClass
	{
		TokenElevation = 20
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenElevation
	{
		internal int TokenIsElevated;
	}

	private static class NativeMethods
	{
		[DllImport("kernel32.dll")]
		internal static extern IntPtr GetCurrentProcess();

		[DllImport("advapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool OpenProcessToken(
			IntPtr processHandle,
			uint desiredAccess,
			out SafeAccessTokenHandle tokenHandle);

		[DllImport("advapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetTokenInformation(
			SafeAccessTokenHandle tokenHandle,
			TokenInformationClass tokenInformationClass,
			out TokenElevation tokenInformation,
			int tokenInformationLength,
			out int returnLength);
	}
}
