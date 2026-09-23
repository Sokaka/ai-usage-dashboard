using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.AntigravitySpike;
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
	private Exception? _completionFailure;
	private bool _allowClose;
	private bool _isClosePending;
	private bool _isWorkAreaRefreshQueued;
	private bool _shouldTransferOperationFocus = true;

	internal SetupWindow(
		IAntigravityMachineSetupService setupService,
		Guid setupAttemptId)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		InitializeComponent();
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
		LocationChanged -= SetupWindow_LocationChanged;
		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		base.OnClosed(e);
		_completionSource.TrySetResult(new AntigravitySetupDialogResult(
			_workflow.DialogOutcome,
			_completionFailure));
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
			return "即將";
		}

		if (duration.TotalDays >= 1)
		{
			return string.Format(
				CultureInfo.CurrentCulture,
				"{0} 天 {1} 小時後",
				(int)duration.TotalDays,
				duration.Hours);
		}

		if (duration.TotalHours >= 1)
		{
			return string.Format(
				CultureInfo.CurrentCulture,
				"{0} 小時 {1} 分後",
				(int)duration.TotalHours,
				duration.Minutes);
		}

		return string.Format(
			CultureInfo.CurrentCulture,
			"{0} 分後",
			Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes)));
	}

	internal static string GetProgressText(
		AntigravityMachineSetupStage stage)
	{
		return stage switch
		{
			AntigravityMachineSetupStage.LoadingReviewedContract =>
				"正在檢查支援的 Antigravity 版本…",
			AntigravityMachineSetupStage.DiscoveringExecutable =>
				"正在尋找這台電腦上的 Antigravity…",
			AntigravityMachineSetupStage.PreparingPrivateStorage =>
				"正在準備這台電腦…",
			AntigravityMachineSetupStage.CalibratingPrompt =>
				"正在確認 Antigravity 可以正常使用…",
			AntigravityMachineSetupStage.MaterializingProfile =>
				"正在準備本機連接設定…",
			AntigravityMachineSetupStage.ValidatingUsage =>
				"正在讀取 Antigravity 的四項用量…",
			AntigravityMachineSetupStage.ReadyForApproval =>
				"正在準備帳號確認畫面…",
			AntigravityMachineSetupStage.Approving =>
				"正在完成 Antigravity 連接…",
			_ => "正在連接 Antigravity…"
		};
	}

	internal static string CreateSafeDiagnosticInfo(
		string appVersion,
		AntigravityMachineSetupFailureKind failureKind,
		AntigravityMachineSetupStage? lastStage)
	{
		return string.Join(
			Environment.NewLine,
			"Antigravity 連接技術資訊",
			$"應用程式版本：{appVersion}",
			$"失敗類型：{GetFailureKindDisplayText(failureKind)}",
			$"最後階段：{GetStageDisplayText(lastStage)}",
			$"建議處理方式：{GetDiagnosticNextStep(failureKind, lastStage)}");
	}

	internal static string GetFailureKindDisplayText(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.None => "無（None）",
			AntigravityMachineSetupFailureKind.ConsentRequired =>
				"需要確認讀取說明（ConsentRequired）",
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				"找不到支援的 Antigravity CLI（UnsupportedBuild）",
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				"找到多個 Antigravity CLI（AmbiguousExecutable）",
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				"其他 Antigravity 視窗仍在執行（ExistingProcessDetected）",
			AntigravityMachineSetupFailureKind.PrivateStorageRejected =>
				"無法準備連接資料（PrivateStorageRejected）",
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				"Antigravity 設定不符（SettingsRejected）",
			AntigravityMachineSetupFailureKind.PromptRejected =>
				"Antigravity 尚未完成登入或設定（PromptRejected）",
			AntigravityMachineSetupFailureKind.UsageRejected =>
				"無法確認用量（UsageRejected）",
			AntigravityMachineSetupFailureKind.ApprovalRejected =>
				"無法儲存連接（ApprovalRejected）",
			AntigravityMachineSetupFailureKind.UnexpectedFailure =>
				"無法完成連接（UnexpectedFailure）",
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				"另一項用量檢查仍在進行（ExecutionBusy）",
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				"需要確認後再檢查用量（SafetyRevalidationRequired）",
			_ => $"無法判斷（{failureKind}）"
		};
	}

	internal static string GetStageDisplayText(
		AntigravityMachineSetupStage? stage)
	{
		return stage switch
		{
			null => "尚未開始",
			AntigravityMachineSetupStage.LoadingReviewedContract =>
				"載入支援資料（LoadingReviewedContract）",
			AntigravityMachineSetupStage.DiscoveringExecutable =>
				"尋找 Antigravity CLI（DiscoveringExecutable）",
			AntigravityMachineSetupStage.PreparingPrivateStorage =>
				"準備連接資料（PreparingPrivateStorage）",
			AntigravityMachineSetupStage.CalibratingPrompt =>
				"確認 Antigravity 可用（CalibratingPrompt）",
			AntigravityMachineSetupStage.MaterializingProfile =>
				"準備本機連接設定（MaterializingProfile）",
			AntigravityMachineSetupStage.ValidatingUsage =>
				"讀取 Antigravity 用量（ValidatingUsage）",
			AntigravityMachineSetupStage.ReadyForApproval =>
				"準備帳號確認（ReadyForApproval）",
			AntigravityMachineSetupStage.Approving =>
				"儲存 Antigravity 連接（Approving）",
			_ => $"無法判斷（{stage}）"
		};
	}

	internal static string GetDiagnosticNextStep(
		AntigravityMachineSetupFailureKind failureKind,
		AntigravityMachineSetupStage? lastStage)
	{
		return (failureKind, lastStage) switch
		{
			(AntigravityMachineSetupFailureKind.ExecutionBusy, _) =>
				"請稍候目前的 Antigravity 用量檢查完成後再試，不需要重新安裝 Antigravity 或重新登入。",
			(AntigravityMachineSetupFailureKind.SafetyRevalidationRequired, _) =>
				"請確認目前可以執行一次唯讀 /usage，再按「重新檢查 Antigravity 用量」；不需要重新登入。",
			(
				AntigravityMachineSetupFailureKind.UnsupportedBuild,
				AntigravityMachineSetupStage.DiscoveringExecutable) =>
				"請執行 agy --version。若無法執行或顯示的版本不受支援，請更新或重新安裝官方 Antigravity CLI；若更新後仍無法連接，請提供版本與技術資訊給維護者。",
			(
				AntigravityMachineSetupFailureKind.AmbiguousExecutable,
				AntigravityMachineSetupStage.DiscoveringExecutable) =>
				"請只保留要使用的一個 Antigravity CLI，移除其他安裝或 PATH 路徑後再試。",
			(
				AntigravityMachineSetupFailureKind.UsageRejected,
				AntigravityMachineSetupStage.ValidatingUsage) =>
				"請確認 Antigravity 已登入且能顯示用量；若仍失敗，請更新 AI Usage 或 Antigravity，並提供技術資訊給維護者。",
			(AntigravityMachineSetupFailureKind.ConsentRequired, _) =>
				"請回到 AI Usage，重新選擇連接並確認讀取說明。",
			(AntigravityMachineSetupFailureKind.ExistingProcessDetected, _) =>
				"請關閉其他 Antigravity 視窗後再試。",
			(AntigravityMachineSetupFailureKind.PrivateStorageRejected, _) =>
				"請再試一次；若仍失敗，請提供完整技術資訊給維護者。",
			(AntigravityMachineSetupFailureKind.SettingsRejected, _) =>
				"請確認 Antigravity 已登入且可正常使用；若仍失敗，請更新 AI Usage 或 Antigravity，並提供技術資訊給維護者。",
			(AntigravityMachineSetupFailureKind.PromptRejected, _) =>
				"請開啟 Antigravity，確認已登入且可正常使用後再試。",
			(AntigravityMachineSetupFailureKind.ApprovalRejected, _) =>
				"請再試一次；若仍失敗，請提供完整技術資訊給維護者。",
			_ =>
				"請再試一次；若仍失敗，請提供完整技術資訊給維護者。"
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

		return assemblyVersion?.ToString() ?? "未知";
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
			"agy.gemini.weekly" => "週用量 · Gemini",
			"agy.gemini.rolling-5h" => "5小時用量 · Gemini",
			"agy.claude.weekly" => "週用量 · Claude + GPT",
			"agy.claude.rolling-5h" => "5小時用量 · Claude + GPT",
			_ => "Antigravity 用量區間"
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
			? "目前登入的 Antigravity 帳號（未取得電子郵件）"
			: accountIdentity;
	}

	private static UsageWindowPresentation CreatePresentation(
		AntigravityProductionUsageWindow window)
	{
		string resetText = window.IsAvailable
			? "目前可用"
			: window.ResetsIn.HasValue
				? $"{FormatDuration(window.ResetsIn.Value)}重置"
				: "未提供重置時間";
		return new UsageWindowPresentation(
			GetUsageWindowDisplayName(window.StableWindowId),
			$"剩餘 {window.RemainingPercent:0.##}%",
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
		SetBusyView("正在完成 Antigravity 連接…");
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
		const string cancellationStatus =
			"正在取消並清理這次連接…";
		ProgressTextBlock.Text = cancellationStatus;
		FooterStatusTextBlock.Text = cancellationStatus;
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
		SetBusyView("正在重新檢查 Antigravity 用量…");
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
			FooterStatusTextBlock.Text = "技術資訊已複製";
		}
		catch (ExternalException)
		{
			FooterStatusTextBlock.Text =
				"目前無法存取剪貼簿，請稍後再試。";
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
		FailureDiagnosticDisclosureButton.Content = showDetails
			? "隱藏技術資訊"
			: "顯示技術資訊";
		QueueLiveRegionAnnouncement(FailureDiagnosticDisclosureButton);
	}

	private void SetBusyView(string status)
	{
		ProgressTextBlock.Text = status;
		FooterStatusTextBlock.Text = "處理中";
		CancelButton.Content = "取消";
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
		FooterStatusTextBlock.Text = "連接完成";
		CancelButton.Content = "關閉";
		CancelButton.IsEnabled = true;
		ShowOnly(CompletedPanel);
	}

	private void ShowFailure()
	{
		bool hasCommittedSetting = _workflow.HasCommittedSetting;
		FailureKindTextBlock.Text = GetFailureTitle(_workflow.FailureKind);
		FailureGuidanceTextBlock.Text = hasCommittedSetting
			? "連接設定已儲存，但收尾時發生問題，因此無法確認是否完成。請再試一次；若仍失敗，請提供技術資訊給維護者。"
			: GetFailureGuidance(_workflow.FailureKind);
		_failureDiagnosticInfo = CreateSafeDiagnosticInfo(
			AppVersion,
			_workflow.FailureKind,
			_lastStage);
		FailureDiagnosticTextBlock.Text = _failureDiagnosticInfo;
		FailureDiagnosticDetailsPanel.Visibility = Visibility.Collapsed;
		FailureDiagnosticDisclosureButton.Content = "顯示技術資訊";
		FooterStatusTextBlock.Text = hasCommittedSetting
			? "連接設定已儲存，但完成狀態待確認"
			: "Antigravity 連接未完成";
		CancelButton.Content = "關閉";
		CancelButton.IsEnabled = true;
		RetryButton.Content = GetFailureActionText(_workflow.FailureKind);
		ShowOnly(FailurePanel);
	}

	internal static string GetFailureActionText(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind ==
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired
			? "重新檢查 Antigravity 用量"
			: "再試一次";
	}

	internal static string GetFailureGuidance(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.ConsentRequired =>
				"請回到 AI Usage，重新選擇連接並確認讀取說明。",
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				"AI Usage 正在進行另一項 Antigravity 用量檢查。請稍候片刻再試，不需要重新安裝 Antigravity 或重新登入。",
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				"目前需要你確認後才能再次讀取 Antigravity 用量。AI Usage 不會自動再試，以免重複執行。請確認目前可以執行一次唯讀 /usage，再按「重新檢查 Antigravity 用量」；這不會重新登入 Antigravity。",
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				"找不到支援的 Antigravity CLI。請執行 agy --version。若無法執行或顯示的版本不受支援，請更新或重新安裝官方 Antigravity CLI；若更新後仍無法連接，請提供版本與技術資訊給維護者。",
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				"這台電腦上找到多個 Antigravity CLI。請只保留要使用的一個，移除其他安裝或 PATH 路徑後再試。",
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				"目前使用的 Antigravity 版本需要暫時關閉其他 Antigravity 視窗。請關閉後再試。",
			AntigravityMachineSetupFailureKind.PrivateStorageRejected =>
				"目前無法儲存 Antigravity 連接設定。請再試一次；若仍失敗，請提供技術資訊給維護者。",
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				"無法使用目前的 Antigravity 設定。請確認 Antigravity 已登入且可正常使用；若仍失敗，請更新 AI Usage 或 Antigravity。",
			AntigravityMachineSetupFailureKind.PromptRejected =>
				"無法確認 Antigravity 是否可用。請開啟 Antigravity，確認已登入且可正常使用後再試。",
			AntigravityMachineSetupFailureKind.UsageRejected =>
				"AI Usage 無法辨識 Antigravity 回傳的四項用量。請確認 Antigravity 已登入且能顯示用量；若仍失敗，請更新 AI Usage 或 Antigravity，並提供技術資訊給維護者。",
			AntigravityMachineSetupFailureKind.ApprovalRejected =>
				"完成連接時發生問題。請再試一次；若仍失敗，請提供技術資訊給維護者。",
			_ =>
				"Antigravity 連接未完成。請再試一次；若問題持續，請提供技術資訊給維護者。"
		};
	}

	private static string GetFailureTitle(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return failureKind switch
		{
			AntigravityMachineSetupFailureKind.ExecutionBusy =>
				"Antigravity 用量檢查仍在進行",
			AntigravityMachineSetupFailureKind.SafetyRevalidationRequired =>
				"需要確認後再檢查 Antigravity 用量",
			AntigravityMachineSetupFailureKind.UnsupportedBuild =>
				"找不到支援的 Antigravity CLI",
			AntigravityMachineSetupFailureKind.AmbiguousExecutable =>
				"找到多個 Antigravity 安裝",
			AntigravityMachineSetupFailureKind.ExistingProcessDetected =>
				"請先關閉其他 Antigravity 視窗",
			AntigravityMachineSetupFailureKind.SettingsRejected =>
				"無法使用目前的 Antigravity 設定",
			AntigravityMachineSetupFailureKind.PromptRejected =>
				"無法確認 Antigravity 狀態",
			AntigravityMachineSetupFailureKind.UsageRejected =>
				"無法確認 Antigravity 用量",
			_ => "Antigravity 連接未完成"
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
		AccountIdentityTextBlock.Text = GetAccountIdentityDisplayText(
			candidate.AccountIdentity);
		UsageWindowsItemsControl.ItemsSource =
			candidate.Windows.Select(CreatePresentation).ToArray();
		ShowOnly(ReviewPanel);
		FooterStatusTextBlock.Text = "請確認帳號";
		CancelButton.Content = "取消";
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

		SetBusyView("正在準備這台電腦…");
		Progress<AntigravityMachineSetupProgress> progress = new(
			value =>
			{
				_lastStage = value.Stage;
				ProgressTextBlock.Text = GetProgressText(value.Stage);
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
		FooterStatusTextBlock.Text = "連接完成";
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
					"Antigravity 連接與視窗收尾都失敗。",
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
				"Antigravity 連接失敗，且清理未完整完成。",
				failure,
				cleanupFailure);
		}

		RecordCompletionFailure(completionFailure);
		_allowClose = true;
		Close();
	}
}
