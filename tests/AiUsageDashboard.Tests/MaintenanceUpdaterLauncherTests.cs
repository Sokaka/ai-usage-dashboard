using System.ComponentModel;
using System.Diagnostics;

using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class MaintenanceUpdaterLauncherTests
{
	[Fact]
	public void CreateStartInfo_UsesVisibleNoArgumentLaunchFromUpdaterDirectory()
	{
		string executablePath = Path.Combine(
			Path.GetTempPath(),
			"maintenance-updater",
			"AiUsageDashboard.Updater.exe");

		ProcessStartInfo startInfo =
			MaintenanceUpdaterLauncher.CreateStartInfo(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), startInfo.FileName);
		Assert.Equal(
			Path.GetDirectoryName(Path.GetFullPath(executablePath)),
			startInfo.WorkingDirectory);
		Assert.Empty(startInfo.ArgumentList);
		Assert.Empty(startInfo.Arguments);
		Assert.True(startInfo.UseShellExecute);
		Assert.False(startInfo.CreateNoWindow);
		Assert.Equal(ProcessWindowStyle.Normal, startInfo.WindowStyle);
	}

	[Fact]
	public async Task LaunchAsync_WhenInstallationIsNotCanonical_DoesNotStart()
	{
		foreach (AppInstallationKind installationKind in new[]
			{
				AppInstallationKind.CustomManaged,
				AppInstallationKind.Unmanaged
			})
		{
			int startCount = 0;
			MaintenanceUpdaterLauncher launcher = new(
				() => throw new InvalidOperationException("Path must not be read."),
				_ =>
				{
					startCount++;
					return Task.FromResult(0);
				});

			MaintenanceUpdaterLaunchResult result = await launcher.LaunchAsync(
				CreateContext(installationKind),
				isShutdownChannelReady: true);

			Assert.False(result.WasStarted);
			Assert.Equal(
				MaintenanceUpdaterLaunchFailure.NotCanonicalInstallation,
				result.Failure);
			Assert.Equal(0, startCount);
		}
	}

	[Fact]
	public async Task CanLaunch_RequiresCanonicalContextReadyChannelAndExecutable()
	{
		string testDirectory = CreateTestDirectory();
		string executablePath = Path.Combine(
			testDirectory,
			"AiUsageDashboard.Updater.exe");
		await File.WriteAllBytesAsync(executablePath, [0]);
		MaintenanceUpdaterLauncher launcher = new(
			() => executablePath,
			_ => throw new InvalidOperationException(
				"Capability check must not start a process."));

		try
		{
			Assert.True(launcher.CanLaunch(
				CreateContext(AppInstallationKind.CanonicalManaged),
				isShutdownChannelReady: true));
			Assert.False(launcher.CanLaunch(
				CreateContext(AppInstallationKind.CustomManaged),
				isShutdownChannelReady: true));
			Assert.False(launcher.CanLaunch(
				CreateContext(AppInstallationKind.CanonicalManaged),
				isShutdownChannelReady: false));
		}
		finally
		{
			Directory.Delete(testDirectory, recursive: true);
		}
	}

	[Fact]
	public void CanLaunch_WhenCanonicalPathIsUnavailable_FailsClosed()
	{
		MaintenanceUpdaterLauncher launcher = new(
			() => throw new InvalidOperationException(
				"Synthetic unavailable LocalAppData path."),
			_ => throw new InvalidOperationException(
				"Capability check must not start a process."));

		Assert.False(launcher.CanLaunch(
			CreateContext(AppInstallationKind.CanonicalManaged),
			isShutdownChannelReady: true));
	}

	[Fact]
	public async Task LaunchAsync_WhenShutdownChannelIsUnavailable_DoesNotStart()
	{
		int startCount = 0;
		MaintenanceUpdaterLauncher launcher = new(
			() => throw new InvalidOperationException("Path must not be read."),
			_ =>
			{
				startCount++;
				return Task.FromResult(0);
			});

		MaintenanceUpdaterLaunchResult result = await launcher.LaunchAsync(
			CreateContext(AppInstallationKind.CanonicalManaged),
			isShutdownChannelReady: false);

		Assert.False(result.WasStarted);
		Assert.Equal(
			MaintenanceUpdaterLaunchFailure.ShutdownChannelUnavailable,
			result.Failure);
		Assert.Equal(0, startCount);
	}

	[Fact]
	public async Task LaunchAsync_WhenExecutableIsMissing_ReturnsFallbackFailure()
	{
		string missingPath = Path.Combine(
			Path.GetTempPath(),
			Guid.NewGuid().ToString("N"),
			"AiUsageDashboard.Updater.exe");
		MaintenanceUpdaterLauncher launcher = new(
			() => missingPath,
			_ => throw new InvalidOperationException("Runner must not be called."));

		MaintenanceUpdaterLaunchResult result = await launcher.LaunchAsync(
			CreateContext(AppInstallationKind.CanonicalManaged),
			isShutdownChannelReady: true);

		Assert.False(result.WasStarted);
		Assert.Equal(
			MaintenanceUpdaterLaunchFailure.ExecutableUnavailable,
			result.Failure);
	}

	[Fact]
	public async Task LaunchAsync_WhenAlreadyRunning_StartsOnlyOneProcess()
	{
		string testDirectory = CreateTestDirectory();
		string executablePath = Path.Combine(
			testDirectory,
			"AiUsageDashboard.Updater.exe");
		await File.WriteAllBytesAsync(executablePath, [0]);
		TaskCompletionSource processStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<int> allowExit = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int startCount = 0;
		MaintenanceUpdaterLauncher launcher = new(
			() => executablePath,
			_ =>
			{
				Interlocked.Increment(ref startCount);
				processStarted.TrySetResult();
				return allowExit.Task;
			});

		try
		{
			Task<MaintenanceUpdaterLaunchResult> firstLaunch =
				launcher.LaunchAsync(
					CreateContext(AppInstallationKind.CanonicalManaged),
					isShutdownChannelReady: true);
			await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

			MaintenanceUpdaterLaunchResult secondResult =
				await launcher.LaunchAsync(
					CreateContext(AppInstallationKind.CanonicalManaged),
					isShutdownChannelReady: true);

			Assert.Equal(
				MaintenanceUpdaterLaunchFailure.AlreadyRunning,
				secondResult.Failure);
			Assert.Equal(1, Volatile.Read(ref startCount));

			allowExit.TrySetResult(0);
			MaintenanceUpdaterLaunchResult firstResult = await firstLaunch;
			Assert.True(firstResult.WasStarted);
			Assert.Equal(0, firstResult.ExitCode);
			Assert.False(launcher.IsRunning);
		}
		finally
		{
			allowExit.TrySetResult(1);
			Directory.Delete(testDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task LaunchAsync_WhenRunnerThrows_PreservesOriginalFailure()
	{
		string testDirectory = CreateTestDirectory();
		string executablePath = Path.Combine(
			testDirectory,
			"AiUsageDashboard.Updater.exe");
		await File.WriteAllBytesAsync(executablePath, [0]);
		Win32Exception expectedException = new(5, "start rejected");
		MaintenanceUpdaterLauncher launcher = new(
			() => executablePath,
			_ => throw expectedException);

		try
		{
			MaintenanceUpdaterLaunchResult result = await launcher.LaunchAsync(
				CreateContext(AppInstallationKind.CanonicalManaged),
				isShutdownChannelReady: true);

			Assert.False(result.WasStarted);
			Assert.Equal(
				MaintenanceUpdaterLaunchFailure.StartFailed,
				result.Failure);
			Assert.Same(expectedException, result.Exception);
			Assert.False(launcher.IsRunning);
		}
		finally
		{
			Directory.Delete(testDirectory, recursive: true);
		}
	}

	private static AppInstallationContext CreateContext(
		AppInstallationKind kind)
	{
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.0.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			ArchiveSizeBytes: 1,
			ArchiveSha256: new string('a', 64),
			SourceRevision: "source",
			ReleaseSequence: 1017);
		return new AppInstallationContext(
			kind,
			ExecutablePath: "C:\\app\\AiUsageDashboard.App.exe",
			InstallRoot: "C:\\app",
			ManifestPath: "C:\\app\\current\\update-manifest.json",
			manifest);
	}

	private static string CreateTestDirectory()
	{
		string path = Path.Combine(
			Path.GetTempPath(),
			$"ai-usage-maintenance-launcher-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
	}
}
