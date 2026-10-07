using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal enum WindowsOfficialCliExecutableStagingFailureReason
{
	InvalidSourcePath,
	SourceNotFound,
	UnsafeSourcePath,
	InvalidSourceSize,
	SourceChanged,
	UnsafeTrustedRoot,
	UnsafeTemporaryFile,
	UnsafeStagedFile,
	InvalidStagedContent,
	InvalidSignature,
	StageFailed,
	ProtectionFailed
}

internal sealed class WindowsOfficialCliExecutableStagingException :
	InvalidOperationException
{
	internal WindowsOfficialCliExecutableStagingFailureReason FailureReason { get; }

	internal WindowsOfficialCliExecutableStagingException(
		WindowsOfficialCliExecutableStagingFailureReason failureReason,
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
		FailureReason = failureReason;
	}
}

internal sealed class WindowsOfficialCliExecutableLease : IDisposable
{
	private const uint DuplicateSameAccess = 0x00000002;
	private readonly bool _wasCreatedProtected;
	private readonly object _sync = new();
	private bool _isDisposed;
	private FileStream? _stream;

	internal string ExecutablePath { get; }
	internal bool IsProtected
	{
		get
		{
			lock (_sync)
			{
				return !_isDisposed && (_stream is not null);
			}
		}
	}

	private WindowsOfficialCliExecutableLease(
		string executablePath,
		FileStream? stream)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ExecutablePath = executablePath;
		_stream = stream;
		_wasCreatedProtected = stream is not null;
	}

	internal static WindowsOfficialCliExecutableLease CreateProtected(
		string executablePath,
		FileStream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);

		if (!stream.CanRead)
		{
			throw new ArgumentException(
				"A protected executable lease requires a readable stream.",
				nameof(stream));
		}

		return new WindowsOfficialCliExecutableLease(executablePath, stream);
	}

	internal static WindowsOfficialCliExecutableLease CreateUnprotected(
		string executablePath)
	{
		return new WindowsOfficialCliExecutableLease(executablePath, stream: null);
	}

	internal WindowsOfficialCliExecutableLease Duplicate()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			if (!_wasCreatedProtected)
			{
				return CreateUnprotected(ExecutablePath);
			}

			FileStream stream = _stream ??
				throw new ObjectDisposedException(nameof(WindowsOfficialCliExecutableLease));
			SafeFileHandle duplicatedHandle = DuplicateFileHandle(
				stream.SafeFileHandle);

			try
			{
				FileStream duplicatedStream = new(
					duplicatedHandle,
					FileAccess.Read,
					bufferSize: 1,
					isAsync: false);
				return CreateProtected(ExecutablePath, duplicatedStream);
			}
			catch
			{
				duplicatedHandle.Dispose();
				throw;
			}
		}
	}

	public void Dispose()
	{
		FileStream? stream;

		lock (_sync)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			stream = _stream;
			_stream = null;
		}

		stream?.Dispose();
	}

	private static SafeFileHandle DuplicateFileHandle(
		SafeFileHandle sourceHandle)
	{
		IntPtr currentProcess = GetCurrentProcess();

		if (!DuplicateHandle(
				currentProcess,
				sourceHandle,
				currentProcess,
				out IntPtr duplicatedHandle,
				desiredAccess: 0,
				inheritHandle: false,
				DuplicateSameAccess))
		{
			throw new IOException(
				"The protected official CLI executable handle could not be duplicated.",
				new Win32Exception(Marshal.GetLastWin32Error()));
		}

		return new SafeFileHandle(duplicatedHandle, ownsHandle: true);
	}

	[DllImport("kernel32.dll", ExactSpelling = true)]
	private static extern IntPtr GetCurrentProcess();

	[DllImport(
		"kernel32.dll",
		ExactSpelling = true,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DuplicateHandle(
		IntPtr sourceProcessHandle,
		SafeFileHandle sourceHandle,
		IntPtr targetProcessHandle,
		out IntPtr targetHandle,
		uint desiredAccess,
		[MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
		uint options);
}

internal sealed record WindowsOfficialCliExecutableTrustStamp(
	long Length,
	long CreationTime,
	long LastWriteTime,
	long ChangeTime,
	ulong VolumeSerialNumber,
	ulong FileIdLowPart,
	ulong FileIdHighPart);

internal sealed class WindowsOfficialCliExecutableStager : IDisposable
{
	private enum FileInfoByHandleClass
	{
		FileBasicInfo = 0,
		FileIdInfo = 18
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileBasicInfo
	{
		internal long CreationTime;
		internal long LastAccessTime;
		internal long LastWriteTime;
		internal long ChangeTime;
		internal uint FileAttributes;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileId128
	{
		internal ulong LowPart;
		internal ulong HighPart;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileIdInfo
	{
		internal ulong VolumeSerialNumber;
		internal FileId128 FileId;
	}

	internal const long MaximumExecutableSizeBytes = 512L * 1024 * 1024;
	private readonly string _expectedSignerCommonName;
	private readonly Func<string, WindowsAuthenticodeInspection> _inspectSignature;
	private readonly Func<string, bool> _isCanonicalNonReparseFile;
	private readonly Func<string, bool> _isFixedDrivePath;
	private readonly Func<string, bool> _isPathAclSafe;
	private readonly Func<FileStream, WindowsOfficialCliExecutableTrustStamp>
		_readTrustStamp;
	private readonly object _sync = new();
	private bool _isDisposed;

	internal WindowsOfficialCliExecutableStager(
		string expectedSignerCommonName,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, bool> isPathAclSafe,
		Func<FileStream, WindowsOfficialCliExecutableTrustStamp>?
			readTrustStamp = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedSignerCommonName);
		_expectedSignerCommonName = expectedSignerCommonName;
		_inspectSignature = inspectSignature ??
			throw new ArgumentNullException(nameof(inspectSignature));
		_isCanonicalNonReparseFile = isCanonicalNonReparseFile ??
			throw new ArgumentNullException(nameof(isCanonicalNonReparseFile));
		_isFixedDrivePath = isFixedDrivePath ??
			throw new ArgumentNullException(nameof(isFixedDrivePath));
		_isPathAclSafe = isPathAclSafe ??
			throw new ArgumentNullException(nameof(isPathAclSafe));
		_readTrustStamp = readTrustStamp ?? ReadTrustStamp;
	}

	internal WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutablePath);
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfDisposed();

		try
		{
			string fullPath = NormalizeAndValidateSourcePath(sourceExecutablePath);
			FileStream? lockedStream = OpenLockedSource(fullPath);

			try
			{
				WindowsOfficialCliExecutableTrustStamp trustStamp =
					_readTrustStamp(lockedStream);
				EnsureValidSize(trustStamp);
				EnsurePathMatchesLockedSource(fullPath, trustStamp);
				cancellationToken.ThrowIfCancellationRequested();

				WindowsAuthenticodeInspection signature =
					InspectSignature(fullPath);
				cancellationToken.ThrowIfCancellationRequested();

				if ((_readTrustStamp(lockedStream) != trustStamp) ||
					!IsSourcePathSafe(fullPath))
				{
					throw CreateSourceChangedException();
				}

				EnsurePathMatchesLockedSource(fullPath, trustStamp);
				EnsureExpectedSigner(signature);

				WindowsOfficialCliExecutableLease lease =
					WindowsOfficialCliExecutableLease.CreateProtected(
						fullPath,
						lockedStream);
				lockedStream = null;
				return lease;
			}
			finally
			{
				lockedStream?.Dispose();
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (WindowsOfficialCliExecutableStagingException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.ProtectionFailed,
				"The official CLI source executable could not be protected and verified.",
				exception);
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			_isDisposed = true;
		}
	}

	private string NormalizeAndValidateSourcePath(string sourceExecutablePath)
	{
		string fullPath;

		try
		{
			fullPath = Path.GetFullPath(sourceExecutablePath);
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSourcePath,
				"The official CLI source path is invalid.",
				exception);
		}

		if (!Path.IsPathFullyQualified(sourceExecutablePath) ||
			!string.Equals(
				Path.GetExtension(fullPath),
				".exe",
				StringComparison.OrdinalIgnoreCase) ||
			!_isFixedDrivePath(fullPath))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSourcePath,
				"The official CLI source must be an absolute EXE path on a fixed drive.");
		}

		if (!File.Exists(fullPath))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound,
				"The official CLI source executable was not found.");
		}

		if (!IsSourcePathSafe(fullPath))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeSourcePath,
				"The official CLI source path or access control is unsafe.");
		}

		return fullPath;
	}

	private static FileStream OpenLockedSource(string fullPath)
	{
		try
		{
			return new FileStream(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 1,
				FileOptions.RandomAccess);
		}
		catch (FileNotFoundException exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound,
				"The official CLI source executable was not found.",
				exception);
		}
		catch (DirectoryNotFoundException exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound,
				"The official CLI source executable was not found.",
				exception);
		}
	}

	private void EnsurePathMatchesLockedSource(
		string fullPath,
		WindowsOfficialCliExecutableTrustStamp expectedTrustStamp)
	{
		if (!IsSourcePathSafe(fullPath))
		{
			throw CreateSourceChangedException();
		}

		try
		{
			using FileStream comparisonStream = OpenLockedSource(fullPath);

			if (_readTrustStamp(comparisonStream) != expectedTrustStamp)
			{
				throw CreateSourceChangedException();
			}
		}
		catch (WindowsOfficialCliExecutableStagingException exception) when (
			exception.FailureReason ==
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound)
		{
			throw CreateSourceChangedException(exception);
		}
		catch (IOException exception)
		{
			throw CreateSourceChangedException(exception);
		}
	}

	private WindowsAuthenticodeInspection InspectSignature(string fullPath)
	{
		try
		{
			return _inspectSignature(fullPath);
		}
		catch (Exception exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The official CLI source signature could not be verified.",
				exception);
		}
	}

	private void EnsureExpectedSigner(WindowsAuthenticodeInspection signature)
	{
		if ((signature.WinVerifyTrustStatus != 0) ||
			!WindowsOfficialCliExecutableValidator.HasExpectedSigner(
				signature.SignerSubject,
				_expectedSignerCommonName))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The official CLI source signature or publisher is invalid.");
		}
	}

	private static void EnsureValidSize(
		WindowsOfficialCliExecutableTrustStamp trustStamp)
	{
		if ((trustStamp.Length <= 0) ||
			(trustStamp.Length > MaximumExecutableSizeBytes))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSourceSize,
				"The official CLI source size is outside the safe range.");
		}
	}

	private bool IsSourcePathSafe(string fullPath)
	{
		return _isFixedDrivePath(fullPath) &&
			_isCanonicalNonReparseFile(fullPath) &&
			_isPathAclSafe(fullPath);
	}

	private void ThrowIfDisposed()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
		}
	}

	private static WindowsOfficialCliExecutableStagingException
		CreateSourceChangedException(Exception? innerException = null)
	{
		return new WindowsOfficialCliExecutableStagingException(
			WindowsOfficialCliExecutableStagingFailureReason.SourceChanged,
			"The official CLI source identity changed during safety validation.",
			innerException);
	}

	internal static WindowsOfficialCliExecutableTrustStamp ReadTrustStamp(
		FileStream stream)
	{
		SafeFileHandle handle = stream.SafeFileHandle;

		if (!GetFileInformationByHandleEx(
				handle,
				FileInfoByHandleClass.FileBasicInfo,
				out FileBasicInfo basicInfo,
				checked((uint)Marshal.SizeOf<FileBasicInfo>())))
		{
			throw new IOException(
				"The official CLI source metadata could not be read.",
				new Win32Exception(Marshal.GetLastWin32Error()));
		}

		if (!GetFileInformationByHandleEx(
				handle,
				FileInfoByHandleClass.FileIdInfo,
				out FileIdInfo fileIdInfo,
				checked((uint)Marshal.SizeOf<FileIdInfo>())))
		{
			throw new IOException(
				"The official CLI source identity could not be read.",
				new Win32Exception(Marshal.GetLastWin32Error()));
		}

		return new WindowsOfficialCliExecutableTrustStamp(
			stream.Length,
			basicInfo.CreationTime,
			basicInfo.LastWriteTime,
			basicInfo.ChangeTime,
			fileIdInfo.VolumeSerialNumber,
			fileIdInfo.FileId.LowPart,
			fileIdInfo.FileId.HighPart);
	}

	[DllImport(
		"kernel32.dll",
		ExactSpelling = true,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandleEx(
		SafeFileHandle fileHandle,
		FileInfoByHandleClass fileInformationClass,
		out FileBasicInfo fileInformation,
		uint bufferSize);

	[DllImport(
		"kernel32.dll",
		ExactSpelling = true,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandleEx(
		SafeFileHandle fileHandle,
		FileInfoByHandleClass fileInformationClass,
		out FileIdInfo fileInformation,
		uint bufferSize);
}
