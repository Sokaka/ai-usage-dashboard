using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.App.Updates;

internal enum ManualUpdateCheckInvocationSurface
{
	Inline,
	Tray
}

internal sealed record ManualUpdateCheckFeedback(
	string Title,
	string Message,
	bool IsWarning);

internal static class ManualUpdateCheckFeedbackPolicy
{
	internal static ManualUpdateCheckFeedback? Create(
		ManualUpdateCheckInvocationSurface invocationSurface,
		bool isWindowVisible,
		bool isWindowCollapsed,
		UpdateCheckExecutionResult result)
	{
		ArgumentNullException.ThrowIfNull(result);

		if (invocationSurface != ManualUpdateCheckInvocationSurface.Tray)
		{
			return null;
		}

		bool canShowInlineFailure = isWindowVisible && !isWindowCollapsed;

		return result.Outcome switch
		{
			UpdateCheckExecutionOutcome.Completed when
				result.State.Status == UpdatePresentationStatus.UpToDate =>
				new ManualUpdateCheckFeedback(
					UiText.Translate("更新檢查完成"),
					UiText.Translate("目前已是最新版。"),
					IsWarning: false),
			UpdateCheckExecutionOutcome.Completed => null,
			UpdateCheckExecutionOutcome.Failed when !canShowInlineFailure =>
				CreateFailureFeedback(result.State),
			UpdateCheckExecutionOutcome.Failed => null,
			UpdateCheckExecutionOutcome.Cancelled => null,
			UpdateCheckExecutionOutcome.SkippedNotDue => null,
			UpdateCheckExecutionOutcome.SkippedDisabled => null,
			UpdateCheckExecutionOutcome.UnavailableInThisBuild => null,
			_ => throw new ArgumentOutOfRangeException(
				nameof(result),
				result.Outcome,
				UiText.Translate("未知的更新檢查結果。"))
		};
	}

	private static ManualUpdateCheckFeedback CreateFailureFeedback(
		UpdatePresentationState state)
	{
		string failureMessage = string.IsNullOrWhiteSpace(state.FailureMessage)
			? UiText.Translate("無法完成更新檢查，請稍後再試。")
			: UiText.Translate(state.FailureMessage.Trim());
		string knownUpdateGuidance =
			state.LastKnownResult?.IsUpdateAvailable == true
				? UiText.Translate("\n\n上次確認的更新操作仍可從 tray 使用。")
				: string.Empty;
		return new ManualUpdateCheckFeedback(
			UiText.Translate("無法檢查更新"),
			$"{failureMessage}{knownUpdateGuidance}",
			IsWarning: true);
	}
}
