using System.Diagnostics;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class AppActivationTests
{
	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void CanStartInteractiveShutdown_BlocksWhileUpdateReservationIsHeld(
		bool isUpdateShutdownReserved,
		bool expected)
	{
		Assert.Equal(
			expected,
			AiUsageDashboard.App.App.CanStartInteractiveShutdown(
				isUpdateShutdownReserved));
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_ClearsRemovedAccountAndPreservesKnownDisabledLatch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid removedAccountId = Guid.NewGuid();
		Guid disabledAccountId = Guid.NewGuid();
		string StatePath(Guid accountId) => Path.Combine(
			accountRootPath,
			accountId.ToString("N"),
			"usage-safety-v1.json");
		string stateFilePath = StatePath(removedAccountId);
		string orphanedTemporaryFilePath = Path.Combine(
			Path.GetDirectoryName(stateFilePath)!,
			$".{Path.GetFileName(stateFilePath)}.{Guid.NewGuid():N}.tmp");
		string disabledTemporaryFilePath = Path.Combine(
			Path.GetDirectoryName(StatePath(disabledAccountId))!,
			$".{Path.GetFileName(StatePath(disabledAccountId))}." +
			$"{Guid.NewGuid():N}.tmp");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		ClaudeUsageSafetyState removedState = new(
			AutomaticRevalidationAttemptCount: 3,
			AccountIdentity: "removed@example.com",
			FailureReason: "manual safety latch",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null);
		ClaudeUsageSafetyState disabledState = removedState with
		{
			AccountIdentity = "disabled@example.com"
		};
		await store.SaveAsync(removedAccountId, removedState);
		await store.SaveAsync(disabledAccountId, disabledState);

		await File.WriteAllTextAsync(
			orphanedTemporaryFilePath,
			"removed@example.com");
		await File.WriteAllTextAsync(
			disabledTemporaryFilePath,
			"disabled@example.com");
		ClaudeCliUsagePoller poller = CreateRemovalPurger(store);

		var recovery = await AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				[disabledAccountId],
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(5),
				(accountId, token) => poller.PurgeAccountSafetyStateAsync(
					accountId,
					ClaudeUsageSafetyStateClearPurpose.AccountRemoval,
					token),
				(_, _) => throw new InvalidOperationException(
					"Successful cleanup must not schedule a retry."));

		Assert.False(recovery.DidDiskDiscoveryFail);
		Assert.Empty(recovery.FailedAccountIds);
		Assert.False(File.Exists(stateFilePath));
		Assert.False(File.Exists(orphanedTemporaryFilePath));
		Assert.False(File.Exists(disabledTemporaryFilePath));
		Assert.Equal(disabledState, await store.LoadAsync(disabledAccountId));
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_WhenProfilesAreNotAuthoritative_PreservesDiskOnlyLatch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid accountId = Guid.NewGuid();
		string StatePath(Guid id) => Path.Combine(
			accountRootPath,
			id.ToString("N"),
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 3,
			AccountIdentity: "unresolved@example.com",
			FailureReason: "manual safety latch",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null);
		await store.SaveAsync(accountId, expected);

		var recovery = await AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: false,
				accountRootPath,
				TimeSpan.FromSeconds(5),
				(_, _) => throw new InvalidOperationException(
					"A non-authoritative profile set must not purge disk-only state."),
				(_, _) => throw new InvalidOperationException(
					"A non-authoritative profile set must not schedule cleanup."));

		Assert.False(recovery.DidDiskDiscoveryFail);
		Assert.Empty(recovery.FailedAccountIds);
		Assert.Equal(expected, await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_RemovedContainedAttemptWaitsForPositiveRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		string StatePath(Guid id) => Path.Combine(
			accountRootPath,
			id.ToString("N"),
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 1,
				AccountIdentity: "removed@example.com",
				FailureReason: "interrupted contained attempt",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase:
					ClaudeUsageSafetyAttemptPhase.StartedContained));
		int recoveryCallCount = 0;
		Task<bool> RecoverAsync(
			Guid observedAttemptId,
			TimeSpan timeout,
			CancellationToken _)
		{
			Assert.Equal(attemptId, observedAttemptId);
			Assert.True(timeout > TimeSpan.Zero);
			return Task.FromResult(
				Interlocked.Increment(ref recoveryCallCount) > 1);
		}
		ClaudeCliUsagePoller poller = CreateRemovalPurger(store, RecoverAsync);
		int scheduledRetryCount = 0;
		Task PurgeRemovedAsync(Guid id, CancellationToken token) =>
			poller.PurgeAccountSafetyStateAsync(
				id,
				ClaudeUsageSafetyStateClearPurpose.AccountRemoval,
				token);
		Task ScheduleRetryAsync(Guid id, CancellationToken token)
		{
			Assert.Equal(accountId, id);
			Assert.False(token.IsCancellationRequested);
			Interlocked.Increment(ref scheduledRetryCount);
			return Task.CompletedTask;
		}

		var firstRecovery = await AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(5),
				PurgeRemovedAsync,
				ScheduleRetryAsync);

		Assert.False(firstRecovery.DidDiskDiscoveryFail);
		Assert.Equal([accountId], firstRecovery.FailedAccountIds);
		ClaudeUsageSafetyState? retainedState = await store.LoadAsync(accountId);
		Assert.NotNull(retainedState);
		Assert.Null(retainedState.AccountIdentity);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			retainedState.RecoveryMode);
		Assert.Equal(attemptId, retainedState.AttemptId);
		Assert.Equal(1, scheduledRetryCount);

		var secondRecovery = await AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(5),
				PurgeRemovedAsync,
				ScheduleRetryAsync);

		Assert.False(secondRecovery.DidDiskDiscoveryFail);
		Assert.Empty(secondRecovery.FailedAccountIds);
		Assert.Equal(2, recoveryCallCount);
		Assert.Equal(1, scheduledRetryCount);
		Assert.Null(await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_WhenTimeoutExpires_SchedulesEveryRemovedAccount()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		Guid[] accountIds = [firstAccountId, secondAccountId];
		string StatePath(Guid id) => Path.Combine(
			accountRootPath,
			id.ToString("N"),
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		foreach (Guid accountId in accountIds)
		{
			await store.SaveAsync(
				accountId,
				new ClaudeUsageSafetyState(
					AutomaticRevalidationAttemptCount: 0,
					AccountIdentity: "removed@example.com",
					FailureReason: "manual safety latch",
					RecoveryMode:
						ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
					RetryNotBefore: null));
		}

		List<Guid> scheduledAccountIds = new();
		var recovery = await AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(1),
				async (_, token) =>
					await Task.Delay(Timeout.InfiniteTimeSpan, token),
				(accountId, token) =>
				{
					Assert.False(token.IsCancellationRequested);
					scheduledAccountIds.Add(accountId);
					return Task.CompletedTask;
				});

		Assert.False(recovery.DidDiskDiscoveryFail);
		Assert.Equal(
			accountIds.OrderBy(id => id),
			recovery.FailedAccountIds.OrderBy(id => id));
		Assert.Equal(
			accountIds.OrderBy(id => id),
			scheduledAccountIds.OrderBy(id => id));
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_WhenAppCancellationIsRequested_StopsWithoutSchedulingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid accountId = Guid.NewGuid();
		string StatePath(Guid id) => Path.Combine(
			accountRootPath,
			id.ToString("N"),
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 0,
				AccountIdentity: "removed@example.com",
				FailureReason: "manual safety latch",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
				RetryNotBefore: null));
		TaskCompletionSource purgeStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int scheduledRetryCount = 0;
		using CancellationTokenSource cancellationSource = new();

		Task recoveryTask = AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(30),
				async (_, token) =>
				{
					purgeStarted.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, token);
				},
				(_, _) =>
				{
					Interlocked.Increment(ref scheduledRetryCount);
					return Task.CompletedTask;
				},
				cancellationSource.Token);
		await purgeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellationSource.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => recoveryTask);
		Assert.Equal(0, scheduledRetryCount);
	}

	[Fact]
	public async Task RecoverClaudeUsageSafetyStateFilesAsync_WhenAppCancellationStartsDuringTimeoutRetryScheduling_StopsBeforeNextAccount()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid[] accountIds = [Guid.NewGuid(), Guid.NewGuid()];
		string StatePath(Guid id) => Path.Combine(
			accountRootPath,
			id.ToString("N"),
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(StatePath);
		foreach (Guid accountId in accountIds)
		{
			await store.SaveAsync(
				accountId,
				new ClaudeUsageSafetyState(
					AutomaticRevalidationAttemptCount: 0,
					AccountIdentity: "removed@example.com",
					FailureReason: "manual safety latch",
					RecoveryMode:
						ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
					RetryNotBefore: null));
		}

		TaskCompletionSource firstScheduleStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowFirstScheduleToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int scheduledRetryCount = 0;
		using CancellationTokenSource cancellationSource = new();
		Task recoveryTask = AiUsageDashboard.App.App
			.RecoverClaudeUsageSafetyStateFilesAsync(
				store,
				Array.Empty<Guid>(),
				canIdentifyRemovedAccounts: true,
				accountRootPath,
				TimeSpan.FromSeconds(1),
				async (_, token) =>
					await Task.Delay(Timeout.InfiniteTimeSpan, token),
				async (_, _) =>
				{
					int attempt =
						Interlocked.Increment(ref scheduledRetryCount);
					if (attempt == 1)
					{
						firstScheduleStarted.TrySetResult();
						await allowFirstScheduleToFinish.Task;
					}
				},
				cancellationSource.Token);
		await firstScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellationSource.Cancel();
		allowFirstScheduleToFinish.TrySetResult();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => recoveryTask);
		Assert.Equal(1, scheduledRetryCount);
	}

	private static ClaudeCliUsagePoller CreateRemovalPurger(
		IClaudeUsageSafetyStateStore store,
		Func<Guid, TimeSpan, CancellationToken, Task<bool>>? recovery = null)
	{
		return new ClaudeCliUsagePoller(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) => throw new InvalidOperationException("process must not start"),
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			store,
			containedUsageAttemptRecovery: recovery);
	}

	[Fact]
	public void StableApplicationUserModelId_DoesNotContainVersion()
	{
		string applicationUserModelId =
			AiUsageDashboard.App.App.StableApplicationUserModelId;

		Assert.Equal("AiUsageDashboard.Desktop", applicationUserModelId);
		Assert.DoesNotContain("1.0", applicationUserModelId, StringComparison.Ordinal);
	}

	[Fact]
	public void RestartWaitBudget_ExceedsExplicitShutdownDrainBudget()
	{
		Assert.True(
			AiUsageDashboard.App.App.RestartWaitBudget >=
				AiUsageDashboard.App.App.ShutdownDrainBudget + TimeSpan.FromSeconds(15),
			$"Restart wait budget ({AiUsageDashboard.App.App.RestartWaitBudget}) must leave " +
			$"enough time after the explicit shutdown drains " +
			$"({AiUsageDashboard.App.App.ShutdownDrainBudget}).");
	}

	[Fact]
	public void ShutdownDrainBudget_CoversBoundedAgyCommandAndCleanup()
	{
		Assert.True(
			AiUsageDashboard.App.App.UsageRefreshShutdownBudget >=
				AntigravityOfficialPrintTiming.MaximumCaptureDuration +
				DashboardViewModel.MaximumAntigravityReportedAccountReadDuration +
				DashboardViewModel.MaximumAntigravityReportedAccountReadDuration +
				TimeSpan.FromSeconds(5),
			$"Usage refresh shutdown budget " +
			$"({AiUsageDashboard.App.App.UsageRefreshShutdownBudget}) must cover " +
			"the bounded AGY capability check and usage process cleanup.");
	}

	[Theory]
	[InlineData(false, "結束")]
	[InlineData(true, "重新啟動")]
	public void CreateActiveAntigravitySetupCancellationMessage_ExplainsCooperativeCloseAndResume(
		bool isRestart,
		string expectedAction)
	{
		string message = AiUsageDashboard.App.App
			.CreateActiveAntigravitySetupCancellationMessage(isRestart);

		Assert.Contains("關閉連接視窗", message, StringComparison.Ordinal);
		Assert.Contains("尚未完成的連接會取消", message, StringComparison.Ordinal);
		Assert.Contains("重新啟動後會自動繼續", message, StringComparison.Ordinal);
		Assert.DoesNotContain("強制停止", message, StringComparison.Ordinal);
		Assert.Contains(expectedAction, message, StringComparison.Ordinal);
	}

	[Fact]
	public void CreateSingleInstanceActivationRequest_WithEnsureArgument_ReturnsBoundedRequest()
	{
		SingleInstanceActivationRequest request =
			AiUsageDashboard.App.App.CreateSingleInstanceActivationRequest(
				new[]
				{
					"--unrelated-sensitive-value",
					AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount
				});

		Assert.Equal(
			SingleInstanceActivationRequest.EnsureAntigravityAccount,
			request);
	}

	[Fact]
	public void CreateLaunchIntent_WithStartupArgument_ReturnsLogonStartup()
	{
		AppLaunchIntent launchIntent =
			AiUsageDashboard.App.App.CreateLaunchIntent(
				new[] { "--startup" });

		Assert.Equal(AppLaunchIntent.LogonStartup, launchIntent);
	}

	[Fact]
	public void CreateLaunchIntent_WithSimilarStartupArgument_RemainsInteractive()
	{
		AppLaunchIntent launchIntent =
			AiUsageDashboard.App.App.CreateLaunchIntent(
				new[] { "--startup=true" });

		Assert.Equal(AppLaunchIntent.Interactive, launchIntent);
	}

	[Fact]
	public void CreateLaunchIntent_WithEnsureAndStartupArguments_PrefersEnsure()
	{
		AppLaunchIntent launchIntent =
			AiUsageDashboard.App.App.CreateLaunchIntent(
				new[]
				{
					"--startup",
					AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount
				});

		Assert.Equal(AppLaunchIntent.EnsureAntigravityAccount, launchIntent);
	}

	[Fact]
	public void CreateLaunchIntent_WithShutdownEnsureAndStartupArguments_PrefersShutdown()
	{
		AppLaunchIntent launchIntent =
			AiUsageDashboard.App.App.CreateLaunchIntent(
				new[]
				{
					"--startup",
					AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount,
					"--shutdown-for-update"
				});

		Assert.Equal(AppLaunchIntent.ShutdownForUpdate, launchIntent);
	}

	[Fact]
	public void TryCreateSingleInstanceActivationRequest_LogonStartupIsQuiet()
	{
		bool wasCreated = AiUsageDashboard.App.App
			.TryCreateSingleInstanceActivationRequest(
				AppLaunchIntent.LogonStartup,
				out _);

		Assert.False(wasCreated);
	}

	[Theory]
	[InlineData(
		(int)AppLaunchIntent.Interactive,
		(int)SingleInstanceActivationRequest.ShowFloatingWidget)]
	[InlineData(
		(int)AppLaunchIntent.EnsureAntigravityAccount,
		(int)SingleInstanceActivationRequest.EnsureAntigravityAccount)]
	[InlineData(
		(int)AppLaunchIntent.ShutdownForUpdate,
		(int)SingleInstanceActivationRequest.ShutdownForUpdate)]
	public void TryCreateSingleInstanceActivationRequest_MapsInteractiveIntents(
		int launchIntentValue,
		int expectedRequestValue)
	{
		bool wasCreated = AiUsageDashboard.App.App
			.TryCreateSingleInstanceActivationRequest(
				(AppLaunchIntent)launchIntentValue,
				out SingleInstanceActivationRequest request);

		Assert.True(wasCreated);
		Assert.Equal(expectedRequestValue, (int)request);
	}

	[Theory]
	[InlineData((int)AppLaunchIntent.Interactive, false)]
	[InlineData((int)AppLaunchIntent.EnsureAntigravityAccount, false)]
	[InlineData((int)AppLaunchIntent.ShutdownForUpdate, false)]
	[InlineData((int)AppLaunchIntent.LogonStartup, true)]
	public void ShouldSuppressStartupWindowActivation_OnlyForLogonStartup(
		int launchIntentValue,
		bool expected)
	{
		Assert.Equal(
			expected,
			AiUsageDashboard.App.App.ShouldSuppressStartupWindowActivation(
				(AppLaunchIntent)launchIntentValue));
	}

	[Theory]
	[InlineData(
		(int)LogonStartupRegistrationState.Absent,
		"Windows 登入啟動項：未登錄",
		false)]
	[InlineData(
		(int)LogonStartupRegistrationState.ExactMatch,
		"Windows 登入啟動項：已登錄",
		true)]
	[InlineData(
		(int)LogonStartupRegistrationState.Conflict,
		"Windows 登入啟動項：需修復",
		false)]
	public void CreateLogonStartupMenuPresentation_MapsRegistrationState(
		int stateValue,
		string expectedText,
		bool expectedChecked)
	{
		LogonStartupMenuPresentation presentation =
			AiUsageDashboard.App.App.CreateLogonStartupMenuPresentation(
				canManageRegistration: true,
				state: (LogonStartupRegistrationState)stateValue);

		Assert.Equal(expectedText, presentation.Text);
		Assert.Equal(expectedChecked, presentation.IsChecked);
		Assert.True(presentation.IsEnabled);
	}

	[Fact]
	public void CreateLogonStartupMenuPresentation_QueryFailureIsDisabled()
	{
		LogonStartupMenuPresentation presentation =
			AiUsageDashboard.App.App.CreateLogonStartupMenuPresentation(
				canManageRegistration: true,
				state: null);

		Assert.Equal("Windows 登入啟動項：無法讀取", presentation.Text);
		Assert.False(presentation.IsChecked);
		Assert.False(presentation.IsEnabled);
	}

	[Fact]
	public void CreateLogonStartupMenuPresentation_CommandTooLongIsDisabled()
	{
		LogonStartupMenuPresentation presentation =
			AiUsageDashboard.App.App.CreateLogonStartupMenuPresentation(
				canManageRegistration: true,
				state: null,
				canCreateExpectedCommand: false);

		Assert.Equal(
			"Windows 登入啟動項：無法使用（啟動命令過長）",
			presentation.Text);
		Assert.False(presentation.IsChecked);
		Assert.False(presentation.IsEnabled);
	}

	[Fact]
	public void CreateWindowsStartupAppsStartInfo_UsesOfficialSettingsUri()
	{
		ProcessStartInfo startInfo = AiUsageDashboard.App.App
			.CreateWindowsStartupAppsStartInfo();

		Assert.Equal("ms-settings:startupapps", startInfo.FileName);
		Assert.True(startInfo.UseShellExecute);
	}

	[Fact]
	public void LaunchWindowsStartupAppsSettings_NullShellProcessIsAccepted()
	{
		ProcessStartInfo? observedStartInfo = null;

		AiUsageDashboard.App.App.LaunchWindowsStartupAppsSettings(startInfo =>
		{
			observedStartInfo = startInfo;
			return null;
		});

		Assert.NotNull(observedStartInfo);
	}

	[Fact]
	public void CreateLogonStartupMenuPresentation_NonCanonicalInstallIsDisabled()
	{
		LogonStartupMenuPresentation presentation =
			AiUsageDashboard.App.App.CreateLogonStartupMenuPresentation(
				canManageRegistration: false,
				state: LogonStartupRegistrationState.ExactMatch);

		Assert.Equal(
			"Windows 登入啟動項：僅標準安裝可用",
			presentation.Text);
		Assert.False(presentation.IsChecked);
		Assert.False(presentation.IsEnabled);
	}

	[Fact]
	public void CreateSingleInstanceActivationRequest_WithoutEnsureArgument_ReturnsShowRequest()
	{
		SingleInstanceActivationRequest request =
			AiUsageDashboard.App.App.CreateSingleInstanceActivationRequest(
				new[]
				{
					"--unrelated-sensitive-value"
				});

		Assert.Equal(
			SingleInstanceActivationRequest.ShowFloatingWidget,
			request);
	}

	[Fact]
	public void CreateSingleInstanceActivationRequest_WithShutdownArgument_PrefersShutdown()
	{
		SingleInstanceActivationRequest request =
			AiUsageDashboard.App.App.CreateSingleInstanceActivationRequest(
				new[]
				{
					AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount,
					"--shutdown-for-update"
				});

		Assert.Equal(
			SingleInstanceActivationRequest.ShutdownForUpdate,
			request);
	}

	[Fact]
	public void CreateSingleInstanceActivationRequest_WithSimilarShutdownArgument_DoesNotShutdown()
	{
		SingleInstanceActivationRequest request =
			AiUsageDashboard.App.App.CreateSingleInstanceActivationRequest(
				new[] { "--shutdown-for-update=true" });

		Assert.Equal(
			SingleInstanceActivationRequest.ShowFloatingWidget,
			request);
	}

	[Theory]
	[InlineData(false, 0, true)]
	[InlineData(false, 2, true)]
	[InlineData(false, 1, true)]
	[InlineData(false, 3, false)]
	[InlineData(true, 3, true)]
	public void ShouldShowFloatingWidgetOnStartup_NormalizesLegacySurfaces(
		bool isWidgetVisible,
		int startupSurfaceValue,
		bool expected)
	{
		DashboardStartupSurface startupSurface =
			(DashboardStartupSurface)startupSurfaceValue;
		DashboardShellPreferences preferences = new(
			isWidgetVisible,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: string.Empty,
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: startupSurface);

		Assert.Equal(
			expected,
			AiUsageDashboard.App.App
				.ShouldShowFloatingWidgetOnStartup(preferences));
	}

	[Fact]
	public void DefaultShellPreferences_ShowFloatingWidget()
	{
		Assert.True(DashboardShellPreferences.Default.IsWidgetVisible);
		Assert.Equal(
			DashboardStartupSurface.Widget,
			DashboardShellPreferences.Default.StartupSurface);
	}

	[Fact]
	public void ShouldShowFloatingWidgetOnStartup_EnsureLaunchOverridesTrayPreference()
	{
		DashboardShellPreferences preferences = new(
			IsWidgetVisible: false,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: string.Empty,
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Tray);

		Assert.True(
			AiUsageDashboard.App.App.ShouldShowFloatingWidgetOnStartup(
				preferences,
				shouldForceShow: true));
	}

	[Fact]
	public void CreateDefaultAntigravityAccountProfile_DoesNotInventNickname()
	{
		AccountProfile profile = AiUsageDashboard.App.App
			.CreateDefaultAntigravityAccountProfile();

		Assert.NotEqual(Guid.Empty, profile.Id);
		Assert.Equal(ProviderKind.Antigravity, profile.Provider);
		Assert.Equal(string.Empty, profile.DisplayName);
	}

	[Fact]
	public void CreateEnsureFailureNotification_WhenChangesCommitted_ReportsCreatedCard()
	{
		const string DiagnosticPath =
			@"C:\Users\test\AppData\Local\AiUsageDashboard\diagnostics.log";
		AccountProfileStoreException exception = new(
			"backup failed",
			hasCommittedChanges: true);

		AntigravityAccountEnsureFailureNotification notification =
			AiUsageDashboard.App.App
				.CreateAntigravityAccountEnsureFailureNotification(
					exception,
					new AppDiagnosticWriteResult(
						DiagnosticPath,
						WasWritten: true));

		Assert.Contains("已新增", notification.Title, StringComparison.Ordinal);
		Assert.Contains("新增到 AI Usage", notification.Message, StringComparison.Ordinal);
		Assert.Contains("備份", notification.Message, StringComparison.Ordinal);
		Assert.Contains(
			DiagnosticPath,
			notification.Message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"手動新增",
			notification.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CreateEnsureFailureNotification_WhenChangesNotCommitted_RequestsManualAdd()
	{
		AccountProfileStoreException exception = new(
			"write failed",
			hasCommittedChanges: false);

		AntigravityAccountEnsureFailureNotification notification =
			AiUsageDashboard.App.App
				.CreateAntigravityAccountEnsureFailureNotification(
					exception,
					new AppDiagnosticWriteResult(
						FilePath: null,
						WasWritten: false));

		Assert.Contains(
			"手動新增",
			notification.Message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"已建立 Antigravity 卡片",
			notification.Message,
			StringComparison.Ordinal);
		Assert.Contains(
			"診斷紀錄也無法建立",
			notification.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CreateUnexpectedEnsureFailureNotification_IncludesDiagnosticPath()
	{
		const string DiagnosticPath =
			@"C:\Users\test\AppData\Local\AiUsageDashboard\diagnostics.log";

		AntigravityAccountEnsureFailureNotification notification =
			AiUsageDashboard.App.App
				.CreateUnexpectedAntigravityAccountEnsureFailureNotification(
					new AppDiagnosticWriteResult(
						DiagnosticPath,
						WasWritten: true));

		Assert.Contains(
			DiagnosticPath,
			notification.Message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"請查看 Dashboard 診斷紀錄",
			notification.Message,
			StringComparison.Ordinal);
	}

}
