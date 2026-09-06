using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterPackageStagerTests
{
	private sealed record PackageEntry(
		string Path,
		byte[]? Contents = null,
		int? ExternalAttributes = null);

	[Fact]
	public async Task StageAsync_WithCanonicalPackageWithoutDirectoryEntries_PreservesLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/使用說明.md", "說明"u8.ToArray()),
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D, 0x5A]),
				new("AiUsageDashboard/app/runtime.json", "{}"u8.ToArray())
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"canonical-package");

		StagedUpdatePackage stagedPackage = await new UpdatePackageStager().StageAsync(
			packagePath,
			manifest,
			transaction);

		Assert.Equal(
			"說明",
			await File.ReadAllTextAsync(Path.Combine(
				stagedPackage.PayloadDirectoryPath,
				"使用說明.md")));
		Assert.True(File.Exists(stagedPackage.ApplicationExecutablePath));
	}

	[Fact]
	public async Task StageAsync_WithValidPackage_ExtractsPayloadAndWritesManifest()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/"),
				new("AiUsageDashboard/app/"),
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D, 0x5A]),
				new("AiUsageDashboard/app/README.md", "guide"u8.ToArray())
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"valid-package");
		UpdatePackageStager stager = new();

		StagedUpdatePackage stagedPackage = await stager.StageAsync(
			packagePath,
			manifest,
			transaction);

		Assert.Equal(UpdateTransactionState.Staged, transaction.State);
		Assert.Equal(
			[0x4D, 0x5A],
			await File.ReadAllBytesAsync(stagedPackage.ApplicationExecutablePath));
		Assert.Equal(
			"guide",
			await File.ReadAllTextAsync(Path.Combine(
				stagedPackage.PayloadDirectoryPath,
				"app",
				"README.md")));
		UpdateManifest installedManifest = await UpdateManifest.ReadAsync(
			Path.Combine(
				stagedPackage.PayloadDirectoryPath,
				UpdateManifest.InstalledFileName));
		Assert.Equal(manifest, installedManifest);
	}

	[Fact]
	public async Task StageAsync_WithThirdPartyNoticesInsideApp_PreservesBothGuideLinkTargets()
	{
		const string RootGuide = """
			[Copilot CLI](app/third-party-notices/GitHub-Copilot-CLI-LICENSE.md)
			[Copilot SDK](app/third-party-notices/GitHub-Copilot-SDK-LICENSE.md)
			""";
		const string AppGuide = """
			[Copilot CLI](third-party-notices/GitHub-Copilot-CLI-LICENSE.md)
			[Copilot SDK](third-party-notices/GitHub-Copilot-SDK-LICENSE.md)
			""";
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/"),
				new("AiUsageDashboard/app/"),
				new("AiUsageDashboard/app/third-party-notices/"),
				new("AiUsageDashboard/使用說明.md", Encoding.UTF8.GetBytes(RootGuide)),
				new("AiUsageDashboard/app/README.md", Encoding.UTF8.GetBytes(AppGuide)),
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D, 0x5A]),
				new(
					"AiUsageDashboard/app/third-party-notices/GitHub-Copilot-CLI-LICENSE.md",
					"CLI license"u8.ToArray()),
				new(
					"AiUsageDashboard/app/third-party-notices/GitHub-Copilot-SDK-LICENSE.md",
					"SDK license"u8.ToArray())
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"package-guide-links");

		StagedUpdatePackage stagedPackage = await new UpdatePackageStager().StageAsync(
			packagePath,
			manifest,
			transaction);

		Assert.Equal(UpdateTransactionState.Staged, transaction.State);
		Assert.False(Directory.Exists(Path.Combine(
			stagedPackage.PayloadDirectoryPath,
			"third-party-notices")));
		foreach (string guidePath in new[]
		{
			Path.Combine(stagedPackage.PayloadDirectoryPath, "使用說明.md"),
			Path.Combine(stagedPackage.PayloadDirectoryPath, "app", "README.md")
		})
		{
			string guide = await File.ReadAllTextAsync(guidePath);
			MatchCollection links = Regex.Matches(guide, @"\]\((?<target>[^)]+)\)");
			Assert.Equal(2, links.Count);
			foreach (Match link in links)
			{
				string target = Path.GetFullPath(Path.Combine(
					Path.GetDirectoryName(guidePath)!,
					link.Groups["target"].Value));
				Assert.Equal(
					Path.Combine(stagedPackage.PayloadDirectoryPath, "app", "third-party-notices"),
					Path.GetDirectoryName(target));
				Assert.True(File.Exists(target), $"Packaged guide target is missing: {target}");
			}
		}
	}

	[Fact]
	public async Task StageAsync_WithSizeMismatch_RejectsBeforeCreatingStaging()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreateValidPackage(
			temporaryDirectory);
		manifest = manifest with { ArchiveSizeBytes = manifest.ArchiveSizeBytes + 1 };
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"size-mismatch");

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));

		Assert.False(Directory.Exists(transaction.StagingDirectoryPath));
		Assert.Equal(UpdateTransactionState.Prepared, transaction.State);
	}

	[Fact]
	public async Task StageAsync_WithHashMismatch_RejectsBeforeCreatingStaging()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreateValidPackage(
			temporaryDirectory);
		manifest = manifest with { ArchiveSha256 = new string('0', 64) };
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"hash-mismatch");

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));

		Assert.False(Directory.Exists(transaction.StagingDirectoryPath));
		Assert.Equal(UpdateTransactionState.Prepared, transaction.State);
	}

	[Theory]
	[InlineData("../outside.txt")]
	[InlineData("/AiUsageDashboard/app/outside.txt")]
	[InlineData("C:/AiUsageDashboard/app/outside.txt")]
	[InlineData("AiUsageDashboard/app/../outside.txt")]
	[InlineData("OtherRoot/app/AiUsageDashboard.App.exe")]
	public async Task StageAsync_WithUnsafeOrUnexpectedPath_FailsClosed(
		string unsafePath)
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new(unsafePath, [0x01]),
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D, 0x5A])
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));

		Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "outside.txt")));
		Assert.False(Directory.Exists(transaction.StagingDirectoryPath));
	}

	[Fact]
	public async Task StageAsync_WithDuplicateEntry_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D]),
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x5A])
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));
	}

	[Fact]
	public async Task StageAsync_WithCaseCollision_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D]),
				new("AiUsageDashboard/app/readme.md", [0x01]),
				new("AiUsageDashboard/app/README.md", [0x02])
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));
	}

	[Fact]
	public async Task StageAsync_WithReparseEntry_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const int symbolicLinkAttributes = unchecked((int)0xA0000000);
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D]),
				new(
					"AiUsageDashboard/app/link",
					"target"u8.ToArray(),
					symbolicLinkAttributes)
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new UpdatePackageStager().StageAsync(packagePath, manifest, transaction));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task StageAsync_WhenStagingDirectoryIsJunction_FailsBeforeExtraction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreateValidPackage(
			temporaryDirectory);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"),
			"staging-junction");
		string externalDirectory = Path.Combine(
			temporaryDirectory.Path,
			"external");
		Directory.CreateDirectory(externalDirectory);
		await JunctionTestHelper.CreateAsync(
			transaction.StagingDirectoryPath,
			externalDirectory);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				new UpdatePackageStager().StageAsync(
					packagePath,
					manifest,
					transaction));
			Assert.Empty(Directory.EnumerateFileSystemEntries(externalDirectory));
		}
		finally
		{
			JunctionTestHelper.Delete(transaction.StagingDirectoryPath);
		}
	}

	[Fact]
	public async Task StageAsync_WhenExpandedLimitIsExceeded_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreateValidPackage(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		UpdatePackageStager stager = new(
			maximumEntryCount: 10,
			maximumArchiveSizeBytes: 1024,
			maximumEntrySizeBytes: 3,
			maximumExpandedSizeBytes: 3);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			stager.StageAsync(packagePath, manifest, transaction));
	}

	[Fact]
	public async Task StageAsync_WhenEntryCountLimitIsExceeded_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string packagePath, UpdateManifest manifest) = CreatePackage(
			temporaryDirectory,
			[
				new("AiUsageDashboard/app/AiUsageDashboard.App.exe", [0x4D]),
				new("AiUsageDashboard/app/README.md", [0x01])
			]);
		UpdateTransaction transaction = UpdateTransaction.Create(
			Path.Combine(temporaryDirectory.Path, "install"));
		UpdatePackageStager stager = new(
			maximumEntryCount: 1,
			maximumArchiveSizeBytes: 1024,
			maximumEntrySizeBytes: 1024,
			maximumExpandedSizeBytes: 1024);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			stager.StageAsync(packagePath, manifest, transaction));
	}

	private static (string PackagePath, UpdateManifest Manifest) CreateValidPackage(
		TemporaryDirectory temporaryDirectory,
		byte[]? executableContents = null)
	{
		return CreatePackage(
			temporaryDirectory,
			[
				new(
					"AiUsageDashboard/app/AiUsageDashboard.App.exe",
					executableContents ?? [0x4D, 0x5A])
			]);
	}

	private static (string PackagePath, UpdateManifest Manifest) CreatePackage(
		TemporaryDirectory temporaryDirectory,
		IReadOnlyList<PackageEntry> entries)
	{
		string packagePath = Path.Combine(temporaryDirectory.Path, "package.zip");
		using (FileStream packageStream = File.Create(packagePath))
		using (ZipArchive archive = new(
			packageStream,
			ZipArchiveMode.Create,
			leaveOpen: false))
		{
			foreach (PackageEntry packageEntry in entries)
			{
				ZipArchiveEntry entry = archive.CreateEntry(packageEntry.Path);
				if (packageEntry.ExternalAttributes is int externalAttributes)
				{
					entry.ExternalAttributes = externalAttributes;
				}

				if (packageEntry.Contents is not null)
				{
					using Stream entryStream = entry.Open();
					entryStream.Write(packageEntry.Contents);
				}
			}
		}

		byte[] packageBytes = File.ReadAllBytes(packagePath);
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			packageBytes.LongLength,
			Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant());
		return (packagePath, manifest);
	}
}
