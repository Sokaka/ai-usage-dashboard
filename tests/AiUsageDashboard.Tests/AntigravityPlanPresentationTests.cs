using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityPlanPresentationTests
{
	[Fact]
	public void SubscriptionPlan_WithFreshAssociatedMetadata_DisplaysPlan()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Antigravity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false,
			planTier: "Pro"));

		Assert.Equal("Pro", viewModel.CurrentSnapshot?.PlanTier);
		Assert.Equal("Pro", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Antigravity · Pro", viewModel.AccountHeaderText);
		Assert.Equal(" · Pro", viewModel.AccountHeaderSuffixText);
		Assert.Contains(nameof(AccountUsageViewModel.AccountHeaderSuffixText), changedProperties);
	}

	[Fact]
	public void SubscriptionPlan_WithoutAssociatedEmail_HidesSnapshotPlan()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Antigravity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = CreateReadySnapshot(
			profile,
			DateTimeOffset.UtcNow) with
		{
			PlanTier = "Pro"
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(string.Empty, viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Antigravity", viewModel.AccountHeaderText);
		Assert.Equal(string.Empty, viewModel.AccountHeaderSuffixText);
	}

	[Fact]
	public void SubscriptionPlan_WithStaleAssociatedMetadata_HidesPlan()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Antigravity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));

		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: true,
			planTier: "Pro"));

		Assert.Equal("Pro", viewModel.CurrentSnapshot?.PlanTier);
		Assert.Equal(string.Empty, viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Antigravity", viewModel.AccountHeaderText);
		Assert.Equal(string.Empty, viewModel.AccountHeaderSuffixText);
	}

	[Theory]
	[InlineData(ProviderKind.Copilot)]
	[InlineData(ProviderKind.Grok)]
	public void SubscriptionPlan_ForUnsupportedProvider_HidesSyntheticPlan(
		ProviderKind provider)
	{
		AccountProfile profile = CreateProfile(provider);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = CreateReadySnapshot(
			profile,
			DateTimeOffset.UtcNow) with
		{
			ProviderAccountIdentity = "person@example.com",
			PlanTier = "Synthetic"
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal("Synthetic", viewModel.CurrentSnapshot?.PlanTier);
		Assert.Equal(string.Empty, viewModel.SubscriptionPlanDisplayText);
	}

	private static AccountProfile CreateProfile(ProviderKind provider)
	{
		string identity = provider == ProviderKind.Antigravity
			? AntigravityOfficialPrintUsageClient.LocalSessionIdentity
			: "person@example.com";
		return new(
			Guid.NewGuid(),
			provider,
			DisplayName: string.Empty,
			ProviderAccountIdentity: identity);
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile profile,
		DateTimeOffset observedAt)
	{
		return new(
			profile,
			new[]
			{
				new UsageMetric(
					"usage",
					"Usage",
					25d,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: profile.ProviderAccountIdentity);
	}
}
