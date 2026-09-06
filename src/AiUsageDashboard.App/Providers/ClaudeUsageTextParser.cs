using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeUsageQuotaUnavailableException : IOException
{
	public ClaudeUsageQuotaUnavailableException(string message)
		: base(message)
	{
	}
}

internal static class ClaudeUsageTextParser
{
	internal sealed record Result(
		IReadOnlyList<UsageMetric> Metrics,
		bool IsStale);

	private sealed record ParsedQuota(
		string Key,
		string Label,
		double UsedPercent,
		string? ResetText,
		TimeSpan MaximumWindow);

	private const string SubscriptionGuard =
		"You are currently using your subscription to power your Claude Code usage";
	private static readonly TimeSpan FreshResultGrace = TimeSpan.FromMinutes(10);
	private static readonly Regex ResetRegex = new(
		"^(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) " +
		"(?<day>[1-9]|[12][0-9]|3[01]), " +
		"(?<hour>[1-9]|1[0-2])(?::(?<minute>[0-5][0-9]))?" +
		"(?<period>am|pm) " +
		"\\((?<zone>UTC|[A-Za-z0-9._+-]+/[A-Za-z0-9._+/-]+)\\)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly TimeSpan SessionMaximumWindow =
		TimeSpan.FromHours(5) + TimeSpan.FromMinutes(10);
	private static readonly Regex SessionRegex = new(
		"^Current session: (?<percent>100|[0-9]{1,2})% used" +
		"(?: · resets (?<reset>.+))?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly TimeSpan StaleResultGrace = TimeSpan.FromMinutes(65);
	private static readonly TimeSpan WeekMaximumWindow =
		TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
	private static readonly Regex WeekRegex = new(
		"^Current week \\((?<bucket>[^()\\p{Cc}\\p{Cf}]{1,128})\\): " +
		"(?<percent>100|[0-9]{1,2})% used" +
		"(?: · resets (?<reset>.+))?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	internal static Result Parse(
		string output,
		DateTimeOffset observedAt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(output);
		string[] lines = output
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\r', '\n')
			.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.ToArray();

		if ((lines.Length == 0) ||
			!string.Equals(lines[0], SubscriptionGuard, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Claude `/usage` 回傳的內容不是 claude.ai 訂閱用量。");
		}

		if (lines.Length == 1)
		{
			throw new ClaudeUsageQuotaUnavailableException(
				"Claude `/usage` 只有訂閱提示，未回傳 quota。");
		}

		Dictionary<string, ParsedQuota> parsedQuotas = new(StringComparer.Ordinal);
		bool isStale = false;

		foreach (string line in lines.Skip(1))
		{
			if (line.Any(char.IsControl))
			{
				throw new InvalidDataException("Claude `/usage` 包含不支援的控制字元。");
			}

			if (line.StartsWith("Showing last-known usage", StringComparison.Ordinal))
			{
				isStale = true;
				continue;
			}

			Match sessionMatch = SessionRegex.Match(line);

			if (sessionMatch.Success)
			{
				double usedPercent = ParsePercentage(
					sessionMatch.Groups["percent"].Value);
				AddQuota(
					parsedQuotas,
					new ParsedQuota(
						"claude.rate_limit.five_hour",
						"5小時用量",
						usedPercent,
						GetResetText(sessionMatch, usedPercent),
						SessionMaximumWindow));
				continue;
			}

			Match weekMatch = WeekRegex.Match(line);

			if (weekMatch.Success)
			{
				double usedPercent = ParsePercentage(
					weekMatch.Groups["percent"].Value);
				string bucket = NormalizeBucket(weekMatch.Groups["bucket"].Value);
				bool isAllModels = string.Equals(
					bucket,
					"all models",
					StringComparison.Ordinal);
				string key = isAllModels
					? "claude.rate_limit.seven_day.all_models"
					: $"claude.rate_limit.seven_day.bucket.{CreateBucketKey(bucket)}";
				string label = isAllModels
					? "週用量（所有模型）"
					: $"週用量（{weekMatch.Groups["bucket"].Value.Trim()}）";
				AddQuota(
					parsedQuotas,
					new ParsedQuota(
						key,
						label,
						usedPercent,
						GetResetText(weekMatch, usedPercent),
						WeekMaximumWindow));
				continue;
			}

			if (line.StartsWith("Current session", StringComparison.Ordinal) ||
				line.StartsWith("Current week", StringComparison.Ordinal))
			{
				throw new InvalidDataException("Claude `/usage` quota 格式已變更。");
			}
		}

		if (!parsedQuotas.ContainsKey("claude.rate_limit.five_hour") ||
			!parsedQuotas.ContainsKey("claude.rate_limit.seven_day.all_models"))
		{
			throw new InvalidDataException("Claude `/usage` 缺少核心 session／week quota。");
		}

		TimeSpan pastGrace = isStale ? StaleResultGrace : FreshResultGrace;
		IEnumerable<ParsedQuota> orderedQuotas = new[]
		{
			parsedQuotas["claude.rate_limit.five_hour"],
			parsedQuotas["claude.rate_limit.seven_day.all_models"]
		}.Concat(parsedQuotas.Values.Where(quota =>
			(quota.Key != "claude.rate_limit.five_hour") &&
			(quota.Key != "claude.rate_limit.seven_day.all_models")));
		IReadOnlyList<UsageMetric> metrics = orderedQuotas
			.Select(quota => new UsageMetric(
				quota.Key,
				quota.Label,
				quota.UsedPercent,
				$"已使用 {quota.UsedPercent.ToString("0", CultureInfo.InvariantCulture)}%",
				TryParseResetTime(
					quota.ResetText,
					observedAt,
					pastGrace,
					quota.MaximumWindow)))
			.ToArray();
		return new Result(metrics, isStale);
	}

	private static void AddQuota(
		IDictionary<string, ParsedQuota> parsedQuotas,
		ParsedQuota quota)
	{
		if (!parsedQuotas.TryGetValue(quota.Key, out ParsedQuota? existingQuota))
		{
			parsedQuotas.Add(quota.Key, quota);
			return;
		}

		if (!Equals(existingQuota, quota))
		{
			throw new InvalidDataException("Claude `/usage` 包含衝突的重複 quota。");
		}
	}

	private static string CreateBucketKey(string normalizedBucket)
	{
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedBucket))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private static int GetMonth(string month)
	{
		return month switch
		{
			"Jan" => 1,
			"Feb" => 2,
			"Mar" => 3,
			"Apr" => 4,
			"May" => 5,
			"Jun" => 6,
			"Jul" => 7,
			"Aug" => 8,
			"Sep" => 9,
			"Oct" => 10,
			"Nov" => 11,
			"Dec" => 12,
			_ => throw new ArgumentOutOfRangeException(nameof(month))
		};
	}

	private static string? GetResetText(Match quotaMatch, double usedPercent)
	{
		if (quotaMatch.Groups["reset"].Success)
		{
			return quotaMatch.Groups["reset"].Value;
		}

		if (usedPercent == 0)
		{
			return null;
		}

		throw new InvalidDataException(
			"Claude `/usage` 非零 quota 缺少 reset time。");
	}

	private static TimeZoneInfo? GetTimeZone(string id)
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(id);
		}
		catch (TimeZoneNotFoundException)
		{
			if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out string? windowsId))
			{
				try
				{
					return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
				}
				catch (TimeZoneNotFoundException)
				{
					return null;
				}
				catch (InvalidTimeZoneException)
				{
					return null;
				}
			}

			return null;
		}
		catch (InvalidTimeZoneException)
		{
			return null;
		}
	}

	private static string NormalizeBucket(string bucket)
	{
		string normalized = bucket.Normalize(NormalizationForm.FormKC).Trim();
		normalized = Regex.Replace(normalized, "[ \\t]+", " ");
		return normalized.ToLowerInvariant();
	}

	private static double ParsePercentage(string value)
	{
		if (!int.TryParse(
			value,
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out int percentage) ||
			(percentage < 0) ||
			(percentage > 100))
		{
			throw new InvalidDataException("Claude `/usage` percentage 超出範圍。");
		}

		return percentage;
	}

	private static DateTimeOffset? TryParseResetTime(
		string? resetText,
		DateTimeOffset observedAt,
		TimeSpan pastGrace,
		TimeSpan maximumWindow)
	{
		if (string.IsNullOrWhiteSpace(resetText))
		{
			return null;
		}

		Match match = ResetRegex.Match(resetText);

		if (!match.Success ||
			!int.TryParse(match.Groups["day"].Value, out int day) ||
			!int.TryParse(match.Groups["hour"].Value, out int hour) ||
			!int.TryParse(
				match.Groups["minute"].Success
					? match.Groups["minute"].Value
					: "0",
				out int minute))
		{
			return null;
		}

		if (string.Equals(match.Groups["period"].Value, "pm", StringComparison.Ordinal) &&
			(hour < 12))
		{
			hour += 12;
		}
		else if (string.Equals(
			match.Groups["period"].Value,
			"am",
			StringComparison.Ordinal) &&
			(hour == 12))
		{
			hour = 0;
		}

		TimeZoneInfo? timeZone = GetTimeZone(match.Groups["zone"].Value);

		if (timeZone is null)
		{
			return null;
		}

		DateTimeOffset observedInZone = TimeZoneInfo.ConvertTime(observedAt, timeZone);
		List<DateTimeOffset> candidates = new(3);

		for (int year = observedInZone.Year - 1; year <= observedInZone.Year + 1; year++)
		{
			DateTime localTime;

			try
			{
				localTime = new DateTime(
					year,
					GetMonth(match.Groups["month"].Value),
					day,
					hour,
					minute,
					0,
					DateTimeKind.Unspecified);
			}
			catch (ArgumentOutOfRangeException)
			{
				continue;
			}

			if (timeZone.IsInvalidTime(localTime) || timeZone.IsAmbiguousTime(localTime))
			{
				continue;
			}

			DateTimeOffset candidate = new(localTime, timeZone.GetUtcOffset(localTime));

			if ((candidate >= (observedAt - pastGrace)) &&
				(candidate <= (observedAt + maximumWindow)))
			{
				candidates.Add(candidate);
			}
		}

		return candidates.Count == 1 ? candidates[0] : null;
	}
}
