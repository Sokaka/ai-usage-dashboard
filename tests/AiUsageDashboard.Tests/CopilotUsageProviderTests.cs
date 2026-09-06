using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class CopilotUsageProviderTests
{
	private sealed class DelegateQuotaClient : ICopilotQuotaClient
	{
		private readonly Func<
			Guid,
			string?,
			CancellationToken,
			Task> _reconcileHandler;
		private readonly Func<
			Guid,
			string?,
			CancellationToken,
			Task<CopilotUsageReport>> _handler;

		internal DelegateQuotaClient(
			Func<
				Guid,
				string?,
				CancellationToken,
				Task<CopilotUsageReport>> handler,
			Func<
				Guid,
				string?,
				CancellationToken,
				Task>? reconcileHandler = null)
		{
			_handler = handler ?? throw new ArgumentNullException(nameof(handler));
			_reconcileHandler = reconcileHandler ??
				((_, _, _) => Task.CompletedTask);
		}

		public Task ReconcileCredentialAsync(
			Guid accountId,
			string? expectedProviderAccountIdentity,
			CancellationToken cancellationToken = default)
		{
			return _reconcileHandler(
				accountId,
				expectedProviderAccountIdentity,
				cancellationToken);
		}

		public Task<CopilotUsageReport> GetAccountQuotaAsync(
			Guid accountId,
			string? expectedProviderAccountIdentity,
			CancellationToken cancellationToken = default)
		{
			return _handler(
				accountId,
				expectedProviderAccountIdentity,
				cancellationToken);
		}
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _utcNow;

		internal FixedTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}
	}

	[Fact]
	public async Task GetUsageAsync_ForUnboundAccount_ReconcilesBeforeReturningNotConfigured()
	{
		Guid accountId = Guid.NewGuid();
		AccountProfile account = new(
			accountId,
			ProviderKind.Copilot,
			"Copilot");
		int reconcileCount = 0;
		DelegateQuotaClient client = new(
			(_, _, _) => throw new InvalidOperationException(
				"Quota must not be requested for an unbound account."),
			(actualAccountId, expectedIdentity, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(accountId, actualAccountId);
				Assert.Null(expectedIdentity);
				Interlocked.Increment(ref reconcileCount);
				return Task.CompletedTask;
			});
		CopilotUsageProvider provider = new(client);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(1, reconcileCount);
		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_MapsQuotaPlanAndHidesUnreliableResetDate()
	{
		DateTimeOffset fetchedAt = new(
			2026,
			9,
			1,
			12,
			0,
			0,
			TimeSpan.Zero);
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_PRIMARY",
			"octocat",
			1001);
		AccountProfile account = CreateConnectedProfile(principal);
		DelegateQuotaClient client = new((accountId, expectedIdentity, token) =>
		{
			token.ThrowIfCancellationRequested();
			Assert.Equal(account.Id, accountId);
			Assert.Equal(account.ProviderAccountIdentity, expectedIdentity);
			return Task.FromResult(new CopilotUsageReport(
				principal,
				new[]
				{
					new CopilotQuotaSnapshot(
						"completions",
						0,
						0,
						100,
						fetchedAt.AddDays(1),
						IsUnlimitedEntitlement: false,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"chat",
						-1,
						42,
						100,
						fetchedAt.AddDays(1),
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"premium_interactions",
						100,
						25,
						75,
						fetchedAt.AddDays(1),
						IsUnlimitedEntitlement: false,
						OverageAllowedWithExhaustedQuota: true,
						UsageAllowedWithExhaustedQuota: false)
				},
				fetchedAt,
				"copilot_pro_plus",
				IsTokenBasedBilling: false));
		});
		CopilotUsageProvider provider = new(client);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal("Pro+", snapshot.PlanTier);
		Assert.Equal("@octocat", snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal("github.com", snapshot.SubscriptionScopeDisplayName);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(fetchedAt.AddMinutes(30), snapshot.StaleAfter);
		Assert.Equal(
			new[]
			{
				"copilot-quota-premium-interactions",
				"copilot-quota-chat",
				"copilot-quota-completions"
			},
			snapshot.Metrics.Select(metric => metric.Key));

		UsageMetric premium = snapshot.Metrics[0];
		Assert.Equal("Premium requests", premium.Label);
		Assert.Equal(25d, premium.UsedPercent);
		Assert.Contains("25 / 100", premium.DisplayValue, StringComparison.Ordinal);
		Assert.Contains("額外用量已開啟", premium.DisplayValue, StringComparison.Ordinal);
		Assert.Null(premium.ResetsAt);

		UsageMetric chat = snapshot.Metrics[1];
		Assert.Null(chat.UsedPercent);
		Assert.Equal("已使用 42 次（無上限）", chat.DisplayValue);
		Assert.Null(chat.ResetsAt);

		UsageMetric completions = snapshot.Metrics[2];
		Assert.Null(completions.UsedPercent);
		Assert.Equal("此方案未提供", completions.DisplayValue);
		Assert.Null(completions.ResetsAt);
	}

	[Fact]
	public async Task GetUsageAsync_ForAiCreditsPlan_UsesCreditsAndHidesChatBucket()
	{
		DateTimeOffset fetchedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_AI_CREDITS",
			"credits-user",
			1006);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(new DelegateQuotaClient((_, _, _) =>
			Task.FromResult(new CopilotUsageReport(
				principal,
				new[]
				{
					new CopilotQuotaSnapshot(
						"premium_interactions",
						1500,
						250,
						83.33,
						null,
						IsUnlimitedEntitlement: false,
						OverageAllowedWithExhaustedQuota: true,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"chat",
						-1,
						42,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"completions",
						-1,
						9,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false)
				},
				fetchedAt,
				"copilot_pro",
				IsTokenBasedBilling: true))));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(
			new[]
			{
				"copilot-quota-premium-interactions",
				"copilot-quota-completions"
			},
			snapshot.Metrics.Select(metric => metric.Key));
		UsageMetric credits = snapshot.Metrics[0];
		Assert.Equal("AI Credits", credits.Label);
		Assert.Equal(16.67d, credits.UsedPercent);
		Assert.Equal(
			"250 / 1,500 已使用，額外用量已開啟",
			credits.DisplayValue);
		Assert.Equal("已使用 9 次（無上限）", snapshot.Metrics[1].DisplayValue);
	}

	[Theory]
	[InlineData("business", "Business")]
	[InlineData("copilot_business", "Business")]
	[InlineData("enterprise", "Enterprise")]
	[InlineData("copilot_enterprise", "Enterprise")]
	public async Task GetUsageAsync_ForOrganizationAiCreditsPlan_ProjectsSharedLimit(
		string planTier,
		string expectedPlanTier)
	{
		DateTimeOffset fetchedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_ORGANIZATION",
			"organization-user",
			1005);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(new DelegateQuotaClient((_, _, _) =>
			Task.FromResult(new CopilotUsageReport(
				principal,
				new[]
				{
					new CopilotQuotaSnapshot(
						"premium_interactions",
						0,
						7,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"chat",
						0,
						8,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false),
					new CopilotQuotaSnapshot(
						"completions",
						-1,
						9,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false)
				},
				fetchedAt,
				planTier,
				IsTokenBasedBilling: true))));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(expectedPlanTier, snapshot.PlanTier);
		Assert.Equal(
			new[]
			{
				"copilot-quota-premium-interactions",
				"copilot-quota-completions"
			},
			snapshot.Metrics.Select(metric => metric.Key));
		UsageMetric credits = snapshot.Metrics[0];
		Assert.Equal("AI Credits", credits.Label);
		Assert.Equal(
			"已使用 7；上限由共用額度與預算控制",
			credits.DisplayValue);
		Assert.Equal(
			"已使用 9 次（無上限）",
			snapshot.Metrics.Single(metric =>
				metric.Key == "copilot-quota-completions").DisplayValue);
	}

	[Fact]
	public async Task GetUsageAsync_ForUnknownAiCreditsPlan_DoesNotClaimUnlimited()
	{
		DateTimeOffset fetchedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_UNKNOWN_AI_CREDITS",
			"unknown-credits-user",
			1007);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(new DelegateQuotaClient((_, _, _) =>
			Task.FromResult(new CopilotUsageReport(
				principal,
				new[]
				{
					new CopilotQuotaSnapshot(
						"premium_interactions",
						-1,
						3,
						100,
						null,
						IsUnlimitedEntitlement: true,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false)
				},
				fetchedAt,
				PlanTier: null,
				IsTokenBasedBilling: true))));

		UsageMetric metric = Assert.Single((await provider.GetUsageAsync(
			account,
			CancellationToken.None)).Metrics);

		Assert.Equal("AI Credits", metric.Label);
		Assert.Equal("已使用 3；此來源未提供上限", metric.DisplayValue);
		Assert.DoesNotContain("無上限", metric.DisplayValue, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetUsageAsync_ForGenericPlan_HidesPlanAndResetDate()
	{
		DateTimeOffset fetchedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_GENERIC_PLAN",
			"generic-user",
			1002);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(new DelegateQuotaClient((_, _, _) =>
			Task.FromResult(new CopilotUsageReport(
				principal,
				new[]
				{
					new CopilotQuotaSnapshot(
						"premium_interactions",
						50,
						5,
						90,
						fetchedAt.AddHours(2),
						IsUnlimitedEntitlement: false,
						OverageAllowedWithExhaustedQuota: false,
						UsageAllowedWithExhaustedQuota: false)
				},
				fetchedAt,
				"github_copilot"))));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Null(snapshot.PlanTier);
		UsageMetric metric = Assert.Single(snapshot.Metrics);
		Assert.Equal("Premium usage", metric.Label);
		Assert.Null(metric.ResetsAt);
	}

	[Fact]
	public async Task GetUsageAsync_ForEmptyQuota_ReturnsUsageUnavailableWithVerifiedPrincipal()
	{
		DateTimeOffset fetchedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_EMPTY",
			"empty-user",
			1003);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(new DelegateQuotaClient((_, _, _) =>
			Task.FromResult(new CopilotUsageReport(
				principal,
				Array.Empty<CopilotQuotaSnapshot>(),
				fetchedAt,
				"business"))));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Unsupported, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal("Business", snapshot.PlanTier);
		Assert.Equal(account.ProviderAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal("@empty-user", snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			snapshot.SubscriptionVerificationState);
		Assert.Contains("沒有可顯示", snapshot.Error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		(int)CopilotFailureKind.RateLimited,
		SnapshotStatus.Error,
		UsageRecoveryAction.Retry)]
	[InlineData(
		(int)CopilotFailureKind.PermissionDenied,
		SnapshotStatus.Error,
		UsageRecoveryAction.SwitchAccount)]
	[InlineData(
		(int)CopilotFailureKind.InvalidResponse,
		SnapshotStatus.Error,
		UsageRecoveryAction.UpdateApplication)]
	public async Task GetUsageAsync_ForTypedClientFailure_MapsSafeRecovery(
		int failureKindValue,
		SnapshotStatus expectedStatus,
		UsageRecoveryAction expectedRecoveryAction)
	{
		CopilotFailureKind failureKind =
			(CopilotFailureKind)failureKindValue;
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		DateTimeOffset retryAt = now.AddMinutes(2);
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_FAILURE",
			"failure-user",
			1004);
		AccountProfile account = CreateConnectedProfile(principal);
		CopilotUsageProvider provider = new(
			new DelegateQuotaClient((_, _, _) =>
				Task.FromException<CopilotUsageReport>(new CopilotClientException(
					failureKind,
					"synthetic private detail",
					retryAt))),
			new FixedTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(expectedStatus, snapshot.Status);
		Assert.Equal(expectedRecoveryAction, snapshot.RecoveryAction);
		Assert.Equal(account.ProviderAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.DoesNotContain(
			"synthetic private detail",
			snapshot.Error,
			StringComparison.Ordinal);
		Assert.Equal(
			failureKind == CopilotFailureKind.RateLimited ? retryAt : null,
			snapshot.RetryNotBefore);
	}

	private static AccountProfile CreateConnectedProfile(
		CopilotAccountIdentity principal)
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(principal));
	}

	private static CopilotAccountIdentity CreatePrincipal(
		string nodeId,
		string login,
		long databaseId)
	{
		return new CopilotAccountIdentity(
			"github.com",
			nodeId,
			databaseId,
			login);
	}
}
