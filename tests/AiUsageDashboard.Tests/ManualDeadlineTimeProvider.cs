namespace AiUsageDashboard.Tests;

internal sealed class ManualDeadlineTimeProvider : TimeProvider
{
	private sealed class DeadlineTimer : ITimer
	{
		private readonly TimerCallback _callback;
		private readonly object? _state;
		private int _isDisposed;

		internal DeadlineTimer(TimerCallback callback, object? state)
		{
			_callback = callback;
			_state = state;
		}

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			throw new NotSupportedException("The test deadline must not be rescheduled.");
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _isDisposed, 1);
		}

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}

		internal void Expire()
		{
			if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
			{
				throw new InvalidOperationException("The test deadline was already disposed or expired.");
			}

			_callback(_state);
		}
	}

	private readonly TimeSpan _expectedTimeout;
	private DeadlineTimer? _timer;

	internal ManualDeadlineTimeProvider(TimeSpan expectedTimeout)
	{
		_expectedTimeout = expectedTimeout;
	}

	public override ITimer CreateTimer(
		TimerCallback callback,
		object? state,
		TimeSpan dueTime,
		TimeSpan period)
	{
		ArgumentNullException.ThrowIfNull(callback);
		Assert.Null(_timer);
		Assert.Equal(_expectedTimeout, dueTime);
		Assert.Equal(Timeout.InfiniteTimeSpan, period);
		_timer = new DeadlineTimer(callback, state);
		return _timer;
	}

	internal void Expire()
	{
		if (_timer is null)
		{
			throw new InvalidOperationException("The operation did not create its test deadline timer.");
		}

		_timer.Expire();
	}
}
