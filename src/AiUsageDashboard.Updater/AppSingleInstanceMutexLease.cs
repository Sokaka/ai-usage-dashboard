using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class AppSingleInstanceMutexLease : IDisposable
{
	private readonly Mutex _mutex;
	private bool _ownsMutex;

	private AppSingleInstanceMutexLease(Mutex mutex)
	{
		_mutex = mutex;
		_ownsMutex = true;
	}

	internal static AppSingleInstanceMutexLease Acquire(TimeSpan timeout)
	{
		return Acquire(
			UpdateInstanceNames.CreateCurrentUserSingleInstanceMutexName(),
			timeout);
	}

	internal static AppSingleInstanceMutexLease Acquire(
		string mutexName,
		TimeSpan timeout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Mutex mutex = new(
			initiallyOwned: false,
			mutexName);
		bool ownsMutex = false;

		try
		{
			try
			{
				ownsMutex = mutex.WaitOne(timeout);
			}
			catch (AbandonedMutexException)
			{
				ownsMutex = true;
			}

			if (!ownsMutex)
			{
				throw new RunningPayloadProcessGateException(
					"The dashboard single-instance mutex is still owned. " +
					"Close AI Usage and retry the update.");
			}

			return new AppSingleInstanceMutexLease(mutex);
		}
		catch
		{
			try
			{
				if (ownsMutex)
				{
					mutex.ReleaseMutex();
				}
			}
			finally
			{
				mutex.Dispose();
			}

			throw;
		}
	}

	public void Dispose()
	{
		if (!_ownsMutex)
		{
			return;
		}

		_ownsMutex = false;
		try
		{
			_mutex.ReleaseMutex();
		}
		finally
		{
			_mutex.Dispose();
		}
	}
}
