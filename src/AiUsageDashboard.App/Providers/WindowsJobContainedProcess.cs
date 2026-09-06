using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.App.Providers;

internal sealed class WindowsJobContainedProcessLaunchException :
	InvalidOperationException
{
	internal Task ContainmentCompletion { get; }
	internal bool IsLaunchBlocked { get; }
	internal bool IsTreeEmptyConfirmed { get; }

	internal WindowsJobContainedProcessLaunchException(
		string message,
		bool isLaunchBlocked,
		bool isTreeEmptyConfirmed,
		Task containmentCompletion,
		Exception innerException)
		: base(message, innerException)
	{
		ContainmentCompletion = containmentCompletion ??
			throw new ArgumentNullException(nameof(containmentCompletion));
		IsLaunchBlocked = isLaunchBlocked;
		IsTreeEmptyConfirmed = isTreeEmptyConfirmed;
	}

	internal WindowsJobContainedProcessLaunchException(
		string message,
		bool isLaunchBlocked,
		bool isTreeEmptyConfirmed,
		Exception innerException)
		: this(
			message,
			isLaunchBlocked,
			isTreeEmptyConfirmed,
			Task.CompletedTask,
			innerException)
	{
	}
}

internal sealed class WindowsJobContainedProcess : IDisposable
{
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		internal IntPtr Process;
		internal IntPtr Thread;
		internal uint ProcessId;
		internal uint ThreadId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		internal uint Size;
		internal IntPtr Reserved;
		internal IntPtr Desktop;
		internal IntPtr Title;
		internal uint X;
		internal uint Y;
		internal uint XSize;
		internal uint YSize;
		internal uint XCountCharacters;
		internal uint YCountCharacters;
		internal uint FillAttribute;
		internal uint Flags;
		internal ushort ShowWindow;
		internal ushort ReservedByteCount;
		internal IntPtr ReservedBytes;
		internal IntPtr StandardInput;
		internal IntPtr StandardOutput;
		internal IntPtr StandardError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfoEx
	{
		internal StartupInfo StartupInfo;
		internal IntPtr AttributeList;
	}

	private sealed class ProcessCreationAttributeList : IDisposable
	{
		private IntPtr _handleArray;
		private IntPtr _jobHandleArray;
		private WindowsProcessJob.SafeKernelHandleLease? _jobHandleLease;
		private IntPtr _pointer;

		internal IntPtr Pointer => _pointer;

		private ProcessCreationAttributeList(
			IntPtr pointer,
			IntPtr handleArray,
			IntPtr jobHandleArray,
			WindowsProcessJob.SafeKernelHandleLease jobHandleLease)
		{
			_pointer = pointer;
			_handleArray = handleArray;
			_jobHandleArray = jobHandleArray;
			_jobHandleLease = jobHandleLease;
		}

		internal static ProcessCreationAttributeList Create(
			IReadOnlyList<SafeFileHandle> handles,
			WindowsProcessJob job)
		{
			ArgumentNullException.ThrowIfNull(handles);
			ArgumentNullException.ThrowIfNull(job);

			int attributeCount = handles.Count == 0 ? 1 : 2;
			nuint requiredBytes = 0;
			_ = NativeMethods.InitializeProcThreadAttributeList(
				IntPtr.Zero,
				attributeCount,
				0,
				ref requiredBytes);

			if (requiredBytes == 0)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to determine the contained process attribute-list size.");
			}

			IntPtr pointer = IntPtr.Zero;
			IntPtr handleArray = IntPtr.Zero;
			IntPtr jobHandleArray = IntPtr.Zero;
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease = null;
			bool isInitialized = false;

			try
			{
				pointer = Marshal.AllocHGlobal(checked((int)requiredBytes));
				if (handles.Count > 0)
				{
					handleArray = Marshal.AllocHGlobal(
						checked(handles.Count * IntPtr.Size));
				}
				jobHandleArray = Marshal.AllocHGlobal(IntPtr.Size);

				if (!NativeMethods.InitializeProcThreadAttributeList(
					pointer,
					attributeCount,
					0,
					ref requiredBytes))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to initialize the contained process attribute list.");
				}

				isInitialized = true;

				for (int index = 0; index < handles.Count; index++)
				{
					SafeFileHandle handle = handles[index];

					if (handle.IsInvalid || handle.IsClosed)
					{
						throw new ArgumentException(
							"Inherited process handles must be open and valid.",
							nameof(handles));
					}

					Marshal.WriteIntPtr(
						handleArray,
						checked(index * IntPtr.Size),
						handle.DangerousGetHandle());
				}

				if ((handles.Count > 0) &&
					!NativeMethods.UpdateProcThreadAttribute(
						pointer,
						0,
						ProcThreadAttributeHandleList,
						handleArray,
						checked((nuint)(handles.Count * IntPtr.Size)),
						IntPtr.Zero,
						IntPtr.Zero))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to restrict inherited process handles.");
				}

				jobHandleLease = job.AcquireHandleLease();
				Marshal.WriteIntPtr(jobHandleArray, jobHandleLease.Value);

				if (!NativeMethods.UpdateProcThreadAttribute(
					pointer,
					0,
					ProcThreadAttributeJobList,
					jobHandleArray,
					checked((nuint)IntPtr.Size),
					IntPtr.Zero,
					IntPtr.Zero))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to contain the process at creation.");
				}

				GC.KeepAlive(handles);
				GC.KeepAlive(job);
				return new ProcessCreationAttributeList(
					pointer,
					handleArray,
					jobHandleArray,
					jobHandleLease);
			}
			catch
			{
				if (isInitialized)
				{
					NativeMethods.DeleteProcThreadAttributeList(pointer);
				}

				if (jobHandleArray != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(jobHandleArray);
				}

				jobHandleLease?.Dispose();

				if (handleArray != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(handleArray);
				}

				if (pointer != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(pointer);
				}

				throw;
			}
		}

		public void Dispose()
		{
			IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
			IntPtr handleArray = Interlocked.Exchange(
				ref _handleArray,
				IntPtr.Zero);
			IntPtr jobHandleArray = Interlocked.Exchange(
				ref _jobHandleArray,
				IntPtr.Zero);
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease =
				Interlocked.Exchange(ref _jobHandleLease, null);

			if (pointer != IntPtr.Zero)
			{
				NativeMethods.DeleteProcThreadAttributeList(pointer);
				Marshal.FreeHGlobal(pointer);
			}

			if (handleArray != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(handleArray);
			}

			if (jobHandleArray != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(jobHandleArray);
			}

			jobHandleLease?.Dispose();
		}
	}

	private static class NativeMethods
	{
		[DllImport(
			"kernel32.dll",
			CharSet = CharSet.Unicode,
			EntryPoint = "CreateProcessW",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CreateProcess(
			[MarshalAs(UnmanagedType.LPWStr)] string applicationName,
			[In, Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder commandLine,
			IntPtr processAttributes,
			IntPtr threadAttributes,
			[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
			uint creationFlags,
			IntPtr environment,
			[MarshalAs(UnmanagedType.LPWStr)] string currentDirectory,
			ref StartupInfoEx startupInfo,
			out ProcessInformation processInformation);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "DeleteProcThreadAttributeList",
			ExactSpelling = true)]
		internal static extern void DeleteProcThreadAttributeList(
			IntPtr attributeList);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "GetExitCodeProcess",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetExitCodeProcess(
			SafeKernelHandle process,
			out uint exitCode);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "InitializeProcThreadAttributeList",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool InitializeProcThreadAttributeList(
			IntPtr attributeList,
			int attributeCount,
			uint flags,
			ref nuint size);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "ResumeThread",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern uint ResumeThread(SafeKernelHandle thread);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "TerminateProcess",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool TerminateProcess(
			SafeKernelHandle process,
			uint exitCode);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "UpdateProcThreadAttribute",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool UpdateProcThreadAttribute(
			IntPtr attributeList,
			uint flags,
			nuint attribute,
			IntPtr value,
			nuint size,
			IntPtr previousValue,
			IntPtr returnSize);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "WaitForSingleObject",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern uint WaitForSingleObject(
			SafeKernelHandle handle,
			uint milliseconds);
	}

	private const int MaximumCommandLineCharacters = 32767;
	private const int MaximumEnvironmentBlockCharacters = 32767;
	private const uint CreateNewConsole = 0x00000010;
	private const uint CreateNoWindow = 0x08000000;
	private const uint CreateSuspended = 0x00000004;
	private const uint CreateUnicodeEnvironment = 0x00000400;
	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const uint ForcedExitCode = 0xC0DE0004;
	private const uint ResumeThreadFailed = 0xFFFFFFFF;
	private const uint StartfUseStdHandles = 0x00000100;
	private const uint WaitFailed = 0xFFFFFFFF;
	private const uint WaitObject0 = 0x00000000;
	private const uint WaitTimeout = 0x00000102;
	private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;
	private static readonly nuint ProcThreadAttributeJobList = 0x0002000D;
	private static readonly TimeSpan FailedStartCleanupTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan ProcessPollInterval =
		TimeSpan.FromMilliseconds(20);
	private FileStream? _standardError;
	private FileStream? _standardOutput;
	private WindowsProcessJob? _job;
	private SafeKernelHandle? _processHandle;
	private int _isDisposed;

	internal Stream StandardError =>
		Volatile.Read(ref _standardError) ??
		throw new ObjectDisposedException(GetType().Name);

	internal Stream StandardOutput =>
		Volatile.Read(ref _standardOutput) ??
		throw new ObjectDisposedException(GetType().Name);

	private WindowsJobContainedProcess(
		WindowsProcessJob job,
		SafeKernelHandle processHandle,
		FileStream standardOutput,
		FileStream standardError)
	{
		_job = job;
		_processHandle = processHandle;
		_standardOutput = standardOutput;
		_standardError = standardError;
	}

	private WindowsJobContainedProcess(
		WindowsProcessJob job,
		SafeKernelHandle processHandle)
	{
		_job = job;
		_processHandle = processHandle;
	}

	internal static WindowsJobContainedProcess Start(
		ProcessStartInfo startInfo,
		Action throwIfLaunchBlocked)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentNullException.ThrowIfNull(throwIfLaunchBlocked);

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Job-contained process launch requires Windows.");
		}

		ValidateStartInfo(startInfo);
		WindowsProcessJob? job = null;
		SafeFileHandle? inputReadHandle = null;
		SafeFileHandle? inputWriteHandle = null;
		SafeFileHandle? outputReadHandle = null;
		SafeFileHandle? outputWriteHandle = null;
		SafeFileHandle? errorReadHandle = null;
		SafeFileHandle? errorWriteHandle = null;
		ProcessCreationAttributeList? processAttributes = null;
		SafeKernelHandle? processHandle = null;
		SafeKernelHandle? threadHandle = null;
		FileStream? outputStream = null;
		FileStream? errorStream = null;
		IntPtr environment = IntPtr.Zero;
		bool isJobMembershipVerified = false;
		bool isLaunchBlocked = false;
		bool ownershipTransferred = false;

		try
		{
			job = WindowsProcessJob.CreateKillOnClose();
			WindowsGrokAcpProcessFactory.CreateRedirectedPipe(
				out inputReadHandle,
				out inputWriteHandle,
				parentReads: false);
			WindowsGrokAcpProcessFactory.CreateRedirectedPipe(
				out outputReadHandle,
				out outputWriteHandle,
				parentReads: true);
			WindowsGrokAcpProcessFactory.CreateRedirectedPipe(
				out errorReadHandle,
				out errorWriteHandle,
				parentReads: true);
			processAttributes = ProcessCreationAttributeList.Create(
				[inputReadHandle, outputWriteHandle, errorWriteHandle],
				job);
			environment = Marshal.StringToHGlobalUni(
				BuildEnvironmentBlock(startInfo));
			StartupInfoEx startupInfo = default;
			startupInfo.StartupInfo.Size =
				checked((uint)Marshal.SizeOf<StartupInfoEx>());
			startupInfo.StartupInfo.Flags = StartfUseStdHandles;
			startupInfo.StartupInfo.StandardInput =
				inputReadHandle.DangerousGetHandle();
			startupInfo.StartupInfo.StandardOutput =
				outputWriteHandle.DangerousGetHandle();
			startupInfo.StartupInfo.StandardError =
				errorWriteHandle.DangerousGetHandle();
			startupInfo.AttributeList = processAttributes.Pointer;
			StringBuilder commandLine = BuildCommandLine(startInfo);
			InvokeLaunchGuard(throwIfLaunchBlocked, ref isLaunchBlocked);

			if (!NativeMethods.CreateProcess(
				startInfo.FileName,
				commandLine,
				IntPtr.Zero,
				IntPtr.Zero,
				inheritHandles: true,
				CreateNoWindow |
					CreateSuspended |
					CreateUnicodeEnvironment |
					ExtendedStartupInfoPresent,
				environment,
				startInfo.WorkingDirectory,
				ref startupInfo,
				out ProcessInformation processInformation))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to create the suspended contained process.");
			}

			GC.KeepAlive(inputReadHandle);
			GC.KeepAlive(outputWriteHandle);
			GC.KeepAlive(errorWriteHandle);
			GC.KeepAlive(job);
			processHandle = new SafeKernelHandle(processInformation.Process);
			threadHandle = new SafeKernelHandle(processInformation.Thread);
			job.VerifyMembership(processHandle);
			isJobMembershipVerified = true;

			inputReadHandle.Dispose();
			inputReadHandle = null;
			inputWriteHandle.Dispose();
			inputWriteHandle = null;
			outputWriteHandle.Dispose();
			outputWriteHandle = null;
			errorWriteHandle.Dispose();
			errorWriteHandle = null;
			outputStream = new FileStream(
				outputReadHandle,
				FileAccess.Read,
				bufferSize: 4096,
				isAsync: true);
			outputReadHandle = null;
			errorStream = new FileStream(
				errorReadHandle,
				FileAccess.Read,
				bufferSize: 4096,
				isAsync: true);
			errorReadHandle = null;
			InvokeLaunchGuard(throwIfLaunchBlocked, ref isLaunchBlocked);
			uint previousSuspendCount = NativeMethods.ResumeThread(threadHandle);

			if (previousSuspendCount == ResumeThreadFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to resume the contained process.");
			}

			if (previousSuspendCount != 1)
			{
				throw new InvalidOperationException(
					$"The contained process had an unexpected suspend count of {previousSuspendCount}.");
			}

			WindowsJobContainedProcess result = new(
				job,
				processHandle,
				outputStream,
				errorStream);
			job = null;
			processHandle = null;
			outputStream = null;
			errorStream = null;
			ownershipTransferred = true;
			return result;
		}
		catch (Exception exception)
		{
			bool isTreeEmptyConfirmed = TryTerminateFailedStart(
				job,
				processHandle,
				isJobMembershipVerified,
				FailedStartCleanupTimeout);
			Task containmentCompletion = Task.CompletedTask;
			if (!isTreeEmptyConfirmed)
			{
				containmentCompletion = RetainFailedStartContainmentUntilEmptyAsync(
					job,
					processHandle,
					isJobMembershipVerified);
				job = null;
				processHandle = null;
			}

			throw new WindowsJobContainedProcessLaunchException(
				"The contained process could not be started safely.",
				isLaunchBlocked,
				isTreeEmptyConfirmed,
				containmentCompletion,
				exception);
		}
		finally
		{
			threadHandle?.Dispose();
			processAttributes?.Dispose();

			if (environment != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(environment);
			}

			inputReadHandle?.Dispose();
			inputWriteHandle?.Dispose();
			outputReadHandle?.Dispose();
			outputWriteHandle?.Dispose();
			errorReadHandle?.Dispose();
			errorWriteHandle?.Dispose();

			if (!ownershipTransferred)
			{
				TryDispose(outputStream);
				TryDispose(errorStream);
				processHandle?.Dispose();
				job?.Dispose();
			}
		}
	}

	internal static WindowsJobContainedProcess StartInteractive(
		ProcessStartInfo startInfo,
		Action throwIfLaunchBlocked)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentNullException.ThrowIfNull(throwIfLaunchBlocked);

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Job-contained process launch requires Windows.");
		}

		ValidateStartInfo(startInfo);
		if (startInfo.CreateNoWindow ||
			startInfo.RedirectStandardInput ||
			startInfo.RedirectStandardOutput ||
			startInfo.RedirectStandardError)
		{
			throw new ArgumentException(
				"Interactive contained process launch requires a visible console without redirected standard handles.",
				nameof(startInfo));
		}

		WindowsProcessJob? job = null;
		ProcessCreationAttributeList? processAttributes = null;
		SafeKernelHandle? processHandle = null;
		SafeKernelHandle? threadHandle = null;
		IntPtr environment = IntPtr.Zero;
		bool isJobMembershipVerified = false;
		bool isLaunchBlocked = false;
		bool ownershipTransferred = false;

		try
		{
			job = WindowsProcessJob.CreateKillOnClose();
			processAttributes = ProcessCreationAttributeList.Create(
				Array.Empty<SafeFileHandle>(),
				job);
			environment = Marshal.StringToHGlobalUni(
				BuildEnvironmentBlock(startInfo));
			StartupInfoEx startupInfo = default;
			startupInfo.StartupInfo.Size =
				checked((uint)Marshal.SizeOf<StartupInfoEx>());
			startupInfo.AttributeList = processAttributes.Pointer;
			StringBuilder commandLine = BuildCommandLine(startInfo);
			InvokeLaunchGuard(throwIfLaunchBlocked, ref isLaunchBlocked);

			if (!NativeMethods.CreateProcess(
				startInfo.FileName,
				commandLine,
				IntPtr.Zero,
				IntPtr.Zero,
				inheritHandles: false,
				CreateNewConsole |
					CreateSuspended |
					CreateUnicodeEnvironment |
					ExtendedStartupInfoPresent,
				environment,
				startInfo.WorkingDirectory,
				ref startupInfo,
				out ProcessInformation processInformation))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to create the suspended interactive process.");
			}

			GC.KeepAlive(job);
			processHandle = new SafeKernelHandle(processInformation.Process);
			threadHandle = new SafeKernelHandle(processInformation.Thread);
			job.VerifyMembership(processHandle);
			isJobMembershipVerified = true;
			InvokeLaunchGuard(throwIfLaunchBlocked, ref isLaunchBlocked);
			uint previousSuspendCount = NativeMethods.ResumeThread(threadHandle);

			if (previousSuspendCount == ResumeThreadFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to resume the contained interactive process.");
			}

			if (previousSuspendCount != 1)
			{
				throw new InvalidOperationException(
					$"The contained interactive process had an unexpected suspend count of {previousSuspendCount}.");
			}

			WindowsJobContainedProcess result = new(job, processHandle);
			job = null;
			processHandle = null;
			ownershipTransferred = true;
			return result;
		}
		catch (Exception exception)
		{
			bool isTreeEmptyConfirmed = TryTerminateFailedStart(
				job,
				processHandle,
				isJobMembershipVerified,
				FailedStartCleanupTimeout);
			Task containmentCompletion = Task.CompletedTask;
			if (!isTreeEmptyConfirmed)
			{
				containmentCompletion = RetainFailedStartContainmentUntilEmptyAsync(
					job,
					processHandle,
					isJobMembershipVerified);
				job = null;
				processHandle = null;
			}

			throw new WindowsJobContainedProcessLaunchException(
				"The interactive process could not be started safely.",
				isLaunchBlocked,
				isTreeEmptyConfirmed,
				containmentCompletion,
				exception);
		}
		finally
		{
			threadHandle?.Dispose();
			processAttributes?.Dispose();

			if (environment != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(environment);
			}

			if (!ownershipTransferred)
			{
				processHandle?.Dispose();
				job?.Dispose();
			}
		}
	}

	internal async Task<int> WaitForExitAsync(
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
		SafeKernelHandle processHandle = Volatile.Read(ref _processHandle) ??
			throw new ObjectDisposedException(GetType().Name);

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			uint waitResult = NativeMethods.WaitForSingleObject(processHandle, 0);

			if (waitResult == WaitObject0)
			{
				if (!NativeMethods.GetExitCodeProcess(
					processHandle,
					out uint exitCode))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to read the contained process exit code.");
				}

				return unchecked((int)exitCode);
			}

			if (waitResult == WaitFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to wait for the contained process.");
			}

			if (waitResult != WaitTimeout)
			{
				throw new InvalidOperationException(
					$"The contained process wait returned unexpected status 0x{waitResult:X8}.");
			}

			await Task.Delay(ProcessPollInterval, cancellationToken);
		}
	}

	internal bool TryGetExitCode(out int exitCode)
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
		SafeKernelHandle processHandle = Volatile.Read(ref _processHandle) ??
			throw new ObjectDisposedException(GetType().Name);
		uint waitResult = NativeMethods.WaitForSingleObject(processHandle, 0);

		if (waitResult == WaitTimeout)
		{
			exitCode = default;
			return false;
		}

		if (waitResult == WaitFailed)
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to query the contained process exit state.");
		}

		if (waitResult != WaitObject0)
		{
			throw new InvalidOperationException(
				$"The contained process wait returned unexpected status 0x{waitResult:X8}.");
		}

		if (!NativeMethods.GetExitCodeProcess(processHandle, out uint rawExitCode))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to read the contained process exit code.");
		}

		exitCode = unchecked((int)rawExitCode);
		return true;
	}

	internal void TerminateTree()
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
		WindowsProcessJob job = Volatile.Read(ref _job) ??
			throw new ObjectDisposedException(GetType().Name);
		job.Terminate(ForcedExitCode);
	}

	internal async Task TerminateTreeAndConfirmEmptyAsync(TimeSpan timeout)
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		WindowsProcessJob job = Volatile.Read(ref _job) ??
			throw new ObjectDisposedException(GetType().Name);
		Exception? terminationFailure = null;

		try
		{
			job.Terminate(ForcedExitCode);
		}
		catch (Exception exception)
		{
			terminationFailure = exception;
		}

		bool isEmpty = await job.WaitForPositiveEmptyConfirmationAsync(
			timeout,
			CancellationToken.None);

		if (!isEmpty)
		{
			throw new TimeoutException(
				"The contained process tree did not report positive quiescence.",
				terminationFailure);
		}

	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
		{
			return;
		}

		TryDispose(Interlocked.Exchange(ref _standardOutput, null));
		TryDispose(Interlocked.Exchange(ref _standardError, null));
		Interlocked.Exchange(ref _processHandle, null)?.Dispose();
		Interlocked.Exchange(ref _job, null)?.Dispose();
	}

	private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
	{
		StringBuilder block = new();

		foreach ((string name, string? value) in startInfo.Environment
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (string.IsNullOrWhiteSpace(name) ||
				name.Contains('=') ||
				name.Contains('\0') ||
				(value is null) ||
				value.Contains('\0'))
			{
				throw new InvalidOperationException(
					"The contained process environment contains an invalid entry.");
			}

			block.Append(name);
			block.Append('=');
			block.Append(value);
			block.Append('\0');
		}

		if (block.Length == 0)
		{
			block.Append('\0');
		}

		block.Append('\0');

		if (block.Length > MaximumEnvironmentBlockCharacters)
		{
			throw new InvalidOperationException(
				"The contained process environment exceeds the Windows process limit.");
		}

		return block.ToString();
	}

	private static StringBuilder BuildCommandLine(ProcessStartInfo startInfo)
	{
		StringBuilder commandLine = new();
		AppendCommandLineArgument(commandLine, startInfo.FileName);

		foreach (string argument in startInfo.ArgumentList)
		{
			AppendCommandLineArgument(commandLine, argument);
		}

		if (commandLine.Length >= MaximumCommandLineCharacters)
		{
			throw new InvalidOperationException(
				"The contained process command line exceeds the Windows process limit.");
		}

		return commandLine;
	}

	private static void AppendCommandLineArgument(
		StringBuilder commandLine,
		string argument)
	{
		if (argument.Contains('\0'))
		{
			throw new ArgumentException(
				"Contained process arguments must not contain NUL.",
				nameof(argument));
		}

		if (commandLine.Length > 0)
		{
			commandLine.Append(' ');
		}

		if ((argument.Length > 0) &&
			!argument.Any(character =>
				char.IsWhiteSpace(character) || (character == '"')))
		{
			commandLine.Append(argument);
			return;
		}

		commandLine.Append('"');
		int backslashCount = 0;

		foreach (char character in argument)
		{
			if (character == '\\')
			{
				backslashCount++;
				continue;
			}

			if (character == '"')
			{
				commandLine.Append('\\', checked((backslashCount * 2) + 1));
				commandLine.Append('"');
				backslashCount = 0;
				continue;
			}

			commandLine.Append('\\', backslashCount);
			commandLine.Append(character);
			backslashCount = 0;
		}

		commandLine.Append('\\', checked(backslashCount * 2));
		commandLine.Append('"');
	}

	private static bool TryTerminateAndConfirmEmpty(
		WindowsProcessJob job,
		TimeSpan timeout)
	{
		try
		{
			try
			{
				job.Terminate(ForcedExitCode);
			}
			catch
			{
				// A positive zero-process query remains authoritative if the job
				// already became empty before termination was requested.
			}

			return job.WaitForPositiveEmptyConfirmationAsync(
					timeout,
					CancellationToken.None)
				.GetAwaiter()
				.GetResult();
		}
		catch
		{
			return false;
		}
	}

	private static void InvokeLaunchGuard(
		Action throwIfLaunchBlocked,
		ref bool isLaunchBlocked)
	{
		try
		{
			throwIfLaunchBlocked();
		}
		catch
		{
			isLaunchBlocked = true;
			throw;
		}
	}

	private static bool TryTerminateFailedStart(
		WindowsProcessJob? job,
		SafeKernelHandle? processHandle,
		bool isJobMembershipVerified,
		TimeSpan timeout)
	{
		try
		{
			if ((processHandle is not null) && !isJobMembershipVerified)
			{
				_ = NativeMethods.TerminateProcess(
					processHandle,
					ForcedExitCode);
			}

			bool isJobEmpty = job is null ||
				TryTerminateAndConfirmEmpty(job, timeout);
			bool hasProcessExited = processHandle is null ||
				WaitForPositiveProcessExit(processHandle, timeout);
			return isJobEmpty && hasProcessExited;
		}
		catch
		{
			return false;
		}
	}

	private static Task RetainFailedStartContainmentUntilEmptyAsync(
		WindowsProcessJob? job,
		SafeKernelHandle? processHandle,
		bool isJobMembershipVerified)
	{
		return Task.Run(async () =>
		{
			TimeSpan retryDelay = TimeSpan.FromMilliseconds(100);
			try
			{
				while (true)
				{
					await Task.Delay(retryDelay).ConfigureAwait(false);
					if (TryTerminateFailedStart(
							job,
							processHandle,
							isJobMembershipVerified,
							FailedStartCleanupTimeout))
					{
						return;
					}

					retryDelay = TimeSpan.FromMilliseconds(Math.Min(
						retryDelay.TotalMilliseconds * 2,
						5000));
				}
			}
			finally
			{
				processHandle?.Dispose();
				job?.Dispose();
			}
		});
	}

	private static bool WaitForPositiveProcessExit(
		SafeKernelHandle processHandle,
		TimeSpan timeout)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			try
			{
				if (NativeMethods.WaitForSingleObject(
						processHandle,
						0) == WaitObject0)
				{
					return true;
				}
			}
			catch
			{
				// Query failures are not positive exit confirmation.
			}

			TimeSpan remaining = timeout - stopwatch.Elapsed;

			if (remaining <= TimeSpan.Zero)
			{
				return false;
			}

			Thread.Sleep(
				remaining < ProcessPollInterval
					? remaining
					: ProcessPollInterval);
		}
	}

	private static void TryDispose(IDisposable? disposable)
	{
		try
		{
			disposable?.Dispose();
		}
		catch
		{
			// Closing the Job still provides the final kill-on-close barrier.
		}
	}

	private static void ValidateStartInfo(ProcessStartInfo startInfo)
	{
		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				startInfo.FileName,
				out string executablePath) ||
			!string.Equals(
				executablePath,
				startInfo.FileName,
				StringComparison.OrdinalIgnoreCase) ||
			!File.Exists(executablePath) ||
			!Path.IsPathFullyQualified(startInfo.WorkingDirectory) ||
			!Directory.Exists(startInfo.WorkingDirectory) ||
			startInfo.UseShellExecute ||
			!string.IsNullOrEmpty(startInfo.Arguments))
		{
			throw new ArgumentException(
				"The contained process requires an existing canonical local executable, an existing absolute working directory, ArgumentList, and UseShellExecute=false.",
				nameof(startInfo));
		}
	}
}
