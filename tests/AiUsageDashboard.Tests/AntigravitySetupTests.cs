using AiUsageDashboard.Antigravity.Setup;
using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class AntigravitySetupTests
{
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
			AntigravityMachineSetupPreparationResult result)
			: this((_, _) => Task.FromResult(result))
		{
		}

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

	[Fact]
	public async Task PrepareAsync_WhenConsentIsMissing_DoesNotCallServiceOrApprove()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => releaseCount++);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: false);
		bool approved = await workflow.ApproveAsync(
			isReviewConfirmed: true);

		Assert.Equal(0, service.CallCount);
		Assert.False(approved);
		Assert.Equal(0, approveCount);
		Assert.Equal(0, releaseCount);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.ConsentRequired,
			workflow.FailureKind);
		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
	}

	[Fact]
	public async Task PrepareAsync_WhenExecutionIsBusy_RetriesWithBoundedBackoffUntilReady()
	{
		int preparationCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			approve: () => { },
			release: () => { });
		FakeMachineSetupService service = new((_, _) =>
		{
			preparationCount++;
			return Task.FromResult(preparationCount < 3
				? new AntigravityMachineSetupPreparationResult(
					AntigravityMachineSetupFailureKind.ExecutionBusy)
				: new AntigravityMachineSetupPreparationResult(candidate));
		});
		List<TimeSpan> delays = new();
		await using AntigravitySetupWorkflow workflow = new(
			service,
			(delay, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				delays.Add(delay);
				return Task.CompletedTask;
			});

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.Equal(3, service.CallCount);
		Assert.Equal(
			new[]
			{
				TimeSpan.FromMilliseconds(250),
				TimeSpan.FromMilliseconds(500)
			},
			delays);
		Assert.Same(candidate, workflow.Candidate);
		Assert.Equal(
			AntigravitySetupWorkflowState.Reviewing,
			workflow.State);
	}

	[Fact]
	public async Task PrepareAsync_WhenExecutionRemainsBusy_StopsAfterBoundedAttempts()
	{
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(
				AntigravityMachineSetupFailureKind.ExecutionBusy));
		List<TimeSpan> delays = new();
		await using AntigravitySetupWorkflow workflow = new(
			service,
			(delay, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				delays.Add(delay);
				return Task.CompletedTask;
			});

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.Equal(3, service.CallCount);
		Assert.Equal(2, delays.Count);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.ExecutionBusy,
			workflow.FailureKind);
		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
	}

	[Fact]
	public async Task PrepareAsync_WhenFailureIsNotTransient_DoesNotRetry()
	{
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(
				AntigravityMachineSetupFailureKind.UnsupportedBuild));
		int delayCount = 0;
		await using AntigravitySetupWorkflow workflow = new(
			service,
			(_, _) =>
			{
				delayCount++;
				return Task.CompletedTask;
			});

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.Equal(1, service.CallCount);
		Assert.Equal(0, delayCount);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			workflow.FailureKind);
	}

	[Fact]
	public async Task RevalidateSafetyAsync_WithExplicitUserAction_ContinuesSetupAfterSuccessfulCheck()
	{
		int revalidationCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			approve: () => { },
			release: () => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		AntigravityMachineSetupPreparationResult safetyFailure =
			CreateSafetyRevalidationResult(_ =>
			{
				revalidationCount++;
				return Task.FromResult(
					new AntigravityMachineSetupPreparationResult(candidate));
			});
		FakeMachineSetupService service = new(safetyFailure);
		await using AntigravitySetupWorkflow workflow = new(service);

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.Equal(0, revalidationCount);
		Assert.True(workflow.CanRevalidateSafety);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			workflow.FailureKind);
		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);

		Assert.True(await workflow.RevalidateSafetyAsync());

		Assert.Equal(1, revalidationCount);
		Assert.False(workflow.CanRevalidateSafety);
		Assert.Same(candidate, workflow.Candidate);
		Assert.Equal(
			AntigravitySetupWorkflowState.Reviewing,
			workflow.State);
	}

	[Fact]
	public async Task RevalidateSafetyAsync_WhenCheckFails_RemainsFailClosedAndRequiresAnotherExplicitAction()
	{
		int revalidationCount = 0;

		AntigravityMachineSetupPreparationResult CreateFailedResult()
		{
			return CreateSafetyRevalidationResult(_ =>
			{
				revalidationCount++;
				return Task.FromResult(CreateFailedResult());
			});
		}

		FakeMachineSetupService service = new(CreateFailedResult());
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.True(await workflow.RevalidateSafetyAsync());

		Assert.Equal(1, revalidationCount);
		Assert.True(workflow.CanRevalidateSafety);
		Assert.Null(workflow.Candidate);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			workflow.FailureKind);
		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
	}

	[Fact]
	public async Task CancelAsync_WhenWaitingToRetryBusyPreparation_CancelsBackoff()
	{
		TaskCompletionSource retryDelayStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(
				AntigravityMachineSetupFailureKind.ExecutionBusy));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			async (_, cancellationToken) =>
			{
				retryDelayStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			});
		Task preparation = workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		await retryDelayStarted.Task;

		await workflow.CancelAsync();
		await preparation;

		Assert.Equal(1, service.CallCount);
		Assert.Equal(
			AntigravitySetupWorkflowState.Cancelled,
			workflow.State);
	}

	[Fact]
	public void DashboardLauncher_WhenProcessStartFails_ReturnsFalse()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		Directory.CreateDirectory(setupDirectory);
		Directory.CreateDirectory(appDirectory);
		File.WriteAllBytes(
			Path.Combine(appDirectory, "AiUsageDashboard.App.exe"),
			new byte[] { 0 });

		bool opened = DashboardLauncher.TryOpen(
			setupDirectory,
			_ => throw new System.ComponentModel.Win32Exception());

		Assert.False(opened);
	}

	[Fact]
	public void DashboardLauncher_WhenProcessStarts_PassesEnsureAccountArgument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		Directory.CreateDirectory(setupDirectory);
		Directory.CreateDirectory(appDirectory);
		File.WriteAllBytes(
			Path.Combine(appDirectory, "AiUsageDashboard.App.exe"),
			new byte[] { 0 });
		System.Diagnostics.ProcessStartInfo? observed = null;

		bool opened = DashboardLauncher.TryOpen(
			setupDirectory,
			startInfo =>
			{
				observed = startInfo;
				return System.Diagnostics.Process.GetCurrentProcess();
			});

		Assert.True(opened);
		Assert.NotNull(observed);
		Assert.Contains(
			AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount,
			observed.ArgumentList);
	}

	[Fact]
	public void AppEnsureAccountArgument_RequiresExactToken()
	{
		Assert.True(AiUsageDashboard.App.App
			.ShouldEnsureAntigravityAccount(new[]
		{
			AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount
		}));
		Assert.False(AiUsageDashboard.App.App
			.ShouldEnsureAntigravityAccount(new[]
		{
			"--ensure-antigravity-account-extra"
		}));
		Assert.False(AiUsageDashboard.App.App
			.ShouldEnsureAntigravityAccount(Array.Empty<string>()));
	}

	[Theory]
	[InlineData(960, 2, 360, 16, 464)]
	[InlineData(680, 2, 360, 16, 324)]
	[InlineData(1080, 1.5, 360, 16, 704)]
	public void CalculateMaximumWindowHeight_CapsToWorkAreaInDeviceIndependentPixels(
		double workAreaPixelHeight,
		double dpiScaleY,
		double minimumHeight,
		double verticalMargin,
		double expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.CalculateMaximumWindowHeight(
				workAreaPixelHeight,
				dpiScaleY,
				minimumHeight,
				verticalMargin),
			precision: 3);
	}

	[Theory]
	[InlineData(1600, 2, 420, 16, 784)]
	[InlineData(700, 2, 420, 16, 334)]
	[InlineData(1920, 1.5, 420, 16, 1264)]
	public void CalculateMaximumWindowWidth_CapsToWorkAreaInDeviceIndependentPixels(
		double workAreaPixelWidth,
		double dpiScaleX,
		double minimumWidth,
		double horizontalMargin,
		double expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.CalculateMaximumWindowWidth(
				workAreaPixelWidth,
				dpiScaleX,
				minimumWidth,
				horizontalMargin),
			precision: 3);
	}

	[Theory]
	[InlineData(0x001A, true)]
	[InlineData(0x007E, true)]
	[InlineData(0x02E0, true)]
	[InlineData(0x0003, false)]
	[InlineData(0x0005, false)]
	public void RequiresRefreshForWindowMessage_MatchesDisplayAndDpiChanges(
		int message,
		bool expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.RequiresRefreshForWindowMessage(message));
	}

	[Fact]
	public void GetFailureGuidance_WhenBuildIsUnsupported_HandlesBothVersionCommandOutcomes()
	{
		string guidance = SetupWindow.GetFailureGuidance(
			AntigravityMachineSetupFailureKind.UnsupportedBuild);

		Assert.Contains("agy --version", guidance, StringComparison.Ordinal);
		Assert.Contains("若無法執行或顯示的版本不受支援", guidance, StringComparison.Ordinal);
		Assert.Contains("若更新後仍無法連接", guidance, StringComparison.Ordinal);
		Assert.Contains("提供版本與技術資訊", guidance, StringComparison.Ordinal);
	}

	[Fact]
	public void GetFailureGuidance_WhenMultipleCliInstallationsAreFound_CoversInstallationsAndPath()
	{
		string guidance = SetupWindow.GetFailureGuidance(
			AntigravityMachineSetupFailureKind.AmbiguousExecutable);
		string diagnosticNextStep = SetupWindow.GetDiagnosticNextStep(
			AntigravityMachineSetupFailureKind.AmbiguousExecutable,
			AntigravityMachineSetupStage.DiscoveringExecutable);

		Assert.Contains("只保留要使用的一個", guidance, StringComparison.Ordinal);
		Assert.Contains("其他安裝", guidance, StringComparison.Ordinal);
		Assert.Contains("PATH 路徑", guidance, StringComparison.Ordinal);
		Assert.Contains(
			"其他安裝",
			diagnosticNextStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"PATH 路徑",
			diagnosticNextStep,
			StringComparison.Ordinal);
	}

	[Fact]
	public void GetFailureGuidance_WhenUsageIsRejected_ExplainsValidationFailure()
	{
		string guidance = SetupWindow.GetFailureGuidance(
			AntigravityMachineSetupFailureKind.UsageRejected);

		Assert.Contains("四項用量", guidance, StringComparison.Ordinal);
		Assert.Contains("已登入且能顯示用量", guidance, StringComparison.Ordinal);
		Assert.Contains("更新 AI Usage 或 Antigravity", guidance, StringComparison.Ordinal);
		Assert.DoesNotContain("agy -p /usage", guidance, StringComparison.Ordinal);
	}

	[Fact]
	public void GetFailureGuidance_WhenOfficialExecutionIsBusy_ExplainsTransientContentionWithoutReauthentication()
	{
		string guidance = SetupWindow.GetFailureGuidance(
			AntigravityMachineSetupFailureKind.ExecutionBusy);

		Assert.Contains("正在進行另一項", guidance, StringComparison.Ordinal);
		Assert.Contains("稍候片刻再試", guidance, StringComparison.Ordinal);
		Assert.Contains(
			"不需要重新安裝 Antigravity",
			guidance,
			StringComparison.Ordinal);
		Assert.Contains("不需要", guidance, StringComparison.Ordinal);
		Assert.Contains("重新登入", guidance, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(AntigravityMachineSetupFailureKind.ExistingProcessDetected)]
	[InlineData(AntigravityMachineSetupFailureKind.SettingsRejected)]
	public void GetFailureGuidance_WhenClosingAntigravityIsRequired_GivesDirectSteps(
		AntigravityMachineSetupFailureKind failureKind)
	{
		string guidance = SetupWindow.GetFailureGuidance(failureKind);

		Assert.Contains(
			"關閉其他 Antigravity 視窗",
			guidance,
			StringComparison.Ordinal);
		Assert.DoesNotContain("舊版相容模式", guidance, StringComparison.Ordinal);
		Assert.DoesNotContain("新版官方", guidance, StringComparison.Ordinal);
	}

	[Fact]
	public void SetupDashboardManagedArgument_RequiresOneExactToken()
	{
		Assert.True(AiUsageDashboard.Antigravity.Setup.App
			.ShouldUseDashboardManagedMode(new[]
		{
			AntigravityMachineSetupLaunchArguments.DashboardManaged
		}));
		Assert.False(AiUsageDashboard.Antigravity.Setup.App
			.ShouldUseDashboardManagedMode(new[]
		{
			"--dashboard-managed-extra"
		}));
		Assert.False(AiUsageDashboard.Antigravity.Setup.App
			.ShouldUseDashboardManagedMode(new[]
		{
			AntigravityMachineSetupLaunchArguments.DashboardManaged,
			"--unexpected"
		}));
		Assert.False(AiUsageDashboard.Antigravity.Setup.App
			.ShouldUseDashboardManagedMode(Array.Empty<string>()));
	}

	[Theory]
	[InlineData(ProviderKind.Claude, true)]
	[InlineData(ProviderKind.Codex, true)]
	[InlineData(ProviderKind.Antigravity, true)]
	[InlineData(ProviderKind.Grok, true)]
	[InlineData(ProviderKind.Copilot, true)]
	public void AccountEditorSupportsPostSaveAction_MatchesProvider(
		ProviderKind provider,
		bool expected)
	{
		Assert.Equal(
			expected,
			AiUsageDashboard.App.AccountEditorWindow
				.SupportsPostSaveAction(provider));
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenPackagedHelperExists_UsesExactManagedLaunch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		string setupExecutablePath = Path.Combine(
			setupDirectory,
			"AiUsageDashboard.Antigravity.Setup.exe");
		File.WriteAllBytes(setupExecutablePath, new byte[] { 0 });
		System.Diagnostics.ProcessStartInfo? observed = null;
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(startInfo, _) =>
			{
				observed = startInfo;
				return Task.FromResult<int?>(
					AntigravityMachineSetupLaunchArguments
						.DashboardManagedSuccessExitCode);
			});
		Guid setupAttemptId = Guid.NewGuid();

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(setupAttemptId, CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.Completed,
			outcome);
		Assert.NotNull(observed);
		Assert.Equal(setupExecutablePath, observed.FileName);
		Assert.Equal(setupDirectory, observed.WorkingDirectory);
		Assert.False(observed.UseShellExecute);
		Assert.Equal(
			new[]
			{
				AntigravityMachineSetupLaunchArguments.DashboardManaged
			},
			observed.ArgumentList);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSourceKindExitProtocol,
			observed.Environment[
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedResultProtocolEnvironmentVariable]);
		Assert.Equal(
			setupAttemptId.ToString("N"),
			observed.Environment[
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedSetupAttemptIdEnvironmentVariable]);
	}

	[Theory]
	[InlineData(null, 0)]
	[InlineData("", 0)]
	[InlineData("unexpected", 0)]
	[InlineData(
		AntigravityMachineSetupLaunchArguments
			.DashboardManagedSourceKindExitProtocol,
		AntigravityMachineSetupLaunchArguments
			.DashboardManagedOfficialPrintSuccessExitCode)]
	public void ResolveDashboardManagedCompletionExitCode_RequiresNewParentProtocol(
		string? resultProtocol,
		int expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.ResolveDashboardManagedCompletionExitCode(
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedOfficialPrintSuccessExitCode,
				resultProtocol));
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSuccessExitCode,
			SetupWindow.ResolveDashboardManagedCompletionExitCode(
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedSuccessExitCode,
				resultProtocol));
	}

	[Theory]
	[InlineData(true, true, true)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	public void ShouldAutoCloseAfterApprovalUncertainty_OnlyForManagedCommittedFlow(
		bool isDashboardManaged,
		bool hasApprovalStarted,
		bool expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.ShouldAutoCloseAfterApprovalUncertainty(
				isDashboardManaged,
				hasApprovalStarted));
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenOfficialPrintHelperCompletes_ReturnsOfficialPrintCompletion()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		File.WriteAllBytes(
			Path.Combine(
				setupDirectory,
				"AiUsageDashboard.Antigravity.Setup.exe"),
			new byte[] { 0 });
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) => Task.FromResult<int?>(
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedOfficialPrintSuccessExitCode));

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint,
			outcome);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenHelperIsMissing_DoesNotStartProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		Directory.CreateDirectory(appDirectory);
		int startCount = 0;
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) =>
			{
				startCount++;
				return Task.FromResult<int?>(0);
			});

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.Unavailable,
			outcome);
		Assert.Equal(0, startCount);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenAlreadyCancelled_DoesNotStartProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		File.WriteAllBytes(
			Path.Combine(
				setupDirectory,
				"AiUsageDashboard.Antigravity.Setup.exe"),
			new byte[] { 0 });
		int startCount = 0;
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) =>
			{
				startCount++;
				return Task.FromResult<int?>(0);
			});
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();

		AntigravityAccountSetupOutcome outcome = await launcher.RunAsync(
			Guid.NewGuid(),
			cancellationSource.Token);

		Assert.Equal(AntigravityAccountSetupOutcome.Cancelled, outcome);
		Assert.Equal(0, startCount);
	}

	[Theory]
	[InlineData(
		AntigravityMachineSetupLaunchArguments.DashboardManagedCancelledExitCode,
		"Cancelled")]
	[InlineData(
		AntigravityMachineSetupLaunchArguments.DashboardManagedFailedExitCode,
		"Failed")]
	[InlineData(
		AntigravityMachineSetupLaunchArguments
			.DashboardManagedCompletionUnknownExitCode,
		"CompletionUnknown")]
	[InlineData(99, "CompletionUnknown")]
	public async Task AntigravitySetupLauncher_MapsNonSuccessExitCode(
		int? exitCode,
		string expected)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		File.WriteAllBytes(
			Path.Combine(
				setupDirectory,
				"AiUsageDashboard.Antigravity.Setup.exe"),
			new byte[] { 0 });
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) => Task.FromResult<int?>(exitCode));

		Assert.Equal(
			expected,
			(await launcher.RunAsync(CancellationToken.None)).ToString());
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenProcessReturnsNoExitCode_ReturnsLaunchFailed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		File.WriteAllBytes(
			Path.Combine(
				setupDirectory,
				"AiUsageDashboard.Antigravity.Setup.exe"),
			new byte[] { 0 });
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) => Task.FromResult<int?>(null));

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.LaunchFailed,
			outcome);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenProcessStartThrows_ReturnsLaunchFailed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		string setupDirectory = Path.Combine(
			temporaryDirectory.Path,
			"setup");
		Directory.CreateDirectory(appDirectory);
		Directory.CreateDirectory(setupDirectory);
		File.WriteAllBytes(
			Path.Combine(
				setupDirectory,
				"AiUsageDashboard.Antigravity.Setup.exe"),
			new byte[] { 0 });
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(_, _) => throw new IOException("Synthetic launch failure."));

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.LaunchFailed,
			outcome);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenActiveRegistrationFails_AcknowledgesUnknownCompletion()
	{
		bool wasRegistered = await AntigravityAccountSetupLauncher
			.TryRegisterActiveProcessAsync(
				() => Task.FromException(
					new IOException("Synthetic state-store failure.")));

		Assert.False(wasRegistered);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenCancelled_RequestsCooperativeClose()
	{
		using CancellationTokenSource cancellationSource = new();
		int terminateCount = 0;
		TaskCompletionSource processExited = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		Task<bool> waitTask =
			AntigravityAccountSetupLauncher.WaitForExitOrTerminateAsync(
			cancellationToken => processExited.Task.WaitAsync(cancellationToken),
			() => false,
			() =>
			{
				terminateCount++;
				processExited.TrySetResult();
			},
			TimeSpan.FromSeconds(1),
			cancellationSource.Token);
		cancellationSource.Cancel();

		Assert.True(await waitTask);
		Assert.Equal(1, terminateCount);
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenTerminationDoesNotExit_ReturnsWithinBound()
	{
		using CancellationTokenSource cancellationSource = new();
		TaskCompletionSource processExited = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		Task<bool> waitTask =
			AntigravityAccountSetupLauncher.WaitForExitOrTerminateAsync(
			cancellationToken => processExited.Task.WaitAsync(cancellationToken),
			() => false,
			() => { },
			TimeSpan.FromMilliseconds(30),
			cancellationSource.Token);
		cancellationSource.Cancel();

		Assert.False(await waitTask.WaitAsync(TimeSpan.FromSeconds(1)));
	}

	[Fact]
	public async Task AntigravitySetupLauncher_WhenCancellationRacesWithExit_DoesNotRequestClose()
	{
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		int closeRequestCount = 0;

		bool hasConfirmedExit = await AntigravityAccountSetupLauncher
			.WaitForExitOrTerminateAsync(
				cancellationToken => Task.Delay(
					Timeout.InfiniteTimeSpan,
					cancellationToken),
				() => true,
				() => closeRequestCount++,
				TimeSpan.FromSeconds(1),
				cancellationSource.Token);

		Assert.True(hasConfirmedExit);
		Assert.Equal(0, closeRequestCount);
	}

	[Fact]
	public async Task ApproveAsync_WhenReviewIsConfirmed_ApprovesAndDisposesCandidate()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => releaseCount++,
			AntigravityMachineSetupSourceKind.ReviewedConPty);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		bool prematurelyApproved = await workflow.ApproveAsync(
			isReviewConfirmed: false);
		bool approved = await workflow.ApproveAsync(
			isReviewConfirmed: true);

		Assert.False(prematurelyApproved);
		Assert.True(approved);
		Assert.Equal(1, service.CallCount);
		Assert.Equal(
			new AntigravityMachineSetupConsent(true, true),
			service.LastConsent);
		Assert.Equal(1, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.True(candidate.IsApproved);
		Assert.True(workflow.HasCommittedSetting);
		Assert.Null(workflow.Candidate);
		Assert.Equal(
			AntigravitySetupWorkflowState.Completed,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSuccessExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_WhenOfficialPrintCandidateCompletes_PreservesSourceAwareExitCode()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => releaseCount++,
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);

		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		bool approved = await workflow.ApproveAsync(
			isReviewConfirmed: true);

		Assert.True(approved);
		Assert.Equal(1, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.True(candidate.IsApproved);
		Assert.Null(workflow.Candidate);
		Assert.Equal(
			AntigravitySetupWorkflowState.Completed,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedOfficialPrintSuccessExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_PersistsReceiptOnlyAfterApprovalAndDisposal()
	{
		List<string> sequence = new();
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			_ =>
			{
				sequence.Add("approve");
				return Task.CompletedTask;
			},
			() =>
			{
				sequence.Add("dispose");
				return ValueTask.CompletedTask;
			},
			AntigravityMachineSetupSourceKind.ReviewedConPty);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			beforeApprovalCommitAsync: (_, _) =>
			{
				sequence.Add("request");
				return Task.CompletedTask;
			},
			afterApprovalCommittedAsync: (_, _) =>
			{
				sequence.Add("receipt");
				return Task.CompletedTask;
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.True(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(
			new[] { "request", "approve", "dispose", "receipt" },
			sequence);
		Assert.Equal(AntigravitySetupWorkflowState.Completed, workflow.State);
	}

	[Fact]
	public async Task ApproveAsync_WhenReceiptPersistenceFails_ReturnsUnknownCompletion()
	{
		int receiptCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => { },
			() => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			afterApprovalCommittedAsync: (_, _) =>
			{
				receiptCount++;
				return Task.FromException(
					new IOException("Synthetic receipt failure."));
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.False(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(1, receiptCount);
		Assert.True(workflow.HasCommittedSetting);
		Assert.Equal(AntigravitySetupWorkflowState.Failed, workflow.State);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedCompletionUnknownExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ResetAndCancel_AfterCommittedReceiptFailure_RemainsUnknown()
	{
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => { },
			() => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			afterApprovalCommittedAsync: (_, _) => Task.FromException(
				new IOException("Synthetic receipt failure.")));
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		Assert.False(await workflow.ApproveAsync(isReviewConfirmed: true));

		workflow.Reset();
		await workflow.CancelAsync();

		Assert.True(workflow.HasApprovalStarted);
		Assert.True(workflow.HasCommittedSetting);
		Assert.Equal(AntigravitySetupWorkflowState.Cancelled, workflow.State);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedCompletionUnknownExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_WhenCandidateApprovalFails_DoesNotPersistReceipt()
	{
		int receiptCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			_ => Task.FromException(
				new IOException("Synthetic approval failure.")),
			() => ValueTask.CompletedTask,
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			afterApprovalCommittedAsync: (_, _) =>
			{
				receiptCount++;
				return Task.CompletedTask;
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.False(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(0, receiptCount);
		Assert.False(workflow.HasCommittedSetting);
		Assert.True(workflow.HasApprovalStarted);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedCompletionUnknownExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_WhenApprovalRequestPersistenceFailsTransiently_RetriesBeforeApproval()
	{
		List<string> sequence = new();
		List<TimeSpan> retryDelays = new();
		int persistenceCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => sequence.Add("approve"),
			() => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			delayAsync: (delay, _) =>
			{
				retryDelays.Add(delay);
				return Task.CompletedTask;
			},
			beforeApprovalCommitAsync: (_, _) =>
			{
				persistenceCount++;
				sequence.Add($"persist-{persistenceCount}");
				return persistenceCount < 3
					? Task.FromException(
						new IOException("Synthetic transient failure."))
					: Task.CompletedTask;
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.True(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(3, persistenceCount);
		Assert.Equal(
			new[]
			{
				TimeSpan.FromMilliseconds(250),
				TimeSpan.FromMilliseconds(500)
			},
			retryDelays);
		Assert.Equal(
			new[] { "persist-1", "persist-2", "persist-3", "approve" },
			sequence);
		Assert.True(workflow.HasApprovalStarted);
		Assert.True(workflow.HasCommittedSetting);
		Assert.Equal(AntigravitySetupWorkflowState.Completed, workflow.State);
	}

	[Fact]
	public async Task ApproveAsync_WhenApprovalRequestPersistenceRemainsUnavailable_ExhaustsRetriesBeforeFailing()
	{
		int approveCount = 0;
		int persistenceCount = 0;
		List<TimeSpan> retryDelays = new();
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			delayAsync: (delay, _) =>
			{
				retryDelays.Add(delay);
				return Task.CompletedTask;
			},
			beforeApprovalCommitAsync: (_, _) =>
			{
				persistenceCount++;
				return Task.FromException(
					new IOException("Synthetic approval request failure."));
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.False(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(7, persistenceCount);
		Assert.Equal(
			new[]
			{
				TimeSpan.FromMilliseconds(250),
				TimeSpan.FromMilliseconds(500),
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(2),
				TimeSpan.FromSeconds(4),
				TimeSpan.FromSeconds(5)
			},
			retryDelays);
		Assert.Equal(0, approveCount);
		Assert.False(workflow.HasApprovalStarted);
		Assert.False(workflow.HasCommittedSetting);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments.DashboardManagedFailedExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_WhenApprovalRequestPersistenceIsInvalid_DoesNotRetryOrStartApproval()
	{
		int approveCount = 0;
		int persistenceCount = 0;
		int delayCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => { },
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			delayAsync: (_, _) =>
			{
				delayCount++;
				return Task.CompletedTask;
			},
			beforeApprovalCommitAsync: (_, _) =>
			{
				persistenceCount++;
				return Task.FromException(
					new InvalidDataException("Synthetic state conflict."));
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		Assert.False(await workflow.ApproveAsync(isReviewConfirmed: true));

		Assert.Equal(1, persistenceCount);
		Assert.Equal(0, delayCount);
		Assert.Equal(0, approveCount);
		Assert.False(workflow.HasApprovalStarted);
		Assert.False(workflow.HasCommittedSetting);
		Assert.Equal(AntigravitySetupWorkflowState.Failed, workflow.State);
	}

	[Fact]
	public async Task ApproveAsync_WhenApprovalRequestRetryIsCancelled_DoesNotStartApproval()
	{
		int approveCount = 0;
		int releaseCount = 0;
		int persistenceCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => releaseCount++,
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(
			service,
			delayAsync: (delay, cancellationToken) =>
				Task.Delay(delay, cancellationToken),
			beforeApprovalCommitAsync: (_, _) =>
			{
				persistenceCount++;
				return Task.FromException(
					new IOException("Synthetic transient failure."));
			});
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();

		Assert.False(await workflow.ApproveAsync(
			isReviewConfirmed: true,
			cancellationSource.Token));

		Assert.Equal(1, persistenceCount);
		Assert.Equal(0, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.False(workflow.HasApprovalStarted);
		Assert.False(workflow.HasCommittedSetting);
		Assert.Equal(AntigravitySetupWorkflowState.Cancelled, workflow.State);
	}

	[Fact]
	public async Task CancelAsync_WhenCandidateAwaitsReview_DisposesWithoutApproval()
	{
		int approveCount = 0;
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			() => approveCount++,
			() => releaseCount++);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		await workflow.CancelAsync();

		Assert.Equal(0, approveCount);
		Assert.Equal(1, releaseCount);
		Assert.False(candidate.IsApproved);
		Assert.Null(workflow.Candidate);
		Assert.Equal(
			AntigravitySetupWorkflowState.Cancelled,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedCancelledExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task CancelAsync_WhenPrepareIsRunning_PropagatesCancellation()
	{
		TaskCompletionSource started = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeMachineSetupService service = new(
			async (_, cancellationToken) =>
			{
				started.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				throw new InvalidOperationException("Unreachable.");
			});
		await using AntigravitySetupWorkflow workflow = new(service);
		Task prepareTask = workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		await started.Task;

		await workflow.CancelAsync();
		await prepareTask;

		Assert.Equal(
			AntigravitySetupWorkflowState.Cancelled,
			workflow.State);
		Assert.Null(workflow.Candidate);
	}

	[Fact]
	public async Task CancelAsync_WhenApprovalHasStarted_WaitsForCommitSemantics()
	{
		TaskCompletionSource approvalStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowApproval = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int releaseCount = 0;
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			async _ =>
			{
				approvalStarted.SetResult();
				await allowApproval.Task;
			},
			() =>
			{
				releaseCount++;
				return ValueTask.CompletedTask;
			});
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);
		Task<bool> approval = workflow.ApproveAsync(
			isReviewConfirmed: true);
		await approvalStarted.Task;

		await workflow.CancelAsync();

		Assert.Equal(
			AntigravitySetupWorkflowState.Approving,
			workflow.State);
		allowApproval.SetResult();
		Assert.True(await approval);
		Assert.Equal(1, releaseCount);
		Assert.Equal(
			AntigravitySetupWorkflowState.Completed,
			workflow.State);
	}

	[Fact]
	public async Task CancelAsync_WhenCandidateCleanupFails_ReportsPrivateStorageFailure()
	{
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			_ => Task.CompletedTask,
			() => throw new IOException("Synthetic cleanup failure."));
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		await workflow.CancelAsync();

		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.PrivateStorageRejected,
			workflow.FailureKind);
	}

	[Fact]
	public async Task CancelAsync_WhenWorkflowAlreadyFailed_PreservesFailureExit()
	{
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(
				AntigravityMachineSetupFailureKind.UnsupportedBuild));
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		await workflow.CancelAsync();

		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			workflow.FailureKind);
		Assert.False(workflow.HasCommittedSetting);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedFailedExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public async Task ApproveAsync_WhenFinalPrivateVerificationFails_DoesNotReportCompleted()
	{
		AntigravityMachineSetupCandidate candidate = CreateCandidate(
			_ => Task.CompletedTask,
			() => throw new IOException("Synthetic final verification failure."),
			AntigravityMachineSetupSourceKind.OfficialPrint);
		FakeMachineSetupService service = new(
			new AntigravityMachineSetupPreparationResult(candidate));
		await using AntigravitySetupWorkflow workflow = new(service);
		await workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true);

		bool approved = await workflow.ApproveAsync(
			isReviewConfirmed: true);

		Assert.False(approved);
		Assert.True(candidate.IsApproved);
		Assert.True(workflow.HasCommittedSetting);
		Assert.Equal(
			AntigravitySetupWorkflowState.Failed,
			workflow.State);
		Assert.Equal(
			AntigravityMachineSetupFailureKind.PrivateStorageRejected,
			workflow.FailureKind);
		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedCompletionUnknownExitCode,
			workflow.DashboardManagedExitCode);
	}

	[Fact]
	public void GetProgressText_WhenProfileIsMaterializing_DoesNotClaimItWasSaved()
	{
		Assert.Equal(
			"正在準備本機連接設定…",
			SetupWindow.GetProgressText(
				AntigravityMachineSetupStage.MaterializingProfile));
	}

	[Fact]
	public void SafetyRevalidationFailure_ExposesExplicitOneShotActionAndGuidance()
	{
		Assert.Equal(
			"重新檢查 Antigravity 用量",
			SetupWindow.GetFailureActionText(
				AntigravityMachineSetupFailureKind
					.SafetyRevalidationRequired));
		Assert.Contains(
			"不會自動再試",
			SetupWindow.GetFailureGuidance(
				AntigravityMachineSetupFailureKind
					.SafetyRevalidationRequired),
			StringComparison.Ordinal);
		Assert.Contains(
			"需要你確認後",
			SetupWindow.GetFailureGuidance(
				AntigravityMachineSetupFailureKind
					.SafetyRevalidationRequired),
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"中斷",
			SetupWindow.GetFailureGuidance(
				AntigravityMachineSetupFailureKind
					.SafetyRevalidationRequired),
			StringComparison.Ordinal);
		Assert.Contains(
			"執行一次唯讀 /usage",
			SetupWindow.GetDiagnosticNextStep(
				AntigravityMachineSetupFailureKind
					.SafetyRevalidationRequired,
				AntigravityMachineSetupStage.ValidatingUsage),
			StringComparison.Ordinal);
	}

	[Fact]
	public void CreateSafeDiagnosticInfo_ContainsOnlyApprovedDiagnosticFields()
	{
		string diagnostic = SetupWindow.CreateSafeDiagnosticInfo(
			"1.2.3",
			AntigravityMachineSetupFailureKind.UsageRejected,
			AntigravityMachineSetupStage.ValidatingUsage);

		Assert.Equal(
			string.Join(
				Environment.NewLine,
				"Antigravity 連接技術資訊",
				"應用程式版本：1.2.3",
				"失敗類型：無法確認用量（UsageRejected）",
				"最後階段：讀取 Antigravity 用量（ValidatingUsage）",
				"建議處理方式：請確認 Antigravity 已登入且能顯示用量；若仍失敗，請更新 AI Usage 或 Antigravity，並提供技術資訊給維護者。"),
			diagnostic);
		Assert.DoesNotContain(
			"Reference:",
			diagnostic,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("@", diagnostic, StringComparison.Ordinal);
		Assert.DoesNotContain(
			@"C:\Users\",
			diagnostic,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"quota",
			diagnostic,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CreateSafeDiagnosticInfo_ForUnsupportedDiscovery_IsActionableAndDeterministic()
	{
		string first = SetupWindow.CreateSafeDiagnosticInfo(
			"1.2.3",
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			AntigravityMachineSetupStage.DiscoveringExecutable);
		string second = SetupWindow.CreateSafeDiagnosticInfo(
			"1.2.3",
			AntigravityMachineSetupFailureKind.UnsupportedBuild,
			AntigravityMachineSetupStage.DiscoveringExecutable);

		Assert.Equal(first, second);
		Assert.Contains("agy --version", first, StringComparison.Ordinal);
		Assert.Contains("若無法執行或顯示的版本不受支援", first, StringComparison.Ordinal);
		Assert.Contains("若更新後仍無法連接", first, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Reference:",
			first,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CreateSafeDiagnosticInfo_ForBusyOfficialExecution_DoesNotMisdiagnoseBuild()
	{
		string diagnostic = SetupWindow.CreateSafeDiagnosticInfo(
			"1.2.3",
			AntigravityMachineSetupFailureKind.ExecutionBusy,
			AntigravityMachineSetupStage.DiscoveringExecutable);

		Assert.Contains(
			"失敗類型：另一項用量檢查仍在進行（ExecutionBusy）",
			diagnostic,
			StringComparison.Ordinal);
		Assert.Contains("請稍候", diagnostic, StringComparison.Ordinal);
		Assert.Contains(
			"不需要重新安裝 Antigravity",
			diagnostic,
			StringComparison.Ordinal);
		Assert.DoesNotContain("agy --version", diagnostic, StringComparison.Ordinal);
	}

	[Fact]
	public void ResolveAppVersion_WithProductVersion_UsesFullPackageVersion()
	{
		const string ProductVersion =
			"1.0.0-internal.20260811.13.ga30b3a9db7c8.run31476253323.a1+" +
			"a30b3a9db7c8eb67187d90dc423ba70300380d6c";

		string result = SetupWindow.ResolveAppVersion(
			() => ProductVersion,
			new Version(1, 0, 0, 0));

		Assert.Equal(ProductVersion, result);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void ResolveAppVersion_WithMissingProductVersion_UsesAssemblyVersion(
		string? productVersion)
	{
		string result = SetupWindow.ResolveAppVersion(
			() => productVersion,
			new Version(1, 2, 3, 4));

		Assert.Equal("1.2.3.4", result);
	}

	[Fact]
	public void ResolveAppVersion_WhenProductVersionReadThrows_UsesAssemblyVersion()
	{
		string result = SetupWindow.ResolveAppVersion(
			() => throw new IOException("Synthetic metadata read failure."),
			new Version(2, 3, 4, 5));

		Assert.Equal("2.3.4.5", result);
	}

	[Fact]
	public void ResolveAppVersion_WithControlCharacters_RejectsDiagnosticInjection()
	{
		string result = SetupWindow.ResolveAppVersion(
			() => "1.2.3\r\nReference: PRIVATE-SENTINEL",
			new Version(3, 4, 5, 6));

		Assert.Equal("3.4.5.6", result);
		Assert.DoesNotContain("PRIVATE-SENTINEL", result, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(@"C:\local-tools", true)]
	[InlineData(@"\\server\share", false)]
	[InlineData(@"\\?\UNC\server\share", false)]
	[InlineData("relative-tools", false)]
	public void IsLocalExecutableSearchDirectory_RejectsNetworkAndRelativePaths(
		string path,
		bool expected)
	{
		Assert.Equal(
			expected,
			AntigravityMachineSetupService
				.IsLocalExecutableSearchDirectory(
					path,
					_ => AntigravityExecutableSearchDriveType.Fixed));
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(3, true)]
	[InlineData(4, false)]
	[InlineData(5, false)]
	[InlineData(6, true)]
	public void IsLocalExecutableSearchDirectory_RequiresAllowedDriveType(
		int driveTypeValue,
		bool expected)
	{
		string? observedRoot = null;
		AntigravityExecutableSearchDriveType driveType =
			(AntigravityExecutableSearchDriveType)driveTypeValue;

		bool isAllowed = AntigravityMachineSetupService
			.IsLocalExecutableSearchDirectory(
				@"X:\local-tools",
				root =>
				{
					observedRoot = root;
					return driveType;
				});

		Assert.Equal(expected, isAllowed);
		Assert.Equal(@"X:\", observedRoot);
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_WithUncPath_DoesNotProbeNetworkCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		List<string> observedPaths = new();
		string pathValue = string.Join(
			Path.PathSeparator,
			@"\\server\share",
			@"C:\local-tools");

		IReadOnlyList<string> candidates =
			await AntigravityMachineSetupService
				.DiscoverExecutableCandidatesAsync(
					temporaryDirectory.Path,
					pathValue,
					path =>
					{
						observedPaths.Add(path);
						return false;
					},
					_ => AntigravityExecutableSearchDriveType.Fixed,
					CancellationToken.None);

		Assert.Empty(candidates);
		Assert.DoesNotContain(
			observedPaths,
			path => path.StartsWith(@"\\", StringComparison.Ordinal));
		Assert.Contains(
			observedPaths,
			path => string.Equals(
				path,
				@"C:\local-tools\agy.exe",
				StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_WithMappedRemoteDrive_DoesNotProbeFileSystem()
	{
		int fileProbeCount = 0;
		int driveTypeProbeCount = 0;

		IReadOnlyList<string> candidates =
			await AntigravityMachineSetupService
				.DiscoverExecutableCandidatesAsync(
					@"X:\AppData\Local",
					@"X:\local-tools",
					_ =>
					{
						fileProbeCount++;
						return true;
					},
					_ =>
					{
						driveTypeProbeCount++;
						return AntigravityExecutableSearchDriveType.Remote;
					},
					CancellationToken.None);

		Assert.Empty(candidates);
		Assert.True(driveTypeProbeCount > 0);
		Assert.Equal(0, fileProbeCount);
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_WithInvalidEntriesBeforeBoundary_StillProbesValidLocalEntry()
	{
		string[] pathEntries = Enumerable.Range(0, 256)
			.Select(index => $"relative-{index}")
			.Append(@"X:\valid-tools")
			.ToArray();
		List<string> observedPaths = new();

		IReadOnlyList<string> candidates =
			await AntigravityMachineSetupService
				.DiscoverExecutableCandidatesAsync(
					"relative-app-data",
					string.Join(Path.PathSeparator, pathEntries),
					path =>
					{
						observedPaths.Add(path);
						return false;
					},
					_ => AntigravityExecutableSearchDriveType.Fixed,
					CancellationToken.None);

		Assert.Empty(candidates);
		Assert.Equal(
			new[] { @"X:\valid-tools\agy.exe" },
			observedPaths);
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_WhenFileProbeBlocks_KeepsSingleFlightUntilProbeFinishes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim probeEntered = new();
		using ManualResetEventSlim releaseProbe = new();
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource probeExited = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondProbeEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task<IReadOnlyList<string>>? secondDiscoveryTask = null;
		Task<IReadOnlyList<string>> discoveryTask =
			AntigravityMachineSetupService.DiscoverExecutableCandidatesAsync(
				temporaryDirectory.Path,
				string.Empty,
				_ =>
				{
					probeEntered.Set();

					try
					{
						releaseProbe.Wait();
						return false;
					}
					finally
					{
						probeExited.TrySetResult();
					}
				},
				_ => AntigravityExecutableSearchDriveType.Fixed,
				cancellation.Token);

		try
		{
			Assert.True(probeEntered.Wait(TimeSpan.FromSeconds(5)));
			Assert.False(discoveryTask.IsCompleted);

			cancellation.Cancel();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => discoveryTask);

			secondDiscoveryTask =
				AntigravityMachineSetupService
					.DiscoverExecutableCandidatesAsync(
						temporaryDirectory.Path,
						string.Empty,
						_ =>
						{
							secondProbeEntered.TrySetResult();
							return false;
						},
						_ => AntigravityExecutableSearchDriveType.Fixed,
						CancellationToken.None);
			await Task.Delay(100);
			Assert.False(secondProbeEntered.Task.IsCompleted);

			releaseProbe.Set();
			await probeExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await secondProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Empty(await secondDiscoveryTask);
		}
		finally
		{
			releaseProbe.Set();
			await probeExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

			if (secondDiscoveryTask is not null)
			{
				await secondDiscoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
			}
		}
	}

	[Fact]
	public async Task DiscoverExecutableCandidatesAsync_WhenProbeIgnoresCancellation_TimesOutWithoutStartingAnotherProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim probeEntered = new();
		using ManualResetEventSlim releaseProbe = new();
		TaskCompletionSource probeExited = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int laterProbeCount = 0;
		Task<IReadOnlyList<string>> firstDiscoveryTask =
			AntigravityMachineSetupService.DiscoverExecutableCandidatesAsync(
				temporaryDirectory.Path,
				string.Empty,
				_ =>
				{
					probeEntered.Set();

					try
					{
						releaseProbe.Wait();
						return false;
					}
					finally
					{
						probeExited.TrySetResult();
					}
				},
				_ => AntigravityExecutableSearchDriveType.Fixed,
				TimeSpan.FromMilliseconds(250),
				CancellationToken.None);

		try
		{
			Assert.True(probeEntered.Wait(TimeSpan.FromSeconds(5)));
			await Assert.ThrowsAsync<TimeoutException>(() => firstDiscoveryTask);

			Task<IReadOnlyList<string>> secondDiscoveryTask =
				AntigravityMachineSetupService.DiscoverExecutableCandidatesAsync(
					temporaryDirectory.Path,
					string.Empty,
					_ =>
					{
						Interlocked.Increment(ref laterProbeCount);
						return false;
					},
					_ => AntigravityExecutableSearchDriveType.Fixed,
					TimeSpan.FromMilliseconds(250),
					CancellationToken.None);

			await Assert.ThrowsAsync<TimeoutException>(() => secondDiscoveryTask);
			Assert.Equal(0, Volatile.Read(ref laterProbeCount));

			releaseProbe.Set();
			await probeExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

			IReadOnlyList<string> candidates =
				await AntigravityMachineSetupService
					.DiscoverExecutableCandidatesAsync(
						temporaryDirectory.Path,
						string.Empty,
						_ =>
						{
							Interlocked.Increment(ref laterProbeCount);
							return false;
						},
						_ => AntigravityExecutableSearchDriveType.Fixed,
						TimeSpan.FromSeconds(2),
						CancellationToken.None);

			Assert.Empty(candidates);
			Assert.True(Volatile.Read(ref laterProbeCount) > 0);
		}
		finally
		{
			releaseProbe.Set();
			await probeExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	private static AntigravityMachineSetupPreparationResult
		CreateSafetyRevalidationResult(
			Func<
				CancellationToken,
				Task<AntigravityMachineSetupPreparationResult>>
				revalidateSafetyAsync)
	{
		return new AntigravityMachineSetupPreparationResult(
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			new AntigravityProductionUsageResult(
				isSuccessful: false,
				AntigravityProductionUsageFailureKind.SafetyLatched,
				accountIdentity: null,
				Array.Empty<AntigravityProductionUsageWindow>(),
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				automaticRevalidationPending: false),
			revalidateSafetyAsync);
	}

	private static AntigravityMachineSetupCandidate CreateCandidate(
		Action approve,
		Action release,
		AntigravityMachineSetupSourceKind sourceKind =
			AntigravityMachineSetupSourceKind.ReviewedConPty)
	{
		return CreateCandidate(
			_ =>
			{
				approve();
				return Task.CompletedTask;
			},
			() =>
			{
				release();
				return ValueTask.CompletedTask;
			},
			sourceKind);
	}

	private static AntigravityMachineSetupCandidate CreateCandidate(
		Func<CancellationToken, Task> approveAsync,
		Func<ValueTask> releaseAsync,
		AntigravityMachineSetupSourceKind sourceKind =
			AntigravityMachineSetupSourceKind.ReviewedConPty)
	{
		AntigravityProductionUsageWindow[] windows =
		{
			new(
				"agy.gemini.weekly",
				90m,
				TimeSpan.FromDays(1),
				IsAvailable: false),
			new(
				"agy.gemini.rolling-5h",
				80m,
				ResetsIn: null,
				IsAvailable: true),
			new(
				"agy.claude.weekly",
				70m,
				TimeSpan.FromHours(2),
				IsAvailable: false),
			new(
				"agy.claude.rolling-5h",
				60m,
				ResetsIn: null,
				IsAvailable: true)
		};
		return new AntigravityMachineSetupCandidate(
			"1.2.3",
			"synthetic@example.invalid",
			windows,
			approveAsync,
			releaseAsync,
			sourceKind);
	}
}
