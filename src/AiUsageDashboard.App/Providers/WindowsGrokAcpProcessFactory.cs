using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

using Microsoft.Win32.SafeHandles;

namespace AiUsageDashboard.App.Providers;

internal sealed class GrokProcessContainmentState
{
	private sealed class ExecutableLeaseRetention : IDisposable
	{
		private WindowsOfficialCliExecutableLease? _executableLease;
		private GrokProcessContainmentState? _owner;

		internal ExecutableLeaseRetention(
			GrokProcessContainmentState owner,
			WindowsOfficialCliExecutableLease executableLease)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_executableLease = executableLease ??
				throw new ArgumentNullException(nameof(executableLease));
		}

		public void Dispose()
		{
			GrokProcessContainmentState? owner = Interlocked.Exchange(
				ref _owner,
				null);
			owner?.ReleaseExecutableLease(this);
		}

		internal WindowsOfficialCliExecutableLease? TakeExecutableLease()
		{
			return Interlocked.Exchange(ref _executableLease, null);
		}
	}

	private sealed class LaunchTransitionLease : IDisposable
	{
		private object? _transitionGate;

		internal LaunchTransitionLease(object transitionGate)
		{
			_transitionGate = transitionGate ??
				throw new ArgumentNullException(nameof(transitionGate));
		}

		public void Dispose()
		{
			object? transitionGate = Interlocked.Exchange(
				ref _transitionGate,
				null);

			if (transitionGate is not null)
			{
				Monitor.Exit(transitionGate);
			}
		}
	}

	private readonly HashSet<ExecutableLeaseRetention> _activeExecutableLeases = new();
	private readonly object _executableLeaseGate = new();
	private readonly object _launchTransitionGate = new();
	private readonly List<WindowsOfficialCliExecutableLease>
		_quarantinedExecutableLeases = new();
	private int _isCompromised;
	private int _isCompromiseRequested;
	internal static GrokProcessContainmentState Shared { get; } = new();

	internal bool IsCompromised =>
		(Volatile.Read(ref _isCompromiseRequested) != 0) ||
		(Volatile.Read(ref _isCompromised) != 0);
	internal int QuarantinedExecutableLeaseCount
	{
		get
		{
			lock (_executableLeaseGate)
			{
				return _quarantinedExecutableLeases.Count;
			}
		}
	}

	internal IDisposable EnterLaunchTransition()
	{
		Monitor.Enter(_launchTransitionGate);

		try
		{
			ThrowIfCompromised();
			return new LaunchTransitionLease(_launchTransitionGate);
		}
		catch
		{
			Monitor.Exit(_launchTransitionGate);
			throw;
		}
	}

	internal void MarkCompromised()
	{
		lock (_executableLeaseGate)
		{
			// 先關閉新 launch；未確認已結束的 process 所用 executable
			// 必須鎖定到本次 App 結束。
			Interlocked.Exchange(ref _isCompromiseRequested, 1);

			foreach (ExecutableLeaseRetention retention in _activeExecutableLeases)
			{
				WindowsOfficialCliExecutableLease? executableLease =
					retention.TakeExecutableLease();

				if (executableLease is not null)
				{
					_quarantinedExecutableLeases.Add(executableLease);
				}
			}

			_activeExecutableLeases.Clear();
		}

		lock (_launchTransitionGate)
		{
			Volatile.Write(ref _isCompromised, 1);
		}
	}

	internal IDisposable RetainExecutableLease(
		WindowsOfficialCliExecutableLease executableLease)
	{
		ArgumentNullException.ThrowIfNull(executableLease);
		ExecutableLeaseRetention retention = new(this, executableLease);

		lock (_executableLeaseGate)
		{
			if (IsCompromised)
			{
				WindowsOfficialCliExecutableLease retainedLease =
					retention.TakeExecutableLease() ??
					throw new InvalidOperationException(
						"Grok executable lease ownership was lost.");
				_quarantinedExecutableLeases.Add(retainedLease);
			}
			else
			{
				_activeExecutableLeases.Add(retention);
			}
		}

		return retention;
	}

	internal void ThrowIfCompromised()
	{
		if (IsCompromised)
		{
			throw new GrokProcessContainmentException(
				"Grok process containment could not be confirmed earlier in this run. Restart AI Usage before launching Grok again.");
		}
	}

	private void ReleaseExecutableLease(ExecutableLeaseRetention retention)
	{
		WindowsOfficialCliExecutableLease? executableLease = null;

		lock (_executableLeaseGate)
		{
			if (!_activeExecutableLeases.Remove(retention))
			{
				return;
			}

			executableLease = retention.TakeExecutableLease();

			if ((executableLease is not null) && IsCompromised)
			{
				_quarantinedExecutableLeases.Add(executableLease);
				executableLease = null;
			}
		}

		executableLease?.Dispose();
	}
}

internal sealed class WindowsGrokAcpProcessFactory : IGrokAcpProcessFactory
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
	private struct SecurityAttributes
	{
		internal uint Length;
		internal IntPtr SecurityDescriptor;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool InheritHandle;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SidAndAttributes
	{
		internal IntPtr Sid;
		internal uint Attributes;
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

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenGroups
	{
		internal uint GroupCount;
		internal SidAndAttributes Groups;
	}

	private sealed record ValidatedLaunchOptions(
		string ExecutablePath,
		string HomeDirectory,
		string WorkingDirectory,
		GrokContainedCommand Command);

	private sealed class CurrentTokenPipeSecurityDescriptor : IDisposable
	{
		private IntPtr _pointer;

		internal IntPtr Pointer => _pointer;

		private CurrentTokenPipeSecurityDescriptor(IntPtr pointer)
		{
			_pointer = pointer;
		}

		internal static CurrentTokenPipeSecurityDescriptor Create()
		{
			using WindowsIdentity identity = WindowsIdentity.GetCurrent();
			SecurityIdentifier user = identity.User ??
				throw new InvalidOperationException(
					"The current Windows user SID is unavailable.");
			StringBuilder descriptor = new("D:P");
			AppendGenericAllAce(descriptor, user.Value);

			foreach (string restrictedSid in GetRestrictedSidValues(
				identity.AccessToken))
			{
				AppendGenericAllAce(descriptor, restrictedSid);
			}

			if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
				descriptor.ToString(),
				SecurityDescriptorRevision,
				out IntPtr pointer,
				out _))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to create the current-user Grok pipe security descriptor.");
			}

			try
			{
				return new CurrentTokenPipeSecurityDescriptor(pointer);
			}
			catch
			{
				_ = NativeMethods.LocalFree(pointer);
				throw;
			}
		}

		public void Dispose()
		{
			IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);

			if (pointer != IntPtr.Zero)
			{
				_ = NativeMethods.LocalFree(pointer);
			}
		}

		private static void AppendGenericAllAce(
			StringBuilder descriptor,
			string sid)
		{
			descriptor.Append("(A;;GA;;;");
			descriptor.Append(sid);
			descriptor.Append(')');
		}

		private static IReadOnlyList<string> GetRestrictedSidValues(
			SafeAccessTokenHandle accessToken)
		{
			bool didReadWithoutBuffer = NativeMethods.GetTokenInformation(
				accessToken,
				TokenRestrictedSids,
				IntPtr.Zero,
				0,
				out uint requiredBytes);
			int initialErrorCode = Marshal.GetLastWin32Error();

			if (didReadWithoutBuffer)
			{
				return Array.Empty<string>();
			}

			if ((initialErrorCode != ErrorInsufficientBuffer) ||
				(requiredBytes == 0))
			{
				throw new Win32Exception(
					initialErrorCode,
					"Unable to query Grok pipe token restrictions.");
			}

			IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));

			try
			{
				if (!NativeMethods.GetTokenInformation(
					accessToken,
					TokenRestrictedSids,
					buffer,
					requiredBytes,
					out _))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to read Grok pipe token restrictions.");
				}

				uint groupCount = checked((uint)Marshal.ReadInt32(buffer));
				int groupOffset = checked((int)Marshal.OffsetOf<TokenGroups>(
					nameof(TokenGroups.Groups)));
				int groupSize = Marshal.SizeOf<SidAndAttributes>();
				List<string> values = new(checked((int)groupCount));

				for (uint index = 0; index < groupCount; index++)
				{
					IntPtr sid = Marshal.ReadIntPtr(
						buffer,
						checked(groupOffset + ((int)index * groupSize)));
					values.Add(new SecurityIdentifier(sid).Value);
				}

				return values;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}
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

			if (handles.Count == 0)
			{
				throw new ArgumentException(
					"At least one inherited Grok ACP handle is required.",
					nameof(handles));
			}

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
					"Unable to determine the Grok ACP process attribute-list size.");
			}

			IntPtr pointer = IntPtr.Zero;
			IntPtr handleArray = IntPtr.Zero;
			IntPtr jobHandleArray = IntPtr.Zero;
			WindowsProcessJob.SafeKernelHandleLease? jobHandleLease = null;
			bool isInitialized = false;

			try
			{
				pointer = Marshal.AllocHGlobal(checked((int)requiredBytes));
				handleArray = Marshal.AllocHGlobal(
					checked(handles.Count * IntPtr.Size));
				jobHandleArray = Marshal.AllocHGlobal(IntPtr.Size);

				if (!NativeMethods.InitializeProcThreadAttributeList(
					pointer,
					2,
					0,
					ref requiredBytes))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to initialize the Grok ACP process attribute list.");
				}

				isInitialized = true;

				for (int index = 0; index < handles.Count; index++)
				{
					SafeFileHandle handle = handles[index];

					if (handle.IsInvalid || handle.IsClosed)
					{
						throw new ArgumentException(
							"Inherited Grok ACP handles must be open and valid.",
							nameof(handles));
					}

					Marshal.WriteIntPtr(
						handleArray,
						checked(index * IntPtr.Size),
						handle.DangerousGetHandle());
				}

				if (!NativeMethods.UpdateProcThreadAttribute(
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
						"Unable to restrict inherited Grok ACP handles.");
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
						"Unable to contain the Grok ACP process at creation.");
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

	private sealed class WindowsGrokAcpProcess : IGrokAcpProcess
	{
		private readonly GrokProcessContainmentState _containmentState;
		private readonly SemaphoreSlim _terminationGate = new(1, 1);
		private WindowsProcessJob? _job;
		private SafeKernelHandle? _processHandle;
		private FileStream? _standardError;
		private FileStream? _standardInput;
		private FileStream? _standardOutput;
		private int _isDisposed;

		public Stream StandardInput =>
			Volatile.Read(ref _standardInput) ??
			throw new ObjectDisposedException(GetType().Name);

		public Stream StandardOutput =>
			Volatile.Read(ref _standardOutput) ??
			throw new ObjectDisposedException(GetType().Name);

		public Stream StandardError =>
			Volatile.Read(ref _standardError) ??
			throw new ObjectDisposedException(GetType().Name);

		internal WindowsGrokAcpProcess(
			GrokProcessContainmentState containmentState,
			WindowsProcessJob job,
			SafeKernelHandle processHandle,
			FileStream standardInput,
			FileStream standardOutput,
			FileStream standardError)
		{
			_containmentState = containmentState ??
				throw new ArgumentNullException(nameof(containmentState));
			_job = job;
			_processHandle = processHandle;
			_standardInput = standardInput;
			_standardOutput = standardOutput;
			_standardError = standardError;
		}

		public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
		{
			ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
			SafeKernelHandle processHandle = Volatile.Read(ref _processHandle) ??
				throw new ObjectDisposedException(GetType().Name);
			return WaitForProcessExitAsync(processHandle, cancellationToken);
		}

		public async Task TerminateTreeAsync(
			CancellationToken cancellationToken)
		{
			ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
			await _terminationGate.WaitAsync(cancellationToken);

			try
			{
				ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
				await TerminateAndConfirmCoreAsync(cancellationToken);
			}
			catch (Exception exception)
			{
				_containmentState.MarkCompromised();
				throw new GrokProcessContainmentException(
					"Grok ACP process tree termination could not be positively confirmed. Restart AI Usage before launching Grok again.",
					exception);
			}
			finally
			{
				_terminationGate.Release();
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
			{
				return;
			}

			TryDispose(Interlocked.Exchange(ref _standardInput, null));
			await _terminationGate.WaitAsync(CancellationToken.None);
			Exception? containmentFailure = null;

			try
			{
				using CancellationTokenSource cleanupSource =
					new(DefaultTerminationTimeout);

				try
				{
					await TerminateAndConfirmCoreAsync(cleanupSource.Token);
				}
				catch (Exception exception)
				{
					containmentFailure = exception;
				}
			}
			finally
			{
				_terminationGate.Release();
				TryDispose(Interlocked.Exchange(ref _standardOutput, null));
				TryDispose(Interlocked.Exchange(ref _standardError, null));
				Interlocked.Exchange(ref _job, null)?.Dispose();
				Interlocked.Exchange(ref _processHandle, null)?.Dispose();
			}

			if (containmentFailure is not null)
			{
				_containmentState.MarkCompromised();
				throw new GrokProcessContainmentException(
					"Grok ACP process tree termination could not be positively confirmed.",
					containmentFailure);
			}
		}

		private async Task TerminateAndConfirmCoreAsync(
			CancellationToken cancellationToken)
		{
			WindowsProcessJob job = Volatile.Read(ref _job) ??
				throw new ObjectDisposedException(GetType().Name);

			if (job.GetActiveProcessCount() > 0)
			{
				job.Terminate(ForcedExitCode);
			}

			bool isEmpty = await job.WaitForPositiveEmptyConfirmationAsync(
				DefaultTerminationTimeout,
				cancellationToken);

			if (!isEmpty)
			{
				throw new TimeoutException(
					"The Grok ACP process tree did not report positive quiescence.");
			}
		}
	}

	private static class NativeMethods
	{
		[DllImport(
			"advapi32.dll",
			CharSet = CharSet.Unicode,
			EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
			[MarshalAs(UnmanagedType.LPWStr)] string stringSecurityDescriptor,
			uint stringSecurityDescriptorRevision,
			out IntPtr securityDescriptor,
			out uint securityDescriptorSize);

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
			CharSet = CharSet.Unicode,
			EntryPoint = "CreateFileW",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern SafeFileHandle CreateFile(
			[MarshalAs(UnmanagedType.LPWStr)] string fileName,
			uint desiredAccess,
			uint shareMode,
			ref SecurityAttributes securityAttributes,
			uint creationDisposition,
			uint flagsAndAttributes,
			IntPtr templateFile);

		[DllImport(
			"kernel32.dll",
			CharSet = CharSet.Unicode,
			EntryPoint = "CreateNamedPipeW",
			ExactSpelling = true,
			SetLastError = true)]
		internal static extern SafeFileHandle CreateNamedPipe(
			[MarshalAs(UnmanagedType.LPWStr)] string name,
			uint openMode,
			uint pipeMode,
			uint maximumInstances,
			uint outputBufferSize,
			uint inputBufferSize,
			uint defaultTimeout,
			ref SecurityAttributes securityAttributes);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "ConnectNamedPipe",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool ConnectNamedPipe(
			SafeFileHandle namedPipe,
			IntPtr overlapped);

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
			"advapi32.dll",
			EntryPoint = "GetTokenInformation",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool GetTokenInformation(
			SafeAccessTokenHandle token,
			int tokenInformationClass,
			IntPtr tokenInformation,
			uint tokenInformationLength,
			out uint returnLength);

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
			EntryPoint = "LocalFree",
			ExactSpelling = true)]
		internal static extern IntPtr LocalFree(IntPtr memory);

		[DllImport(
			"kernel32.dll",
			EntryPoint = "SetHandleInformation",
			ExactSpelling = true,
			SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool SetHandleInformation(
			SafeFileHandle handle,
			uint mask,
			uint flags);

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

	private const int MaximumEnvironmentBlockCharacters = 32767;
	private const uint CreateNoWindow = 0x08000000;
	private const uint CreateSuspended = 0x00000004;
	private const uint CreateUnicodeEnvironment = 0x00000400;
	private const int ErrorInsufficientBuffer = 122;
	private const int ErrorPipeConnected = 535;
	private const uint ExtendedStartupInfoPresent = 0x00080000;
	private const uint FileFlagFirstPipeInstance = 0x00080000;
	private const uint FileFlagOverlapped = 0x40000000;
	private const uint ForcedExitCode = 0xC0DE0003;
	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint HandleFlagInherit = 0x00000001;
	private const uint OpenExisting = 3;
	private const uint PipeAccessInbound = 0x00000001;
	private const uint PipeAccessOutbound = 0x00000002;
	private const uint PipeRejectRemoteClients = 0x00000008;
	private const uint RedirectedPipeBufferSize = 8192;
	private const uint ResumeThreadFailed = 0xFFFFFFFF;
	private const uint SecurityDescriptorRevision = 1;
	private const uint StartfUseStdHandles = 0x00000100;
	private const uint WaitFailed = 0xFFFFFFFF;
	private const uint WaitObject0 = 0x00000000;
	private const uint WaitTimeout = 0x00000102;
	private const int TokenRestrictedSids = 11;
	private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;
	private static readonly nuint ProcThreadAttributeJobList = 0x0002000D;
	private static readonly TimeSpan DefaultTerminationTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan ProcessPollInterval =
		TimeSpan.FromMilliseconds(20);
	private readonly GrokProcessContainmentState _containmentState;

	internal WindowsGrokAcpProcessFactory()
		: this(GrokProcessContainmentState.Shared)
	{
	}

	internal WindowsGrokAcpProcessFactory(
		GrokProcessContainmentState containmentState)
	{
		_containmentState = containmentState ??
			throw new ArgumentNullException(nameof(containmentState));
	}

	public async Task<IGrokAcpProcess> StartAsync(
		GrokAcpLaunchOptions options,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		cancellationToken.ThrowIfCancellationRequested();
		_containmentState.ThrowIfCompromised();

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Grok ACP containment requires Windows.");
		}

		ValidatedLaunchOptions validated = ValidateLaunchOptions(options);
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
		FileStream? inputStream = null;
		FileStream? outputStream = null;
		FileStream? errorStream = null;
		IntPtr environment = IntPtr.Zero;
		bool isJobMembershipVerified = false;
		bool ownershipTransferred = false;

		try
		{
			job = WindowsProcessJob.CreateKillOnClose();
			CreateRedirectedPipe(
				out inputReadHandle,
				out inputWriteHandle,
				parentReads: false);
			CreateRedirectedPipe(
				out outputReadHandle,
				out outputWriteHandle,
				parentReads: true);
			CreateRedirectedPipe(
				out errorReadHandle,
				out errorWriteHandle,
				parentReads: true);
			processAttributes = ProcessCreationAttributeList.Create(
				new[]
				{
					inputReadHandle,
					outputWriteHandle,
					errorWriteHandle
				},
				job);
			string environmentBlock = BuildEnvironmentBlock(validated);
			environment = Marshal.StringToHGlobalUni(environmentBlock);
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
			StringBuilder commandLine = BuildCommandLine(
				validated.ExecutablePath,
				validated.Command);
			ProcessInformation processInformation;
			using (IDisposable transition =
				_containmentState.EnterLaunchTransition())
			{
				if (!NativeMethods.CreateProcess(
					validated.ExecutablePath,
					commandLine,
					IntPtr.Zero,
					IntPtr.Zero,
					inheritHandles: true,
					CreateNoWindow |
						CreateSuspended |
						CreateUnicodeEnvironment |
						ExtendedStartupInfoPresent,
					environment,
					validated.WorkingDirectory,
					ref startupInfo,
					out processInformation))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to create the suspended Grok ACP process.");
				}
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
			outputWriteHandle.Dispose();
			outputWriteHandle = null;
			errorWriteHandle.Dispose();
			errorWriteHandle = null;
			inputStream = new FileStream(
				inputWriteHandle,
				FileAccess.Write,
				bufferSize: 4096,
				isAsync: true);
			inputWriteHandle = null;
			outputStream = new FileStream(
				outputReadHandle,
				FileAccess.Read,
				bufferSize: 8192,
				isAsync: true);
			outputReadHandle = null;
			errorStream = new FileStream(
				errorReadHandle,
				FileAccess.Read,
				bufferSize: 8192,
				isAsync: true);
			errorReadHandle = null;

			cancellationToken.ThrowIfCancellationRequested();
			int resumeError = 0;
			uint previousSuspendCount;
			using (IDisposable transition =
				_containmentState.EnterLaunchTransition())
			{
				previousSuspendCount = NativeMethods.ResumeThread(threadHandle);

				if (previousSuspendCount == ResumeThreadFailed)
				{
					resumeError = Marshal.GetLastWin32Error();
				}
			}

			if (previousSuspendCount == ResumeThreadFailed)
			{
				throw new Win32Exception(
					resumeError,
					"Unable to resume the contained Grok ACP process.");
			}

			if (previousSuspendCount != 1)
			{
				throw new InvalidOperationException(
					$"The Grok ACP process had an unexpected suspend count of {previousSuspendCount}.");
			}

			WindowsGrokAcpProcess result = new(
				_containmentState,
				job,
				processHandle,
				inputStream,
				outputStream,
				errorStream);
			job = null;
			processHandle = null;
			inputStream = null;
			outputStream = null;
			errorStream = null;
			ownershipTransferred = true;
			return result;
		}
		catch (Exception startException)
		{
			if ((job is not null) && (processHandle is not null))
			{
				bool isTerminationConfirmed =
					await TryTerminateFailedStartAsync(
					job,
					processHandle,
					isJobMembershipVerified);

				if (!isTerminationConfirmed)
				{
					_containmentState.MarkCompromised();
					throw new GrokProcessContainmentException(
						"Failed Grok ACP startup could not be positively contained.",
						startException);
				}
			}

			throw;
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
				TryDispose(inputStream);
				TryDispose(outputStream);
				TryDispose(errorStream);
				processHandle?.Dispose();
				job?.Dispose();
			}
		}
	}

	private static ValidatedLaunchOptions ValidateLaunchOptions(
		GrokAcpLaunchOptions options)
	{
		if ((options.Command != GrokContainedCommand.AcpStdio) &&
			(options.Command != GrokContainedCommand.Version))
		{
			throw new ArgumentOutOfRangeException(
				nameof(options),
				options.Command,
				"Unknown contained Grok command.");
		}

		string executablePath = NormalizeAbsolutePath(
			options.ExecutablePath,
			"Grok ACP executable");
		string homeDirectory = NormalizeAbsolutePath(
			options.HomeDirectory,
			"Grok private home");
		string workingDirectory = NormalizeAbsolutePath(
			options.WorkingDirectory,
			"Grok blank workspace");

		if (!string.Equals(
				Path.GetExtension(executablePath),
				".exe",
				StringComparison.OrdinalIgnoreCase) ||
			!File.Exists(executablePath))
		{
			throw new ArgumentException(
				"The Grok ACP executable must be an existing absolute EXE path.",
				nameof(options));
		}

		if (!IsExistingNonReparseDirectory(homeDirectory) ||
			!AntigravityPrivateKeyAcl.IsPrivateDirectory(homeDirectory) ||
			!IsExistingNonReparseDirectory(workingDirectory) ||
			!AntigravityPrivateKeyAcl.IsPrivateDirectory(workingDirectory))
		{
			throw new ArgumentException(
				"Grok ACP directories must retain their private non-reparse boundary.",
				nameof(options));
		}

		if (string.Equals(
			homeDirectory,
			workingDirectory,
			StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				"The Grok private home and blank workspace must be distinct.",
				nameof(options));
		}

		string temporaryDirectory = Path.Combine(homeDirectory, "tmp");

		if (!IsExistingNonReparseDirectory(temporaryDirectory) ||
			!AntigravityPrivateKeyAcl.IsPrivateDirectory(temporaryDirectory))
		{
			throw new ArgumentException(
				"The private Grok temporary directory boundary is unavailable.",
				nameof(options));
		}

		try
		{
			if (Directory.EnumerateFileSystemEntries(workingDirectory).Any())
			{
				throw new ArgumentException(
					"The Grok ACP workspace must be blank.",
					nameof(options));
			}
		}
		catch (ArgumentException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is UnauthorizedAccessException))
		{
			throw new ArgumentException(
				"The Grok ACP workspace cannot be verified as blank.",
				nameof(options),
				exception);
		}

		return new ValidatedLaunchOptions(
			executablePath,
			homeDirectory,
			workingDirectory,
			options.Command);
	}

	private static string NormalizeAbsolutePath(string path, string description)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException(
				$"The {description} must use a fully qualified path.",
				nameof(path));
		}

		return Path.GetFullPath(path);
	}

	private static bool IsExistingNonReparseDirectory(string path)
	{
		try
		{
			DirectoryInfo directory = new(path);
			return directory.Exists &&
				((directory.Attributes & FileAttributes.ReparsePoint) == 0);
		}
		catch
		{
			return false;
		}
	}

	internal static void CreateRedirectedPipe(
		out SafeFileHandle readHandle,
		out SafeFileHandle writeHandle,
		bool parentReads)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Grok ACP redirection requires Windows named pipes.");
		}

		using CurrentTokenPipeSecurityDescriptor securityDescriptor =
			CurrentTokenPipeSecurityDescriptor.Create();
		SecurityAttributes parentAttributes = new()
		{
			Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
			SecurityDescriptor = securityDescriptor.Pointer,
			InheritHandle = false
		};
		SecurityAttributes childAttributes = new()
		{
			Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
			SecurityDescriptor = securityDescriptor.Pointer,
			InheritHandle = true
		};
		string pipeName = $"\\\\.\\pipe\\AiUsageDashboard.Grok.{Environment.ProcessId}.{Guid.NewGuid():N}";
		uint parentAccess = parentReads
			? PipeAccessInbound
			: PipeAccessOutbound;
		uint childAccess = parentReads
			? GenericWrite
			: GenericRead;
		SafeFileHandle parentHandle = NativeMethods.CreateNamedPipe(
			pipeName,
			parentAccess |
				FileFlagFirstPipeInstance |
				FileFlagOverlapped,
			PipeRejectRemoteClients,
			1,
			RedirectedPipeBufferSize,
			RedirectedPipeBufferSize,
			0,
			ref parentAttributes);

		if (parentHandle.IsInvalid)
		{
			int errorCode = Marshal.GetLastWin32Error();
			parentHandle.Dispose();
			throw new Win32Exception(
				errorCode,
				$"Unable to create an asynchronous Grok ACP parent pipe handle (Windows error {errorCode}).");
		}

		SafeFileHandle childHandle = NativeMethods.CreateFile(
			pipeName,
			childAccess,
			0,
			ref childAttributes,
			OpenExisting,
			0,
			IntPtr.Zero);

		if (childHandle.IsInvalid)
		{
			int errorCode = Marshal.GetLastWin32Error();
			childHandle.Dispose();
			parentHandle.Dispose();
			throw new Win32Exception(
				errorCode,
				$"Unable to open the Grok ACP child pipe handle (Windows error {errorCode}).");
		}

		if (!NativeMethods.ConnectNamedPipe(parentHandle, IntPtr.Zero))
		{
			int errorCode = Marshal.GetLastWin32Error();

			if (errorCode != ErrorPipeConnected)
			{
				childHandle.Dispose();
				parentHandle.Dispose();
				throw new Win32Exception(
					errorCode,
					"Unable to connect the Grok ACP redirection pipe.");
			}
		}

		if (!NativeMethods.SetHandleInformation(
			parentHandle,
			HandleFlagInherit,
			0))
		{
			int errorCode = Marshal.GetLastWin32Error();
			childHandle.Dispose();
			parentHandle.Dispose();
			throw new Win32Exception(
				errorCode,
				"Unable to restrict a Grok ACP parent pipe handle.");
		}

		if (parentReads)
		{
			readHandle = parentHandle;
			writeHandle = childHandle;
		}
		else
		{
			readHandle = childHandle;
			writeHandle = parentHandle;
		}
	}

	internal static StringBuilder BuildCommandLine(
		string executablePath,
		GrokContainedCommand command)
	{
		StringBuilder commandLine = new();
		AppendCommandLineArgument(commandLine, executablePath);
		AppendCommandLineArgument(commandLine, "--no-auto-update");

		switch (command)
		{
			case GrokContainedCommand.AcpStdio:
				AppendCommandLineArgument(commandLine, "agent");
				AppendCommandLineArgument(commandLine, "stdio");
				break;
			case GrokContainedCommand.Version:
				AppendCommandLineArgument(commandLine, "--version");
				break;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(command),
					command,
					"Unknown contained Grok command.");
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
				"Grok ACP process arguments must not contain NUL.",
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

	private static string BuildEnvironmentBlock(ValidatedLaunchOptions options)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = options.ExecutablePath,
			UseShellExecute = false
		};
		GrokProcessEnvironment.Apply(startInfo, options.HomeDirectory);
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
					"The Grok ACP environment contains an invalid entry.");
			}

			block.Append(name);
			block.Append('=');
			block.Append(value);
			block.Append('\0');
		}

		block.Append('\0');

		if (block.Length > MaximumEnvironmentBlockCharacters)
		{
			throw new InvalidOperationException(
				"The Grok ACP environment exceeds the Windows process limit.");
		}

		return block.ToString();
	}

	private static async Task<int> WaitForProcessExitAsync(
		SafeKernelHandle processHandle,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			uint waitResult = NativeMethods.WaitForSingleObject(processHandle, 0);

			if (waitResult == WaitObject0)
			{
				if (!NativeMethods.GetExitCodeProcess(processHandle, out uint exitCode))
				{
					throw new Win32Exception(
						Marshal.GetLastWin32Error(),
						"Unable to read the Grok ACP process exit code.");
				}

				return unchecked((int)exitCode);
			}

			if (waitResult == WaitFailed)
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"Unable to wait for the Grok ACP process.");
			}

			if (waitResult != WaitTimeout)
			{
				throw new InvalidOperationException(
					$"The Grok ACP process wait returned unexpected status 0x{waitResult:X8}.");
			}

			await Task.Delay(ProcessPollInterval, cancellationToken);
		}
	}

	private static async Task<bool> TryTerminateFailedStartAsync(
		WindowsProcessJob job,
		SafeKernelHandle processHandle,
		bool isJobMembershipVerified)
	{
		try
		{
			if (!isJobMembershipVerified)
			{
				_ = NativeMethods.TerminateProcess(processHandle, ForcedExitCode);
			}

			if (job.GetActiveProcessCount() > 0)
			{
				job.Terminate(ForcedExitCode);
			}

			bool isJobEmpty = await job.WaitForPositiveEmptyConfirmationAsync(
				DefaultTerminationTimeout,
				CancellationToken.None);
			bool hasProcessExited = await WaitForPositiveProcessExitAsync(
				processHandle,
				DefaultTerminationTimeout);
			return isJobEmpty && hasProcessExited;
		}
		catch
		{
			return false;
		}
	}

	private static async Task<bool> WaitForPositiveProcessExitAsync(
		SafeKernelHandle processHandle,
		TimeSpan timeout)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			try
			{
				if (NativeMethods.WaitForSingleObject(processHandle, 0) == WaitObject0)
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

			await Task.Delay(
				remaining < ProcessPollInterval
					? remaining
					: ProcessPollInterval,
				CancellationToken.None);
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
			// Cleanup remains best-effort after the Job termination barrier.
		}
	}

}
