namespace AiUsageDashboard.Core.Providers;

public interface IUsageProviderInvalidator
{
	void Invalidate(Guid accountId);
}
