namespace AiUsageDashboard.AntigravitySpike;

internal sealed record ConPtyStartRequest(
	string ExecutablePath,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	IReadOnlyDictionary<string, string> Environment,
	short Columns,
	short Rows,
	int MaximumSavedOutputBytes,
	TimeSpan CleanupTimeout);

internal sealed record ConPtyOutputSnapshot(
	ReadOnlyMemory<byte> SavedBytes,
	int SavedByteOffset,
	int SavedByteCount,
	long TotalBytesRead,
	bool IsSavedByteLimitExceeded,
	Exception? ReadFailure);

internal interface IConPtySession : IAsyncDisposable
{
	int ProcessId { get; }

	ConPtyOutputSnapshot GetOutputSnapshot(int savedByteOffset = 0);

	ValueTask WriteInputAsync(
		ReadOnlyMemory<byte> input,
		CancellationToken cancellationToken = default);

	Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

	ValueTask TerminateAsync(CancellationToken cancellationToken = default);
}
