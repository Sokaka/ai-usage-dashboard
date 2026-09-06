namespace AiUsageDashboard.App.Providers;

internal enum CopilotFailureKind
{
	AuthenticationRequired,
	AccountMismatch,
	PermissionDenied,
	RateLimited,
	RuntimeUnavailable,
	InvalidResponse,
	Transient
}

internal enum CopilotAccountLoginFailureKind
{
	Cancelled,
	RuntimeUnavailable,
	AuthenticationFailed,
	ProcessFailed
}

internal interface ICopilotQuotaClient
{
	Task ReconcileCredentialAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		CancellationToken cancellationToken = default);

	Task<CopilotUsageReport> GetAccountQuotaAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		CancellationToken cancellationToken = default);
}

internal interface ICopilotAccountConnector
{
	Task<ICopilotConnectionCandidate> BeginConnectAsync(
		Guid accountId,
		string? expectedProviderAccountIdentity,
		bool allowAccountSwitch,
		CancellationToken cancellationToken = default);
}

internal interface ICopilotConnectionCandidate : IAsyncDisposable
{
	CopilotUsageReport UsageReport { get; }

	Task CommitAsync(CancellationToken cancellationToken = default);
}

internal sealed record CopilotAccountIdentity(
	string Host,
	string NodeId,
	long DatabaseId,
	string Login);

internal sealed record CopilotQuotaSnapshot(
	string Key,
	long EntitlementRequests,
	long UsedRequests,
	double RemainingPercentage,
	DateTimeOffset? ResetDate,
	bool IsUnlimitedEntitlement,
	bool OverageAllowedWithExhaustedQuota,
	bool UsageAllowedWithExhaustedQuota);

internal sealed record CopilotUsageReport(
	CopilotAccountIdentity Account,
	IReadOnlyList<CopilotQuotaSnapshot> Quotas,
	DateTimeOffset FetchedAt,
	string? PlanTier = null,
	bool? IsTokenBasedBilling = null);

internal sealed class CopilotClientException : Exception
{
	public CopilotFailureKind Kind { get; }

	public DateTimeOffset? RetryAt { get; }

	internal CopilotClientException(
		CopilotFailureKind kind,
		string message,
		DateTimeOffset? retryAt = null,
		Exception? innerException = null)
		: base(message, innerException)
	{
		Kind = kind;
		RetryAt = retryAt?.ToUniversalTime();
	}
}

internal sealed class CopilotAccountLoginException : Exception
{
	public CopilotAccountLoginFailureKind Kind { get; }

	public int? ExitCode { get; }

	internal CopilotAccountLoginException(
		CopilotAccountLoginFailureKind kind,
		string message,
		int? exitCode = null,
		Exception? innerException = null)
		: base(message, innerException)
	{
		Kind = kind;
		ExitCode = exitCode;
	}
}
