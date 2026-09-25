using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class CopilotPlanPresentationTests
{
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly string AccountIdentity =
		CopilotAccountIdentityRules.Create(new CopilotAccountIdentity(
			"github.com",
			"NODE_COPILOT_PLAN_TEST",
			1234,
			"octocat"));

	[Fact]
	public void VerifiedPlan_NormalizesInHeaderAndAccountDisplay()
	{
		AccountProfile profile = CreateProfile();
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(CreateReadySnapshot(profile, "individual_pro"));

		Assert.Equal("Pro+", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("GitHub Copilot · Pro+", viewModel.AccountHeaderText);
		Assert.Equal("@octocat · 方案 Pro+", viewModel.AccountDisplayText);
	}

	[Fact]
	public void LegacyPlaceholder_DoesNotAppearAsPlan()
	{
		AccountProfile profile = CreateProfile();
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(CreateReadySnapshot(profile, "GitHub Copilot"));

		Assert.Empty(viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("GitHub Copilot", viewModel.AccountHeaderText);
		Assert.Equal("@octocat", viewModel.AccountDisplayText);
	}

	[Theory]
	[InlineData(SourceTrust.PrivateExperimental, SubscriptionVerificationState.Verified)]
	[InlineData(SourceTrust.OfficialExperimental, SubscriptionVerificationState.TransientProbeError)]
	[InlineData(SourceTrust.OfficialExperimental, SubscriptionVerificationState.Unverified)]
	public void UnverifiedEvidence_DoesNotShowPlan(
		SourceTrust sourceTrust,
		SubscriptionVerificationState verificationState)
	{
		AccountProfile profile = CreateProfile();
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = CreateReadySnapshot(profile, "Enterprise") with
		{
			SourceTrust = sourceTrust,
			SubscriptionVerificationState = verificationState
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Empty(viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("GitHub Copilot", viewModel.AccountHeaderText);
		Assert.DoesNotContain("方案 ", viewModel.AccountDisplayText);
	}

	[Fact]
	public void VerifiedPlan_WithUnavailableQuota_RemainsVisible()
	{
		AccountProfile profile = CreateProfile();
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = CreateReadySnapshot(profile, "individual") with
		{
			Metrics = Array.Empty<UsageMetric>(),
			SourceTrust = SourceTrust.Unavailable,
			Status = SnapshotStatus.Unsupported,
			SubscriptionVerificationState =
				SubscriptionVerificationState.UsageUnavailable
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal("Pro", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("GitHub Copilot · Pro", viewModel.AccountHeaderText);
		Assert.Equal("@octocat · 方案 Pro", viewModel.AccountDisplayText);
	}

	private static AccountProfile CreateProfile()
	{
		return new AccountProfile(
			Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
			ProviderKind.Copilot,
			DisplayName: string.Empty,
			ProviderAccountIdentity: AccountIdentity);
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile profile,
		string planTier)
	{
		return new UsageSnapshot(
			profile,
			[
				new UsageMetric(
					"copilot-quota-premium-interactions",
					"Premium requests",
					25d,
					"25 / 100 已使用")
			],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			ObservedAt,
			ObservedAt,
			ProviderAccountIdentity: AccountIdentity,
			ProviderAccountDisplayIdentity: "@octocat",
			SubscriptionScopeDisplayName: "github.com",
			PlanTier: planTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
	}
}
