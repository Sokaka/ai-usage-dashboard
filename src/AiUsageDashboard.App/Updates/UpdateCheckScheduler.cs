namespace AiUsageDashboard.App.Updates;

internal sealed record UpdateScheduleEvaluation(
	UpdateCheckPersistentState State,
	DateTimeOffset AutomaticChecksNotBeforeUtc,
	DateTimeOffset? NextAutomaticCheckUtc,
	bool WasClockAnomaly,
	bool WasStateCleanedUp)
{
	internal bool IsDue(DateTimeOffset utcNow)
	{
		return NextAutomaticCheckUtc is DateTimeOffset nextCheckUtc &&
			(nextCheckUtc <= utcNow);
	}
}

internal static class UpdateCheckScheduler
{
	internal static readonly TimeSpan AutomaticCheckNoticeDelay =
		TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan ClockFutureTolerance =
		TimeSpan.FromMinutes(5);
	internal static readonly TimeSpan ResultFreshness =
		TimeSpan.FromHours(24);
	internal static readonly TimeSpan SnoozeDuration =
		TimeSpan.FromHours(24);

	internal static UpdateScheduleEvaluation Evaluate(
		UpdateCheckPersistentState state,
		DateTimeOffset utcNow,
		DateTimeOffset automaticChecksNotBeforeUtc)
	{
		ArgumentNullException.ThrowIfNull(state);
		utcNow = utcNow.ToUniversalTime();
		automaticChecksNotBeforeUtc =
			automaticChecksNotBeforeUtc.ToUniversalTime();
		DateTimeOffset futureThreshold = SafeAdd(
			utcNow,
			ClockFutureTolerance);
		bool hasFutureAutomaticGate =
			automaticChecksNotBeforeUtc > futureThreshold;
		bool hasFutureAttempt = state.LastAttemptUtc is DateTimeOffset attemptUtc &&
			(attemptUtc.ToUniversalTime() > futureThreshold);
		bool hasFutureSuccess =
			state.LastSuccessfulCheckUtc is DateTimeOffset successfulCheckUtc &&
			(successfulCheckUtc.ToUniversalTime() > futureThreshold);
		bool hasFutureSnooze = state.Snooze is UpdateSnoozeState snooze &&
			(snooze.SnoozedUntilUtc.ToUniversalTime() > SafeAdd(
				utcNow,
				SnoozeDuration + ClockFutureTolerance));
		bool hasPersistentClockAnomaly =
			hasFutureAttempt || hasFutureSuccess || hasFutureSnooze;
		bool wasClockAnomaly =
			hasFutureAutomaticGate || hasPersistentClockAnomaly;
		bool hasExpiredSnooze = state.Snooze is UpdateSnoozeState currentSnooze &&
			(currentSnooze.SnoozedUntilUtc.ToUniversalTime() <= utcNow);
		UpdateCheckPersistentState normalizedState = state with
		{
			LastAttemptUtc = hasFutureAttempt
				? null
				: state.LastAttemptUtc?.ToUniversalTime(),
			LastSuccessfulCheckUtc = hasFutureSuccess
				? null
				: state.LastSuccessfulCheckUtc?.ToUniversalTime(),
			ConsecutiveFailureCount = hasFutureAttempt
				? 0
				: state.ConsecutiveFailureCount,
			LastKnownResult = hasFutureSuccess
				? null
				: state.LastKnownResult,
			LastBalloonAttemptKey = hasFutureSuccess
				? null
				: state.LastBalloonAttemptKey,
			Snooze = hasFutureSuccess || hasFutureSnooze || hasExpiredSnooze
				? null
				: state.Snooze is null
					? null
					: state.Snooze with
					{
						SnoozedUntilUtc =
							state.Snooze.SnoozedUntilUtc.ToUniversalTime()
					}
		};

		if (wasClockAnomaly)
		{
			automaticChecksNotBeforeUtc = SafeAdd(
				utcNow,
				AutomaticCheckNoticeDelay);
		}

		DateTimeOffset? nextAutomaticCheckUtc =
			normalizedState.IsAutoCheckEnabled
				? GetCadenceDueUtc(
					normalizedState,
					automaticChecksNotBeforeUtc)
				: null;

		return new UpdateScheduleEvaluation(
			normalizedState,
			automaticChecksNotBeforeUtc,
			nextAutomaticCheckUtc,
			wasClockAnomaly,
			hasPersistentClockAnomaly || hasExpiredSnooze);
	}

	internal static TimeSpan GetFailureDelay(int consecutiveFailureCount)
	{
		if (consecutiveFailureCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(consecutiveFailureCount));
		}

		return consecutiveFailureCount switch
		{
			1 => TimeSpan.FromMinutes(15),
			2 => TimeSpan.FromHours(1),
			3 => TimeSpan.FromHours(4),
			_ => TimeSpan.FromHours(24)
		};
	}

	private static DateTimeOffset GetCadenceDueUtc(
		UpdateCheckPersistentState state,
		DateTimeOffset automaticChecksNotBeforeUtc)
	{
		DateTimeOffset cadenceDueUtc = automaticChecksNotBeforeUtc;

		if ((state.ConsecutiveFailureCount > 0) &&
			(state.LastAttemptUtc is DateTimeOffset failedAttemptUtc))
		{
			cadenceDueUtc = SafeAdd(
				failedAttemptUtc.ToUniversalTime(),
				GetFailureDelay(state.ConsecutiveFailureCount));
		}
		else if (state.LastSuccessfulCheckUtc is
			DateTimeOffset successfulCheckUtc)
		{
			cadenceDueUtc = SafeAdd(
				successfulCheckUtc.ToUniversalTime(),
				ResultFreshness);
		}

		return cadenceDueUtc > automaticChecksNotBeforeUtc
			? cadenceDueUtc
			: automaticChecksNotBeforeUtc;
	}

	private static DateTimeOffset SafeAdd(
		DateTimeOffset value,
		TimeSpan duration)
	{
		try
		{
			return value + duration;
		}
		catch (ArgumentOutOfRangeException)
		{
			return DateTimeOffset.MaxValue;
		}
	}
}
