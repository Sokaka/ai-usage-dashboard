using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdateAvailabilityEvaluatorTests
{
	private const string FirstHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string SecondHash =
		"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

	[Fact]
	public void EvaluateManagedPayload_WithoutInstalledPayload_ReturnsUpdateAvailable()
	{
		Assert.Equal(
			UpdateAvailabilityStatus.UpdateAvailable,
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				installed: null,
				CreateManifest("1.0.0", FirstHash)));
	}

	[Fact]
	public void EvaluateManagedPayload_WithNewerVersion_ReturnsUpdateAvailable()
	{
		Assert.Equal(
			UpdateAvailabilityStatus.UpdateAvailable,
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				CreateManifest("1.0.0-preview.9", FirstHash),
				CreateManifest("1.0.0", SecondHash)));
	}

	[Fact]
	public void EvaluateManagedPayload_WithSameIdentity_ReturnsUpToDate()
	{
		Assert.Equal(
			UpdateAvailabilityStatus.UpToDate,
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				CreateManifest("1.2.3", FirstHash, releaseSequence: 10),
				CreateManifest("1.2.3", FirstHash, releaseSequence: 11)));
	}

	[Fact]
	public void EvaluateManagedPayload_WithOlderVersion_RejectsDowngrade()
	{
		Assert.Throws<InvalidDataException>(() =>
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				CreateManifest("2.0.0", FirstHash),
				CreateManifest("1.9.9", SecondHash)));
	}

	[Fact]
	public void EvaluateManagedPayload_WithReusedVersionAndDifferentBytes_Rejects()
	{
		Assert.Throws<InvalidDataException>(() =>
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				CreateManifest("1.2.3", FirstHash),
				CreateManifest("1.2.3", SecondHash)));
	}

	[Fact]
	public void EvaluateManagedPayload_WithOlderReleaseSequence_RejectsReplay()
	{
		Assert.Throws<InvalidDataException>(() =>
			UpdateAvailabilityEvaluator.EvaluateManagedPayload(
				CreateManifest("1.2.3", FirstHash, releaseSequence: 20),
				CreateManifest("1.2.4", SecondHash, releaseSequence: 19)));
	}

	[Theory]
	[InlineData("1.0.4+0123456789abcdef", "1.0.5", (int)UpdateAvailabilityStatus.UpdateAvailable)]
	[InlineData("1.0.4+0123456789abcdef", "1.0.4", (int)UpdateAvailabilityStatus.UpToDate)]
	[InlineData("1.0.5+0123456789abcdef", "1.0.4", (int)UpdateAvailabilityStatus.UpToDate)]
	public void EvaluateUnmanagedProductVersion_WithKnownVersion_ComparesSemVerWithoutBuildMetadata(
		string currentProductVersion,
		string availableVersion,
		int expectedValue)
	{
		UpdateAvailabilityStatus expected =
			(UpdateAvailabilityStatus)expectedValue;
		Assert.Equal(
			expected,
			UpdateAvailabilityEvaluator.EvaluateUnmanagedProductVersion(
				currentProductVersion,
				CreateManifest(availableVersion, FirstHash)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("1.0")]
	[InlineData("1.0.4+")]
	[InlineData("1.0.4+sha+other")]
	[InlineData("1.0.4+sha..other")]
	public void EvaluateUnmanagedProductVersion_WithUnknownVersion_ReturnsUnknown(
		string? currentProductVersion)
	{
		Assert.Equal(
			UpdateAvailabilityStatus.UnknownCurrentVersion,
			UpdateAvailabilityEvaluator.EvaluateUnmanagedProductVersion(
				currentProductVersion,
				CreateManifest("1.0.5", FirstHash)));
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
