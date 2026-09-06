using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace AiUsageDashboard.App.Persistence;

internal interface IPortableSettingsImportTransaction
{
	Task ExecuteAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken = default);

	Task RecoverInterruptedImportAsync(
		CancellationToken cancellationToken = default);
}

internal sealed class PortableSettingsImportCommittedException : Exception
{
	internal PortableSettingsImportCommittedException(
		string message,
		Exception innerException)
		: base(message, innerException)
	{
	}
}

internal readonly record struct PortableSettingsImportFileState(
	bool Exists,
	string? ContentFingerprint);

internal sealed record PortableSettingsImportTargetRestoreResult(
	PortableSettingsImportFileState? RollbackBaselineState,
	bool DoesFinalTargetMatchRollbackBaseline);

internal sealed class PortableSettingsImportRestoreResult
{
	private readonly IReadOnlyDictionary<
		string,
		PortableSettingsImportTargetRestoreResult> _targetResults;

	internal PortableSettingsImportRestoreResult(
		IEnumerable<KeyValuePair<
			string,
			PortableSettingsImportTargetRestoreResult>> targetResults)
	{
		ArgumentNullException.ThrowIfNull(targetResults);
		_targetResults = targetResults
			.ToDictionary(
				pair => Path.GetFullPath(pair.Key),
				pair => pair.Value,
				StringComparer.OrdinalIgnoreCase);
	}

	internal PortableSettingsImportTargetRestoreResult GetTargetResult(
		string targetFilePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
		string normalizedPath = Path.GetFullPath(targetFilePath);

		if (_targetResults.TryGetValue(
			normalizedPath,
			out PortableSettingsImportTargetRestoreResult? targetResult))
		{
			return targetResult;
		}

		throw new ArgumentException(
			"指定的檔案不屬於這次設定匯入交易。",
			nameof(targetFilePath));
	}
}

internal sealed class PortableSettingsImportTransaction :
	IPortableSettingsImportTransaction
{
	private const string AbsentMarkerSuffix = ".absent";
	private const string BackupFileSuffix = ".backup";
	private const string CommittedMarkerFileName = "committed";
	private const string CommittedMarkerTemporaryFilePrefix = "committed.";
	private const string CommittedMarkerTemporaryFileSuffix = ".tmp";
	private const string CommitCallbackCompletedMarkerFileName =
		"commit-callback-completed";
	private const string CommitCallbackCompletedMarkerTemporaryFilePrefix =
		"commit-callback-completed.";
	private const long DefaultMaximumTargetFileSizeBytes = 1024 * 1024;
	private const string OperationBaselineAbsentMarkerSuffix =
		".operation-baseline.absent";
	private const string OperationBaselineBackupFileSuffix =
		".operation-baseline.backup";
	private const string OperationBaselineVersionMarkerFileName =
		"operation-baseline-v1";
	private const string OperationStartedMarkerFileName = "operation-started";
	private const string OwnedContentMarkerPrefix = ".owned-";
	private const int Sha256HexLength = 64;
	private const string PreparedMarkerFileName = "prepared";
	private const string RestoreCallbackCompletedMarkerFileName =
		"restore-callback-completed";
	private const string RestoredMarkerFileName = "restored";
	private const string RestoredMarkerTemporaryFilePrefix = "restored.";
	private static readonly TimeSpan DefaultRecoveryMutationLockWaitTimeout =
		TimeSpan.FromSeconds(3);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Action<FileStream> _flushCommittedMarkerToDisk;
	private readonly Action<FileStream> _flushCommitCallbackCompletedMarkerToDisk;
	private readonly Action<FileStream> _flushRestoredMarkerToDisk;
	private readonly Action<string> _deleteTransactionFile;
	private readonly long[] _maximumTargetFileSizes;
	private readonly Func<CancellationToken, Task>? _onOperationStarting;
	private readonly Func<CancellationToken, Task>? _onTargetsCommitted;
	private readonly SettingsPersistenceGate _persistenceGate;
	private readonly TimeSpan _recoveryMutationLockWaitTimeout;
	private readonly PortableSettingsImportWriteTracker? _writeTracker;
	private readonly Func<CancellationToken, Task>? _onTargetsRestored;
	private readonly Func<PortableSettingsImportRestoreResult, CancellationToken, Task>?
		_onTargetsRestoredWithResult;
	private readonly Action? _onTransactionFinished;
	private readonly Action<string, string> _publishCommittedMarkerAtomically;
	private readonly Action<string, string>
		_publishCommitCallbackCompletedMarkerAtomically;
	private readonly Action<string, string> _publishRestoredMarkerAtomically;
	private readonly Action<string, string> _replaceFileAtomically;
	private readonly string[] _targetFilePaths;
	private readonly string _transactionDirectoryPath;

	private sealed class RecoveryMutationLease : IAsyncDisposable
	{
		private FileStream? _lockStream;

		internal RecoveryMutationLease(FileStream lockStream)
		{
			_lockStream = lockStream;
		}

		public async ValueTask DisposeAsync()
		{
			FileStream? lockStream = Interlocked.Exchange(
				ref _lockStream,
				null);

			if (lockStream is null)
			{
				return;
			}

			await lockStream.DisposeAsync().ConfigureAwait(false);
		}
	}

	internal PortableSettingsImportTransaction(
		string transactionDirectoryPath,
		IReadOnlyList<string> targetFilePaths,
		Action<string, string>? replaceFileAtomically = null,
		Action<FileStream>? flushCommittedMarkerToDisk = null,
		Action<string, string>? publishCommittedMarkerAtomically = null,
		SettingsPersistenceGate? persistenceGate = null,
		Func<CancellationToken, Task>? onTargetsRestored = null,
		Func<CancellationToken, Task>? onOperationStarting = null,
		Action? onTransactionFinished = null,
		Action<FileStream>? flushRestoredMarkerToDisk = null,
		Action<string, string>? publishRestoredMarkerAtomically = null,
		PortableSettingsImportWriteTracker? writeTracker = null,
		TimeSpan? recoveryMutationLockWaitTimeout = null,
		IReadOnlyList<long>? maximumTargetFileSizes = null,
		Action<string>? deleteTransactionFile = null,
		Func<PortableSettingsImportRestoreResult, CancellationToken, Task>?
			onTargetsRestoredWithResult = null,
		Func<CancellationToken, Task>? onTargetsCommitted = null,
		Action<FileStream>? flushCommitCallbackCompletedMarkerToDisk = null,
		Action<string, string>?
			publishCommitCallbackCompletedMarkerAtomically = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(transactionDirectoryPath);
		ArgumentNullException.ThrowIfNull(targetFilePaths);

		if (targetFilePaths.Count == 0)
		{
			throw new ArgumentException(
				"交易至少需要一個目標檔案。",
				nameof(targetFilePaths));
		}

		_transactionDirectoryPath = Path.GetFullPath(transactionDirectoryPath);
		_replaceFileAtomically = replaceFileAtomically ??
			ReplaceFileAtomically;
		_flushCommittedMarkerToDisk = flushCommittedMarkerToDisk ??
			FlushFileToDisk;
		_flushCommitCallbackCompletedMarkerToDisk =
			flushCommitCallbackCompletedMarkerToDisk ?? FlushFileToDisk;
		_flushRestoredMarkerToDisk = flushRestoredMarkerToDisk ??
			FlushFileToDisk;
		_publishCommittedMarkerAtomically =
			publishCommittedMarkerAtomically ?? PublishFileAtomically;
		_publishCommitCallbackCompletedMarkerAtomically =
			publishCommitCallbackCompletedMarkerAtomically ??
			PublishFileAtomically;
		_publishRestoredMarkerAtomically =
			publishRestoredMarkerAtomically ?? PublishFileAtomically;
		_deleteTransactionFile = deleteTransactionFile ?? File.Delete;
		_persistenceGate = persistenceGate ?? new SettingsPersistenceGate();
		_recoveryMutationLockWaitTimeout = recoveryMutationLockWaitTimeout ??
			DefaultRecoveryMutationLockWaitTimeout;

		if (_recoveryMutationLockWaitTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(recoveryMutationLockWaitTimeout));
		}

		_writeTracker = writeTracker;
		_onTargetsRestored = onTargetsRestored;
		_onTargetsRestoredWithResult = onTargetsRestoredWithResult;
		_onOperationStarting = onOperationStarting;
		_onTargetsCommitted = onTargetsCommitted;
		_onTransactionFinished = onTransactionFinished;
		_targetFilePaths = targetFilePaths
			.Select(path =>
			{
				ArgumentException.ThrowIfNullOrWhiteSpace(path);
				return Path.GetFullPath(path);
			})
			.ToArray();

		if ((_onTargetsRestored is not null) &&
			(_onTargetsRestoredWithResult is not null))
		{
			throw new ArgumentException(
				"設定匯入的還原方式重複。");
		}
		_maximumTargetFileSizes = maximumTargetFileSizes is null
			? Enumerable.Repeat(
				DefaultMaximumTargetFileSizeBytes,
				_targetFilePaths.Length).ToArray()
			: maximumTargetFileSizes.ToArray();

		if (_maximumTargetFileSizes.Length != _targetFilePaths.Length)
		{
			throw new ArgumentException(
				"每個交易目標都必須有且只能有一個檔案大小上限。",
				nameof(maximumTargetFileSizes));
		}

		if (_maximumTargetFileSizes.Any(maximumSize => maximumSize <= 0))
		{
			throw new ArgumentOutOfRangeException(
				nameof(maximumTargetFileSizes),
				"交易目標檔案大小上限必須大於零。");
		}

		if (_targetFilePaths.Distinct(
				StringComparer.OrdinalIgnoreCase).Count() !=
			_targetFilePaths.Length)
		{
			throw new ArgumentException(
				"交易目標檔案不可重複。",
				nameof(targetFilePaths));
		}
	}

	private static void ReplaceFileAtomically(
		string sourceFilePath,
		string destinationFilePath)
	{
		File.Replace(
			sourceFilePath,
			destinationFilePath,
			destinationBackupFileName: null,
			ignoreMetadataErrors: false);
	}

	private static void FlushFileToDisk(FileStream stream)
	{
		stream.Flush(flushToDisk: true);
	}

	private static void PublishFileAtomically(
		string sourceFilePath,
		string destinationFilePath)
	{
		File.Move(sourceFilePath, destinationFilePath);
	}

	private async Task<RecoveryMutationLease> AcquireRecoveryMutationLeaseAsync(
		CancellationToken cancellationToken)
	{
		string? parentDirectoryPath = Path.GetDirectoryName(
			_transactionDirectoryPath);

		if (string.IsNullOrWhiteSpace(parentDirectoryPath))
		{
			throw new InvalidOperationException(
				"設定匯入交易缺少有效的父目錄。");
		}

		Directory.CreateDirectory(parentDirectoryPath);
		string lockFilePath = _transactionDirectoryPath + ".lock";
		long waitStartedTimestamp = Stopwatch.GetTimestamp();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				if (File.Exists(lockFilePath))
				{
					EnsureFileIsNotReparsePoint(lockFilePath);
				}

				FileStream lockStream = new(
					lockFilePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.Asynchronous | FileOptions.WriteThrough);

				if ((File.GetAttributes(lockFilePath) &
					FileAttributes.ReparsePoint) != 0)
				{
					await lockStream.DisposeAsync().ConfigureAwait(false);
					throw new IOException(
						"設定匯入交易鎖定檔不可為連結或重新解析點。");
				}

				return new RecoveryMutationLease(lockStream);
			}
			catch (IOException) when (
				Stopwatch.GetElapsedTime(waitStartedTimestamp) <
					_recoveryMutationLockWaitTimeout)
			{
				await Task.Delay(
					TimeSpan.FromMilliseconds(50),
					cancellationToken).ConfigureAwait(false);
			}
			catch (UnauthorizedAccessException) when (
				Stopwatch.GetElapsedTime(waitStartedTimestamp) <
					_recoveryMutationLockWaitTimeout)
			{
				await Task.Delay(
					TimeSpan.FromMilliseconds(50),
					cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				throw new IOException(
					"另一個 AI Usage 正在匯入或還原設定；請稍後再試。",
					exception);
			}
		}
	}

	public async Task ExecuteAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(operation);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await _persistenceGate.RunExclusiveAsync(
				async operationCancellationToken =>
				{
					await RunWithRecoveryMutationLeaseAsync(
						async leaseCancellationToken =>
						{
							await ExecuteCoreAsync(
							operation,
							leaseCancellationToken).ConfigureAwait(false);
						},
						operationCancellationToken).ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task RecoverInterruptedImportAsync(
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await _persistenceGate.RunExclusiveAsync(
				async operationCancellationToken =>
				{
					await RunWithRecoveryMutationLeaseAsync(
						RecoverCoreAsync,
						operationCancellationToken).ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task RunWithRecoveryMutationLeaseAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken)
	{
		RecoveryMutationLease lease =
			await AcquireRecoveryMutationLeaseAsync(cancellationToken)
				.ConfigureAwait(false);

		try
		{
			await operation(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			try
			{
				await lease.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				DeleteRecoveryLockFileBestEffort();
			}
		}
	}

	private async Task ExecuteCoreAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken)
	{
		await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
		await PrepareAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await NotifyOperationStartingAsync(cancellationToken)
				.ConfigureAwait(false);

			if (_writeTracker is null)
			{
				await operation(cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await CaptureOperationBaselineAsync(cancellationToken)
					.ConfigureAwait(false);
				await _writeTracker.RunTrackedAsync(
					operation,
					RecordPreparedWriteAsync,
					cancellationToken).ConfigureAwait(false);
			}
			await PublishCommittedMarkerAsync(
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception operationException)
		{
			try
			{
				await RestorePreparedTargetsAsync(
					CancellationToken.None).ConfigureAwait(false);
				await PublishRestoredMarkerAsync(
					CancellationToken.None).ConfigureAwait(false);
				await NotifyTargetsRestoredAsync(
					CancellationToken.None,
					allowUnverifiableRollbackBaseline: false).ConfigureAwait(false);
				await PublishRestoreCallbackCompletedMarkerAsync(
					CancellationToken.None).ConfigureAwait(false);
				NotifyTransactionFinished();
				DeleteTransactionDirectoryBestEffort();
			}
			catch (Exception rollbackException)
			{
				throw new InvalidOperationException(
					"設定匯入未完成，也無法立即還原原本設定。重新啟動 AI Usage 後會自動再試。",
					new AggregateException(
						operationException,
						rollbackException));
			}

			throw;
		}

		try
		{
			await CompleteCommittedCallbackAsync(CancellationToken.None)
				.ConfigureAwait(false);
			NotifyTransactionFinished();
		}
		catch (Exception exception)
		{
			try
			{
				NotifyTransactionFinished();
			}
			catch (Exception finishException)
			{
				exception = new AggregateException(exception, finishException);
			}

			throw new PortableSettingsImportCommittedException(
				"設定匯入已提交，但無法記錄匯入後的帳號清理授權。重新啟動 AI Usage 後會自動再試。",
				exception);
		}

		DeleteTransactionDirectoryBestEffort();
	}

	private async Task PrepareAsync(CancellationToken cancellationToken)
	{
		string? parentDirectoryPath = Path.GetDirectoryName(
			_transactionDirectoryPath);

		if (string.IsNullOrWhiteSpace(parentDirectoryPath))
		{
			throw new InvalidOperationException(
				"設定匯入交易缺少有效的父目錄。");
		}

		Directory.CreateDirectory(parentDirectoryPath);
		Directory.CreateDirectory(_transactionDirectoryPath);
		EnsureTransactionDirectoryIsNotReparsePoint();

		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string targetFilePath = _targetFilePaths[index];

			if (File.Exists(targetFilePath))
			{
				EnsureFileIsNotReparsePoint(targetFilePath);
				await CopyFileDurablyAsync(
					targetFilePath,
					GetBackupFilePath(index),
					createNew: true,
					_maximumTargetFileSizes[index],
					cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await WriteDurableMarkerAsync(
					GetAbsentMarkerPath(index),
					cancellationToken).ConfigureAwait(false);
			}
		}

		if (_writeTracker is not null)
		{
			// Journals created before operation-baseline-v1 retain their legacy
			// recovery behavior. Publishing this marker before "prepared" lets a
			// newer process recognize a tracked journal even if it crashes before
			// the importing operation is allowed to start.
			await WriteDurableMarkerAsync(
				GetMarkerPath(OperationBaselineVersionMarkerFileName),
				cancellationToken).ConfigureAwait(false);
		}
		await WriteDurableMarkerAsync(
			GetMarkerPath(PreparedMarkerFileName),
			cancellationToken).ConfigureAwait(false);
	}

	private async Task CaptureOperationBaselineAsync(
		CancellationToken cancellationToken)
	{
		// onOperationStarting may observe or expose a stale-writer conflict after
		// PrepareAsync took its crash-recovery backups. Capture the files again at
		// the last boundary before invoking the importing operation so rollback
		// preserves legitimate writes that became visible during that window.
		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string targetFilePath = _targetFilePaths[index];

			if (File.Exists(targetFilePath))
			{
				EnsureFileIsNotReparsePoint(targetFilePath);
				await CopyFileDurablyAsync(
					targetFilePath,
					GetOperationBaselineBackupFilePath(index),
					createNew: true,
					_maximumTargetFileSizes[index],
					cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await WriteDurableMarkerAsync(
					GetOperationBaselineAbsentMarkerPath(index),
					cancellationToken).ConfigureAwait(false);
			}
		}

		// No operation is invoked until every baseline is durable. Recovery may
		// therefore preserve the current targets when this marker is absent.
		await WriteDurableMarkerAsync(
			GetMarkerPath(OperationStartedMarkerFileName),
			cancellationToken).ConfigureAwait(false);
	}

	private async Task RecordPreparedWriteAsync(
		string destinationFilePath,
		string preparedFilePath,
		CancellationToken cancellationToken)
	{
		string normalizedDestinationPath = Path.GetFullPath(destinationFilePath);
		int targetIndex = Array.FindIndex(
			_targetFilePaths,
			path => string.Equals(
				path,
				normalizedDestinationPath,
				StringComparison.OrdinalIgnoreCase));

		if (targetIndex < 0)
		{
			throw new InvalidOperationException(
				"設定匯入嘗試寫入交易範圍以外的檔案。");
		}

		string normalizedPreparedPath = Path.GetFullPath(preparedFilePath);
		if (!File.Exists(normalizedPreparedPath))
		{
			throw new FileNotFoundException(
				"設定匯入的待寫入檔案不存在。",
				normalizedPreparedPath);
		}

		EnsureFileIsNotReparsePoint(normalizedPreparedPath);
		string contentHash = await ComputeFileHashAsync(
			normalizedPreparedPath,
			_maximumTargetFileSizes[targetIndex],
			cancellationToken).ConfigureAwait(false);
		string markerPath = GetOwnedContentMarkerPath(targetIndex, contentHash);

		if (File.Exists(markerPath))
		{
			EnsureFileIsNotReparsePoint(markerPath);
			return;
		}

		await WriteDurableMarkerAsync(markerPath, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task RecoverCoreAsync(CancellationToken cancellationToken)
	{
		if (!Directory.Exists(_transactionDirectoryPath))
		{
			return;
		}

		EnsureTransactionDirectoryIsNotReparsePoint();

		bool committedMarkerExists = File.Exists(
			GetMarkerPath(CommittedMarkerFileName));
		bool commitCallbackCompletedMarkerExists = File.Exists(
			GetMarkerPath(CommitCallbackCompletedMarkerFileName));
		bool restoredMarkerExists = File.Exists(
			GetMarkerPath(RestoredMarkerFileName));
		bool restoreCallbackCompletedMarkerExists = File.Exists(
			GetMarkerPath(RestoreCallbackCompletedMarkerFileName));
		if (committedMarkerExists &&
			(restoredMarkerExists || restoreCallbackCompletedMarkerExists))
		{
			throw new InvalidDataException(
				"設定匯入交易同時包含互相衝突的 committed 與 restored 狀態。");
		}
		if (commitCallbackCompletedMarkerExists &&
			(restoredMarkerExists || restoreCallbackCompletedMarkerExists))
		{
			throw new InvalidDataException(
				"設定匯入交易同時包含互相衝突的 commit callback 與 restored 狀態。");
		}

		if (committedMarkerExists)
		{
			await CompleteCommittedCallbackAsync(cancellationToken)
				.ConfigureAwait(false);
			NotifyTransactionFinished();
			DeleteTransactionDirectoryBestEffort();
			return;
		}
		if (commitCallbackCompletedMarkerExists)
		{
			NotifyTransactionFinished();
			DeleteTransactionDirectoryBestEffort();
			return;
		}

		if (restoreCallbackCompletedMarkerExists)
		{
			NotifyTransactionFinished();
			DeleteTransactionDirectoryBestEffort();
			return;
		}

		if (restoredMarkerExists)
		{
			// The targets have already crossed the durable restore boundary. A
			// callback may still need to synchronize process-local state, but the
			// old backups must never be replayed over later legitimate writes.
			await NotifyTargetsRestoredAsync(
				cancellationToken,
				allowUnverifiableRollbackBaseline: true).ConfigureAwait(false);
			await PublishRestoreCallbackCompletedMarkerAsync(cancellationToken)
				.ConfigureAwait(false);
			NotifyTransactionFinished();
			DeleteTransactionDirectoryBestEffort();
			return;
		}

		if (!File.Exists(GetMarkerPath(PreparedMarkerFileName)))
		{
			NotifyTransactionFinished();
			DeleteTransactionDirectoryBestEffort();
			return;
		}

		await RestorePreparedTargetsAsync(cancellationToken).ConfigureAwait(false);
		await PublishRestoredMarkerAsync(cancellationToken).ConfigureAwait(false);
		await NotifyTargetsRestoredAsync(
			cancellationToken,
			allowUnverifiableRollbackBaseline: false).ConfigureAwait(false);
		await PublishRestoreCallbackCompletedMarkerAsync(cancellationToken)
			.ConfigureAwait(false);
		NotifyTransactionFinished();
		DeleteTransactionDirectoryBestEffort();
	}

	private void DeleteRecoveryLockFileBestEffort()
	{
		string lockFilePath = _transactionDirectoryPath + ".lock";

		try
		{
			if (File.Exists(lockFilePath))
			{
				EnsureFileIsNotReparsePoint(lockFilePath);
				File.Delete(lockFilePath);
			}
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			// The exclusive handle held by this process is the authority. A stale
			// empty lock file is ignored and reused by the next operation.
		}
	}

	private Task NotifyOperationStartingAsync(
		CancellationToken cancellationToken)
	{
		return _onOperationStarting is null
			? Task.CompletedTask
			: _onOperationStarting(cancellationToken);
	}

	private async Task NotifyTargetsRestoredAsync(
		CancellationToken cancellationToken,
		bool allowUnverifiableRollbackBaseline)
	{
		if (_onTargetsRestoredWithResult is not null)
		{
			PortableSettingsImportRestoreResult restoreResult =
				await CreateRestoreResultAsync(
					cancellationToken,
					allowUnverifiableRollbackBaseline)
					.ConfigureAwait(false);
			await _onTargetsRestoredWithResult(
				restoreResult,
				cancellationToken).ConfigureAwait(false);
			return;
		}

		if (_onTargetsRestored is not null)
		{
			await _onTargetsRestored(cancellationToken).ConfigureAwait(false);
		}
	}

	private void NotifyTransactionFinished()
	{
		_onTransactionFinished?.Invoke();
	}

	private async Task RestorePreparedTargetsAsync(
		CancellationToken cancellationToken)
	{
		bool hasOperationBaselineVersion = File.Exists(
			GetMarkerPath(OperationBaselineVersionMarkerFileName));
		bool wasOperationStarted = File.Exists(
			GetMarkerPath(OperationStartedMarkerFileName));
		bool usesOperationBaseline =
			hasOperationBaselineVersion && wasOperationStarted;
		ValidateRollbackBaselines(
			usesOperationBaseline,
			cancellationToken);

		if (hasOperationBaselineVersion && !wasOperationStarted)
		{
			// The importing operation was never invoked, so any target changes made
			// after PrepareAsync belong to another writer and must be preserved.
			return;
		}

		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string targetFilePath = _targetFilePaths[index];

			if (usesOperationBaseline &&
				!await IsTargetOwnedByTransactionAsync(
					index,
					targetFilePath,
					cancellationToken).ConfigureAwait(false))
			{
				// This target was never published by the importing operation, or a
				// later external writer replaced it. In either case the transaction
				// must not replay an older backup over the current contents.
				continue;
			}

			string backupFilePath = usesOperationBaseline
				? GetOperationBaselineBackupFilePath(index)
				: GetBackupFilePath(index);
			string absentMarkerPath = usesOperationBaseline
				? GetOperationBaselineAbsentMarkerPath(index)
				: GetAbsentMarkerPath(index);
			bool backupExists = File.Exists(backupFilePath);
			bool absentMarkerExists = File.Exists(absentMarkerPath);

			if (backupExists == absentMarkerExists)
			{
				throw new InvalidDataException(
					"設定匯入缺少可用的還原資料。");
			}

			if (backupExists)
			{
				EnsureFileIsNotReparsePoint(backupFilePath);

				await RestoreFileRecoverablyAsync(
					backupFilePath,
					targetFilePath,
					index,
					requireOwnedTarget: usesOperationBaseline,
					cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (absentMarkerExists)
			{
				if (File.Exists(targetFilePath))
				{
					if (usesOperationBaseline &&
						!await IsTargetOwnedByTransactionAsync(
							index,
							targetFilePath,
							cancellationToken).ConfigureAwait(false))
					{
						continue;
					}

					EnsureFileIsNotReparsePoint(targetFilePath);
					File.Delete(targetFilePath);
				}

				continue;
			}

			throw new InvalidDataException(
				"設定匯入缺少還原資料。");
		}
	}

	private void ValidateRollbackBaselines(
		bool usesOperationBaseline,
		CancellationToken cancellationToken)
	{
		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string backupFilePath = usesOperationBaseline
				? GetOperationBaselineBackupFilePath(index)
				: GetBackupFilePath(index);
			string absentMarkerPath = usesOperationBaseline
				? GetOperationBaselineAbsentMarkerPath(index)
				: GetAbsentMarkerPath(index);
			bool backupExists = File.Exists(backupFilePath);
			bool absentMarkerExists = File.Exists(absentMarkerPath);

			if (backupExists == absentMarkerExists)
			{
				throw new InvalidDataException(
					"設定匯入缺少可用的還原資料。");
			}

			EnsureFileIsNotReparsePoint(
				backupExists ? backupFilePath : absentMarkerPath);
		}
	}

	private async Task<bool> IsTargetOwnedByTransactionAsync(
		int targetIndex,
		string targetFilePath,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(targetFilePath))
		{
			return false;
		}

		EnsureFileIsNotReparsePoint(targetFilePath);
		string currentHash = await ComputeFileHashAsync(
			targetFilePath,
			_maximumTargetFileSizes[targetIndex],
			cancellationToken).ConfigureAwait(false);
		string markerPath = GetOwnedContentMarkerPath(targetIndex, currentHash);

		if (!File.Exists(markerPath))
		{
			return false;
		}

		EnsureFileIsNotReparsePoint(markerPath);
		return true;
	}

	private async Task<PortableSettingsImportRestoreResult>
		CreateRestoreResultAsync(
			CancellationToken cancellationToken,
			bool allowUnverifiableRollbackBaseline)
	{
		// operation-started proves every operation baseline was captured durably.
		// Earlier tracked journals and legacy journals fall back to the complete
		// prepare baseline instead of treating an unchanged target as external.
		bool usesOperationBaseline =
			File.Exists(GetMarkerPath(OperationBaselineVersionMarkerFileName)) &&
			File.Exists(GetMarkerPath(OperationStartedMarkerFileName));
		KeyValuePair<string, PortableSettingsImportTargetRestoreResult>[]
			targetResults =
				new KeyValuePair<
					string,
					PortableSettingsImportTargetRestoreResult>[
						_targetFilePaths.Length];

		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			PortableSettingsImportTargetRestoreResult targetResult =
				await CreateTargetRestoreResultAsync(
					index,
					usesOperationBaseline,
					allowUnverifiableRollbackBaseline,
					cancellationToken).ConfigureAwait(false);
			targetResults[index] = KeyValuePair.Create(
				_targetFilePaths[index],
				targetResult);
		}

		return new PortableSettingsImportRestoreResult(targetResults);
	}

	private async Task<PortableSettingsImportTargetRestoreResult>
		CreateTargetRestoreResultAsync(
		int targetIndex,
		bool usesOperationBaseline,
		bool allowUnverifiableRollbackBaseline,
		CancellationToken cancellationToken)
	{
		string backupFilePath = usesOperationBaseline
			? GetOperationBaselineBackupFilePath(targetIndex)
			: GetBackupFilePath(targetIndex);
		string absentMarkerPath = usesOperationBaseline
			? GetOperationBaselineAbsentMarkerPath(targetIndex)
			: GetAbsentMarkerPath(targetIndex);
		bool backupExists = File.Exists(backupFilePath);
		bool absentMarkerExists = File.Exists(absentMarkerPath);

		if (backupExists && absentMarkerExists)
		{
			throw new InvalidDataException(
				"設定匯入的還原資料互相衝突。");
		}

		if (!backupExists && !absentMarkerExists)
		{
			if (!allowUnverifiableRollbackBaseline)
			{
				throw new InvalidDataException(
					"設定匯入缺少還原資料。");
			}

			// Older versions could delete part of a restored journal before cleanup
			// failed. The target has already crossed the durable restore boundary, so
			// report an unverifiable baseline instead of replaying or fabricating one.
			return new PortableSettingsImportTargetRestoreResult(
				RollbackBaselineState: null,
				DoesFinalTargetMatchRollbackBaseline: false);
		}

		string targetFilePath = _targetFilePaths[targetIndex];

		if (absentMarkerExists)
		{
			bool targetExists = DoesPathExist(targetFilePath);
			return new PortableSettingsImportTargetRestoreResult(
				new PortableSettingsImportFileState(
					Exists: false,
					ContentFingerprint: null),
				DoesFinalTargetMatchRollbackBaseline: !targetExists);
		}

		EnsureFileIsNotReparsePoint(backupFilePath);
		string baselineHash = await ComputeFileHashAsync(
			backupFilePath,
			_maximumTargetFileSizes[targetIndex],
			cancellationToken).ConfigureAwait(false);
		PortableSettingsImportFileState baselineState = new(
			Exists: true,
			ContentFingerprint: baselineHash);

		if (!DoesPathExist(targetFilePath))
		{
			return new PortableSettingsImportTargetRestoreResult(
				baselineState,
				DoesFinalTargetMatchRollbackBaseline: false);
		}

		EnsureFileIsNotReparsePoint(targetFilePath);

		try
		{
			string targetHash = await ComputeFileHashAsync(
				targetFilePath,
				_maximumTargetFileSizes[targetIndex],
				cancellationToken).ConfigureAwait(false);
			return new PortableSettingsImportTargetRestoreResult(
				baselineState,
				DoesFinalTargetMatchRollbackBaseline: string.Equals(
					baselineHash,
					targetHash,
					StringComparison.Ordinal));
		}
		catch (InvalidDataException)
		{
			// An oversized external replacement cannot equal the bounded baseline.
			return new PortableSettingsImportTargetRestoreResult(
				baselineState,
				DoesFinalTargetMatchRollbackBaseline: false);
		}
		catch (FileNotFoundException)
		{
			return new PortableSettingsImportTargetRestoreResult(
				baselineState,
				DoesFinalTargetMatchRollbackBaseline: false);
		}
		catch (DirectoryNotFoundException)
		{
			return new PortableSettingsImportTargetRestoreResult(
				baselineState,
				DoesFinalTargetMatchRollbackBaseline: false);
		}
	}

	private static bool DoesPathExist(string path)
	{
		try
		{
			File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}

	private static async Task<string> ComputeFileHashAsync(
		string filePath,
		long maximumFileSize,
		CancellationToken cancellationToken)
	{
		await using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		EnsureStreamIsWithinSizeLimit(stream, maximumFileSize);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
			.ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private async Task RestoreFileRecoverablyAsync(
		string backupFilePath,
		string targetFilePath,
		int targetIndex,
		bool requireOwnedTarget,
		CancellationToken cancellationToken)
	{
		string? targetDirectoryPath = Path.GetDirectoryName(targetFilePath);

		if (string.IsNullOrWhiteSpace(targetDirectoryPath))
		{
			throw new InvalidOperationException("還原位置缺少有效的所在資料夾。");
		}

		Directory.CreateDirectory(targetDirectoryPath);
		string temporaryFilePath = Path.Combine(
			targetDirectoryPath,
			$".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.rollback.tmp");

		try
		{
			await CopyFileDurablyAsync(
				backupFilePath,
				temporaryFilePath,
				createNew: true,
				_maximumTargetFileSizes[targetIndex],
				cancellationToken).ConfigureAwait(false);

			if (requireOwnedTarget &&
				!await IsTargetOwnedByTransactionAsync(
					targetIndex,
					targetFilePath,
					cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			if (File.Exists(targetFilePath))
			{
				EnsureFileIsNotReparsePoint(targetFilePath);

				try
				{
					_replaceFileAtomically(
						temporaryFilePath,
						targetFilePath);
				}
				catch (UnauthorizedAccessException) when (!requireOwnedTarget)
				{
					// Some restricted Windows tokens may write an existing
					// settings file but cannot replace its directory entry. Preserve
					// the legacy journal's existing in-place fallback. A tracked
					// rollback must instead propagate the access error: crashing during
					// an in-place copy could leave a partial file whose hash no longer
					// matches the owned marker, causing recovery to misclassify it as an
					// external write instead of retrying the rollback.
					EnsureFileIsNotReparsePoint(targetFilePath);
					await CopyFileDurablyAsync(
						temporaryFilePath,
						targetFilePath,
						createNew: false,
						_maximumTargetFileSizes[targetIndex],
						cancellationToken).ConfigureAwait(false);
				}
			}
			else
			{
				File.Move(temporaryFilePath, targetFilePath);
			}
		}
		finally
		{
			try
			{
				File.Delete(temporaryFilePath);
			}
			catch (Exception exception) when (IsFileOperationException(exception))
			{
				// A uniquely named rollback temp file is never loaded by the app.
			}
		}
	}

	private static async Task CopyFileDurablyAsync(
		string sourceFilePath,
		string destinationFilePath,
		bool createNew,
		long maximumFileSize,
		CancellationToken cancellationToken)
	{
		await using FileStream source = new(
			sourceFilePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		EnsureStreamIsWithinSizeLimit(source, maximumFileSize);
		await using FileStream destination = new(
			destinationFilePath,
			createNew ? FileMode.CreateNew : FileMode.Create,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 81920,
			FileOptions.Asynchronous |
			FileOptions.SequentialScan |
			FileOptions.WriteThrough);
		await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
		await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
		destination.Flush(flushToDisk: true);
	}

	private static void EnsureStreamIsWithinSizeLimit(
		FileStream stream,
		long maximumFileSize)
	{
		// The source is already open with FileShare.Read, so no writer can replace
		// or extend it between this check and the bounded copy/hash operation.
		if (stream.Length > maximumFileSize)
		{
			throw new InvalidDataException(
				$"設定匯入檔案 {Path.GetFileName(stream.Name)} 太大；" +
				$"上限為 {maximumFileSize:N0} 位元組。");
		}
	}

	private static async Task WriteDurableMarkerAsync(
		string markerFilePath,
		CancellationToken cancellationToken)
	{
		await using FileStream stream = new(
			markerFilePath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 1,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		stream.Flush(flushToDisk: true);
	}

	private async Task PublishCommittedMarkerAsync(
		CancellationToken cancellationToken)
	{
		await PublishTerminalMarkerAsync(
			CommittedMarkerFileName,
			CommittedMarkerTemporaryFilePrefix,
			_flushCommittedMarkerToDisk,
			_publishCommittedMarkerAtomically,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishRestoredMarkerAsync(
		CancellationToken cancellationToken)
	{
		await PublishTerminalMarkerAsync(
			RestoredMarkerFileName,
			RestoredMarkerTemporaryFilePrefix,
			_flushRestoredMarkerToDisk,
			_publishRestoredMarkerAtomically,
			cancellationToken).ConfigureAwait(false);
	}

	private Task PublishRestoreCallbackCompletedMarkerAsync(
		CancellationToken cancellationToken)
	{
		return WriteDurableMarkerAsync(
			GetMarkerPath(RestoreCallbackCompletedMarkerFileName),
			cancellationToken);
	}

	private async Task CompleteCommittedCallbackAsync(
		CancellationToken cancellationToken)
	{
		string completedMarkerPath = GetMarkerPath(
			CommitCallbackCompletedMarkerFileName);
		if (File.Exists(completedMarkerPath))
		{
			EnsureFileIsNotReparsePoint(completedMarkerPath);
			return;
		}

		if (_onTargetsCommitted is not null)
		{
			await _onTargetsCommitted(cancellationToken).ConfigureAwait(false);
		}

		await PublishTerminalMarkerAsync(
			CommitCallbackCompletedMarkerFileName,
			CommitCallbackCompletedMarkerTemporaryFilePrefix,
			_flushCommitCallbackCompletedMarkerToDisk,
			_publishCommitCallbackCompletedMarkerAtomically,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishTerminalMarkerAsync(
		string markerFileName,
		string temporaryFilePrefix,
		Action<FileStream> flushMarkerToDisk,
		Action<string, string> publishMarkerAtomically,
		CancellationToken cancellationToken)
	{
		string markerPath = GetMarkerPath(markerFileName);
		string temporaryMarkerPath = Path.Combine(
			_transactionDirectoryPath,
			$"{temporaryFilePrefix}{Guid.NewGuid():N}{CommittedMarkerTemporaryFileSuffix}");

		try
		{
			await using (FileStream stream = new(
				temporaryMarkerPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 1,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(
					new byte[] { 1 },
					cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				flushMarkerToDisk(stream);
			}

			// There must be no cancellable work between the durable temporary
			// marker and this same-directory atomic rename. Once the rename has
			// happened, recovery must honor the selected terminal state.
			try
			{
				publishMarkerAtomically(
					temporaryMarkerPath,
					markerPath);
			}
			catch (Exception) when (
				File.Exists(markerPath) &&
				!File.Exists(temporaryMarkerPath))
			{
				// The publisher may report an error after the atomic rename. The
				// final marker is already the irreversible terminal boundary.
			}
		}
		finally
		{
			try
			{
				File.Delete(temporaryMarkerPath);
			}
			catch (Exception exception) when (IsFileOperationException(exception))
			{
				// Recovery recognizes only strictly named marker temp files and
				// can safely remove one left behind before a terminal boundary.
			}
		}
	}

	private void DeleteTransactionDirectoryBestEffort()
	{
		try
		{
			if (!Directory.Exists(_transactionDirectoryPath))
			{
				return;
			}

			EnsureTransactionDirectoryIsNotReparsePoint();
			HashSet<string> expectedFilePaths = new(
				StringComparer.OrdinalIgnoreCase)
			{
				GetMarkerPath(PreparedMarkerFileName),
				GetMarkerPath(CommittedMarkerFileName),
				GetMarkerPath(CommitCallbackCompletedMarkerFileName),
				GetMarkerPath(RestoredMarkerFileName),
				GetMarkerPath(RestoreCallbackCompletedMarkerFileName),
				GetMarkerPath(OperationBaselineVersionMarkerFileName),
				GetMarkerPath(OperationStartedMarkerFileName)
			};

			for (int index = 0; index < _targetFilePaths.Length; index++)
			{
				expectedFilePaths.Add(GetBackupFilePath(index));
				expectedFilePaths.Add(GetAbsentMarkerPath(index));
				expectedFilePaths.Add(
					GetOperationBaselineBackupFilePath(index));
				expectedFilePaths.Add(
					GetOperationBaselineAbsentMarkerPath(index));
			}

			FileSystemInfo[] entries = new DirectoryInfo(
				_transactionDirectoryPath).GetFileSystemInfos();

			if (entries.Any(entry =>
				entry is not FileInfo ||
				(entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
				(!expectedFilePaths.Contains(entry.FullName) &&
					!IsTerminalMarkerTemporaryFile(entry.Name) &&
					!IsOwnedContentMarkerFile(entry.Name))))
			{
				return;
			}

			string committedMarkerPath = GetMarkerPath(CommittedMarkerFileName);
			string commitCallbackCompletedMarkerPath = GetMarkerPath(
				CommitCallbackCompletedMarkerFileName);
			string restoredMarkerPath = GetMarkerPath(RestoredMarkerFileName);
			string restoreCallbackCompletedMarkerPath = GetMarkerPath(
				RestoreCallbackCompletedMarkerFileName);
			FileSystemInfo? committedMarker = entries.SingleOrDefault(entry =>
				string.Equals(
					entry.FullName,
					committedMarkerPath,
					StringComparison.OrdinalIgnoreCase));
			FileSystemInfo? commitCallbackCompletedMarker =
				entries.SingleOrDefault(entry =>
					string.Equals(
						entry.FullName,
						commitCallbackCompletedMarkerPath,
						StringComparison.OrdinalIgnoreCase));
			FileSystemInfo? restoredMarker = entries.SingleOrDefault(entry =>
				string.Equals(
					entry.FullName,
					restoredMarkerPath,
					StringComparison.OrdinalIgnoreCase));
			FileSystemInfo? restoreCallbackCompletedMarker =
				entries.SingleOrDefault(entry =>
					string.Equals(
						entry.FullName,
						restoreCallbackCompletedMarkerPath,
						StringComparison.OrdinalIgnoreCase));

			if ((committedMarker is not null) &&
				((restoredMarker is not null) ||
					(restoreCallbackCompletedMarker is not null)))
			{
				// Both terminal states cannot be produced by a valid transaction. Keep
				// the complete journal rather than erase evidence or choose a state.
				return;
			}
			if ((commitCallbackCompletedMarker is not null) &&
				((restoredMarker is not null) ||
					(restoreCallbackCompletedMarker is not null)))
			{
				return;
			}
			if ((committedMarker is not null) &&
				(commitCallbackCompletedMarker is null))
			{
				return;
			}

			if ((restoredMarker is not null) &&
				(restoreCallbackCompletedMarker is null))
			{
				// The callback may still need every baseline file. Cleanup begins only
				// after its durable completion marker has been published.
				return;
			}

			FileSystemInfo? terminalMarker = commitCallbackCompletedMarker ??
				restoreCallbackCompletedMarker;

			if ((restoreCallbackCompletedMarker is not null) &&
				(restoredMarker is not null))
			{
				// Once this deletion succeeds, recovery uses the completion marker and
				// no longer needs the baselines. If it fails, no baseline is touched.
				_deleteTransactionFile(restoredMarker.FullName);
			}

			foreach (FileSystemInfo entry in entries.Where(entry =>
				!ReferenceEquals(entry, terminalMarker) &&
				!ReferenceEquals(entry, restoredMarker)))
			{
				_deleteTransactionFile(entry.FullName);
			}

			if (terminalMarker is not null)
			{
				_deleteTransactionFile(terminalMarker.FullName);
			}

			Directory.Delete(_transactionDirectoryPath, recursive: false);
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			// A retained committed/completion marker makes a partially cleaned
			// journal safe to inspect again on restart.
		}
	}

	private string GetAbsentMarkerPath(int targetIndex)
	{
		return Path.Combine(
			_transactionDirectoryPath,
			$"target-{targetIndex}{AbsentMarkerSuffix}");
	}

	private string GetBackupFilePath(int targetIndex)
	{
		return Path.Combine(
			_transactionDirectoryPath,
			$"target-{targetIndex}{BackupFileSuffix}");
	}

	private string GetOperationBaselineAbsentMarkerPath(int targetIndex)
	{
		return Path.Combine(
			_transactionDirectoryPath,
			$"target-{targetIndex}{OperationBaselineAbsentMarkerSuffix}");
	}

	private string GetOperationBaselineBackupFilePath(int targetIndex)
	{
		return Path.Combine(
			_transactionDirectoryPath,
			$"target-{targetIndex}{OperationBaselineBackupFileSuffix}");
	}

	private string GetOwnedContentMarkerPath(
		int targetIndex,
		string contentHash)
	{
		return Path.Combine(
			_transactionDirectoryPath,
			$"target-{targetIndex}{OwnedContentMarkerPrefix}{contentHash}");
	}

	private string GetMarkerPath(string markerFileName)
	{
		return Path.Combine(_transactionDirectoryPath, markerFileName);
	}

	private static bool IsTerminalMarkerTemporaryFile(string fileName)
	{
		string? matchedPrefix = fileName.StartsWith(
			CommittedMarkerTemporaryFilePrefix,
			StringComparison.Ordinal)
			? CommittedMarkerTemporaryFilePrefix
			: fileName.StartsWith(
					CommitCallbackCompletedMarkerTemporaryFilePrefix,
					StringComparison.Ordinal)
				? CommitCallbackCompletedMarkerTemporaryFilePrefix
				: fileName.StartsWith(
					RestoredMarkerTemporaryFilePrefix,
					StringComparison.Ordinal)
					? RestoredMarkerTemporaryFilePrefix
					: null;

		if ((matchedPrefix is null) ||
			!fileName.EndsWith(
				CommittedMarkerTemporaryFileSuffix,
				StringComparison.Ordinal))
		{
			return false;
		}

		int identifierLength = fileName.Length -
			matchedPrefix.Length -
			CommittedMarkerTemporaryFileSuffix.Length;

		if (identifierLength != 32)
		{
			return false;
		}

		ReadOnlySpan<char> identifier = fileName.AsSpan(
			matchedPrefix.Length,
			identifierLength);
		return Guid.TryParseExact(identifier, "N", out _);
	}

	private bool IsOwnedContentMarkerFile(string fileName)
	{
		for (int index = 0; index < _targetFilePaths.Length; index++)
		{
			string prefix = $"target-{index}{OwnedContentMarkerPrefix}";
			if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
				(fileName.Length != prefix.Length + Sha256HexLength))
			{
				continue;
			}

			return fileName.AsSpan(prefix.Length).IndexOfAnyExcept(
				"0123456789abcdef") < 0;
		}

		return false;
	}

	private void EnsureTransactionDirectoryIsNotReparsePoint()
	{
		FileAttributes attributes = File.GetAttributes(_transactionDirectoryPath);

		if ((attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException("設定匯入交易目錄不可為連結或重新解析點。");
		}
	}

	private static void EnsureFileIsNotReparsePoint(string filePath)
	{
		FileAttributes attributes = File.GetAttributes(filePath);

		if ((attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new IOException("設定匯入檔案不可為連結的檔案。");
		}
	}

	private static bool IsFileOperationException(Exception exception)
	{
		return exception is IOException or
			UnauthorizedAccessException or
			NotSupportedException or
			ArgumentException;
	}
}
