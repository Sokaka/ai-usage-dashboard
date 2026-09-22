using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class UpdateCheckSchedulerTests
{
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		6,
		0,
		0,
		TimeSpan.Zero);

	[Fact]
	public void Evaluate_WhenNoAttemptExists_UsesThirtySecondStartupDelay()
	{
		DateTimeOffset startupDue = TestNow + TimeSpan.FromSeconds(30);

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			UpdateCheckPersistentState.Default,
			TestNow,
			startupDue);

		Assert.Equal(startupDue, evaluation.NextAutomaticCheckUtc);
		Assert.False(evaluation.IsDue(TestNow));
		Assert.True(evaluation.IsDue(startupDue));
	}

	[Fact]
	public void Evaluate_AfterSuccess_ThrottlesForTwentyFourHours()
	{
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow + TimeSpan.FromSeconds(30));

		Assert.Equal(
			TestNow + TimeSpan.FromHours(24),
			evaluation.NextAutomaticCheckUtc);
	}

	[Theory]
	[InlineData(1, 15)]
	[InlineData(2, 60)]
	[InlineData(3, 240)]
	[InlineData(4, 1440)]
	[InlineData(50, 1440)]
	public void Evaluate_AfterFailures_UsesBoundedBackoff(
		int failureCount,
		int expectedDelayMinutes)
	{
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				ConsecutiveFailureCount = failureCount
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow);

		Assert.Equal(
			TestNow + TimeSpan.FromMinutes(expectedDelayMinutes),
			evaluation.NextAutomaticCheckUtc);
	}

	[Fact]
	public void Evaluate_WhenAutoCheckIsDisabled_HasNoAutomaticDueTime()
	{
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				IsAutoCheckEnabled = false
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow);

		Assert.Null(evaluation.NextAutomaticCheckUtc);
	}

	[Fact]
	public void Evaluate_WhenPastTimestampIsFarInFuture_ResetsThrottleAfterClockRollback()
	{
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow + TimeSpan.FromHours(2),
				ConsecutiveFailureCount = 3
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow - TimeSpan.FromHours(1));

		Assert.True(evaluation.WasClockAnomaly);
		Assert.True(evaluation.WasStateCleanedUp);
		Assert.Null(evaluation.State.LastAttemptUtc);
		Assert.Equal(0, evaluation.State.ConsecutiveFailureCount);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			evaluation.NextAutomaticCheckUtc);
	}

	[Fact]
	public void Evaluate_WhenRuntimeGateIsFarInFuture_ResetsGateAfterClockRollback()
	{
		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			UpdateCheckPersistentState.Default,
			TestNow,
			TestNow + TimeSpan.FromHours(2));

		Assert.True(evaluation.WasClockAnomaly);
		Assert.False(evaluation.WasStateCleanedUp);
		Assert.Equal(
			TestNow + TimeSpan.FromSeconds(30),
			evaluation.NextAutomaticCheckUtc);
	}

	[Fact]
	public void Evaluate_WhenClockMovesForward_MakesOverdueSuccessDueImmediately()
	{
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow
			};
		DateTimeOffset movedForwardUtc = TestNow + TimeSpan.FromHours(25);

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			movedForwardUtc,
			TestNow + TimeSpan.FromSeconds(30));

		Assert.False(evaluation.WasClockAnomaly);
		Assert.Equal(
			TestNow + TimeSpan.FromHours(24),
			evaluation.NextAutomaticCheckUtc);
		Assert.True(evaluation.IsDue(movedForwardUtc));
	}

	[Fact]
	public void Evaluate_WhenSuccessfulTimestampIsInFuture_ClearsDerivedNotificationState()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			10,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow + TimeSpan.FromHours(2),
				LastSuccessfulCheckUtc = TestNow + TimeSpan.FromHours(2),
				HighestObservedReleaseSequence = 10,
				LastKnownResult = knownResult,
				LastBalloonAttemptKey = knownResult.NotificationKey.ToString(),
				Snooze = new UpdateSnoozeState(
					"1.0.4",
					10,
					TestNow + TimeSpan.FromHours(3))
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow);

		Assert.True(evaluation.WasClockAnomaly);
		Assert.Null(evaluation.State.LastKnownResult);
		Assert.Null(evaluation.State.LastBalloonAttemptKey);
		Assert.Null(evaluation.State.Snooze);
	}

	[Fact]
	public void Evaluate_WhenSnoozeExpired_ClearsOnlySnooze()
	{
		UpdateKnownResult knownResult = new(
			"1.0.3",
			"1.0.4",
			10,
			IsUpdateAvailable: true);
		UpdateCheckPersistentState state =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow - TimeSpan.FromHours(1),
				LastSuccessfulCheckUtc = TestNow - TimeSpan.FromHours(1),
				HighestObservedReleaseSequence = 10,
				LastKnownResult = knownResult,
				Snooze = new UpdateSnoozeState(
					"1.0.4",
					10,
					TestNow)
			};

		UpdateScheduleEvaluation evaluation = UpdateCheckScheduler.Evaluate(
			state,
			TestNow,
			TestNow);

		Assert.False(evaluation.WasClockAnomaly);
		Assert.True(evaluation.WasStateCleanedUp);
		Assert.Null(evaluation.State.Snooze);
		Assert.Equal(knownResult, evaluation.State.LastKnownResult);
	}
}
