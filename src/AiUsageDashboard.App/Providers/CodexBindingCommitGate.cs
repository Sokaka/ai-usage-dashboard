namespace AiUsageDashboard.App.Providers;

internal sealed class CodexBindingCommitGate
{
	private sealed class Lease : IDisposable
	{
		private SemaphoreSlim? _gate;

		internal Lease(SemaphoreSlim gate)
		{
			_gate = gate;
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _gate, null)?.Release();
		}
	}

	private readonly SemaphoreSlim _gate = new(1, 1);

	internal async ValueTask<IDisposable> EnterAsync(
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		return new Lease(_gate);
	}
}
