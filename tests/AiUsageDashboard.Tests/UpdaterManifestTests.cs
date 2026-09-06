using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterManifestTests
{
	private const string ValidSha256 =
		"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

	[Fact]
	public void Parse_WithValidManifest_ReturnsCanonicalGenerationIdentity()
	{
		const string json = """
			{
			  "schemaVersion": 1,
			  "packageId": "AiUsageDashboard",
			  "version": "1.2.3-preview.4",
			  "runtimeIdentifier": "win-x64",
			  "archiveSizeBytes": 12345,
			  "archiveSha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
			  "sourceRevision": "7153ed1d3175"
			}
			""";

		UpdateManifest manifest = UpdateManifest.Parse(json);

		Assert.Equal("1.2.3-preview.4", manifest.Version);
		Assert.Equal(ValidSha256.ToLowerInvariant(), manifest.PayloadGenerationId);
	}

	[Fact]
	public void Parse_WithUnknownProperty_FailsClosed()
	{
		string json = CreateManifestJson();
		json = json.Replace(
			"\"sourceRevision\": null",
			"\"sourceRevision\": null, \"unexpected\": true",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(json));
	}

	[Fact]
	public void Parse_WithLegacyComputedGenerationProperty_RemainsCompatible()
	{
		string json = CreateManifestJson().Replace(
			"\"sourceRevision\": null",
			$"\"sourceRevision\": null, \"PayloadGenerationId\": \"{ValidSha256.ToLowerInvariant()}\"",
			StringComparison.Ordinal);

		UpdateManifest manifest = UpdateManifest.Parse(json);

		Assert.Equal(
			ValidSha256.ToLowerInvariant(),
			manifest.PayloadGenerationId);
	}

	[Fact]
	public void Validate_WithNonPositiveReleaseSequence_FailsClosed()
	{
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			ValidSha256,
			SourceRevision: null,
			ReleaseSequence: 0);

		Assert.Throws<InvalidDataException>(manifest.Validate);
	}

	[Theory]
	[InlineData(0, "AiUsageDashboard", "1.2.3", "win-x64", 1)]
	[InlineData(1, "OtherProduct", "1.2.3", "win-x64", 1)]
	[InlineData(1, "AiUsageDashboard", "latest", "win-x64", 1)]
	[InlineData(1, "AiUsageDashboard", "1.2.3", "win-arm64", 1)]
	[InlineData(1, "AiUsageDashboard", "1.2.3", "win-x64", 0)]
	public void Validate_WithInvalidIdentityOrSize_FailsClosed(
		int schemaVersion,
		string packageId,
		string version,
		string runtimeIdentifier,
		long archiveSizeBytes)
	{
		UpdateManifest manifest = new(
			schemaVersion,
			packageId,
			version,
			runtimeIdentifier,
			archiveSizeBytes,
			ValidSha256);

		Assert.Throws<InvalidDataException>(manifest.Validate);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Validate_WithMissingArchiveSha256_ThrowsInvalidDataException(
		string? archiveSha256)
	{
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			archiveSha256!);

		Assert.Throws<InvalidDataException>(manifest.Validate);
	}

	[Fact]
	public void Parse_WithNullArchiveSha256_ThrowsInvalidDataException()
	{
		string json = CreateManifestJson().Replace(
			$"\"archiveSha256\": \"{ValidSha256}\"",
			"\"archiveSha256\": null",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(json));
	}

	[Fact]
	public async Task WriteAndReadAsync_RoundTripsCanonicalManifest()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string manifestPath = Path.Combine(
			temporaryDirectory.Path,
			UpdateManifest.InstalledFileName);
		UpdateManifest expected = CreateManifest();

		await expected.WriteAsync(manifestPath);
		UpdateManifest actual = await UpdateManifest.ReadAsync(manifestPath);

		Assert.Equal(expected, actual);
	}

	private static UpdateManifest CreateManifest()
	{
		return new UpdateManifest(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			ValidSha256);
	}

	private static string CreateManifestJson()
	{
		return """
			{
			  "schemaVersion": 1,
			  "packageId": "AiUsageDashboard",
			  "version": "1.2.3",
			  "runtimeIdentifier": "win-x64",
			  "archiveSizeBytes": 12345,
			  "archiveSha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
			  "sourceRevision": null
			}
			""";
	}
}
