using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityOfficialPrintParseResult(
	AntigravityProductionUsageResult Result,
	bool RequiresSafetyLatch,
	AntigravityUsageSafetyFailureReason SafetyFailureReason);

internal static class AntigravityOfficialPrintEnvelopeParser
{
	private sealed record ExpectedBucket(
		string BucketId,
		string Window,
		string StableWindowId);

	private sealed record ParsedBucket(
		string BucketId,
		decimal RemainingFraction,
		long RemainingAmount,
		DateTimeOffset? ResetAt);

	private const string CommandName = "usage";
	private static readonly string[] RequiredUsageCounters =
	{
		"input_tokens",
		"output_tokens",
		"thinking_tokens",
		"cache_read_tokens",
		"total_tokens"
	};
	private static readonly HashSet<string> CommandEventProperties =
		new(new[] { "event", "command" }, StringComparer.Ordinal);
	private static readonly HashSet<string> ResultEventProperties =
		new(new[] { "event", "result" }, StringComparer.Ordinal);
	private static readonly HashSet<string> CommandProperties =
		new(new[] { "name", "data" }, StringComparer.Ordinal);
	private static readonly HashSet<string> CommandDataProperties =
		new(new[] { "groups" }, StringComparer.Ordinal);
	private static readonly HashSet<string> GroupProperties =
		new(new[] { "name", "buckets" }, StringComparer.Ordinal);
	private static readonly HashSet<string> BucketProperties = new(
		new[] { "id", "name" },
		StringComparer.Ordinal);
	private static readonly HashSet<string> ResultProperties = new(
		new[]
		{
			"conversation_id",
			"status",
			"response",
			"duration_seconds",
			"num_turns",
			"usage",
			"command"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> UsageProperties =
		new(RequiredUsageCounters, StringComparer.Ordinal);
	private static readonly string[] Rfc3339Formats =
	{
		"yyyy-MM-dd'T'HH:mm:ss'Z'",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
		"yyyy-MM-dd'T'HH:mm:sszzz",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
	};
	private static readonly ExpectedBucket[] ExpectedBuckets =
	{
		new(
			"gemini-weekly",
			"weekly",
			"agy.gemini.weekly"),
		new(
			"gemini-5h",
			"5h",
			"agy.gemini.rolling-5h"),
		new(
			"3p-weekly",
			"weekly",
			"agy.claude.weekly"),
		new(
			"3p-5h",
			"5h",
			"agy.claude.rolling-5h")
	};
	private static readonly IReadOnlySet<string>[] ExpectedBucketGroups =
	{
		new HashSet<string>(
			new[] { "gemini-weekly", "gemini-5h" },
			StringComparer.Ordinal),
		new HashSet<string>(
			new[] { "3p-weekly", "3p-5h" },
			StringComparer.Ordinal)
	};

	internal static AntigravityOfficialPrintParseResult Parse(
		ReadOnlyMemory<byte> stdoutUtf8,
		DateTimeOffset observedAt)
	{
		try
		{
			if (!TrySplitJsonLines(
				stdoutUtf8,
				out ReadOnlyMemory<byte> commandLine,
				out ReadOnlyMemory<byte> resultLine))
			{
				return UnverifiableResponseFailure(
					AntigravityUsageSafetyFailureReason
						.OutputEventSequenceInvalid);
			}

			using JsonDocument commandDocument = JsonDocument.Parse(commandLine);
			using JsonDocument resultDocument = JsonDocument.Parse(resultLine);
			JsonElement commandRoot = commandDocument.RootElement;
			JsonElement resultRoot = resultDocument.RootElement;
			bool requiresSafetyLatch = HasExplicitSafetyViolation(resultRoot);

			if (requiresSafetyLatch)
			{
				return SafetyFailure();
			}

			if (!HasUniquePropertiesRecursively(commandRoot) ||
				!HasUniquePropertiesRecursively(resultRoot))
			{
				return UnverifiableResponseFailure(
					AntigravityUsageSafetyFailureReason
						.OutputHasDuplicateProperties);
			}

			if (!TryParseCommandEvent(
				commandRoot,
				out IReadOnlyList<ParsedBucket>? firstCommand) ||
				(firstCommand is null))
			{
				return UnverifiableResponseFailure(
					AntigravityUsageSafetyFailureReason
						.OutputCommandEnvelopeInvalid);
			}

			if (!TryParseResultEvent(
					resultRoot,
					out IReadOnlyList<ParsedBucket>? finalCommand) ||
				(finalCommand is null))
			{
				return UnverifiableResponseFailure(
					AntigravityUsageSafetyFailureReason
						.OutputResultEnvelopeInvalid);
			}

			if (!firstCommand.SequenceEqual(finalCommand))
			{
				return UnverifiableResponseFailure(
					AntigravityUsageSafetyFailureReason
						.OutputCommandCopiesDiffer);
			}

			List<AntigravityProductionUsageWindow> windows = new(4);

			foreach (ExpectedBucket expected in ExpectedBuckets)
			{
				ParsedBucket parsed = finalCommand.Single(
					bucket => string.Equals(
						bucket.BucketId,
						expected.BucketId,
						StringComparison.Ordinal));
				TimeSpan? resetsIn = parsed.ResetAt.HasValue
					? parsed.ResetAt.Value > observedAt
						? parsed.ResetAt.Value - observedAt
						: TimeSpan.Zero
					: null;
				windows.Add(new AntigravityProductionUsageWindow(
					expected.StableWindowId,
					parsed.RemainingFraction * 100m,
					resetsIn,
					IsAvailable: !parsed.ResetAt.HasValue));
			}

			return new AntigravityOfficialPrintParseResult(
				new AntigravityProductionUsageResult(
					isSuccessful: true,
					AntigravityProductionUsageFailureKind.None,
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
					new ReadOnlyCollection<AntigravityProductionUsageWindow>(
						windows)),
				RequiresSafetyLatch: false,
				AntigravityUsageSafetyFailureReason.None);
		}
		catch (JsonException)
		{
			return UnverifiableResponseFailure(
				AntigravityUsageSafetyFailureReason.OutputJsonInvalid);
		}
		catch (InvalidOperationException)
		{
			return UnverifiableResponseFailure();
		}
		catch (OverflowException)
		{
			return UnverifiableResponseFailure();
		}
	}

	private static bool TrySplitJsonLines(
		ReadOnlyMemory<byte> output,
		out ReadOnlyMemory<byte> commandLine,
		out ReadOnlyMemory<byte> resultLine)
	{
		commandLine = ReadOnlyMemory<byte>.Empty;
		resultLine = ReadOnlyMemory<byte>.Empty;
		ReadOnlySpan<byte> bytes = output.Span;

		if (bytes.EndsWith("\r\n"u8))
		{
			output = output[..^2];
		}
		else if (bytes.EndsWith("\n"u8))
		{
			output = output[..^1];
		}

		bytes = output.Span;
		int separator = bytes.IndexOf((byte)'\n');

		if ((separator < 0) ||
			(bytes[(separator + 1)..].IndexOf((byte)'\n') >= 0))
		{
			return false;
		}

		int commandLength = separator;

		if ((commandLength > 0) && (bytes[commandLength - 1] == (byte)'\r'))
		{
			commandLength--;
		}

		commandLine = output[..commandLength];
		resultLine = output[(separator + 1)..];
		ReadOnlySpan<byte> commandBytes = commandLine.Span;
		ReadOnlySpan<byte> resultBytes = resultLine.Span;

		return (commandBytes.Length >= 2) &&
			(resultBytes.Length >= 2) &&
			(commandBytes[0] == (byte)'{') &&
			(commandBytes[^1] == (byte)'}') &&
			(resultBytes[0] == (byte)'{') &&
			(resultBytes[^1] == (byte)'}');
	}

	private static bool TryParseCommandEvent(
		JsonElement root,
		out IReadOnlyList<ParsedBucket>? command)
	{
		command = null;

		return root.ValueKind == JsonValueKind.Object &&
			HasExactProperties(root, CommandEventProperties) &&
			TryGetRequiredString(root, "event", out string? eventName) &&
			string.Equals(
				eventName,
				"command_result",
				StringComparison.Ordinal) &&
			root.TryGetProperty("command", out JsonElement commandElement) &&
			TryParseCommand(commandElement, out command);
	}

	private static bool TryParseResultEvent(
		JsonElement root,
		out IReadOnlyList<ParsedBucket>? command)
	{
		command = null;

		if ((root.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(root, ResultEventProperties) ||
			!TryGetRequiredString(root, "event", out string? eventName) ||
			!string.Equals(eventName, "result", StringComparison.Ordinal) ||
			!root.TryGetProperty("result", out JsonElement result) ||
			(result.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(result, ResultProperties, "error") ||
			!TryGetRequiredString(
				result,
				"conversation_id",
				out string? conversationId) ||
			!string.Equals(conversationId, string.Empty, StringComparison.Ordinal) ||
			!TryGetRequiredString(result, "status", out string? status) ||
			!string.Equals(status, "SUCCESS", StringComparison.Ordinal) ||
			!HasRequiredStringValue(result, "response") ||
			!TryGetNonNegativeFiniteNumber(result, "duration_seconds") ||
			!TryGetExactZeroInteger(result, "num_turns") ||
			!TryValidateZeroUsage(result) ||
			!TryValidateAbsentOrNullError(result) ||
			!result.TryGetProperty("command", out JsonElement commandElement))
		{
			return false;
		}

		return TryParseCommand(commandElement, out command);
	}

	private static bool TryParseCommand(
		JsonElement commandElement,
		out IReadOnlyList<ParsedBucket>? parsedBuckets)
	{
		parsedBuckets = null;

		if ((commandElement.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(commandElement, CommandProperties) ||
			!TryGetRequiredString(
				commandElement,
				"name",
				out string? commandName) ||
			!string.Equals(commandName, CommandName, StringComparison.Ordinal) ||
			!commandElement.TryGetProperty("data", out JsonElement data) ||
			(data.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(
				data,
				CommandDataProperties,
				"description") ||
			!HasAbsentOrStringValue(data, "description") ||
			!data.TryGetProperty("groups", out JsonElement groups) ||
			(groups.ValueKind != JsonValueKind.Array) ||
			(groups.GetArrayLength() != 2))
		{
			return false;
		}

		Dictionary<string, ParsedBucket> byId =
			new(StringComparer.Ordinal);
		List<IReadOnlySet<string>> observedBucketGroups = new(2);

		foreach (JsonElement group in groups.EnumerateArray())
		{
			if ((group.ValueKind != JsonValueKind.Object) ||
				!HasExactProperties(
					group,
					GroupProperties,
					"description") ||
				!TryGetRequiredString(group, "name", out _) ||
				!HasAbsentOrStringValue(group, "description") ||
				!group.TryGetProperty("buckets", out JsonElement buckets) ||
				(buckets.ValueKind != JsonValueKind.Array) ||
				(buckets.GetArrayLength() != 2))
			{
				return false;
			}

			HashSet<string> bucketIds = new(StringComparer.Ordinal);

			foreach (JsonElement bucket in buckets.EnumerateArray())
			{
				if (!TryParseBucket(
					bucket,
					out ParsedBucket? parsed) ||
					(parsed is null) ||
					!bucketIds.Add(parsed.BucketId) ||
					!byId.TryAdd(parsed.BucketId, parsed))
				{
					return false;
				}
			}

			if (!ExpectedBucketGroups.Any(expected =>
				expected.SetEquals(bucketIds)))
			{
				return false;
			}

			observedBucketGroups.Add(bucketIds);
		}

		if ((byId.Count != ExpectedBuckets.Length) ||
			(observedBucketGroups.Count != ExpectedBucketGroups.Length) ||
			ExpectedBucketGroups.Any(expected =>
				observedBucketGroups.Count(observed =>
					expected.SetEquals(observed)) != 1) ||
			!byId.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
				ExpectedBuckets.Select(expected => expected.BucketId)))
		{
			return false;
		}

		parsedBuckets = Array.AsReadOnly(
			ExpectedBuckets.Select(expected => byId[expected.BucketId]).ToArray());
		return true;
	}

	private static bool TryParseBucket(
		JsonElement bucket,
		out ParsedBucket? parsed)
	{
		parsed = null;

		if ((bucket.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(
				bucket,
				BucketProperties,
				"description",
				"window",
				"disabled",
				"remaining_fraction",
				"remaining_amount",
				"reset_time") ||
			!TryGetRequiredString(bucket, "id", out string? id) ||
			!HasRequiredStringValue(bucket, "name") ||
			!HasAbsentOrStringValue(bucket, "description") ||
			!HasAbsentOrFalseBoolean(bucket, "disabled") ||
			!TryGetAbsentOrRemainingFraction(
				bucket,
				"remaining_fraction",
				out decimal remainingFraction) ||
			!TryGetAbsentOrNonNegativeInteger(
				bucket,
				"remaining_amount",
				out long remainingAmount) ||
			!TryGetOptionalRfc3339(
				bucket,
				"reset_time",
				out DateTimeOffset? resetAt) ||
			(!resetAt.HasValue && (remainingFraction != 1m)))
		{
			return false;
		}

		ExpectedBucket? expected = ExpectedBuckets.SingleOrDefault(
			candidate => string.Equals(
				candidate.BucketId,
				id,
				StringComparison.Ordinal));

		if ((expected is null) ||
			!HasAbsentOrExactStringValue(
				bucket,
				"window",
				expected.Window))
		{
			return false;
		}

		parsed = new ParsedBucket(
			id!,
			remainingFraction,
			remainingAmount,
			resetAt);
		return true;
	}

	private static bool TryGetOptionalRfc3339(
		JsonElement parent,
		string propertyName,
		out DateTimeOffset? parsed)
	{
		parsed = null;

		if (!parent.TryGetProperty(propertyName, out JsonElement element))
		{
			return true;
		}

		if ((element.ValueKind != JsonValueKind.String) ||
			!TryParseRfc3339(element.GetString()!, out DateTimeOffset value))
		{
			return false;
		}

		parsed = value;
		return true;
	}

	private static bool TryParseRfc3339(
		string value,
		out DateTimeOffset parsed)
	{
		return DateTimeOffset.TryParseExact(
			value,
			Rfc3339Formats,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal |
			DateTimeStyles.AdjustToUniversal,
			out parsed);
	}

	private static bool TryValidateZeroUsage(JsonElement result)
	{
		if (!result.TryGetProperty("usage", out JsonElement usage) ||
			(usage.ValueKind != JsonValueKind.Object) ||
			!HasExactProperties(usage, UsageProperties))
		{
			return false;
		}

		return RequiredUsageCounters.All(counter =>
			TryGetExactZeroInteger(usage, counter));
	}

	private static bool TryGetExactZeroInteger(
		JsonElement parent,
		string propertyName)
	{
		return parent.TryGetProperty(propertyName, out JsonElement value) &&
			(value.ValueKind == JsonValueKind.Number) &&
			value.TryGetInt64(out long integer) &&
			(integer == 0);
	}

	private static bool TryGetNonNegativeFiniteNumber(
		JsonElement parent,
		string propertyName)
	{
		return parent.TryGetProperty(propertyName, out JsonElement value) &&
			(value.ValueKind == JsonValueKind.Number) &&
			value.TryGetDouble(out double number) &&
			double.IsFinite(number) &&
			(number >= 0d);
	}

	private static bool TryValidateAbsentOrNullError(JsonElement result)
	{
		return !result.TryGetProperty("error", out JsonElement error) ||
			(error.ValueKind == JsonValueKind.Null);
	}

	private static bool TryGetRequiredString(
		JsonElement parent,
		string propertyName,
		out string? value)
	{
		value = null;

		if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
			(element.ValueKind != JsonValueKind.String))
		{
			return false;
		}

		value = element.GetString();
		return value is not null;
	}

	private static bool HasRequiredStringValue(
		JsonElement parent,
		string propertyName)
	{
		return parent.TryGetProperty(propertyName, out JsonElement element) &&
			(element.ValueKind == JsonValueKind.String);
	}

	private static bool HasAbsentOrStringValue(
		JsonElement parent,
		string propertyName)
	{
		return !parent.TryGetProperty(propertyName, out JsonElement element) ||
			(element.ValueKind == JsonValueKind.String);
	}

	private static bool HasAbsentOrExactStringValue(
		JsonElement parent,
		string propertyName,
		string expected)
	{
		return !parent.TryGetProperty(propertyName, out JsonElement element) ||
			((element.ValueKind == JsonValueKind.String) &&
			 string.Equals(
				element.GetString(),
				expected,
				StringComparison.Ordinal));
	}

	private static bool HasAbsentOrFalseBoolean(
		JsonElement parent,
		string propertyName)
	{
		return !parent.TryGetProperty(propertyName, out JsonElement element) ||
			(element.ValueKind == JsonValueKind.False);
	}

	private static bool TryGetAbsentOrNonNegativeInteger(
		JsonElement parent,
		string propertyName,
		out long parsed)
	{
		parsed = 0;

		if (!parent.TryGetProperty(propertyName, out JsonElement element))
		{
			return true;
		}

		if ((element.ValueKind != JsonValueKind.Number) ||
			!element.TryGetInt64(out long value) ||
			(value < 0))
		{
			return false;
		}

		parsed = value;
		return true;
	}

	private static bool TryGetAbsentOrRemainingFraction(
		JsonElement parent,
		string propertyName,
		out decimal parsed)
	{
		parsed = 0m;

		if (!parent.TryGetProperty(propertyName, out JsonElement element))
		{
			return true;
		}

		return (element.ValueKind == JsonValueKind.Number) &&
			element.TryGetDecimal(out parsed) &&
			(parsed >= 0m) &&
			(parsed <= 1m);
	}

	private static bool HasExplicitSafetyViolation(JsonElement resultRoot)
	{
		if ((resultRoot.ValueKind != JsonValueKind.Object) ||
			!resultRoot.TryGetProperty("result", out JsonElement result) ||
			(result.ValueKind != JsonValueKind.Object))
		{
			return false;
		}

		if (result.TryGetProperty(
			"conversation_id",
			out JsonElement conversationId) &&
			(conversationId.ValueKind == JsonValueKind.String) &&
			!string.IsNullOrEmpty(conversationId.GetString()))
		{
			return true;
		}

		if (HasExplicitNonZeroNumber(result, "num_turns"))
		{
			return true;
		}

		if (!result.TryGetProperty("usage", out JsonElement usage) ||
			(usage.ValueKind != JsonValueKind.Object))
		{
			return HasUnknownNonZeroSafetyProperty(result);
		}

		return usage.EnumerateObject().Any(property =>
				HasConservativeNonZeroNumber(property.Value)) ||
			HasUnknownNonZeroSafetyProperty(result);
	}

	private static bool HasExplicitNonZeroNumber(
		JsonElement parent,
		string propertyName)
	{
		return parent.TryGetProperty(propertyName, out JsonElement value) &&
			HasConservativeNonZeroNumber(value);
	}

	private static bool HasUnknownNonZeroSafetyProperty(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				bool isSafetyProperty =
					property.Name.Contains(
						"turn",
						StringComparison.OrdinalIgnoreCase) ||
					property.Name.Contains(
						"token",
						StringComparison.OrdinalIgnoreCase) ||
					property.Name.Contains(
						"cost",
						StringComparison.OrdinalIgnoreCase);

				if ((isSafetyProperty &&
					 HasConservativeNonZeroNumber(property.Value)) ||
					HasUnknownNonZeroSafetyProperty(property.Value))
				{
					return true;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				if (HasUnknownNonZeroSafetyProperty(item))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static bool HasConservativeNonZeroNumber(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Number)
		{
			return false;
		}

		if (value.TryGetDecimal(out decimal decimalNumber))
		{
			return decimalNumber != 0m;
		}

		if (value.TryGetDouble(out double doubleNumber))
		{
			return !double.IsFinite(doubleNumber) || (doubleNumber != 0d);
		}

		// A numeric literal that cannot be represented by either supported
		// numeric type is conservatively treated as a safety violation.
		return true;
	}

	private static bool HasExactProperties(
		JsonElement element,
		IReadOnlySet<string> required,
		params string[] optional)
	{
		HashSet<string> allowed = new(required, StringComparer.Ordinal);
		allowed.UnionWith(optional);
		HashSet<string> observed = element
			.EnumerateObject()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);
		return required.All(observed.Contains) &&
			observed.IsSubsetOf(allowed);
	}

	private static bool HasUniquePropertiesRecursively(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> names = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!names.Add(property.Name) ||
					!HasUniquePropertiesRecursively(property.Value))
				{
					return false;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				if (!HasUniquePropertiesRecursively(item))
				{
					return false;
				}
			}
		}

		return true;
	}

	private static AntigravityOfficialPrintParseResult
		UnverifiableResponseFailure(
			AntigravityUsageSafetyFailureReason reason =
				AntigravityUsageSafetyFailureReason.UnverifiableOutput)
	{
		// The caller only parses output after the command has run. An unknown,
		// duplicate, malformed, or truncated envelope cannot prove that every
		// turn/token counter was present, numeric, and exactly zero.
		return new AntigravityOfficialPrintParseResult(
			AntigravityOfficialPrintUsageClient.Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				reason),
			RequiresSafetyLatch: true,
			reason);
	}

	private static AntigravityOfficialPrintParseResult SafetyFailure()
	{
		return new AntigravityOfficialPrintParseResult(
			AntigravityOfficialPrintUsageClient.Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				AntigravityUsageSafetyFailureReason.UsageActivityDetected),
			RequiresSafetyLatch: true,
			AntigravityUsageSafetyFailureReason.UsageActivityDetected);
	}
}
