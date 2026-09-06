using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityMachineSetupRoutingTests
{
	private sealed class CapabilityGateStore :
		IAntigravityOfficialPrintSafetyStateStore,
		IAntigravityOfficialPrintExecutionGate
	{
		private sealed class Lease : IDisposable
		{
			private Action? _release;

			internal Lease(Action release)
			{
				_release = release;
			}

			public void Dispose()
			{
				Interlocked.Exchange(ref _release, null)?.Invoke();
			}
		}

		private readonly SemaphoreSlim _gate = new(1, 1);
		private int _isHeld;

		internal int AcquireCallCount { get; private set; }

		internal bool IsHeld => Volatile.Read(ref _isHeld) != 0;

		public async Task<IDisposable> AcquireExecutionLeaseAsync(
			CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken);
			AcquireCallCount++;
			Interlocked.Exchange(ref _isHeld, 1);
			return new Lease(() =>
			{
				Interlocked.Exchange(ref _isHeld, 0);
				_gate.Release();
			});
		}

		public Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<
				AntigravityOfficialPrintSafetyState?>(null);
		}

		public Task SaveAsync(
			AntigravityOfficialPrintSafetyState state,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public Task ClearAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}
	}

	private sealed class NeverRunOfficialProcessRunner :
		IAntigravityOfficialPrintProcessRunner
	{
		public Task<AntigravityOfficialPrintProcessResult> RunAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			throw new InvalidOperationException(
				"Machine-setup capability routing must not run /usage.");
		}
	}

	private sealed class FakeOfficialPrintUsageClient :
		IAntigravityOfficialPrintUsageClient
	{
		private readonly AntigravityProductionUsageResult _captureResult;
		private readonly AntigravityProductionUsageResult _revalidationResult;

		internal int CallCount => CaptureCallCount + RevalidationCallCount;

		internal int CaptureCallCount { get; private set; }

		internal int RevalidationCallCount { get; private set; }

		internal string? ObservedExecutablePath { get; private set; }

		internal FakeOfficialPrintUsageClient(
			AntigravityProductionUsageResult captureResult,
			AntigravityProductionUsageResult? revalidationResult = null)
		{
			_captureResult = captureResult;
			_revalidationResult = revalidationResult ?? captureResult;
		}

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CaptureCallCount++;
			ObservedExecutablePath = executablePath;
			return Task.FromResult(_captureResult);
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
			return Task.FromResult(_revalidationResult);
		}
	}

	private sealed class FakeProductionUsageClient :
		IAntigravityProductionUsageClient
	{
		private readonly AntigravityProductionUsageResult _result;

		internal int CallCount { get; private set; }

		internal string? ObservedProfilePath { get; private set; }

		internal FakeProductionUsageClient(
			AntigravityProductionUsageResult result)
		{
			_result = result;
		}

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string profilePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			ObservedProfilePath = profilePath;
			return Task.FromResult(_result);
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

	private const string OfficialExecutableVariable =
		"AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE";
	private const string ProfileVariable =
		"AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE";
	private static readonly string ExistingProfileSettingsBaselineFingerprint =
		new('D', 64);

	[Fact]
	public async Task PublicConstructor_WithProductionClient_RoutesCapabilityValidationThroughExecutionGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		CapabilityGateStore store = new();
		bool validationObservedGate = false;
		AntigravityOfficialPrintUsageClient client = new(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(executablePath, path);
				validationObservedGate = store.IsHeld;
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						cliVersion: "1.1.12",
						executableLease:
							AntigravityExecutableLease.Acquire(path)));
			},
			new NeverRunOfficialProcessRunner(),
			timeProvider: null,
			store);
		AntigravityMachineSetupService service = new(client);
		System.Reflection.FieldInfo validatorField = Assert.IsAssignableFrom<
			System.Reflection.FieldInfo>(
				typeof(AntigravityMachineSetupService).GetField(
					"_validateOfficialPrintCapabilityAsync",
					System.Reflection.BindingFlags.Instance |
					System.Reflection.BindingFlags.NonPublic));
		Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintCapabilityValidationResult>> validator =
			Assert.IsType<Func<
				string,
				CancellationToken,
				Task<AntigravityOfficialPrintCapabilityValidationResult>>>(
					validatorField.GetValue(service));

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator(executablePath, CancellationToken.None);

		Assert.True(result.IsSupported);
		Assert.True(validationObservedGate);
		Assert.Equal(1, store.AcquireCallCount);
		Assert.False(store.IsHeld);
	}

	[Fact]
	public async Task PrepareAsync_WithMultipleOfficialBuilds_FailsAmbiguousBeforePreview()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "first", "agy.exe");
		string secondPath = Path.Combine(temporaryDirectory.Path, "second", "agy.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
		File.WriteAllBytes(firstPath, new byte[] { 1 });
		File.WriteAllBytes(secondPath, new byte[] { 2 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		AntigravityMachineSetupService service = new(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						"1.1.12",
						AntigravityExecutableLease.Acquire(path)));
			},
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { firstPath, secondPath })));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.AmbiguousExecutable,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
	}

	[Fact]
	public async Task PrepareAsync_WhenOfficialExecutionGateIsBusy_StopsAfterFirstCandidateAndReportsRetryableFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(
			temporaryDirectory.Path,
			"first",
			"agy.exe");
		string secondPath = Path.Combine(
			temporaryDirectory.Path,
			"second",
			"agy.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
		File.WriteAllBytes(firstPath, new byte[] { 1 });
		File.WriteAllBytes(secondPath, new byte[] { 2 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		int validationCount = 0;
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(_, _) =>
			{
				validationCount++;
				throw new AntigravityOfficialPrintExecutionGateException(
					AntigravityOfficialPrintExecutionGateFailureKind
						.ContentionTimedOut,
					new TimeoutException("Synthetic gate contention."));
			},
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { firstPath, secondPath })),
			(_, _, _) =>
			{
				legacyValidationCount++;
				throw new InvalidOperationException(
					"Legacy validation must not run after a busy official probe.");
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.ExecutionBusy,
			result.FailureKind);
		Assert.Equal(1, validationCount);
		Assert.Equal(0, legacyValidationCount);
		Assert.Equal(0, usageClient.CallCount);
	}

	[Fact]
	public async Task PrepareAsync_WhenOfficialExecutionGateIsUnavailable_DoesNotReportBusyOrUnsupportedBuild()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		AntigravityMachineSetupService service = new(
			(_, _) => throw new
				AntigravityOfficialPrintExecutionGateException(
					AntigravityOfficialPrintExecutionGateFailureKind.Unavailable,
					new UnauthorizedAccessException(
						"Synthetic gate access failure.")),
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnexpectedFailure,
			result.FailureKind);
		Assert.NotEqual(
			AntigravityMachineSetupFailureKind.ExecutionBusy,
			result.FailureKind);
		Assert.NotEqual(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
	}

	[Fact]
	public async Task PrepareAsync_WithInvalidOfficialMarker_DoesNotPreviewOrUseLegacySource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, _) => string.Equals(
				name,
				OfficialExecutableVariable,
				StringComparison.OrdinalIgnoreCase)
					? executablePath
					: @"C:\legacy\profile.json",
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
	}

	[Fact]
	public async Task PrepareAsync_WithOfficialAndPotentialLegacyBuild_OfficialWinsGlobally()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string officialPath = Path.Combine(
			temporaryDirectory.Path,
			"official",
			"agy.exe");
		string legacyPath = Path.Combine(
			temporaryDirectory.Path,
			"legacy",
			"agy.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(officialPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
		File.WriteAllBytes(officialPath, new byte[] { 1 });
		File.WriteAllBytes(legacyPath, new byte[] { 2 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();

				return Task.FromResult(
					string.Equals(
						path,
						officialPath,
						StringComparison.OrdinalIgnoreCase)
						? new AntigravityOfficialPrintCapabilityValidationResult(
							isSupported: true,
							"1.1.12",
							AntigravityExecutableLease.Acquire(path))
						: new AntigravityOfficialPrintCapabilityValidationResult(
							isSupported: false));
			},
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { legacyPath, officialPath })),
			(path, contract, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				legacyValidationCount++;
				AntigravityReviewedPackageExecutable executable =
					contract.Executable;
				AntigravityCliFingerprint fingerprint = new(
					path,
					executable.CliVersion,
					executable.FileVersion,
					executable.ProductVersion,
					executable.Sha256,
					executable.WinVerifyTrustStatus,
					executable.SignerSubject,
					executable.SignerThumbprint);
				return Task.FromResult(
					new AntigravityCliCapabilityValidationResult(
						isSupported: true,
						AntigravityCliCapabilityFailureReason.None,
						"Synthetic valid legacy candidate.",
						fingerprint,
						AntigravityExecutableLease.Acquire(path)));
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.True(result.IsSuccessful);
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(result.Candidate);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			candidate.SourceKind);
		Assert.Equal(officialPath, usageClient.ObservedExecutablePath);
		Assert.Equal(0, legacyValidationCount);
	}

	[Fact]
	public async Task PrepareAsync_WithInvalidProcessOfficialMarker_DoesNotEnterLegacyFallback()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, target) =>
				string.Equals(
					name,
					OfficialExecutableVariable,
					StringComparison.OrdinalIgnoreCase) &&
				(target == EnvironmentVariableTarget.Process)
					? executablePath
					: null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, _, _) =>
			{
				legacyValidationCount++;
				throw new InvalidOperationException(
					"Legacy validation must not run under an official marker.");
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
		Assert.Equal(0, legacyValidationCount);
	}

	[Fact]
	public async Task PrepareAsync_WithLegacyBuildAndNoCurrentUserProfileMarker_ReturnsUnsupportedBeforeLegacyValidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, _, _) =>
			{
				legacyValidationCount++;
				return Task.FromResult(
					CreateUnsupportedLegacyValidationResult());
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
		Assert.Equal(0, legacyValidationCount);
	}

	[Fact]
	public async Task PrepareAsync_WithProcessOnlyLegacyProfileMarker_ReturnsUnsupported()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		AntigravityReviewedPackageContract contract =
			AntigravityReviewedPackageManifestCatalog.Contracts[0];
		string profilePath = await CreateExistingReviewedProfileAsync(
			temporaryDirectory.Path,
			executablePath,
			contract);
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		List<EnvironmentVariableTarget> profileLookupTargets = new();
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, target) =>
			{
				if (!string.Equals(
					name,
					ProfileVariable,
					StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				profileLookupTargets.Add(target);
				return target == EnvironmentVariableTarget.Process
					? profilePath
					: null;
			},
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, _, _) =>
			{
				legacyValidationCount++;
				return Task.FromResult(
					CreateUnsupportedLegacyValidationResult());
			});

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
		Assert.Equal(0, legacyValidationCount);
		Assert.Equal(
			new[] { EnvironmentVariableTarget.User },
			profileLookupTargets);
	}

	[Fact]
	public async Task PrepareAsync_WithTamperedCurrentUserProfileBinding_ReturnsUnsupportedBeforeLegacyValidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		AntigravityReviewedPackageContract contract =
			AntigravityReviewedPackageManifestCatalog.Contracts[0];
		string profilePath = await CreateExistingReviewedProfileAsync(
			temporaryDirectory.Path,
			executablePath,
			contract,
			hasValidLocalBinding: false);
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		int legacyValidationCount = 0;
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, target) =>
				string.Equals(
					name,
					ProfileVariable,
					StringComparison.OrdinalIgnoreCase) &&
				(target == EnvironmentVariableTarget.User)
					? profilePath
					: null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, _, _) =>
			{
				legacyValidationCount++;
				return Task.FromResult(
					CreateUnsupportedLegacyValidationResult());
			},
			captureReviewedConPtySettingsBaselineAsync:
				CaptureExistingProfileSettingsBaselineAsync);

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		Assert.Equal(0, usageClient.CallCount);
		Assert.Equal(0, legacyValidationCount);
	}

	[Fact]
	public async Task PrepareAsync_WithValidCurrentUserProfile_OnlyEvaluatesMatchingReviewedContract()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		AntigravityReviewedPackageContract contract =
			AntigravityReviewedPackageManifestCatalog.Contracts[0];
		string profilePath = await CreateExistingReviewedProfileAsync(
			temporaryDirectory.Path,
			executablePath,
			contract);
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		List<AntigravityReviewedPackageContract> observedContracts = new();
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, target) =>
				string.Equals(
					name,
					ProfileVariable,
					StringComparison.OrdinalIgnoreCase) &&
				(target == EnvironmentVariableTarget.User)
					? profilePath
					: null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, observedContract, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				observedContracts.Add(observedContract);
				return Task.FromResult(
					CreateUnsupportedLegacyValidationResult());
			},
			captureReviewedConPtySettingsBaselineAsync:
				CaptureExistingProfileSettingsBaselineAsync);

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		AntigravityReviewedPackageContract observedContract =
			Assert.Single(observedContracts);
		Assert.Equal(contract.ContractId, observedContract.ContractId);
		Assert.Equal(
			contract.ContractFingerprint,
			observedContract.ContractFingerprint);
	}

	[Fact]
	public async Task PrepareAsync_WithExistingProfileForDifferentLegacyContract_ReturnsUnsupported()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1 });
		IReadOnlyList<AntigravityReviewedPackageContract> contracts =
			AntigravityReviewedPackageManifestCatalog.Contracts;
		Assert.True(contracts.Count >= 2);
		AntigravityReviewedPackageContract existingContract = contracts[0];
		AntigravityReviewedPackageContract differentContract = contracts[1];
		string profilePath = await CreateExistingReviewedProfileAsync(
			temporaryDirectory.Path,
			executablePath,
			existingContract);
		FakeOfficialPrintUsageClient usageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		List<AntigravityReviewedPackageContract> observedContracts = new();
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			usageClient,
			(name, target) =>
				string.Equals(
					name,
					ProfileVariable,
					StringComparison.OrdinalIgnoreCase) &&
				(target == EnvironmentVariableTarget.User)
					? profilePath
					: null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(_, observedContract, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				observedContracts.Add(observedContract);

				if (string.Equals(
					observedContract.ContractId,
					differentContract.ContractId,
					StringComparison.Ordinal))
				{
					throw new InvalidOperationException(
						"A different legacy contract must not be evaluated.");
				}

				return Task.FromResult(
					CreateUnsupportedLegacyValidationResult());
			},
			captureReviewedConPtySettingsBaselineAsync:
				CaptureExistingProfileSettingsBaselineAsync);

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			result.FailureKind);
		AntigravityReviewedPackageContract observedContract =
			Assert.Single(observedContracts);
		Assert.Equal(existingContract.ContractId, observedContract.ContractId);
		Assert.NotEqual(differentContract.ContractId, observedContract.ContractId);
	}

	[Theory]
	[InlineData(
		AntigravityProductionUsageFailureKind.ExecutionBusy,
		AntigravityMachineSetupFailureKind.ExecutionBusy)]
	[InlineData(
		AntigravityProductionUsageFailureKind.ProfileRejected,
		AntigravityMachineSetupFailureKind.SettingsRejected)]
	[InlineData(
		AntigravityProductionUsageFailureKind.ProvenanceRejected,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.CaptureRejected,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UnexpectedFailure,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UsageShapeRejected,
		AntigravityMachineSetupFailureKind.UsageRejected)]
	public async Task PrepareAsync_WithReviewedConPtyPreviewFailure_PreservesExactFailure(
		AntigravityProductionUsageFailureKind previewFailureKind,
		AntigravityMachineSetupFailureKind expectedSetupFailureKind)
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityProductionUsageResult preview = new(
			isSuccessful: false,
			previewFailureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>());
		(AntigravityMachineSetupService service,
			Dictionary<string, string?> _,
			AntigravityReviewedPackageContract _,
			string _,
			string _,
			FakeProductionUsageClient usageClient) =
				await CreateReviewedConPtySetupHarnessAsync(
					temporaryDirectory.Path,
					preview);

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(1, usageClient.CallCount);
		Assert.Equal(expectedSetupFailureKind, result.FailureKind);
		AntigravityMachineSetupPreviewFailure previewFailure =
			Assert.IsType<AntigravityMachineSetupPreviewFailure>(
				result.PreviewFailure);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			previewFailure.SourceKind);
		Assert.Equal(previewFailureKind, previewFailure.FailureKind);
		Assert.NotNull(usageClient.ObservedProfilePath);
		Assert.False(File.Exists(usageClient.ObservedProfilePath));
	}

	[Theory]
	[InlineData("official")]
	[InlineData("profile-path")]
	[InlineData("profile-bytes")]
	public async Task ReviewedConPtyApproval_WhenSourceChangesAfterPreparation_RejectsWithoutOverwritingNewState(
		string mutationKind)
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityProductionUsageResult preview = new(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			"legacy@example.invalid",
			CreateWindows());
		(AntigravityMachineSetupService service,
			Dictionary<string, string?> environment,
			AntigravityReviewedPackageContract contract,
			string executablePath,
			string existingProfilePath,
			FakeProductionUsageClient usageClient) =
				await CreateReviewedConPtySetupHarnessAsync(
					temporaryDirectory.Path,
					preview);
		AntigravityMachineSetupPreparationResult preparation =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);
		Assert.True(
			preparation.IsSuccessful,
			$"Failure={preparation.FailureKind}; PreviewCalls={usageClient.CallCount}");
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(
				preparation.Candidate);
		string replacementProfilePath = existingProfilePath;

		if (string.Equals(
				mutationKind,
				"official",
				StringComparison.Ordinal))
		{
			environment[OfficialExecutableVariable] = executablePath;
		}
		else if (string.Equals(
			mutationKind,
			"profile-path",
			StringComparison.Ordinal))
		{
			replacementProfilePath =
				await CreateExistingReviewedProfileAsync(
					Path.Combine(
						temporaryDirectory.Path,
						"replacement"),
					executablePath,
					contract);
			environment[ProfileVariable] = replacementProfilePath;
		}
		else
		{
			Assert.Equal("profile-bytes", mutationKind);
			File.AppendAllText(existingProfilePath, Environment.NewLine);
		}

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => candidate.ApproveAsync());

		Assert.False(candidate.IsApproved);
		Assert.Equal(
			string.Equals(
				mutationKind,
				"official",
				StringComparison.Ordinal)
					? executablePath
					: null,
			environment.GetValueOrDefault(OfficialExecutableVariable));
		Assert.Equal(
			string.Equals(
				mutationKind,
				"profile-path",
				StringComparison.Ordinal)
					? replacementProfilePath
					: existingProfilePath,
			environment[ProfileVariable]);
		Assert.True(File.Exists(Assert.IsType<string>(
			environment[ProfileVariable])));
	}

	[Fact]
	public async Task ReviewedConPtyApproval_WhenEligibilityIsUnchanged_ApprovesPreparedProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityProductionUsageResult preview = new(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			"legacy@example.invalid",
			CreateWindows());
		(AntigravityMachineSetupService service,
			Dictionary<string, string?> environment,
			AntigravityReviewedPackageContract _,
			string _,
			string _,
			FakeProductionUsageClient usageClient) =
				await CreateReviewedConPtySetupHarnessAsync(
					temporaryDirectory.Path,
					preview);
		AntigravityMachineSetupPreparationResult preparation =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);
		Assert.True(preparation.IsSuccessful);
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(
				preparation.Candidate);

		await candidate.ApproveAsync();

		Assert.True(candidate.IsApproved);
		Assert.NotNull(usageClient.ObservedProfilePath);
		Assert.Equal(
			usageClient.ObservedProfilePath,
			environment[ProfileVariable]);
		Assert.Null(
			environment.GetValueOrDefault(OfficialExecutableVariable));
	}

	[Fact]
	public async Task PrepareAsync_WithOfficialPrintBuild_UsesOfficialPreviewAndApprovalMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		Dictionary<string, string?> environment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			[ProfileVariable] = @"C:\legacy\profile.json"
		};
		AntigravityProductionUsageResult preview = new(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			CreateWindows());
		FakeOfficialPrintUsageClient usageClient = new(preview);
		List<AntigravityMachineSetupProgress> progress = new();
		int validationCount = 0;
		AntigravityMachineSetupService service = new(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(executablePath, path);
				validationCount++;
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						"1.1.12",
						AntigravityExecutableLease.Acquire(path)));
			},
			usageClient,
			(name, target) =>
			{
				if (target == EnvironmentVariableTarget.Process)
				{
					Assert.Equal(OfficialExecutableVariable, name);
					return null;
				}

				Assert.Equal(EnvironmentVariableTarget.User, target);
				return environment.GetValueOrDefault(name);
			},
			(name, value, target) =>
			{
				Assert.Equal(EnvironmentVariableTarget.User, target);
				environment[name] = value;
			},
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				new InlineProgress<AntigravityMachineSetupProgress>(progress),
				CancellationToken.None);

		Assert.True(result.IsSuccessful);
		AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(result.Candidate);
		await using (candidate)
		{
			Assert.Equal("1.1.12", candidate.CliVersion);
			Assert.Equal(
				AntigravityMachineSetupSourceKind.OfficialPrint,
				candidate.SourceKind);
			Assert.Equal(executablePath, usageClient.ObservedExecutablePath);
			Assert.DoesNotContain(
				progress,
				item => item.Stage is
					AntigravityMachineSetupStage.PreparingPrivateStorage or
					AntigravityMachineSetupStage.CalibratingPrompt or
					AntigravityMachineSetupStage.MaterializingProfile);

			await candidate.ApproveAsync();
		}

		Assert.Equal(
			executablePath,
			environment[OfficialExecutableVariable]);
		Assert.Equal(2, validationCount);
		Assert.Equal(
			@"C:\legacy\profile.json",
			environment[ProfileVariable]);
	}

	[Theory]
	[InlineData(
		AntigravityProductionUsageFailureKind.ExecutionBusy,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.ExecutionBusy)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UnexpectedFailure,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		AntigravityUsageSafetyFailureReason.TimedOut,
		true,
		AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		AntigravityUsageSafetyFailureReason.AttemptInterrupted,
		false,
		AntigravityMachineSetupFailureKind.SafetyRevalidationRequired)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UsageShapeRejected,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.UsageRejected)]
	public async Task PrepareAsync_WithOfficialPrintPreviewFailure_PreservesExactFailure(
		AntigravityProductionUsageFailureKind previewFailureKind,
		AntigravityUsageSafetyFailureReason safetyFailureReason,
		bool automaticRevalidationPending,
		AntigravityMachineSetupFailureKind expectedSetupFailureKind)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		AntigravityProductionUsageResult preview = new(
			isSuccessful: false,
			previewFailureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			safetyFailureReason,
			automaticRevalidationPending);
		FakeOfficialPrintUsageClient usageClient = new(preview);
		AntigravityMachineSetupService service = new(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						"1.1.12",
						AntigravityExecutableLease.Acquire(path)));
			},
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })));

		AntigravityMachineSetupPreparationResult result =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(expectedSetupFailureKind, result.FailureKind);
		Assert.Equal(
			expectedSetupFailureKind ==
				AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			result.CanRevalidateSafety);
		AntigravityMachineSetupPreviewFailure previewFailure =
			Assert.IsType<AntigravityMachineSetupPreviewFailure>(
				result.PreviewFailure);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			previewFailure.SourceKind);
		Assert.Equal(previewFailureKind, previewFailure.FailureKind);
		Assert.Equal(
			safetyFailureReason,
			previewFailure.SafetyFailureReason);
		Assert.Equal(
			automaticRevalidationPending,
			previewFailure.AutomaticRevalidationPending);
		Assert.Equal(1, usageClient.CallCount);
	}

	[Fact]
	public async Task PrepareAsync_WithManualOfficialSafetyLatch_OnlyRevalidatesAfterExplicitRequestAndReturnsCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		AntigravityProductionUsageResult manualLatch = new(
			isSuccessful: false,
			AntigravityProductionUsageFailureKind.SafetyLatched,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			automaticRevalidationPending: false);
		AntigravityProductionUsageResult recovered = new(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			CreateWindows());
		FakeOfficialPrintUsageClient usageClient = new(
			manualLatch,
			recovered);
		AntigravityMachineSetupService service = CreateOfficialSetupService(
			executablePath,
			usageClient);

		AntigravityMachineSetupPreparationResult initial =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		Assert.False(initial.IsSuccessful);
		Assert.True(initial.CanRevalidateSafety);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			initial.FailureKind);
		Assert.Equal(1, usageClient.CaptureCallCount);
		Assert.Equal(0, usageClient.RevalidationCallCount);

		AntigravityMachineSetupPreparationResult result =
			await initial.RevalidateSafetyAsync();

		Assert.True(result.IsSuccessful);
		Assert.False(initial.CanRevalidateSafety);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => initial.RevalidateSafetyAsync());
		await using AntigravityMachineSetupCandidate candidate =
			Assert.IsType<AntigravityMachineSetupCandidate>(result.Candidate);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			candidate.SourceKind);
		Assert.Equal(1, usageClient.CaptureCallCount);
		Assert.Equal(1, usageClient.RevalidationCallCount);
	}

	[Fact]
	public async Task RevalidateSafetyAsync_WhenOfficialSafetyCheckFails_RemainsLatchedAndRevalidationCapable()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		AntigravityProductionUsageResult manualLatch = new(
			isSuccessful: false,
			AntigravityProductionUsageFailureKind.SafetyLatched,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			automaticRevalidationPending: false);
		AntigravityProductionUsageResult failedRevalidation = new(
			isSuccessful: false,
			AntigravityProductionUsageFailureKind.SafetyLatched,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			automaticRevalidationPending: false);
		FakeOfficialPrintUsageClient usageClient = new(
			manualLatch,
			failedRevalidation);
		AntigravityMachineSetupService service = CreateOfficialSetupService(
			executablePath,
			usageClient);
		AntigravityMachineSetupPreparationResult initial =
			await service.PrepareAsync(
				new AntigravityMachineSetupConsent(true, true),
				progress: null,
				CancellationToken.None);

		AntigravityMachineSetupPreparationResult result =
			await initial.RevalidateSafetyAsync();

		Assert.False(result.IsSuccessful);
		Assert.True(result.CanRevalidateSafety);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			result.FailureKind);
		Assert.Equal(1, usageClient.CaptureCallCount);
		Assert.Equal(1, usageClient.RevalidationCallCount);
	}

	[Fact]
	public void Candidate_WithExplicitOfficialSource_ExposesSourceKind()
	{
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.12",
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			CreateWindows(),
			_ => Task.CompletedTask,
			() => ValueTask.CompletedTask,
			AntigravityMachineSetupSourceKind.OfficialPrint);

		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			candidate.SourceKind);
	}

	[Fact]
	public void ApplyUserEnvironmentChanges_WhenSecondWriteFails_RollsBackBoth()
	{
		Dictionary<string, string?> environment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			[ProfileVariable] = @"C:\old\profile.json",
			[OfficialExecutableVariable] = @"C:\old\agy.exe"
		};
		bool rejectNewOfficialValue = true;

		Assert.Throws<IOException>(() =>
			AntigravityMachineSetupService.ApplyUserEnvironmentChanges(
				new[]
				{
					new AntigravityMachineSetupService.UserEnvironmentChange(
						ProfileVariable,
						@"C:\new\profile.json"),
					new AntigravityMachineSetupService.UserEnvironmentChange(
						OfficialExecutableVariable,
						Value: null)
				},
				(name, target) =>
				{
					Assert.Equal(EnvironmentVariableTarget.User, target);
					return environment.GetValueOrDefault(name);
				},
				(name, value, target) =>
				{
					Assert.Equal(EnvironmentVariableTarget.User, target);

					if (rejectNewOfficialValue &&
						string.Equals(
							name,
							OfficialExecutableVariable,
							StringComparison.OrdinalIgnoreCase) &&
						(value is null))
					{
						rejectNewOfficialValue = false;
						throw new IOException("Synthetic environment write failure.");
					}

					environment[name] = value;
				},
				CancellationToken.None));

		Assert.Equal(@"C:\old\profile.json", environment[ProfileVariable]);
		Assert.Equal(@"C:\old\agy.exe", environment[OfficialExecutableVariable]);
	}

	[Fact]
	public void ApplyUserEnvironmentChanges_WhenCommitFails_RollsBackMarker()
	{
		Dictionary<string, string?> environment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			[OfficialExecutableVariable] = @"C:\old\agy.exe"
		};

		Assert.Throws<InvalidOperationException>(() =>
			AntigravityMachineSetupService.ApplyUserEnvironmentChanges(
				new[]
				{
					new AntigravityMachineSetupService.UserEnvironmentChange(
						OfficialExecutableVariable,
						@"C:\new\agy.exe")
				},
				(name, _) => environment.GetValueOrDefault(name),
				(name, value, _) => environment[name] = value,
				CancellationToken.None,
				commit: () => throw new InvalidOperationException(
					"Synthetic commit failure.")));

		Assert.Equal(@"C:\old\agy.exe", environment[OfficialExecutableVariable]);
	}

	private static async Task<(
		AntigravityMachineSetupService Service,
		Dictionary<string, string?> Environment,
		AntigravityReviewedPackageContract Contract,
		string ExecutablePath,
		string ExistingProfilePath,
		FakeProductionUsageClient UsageClient)>
		CreateReviewedConPtySetupHarnessAsync(
			string rootPath,
			AntigravityProductionUsageResult preview)
	{
		string executablePath = Path.Combine(rootPath, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 1, 2, 3 });
		AntigravityReviewedPackageContract contract =
			AntigravityReviewedPackageManifestCatalog.Contracts[0];
		string existingProfilePath =
			await CreateExistingReviewedProfileAsync(
				Path.Combine(rootPath, "existing"),
				executablePath,
				contract);
		string localApplicationData = Path.Combine(rootPath, "local");
		string userProfile = Path.Combine(rootPath, "user");
		Directory.CreateDirectory(Path.Combine(
			localApplicationData,
			"AiUsageDashboard"));
		string settingsPath = Path.Combine(
			userProfile,
			".gemini",
			"antigravity-cli",
			"settings.json");
		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
		File.WriteAllText(settingsPath, "{}");
		Dictionary<string, string?> environment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			[ProfileVariable] = existingProfilePath
		};
		FakeOfficialPrintUsageClient officialUsageClient = new(
			new AntigravityProductionUsageResult(
				isSuccessful: true,
				AntigravityProductionUsageFailureKind.None,
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
				CreateWindows()));
		FakeProductionUsageClient reviewedUsageClient = new(preview);
		AntigravityMachineSetupService service = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: false)),
			officialUsageClient,
			(name, target) => target == EnvironmentVariableTarget.User
				? environment.GetValueOrDefault(name)
				: null,
			(name, value, target) =>
			{
				Assert.Equal(EnvironmentVariableTarget.User, target);
				environment[name] = value;
			},
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })),
			(path, observedContract, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(executablePath, path);
				Assert.Equal(contract.ContractId, observedContract.ContractId);
				return Task.FromResult(
					new AntigravityCliCapabilityValidationResult(
						isSupported: true,
						AntigravityCliCapabilityFailureReason.None,
						"Synthetic valid legacy candidate.",
						CreateReviewedFingerprint(path, observedContract),
						AntigravityExecutableLease.Acquire(path)));
			},
			CaptureExistingProfileSettingsBaselineAsync,
			reviewedUsageClient,
			folder => folder switch
			{
				Environment.SpecialFolder.LocalApplicationData =>
					localApplicationData,
				Environment.SpecialFolder.UserProfile => userProfile,
				_ => throw new ArgumentOutOfRangeException(nameof(folder))
			},
			(profile, cancellationToken) => Task.FromResult(
				CreateSafePromptCalibrationReport(
					profile,
					contract,
					cancellationToken)));
		return (
			service,
			environment,
			contract,
			executablePath,
			existingProfilePath,
			reviewedUsageClient);
	}

	private static AntigravityLiveR0Report CreateSafePromptCalibrationReport(
		AntigravityLiveR0Profile profile,
		AntigravityReviewedPackageContract contract,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		AntigravityUsageR1SectionSpec spec = contract.UsageLayout.Spec;
		AntigravityRedactedStructuralCapture promptCapture = new(
			profile.Columns,
			profile.Rows,
			spec.IsAlternateScreen,
			contract.ExpectedPromptStructuralFingerprint,
			Array.Empty<AntigravityRedactedLineShape>(),
			IsBracketedPasteEnabled: false,
			IsFocusReportingEnabled: false,
			IsWin32InputModeEnabled: false,
			ModifyOtherKeysLevel: 0);
		return new AntigravityLiveR0Report(
			AntigravityLiveR0Mode.CalibratePrompt,
			IsCaptureSuccessful: true,
			IsR0Go: true,
			ExistingProcessDetected: false,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.NotApplicable,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.NotVerified,
			AntigravityLiveR0GateStatus.NotVerified,
			InputWriteCount: 0,
			PromptExactLocalFingerprint: new string('B', 64),
			promptCapture,
			UsageCapture: null,
			Array.Empty<AntigravityLiveR0FailureReason>());
	}

	private static AntigravityMachineSetupService CreateOfficialSetupService(
		string executablePath,
		IAntigravityOfficialPrintUsageClient usageClient)
	{
		return new AntigravityMachineSetupService(
			(path, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						"1.1.12",
						AntigravityExecutableLease.Acquire(path)));
			},
			usageClient,
			(_, _) => null,
			(_, _, _) => { },
			_ => Task.FromResult<IReadOnlyList<string>>(
				Array.AsReadOnly(new[] { executablePath })));
	}

	private static async Task<string> CreateExistingReviewedProfileAsync(
		string rootPath,
		string executablePath,
		AntigravityReviewedPackageContract contract,
		bool hasValidLocalBinding = true)
	{
		string privateRoot = Path.Combine(
			rootPath,
			$"private-{Guid.NewGuid():N}");
		await using AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				contract.ContractId);
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			hmacKey = await File.ReadAllBytesAsync(transaction.KeyPath);
			AntigravityReviewedPackageUsageLayout reviewedLayout =
				contract.UsageLayout;
			AntigravityUsageR1SectionSpec spec = reviewedLayout.Spec;
			string settingsPath = Path.Combine(rootPath, "settings.json");
			File.WriteAllText(settingsPath, "{}");
			string profileId = $"existing-{contract.ContractId}";
			AntigravityCliFingerprint executableFingerprint =
				CreateReviewedFingerprint(executablePath, contract);
			Dictionary<string, string> environment = new();
			short columns = checked((short)spec.Columns);
			short rows = checked((short)spec.Rows);
			string exactPromptFingerprint = new('B', 64);
			IReadOnlyList<string> settingsFiles =
				Array.AsReadOnly(new[] { settingsPath });
			AntigravityLiveR0Profile captureProfile = new(
				profileId,
				executableFingerprint,
				rootPath,
				environment,
				columns,
				rows,
				contract.ExpectedPromptStructuralFingerprint,
				hmacKey,
				exactPromptFingerprint,
				ExpectedUsageStructuralFingerprint: null,
				settingsFiles,
				MaximumSavedOutputBytes: 4096,
				PromptTimeout: TimeSpan.FromSeconds(1),
				UsageTimeout: TimeSpan.FromSeconds(1),
				StableScreenDuration: TimeSpan.FromMilliseconds(100),
				PollInterval: TimeSpan.FromMilliseconds(10),
				CleanupTimeout: TimeSpan.FromSeconds(1),
				ExpectedExactUsageFingerprint: null);
			string captureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					captureProfile,
					spec.IsAlternateScreen,
					ExistingProfileSettingsBaselineFingerprint);
			string localBinding = hasValidLocalBinding
				? AntigravityReviewedPackageLocalBinding.Compute(
					contract.ContractFingerprint,
					captureContractFingerprint,
					hmacKey)
				: new string('C', 64);
			AntigravityUsageR1SectionLayoutFile localLayout = new(
				AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				spec,
				reviewedLayout.SchemaFingerprint,
				reviewedLayout.ExpectedPageFingerprints,
				captureContractFingerprint,
				localBinding);
			AntigravityLiveR1SectionProfileFile profile = new(
				AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
				profileId,
				executableFingerprint,
				captureProfile.WorkingDirectory,
				environment,
				columns,
				rows,
				new AntigravityLiveR1PromptGuard(
					contract.ExpectedPromptStructuralFingerprint,
					exactPromptFingerprint,
					transaction.KeyPath),
				localLayout,
				settingsFiles,
				captureProfile.MaximumSavedOutputBytes,
				captureProfile.PromptTimeout,
				captureProfile.UsageTimeout,
				captureProfile.StableScreenDuration,
				captureProfile.PollInterval,
				captureProfile.CleanupTimeout);
			profileBytes = AntigravityLiveR1SectionProfileJson.Serialize(profile);
			await transaction.WriteProfileAsync(profileBytes);
			transaction.MarkApproved();
			return transaction.ProfilePath;
		}
		finally
		{
			if (profileBytes is not null)
			{
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(
					profileBytes);
			}

			if (hmacKey is not null)
			{
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(
					hmacKey);
			}
		}
	}

	private static Task<string> CaptureExistingProfileSettingsBaselineAsync(
		AntigravityLiveR0Profile profile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(ExistingProfileSettingsBaselineFingerprint);
	}

	private static AntigravityCliCapabilityValidationResult
		CreateUnsupportedLegacyValidationResult()
	{
		return new AntigravityCliCapabilityValidationResult(
			isSupported: false,
			AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			"Synthetic unsupported legacy candidate.");
	}

	private static AntigravityCliFingerprint CreateReviewedFingerprint(
		string executablePath,
		AntigravityReviewedPackageContract contract)
	{
		AntigravityReviewedPackageExecutable executable = contract.Executable;
		return new AntigravityCliFingerprint(
			executablePath,
			executable.CliVersion,
			executable.FileVersion,
			executable.ProductVersion,
			executable.Sha256,
			executable.WinVerifyTrustStatus,
			executable.SignerSubject,
			executable.SignerThumbprint);
	}

	private static IReadOnlyList<AntigravityProductionUsageWindow>
		CreateWindows()
	{
		return Array.AsReadOnly(new[]
		{
			new AntigravityProductionUsageWindow(
				"agy.gemini.weekly",
				80m,
				TimeSpan.FromDays(2),
				IsAvailable: false),
			new AntigravityProductionUsageWindow(
				"agy.gemini.rolling-5h",
				60m,
				TimeSpan.FromHours(2),
				IsAvailable: false),
			new AntigravityProductionUsageWindow(
				"agy.claude.weekly",
				40m,
				TimeSpan.FromDays(3),
				IsAvailable: false),
			new AntigravityProductionUsageWindow(
				"agy.claude.rolling-5h",
				20m,
				TimeSpan.FromHours(1),
				IsAvailable: false)
		});
	}
}
