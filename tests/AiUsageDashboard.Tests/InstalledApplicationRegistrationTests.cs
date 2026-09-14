using System.Security.Cryptography;

using Microsoft.Win32;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class InstalledApplicationRegistrationTests
{
	private const string ArchiveHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public async Task EnsureRegisteredAsync_InstallsMaintenanceUpdaterAndUpsertsEntry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		string userDataRoot = Path.Combine(temporaryDirectory.Path, "data");
		UpdateManifest manifest = CreateInstalledPayload(installRoot, "1.2.3");
		FakeRegistrationStore store = new();
		ManagedInstallationRegistrar registrar = new(
			store,
			new ManagedStartMenuShortcut(Path.Combine(temporaryDirectory.Path, "Programs")));
		string sourceUpdater = GetUpdaterExecutablePath();

		string? warning = await registrar.EnsureRegisteredAsync(
			sourceUpdater,
			installRoot,
			maintenanceRoot,
			userDataRoot,
			manifest);

		InstalledApplicationRegistration registration = Assert.Single(
			store.Upserts);
		string maintenanceUpdater =
			ManagedInstallationPaths.GetMaintenanceUpdater(maintenanceRoot);
		Assert.True(File.Exists(maintenanceUpdater));
		Assert.Equal(
			GetSha256(sourceUpdater),
			GetSha256(maintenanceUpdater));
		Assert.Equal("1.2.3", registration.DisplayVersion);
		Assert.Equal(Path.GetFullPath(installRoot), registration.InstallLocation);
		Assert.Contains(
			ManagedInstallationPaths.GetInstalledAppExecutable(installRoot),
			registration.DisplayIcon,
			StringComparison.OrdinalIgnoreCase);
		Assert.Contains(
			$"\"{maintenanceUpdater}\" uninstall --confirm --notify",
			registration.UninstallCommand,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"--notify",
			registration.QuietUninstallCommand,
			StringComparison.Ordinal);
		Assert.Contains(
			$"--install-root \"{Path.GetFullPath(installRoot)}\"",
			registration.UninstallCommand,
			StringComparison.Ordinal);
		Assert.Matches("^[0-9]{8}$", registration.InstallDate);
		Assert.Null(warning);
		ShellShortcutDefinition shortcut = WindowsShellShortcut.Read(Path.Combine(
			temporaryDirectory.Path,
			"Programs",
			ManagedStartMenuShortcut.ShortcutFileName));
		Assert.Equal(ManagedInstallationPaths.GetInstalledAppExecutable(installRoot), shortcut.TargetPath);
		Assert.Empty(shortcut.Arguments);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EnsureRegisteredAsync_WhenVersionChanges_ReusesEntryAndCreatesMissingShortcut(bool previousVersionHasNoShortcut)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		string userDataRoot = Path.Combine(temporaryDirectory.Path, "data");
		FakeRegistrationStore store = new();
		ManagedInstallationRegistrar registrar = new(
			store,
			new ManagedStartMenuShortcut(Path.Combine(temporaryDirectory.Path, "Programs")));
		string sourceUpdater = GetUpdaterExecutablePath();
		UpdateManifest first = CreateInstalledPayload(installRoot, "1.2.3");
		Assert.Null(await registrar.EnsureRegisteredAsync(
			sourceUpdater,
			installRoot,
			maintenanceRoot,
			userDataRoot,
			first));

		if (previousVersionHasNoShortcut)
		{
			File.Delete(Path.Combine(
				temporaryDirectory.Path,
				"Programs",
				ManagedStartMenuShortcut.ShortcutFileName));
		}

		File.Delete(ManagedInstallationPaths.GetInstalledManifest(installRoot));
		UpdateManifest second = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.4",
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			ArchiveHash);
		await second.WriteAsync(
			ManagedInstallationPaths.GetInstalledManifest(installRoot));

		Assert.Null(await registrar.EnsureRegisteredAsync(
			sourceUpdater,
			installRoot,
			maintenanceRoot,
			userDataRoot,
			second));

		Assert.Equal(2, store.Upserts.Count);
		Assert.Equal("1.2.4", store.Upserts[^1].DisplayVersion);
		ShellShortcutDefinition shortcut = WindowsShellShortcut.Read(Path.Combine(
			temporaryDirectory.Path,
			"Programs",
			ManagedStartMenuShortcut.ShortcutFileName));
		Assert.Equal(ManagedInstallationPaths.GetInstalledAppExecutable(installRoot), shortcut.TargetPath);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task EnsureRegisteredAsync_WhenStableUpdaterIsLocked_UsesVerifiedGeneration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		string userDataRoot = Path.Combine(temporaryDirectory.Path, "data");
		UpdateManifest manifest = CreateInstalledPayload(installRoot, "1.2.3");
		FakeRegistrationStore store = new();
		ManagedInstallationRegistrar registrar = new(
			store,
			new ManagedStartMenuShortcut(Path.Combine(temporaryDirectory.Path, "Programs")));
		string sourceUpdater = GetUpdaterExecutablePath();
		string stableUpdater = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(sourceUpdater, stableUpdater);
		await File.AppendAllTextAsync(stableUpdater, "stale");
		string sourceHash = GetSha256(sourceUpdater).ToLowerInvariant();
		string generationUpdater =
			ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				sourceHash);

		using (FileStream lockedStableUpdater = new(
			stableUpdater,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			Assert.Null(await registrar.EnsureRegisteredAsync(
				sourceUpdater,
				installRoot,
				maintenanceRoot,
				userDataRoot,
				manifest));

			Assert.True(File.Exists(generationUpdater));
			Assert.Equal(GetSha256(sourceUpdater), GetSha256(generationUpdater));
			Assert.Contains(
				$"\"{generationUpdater}\" uninstall",
				store.Upserts[^1].UninstallCommand,
				StringComparison.OrdinalIgnoreCase);
		}

		Assert.Null(await registrar.EnsureRegisteredAsync(
			generationUpdater,
			installRoot,
			maintenanceRoot,
			userDataRoot,
			manifest));

		Assert.Equal(GetSha256(sourceUpdater), GetSha256(stableUpdater));
		Assert.Contains(
			$"\"{stableUpdater}\" uninstall",
			store.Upserts[^1].UninstallCommand,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnsureRegisteredAsync_WhenShortcutNameConflicts_ReturnsWarningAndKeepsWindowsRegistration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		UpdateManifest manifest = CreateInstalledPayload(installRoot, "1.2.3");
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		Directory.CreateDirectory(programsRoot);
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		File.WriteAllText(shortcutPath, "keep");
		FakeRegistrationStore store = new();
		ManagedInstallationRegistrar registrar = new(store, new ManagedStartMenuShortcut(programsRoot));

		string? warning = await registrar.EnsureRegisteredAsync(
			GetUpdaterExecutablePath(),
			installRoot,
			Path.Combine(temporaryDirectory.Path, "maintenance"),
			Path.Combine(temporaryDirectory.Path, "data"),
			manifest);

		Assert.NotNull(warning);
		Assert.Contains(shortcutPath, warning, StringComparison.Ordinal);
		Assert.Single(store.Upserts);
		Assert.Equal("keep", File.ReadAllText(shortcutPath));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EnsureRegisteredAsync_WhenProgramsResolutionFails_KeepsMaintenanceAndRegistration(
		bool throwsOnResolve)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string userDataRoot = Path.Combine(temporaryDirectory.Path, "data");
		Directory.CreateDirectory(userDataRoot);
		string userDataCanary = Path.Combine(userDataRoot, "settings.json");
		File.WriteAllText(userDataCanary, "keep");
		UpdateManifest manifest = CreateInstalledPayload(installRoot, "1.2.3");
		FakeRegistrationStore store = new();
		int resolveCount = 0;
		ManagedStartMenuShortcut shortcut = new(() =>
		{
			resolveCount++;

			if (throwsOnResolve)
			{
				throw new UnauthorizedAccessException("Synthetic Programs access denied.");
			}

			return string.Empty;
		});
		ManagedInstallationRegistrar registrar = new(store, shortcut);
		string sourceUpdater = GetUpdaterExecutablePath();
		Assert.Equal(0, resolveCount);

		string? warning = await registrar.EnsureRegisteredAsync(
			sourceUpdater,
			installRoot,
			maintenanceRoot,
			userDataRoot,
			manifest);

		InstalledApplicationRegistration registration = Assert.Single(store.Upserts);
		string maintenanceUpdater = ManagedInstallationPaths.GetMaintenanceUpdater(maintenanceRoot);
		Assert.Equal(GetSha256(sourceUpdater), GetSha256(maintenanceUpdater));
		Assert.Equal(Path.GetFullPath(installRoot), registration.InstallLocation);
		Assert.Equal("1.2.3", registration.DisplayVersion);
		Assert.Contains(maintenanceUpdater, registration.UninstallCommand, StringComparison.Ordinal);
		Assert.True(File.Exists(ManagedInstallationPaths.GetInstalledAppExecutable(installRoot)));
		Assert.Equal("keep", File.ReadAllText(userDataCanary));
		Assert.Equal(1, resolveCount);
		Assert.NotNull(warning);
		Assert.Contains("開始選單", warning, StringComparison.Ordinal);

		if (throwsOnResolve)
		{
			Assert.Contains("Synthetic Programs access denied.", warning, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task EnsureRegisteredAsync_WhenDiskManifestDiffers_DoesNotRegister()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		UpdateManifest installed = CreateInstalledPayload(installRoot, "1.2.3");
		UpdateManifest expected = installed with { Version = "1.2.4" };
		FakeRegistrationStore store = new();
		ManagedInstallationRegistrar registrar = new(
			store,
			new ManagedStartMenuShortcut(Path.Combine(temporaryDirectory.Path, "Programs")));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			registrar.EnsureRegisteredAsync(
				GetUpdaterExecutablePath(),
				installRoot,
				Path.Combine(temporaryDirectory.Path, "maintenance"),
				Path.Combine(temporaryDirectory.Path, "data"),
				expected));

		Assert.Empty(store.Upserts);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public void CurrentUserRegistryStore_RoundTripsStableInstalledAppEntry()
	{
		string testRoot = $@"Software\AiUsageDashboard.Tests\{Guid.NewGuid():N}";
		string subKeyPath = $@"{testRoot}\Uninstall\AiUsageDashboard";
		CurrentUserUninstallRegistryStore store = new(subKeyPath);
		string installRoot = Path.Combine(
			Path.GetTempPath(),
			"AiUsageDashboard.RegistryTest");
		InstalledApplicationRegistration registration = new(
			"1.2.3",
			installRoot,
			@"C:\App\AiUsageDashboard.App.exe,0",
			"\"C:\\App\\AiUsageDashboard.Updater.exe\" uninstall --confirm --notify",
			"\"C:\\App\\AiUsageDashboard.Updater.exe\" uninstall --confirm",
			"20260826");

		try
		{
			store.Upsert(registration);

			using RegistryKey currentUser = RegistryKey.OpenBaseKey(
				RegistryHive.CurrentUser,
				RegistryView.Registry64);
			using RegistryKey key = currentUser.OpenSubKey(subKeyPath) ??
				throw new InvalidOperationException("Test registry key was not created.");
			Assert.Equal("AI Usage", key.GetValue("DisplayName"));
			Assert.Equal("1.2.3", key.GetValue("DisplayVersion"));
			Assert.Equal(installRoot, key.GetValue("InstallLocation"));
			Assert.Equal(1, key.GetValue("NoModify"));
			Assert.Equal(1, key.GetValue("NoRepair"));
			Assert.Null(key.GetValue("NoRemove"));
			Assert.Equal(
				registration.UninstallCommand,
				key.GetValue("UninstallString"));
			Assert.Equal(
				registration.QuietUninstallCommand,
				key.GetValue("QuietUninstallString"));
			Assert.True(store.HasMatchingInstallLocation(installRoot));
			Assert.True(store.RemoveIfMatches(installRoot));
			Assert.False(store.HasMatchingInstallLocation(installRoot));
		}
		finally
		{
			using RegistryKey currentUser = RegistryKey.OpenBaseKey(
				RegistryHive.CurrentUser,
				RegistryView.Registry64);
			currentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
		}
	}

	private static UpdateManifest CreateInstalledPayload(
		string installRoot,
		string version)
	{
		string appPath = ManagedInstallationPaths.GetInstalledAppExecutable(
			installRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
		File.WriteAllText(appPath, "app");
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			version,
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			ArchiveHash);
		manifest.WriteAsync(
			ManagedInstallationPaths.GetInstalledManifest(installRoot))
			.GetAwaiter()
			.GetResult();
		return manifest;
	}

	private static string GetSha256(string path)
	{
		return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
	}

	private static string GetUpdaterExecutablePath()
	{
		string path = Path.Combine(
			AppContext.BaseDirectory,
			ManagedInstallationPaths.MaintenanceUpdaterFileName);
		Assert.True(File.Exists(path), $"Updater test executable missing: {path}");
		return path;
	}

	private sealed class FakeRegistrationStore :
		IInstalledApplicationRegistrationStore
	{
		internal List<InstalledApplicationRegistration> Upserts { get; } = [];

		public void Upsert(InstalledApplicationRegistration registration)
		{
			Upserts.Add(registration);
		}

		public bool HasMatchingInstallLocation(string installRoot)
		{
			return Upserts.Count > 0 && string.Equals(
				Upserts[^1].InstallLocation,
				Path.GetFullPath(installRoot),
				StringComparison.OrdinalIgnoreCase);
		}

		public bool RemoveIfMatches(string installRoot)
		{
			return HasMatchingInstallLocation(installRoot);
		}
	}
}
