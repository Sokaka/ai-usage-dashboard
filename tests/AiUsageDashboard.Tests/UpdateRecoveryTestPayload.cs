using System.Text.Json;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

internal static class UpdateRecoveryTestPayload
{
	internal static void Write(string directoryPath, string contents)
	{
		Directory.CreateDirectory(Path.Combine(directoryPath, "app"));
		File.WriteAllText(Path.Combine(directoryPath, "app", "AiUsageDashboard.App.exe"), contents);
		File.WriteAllText(Path.Combine(directoryPath, "version.txt"), contents);
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.0.0",
			UpdateManifest.ExpectedRuntimeIdentifier,
			1,
			new string('a', 64));
		File.WriteAllText(Path.Combine(directoryPath, UpdateManifest.InstalledFileName), JsonSerializer.Serialize(manifest));
	}
}
