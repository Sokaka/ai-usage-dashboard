using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class UsageMetricViewModelTests
{
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
	public void Constructor_WithPercentageAndResetTime_ProjectsUsageRow()
	{
		DateTimeOffset resetsAt = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
		UsageMetric metric = new(
			"claude.rate_limit.seven_day.all_models",
			"本週（所有模型）",
			84,
			"已使用 84%",
			resetsAt);

		UsageMetricViewModel viewModel = new(metric);

		Assert.Equal(metric.Key, viewModel.Key);
		Assert.Equal("週用量 · 所有模型", viewModel.Label);
		Assert.Equal("已使用 84%", viewModel.DisplayValue);
		Assert.Equal(84, viewModel.DisplayPercent);
		Assert.Equal(84, viewModel.UsedPercent);
		Assert.Equal(UsageLevel.Warning, viewModel.Level);
		Assert.Equal("週用量 · 所有模型，已使用", viewModel.ProgressAutomationName);
		Assert.True(viewModel.HasUsageBar);
		Assert.True(viewModel.HasResetText);
		Assert.Equal(
			$"重置 · {resetsAt.ToLocalTime():MM/dd HH:mm}",
			viewModel.ResetText);
	}

	[Fact]
	public void Constructor_WithRemainingMode_ProjectsRemainingValueAndKeepsRiskLevel()
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"claude.rate_limit.seven_day.all_models",
				"本週（所有模型）",
				84,
				"已使用 84%"),
			UsageDisplayMode.Remaining);

		Assert.Equal(84, viewModel.UsedPercent);
		Assert.Equal(16, viewModel.DisplayPercent);
		Assert.Equal("剩餘 16%", viewModel.DisplayValue);
		Assert.Equal(UsageLevel.Warning, viewModel.Level);
		Assert.Equal("週用量 · 所有模型，剩餘", viewModel.ProgressAutomationName);
	}

	[Theory]
	[InlineData(
		UsageDisplayMode.Used,
		"75 / 300 已使用，額外用量已開啟",
		"已使用 25%")]
	[InlineData(
		UsageDisplayMode.Remaining,
		"75 / 300 已使用，額外用量已開啟",
		"剩餘 75%")]
	[InlineData(
		UsageDisplayMode.Used,
		"75 / 300 已使用，額度用完後仍可使用",
		"已使用 25%")]
	[InlineData(
		UsageDisplayMode.Remaining,
		"75 / 300 已使用，額度用完後仍可使用",
		"剩餘 75%")]
	public void Constructor_WithPercentageDisplayValue_HidesKnownUsageStatus(
		UsageDisplayMode usageDisplayMode,
		string providerDisplayValue,
		string expectedDisplayValue)
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-premium-interactions",
				"Premium requests",
				25,
				providerDisplayValue),
			usageDisplayMode,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal(expectedDisplayValue, viewModel.DisplayValue);
		Assert.Equal("75 / 300 已使用", viewModel.ToolTipValue);
	}

	[Theory]
	[InlineData(UsageDisplayMode.Used, "0 / 1,500 已使用")]
	[InlineData(UsageDisplayMode.Remaining, "剩餘 100%")]
	public void Constructor_WithAiCredits_PreservesAbsoluteUsedValue(
		UsageDisplayMode usageDisplayMode,
		string expectedDisplayValue)
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-premium-interactions",
				"AI Credits",
				0,
				"0 / 1,500 已使用，額外用量已開啟"),
			usageDisplayMode,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal(expectedDisplayValue, viewModel.DisplayValue);
		Assert.Equal("0 / 1,500 已使用", viewModel.ToolTipValue);
	}

	[Theory]
	[InlineData(
		"3 已使用（方案未包含額度），額外用量已開啟",
		"3 已使用（方案未包含額度）")]
	[InlineData(
		"此方案未提供，額度用完後仍可使用",
		"此方案未提供")]
	public void Constructor_WithoutProgressBar_HidesKnownUsageStatus(
		string providerDisplayValue,
		string expectedDisplayValue)
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-premium-interactions",
				"Premium requests",
				null,
				providerDisplayValue),
			UsageDisplayMode.Remaining,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.False(viewModel.HasUsageBar);
		Assert.Equal(expectedDisplayValue, viewModel.DisplayValue);
		Assert.Equal(expectedDisplayValue, viewModel.ToolTipValue);
	}

	[Fact]
	public void Constructor_WithPercentageDisplayValue_PreservesPrimaryDetail()
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-premium-interactions",
				"Premium requests",
				25,
				"75 / 300 已使用"),
			UsageDisplayMode.Used,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal("已使用 25%", viewModel.DisplayValue);
		Assert.Equal("75 / 300 已使用", viewModel.ToolTipValue);
	}

	[Fact]
	public void Constructor_WithUnlimitedQuotaAndStatusSuffix_HidesStatus()
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-chat",
				"Chat requests",
				null,
				"已使用 123 次（無上限），額外用量已開啟"),
			UsageDisplayMode.Used,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal("已使用 123 次（無上限）", viewModel.DisplayValue);
		Assert.Equal("已使用 123 次（無上限）", viewModel.ToolTipValue);
	}

	[Fact]
	public void Constructor_WithPercentageDisplayValueAndUnlimitedQuota_PreservesProviderValue()
	{
		const string ProviderDisplayValue = "已使用 123 次（無上限）";
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"copilot-quota-chat",
				"Chat requests",
				null,
				ProviderDisplayValue),
			UsageDisplayMode.Remaining,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal(ProviderDisplayValue, viewModel.DisplayValue);
		Assert.Equal(ProviderDisplayValue, viewModel.ToolTipValue);
		Assert.False(viewModel.HasUsageBar);
	}

	[Theory]
	[InlineData("copilot-quota-premium-interactions")]
	[InlineData("copilot-quota-chat")]
	public void Constructor_WithUnreportedOrganizationQuotaLimit_ExplainsTooltip(
		string metricKey)
	{
		const string ProviderDisplayValue =
			"已使用 123 次（此來源未提供上限）";
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				metricKey,
				"Copilot quota",
				null,
				ProviderDisplayValue),
			UsageDisplayMode.Used,
			showResetText: false,
			usePercentageDisplayValue: true);

		Assert.Equal(ProviderDisplayValue, viewModel.DisplayValue);
		Assert.Equal(
			$"{ProviderDisplayValue}。Business／Enterprise 的 organization AI Credits 與預算不包含在這個來源中，請以 GitHub Copilot settings 為準。",
			viewModel.ToolTipValue);
		Assert.False(viewModel.HasUsageBar);
	}

	[Theory]
	[InlineData(0, 100, "剩餘 100%", UsageLevel.Normal)]
	[InlineData(41.25, 58.75, "剩餘 58.75%", UsageLevel.Normal)]
	[InlineData(80, 20, "剩餘 20%", UsageLevel.Normal)]
	[InlineData(80.01, 19.99, "剩餘 19.99%", UsageLevel.Warning)]
	[InlineData(100, 0, "剩餘 0%", UsageLevel.Critical)]
	public void Constructor_WithRemainingMode_UsesComplementButCanonicalRisk(
		double usedPercent,
		double expectedDisplayPercent,
		string expectedDisplayValue,
		UsageLevel expectedLevel)
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"quota",
				"用量",
				usedPercent,
				$"已使用 {usedPercent}%"),
			UsageDisplayMode.Remaining);

		Assert.Equal(expectedDisplayPercent, viewModel.DisplayPercent, 2);
		Assert.Equal(expectedDisplayValue, viewModel.DisplayValue);
		Assert.Equal(expectedLevel, viewModel.Level);
	}

	[Fact]
	public void Constructor_WithoutResetTime_ReportsUnavailableTime()
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"five_hour",
			"5 小時",
			25,
			"已使用 25%"));

		Assert.True(viewModel.HasUsageBar);
		Assert.True(viewModel.HasResetText);
		Assert.Equal("未提供重置時間", viewModel.ResetText);
		Assert.Equal("5小時用量", viewModel.Label);
		Assert.False(viewModel.IsResetImminent);
	}

	[Theory]
	[InlineData("five_hour", "5 小時用量", "5小時用量")]
	[InlineData("seven_day", "7 天用量", "週用量")]
	[InlineData("codex:codex:primary", "codex · 5 小時用量", "5小時用量 · Codex")]
	[InlineData("codex:default:secondary", "Default · 7 天用量", "週用量 · Default")]
	[InlineData("codex:codex:secondary", "codex · 7 天用量", "週用量 · Codex")]
	[InlineData(
		"codex:gpt-5.3-codex-spark:secondary",
		"gpt-5.3-codex-spark · 7 天用量",
		"週用量 · GPT-5.3-Codex-Spark")]
	[InlineData("agy.gemini.rolling-5h", "Gemini rolling 5h", "5小時用量 · Gemini")]
	[InlineData("agy.gemini.weekly", "gEMINI weekly", "週用量 · Gemini")]
	[InlineData("agy.claude.rolling-5h", "Claude rolling 5h", "5小時用量 · Claude + GPT")]
	[InlineData("agy.claude.weekly", "cLaUdE weekly", "週用量 · Claude + GPT")]
	[InlineData(
		"claude.rate_limit.seven_day.bucket.fable",
		"本週（fAbLe）",
		"週用量 · Fable")]
	[InlineData(
		"claude.rate_limit.seven_day.all_models",
		"本週（所有模型）",
		"週用量 · 所有模型")]
	[InlineData("codex:codex:secondary", "週用量 · codex", "週用量 · Codex")]
	[InlineData("seven_day", "週用量 · fable", "週用量 · Fable")]
	[InlineData("seven_day", "本週（Team plan）", "週用量 · Team plan")]
	public void Constructor_NormalizesKnownWindowLabels(
		string key,
		string label,
		string expectedLabel)
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			key,
			label,
			25,
			"已使用 25%"));

		Assert.Equal(expectedLabel, viewModel.Label);
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, true)]
	[InlineData(60, true)]
	[InlineData(61, false)]
	public void Constructor_ProjectsImminentResetWithinOneHour(
		int minutesUntilReset,
		bool expectedIsResetImminent)
	{
		DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"five_hour",
				"5小時用量",
				25,
				"已使用 25%",
				now.AddMinutes(minutesUntilReset)),
			new FixedTimeProvider(now));

		Assert.Equal(expectedIsResetImminent, viewModel.IsResetImminent);
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, true)]
	[InlineData(2880, true)]
	[InlineData(2881, false)]
	public void Constructor_ProjectsImminentWeeklyResetWithinTwoDays(
		int minutesUntilReset,
		bool expectedIsResetImminent)
	{
		DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"weekly",
				"週用量",
				25,
				"已使用 25%",
				now.AddMinutes(minutesUntilReset)),
			new FixedTimeProvider(now));

		Assert.Equal(expectedIsResetImminent, viewModel.IsResetImminent);
	}

	[Theory]
	[InlineData("claude.rate_limit.seven_day.all_models", "週用量")]
	[InlineData("codex:codex:secondary", "7 天用量")]
	[InlineData("agy.gemini.weekly", "Gemini weekly")]
	public void Constructor_RecognizesProviderWeeklyResetThreshold(
		string metricKey,
		string metricLabel)
	{
		DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				metricKey,
				metricLabel,
				25,
				"已使用 25%",
				now.AddDays(1)),
			new FixedTimeProvider(now));

		Assert.True(viewModel.IsResetImminent);
	}

	[Theory]
	[InlineData("agy.gemini.rolling-5h", "Gemini rolling 5h")]
	[InlineData("agy.gemini.weekly", "Gemini weekly")]
	public void Constructor_WithProviderResetDisplay_DoesNotMarkResetImminent(
		string metricKey,
		string metricLabel)
	{
		DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				metricKey,
				metricLabel,
				0,
				"已使用 0%",
				now.AddMinutes(10),
				"目前可用"),
			new FixedTimeProvider(now));

		Assert.False(viewModel.IsResetImminent);
	}

	[Fact]
	public void Constructor_WithResetDisplayValue_PrefersProviderText()
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"agy.weekly",
			"每週用量",
			50,
			"已使用 50%",
			new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
			"目前可用"));

		Assert.True(viewModel.HasUsageBar);
		Assert.True(viewModel.HasResetText);
		Assert.Equal("目前可用", viewModel.ResetText);
	}

	[Fact]
	public void Constructor_WithWhitespaceResetDisplayValue_FallsBackToResetTime()
	{
		DateTimeOffset resetsAt = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"agy.weekly",
			"每週用量",
			50,
			"已使用 50%",
			resetsAt,
			" "));

		Assert.True(viewModel.HasResetText);
		Assert.Equal(
			$"重置 · {resetsAt.ToLocalTime():MM/dd HH:mm}",
			viewModel.ResetText);
	}

	[Fact]
	public void Constructor_WithCustomPercentageDisplayValue_PreservesProviderText()
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"quota",
			"用量",
			25,
			"最新資料 25%"));

		Assert.Equal("最新資料 25%", viewModel.DisplayValue);
		Assert.Equal("最新資料 25%", viewModel.ToolTipValue);
	}

	[Fact]
	public void Constructor_WithNonPercentageMetric_HidesUsageBarAndResetTime()
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"codex:rate_limit_reset_credits",
			"可用重置次數",
			null,
			"2 次"));

		Assert.False(viewModel.HasUsageBar);
		Assert.False(viewModel.HasResetText);
		Assert.False(viewModel.IsResetImminent);
		Assert.Equal(0, viewModel.UsedPercent);
		Assert.Equal(UsageLevel.Normal, viewModel.Level);
		Assert.Equal("2 次", viewModel.DisplayValue);
		Assert.Equal("未提供重置時間", viewModel.ResetText);
	}

	[Theory]
	[InlineData(2881, false)]
	[InlineData(2880, true)]
	[InlineData(-1, false)]
	[InlineData(null, false)]
	public void Constructor_WithCodexResetCreditsExpiry_UsesTwoDayWarningThreshold(
		int? minutesUntilExpiry,
		bool expectedIsResetImminent)
	{
		DateTimeOffset now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset? expiresAt = minutesUntilExpiry is int minutes
			? now.AddMinutes(minutes)
			: null;
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"codex:rate_limit_reset_credits",
				"可用重置次數",
				null,
				"2 次",
				expiresAt),
			new FixedTimeProvider(now));

		Assert.False(viewModel.HasUsageBar);
		Assert.Equal(expiresAt is not null, viewModel.HasResetText);
		Assert.Equal(expectedIsResetImminent, viewModel.IsResetImminent);
		if (expiresAt is DateTimeOffset expiry)
		{
			Assert.Equal($"到期 · {expiry.ToLocalTime():MM/dd HH:mm}", viewModel.ResetText);
		}
	}

	[Fact]
	public void Constructor_WithNonPercentageMetricAndResetTime_ShowsResetTime()
	{
		DateTimeOffset now = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset resetsAt = now.AddDays(1);
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"grok.weekly",
				"週用量",
				null,
				"尚未提供用量",
				resetsAt),
			new FixedTimeProvider(now));

		Assert.False(viewModel.HasUsageBar);
		Assert.True(viewModel.HasResetText);
		Assert.True(viewModel.IsResetImminent);
		Assert.Equal(
			$"重置 · {resetsAt.ToLocalTime():MM/dd HH:mm}",
			viewModel.ResetText);
	}

	[Fact]
	public void Constructor_WithRemainingModeAndNonPercentageMetric_PreservesProviderValue()
	{
		UsageMetricViewModel viewModel = new(
			new UsageMetric(
				"codex:rate_limit_reset_credits",
				"可用重置次數",
				null,
				"2 次"),
			UsageDisplayMode.Remaining);

		Assert.False(viewModel.HasUsageBar);
		Assert.Equal(0, viewModel.DisplayPercent);
		Assert.Equal("2 次", viewModel.DisplayValue);
	}

	[Fact]
	public void Constructor_WithNonPercentageAvailability_HidesResetText()
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"agy.availability",
			"可用狀態",
			null,
			"可用",
			ResetDisplayValue: "目前可用"));

		Assert.False(viewModel.HasUsageBar);
		Assert.False(viewModel.HasResetText);
		Assert.Equal("目前可用", viewModel.ResetText);
	}

	[Theory]
	[InlineData(-10, UsageLevel.Normal, 0)]
	[InlineData(0, UsageLevel.Normal, 0)]
	[InlineData(79.99, UsageLevel.Normal, 79.99)]
	[InlineData(80, UsageLevel.Normal, 80)]
	[InlineData(80.01, UsageLevel.Warning, 80.01)]
	[InlineData(99.99, UsageLevel.Warning, 99.99)]
	[InlineData(100, UsageLevel.Critical, 100)]
	[InlineData(125, UsageLevel.Critical, 100)]
	public void Constructor_WithPercentage_AssignsSemanticUsageLevel(
		double usedPercent,
		UsageLevel expectedLevel,
		double expectedUsedPercent)
	{
		UsageMetricViewModel viewModel = new(new UsageMetric(
			"quota",
			"用量",
			usedPercent,
			$"已使用 {usedPercent}%"));

		Assert.Equal(expectedLevel, viewModel.Level);
		Assert.Equal(expectedUsedPercent, viewModel.UsedPercent);
	}

	[Fact]
	public void Constructor_WithUnknownDisplayMode_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new UsageMetricViewModel(
				new UsageMetric("quota", "用量", 25, "已使用 25%"),
				(UsageDisplayMode)int.MaxValue));
	}
}
