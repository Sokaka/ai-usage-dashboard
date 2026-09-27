using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Providers;

public interface IUsageProviderRefreshAdmission
{
	bool RequiresSerializedRefresh(AccountProfile account);
}
