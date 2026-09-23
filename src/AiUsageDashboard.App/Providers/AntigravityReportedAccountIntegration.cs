using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal sealed class AntigravityReportedAccountSource :
	IAntigravityReportedAccountSource
{
	private int _isLegacyCleanupComplete;

	public async ValueTask<AntigravityReportedAccountObservation> ReadAsync(
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (Volatile.Read(ref _isLegacyCleanupComplete) == 0)
		{
			bool removed = await Task.Run(
				AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts,
				cancellationToken).ConfigureAwait(false);

			if (removed)
			{
				Interlocked.Exchange(ref _isLegacyCleanupComplete, 1);
			}
		}

		return new AntigravityReportedAccountObservation(
			Generation: 0,
			Account: null);
	}

	public async ValueTask<bool> RemoveOwnedAsync(
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		bool removed = await Task.Run(
			AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts,
			cancellationToken).ConfigureAwait(false);

		if (removed)
		{
			Interlocked.Exchange(ref _isLegacyCleanupComplete, 1);
		}

		return removed;
	}
}
