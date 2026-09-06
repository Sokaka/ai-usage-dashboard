using System.Text.Json;

namespace AiUsageDashboard.Core.Claude;

public static class ClaudeStatusLineParser
{
	public static ClaudeStatusLineCapture Parse(
		string json,
		DateTimeOffset capturedAt)
	{
		ArgumentNullException.ThrowIfNull(json);

		if (capturedAt == default)
		{
			throw new ArgumentOutOfRangeException(
				nameof(capturedAt),
				"Capture time is required.");
		}

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		RequireObject(root, "status line input");

		string? claudeVersion = ReadOptionalString(root, "version");
		ClaudeStatusLineModel? model = ReadOptionalModel(root);
		bool hasCurrentUsage = ReadHasCurrentUsage(root);

		ClaudeRateLimitWindow? fiveHour = null;
		ClaudeRateLimitWindow? sevenDay = null;
		if (root.TryGetProperty("rate_limits", out JsonElement rateLimitsElement) &&
			(rateLimitsElement.ValueKind != JsonValueKind.Null))
		{
			RequireObject(rateLimitsElement, "rate_limits");
			fiveHour = ReadOptionalWindow(rateLimitsElement, "five_hour");
			sevenDay = ReadOptionalWindow(rateLimitsElement, "seven_day");
		}

		return new ClaudeStatusLineCapture(
			ClaudeStatusLineCapture.CurrentSchemaVersion,
			capturedAt,
			claudeVersion,
			model,
			hasCurrentUsage,
			fiveHour,
			sevenDay);
	}

	private static bool ReadHasCurrentUsage(JsonElement root)
	{
		if (!root.TryGetProperty("context_window", out JsonElement contextElement) ||
			(contextElement.ValueKind == JsonValueKind.Null))
		{
			return false;
		}

		RequireObject(contextElement, "context_window");
		if (!contextElement.TryGetProperty("current_usage", out JsonElement usageElement) ||
			(usageElement.ValueKind == JsonValueKind.Null))
		{
			return false;
		}

		RequireObject(usageElement, "context_window.current_usage");
		return true;
	}

	private static ClaudeRateLimitWindow? ReadOptionalWindow(
		JsonElement rateLimitsElement,
		string propertyName)
	{
		if (!rateLimitsElement.TryGetProperty(propertyName, out JsonElement windowElement) ||
			(windowElement.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		RequireObject(windowElement, $"rate_limits.{propertyName}");
		if (!windowElement.TryGetProperty("used_percentage", out JsonElement percentageElement) ||
			(percentageElement.ValueKind != JsonValueKind.Number) ||
			!percentageElement.TryGetDouble(out double percentage) ||
			!double.IsFinite(percentage) ||
			(percentage < 0) ||
			(percentage > 100))
		{
			throw new JsonException(
				$"rate_limits.{propertyName}.used_percentage must be a number from 0 through 100.");
		}

		DateTimeOffset? resetsAt = null;
		if (windowElement.TryGetProperty("resets_at", out JsonElement resetElement) &&
			(resetElement.ValueKind != JsonValueKind.Null))
		{
			if ((resetElement.ValueKind != JsonValueKind.Number) ||
				!resetElement.TryGetInt64(out long resetSeconds) ||
				(resetSeconds <= 0))
			{
				throw new JsonException(
					$"rate_limits.{propertyName}.resets_at must be a positive Unix timestamp.");
			}

			try
			{
				resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
			}
			catch (ArgumentOutOfRangeException exception)
			{
				throw new JsonException(
					$"rate_limits.{propertyName}.resets_at is outside the supported range.",
					exception);
			}
		}

		return new ClaudeRateLimitWindow(percentage, resetsAt);
	}

	private static ClaudeStatusLineModel? ReadOptionalModel(JsonElement root)
	{
		if (!root.TryGetProperty("model", out JsonElement modelElement) ||
			(modelElement.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		RequireObject(modelElement, "model");
		string? id = ReadOptionalString(modelElement, "id");
		string? displayName = ReadOptionalString(modelElement, "display_name");
		if ((id is null) && (displayName is null))
		{
			return null;
		}

		return new ClaudeStatusLineModel(id ?? string.Empty, displayName ?? string.Empty);
	}

	private static string? ReadOptionalString(
		JsonElement parent,
		string propertyName)
	{
		if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
			(value.ValueKind == JsonValueKind.Null))
		{
			return null;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			throw new JsonException($"Optional property '{propertyName}' must be a string.");
		}

		return value.GetString();
	}

	private static void RequireObject(
		JsonElement element,
		string name)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException($"{name} must be a JSON object.");
		}
	}
}
