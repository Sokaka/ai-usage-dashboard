using System.Diagnostics;
using System.Text.Json;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.UpdaterProcessFixture;

internal static class Program
{
	private const string DelegatedChildObservationFileName =
		"delegated-child.json";
	private const string ParentCommand = "fixture-parent";
	private const string ParentObservationFileName = "parent.json";
	private static readonly TimeSpan FixtureTimeout = TimeSpan.FromMinutes(1);

	public static async Task<int> Main(string[] arguments)
	{
		try
		{
			FixtureConfiguration configuration =
				await FixtureConfiguration.LoadAsync();

			if (MaintenanceUpdaterPromotionCommandLine.IsCommand(arguments))
			{
				return await RunPromoterAsync(configuration, arguments);
			}

			if (arguments.SequenceEqual([ParentCommand]))
			{
				return await RunParentAsync(configuration);
			}

			if ((arguments.Length > 0) &&
				string.Equals(arguments[0], "update-online", StringComparison.Ordinal))
			{
				return await RunDelegatedChildAsync(configuration, arguments);
			}

			throw new ArgumentException("The updater process fixture command is invalid.");
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);
			return 1;
		}
	}

	private static async Task<int> RunParentAsync(
		FixtureConfiguration configuration)
	{
		using EventWaitHandle childCompleted = EventWaitHandle.OpenExisting(
			configuration.ChildCompletedEventName);
		using EventWaitHandle parentRelease = EventWaitHandle.OpenExisting(
			configuration.ParentReleaseEventName);
		ProcessStartInfo startInfo = new()
		{
			FileName = configuration.TargetUpdaterPath,
			WorkingDirectory = Path.GetDirectoryName(
				configuration.TargetUpdaterPath) ?? throw new InvalidOperationException(
					"The target updater fixture has no working directory."),
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("update-online");
		startInfo.ArgumentList.Add("--feed-url");
		startInfo.ArgumentList.Add("https://updates.example.test/feed.json");
		startInfo.ArgumentList.Add("--install-root");
		startInfo.ArgumentList.Add(
			WindowsLogonStartupRegistrationContract.GetCanonicalInstallRoot(
				configuration.LocalApplicationDataDirectory));
		startInfo.ArgumentList.Add("--prompt-for-licenses");
		startInfo.ArgumentList.Add("--skip-updater-refresh");

		using Process child = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"Windows did not start the delegated updater fixture.");
		long childStartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks;
		await child.WaitForExitAsync().WaitAsync(FixtureTimeout);
		using Process current = Process.GetCurrentProcess();
		ParentObservation observation = new(
			Environment.ProcessId,
			current.StartTime.ToUniversalTime().Ticks,
			Path.GetFullPath(Environment.ProcessPath ??
				throw new InvalidOperationException(
					"The parent fixture executable path is unavailable.")),
			child.Id,
			childStartTimeUtcTicks,
			child.ExitCode);
		await WriteObservationAsync(
			Path.Combine(
				configuration.ObservationDirectory,
				ParentObservationFileName),
			observation);
		childCompleted.Set();

		if (!parentRelease.WaitOne(FixtureTimeout))
		{
			throw new TimeoutException(
				"The updater process fixture parent release was not signaled.");
		}

		return child.ExitCode;
	}

	private static async Task<int> RunDelegatedChildAsync(
		FixtureConfiguration configuration,
		IReadOnlyList<string> arguments)
	{
		UpdaterCommandLineOptions options = UpdaterCommandLine.Parse(
			arguments,
			configuration.LocalApplicationDataDirectory,
			Environment.CurrentDirectory,
			new Uri("https://updates.example.test/default-feed.json"),
			"stable");
		string runningPath = Path.GetFullPath(Environment.ProcessPath ??
			throw new InvalidOperationException(
				"The delegated child fixture executable path is unavailable."));
		CurrentUpdaterIdentity currentUpdater =
			await CurrentUpdaterIdentity.ReadAsync(runningPath);
		UpdateReleaseArtifact availableUpdater = new(
			"AiUsageDashboard.Updater",
			currentUpdater.Version,
			"win-x64",
			Path.GetFileName(runningPath),
			"https://updates.example.test/updater.exe",
			currentUpdater.SizeBytes,
			currentUpdater.Sha256,
			"fixture");
		RecordingProcessLineageProbe lineageProbe = new();
		DelegatedMaintenanceUpdaterPromotionCoordinator coordinator = new(
			lineageProbe,
			new MaintenanceUpdaterPromotionLauncher());
		DelegatedMaintenanceUpdaterPromotionContext context =
			await coordinator.CaptureAsync(currentUpdater, options) ??
			throw new InvalidOperationException(
				"The delegated updater fixture did not capture promotion context.");
		DelegatedMaintenanceUpdaterPromotionState promotionState =
			await coordinator.ScheduleAsync(
			context,
			currentUpdater,
			availableUpdater,
			options);

		if (promotionState !=
			DelegatedMaintenanceUpdaterPromotionState.PendingForExactParent)
		{
			throw new InvalidOperationException(
				$"The delegated updater fixture observed promotion state " +
					$"'{promotionState}'.");
		}

		ProcessLineage lineage = lineageProbe.ObservedLineage ??
			throw new InvalidOperationException(
				"The delegated updater fixture did not observe process lineage.");
		DelegatedChildObservation observation = new(
			lineage.CurrentIdentity.ProcessId,
			lineage.CurrentIdentity.ProcessStartTimeUtcTicks,
			lineage.CurrentExecutablePath,
			lineage.ParentIdentity.ProcessId,
			lineage.ParentIdentity.ProcessStartTimeUtcTicks,
			lineage.ParentExecutablePath,
			options.ShouldRefreshUpdater,
			options.ShouldRegisterInstalledApp,
			options.ShouldPromptForLicenses,
			WasPromotionScheduled: true,
			context.ParentIdentity.ProcessId,
			context.ParentIdentity.ProcessStartTimeUtcTicks,
			context.ExpectedCanonicalSha256,
			currentUpdater.Sha256);
		await WriteObservationAsync(
			Path.Combine(
				configuration.ObservationDirectory,
				DelegatedChildObservationFileName),
			observation);
		using EventWaitHandle childScheduled = EventWaitHandle.OpenExisting(
			configuration.ChildScheduledEventName);
		using EventWaitHandle childRelease = EventWaitHandle.OpenExisting(
			configuration.ChildReleaseEventName);
		childScheduled.Set();

		if (!childRelease.WaitOne(FixtureTimeout))
		{
			throw new TimeoutException(
				"The delegated updater fixture release was not signaled.");
		}

		return 0;
	}

	private static async Task<int> RunPromoterAsync(
		FixtureConfiguration configuration,
		IReadOnlyList<string> arguments)
	{
		using EventWaitHandle promoterStarted = EventWaitHandle.OpenExisting(
			configuration.PromoterStartedEventName);
		using Process current = Process.GetCurrentProcess();
		PromoterObservation observation = new(
			Environment.ProcessId,
			current.StartTime.ToUniversalTime().Ticks,
			Path.GetFullPath(Environment.ProcessPath ??
				throw new InvalidOperationException(
					"The promoter fixture executable path is unavailable.")),
			arguments.ToArray());
		await WriteObservationAsync(
			Path.Combine(
				configuration.ObservationDirectory,
				$"promoter-{Environment.ProcessId}.json"),
			observation);
		promoterStarted.Set();
		MaintenanceUpdaterPromotionOptions options =
			MaintenanceUpdaterPromotionCommandLine.Parse(
				arguments,
				configuration.LocalApplicationDataDirectory);
		await new MaintenanceUpdaterPromotion(
			new SystemExactProcessExitWaiter()).PromoteAndRecordAsync(
				options,
				observation.ExecutablePath);
		return 0;
	}

	private static async Task WriteObservationAsync<T>(
		string destinationPath,
		T observation)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ??
			throw new InvalidOperationException(
				"The fixture observation has no parent directory."));
		string temporaryPath = destinationPath + "." +
			Guid.NewGuid().ToString("N") + ".writing";

		try
		{
			await File.WriteAllBytesAsync(
				temporaryPath,
				JsonSerializer.SerializeToUtf8Bytes(observation));
			File.Move(temporaryPath, destinationPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private sealed class RecordingProcessLineageProbe : IProcessLineageProbe
	{
		internal ProcessLineage? ObservedLineage { get; private set; }

		public ProcessLineage CaptureCurrent()
		{
			ObservedLineage = new SystemProcessLineageProbe().CaptureCurrent();
			return ObservedLineage;
		}
	}
}
