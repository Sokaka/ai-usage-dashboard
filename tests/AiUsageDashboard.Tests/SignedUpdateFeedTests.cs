using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class SignedUpdateFeedTests : IClassFixture<FeedSigningTestKeys>
{
	private readonly FeedSigningTestKeys _keys;

	public SignedUpdateFeedTests(FeedSigningTestKeys keys)
	{
		_keys = keys;
	}

	[Fact]
	public void Verify_WithPinnedSigner_ReturnsValidatedCanonicalPayload()
	{
		string json = CreatePayload();
		string signed = _keys.Sign(json);
		string verified = SignedUpdateFeed.Verify(signed, _keys.Trust("test-first"), "stable");
		Assert.Equal(JsonSerializer.Serialize(UpdateReleaseFeed.Parse(json)), verified);
	}

	[Theory]
	[InlineData("schemaVersion")]
	[InlineData("channel")]
	[InlineData("releaseSequence")]
	[InlineData("minimumUpdaterVersion")]
	[InlineData("package.artifactId")]
	[InlineData("package.version")]
	[InlineData("package.runtimeIdentifier")]
	[InlineData("package.fileName")]
	[InlineData("package.downloadUrl")]
	[InlineData("package.sizeBytes")]
	[InlineData("package.sha256")]
	[InlineData("package.sourceRevision")]
	[InlineData("updater.artifactId")]
	[InlineData("updater.version")]
	[InlineData("updater.runtimeIdentifier")]
	[InlineData("updater.fileName")]
	[InlineData("updater.downloadUrl")]
	[InlineData("updater.sizeBytes")]
	[InlineData("updater.sha256")]
	[InlineData("updater.sourceRevision")]
	public void Verify_WhenAnySecurityFieldIsTampered_RejectsBeforePayloadValidation(string path)
	{
		JsonObject envelope = ParseObject(_keys.Sign(CreatePayload()));
		JsonObject payload = ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(envelope["payload"]!.GetValue<string>())));
		string[] parts = path.Split('.');
		JsonObject owner = parts.Length == 1 ? payload : payload[parts[0]]!.AsObject();
		owner[parts[^1]] = "tampered-invalid-value";
		envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));

		InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
			SignedUpdateFeed.Verify(envelope.ToJsonString(), _keys.Trust("test-first"), "stable"));
		Assert.Contains("signature verification failed", error.Message);
	}

	[Theory]
	[InlineData("signature")]
	[InlineData("payload")]
	[InlineData("formatVersion")]
	[InlineData("algorithm")]
	[InlineData("signerKeyId")]
	public void Verify_WithMissingEnvelopeField_FailsClosed(string field)
	{
		JsonObject envelope = ParseObject(_keys.Sign(CreatePayload()));
		Assert.True(envelope.Remove(field));
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(envelope.ToJsonString(), _keys.Trust("test-first"), "stable"));
	}

	[Theory]
	[InlineData("algorithm", "RSA-SHA256")]
	[InlineData("signerKeyId", "unknown")]
	[InlineData("signature", "AAAA")]
	[InlineData("signature", "invalid-base64")]
	[InlineData("publicKeyPem", "untrusted-feed-supplied-key")]
	public void Verify_WithWrongSignerOrSignatureOrFeedSuppliedTrust_FailsClosed(string field, string value)
	{
		JsonObject envelope = ParseObject(_keys.Sign(CreatePayload()));
		envelope[field] = value;
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(envelope.ToJsonString(), _keys.Trust("test-first"), "stable"));
	}

	[Fact]
	public void Verify_WhenSignerIdChangesToTrustedAliasForSameKey_RejectsSignedIdentityChange()
	{
		JsonObject envelope = ParseObject(_keys.Sign(CreatePayload()));
		envelope["signerKeyId"] = "test-alias";
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(envelope.ToJsonString(), _keys.Trust("test-first", "test-alias"), "stable"));
	}

	[Fact]
	public void Verify_WithUnsignedFeedOrWrongChannelOrChangedFormat_FailsClosed()
	{
		UpdateFeedTrustStore trust = _keys.Trust("test-first");
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(CreatePayload(), trust, "stable"));
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(_keys.Sign(CreatePayload()), trust, "internal"));
		JsonObject envelope = ParseObject(_keys.Sign(CreatePayload()));
		envelope["formatVersion"] = 2;
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(envelope.ToJsonString(), trust, "stable"));
	}

	[Fact]
	public void Verify_WithDuplicateEnvelopePropertyOrNoncanonicalBase64_FailsClosed()
	{
		string signed = _keys.Sign(CreatePayload());
		string duplicate = signed.Insert(1, "\"formatVersion\":1,");
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(duplicate, _keys.Trust("test-first"), "stable"));
		JsonObject envelope = ParseObject(signed);
		envelope["payload"] = " " + envelope["payload"]!.GetValue<string>();
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(envelope.ToJsonString(), _keys.Trust("test-first"), "stable"));
	}

	[Fact]
	public void Verify_WithValidSignatureOverNoncanonicalPayload_RejectsEncoding()
	{
		string formatted = ParseObject(CreatePayload()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		string signed = _keys.SignRawPayload(formatted);
		InvalidDataException error = Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(signed, _keys.Trust("test-first"), "stable"));
		Assert.Contains("canonical", error.Message);
	}

	[Fact]
	public void Verify_WithKeyRotation_RequiresPreviouslyProvisionedBridgeTrust()
	{
		string oldSigned = _keys.Sign(CreatePayload());
		string newSigned = _keys.Sign(CreatePayload(), "test-second");
		UpdateFeedTrustStore original = _keys.Trust("test-first");
		UpdateFeedTrustStore bridge = _keys.Trust("test-first", "test-second");
		UpdateFeedTrustStore replacement = _keys.Trust("test-second");
		_ = SignedUpdateFeed.Verify(oldSigned, original, "stable");
		_ = SignedUpdateFeed.Verify(oldSigned, bridge, "stable");
		_ = SignedUpdateFeed.Verify(newSigned, bridge, "stable");
		_ = SignedUpdateFeed.Verify(newSigned, replacement, "stable");
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(newSigned, original, "stable"));
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(oldSigned, replacement, "stable"));
	}

	[Fact]
	public void SignedFeed_WithOlderMinimumUpdaterVersion_RejectsStaleInstallerCatalog()
	{
		JsonObject payload = ParseObject(CreatePayload());
		payload["minimumUpdaterVersion"] = "1.0.0";
		string canonical = JsonSerializer.Serialize(UpdateReleaseFeed.Parse(payload.ToJsonString()));
		Assert.Throws<InvalidDataException>(() => _keys.Sign(canonical));
		string signed = _keys.SignRawPayload(canonical);
		Assert.Throws<InvalidDataException>(() => SignedUpdateFeed.Verify(signed, _keys.Trust("test-first"), "stable"));
	}

	[Fact]
	public void SignedFeed_AppOnlyReleaseRequiresExactUpdaterAndRejectsReplay()
	{
		UpdateReleaseArtifact package = new(
			"AiUsageDashboard", "1.0.9", "win-x64",
			"AiUsageDashboard-1.0.9-win-x64.zip",
			"https://example.test/releases/AiUsageDashboard-1.0.9-win-x64.zip",
			300, new string('a', 64), "new-source");
		UpdateReleaseArtifact updater = new(
			"AiUsageDashboard.Updater", "1.0.8", "win-x64",
			"AiUsageDashboard-Updater-1.0.8-win-x64.exe",
			"https://example.test/releases/AiUsageDashboard-Updater-1.0.8-win-x64.exe",
			200, new string('b', 64), "old-source");
		UpdateReleaseFeed proposal = new(
			1, "stable", 1023, "1.0.8", package, updater);
		string verifiedPayload = SignedUpdateFeed.Verify(
			_keys.Sign(JsonSerializer.Serialize(proposal)),
			_keys.Trust("test-first"),
			"stable");
		UpdateReleaseFeed verifiedFeed = UpdateReleaseFeed.Parse(verifiedPayload);
		UpdateManifest available = verifiedFeed.CreatePackageManifest();
		UpdateManifest installed = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.0.8",
			UpdateManifest.ExpectedRuntimeIdentifier,
			100,
			new string('c', 64),
			"old-source",
			1022);
		CurrentUpdaterIdentity runningUpdater = new(
			@"C:\Tools\AiUsageDashboard.Updater.exe",
			"1.0.8",
			updater.SizeBytes,
			updater.Sha256);

		Assert.Equal("1.0.9", verifiedFeed.Package.Version);
		Assert.Equal("1.0.8", verifiedFeed.Updater.Version);
		Assert.Equal("old-source", verifiedFeed.Updater.SourceRevision);
		Assert.Equal(1023, verifiedFeed.ReleaseSequence);
		Assert.Equal(
			OnlinePayloadUpdateAction.InstallAvailable,
			OnlinePayloadUpdatePolicy.Evaluate(installed, available));
		Assert.Equal(
			UpdaterRefreshAction.RunCurrent,
			UpdaterRefreshPolicy.Evaluate(runningUpdater, verifiedFeed.Updater));
		UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(
			runningUpdater, verifiedFeed.Updater);
		Assert.Throws<InvalidDataException>(() =>
			UpdaterRefreshPolicy.Evaluate(
				runningUpdater with { SizeBytes = 201 }, verifiedFeed.Updater));
		Assert.Throws<InvalidDataException>(() =>
			UpdaterRefreshPolicy.Evaluate(
				runningUpdater with { Sha256 = new string('d', 64) },
				verifiedFeed.Updater));
		Assert.Throws<InvalidDataException>(() =>
			UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(
				runningUpdater with { Version = "1.0.9" }, verifiedFeed.Updater));
		Assert.Throws<InvalidDataException>(() =>
			OnlinePayloadUpdatePolicy.Evaluate(
				installed with { ReleaseSequence = 1024 }, available));
		Assert.Throws<InvalidDataException>(() =>
			OnlinePayloadUpdatePolicy.Evaluate(
				installed with { Version = "1.0.10" }, available));
	}

	[Fact]
	public void TrustStore_WithEmptyOrDuplicateKeysOrPrivatePem_FailsClosed()
	{
		Assert.Throws<InvalidDataException>(() => UpdateFeedTrustStore.Parse("{\"schemaVersion\":1,\"keys\":[]}"));
		Assert.Throws<InvalidDataException>(() => _keys.Trust("test-first", "test-first"));
		string privatePem = _keys.TrustJson("test-first").Replace("BEGIN PUBLIC KEY", "BEGIN PRIVATE KEY", StringComparison.Ordinal);
		Assert.Throws<InvalidDataException>(() => UpdateFeedTrustStore.Parse(privatePem));
	}

	private static JsonObject ParseObject(string json)
	{
		return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Test JSON object is missing.");
	}

	private static string CreatePayload()
	{
		UpdateReleaseArtifact package = new("AiUsageDashboard", "1.2.3", "win-x64", "AiUsageDashboard-1.2.3-win-x64.zip", "https://example.test/AiUsageDashboard-1.2.3-win-x64.zip", 100, new string('a', 64), "test-source");
		UpdateReleaseArtifact updater = new("AiUsageDashboard.Updater", "1.2.3", "win-x64", "AiUsageDashboard-Updater-1.2.3-win-x64.exe", "https://example.test/AiUsageDashboard-Updater-1.2.3-win-x64.exe", 100, new string('b', 64), "test-source");
		return JsonSerializer.Serialize(new UpdateReleaseFeed(1, "stable", 100, "1.2.3", package, updater));
	}
}
