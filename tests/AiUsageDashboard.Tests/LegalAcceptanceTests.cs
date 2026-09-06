using System.Text.Json.Nodes;

using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.Tests;

public sealed class LegalAcceptanceTests : IDisposable
{
	private sealed class FixedTimeProvider : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
	}

	private readonly string _testDirectory = Path.Combine(Path.GetTempPath(),
		"AiUsageDashboard.LegalTests." + Guid.NewGuid().ToString("N"));

	[Fact]
	public void AppAcceptanceCoversSetupUpdaterAndCaptureForTheSameWindowsUser()
	{
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		LegalCatalog app = LegalCatalog.Load(LegalProfile.App);
		Assert.False(store.IsAccepted(app));
		store.Accept(app, app.Digest);
		Assert.True(store.IsAccepted(app));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Setup)));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Updater)));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Capture)));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Installer)));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.ClaudeCapture)));
	}

	[Theory]
	[InlineData(LegalProfile.Capture)]
	[InlineData(LegalProfile.Updater)]
	public void CoreRuntimeAcceptanceDoesNotAuthorizeClaudeSharedJsonDependencies(LegalProfile acceptedProfile)
	{
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		LegalCatalog accepted = LegalCatalog.Load(acceptedProfile);
		store.Accept(accepted, accepted.Digest);
		LegalCatalog claude = LegalCatalog.Load(LegalProfile.ClaudeCapture);

		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Capture)));
		Assert.False(store.IsAccepted(claude));
		Assert.Equal(LegalCommandLine.AcceptanceRequiredExitCode,
			LegalCallbackGate.CheckAcceptance(() => claude, () => store));
	}

	[Fact]
	public void ClaudeCaptureTermsCoverJsonDependenciesWithoutDesktopOrCopilotComponents()
	{
		LegalCatalog claude = LegalCatalog.Load(LegalProfile.ClaudeCapture);
		string[] componentIds = claude.Components.Select(component => component.Id)
			.Order(StringComparer.Ordinal).ToArray();

		Assert.Equal(["dashboard", "dotnet8", "microsoft-terms", "system.io.pipelines",
			"system.text.encodings.web", "system.text.json"], componentIds);
		Assert.Contains("claude-capture", claude.GetReadableText(), StringComparison.Ordinal);
	}

	[Fact]
	public void UpdaterAcceptanceDoesNotAuthorizeWindowsDesktopOrCopilotComponents()
	{
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		LegalCatalog updater = LegalCatalog.Load(LegalProfile.Updater);
		store.Accept(updater, updater.Digest);
		Assert.False(store.IsAccepted(LegalCatalog.Load(LegalProfile.App)));
		Assert.False(store.IsAccepted(LegalCatalog.Load(LegalProfile.Setup)));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Capture)));
	}

	[Fact]
	public void CopiedReceiptFromAnotherWindowsSidDoesNotAuthorizeUse()
	{
		LegalCatalog app = LegalCatalog.Load(LegalProfile.App);
		CreateStore("S-1-5-21-test-user-a").Accept(app, app.Digest);
		Assert.False(CreateStore("S-1-5-21-test-user-b").IsAccepted(app));
	}

	[Fact]
	public void ExplicitAcceptanceRejectsAStaleDigestWithoutCreatingAReceipt()
	{
		LegalCatalog app = LegalCatalog.Load(LegalProfile.App);
		Assert.Throws<InvalidOperationException>(() =>
			CreateStore("S-1-5-21-test-user-a").Accept(app, new string('0', 64)));
		Assert.False(Directory.Exists(_testDirectory));
	}

	[Fact]
	public void ChangedComponentVersionRequiresNewAcceptanceButPreservesUnrelatedScope()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.App);
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		store.Accept(original, original.Digest);
		JsonObject manifest = ReadManifest(original);
		JsonObject component = manifest["components"]!.AsArray().Select(item => item!.AsObject())
			.Single(item => item["id"]!.GetValue<string>() == "copilot-cli");
		component["version"] = "future-test-version";
		LegalCatalog changed = LoadChangedCatalog(original, manifest);
		Assert.NotEqual(original.Digest, changed.Digest);
		Assert.False(store.IsAccepted(changed));
		Assert.True(store.IsAccepted(LegalCatalog.Load(LegalProfile.Updater)));
	}

	[Fact]
	public void ChangedTopLevelTermsVersionRequiresNewAcceptance()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.Updater);
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		store.Accept(original, original.Digest);
		string manifest = LegalCatalog.StrictUtf8.GetString(original.GetScopedManifestBytes());
		string changedManifest = manifest.Replace(
			$"\"termsVersion\": \"{original.TermsVersion}\"",
			"\"termsVersion\": \"future-test-terms\"", StringComparison.Ordinal);
		Assert.NotEqual(manifest, changedManifest);
		LegalCatalog changed = LegalCatalog.Load(original.Profile,
			LegalCatalog.StrictUtf8.GetBytes(changedManifest), path => ReadBytes(original, path));
		Assert.Equal(original.Components.Select(component => component.ManifestJson),
			changed.Components.Select(component => component.ManifestJson));
		Assert.NotEqual(original.Digest, changed.Digest);
		Assert.False(store.IsAccepted(changed));
	}

	[Fact]
	public void ExportedManifestPreservesTheComponentAcceptanceDigests()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.Installer);
		LegalCatalog reloaded = LegalCatalog.Load(original.Profile,
			original.GetScopedManifestBytes(), path => ReadBytes(original, path));
		Assert.Equal(original.Digest, reloaded.Digest);
		Assert.Equal(original.Components.Select(component => component.Digest),
			reloaded.Components.Select(component => component.Digest));
	}

	[Fact]
	public void InstallerScopeIncludesDestinationAppTermsBeforePayloadInstallation()
	{
		LegalCatalog installer = LegalCatalog.Load(LegalProfile.Installer);
		Assert.Contains(installer.Components, component => component.Id == "windowsdesktop8");
		Assert.Contains(installer.Components, component => component.Id == "copilot-cli");
		Assert.Contains("App ZIP", installer.GetReadableText());
		Assert.Equal(LegalCatalog.Load(LegalProfile.App).Digest, installer.Digest);
	}

	[Fact]
	public void ChangedDocumentBytesWithANewManifestHashRequireNewAcceptance()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.Updater);
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		store.Accept(original, original.Digest);
		JsonObject manifest = ReadManifest(original);
		JsonObject metadata = manifest["documents"]!.AsArray().Select(item => item!.AsObject())
			.Single(item => item["id"]!.GetValue<string>() == "supplemental");
		string changedPath = metadata["path"]!.GetValue<string>();
		byte[] changedBytes = LegalCatalog.StrictUtf8.GetBytes("Changed terms for an isolated test.\n");
		metadata["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(changedBytes)).ToLowerInvariant();
		LegalCatalog changed = LegalCatalog.Load(original.Profile,
			LegalCatalog.StrictUtf8.GetBytes(manifest.ToJsonString()),
			path => path == changedPath ? changedBytes : ReadBytes(original, path));
		Assert.False(store.IsAccepted(changed));
	}

	[Fact]
	public void CorruptEmbeddedDocumentFailsBeforeItsTermsCanBeAccepted()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.Updater);
		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			LegalCatalog.Load(original.Profile, original.GetScopedManifestBytes(), _ => [1, 2, 3]));
		Assert.Contains("SHA256", exception.Message);
	}

	[Fact]
	public void TraversalInManifestIsRejectedBeforeReadingOutsideItsDocumentRoot()
	{
		LegalCatalog original = LegalCatalog.Load(LegalProfile.Updater);
		JsonObject manifest = ReadManifest(original);
		manifest["documents"]!.AsArray()[0]!["path"] = "../outside-license.txt";
		bool documentRead = false;
		Assert.Throws<InvalidDataException>(() => LegalCatalog.Load(original.Profile,
			LegalCatalog.StrictUtf8.GetBytes(manifest.ToJsonString()), _ =>
			{
				documentRead = true;
				return [];
			}));
		Assert.False(documentRead);
	}

	[Fact]
	public void OfflineExportContainsExactScopedDocumentsAndNoAcceptanceReceipt()
	{
		LegalCatalog updater = LegalCatalog.Load(LegalProfile.Updater);
		updater.Export(_testDirectory);
		foreach (LegalDocument document in updater.Documents)
		{
			Assert.Equal(document.GetBytes().ToArray(), File.ReadAllBytes(Path.Combine(_testDirectory, document.Path)));
		}
		Assert.DoesNotContain(updater.Documents, document => document.Path.Contains("Copilot", StringComparison.Ordinal));
		Assert.DoesNotContain(updater.Documents, document => document.Path.Contains("WINDOWS-SDK", StringComparison.Ordinal));
		Assert.False(File.Exists(Path.Combine(_testDirectory, "acceptance.json")));
		Assert.Equal(updater.Digest, File.ReadAllText(Path.Combine(_testDirectory, "ACCEPTANCE-DIGEST.txt")).Trim());
	}

	[Fact]
	public void ExportDoesNotOverwriteAnExistingDifferentUserFile()
	{
		Directory.CreateDirectory(_testDirectory);
		File.WriteAllText(Path.Combine(_testDirectory, "LICENSE"), "user-owned text");
		Assert.Throws<IOException>(() => LegalCatalog.Load(LegalProfile.Updater).Export(_testDirectory));
		Assert.Equal("user-owned text", File.ReadAllText(Path.Combine(_testDirectory, "LICENSE")));
	}

	[Fact]
	public void ReadOnlyCommandDoesNotAccessTheAcceptanceStore()
	{
		LegalCatalog updater = LegalCatalog.Load(LegalProfile.Updater);
		using StringWriter output = new();
		using StringWriter error = new();
		Assert.True(LegalCommandLine.TryHandle(["--license-digest"], updater,
			() => throw new InvalidOperationException("The read-only command must not access the user store."),
			out int exitCode, output, error));
		Assert.Equal(0, exitCode);
		Assert.Equal(updater.Digest, output.ToString().Trim());
		Assert.Equal(string.Empty, error.ToString());
		Assert.False(Directory.Exists(_testDirectory));
	}

	[Fact]
	public void IncompleteAcceptCommandFailsWithoutSavingAcceptance()
	{
		LegalCatalog updater = LegalCatalog.Load(LegalProfile.Updater);
		using StringWriter output = new();
		using StringWriter error = new();
		Assert.True(LegalCommandLine.TryHandle(["--accept-licenses"], updater,
			CreateStore("S-1-5-21-test-user-a"), out int exitCode, output, error));
		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
		Assert.False(Directory.Exists(_testDirectory));
	}

	[Fact]
	public void CorruptReceiptFailsClosedWithItsPathAsContext()
	{
		Directory.CreateDirectory(_testDirectory);
		string path = Path.Combine(_testDirectory, "acceptance.json");
		File.WriteAllText(path, "{");
		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			CreateStore("S-1-5-21-test-user-a").IsAccepted(LegalCatalog.Load(LegalProfile.Updater)));
		Assert.Contains(path, exception.Message);
		Assert.NotNull(exception.InnerException);
	}

	[Fact]
	public void AcceptCommandWithCorruptReceiptReturnsLicenseFailureAndPreservesTheReceipt()
	{
		Directory.CreateDirectory(_testDirectory);
		string receiptPath = Path.Combine(_testDirectory, "acceptance.json");
		const string CorruptReceipt = "{incomplete-receipt";
		File.WriteAllText(receiptPath, CorruptReceipt);
		LegalCatalog catalog = LegalCatalog.Load(LegalProfile.Capture);
		using StringWriter output = new();
		using StringWriter error = new();

		bool handled = LegalCommandLine.TryHandle(["--accept-licenses", catalog.Digest],
			catalog, CreateStore("S-1-5-21-isolated-license-command"), out int exitCode, output, error);

		Assert.True(handled);
		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("License command failed:", error.ToString(), StringComparison.Ordinal);
		Assert.Contains(receiptPath, error.ToString(), StringComparison.Ordinal);
		Assert.Equal(CorruptReceipt, File.ReadAllText(receiptPath));
	}

	[Fact]
	public void ExportCommandWithMissingEmbeddedTermsReturnsLicenseFailureWithoutAccessingUserState()
	{
		bool accessedReceipt = false;
		using StringWriter output = new();
		using StringWriter error = new();

		bool handled = LegalCommandLine.TryHandle(["--export-licenses", _testDirectory],
			() => throw new InvalidDataException("Injected missing embedded license document."),
			() =>
			{
				accessedReceipt = true;
				return CreateStore("S-1-5-21-isolated-license-command");
			}, out int exitCode, output, error);

		Assert.True(handled);
		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Injected missing embedded license document.", error.ToString(), StringComparison.Ordinal);
		Assert.False(accessedReceipt);
		Assert.False(Directory.Exists(_testDirectory));
	}

	[Fact]
	public void AcceptingASubsetPreservesPreviouslyAcceptedApplicationComponents()
	{
		LegalAcceptanceStore store = CreateStore("S-1-5-21-test-user-a");
		LegalCatalog app = LegalCatalog.Load(LegalProfile.App);
		LegalCatalog updater = LegalCatalog.Load(LegalProfile.Updater);
		store.Accept(app, app.Digest);
		store.Accept(updater, updater.Digest);
		Assert.True(store.IsAccepted(app));
		Assert.Empty(Directory.GetFiles(_testDirectory, "*.tmp"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_testDirectory))
		{
			Directory.Delete(_testDirectory, recursive: true);
		}
	}

	private LegalAcceptanceStore CreateStore(string sid) =>
		new(Path.Combine(_testDirectory, "acceptance.json"), sid, new FixedTimeProvider());

	private static JsonObject ReadManifest(LegalCatalog catalog) =>
		JsonNode.Parse(catalog.GetScopedManifestBytes())!.AsObject();

	private static LegalCatalog LoadChangedCatalog(LegalCatalog original, JsonObject manifest) =>
		LegalCatalog.Load(original.Profile, LegalCatalog.StrictUtf8.GetBytes(manifest.ToJsonString()),
			path => ReadBytes(original, path));

	private static byte[] ReadBytes(LegalCatalog original, string path) =>
		original.Documents.Single(document => document.Path == path).GetBytes().ToArray();
}
