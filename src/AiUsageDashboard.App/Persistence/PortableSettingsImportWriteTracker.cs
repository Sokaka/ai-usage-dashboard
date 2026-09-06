namespace AiUsageDashboard.App.Persistence;

internal sealed class PortableSettingsImportWriteTracker
{
	private sealed class TrackingSession
	{
		private int _isActive = 1;

		internal TrackingSession(
			PortableSettingsImportWriteTracker tracker,
			Func<string, string, CancellationToken, Task> recordPreparedWrite,
			TrackingSession? previous)
		{
			Tracker = tracker;
			RecordPreparedWrite = recordPreparedWrite;
			Previous = previous;
		}

		internal bool IsActive => Volatile.Read(ref _isActive) == 1;

		internal TrackingSession? Previous { get; }

		internal Func<string, string, CancellationToken, Task>
			RecordPreparedWrite { get; }

		internal PortableSettingsImportWriteTracker Tracker { get; }

		internal void Deactivate()
		{
			Interlocked.Exchange(ref _isActive, 0);
		}
	}

	private static readonly AsyncLocal<TrackingSession?> CurrentSession = new();

	internal async Task RunTrackedAsync(
		Func<CancellationToken, Task> operation,
		Func<string, string, CancellationToken, Task> recordPreparedWrite,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentNullException.ThrowIfNull(recordPreparedWrite);
		TrackingSession? previousSession = CurrentSession.Value;
		TrackingSession session = new(this, recordPreparedWrite, previousSession);
		CurrentSession.Value = session;

		try
		{
			await operation(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// Child tasks inherit the same session object. Mark it inactive before
			// returning so a late writer cannot add ownership to a finished journal.
			session.Deactivate();
			CurrentSession.Value = previousSession;
		}
	}

	internal Task RecordPreparedWriteAsync(
		string destinationFilePath,
		string preparedFilePath,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(preparedFilePath);

		for (TrackingSession? session = CurrentSession.Value;
			session is not null;
			session = session.Previous)
		{
			if (ReferenceEquals(session.Tracker, this) && session.IsActive)
			{
				return session.RecordPreparedWrite(
					destinationFilePath,
					preparedFilePath,
					cancellationToken);
			}
		}

		return Task.CompletedTask;
	}
}
