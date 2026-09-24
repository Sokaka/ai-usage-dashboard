using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal sealed class CodexUsageProvider : IUsageProvider
{
	private const int MaximumAccountIdentityLength = 320;
	private const int MaximumDisplayValueLength = 512;
	private const int MaximumKeyLength = 200;
	private const int MaximumLabelLength = 200;
	private const int MaximumPlanTierLength = 200;
	private const int MaximumMetricCount = 64;
	private const int MaximumRateLimitBucketCount = 32;
	private const string ConnectAccountMessage =
		"尚未連接這張卡片的 Codex 帳號；請連接或重新連接。";
	private const string RetryMessage =
		"暫時無法確認 Codex 帳號與 workspace，稍後會自動再試。";
	private const string SwitchWorkspaceMessage =
		"Codex 目前的帳號或 workspace 與這張卡片的連接不同；請切換或重新連接。";
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
	private readonly ICodexUsagePoller _poller;
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider => ProviderKind.Codex;

	public TimeSpan MinimumRefreshInterval => RefreshInterval;

	public CodexUsageProvider(
		ICodexUsagePoller poller,
		TimeProvider? timeProvider = null)
	{
		_poller = poller ?? throw new ArgumentNullException(nameof(poller));
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Codex)
		{
			throw new ArgumentException(
				"Codex provider 只能讀取 Codex 帳號。",
				nameof(account));
		}

		DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
		CliVersionEvidence? versionEvidence = null;
		bool isWorkspaceBound =
			CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				account.ProviderAccountIdentity,
				out string? publicBindingIdentity) &&
			(publicBindingIdentity is not null);
		string? accountBindingIdentity = null;
		if (!isWorkspaceBound &&
			(!ProviderAccountIdentityRules.TryNormalize(
				account.ProviderAccountIdentity,
				out accountBindingIdentity) ||
			(accountBindingIdentity is null) ||
			Guid.TryParse(accountBindingIdentity, out _)))
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				ConnectAccountMessage,
				UsageRecoveryAction.ConnectAccount);
		}
		string providerBindingIdentity = isWorkspaceBound
			? publicBindingIdentity!
			: accountBindingIdentity!;
		SubscriptionVerificationState transientVerificationState =
			isWorkspaceBound
				? SubscriptionVerificationState.TransientProbeError
				: SubscriptionVerificationState.Unverified;

		try
		{
			CodexUsagePollResult pollResult = isWorkspaceBound
				? await _poller.PollBoundAsync(
					account.Id,
					publicBindingIdentity!,
					cancellationToken)
				: await _poller.PollAsync(account.Id, cancellationToken);
			versionEvidence = pollResult.VersionEvidence;

			if (isWorkspaceBound &&
				(!string.Equals(
					pollResult.PublicBindingIdentity,
					publicBindingIdentity,
					StringComparison.Ordinal) ||
				(pollResult.WorkspaceId == Guid.Empty)))
			{
				throw new CodexWorkspaceBindingInvalidException();
			}

			string? accountIdentity = NormalizeDisplayText(
				pollResult.AccountIdentity,
				MaximumAccountIdentityLength);
			string? planTier = NormalizeDisplayText(
				pollResult.PlanType,
				MaximumPlanTierLength);
			if ((accountIdentity is null) || (planTier is null))
			{
				throw new InvalidDataException(
					"Codex 帳號或方案格式無效。");
			}

			if (!isWorkspaceBound &&
				!ProviderAccountIdentityRules.Comparer.Equals(
					accountIdentity,
					accountBindingIdentity))
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					fetchedAt,
					"Codex 目前登入的帳號與這張卡片不同；請切換或重新連接。",
					UsageRecoveryAction.SwitchAccount,
					providerBindingIdentity,
					SubscriptionVerificationState.DefiniteScopeMismatch);
			}

			IReadOnlyList<UsageMetric> metrics = CreateMetrics(
				pollResult.RateLimits,
				pollResult.AvailableResetCredits,
				pollResult.NextResetCreditExpiresAt);
			EnsureValidMetricShape(metrics);

			if (!metrics.Any(metric => metric.UsedPercent is not null))
			{
				throw new InvalidDataException("Codex 未回傳可顯示的 rate-limit window。");
			}

			return new UsageSnapshot(
				account,
				metrics,
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				fetchedAt,
				pollResult.ObservedAt,
				pollResult.ObservedAt + StaleAfter,
				ProviderAccountIdentity: providerBindingIdentity,
				ProviderAccountDisplayIdentity: accountIdentity,
				PlanTier: planTier,
				SubscriptionVerificationState:
					isWorkspaceBound
						? SubscriptionVerificationState.Verified
						: SubscriptionVerificationState.Unverified,
				CodexResetCredits: pollResult.ResetCreditDetails);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (CodexCliNotFoundException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"找不到 Codex CLI；請安裝或修復 Codex 後重試。",
				UsageRecoveryAction.InstallOrUpdate,
				providerBindingIdentity,
				transientVerificationState);
		}
		catch (CodexCliContainmentException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				CodexCliContainmentException.RestartRequiredMessage,
				UsageRecoveryAction.RestartApplication,
				providerBindingIdentity,
				transientVerificationState);
		}
		catch (CodexCliProbeException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"暫時無法確認 Codex CLI 版本，稍後會自動再試。",
				UsageRecoveryAction.Retry,
				providerBindingIdentity,
				transientVerificationState);
		}
		catch (CodexCliUntrustedException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"Codex CLI 未通過官方簽章、路徑或版本驗證；請從 OpenAI 官方來源重新安裝或更新。",
				UsageRecoveryAction.InstallOrUpdate,
				providerBindingIdentity,
				transientVerificationState);
		}
		catch (CodexAppServerFailureException exception)
		{
			return CreateAppServerFailureSnapshot(
				account,
				fetchedAt,
				exception,
				providerBindingIdentity,
				transientVerificationState);
		}
		catch (CodexUsageAccountActionRequiredException exception)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				exception.Message,
				UsageRecoveryAction.ConnectAccount,
				providerBindingIdentity);
		}
		catch (CodexWorkspaceMismatchException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				SwitchWorkspaceMessage,
				UsageRecoveryAction.SwitchAccount,
				providerBindingIdentity,
				SubscriptionVerificationState.DefiniteScopeMismatch);
		}
		catch (CodexWorkspaceBindingMissingException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				ConnectAccountMessage,
				UsageRecoveryAction.ConnectAccount,
				providerBindingIdentity);
		}
		catch (CodexWorkspaceBindingInvalidException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"Codex workspace 連接資料已損壞；請重新連接這張帳號卡片。",
				UsageRecoveryAction.SwitchAccount,
				providerBindingIdentity);
		}
		catch (Exception exception) when (
			exception is CodexWorkspaceConfigurationException)
		{
			return CreateTransientSnapshot(
				account,
				fetchedAt,
				providerBindingIdentity,
				AppendVersionDiagnostic(
					exception,
					RetryMessage,
					versionEvidence));
		}
		catch (Exception exception) when (IsNotConfiguredFailure(exception))
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				exception is CodexUsageNotConfiguredException
					? exception.Message
					: "Codex 帳號尚未連接；請先完成此帳號的 ChatGPT 登入。",
				UsageRecoveryAction.ConnectAccount,
				providerBindingIdentity);
		}
		catch (Exception exception) when (IsPollingFailure(exception))
		{
			return CreateTransientSnapshot(
				account,
				fetchedAt,
				providerBindingIdentity,
				AppendVersionDiagnostic(
					exception,
					"暫時無法讀取 Codex 用量，稍後會自動再試。",
					versionEvidence),
				transientVerificationState);
		}
	}

	private static UsageSnapshot CreateAppServerFailureSnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		CodexAppServerFailureException exception,
		string providerBindingIdentity,
		SubscriptionVerificationState transientVerificationState)
	{
		return exception.Category switch
		{
			CodexAppServerFailureCategory.Authentication => CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"Codex 登入已失效；請重新連接此帳號。",
				UsageRecoveryAction.ConnectAccount,
				providerBindingIdentity),
			CodexAppServerFailureCategory.Compatibility => CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				exception.AppendVersionDiagnostic(
					"AI Usage 目前無法透過 Codex 讀取用量，請更新或修復 Codex。"),
				UsageRecoveryAction.InstallOrUpdate,
				providerBindingIdentity,
				transientVerificationState),
			CodexAppServerFailureCategory.Transient => CreateTransientSnapshot(
				account,
				fetchedAt,
				providerBindingIdentity,
				"暫時無法讀取 Codex 用量，稍後會自動再試。",
				transientVerificationState),
			_ => CreateTransientSnapshot(
				account,
				fetchedAt,
				providerBindingIdentity,
				exception.AppendVersionDiagnostic(
					"Codex 回傳無法辨識的錯誤；AI Usage 稍後會自動再試，不需要操作。"),
				transientVerificationState)
		};
	}

	private static string AppendVersionDiagnostic(
		Exception exception,
		string message,
		CliVersionEvidence? versionEvidence)
	{
		return versionEvidence is null
			? exception.AppendVersionDiagnostic(message)
			: versionEvidence.AppendFailureDiagnostic(message);
	}

	private static UsageSnapshot CreateEmptySnapshot(
		AccountProfile account,
		SnapshotStatus status,
		DateTimeOffset fetchedAt,
		string message,
		UsageRecoveryAction recoveryAction,
		string? providerAccountIdentity = null,
		SubscriptionVerificationState subscriptionVerificationState =
			SubscriptionVerificationState.Unverified)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			fetchedAt,
			Error: message,
			ProviderAccountIdentity: providerAccountIdentity,
			RecoveryAction: recoveryAction,
			SubscriptionVerificationState: subscriptionVerificationState);
	}

	private static UsageSnapshot CreateTransientSnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		string providerBindingIdentity,
		string message,
		SubscriptionVerificationState subscriptionVerificationState =
			SubscriptionVerificationState.TransientProbeError)
	{
		return CreateEmptySnapshot(
			account,
			SnapshotStatus.Error,
			fetchedAt,
			message,
			UsageRecoveryAction.Retry,
			providerBindingIdentity,
			subscriptionVerificationState);
	}

	private static string? NormalizeDisplayText(
		string? value,
		int maximumLength)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		string normalized = value.Trim();
		return (normalized.Length > maximumLength) ||
			normalized.Any(char.IsControl)
			? null
			: normalized;
	}

	private static IReadOnlyList<UsageMetric> CreateMetrics(
		IReadOnlyList<CodexRateLimitBucket> rateLimits,
		long? availableResetCredits,
		DateTimeOffset? nextResetCreditExpiresAt)
	{
		if ((rateLimits is null) ||
			(rateLimits.Count > MaximumRateLimitBucketCount) ||
			rateLimits.Any(bucket => bucket is null))
		{
			throw new InvalidDataException("Codex rate-limit bucket 數量或內容無效。");
		}

		List<CodexRateLimitBucket> orderedBuckets = rateLimits
			.OrderBy(bucket => string.Equals(
				bucket.LimitId,
				"codex",
				StringComparison.OrdinalIgnoreCase)
				? 0
				: 1)
			.ThenBy(bucket => bucket.LimitId, StringComparer.Ordinal)
			.ToList();
		List<UsageMetric> metrics = new();
		bool includeBucketName = orderedBuckets.Count > 1;

		foreach (CodexRateLimitBucket bucket in orderedBuckets)
		{
			AddMetric(metrics, bucket, bucket.Primary, "primary", "主要用量", includeBucketName);
			AddMetric(metrics, bucket, bucket.Secondary, "secondary", "次要用量", includeBucketName);
		}

		if (availableResetCredits is not null)
		{
			metrics.Add(new UsageMetric(
				"codex:rate_limit_reset_credits",
				"可用重置次數",
				null,
				$"{availableResetCredits.Value} 次",
				availableResetCredits.Value > 0
					? nextResetCreditExpiresAt
					: null));
		}

		return metrics;
	}

	private static string CreateWindowLabel(
		CodexRateLimitWindow window,
		string fallbackLabel)
	{
		if (window.WindowDurationMinutes is null)
		{
			return fallbackLabel;
		}

		long minutes = window.WindowDurationMinutes.Value;

		if ((minutes % (24 * 60)) == 0)
		{
			return $"{minutes / (24 * 60)} 天用量";
		}

		if ((minutes % 60) == 0)
		{
			return $"{minutes / 60} 小時用量";
		}

		return $"{minutes} 分鐘用量";
	}

	private static void AddMetric(
		ICollection<UsageMetric> metrics,
		CodexRateLimitBucket bucket,
		CodexRateLimitWindow? window,
		string windowKey,
		string fallbackLabel,
		bool includeBucketName)
	{
		if (window is null)
		{
			return;
		}

		string label = CreateWindowLabel(window, fallbackLabel);

		if (includeBucketName)
		{
			string bucketName = string.IsNullOrWhiteSpace(bucket.LimitName)
				? bucket.LimitId
				: bucket.LimitName;
			label = $"{bucketName} · {label}";
		}

		metrics.Add(new UsageMetric(
			$"codex:{bucket.LimitId}:{windowKey}",
			label,
			window.UsedPercent,
			$"已使用 {window.UsedPercent}%",
			window.ResetsAt));
	}

	private static void EnsureValidMetricShape(IReadOnlyList<UsageMetric> metrics)
	{
		if (metrics.Count > MaximumMetricCount)
		{
			throw new InvalidDataException(
				$"Codex 用量項目不得超過 {MaximumMetricCount} 個。");
		}

		HashSet<string> metricKeys = new(StringComparer.Ordinal);

		foreach (UsageMetric? metric in metrics)
		{
			if ((metric is null) ||
				!IsValidRequiredText(metric.Key, MaximumKeyLength) ||
				!IsValidRequiredText(metric.Label, MaximumLabelLength) ||
				!IsValidRequiredText(
					metric.DisplayValue,
					MaximumDisplayValueLength) ||
				((metric.ResetDisplayValue is not null) &&
					!IsValidRequiredText(
						metric.ResetDisplayValue,
						MaximumDisplayValueLength)) ||
				!metricKeys.Add(metric.Key) ||
				((metric.UsedPercent is not null) &&
					(!double.IsFinite(metric.UsedPercent.Value) ||
					(metric.UsedPercent.Value < 0) ||
					(metric.UsedPercent.Value > 100))) ||
				((metric.ResetsAt is not null) &&
					(metric.ResetsAt == default)))
			{
				throw new InvalidDataException("Codex 用量項目格式無效。");
			}
		}
	}

	private static bool IsValidRequiredText(string? value, int maximumLength)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= maximumLength) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
			!value.Any(char.IsControl);
	}

	private static bool IsNotConfiguredFailure(Exception exception)
	{
		return (exception is CodexUsageNotConfiguredException) ||
			(exception is DirectoryNotFoundException);
	}

	private static bool IsPollingFailure(Exception exception)
	{
		return (exception is DecoderFallbackException) ||
			(exception is InvalidDataException) ||
			(exception is IOException) ||
			(exception is JsonException) ||
			(exception is OperationCanceledException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception) ||
			(exception is NotSupportedException) ||
			(exception is CryptographicException) ||
			(exception is FormatException);
	}
}
