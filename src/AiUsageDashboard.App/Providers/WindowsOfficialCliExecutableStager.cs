using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
	StageFailed
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

			FileStream stream = new(
				ExecutablePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 1,
				FileOptions.RandomAccess);
			return CreateProtected(ExecutablePath, stream);
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
}

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

	private sealed record FileTrustStamp(
		long Length,
		long CreationTime,
		long LastWriteTime,
		long ChangeTime,
		ulong VolumeSerialNumber,
		ulong FileIdLowPart,
		ulong FileIdHighPart);
	private sealed record SourceSnapshot(
		string FullPath,
		FileTrustStamp TrustStamp);

	private sealed class VerifiedStagedFile : IDisposable
	{
		private FileStream? _lockedStream;

		internal FileTrustStamp TrustStamp { get; }

		internal VerifiedStagedFile(
			FileTrustStamp trustStamp,
			FileStream lockedStream)
		{
			TrustStamp = trustStamp;
			_lockedStream = lockedStream;
		}

		internal FileStream TakeLockedStream()
		{
			return Interlocked.Exchange(ref _lockedStream, null) ??
				throw new ObjectDisposedException(nameof(VerifiedStagedFile));
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _lockedStream, null)?.Dispose();
		}
	}

	private sealed class StageResult : IDisposable
	{
		internal string ContentHash { get; }
		internal string StagedPath { get; }
		internal string TrustedRoot { get; }
		internal VerifiedStagedFile VerifiedFile { get; }

		internal StageResult(
			string stagedPath,
			string contentHash,
			VerifiedStagedFile verifiedFile,
			string trustedRoot)
		{
			StagedPath = stagedPath;
			ContentHash = contentHash;
			VerifiedFile = verifiedFile;
			TrustedRoot = trustedRoot;
		}

		public void Dispose()
		{
			VerifiedFile.Dispose();
		}
	}

	internal const long MaximumExecutableSizeBytes = 512L * 1024 * 1024;
	private const int CopyBufferSize = 1024 * 1024;
	private static readonly TimeSpan TemporaryFileRetentionAge =
		TimeSpan.FromHours(1);
	private readonly string _expectedSignerCommonName;
	private readonly Func<string, WindowsAuthenticodeInspection> _inspectSignature;
	private readonly Func<string, bool> _isCanonicalNonReparseFile;
	private readonly Func<string, bool> _isFixedDrivePath;
	private readonly Func<string, string, bool> _isPathAclSafe;
	private readonly Func<string, string, bool> _isRootAclSafe;
	private readonly SemaphoreSlim _stageGate = new(1, 1);
	private readonly string _stagedFilePrefix;
	private readonly string _temporaryFilePrefix;
	private readonly Func<string> _trustedRootResolver;
	private readonly Func<string, bool> _tryPrepareTrustedRoot;
	private readonly Func<string, bool> _tryProtectFile;
	private SourceSnapshot? _cachedSource;
	private string? _cachedStagedHash;
	private string? _cachedStagedPath;
	private FileTrustStamp? _cachedStagedTrustStamp;
	private FileStream? _currentStagedBaseStream;
	private bool _isDisposed;
	private string? _retainedPreviousStagedPath;

	internal WindowsOfficialCliExecutableStager(
		string expectedSignerCommonName,
		string stagedFilePrefix,
		string temporaryFilePrefix,
		Func<string> trustedRootResolver,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, string, bool> isPathAclSafe,
		Func<string, string, bool> isRootAclSafe,
		Func<string, bool> tryPrepareTrustedRoot,
		Func<string, bool> tryProtectFile)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedSignerCommonName);
		ArgumentException.ThrowIfNullOrWhiteSpace(stagedFilePrefix);
		ArgumentException.ThrowIfNullOrWhiteSpace(temporaryFilePrefix);
		_expectedSignerCommonName = expectedSignerCommonName;
		_stagedFilePrefix = stagedFilePrefix;
		_temporaryFilePrefix = temporaryFilePrefix;
		_trustedRootResolver = trustedRootResolver ??
			throw new ArgumentNullException(nameof(trustedRootResolver));
		_inspectSignature = inspectSignature ??
			throw new ArgumentNullException(nameof(inspectSignature));
		_isCanonicalNonReparseFile = isCanonicalNonReparseFile ??
			throw new ArgumentNullException(nameof(isCanonicalNonReparseFile));
		_isFixedDrivePath = isFixedDrivePath ??
			throw new ArgumentNullException(nameof(isFixedDrivePath));
		_isPathAclSafe = isPathAclSafe ??
			throw new ArgumentNullException(nameof(isPathAclSafe));
		_isRootAclSafe = isRootAclSafe ??
			throw new ArgumentNullException(nameof(isRootAclSafe));
		_tryPrepareTrustedRoot = tryPrepareTrustedRoot ??
			throw new ArgumentNullException(nameof(tryPrepareTrustedRoot));
		_tryProtectFile = tryProtectFile ??
			throw new ArgumentNullException(nameof(tryProtectFile));
	}

	internal WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutablePath);
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			_stageGate.Wait(cancellationToken);

			try
			{
				ThrowIfDisposed();
				cancellationToken.ThrowIfCancellationRequested();
				SourceSnapshot source = InspectSourcePath(sourceExecutablePath);
				string trustedRoot = ResolveAndPrepareTrustedRoot();
				TryDeleteStaleTemporaryFiles(trustedRoot, DateTime.UtcNow);

				if ((_cachedSource == source) &&
					(_cachedStagedHash is not null) &&
					(_cachedStagedPath is not null) &&
					(_cachedStagedTrustStamp is not null) &&
					(_currentStagedBaseStream is not null) &&
					File.Exists(_cachedStagedPath))
				{
					WindowsOfficialCliExecutableLease? cachedLease =
						TryCreateCachedLease(
							_cachedStagedPath,
							trustedRoot,
							_cachedStagedTrustStamp,
							_currentStagedBaseStream,
							cancellationToken);

					if (cachedLease is not null)
					{
						TryPruneOldGenerations(
							trustedRoot,
							_cachedStagedPath,
							_retainedPreviousStagedPath);
						return cachedLease;
					}
				}

				using StageResult result = StageCore(
					source,
					trustedRoot,
					cancellationToken);
				return InstallStagedGeneration(source, result, cancellationToken);
			}
			finally
			{
				_stageGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (ObjectDisposedException)
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
				WindowsOfficialCliExecutableStagingFailureReason.StageFailed,
				"The protected official CLI executable could not be staged or verified.",
				exception);
		}
	}

	public void Dispose()
	{
		_stageGate.Wait();

		try
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			_currentStagedBaseStream?.Dispose();
			_currentStagedBaseStream = null;
		}
		finally
		{
			_stageGate.Release();
		}
	}

	private StageResult StageCore(
		SourceSnapshot source,
		string trustedRoot,
		CancellationToken cancellationToken)
	{
		string temporaryPath = Path.Combine(
			trustedRoot,
			$"{_temporaryFilePrefix}{Guid.NewGuid():N}.exe");

		try
		{
			using FileStream sourceStream = OpenLockedSource(source);
			EnsureExpectedSignature(source.FullPath);
			string sourceHash = CopyToProtectedTemporaryFile(
				sourceStream,
				temporaryPath,
				trustedRoot,
				cancellationToken);
			using (VerifiedStagedFile temporaryFile = EnsureStagedFile(
				temporaryPath,
				trustedRoot,
				sourceHash,
				cancellationToken))
			{
			}

			string stagedPath = Path.Combine(
				trustedRoot,
				$"{_stagedFilePrefix}{sourceHash.ToLowerInvariant()}.exe");

			if (File.Exists(stagedPath))
			{
				VerifiedStagedFile verifiedFile = EnsureStagedFile(
					stagedPath,
					trustedRoot,
					sourceHash,
					cancellationToken);
				return new StageResult(
					stagedPath,
					sourceHash,
					verifiedFile,
					trustedRoot);
			}

			try
			{
				File.Move(temporaryPath, stagedPath);
			}
			catch (IOException) when (File.Exists(stagedPath))
			{
				VerifiedStagedFile verifiedFile = EnsureStagedFile(
					stagedPath,
					trustedRoot,
					sourceHash,
					cancellationToken);
				return new StageResult(
					stagedPath,
					sourceHash,
					verifiedFile,
					trustedRoot);
			}

			VerifiedStagedFile committedFile = EnsureStagedFile(
				stagedPath,
				trustedRoot,
				sourceHash,
				cancellationToken);
			return new StageResult(
				stagedPath,
				sourceHash,
				committedFile,
				trustedRoot);
		}
		finally
		{
			TryDeleteTemporaryFile(temporaryPath, trustedRoot);
		}
	}

	private SourceSnapshot InspectSourcePath(string sourceExecutablePath)
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

		if (!_isCanonicalNonReparseFile(fullPath))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeSourcePath,
				"The official CLI source path contains a reparse point.");
		}

		using FileStream stream = new(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 1,
			FileOptions.RandomAccess);
		FileTrustStamp trustStamp = ReadTrustStamp(stream);

		if ((trustStamp.Length <= 0) ||
			(trustStamp.Length > MaximumExecutableSizeBytes))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSourceSize,
				"The official CLI source size is outside the safe range.");
		}

		return new SourceSnapshot(
			fullPath,
			trustStamp);
	}

	private string ResolveAndPrepareTrustedRoot()
	{
		string trustedRoot = Path.GetFullPath(_trustedRootResolver());

		if (!Path.IsPathFullyQualified(trustedRoot) ||
			!_isFixedDrivePath(trustedRoot) ||
			!_tryPrepareTrustedRoot(trustedRoot) ||
			!_isRootAclSafe(trustedRoot, trustedRoot))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeTrustedRoot,
				"The protected official CLI staging root is unsafe.");
		}

		return trustedRoot;
	}

	private FileStream OpenLockedSource(SourceSnapshot source)
	{
		FileStream stream;

		try
		{
			stream = new FileStream(
				source.FullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				CopyBufferSize,
				FileOptions.SequentialScan);
		}
		catch (FileNotFoundException exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound,
				"The official CLI source executable was not found.",
				exception);
		}

		if ((ReadTrustStamp(stream) != source.TrustStamp) ||
			!_isCanonicalNonReparseFile(source.FullPath))
		{
			stream.Dispose();
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.SourceChanged,
				"The official CLI source changed during safety validation.");
		}

		return stream;
	}

	private string CopyToProtectedTemporaryFile(
		FileStream sourceStream,
		string temporaryPath,
		string trustedRoot,
		CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(
			HashAlgorithmName.SHA256);
		using (FileStream destinationStream = new(
			temporaryPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			CopyBufferSize,
			FileOptions.SequentialScan))
		{
			byte[] buffer = new byte[CopyBufferSize];
			int bytesRead;

			while ((bytesRead = sourceStream.Read(
				buffer,
				0,
				buffer.Length)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				hash.AppendData(buffer, 0, bytesRead);
				destinationStream.Write(buffer, 0, bytesRead);
			}

			destinationStream.Flush(flushToDisk: true);
		}

		if (!_tryProtectFile(temporaryPath) ||
			!_isPathAclSafe(temporaryPath, trustedRoot))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeTemporaryFile,
				"The temporary official CLI copy did not receive safe access control.");
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private WindowsOfficialCliExecutableLease? TryCreateCachedLease(
		string stagedPath,
		string trustedRoot,
		FileTrustStamp expectedTrustStamp,
		FileStream baseStream,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSafeStagedPath(stagedPath, trustedRoot);

		if (!baseStream.CanRead ||
			!string.Equals(
				Path.GetFullPath(baseStream.Name),
				Path.GetFullPath(stagedPath),
				StringComparison.OrdinalIgnoreCase) ||
			(ReadTrustStamp(baseStream) != expectedTrustStamp))
		{
			return null;
		}

		FileStream leaseStream = OpenStagedReadStream(stagedPath);

		try
		{
			if (ReadTrustStamp(leaseStream) != expectedTrustStamp)
			{
				leaseStream.Dispose();
				return null;
			}

			return WindowsOfficialCliExecutableLease.CreateProtected(
				stagedPath,
				leaseStream);
		}
		catch
		{
			leaseStream.Dispose();
			throw;
		}
	}

	private WindowsOfficialCliExecutableLease InstallStagedGeneration(
		SourceSnapshot source,
		StageResult result,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSafeStagedPath(result.StagedPath, result.TrustedRoot);
		FileTrustStamp verifiedTrustStamp = result.VerifiedFile.TrustStamp;
		FileStream newBaseStream = result.VerifiedFile.TakeLockedStream();
		FileStream? leaseStream = null;

		try
		{
			if (ReadTrustStamp(newBaseStream) != verifiedTrustStamp)
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.InvalidStagedContent,
					"The protected official CLI copy changed before it could be locked.");
			}

			leaseStream = OpenStagedReadStream(result.StagedPath);

			if (ReadTrustStamp(leaseStream) != verifiedTrustStamp)
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.InvalidStagedContent,
					"The protected official CLI copy changed before its lease could be created.");
			}
		}
		catch
		{
			leaseStream?.Dispose();
			newBaseStream.Dispose();
			throw;
		}

		WindowsOfficialCliExecutableLease lease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				result.StagedPath,
				leaseStream);
		FileStream? previousBaseStream = _currentStagedBaseStream;
		string? previousStagedPath = _cachedStagedPath;

		_currentStagedBaseStream = newBaseStream;

		if ((previousStagedPath is not null) &&
			!string.Equals(
				previousStagedPath,
				result.StagedPath,
				StringComparison.OrdinalIgnoreCase))
		{
			_retainedPreviousStagedPath = previousStagedPath;
		}

		_cachedSource = source;
		_cachedStagedHash = result.ContentHash;
		_cachedStagedPath = result.StagedPath;
		_cachedStagedTrustStamp = verifiedTrustStamp;

		try
		{
			previousBaseStream?.Dispose();
		}
		catch
		{
			// 新 generation 已受 base 與 consumer lease 保護，舊 handle 交由終結器回收。
		}

		TryPruneOldGenerations(
			result.TrustedRoot,
			result.StagedPath,
			_retainedPreviousStagedPath);
		return lease;
	}

	private static FileStream OpenStagedReadStream(string stagedPath)
	{
		return new FileStream(
			stagedPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 1,
			FileOptions.RandomAccess);
	}

	private VerifiedStagedFile EnsureStagedFile(
		string stagedPath,
		string trustedRoot,
		string expectedHash,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		EnsureSafeStagedPath(stagedPath, trustedRoot);

		FileStream stream = new(
			stagedPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			CopyBufferSize,
			FileOptions.SequentialScan);
		try
		{
			string actualHash = ComputeHash(stream, cancellationToken);

			if (!string.Equals(
				actualHash,
				expectedHash,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.InvalidStagedContent,
					"The protected official CLI copy content does not match its source.");
			}

			EnsureExpectedSignature(stagedPath);
			return new VerifiedStagedFile(ReadTrustStamp(stream), stream);
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	private void EnsureSafeStagedPath(string stagedPath, string trustedRoot)
	{
		if (!_isCanonicalNonReparseFile(stagedPath) ||
			!_isPathAclSafe(stagedPath, trustedRoot))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeStagedFile,
				"The protected official CLI copy path or access control is unsafe.");
		}
	}

	private void EnsureExpectedSignature(string executablePath)
	{
		WindowsAuthenticodeInspection signature;

		try
		{
			signature = _inspectSignature(executablePath);
		}
		catch (Exception exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The official CLI signature could not be verified.",
				exception);
		}

		if ((signature.WinVerifyTrustStatus != 0) ||
			!WindowsOfficialCliExecutableValidator.HasExpectedSigner(
				signature.SignerSubject,
				_expectedSignerCommonName))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The official CLI signature is invalid.");
		}
	}

	private static string ComputeHash(
		FileStream stream,
		CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(
			HashAlgorithmName.SHA256);
		byte[] buffer = new byte[CopyBufferSize];
		int bytesRead;

		while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(buffer, 0, bytesRead);
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static FileTrustStamp ReadTrustStamp(FileStream stream)
	{
		SafeFileHandle handle = stream.SafeFileHandle;

		if (!GetFileInformationByHandleEx(
				handle,
				FileInfoByHandleClass.FileBasicInfo,
				out FileBasicInfo basicInfo,
				checked((uint)Marshal.SizeOf<FileBasicInfo>())))
		{
			throw new IOException(
				"The executable file metadata could not be read.",
				Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
		}

		if (!GetFileInformationByHandleEx(
				handle,
				FileInfoByHandleClass.FileIdInfo,
				out FileIdInfo fileIdInfo,
				checked((uint)Marshal.SizeOf<FileIdInfo>())))
		{
			throw new IOException(
				"The executable file identity could not be read.",
				Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
		}

		return new FileTrustStamp(
			stream.Length,
			basicInfo.CreationTime,
			basicInfo.LastWriteTime,
			basicInfo.ChangeTime,
			fileIdInfo.VolumeSerialNumber,
			fileIdInfo.FileId.LowPart,
			fileIdInfo.FileId.HighPart);
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
	}

	private void TryDeleteStaleTemporaryFiles(
		string trustedRoot,
		DateTime utcNow)
	{
		try
		{
			string fullRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(trustedRoot));

			if (!_isRootAclSafe(fullRoot, fullRoot))
			{
				return;
			}

			DateTime staleBeforeUtc = utcNow - TemporaryFileRetentionAge;

			foreach (string path in Directory.EnumerateFiles(
				fullRoot,
				"*.exe",
				SearchOption.TopDirectoryOnly))
			{
				try
				{
					if (!IsTemporaryFilePath(path, fullRoot) ||
						!_isCanonicalNonReparseFile(path) ||
						!_isPathAclSafe(path, fullRoot))
					{
						continue;
					}

					FileInfo candidate = new(path);

					if (candidate.LastWriteTimeUtc > staleBeforeUtc)
					{
						continue;
					}

					candidate.Delete();
				}
				catch
				{
					// Locked 或競態中的 temporary file 留待後續 staging 再清理。
				}
			}
		}
		catch
		{
			// Crash-left temporary 清理不得影響 executable staging。
		}
	}

	private bool IsTemporaryFilePath(string path, string trustedRoot)
	{
		string fullPath = Path.GetFullPath(path);

		if (!string.Equals(
				Path.GetDirectoryName(fullPath),
				trustedRoot,
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				Path.GetExtension(fullPath),
				".exe",
				StringComparison.Ordinal))
		{
			return false;
		}

		string fileName = Path.GetFileNameWithoutExtension(fullPath);

		if (!fileName.StartsWith(_temporaryFilePrefix, StringComparison.Ordinal) ||
			(fileName.Length != (_temporaryFilePrefix.Length + 32)))
		{
			return false;
		}

		return fileName.AsSpan(_temporaryFilePrefix.Length)
			.ToArray()
			.All(Uri.IsHexDigit);
	}

	private void TryPruneOldGenerations(
		string trustedRoot,
		string currentStagedPath,
		string? previousStagedPath)
	{
		try
		{
			string fullRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(trustedRoot));
			string currentPath = Path.GetFullPath(currentStagedPath);

			if (!_isRootAclSafe(fullRoot, fullRoot) ||
				!IsStagedGenerationPath(currentPath, fullRoot))
			{
				return;
			}

			List<FileInfo> candidates = Directory
				.EnumerateFiles(fullRoot, "*.exe", SearchOption.TopDirectoryOnly)
				.Where(path => IsStagedGenerationPath(path, fullRoot))
				.Where(path => !string.Equals(
					path,
					currentPath,
					StringComparison.OrdinalIgnoreCase))
				.Where(path => _isCanonicalNonReparseFile(path) &&
					_isPathAclSafe(path, fullRoot))
				.Select(path => new FileInfo(path))
				.OrderByDescending(file => file.CreationTimeUtc)
				.ThenByDescending(file => file.LastWriteTimeUtc)
				.ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
				.ToList();
			string? retainedPath = candidates
				.Select(file => file.FullName)
				.FirstOrDefault(path => previousStagedPath is not null &&
					string.Equals(
						path,
						Path.GetFullPath(previousStagedPath),
						StringComparison.OrdinalIgnoreCase)) ??
				candidates.FirstOrDefault()?.FullName;

			foreach (FileInfo candidate in candidates)
			{
				if (string.Equals(
						candidate.FullName,
						retainedPath,
						StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				try
				{
					candidate.Delete();
				}
				catch
				{
					// 使用中的前代 executable 留待後續 staging 再清理。
				}
			}
		}
		catch
		{
			// Generation 清理不得影響已驗證 executable 的使用。
		}
	}

	private bool IsStagedGenerationPath(string path, string trustedRoot)
	{
		string fullPath = Path.GetFullPath(path);

		if (!string.Equals(
				Path.GetDirectoryName(fullPath),
				trustedRoot,
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				Path.GetExtension(fullPath),
				".exe",
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string fileName = Path.GetFileNameWithoutExtension(fullPath);

		if (!fileName.StartsWith(_stagedFilePrefix, StringComparison.Ordinal) ||
			(fileName.Length != (_stagedFilePrefix.Length + 64)))
		{
			return false;
		}

		return fileName.AsSpan(_stagedFilePrefix.Length)
			.ToArray()
			.All(Uri.IsHexDigit);
	}

	private void TryDeleteTemporaryFile(
		string temporaryPath,
		string trustedRoot)
	{
		try
		{
			string fullPath = Path.GetFullPath(temporaryPath);
			string expectedParent = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(trustedRoot));

			if (string.Equals(
				Path.GetDirectoryName(fullPath),
				expectedParent,
				StringComparison.OrdinalIgnoreCase) &&
				Path.GetFileName(fullPath).StartsWith(
					_temporaryFilePrefix,
					StringComparison.Ordinal) &&
				File.Exists(fullPath))
			{
				File.Delete(fullPath);
			}
		}
		catch
		{
			// 暫存清理不得掩蓋 fail-closed 結果。
		}
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
