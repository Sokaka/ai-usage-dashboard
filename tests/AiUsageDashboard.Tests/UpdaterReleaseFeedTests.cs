using System.Text;
using System.Text.Json.Nodes;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterReleaseFeedTests
{
	private const string Hash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public void Parse_WithValidUtf8Feed_CreatesCanonicalPackageManifest()
	{
		byte[] json = Encoding.UTF8.GetBytes(CreateValidJson());

		UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(json);
		UpdateManifest manifest = feed.CreatePackageManifest();

		Assert.Equal("internal", feed.Channel);
		Assert.Equal(42, feed.ReleaseSequence);
		Assert.Equal(
			"https://example.test/releases/AiUsageDashboard-1.2.3-preview.4-win-x64.zip",
			feed.Package.DownloadUri.AbsoluteUri);
		Assert.Equal("AiUsageDashboard", manifest.PackageId);
		Assert.Equal("1.2.3-preview.4", manifest.Version);
		Assert.Equal(123456, manifest.ArchiveSizeBytes);
		Assert.Equal(Hash, manifest.ArchiveSha256);
		Assert.Equal("7153ed1d3175", manifest.SourceRevision);
	}

	[Fact]
	public void ValidateExpectedChannel_WithMismatch_FailsClosed()
	{
		UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(CreateValidJson());

		feed.ValidateExpectedChannel("internal");
		Assert.Throws<InvalidDataException>(() =>
			feed.ValidateExpectedChannel("stable"));
	}

	[Fact]
	public void PublicValidator_UsesStrictFeedAndChannelContract()
	{
		UpdateFeedValidator.Validate(CreateValidJson(), "internal");

		Assert.Throws<InvalidDataException>(() =>
			UpdateFeedValidator.Validate(CreateValidJson(), "stable"));
	}

	[Theory]
	[InlineData("\"sourceRevision\": \"7153ed1d3175\"", "\"sourceRevision\": \"7153ed1d3175\", \"unexpected\": true")]
	[InlineData("\"channel\": \"internal\"", "\"channel\": \"internal\", \"unexpected\": true")]
	public void Parse_WithUnknownProperty_FailsClosed(
		string oldValue,
		string newValue)
	{
		string json = CreateValidJson().Replace(
			oldValue,
			newValue,
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Theory]
	[InlineData("\"artifactId\": \"AiUsageDashboard\"", "\"artifactId\": \"AiUsageDashboard\", \"artifactId\": \"AiUsageDashboard\"")]
	[InlineData("\"channel\": \"internal\"", "\"channel\": \"internal\", \"channel\": \"internal\"")]
	public void Parse_WithDuplicatePropertyAtAnyDepth_FailsClosed(
		string oldValue,
		string newValue)
	{
		string json = CreateValidJson().Replace(
			oldValue,
			newValue,
			StringComparison.Ordinal);

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			UpdateReleaseFeed.Parse(json));

		Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Parse_WithFeedLargerThanLimit_FailsBeforeJsonParsing()
	{
		byte[] json = new byte[UpdateReleaseFeed.MaximumFeedSizeBytes + 1];

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			UpdateReleaseFeed.Parse(json));

		Assert.Contains("size", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("https://example.test/releases/AiUsageDashboard-1.2.3-preview.4-win-x64.zip", "http://example.test/release.zip")]
	[InlineData("https://example.test/releases/AiUsageDashboard-1.2.3-preview.4-win-x64.zip", "https://example.test/release.zip#download")]
	[InlineData("AiUsageDashboard-1.2.3-preview.4-win-x64.zip", "release.zip")]
	[InlineData("\"sizeBytes\": 123456", "\"sizeBytes\": 536870913")]
	[InlineData(Hash, "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
	[InlineData("\"runtimeIdentifier\": \"win-x64\"", "\"runtimeIdentifier\": \"win-arm64\"")]
	public void Parse_WithInvalidPackageArtifact_FailsClosed(
		string oldValue,
		string newValue)
	{
		string json = CreateValidJson().Replace(
			oldValue,
			newValue,
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Fact]
	public void Parse_WithUserInfoInPackageUrl_FailsClosed()
	{
		string userInfoUrl = string.Concat(
			"https://",
			"synthetic-user",
			":",
			"synthetic-credential",
			"@example.test/release.zip");
		string json = CreateValidJson().Replace(
			"https://example.test/releases/AiUsageDashboard-1.2.3-preview.4-win-x64.zip",
			userInfoUrl,
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Theory]
	[InlineData("Internal")]
	[InlineData("1internal")]
	[InlineData("internal_test")]
	[InlineData("internal/stable")]
	[InlineData("internal-channel-name-is-too-long-1")]
	public void Parse_WithNonCanonicalChannel_FailsClosed(string channel)
	{
		string json = CreateValidJson().Replace(
			"\"channel\": \"internal\"",
			$"\"channel\": \"{channel}\"",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Fact]
	public void Parse_WithOversizedUpdater_FailsClosed()
	{
		string json = CreateValidJson().Replace(
			"\"sizeBytes\": 34567890",
			"\"sizeBytes\": 134217729",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Fact]
	public void Parse_WhenMinimumVersionExceedsPublishedUpdater_FailsClosed()
	{
		string json = CreateValidJson().Replace(
			"\"minimumUpdaterVersion\": \"1.0.0\"",
			"\"minimumUpdaterVersion\": \"1.1.1\"",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => UpdateReleaseFeed.Parse(json));
	}

	[Fact]
	public void Parse_WithCandidateVersionAsMinimumUpdaterVersion_Succeeds()
	{
		const string candidateVersion =
			"0.1.0-rc.1.run.42.attempt.1.g0123456789ab";
		string json = CreateValidJson()
			.Replace("1.2.3-preview.4", candidateVersion, StringComparison.Ordinal)
			.Replace("1.1.0", candidateVersion, StringComparison.Ordinal)
			.Replace(
				"\"minimumUpdaterVersion\": \"1.0.0\"",
				$"\"minimumUpdaterVersion\": \"{candidateVersion}\"",
				StringComparison.Ordinal);

		UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(json);

		Assert.Equal(candidateVersion, feed.MinimumUpdaterVersion);
		Assert.Equal(candidateVersion, feed.Updater.Version);
	}

	[Fact]
	public void Parse_WithMissingRequiredArtifactField_FailsClosed()
	{
		JsonObject root = JsonNode.Parse(CreateValidJson())?.AsObject() ??
			throw new InvalidOperationException("Test feed is not valid JSON.");
		JsonObject package = root["package"]?.AsObject() ??
			throw new InvalidOperationException("Test feed has no package.");
		Assert.True(package.Remove("sourceRevision"));

		Assert.Throws<InvalidDataException>(() =>
			UpdateReleaseFeed.Parse(root.ToJsonString()));
	}

	private static string CreateValidJson()
	{
		return $$"""
			{
			  "schemaVersion": 1,
			  "channel": "internal",
			  "releaseSequence": 42,
			  "minimumUpdaterVersion": "1.0.0",
			  "package": {
			    "artifactId": "AiUsageDashboard",
			    "version": "1.2.3-preview.4",
			    "runtimeIdentifier": "win-x64",
			    "fileName": "AiUsageDashboard-1.2.3-preview.4-win-x64.zip",
			    "downloadUrl": "https://example.test/releases/AiUsageDashboard-1.2.3-preview.4-win-x64.zip",
			    "sizeBytes": 123456,
			    "sha256": "{{Hash}}",
			    "sourceRevision": "7153ed1d3175"
			  },
			  "updater": {
			    "artifactId": "AiUsageDashboard.Updater",
			    "version": "1.1.0",
			    "runtimeIdentifier": "win-x64",
			    "fileName": "AiUsageDashboard-Updater-1.1.0-win-x64.exe",
			    "downloadUrl": "https://example.test/releases/AiUsageDashboard-Updater-1.1.0-win-x64.exe",
			    "sizeBytes": 34567890,
			    "sha256": "{{Hash}}",
			    "sourceRevision": "7153ed1d3175"
			  }
			}
			""";
	}
}
