using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal static class CopilotAccountIdentityRules
{
	private const int MaximumHostLength = 253;
	private const int MaximumNodeIdLength = 512;
	private const string Prefix = "github:copilot:v1:";

	internal static string Create(CopilotAccountIdentity identity)
	{
		ArgumentNullException.ThrowIfNull(identity);

		if (!TryNormalizeHost(identity.Host, out string host) ||
			!TryNormalizeNodeId(identity.NodeId, out string nodeId) ||
			(identity.DatabaseId <= 0) ||
			string.IsNullOrWhiteSpace(identity.Login))
		{
			throw new ArgumentException(
				"GitHub Copilot 帳號身分格式無效。",
				nameof(identity));
		}

		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(nodeId));
		string encodedDigest = Convert.ToBase64String(digest)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
		return $"{Prefix}{host}/{encodedDigest}";
	}

	internal static bool TryParse(
		string? value,
		out string? normalizedIdentity)
	{
		return TryParse(value, out normalizedIdentity, out _);
	}

	internal static bool TryParse(
		string? value,
		out string? normalizedIdentity,
		out string? normalizedHost)
	{
		normalizedIdentity = null;
		normalizedHost = null;

		if (string.IsNullOrWhiteSpace(value) ||
			!value.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		ReadOnlySpan<char> remainder = value.AsSpan(Prefix.Length);
		int separatorIndex = remainder.IndexOf('/');
		if ((separatorIndex <= 0) ||
			(separatorIndex == remainder.Length - 1) ||
			(remainder[(separatorIndex + 1)..].IndexOf('/') >= 0))
		{
			return false;
		}

		string host = remainder[..separatorIndex].ToString();
		string digest = remainder[(separatorIndex + 1)..].ToString();
		if (!TryNormalizeHost(host, out string canonicalHost) ||
			(digest.Length != 43) ||
			digest.Any(character =>
				!(char.IsAsciiLetterOrDigit(character) ||
					(character == '-') ||
					(character == '_'))))
		{
			return false;
		}

		normalizedIdentity = $"{Prefix}{canonicalHost}/{digest}";
		normalizedHost = canonicalHost;
		return true;
	}

	internal static bool AreEquivalent(string? left, string? right)
	{
		return TryParse(left, out string? normalizedLeft) &&
			TryParse(right, out string? normalizedRight) &&
			string.Equals(
				normalizedLeft,
				normalizedRight,
				StringComparison.Ordinal);
	}

	internal static bool TryNormalizeHost(
		string? value,
		out string normalizedHost)
	{
		normalizedHost = string.Empty;
		string? candidate = string.IsNullOrWhiteSpace(value)
			? null
			: value.Trim();
		if ((candidate is null) || (candidate.Length > 2048))
		{
			return false;
		}

		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
		{
			if (!Uri.TryCreate(
					$"https://{candidate}",
					UriKind.Absolute,
					out uri))
			{
				return false;
			}
		}

		if (!string.Equals(
				uri.Scheme,
				Uri.UriSchemeHttps,
				StringComparison.Ordinal) ||
			!string.IsNullOrEmpty(uri.UserInfo) ||
			!string.IsNullOrEmpty(uri.Query) ||
			!string.IsNullOrEmpty(uri.Fragment) ||
			(uri.AbsolutePath != "/"))
		{
			return false;
		}

		string host = uri.IdnHost.ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(host) ||
			(host.Length > MaximumHostLength))
		{
			return false;
		}

		normalizedHost = uri.IsDefaultPort
			? host
			: $"{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
		return true;
	}

	private static bool TryNormalizeNodeId(
		string? value,
		out string normalizedNodeId)
	{
		normalizedNodeId = string.Empty;
		string? candidate = string.IsNullOrWhiteSpace(value)
			? null
			: value.Trim();
		if ((candidate is null) ||
			(candidate.Length > MaximumNodeIdLength) ||
			candidate.Any(char.IsControl))
		{
			return false;
		}

		normalizedNodeId = candidate;
		return true;
	}
}

internal static class CopilotPlanTierRules
{
	private const int MaximumPlanTierLength = 64;

	internal static string? CreateDisplayText(string? value)
	{
		string? candidate = string.IsNullOrWhiteSpace(value)
			? null
			: value.Trim();
		if ((candidate is null) ||
			(candidate.Length > MaximumPlanTierLength) ||
			!candidate.Any(char.IsAsciiLetterOrDigit) ||
			candidate.Any(character =>
				!(char.IsAsciiLetterOrDigit(character) ||
					(character == '_') ||
					(character == '-') ||
					(character == ' ') ||
					(character == '+') ||
					(character == '.'))))
		{
			return null;
		}

		string[] words = candidate.Split(
			['_', '-', ' '],
			StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0)
		{
			return null;
		}

		string key = string.Join('_', words).ToLowerInvariant();
		return key switch
		{
			"github_copilot" => null,
			"free" or
			"copilot_free" or
			"individual_free" or
			"free_limited" or
			"free_limited_copilot" => "Free",
			"pro" or
			"copilot_pro" or
			"individual" => "Pro",
			"pro+" or
			"pro_plus" or
			"copilot_pro+" or
			"copilot_pro_plus" or
			"individual_pro" or
			"individual_pro+" or
			"individual_pro_plus" => "Pro+",
			"edu" or
			"student" or
			"copilot_student" or
			"individual_edu" => "Student",
			"max" or
			"copilot_max" or
			"individual_max" => "Max",
			"business" or "copilot_business" => "Business",
			"enterprise" or "copilot_enterprise" => "Enterprise",
			_ => string.Join(
				' ',
				words.Select(word =>
					CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
						word.ToLowerInvariant())))
		};
	}
}

internal sealed class CopilotUsageProvider : IUsageProvider
{
	private const int MaximumQuotaKeyLength = 80;
	private const string AuthenticationRequiredMessage =
		"Copilot 尚未登入。";
	private const string ConnectAccountMessage =
		"尚未連接 Copilot。";
	private const string InvalidQuotaMessage =
		"無法辨識 Copilot 用量資料。";
	private const string PermissionDeniedMessage =
		"目前登入無權讀取 Copilot 用量。";
	private const string QuotaUnavailableMessage =
		"這個 Copilot 帳號沒有可顯示的用量資料。";
	private const string RateLimitMessage =
		"GitHub 暫時限制用量查詢。";
	private const string RetryMessage =
		"暫時無法讀取 Copilot 用量。";
	private const string RuntimeUnavailableMessage =
		"本機 GitHub Copilot CLI 無法使用，請安裝或更新官方 CLI。";
	private const string SwitchAccountMessage =
		"Copilot credential 與卡片身分不同。";
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
	private readonly ICopilotQuotaClient _client;
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider => ProviderKind.Copilot;

	public TimeSpan MinimumRefreshInterval => RefreshInterval;

	internal CopilotUsageProvider(
		ICopilotQuotaClient client,
		TimeProvider? timeProvider = null)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Copilot)
		{
			throw new ArgumentException(
				"Copilot provider 只能讀取 Copilot 帳號。",
				nameof(account));
		}

		try
		{
			await _client.ReconcileCredentialAsync(
				account.Id,
				account.ProviderAccountIdentity,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return CreateFailureSnapshot(
				account,
				SnapshotStatus.Error,
				RetryMessage,
				UsageRecoveryAction.Retry,
				account.ProviderAccountIdentity);
		}

		if (!CopilotAccountIdentityRules.TryParse(
				account.ProviderAccountIdentity,
				out string? expectedIdentity))
		{
			return CreateFailureSnapshot(
				account,
				SnapshotStatus.NotConfigured,
				ConnectAccountMessage,
				UsageRecoveryAction.ConnectAccount);
		}

		try
		{
			CopilotUsageReport report = await _client.GetAccountQuotaAsync(
				account.Id,
				account.ProviderAccountIdentity,
				cancellationToken);
			if ((report is null) || (report.Account is null))
			{
				return CreateInvalidQuotaSnapshot(account);
			}

			string observedIdentity;
			try
			{
				observedIdentity = CopilotAccountIdentityRules.Create(
					report.Account);
			}
			catch (ArgumentException)
			{
				return CreateInvalidQuotaSnapshot(account);
			}

			if (!CopilotAccountIdentityRules.AreEquivalent(
					expectedIdentity,
					observedIdentity))
			{
				return CreateFailureSnapshot(
					account,
					SnapshotStatus.NotConfigured,
					SwitchAccountMessage,
					UsageRecoveryAction.SwitchAccount,
					account.ProviderAccountIdentity);
			}

			if (report.Quotas is null)
			{
				return CreateInvalidQuotaSnapshot(account);
			}

			if (report.Quotas.Count == 0)
			{
				return CreateQuotaUnavailableSnapshot(
					account,
					report,
					observedIdentity);
			}

			string? planTier = CopilotPlanTierRules.CreateDisplayText(
				report.PlanTier);
			bool isOrganizationPlan =
				planTier is "Business" or "Enterprise";
			if (!TryCreateMetrics(
					report.Quotas,
					report.IsTokenBasedBilling,
					isOrganizationPlan,
					out IReadOnlyList<UsageMetric>? metrics) ||
				(metrics is null))
			{
				return CreateInvalidQuotaSnapshot(account);
			}

			return new UsageSnapshot(
				account,
				metrics,
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				report.FetchedAt,
				report.FetchedAt,
				report.FetchedAt + StaleAfter,
				ProviderAccountIdentity: observedIdentity,
				ProviderAccountDisplayIdentity:
					$"@{report.Account.Login.Trim()}",
				SubscriptionScopeDisplayName:
					report.Account.Host.Trim(),
				PlanTier: planTier,
				SubscriptionVerificationState:
					SubscriptionVerificationState.Verified);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (CopilotClientException exception)
		{
			return exception.Kind switch
			{
				CopilotFailureKind.AuthenticationRequired =>
					CreateFailureSnapshot(
						account,
						SnapshotStatus.NotConfigured,
						AuthenticationRequiredMessage,
						UsageRecoveryAction.ConnectAccount,
						account.ProviderAccountIdentity),
				CopilotFailureKind.AccountMismatch =>
					CreateFailureSnapshot(
						account,
						SnapshotStatus.NotConfigured,
						SwitchAccountMessage,
						UsageRecoveryAction.SwitchAccount,
						account.ProviderAccountIdentity),
				CopilotFailureKind.PermissionDenied =>
					CreateFailureSnapshot(
						account,
						SnapshotStatus.Error,
						PermissionDeniedMessage,
						UsageRecoveryAction.SwitchAccount,
						account.ProviderAccountIdentity),
				CopilotFailureKind.RateLimited =>
					CreateFailureSnapshot(
						account,
						SnapshotStatus.Error,
						RateLimitMessage,
						UsageRecoveryAction.Retry,
						account.ProviderAccountIdentity,
						exception.RetryAt),
				CopilotFailureKind.RuntimeUnavailable =>
					CreateFailureSnapshot(
						account,
						SnapshotStatus.Error,
						RuntimeUnavailableMessage,
						UsageRecoveryAction.InstallOrUpdate,
						account.ProviderAccountIdentity),
				CopilotFailureKind.InvalidResponse =>
					CreateInvalidQuotaSnapshot(account),
				_ => CreateFailureSnapshot(
					account,
					SnapshotStatus.Error,
					RetryMessage,
					UsageRecoveryAction.Retry,
					account.ProviderAccountIdentity,
					exception.RetryAt)
			};
		}
		catch
		{
			return CreateFailureSnapshot(
				account,
				SnapshotStatus.Error,
				RetryMessage,
				UsageRecoveryAction.Retry,
				account.ProviderAccountIdentity);
		}
	}

	private static bool TryCreateMetrics(
		IReadOnlyList<CopilotQuotaSnapshot>? quotas,
		bool? isTokenBasedBilling,
		bool isOrganizationPlan,
		out IReadOnlyList<UsageMetric>? metrics)
	{
		metrics = null;
		if ((quotas is null) || (quotas.Count == 0))
		{
			return false;
		}

		List<(string QuotaKey, UsageMetric Metric)> candidates = [];
		HashSet<string> quotaKeys = new(StringComparer.Ordinal);
		HashSet<string> metricKeys = new(StringComparer.Ordinal);

		foreach (CopilotQuotaSnapshot? quota in quotas)
		{
			if ((quota is null) ||
				!TryNormalizeQuotaKey(quota.Key, out string? quotaKey) ||
				(quotaKey is null) ||
				!quotaKeys.Add(quotaKey) ||
				(quota.UsedRequests < 0) ||
				!double.IsFinite(quota.RemainingPercentage) ||
				(quota.RemainingPercentage < 0d) ||
				(quota.RemainingPercentage > 100d) ||
				(quota.IsUnlimitedEntitlement
					? quota.EntitlementRequests < -1
					: quota.EntitlementRequests < 0))
			{
				return false;
			}

			if ((isTokenBasedBilling == true) && (quotaKey == "chat"))
			{
				continue;
			}

			string metricKey =
				$"copilot-quota-{quotaKey.Replace('_', '-')}";
			if (!metricKeys.Add(metricKey))
			{
				return false;
			}

			double? usedPercent;
			string displayValue;
			if (quota.IsUnlimitedEntitlement)
			{
				usedPercent = null;
				string usedRequests = quota.UsedRequests.ToString(
					"N0",
					CultureInfo.CurrentCulture);
				if (isOrganizationPlan &&
					(isTokenBasedBilling == true) &&
					(quotaKey == "premium_interactions"))
				{
					displayValue =
						$"已使用 {usedRequests}；上限由共用額度與預算控制";
				}
				else if ((isTokenBasedBilling == true) &&
					(quotaKey == "premium_interactions"))
				{
					displayValue =
						$"已使用 {usedRequests}；此來源未提供上限";
				}
				else
				{
					bool hasUnreportedOrganizationLimit =
						isOrganizationPlan &&
						(quotaKey is "premium_interactions" or "chat");
					string limitText = hasUnreportedOrganizationLimit
						? "此來源未提供上限"
						: "無上限";
					displayValue =
						$"已使用 {usedRequests} 次（{limitText}）";
				}
			}
			else if (quota.EntitlementRequests == 0)
			{
				usedPercent = null;
				displayValue = quota.UsedRequests == 0
					? "此方案未提供"
					: $"{quota.UsedRequests.ToString("N0", CultureInfo.CurrentCulture)} 已使用（方案未包含額度）";
			}
			else
			{
				usedPercent = Math.Clamp(
					100d - quota.RemainingPercentage,
					0d,
					100d);
				displayValue =
					$"{quota.UsedRequests.ToString("N0", CultureInfo.CurrentCulture)} / " +
					$"{quota.EntitlementRequests.ToString("N0", CultureInfo.CurrentCulture)} 已使用";
			}

			if (quota.OverageAllowedWithExhaustedQuota)
			{
				displayValue += "，額外用量已開啟";
			}
			else if (quota.UsageAllowedWithExhaustedQuota)
			{
				displayValue += "，額度用完後仍可使用";
			}

			// 1.0.11 會把查詢時間誤報為 resetDate；修正前不顯示誤導時間。
			candidates.Add((
				quotaKey,
				new UsageMetric(
					metricKey,
					CreateQuotaLabel(quotaKey, isTokenBasedBilling),
					usedPercent,
					displayValue,
					ResetsAt: null)));
		}

		metrics = candidates
			.OrderBy(candidate => GetQuotaSortOrder(candidate.QuotaKey))
			.ThenBy(candidate => candidate.QuotaKey, StringComparer.Ordinal)
			.Select(candidate => candidate.Metric)
			.ToArray();
		return metrics.Count > 0;
	}

	private static bool TryNormalizeQuotaKey(
		string? value,
		out string? normalizedKey)
	{
		normalizedKey = null;
		string? candidate = string.IsNullOrWhiteSpace(value)
			? null
			: value.Trim().ToLowerInvariant();
		if ((candidate is null) ||
			(candidate.Length > MaximumQuotaKeyLength) ||
			candidate.Any(character =>
				!((character >= 'a' && character <= 'z') ||
					(character >= '0' && character <= '9') ||
					(character == '_') ||
					(character == '-'))))
		{
			return false;
		}

		normalizedKey = candidate;
		return true;
	}

	private static string CreateQuotaLabel(
		string quotaKey,
		bool? isTokenBasedBilling)
	{
		return quotaKey switch
		{
			"premium_interactions" => isTokenBasedBilling switch
			{
				true => "AI Credits",
				false => "Premium requests",
				null => "Premium usage"
			},
			"chat" => "Chat requests",
			"completions" => "Code completions",
			_ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
				quotaKey.Replace('_', ' ').Replace('-', ' '))
		};
	}

	private static int GetQuotaSortOrder(string quotaKey)
	{
		return quotaKey switch
		{
			"premium_interactions" => 0,
			"chat" => 1,
			"completions" => 2,
			_ => 3
		};
	}

	private UsageSnapshot CreateInvalidQuotaSnapshot(AccountProfile account)
	{
		return CreateFailureSnapshot(
			account,
			SnapshotStatus.Error,
			InvalidQuotaMessage,
			UsageRecoveryAction.UpdateApplication,
			account.ProviderAccountIdentity);
	}

	private static UsageSnapshot CreateQuotaUnavailableSnapshot(
		AccountProfile account,
		CopilotUsageReport report,
		string providerAccountIdentity)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Unsupported,
			report.FetchedAt,
			report.FetchedAt,
			Error: QuotaUnavailableMessage,
			ProviderAccountIdentity: providerAccountIdentity,
			ProviderAccountDisplayIdentity:
				$"@{report.Account.Login.Trim()}",
			SubscriptionScopeDisplayName: report.Account.Host.Trim(),
			PlanTier: CopilotPlanTierRules.CreateDisplayText(report.PlanTier),
			SubscriptionVerificationState:
				SubscriptionVerificationState.UsageUnavailable);
	}

	private UsageSnapshot CreateFailureSnapshot(
		AccountProfile account,
		SnapshotStatus status,
		string error,
		UsageRecoveryAction recoveryAction,
		string? providerAccountIdentity = null,
		DateTimeOffset? retryNotBefore = null)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			_timeProvider.GetUtcNow(),
			Error: error,
			ProviderAccountIdentity: providerAccountIdentity,
			RecoveryAction: recoveryAction,
			RetryNotBefore: retryNotBefore);
	}
}
