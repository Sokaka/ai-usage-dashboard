using System.IO;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeUsageSafetyStateCleanupPendingException :
	IOException
{
	internal ClaudeUsageSafetyStateCleanupPendingException(string message)
		: base(message)
	{
	}
}

internal enum ClaudeUsageSafetyRecoveryMode
{
	Healthy,
	AutomaticRevalidationPending,
	ManualRevalidationRequired,
	AttemptInProgress
}

internal enum ClaudeUsageSafetyAttemptPhase
{
	Uncontained,
	Prepared,
	StartedContained
}

internal sealed record ClaudeUsageSafetyState(
	int AutomaticRevalidationAttemptCount,
	string? AccountIdentity,
	string FailureReason,
	ClaudeUsageSafetyRecoveryMode RecoveryMode,
	DateTimeOffset? RetryNotBefore,
	Guid? AttemptId = null,
	ClaudeUsageSafetyAttemptPhase? AttemptPhase = null,
	int? LegacySourceSchemaVersion = null,
	string? DiagnosticCliVersion = null);

internal interface IClaudeUsageSafetyStateStore
{
	Task<ClaudeUsageSafetyState?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		Guid accountId,
		ClaudeUsageSafetyState state,
		CancellationToken cancellationToken = default);

	Task ClearAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
