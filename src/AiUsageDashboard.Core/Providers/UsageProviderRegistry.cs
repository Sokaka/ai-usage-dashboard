using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Core.Providers;

public sealed class UsageProviderRegistry
{
	private readonly IReadOnlyDictionary<ProviderKind, IUsageProvider> _providers;

	public UsageProviderRegistry(IEnumerable<IUsageProvider> providers)
	{
		ArgumentNullException.ThrowIfNull(providers);
		Dictionary<ProviderKind, IUsageProvider> registeredProviders = new();

		foreach (IUsageProvider? provider in providers)
		{
			if (provider is null)
			{
				throw new ArgumentException(
					"Provider 清單不可包含 null。",
					nameof(providers));
			}

			if (!Enum.IsDefined(typeof(ProviderKind), provider.Provider))
			{
				throw new ArgumentException(
					"Provider 清單包含無效的 ProviderKind。",
					nameof(providers));
			}

			if (provider.MinimumRefreshInterval < TimeSpan.Zero)
			{
				throw new ArgumentException(
					"Provider 的最短刷新間隔不可小於零。",
					nameof(providers));
			}

			if (!registeredProviders.TryAdd(provider.Provider, provider))
			{
				throw new ArgumentException(
					$"Provider {provider.Provider} 重複註冊。",
					nameof(providers));
			}
		}

		_providers = registeredProviders;
	}

	public IUsageProvider GetRequired(ProviderKind provider)
	{
		if (!_providers.TryGetValue(provider, out IUsageProvider? usageProvider))
		{
			throw new InvalidOperationException($"Provider {provider} 尚未註冊。");
		}

		return usageProvider;
	}
}
