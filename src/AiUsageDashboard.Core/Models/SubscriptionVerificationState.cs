namespace AiUsageDashboard.Core.Models;

public enum SubscriptionVerificationState
{
	Unverified,
	Verified,
	TransientProbeError,
	DefiniteScopeMismatch,
	UsageUnavailable
}
