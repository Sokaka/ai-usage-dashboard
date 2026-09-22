using System.Diagnostics;
using System.Security.Cryptography;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class DelegatedMaintenanceUpdaterPromotionTests
{
	[Fact]
	public async Task MatchingLegacyDelegationShape_SchedulesGenerationAgainstExactCanonicalParent()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();

		DelegatedMaintenanceUpdaterPromotionContext? context =
			await scenario.Coordinator.CaptureAsync(
				scenario.CurrentUpdater,
				scenario.Options);
		DelegatedMaintenanceUpdaterPromotionState state =
			await scenario.Coordinator.ScheduleAsync(
			Assert.IsType<DelegatedMaintenanceUpdaterPromotionContext>(context),
			scenario.CurrentUpdater,
			scenario.AvailableUpdater,
			scenario.Options);

		Assert.Equal(
			DelegatedMaintenanceUpdaterPromotionState.PendingForExactParent,
			state);
		MaintenanceUpdaterPromotionOptions observed = Assert.IsType<
			MaintenanceUpdaterPromotionOptions>(scenario.Launcher.ObservedOptions);
		Assert.Equal(scenario.CurrentUpdater.Sha256, observed.SourceSha256);
		Assert.Equal(scenario.CanonicalHash, observed.ExpectedCanonicalSha256);
		Assert.Equal(scenario.ParentIdentity, observed.ParentIdentity);
	}

	[Fact]
	public async Task ScheduleAsync_WhenLauncherReturnsWithoutReceipt_FailsClosed()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();
		scenario.Launcher.ShouldSavePendingReceipt = false;
		DelegatedMaintenanceUpdaterPromotionContext context = Assert.IsType<
			DelegatedMaintenanceUpdaterPromotionContext>(
				await scenario.Coordinator.CaptureAsync(
					scenario.CurrentUpdater,
					scenario.Options));

		InvalidOperationException exception = await Assert.ThrowsAsync<
			InvalidOperationException>(() => scenario.Coordinator.ScheduleAsync(
				context,
				scenario.CurrentUpdater,
				scenario.AvailableUpdater,
				scenario.Options));

		Assert.Contains("without a durable pending receipt", exception.Message);
		MaintenanceUpdaterPromotionReceipt receipt = Assert.IsType<
			MaintenanceUpdaterPromotionReceipt>(
				await new MaintenanceUpdaterPromotionReceiptStore(
					scenario.Options.MaintenanceRoot).LoadAsync());
		Assert.Equal(
			MaintenanceUpdaterPromotionReceiptStore.FailedStatus,
			receipt.Status);
		Assert.Equal(scenario.CurrentUpdater.Sha256, receipt.SourceSha256);
		Assert.Equal(scenario.CanonicalHash, receipt.ExpectedCanonicalSha256);
		Assert.Equal(scenario.ParentIdentity.ProcessId, receipt.ParentProcessId);
		Assert.Equal(
			scenario.ParentIdentity.ProcessStartTimeUtcTicks,
			receipt.ParentProcessStartTimeUtcTicks);
	}

	[Fact]
	public async Task ScheduleAsync_WhenCanonicalBecameCurrent_ReturnsCurrentState()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();
		DelegatedMaintenanceUpdaterPromotionContext context = Assert.IsType<
			DelegatedMaintenanceUpdaterPromotionContext>(
				await scenario.Coordinator.CaptureAsync(
					scenario.CurrentUpdater,
					scenario.Options));
		File.Copy(
			scenario.CurrentUpdater.ExecutablePath,
			scenario.CanonicalPath,
			overwrite: true);
		scenario.Launcher.ShouldSavePendingReceipt = false;
		scenario.Launcher.LaunchResult = false;

		DelegatedMaintenanceUpdaterPromotionState state =
			await scenario.Coordinator.ScheduleAsync(
				context,
				scenario.CurrentUpdater,
				scenario.AvailableUpdater,
				scenario.Options);

		Assert.Equal(
			DelegatedMaintenanceUpdaterPromotionState.CanonicalCurrent,
			state);
		Assert.Null(await new MaintenanceUpdaterPromotionReceiptStore(
			scenario.Options.MaintenanceRoot).LoadAsync());
	}

	[Fact]
	public async Task NonCanonicalParent_WhenRegistrationReplacesCanonical_DoesNotRequirePromotion()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync(
			parentPathOverride: @"C:\Windows\explorer.exe");

		DelegatedMaintenanceUpdaterPromotionContext? context =
			await scenario.Coordinator.CaptureAsync(
				scenario.CurrentUpdater,
				scenario.Options);
		Assert.False(await scenario.Coordinator.IsCanonicalUpdaterCurrentAsync(
			scenario.CurrentUpdater,
			scenario.AvailableUpdater,
			scenario.Options));
		File.Copy(
			scenario.CurrentUpdater.ExecutablePath,
			scenario.CanonicalPath,
			overwrite: true);
		bool isCanonicalCurrent =
			await scenario.Coordinator.IsCanonicalUpdaterCurrentAsync(
				scenario.CurrentUpdater,
				scenario.AvailableUpdater,
				scenario.Options);

		Assert.Null(context);
		Assert.True(isCanonicalCurrent);
		Assert.Null(scenario.Launcher.ObservedOptions);
	}

	[Fact]
	public async Task ScheduleAsync_WhenSignedIdentityDiffers_DoesNotLaunch()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();
		DelegatedMaintenanceUpdaterPromotionContext context = Assert.IsType<
			DelegatedMaintenanceUpdaterPromotionContext>(
				await scenario.Coordinator.CaptureAsync(
					scenario.CurrentUpdater,
					scenario.Options));
		UpdateReleaseArtifact changedArtifact = scenario.AvailableUpdater with
		{
			SizeBytes = scenario.AvailableUpdater.SizeBytes + 1
		};

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			scenario.Coordinator.ScheduleAsync(
				context,
				scenario.CurrentUpdater,
				changedArtifact,
				scenario.Options));

		Assert.Null(scenario.Launcher.ObservedOptions);
	}

	[Fact]
	public async Task ScheduleAsync_WhenUpdaterIsOutsideSignedCache_FailsClosed()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();
		DelegatedMaintenanceUpdaterPromotionContext context = Assert.IsType<
			DelegatedMaintenanceUpdaterPromotionContext>(
				await scenario.Coordinator.CaptureAsync(
					scenario.CurrentUpdater,
					scenario.Options));
		CurrentUpdaterIdentity unexpectedPath = scenario.CurrentUpdater with
		{
			ExecutablePath = GetUpdaterExecutablePath()
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<
			InvalidOperationException>(() => scenario.Coordinator.ScheduleAsync(
				context,
				unexpectedPath,
				scenario.AvailableUpdater,
				scenario.Options));

		Assert.Contains("is not running from the signed cache path", exception.Message);
		Assert.Null(scenario.Launcher.ObservedOptions);
	}

	[Fact]
	public async Task ScheduleAsync_WhenVerifiedGenerationIsMissing_DoesNotLaunch()
	{
		using PromotionScenario scenario = await PromotionScenario.CreateAsync();
		DelegatedMaintenanceUpdaterPromotionContext context = Assert.IsType<
			DelegatedMaintenanceUpdaterPromotionContext>(
				await scenario.Coordinator.CaptureAsync(
					scenario.CurrentUpdater,
					scenario.Options));
		File.Delete(scenario.GenerationPath);

		await Assert.ThrowsAsync<FileNotFoundException>(() =>
			scenario.Coordinator.ScheduleAsync(
				context,
				scenario.CurrentUpdater,
				scenario.AvailableUpdater,
				scenario.Options));

		Assert.Null(scenario.Launcher.ObservedOptions);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public void SystemProcessLineageProbe_CapturesCurrentProcessAndDirectParent()
	{
		ProcessLineage lineage = new SystemProcessLineageProbe().CaptureCurrent();
		using Process currentProcess = Process.GetCurrentProcess();

		Assert.Equal(Environment.ProcessId, lineage.CurrentIdentity.ProcessId);
		Assert.Equal(
			Path.GetFullPath(Environment.ProcessPath!),
			lineage.CurrentExecutablePath,
			ignoreCase: true);
		Assert.Equal(
			currentProcess.StartTime.ToUniversalTime().Ticks,
			lineage.CurrentIdentity.ProcessStartTimeUtcTicks);
		Assert.True(lineage.ParentIdentity.ProcessId > 0);
		Assert.True(lineage.ParentIdentity.ProcessStartTimeUtcTicks > 0);
		Assert.True(Path.IsPathFullyQualified(lineage.ParentExecutablePath));
	}

	private static string GetSha256(string path)
	{
		return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
			.ToLowerInvariant();
	}

	private static string GetUpdaterExecutablePath()
	{
		string path = Path.Combine(
			AppContext.BaseDirectory,
			ManagedInstallationPaths.MaintenanceUpdaterFileName);
		Assert.True(File.Exists(path), $"Updater test executable missing: {path}");
		return path;
	}

	private sealed class FakeProcessLineageProbe : IProcessLineageProbe
	{
		private readonly ProcessLineage _lineage;

		internal FakeProcessLineageProbe(ProcessLineage lineage)
		{
			_lineage = lineage;
		}

		public ProcessLineage CaptureCurrent()
		{
			return _lineage;
		}
	}

	private sealed class FakePromotionLauncher :
		IMaintenanceUpdaterPromotionLauncher
	{
		internal bool LaunchResult { get; set; } = true;

		internal bool ShouldSavePendingReceipt { get; set; } = true;

		internal MaintenanceUpdaterPromotionOptions? ObservedOptions
		{
			get;
			private set;
		}

		public async Task<bool> LaunchAsync(
			MaintenanceUpdaterPromotionOptions options,
			CancellationToken cancellationToken = default)
		{
			ObservedOptions = options;

			if (ShouldSavePendingReceipt)
			{
				await new MaintenanceUpdaterPromotionReceiptStore(
					options.MaintenanceRoot).SavePendingAsync(
						options,
						cancellationToken);
			}

			return LaunchResult;
		}
	}

	private sealed record PromotionScenario(
		TemporaryDirectory TemporaryDirectory,
		UpdaterCommandLineOptions Options,
		CurrentUpdaterIdentity CurrentUpdater,
		UpdateReleaseArtifact AvailableUpdater,
		string CanonicalHash,
		string CanonicalPath,
		string GenerationPath,
		ExactProcessIdentity ParentIdentity,
		FakePromotionLauncher Launcher,
		DelegatedMaintenanceUpdaterPromotionCoordinator Coordinator) : IDisposable
	{
		internal static async Task<PromotionScenario> CreateAsync(
			string? parentPathOverride = null)
		{
			TemporaryDirectory temporaryDirectory = new();
			string installRoot = Path.Combine(temporaryDirectory.Path, "install");
			string maintenanceRoot = Path.Combine(
				temporaryDirectory.Path,
				"maintenance");
			string userDataRoot = Path.Combine(temporaryDirectory.Path, "data");
			Directory.CreateDirectory(installRoot);
			Directory.CreateDirectory(maintenanceRoot);
			string sourceUpdater = GetUpdaterExecutablePath();
			CurrentUpdaterIdentity sourceIdentity =
				await CurrentUpdaterIdentity.ReadAsync(sourceUpdater);
			UpdateReleaseArtifact artifact = new(
				"AiUsageDashboard.Updater",
				sourceIdentity.Version,
				"win-x64",
				$"AiUsageDashboard-Updater-{sourceIdentity.Version}-win-x64.exe",
				$"https://downloads.example.test/updater-{sourceIdentity.Version}.exe",
				sourceIdentity.SizeBytes,
				sourceIdentity.Sha256,
				"0123456789abcdef0123456789abcdef01234567");
			string cachePath = UpdaterArtifactCache.GetExecutablePath(
				artifact,
				installRoot);
			Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
			File.Copy(sourceUpdater, cachePath);
			CurrentUpdaterIdentity currentUpdater =
				await CurrentUpdaterIdentity.ReadAsync(cachePath);
			string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
				maintenanceRoot);
			File.Copy(sourceUpdater, canonicalPath);
			await File.AppendAllTextAsync(canonicalPath, "legacy-canonical");
			string canonicalHash = GetSha256(canonicalPath);
			string generationPath = ManagedInstallationPaths
				.GetMaintenanceUpdaterGeneration(
					maintenanceRoot,
					currentUpdater.Sha256);
			File.Copy(sourceUpdater, generationPath);
			ExactProcessIdentity parentIdentity = new(1234, 5678);
			ProcessLineage lineage = new(
				cachePath,
				new ExactProcessIdentity(2345, 6789),
				parentPathOverride ?? canonicalPath,
				parentIdentity);
			UpdaterCommandLineOptions options = new(
				UpdaterCommand.UpdateOnline,
				PackagePath: null,
				Sha256Path: null,
				new Uri("https://updates.example.test/feed.json"),
				"stable",
				installRoot,
				maintenanceRoot,
				userDataRoot,
				ShouldRestart: true,
				ShouldRefreshUpdater: false,
				ShouldRegisterInstalledApp: true,
				IsUninstallConfirmed: false,
				ShouldNotifyUser: false);
			FakePromotionLauncher launcher = new();
			DelegatedMaintenanceUpdaterPromotionCoordinator coordinator = new(
				new FakeProcessLineageProbe(lineage),
				launcher);
			return new PromotionScenario(
				temporaryDirectory,
				options,
				currentUpdater,
				artifact,
				canonicalHash,
				canonicalPath,
				generationPath,
				parentIdentity,
				launcher,
				coordinator);
		}

		public void Dispose()
		{
			TemporaryDirectory.Dispose();
		}
	}
}
