using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class ManagedUninstallReceiptTests
{
	[Fact]
	public async Task ReadAsync_WhenManifestIsNull_RejectsReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string receiptPath = Path.Combine(
			temporaryDirectory.Path,
			"receipt.json");
		await File.WriteAllTextAsync(
			receiptPath,
			$$"""
			{
			  "schemaVersion": 1,
			  "installRoot": {{System.Text.Json.JsonSerializer.Serialize(installRoot)}},
			  "manifest": null
			}
			""");

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				ManagedUninstallReceipt.ReadAsync(receiptPath, installRoot));

		Assert.Contains("manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ReadAsync_WithUnknownProperty_RejectsReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string receiptPath = Path.Combine(
			temporaryDirectory.Path,
			"receipt.json");
		await File.WriteAllTextAsync(
			receiptPath,
			$$"""
			{
			  "schemaVersion": 1,
			  "installRoot": {{System.Text.Json.JsonSerializer.Serialize(installRoot)}},
			  "manifest": null,
			  "unexpected": true
			}
			""");

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			ManagedUninstallReceipt.ReadAsync(receiptPath, installRoot));
	}

	[Fact]
	public async Task ReadAsync_ForAnotherInstallRoot_RejectsReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstInstallRoot = Path.Combine(temporaryDirectory.Path, "first");
		string secondInstallRoot = Path.Combine(temporaryDirectory.Path, "second");
		string receiptPath = Path.Combine(
			temporaryDirectory.Path,
			"receipt.json");
		AiUsageDashboard.Updater.Core.UpdateManifest manifest = new(
			AiUsageDashboard.Updater.Core.UpdateManifest.CurrentSchemaVersion,
			AiUsageDashboard.Updater.Core.UpdateManifest.ExpectedPackageId,
			"1.2.3",
			AiUsageDashboard.Updater.Core.UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
		await ManagedUninstallReceipt.WriteNewAsync(
			receiptPath,
			firstInstallRoot,
			manifest);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			ManagedUninstallReceipt.ReadAsync(receiptPath, secondInstallRoot));
	}
}
