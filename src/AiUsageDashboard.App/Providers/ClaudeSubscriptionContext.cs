using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed record ClaudeProviderDefinedEntitlementKey(
	string Version,
	string AccountIdentity,
	string SubscriptionScopeIdentity);

internal sealed record ClaudeSubscriptionContext(
	string AccountIdentity,
	string SubscriptionScopeIdentity,
	ClaudeProviderDefinedEntitlementKey ProviderDefinedEntitlementKey,
	string PlanTier,
	string? SubscriptionScopeDisplayName,
	SubscriptionVerificationState VerificationState)
{
	internal const string EntitlementKeyVersion = "claude.account-org.v1";

	internal static ClaudeSubscriptionContext CreateVerified(
		string accountIdentity,
		string subscriptionScopeIdentity,
		string planTier,
		string? subscriptionScopeDisplayName)
	{
		string normalizedAccountIdentity = NormalizeRequired(
			accountIdentity,
			ProviderAccountIdentityRules.MaximumLength,
			nameof(accountIdentity),
			toLowerInvariant: true);
		string normalizedScopeIdentity = NormalizeRequired(
			subscriptionScopeIdentity,
			256,
			nameof(subscriptionScopeIdentity));
		string normalizedPlanTier = NormalizeRequired(
			planTier,
			64,
			nameof(planTier),
			toLowerInvariant: true);
		string? normalizedScopeDisplayName = NormalizeOptional(
			subscriptionScopeDisplayName,
			256,
			nameof(subscriptionScopeDisplayName));

		return new ClaudeSubscriptionContext(
			normalizedAccountIdentity,
			normalizedScopeIdentity,
			new ClaudeProviderDefinedEntitlementKey(
				EntitlementKeyVersion,
				normalizedAccountIdentity,
				normalizedScopeIdentity),
			normalizedPlanTier,
			normalizedScopeDisplayName,
			SubscriptionVerificationState.Verified);
	}

	private static string NormalizeRequired(
		string value,
		int maximumLength,
		string parameterName,
		bool toLowerInvariant = false)
	{
		string normalized = value?.Trim() ?? string.Empty;

		if ((normalized.Length == 0) ||
			(normalized.Length > maximumLength) ||
			normalized.Any(char.IsControl))
		{
			throw new ArgumentException(
				"Claude 訂閱範圍格式無效。",
				parameterName);
		}

		return toLowerInvariant ? normalized.ToLowerInvariant() : normalized;
	}

	private static string? NormalizeOptional(
		string? value,
		int maximumLength,
		string parameterName)
	{
		if (value is null)
		{
			return null;
		}

		string normalized = value.Trim();

		if ((normalized.Length == 0) ||
			(normalized.Length > maximumLength) ||
			normalized.Any(char.IsControl))
		{
			throw new ArgumentException(
				"Claude 訂閱範圍顯示名稱格式無效。",
				parameterName);
		}

		return normalized;
	}
}

internal sealed record ClaudeSubscriptionObservation(
	ClaudeSubscriptionContext Context,
	CliVersionEvidence VersionEvidence);

internal interface IClaudeSubscriptionContextProbe
{
	Task<ClaudeSubscriptionObservation> ProbeAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
