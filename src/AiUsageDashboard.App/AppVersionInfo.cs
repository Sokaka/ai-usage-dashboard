using System.Reflection;

using AiUsageDashboard.Core.Localization;

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

		string version = assembly.GetName().Version?.ToString() ?? UiText.Get("Shell.VersionUnknown");
		return UiText.Format("Shell.VersionIncomplete", version);
	}
}
