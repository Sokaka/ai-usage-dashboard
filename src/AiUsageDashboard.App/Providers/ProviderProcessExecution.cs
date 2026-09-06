using System.Diagnostics;
using System.IO;

namespace AiUsageDashboard.App.Providers;

internal static class ProviderProcessExecution
{
	internal static async Task<T> RunSynchronousAsync<T>(
		Func<T> operation,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null,
		Func<T, Task>? lateResultCleanup = null)
	{
		ArgumentNullException.ThrowIfNull(operation);
		cancellationToken.ThrowIfCancellationRequested();
		Task<T> operationTask = Task.Run(operation, cancellationToken);

		try
		{
			return await operationTask.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			Task trackedOperation = lateResultCleanup is null
				? operationTask
				: CleanupLateResultAsync(operationTask, lateResultCleanup);
			operationTracker?.Track(trackedOperation);
			ObserveFault(trackedOperation);
			throw;
		}
	}

	internal static async Task<T> RunAsynchronousAsync<T>(
		Func<CancellationToken, Task<T>> operation,
		CancellationToken cancellationToken,
		ProviderProcessOperationTracker? operationTracker = null,
		Func<T, Task>? lateResultCleanup = null)
	{
		ArgumentNullException.ThrowIfNull(operation);
		cancellationToken.ThrowIfCancellationRequested();
		Task<T> operationTask = Task.Run(
			() => operation(cancellationToken),
			cancellationToken);

		try
		{
			return await operationTask.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			Task trackedOperation = lateResultCleanup is null
				? operationTask
				: CleanupLateResultAsync(operationTask, lateResultCleanup);
			operationTracker?.Track(trackedOperation);
			ObserveFault(trackedOperation);
			throw;
		}
	}

	internal static bool TryNormalizeLocalExecutablePath(
		string? candidate,
		out string fullPath)
	{
		fullPath = string.Empty;

		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		try
		{
			if (!Path.IsPathFullyQualified(candidate))
			{
				return false;
			}

			string normalizedPath = Path.GetFullPath(candidate);
			string? root = Path.GetPathRoot(normalizedPath);

			if (string.IsNullOrWhiteSpace(root) ||
				root.StartsWith("\\\\", StringComparison.Ordinal) ||
				(root.Length < 2) ||
				(root[1] != Path.VolumeSeparatorChar))
			{
				return false;
			}

			DriveType driveType = new DriveInfo(root).DriveType;

			if ((driveType != DriveType.Fixed) &&
				(driveType != DriveType.Ram))
			{
				return false;
			}

			fullPath = normalizedPath;
			return true;
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is IOException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException) ||
			(exception is UnauthorizedAccessException))
		{
			return false;
		}
	}

	internal static async Task WaitForConfirmedExitAsync(
		Process process,
		ProviderProcessOperationTracker operationTracker)
	{
		ArgumentNullException.ThrowIfNull(process);
		ArgumentNullException.ThrowIfNull(operationTracker);

		bool isExitConfirmed = await WaitForConfirmedCompletionAsync(
			() => process.WaitForExitAsync(CancellationToken.None),
			() => process.HasExited).ConfigureAwait(false);

		if (isExitConfirmed)
		{
			return;
		}

		operationTracker.MarkContainmentCompromised();

		// 維持 cleanup pending，讓 bounded cleanup 仍能執行 terminate 重試；
		// lease 的 process-lifetime 保護已由 tracker quarantine 提供。
		await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
	}

	internal static async Task<bool> WaitForConfirmedCompletionAsync(
		Func<Task> waitForCompletion,
		Func<bool> isCompletionConfirmed)
	{
		ArgumentNullException.ThrowIfNull(waitForCompletion);
		ArgumentNullException.ThrowIfNull(isCompletionConfirmed);

		try
		{
			await waitForCompletion().ConfigureAwait(false);
			return true;
		}
		catch
		{
			try
			{
				if (isCompletionConfirmed())
				{
					return true;
				}
			}
			catch
			{
				// 無法取得正向完成證據時，由呼叫端隔離 operation leases。
			}
		}

		return false;
	}

	internal static void ObserveFault(Task task)
	{
		_ = task.ContinueWith(
			completedTask => _ = completedTask.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously |
				TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	private static async Task CleanupLateResultAsync<T>(
		Task<T> operationTask,
		Func<T, Task> lateResultCleanup)
	{
		T result = await operationTask.ConfigureAwait(false);
		await lateResultCleanup(result).ConfigureAwait(false);
	}
}

internal sealed class ProviderProcessOperationTracker
{
	private sealed class TrackedLease : IDisposable
	{
		private readonly ProviderProcessOperationTracker _tracker;
		private IDisposable? _lease;

		internal TrackedLease(
			ProviderProcessOperationTracker tracker,
			IDisposable lease)
		{
			_tracker = tracker;
			_lease = lease;
		}

		public void Dispose()
		{
			IDisposable? lease = Interlocked.Exchange(ref _lease, null);

			if (lease is null)
			{
				return;
			}

			_tracker.ReleaseOrTransferLease(lease);
		}
	}

	private static readonly List<IDisposable> ProcessLifetimeQuarantinedLeases = new();
	private static readonly object ProcessLifetimeQuarantineGate = new();
	private readonly Action? _containmentCompromised;
	private readonly HashSet<IDisposable> _deferredLeases = new();
	private readonly List<Task> _operations = new();
	private readonly object _sync = new();
	private bool _isContainmentCompromised;

	internal bool IsContainmentCompromised
	{
		get
		{
			lock (_sync)
			{
				return _isContainmentCompromised;
			}
		}
	}

	internal ProviderProcessOperationTracker(
		Action? containmentCompromised = null)
	{
		_containmentCompromised = containmentCompromised;
	}

	internal IDisposable HoldLease(IDisposable lease)
	{
		ArgumentNullException.ThrowIfNull(lease);
		return new TrackedLease(this, lease);
	}

	internal void MarkContainmentCompromised()
	{
		Action? containmentCompromised = null;

		lock (_sync)
		{
			if (_isContainmentCompromised)
			{
				return;
			}

			_isContainmentCompromised = true;
			containmentCompromised = _containmentCompromised;

			foreach (IDisposable lease in _deferredLeases)
			{
				QuarantineLease(lease);
			}

			_deferredLeases.Clear();
		}

		containmentCompromised?.Invoke();
	}

	internal void Track(Task operation)
	{
		ArgumentNullException.ThrowIfNull(operation);

		if (operation.IsCompleted)
		{
			return;
		}

		lock (_sync)
		{
			_operations.Add(operation);
		}
	}

	private async Task ReleaseLeaseWhenOperationsCompleteAsync(
		IDisposable lease)
	{
		try
		{
			while (true)
			{
				Task[] pendingOperations;

				lock (_sync)
				{
					_operations.RemoveAll(operation => operation.IsCompleted);

					if (_operations.Count == 0)
					{
						break;
					}

					pendingOperations = _operations.ToArray();
				}

				try
				{
					await Task.WhenAll(pendingOperations).ConfigureAwait(false);
				}
				catch
				{
					// Only completion matters; provider failures are observed elsewhere.
				}
			}
		}
		finally
		{
			CompleteDeferredLease(lease);
		}
	}

	private void CompleteDeferredLease(IDisposable lease)
	{
		lock (_sync)
		{
			if (!_deferredLeases.Remove(lease))
			{
				return;
			}

			if (_isContainmentCompromised)
			{
				QuarantineLease(lease);
				return;
			}

			lease.Dispose();
		}
	}

	private static void QuarantineLease(IDisposable lease)
	{
		lock (ProcessLifetimeQuarantineGate)
		{
			ProcessLifetimeQuarantinedLeases.Add(lease);
		}
	}

	private void ReleaseOrTransferLease(IDisposable lease)
	{
		lock (_sync)
		{
			_operations.RemoveAll(operation => operation.IsCompleted);

			if (_isContainmentCompromised)
			{
				QuarantineLease(lease);
				return;
			}

			if (_operations.Count == 0)
			{
				lease.Dispose();
				return;
			}

			_deferredLeases.Add(lease);
		}

		Task releaseTask = ReleaseLeaseWhenOperationsCompleteAsync(lease);
		ProviderProcessExecution.ObserveFault(releaseTask);
	}
}
