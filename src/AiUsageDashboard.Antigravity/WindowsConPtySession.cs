using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed class WindowsConPtySession : IConPtySession
{
	[StructLayout(LayoutKind.Sequential)]
	private struct Coord
	{
		internal short X;
		internal short Y;
	}

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

	private sealed class ProcessWaitHandle : WaitHandle
	{
		internal ProcessWaitHandle(SafeKernelHandle processHandle)
		{
			ArgumentNullException.ThrowIfNull(processHandle);
			SafeWaitHandle = new SafeWaitHandle(
				processHandle.DangerousGetHandle(),
				ownsHandle: false);
		}
	}

	private sealed class ProcThreadAttributeList : IDisposable
	{
		private IntPtr _jobHandleArray;
		private WindowsProcessJob.SafeKernelHandleLease? _jobHandleLease;
		private IntPtr _pointer;

		internal IntPtr Pointer => _pointer;

		private ProcThreadAttributeList(
			IntPtr pointer,
			IntPtr jobHandleArray,
			WindowsProcessJob.SafeKernelHandleLease jobHandleLease)
		{
			_pointer = pointer;
			_jobHandleArray = jobHandleArray;
			_jobHandleLease = jobHandleLease;
		}

		internal static ProcThreadAttributeList Create(
			SafePseudoConsoleHandle pseudoConsole,
			WindowsProcessJob job)
		{
			ArgumentNullException.ThrowIfNull(pseudoConsole);
			ArgumentNullException.ThrowIfNull(job);
			nuint requiredBytes = 0;
			_ = NativeMethods.InitializeProcThreadAttributeList(
				IntPtr.Zero,
				2,
				0,
				ref requiredBytes);

			if (requiredBytes == 0)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to determine the process attribute-list size.");
			}

			IntPtr pointer = IntPtr.Zero;
			IntPtr jobHandleArray = IntPtr.Zero;
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease = null;
			bool isInitialized = false;

			try
			{
				pointer = Marshal.AllocHGlobal(checked((int)requiredBytes));
				jobHandleArray = Marshal.AllocHGlobal(IntPtr.Size);

				if (!NativeMethods.InitializeProcThreadAttributeList(
					pointer,
					2,
					0,
					ref requiredBytes))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to initialize the process attribute list.");
				}

				isInitialized = true;

				if (!NativeMethods.UpdateProcThreadAttribute(
					pointer,
					0,
					ProcThreadAttributePseudoConsole,
					pseudoConsole.DangerousGetHandle(),
					checked((nuint)IntPtr.Size),
					IntPtr.Zero,
					IntPtr.Zero))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to attach the pseudoconsole process attribute.");
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
						"Unable to assign the pseudoconsole Job Object at creation.");
				}

				GC.KeepAlive(pseudoConsole);
				GC.KeepAlive(job);
				return new ProcThreadAttributeList(
					pointer,
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

			if (jobHandleArray != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(jobHandleArray);
			}

			jobHandleLease?.Dispose();
		}
	}

	private sealed class SafePseudoConsoleHandle : SafeHandle
	{
		public override bool IsInvalid => handle == IntPtr.Zero;

		internal SafePseudoConsoleHandle(IntPtr handle)
			: base(IntPtr.Zero, ownsHandle: true)
		{
			SetHandle(handle);
		}

		protected override bool ReleaseHandle()
		{
			NativeMethods.ClosePseudoConsole(handle);
			return true;
		}
	}

	private static class NativeMethods
	{
		[DllImport(
			"kernel32.dll",
			EntryPoint = "ClosePseudoConsole",
			ExactSpelling = true)]
		internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "CreatePipe",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CreatePipe(
			out SafeFileHandle readPipe,
			out SafeFileHandle writePipe,
			IntPtr pipeAttributes,
			uint size);

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
			EntryPoint = "CreatePseudoConsole",
			ExactSpelling = true)]
		internal static extern int CreatePseudoConsole(
			Coord size,
			SafeFileHandle input,
			SafeFileHandle output,
			uint flags,
			out IntPtr pseudoConsole);

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

	private const uint CreateSuspended = 0x00000004;
	private const uint CreateUnicodeEnvironment = 0x00000400;
	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const uint ForcedExitCode = 0xC0DE0001;
	private const uint ResumeThreadFailed = 0xFFFFFFFF;
	private const uint StartfUseStdHandles = 0x00000100;
	private const uint StillActive = 259;
	private static readonly nuint ProcThreadAttributeJobList = 0x0002000D;
	private static readonly nuint ProcThreadAttributePseudoConsole = 0x00020016;
	private readonly TimeSpan _cleanupTimeout;
	private readonly Func<Exception?>? _disposeTerminationFailureFactory;
	private readonly TaskCompletionSource<int> _exitCompletion = new(
		TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _quiescenceCompletion = new(
		TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly FileStream _inputStream;
	private readonly SemaphoreSlim _inputWriteGate = new(1, 1);
	private readonly WindowsProcessJob _job;
	private readonly int _maximumSavedOutputBytes;
	private readonly MemoryStream _outputBuffer;
	private readonly Task _outputPump;
	private readonly FileStream _outputStream;
	private readonly object _outputSyncRoot = new();
	private readonly SafeKernelHandle _processHandle;
	private readonly ProcessWaitHandle _processWaitHandle;
	private readonly SafePseudoConsoleHandle _pseudoConsole;
	private readonly RegisteredWaitHandle _registeredProcessWait;
	private readonly SemaphoreSlim _terminationGate = new(1, 1);
	private Exception? _outputReadFailure;
	private long _totalOutputBytesRead;
	private int _isDisposed;
	private bool _isSavedByteLimitExceeded;
	private int _isTerminationRequested;

	public int ProcessId { get; }

	internal bool IsProcessHandleClosed => _processHandle.IsClosed;

	internal bool IsPseudoConsoleClosed => _pseudoConsole.IsClosed;

	internal Task OutputPumpCompletion => _outputPump;

	internal Task QuiescenceTask => _quiescenceCompletion.Task;

	private WindowsConPtySession(
		WindowsProcessJob job,
		SafePseudoConsoleHandle pseudoConsole,
		SafeKernelHandle processHandle,
		int processId,
		FileStream inputStream,
		FileStream outputStream,
		int maximumSavedOutputBytes,
		TimeSpan cleanupTimeout,
		Func<Exception?>? disposeTerminationFailureFactory)
	{
		_job = job;
		_pseudoConsole = pseudoConsole;
		_processHandle = processHandle;
		ProcessId = processId;
		_inputStream = inputStream;
		_outputStream = outputStream;
		_maximumSavedOutputBytes = maximumSavedOutputBytes;
		_cleanupTimeout = cleanupTimeout;
		_disposeTerminationFailureFactory =
			disposeTerminationFailureFactory;
		_outputBuffer = new MemoryStream(Math.Min(maximumSavedOutputBytes, 8192));
		_processWaitHandle = new ProcessWaitHandle(processHandle);
		_registeredProcessWait = ThreadPool.RegisterWaitForSingleObject(
			_processWaitHandle,
			static (state, _) => ((WindowsConPtySession)state!).OnProcessExited(),
			this,
			Timeout.InfiniteTimeSpan,
			executeOnlyOnce: true);
		_outputPump = Task.Factory.StartNew(
			DrainOutput,
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
	}

	internal static async ValueTask<WindowsConPtySession> StartAsync(
		ConPtyStartRequest request,
		CancellationToken cancellationToken = default,
		Func<Exception?>? disposeTerminationFailureFactory = null,
		Action<uint, WindowsProcessJob>? afterProcessCreated = null)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidatePlatform();
		string executablePath = ValidateExecutablePath(request.ExecutablePath);
		string workingDirectory = ValidateWorkingDirectory(request.WorkingDirectory);
		ValidateRequest(request);
		string commandLineText = BuildCommandLine(executablePath, request.Arguments);
		string environmentText = BuildEnvironmentBlock(request.Environment);
		cancellationToken.ThrowIfCancellationRequested();

		WindowsProcessJob? job = null;
		SafeFileHandle? inputReadHandle = null;
		SafeFileHandle? inputWriteHandle = null;
		SafeFileHandle? outputReadHandle = null;
		SafeFileHandle? outputWriteHandle = null;
		SafePseudoConsoleHandle? pseudoConsole = null;
		ProcThreadAttributeList? attributeList = null;
		SafeKernelHandle? processHandle = null;
		SafeKernelHandle? threadHandle = null;
		FileStream? inputStream = null;
		FileStream? outputStream = null;
		WindowsConPtySession? session = null;
		IntPtr environment = IntPtr.Zero;
		bool isJobMembershipVerified = false;

		try
		{
			job = WindowsProcessJob.CreateKillOnClose();
			CreatePipePair(out inputReadHandle, out inputWriteHandle);
			CreatePipePair(out outputReadHandle, out outputWriteHandle);

			Coord viewport = new()
			{
				X = request.Columns,
				Y = request.Rows
			};
			int createPseudoConsoleResult = NativeMethods.CreatePseudoConsole(
				viewport,
				inputReadHandle,
				outputWriteHandle,
				0,
				out IntPtr rawPseudoConsole);

			if (createPseudoConsoleResult < 0)
			{
				Marshal.ThrowExceptionForHR(createPseudoConsoleResult);
			}

			pseudoConsole = new SafePseudoConsoleHandle(rawPseudoConsole);
			attributeList = ProcThreadAttributeList.Create(
				pseudoConsole,
				job);
			environment = Marshal.StringToHGlobalUni(environmentText);
			StartupInfoEx startupInfo = default;
			startupInfo.StartupInfo.Size = checked((uint)Marshal.SizeOf<StartupInfoEx>());
			// Keep test runners and debuggers from substituting their redirected
			// standard handles before ConPTY attaches the child process.
			startupInfo.StartupInfo.Flags = StartfUseStdHandles;
			startupInfo.AttributeList = attributeList.Pointer;
			StringBuilder commandLine = new(
				commandLineText,
				commandLineText.Length + 1);

			if (!NativeMethods.CreateProcess(
				executablePath,
				commandLine,
				IntPtr.Zero,
				IntPtr.Zero,
				inheritHandles: false,
				ExtendedStartupInfoPresent |
					CreateSuspended |
					CreateUnicodeEnvironment,
				environment,
				workingDirectory,
				ref startupInfo,
				out ProcessInformation processInformation))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to create the suspended pseudoconsole process.");
			}

			GC.KeepAlive(pseudoConsole);
			GC.KeepAlive(job);
			processHandle = new SafeKernelHandle(processInformation.Process);
			threadHandle = new SafeKernelHandle(processInformation.Thread);
			afterProcessCreated?.Invoke(
				processInformation.ProcessId,
				job);
			job.VerifyMembership(processHandle);
			isJobMembershipVerified = true;

			inputReadHandle.Dispose();
			inputReadHandle = null;
			outputWriteHandle.Dispose();
			outputWriteHandle = null;
			inputStream = new FileStream(
				inputWriteHandle,
				FileAccess.Write,
				bufferSize: 4096,
				isAsync: false);
			inputWriteHandle = null;
			outputStream = new FileStream(
				outputReadHandle,
				FileAccess.Read,
				bufferSize: 4096,
				isAsync: false);
			outputReadHandle = null;
			session = new WindowsConPtySession(
				job,
				pseudoConsole,
				processHandle,
				checked((int)processInformation.ProcessId),
				inputStream,
				outputStream,
				request.MaximumSavedOutputBytes,
				request.CleanupTimeout,
				disposeTerminationFailureFactory);

			job = null;
			pseudoConsole = null;
			processHandle = null;
			inputStream = null;
			outputStream = null;
			cancellationToken.ThrowIfCancellationRequested();
			uint previousSuspendCount = NativeMethods.ResumeThread(threadHandle);

			if (previousSuspendCount == ResumeThreadFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to resume the contained pseudoconsole process.");
			}

			if (previousSuspendCount != 1)
			{
				throw new InvalidOperationException(
					$"The new process had an unexpected suspend count of {previousSuspendCount}.");
			}

			return session;
		}
		catch
		{
			if (session is not null)
			{
				try
				{
					await session.DisposeAsync();
				}
				catch
				{
					// Preserve the original startup failure after best-effort cleanup.
				}

				try
				{
					await session.QuiescenceTask;
				}
				catch
				{
					// Preserve the original startup failure after bounded cleanup.
				}
			}
			else if ((processHandle is not null) &&
				!processHandle.IsInvalid &&
				!processHandle.IsClosed)
			{
				await TerminateFailedStartAndWaitForPositiveQuiescenceAsync(
					processHandle,
					job,
					isJobMembershipVerified,
					request.CleanupTimeout);
			}

			throw;
		}
		finally
		{
			threadHandle?.Dispose();
			inputReadHandle?.Dispose();
			inputWriteHandle?.Dispose();
			outputReadHandle?.Dispose();
			outputWriteHandle?.Dispose();
			inputStream?.Dispose();
			outputStream?.Dispose();
			attributeList?.Dispose();

			if (environment != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(environment);
			}

			pseudoConsole?.Dispose();
			processHandle?.Dispose();
			job?.Dispose();
		}
	}

	public ConPtyOutputSnapshot GetOutputSnapshot(int savedByteOffset = 0)
	{
		lock (_outputSyncRoot)
		{
			int savedByteCount = checked((int)_outputBuffer.Length);

			if ((savedByteOffset < 0) ||
				(savedByteOffset > savedByteCount))
			{
				throw new ArgumentOutOfRangeException(nameof(savedByteOffset));
			}

			if (!_outputBuffer.TryGetBuffer(out ArraySegment<byte> buffer))
			{
				throw new InvalidOperationException(
					"The pseudoconsole output buffer is unavailable.");
			}

			byte[] unreadBytes = buffer.AsSpan(
				savedByteOffset,
				savedByteCount - savedByteOffset).ToArray();
			return new ConPtyOutputSnapshot(
				unreadBytes,
				savedByteOffset,
				savedByteCount,
				_totalOutputBytesRead,
				_isSavedByteLimitExceeded,
				_outputReadFailure);
		}
	}

	public async ValueTask WriteInputAsync(
		ReadOnlyMemory<byte> input,
		CancellationToken cancellationToken = default)
	{
		ThrowIfUnavailableForInput();
		await _inputWriteGate.WaitAsync(cancellationToken);

		try
		{
			ThrowIfUnavailableForInput();
			await _inputStream.WriteAsync(input, cancellationToken);
			await _inputStream.FlushAsync(cancellationToken);
		}
		finally
		{
			_inputWriteGate.Release();
		}
	}

	public async Task<int> WaitForExitAsync(
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
		return await _exitCompletion.Task.WaitAsync(cancellationToken);
	}

	public async ValueTask TerminateAsync(
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
		await TerminateCoreAsync(cancellationToken, throwOnTimeout: true);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
		{
			return;
		}

		long cleanupStartedTimestamp = Stopwatch.GetTimestamp();
		Exception? cleanupFailure = null;
		bool isTreeEmptyConfirmed = false;
		TryDispose(_inputStream);

		try
		{
			Exception? injectedFailure =
				_disposeTerminationFailureFactory?.Invoke();
			if (injectedFailure is not null)
			{
				throw injectedFailure;
			}

			await TerminateCoreAsync(
				CancellationToken.None,
				throwOnTimeout: true,
				GetRemainingCleanupTime(cleanupStartedTimestamp));
			isTreeEmptyConfirmed = true;
		}
		catch (Exception exception)
		{
			cleanupFailure ??= exception;
		}

		if (!isTreeEmptyConfirmed)
		{
			try
			{
				if (_job.GetActiveProcessCount() > 0)
				{
					_job.Terminate(ForcedExitCode);
				}

				TimeSpan remaining = GetRemainingCleanupTime(
					cleanupStartedTimestamp);
				isTreeEmptyConfirmed = _job.GetActiveProcessCount() == 0;

				if (!isTreeEmptyConfirmed && (remaining > TimeSpan.Zero))
				{
					isTreeEmptyConfirmed = await _job.WaitForEmptyAsync(
						remaining,
						CancellationToken.None);
				}
			}
			catch (Exception exception)
			{
				cleanupFailure ??= exception;
			}
		}

		if (!isTreeEmptyConfirmed)
		{
			cleanupFailure ??= new TimeoutException(
				"The pseudoconsole process Job Object did not become positively empty before cleanup timed out.");
			_ = CompleteBoundedLateDisposalAsync(cleanupStartedTimestamp);
			throw cleanupFailure;
		}

		try
		{
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (!_exitCompletion.Task.IsCompleted &&
				(remaining <= TimeSpan.Zero))
			{
				throw new TimeoutException(
					"The pseudoconsole process did not exit before cleanup timed out.");
			}

			await _exitCompletion.Task.WaitAsync(remaining);
		}
		catch (Exception exception)
		{
			// The Job Object and pseudoconsole handles remain the final kill barriers.
			cleanupFailure ??= exception;
		}

		Exception? pseudoConsoleFailure = await ClosePseudoConsoleAsync(
			cleanupStartedTimestamp);
		cleanupFailure ??= pseudoConsoleFailure;
		Exception? outputPumpFailure = await FinishOutputPumpAsync(
			cleanupStartedTimestamp);
		cleanupFailure ??= outputPumpFailure;

		try
		{
			_registeredProcessWait.Unregister(null);
		}
		catch (Exception exception)
		{
			cleanupFailure ??= exception;
		}

		try
		{
			_processWaitHandle.Dispose();
		}
		catch (Exception exception)
		{
			cleanupFailure ??= exception;
		}

		TryDispose(_outputStream);

		try
		{
			_processHandle.Dispose();
		}
		catch (Exception exception)
		{
			cleanupFailure ??= exception;
		}

		try
		{
			_job.Dispose();
		}
		catch (Exception exception)
		{
			cleanupFailure ??= exception;
		}

		_quiescenceCompletion.TrySetResult();

		if (cleanupFailure is not null)
		{
			throw cleanupFailure;
		}
	}

	private static void AppendCommandLineArgument(
		StringBuilder commandLine,
		string argument)
	{
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

	private static string BuildCommandLine(
		string executablePath,
		IReadOnlyList<string> arguments)
	{
		StringBuilder commandLine = new();
		AppendCommandLineArgument(commandLine, executablePath);

		foreach (string argument in arguments)
		{
			commandLine.Append(' ');
			AppendCommandLineArgument(commandLine, argument);
		}

		return commandLine.ToString();
	}

	private static string BuildEnvironmentBlock(
		IReadOnlyDictionary<string, string> environment)
	{
		ArgumentNullException.ThrowIfNull(environment);
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		List<KeyValuePair<string, string>> variables = environment
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal)
			.ToList();
		StringBuilder block = new();

		foreach ((string name, string value) in variables)
		{
			if (string.IsNullOrWhiteSpace(name) ||
				(name[0] == '=') ||
				name.Contains('=') ||
				name.Contains('\0'))
			{
				throw new ArgumentException(
					"Environment variable names must be non-empty and contain neither '=' nor NUL.",
					nameof(environment));
			}

			if ((value is null) || value.Contains('\0'))
			{
				throw new ArgumentException(
					"Environment variable values must be non-null and contain no NUL.",
					nameof(environment));
			}

			if (!names.Add(name))
			{
				throw new ArgumentException(
					$"The environment allowlist contains a case-insensitive duplicate for '{name}'.",
					nameof(environment));
			}

			block.Append(name);
			block.Append('=');
			block.Append(value);
			block.Append('\0');
		}

		block.Append('\0');
		return block.ToString();
	}

	private static void CreatePipePair(
		out SafeFileHandle readHandle,
		out SafeFileHandle writeHandle)
	{
		if (!NativeMethods.CreatePipe(
			out readHandle,
			out writeHandle,
			IntPtr.Zero,
			0))
		{
			throw new Win32Exception(
				Marshal.GetLastWin32Error(),
				"Unable to create a synchronous pseudoconsole pipe.");
		}
	}

	private static void ObserveFault(Task task)
	{
		_ = task.ContinueWith(
			completedTask => _ = completedTask.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously |
				TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	private static void TryDispose(IDisposable disposable)
	{
		try
		{
			disposable.Dispose();
		}
		catch
		{
			// Teardown is best effort and bounded by the owning session.
		}
	}

	private static async Task
		TerminateFailedStartAndWaitForPositiveQuiescenceAsync(
			SafeKernelHandle processHandle,
			WindowsProcessJob? job,
			bool isJobMembershipVerified,
			TimeSpan cleanupTimeout)
	{
		try
		{
			if (isJobMembershipVerified && (job is not null))
			{
				job.Terminate(ForcedExitCode);
			}
			else
			{
				_ = NativeMethods.TerminateProcess(processHandle, ForcedExitCode);
			}
		}
		catch
		{
			// Keep the ownership handles and positively observe quiescence below.
		}

		if (isJobMembershipVerified && (job is not null))
		{
			_ = await job.WaitForPositiveEmptyConfirmationAsync(cleanupTimeout);
			return;
		}

		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			try
			{
				if (NativeMethods.WaitForSingleObject(processHandle, 0) == 0)
				{
					return;
				}
			}
			catch
			{
				// Only a positively signaled process handle permits cleanup.
			}

			TimeSpan remaining = cleanupTimeout - stopwatch.Elapsed;

			if (remaining <= TimeSpan.Zero)
			{
				return;
			}

			TimeSpan pollInterval = TimeSpan.FromMilliseconds(20);
			await Task.Delay(
				remaining < pollInterval ? remaining : pollInterval);
		}
	}

	private static void ValidatePlatform()
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
		{
			throw new PlatformNotSupportedException(
				"ConPTY requires Windows 10 version 1809 or later.");
		}
	}

	private static string ValidateExecutablePath(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

		if (!Path.IsPathFullyQualified(executablePath))
		{
			throw new ArgumentException(
				"The executable path must be absolute; shell or PATH resolution is not allowed.",
				nameof(executablePath));
		}

		string fullPath = Path.GetFullPath(executablePath);

		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException(
				"The pseudoconsole executable was not found.",
				fullPath);
		}

		return fullPath;
	}

	private static void ValidateRequest(ConPtyStartRequest request)
	{
		ArgumentNullException.ThrowIfNull(request.Arguments);
		ArgumentNullException.ThrowIfNull(request.Environment);

		if ((request.Columns <= 0) || (request.Rows <= 0))
		{
			throw new ArgumentOutOfRangeException(
				nameof(request),
				"The pseudoconsole viewport must have positive dimensions.");
		}

		if (request.MaximumSavedOutputBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(request),
				"The saved-output byte limit must be positive.");
		}

		if (request.CleanupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(request),
				"The cleanup timeout must be positive.");
		}

		foreach (string argument in request.Arguments)
		{
			if ((argument is null) || argument.Contains('\0'))
			{
				throw new ArgumentException(
					"Process arguments must be non-null and contain no NUL.",
					nameof(request));
			}
		}
	}

	private static string ValidateWorkingDirectory(string workingDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

		if (!Path.IsPathFullyQualified(workingDirectory))
		{
			throw new ArgumentException(
				"The working directory must be absolute.",
				nameof(workingDirectory));
		}

		string fullPath = Path.GetFullPath(workingDirectory);

		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException(
				$"The pseudoconsole working directory does not exist: {fullPath}");
		}

		return fullPath;
	}

	private TimeSpan GetRemainingCleanupTime(long cleanupStartedTimestamp)
	{
		TimeSpan elapsed = Stopwatch.GetElapsedTime(cleanupStartedTimestamp);
		TimeSpan remaining = _cleanupTimeout - elapsed;
		return remaining > TimeSpan.Zero
			? remaining
			: TimeSpan.Zero;
	}

	private async Task CompleteBoundedLateDisposalAsync(
		long cleanupStartedTimestamp)
	{
		Exception? cleanupFailure = null;
		bool isTreeEmptyConfirmed = false;

		try
		{
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (remaining > TimeSpan.Zero)
			{
				isTreeEmptyConfirmed = await _job
					.WaitForPositiveEmptyConfirmationAsync(remaining);
			}
		}
		catch (Exception exception)
		{
			cleanupFailure = exception;
		}

		if (!isTreeEmptyConfirmed)
		{
			cleanupFailure ??= new TimeoutException(
				"The pseudoconsole process tree did not provide positive " +
				"quiescence during bounded late cleanup.");
			// Closing the last kill-on-close Job handle is the bounded final
			// containment step. Do this before releasing any other ownership.
			TryDispose(_job);
		}

		TryDispose(_inputStream);

		Exception? pseudoConsoleFailure = await ClosePseudoConsoleAsync(
			cleanupStartedTimestamp);
		cleanupFailure ??= pseudoConsoleFailure;
		Exception? outputPumpFailure = await FinishOutputPumpAsync(
			cleanupStartedTimestamp);
		cleanupFailure ??= outputPumpFailure;

		try
		{
			_registeredProcessWait.Unregister(null);
		}
		catch
		{
		}

		TryDispose(_processWaitHandle);
		TryDispose(_processHandle);
		TryDispose(_job);

		if (cleanupFailure is null)
		{
			_quiescenceCompletion.TrySetResult();
		}
		else
		{
			_quiescenceCompletion.TrySetException(cleanupFailure);
		}
	}

	private async Task<Exception?> ClosePseudoConsoleAsync(
		long cleanupStartedTimestamp)
	{
		Task closeTask = Task.Factory.StartNew(
			_pseudoConsole.Dispose,
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);

		try
		{
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (!closeTask.IsCompleted && (remaining <= TimeSpan.Zero))
			{
				throw new TimeoutException(
					"The pseudoconsole did not close before cleanup timed out.");
			}

			await closeTask.WaitAsync(remaining);
			return null;
		}
		catch (Exception exception)
		{
			TryDispose(_outputStream);
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (closeTask.IsCompleted || (remaining > TimeSpan.Zero))
			{
				try
				{
					await closeTask.WaitAsync(remaining);
				}
				catch
				{
					ObserveFault(closeTask);
				}
			}

			if (!closeTask.IsCompleted)
			{
				ObserveFault(closeTask);
			}

			return exception;
		}
	}

	private void DrainOutput()
	{
		byte[] buffer = new byte[8192];

		try
		{
			while (true)
			{
				int bytesRead = _outputStream.Read(buffer, 0, buffer.Length);

				if (bytesRead == 0)
				{
					return;
				}

				lock (_outputSyncRoot)
				{
					_totalOutputBytesRead = checked(_totalOutputBytesRead + bytesRead);
					int remainingCapacity = checked(
						_maximumSavedOutputBytes - (int)_outputBuffer.Length);
					int bytesToSave = Math.Min(remainingCapacity, bytesRead);

					if (bytesToSave > 0)
					{
						_outputBuffer.Write(buffer, 0, bytesToSave);
					}

					if (bytesToSave < bytesRead)
					{
						_isSavedByteLimitExceeded = true;
					}
				}
			}
		}
		catch (Exception exception)
		{
			if ((_isDisposed == 0) && (_isTerminationRequested == 0))
			{
				lock (_outputSyncRoot)
				{
					_outputReadFailure ??= exception;
				}
			}
		}
	}

	private async Task<Exception?> FinishOutputPumpAsync(
		long cleanupStartedTimestamp)
	{
		try
		{
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (!_outputPump.IsCompleted && (remaining <= TimeSpan.Zero))
			{
				throw new TimeoutException(
					"The pseudoconsole output pump did not finish before cleanup timed out.");
			}

			await _outputPump.WaitAsync(remaining);
			return null;
		}
		catch (Exception exception)
		{
			TryDispose(_outputStream);
			TimeSpan remaining = GetRemainingCleanupTime(
				cleanupStartedTimestamp);

			if (_outputPump.IsCompleted || (remaining > TimeSpan.Zero))
			{
				try
				{
					await _outputPump.WaitAsync(remaining);
				}
				catch
				{
					ObserveFault(_outputPump);
				}
			}

			if (!_outputPump.IsCompleted)
			{
				ObserveFault(_outputPump);
			}

			return exception;
		}
	}

	internal static void CompleteProcessExit(
		TaskCompletionSource<int> exitCompletion,
		Func<(bool IsSuccessful, uint ExitCode, int ErrorCode)> readExitCode)
	{
		ArgumentNullException.ThrowIfNull(exitCompletion);
		ArgumentNullException.ThrowIfNull(readExitCode);

		try
		{
			(bool isSuccessful, uint exitCode, int errorCode) =
				readExitCode();
			if (!isSuccessful)
			{
				exitCompletion.TrySetException(new Win32Exception(
					errorCode,
					"Unable to read the pseudoconsole process exit code."));
				return;
			}

			if (exitCode == StillActive)
			{
				exitCompletion.TrySetException(new InvalidOperationException(
					"The process wait was signaled while its exit code was still active."));
				return;
			}

			exitCompletion.TrySetResult(unchecked((int)exitCode));
		}
		catch (Exception exception)
		{
			// Registered-wait callbacks run on the ThreadPool and must not leak an
			// exception if cleanup closes the process handle first.
			exitCompletion.TrySetException(exception);
		}
	}

	private void OnProcessExited()
	{
		CompleteProcessExit(
			_exitCompletion,
			() =>
			{
				bool isSuccessful = NativeMethods.GetExitCodeProcess(
					_processHandle,
					out uint exitCode);
				int errorCode = isSuccessful
					? 0
					: Marshal.GetLastWin32Error();
				return (isSuccessful, exitCode, errorCode);
			});
	}

	private async Task TerminateCoreAsync(
		CancellationToken cancellationToken,
		bool throwOnTimeout,
		TimeSpan? timeout = null)
	{
		TimeSpan waitTimeout = timeout ?? _cleanupTimeout;
		long waitStartedTimestamp = Stopwatch.GetTimestamp();
		bool isGateAcquired = await _terminationGate.WaitAsync(
			waitTimeout,
			cancellationToken);

		if (!isGateAcquired)
		{
			if (throwOnTimeout)
			{
				throw new TimeoutException(
					"The pseudoconsole termination gate did not become available before cleanup timed out.");
			}

			return;
		}

		try
		{
			if (_isTerminationRequested == 0)
			{
				TryDispose(_inputStream);

				if (_job.GetActiveProcessCount() > 0)
				{
					_job.Terminate(ForcedExitCode);
				}

				Volatile.Write(ref _isTerminationRequested, 1);
			}
		}
		finally
		{
			_terminationGate.Release();
		}

		bool isEmpty = _job.GetActiveProcessCount() == 0;
		TimeSpan elapsed = Stopwatch.GetElapsedTime(waitStartedTimestamp);
		TimeSpan remaining = waitTimeout - elapsed;

		if (!isEmpty && (remaining > TimeSpan.Zero))
		{
			isEmpty = await _job.WaitForEmptyAsync(
				remaining,
				cancellationToken);
		}

		if (!isEmpty && throwOnTimeout)
		{
			throw new TimeoutException(
				"The pseudoconsole process Job Object did not become empty before cleanup timed out.");
		}
	}

	private void ThrowIfUnavailableForInput()
	{
		ObjectDisposedException.ThrowIf(_isDisposed != 0, this);

		if (Volatile.Read(ref _isTerminationRequested) != 0)
		{
			throw new InvalidOperationException(
				"Input is unavailable after pseudoconsole termination begins.");
		}
	}
}
