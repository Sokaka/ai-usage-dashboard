using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Antigravity.Setup;

internal static class MachineSetupServiceFactory
{
	internal static IAntigravityMachineSetupService Create()
	{
		return new AntigravityMachineSetupService();
	}
}
