using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Core.Localization;
namespace AiUsageDashboard.Antigravity.Setup;

public partial class SetupWindow : Window
{
	private sealed record UsageWindowPresentation(
		string DisplayName,
		string RemainingText,
		string ResetText);

	private sealed record SetupAttemptPersistence(
		Func<
			AntigravityMachineSetupCandidate,
			CancellationToken,
			Task> BeforeApprovalCommitAsync,
		Func<
			AntigravityMachineSetupCandidate,
			CancellationToken,
			Task> AfterApprovalCommittedAsync);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRectangle
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInformation
	{
		public int Size;
		public NativeRectangle MonitorArea;
		public NativeRectangle WorkArea;
		public uint Flags;
	}

	private const uint MonitorDefaultToNearest = 0x00000002;
	private const double WorkAreaMargin = 16;

	private static readonly string AppVersion = ResolveAppVersion(
		ReadCurrentExecutableProductVersion,
		typeof(SetupWindow).Assembly.GetName().Version);

	private readonly AntigravitySetupWorkflow _workflow;
	private readonly TaskCompletionSource<AntigravitySetupDialogResult>
		_completionSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly double _preferredMinHeight;
	private readonly double _preferredMinWidth;
	private Task? _activeOperation;
	private FrameworkElement? _visiblePanel;
	private HwndSource? _windowSource;
	private AntigravityMachineSetupStage? _lastStage;
	private string _failureDiagnosticInfo = string.Empty;
	private Func<string>? _progressTextProvider;
	private Func<string>? _footerTextProvider;
	private Exception? _completionFailure;
	private bool _allowClose;
	private bool _isClosePending;
	private bool _isWorkAreaRefreshQueued;
	private bool _shouldTransferOperationFocus = true;

	internal SetupWindow(
		IAntigravityMachineSetupService setupService,
		Guid setupAttemptId,
		ResourceDictionary? windowResources = null)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				UiText.Get("Windows.Setup.TheAntigravitySetupAttemptIDCannotBeEmpty"),
				nameof(setupAttemptId));
		}

		InitializeComponent();
		if (windowResources is not null)
		{
			Resources.MergedDictionaries.Add(windowResources);
		}
		SetupAttemptPersistence persistence =
			CreateSetupAttemptPersistence(setupAttemptId);
		_workflow = new AntigravitySetupWorkflow(
			setupService,
			beforeApprovalCommitAsync:
				persistence.BeforeApprovalCommitAsync,
			afterApprovalCommittedAsync:
				persistence.AfterApprovalCommittedAsync);
		_preferredMinHeight = MinHeight;
		_preferredMinWidth = MinWidth;
		UiText.LanguageChanged += LanguageChanged;
		Loaded += SetupWindow_Loaded;
	}

	internal Task<AntigravitySetupDialogResult> Completion =>
		_completionSource.Task;

	private static SetupAttemptPersistence CreateSetupAttemptPersistence(
		Guid attemptId)
	{
		AntigravitySetupAttemptStateStore attemptStateStore =
			AntigravitySetupAttemptStateStore.CreateDefault();
		AntigravitySetupApprovalReceiptStore receiptStore =
			AntigravitySetupApprovalReceiptStore.CreateDefault();
		return new SetupAttemptPersistence(
			(candidate, cancellationToken) =>
				attemptStateStore.MarkApprovalRequestedAsync(
					attemptId,
					candidate.SourceKind,
					candidate.AccountIdentity,
					cancellationToken),
			(candidate, cancellationToken) => receiptStore.WriteAsync(
				attemptId,
				candidate.SourceKind,
				candidate.AccountIdentity,
				cancellationToken));
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);

		IntPtr windowHandle = new WindowInteropHelper(this).Handle;
		_windowSource = HwndSource.FromHwnd(windowHandle);
		_windowSource?.AddHook(WindowMessageHook);
		LocationChanged += SetupWindow_LocationChanged;
		UpdateWorkAreaConstraints(windowHandle);
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		_shouldTransferOperationFocus = true;
		base.OnPreviewKeyDown(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		UiText.LanguageChanged -= LanguageChanged;
		LocationChanged -= SetupWindow_LocationChanged;
		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		base.OnClosed(e);
		_completionSource.TrySetResult(new AntigravitySetupDialogResult(
			_workflow.DialogOutcome,
			_completionFailure));
	}

	private void LanguageChanged(object? sender, EventArgs e)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(RefreshLocalizedPresentation);
			return;
		}

		RefreshLocalizedPresentation();
	}

	internal void RefreshLocalizedPresentation()
	{
		if (_progressTextProvider is not null)
		{
			ProgressTextBlock.Text = _progressTextProvider();
		}
		if (_footerTextProvider is not null)
		{
			FooterStatusTextBlock.Text = _footerTextProvider();
		}
		if (ReferenceEquals(_visiblePanel, ReviewPanel) &&
			(_workflow.Candidate is AntigravityMachineSetupCandidate candidate))
		{
			RefreshReviewPresentation(candidate);
		}
		else if (ReferenceEquals(_visiblePanel, FailurePanel))
		{
			RefreshFailurePresentation();
		}
	}

	private void SetProgressText(Func<string> textProvider)
	{
		_progressTextProvider = textProvider;
		ProgressTextBlock.Text = textProvider();
	}

	private void SetFooterText(Func<string> textProvider)
	{
		_footerTextProvider = textProvider;
		FooterStatusTextBlock.Text = textProvider();
	}

	internal static double CalculateMaximumWindowHeight(
		double workAreaPixelHeight,
		double dpiScaleY,
		double minimumHeight,
		double verticalMargin)
	{
		return CalculateMaximumWindowDimension(
			workAreaPixelHeight,
			dpiScaleY,
			minimumHeight,
			verticalMargin,
			nameof(minimumHeight));
	}

	internal static double CalculateMaximumWindowWidth(
		double workAreaPixelWidth,
		double dpiScaleX,
		double minimumWidth,
		double horizontalMargin)
	{
		return CalculateMaximumWindowDimension(
			workAreaPixelWidth,
			dpiScaleX,
			minimumWidth,
			horizontalMargin,
			nameof(minimumWidth));
	}

	internal static bool RequiresRefreshForWindowMessage(int message)
	{
		return message is 0x001A or 0x007E or 0x02E0;
	}

	private static double CalculateMaximumWindowDimension(
		double workAreaPixels,
		double dpiScale,
		double preferredMinimum,
		double margin,
		string minimumParameterName)
	{
		if (!double.IsFinite(preferredMinimum) || (preferredMinimum < 0))
		{
			throw new ArgumentOutOfRangeException(minimumParameterName);
		}

		if (!double.IsFinite(workAreaPixels) ||
			(workAreaPixels <= 0) ||
			!double.IsFinite(dpiScale) ||
			(dpiScale <= 0))
		{
			return preferredMinimum;
		}

		double normalizedMargin = double.IsFinite(margin)
			? Math.Max(0, margin)
			: 0;
		return Math.Max(
			1,
			Math.Floor((workAreaPixels / dpiScale) - normalizedMargin));
	}

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(
		IntPtr windowHandle,
		uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(
		IntPtr monitorHandle,
		ref MonitorInformation monitorInformation);

	private static string FormatDuration(TimeSpan duration)
	{
		if (duration <= TimeSpan.Zero)
		{
			return UiText.Get("Windows.Setup.Soon");
		}

		if (duration.TotalDays >= 1)
		{
			return string.Format(
				UiText.Culture,
				UiText.Get("Windows.Setup.In0DaysAnd1Hours"),
				(int)duration.TotalDays,
				duration.Hours);
		}

		if (duration.TotalHours >= 1)
		{
			return string.Format(
				UiText.Culture,
				UiText.Get("Windows.Setup.In0HoursAnd1Minutes"),
				(int)duration.TotalHours,
				duration.Minutes);
		}

		return string.Format(
			UiText.Culture,
			UiText.Get("Windows.Setup.In0Minutes"),
			Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes)));
	}

	internal static string GetProgressText(
		AntigravityMachineSetupStage stage)
	{
		return stage switch
		{
			AntigravityMachineSetupStage.LoadingReviewedContract =>
				UiText.Get("Windows.Setup.CheckingSupportedAntigravityVersions"),
			AntigravityMachineSetupStage.DiscoveringExecutable =>
				UiText.Get("Windows.Setup.LookingForAntigravityOnThisComputer"),
			AntigravityMachineSetupStage.PreparingPrivateStorage =>
				UiText.Get("Windows.Setup.PreparingThisComputer"),
			AntigravityMachineSetupStage.CalibratingPrompt =>
				UiText.Get("Windows.Setup.CheckingThatAntigravityIsReady"),
			AntigravityMachineSetupStage.MaterializingProfile =>
				UiText.Get("Windows.Setup.PreparingLocalConnectionSettings"),
			AntigravityMachineSetupStage.ValidatingUsage =>
				UiText.Get("Windows.Setup.ReadingAntigravitySFourUsageValues"),
			AntigravityMachineSetupStage.ReadyForApproval =>
				UiText.Get("Windows.Setup.PreparingAccountReview"),
			AntigravityMachineSetupStage.Approving =>
				UiText.Get("Windows.Setup.FinishingAntigravityConnection"),
			_ => UiText.Get("Windows.Setup.ConnectingAntigravity233")
		};
	}

	internal static string CreateSafeDiagnosticInfo(
		string appVersion,
		AntigravityMachineSetupFailureKind failureKind,
		AntigravityMachineSetupStage? lastStage)
	{
		return string.Join(
			Environment.NewLine,
			UiText.Get("Windows.Setup.AntigravityConnectionTechnicalInformation"),
			UiText.Format("Windows.Setup.AppVersion0", appVersion),
			UiText.Format("Windows.Setup.FailureType0", GetFailureKindDisplayText(failureKind)),
			UiText.Format("Windows.Setup.LastStage0", GetStageDisplayText(lastStage)),
			UiText.Format("Windows.Setup.SuggestedNextSteps0", GetDiagnosticNextStep(failureKind, lastStage)));
	}

	internal static string GetFailureKindDisplayText(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.None => UiText.Get("Windows.Setup.NoneNone"),
			AntigravityMachineSetupFailureKind.ConsentRequired =>
				UiText.Get("Windows.Setup.UsageDisclosureConfirmationRequiredConsentRequired"),
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				UiText.Get("Windows.Setup.SupportedAntigravityCLINotFoundUnsupportedBuild"),
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				UiText.Get("Windows.Setup.MultipleAntigravityCLIsFoundAmbiguousExecutable"),
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				UiText.Get("Windows.Setup.OtherAntigravityWindowsAreRunningExistingProcessDetected"),
			AntigravityMachineSetupFailureKind.PrivateStorageRejected =>
				UiText.Get("Windows.Setup.CannotPrepareConnectionDataPrivateStorageRejected"),
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				UiText.Get("Windows.Setup.AntigravitySettingsRejectedSettingsRejected"),
			AntigravityMachineSetupFailureKind.PromptRejected =>
				UiText.Get("Windows.Setup.AntigravitySignInOrSetupIsIncompletePromptRejected"),
			AntigravityMachineSetupFailureKind.UsageRejected =>
				UiText.Get("Windows.Setup.CannotVerifyUsageUsageRejected"),
			AntigravityMachineSetupFailureKind.ApprovalRejected =>
				UiText.Get("Windows.Setup.CannotSaveConnectionApprovalRejected"),
			AntigravityMachineSetupFailureKind.UnexpectedFailure =>
				UiText.Get("Windows.Setup.CannotFinishConnectionUnexpectedFailure"),
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				UiText.Get("Windows.Setup.AnotherUsageCheckIsRunningExecutionBusy"),
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				UiText.Get("Windows.Setup.ConfirmationRequiredBeforeCheckingUsageAgainSafetyRevalidationRequired"),
			_ => UiText.Format("Windows.Setup.Unknown0", failureKind)
		};
	}

	internal static string GetStageDisplayText(
		AntigravityMachineSetupStage? stage)
	{
		return stage switch
		{
			null => UiText.Get("Windows.Setup.NotStarted"),
			AntigravityMachineSetupStage.LoadingReviewedContract =>
				UiText.Get("Windows.Setup.LoadingCompatibilityDataLoadingReviewedContract"),
			AntigravityMachineSetupStage.DiscoveringExecutable =>
				UiText.Get("Windows.Setup.FindingAntigravityCLIDiscoveringExecutable"),
			AntigravityMachineSetupStage.PreparingPrivateStorage =>
				UiText.Get("Windows.Setup.PreparingConnectionDataPreparingPrivateStorage"),
			AntigravityMachineSetupStage.CalibratingPrompt =>
				UiText.Get("Windows.Setup.CheckingAntigravityReadinessCalibratingPrompt"),
			AntigravityMachineSetupStage.MaterializingProfile =>
				UiText.Get("Windows.Setup.PreparingLocalConnectionSettingsMaterializingProfile"),
			AntigravityMachineSetupStage.ValidatingUsage =>
				UiText.Get("Windows.Setup.ReadingAntigravityUsageValidatingUsage"),
			AntigravityMachineSetupStage.ReadyForApproval =>
				UiText.Get("Windows.Setup.PreparingAccountReviewReadyForApproval"),
			AntigravityMachineSetupStage.Approving =>
				UiText.Get("Windows.Setup.SavingAntigravityConnectionApproving"),
			_ => UiText.Format("Windows.Setup.Unknown0", stage)
		};
	}

	internal static string GetDiagnosticNextStep(
		AntigravityMachineSetupFailureKind failureKind,
		AntigravityMachineSetupStage? lastStage)
	{
		return (failureKind, lastStage) switch
		{
			(AntigravityMachineSetupFailureKind.ExecutionBusy, _) =>
				UiText.Get("Windows.Setup.WaitForTheCurrentAntigravityUsageCheckTo"),
			(AntigravityMachineSetupFailureKind.SafetyRevalidationRequired, _) =>
				UiText.Get("Windows.Setup.ConfirmThatOneReadOnlyUsageCheckCan"),
			(
				AntigravityMachineSetupFailureKind.UnsupportedBuild,
				AntigravityMachineSetupStage.DiscoveringExecutable) =>
				UiText.Get("Windows.Setup.RunAgyVersionIfItFailsOrReports"),
			(
				AntigravityMachineSetupFailureKind.AmbiguousExecutable,
				AntigravityMachineSetupStage.DiscoveringExecutable) =>
				UiText.Get("Windows.Setup.KeepOnlyTheAntigravityCLIYouWantTo"),
			(
				AntigravityMachineSetupFailureKind.UsageRejected,
				AntigravityMachineSetupStage.ValidatingUsage) =>
				UiText.Get("Windows.Setup.CheckThatAntigravityIsSignedInAndCan"),
			(AntigravityMachineSetupFailureKind.ConsentRequired, _) =>
				UiText.Get("Windows.Setup.ReturnToAIUsageChooseToConnectAgain"),
			(AntigravityMachineSetupFailureKind.ExistingProcessDetected, _) =>
				UiText.Get("Windows.Setup.CloseOtherAntigravityWindowsThenTryAgain"),
			(AntigravityMachineSetupFailureKind.PrivateStorageRejected, _) =>
				UiText.Get("Windows.Setup.TryAgainIfItStillFailsShareThe"),
			(AntigravityMachineSetupFailureKind.SettingsRejected, _) =>
				UiText.Get("Windows.Setup.CheckThatAntigravityIsSignedInAndWorking"),
			(AntigravityMachineSetupFailureKind.PromptRejected, _) =>
				UiText.Get("Windows.Setup.OpenAntigravityCheckThatItIsSignedIn"),
			(AntigravityMachineSetupFailureKind.ApprovalRejected, _) =>
				UiText.Get("Windows.Setup.TryAgainIfItStillFailsShareThe"),
			_ =>
				UiText.Get("Windows.Setup.TryAgainIfItStillFailsShareThe")
		};
	}

	internal static string ResolveAppVersion(
		Func<string?> productVersionProvider,
		Version? assemblyVersion)
	{
		ArgumentNullException.ThrowIfNull(productVersionProvider);

		try
		{
			if (TryNormalizeDiagnosticVersion(
				productVersionProvider(),
				out string productVersion))
			{
				return productVersion;
			}
		}
		catch (Exception)
		{
			// File metadata is diagnostic-only. Fall back to the assembly version.
		}

		return assemblyVersion?.ToString() ?? UiText.Get("Windows.Setup.Unknown");
	}

	private static string? ReadCurrentExecutableProductVersion()
	{
		string? processPath = Environment.ProcessPath;
		return string.IsNullOrWhiteSpace(processPath)
			? null
			: FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
	}

	private static bool TryNormalizeDiagnosticVersion(
		string? candidate,
		out string normalized)
	{
		normalized = string.Empty;

		if (string.IsNullOrWhiteSpace(candidate) ||
			candidate.Any(char.IsControl))
		{
			return false;
		}

		normalized = candidate.Trim();
		return normalized.Length > 0;
	}

	internal static string GetUsageWindowDisplayName(string stableWindowId)
	{
		return stableWindowId switch
		{
			"agy.gemini.weekly" => UiText.Get("Windows.Setup.WeeklyUsageGemini"),
			"agy.gemini.rolling-5h" => UiText.Get("Windows.Setup.5HourUsageGemini"),
			"agy.claude.weekly" => UiText.Get("Windows.Setup.WeeklyUsageClaudeGPT"),
			"agy.claude.rolling-5h" => UiText.Get("Windows.Setup.5HourUsageClaudeGPT"),
			_ => UiText.Get("Windows.Setup.AntigravityUsageWindow")
		};
	}

	internal static string GetAccountIdentityDisplayText(
		string accountIdentity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentity);
		return string.Equals(
			accountIdentity,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			StringComparison.OrdinalIgnoreCase)
			? UiText.Get("Windows.Setup.CurrentAntigravitySignInEmailUnavailable")
			: accountIdentity;
	}

	private static UsageWindowPresentation CreatePresentation(
		AntigravityProductionUsageWindow window)
	{
		string resetText = window.IsAvailable
			? UiText.Get("Windows.Setup.AvailableNow")
			: window.ResetsIn.HasValue
				? UiText.Format("Windows.Setup.Resets0", FormatDuration(window.ResetsIn.Value))
				: UiText.Get("Windows.Setup.ResetTimeNotProvided");
		return new UsageWindowPresentation(
			GetUsageWindowDisplayName(window.StableWindowId),
			UiText.Format("Windows.Setup.00Remaining", window.RemainingPercent),
			resetText);
	}

	private async void ApproveButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		CaptureOperationFocusMode(sender);
		await RunEventHandlerAsync(ApproveAsync);
	}

	private async Task ApproveAsync()
	{
		_lastStage = AntigravityMachineSetupStage.Approving;
		SetBusyView(() => UiText.Get("Windows.Setup.FinishingAntigravityConnection"));
		CancelButton.IsEnabled = false;
		_activeOperation = _workflow.ApproveAsync(
			isReviewConfirmed: true);

		try
		{
			bool approved = await (Task<bool>)_activeOperation;

			if (_isClosePending)
			{
				return;
			}

			if (approved)
			{
				await CompleteSetupAsync();
				return;
			}
			else
			{
				if (ShouldAutoCloseAfterApprovalUncertainty(
						_workflow.HasApprovalStarted))
				{
					await CloseAfterWorkflowDisposalAsync();
					return;
				}

				ShowWorkflowResult();
			}
		}
		finally
		{
			_activeOperation = null;

			if (!_isClosePending)
			{
				CancelButton.IsEnabled = true;
			}
		}
	}

	private async void CancelButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		CaptureOperationFocusMode(sender);
		await RunEventHandlerAsync(CancelAndCloseAsync);
	}

	private async Task CancelAndCloseAsync()
	{
		if (_isClosePending)
		{
			return;
		}

		_isClosePending = true;
		string closingStatusKey =
			(_workflow.State == AntigravitySetupWorkflowState.Approving) ||
			(_workflow.HasApprovalStarted)
				? "Windows.Setup.FinishingTheConnectionTheWindowWillCloseWhen"
				: "Windows.Setup.CancellingAndCleaningUpThisConnection";
		SetProgressText(() => UiText.Get(closingStatusKey));
		SetFooterText(() => UiText.Get(closingStatusKey));
		CancelButton.IsEnabled = false;
		FocusProgressIndicator();
		QueueLiveRegionAnnouncement(FooterStatusTextBlock);
		bool wasAlreadyFailed =
			_workflow.State == AntigravitySetupWorkflowState.Failed;
		await _workflow.CancelAsync();

		if (_activeOperation is not null)
		{
			try
			{
				await _activeOperation;
			}
			catch (OperationCanceledException)
			{
			}
		}

		if (!wasAlreadyFailed &&
			(_workflow.State == AntigravitySetupWorkflowState.Failed) &&
			(_workflow.FailureKind ==
				AntigravityMachineSetupFailureKind.PrivateStorageRejected))
		{
			_isClosePending = false;
			ShowFailure();
			return;
		}

		await _workflow.DisposeAsync();
		_allowClose = true;

		Close();
	}

	private async void OpenAiUsageButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		CaptureOperationFocusMode(sender);
		await RunEventHandlerAsync(CloseAfterWorkflowDisposalAsync);
	}

	private async void RetryButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		CaptureOperationFocusMode(sender);
		await RunEventHandlerAsync(RetryAsync);
	}

	private async Task RetryAsync()
	{
		if (_workflow.CanRevalidateSafety)
		{
			await StartSafetyRevalidationAsync();
			return;
		}

		_workflow.Reset();
		_lastStage = null;
		_failureDiagnosticInfo = string.Empty;
		await StartPreparationAsync();
	}

	internal static bool ShouldAutoCloseAfterApprovalUncertainty(
		bool hasApprovalStarted)
	{
		return hasApprovalStarted;
	}

	private async Task StartSafetyRevalidationAsync()
	{
		if (_activeOperation is not null)
		{
			return;
		}

		_lastStage = AntigravityMachineSetupStage.ValidatingUsage;
		SetBusyView(() => UiText.Get("Windows.Setup.CheckingAntigravityUsageAgain"));
		CancelButton.IsEnabled = false;
		_activeOperation = _workflow.RevalidateSafetyAsync();

		try
		{
			await _activeOperation;

			if (!_isClosePending)
			{
				ShowWorkflowResult();
			}
		}
		finally
		{
			_activeOperation = null;

			if (!_isClosePending)
			{
				CancelButton.IsEnabled = true;
			}
		}
	}

	private void CopyDiagnosticButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(_failureDiagnosticInfo))
		{
			return;
		}

		try
		{
			Clipboard.SetText(_failureDiagnosticInfo);
			SetFooterText(() => UiText.Get("Windows.Setup.TechnicalInformationCopied"));
		}
		catch (ExternalException)
		{
			SetFooterText(() => UiText.Get("Windows.Setup.TheClipboardIsUnavailableTryAgainLater"));
		}

		QueueLiveRegionAnnouncement(FooterStatusTextBlock);
	}

	private void FailureDiagnosticDisclosureButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		bool showDetails =
			FailureDiagnosticDetailsPanel.Visibility != Visibility.Visible;
		FailureDiagnosticDetailsPanel.Visibility = showDetails
			? Visibility.Visible
			: Visibility.Collapsed;
		FailureDiagnosticDisclosureButton.SetResourceReference(
			System.Windows.Controls.ContentControl.ContentProperty,
			showDetails ? "Windows.Setup.HideTechnicalInformation" : "Windows.Setup.ShowTechnicalInformation");
		QueueLiveRegionAnnouncement(FailureDiagnosticDisclosureButton);
	}

	private void SetBusyView(Func<string> statusTextProvider)
	{
		SetProgressText(statusTextProvider);
		SetFooterText(() => UiText.Get("Windows.Setup.Working"));
		CancelButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Windows.Common.Cancel");
		ShowOnly(ProgressPanel);
	}

	private void FocusProgressIndicator()
	{
		if (!ProgressIndicator.IsVisible || !ProgressIndicator.IsEnabled)
		{
			return;
		}

		ProgressIndicator.Focus();
		Keyboard.Focus(ProgressIndicator);
	}

	private void ShowCompleted()
	{
		SetFooterText(() => UiText.Get("Windows.Setup.ConnectionComplete"));
		CancelButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Windows.Common.Close");
		CancelButton.IsEnabled = true;
		ShowOnly(CompletedPanel);
	}

	private void ShowFailure()
	{
		bool hasCommittedSetting = _workflow.HasCommittedSetting;
		RefreshFailurePresentation();
		FailureDiagnosticDetailsPanel.Visibility = Visibility.Collapsed;
		FailureDiagnosticDisclosureButton.SetResourceReference(
			System.Windows.Controls.ContentControl.ContentProperty,
			"Windows.Setup.ShowTechnicalInformation");
		SetFooterText(() => UiText.Get(hasCommittedSetting
			? "Windows.Setup.ConnectionSettingsSavedCompletionNotConfirmed"
			: "Windows.Setup.AntigravityConnectionIncomplete"));
		CancelButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Windows.Common.Close");
		CancelButton.IsEnabled = true;
		ShowOnly(FailurePanel);
	}

	private void RefreshFailurePresentation()
	{
		bool hasCommittedSetting = _workflow.HasCommittedSetting;
		FailureKindTextBlock.Text = GetFailureTitle(_workflow.FailureKind);
		FailureGuidanceTextBlock.Text = hasCommittedSetting
			? UiText.Get("Windows.Setup.ConnectionSettingsWereSavedButCleanupEncounteredA")
			: GetFailureGuidance(_workflow.FailureKind);
		_failureDiagnosticInfo = CreateSafeDiagnosticInfo(
			AppVersion,
			_workflow.FailureKind,
			_lastStage);
		FailureDiagnosticTextBlock.Text = _failureDiagnosticInfo;
		RetryButton.Content = GetFailureActionText(_workflow.FailureKind);
	}

	internal static string GetFailureActionText(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind ==
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired
			? UiText.Get("Windows.Setup.CheckAntigravityUsageAgain")
			: UiText.Get("Windows.Setup.TryAgain");
	}

	internal static string GetFailureGuidance(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.ConsentRequired =>
				UiText.Get("Windows.Setup.ReturnToAIUsageChooseToConnectAgain"),
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				UiText.Get("Windows.Setup.AIUsageIsRunningAnotherAntigravityUsageCheck"),
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				UiText.Get("Windows.Setup.YourConfirmationIsRequiredBeforeCheckingAntigravityUsage"),
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				UiText.Get("Windows.Setup.ASupportedAntigravityCLICouldNotBeFound"),
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				UiText.Get("Windows.Setup.MultipleAntigravityCLIsWereFoundOnThisComputer"),
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				UiText.Get("Windows.Setup.ThisAntigravityVersionRequiresOtherAntigravityWindowsTo"),
			AntigravityMachineSetupFailureKind.PrivateStorageRejected =>
				UiText.Get("Windows.Setup.AntigravityConnectionSettingsCannotBeSavedRightNow"),
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				UiText.Get("Windows.Setup.TheCurrentAntigravitySettingsCannotBeUsedCheck"),
			AntigravityMachineSetupFailureKind.PromptRejected =>
				UiText.Get("Windows.Setup.AntigravityReadinessCouldNotBeVerifiedOpenAntigravity"),
			AntigravityMachineSetupFailureKind.UsageRejected =>
				UiText.Get("Windows.Setup.AIUsageCouldNotRecognizeTheFourUsage"),
			AntigravityMachineSetupFailureKind.ApprovalRejected =>
				UiText.Get("Windows.Setup.AProblemOccurredWhileFinishingTheConnectionTry"),
			_ =>
				UiText.Get("Windows.Setup.AntigravityConnectionIsIncompleteTryAgainIfThe")
		};
	}

	private static string GetFailureTitle(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				UiText.Get("Windows.Setup.AntigravityUsageCheckIsStillRunning"),
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				UiText.Get("Windows.Setup.ConfirmationRequiredBeforeCheckingAntigravityUsageAgain"),
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				UiText.Get("Windows.Setup.SupportedAntigravityCLINotFound"),
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				UiText.Get("Windows.Setup.MultipleAntigravityInstallationsFound"),
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				UiText.Get("Windows.Setup.CloseOtherAntigravityWindowsFirst"),
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				UiText.Get("Windows.Setup.CannotUseCurrentAntigravitySettings"),
			AntigravityMachineSetupFailureKind.PromptRejected =>
				UiText.Get("Windows.Setup.CannotVerifyAntigravityStatus"),
			AntigravityMachineSetupFailureKind.UsageRejected =>
				UiText.Get("Windows.Setup.CannotVerifyAntigravityUsage"),
			_ => UiText.Get("Windows.Setup.AntigravityConnectionIncomplete")
		};
	}

	private void ShowOnly(FrameworkElement visiblePanel)
	{
		FrameworkElement[] panels =
		{
			WelcomePanel,
			ProgressPanel,
			ReviewPanel,
			FailurePanel,
			CompletedPanel
		};

		foreach (FrameworkElement panel in panels)
		{
			panel.Visibility = ReferenceEquals(panel, visiblePanel)
				? Visibility.Visible
				: Visibility.Collapsed;
		}

		_visiblePanel = visiblePanel;
		StartButton.IsDefault = ReferenceEquals(
			visiblePanel,
			WelcomePanel);
		ApproveButton.IsDefault = ReferenceEquals(
			visiblePanel,
			ReviewPanel);
		RetryButton.IsDefault = ReferenceEquals(
			visiblePanel,
			FailurePanel);
		OpenAiUsageButton.IsDefault = ReferenceEquals(
			visiblePanel,
			CompletedPanel);

		FrameworkElement? liveRegion = ReferenceEquals(
			visiblePanel,
			ProgressPanel)
			? ProgressTextBlock
			: ReferenceEquals(visiblePanel, ReviewPanel)
				? ReviewHeadingTextBlock
				: ReferenceEquals(visiblePanel, FailurePanel)
					? FailureKindTextBlock
					: ReferenceEquals(visiblePanel, CompletedPanel)
						? CompletionTextBlock
						: null;

		Dispatcher.BeginInvoke(
			DispatcherPriority.ContextIdle,
			new Action(
				() =>
				{
					if (!ReferenceEquals(_visiblePanel, visiblePanel))
					{
						return;
					}

					FrameworkElement? focusTarget =
						GetPanelFocusTarget(visiblePanel);
					if ((focusTarget is not null) &&
						focusTarget.IsVisible &&
						focusTarget.IsEnabled)
					{
						focusTarget.Focus();
						Keyboard.Focus(focusTarget);
					}

					if (liveRegion is not null)
					{
						RaiseLiveRegionChanged(liveRegion);
					}
				}));
	}

	private FrameworkElement? GetPanelFocusTarget(
		FrameworkElement visiblePanel)
	{
		if (!_shouldTransferOperationFocus)
		{
			return ReferenceEquals(visiblePanel, ProgressPanel)
				? ProgressIndicator
				: null;
		}

		return ReferenceEquals(visiblePanel, WelcomePanel)
			? StartButton
			: ReferenceEquals(visiblePanel, ProgressPanel)
				? CancelButton.IsEnabled
					? CancelButton
					: ProgressIndicator
				: ReferenceEquals(visiblePanel, ReviewPanel)
					? ApproveButton
					: ReferenceEquals(visiblePanel, FailurePanel)
						? RetryButton
						: OpenAiUsageButton;
	}

	private void CaptureOperationFocusMode(object sender)
	{
		_shouldTransferOperationFocus =
			!PointerFocusRelease.WasInvokedByPointer(sender);
	}

	private void QueueLiveRegionAnnouncement(FrameworkElement liveRegion)
	{
		Dispatcher.BeginInvoke(
			DispatcherPriority.ContextIdle,
			new Action(
				() =>
				{
					if (liveRegion.IsVisible)
					{
						RaiseLiveRegionChanged(liveRegion);
					}
				}));
	}

	private static void RaiseLiveRegionChanged(UIElement liveRegion)
	{
		AutomationPeer? peer =
			UIElementAutomationPeer.FromElement(liveRegion)
			?? UIElementAutomationPeer.CreatePeerForElement(
				liveRegion);
		peer?.RaiseAutomationEvent(
			AutomationEvents.LiveRegionChanged);
	}

	private void ShowReview(AntigravityMachineSetupCandidate candidate)
	{
		RefreshReviewPresentation(candidate);
		ShowOnly(ReviewPanel);
		SetFooterText(() => UiText.Get("Windows.Setup.ReviewTheAccount"));
		CancelButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "Windows.Common.Cancel");
	}

	private void RefreshReviewPresentation(AntigravityMachineSetupCandidate candidate)
	{
		AccountIdentityTextBlock.Text = GetAccountIdentityDisplayText(
			candidate.AccountIdentity);
		UsageWindowsItemsControl.ItemsSource =
			candidate.Windows.Select(CreatePresentation).ToArray();
	}

	private void ShowWorkflowResult()
	{
		if ((_workflow.State == AntigravitySetupWorkflowState.Reviewing) &&
			(_workflow.Candidate is AntigravityMachineSetupCandidate candidate))
		{
			ShowReview(candidate);
			return;
		}

		if (_workflow.State == AntigravitySetupWorkflowState.Completed)
		{
			ShowCompleted();
			return;
		}

		ShowFailure();
	}

	private async void StartButton_Click(
		object sender,
		RoutedEventArgs e)
	{
		CaptureOperationFocusMode(sender);
		await RunEventHandlerAsync(StartPreparationAsync);
	}

	private async Task StartPreparationAsync()
	{
		if (_activeOperation is not null)
		{
			return;
		}

		SetBusyView(() => UiText.Get("Windows.Setup.PreparingThisComputer"));
		Progress<AntigravityMachineSetupProgress> progress = new(
			value =>
			{
				_lastStage = value.Stage;
				SetProgressText(() => GetProgressText(value.Stage));
				QueueLiveRegionAnnouncement(ProgressTextBlock);
			});
		_activeOperation = _workflow.PrepareAsync(
			hasConfirmedCommandReadyPrompt: true,
			isLiveCaptureApproved: true,
			progress: progress);

		try
		{
			await _activeOperation;

			if (!_isClosePending)
			{
				ShowWorkflowResult();
			}
		}
		finally
		{
			_activeOperation = null;
		}
	}

	private async Task CompleteSetupAsync()
	{
		SetFooterText(() => UiText.Get("Windows.Setup.ConnectionComplete"));
		await CloseAfterWorkflowDisposalAsync();
	}

	private void RecordCompletionFailure(Exception failure)
	{
		ArgumentNullException.ThrowIfNull(failure);
		_completionFailure ??= failure;
	}

	private async Task CloseAfterWorkflowDisposalAsync()
	{
		if (_isClosePending)
		{
			return;
		}

		_isClosePending = true;
		CancelButton.IsEnabled = false;

		try
		{
			await _workflow.DisposeAsync();
			_allowClose = true;
			Close();
		}
		catch (Exception exception)
		{
			RecordCompletionFailure(exception);
			_allowClose = false;
			_isClosePending = false;
			if (ShouldAutoCloseAfterApprovalUncertainty(
					_workflow.HasApprovalStarted))
			{
				_allowClose = true;
				Close();
				return;
			}

			ShowFailure();
		}
	}

	private void UpdateWorkAreaConstraints(IntPtr? knownWindowHandle = null)
	{
		IntPtr windowHandle = knownWindowHandle ??
			new WindowInteropHelper(this).Handle;

		if (windowHandle == IntPtr.Zero)
		{
			return;
		}

		IntPtr monitorHandle = MonitorFromWindow(
			windowHandle,
			MonitorDefaultToNearest);
		MonitorInformation monitorInformation = new()
		{
			Size = Marshal.SizeOf<MonitorInformation>()
		};

		double workAreaPixelWidth;
		double workAreaPixelHeight;
		double dpiScaleX;
		double dpiScaleY;

		if ((monitorHandle != IntPtr.Zero) &&
			GetMonitorInfo(monitorHandle, ref monitorInformation))
		{
			workAreaPixelWidth =
				monitorInformation.WorkArea.Right -
				monitorInformation.WorkArea.Left;
			workAreaPixelHeight =
				monitorInformation.WorkArea.Bottom -
				monitorInformation.WorkArea.Top;
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			dpiScaleX = dpi.DpiScaleX;
			dpiScaleY = dpi.DpiScaleY;
		}
		else
		{
			workAreaPixelWidth = SystemParameters.WorkArea.Width;
			workAreaPixelHeight = SystemParameters.WorkArea.Height;
			dpiScaleX = 1;
			dpiScaleY = 1;
		}

		double maximumWidth = CalculateMaximumWindowWidth(
			workAreaPixelWidth,
			dpiScaleX,
			_preferredMinWidth,
			WorkAreaMargin);
		ApplyWidthConstraints(maximumWidth);
		double maximumHeight = CalculateMaximumWindowHeight(
			workAreaPixelHeight,
			dpiScaleY,
			_preferredMinHeight,
			WorkAreaMargin);
		ApplyHeightConstraints(maximumHeight);
	}

	private void ApplyHeightConstraints(double maximumHeight)
	{
		double targetMinHeight = Math.Min(
			_preferredMinHeight,
			maximumHeight);

		if (MaxHeight < targetMinHeight)
		{
			MaxHeight = maximumHeight;
		}

		MinHeight = targetMinHeight;
		MaxHeight = maximumHeight;

		if (Height > MaxHeight)
		{
			Height = MaxHeight;
		}
	}

	private void ApplyWidthConstraints(double maximumWidth)
	{
		double targetMinWidth = Math.Min(
			_preferredMinWidth,
			maximumWidth);

		if (MaxWidth < targetMinWidth)
		{
			MaxWidth = maximumWidth;
		}

		MinWidth = targetMinWidth;
		MaxWidth = maximumWidth;

		if (Width > MaxWidth)
		{
			Width = MaxWidth;
		}
	}

	private void SetupWindow_LocationChanged(
		object? sender,
		EventArgs e)
	{
		QueueWorkAreaRefresh();
	}

	private IntPtr WindowMessageHook(
		IntPtr hwnd,
		int message,
		IntPtr wParam,
		IntPtr lParam,
		ref bool handled)
	{
		if (RequiresRefreshForWindowMessage(message))
		{
			QueueWorkAreaRefresh();
		}

		return IntPtr.Zero;
	}

	private void QueueWorkAreaRefresh()
	{
		if (_isWorkAreaRefreshQueued)
		{
			return;
		}

		_isWorkAreaRefreshQueued = true;
		_ = Dispatcher.BeginInvoke(
			() =>
			{
				_isWorkAreaRefreshQueued = false;
				UpdateWorkAreaConstraints();
			},
			DispatcherPriority.ContextIdle);
	}

	private async void SetupWindow_Loaded(object sender, RoutedEventArgs e)
	{
		await RunEventHandlerAsync(StartPreparationAsync);
	}

	private async void Window_Closing(
		object? sender,
		CancelEventArgs e)
	{
		if (_allowClose)
		{
			return;
		}

		e.Cancel = true;

		if (_isClosePending)
		{
			return;
		}

		await RunEventHandlerAsync(CancelAndCloseAsync);
	}

	private async Task RunEventHandlerAsync(Func<Task> operation)
	{
		ArgumentNullException.ThrowIfNull(operation);

		try
		{
			await operation();
		}
		catch (Exception exception)
		{
			try
			{
				await CloseAfterUnexpectedFailureAsync(exception);
			}
			catch (Exception recoveryFailure)
			{
				Exception combinedFailure = new AggregateException(
					UiText.Get("Windows.Setup.AntigravityConnectionAndWindowCleanupBothFailed"),
					exception,
					recoveryFailure);
				RecordCompletionFailure(combinedFailure);
				_completionSource.TrySetResult(
					new AntigravitySetupDialogResult(
						_workflow.DialogOutcome,
						combinedFailure));
			}
		}
	}

	private async Task CloseAfterUnexpectedFailureAsync(Exception failure)
	{
		Exception completionFailure = failure;
		_isClosePending = true;
		CancelButton.IsEnabled = false;

		try
		{
			await _workflow.CancelAsync();
			await _workflow.DisposeAsync();
		}
		catch (Exception cleanupFailure)
		{
			completionFailure = new AggregateException(
				UiText.Get("Windows.Setup.AntigravityConnectionFailedAndCleanupDidNotFinish"),
				failure,
				cleanupFailure);
		}

		RecordCompletionFailure(completionFailure);
		_allowClose = true;
		Close();
	}
}
