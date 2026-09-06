using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeCliUntrustedException : InvalidOperationException
{
	public ClaudeCliUntrustedException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class ClaudeCliContainmentException : InvalidOperationException
{
	internal const string RestartRequiredMessage =
		"無法確認 Claude CLI 程序是否已完全結束；請重新啟動 AI Usage 後再試。";

	public ClaudeCliContainmentException(Exception? innerException = null)
		: base(RestartRequiredMessage, innerException)
	{
	}
}

internal sealed class ClaudeUsageNotConfiguredException : InvalidOperationException
{
	public string? AccountIdentity { get; }

	public bool IsAccountIdentityUnavailable { get; }

	public UsageRecoveryAction RecoveryAction { get; }

	public ClaudeUsageNotConfiguredException(
		string message,
		UsageRecoveryAction recoveryAction,
		string? accountIdentity = null,
		bool isAccountIdentityUnavailable = false)
		: base(message)
	{
		AccountIdentity = accountIdentity;
		IsAccountIdentityUnavailable = isAccountIdentityUnavailable;
		RecoveryAction = recoveryAction;
	}
}

internal sealed class ClaudeUsageSafetyException : InvalidOperationException
{
	public string? AccountIdentity { get; }

	public DateTimeOffset? AutomaticRetryNotBefore { get; }

	public bool CanRetryAutomatically => AutomaticRetryNotBefore is not null;

	public ClaudeSubscriptionContext? SubscriptionContext { get; }

	public ClaudeUsageSafetyException(
		string message,
		string? accountIdentity = null,
		DateTimeOffset? automaticRetryNotBefore = null,
		ClaudeSubscriptionContext? subscriptionContext = null)
		: base(message)
	{
		AccountIdentity = accountIdentity;
		AutomaticRetryNotBefore = automaticRetryNotBefore;
		SubscriptionContext = subscriptionContext;
	}

	public ClaudeUsageSafetyException(
		string message,
		Exception innerException,
		string? accountIdentity = null,
		DateTimeOffset? automaticRetryNotBefore = null,
		ClaudeSubscriptionContext? subscriptionContext = null)
		: base(message, innerException)
	{
		AccountIdentity = accountIdentity;
		AutomaticRetryNotBefore = automaticRetryNotBefore;
		SubscriptionContext = subscriptionContext;
	}
}

internal sealed class ClaudeSubscriptionBindingMissingException :
	InvalidOperationException
{
	internal ClaudeSubscriptionBindingMissingException()
		: base("Claude 訂閱範圍尚未連接。")
	{
	}
}

internal sealed class ClaudeSubscriptionScopeMismatchException :
	InvalidOperationException
{
	internal ClaudeSubscriptionScopeMismatchException()
		: base("Claude 訂閱範圍與帳號卡片的連接不同。")
	{
	}
}

internal sealed class ClaudeSubscriptionBindingInvalidException :
	InvalidOperationException
{
	internal ClaudeSubscriptionBindingInvalidException()
		: base("Claude 訂閱連接資料格式無效。")
	{
	}
}

internal sealed class ClaudeSubscriptionUsageUnavailableException :
	InvalidOperationException
{
	internal ClaudeSubscriptionContext SubscriptionContext { get; }

	internal CliVersionEvidence? VersionEvidence { get; }

	internal ClaudeSubscriptionUsageUnavailableException(
		ClaudeSubscriptionContext subscriptionContext,
		CliVersionEvidence? versionEvidence = null)
		: base("Claude 訂閱範圍已確認，但此方案的用量格式尚未驗證。")
	{
		SubscriptionContext = subscriptionContext ??
			throw new ArgumentNullException(nameof(subscriptionContext));
		VersionEvidence = versionEvidence;
	}
}

internal interface IClaudeUsagePoller
{
	Task<ClaudeUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task<ClaudeUsagePollResult> PollBoundAsync(
		Guid accountId,
		string expectedPublicBindingIdentity,
		CancellationToken cancellationToken = default);
}
