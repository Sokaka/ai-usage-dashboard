using System.Diagnostics;
using System.Security.Cryptography;

using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class MaintenanceUpdaterPromotionTests
{
	[Fact]
	public async Task PromoteAsync_WhenCanonicalChangesAfterParentExit_RefusesToOverwriteConcurrentGeneration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "install"));
		Directory.CreateDirectory(maintenanceRoot);
		string sourceUpdater = GetUpdaterExecutablePath();
		string sourceHash = GetSha256(sourceUpdater);
		string generationPath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(maintenanceRoot, sourceHash);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		File.Copy(sourceUpdater, generationPath);
		File.Copy(sourceUpdater, canonicalPath);
		await File.AppendAllTextAsync(canonicalPath, "old-generation");
		string expectedCanonicalHash = GetSha256(canonicalPath);
		FakeExactProcessExitWaiter waiter = new(() =>
			File.AppendAllText(canonicalPath, "concurrent-generation"));
		MaintenanceUpdaterPromotion promotion = new(waiter);
		MaintenanceUpdaterPromotionOptions options = new(
			Path.Combine(temporaryDirectory.Path, "install"),
			maintenanceRoot,
			sourceHash,
			expectedCanonicalHash,
			new ExactProcessIdentity(1234, 5678));
		await new MaintenanceUpdaterPromotionReceiptStore(maintenanceRoot)
			.SavePendingAsync(options);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			promotion.PromoteAndRecordAsync(
				options,
				generationPath));

		Assert.Equal(1, waiter.CallCount);
		Assert.NotEqual(sourceHash, GetSha256(canonicalPath));
		MaintenanceUpdaterPromotionReceipt receipt = Assert.IsType<
			MaintenanceUpdaterPromotionReceipt>(
				await new MaintenanceUpdaterPromotionReceiptStore(maintenanceRoot)
					.LoadAsync());
		Assert.Equal("failed", receipt.Status);
		Assert.Contains(
			"changed before",
			receipt.FailureMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task PromoteAsync_WhenNotRunningFromVerifiedGeneration_RejectsBeforeWaiting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "install"));
		Directory.CreateDirectory(maintenanceRoot);
		string sourceUpdater = GetUpdaterExecutablePath();
		string sourceHash = GetSha256(sourceUpdater);
		string generationPath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(maintenanceRoot, sourceHash);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		File.Copy(sourceUpdater, generationPath);
		File.Copy(sourceUpdater, canonicalPath);
		await File.AppendAllTextAsync(canonicalPath, "old-generation");
		FakeExactProcessExitWaiter waiter = new();
		MaintenanceUpdaterPromotion promotion = new(waiter);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			promotion.PromoteAsync(
				new MaintenanceUpdaterPromotionOptions(
					Path.Combine(temporaryDirectory.Path, "install"),
					maintenanceRoot,
					sourceHash,
					GetSha256(canonicalPath),
					new ExactProcessIdentity(1234, 5678)),
				canonicalPath));

		Assert.Equal(0, waiter.CallCount);
	}

	[Fact]
	public async Task ExactProcessWaiter_WhenPidWasReused_DoesNotWaitForDifferentStartTime()
	{
		using Process currentProcess = Process.GetCurrentProcess();
		ExactProcessIdentity reusedIdentity = new(
			currentProcess.Id,
			currentProcess.StartTime.ToUniversalTime().Ticks + 1);

		await new SystemExactProcessExitWaiter().WaitForExitAsync(
			reusedIdentity,
			TimeSpan.FromMilliseconds(1),
			CancellationToken.None);
	}

	[Fact]
	public async Task ExactProcessWaiter_WhenProcessIdentityMatches_WaitsInsteadOfTreatingItAsPidReuse()
	{
		using Process currentProcess = Process.GetCurrentProcess();
		ExactProcessIdentity currentIdentity = new(
			currentProcess.Id,
			currentProcess.StartTime.ToUniversalTime().Ticks);

		await Assert.ThrowsAsync<TimeoutException>(() =>
			new SystemExactProcessExitWaiter().WaitForExitAsync(
				currentIdentity,
				TimeSpan.FromMilliseconds(10),
				CancellationToken.None));
	}

	[Fact]
	public void CommandLine_RequiresExactIdentityAndContentHashesWithoutPathOverrides()
	{
		string sourceHash = new string('A', 64);
		string canonicalHash = new string('B', 64);
		MaintenanceUpdaterPromotionOptions options =
			MaintenanceUpdaterPromotionCommandLine.Parse(
			[
				MaintenanceUpdaterPromotionCommandLine.Command,
				"--source-sha256",
				sourceHash,
				"--expected-canonical-sha256",
				canonicalHash,
				"--parent-process-id",
				"1234",
				"--parent-process-start-time-utc-ticks",
				"5678"
			],
			@"C:\Users\Test\AppData\Local");

		Assert.Equal(new string('a', 64), options.SourceSha256);
		Assert.Equal(new string('b', 64), options.ExpectedCanonicalSha256);
		Assert.Equal(new ExactProcessIdentity(1234, 5678), options.ParentIdentity);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboard",
			options.InstallRoot);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboardUpdater",
			options.MaintenanceRoot);
		ProcessStartInfo startInfo =
			MaintenanceUpdaterPromotionLauncher.CreateStartInfo(
				ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
					options.MaintenanceRoot,
					options.SourceSha256),
				options);
		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.CreateNoWindow);
		Assert.DoesNotContain("--maintenance-root", startInfo.ArgumentList);
		Assert.DoesNotContain("--install-root", startInfo.ArgumentList);
		Assert.DoesNotContain("--destination", startInfo.ArgumentList);
	}

	[Theory]
	[InlineData("--parent-process-id", "0")]
	[InlineData("--parent-process-id", "1234", "--parent-process-id", "5678")]
	[InlineData("--parent-process-start-time-utc-ticks", "-1")]
	[InlineData("--source-sha256", "not-a-hash")]
	[InlineData("--destination", @"C:\arbitrary.exe")]
	public void CommandLine_WithInvalidPromotionContract_Rejects(
		params string[] replacementArguments)
	{
		List<string> arguments =
		[
			MaintenanceUpdaterPromotionCommandLine.Command,
			"--source-sha256",
			new string('a', 64),
			"--expected-canonical-sha256",
			new string('b', 64),
			"--parent-process-id",
			"1234",
			"--parent-process-start-time-utc-ticks",
			"5678",
			.. replacementArguments
		];

		Assert.Throws<UpdaterCommandLineException>(() =>
			MaintenanceUpdaterPromotionCommandLine.Parse(
				arguments,
				@"C:\Users\Test\AppData\Local"));
	}

	[Fact]
	public async Task ReceiptCleanup_WhenNewHandoffReplacesReceipt_KeepsNewPendingPromotion()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		Directory.CreateDirectory(installRoot);
		Directory.CreateDirectory(maintenanceRoot);
		MaintenanceUpdaterPromotionReceiptStore store = new(maintenanceRoot);
		MaintenanceUpdaterPromotionOptions first = new(
			installRoot,
			maintenanceRoot,
			new string('a', 64),
			new string('b', 64),
			new ExactProcessIdentity(1234, 5678));
		MaintenanceUpdaterPromotionOptions second = new(
			installRoot,
			maintenanceRoot,
			new string('c', 64),
			new string('d', 64),
			new ExactProcessIdentity(2345, 6789));
		await store.SavePendingAsync(first);
		await store.SavePendingAsync(second);

		await store.SaveFailureIfCurrentAsync(
			first,
			new InvalidOperationException("stale failure"));
		await store.DeleteIfMatchesAsync(first);

		MaintenanceUpdaterPromotionReceipt receipt = Assert.IsType<
			MaintenanceUpdaterPromotionReceipt>(await store.LoadAsync());
		Assert.Equal(second.SourceSha256, receipt.SourceSha256);
		Assert.Equal(second.ParentIdentity.ProcessId, receipt.ParentProcessId);
		Assert.Equal("pending", receipt.Status);
	}

	[Fact]
	public async Task ReceiptFailure_WhenCurrentReceiptIsMissing_DoesNotRecreateStaleReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		Directory.CreateDirectory(installRoot);
		Directory.CreateDirectory(maintenanceRoot);
		MaintenanceUpdaterPromotionReceiptStore store = new(maintenanceRoot);
		MaintenanceUpdaterPromotionOptions staleOptions = new(
			installRoot,
			maintenanceRoot,
			new string('a', 64),
			new string('b', 64),
			new ExactProcessIdentity(1234, 5678));

		await store.SaveFailureIfCurrentAsync(
			staleOptions,
			new InvalidOperationException("stale failure"));

		Assert.Null(await store.LoadAsync());
	}

	[Fact]
	public async Task RetryRebind_WhenReceiptWasSuperseded_DoesNotRestoreStalePromotion()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string maintenanceRoot = Path.Combine(
			temporaryDirectory.Path,
			"maintenance");
		Directory.CreateDirectory(installRoot);
		Directory.CreateDirectory(maintenanceRoot);
		MaintenanceUpdaterPromotionReceiptStore store = new(maintenanceRoot);
		MaintenanceUpdaterPromotionOptions staleOptions = new(
			installRoot,
			maintenanceRoot,
			new string('a', 64),
			new string('b', 64),
			new ExactProcessIdentity(1234, 5678));
		MaintenanceUpdaterPromotionOptions newerOptions = new(
			installRoot,
			maintenanceRoot,
			new string('c', 64),
			new string('d', 64),
			new ExactProcessIdentity(2345, 6789));
		MaintenanceUpdaterPromotionOptions staleRetryOptions = staleOptions with
		{
			ParentIdentity = new ExactProcessIdentity(3456, 7890)
		};
		await store.SavePendingAsync(staleOptions);
		MaintenanceUpdaterPromotionReceipt staleSnapshot = Assert.IsType<
			MaintenanceUpdaterPromotionReceipt>(await store.LoadAsync());
		await store.SavePendingAsync(newerOptions);

		bool wasRebound;
		using (UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			installRoot))
		{
			wasRebound = await store
				.SavePendingIfSnapshotMatchesWhileInstallLockHeldAsync(
					staleSnapshot,
					staleRetryOptions);
		}

		Assert.False(wasRebound);
		MaintenanceUpdaterPromotionReceipt current = Assert.IsType<
			MaintenanceUpdaterPromotionReceipt>(await store.LoadAsync());
		Assert.Equal(newerOptions.SourceSha256, current.SourceSha256);
		Assert.Equal(
			newerOptions.ParentIdentity.ProcessId,
			current.ParentProcessId);
	}

	[Fact]
	public async Task SupersededPromotion_WhenOlderWorkerFinishesFirst_PromotesOnlyNewestGeneration()
	{
		using PromotionRaceScenario scenario =
			await PromotionRaceScenario.CreateAsync();

		await scenario.OlderPromotion.PromoteAndRecordAsync(
			scenario.OlderOptions,
			scenario.OlderGenerationPath);
		await scenario.NewerPromotion.PromoteAndRecordAsync(
			scenario.NewerOptions,
			scenario.NewerGenerationPath);

		Assert.Equal(scenario.NewerHash, GetSha256(scenario.CanonicalPath));
		Assert.Null(await scenario.ReceiptStore.LoadAsync());
	}

	[Fact]
	public async Task SupersededPromotion_WhenNewerWorkerFinishesFirst_DoesNotRecreateStaleReceipt()
	{
		using PromotionRaceScenario scenario =
			await PromotionRaceScenario.CreateAsync();

		await scenario.NewerPromotion.PromoteAndRecordAsync(
			scenario.NewerOptions,
			scenario.NewerGenerationPath);
		await scenario.OlderPromotion.PromoteAndRecordAsync(
			scenario.OlderOptions,
			scenario.OlderGenerationPath);

		Assert.Equal(scenario.NewerHash, GetSha256(scenario.CanonicalPath));
		Assert.Null(await scenario.ReceiptStore.LoadAsync());
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

	private sealed class FakeExactProcessExitWaiter : IExactProcessExitWaiter
	{
		private readonly Action? _onWait;

		internal int CallCount { get; private set; }

		internal FakeExactProcessExitWaiter(Action? onWait = null)
		{
			_onWait = onWait;
		}

		public Task WaitForExitAsync(
			ExactProcessIdentity identity,
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			CallCount++;
			_onWait?.Invoke();
			return Task.CompletedTask;
		}
	}

	private sealed record PromotionRaceScenario(
		TemporaryDirectory TemporaryDirectory,
		string CanonicalPath,
		string OlderGenerationPath,
		string NewerGenerationPath,
		string NewerHash,
		MaintenanceUpdaterPromotionOptions OlderOptions,
		MaintenanceUpdaterPromotionOptions NewerOptions,
		MaintenanceUpdaterPromotion OlderPromotion,
		MaintenanceUpdaterPromotion NewerPromotion,
		MaintenanceUpdaterPromotionReceiptStore ReceiptStore) : IDisposable
	{
		internal static async Task<PromotionRaceScenario> CreateAsync()
		{
			TemporaryDirectory temporaryDirectory = new();
			string installRoot = Path.Combine(
				temporaryDirectory.Path,
				"install");
			string maintenanceRoot = Path.Combine(
				temporaryDirectory.Path,
				"maintenance");
			Directory.CreateDirectory(installRoot);
			Directory.CreateDirectory(maintenanceRoot);
			string sourceUpdater = GetUpdaterExecutablePath();
			string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
				maintenanceRoot);
			File.Copy(sourceUpdater, canonicalPath);
			await File.AppendAllTextAsync(canonicalPath, "canonical-a");
			string canonicalHash = GetSha256(canonicalPath);
			string olderSource = Path.Combine(
				temporaryDirectory.Path,
				"older.exe");
			File.Copy(sourceUpdater, olderSource);
			await File.AppendAllTextAsync(olderSource, "generation-b");
			string olderHash = GetSha256(olderSource);
			string olderGenerationPath = ManagedInstallationPaths
				.GetMaintenanceUpdaterGeneration(maintenanceRoot, olderHash);
			File.Move(olderSource, olderGenerationPath);
			string newerSource = Path.Combine(
				temporaryDirectory.Path,
				"newer.exe");
			File.Copy(sourceUpdater, newerSource);
			await File.AppendAllTextAsync(newerSource, "generation-c");
			string newerHash = GetSha256(newerSource);
			string newerGenerationPath = ManagedInstallationPaths
				.GetMaintenanceUpdaterGeneration(maintenanceRoot, newerHash);
			File.Move(newerSource, newerGenerationPath);
			MaintenanceUpdaterPromotionOptions olderOptions = new(
				installRoot,
				maintenanceRoot,
				olderHash,
				canonicalHash,
				new ExactProcessIdentity(1234, 5678));
			MaintenanceUpdaterPromotionOptions newerOptions = new(
				installRoot,
				maintenanceRoot,
				newerHash,
				canonicalHash,
				new ExactProcessIdentity(1234, 5678));
			MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
				maintenanceRoot);
			await receiptStore.SavePendingAsync(olderOptions);
			await receiptStore.SavePendingAsync(newerOptions);
			return new PromotionRaceScenario(
				temporaryDirectory,
				canonicalPath,
				olderGenerationPath,
				newerGenerationPath,
				newerHash,
				olderOptions,
				newerOptions,
				new MaintenanceUpdaterPromotion(
					new FakeExactProcessExitWaiter()),
				new MaintenanceUpdaterPromotion(
					new FakeExactProcessExitWaiter()),
				receiptStore);
		}

		public void Dispose()
		{
			TemporaryDirectory.Dispose();
		}
	}
}
