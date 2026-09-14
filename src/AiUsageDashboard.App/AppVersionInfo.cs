using System.Reflection;

namespace AiUsageDashboard.App;

internal static class AppVersionInfo
{
	internal static string GetDisplayVersion(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);
		string? informationalVersion = assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;

		if (!string.IsNullOrWhiteSpace(informationalVersion))
		{
			return informationalVersion.Trim();
		}

		string version = assembly.GetName().Version?.ToString() ?? "無法辨識";
		return $"{version}（版本資訊不完整）";
	}
}
