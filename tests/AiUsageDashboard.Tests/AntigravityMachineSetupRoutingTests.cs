using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityMachineSetupRoutingTests
{
	private sealed class MemoryApprovedSourceStore :
		IAntigravityApprovedSourceStore
	{
		internal AntigravityApprovedSource? Source { get; private set; }

		internal bool ShouldFailClear { get; set; }

		internal bool ShouldFailSave { get; set; }

		public AntigravityApprovedSource? Load()
		{
			return Source;
		}

		public void Save(AntigravityApprovedSource source)
		{
			if (ShouldFailSave)
			{
				throw new IOException("Synthetic approved-source save failure.");
			}

			Source = source;
		}

		public bool TrySaveIfMissing(AntigravityApprovedSource source)
		{
			if (Source is not null)
			{
				return false;
			}

			Save(source);
			return true;
		}

		public void Clear()
		{
			if (ShouldFailClear)
			{
				throw new IOException("Synthetic approved-source clear failure.");
			}

			Source = null;
		}
	}

	private sealed class FakeOfficialPrintUsageClient :
		IAntigravityOfficialPrintUsageClient
	{
		private readonly Func<
			bool,
			AntigravityProductionUsageResult> _createResult;

		internal int CaptureCallCount { get; private set; }

		internal int RevalidationCallCount { get; private set; }

		internal string? ObservedExecutablePath { get; private set; }

		internal FakeOfficialPrintUsageClient(
			AntigravityProductionUsageResult captureResult,
			AntigravityProductionUsageResult? revalidationResult = null)
			: this(isRevalidation =>
				isRevalidation
					? revalidationResult ?? captureResult
					: captureResult)
		{
		}

		internal FakeOfficialPrintUsageClient(
			Func<bool, AntigravityProductionUsageResult> createResult)
		{
			_createResult = createResult;
		}

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CaptureCallCount++;
			ObservedExecutablePath = executablePath;
			return Task.FromResult(_createResult(false));
		}

		public Task<AntigravityProductionUsageResult> RevalidateAsync(
			string executablePath,
			Action onSafetyTrackedAttemptStarted,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RevalidationCallCount++;
			ObservedExecutablePath = executablePath;
			onSafetyTrackedAttemptStarted();
			return Task.FromResult(_createResult(true));
		}
	}

	private sealed class InlineProgress<T> : IProgress<T>
	{
		private readonly ICollection<T> _values;

		internal InlineProgress(ICollection<T> values)
		{
			_values = values;
		}

		public void Report(T value)
		{
			_values.Add(value);
		}
	}

	[Fact]
	public async Task PrepareAsync_WithoutConsent_FailsBeforeDiscovery()
	{
		bool wasDiscoveryCalled = false;
		AntigravityMachineSetupService service = CreateService(
			Array.Empty<string>(),
			new FakeOfficialPrintUsageClient(Success()),
			new MemoryApprovedSourceStore(),
			discoveryOverride: _ =>
			{
				wasDiscoveryCalled = true;
				return Task.FromResult<IReadOnlyList<string>>(
					Array.Empty<string>());
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(
					IsOfficialUsageReadApproved: false),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.ConsentRequired,
			result.FailureKind);
		Assert.False(wasDiscoveryCalled);
	}

	[Fact]
	public async Task PrepareAsync_WithSupportedOfficialBuild_PreviewsAndPersistsApproval()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		MemoryApprovedSourceStore store = new();
		FakeOfficialPrintUsageClient usageClient = new(Success());
		int validationCount = 0;
		List<AntigravityMachineSetupProgress> progress = new();
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			usageClient,
			store,
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				validationCount++;
				return Task.FromResult(Supported(path));
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				new InlineProgress<AntigravityMachineSetupProgress>(progress),
				CancellationToken.None);

		Assert.True(result.IsSuccessful);
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(result.Candidate);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			candidate.SourceKind);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			candidate.AccountIdentity);
		Assert.Equal(executablePath, usageClient.ObservedExecutablePath);
		Assert.Contains(
			progress,
			entry => entry.Stage ==
				AntigravityMachineSetupStage.ReadyForApproval);

		await candidate.ApproveAsync();

		Assert.True(candidate.IsApproved);
		Assert.Equal(2, validationCount);
		Assert.Equal(
			new AntigravityApprovedSource(
				AntigravityMachineSetupSourceKind.OfficialPrint,
				executablePath),
			store.Source);
	}

	[Fact]
	public async Task PrepareAsync_WithMultipleSupportedBuilds_FailsAmbiguousBeforePreview()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = CreateExecutable(temporaryDirectory.Path, "first");
		string secondPath = CreateExecutable(temporaryDirectory.Path, "second");
		FakeOfficialPrintUsageClient usageClient = new(Success());
		AntigravityMachineSetupService service = CreateService(
			new[] { firstPath, secondPath },
			usageClient,
			new MemoryApprovedSourceStore());

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.AmbiguousExecutable,
			result.FailureKind);
		Assert.Equal(0, usageClient.CaptureCallCount);
	}

	[Fact]
	public async Task PrepareAsync_WithUnsupportedBuild_FailsWithoutPreview()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeOfficialPrintUsageClient usageClient = new(Success());
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			usageClient,
			new MemoryApprovedSourceStore(),
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CaptureCallCount);
	}

	[Theory]
	[InlineData(
		(int)AntigravityOfficialPrintExecutionGateFailureKind.ContentionTimedOut,
		AntigravityMachineSetupFailureKind.ExecutionBusy)]
	[InlineData(
		(int)AntigravityOfficialPrintExecutionGateFailureKind.Unavailable,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	public async Task PrepareAsync_WhenExecutionGateFails_PreservesFailureClass(
		int gateFailureValue,
		AntigravityMachineSetupFailureKind expectedFailure)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeOfficialPrintUsageClient usageClient = new(Success());
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			usageClient,
			new MemoryApprovedSourceStore(),
			(_, _) => throw new AntigravityOfficialPrintExecutionGateException(
				(AntigravityOfficialPrintExecutionGateFailureKind)gateFailureValue,
				new IOException("Synthetic execution-gate failure.")));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(expectedFailure, result.FailureKind);
		Assert.Equal(0, usageClient.CaptureCallCount);
	}

	[Theory]
	[InlineData(
		AntigravityProductionUsageFailureKind.ExecutionBusy,
		false,
		AntigravityMachineSetupFailureKind.ExecutionBusy)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UsageShapeRejected,
		false,
		AntigravityMachineSetupFailureKind.UsageRejected)]
	[InlineData(
		AntigravityProductionUsageFailureKind.ProvenanceRejected,
		false,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		true,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		false,
		AntigravityMachineSetupFailureKind.SafetyRevalidationRequired)]
	public async Task PrepareAsync_WhenOfficialPreviewFails_PreservesFailure(
		AntigravityProductionUsageFailureKind previewFailure,
		bool automaticRevalidationPending,
		AntigravityMachineSetupFailureKind expectedFailure)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeOfficialPrintUsageClient usageClient = new(Failure(
			previewFailure,
			automaticRevalidationPending));
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			usageClient,
			new MemoryApprovedSourceStore());

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(expectedFailure, result.FailureKind);
		Assert.Equal(
			expectedFailure ==
				AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			result.CanRevalidateSafety);
		AntigravityMachineSetupPreviewFailure preview =
			Assert.IsType<AntigravityMachineSetupPreviewFailure>(
				result.PreviewFailure);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			preview.SourceKind);
		Assert.Equal(previewFailure, preview.FailureKind);
	}

	[Fact]
	public async Task RevalidateSafetyAsync_UsesOfficialRevalidationOnce()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeOfficialPrintUsageClient usageClient = new(
			Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				automaticRevalidationPending: false),
			Success());
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			usageClient,
			new MemoryApprovedSourceStore());
		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);

		AntigravityMachineSetupPreparationResult revalidated =
			await result.RevalidateSafetyAsync();

		Assert.True(revalidated.IsSuccessful);
		Assert.Equal(1, usageClient.CaptureCallCount);
		Assert.Equal(1, usageClient.RevalidationCallCount);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => result.RevalidateSafetyAsync());
		await revalidated.Candidate!.DisposeAsync();
	}

	[Fact]
	public async Task CandidateApproval_WhenExecutableChanges_FailsWithoutPersistingSource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		MemoryApprovedSourceStore store = new();
		int validationCount = 0;
		AntigravityMachineSetupService service = CreateService(
			new[] { executablePath },
			new FakeOfficialPrintUsageClient(Success()),
			store,
			(path, _) =>
			{
				validationCount++;
				return Task.FromResult(validationCount == 1
					? Supported(path)
					: new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: false));
			});
		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true),
				progress: null,
				CancellationToken.None);
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(result.Candidate);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => candidate.ApproveAsync());

		Assert.False(candidate.IsApproved);
		Assert.Null(store.Source);
	}

	[Fact]
	public void ApplyApprovedSourceChange_WhenCommitFails_RollsBackExistingSource()
	{
		MemoryApprovedSourceStore store = new();
		AntigravityApprovedSource previous = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			@"C:\old\agy.exe");
		AntigravityApprovedSource replacement = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			@"C:\new\agy.exe");
		store.Save(previous);

		Assert.Throws<InvalidOperationException>(() =>
			AntigravityMachineSetupService.ApplyApprovedSourceChange(
				replacement,
				store,
				CancellationToken.None,
				() => throw new InvalidOperationException(
					"Synthetic commit failure.")));

		Assert.Equal(previous, store.Source);
	}

	[Fact]
	public void ApplyApprovedSourceChange_WhenRollbackFails_PreservesBothErrors()
	{
		MemoryApprovedSourceStore store = new();
		AntigravityApprovedSource replacement = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			@"C:\new\agy.exe");

		InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
			() => AntigravityMachineSetupService.ApplyApprovedSourceChange(
				replacement,
				store,
				CancellationToken.None,
				() =>
				{
					store.ShouldFailClear = true;
					throw new IOException("Synthetic commit failure.");
				}));

		AggregateException aggregate = Assert.IsType<AggregateException>(
			failure.InnerException);
		Assert.Equal(2, aggregate.InnerExceptions.Count);
	}

	[Theory]
	[InlineData("agy.exe", false)]
	[InlineData(@"\\server\share\agy", false)]
	[InlineData(@"C:\tools\agy", true)]
	public void IsLocalExecutableSearchDirectory_UsesFixedLocalDrivePolicy(
		string path,
		bool expected)
	{
		Assert.Equal(
			expected,
			AntigravityMachineSetupService.IsLocalExecutableSearchDirectory(
				path,
				_ => AntigravityExecutableSearchDriveType.Fixed));
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_DeduplicatesKnownLocalPaths()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string localApplicationData = temporaryDirectory.Path;
		string knownInstall = Path.Combine(
			localApplicationData,
			"Programs",
			"Antigravity",
			"resources",
			"app",
			"bin",
			"agy.exe");
		string pathDirectory = Path.Combine(temporaryDirectory.Path, "path-bin");
		string pathExecutable = Path.Combine(pathDirectory, "agy.exe");
		HashSet<string> existing = new(
			new[] { knownInstall, pathExecutable },
			StringComparer.OrdinalIgnoreCase);

		IReadOnlyList<string> candidates =
			await AntigravityMachineSetupService.DiscoverExecutableCandidatesAsync(
				localApplicationData,
				$"{pathDirectory};{pathDirectory}",
				existing.Contains,
				_ => AntigravityExecutableSearchDriveType.Fixed,
				CancellationToken.None);

		Assert.Equal(2, candidates.Count);
		Assert.Contains(knownInstall, candidates);
		Assert.Contains(pathExecutable, candidates);
	}

	private static AntigravityMachineSetupService CreateService(
		IReadOnlyList<string> candidates,
		IAntigravityOfficialPrintUsageClient usageClient,
		IAntigravityApprovedSourceStore store,
		Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintCapabilityValidationResult>>?
			validateOverride = null,
		Func<CancellationToken, Task<IReadOnlyList<string>>>?
			discoveryOverride = null)
	{
		return new AntigravityMachineSetupService(
			validateOverride ?? ((path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(Supported(path));
			}),
			usageClient,
			store,
			(_, _) => null,
			discoveryOverride ?? (_ =>
				Task.FromResult(candidates)));
	}

	private static string CreateExecutable(
		string rootPath,
		string directoryName = "agy")
	{
		string directoryPath = Path.Combine(rootPath, directoryName);
		Directory.CreateDirectory(directoryPath);
		string executablePath = Path.Combine(directoryPath, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		return executablePath;
	}

	private static AntigravityOfficialPrintCapabilityValidationResult Supported(
		string executablePath)
	{
		return new AntigravityOfficialPrintCapabilityValidationResult(
			isSupported: true,
			cliVersion: "1.1.12",
			executableLease: AntigravityExecutableLease.Acquire(executablePath));
	}

	private static AntigravityProductionUsageResult Success()
	{
		return new AntigravityProductionUsageResult(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			CreateWindows());
	}

	private static AntigravityProductionUsageResult Failure(
		AntigravityProductionUsageFailureKind failureKind,
		bool automaticRevalidationPending)
	{
		AntigravityUsageSafetyFailureReason safetyFailureReason =
			failureKind == AntigravityProductionUsageFailureKind.SafetyLatched
				? automaticRevalidationPending
					? AntigravityUsageSafetyFailureReason.TimedOut
					: AntigravityUsageSafetyFailureReason.Unknown
				: AntigravityUsageSafetyFailureReason.None;
		return new AntigravityProductionUsageResult(
			isSuccessful: false,
			failureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			safetyFailureReason,
			automaticRevalidationPending);
	}

	private static AntigravityProductionUsageWindow[] CreateWindows()
	{
		return new[]
		{
			new AntigravityProductionUsageWindow(
				"agy.gemini.weekly",
				75m,
				TimeSpan.FromHours(1),
				false),
			new AntigravityProductionUsageWindow(
				"agy.gemini.rolling-5h",
				80m,
				TimeSpan.FromHours(2),
				false),
			new AntigravityProductionUsageWindow(
				"agy.claude.weekly",
				50m,
				TimeSpan.FromHours(3),
				false),
			new AntigravityProductionUsageWindow(
				"agy.claude.rolling-5h",
				100m,
				null,
				true)
		};
	}
}
