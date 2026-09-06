using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterOnlinePayloadUpdatePolicyTests
{
	private const string FirstHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string SecondHash =
		"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

	[Fact]
	public void Evaluate_WithoutInstalledPayload_InstallsAvailable()
	{
		Assert.Equal(
			OnlinePayloadUpdateAction.InstallAvailable,
			OnlinePayloadUpdatePolicy.Evaluate(
				installed: null,
				CreateManifest("1.0.0", FirstHash)));
	}

	[Fact]
	public void Evaluate_WithNewerAvailableVersion_InstallsAvailable()
	{
		Assert.Equal(
			OnlinePayloadUpdateAction.InstallAvailable,
			OnlinePayloadUpdatePolicy.Evaluate(
				CreateManifest("1.0.0-preview.9", FirstHash),
				CreateManifest("1.0.0", SecondHash)));
	}

	[Fact]
	public void Evaluate_WithSameIdentity_UsesInstalled()
	{
		Assert.Equal(
			OnlinePayloadUpdateAction.UseInstalled,
			OnlinePayloadUpdatePolicy.Evaluate(
				CreateManifest("1.2.3", FirstHash),
				CreateManifest("1.2.3", FirstHash)));
	}

	[Fact]
	public void Evaluate_WithSameVersionAndDifferentHash_Rejects()
	{
		Assert.Throws<InvalidDataException>(() =>
			OnlinePayloadUpdatePolicy.Evaluate(
				CreateManifest("1.2.3", FirstHash),
				CreateManifest("1.2.3", SecondHash)));
	}

	[Fact]
	public void Evaluate_WithOlderAvailableVersion_RejectsDowngrade()
	{
		Assert.Throws<InvalidDataException>(() =>
			OnlinePayloadUpdatePolicy.Evaluate(
				CreateManifest("2.0.0", FirstHash),
				CreateManifest("1.9.9", SecondHash)));
	}

	[Fact]
	public void Evaluate_WithOlderReleaseSequence_RejectsReplay()
	{
		Assert.Throws<InvalidDataException>(() =>
			OnlinePayloadUpdatePolicy.Evaluate(
				CreateManifest("1.2.3", FirstHash, releaseSequence: 20),
				CreateManifest("1.2.4", SecondHash, releaseSequence: 19)));
	}

	private static UpdateManifest CreateManifest(
		string version,
		string hash,
		long? releaseSequence = null)
	{
		return new UpdateManifest(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			version,
			UpdateManifest.ExpectedRuntimeIdentifier,
			ArchiveSizeBytes: 100,
			hash,
			SourceRevision: null,
			ReleaseSequence: releaseSequence);
	}
}
