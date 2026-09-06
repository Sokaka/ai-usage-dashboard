using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterLocalPackageTests
{
	private const string Hash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public async Task ReadAsync_WithCanonicalPackageAndSidecar_CreatesManifest()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string packagePath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard-1.2.3-preview.4-win-x64.zip");
		string sidecarPath = packagePath + ".sha256";
		await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
		await File.WriteAllTextAsync(
			sidecarPath,
			$"{Hash}  {Path.GetFileName(packagePath)}");

		LocalUpdatePackage package = await LocalUpdatePackage.ReadAsync(
			packagePath,
			sidecarPath);

		Assert.Equal(Path.GetFullPath(packagePath), package.PackagePath);
		Assert.Equal("1.2.3-preview.4", package.Manifest.Version);
		Assert.Equal(3, package.Manifest.ArchiveSizeBytes);
		Assert.Equal(Hash, package.Manifest.ArchiveSha256);
	}

	[Theory]
	[InlineData("AiUsageDashboard-1.2.3-win-x64.zip", "other.zip")]
	[InlineData("AiUsageDashboard-1.2.3-win-x64.zip", "AiUsageDashboard-1.2.3-win-x64.zip\n")]
	[InlineData("release.zip", "release.zip")]
	public async Task ReadAsync_WithNonCanonicalInput_Rejects(
		string packageFileName,
		string sidecarFileName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string packagePath = Path.Combine(
			temporaryDirectory.Path,
			packageFileName);
		string sidecarPath = packagePath + ".sha256";
		await File.WriteAllBytesAsync(packagePath, [1]);
		await File.WriteAllTextAsync(
			sidecarPath,
			$"{Hash}  {sidecarFileName}");

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			LocalUpdatePackage.ReadAsync(packagePath, sidecarPath));
	}

	[Fact]
	public async Task ReadAsync_WithNonAdjacentSidecar_RejectsBeforeReadingIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string packagePath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard-1.2.3-win-x64.zip");
		string sidecarPath = Path.Combine(
			temporaryDirectory.Path,
			"checksum.sha256");
		await File.WriteAllBytesAsync(packagePath, [1]);
		await File.WriteAllTextAsync(
			sidecarPath,
			$"{Hash}  {Path.GetFileName(packagePath)}");

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				LocalUpdatePackage.ReadAsync(packagePath, sidecarPath));

		Assert.Contains("adjacent", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReadAsync_WithOversizedAdjacentSidecar_Rejects()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string packagePath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard-1.2.3-win-x64.zip");
		string sidecarPath = packagePath + ".sha256";
		await File.WriteAllBytesAsync(packagePath, [1]);
		await File.WriteAllBytesAsync(sidecarPath, new byte[1025]);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			LocalUpdatePackage.ReadAsync(packagePath, sidecarPath));
	}

	[Fact]
	public void Acquire_WhenAnotherUpdaterHoldsLock_RejectsUntilReleased()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");

		using (UpdateInstallLock first = UpdateInstallLock.Acquire(installRoot))
		{
			Assert.Throws<UpdateInstallLockException>(() =>
				UpdateInstallLock.Acquire(installRoot));
		}

		using UpdateInstallLock afterRelease =
			UpdateInstallLock.Acquire(installRoot);
		Assert.True(File.Exists(Path.Combine(installRoot, ".updater.lock")));
	}

	[Fact]
	public void Acquire_WithVolumeRoot_RejectsBeforeOpeningLockFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string volumeRoot = Path.GetPathRoot(temporaryDirectory.Path) ??
			throw new InvalidOperationException("Temporary path has no volume root.");

		Assert.Throws<ArgumentException>(() =>
			UpdateInstallLock.Acquire(volumeRoot));
	}

	[Fact]
	public void AcquireExisting_WhenInstallRootIsMissing_DoesNotCreateIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "missing");

		Assert.Throws<DirectoryNotFoundException>(() =>
			UpdateInstallLock.AcquireExisting(installRoot));

		Assert.False(Directory.Exists(installRoot));
	}
}
