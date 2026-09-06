using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityObservationGatesTests
{
	[Fact]
	public void Bind_WithOneSessionAndStableIdentity_ReturnsSameQuotaPayload()
	{
		Guid sessionId = Guid.NewGuid();
		DateTimeOffset observedAt = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
		AntigravityUsageResult usage = CreateUsage("user@example.invalid");
		AntigravityQuotaObservation quota = new(
			usage,
			1234,
			sessionId,
			observedAt);

		AntigravityBoundObservation result = AntigravityIdentityBindingGate.Bind(
			new AntigravityIdentityObservation(
				" User@Example.Invalid ",
				1234,
				sessionId,
				observedAt.AddSeconds(-1)),
			quota,
			new AntigravityIdentityObservation(
				"USER@example.invalid",
				1234,
				sessionId,
				observedAt.AddSeconds(1)));

		Assert.Equal("user@example.invalid", result.AccountIdentity);
		Assert.Same(usage, result.Usage);
		Assert.Equal(1234, result.RootProcessId);
		Assert.Equal(sessionId, result.SessionId);
	}

	[Fact]
	public void Bind_WhenIdentityChangesOrPayloadDiffers_FailsClosed()
	{
		Guid sessionId = Guid.NewGuid();
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;

		Assert.Throws<InvalidDataException>(() => Bind(
			"first@example.invalid",
			CreateUsage("first@example.invalid"),
			"second@example.invalid",
			1234,
			1234,
			sessionId,
			sessionId,
			observedAt));
		Assert.Throws<InvalidDataException>(() => Bind(
			"first@example.invalid",
			CreateUsage("other@example.invalid"),
			"first@example.invalid",
			1234,
			1234,
			sessionId,
			sessionId,
			observedAt));
	}

	[Fact]
	public void Bind_WhenQuotaComesFromAnotherRootOrSession_FailsClosed()
	{
		Guid sessionId = Guid.NewGuid();
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;

		Assert.Throws<InvalidDataException>(() => Bind(
			"user@example.invalid",
			CreateUsage("user@example.invalid"),
			"user@example.invalid",
			1234,
			5678,
			sessionId,
			sessionId,
			observedAt));
		Assert.Throws<InvalidDataException>(() => Bind(
			"user@example.invalid",
			CreateUsage("user@example.invalid"),
			"user@example.invalid",
			1234,
			1234,
			sessionId,
			Guid.NewGuid(),
			observedAt));
	}

	[Fact]
	public void Bind_WhenQuotaIsOutsideIdentitySandwich_FailsClosed()
	{
		Guid sessionId = Guid.NewGuid();
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AntigravityUsageResult usage = CreateUsage("user@example.invalid");

		Assert.Throws<InvalidDataException>(() =>
			AntigravityIdentityBindingGate.Bind(
				new AntigravityIdentityObservation(
					"user@example.invalid",
					1234,
					sessionId,
					observedAt),
				new AntigravityQuotaObservation(
					usage,
					1234,
					sessionId,
					observedAt.AddSeconds(-1)),
				new AntigravityIdentityObservation(
					"user@example.invalid",
					1234,
					sessionId,
					observedAt.AddSeconds(1))));
	}

	[Fact]
	public void Evaluate_WithFalsifiablePositiveAndZeroNegative_PassesGate()
	{
		AntigravityModelInvocationGateResult result =
			AntigravityModelInvocationGate.Evaluate(
				new AntigravityModelInvocationEvidence(1, 1, 1, 1, 1, 0),
				new AntigravityModelInvocationEvidence(0, 0, 0, 0, 0, 0));

		Assert.True(result.IsSafe);
	}

	[Fact]
	public void Evaluate_WithoutPositiveControlSignal_FailsGate()
	{
		AntigravityModelInvocationGateResult result =
			AntigravityModelInvocationGate.Evaluate(
				new AntigravityModelInvocationEvidence(0, 1, 1, 1, 1, 0),
				new AntigravityModelInvocationEvidence(0, 0, 0, 0, 0, 0));

		Assert.False(result.IsSafe);
	}

	[Theory]
	[InlineData(1, 0, 0, 0, 0)]
	[InlineData(0, 1, 0, 0, 0)]
	[InlineData(0, 0, 1, 0, 0)]
	[InlineData(0, 0, 0, 1, 0)]
	[InlineData(0, 0, 0, 0, 1)]
	public void Evaluate_WithAnyUsageModelEvidence_FailsGate(
		int sentinelCount,
		int responseCount,
		int turnCount,
		long inputTokens,
		long outputTokens)
	{
		AntigravityModelInvocationGateResult result =
			AntigravityModelInvocationGate.Evaluate(
				new AntigravityModelInvocationEvidence(1, 1, 1, 1, 1, 0),
				new AntigravityModelInvocationEvidence(
					sentinelCount,
					responseCount,
					turnCount,
					inputTokens,
					outputTokens,
					0));

		Assert.False(result.IsSafe);
	}

	[Fact]
	public void Evaluate_WithUsageCost_FailsGate()
	{
		AntigravityModelInvocationGateResult result =
			AntigravityModelInvocationGate.Evaluate(
				new AntigravityModelInvocationEvidence(1, 1, 1, 1, 1, 0),
				new AntigravityModelInvocationEvidence(0, 0, 0, 0, 0, 0.01m));

		Assert.False(result.IsSafe);
	}

	private static void Bind(
		string beforeIdentity,
		AntigravityUsageResult usage,
		string afterIdentity,
		int beforeRootProcessId,
		int quotaRootProcessId,
		Guid beforeSessionId,
		Guid quotaSessionId,
		DateTimeOffset observedAt)
	{
		AntigravityIdentityBindingGate.Bind(
			new AntigravityIdentityObservation(
				beforeIdentity,
				beforeRootProcessId,
				beforeSessionId,
				observedAt.AddSeconds(-1)),
			new AntigravityQuotaObservation(
				usage,
				quotaRootProcessId,
				quotaSessionId,
				observedAt),
			new AntigravityIdentityObservation(
				afterIdentity,
				beforeRootProcessId,
				beforeSessionId,
				observedAt.AddSeconds(1)));
	}

	private static AntigravityUsageResult CreateUsage(string identity)
	{
		return new AntigravityUsageResult(
			"synthetic-r0-v1",
			identity,
			Array.AsReadOnly(new[]
			{
				new AntigravityQuotaRow(
					"gemini.synthetic.flash",
					10,
					90,
					null)
			}));
	}
}
