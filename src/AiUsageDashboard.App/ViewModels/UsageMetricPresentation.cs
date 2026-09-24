using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.ViewModels;

internal static class UsageMetricPresentation
{
	internal const string CodexResetCreditsKey = "codex:rate_limit_reset_credits";

	private const string FiveHourUsageLabel = "5小時用量";
	private const string WeeklyUsageLabel = "週用量";
	private static readonly TimeSpan ImminentResetThreshold = TimeSpan.FromHours(1);
	private static readonly TimeSpan WeeklyImminentResetThreshold = TimeSpan.FromDays(2);

	internal static string GetDisplayLabel(UsageMetric metric)
	{
		ArgumentNullException.ThrowIfNull(metric);

		if (IsFiveHour(metric))
		{
			return CreateDisplayLabel(metric, FiveHourUsageLabel);
		}

		if (IsWeekly(metric))
		{
			return CreateDisplayLabel(metric, WeeklyUsageLabel);
		}

		return metric.Label;
	}

	internal static DateTimeOffset GetResetSortValue(
		UsageMetric metric,
		DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(metric);
		return metric.ResetsAt is DateTimeOffset resetsAt && (resetsAt > now)
			? resetsAt
			: DateTimeOffset.MaxValue;
	}

	internal static int GetWindowSortOrder(UsageMetric metric)
	{
		ArgumentNullException.ThrowIfNull(metric);

		if (IsFiveHour(metric))
		{
			return 0;
		}

		return IsWeekly(metric) ? 1 : 2;
	}

	internal static bool IsResetImminent(
		UsageMetric metric,
		DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(metric);

		if (!string.IsNullOrWhiteSpace(metric.ResetDisplayValue) ||
			(metric.ResetsAt is not DateTimeOffset resetsAt))
		{
			return false;
		}

		TimeSpan remaining = resetsAt - now;
		bool hasTwoDayThreshold = (metric.Key == CodexResetCreditsKey) ||
			((!IsFiveHour(metric)) && (IsWeekly(metric)));
		TimeSpan resetThreshold = hasTwoDayThreshold
			? WeeklyImminentResetThreshold
			: ImminentResetThreshold;
		return (remaining >= TimeSpan.Zero) &&
			(remaining <= resetThreshold);
	}

	internal static IOrderedEnumerable<UsageMetric> OrderMetrics(
		IEnumerable<UsageMetric> metrics,
		DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(metrics);
		DateTimeOffset comparisonTime = now ?? TimeProvider.System.GetUtcNow();

		return metrics
			.OrderBy(GetProviderMetricSortOrder)
			.ThenBy(GetWindowSortOrder)
			.ThenBy(metric => GetResetSortValue(metric, comparisonTime));
	}

	private static int GetProviderMetricSortOrder(UsageMetric metric)
	{
		return metric.Key switch
		{
			"copilot-quota-premium-interactions" => 0,
			"copilot-quota-chat" => 1,
			"copilot-quota-completions" => 2,
			_ when metric.Key.StartsWith(
				"copilot-quota-",
				StringComparison.Ordinal) => 3,
			_ when metric.Key.StartsWith(
				"codex:codex:",
				StringComparison.OrdinalIgnoreCase) => 0,
			_ when metric.Key == CodexResetCreditsKey => 5,
			"claude.rate_limit.five_hour" => 0,
			"claude.rate_limit.seven_day.all_models" => 1,
			_ when metric.Key.StartsWith(
				"claude.rate_limit.seven_day.bucket.",
				StringComparison.Ordinal) => 2,
			"agy.gemini.rolling-5h" => 0,
			"agy.gemini.weekly" => 1,
			"agy.claude.rolling-5h" => 2,
			"agy.claude.weekly" => 3,
			_ => 4
		};
	}

	private static string CreateDisplayLabel(
		UsageMetric metric,
		string canonicalWindowLabel)
	{
		string label = metric.Label.Trim();
		List<string> qualifiers = new();
		string? antigravityModel = GetAntigravityModel(metric.Key);

		if (antigravityModel is not null)
		{
			qualifiers.Add(antigravityModel);
		}

		int qualifierStart = label.IndexOf('（');
		int qualifierEnd = label.LastIndexOf('）');
		string labelWithoutQualifier = label;

		if ((qualifierStart >= 0) && (qualifierEnd > qualifierStart))
		{
			qualifiers.Add(label[(qualifierStart + 1)..qualifierEnd]);
			labelWithoutQualifier = label[..qualifierStart];
		}

		if (antigravityModel is null)
		{
			string canonicalPrefix = $"{canonicalWindowLabel} · ";

			if (labelWithoutQualifier.StartsWith(
				canonicalPrefix,
				StringComparison.OrdinalIgnoreCase))
			{
				qualifiers.Insert(
					0,
					labelWithoutQualifier[canonicalPrefix.Length..]);
			}
			else
			{
				int separatorIndex = labelWithoutQualifier.LastIndexOf(
					" · ",
					StringComparison.Ordinal);

				if (separatorIndex >= 0)
				{
					qualifiers.Insert(
						0,
						labelWithoutQualifier[..separatorIndex]);
				}
			}
		}

		string[] normalizedQualifiers = qualifiers
			.Select(NormalizeQualifier)
			.Where(qualifier => qualifier.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return normalizedQualifiers.Length == 0
			? canonicalWindowLabel
			: $"{canonicalWindowLabel} · {string.Join(" · ", normalizedQualifiers)}";
	}

	private static string? GetAntigravityModel(string key)
	{
		if (key.Contains(".claude.", StringComparison.OrdinalIgnoreCase))
		{
			return "Claude + GPT";
		}

		return key.Contains(".gemini.", StringComparison.OrdinalIgnoreCase)
			? "Gemini"
			: null;
	}

	private static string NormalizeQualifier(string qualifier)
	{
		string trimmed = qualifier.Trim();

		return trimmed.ToLowerInvariant() switch
		{
			"claude" => "Claude",
			"codex" => "Codex",
			"default" => "Default",
			"fable" => "Fable",
			"gemini" => "Gemini",
			"gpt-5.3-codex-spark" => "GPT-5.3-Codex-Spark",
			_ => trimmed
		};
	}

	private static bool IsFiveHour(UsageMetric metric)
	{
		return metric.Key.Contains("five_hour", StringComparison.OrdinalIgnoreCase) ||
			metric.Key.Contains("rolling-5h", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains("5小時", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains("5 小時", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Equals("5h", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.StartsWith("5h ", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains(" 5h", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsWeekly(UsageMetric metric)
	{
		return metric.Key.Contains("seven_day", StringComparison.OrdinalIgnoreCase) ||
			metric.Key.Contains(".weekly", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains("週", StringComparison.Ordinal) ||
			metric.Label.Contains("7天", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains("7 天", StringComparison.OrdinalIgnoreCase) ||
			metric.Label.Contains("weekly", StringComparison.OrdinalIgnoreCase);
	}
}
