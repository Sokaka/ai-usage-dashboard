using System.Diagnostics;

using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class MaintenanceUpdaterCleanupTests
{
	[Fact]
	public void Cleanup_WhenUpdaterRunsOutsideMaintenanceRoot_DeletesExactRoot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string updaterPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(GetUpdaterExecutablePath(), updaterPath);
		MaintenanceUpdaterCleanup cleanup = new();

		MaintenanceUpdaterCleanupResult result = cleanup.Cleanup(
			maintenanceRoot,
			Path.Combine(temporaryDirectory.Path, "external", "Updater.exe"));

		Assert.True(result.IsCompleteOrScheduled);
		Assert.Null(result.Warning);
		Assert.False(Directory.Exists(maintenanceRoot));
	}

	[Fact]
	public void Cleanup_WithUnexpectedFile_PreservesMaintenanceRoot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(
			GetUpdaterExecutablePath(),
			ManagedInstallationPaths.GetMaintenanceUpdater(maintenanceRoot));
		string unexpectedFile = Path.Combine(maintenanceRoot, "keep.txt");
		File.WriteAllText(unexpectedFile, "keep");
		MaintenanceUpdaterCleanup cleanup = new();

		MaintenanceUpdaterCleanupResult result = cleanup.Cleanup(
			maintenanceRoot,
			Path.Combine(temporaryDirectory.Path, "external", "Updater.exe"));

		Assert.False(result.IsCompleteOrScheduled);
		Assert.NotNull(result.Warning);
		Assert.True(File.Exists(unexpectedFile));
	}

	[Fact]
	public void Cleanup_WhenRunningFromMaintenanceUpdater_SchedulesHiddenSelfCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string updaterPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(GetUpdaterExecutablePath(), updaterPath);
		ProcessStartInfo? observed = null;
		string? requestedCurrentDirectory = null;
		MaintenanceUpdaterCleanup cleanup = new(
			startInfo =>
			{
				observed = startInfo;
				return Process.GetCurrentProcess();
			},
			path => requestedCurrentDirectory = path);

		MaintenanceUpdaterCleanupResult result = cleanup.Cleanup(
			maintenanceRoot,
			updaterPath);

		Assert.True(result.IsCompleteOrScheduled);
		Assert.NotNull(observed);
		Assert.False(observed.UseShellExecute);
		Assert.True(observed.CreateNoWindow);
		Assert.Equal(ProcessWindowStyle.Hidden, observed.WindowStyle);
		Assert.EndsWith(
			@"System32\cmd.exe",
			observed.FileName,
			StringComparison.OrdinalIgnoreCase);
		Assert.StartsWith(
			"/d /e:on /v:off /q /c ",
			observed.Arguments,
			StringComparison.Ordinal);
		Assert.Contains("for /L", observed.Arguments);
		Assert.Contains("(1,0,2)", observed.Arguments);
		Assert.Contains("del /F /Q", observed.Arguments);
		Assert.Contains("rmdir", observed.Arguments);
		Assert.DoesNotContain(
			maintenanceRoot,
			observed.Arguments,
			StringComparison.OrdinalIgnoreCase);
		string removalRoot = Assert.IsType<string>(
			observed.Environment["AIUD_MAINTENANCE_ROOT"]);
		string removalUpdaterPath = Assert.IsType<string>(
			observed.Environment["AIUD_MAINTENANCE_EXECUTABLE"]);
		Assert.Equal(
			Path.GetFullPath(maintenanceRoot),
			removalRoot,
			ignoreCase: true);
		Assert.Equal(
			Environment.SystemDirectory,
			observed.WorkingDirectory,
			ignoreCase: true);
		Assert.True(
			ManagedInstallationPaths.TryGetMaintenanceUpdaterGenerationHash(
				Path.GetFileName(removalUpdaterPath),
				out string removalHash));
		Assert.Equal(GetSha256(GetUpdaterExecutablePath()), removalHash);
		Assert.Equal(
			Environment.SystemDirectory,
			observed.Environment["AIUD_SYSTEM_DIRECTORY"],
			ignoreCase: true);
		Assert.Equal(
			Path.Combine(Environment.SystemDirectory, "ping.exe"),
			observed.Environment["AIUD_SYSTEM_PING"],
			ignoreCase: true);
		Assert.Equal(
			Environment.SystemDirectory,
			requestedCurrentDirectory,
			ignoreCase: true);
		Assert.True(Directory.Exists(maintenanceRoot));
		Assert.False(File.Exists(updaterPath));
		Assert.True(File.Exists(removalUpdaterPath));
	}

	[Fact]
	public async Task Cleanup_WhenReplacementAppears_DoesNotDeleteReplacement()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string updaterPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(GetUpdaterExecutablePath(), updaterPath);
		Process? childProcess = null;
		string? removalUpdaterPath = null;
		string replacementCanary = Path.Combine(maintenanceRoot, "replacement.txt");
		MaintenanceUpdaterCleanup cleanup = new(startInfo =>
		{
			removalUpdaterPath =
				startInfo.Environment["AIUD_MAINTENANCE_EXECUTABLE"];
			File.Copy(GetUpdaterExecutablePath(), updaterPath);
			File.WriteAllText(replacementCanary, "keep");
			childProcess = Process.Start(startInfo);
			return Process.GetCurrentProcess();
		});

		MaintenanceUpdaterCleanupResult result = cleanup.Cleanup(
			maintenanceRoot,
			updaterPath);

		Assert.True(result.IsCompleteOrScheduled);
		Assert.NotNull(childProcess);
		Assert.NotNull(removalUpdaterPath);
		using (childProcess)
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
			await childProcess.WaitForExitAsync(timeout.Token);
		}

		Assert.True(Directory.Exists(maintenanceRoot));
		Assert.False(File.Exists(removalUpdaterPath));
		Assert.True(File.Exists(updaterPath));
		Assert.True(File.Exists(replacementCanary));
	}

	[Fact]
	public void Cleanup_WithVerifiedGeneration_DeletesAllOwnedUpdaterFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string sourceUpdater = GetUpdaterExecutablePath();
		string stableUpdater = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(sourceUpdater, stableUpdater);
		string sourceHash = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				File.ReadAllBytes(sourceUpdater))).ToLowerInvariant();
		string generationUpdater =
			ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				sourceHash);
		File.Copy(sourceUpdater, generationUpdater);
		MaintenanceUpdaterCleanup cleanup = new();

		MaintenanceUpdaterCleanupResult result = cleanup.Cleanup(
			maintenanceRoot,
			Path.Combine(temporaryDirectory.Path, "external", "Updater.exe"));

		Assert.True(result.IsCompleteOrScheduled);
		Assert.False(Directory.Exists(maintenanceRoot));
	}

	[Fact]
	public async Task SelfCleanup_WhenExecutableUnlocks_RemovesExactOwnedRoot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance space & percent% bang! caret^ (test)");
		string updaterPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(GetUpdaterExecutablePath(), updaterPath);
		ProcessStartInfo startInfo =
			MaintenanceUpdaterCleanup.CreateSelfCleanupStartInfo(
				maintenanceRoot,
				updaterPath);
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		using FileStream executableLock = new(
			updaterPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException("Cleanup process did not start.");
		Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
		Task<string> standardError = process.StandardError.ReadToEndAsync();
		await Task.Delay(TimeSpan.FromMilliseconds(250));
		Assert.True(File.Exists(updaterPath));
		executableLock.Dispose();
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
		await process.WaitForExitAsync(timeout.Token);
		string output = await standardOutput;
		string error = await standardError;

		Assert.True(
			process.ExitCode == 0,
			$"Cleanup exit code: {process.ExitCode}{Environment.NewLine}" +
				$"Root exists: {Directory.Exists(maintenanceRoot)}" +
				$"{Environment.NewLine}stdout: {output}" +
				$"{Environment.NewLine}stderr: {error}");
		Assert.False(
			Directory.Exists(maintenanceRoot),
			$"stdout: {output}{Environment.NewLine}stderr: {error}");
	}

	[Fact]
	public void CreateSelfCleanupStartInfo_WithUnexpectedExecutable_Throws()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(temporaryDirectory.Path, "maintenance");
		string updaterPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		File.Copy(GetUpdaterExecutablePath(), updaterPath);

		Assert.Throws<InvalidDataException>(() =>
			MaintenanceUpdaterCleanup.CreateSelfCleanupStartInfo(
				maintenanceRoot,
				Path.Combine(maintenanceRoot, "unexpected.exe")));
	}

	private static string GetUpdaterExecutablePath()
	{
		string path = Path.Combine(
			AppContext.BaseDirectory,
			ManagedInstallationPaths.MaintenanceUpdaterFileName);
		Assert.True(File.Exists(path), $"Updater test executable missing: {path}");
		return path;
	}

	private static string GetSha256(string path)
	{
		return Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				File.ReadAllBytes(path))).ToLowerInvariant();
	}
}
