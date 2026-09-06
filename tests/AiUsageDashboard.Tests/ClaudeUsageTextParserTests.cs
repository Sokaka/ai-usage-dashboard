using System.IO;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeUsageTextParserTests
{
	private const string FirstAccountOutput = """
		You are currently using your subscription to power your Claude Code usage

		Current session: 0% used · resets Jul 15, 2:19pm (Asia/Taipei)
		Current week (all models): 93% used · resets Jul 17, 10:59am (Asia/Taipei)
		Current week (Fable): 100% used · resets Jul 17, 10:59am (Asia/Taipei)
		""";

	[Fact]
	public void Parse_WithObservedQuotaLines_ReturnsCoreAndDynamicMetrics()
	{
		DateTimeOffset observedAt = new(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			FirstAccountOutput,
			observedAt);

		Assert.False(result.IsStale);
		Assert.Collection(
			result.Metrics,
			metric => AssertMetric(
				metric,
				"claude.rate_limit.five_hour",
				"5小時用量",
				0,
				new DateTimeOffset(2026, 7, 15, 14, 19, 0, TimeSpan.FromHours(8))),
			metric => AssertMetric(
				metric,
				"claude.rate_limit.seven_day.all_models",
				"週用量（所有模型）",
				93,
				new DateTimeOffset(2026, 7, 17, 10, 59, 0, TimeSpan.FromHours(8))),
			metric =>
			{
				Assert.StartsWith(
					"claude.rate_limit.seven_day.bucket.",
					metric.Key,
					StringComparison.Ordinal);
				Assert.Equal("週用量（Fable）", metric.Label);
				Assert.Equal(100, metric.UsedPercent);
				Assert.Equal(
					new DateTimeOffset(
						2026,
						7,
						17,
						10,
						59,
						0,
						TimeSpan.FromHours(8)),
					metric.ResetsAt);
			});
	}

	[Fact]
	public void Parse_WithSubscriptionGuardOnly_ReportsQuotaUnavailable()
	{
		const string output =
			"You are currently using your subscription to power your Claude Code usage";

		Assert.Throws<ClaudeUsageQuotaUnavailableException>(() =>
			ClaudeUsageTextParser.Parse(
				output,
				new DateTimeOffset(2026, 8, 22, 4, 27, 44, TimeSpan.Zero)));
	}

	[Fact]
	public void Parse_WithHourWithoutMinutes_ParsesResetTime()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 49% used · resets Jul 15, 11:30am (Asia/Taipei)
			Current week (all models): 84% used · resets Jul 20, 8am (Asia/Taipei)
			Current week (Fable): 83% used · resets Jul 20, 8am (Asia/Taipei)
			""";
		DateTimeOffset observedAt = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			output,
			observedAt);

		UsageMetric weeklyMetric = result.Metrics[1];
		Assert.Equal(
			new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.FromHours(8)),
			weeklyMetric.ResetsAt);
	}

	[Fact]
	public void Parse_WithZeroOptionalWeeklyBucketWithoutReset_ReturnsMetricWithoutResetTime()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 20% used · resets Jul 16, 2:59pm (Asia/Taipei)
			Current week (all models): 4% used · resets Jul 18, 1:59pm (Asia/Taipei)
			Current week (Fable): 0% used
			""";

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 16, 5, 0, 0, TimeSpan.Zero));

		UsageMetric fableMetric = Assert.Single(result.Metrics.Skip(2));
		Assert.Equal("週用量（Fable）", fableMetric.Label);
		Assert.Equal(0, fableMetric.UsedPercent);
		Assert.Null(fableMetric.ResetsAt);
	}

	[Fact]
	public void Parse_WithTeamQuotaShapeWithoutReset_ReturnsCoreAndDynamicMetrics()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 0% used
			Current week (all models): 0% used
			Current week (Fable): 0% used
			""";

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 8, 30, 7, 30, 0, TimeSpan.Zero));

		Assert.Collection(
			result.Metrics,
			metric => Assert.Equal("claude.rate_limit.five_hour", metric.Key),
			metric => Assert.Equal(
				"claude.rate_limit.seven_day.all_models",
				metric.Key),
			metric => Assert.Equal("週用量（Fable）", metric.Label));
		Assert.All(result.Metrics, metric =>
		{
			Assert.Equal(0, metric.UsedPercent);
			Assert.Null(metric.ResetsAt);
		});
	}

	[Fact]
	public void Parse_WithZeroSessionWithoutReset_ReturnsMetricWithoutResetTime()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 0% used
			Current week (all models): 100% used · resets Jul 17, 7pm (Asia/Taipei)
			Current week (Fable): 46% used · resets Jul 17, 7pm (Asia/Taipei)
			""";

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 16, 5, 0, 0, TimeSpan.Zero));

		UsageMetric sessionMetric = result.Metrics[0];
		Assert.Equal(0, sessionMetric.UsedPercent);
		Assert.Null(sessionMetric.ResetsAt);
	}

	[Fact]
	public void Parse_WithNonzeroAllModelsWithoutReset_FailsClosed()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 20% used · resets Jul 16, 2:59pm (Asia/Taipei)
			Current week (all models): 4% used
			""";

		Assert.Throws<InvalidDataException>(() => ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 16, 5, 0, 0, TimeSpan.Zero)));
	}

	[Fact]
	public void Parse_WithMalformedOptionalWeeklyBucketFormat_FailsClosed()
	{
		const string output = """
			You are currently using your subscription to power your Claude Code usage

			Current session: 20% used · resets Jul 16, 2:59pm (Asia/Taipei)
			Current week (all models): 4% used · resets Jul 18, 1:59pm (Asia/Taipei)
			Current week (Fable): 0% used - resets Jul 18, 1:59pm (Asia/Taipei)
			""";

		Assert.Throws<InvalidDataException>(() => ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 16, 5, 0, 0, TimeSpan.Zero)));
	}

	[Fact]
	public void Parse_WithUnknownTimeZone_KeepsPercentWithoutInventingReset()
	{
		string output = FirstAccountOutput.Replace(
			"Asia/Taipei",
			"Unknown/Nowhere",
			StringComparison.Ordinal);

		ClaudeUsageTextParser.Result result = ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero));

		Assert.All(result.Metrics, metric => Assert.Null(metric.ResetsAt));
		Assert.Equal(93, result.Metrics[1].UsedPercent);
	}

	[Theory]
	[InlineData("Current session: 101% used · resets Jul 15, 2:19pm (Asia/Taipei)")]
	[InlineData("Current session: 1.5% used · resets Jul 15, 2:19pm (Asia/Taipei)")]
	[InlineData("Current session: 1% used - resets Jul 15, 2:19pm (Asia/Taipei)")]
	[InlineData("Current session: 1% used")]
	public void Parse_WithChangedCoreQuotaFormat_FailsClosed(string sessionLine)
	{
		string output = $"""
			You are currently using your subscription to power your Claude Code usage

			{sessionLine}
			Current week (all models): 10% used · resets Jul 17, 10:59am (Asia/Taipei)
			""";

		Assert.Throws<InvalidDataException>(() => ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero)));
	}

	[Fact]
	public void Parse_WithoutSubscriptionGuard_FailsClosed()
	{
		string output = FirstAccountOutput.Replace(
			"You are currently using your subscription to power your Claude Code usage",
			"Current usage",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => ClaudeUsageTextParser.Parse(
			output,
			new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero)));
	}

	private static void AssertMetric(
		UsageMetric metric,
		string key,
		string label,
		double percentage,
		DateTimeOffset resetsAt)
	{
		Assert.Equal(key, metric.Key);
		Assert.Equal(label, metric.Label);
		Assert.Equal(percentage, metric.UsedPercent);
		Assert.Equal(resetsAt, metric.ResetsAt);
	}
}
