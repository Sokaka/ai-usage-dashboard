namespace AiUsageDashboard.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
	private static readonly TimeSpan CleanupRetryTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan CleanupRetryDelay =
		TimeSpan.FromMilliseconds(20);
	private readonly string _testRootPath;
	private readonly Action? _onCleanupRetry;
	private bool _wasRemovedExternally;

	internal string Path { get; }

	internal TemporaryDirectory(Action? onCleanupRetry = null)
	{
		_testRootPath = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"AiUsageDashboard.Tests");
		Path = System.IO.Path.Combine(_testRootPath, Guid.NewGuid().ToString("N"));
		_onCleanupRetry = onCleanupRetry;
		Directory.CreateDirectory(Path);
	}

	internal void MarkAsRemovedExternally()
	{
		if (Directory.Exists(Path))
		{
			throw new InvalidOperationException(
				"測試暫存目錄尚未由外部清理完成。");
		}

		_wasRemovedExternally = true;
	}

	public void Dispose()
	{
		if (_wasRemovedExternally || !Directory.Exists(Path))
		{
			return;
		}

		string normalizedTestRoot = System.IO.Path.GetFullPath(_testRootPath)
			.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
			System.IO.Path.DirectorySeparatorChar;
		string normalizedPath = System.IO.Path.GetFullPath(Path);

		if (!normalizedPath.StartsWith(
			normalizedTestRoot,
			StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("拒絕刪除測試根目錄以外的路徑。");
		}

		long cleanupStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

		while (Directory.Exists(normalizedPath))
		{
			try
			{
				Directory.Delete(normalizedPath, recursive: true);
			}
			catch (Exception exception) when (
				(exception is IOException or UnauthorizedAccessException) &&
				(System.Diagnostics.Stopwatch.GetElapsedTime(cleanupStartedAt) <
					CleanupRetryTimeout))
			{
				// A just-exited Windows test image or scanner can briefly retain
				// an executable or DLL. Retry the exact validated test directory
				// so cleanup does not mask the test's original failure.
				_onCleanupRetry?.Invoke();
				Thread.Sleep(CleanupRetryDelay);
			}
		}
	}
}
