using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal enum OnlinePayloadUpdateAction
{
	InstallAvailable,
	UseInstalled
}

internal static class OnlinePayloadUpdatePolicy
{
	internal static OnlinePayloadUpdateAction Evaluate(
		UpdateManifest? installed,
		UpdateManifest available)
	{
		return UpdateAvailabilityEvaluator.EvaluateManagedPayload(
			installed,
			available) switch
		{
			UpdateAvailabilityStatus.UpdateAvailable =>
				OnlinePayloadUpdateAction.InstallAvailable,
			UpdateAvailabilityStatus.UpToDate =>
				OnlinePayloadUpdateAction.UseInstalled,
			_ => throw new InvalidOperationException(
				"Managed update availability returned an unsupported result.")
		};
	}
}
