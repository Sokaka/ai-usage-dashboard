using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal enum AppInstallationKind
{
	CanonicalManaged,
	CustomManaged,
	Unmanaged
}

internal sealed record AppInstallationContext(
	AppInstallationKind Kind,
	string? ExecutablePath,
	string? InstallRoot,
	string? ManifestPath,
	UpdateManifest? InstalledManifest)
{
	internal string? PayloadGenerationId =>
		InstalledManifest?.PayloadGenerationId;

	internal static AppInstallationContext CreateUnmanaged(
		string? executablePath = null)
	{
		return new AppInstallationContext(
			AppInstallationKind.Unmanaged,
			executablePath,
			InstallRoot: null,
			ManifestPath: null,
			InstalledManifest: null);
	}
}
