using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface IClaudeCliExecutableStager
{
	WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default);
}

internal sealed class ClaudeCliExecutableStager :
	IClaudeCliExecutableStager,
	IDisposable
{
	internal const long MaximumExecutableSizeBytes =
		WindowsOfficialCliExecutableStager.MaximumExecutableSizeBytes;
	private const int CopyBufferSize = 1024 * 1024;
	private const string ExpectedSignerName = "Anthropic, PBC";
	private const string StagedFilePrefix = "claude-";
	private const string TemporaryFilePrefix = ".claude-stage-";
	private static readonly TimeSpan TemporaryFileRetentionAge =
		TimeSpan.FromHours(1);
	private readonly Func<string, WindowsAuthenticodeInspection> _inspectSignature;
	private readonly Func<string, bool> _isCanonicalNonReparseFile;
	private readonly Func<string, bool> _isFixedDrivePath;
	private readonly Func<string, bool> _isPathAclSafe;
	private readonly Func<string> _protectedRootResolver;
	private readonly WindowsOfficialCliExecutableStager _stager;
	private readonly SemaphoreSlim _stageGate = new(1, 1);
	private WindowsOfficialCliExecutableLease? _cachedProtectedLease;
	private string? _cachedSourcePath;
	private WindowsOfficialCliExecutableTrustStamp? _cachedSourceStamp;

	internal static IClaudeCliExecutableStager Shared { get; } =
		new ClaudeCliExecutableStager();

	internal ClaudeCliExecutableStager()
		: this(
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafe)
	{
	}

	internal ClaudeCliExecutableStager(
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, bool> isPathAclSafe,
		Func<string>? protectedRootResolver = null)
	{
		_inspectSignature = inspectSignature ??
			throw new ArgumentNullException(nameof(inspectSignature));
		_isCanonicalNonReparseFile = isCanonicalNonReparseFile ??
			throw new ArgumentNullException(nameof(isCanonicalNonReparseFile));
		_isFixedDrivePath = isFixedDrivePath ??
			throw new ArgumentNullException(nameof(isFixedDrivePath));
		_isPathAclSafe = isPathAclSafe ??
			throw new ArgumentNullException(nameof(isPathAclSafe));
		_protectedRootResolver = protectedRootResolver ??
			ResolveDefaultProtectedRoot;
		_stager = new WindowsOfficialCliExecutableStager(
			ExpectedSignerName,
			_inspectSignature,
			_isCanonicalNonReparseFile,
			_isFixedDrivePath,
			_isPathAclSafe);
	}

	public WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default)
	{
		_stageGate.Wait(cancellationToken);

		try
		{
			try
			{
				try
				{
					return _stager.Stage(sourceExecutablePath, cancellationToken);
				}
				catch (WindowsOfficialCliExecutableStagingException exception) when (
					exception.FailureReason ==
						WindowsOfficialCliExecutableStagingFailureReason.UnsafeSourcePath)
				{
					return StageProtectedCopy(
						sourceExecutablePath,
						cancellationToken);
				}
			}
			catch (WindowsOfficialCliExecutableStagingException exception)
			{
				throw new ClaudeCliUntrustedException(
					"無法鎖定或驗證 Claude Code CLI 的官方執行檔。",
					exception);
			}
		}
		finally
		{
			_stageGate.Release();
		}
	}

	public void Dispose()
	{
		_stageGate.Wait();

		try
		{
			try
			{
				_cachedProtectedLease?.Dispose();
				_cachedProtectedLease = null;
			}
			finally
			{
				_stager.Dispose();
			}
		}
		finally
		{
			_stageGate.Release();
		}
	}

	private WindowsOfficialCliExecutableLease StageProtectedCopy(
		string sourceExecutablePath,
		CancellationToken cancellationToken)
	{
		string fullSourcePath;
		try
		{
			fullSourcePath = Path.GetFullPath(sourceExecutablePath);
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSourcePath,
				"The Claude CLI source path is invalid.",
				exception);
		}

		if (!Path.IsPathFullyQualified(sourceExecutablePath) ||
			!string.Equals(
				Path.GetExtension(fullSourcePath),
				".exe",
				StringComparison.OrdinalIgnoreCase) ||
			!_isFixedDrivePath(fullSourcePath) ||
			!_isCanonicalNonReparseFile(fullSourcePath))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.UnsafeSourcePath,
				"The Claude CLI source must be a non-reparse EXE on a fixed drive.");
		}

		try
		{
			using FileStream sourceStream = new(
				fullSourcePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				CopyBufferSize,
				FileOptions.SequentialScan);

			WindowsOfficialCliExecutableTrustStamp sourceStamp =
				WindowsOfficialCliExecutableStager.ReadTrustStamp(sourceStream);

			if (!_isCanonicalNonReparseFile(fullSourcePath) ||
				(sourceStamp.Length <= 0) ||
				(sourceStamp.Length > MaximumExecutableSizeBytes))
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.UnsafeSourcePath,
					"The Claude CLI source path or size is unsafe.");
			}

			WindowsOfficialCliExecutableLease? cachedLease =
				TryCreateCachedLease(fullSourcePath, sourceStamp);
			if (cachedLease is not null)
			{
				string? cachedRoot = Path.GetDirectoryName(
					cachedLease.ExecutablePath);
				if (cachedRoot is not null)
				{
					PruneOldCopies(cachedRoot, cachedLease.ExecutablePath);
				}

				return cachedLease;
			}

			EnsureSourceSignature(fullSourcePath);
			if ((WindowsOfficialCliExecutableStager.ReadTrustStamp(sourceStream) !=
				sourceStamp) ||
				!_isCanonicalNonReparseFile(fullSourcePath))
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.SourceChanged,
					"The Claude CLI source changed during signature verification.");
			}

			string protectedRoot = PrepareProtectedRoot();
			string sourceHash = ComputeHash(sourceStream, cancellationToken);
			string stagedPath = Path.Combine(
				protectedRoot,
				$"{StagedFilePrefix}{sourceHash}.exe");

			if (!File.Exists(stagedPath))
			{
				CreateProtectedCopy(
					sourceStream,
					protectedRoot,
					stagedPath,
					sourceHash,
					cancellationToken);
			}

			using FileStream stagedStream = new(
				stagedPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				CopyBufferSize,
				FileOptions.SequentialScan);

			if (!string.Equals(
					ComputeHash(stagedStream, cancellationToken),
					sourceHash,
					StringComparison.Ordinal))
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.ProtectionFailed,
					"The protected Claude CLI copy does not match its source.");
			}

			WindowsOfficialCliExecutableLease? protectedLease = _stager.Stage(
				stagedPath,
				cancellationToken);

			try
			{
				WindowsOfficialCliExecutableLease consumerLease =
					protectedLease.Duplicate();
				WindowsOfficialCliExecutableLease? previousLease =
					_cachedProtectedLease;
				_cachedProtectedLease = protectedLease;
				_cachedSourcePath = fullSourcePath;
				_cachedSourceStamp = sourceStamp;
				protectedLease = null;
				try
				{
					previousLease?.Dispose();
				}
				catch (Exception exception)
				{
					Trace.TraceWarning(
						"Unable to release the previous protected Claude CLI lease: {0}",
						exception);
				}

				PruneOldCopies(protectedRoot, stagedPath);
				return consumerLease;
			}
			finally
			{
				protectedLease?.Dispose();
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
				$"The Claude CLI source could not be copied and verified: {fullSourcePath}",
				exception);
		}
	}

	private void EnsureSourceSignature(string sourcePath)
	{
		WindowsAuthenticodeInspection signature;
		try
		{
			signature = _inspectSignature(sourcePath);
		}
		catch (Exception exception)
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The Claude CLI source signature could not be verified.",
				exception);
		}

		if ((signature.WinVerifyTrustStatus != 0) ||
			!WindowsOfficialCliExecutableValidator.HasExpectedSigner(
				signature.SignerSubject,
				ExpectedSignerName))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
				"The Claude CLI source signature or publisher is invalid.");
		}
	}

	private WindowsOfficialCliExecutableLease? TryCreateCachedLease(
		string sourcePath,
		WindowsOfficialCliExecutableTrustStamp sourceStamp)
	{
		WindowsOfficialCliExecutableLease? cachedLease = _cachedProtectedLease;
		if (cachedLease is null ||
			!cachedLease.IsProtected ||
			!string.Equals(
				_cachedSourcePath,
				sourcePath,
				StringComparison.OrdinalIgnoreCase) ||
			(_cachedSourceStamp != sourceStamp) ||
			!_isCanonicalNonReparseFile(cachedLease.ExecutablePath) ||
			!_isPathAclSafe(cachedLease.ExecutablePath))
		{
			return null;
		}

		return cachedLease.Duplicate();
	}

	private string PrepareProtectedRoot()
	{
		string requestedRoot = _protectedRootResolver();
		if (!Path.IsPathFullyQualified(requestedRoot))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.ProtectionFailed,
				"The protected Claude CLI executable directory is not absolute.");
		}

		string protectedRoot = Path.GetFullPath(requestedRoot);

		if (!Path.IsPathFullyQualified(protectedRoot) ||
			!_isFixedDrivePath(protectedRoot) ||
			!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				protectedRoot,
				out _,
				out _) ||
			!WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(
				protectedRoot) ||
			!WindowsExecutablePathSecurity.IsDirectoryPathAclSafe(
				protectedRoot))
		{
			throw new WindowsOfficialCliExecutableStagingException(
				WindowsOfficialCliExecutableStagingFailureReason.ProtectionFailed,
				"The protected Claude CLI executable directory is unsafe.");
		}

		return protectedRoot;
	}

	private void CreateProtectedCopy(
		FileStream sourceStream,
		string protectedRoot,
		string stagedPath,
		string expectedHash,
		CancellationToken cancellationToken)
	{
		string temporaryPath = Path.Combine(
			protectedRoot,
			$"{TemporaryFilePrefix}{Guid.NewGuid():N}.exe");

		try
		{
			sourceStream.Position = 0;
			using (FileStream temporaryStream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				CopyBufferSize,
				FileOptions.SequentialScan))
			{
				string copyHash = CopyAndHash(
					sourceStream,
					temporaryStream,
					cancellationToken);
				if (!string.Equals(copyHash, expectedHash, StringComparison.Ordinal))
				{
					throw new WindowsOfficialCliExecutableStagingException(
						WindowsOfficialCliExecutableStagingFailureReason.SourceChanged,
						"The Claude CLI source changed while it was being copied.");
				}

				temporaryStream.Flush(flushToDisk: true);
			}

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(temporaryPath) ||
				!_isCanonicalNonReparseFile(temporaryPath) ||
				!_isPathAclSafe(temporaryPath))
			{
				throw new WindowsOfficialCliExecutableStagingException(
					WindowsOfficialCliExecutableStagingFailureReason.ProtectionFailed,
					"The temporary Claude CLI copy did not receive safe access control.");
			}

			try
			{
				File.Move(temporaryPath, stagedPath);
			}
			catch (IOException) when (File.Exists(stagedPath))
			{
				// 其他執行個體已建立同 hash 的副本，後續仍會驗證內容與簽章。
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				try
				{
					File.Delete(temporaryPath);
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is UnauthorizedAccessException) ||
					(exception is System.Security.SecurityException))
				{
					Trace.TraceWarning(
						"Unable to remove a temporary protected Claude CLI copy at {0}: {1}",
						temporaryPath,
						exception);
				}
			}
		}
	}

	private static string ComputeHash(
		Stream source,
		CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(
			HashAlgorithmName.SHA256);
		byte[] buffer = new byte[CopyBufferSize];
		int bytesRead;

		while ((bytesRead = source.Read(buffer)) > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(buffer, 0, bytesRead);
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static string CopyAndHash(
		Stream source,
		Stream destination,
		CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(
			HashAlgorithmName.SHA256);
		byte[] buffer = new byte[CopyBufferSize];
		int bytesRead;

		while ((bytesRead = source.Read(buffer)) > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(buffer, 0, bytesRead);
			destination.Write(buffer, 0, bytesRead);
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private void PruneOldCopies(string protectedRoot, string currentPath)
	{
		try
		{
			DateTime staleBeforeUtc = DateTime.UtcNow -
				TemporaryFileRetentionAge;
			foreach (string path in Directory.EnumerateFiles(
				protectedRoot,
				$"{TemporaryFilePrefix}*.exe",
				SearchOption.TopDirectoryOnly))
			{
				if (IsOwnedTemporaryPath(path, protectedRoot) &&
					_isCanonicalNonReparseFile(path) &&
					_isPathAclSafe(path) &&
					(File.GetLastWriteTimeUtc(path) <= staleBeforeUtc))
				{
					TryDeleteOldCopy(path);
				}
			}

			string? previousPath = Directory.EnumerateFiles(
				protectedRoot,
				$"{StagedFilePrefix}*.exe",
				SearchOption.TopDirectoryOnly)
				.Where(path => IsOwnedStagedPath(path, protectedRoot) &&
					!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault();

			foreach (string path in Directory.EnumerateFiles(
				protectedRoot,
				$"{StagedFilePrefix}*.exe",
				SearchOption.TopDirectoryOnly))
			{
				if (!IsOwnedStagedPath(path, protectedRoot) ||
					string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase) ||
					!_isCanonicalNonReparseFile(path) ||
					!_isPathAclSafe(path))
				{
					continue;
				}

				TryDeleteOldCopy(path);
			}
		}
		catch (Exception exception)
		{
			Trace.TraceWarning(
				"Unable to inspect old protected Claude CLI copies in {0}: {1}",
				protectedRoot,
				exception);
		}
	}

	private static void TryDeleteOldCopy(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is System.Security.SecurityException))
		{
			Trace.TraceWarning(
				"Unable to remove an old protected Claude CLI copy at {0}: {1}",
				path,
				exception);
		}
	}

	private static bool IsOwnedStagedPath(string path, string protectedRoot)
	{
		if (!string.Equals(
				Path.GetDirectoryName(path),
				protectedRoot,
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string name = Path.GetFileName(path);
		return name.StartsWith(StagedFilePrefix, StringComparison.Ordinal) &&
			name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
			(name.Length == StagedFilePrefix.Length + 64 + ".exe".Length) &&
			name.AsSpan(StagedFilePrefix.Length, 64).ToArray().All(
				Uri.IsHexDigit);
	}

	private static bool IsOwnedTemporaryPath(
		string path,
		string protectedRoot)
	{
		if (!string.Equals(
				Path.GetDirectoryName(path),
				protectedRoot,
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string name = Path.GetFileName(path);
		return name.StartsWith(TemporaryFilePrefix, StringComparison.Ordinal) &&
			name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
			(name.Length == TemporaryFilePrefix.Length + 32 + ".exe".Length) &&
			name.AsSpan(TemporaryFilePrefix.Length, 32).ToArray().All(
				Uri.IsHexDigit);
	}

	private static string ResolveDefaultProtectedRoot()
	{
		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);
		return Path.Combine(
			userProfile,
			"AiUsageDashboard.ClaudeCli",
			"executables-v1");
	}
}
