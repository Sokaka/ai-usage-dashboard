using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeUsagePresentationTests
{
	private const string AccountIdentity = "claude@example.com";
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData("claude.rate_limit.five_hour", SnapshotStatus.Ready)]
	[InlineData("claude.rate_limit.five_hour", SnapshotStatus.Stale)]
	[InlineData("five_hour", SnapshotStatus.Ready)]
	[InlineData("five_hour", SnapshotStatus.Stale)]
	[InlineData("claude.five_hour", SnapshotStatus.Ready)]
	[InlineData("claude.five_hour", SnapshotStatus.Stale)]
	public void ApplySnapshot_EmptyClaudeSessionWithoutReset_HidesSessionAndPreservesSnapshot(
		string sessionKey,
		SnapshotStatus status)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageSnapshot snapshot = CreateSnapshot(account, new[]
		{
			new UsageMetric(sessionKey, "5小時用量", 0, "已使用 0%"),
			new UsageMetric(
				"seven_day", "週用量（所有模型）", 30, "已使用 30%",
				ObservedAt.AddDays(3)),
			new UsageMetric("fable", "週用量（Fable）", 0, "已使用 0%")
		}) with { Status = status };

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
		Assert.Equal(3, snapshot.Metrics.Count);
		Assert.True(viewModel.HasUsageMetrics);
		Assert.Equal(new[] { "seven_day", "fable" },
			viewModel.UsageMetrics.Select(metric => metric.Key));
		Assert.Equal("已使用 30%", viewModel.PrimaryText);
		Assert.DoesNotContain("5小時", viewModel.SecondaryText, StringComparison.Ordinal);
		Assert.Equal(30, viewModel.UsedPercent);
		Assert.Equal("未提供重置時間", viewModel.UsageMetrics[1].ResetText);

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

		Assert.Equal(new[] { "剩餘 70%", "剩餘 100%" },
			viewModel.UsageMetrics.Select(metric => metric.DisplayValue));
		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
	}

	[Theory]
	[InlineData(0d, true, null)]
	[InlineData(12d, false, null)]
	[InlineData(null, false, null)]
	[InlineData(0d, false, "重置時間待確認")]
	public void ApplySnapshot_ClaudeSessionWithUsageOrResetEvidence_KeepsSession(
		double? usedPercent,
		bool hasResetTime,
		string? resetDisplayValue)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric session = new(
			"claude.rate_limit.five_hour", "5小時用量", usedPercent, "來源回傳用量",
			hasResetTime ? ObservedAt.AddHours(3) : null,
			resetDisplayValue);

		viewModel.ApplySnapshot(CreateSnapshot(account, new[] { session }));

		Assert.Equal(session.Key, Assert.Single(viewModel.UsageMetrics).Key);
		Assert.Equal(session.DisplayValue, viewModel.PrimaryText);
	}

	[Theory]
	[InlineData(ProviderKind.Codex)]
	[InlineData(ProviderKind.Copilot)]
	[InlineData(ProviderKind.Grok)]
	public void ApplySnapshot_EmptySessionFromAnotherProvider_KeepsSession(
		ProviderKind provider)
	{
		AccountProfile account = CreateProfile(provider);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(CreateSnapshot(account, new[]
		{
			new UsageMetric("five_hour", "5小時用量", 0, "已使用 0%")
		}));

		Assert.Equal("five_hour", Assert.Single(viewModel.UsageMetrics).Key);
	}

	[Fact]
	public void ApplySnapshot_OnlyEmptyClaudeSession_DoesNotShowSessionInFallback()
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(CreateSnapshot(account, new[]
		{
			new UsageMetric("five_hour", "5小時用量", 0, "已使用 0%")
		}));

		Assert.True(viewModel.HasNoUsageMetrics);
		Assert.Equal("已連接", viewModel.PrimaryText);
		Assert.DoesNotContain("5小時", viewModel.SecondaryText, StringComparison.Ordinal);
		Assert.Single(Assert.IsType<UsageSnapshot>(viewModel.CurrentSnapshot).Metrics);
	}

	[Fact]
	public void ApplySnapshot_ParsedUsageWithoutSessionReset_ShowsWeeklyQuotas()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage
			Current session: 0% used
			Current week (all models): 30% used · resets Nov 3, 1am (UTC)
			Current week (Fable): 0% used
			""";
		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(output, ObservedAt);
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(CreateSnapshot(account, result.Metrics));

		Assert.Equal(3, result.Metrics.Count);
		Assert.Equal(new[] { "週用量 · 所有模型", "週用量 · Fable" },
			viewModel.UsageMetrics.Select(metric => metric.Label));
	}

	private static AccountProfile CreateProfile(ProviderKind provider)
	{
		return new AccountProfile(
			Guid.Parse("2ebed0d8-295f-4f14-90eb-fcab70be0daf"), provider, "測試帳號",
			ProviderAccountIdentity: AccountIdentity,
			HasAcceptedClaudeQuotaRisk: true);
	}

	private static UsageSnapshot CreateSnapshot(
		AccountProfile account,
		IReadOnlyList<UsageMetric> metrics)
	{
		return new UsageSnapshot(
			account, metrics, SourceTrust.OfficialExperimental, SnapshotStatus.Ready,
			ObservedAt, ObservedAt, ProviderAccountIdentity: AccountIdentity);
	}
}
