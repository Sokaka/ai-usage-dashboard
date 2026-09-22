using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class DelegatedMaintenanceUpdaterPromotionProcessTests
{
	private const string FixtureAssemblyName =
		"AiUsageDashboard.UpdaterProcessFixture";
	private const string FixtureConfigurationEnvironmentVariable =
		"AI_USAGE_DASHBOARD_UPDATER_PROCESS_FIXTURE_CONFIG";
	private const string ParentCommand = "fixture-parent";
	private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task LegacyCanonicalParent_DelegatedTarget_PromotesAfterExactParentExits()
	{
		using UpdaterProcessTestDirectory temporaryDirectory = new();
		string localApplicationDataDirectory = temporaryDirectory.Path;
		string installRoot = WindowsLogonStartupRegistrationContract
			.GetCanonicalInstallRoot(localApplicationDataDirectory);
		string maintenanceRoot = MaintenanceUpdaterPathContract.GetCanonicalRoot(
			localApplicationDataDirectory);
		Directory.CreateDirectory(installRoot);
		Directory.CreateDirectory(maintenanceRoot);
		string fixtureExecutablePath = GetFixtureExecutablePath();
		CurrentUpdaterIdentity targetIdentity =
			await CurrentUpdaterIdentity.ReadAsync(fixtureExecutablePath);
		string targetFileName =
			$"AiUsageDashboard-Updater-{targetIdentity.Version}-win-x64.exe";
		UpdateReleaseArtifact targetArtifact = new(
			"AiUsageDashboard.Updater",
			targetIdentity.Version,
			"win-x64",
			targetFileName,
			$"https://updates.example.test/{targetFileName}",
			targetIdentity.SizeBytes,
			targetIdentity.Sha256,
			"fixture");
		string targetUpdaterPath = UpdaterArtifactCache.GetExecutablePath(
			targetArtifact,
			installRoot);
		CopyFixtureRuntime(fixtureExecutablePath, targetUpdaterPath);
		string generationPath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				targetIdentity.Sha256);
		CopyFixtureRuntime(fixtureExecutablePath, generationPath);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		CopyFixtureRuntime(fixtureExecutablePath, canonicalPath);
		await File.AppendAllTextAsync(
			canonicalPath,
			"legacy-canonical",
			Encoding.UTF8);
		string canonicalLegacySha256 = await GetSha256Async(canonicalPath);
		Assert.NotEqual(targetIdentity.Sha256, canonicalLegacySha256);
		MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
		MaintenanceUpdaterFile.EnsureCompatible(targetUpdaterPath);
		MaintenanceUpdaterFile.EnsureCompatible(generationPath);

		string observationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"observations");
		Directory.CreateDirectory(observationDirectory);
		string eventPrefix =
			$"Local\\AiUsageDashboard.UpdaterProcessFixture.{Guid.NewGuid():N}.";
		using EventWaitHandle childCompleted = new(
			initialState: false,
			EventResetMode.ManualReset,
			eventPrefix + "ChildCompleted");
		using EventWaitHandle childScheduled = new(
			initialState: false,
			EventResetMode.ManualReset,
			eventPrefix + "ChildScheduled");
		using EventWaitHandle childRelease = new(
			initialState: false,
			EventResetMode.ManualReset,
			eventPrefix + "ChildRelease");
		using EventWaitHandle parentRelease = new(
			initialState: false,
			EventResetMode.ManualReset,
			eventPrefix + "ParentRelease");
		using EventWaitHandle promoterStarted = new(
			initialState: false,
			EventResetMode.ManualReset,
			eventPrefix + "PromoterStarted");
		string configurationPath = Path.Combine(
			temporaryDirectory.Path,
			"fixture-config.json");
		await File.WriteAllBytesAsync(
			configurationPath,
			JsonSerializer.SerializeToUtf8Bytes(new
			{
				LocalApplicationDataDirectory = localApplicationDataDirectory,
				TargetUpdaterPath = targetUpdaterPath,
				ObservationDirectory = observationDirectory,
				ChildScheduledEventName = eventPrefix + "ChildScheduled",
				ChildReleaseEventName = eventPrefix + "ChildRelease",
				ChildCompletedEventName = eventPrefix + "ChildCompleted",
				ParentReleaseEventName = eventPrefix + "ParentRelease",
				PromoterStartedEventName = eventPrefix + "PromoterStarted"
			}));
		ProcessStartInfo parentStartInfo = new()
		{
			FileName = canonicalPath,
			WorkingDirectory = maintenanceRoot,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		parentStartInfo.ArgumentList.Add(ParentCommand);
		parentStartInfo.Environment[FixtureConfigurationEnvironmentVariable] =
			configurationPath;
		WindowsJobContainedProcess? containedParent = null;
		Process? parent = null;
		Process? promoter = null;
		StreamReader? standardOutputReader = null;
		StreamReader? standardErrorReader = null;
		Task<string>? standardOutput = null;
		Task<string>? standardError = null;

		try
		{
			containedParent = WindowsJobContainedProcess.Start(
				parentStartInfo,
				static () => { });
			standardOutputReader = new StreamReader(
				containedParent.StandardOutput,
				leaveOpen: true);
			standardErrorReader = new StreamReader(
				containedParent.StandardError,
				leaveOpen: true);
			standardOutput = standardOutputReader.ReadToEndAsync();
			standardError = standardErrorReader.ReadToEndAsync();

			Assert.True(
				childScheduled.WaitOne(ProcessTimeout),
				"The delegated updater fixture did not schedule promotion within the timeout.");
			Assert.True(
				promoterStarted.WaitOne(ProcessTimeout),
				"The maintenance updater promoter did not start within the timeout.");

			DelegatedChildObservation childObservation =
				await ReadObservationAsync<DelegatedChildObservation>(Path.Combine(
					observationDirectory,
					"delegated-child.json"));
			parent = Process.GetProcessById(childObservation.ParentProcessId);
			_ = parent.SafeHandle;
			long parentStartTimeUtcTicks =
				parent.StartTime.ToUniversalTime().Ticks;
			Assert.False(parent.HasExited);
			string[] promoterObservationPaths = Directory.GetFiles(
				observationDirectory,
				"promoter-*.json",
				SearchOption.TopDirectoryOnly);
			string promoterObservationPath = Assert.Single(
				promoterObservationPaths);
			PromoterObservation promoterObservation =
				await ReadObservationAsync<PromoterObservation>(
					promoterObservationPath);
			promoter = Process.GetProcessById(promoterObservation.ProcessId);
			_ = promoter.SafeHandle;

			Assert.Equal(targetUpdaterPath, childObservation.ExecutablePath, true);
			Assert.Equal(parent.Id, childObservation.ParentProcessId);
			Assert.Equal(
				parentStartTimeUtcTicks,
				childObservation.ParentProcessStartTimeUtcTicks);
			Assert.Equal(
				canonicalPath,
				childObservation.ParentExecutablePath,
				true);
			Assert.False(childObservation.ShouldRefreshUpdater);
			Assert.True(childObservation.ShouldRegisterInstalledApp);
			Assert.True(childObservation.ShouldPromptForLicenses);
			Assert.True(childObservation.WasPromotionScheduled);
			Assert.Equal(parent.Id, childObservation.PromotionParentProcessId);
			Assert.Equal(
				parentStartTimeUtcTicks,
				childObservation.PromotionParentProcessStartTimeUtcTicks);
			Assert.Equal(
				canonicalLegacySha256,
				childObservation.ExpectedCanonicalSha256);
			Assert.Equal(
				targetIdentity.Sha256,
				childObservation.SourceSha256);
			Assert.True(IsExactProcessRunning(
				childObservation.ProcessId,
				childObservation.ProcessStartTimeUtcTicks));

			Assert.Equal(
				generationPath,
				promoterObservation.ExecutablePath,
				true);
			Assert.Equal(
				promoterObservation.ProcessStartTimeUtcTicks,
				promoter.StartTime.ToUniversalTime().Ticks);
			Assert.False(promoter.HasExited);
			Assert.Equal(
				[
					MaintenanceUpdaterPromotionCommandLine.Command,
					"--source-sha256",
					targetIdentity.Sha256,
					"--expected-canonical-sha256",
					canonicalLegacySha256,
					"--parent-process-id",
					parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
					"--parent-process-start-time-utc-ticks",
					parentStartTimeUtcTicks.ToString(
						System.Globalization.CultureInfo.InvariantCulture)
				],
				promoterObservation.Arguments);

			Assert.Equal(
				canonicalLegacySha256,
				await GetSha256Async(canonicalPath));
			Assert.Equal(
				targetIdentity.Sha256,
				await GetSha256Async(generationPath));
			MaintenanceUpdaterPromotionReceipt receipt = Assert.IsType<
				MaintenanceUpdaterPromotionReceipt>(
					await new MaintenanceUpdaterPromotionReceiptStore(
						maintenanceRoot).LoadAsync());
			Assert.Equal(1, receipt.SchemaVersion);
			Assert.Equal(targetIdentity.Sha256, receipt.SourceSha256);
			Assert.Equal(
				canonicalLegacySha256,
				receipt.ExpectedCanonicalSha256);
			Assert.Equal(parent.Id, receipt.ParentProcessId);
			Assert.Equal(
				parentStartTimeUtcTicks,
				receipt.ParentProcessStartTimeUtcTicks);
			Assert.Equal("pending", receipt.Status);
			Assert.Null(receipt.FailureType);
			Assert.Null(receipt.FailureMessage);
			Assert.True(receipt.UpdatedAtUtcTicks > 0);
			using (UpdateInstallLock.AcquireExisting(installRoot))
			{
			}
			Assert.Empty(Directory.GetFiles(
				maintenanceRoot,
				"*.promoting",
				SearchOption.TopDirectoryOnly));

			childRelease.Set();
			Assert.True(
				childCompleted.WaitOne(ProcessTimeout),
				"The delegated updater fixture did not exit within the timeout.");
			ParentObservation parentObservation = await ReadObservationAsync<
				ParentObservation>(Path.Combine(
					observationDirectory,
					"parent.json"));
			Assert.Equal(parent.Id, parentObservation.ProcessId);
			Assert.Equal(
				parentStartTimeUtcTicks,
				parentObservation.ProcessStartTimeUtcTicks);
			Assert.Equal(canonicalPath, parentObservation.ExecutablePath, true);
			Assert.Equal(0, parentObservation.ChildExitCode);
			Assert.Equal(
				parentObservation.ChildProcessId,
				childObservation.ProcessId);
			Assert.Equal(
				parentObservation.ChildProcessStartTimeUtcTicks,
				childObservation.ProcessStartTimeUtcTicks);
			Assert.False(IsExactProcessRunning(
				childObservation.ProcessId,
				childObservation.ProcessStartTimeUtcTicks));
			Assert.False(parent.HasExited);
			Assert.False(promoter.HasExited);
			Assert.Equal(
				canonicalLegacySha256,
				await GetSha256Async(canonicalPath));

			parentRelease.Set();
			int parentExitCode = await containedParent.WaitForExitAsync()
				.WaitAsync(ProcessTimeout);
			await promoter.WaitForExitAsync().WaitAsync(ProcessTimeout);
			Assert.Equal(0, parentExitCode);
			Assert.Equal(0, promoter.ExitCode);
			Assert.Equal(string.Empty, await standardOutput);
			Assert.Equal(string.Empty, await standardError);

			Assert.Equal(
				targetIdentity.Sha256,
				await GetSha256Async(canonicalPath));
			Assert.Equal(
				targetIdentity.SizeBytes,
				new FileInfo(canonicalPath).Length);
			Assert.Equal(
				targetIdentity.Sha256,
				await GetSha256Async(generationPath));
			MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
			Assert.Null(await new MaintenanceUpdaterPromotionReceiptStore(
				maintenanceRoot).LoadAsync());
			Assert.Empty(Directory.GetFiles(
				maintenanceRoot,
				"*.promoting",
				SearchOption.TopDirectoryOnly));
			Assert.Empty(Directory.GetFiles(
				maintenanceRoot,
				"*.writing",
				SearchOption.TopDirectoryOnly));
			using (UpdateInstallLock.AcquireExisting(installRoot))
			{
			}
			Assert.False(IsExactProcessRunning(
				parentObservation.ProcessId,
				parentObservation.ProcessStartTimeUtcTicks));
			Assert.False(IsExactProcessRunning(
				promoterObservation.ProcessId,
				promoterObservation.ProcessStartTimeUtcTicks));
		}
		finally
		{
			childRelease.Set();
			parentRelease.Set();

			try
			{
				if (containedParent is not null)
				{
					await containedParent.TerminateTreeAndConfirmEmptyAsync(
						ProcessTimeout);
				}
			}
			finally
			{
				try
				{
					if ((standardOutput is not null) &&
						(standardError is not null))
					{
						await Task.WhenAll(standardOutput, standardError)
							.WaitAsync(ProcessTimeout);
					}
				}
				finally
				{
					standardOutputReader?.Dispose();
					standardErrorReader?.Dispose();
					promoter?.Dispose();
					parent?.Dispose();
					containedParent?.Dispose();
				}
			}
		}
	}

	private static string GetFixtureExecutablePath()
	{
		Assembly fixtureAssembly = Assembly.Load(
			new AssemblyName(FixtureAssemblyName));
		string executablePath = Path.ChangeExtension(
			fixtureAssembly.Location,
			".exe");

		if (!File.Exists(executablePath))
		{
			throw new FileNotFoundException(
				"The updater process fixture apphost was not copied to test output.",
				executablePath);
		}

		return executablePath;
	}

	private static void CopyFixtureRuntime(
		string fixtureExecutablePath,
		string destinationExecutablePath)
	{
		string sourceDirectory = Path.GetDirectoryName(fixtureExecutablePath) ??
			throw new InvalidOperationException(
				"The updater process fixture output directory is unavailable.");
		string destinationDirectory = Path.GetDirectoryName(
			destinationExecutablePath) ?? throw new InvalidOperationException(
				"The updater process fixture destination has no parent directory.");
		Directory.CreateDirectory(destinationDirectory);
		string[] runtimeFiles =
		[
			$"{FixtureAssemblyName}.dll",
			$"{FixtureAssemblyName}.deps.json",
			$"{FixtureAssemblyName}.runtimeconfig.json",
			"AiUsageDashboard.Updater.dll",
			"AiUsageDashboard.Updater.Core.dll",
			"AiUsageDashboard.Licensing.dll"
		];

		foreach (string runtimeFile in runtimeFiles)
		{
			string sourcePath = Path.Combine(sourceDirectory, runtimeFile);
			Assert.True(
				File.Exists(sourcePath),
				$"Updater process fixture runtime file missing: {sourcePath}");
			File.Copy(
				sourcePath,
				Path.Combine(destinationDirectory, runtimeFile),
				overwrite: true);
		}

		File.Copy(
			fixtureExecutablePath,
			destinationExecutablePath,
			overwrite: true);
		string destinationMetadataPath = Path.ChangeExtension(
			destinationExecutablePath,
			".dll");
		string updaterAssemblyPath = Path.Combine(
			destinationDirectory,
			"AiUsageDashboard.Updater.dll");

		if (!string.Equals(
			destinationMetadataPath,
			updaterAssemblyPath,
			StringComparison.OrdinalIgnoreCase))
		{
			File.Copy(
				Path.Combine(sourceDirectory, $"{FixtureAssemblyName}.dll"),
				destinationMetadataPath,
				overwrite: true);
		}
	}

	private static async Task<T> ReadObservationAsync<T>(string path)
	{
		byte[] json = await File.ReadAllBytesAsync(path);
		return JsonSerializer.Deserialize<T>(json) ??
			throw new InvalidDataException(
				$"The updater process fixture observation '{path}' is empty.");
	}

	private static async Task<string> GetSha256Async(string path)
	{
		await using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] hash = await SHA256.HashDataAsync(stream);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static bool IsExactProcessRunning(
		int processId,
		long processStartTimeUtcTicks)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			_ = process.SafeHandle;
			return !process.HasExited &&
				(process.StartTime.ToUniversalTime().Ticks ==
					processStartTimeUtcTicks);
		}
		catch (Exception exception) when (
			exception is ArgumentException or InvalidOperationException or
				System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private sealed record ParentObservation(
		int ProcessId,
		long ProcessStartTimeUtcTicks,
		string ExecutablePath,
		int ChildProcessId,
		long ChildProcessStartTimeUtcTicks,
		int ChildExitCode);

	private sealed record DelegatedChildObservation(
		int ProcessId,
		long ProcessStartTimeUtcTicks,
		string ExecutablePath,
		int ParentProcessId,
		long ParentProcessStartTimeUtcTicks,
		string ParentExecutablePath,
		bool ShouldRefreshUpdater,
		bool ShouldRegisterInstalledApp,
		bool ShouldPromptForLicenses,
		bool WasPromotionScheduled,
		int PromotionParentProcessId,
		long PromotionParentProcessStartTimeUtcTicks,
		string ExpectedCanonicalSha256,
		string SourceSha256);

	private sealed record PromoterObservation(
		int ProcessId,
		long ProcessStartTimeUtcTicks,
		string ExecutablePath,
		string[] Arguments);

	private sealed class UpdaterProcessTestDirectory : IDisposable
	{
		private static readonly TimeSpan CleanupRetryTimeout =
			TimeSpan.FromSeconds(5);
		private static readonly TimeSpan CleanupRetryDelay =
			TimeSpan.FromMilliseconds(20);
		private readonly string _testRoot;

		internal string Path { get; }

		internal UpdaterProcessTestDirectory()
		{
			string localApplicationData = Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData);

			if (string.IsNullOrWhiteSpace(localApplicationData))
			{
				throw new InvalidOperationException(
					"The local application data directory is unavailable.");
			}

			_testRoot = System.IO.Path.Combine(
				System.IO.Path.GetFullPath(localApplicationData),
				"_audpt");
			Path = System.IO.Path.Combine(
				_testRoot,
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public void Dispose()
		{
			if (!Directory.Exists(Path))
			{
				return;
			}

			string normalizedRoot = System.IO.Path.GetFullPath(_testRoot)
				.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
				System.IO.Path.DirectorySeparatorChar;
			string normalizedPath = System.IO.Path.GetFullPath(Path);

			if (!normalizedPath.StartsWith(
				normalizedRoot,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"Refusing to delete a directory outside the updater process test root.");
			}

			long cleanupStartedAt = Stopwatch.GetTimestamp();

			while (Directory.Exists(normalizedPath))
			{
				try
				{
					Directory.Delete(normalizedPath, recursive: true);
				}
				catch (Exception exception) when (
					(exception is IOException or UnauthorizedAccessException) &&
					(Stopwatch.GetElapsedTime(cleanupStartedAt) <
						CleanupRetryTimeout))
				{
					Thread.Sleep(CleanupRetryDelay);
				}
			}
		}
	}
}
