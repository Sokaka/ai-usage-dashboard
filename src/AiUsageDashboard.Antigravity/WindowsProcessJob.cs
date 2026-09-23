using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed class WindowsProcessJob : IDisposable
{
	internal sealed class SafeKernelHandleLease : IDisposable
	{
		private SafeKernelHandle? _handle;

		internal IntPtr Value { get; }

		internal SafeKernelHandleLease(SafeKernelHandle handle)
		{
			_handle = handle;
			Value = handle.DangerousGetHandle();
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _handle, null)?.DangerousRelease();
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicAccountingInformation
	{
		internal long TotalUserTime;
		internal long TotalKernelTime;
		internal long ThisPeriodTotalUserTime;
		internal long ThisPeriodTotalKernelTime;
		internal uint TotalPageFaultCount;
		internal uint TotalProcesses;
		internal uint ActiveProcesses;
		internal uint TotalTerminatedProcesses;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
	{
		internal long PerProcessUserTimeLimit;
		internal long PerJobUserTimeLimit;
		internal uint LimitFlags;
		internal UIntPtr MinimumWorkingSetSize;
		internal UIntPtr MaximumWorkingSetSize;
		internal uint ActiveProcessLimit;
		internal UIntPtr Affinity;
		internal uint PriorityClass;
		internal uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		internal ulong ReadOperationCount;
		internal ulong WriteOperationCount;
		internal ulong OtherOperationCount;
		internal ulong ReadTransferCount;
		internal ulong WriteTransferCount;
		internal ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectExtendedLimitInformation
	{
		internal JobObjectBasicLimitInformation BasicLimitInformation;
		internal IoCounters IoInfo;
		internal UIntPtr ProcessMemoryLimit;
		internal UIntPtr JobMemoryLimit;
		internal UIntPtr PeakProcessMemoryUsed;
		internal UIntPtr PeakJobMemoryUsed;
	}

	private enum JobObjectInformationClass
	{
		BasicAccountingInformation = 1,
		ExtendedLimitInformation = 9
	}

	private static class NativeMethods
	{
		[DllImport(
			"kernel32.dll",
			EntryPoint = "CreateJobObjectW",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern IntPtr CreateJobObject(
			IntPtr jobAttributes,
			[MarshalAs(UnmanagedType.LPWStr)] string? name);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "IsProcessInJob",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool IsProcessInJob(
			SafeKernelHandle process,
			SafeKernelHandle job,
			[MarshalAs(UnmanagedType.Bool)] out bool result);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "OpenJobObjectW",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern IntPtr OpenJobObject(
			uint desiredAccess,
			[MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
			[MarshalAs(UnmanagedType.LPWStr)] string name);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "QueryInformationJobObject",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool QueryInformationJobObject(
			SafeKernelHandle job,
			JobObjectInformationClass informationClass,
			out JobObjectBasicAccountingInformation information,
			uint informationLength,
			out uint returnLength);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "SetInformationJobObject",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool SetInformationJobObject(
			SafeKernelHandle job,
			JobObjectInformationClass informationClass,
			ref JobObjectExtendedLimitInformation information,
			uint informationLength);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "TerminateJobObject",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool TerminateJobObject(
			SafeKernelHandle job,
			uint exitCode);
	}

	private const int ErrorAlreadyExists = 183;
	private const int ErrorFileNotFound = 2;
	private const uint JobObjectLimitActiveProcess = 0x00000008;
	private const uint JobObjectLimitKillOnJobClose = 0x00002000;
	private const uint JobObjectQuery = 0x0004;
	private const uint JobObjectTerminate = 0x0008;
	private static readonly TimeSpan EmptyPollInterval = TimeSpan.FromMilliseconds(20);
	private readonly SafeKernelHandle _handle;
	private int _isDisposed;

	private WindowsProcessJob(SafeKernelHandle handle)
	{
		_handle = handle;
	}

	internal static WindowsProcessJob CreateKillOnClose()
	{
		return CreateKillOnClose(name: null);
	}

	internal static WindowsProcessJob CreateKillOnClose(string? name)
	{
		return CreateKillOnClose(name, activeProcessLimit: null);
	}

	internal static WindowsProcessJob CreateKillOnClose(
		string? name,
		uint activeProcessLimit)
	{
		if (activeProcessLimit == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(activeProcessLimit));
		}

		return CreateKillOnClose(name, (uint?)activeProcessLimit);
	}

	private static WindowsProcessJob CreateKillOnClose(
		string? name,
		uint? activeProcessLimit)
	{
		if (name is not null)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(name);
		}

		IntPtr rawHandle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
		int createError = Marshal.GetLastWin32Error();

		if ((rawHandle == IntPtr.Zero) || (rawHandle == new IntPtr(-1)))
		{
			throw new Win32Exception(
				createError,
				"Unable to create the process Job Object.");
		}

		SafeKernelHandle handle = new(rawHandle);

		try
		{
			if (name is not null && createError == ErrorAlreadyExists)
			{
				throw new InvalidOperationException(
					"The named process Job Object already exists.");
			}

			JobObjectExtendedLimitInformation information = default;
			information.BasicLimitInformation.LimitFlags =
				JobObjectLimitKillOnJobClose;

			if (activeProcessLimit.HasValue)
			{
				information.BasicLimitInformation.LimitFlags |=
					JobObjectLimitActiveProcess;
				information.BasicLimitInformation.ActiveProcessLimit =
					activeProcessLimit.Value;
			}

			if (!NativeMethods.SetInformationJobObject(
				handle,
				JobObjectInformationClass.ExtendedLimitInformation,
				ref information,
				checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to configure kill-on-close for the process Job Object.");
			}

			return new WindowsProcessJob(handle);
		}
		catch
		{
			handle.Dispose();
			throw;
		}
	}

	internal SafeKernelHandleLease AcquireHandleLease()
	{
		ThrowIfDisposed();

		if (_handle.IsInvalid || _handle.IsClosed)
		{
			throw new InvalidOperationException(
				"The process Job Object handle is not open and valid.");
		}

		bool referenceAdded = false;

		try
		{
			_handle.DangerousAddRef(ref referenceAdded);

			if (!referenceAdded)
			{
				throw new InvalidOperationException(
					"Unable to retain the process Job Object handle.");
			}

			return new SafeKernelHandleLease(_handle);
		}
		catch
		{
			if (referenceAdded)
			{
				_handle.DangerousRelease();
			}

			throw;
		}
	}

	internal void VerifyMembership(SafeKernelHandle process)
	{
		ThrowIfDisposed();
		ValidateProcessHandle(process);

		if (!NativeMethods.IsProcessInJob(process, _handle, out bool isInJob))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to verify exact Job Object membership.");
		}

		if (!isInJob)
		{
			throw new InvalidOperationException(
				"The process is not a member of the expected Job Object.");
		}
	}

	internal uint GetActiveProcessCount()
	{
		ThrowIfDisposed();

		if (!NativeMethods.QueryInformationJobObject(
			_handle,
			JobObjectInformationClass.BasicAccountingInformation,
			out JobObjectBasicAccountingInformation information,
			checked((uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>()),
			out _))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to query active processes in the Job Object.");
		}

		return information.ActiveProcesses;
	}

	internal void Terminate(uint exitCode)
	{
		ThrowIfDisposed();

		if (!NativeMethods.TerminateJobObject(_handle, exitCode))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to terminate the process Job Object.");
		}
	}

	internal async Task<bool> WaitForEmptyAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (GetActiveProcessCount() == 0)
			{
				return true;
			}

			TimeSpan remaining = timeout - stopwatch.Elapsed;

			if (remaining <= TimeSpan.Zero)
			{
				return false;
			}

			await Task.Delay(
				remaining < EmptyPollInterval ? remaining : EmptyPollInterval,
				cancellationToken);
		}
	}

	internal async Task WaitForEmptyAsync(
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (GetActiveProcessCount() == 0)
			{
				return;
			}

			await Task.Delay(EmptyPollInterval, cancellationToken);
		}
	}

	internal static bool TryOpenExisting(
		string name,
		out WindowsProcessJob? job)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		IntPtr rawHandle = NativeMethods.OpenJobObject(
			JobObjectQuery | JobObjectTerminate,
			inheritHandle: false,
			name);

		if ((rawHandle == IntPtr.Zero) || (rawHandle == new IntPtr(-1)))
		{
			int error = Marshal.GetLastWin32Error();

			if (error == ErrorFileNotFound)
			{
				job = null;
				return false;
			}

			throw new Win32Exception(
				error,
				"Unable to open the named process Job Object.");
		}

		job = new WindowsProcessJob(new SafeKernelHandle(rawHandle));
		return true;
	}

	internal Task<bool> WaitForPositiveEmptyConfirmationAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		return WaitForPositiveEmptyConfirmationCoreAsync(
			GetActiveProcessCount,
			timeout,
			cancellationToken);
	}

	internal static async Task<bool> WaitForPositiveEmptyConfirmationCoreAsync(
		Func<uint> getActiveProcessCount,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(getActiveProcessCount);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				if (getActiveProcessCount() == 0)
				{
					return true;
				}
			}
			catch
			{
				// Query failures still are not positive quiescence, but callers
				// using this overload have a bounded final kill-on-close fallback.
			}

			TimeSpan remaining = timeout - stopwatch.Elapsed;

			if (remaining <= TimeSpan.Zero)
			{
				return false;
			}

			await Task.Delay(
				remaining < EmptyPollInterval ? remaining : EmptyPollInterval,
				cancellationToken);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
		{
			return;
		}

		_handle.Dispose();
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
	}

	private static void ValidateProcessHandle(SafeKernelHandle process)
	{
		ArgumentNullException.ThrowIfNull(process);

		if (process.IsInvalid || process.IsClosed)
		{
			throw new ArgumentException(
				"The process handle must be open and valid.",
				nameof(process));
		}
	}
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
	private static class NativeMethods
	{
		[DllImport(
			"kernel32.dll",
			EntryPoint = "CloseHandle",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CloseHandle(IntPtr handle);
	}

	internal SafeKernelHandle()
		: base(ownsHandle: true)
	{
	}

	internal SafeKernelHandle(IntPtr handle)
		: base(ownsHandle: true)
	{
		SetHandle(handle);
	}

	protected override bool ReleaseHandle()
	{
		return NativeMethods.CloseHandle(handle);
	}
}
