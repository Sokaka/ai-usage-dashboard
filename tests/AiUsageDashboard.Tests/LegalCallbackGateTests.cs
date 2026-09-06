using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.Tests;

public sealed class LegalCallbackGateTests
{
	[Fact]
	public void CheckAcceptance_WithNoReceipt_ReturnsAcceptanceRequiredWithoutCreatingUserFiles()
	{
		using TemporaryDirectory directory = new();
		LegalAcceptanceStore store = CreateStore(directory);

		int exitCode = LegalCallbackGate.CheckAcceptance(
			() => LegalCatalog.Load(LegalProfile.Capture), () => store);

		Assert.Equal(LegalCommandLine.AcceptanceRequiredExitCode, exitCode);
		Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
	}

	[Fact]
	public void CheckAcceptance_WithCorruptReceipt_ReturnsFailureAndPreservesEvidence()
	{
		using TemporaryDirectory directory = new();
		string receiptPath = Path.Combine(directory.Path, "acceptance.json");
		File.WriteAllText(receiptPath, "{broken-receipt");

		int exitCode = LegalCallbackGate.CheckAcceptance(
			() => LegalCatalog.Load(LegalProfile.Capture), () => CreateStore(directory));

		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
		Assert.Equal("{broken-receipt", File.ReadAllText(receiptPath));
	}

	[Fact]
	public void CheckAcceptance_WhenEmbeddedTermsCannotLoad_ReturnsFailureBeforeAccessingTheReceipt()
	{
		bool accessedReceipt = false;

		int exitCode = LegalCallbackGate.CheckAcceptance(
			() => throw new InvalidDataException("Injected missing embedded license document."),
			() =>
			{
				accessedReceipt = true;
				throw new InvalidOperationException("The receipt must not be accessed.");
			});

		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
		Assert.False(accessedReceipt);
	}

	[Fact]
	public void CheckAcceptance_WhenWindowsIdentityCannotBeResolved_ReturnsFailure()
	{
		int exitCode = LegalCallbackGate.CheckAcceptance(
			() => LegalCatalog.Load(LegalProfile.Capture),
			() => throw new InvalidOperationException("Injected unavailable Windows SID."));

		Assert.Equal(LegalCommandLine.FailureExitCode, exitCode);
	}

	[Fact]
	public void CheckAcceptance_WithAcceptedIsolatedTestReceipt_AllowsCapture()
	{
		using TemporaryDirectory directory = new();
		LegalCatalog catalog = LegalCatalog.Load(LegalProfile.Capture);
		LegalAcceptanceStore store = CreateStore(directory);
		store.Accept(catalog, catalog.Digest);

		int exitCode = LegalCallbackGate.CheckAcceptance(() => catalog, () => store);

		Assert.Equal(0, exitCode);
	}

	private static LegalAcceptanceStore CreateStore(TemporaryDirectory directory)
	{
		return new LegalAcceptanceStore(
			Path.Combine(directory.Path, "acceptance.json"), "S-1-5-21-isolated-callback-test");
	}
}
