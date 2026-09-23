using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageProviderTests
{
	private sealed class FakeApprovedSourceStore :
		IAntigravityApprovedSourceStore
	{
		private AntigravityApprovedSource? _source;

		public AntigravityApprovedSource? Load()
		{
			return _source;
		}

		public void Save(AntigravityApprovedSource source)
		{
			_source = source;
		}

		public bool TrySaveIfMissing(AntigravityApprovedSource source)
		{
			if (_source is not null)
			{
				return false;
			}

			_source = source;
			return true;
		}

		public void Clear()
		{
			_source = null;
		}
	}

	private sealed class FakeAntigravityProductionUsageClient :
		IAntigravityOfficialPrintUsageClient
	{
		private readonly Func<
			string,
			CancellationToken,
			Task<AntigravityProductionUsageResult>> _captureAsync;

		internal int CallCount { get; private set; }

		internal FakeAntigravityProductionUsageClient(
			Func<
				string,
				CancellationToken,
				Task<AntigravityProductionUsageResult>> captureAsync)
		{
			_captureAsync = captureAsync;
		}

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			CallCount++;
			return _captureAsync(executablePath, cancellationToken);
		}

		public Task<AntigravityProductionUsageResult> RevalidateAsync(
			string executablePath,
			Action onSafetyTrackedAttemptStarted,
			CancellationToken cancellationToken)
		{
			onSafetyTrackedAttemptStarted();
			return CaptureAsync(executablePath, cancellationToken);
		}
	}

	private sealed class FakeAntigravityOfficialPrintUsageClient :
		IAntigravityOfficialPrintUsageClient
	{
		private readonly Func<
			string,
			CancellationToken,
			Task<AntigravityProductionUsageResult>> _captureAsync;

		internal int CallCount { get; private set; }

		internal int RevalidateCallCount { get; private set; }

		internal Func<int, bool> ShouldSignalSafetyTrackedAttemptStarted {
			get;
			set;
		} = _ => true;

		internal FakeAntigravityOfficialPrintUsageClient(
			Func<
				string,
				CancellationToken,
				Task<AntigravityProductionUsageResult>> captureAsync)
		{
			_captureAsync = captureAsync;
		}

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			CallCount++;
			return _captureAsync(executablePath, cancellationToken);
		}

		public Task<AntigravityProductionUsageResult> RevalidateAsync(
			string executablePath,
			Action onSafetyTrackedAttemptStarted,
			CancellationToken cancellationToken)
		{
			RevalidateCallCount++;

			if (ShouldSignalSafetyTrackedAttemptStarted(RevalidateCallCount))
			{
				onSafetyTrackedAttemptStarted();
			}

			return _captureAsync(executablePath, cancellationToken);
		}
	}

	private sealed class FakeOfficialExecutablePathResolver :
		IAntigravityOfficialExecutablePathResolver
	{
		private readonly Func<string?> _resolve;

		internal FakeOfficialExecutablePathResolver(Func<string?> resolve)
		{
			_resolve = resolve;
		}

		public string? ResolveExecutablePath()
		{
			return _resolve();
		}
	}

	private sealed class FakeProfilePathResolver :
		IAntigravityOfficialExecutablePathResolver
	{
		private readonly Func<string?> _resolve;

		internal FakeProfilePathResolver(Func<string?> resolve)
		{
			_resolve = resolve;
		}

		public string? ResolveExecutablePath()
		{
			return _resolve();
		}
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private long _timestamp;
		private DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		public override long GetTimestamp()
		{
			return _timestamp;
		}

		internal void Advance(TimeSpan value)
		{
			_utcNow += value;
			_timestamp += value.Ticks;
		}
	}

	private sealed class FakeMachineSetupService :
		IAntigravityMachineSetupService
	{
		private readonly Func<
			AntigravityMachineSetupConsent,
			CancellationToken,
			Task<AntigravityMachineSetupPreparationResult>> _prepareAsync;

		internal int CallCount { get; private set; }

		internal AntigravityMachineSetupConsent? LastConsent { get; private set; }

		internal FakeMachineSetupService(
			Func<
				AntigravityMachineSetupConsent,
				CancellationToken,
				Task<AntigravityMachineSetupPreparationResult>> prepareAsync)
		{
			_prepareAsync = prepareAsync;
		}

		public Task<AntigravityMachineSetupPreparationResult> PrepareAsync(
			AntigravityMachineSetupConsent consent,
			IProgress<AntigravityMachineSetupProgress>? progress,
			CancellationToken cancellationToken)
		{
			CallCount++;
			LastConsent = consent;
			return _prepareAsync(consent, cancellationToken);
		}
	}

	private const string SyntheticProfilePath =
		@"C:\synthetic\agy-profile.json";
	private const string SyntheticAccountIdentity = "agy@example.invalid";
	private const string SyntheticExecutablePath =
		@"C:\synthetic\agy.exe";
	private const string SyntheticUserProfilePath =
		@"C:\synthetic\user-agy-profile.json";

	[Fact]
	public void OfficialExecutablePathResolver_WithUserValue_PrefersUserValue()
	{
		string? requestedName = null;
		EnvironmentVariableTarget? requestedTarget = null;
		AntigravityOfficialExecutablePathResolver resolver = new(
			new FakeApprovedSourceStore(),
			_ => throw new InvalidOperationException(
				"Process-level fallback must not run when user value exists."),
			(name, target) =>
			{
				requestedName = name;
				requestedTarget = target;
				return SyntheticExecutablePath;
			});

		Assert.Equal(
			SyntheticExecutablePath,
			resolver.ResolveExecutablePath());
		Assert.Equal(
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
			requestedName);
		Assert.Equal(EnvironmentVariableTarget.User, requestedTarget);
	}

	[Fact]
	public void Constructor_RequiresOfficialClientAndResolver()
	{
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		FakeOfficialExecutablePathResolver officialResolver = new(() =>
			SyntheticExecutablePath);

		Assert.Throws<ArgumentNullException>(() => new AntigravityUsageProvider(
			null!,
			officialResolver));
		Assert.Throws<ArgumentNullException>(() => new AntigravityUsageProvider(
			officialClient,
			null!));
	}

	[Fact]
	public async Task GetUsageAsync_WithApprovedWindows_ReturnsOrderedOfficialExperimentalMetrics()
	{
		DateTimeOffset now = new(2026, 7, 18, 8, 30, 0, TimeSpan.Zero);
		FakeAntigravityProductionUsageClient client = new((profilePath, _) =>
		{
			Assert.Equal(SyntheticProfilePath, profilePath);
			return Task.FromResult(Success(
				new("agy.claude.rolling-5h", 0m, TimeSpan.Zero, false),
				new("agy.gemini.rolling-5h", 100m, null, true),
				new("agy.claude.weekly", 49.5m, TimeSpan.FromHours(12), false),
				new("agy.gemini.weekly", 75m, TimeSpan.FromHours(1), false)));
		});
		AntigravityUsageProvider provider = CreateProvider(client, now);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(ProviderKind.Antigravity, provider.Provider);
		Assert.Equal(TimeSpan.FromMinutes(1), provider.MinimumRefreshInterval);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Equal(now, snapshot.ObservedAt);
		Assert.Equal(now + TimeSpan.FromMinutes(2), snapshot.StaleAfter);
		Assert.Equal(
			SyntheticAccountIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.Error);
		Assert.Equal(UsageRecoveryAction.None, snapshot.RecoveryAction);
		Assert.Collection(
			snapshot.Metrics,
			metric => AssertMetric(
				metric,
				"agy.gemini.weekly",
				"Gemini weekly",
				25d,
				"已使用 25%",
				now + TimeSpan.FromHours(1),
				resetDisplayValue: null),
			metric => AssertMetric(
				metric,
				"agy.gemini.rolling-5h",
				"Gemini rolling 5h",
				0d,
				"已使用 0%",
				resetsAt: null,
				"目前可用"),
			metric => AssertMetric(
				metric,
				"agy.claude.weekly",
				"Claude + GPT weekly",
				50.5d,
				"已使用 50.5%",
				now + TimeSpan.FromHours(12),
				resetDisplayValue: null),
			metric => AssertMetric(
				metric,
				"agy.claude.rolling-5h",
				"Claude + GPT rolling 5h",
				100d,
				"已使用 100%",
				now,
				resetDisplayValue: null));
		Assert.Equal(1, client.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithOfficialMarker_UsesOfficialClientAndTrust()
	{
		DateTimeOffset now = new(2026, 8, 12, 8, 30, 0, TimeSpan.Zero);
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run when the official marker exists."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new(
			(executablePath, _) =>
			{
				Assert.Equal(SyntheticExecutablePath, executablePath);
				return Task.FromResult(Success(CreateValidWindows()));
			});
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(SyntheticAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(4, snapshot.Metrics.Count);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Theory]
	[InlineData(AntigravityUsageSafetyFailureReason.TimedOut)]
	[InlineData(AntigravityUsageSafetyFailureReason.CanceledAfterStart)]
	[InlineData(AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart)]
	[InlineData(AntigravityUsageSafetyFailureReason.NonZeroExit)]
	[InlineData(AntigravityUsageSafetyFailureReason.EmptyOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.StdoutTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.StderrTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.UnverifiableOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputJsonInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer)]
	public async Task GetUsageAsync_WithPendingAutomaticRevalidation_HidesManualButton(
		AntigravityUsageSafetyFailureReason reason)
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException("Legacy capture must not run."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				reason,
				automaticRevalidationPending: true)));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查尚未完成。AI Usage 稍後會自動再試，並保留現有帳號與用量資料。",
			UsageRecoveryAction.Retry);
	}

	[Fact]
	public async Task GetUsageAsync_WithUnverifiedOfficialOutputFailure_AppendsVersionDiagnostic()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run."));
		AntigravityOfficialPrintVersionAssessment versionAssessment =
			AntigravityOfficialPrintCapabilityValidator
				.AssessVersion("1.1.12");
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.OutputJsonInvalid,
				automaticRevalidationPending: true,
				officialPrintVersionAssessment: versionAssessment)));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查尚未完成。AI Usage 稍後會自動再試，並保留現有帳號與用量資料。 CLI 版本診斷：偵測到 Antigravity CLI 1.1.12；本版相容性基準為 1.1.11。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
			UsageRecoveryAction.Retry);
	}

	[Fact]
	public async Task RefreshAsync_WhenOfficialAgyTemporarilyFails_PreservesLastKnownGoodAndRecoversWithoutUiAction()
	{
		DateTimeOffset now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		int captureCount = 0;
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
		{
			int currentCapture = Interlocked.Increment(ref captureCount);

			if (currentCapture == 2)
			{
				return Task.FromResult(Failure(
					AntigravityProductionUsageFailureKind.SafetyLatched,
					AntigravityUsageSafetyFailureReason.TimedOut,
					automaticRevalidationPending: true));
			}

			AntigravityProductionUsageWindow[] windows = CreateValidWindows();

			if (currentCapture >= 3)
			{
				windows[0] = windows[0] with { RemainingPercent = 60m };
			}

			return Task.FromResult(Success(windows));
		});
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException("Legacy capture must not run."));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			timeProvider);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);
		UsageProviderRegistry registry = new(new IUsageProvider[] { provider });
		UsageRefreshCoordinator coordinator = new(registry, timeProvider);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		UsageSnapshot ready = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Ready, ready.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, ready.SourceTrust);
		Assert.Equal(4, ready.Metrics.Count);
		Assert.Equal(UsageRecoveryAction.None, ready.RecoveryAction);
		Assert.Equal(SyntheticAccountIdentity, ready.ProviderAccountIdentity);
		Assert.Equal(1, officialClient.CallCount);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		UsageSnapshot stale = await coordinator.RefreshAsync(account);

		Assert.Equal(SnapshotStatus.Stale, stale.Status);
		Assert.Equal(UsageRecoveryAction.Retry, stale.RecoveryAction);
		Assert.Equal(ready.SourceTrust, stale.SourceTrust);
		Assert.Equal(ready.FetchedAt, stale.FetchedAt);
		Assert.Equal(ready.ObservedAt, stale.ObservedAt);
		Assert.Equal(ready.ProviderAccountIdentity, stale.ProviderAccountIdentity);
		Assert.Equal(
			ready.Metrics.Select(metric => (
				metric.Key,
				metric.UsedPercent,
				metric.DisplayValue)),
			stale.Metrics.Select(metric => (
				metric.Key,
				metric.UsedPercent,
				metric.DisplayValue)));
		Assert.Contains("稍後會自動再試", stale.Error);
		Assert.Equal(2, officialClient.CallCount);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			coordinator.GetRemainingCooldown(account));

		viewModel.ApplySnapshot(stale);

		Assert.Equal(AccountStatusKind.Stale, viewModel.StatusKind);
		Assert.Equal(4, viewModel.UsageMetrics.Count);
		Assert.True(viewModel.HasRecoveryAction);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		Assert.False(viewModel.HasExecutableRecoveryAction);
		Assert.Equal(string.Empty, viewModel.RecoveryActionText);
		Assert.Equal(string.Empty, viewModel.NoticeText);
		Assert.Null(AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));

		timeProvider.Advance(TimeSpan.FromSeconds(59));
		UsageSnapshot cached = await coordinator.RefreshAsync(account);

		Assert.Equal(2, officialClient.CallCount);
		Assert.Equal(SnapshotStatus.Stale, cached.Status);
		Assert.Equal(
			stale.Metrics.Select(metric => metric.Key),
			cached.Metrics.Select(metric => metric.Key));
		Assert.Equal(
			TimeSpan.FromSeconds(1),
			coordinator.GetRemainingCooldown(account));

		timeProvider.Advance(TimeSpan.FromSeconds(1));
		UsageSnapshot recovered = await coordinator.RefreshAsync(account);
		viewModel.ApplySnapshot(recovered);

		Assert.Equal(3, officialClient.CallCount);
		Assert.Equal(0, officialClient.RevalidateCallCount);
		Assert.Equal(SnapshotStatus.Ready, recovered.Status);
		Assert.Equal(UsageRecoveryAction.None, recovered.RecoveryAction);
		Assert.Equal(
			40d,
			recovered.Metrics.Single(metric =>
				metric.Key == "agy.gemini.weekly").UsedPercent);
		Assert.False(viewModel.HasRecoveryAction);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		Assert.Equal(4, viewModel.UsageMetrics.Count);
	}

	[Fact]
	public async Task GetUsageAsync_WithManualOnlyTimeoutMarker_OffersManualSafetyRetry()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException("Legacy capture must not run."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.TimedOut)));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查已暫停：上次檢查逾時。AI Usage 不會自動再試。請按「重新檢查 Antigravity 用量」再試一次。",
			UsageRecoveryAction.RevalidateUsage);
	}

	[Fact]
	public async Task GetUsageAsync_WithLegacyManualNonZeroMarker_OffersSafetyRetry()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException("Legacy capture must not run."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.NonZeroExit)));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查已暫停：Antigravity 指令回報錯誤。AI Usage 不會自動再試。請按「重新檢查 Antigravity 用量」再試一次。",
			UsageRecoveryAction.RevalidateUsage);
	}

	[Fact]
	public async Task GetUsageAsync_WhenOfficialSafetyRevalidationIsArmed_UsesItExactlyOnce()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run when the official marker exists."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));
		AccountProfile account = CreateAccount();

		provider.ArmOfficialSafetyRevalidation(account.Id);
		await provider.GetUsageAsync(account, CancellationToken.None);
		await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(1, officialClient.RevalidateCallCount);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenArmedAccountStateIsPurged_DoesNotReuseRevalidationAuthorization()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run when the official marker exists."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));
		AccountProfile account = CreateAccount();

		provider.ArmOfficialSafetyRevalidation(account.Id);
		provider.PurgeAccountState(account.Id);
		await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(0, officialClient.RevalidateCallCount);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetUsageAsync_WhenArmedOfficialResolutionIsUnavailable_PreservesOneShotUntilOfficialResolutionSucceeds(
		bool throwDuringFirstResolution)
	{
		int resolutionCount = 0;
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() =>
				{
					resolutionCount++;

					if (resolutionCount != 1)
					{
						return SyntheticExecutablePath;
					}

					if (throwDuringFirstResolution)
					{
						throw new InvalidOperationException(
							"Synthetic resolution failure.");
					}

					return null;
				}),
			new FakeTimeProvider(DateTimeOffset.UtcNow));
		AccountProfile account = CreateAccount();

		provider.ArmOfficialSafetyRevalidation(account.Id);
		await provider.GetUsageAsync(account, CancellationToken.None);
		await provider.GetUsageAsync(account, CancellationToken.None);
		await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(1, officialClient.RevalidateCallCount);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenArmedOfficialPreflightFails_PreservesOneShotUntilAttemptStarts()
	{
		int officialResultCount = 0;
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run when the official marker exists."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(
				Interlocked.Increment(ref officialResultCount) == 1
					? Failure(
						AntigravityProductionUsageFailureKind.ProvenanceRejected)
					: Success(CreateValidWindows())))
		{
			ShouldSignalSafetyTrackedAttemptStarted = revalidateCallCount =>
				revalidateCallCount != 1
		};
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		provider.ArmOfficialSafetyRevalidation(account.Id);
		UsageSnapshot preflightFailure = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot revalidated = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		_ = await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, preflightFailure.Status);
		Assert.Equal(SnapshotStatus.Ready, revalidated.Status);
		Assert.Equal(2, officialClient.RevalidateCallCount);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithArmedDifferentAccount_DoesNotConsumeOneShot()
	{
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Legacy capture must not run when the official marker exists."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new((_, _) =>
			Task.FromResult(Success(CreateValidWindows())));
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => SyntheticExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));
		AccountProfile armedAccount = CreateAccount();
		AccountProfile differentAccount = CreateAccount();

		provider.ArmOfficialSafetyRevalidation(armedAccount.Id);
		await provider.GetUsageAsync(differentAccount, CancellationToken.None);
		await provider.GetUsageAsync(armedAccount, CancellationToken.None);

		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(1, officialClient.RevalidateCallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithInvalidOfficialMarker_DoesNotFallbackToLegacy()
	{
		const string InvalidExecutablePath = "agy.exe";
		FakeAntigravityProductionUsageClient legacyClient = new((_, _) =>
			throw new InvalidOperationException(
				"Invalid official configuration must fail closed."));
		FakeAntigravityOfficialPrintUsageClient officialClient = new(
			(executablePath, _) =>
			{
				Assert.Equal(InvalidExecutablePath, executablePath);
				return Task.FromResult(Failure(
					AntigravityProductionUsageFailureKind.ProvenanceRejected));
			});
		AntigravityUsageProvider provider = new(
			officialClient,
			new FakeOfficialExecutablePathResolver(() => InvalidExecutablePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 無法確認這次 Antigravity 用量讀取是否仍符合安全條件，稍後會自動重新檢查；既有登入不受影響。",
			UsageRecoveryAction.Retry);
		Assert.Equal(1, officialClient.CallCount);
		Assert.Equal(0, legacyClient.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithoutProfile_ReturnsNotConfiguredWithoutCallingClient()
	{
		DateTimeOffset now = new(2026, 7, 18, 8, 30, 0, TimeSpan.Zero);
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			throw new InvalidOperationException());
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => null),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal("尚未連接 Antigravity 帳號。", snapshot.Error);
		Assert.Empty(snapshot.Metrics);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Equal(
			UsageRecoveryAction.ReconfigureUsageSource,
			snapshot.RecoveryAction);
		Assert.Equal(0, client.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenProfileResolutionFails_ReturnsSafeReconfigureAction()
	{
		const string PrivateSentinel = "PRIVATE-PROFILE-PATH";
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			throw new InvalidOperationException());
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() =>
				throw new IOException(PrivateSentinel)),
			new FakeTimeProvider(DateTimeOffset.UtcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 無法確認 Antigravity 的用量讀取。請重新連接 Antigravity 帳號；既有登入不受影響。",
			UsageRecoveryAction.ReconfigureUsageSource);
		Assert.DoesNotContain(
			PrivateSentinel,
			snapshot.Error,
			StringComparison.Ordinal);
		Assert.Equal(0, client.CallCount);
	}

	[Theory]
	[InlineData(
		AntigravityProductionUsageFailureKind.ProfileRejected,
		"AI Usage 無法確認 Antigravity 的用量讀取。請重新連接 Antigravity 帳號；既有登入不受影響。",
		UsageRecoveryAction.ReconfigureUsageSource)]
	[InlineData(
		AntigravityProductionUsageFailureKind.ProvenanceRejected,
		"AI Usage 無法確認這次 Antigravity 用量讀取是否仍符合安全條件，稍後會自動重新檢查；既有登入不受影響。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityProductionUsageFailureKind.CaptureRejected,
		"Antigravity 用量檢查未完成，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityProductionUsageFailureKind.ExecutionBusy,
		"Antigravity 用量檢查未完成，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UsageShapeRejected,
		"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
		UsageRecoveryAction.UpdateApplication)]
	[InlineData(
		AntigravityProductionUsageFailureKind.SafetyLatched,
		"Antigravity 用量檢查已暫停：無法確認上次檢查是否完成。AI Usage 不會自動再試。請按「重新檢查 Antigravity 用量」再試一次。",
		UsageRecoveryAction.RevalidateUsage)]
	[InlineData(
		AntigravityProductionUsageFailureKind.UnexpectedFailure,
		"暫時無法讀取 Antigravity 用量，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	public async Task GetUsageAsync_WhenClientRejectsCapture_ReturnsSafeActionableError(
		AntigravityProductionUsageFailureKind failureKind,
		string expectedError,
		UsageRecoveryAction expectedRecoveryAction)
	{
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(failureKind)));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			expectedError,
			 expectedRecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenExecutionGateIsBusy_DoesNotStartSetupRepair()
	{
		FakeMachineSetupService setupService = new((_, _) =>
			throw new InvalidOperationException(
				"Execution contention must not start AGY setup repair."));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.ExecutionBusy)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查未完成，稍後會自動再試。",
			UsageRecoveryAction.Retry);
		Assert.Equal(0, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenReviewedBuildChanges_RepairsAndReturnsCandidateUsage()
	{
		DateTimeOffset now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.9",
			SyntheticAccountIdentity,
			CreateValidWindows(),
			_ =>
			{
				approveCount++;
				return Task.CompletedTask;
			},
			() =>
			{
				releaseCount++;
				return ValueTask.CompletedTask;
			});
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(candidate)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(now),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(SyntheticAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(4, snapshot.Metrics.Count);
		Assert.Equal(1, client.CallCount);
		Assert.Equal(1, setupService.CallCount);
		Assert.True(setupService.LastConsent?.IsOfficialUsageReadApproved);
		Assert.Equal(1, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.True(candidate.IsApproved);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairUsesOfficialPrint_UsesOfficialTrust()
	{
		DateTimeOffset now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.12",
			SyntheticAccountIdentity,
			CreateValidWindows(),
			_ => Task.CompletedTask,
			() => ValueTask.CompletedTask,
			sourceKind: AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(candidate)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.ProvenanceRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(now),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(SyntheticAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.True(candidate.IsApproved);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairableFailureHasNoExpectedIdentity_RequiresConnection()
	{
		FakeMachineSetupService setupService = new((_, _) =>
			throw new InvalidOperationException(
				"Blank identity must not start automatic repair."));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal("尚未連接 Antigravity 帳號。", snapshot.Error);
		Assert.Equal(
			UsageRecoveryAction.ConnectAccount,
			snapshot.RecoveryAction);
		Assert.Empty(snapshot.Metrics);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Equal(1, client.CallCount);
		Assert.Equal(0, setupService.CallCount);
	}

	[Theory]
	[InlineData(
		false,
		"Antigravity 用量檢查未完成，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		true,
		"找不到 Antigravity CLI 或執行所需檔案。請確認 Antigravity 已安裝且可正常啟動；既有登入不受影響。",
		UsageRecoveryAction.InstallOrUpdate)]
	public async Task GetUsageAsync_WhenRepairApprovalFails_ProjectsActionAcrossThrottle(
		bool isMissingBuild,
		string expectedError,
		UsageRecoveryAction expectedRecoveryAction)
	{
		int releaseCount = 0;
		Exception approvalFailure = isMissingBuild
			? new FileNotFoundException("Synthetic AGY executable disappeared.")
			: new InvalidOperationException("Synthetic approval was rejected.");
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.9",
			SyntheticAccountIdentity,
			CreateValidWindows(),
			_ => Task.FromException(approvalFailure),
			() =>
			{
				releaseCount++;
				return ValueTask.CompletedTask;
			});
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(candidate)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot first = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot throttled = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			first,
			expectedError,
			expectedRecoveryAction);
		AssertSafeCaptureFailure(
			throttled,
			expectedError,
			expectedRecoveryAction);
		Assert.Equal(2, client.CallCount);
		Assert.Equal(1, setupService.CallCount);
		Assert.Equal(1, releaseCount);
		Assert.False(candidate.IsApproved);
	}

	[Fact]
	public async Task GetUsageAsync_WhenAnotherAccountRepairs_DoesNotThrottleThisAccount()
	{
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					AntigravityMachineSetupFailureKind.UnsupportedBuild)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile firstAccount = CreateAccount("first@example.invalid");
		AccountProfile secondAccount = CreateAccount("second@example.invalid");

		UsageSnapshot first = await provider.GetUsageAsync(
			firstAccount,
			CancellationToken.None);
		UsageSnapshot second = await provider.GetUsageAsync(
			secondAccount,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			first,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			second,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		Assert.Equal(2, client.CallCount);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenExpectedIdentityChanges_DoesNotReplayPriorFailure()
	{
		int preparationCount = 0;
		FakeMachineSetupService setupService = new((_, _) =>
		{
			preparationCount++;
			return Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					preparationCount == 1
						? AntigravityMachineSetupFailureKind.UnsupportedBuild
						: AntigravityMachineSetupFailureKind.AmbiguousExecutable));
		});
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile original = CreateAccount("old@example.invalid");
		AccountProfile changed = original with
		{
			ProviderAccountIdentity = "new@example.invalid"
		};

		UsageSnapshot beforeChange = await provider.GetUsageAsync(
			original,
			CancellationToken.None);
		UsageSnapshot afterChange = await provider.GetUsageAsync(
			changed,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			beforeChange,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			afterChange,
			"AI Usage 無法恢復 Antigravity 用量檢查。請重新連接 Antigravity 帳號；既有登入不受影響。",
			UsageRecoveryAction.ReconfigureUsageSource);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenExpectedIdentityIsEquivalent_PreservesRepairScope()
	{
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					AntigravityMachineSetupFailureKind.UnsupportedBuild)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile original = CreateAccount(" AGY@Example.Invalid ");
		AccountProfile equivalent = original with
		{
			ProviderAccountIdentity = "agy@example.invalid"
		};

		UsageSnapshot first = await provider.GetUsageAsync(
			original,
			CancellationToken.None);
		UsageSnapshot throttled = await provider.GetUsageAsync(
			equivalent,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			first,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			throttled,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		Assert.Equal(2, client.CallCount);
		Assert.Equal(1, setupService.CallCount);
	}

	[Fact]
	public async Task Invalidate_ClearsRecordedAutomaticRepairFailure()
	{
		int preparationCount = 0;
		FakeMachineSetupService setupService = new((_, _) =>
		{
			preparationCount++;
			return Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					preparationCount == 1
						? AntigravityMachineSetupFailureKind.UnsupportedBuild
						: AntigravityMachineSetupFailureKind.AmbiguousExecutable));
		});
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot beforeInvalidation = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		provider.Invalidate(account.Id);
		UsageSnapshot afterInvalidation = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			beforeInvalidation,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			afterInvalidation,
			"AI Usage 無法恢復 Antigravity 用量檢查。請重新連接 Antigravity 帳號；既有登入不受影響。",
			UsageRecoveryAction.ReconfigureUsageSource);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenSuccessClearsRepairFailure_NextFailureRepairsImmediately()
	{
		int captureCount = 0;
		FakeAntigravityProductionUsageClient client = new((_, _) =>
		{
			captureCount++;
			return Task.FromResult(captureCount == 2
				? Success(CreateValidWindows())
				: Failure(
					AntigravityProductionUsageFailureKind.CaptureRejected));
		});
		int preparationCount = 0;
		FakeMachineSetupService setupService = new((_, _) =>
		{
			preparationCount++;
			return Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					preparationCount == 1
						? AntigravityMachineSetupFailureKind.UnsupportedBuild
						: AntigravityMachineSetupFailureKind.AmbiguousExecutable));
		});
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot firstFailure = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot success = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot nextFailure = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			firstFailure,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		Assert.Equal(SnapshotStatus.Ready, success.Status);
		AssertSafeCaptureFailure(
			nextFailure,
			"AI Usage 無法恢復 Antigravity 用量檢查。請重新連接 Antigravity 帳號；既有登入不受影響。",
			UsageRecoveryAction.ReconfigureUsageSource);
		Assert.Equal(3, client.CallCount);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairIdentityDiffers_ProjectsSwitchAccountAcrossThrottle()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.9",
			"different@example.invalid",
			CreateValidWindows(),
			_ =>
			{
				approveCount++;
				return Task.CompletedTask;
			},
			() =>
			{
				releaseCount++;
				return ValueTask.CompletedTask;
			});
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(candidate)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.ProvenanceRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot throttledSnapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 回報的帳號與目前連接的帳號不同，已忽略這次用量。請重新連接正確的帳號。",
			UsageRecoveryAction.SwitchAccount);
		AssertSafeCaptureFailure(
			throttledSnapshot,
			"Antigravity 回報的帳號與目前連接的帳號不同，已忽略這次用量。請重新連接正確的帳號。",
			UsageRecoveryAction.SwitchAccount);
		Assert.Equal(1, setupService.CallCount);
		Assert.Equal(2, client.CallCount);
		Assert.Equal(0, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.False(candidate.IsApproved);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairCandidateShapeIsInvalid_ProjectsInstallOrUpdate()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityProductionUsageWindow[] invalidWindows = CreateValidWindows();
		invalidWindows[1] = invalidWindows[0] with
		{
			RemainingPercent = 65m
		};
		AntigravityMachineSetupCandidate candidate = new(
			"1.1.9",
			SyntheticAccountIdentity,
			invalidWindows,
			_ =>
			{
				approveCount++;
				return Task.CompletedTask;
			},
			() =>
			{
				releaseCount++;
				return ValueTask.CompletedTask;
			});
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(candidate)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.ProvenanceRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.UpdateApplication);
		Assert.Equal(1, setupService.CallCount);
		Assert.Equal(0, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.False(candidate.IsApproved);
	}

	[Theory]
	[InlineData(AntigravityMachineSetupFailureKind.ConsentRequired)]
	[InlineData(AntigravityMachineSetupFailureKind.AmbiguousExecutable)]
	[InlineData(AntigravityMachineSetupFailureKind.ExistingProcessDetected)]
	[InlineData(AntigravityMachineSetupFailureKind.SettingsRejected)]
	[InlineData(AntigravityMachineSetupFailureKind.PromptRejected)]
	public async Task GetUsageAsync_WhenRepairPreparationNeedsManualSetup_ProjectsReconfigure(
		AntigravityMachineSetupFailureKind failureKind)
	{
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(failureKind)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 無法恢復 Antigravity 用量檢查。請重新連接 Antigravity 帳號；既有登入不受影響。",
			UsageRecoveryAction.ReconfigureUsageSource);
		Assert.Equal(1, setupService.CallCount);
	}

	[Theory]
	[InlineData(AntigravityMachineSetupFailureKind.ExecutionBusy)]
	[InlineData(AntigravityMachineSetupFailureKind.PrivateStorageRejected)]
	[InlineData(AntigravityMachineSetupFailureKind.ApprovalRejected)]
	[InlineData(AntigravityMachineSetupFailureKind.UnexpectedFailure)]
	public async Task GetUsageAsync_WhenRepairPreparationFailureIsTransient_KeepsAutomaticRetry(
		AntigravityMachineSetupFailureKind failureKind)
	{
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					failureKind)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查未完成，稍後會自動再試。",
			UsageRecoveryAction.Retry);
		Assert.Equal(1, setupService.CallCount);
	}

	[Theory]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.ExecutionBusy,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.ExecutionBusy,
		"Antigravity 用量檢查未完成，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.UnexpectedFailure,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.UnexpectedFailure,
		"暫時無法讀取 Antigravity 用量，稍後會自動再試。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.ProvenanceRejected,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.UnexpectedFailure,
		"AI Usage 無法確認這次 Antigravity 用量讀取是否仍符合安全條件，稍後會自動重新檢查；既有登入不受影響。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.SafetyLatched,
		AntigravityUsageSafetyFailureReason.TimedOut,
		true,
		AntigravityMachineSetupFailureKind.UnexpectedFailure,
		"Antigravity 用量檢查尚未完成。AI Usage 稍後會自動再試，並保留現有帳號與用量資料。",
		UsageRecoveryAction.Retry)]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.ProfileRejected,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.SettingsRejected,
		"AI Usage 無法確認 Antigravity 的用量讀取。請重新連接 Antigravity 帳號；既有登入不受影響。",
		UsageRecoveryAction.ReconfigureUsageSource)]
	[InlineData(
		AntigravityMachineSetupSourceKind.OfficialPrint,
		AntigravityProductionUsageFailureKind.UsageShapeRejected,
		AntigravityUsageSafetyFailureReason.None,
		false,
		AntigravityMachineSetupFailureKind.UsageRejected,
		"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
		UsageRecoveryAction.UpdateApplication)]
	public async Task GetUsageAsync_WhenRepairPreviewFails_PreservesExactPresentation(
		AntigravityMachineSetupSourceKind sourceKind,
		AntigravityProductionUsageFailureKind previewFailureKind,
		AntigravityUsageSafetyFailureReason safetyFailureReason,
		bool automaticRevalidationPending,
		AntigravityMachineSetupFailureKind setupFailureKind,
		string expectedError,
		UsageRecoveryAction expectedRecoveryAction)
	{
		AntigravityProductionUsageResult preview = new(
			isSuccessful: false,
			previewFailureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			safetyFailureReason,
			automaticRevalidationPending);
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					setupFailureKind,
					sourceKind,
					preview)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			expectedError,
			expectedRecoveryAction);
		Assert.Equal(1, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairServiceThrows_KeepsAutomaticRetry()
	{
		FakeMachineSetupService setupService = new((_, _) =>
			throw new IOException("Synthetic transient setup failure."));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(DateTimeOffset.UtcNow),
			setupService);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(SyntheticAccountIdentity),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"Antigravity 用量檢查未完成，稍後會自動再試。",
			UsageRecoveryAction.Retry);
		Assert.Equal(1, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRepairCannotStart_PreservesInstallOrUpdateAcrossThrottle()
	{
		FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
		FakeMachineSetupService setupService = new((_, _) =>
			Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					AntigravityMachineSetupFailureKind.UnsupportedBuild)));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			timeProvider,
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot first = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot throttled = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		timeProvider.Advance(TimeSpan.FromMinutes(15));
		UsageSnapshot retried = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			first,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			throttled,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			retried,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		Assert.Equal(3, client.CallCount);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenNewRepairAttemptIsTransient_DoesNotReplayPriorManualAction()
	{
		FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
		int preparationCount = 0;
		FakeMachineSetupService setupService = new((_, _) =>
		{
			preparationCount++;
			return Task.FromResult(
				new AntigravityMachineSetupPreparationResult(
					preparationCount == 1
						? AntigravityMachineSetupFailureKind.UnsupportedBuild
						: AntigravityMachineSetupFailureKind.UnexpectedFailure));
		});
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected)));
		AntigravityUsageProvider provider = new(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			timeProvider,
			setupService);
		AccountProfile account = CreateAccount(SyntheticAccountIdentity);

		UsageSnapshot hardFailure = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		UsageSnapshot throttled = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		timeProvider.Advance(TimeSpan.FromMinutes(15));
		UsageSnapshot transientRetry = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		AssertSafeCaptureFailure(
			hardFailure,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			throttled,
			"目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請更新 Antigravity 或 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.InstallOrUpdate);
		AssertSafeCaptureFailure(
			transientRetry,
			"Antigravity 用量檢查未完成，稍後會自動再試。",
			UsageRecoveryAction.Retry);
		Assert.Equal(2, setupService.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenClientThrows_DoesNotExposeExceptionOrProfilePath()
	{
		const string PrivateSentinel =
			"PRIVATE-RAW-IDENTITY-QUOTA-COUNTDOWN-CREDIT-PATH";
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			throw new IOException(PrivateSentinel));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"暫時無法讀取 Antigravity 用量，稍後會自動再試。",
			UsageRecoveryAction.Retry);
		Assert.DoesNotContain(
			PrivateSentinel,
			snapshot.Error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			SyntheticProfilePath,
			snapshot.Error,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetUsageAsync_WithUnknownOrDuplicateWindow_FailsClosed()
	{
		AntigravityProductionUsageResult invalid = Success(
			new("agy.gemini.weekly", 75m, TimeSpan.FromHours(1), false),
			new("agy.gemini.weekly", 70m, TimeSpan.FromHours(2), false),
			new("agy.claude.weekly", 50m, TimeSpan.FromHours(3), false),
			new("unknown.window", 40m, TimeSpan.FromHours(4), false));
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(invalid));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.UpdateApplication);
	}

	[Theory]
	[InlineData(-0.01)]
	[InlineData(100.01)]
	public async Task GetUsageAsync_WithOutOfRangeRemainingPercent_FailsClosed(
		double invalidRemainingPercent)
	{
		AntigravityProductionUsageWindow[] windows = CreateValidWindows();
		windows[0] = windows[0] with
		{
			RemainingPercent = (decimal)invalidRemainingPercent
		};
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Success(windows)));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.UpdateApplication);
	}

	[Fact]
	public async Task GetUsageAsync_WithAmbiguousResetState_FailsClosed()
	{
		AntigravityProductionUsageWindow[] windows = CreateValidWindows();
		windows[1] = windows[1] with
		{
			IsAvailable = true,
			ResetsIn = TimeSpan.FromMinutes(1)
		};
		FakeAntigravityProductionUsageClient client = new((_, _) =>
			Task.FromResult(Success(windows)));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		AssertSafeCaptureFailure(
			snapshot,
			"AI Usage 目前無法讀取 Antigravity 回傳的用量格式。請更新 AI Usage；既有登入不受影響。",
			UsageRecoveryAction.UpdateApplication);
	}

	[Fact]
	public async Task GetUsageAsync_ConcurrentRequests_SerializesClientCapture()
	{
		TaskCompletionSource firstCaptureStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseFirstCapture = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int activeCaptures = 0;
		int callSequence = 0;
		int maximumActiveCaptures = 0;
		bool clientCallIsFirst()
		{
			return Interlocked.Increment(ref callSequence) == 1;
		}
		FakeAntigravityProductionUsageClient client = new(async (_, _) =>
		{
			int active = Interlocked.Increment(ref activeCaptures);
			maximumActiveCaptures = Math.Max(maximumActiveCaptures, active);

			try
			{
				if (clientCallIsFirst())
				{
					firstCaptureStarted.TrySetResult();
					await releaseFirstCapture.Task;
				}

				return Success(CreateValidWindows());
			}
			finally
			{
				Interlocked.Decrement(ref activeCaptures);
			}
		});
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);

		Task<UsageSnapshot> first = provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);
		await firstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<UsageSnapshot> second = provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);
		await Task.Delay(50);

		Assert.Equal(1, client.CallCount);
		releaseFirstCapture.TrySetResult();
		UsageSnapshot[] snapshots = await Task.WhenAll(first, second);

		Assert.Equal(2, client.CallCount);
		Assert.Equal(1, maximumActiveCaptures);
		Assert.All(
			snapshots,
			snapshot => Assert.Equal(SnapshotStatus.Ready, snapshot.Status));
	}

	[Fact]
	public async Task GetUsageAsync_WhenCanceled_PropagatesCancellation()
	{
		FakeAntigravityProductionUsageClient client = new((_, token) =>
			Task.FromCanceled<AntigravityProductionUsageResult>(token));
		AntigravityUsageProvider provider = CreateProvider(
			client,
			DateTimeOffset.UtcNow);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.GetUsageAsync(CreateAccount(), cancellation.Token));
	}

	private static void AssertMetric(
		UsageMetric metric,
		string expectedKey,
		string expectedLabel,
		double expectedUsedPercent,
		string expectedDisplayValue,
		DateTimeOffset? resetsAt,
		string? resetDisplayValue)
	{
		Assert.Equal(expectedKey, metric.Key);
		Assert.Equal(expectedLabel, metric.Label);
		Assert.Equal(expectedUsedPercent, metric.UsedPercent);
		Assert.Equal(expectedDisplayValue, metric.DisplayValue);
		Assert.Equal(resetsAt, metric.ResetsAt);
		Assert.Equal(resetDisplayValue, metric.ResetDisplayValue);
	}

	private static void AssertSafeCaptureFailure(
		UsageSnapshot snapshot,
		string expectedError,
		UsageRecoveryAction expectedRecoveryAction)
	{
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(expectedError, snapshot.Error);
		Assert.Equal(expectedRecoveryAction, snapshot.RecoveryAction);
		Assert.Empty(snapshot.Metrics);
		Assert.Null(snapshot.ProviderAccountIdentity);
	}

	private static AccountProfile CreateAccount(
		string? providerAccountIdentity = null)
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"本機 AGY",
			ProviderAccountIdentity: providerAccountIdentity);
	}

	private static AntigravityUsageProvider CreateProvider(
		IAntigravityOfficialPrintUsageClient client,
		DateTimeOffset now)
	{
		return new AntigravityUsageProvider(
			client,
			new FakeProfilePathResolver(() => SyntheticProfilePath),
			new FakeTimeProvider(now));
	}

	private static AntigravityProductionUsageWindow[] CreateValidWindows()
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

	private static AntigravityProductionUsageResult Failure(
		AntigravityProductionUsageFailureKind failureKind,
		AntigravityUsageSafetyFailureReason safetyFailureReason =
			AntigravityUsageSafetyFailureReason.None,
		bool automaticRevalidationPending = false,
		AntigravityOfficialPrintVersionAssessment?
			officialPrintVersionAssessment = null)
	{
		return new AntigravityProductionUsageResult(
			isSuccessful: false,
			failureKind,
			accountIdentity: null,
			Array.Empty<AntigravityProductionUsageWindow>(),
			safetyFailureReason,
			automaticRevalidationPending,
			officialPrintVersionAssessment);
	}

	private static AntigravityProductionUsageResult Success(
		params AntigravityProductionUsageWindow[] windows)
	{
		return new AntigravityProductionUsageResult(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			SyntheticAccountIdentity,
			windows);
	}
}
