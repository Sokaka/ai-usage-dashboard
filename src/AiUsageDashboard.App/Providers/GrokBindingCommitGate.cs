namespace AiUsageDashboard.App.Providers;

internal sealed class GrokBindingCommitGate
{
	private sealed class Lease : IDisposable
	{
		private SemaphoreSlim? _gate;

		public Lease(SemaphoreSlim gate)
		{
			_gate = gate;
		}

		public void Dispose()
		{
			SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
			gate?.Release();
		}
	}

	private readonly SemaphoreSlim _gate = new(1, 1);

	public async ValueTask<IDisposable> EnterAsync(
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		return new Lease(_gate);
	}
}
