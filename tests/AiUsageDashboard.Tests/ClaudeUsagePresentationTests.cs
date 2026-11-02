using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

[LegacyChineseUiTest]
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
	public void ApplySnapshot_EmptyUltraSessionWithoutReset_HidesSessionAndPreservesSnapshot(
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
		}) with { Status = status, PlanTier = "ultra" };

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
	[InlineData("claude.rate_limit.five_hour", "team", SnapshotStatus.Ready)]
	[InlineData("claude.rate_limit.five_hour", "team", SnapshotStatus.Stale)]
	[InlineData("five_hour", "max", SnapshotStatus.Ready)]
	[InlineData("five_hour", "max", SnapshotStatus.Stale)]
	[InlineData("claude.five_hour", "pro", SnapshotStatus.Ready)]
	[InlineData("claude.five_hour", "pro", SnapshotStatus.Stale)]
	[InlineData("claude.rate_limit.five_hour", null, SnapshotStatus.Ready)]
	[InlineData("claude.rate_limit.five_hour", null, SnapshotStatus.Stale)]
	[InlineData("five_hour", "unknown", SnapshotStatus.Ready)]
	[InlineData("five_hour", "unknown", SnapshotStatus.Stale)]
	public void ApplySnapshot_EmptyNonUltraSessionWithoutReset_KeepsSessionAndPreservesSnapshot(
		string sessionKey,
		string? planTier,
		SnapshotStatus status)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric session = new(sessionKey, "5小時用量", 0, "已使用 0%");
		UsageMetric weekly = new(
			"seven_day", "週用量（所有模型）", 30, "已使用 30%",
			ObservedAt.AddDays(3));
		UsageSnapshot snapshot = CreateSnapshot(account, new[] { session, weekly }) with
		{
			Status = status,
			PlanTier = planTier
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
		Assert.Equal(new[] { session, weekly }, snapshot.Metrics);
		Assert.Equal(new[] { sessionKey, weekly.Key },
			viewModel.UsageMetrics.Select(metric => metric.Key));
		UsageMetricViewModel sessionViewModel = viewModel.UsageMetrics[0];
		Assert.Equal("5小時用量", sessionViewModel.Label);
		Assert.Equal("已使用 0%", sessionViewModel.DisplayValue);
		Assert.True(sessionViewModel.HasUsageBar);
		Assert.True(sessionViewModel.HasResetText);
		Assert.Equal("未提供重置時間", sessionViewModel.ResetText);
		Assert.Equal("已使用 0%", viewModel.PrimaryText);
		Assert.Equal(0, viewModel.UsedPercent);

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

		Assert.Equal(new[] { "剩餘 100%", "剩餘 70%" },
			viewModel.UsageMetrics.Select(metric => metric.DisplayValue));
		Assert.Equal("未提供重置時間", viewModel.UsageMetrics[0].ResetText);
		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
		Assert.Same(session, snapshot.Metrics[0]);
		Assert.Equal(0, session.UsedPercent);
		Assert.Null(session.ResetsAt);
		Assert.Null(session.ResetDisplayValue);

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Used);

		Assert.Equal(new[] { "已使用 0%", "已使用 30%" },
			viewModel.UsageMetrics.Select(metric => metric.DisplayValue));
	}

	[Fact]
	public void ApplySnapshot_HiddenClaudeSession_LocalizesVisibleSummaryInEnglish()
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		DateTimeOffset reset = ObservedAt.AddDays(3);
		UsageSnapshot snapshot = CreateSnapshot(account, new[]
		{
			new UsageMetric("five_hour", "5小時用量", 0, "已使用 0%"),
			new UsageMetric("seven_day", "週用量（所有模型）", 30, "已使用 30%", reset),
			new UsageMetric("fable", "週用量（Fable）", 0, "已使用 0%")
		}) with { PlanTier = "ultra" };

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal("Used 30%", viewModel.PrimaryText);
		Assert.Equal(
			$"Weekly usage · All models · Resets {reset.ToLocalTime():MM/dd HH:mm} · Weekly usage · Fable Used 0%",
			viewModel.SecondaryText);
		Assert.Equal(new[] { "seven_day", "fable" },
			viewModel.UsageMetrics.Select(metric => metric.Key));
		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
	}

	[Theory]
	[InlineData(0d, true, null, null)]
	[InlineData(12d, false, null, null)]
	[InlineData(null, false, null, null)]
	[InlineData(0d, false, "重置時間待確認", null)]
	[InlineData(0d, true, null, "ultra")]
	[InlineData(12d, false, null, "ultra")]
	[InlineData(null, false, null, "ultra")]
	[InlineData(0d, false, "重置時間待確認", "ultra")]
	public void ApplySnapshot_ClaudeSessionWithUsageOrResetEvidence_KeepsSession(
		double? usedPercent,
		bool hasResetTime,
		string? resetDisplayValue,
		string? planTier)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric session = new(
			"claude.rate_limit.five_hour", "5小時用量", usedPercent, "來源回傳用量",
			hasResetTime ? ObservedAt.AddHours(3) : null,
			resetDisplayValue);

		viewModel.ApplySnapshot(CreateSnapshot(account, new[] { session }) with
		{
			PlanTier = planTier
		});

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

	[Theory]
	[InlineData("ultra", SnapshotStatus.Ready, false)]
	[InlineData("ultra", SnapshotStatus.Stale, false)]
	[InlineData("team", SnapshotStatus.Ready, true)]
	[InlineData("team", SnapshotStatus.Stale, true)]
	[InlineData("max", SnapshotStatus.Ready, true)]
	[InlineData("pro", SnapshotStatus.Stale, true)]
	[InlineData(null, SnapshotStatus.Ready, true)]
	[InlineData("unknown", SnapshotStatus.Stale, true)]
	public void ApplySnapshot_OnlyEmptyClaudeSession_UsesPlanTierToKeepSession(
		string? planTier,
		SnapshotStatus status,
		bool keepsSession)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(CreateSnapshot(account, new[]
		{
			new UsageMetric("five_hour", "5小時用量", 0, "已使用 0%")
		}) with { PlanTier = planTier, Status = status });

		Assert.Equal(!keepsSession, viewModel.HasNoUsageMetrics);
		Assert.Equal(keepsSession ? "已使用 0%" : "已連接", viewModel.PrimaryText);

		if (keepsSession)
		{
			UsageMetricViewModel session = Assert.Single(viewModel.UsageMetrics);
			Assert.Equal("five_hour", session.Key);
			Assert.True(session.HasUsageBar);
			Assert.True(session.HasResetText);
			Assert.Equal("未提供重置時間", session.ResetText);

			viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

			Assert.Equal("剩餘 100%", Assert.Single(viewModel.UsageMetrics).DisplayValue);
		}
		else
		{
			Assert.Empty(viewModel.UsageMetrics);
			Assert.DoesNotContain("5小時", viewModel.SecondaryText, StringComparison.Ordinal);
		}

		Assert.Single(Assert.IsType<UsageSnapshot>(viewModel.CurrentSnapshot).Metrics);
	}

	[Theory]
	[InlineData("ultra", false)]
	[InlineData("team", true)]
	public void ApplySnapshot_ParsedUsageWithoutSessionReset_PreservesQuotasByPlanTier(
		string planTier,
		bool keepsSession)
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

		viewModel.ApplySnapshot(CreateSnapshot(account, result.Metrics) with
		{
			PlanTier = planTier
		});

		Assert.Equal(3, result.Metrics.Count);
		string[] expectedLabels = keepsSession
			? new[] { "5小時用量", "週用量 · 所有模型", "週用量 · Fable" }
			: new[] { "週用量 · 所有模型", "週用量 · Fable" };
		Assert.Equal(expectedLabels,
			viewModel.UsageMetrics.Select(metric => metric.Label));
	}

	[Theory]
	[InlineData("ultra", SnapshotStatus.Ready, false)]
	[InlineData("ultra", SnapshotStatus.Stale, false)]
	[InlineData("ULTRA", SnapshotStatus.Ready, false)]
	[InlineData("team", SnapshotStatus.Ready, true)]
	[InlineData("team", SnapshotStatus.Stale, true)]
	[InlineData("max", SnapshotStatus.Ready, true)]
	[InlineData("max", SnapshotStatus.Stale, true)]
	[InlineData("pro", SnapshotStatus.Ready, true)]
	[InlineData("pro", SnapshotStatus.Stale, true)]
	[InlineData(null, SnapshotStatus.Ready, true)]
	[InlineData(null, SnapshotStatus.Stale, true)]
	[InlineData("unknown", SnapshotStatus.Ready, true)]
	[InlineData("unknown", SnapshotStatus.Stale, true)]
	public void ApplySnapshot_ClaudeMissingSession_UsesPlanTierAndPreservesWeeklyUsage(
		string? planTier,
		SnapshotStatus status,
		bool showsUnavailableSession)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric weekly = new(
			"claude.rate_limit.seven_day.all_models", "週用量（所有模型）",
			30, "已使用 30%", ObservedAt.AddDays(3));
		UsageSnapshot snapshot = CreateSnapshot(account, new[] { weekly }) with
		{
			Status = status,
			PlanTier = planTier,
			FetchedAt = ObservedAt.AddHours(1)
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
		Assert.Same(weekly, Assert.Single(snapshot.Metrics));
		Assert.Equal(showsUnavailableSession ? 2 : 1, viewModel.UsageMetrics.Count);
		UsageMetricViewModel weeklyViewModel = Assert.Single(viewModel.UsageMetrics,
			metric => metric.Key == weekly.Key);
		Assert.Equal("已使用 30%", weeklyViewModel.DisplayValue);
		Assert.True(weeklyViewModel.HasUsageBar);

		if (showsUnavailableSession)
		{
			UsageMetricViewModel session = viewModel.UsageMetrics[0];
			Assert.Equal("claude.rate_limit.five_hour", session.Key);
			Assert.Equal("5小時用量", session.Label);
			Assert.Equal("未提供", session.DisplayValue);
			Assert.False(session.HasUsageBar);
			Assert.False(session.HasResetText);
			Assert.Equal("未提供", viewModel.PrimaryText);
		}
		else
		{
			Assert.Equal(weekly.Key, Assert.Single(viewModel.UsageMetrics).Key);
			Assert.Equal("已使用 30%", viewModel.PrimaryText);
		}

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

		Assert.Equal("剩餘 70%", Assert.Single(viewModel.UsageMetrics,
			metric => metric.Key == weekly.Key).DisplayValue);
		Assert.Equal(showsUnavailableSession ? "未提供" : "已使用 30%",
			viewModel.PrimaryText);
		Assert.Same(weekly, Assert.Single(snapshot.Metrics));
	}

	[Theory]
	[InlineData("team")]
	[InlineData("max")]
	[InlineData("pro")]
	[InlineData(null)]
	[InlineData("unknown")]
	public void GetVisibleMetrics_ClaudeMissingSession_DoesNotInventUsagePercentage(
		string? planTier)
	{
		UsageMetric weekly = new(
			"seven_day", "週用量", 30, "已使用 30%", ObservedAt.AddDays(3));
		UsageMetric[] reportedMetrics = { weekly };

		UsageMetric[] visibleMetrics = UsageMetricPresentation.GetVisibleMetrics(
			ProviderKind.Claude, reportedMetrics, planTier).ToArray();

		Assert.Equal(2, visibleMetrics.Length);
		Assert.Null(visibleMetrics[0].UsedPercent);
		Assert.Null(visibleMetrics[0].ResetsAt);
		Assert.Equal("未提供", visibleMetrics[0].DisplayValue);
		Assert.Same(weekly, visibleMetrics[1]);
		Assert.Same(weekly, Assert.Single(reportedMetrics));
	}

	[Theory]
	[InlineData("ultra", false)]
	[InlineData("ULTRA", false)]
	[InlineData("team", true)]
	[InlineData("max", true)]
	[InlineData("pro", true)]
	[InlineData(null, true)]
	[InlineData("unknown", true)]
	public void GetVisibleMetrics_ReportedEmptyClaudeSession_DoesNotAddPlaceholder(
		string? planTier,
		bool keepsSession)
	{
		UsageMetric weekly = new(
			"seven_day", "週用量", 30, "已使用 30%", ObservedAt.AddDays(3));
		UsageMetric[] reportedMetrics =
		{
			new("five_hour", "5小時用量", 0, "已使用 0%"),
			weekly
		};

		UsageMetric[] visibleMetrics = UsageMetricPresentation.GetVisibleMetrics(
			ProviderKind.Claude, reportedMetrics, planTier).ToArray();

		Assert.Equal(keepsSession ? 2 : 1, visibleMetrics.Length);
		Assert.Same(weekly, visibleMetrics[^1]);

		if (keepsSession)
		{
			Assert.Same(reportedMetrics[0], visibleMetrics[0]);
			Assert.Equal(0, visibleMetrics[0].UsedPercent);
			Assert.Equal("已使用 0%", visibleMetrics[0].DisplayValue);
		}

		Assert.Equal(2, reportedMetrics.Length);
	}

	[Theory]
	[InlineData("ultra", SnapshotStatus.Ready)]
	[InlineData("ultra", SnapshotStatus.Stale)]
	[InlineData("team", SnapshotStatus.Ready)]
	[InlineData("team", SnapshotStatus.Stale)]
	[InlineData("team", SnapshotStatus.NotConfigured)]
	[InlineData("team", SnapshotStatus.Error)]
	[InlineData("max", SnapshotStatus.Ready)]
	[InlineData("pro", SnapshotStatus.Stale)]
	[InlineData(null, SnapshotStatus.Ready)]
	[InlineData(null, SnapshotStatus.Unsupported)]
	[InlineData("unknown", SnapshotStatus.Stale)]
	public void ApplySnapshot_ClaudeWithoutMetrics_DoesNotAddSessionPlaceholder(
		string? planTier,
		SnapshotStatus status)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(CreateSnapshot(account, Array.Empty<UsageMetric>()) with
		{
			PlanTier = planTier,
			Status = status
		});

		Assert.True(viewModel.HasNoUsageMetrics);
		Assert.Empty(viewModel.UsageMetrics);
		Assert.DoesNotContain("5小時", viewModel.SecondaryText, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("quota", "ultra", SnapshotStatus.Ready, false)]
	[InlineData("usage", "ultra", SnapshotStatus.Stale, false)]
	[InlineData("quota", "team", SnapshotStatus.Ready, true)]
	[InlineData("usage", "team", SnapshotStatus.Stale, true)]
	[InlineData("quota", "max", SnapshotStatus.Ready, true)]
	[InlineData("usage", "max", SnapshotStatus.Stale, true)]
	[InlineData("quota", "pro", SnapshotStatus.Ready, true)]
	[InlineData("usage", "pro", SnapshotStatus.Stale, true)]
	[InlineData("quota", null, SnapshotStatus.Ready, true)]
	[InlineData("usage", null, SnapshotStatus.Stale, true)]
	[InlineData("quota", "unknown", SnapshotStatus.Ready, true)]
	[InlineData("usage", "unknown", SnapshotStatus.Stale, true)]
	public void ApplySnapshot_ClaudeWithOnlyGenericQuota_UsesPlanTierForSessionPlaceholder(
		string metricKey,
		string? planTier,
		SnapshotStatus status,
		bool showsUnavailableSession)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric quota = new(metricKey, "用量", 37, "已使用 37%");
		UsageSnapshot snapshot = CreateSnapshot(account, new[] { quota }) with
		{
			Status = status,
			PlanTier = planTier
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(showsUnavailableSession ? 2 : 1, viewModel.UsageMetrics.Count);
		UsageMetricViewModel quotaViewModel = Assert.Single(viewModel.UsageMetrics,
			metric => metric.Key == metricKey);
		Assert.Equal("已使用 37%", quotaViewModel.DisplayValue);
		Assert.True(quotaViewModel.HasUsageBar);
		Assert.Equal(showsUnavailableSession ? "未提供" : "已使用 37%",
			viewModel.PrimaryText);

		if (showsUnavailableSession)
		{
			UsageMetricViewModel session = viewModel.UsageMetrics[0];
			Assert.Equal("claude.rate_limit.five_hour", session.Key);
			Assert.Equal("未提供", session.DisplayValue);
			Assert.False(session.HasUsageBar);
			Assert.False(session.HasResetText);
		}

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

		Assert.Equal("剩餘 63%", Assert.Single(viewModel.UsageMetrics,
			metric => metric.Key == metricKey).DisplayValue);
		Assert.Equal(showsUnavailableSession ? "未提供" : "剩餘 63%",
			viewModel.UsageMetrics[0].DisplayValue);
		Assert.Equal(snapshot, viewModel.CurrentSnapshot);
		Assert.Same(quota, Assert.Single(snapshot.Metrics));
	}

	[Theory]
	[InlineData(ProviderKind.Codex)]
	[InlineData(ProviderKind.Copilot)]
	[InlineData(ProviderKind.Antigravity)]
	[InlineData(ProviderKind.Grok)]
	public void GetVisibleMetrics_MissingSessionFromAnotherProvider_DoesNotAddPlaceholder(
		ProviderKind provider)
	{
		UsageMetric[] reportedMetrics =
		{
			new("weekly", "週用量", 30, "已使用 30%", ObservedAt.AddDays(3))
		};

		IEnumerable<UsageMetric> visibleMetrics = UsageMetricPresentation.GetVisibleMetrics(
			provider, reportedMetrics, "max");

		Assert.Same(reportedMetrics, visibleMetrics);
		Assert.Same(reportedMetrics[0], Assert.Single(visibleMetrics));
	}

	[Theory]
	[InlineData("ultra", SnapshotStatus.Ready, false)]
	[InlineData("ultra", SnapshotStatus.Stale, false)]
	[InlineData("max", SnapshotStatus.Ready, true)]
	[InlineData("max", SnapshotStatus.Stale, true)]
	public void ApplySnapshot_MissingSession_LocalizesRowsAndSummaryAcrossLanguageChanges(
		string planTier,
		SnapshotStatus status,
		bool showsUnavailableSession)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		DateTimeOffset reset = ObservedAt.AddDays(3);
		UsageSnapshot snapshot = CreateSnapshot(account, new[]
		{
			new UsageMetric("seven_day", "週用量（所有模型）", 30, "已使用 30%", reset),
			new UsageMetric("fable", "週用量（Fable）", 47, "已使用 47%", reset)
		}) with { PlanTier = planTier, Status = status };
		viewModel.ApplySnapshot(snapshot);

		foreach (AppLanguage language in new[]
		{
			AppLanguage.English,
			AppLanguage.TraditionalChinese,
			AppLanguage.English
		})
		{
			using IDisposable languageScope = UiText.UseLanguage(language);
			viewModel.RefreshLocalizedPresentation();
			bool isEnglish = language == AppLanguage.English;
			Assert.Equal(showsUnavailableSession ? 3 : 2, viewModel.UsageMetrics.Count);
			if (showsUnavailableSession)
			{
				Assert.Equal(isEnglish ? "Not provided" : "未提供", viewModel.PrimaryText);
				Assert.Equal(isEnglish ? "Not provided" : "未提供", viewModel.UsageMetrics[0].DisplayValue);
				Assert.Equal(isEnglish ? "5-hour usage" : "5小時用量", viewModel.UsageMetrics[0].Label);
				Assert.Equal(isEnglish
					? "5-hour usage · Weekly usage · All models Used 30%"
					: "5小時用量 · 週用量（所有模型） 已使用 30%", viewModel.SecondaryText);
			}
			else
			{
				Assert.Equal(isEnglish ? "Used 30%" : "已使用 30%", viewModel.PrimaryText);
				Assert.Equal(isEnglish
					? $"Weekly usage · All models · Resets {reset.ToLocalTime():MM/dd HH:mm} · Weekly usage · Fable Used 47%"
					: $"週用量（所有模型） · 重置 {reset.ToLocalTime():MM/dd HH:mm} · 週用量（Fable） 已使用 47%",
					viewModel.SecondaryText);
			}
			Assert.Equal(isEnglish ? "Weekly usage · All models" : "週用量 · 所有模型",
				viewModel.UsageMetrics[showsUnavailableSession ? 1 : 0].Label);
			Assert.Equal(snapshot, viewModel.CurrentSnapshot);
			Assert.Equal(2, snapshot.Metrics.Count);
		}
	}

	[Theory]
	[InlineData(SnapshotStatus.Ready)]
	[InlineData(SnapshotStatus.Stale)]
	public void ApplySnapshot_EmptyTeamSession_LocalizesUsageAndMissingResetAcrossLanguageChanges(
		SnapshotStatus status)
	{
		AccountProfile account = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		UsageMetric session = new("five_hour", "5小時用量", 0, "已使用 0%");
		UsageMetric weekly = new(
			"seven_day", "週用量（所有模型）", 30, "已使用 30%", ObservedAt.AddDays(3));
		UsageSnapshot snapshot = CreateSnapshot(account, new[] { session, weekly }) with
		{
			PlanTier = "team",
			Status = status
		};
		viewModel.ApplySnapshot(snapshot);

		foreach (AppLanguage language in new[]
		{
			AppLanguage.English,
			AppLanguage.TraditionalChinese,
			AppLanguage.English
		})
		{
			using IDisposable languageScope = UiText.UseLanguage(language);
			viewModel.RefreshLocalizedPresentation();
			bool isEnglish = language == AppLanguage.English;
			Assert.Equal(2, viewModel.UsageMetrics.Count);
			UsageMetricViewModel sessionViewModel = viewModel.UsageMetrics[0];
			Assert.Equal(session.Key, sessionViewModel.Key);
			Assert.Equal(isEnglish ? "5-hour usage" : "5小時用量", sessionViewModel.Label);
			Assert.Equal(isEnglish ? "Used 0%" : "已使用 0%", sessionViewModel.DisplayValue);
			Assert.Equal(isEnglish ? "Used 0%" : "已使用 0%", viewModel.PrimaryText);
			Assert.True(sessionViewModel.HasUsageBar);
			Assert.True(sessionViewModel.HasResetText);
			Assert.Equal(isEnglish ? "Reset time not provided" : "未提供重置時間", sessionViewModel.ResetText);
			Assert.Equal(snapshot, viewModel.CurrentSnapshot);
			Assert.Same(session, snapshot.Metrics[0]);
			Assert.Equal(0, session.UsedPercent);
			Assert.Null(session.ResetsAt);
			Assert.Null(session.ResetDisplayValue);
		}
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
