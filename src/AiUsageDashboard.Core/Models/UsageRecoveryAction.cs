namespace AiUsageDashboard.Core.Models;

public enum UsageRecoveryAction
{
	None,
	Retry,
	ConnectAccount,
	SwitchAccount,
	RestartApplication,
	InstallOrUpdate,
	ReconfigureUsageSource,
	RevalidateUsage,
	UpdateApplication,
	ConfirmSubscription
}
