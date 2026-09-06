namespace AiUsageDashboard.App.Persistence;

internal sealed class SettingsPersistenceGate
{
	private sealed class Ownership
	{
		private int _isActive = 1;

		internal Ownership(
			SettingsPersistenceGate gate,
			Ownership? previous)
		{
			Gate = gate;
			Previous = previous;
		}

		internal bool IsActive => Volatile.Read(ref _isActive) == 1;

		internal SettingsPersistenceGate Gate { get; }

		internal Ownership? Previous { get; }

		internal void Deactivate()
		{
			Interlocked.Exchange(ref _isActive, 0);
		}
	}

	private static readonly AsyncLocal<Ownership?> CurrentOwnership = new();
	private readonly SemaphoreSlim _gate = new(1, 1);

	internal async Task RunExclusiveAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(operation);

		for (Ownership? inheritedOwnership = CurrentOwnership.Value;
			inheritedOwnership is not null;
			inheritedOwnership = inheritedOwnership.Previous)
		{
			if (ReferenceEquals(inheritedOwnership.Gate, this) &&
				inheritedOwnership.IsActive)
			{
				await operation(cancellationToken).ConfigureAwait(false);
				return;
			}
		}

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		Ownership? previousOwnership = CurrentOwnership.Value;
		Ownership ownership = new(this, previousOwnership);
		CurrentOwnership.Value = ownership;

		try
		{
			await operation(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// A child Task can inherit this ownership object. Deactivate it before
			// releasing the semaphore so a late continuation cannot permanently
			// bypass a newer transaction that owns the gate.
			ownership.Deactivate();
			CurrentOwnership.Value = previousOwnership;
			_gate.Release();
		}
	}

	internal async Task<TResult> RunExclusiveAsync<TResult>(
		Func<CancellationToken, Task<TResult>> operation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(operation);
		TResult? result = default;

		await RunExclusiveAsync(
			async operationCancellationToken =>
			{
				result = await operation(operationCancellationToken)
					.ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);

		return result!;
	}
}
