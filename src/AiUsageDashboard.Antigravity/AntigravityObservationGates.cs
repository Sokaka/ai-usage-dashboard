using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityIdentityObservation(
	string Identity,
	int RootProcessId,
	Guid SessionId,
	DateTimeOffset ObservedAt);

internal sealed record AntigravityQuotaObservation(
	AntigravityUsageResult Usage,
	int RootProcessId,
	Guid SessionId,
	DateTimeOffset ObservedAt);

internal sealed record AntigravityBoundObservation(
	string AccountIdentity,
	AntigravityUsageResult Usage,
	int RootProcessId,
	Guid SessionId,
	DateTimeOffset ObservedAt);

internal static class AntigravityIdentityBindingGate
{
	private const int MaximumIdentityLength = 320;

	internal static AntigravityBoundObservation Bind(
		AntigravityIdentityObservation identityBefore,
		AntigravityQuotaObservation quota,
		AntigravityIdentityObservation identityAfter)
	{
		ArgumentNullException.ThrowIfNull(identityBefore);
		ArgumentNullException.ThrowIfNull(quota);
		ArgumentNullException.ThrowIfNull(identityAfter);
		ValidateUsage(quota.Usage);
		ValidateProcessObservation(
			identityBefore.RootProcessId,
			identityBefore.SessionId,
			nameof(identityBefore));
		ValidateProcessObservation(
			quota.RootProcessId,
			quota.SessionId,
			nameof(quota));
		ValidateProcessObservation(
			identityAfter.RootProcessId,
			identityAfter.SessionId,
			nameof(identityAfter));

		if ((identityBefore.RootProcessId != quota.RootProcessId) ||
			(identityBefore.RootProcessId != identityAfter.RootProcessId) ||
			(identityBefore.SessionId != quota.SessionId) ||
			(identityBefore.SessionId != identityAfter.SessionId))
		{
			throw new InvalidDataException("AGY identity and quota did not originate from one root session.");
		}

		if ((quota.ObservedAt < identityBefore.ObservedAt) ||
			(quota.ObservedAt > identityAfter.ObservedAt) ||
			(identityAfter.ObservedAt < identityBefore.ObservedAt))
		{
			throw new InvalidDataException("AGY identity observations do not bracket the quota observation.");
		}

		string normalizedBefore = NormalizeIdentity(identityBefore.Identity);
		string normalizedAfter = NormalizeIdentity(identityAfter.Identity);
		string normalizedQuotaIdentity = NormalizeIdentity(
			quota.Usage.AccountIdentity);

		if (!string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal) ||
			!string.Equals(
				normalizedBefore,
				normalizedQuotaIdentity,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException("AGY account identity changed or does not match the quota payload.");
		}

		return new AntigravityBoundObservation(
			normalizedBefore,
			quota.Usage,
			quota.RootProcessId,
			quota.SessionId,
			quota.ObservedAt);
	}

	internal static string NormalizeIdentity(string identity)
	{
		ArgumentNullException.ThrowIfNull(identity);
		string normalized = identity.Trim().Normalize(NormalizationForm.FormKC);

		if ((normalized.Length == 0) ||
			(normalized.Length > MaximumIdentityLength) ||
			normalized.Any(char.IsControl))
		{
			throw new InvalidDataException("AGY account identity is invalid.");
		}

		return normalized.ToLowerInvariant();
	}

	private static void ValidateProcessObservation(
		int rootProcessId,
		Guid sessionId,
		string parameterName)
	{
		if ((rootProcessId <= 0) || (sessionId == Guid.Empty))
		{
			throw new ArgumentException(
				"AGY process observation is incomplete.",
				parameterName);
		}
	}

	private static void ValidateUsage(AntigravityUsageResult usage)
	{
		ArgumentNullException.ThrowIfNull(usage);

		if (string.IsNullOrWhiteSpace(usage.LayoutId) ||
			string.IsNullOrWhiteSpace(usage.AccountIdentity) ||
			(usage.Rows is null) ||
			(usage.Rows.Count == 0))
		{
			throw new InvalidDataException("AGY quota payload is incomplete.");
		}
	}
}

internal sealed record AntigravityModelInvocationEvidence(
	int SentinelInvocationCount,
	int ModelResponseCount,
	int ConversationTurnCount,
	long InputTokenCount,
	long OutputTokenCount,
	decimal Cost);

internal sealed record AntigravityModelInvocationGateResult(
	bool IsSafe,
	string Reason);

internal static class AntigravityModelInvocationGate
{
	internal static AntigravityModelInvocationGateResult Evaluate(
		AntigravityModelInvocationEvidence positiveControl,
		AntigravityModelInvocationEvidence usageCommand)
	{
		ArgumentNullException.ThrowIfNull(positiveControl);
		ArgumentNullException.ThrowIfNull(usageCommand);
		ValidateNonNegative(positiveControl, nameof(positiveControl));
		ValidateNonNegative(usageCommand, nameof(usageCommand));

		if (positiveControl.SentinelInvocationCount <= 0)
		{
			return new AntigravityModelInvocationGateResult(
				false,
				"The positive-control prompt did not trigger the PreInvocation detector.");
		}

		if ((usageCommand.SentinelInvocationCount != 0) ||
			(usageCommand.ModelResponseCount != 0) ||
			(usageCommand.ConversationTurnCount != 0) ||
			(usageCommand.InputTokenCount != 0) ||
			(usageCommand.OutputTokenCount != 0) ||
			(usageCommand.Cost != 0))
		{
			return new AntigravityModelInvocationGateResult(
				false,
				"The /usage negative case contains model-invocation evidence.");
		}

		return new AntigravityModelInvocationGateResult(
			true,
			"The detector is falsifiable and the /usage observation contains no model-call evidence.");
	}

	private static void ValidateNonNegative(
		AntigravityModelInvocationEvidence evidence,
		string parameterName)
	{
		if ((evidence.SentinelInvocationCount < 0) ||
			(evidence.ModelResponseCount < 0) ||
			(evidence.ConversationTurnCount < 0) ||
			(evidence.InputTokenCount < 0) ||
			(evidence.OutputTokenCount < 0) ||
			(evidence.Cost < 0))
		{
			throw new ArgumentOutOfRangeException(
				parameterName,
				"AGY model-invocation evidence cannot contain negative values.");
		}
	}
}
