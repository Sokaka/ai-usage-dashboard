using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityOfficialPrintEnvelopeParserTests
{
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Parse_WithObservedOfficialShape_ReturnsFourWindows()
	{
		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput());

		Assert.False(parsed.RequiresSafetyLatch);
		Assert.True(parsed.Result.IsSuccessful);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.None,
			parsed.SafetyFailureReason);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.None,
			parsed.Result.SafetyFailureReason);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			parsed.Result.AccountIdentity);
		Assert.Equal(
			new[]
			{
				"agy.gemini.weekly",
				"agy.gemini.rolling-5h",
				"agy.claude.weekly",
				"agy.claude.rolling-5h"
			},
			parsed.Result.Windows.Select(window => window.StableWindowId));
		Assert.Equal(
			new[] { 80m, 60m, 40m, 20m },
			parsed.Result.Windows.Select(window => window.RemainingPercent));
		Assert.All(parsed.Result.Windows, window =>
		{
			Assert.False(window.IsAvailable);
			Assert.Equal(TimeSpan.FromDays(1), window.ResetsIn);
		});
	}

	[Fact]
	public void Parse_WithObserved112CommandWithoutRawProperty_Succeeds()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject resultRoot = CreateResultRoot();

		Assert.Null(commandRoot["command"]!["raw"]);
		Assert.Null(resultRoot["result"]!["command"]!["raw"]);

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void Parse_WithArgonGroupAndReorderedCopies_ReturnsOriginalFourWindows(
		bool reverseGroups,
		bool reverseBuckets)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject resultRoot = CreateMatchingResultRoot(commandRoot);
		JsonObject resultData = resultRoot["result"]!["command"]!["data"]!.AsObject();
		JsonArray groups = resultData["groups"]!.AsArray();

		if (reverseBuckets)
		{
			foreach (JsonNode? group in groups)
			{
				group!["buckets"] = new JsonArray(group["buckets"]!.AsArray()
					.Reverse().Select(bucket => bucket!.DeepClone()).ToArray());
			}
		}

		if (reverseGroups)
		{
			resultData["groups"] = new JsonArray(groups.Reverse()
				.Select(group => group!.DeepClone()).ToArray());
		}

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
		Assert.Equal(
			new[]
			{
				"agy.gemini.weekly",
				"agy.gemini.rolling-5h",
				"agy.claude.weekly",
				"agy.claude.rolling-5h"
			},
			parsed.Result.Windows.Select(window => window.StableWindowId));
		Assert.Equal(
			new[] { 80m, 60m, 40m, 20m },
			parsed.Result.Windows.Select(window => window.RemainingPercent));
		Assert.All(parsed.Result.Windows, window =>
		{
			Assert.False(window.IsAvailable);
			Assert.Equal(TimeSpan.FromDays(1), window.ResetsIn);
		});
	}

	[Theory]
	[InlineData("presentation-and-window")]
	[InlineData("zero-quota")]
	[InlineData("full-quota-without-reset")]
	public void Parse_WithArgonOptionalFieldsOmitted_Succeeds(string mutation)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject group = commandRoot["command"]!["data"]!["groups"]![2]!.AsObject();
		JsonObject bucket = group["buckets"]![0]!.AsObject();

		switch (mutation)
		{
			case "presentation-and-window":
				group.Remove("description");
				bucket.Remove("description");
				bucket.Remove("window");
				bucket.Remove("disabled");
				bucket.Remove("remaining_amount");
				break;
			case "zero-quota":
				bucket.Remove("remaining_fraction");
				bucket.Remove("remaining_amount");
				break;
			case "full-quota-without-reset":
				bucket["remaining_fraction"] = 1m;
				bucket.Remove("reset_time");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutation));
		}

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			CreateMatchingResultRoot(commandRoot)));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
		Assert.Equal(4, parsed.Result.Windows.Count);
	}

	[Fact]
	public void Parse_WithArgonOmittedAndExplicitZeroQuotaFields_CopiesMatch()
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject commandBucket = commandRoot["command"]!["data"]!["groups"]![2]![
			"buckets"]![0]!.AsObject();
		commandBucket.Remove("remaining_fraction");
		commandBucket.Remove("remaining_amount");
		JsonObject resultRoot = CreateMatchingResultRoot(commandRoot);
		JsonObject resultBucket = resultRoot["result"]!["command"]!["data"]![
			"groups"]![2]!["buckets"]![0]!.AsObject();
		resultBucket["remaining_fraction"] = 0m;
		resultBucket["remaining_amount"] = 0L;

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Theory]
	[InlineData("unknown-bucket")]
	[InlineData("duplicate-bucket")]
	[InlineData("duplicate-group")]
	[InlineData("extra-group")]
	[InlineData("argon-in-gemini-group")]
	[InlineData("mixed-groups")]
	[InlineData("missing-gemini-group")]
	[InlineData("missing-third-party-group")]
	[InlineData("missing-required-bucket")]
	[InlineData("empty-argon-group")]
	public void Parse_WithInvalidArgonGroupMembership_Latches(string mutation)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonArray groups = commandRoot["command"]!["data"]!["groups"]!.AsArray();
		JsonArray geminiBuckets = groups[0]!["buckets"]!.AsArray();
		JsonArray argonBuckets = groups[2]!["buckets"]!.AsArray();

		switch (mutation)
		{
			case "unknown-bucket":
				argonBuckets[0]!["id"] = "gemini-future-weekly";
				break;
			case "duplicate-bucket":
				argonBuckets.Add(argonBuckets[0]!.DeepClone());
				break;
			case "duplicate-group":
				groups.Add(groups[2]!.DeepClone());
				break;
			case "extra-group":
				groups.Add(CreateGroup(
					"Unknown group",
					("unknown-weekly", "Weekly", "weekly", 0.5)));
				break;
			case "argon-in-gemini-group":
				geminiBuckets.Add(argonBuckets[0]!.DeepClone());
				groups.RemoveAt(2);
				break;
			case "mixed-groups":
				JsonNode geminiBucket = geminiBuckets[0]!.DeepClone();
				geminiBuckets[0] = argonBuckets[0]!.DeepClone();
				argonBuckets[0] = geminiBucket;
				break;
			case "missing-gemini-group":
				groups.RemoveAt(0);
				break;
			case "missing-third-party-group":
				groups.RemoveAt(1);
				break;
			case "missing-required-bucket":
				geminiBuckets.RemoveAt(0);
				break;
			case "empty-argon-group":
				argonBuckets.Clear();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutation));
		}

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, CreateMatchingResultRoot(commandRoot))),
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid);
	}

	[Theory]
	[InlineData("group", "description", "1")]
	[InlineData("bucket", "description", "false")]
	[InlineData("bucket", "disabled", "\"false\"")]
	[InlineData("bucket", "disabled", "true")]
	[InlineData("bucket", "window", "\"5h\"")]
	[InlineData("bucket", "remaining_fraction", "-0.01")]
	[InlineData("bucket", "remaining_fraction", "1.01")]
	[InlineData("bucket", "remaining_fraction", "\"0.9\"")]
	[InlineData("bucket", "remaining_amount", "-1")]
	[InlineData("bucket", "remaining_amount", "1.5")]
	[InlineData("bucket", "remaining_amount", "\"123\"")]
	[InlineData("bucket", "reset_time", "\"not-an-rfc3339-time\"")]
	[InlineData("bucket", "reset_time", "null")]
	[InlineData("bucket", "reset_time", null)]
	[InlineData("bucket", "unknown", "true")]
	[InlineData("group", "unknown", "true")]
	public void Parse_WithInvalidArgonField_Latches(
		string location,
		string propertyName,
		string? jsonValue)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject group = commandRoot["command"]!["data"]!["groups"]![2]!.AsObject();
		JsonObject target = location switch
		{
			"group" => group,
			"bucket" => group["buckets"]![0]!.AsObject(),
			_ => throw new ArgumentOutOfRangeException(nameof(location))
		};

		if (jsonValue is null)
		{
			target.Remove(propertyName);
		}
		else
		{
			target[propertyName] = JsonNode.Parse(jsonValue);
		}

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, CreateMatchingResultRoot(commandRoot))),
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid);
	}

	[Fact]
	public void Parse_WithArgonDuplicateJsonProperty_LatchesAsUnverifiable()
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		string output = CreateOutput(commandRoot, CreateMatchingResultRoot(commandRoot));
		string duplicate = output.Replace(
			"\"id\":\"gemini-argon-wtu-weekly\"",
			"\"id\":\"gemini-argon-wtu-weekly\",\"id\":\"gemini-argon-wtu-weekly\"",
			StringComparison.Ordinal);

		Assert.NotEqual(output, duplicate);
		AssertUnverifiableResponseLatched(
			Parse(duplicate),
			AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Parse_WithArgonInOnlyOneCopy_LatchesAsCopiesDiffer(
		bool argonInFirstCommand)
	{
		JsonObject commandRoot = argonInFirstCommand
			? CreateArgonCommandRoot()
			: CreateCommandRoot();
		JsonObject resultCommandRoot = argonInFirstCommand
			? CreateCommandRoot()
			: CreateArgonCommandRoot();

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, CreateMatchingResultRoot(resultCommandRoot))),
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer);
	}

	[Theory]
	[InlineData("remaining_fraction", "0.75")]
	[InlineData("remaining_amount", "124")]
	[InlineData("reset_time", "\"2026-08-14T00:00:00Z\"")]
	public void Parse_WithArgonCopiesDifferent_LatchesAsCopiesDiffer(
		string propertyName,
		string jsonValue)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject resultRoot = CreateMatchingResultRoot(commandRoot);
		JsonObject bucket = resultRoot["result"]!["command"]!["data"]![
			"groups"]![2]!["buckets"]![0]!.AsObject();
		bucket[propertyName] = JsonNode.Parse(jsonValue);

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Parse_WithArgonAndNonZeroToken_LatchesBeforeShapeCheck(
		bool tokenInsideArgon)
	{
		JsonObject commandRoot = CreateArgonCommandRoot();
		JsonObject resultRoot = CreateMatchingResultRoot(commandRoot);
		JsonObject resultBucket = resultRoot["result"]!["command"]!["data"]![
			"groups"]![2]!["buckets"]![0]!.AsObject();
		resultBucket["disabled"] = true;

		if (tokenInsideArgon)
		{
			resultBucket["model_tokens"] = 1;
		}
		else
		{
			resultRoot["result"]!["usage"]!["total_tokens"] = 1;
		}

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.False(parsed.Result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			parsed.SafetyFailureReason);
		Assert.Equal(parsed.SafetyFailureReason, parsed.Result.SafetyFailureReason);
	}

	[Fact]
	public void Parse_WithKnown112OptionalQuotaFields_Succeeds()
	{
		JsonObject commandRoot = CreateCommandRoot();
		foreach (JsonNode? group in
			commandRoot["command"]!["data"]!["groups"]!.AsArray())
		{
			foreach (JsonNode? bucket in group!["buckets"]!.AsArray())
			{
				bucket!["description"] = "Server-provided presentation text";
				bucket["disabled"] = false;
				bucket["remaining_amount"] = 123L;
			}
		}

		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Fact]
	public void Parse_WithOptionalDescriptionsAndWindowsAbsent_Succeeds()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject data = commandRoot["command"]!["data"]!.AsObject();
		data.Remove("description");

		foreach (JsonNode? group in data["groups"]!.AsArray())
		{
			group!.AsObject().Remove("description");

			foreach (JsonNode? bucket in group["buckets"]!.AsArray())
			{
				bucket!.AsObject().Remove("window");
			}
		}

		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Fact]
	public void Parse_WithFullBucketAndNoReset_ReportsAvailableWindow()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject bucket = commandRoot["command"]!["data"]!["groups"]![0]![
			"buckets"]![0]!.AsObject();
		bucket["remaining_fraction"] = 1m;
		bucket.Remove("reset_time");
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		AntigravityProductionUsageWindow window = parsed.Result.Windows[0];
		Assert.True(window.IsAvailable);
		Assert.Null(window.ResetsIn);
		Assert.Equal(100m, window.RemainingPercent);
	}

	[Fact]
	public void Parse_WithOmittedZeroRemainingFraction_ReportsExhaustedWindow()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject bucket = commandRoot["command"]!["data"]!["groups"]![0]![
			"buckets"]![0]!.AsObject();
		bucket.Remove("remaining_fraction");
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		AntigravityProductionUsageWindow window = parsed.Result.Windows[0];
		Assert.False(window.IsAvailable);
		Assert.Equal(TimeSpan.FromDays(1), window.ResetsIn);
		Assert.Equal(0m, window.RemainingPercent);
	}

	[Fact]
	public void Parse_WithChangedPresentationNames_Succeeds()
	{
		JsonObject commandRoot = CreateCommandRoot();
		commandRoot["command"]!["data"]!["groups"]![0]!["name"] =
			"Localized Gemini group";
		commandRoot["command"]!["data"]!["groups"]![0]!["buckets"]![0]![
			"name"] = "Localized weekly label";
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Fact]
	public void Parse_WithDuplicatePresentationGroupNames_Succeeds()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonArray groups = commandRoot["command"]!["data"]!["groups"]!.AsArray();
		groups[1]!["name"] = groups[0]!["name"]!.GetValue<string>();
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Fact]
	public void Parse_WithOmittedAndExplicitZeroQuotaFields_CopiesMatch()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject commandBucket = commandRoot["command"]!["data"]!["groups"]![0]![
			"buckets"]![0]!.AsObject();
		commandBucket.Remove("remaining_fraction");
		commandBucket.Remove("remaining_amount");
		JsonObject resultRoot = CreateResultRoot();
		JsonObject resultBucket = resultRoot["result"]!["command"]!["data"]![
			"groups"]![0]!["buckets"]![0]!.AsObject();
		resultBucket["remaining_fraction"] = 0m;
		resultBucket["remaining_amount"] = 0L;

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			commandRoot,
			resultRoot));

		Assert.True(parsed.Result.IsSuccessful);
		Assert.False(parsed.RequiresSafetyLatch);
	}

	[Theory]
	[InlineData("data-description")]
	[InlineData("group-description")]
	[InlineData("bucket-description")]
	[InlineData("disabled-string")]
	[InlineData("disabled-true")]
	[InlineData("remaining-amount-string")]
	[InlineData("remaining-amount-negative")]
	[InlineData("remaining-amount-fractional")]
	[InlineData("window-mismatch")]
	[InlineData("missing-reset-with-used-quota")]
	public void Parse_WithInvalidKnown112OptionalField_Latches(
		string mutation)
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject data = commandRoot["command"]!["data"]!.AsObject();
		JsonObject group = data["groups"]![0]!.AsObject();
		JsonObject bucket = group["buckets"]![0]!.AsObject();

		switch (mutation)
		{
			case "data-description":
				data["description"] = 1;
				break;
			case "group-description":
				group["description"] = false;
				break;
			case "bucket-description":
				bucket["description"] = new JsonObject();
				break;
			case "disabled-string":
				bucket["disabled"] = "false";
				break;
			case "disabled-true":
				bucket["disabled"] = true;
				break;
			case "remaining-amount-string":
				bucket["remaining_amount"] = "123";
				break;
			case "remaining-amount-negative":
				bucket["remaining_amount"] = -1;
				break;
			case "remaining-amount-fractional":
				bucket["remaining_amount"] = 1.5;
				break;
			case "window-mismatch":
				bucket["window"] = "5h";
				break;
			case "missing-reset-with-used-quota":
				bucket.Remove("reset_time");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutation));
		}

		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid);
	}

	[Fact]
	public void Parse_WithDifferentRemainingAmounts_LatchesAsCopiesDiffer()
	{
		JsonObject commandRoot = CreateCommandRoot();
		commandRoot["command"]!["data"]!["groups"]![0]!["buckets"]![0]![
			"remaining_amount"] = 123L;
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"]!["data"]!["groups"]![0]![
			"buckets"]![0]!["remaining_amount"] = 124L;

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer);
	}

	[Theory]
	[InlineData("num_turns")]
	[InlineData("input_tokens")]
	[InlineData("output_tokens")]
	[InlineData("thinking_tokens")]
	[InlineData("cache_read_tokens")]
	[InlineData("total_tokens")]
	public void Parse_WithExplicitNonZeroSafetyCounter_Latches(
		string propertyName)
	{
		JsonObject result = CreateResultRoot()["result"]!.AsObject();

		if (propertyName == "num_turns")
		{
			result[propertyName] = 1;
		}
		else
		{
			result["usage"]![propertyName] = 1;
		}

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			resultRoot: new JsonObject
			{
				["event"] = "result",
				["result"] = result.DeepClone()
			}));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.False(parsed.Result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			parsed.SafetyFailureReason);
		Assert.Equal(
			parsed.SafetyFailureReason,
			parsed.Result.SafetyFailureReason);
	}

	[Fact]
	public void Parse_WithNonEmptyConversationId_Latches()
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["conversation_id"] = "unexpected-turn";

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			resultRoot: resultRoot));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			parsed.SafetyFailureReason);
	}

	[Fact]
	public void Parse_WithDuplicateProperty_LatchesAsUnverifiable()
	{
		string output = CreateOutput();
		string duplicate = output.Replace(
			"\"event\":\"command_result\"",
			"\"event\":\"command_result\",\"event\":\"command_result\"",
			StringComparison.Ordinal);

		AssertUnverifiableResponseLatched(
			Parse(duplicate),
			AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties);
	}

	[Fact]
	public void Parse_WithDuplicateTopLevelResult_LatchesAsUnverifiable()
	{
		string result = CreateResultRoot()["result"]!.ToJsonString();
		string output = string.Join(
			'\n',
			CreateCommandRoot().ToJsonString(),
			$"{{\"event\":\"result\",\"result\":{result},\"result\":{result}}}") +
			"\n";

		AssertUnverifiableResponseLatched(
			Parse(output),
			AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties);
	}

	[Theory]
	[InlineData("num_turns")]
	[InlineData("total_tokens")]
	public void Parse_WithStringSafetyCounter_LatchesAsUnverifiable(
		string propertyName)
	{
		JsonObject resultRoot = CreateResultRoot();

		if (propertyName == "num_turns")
		{
			resultRoot["result"]![propertyName] = "0";
		}
		else
		{
			resultRoot["result"]!["usage"]![propertyName] = "0";
		}

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(resultRoot: resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid);
	}

	[Fact]
	public void Parse_WithTopLevelSafetyCounter_LatchesAsUnknownSchema()
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["num_turns"] = 0;

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(resultRoot: resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid);
	}

	[Theory]
	[InlineData("event")]
	[InlineData("result")]
	[InlineData("usage")]
	[InlineData("command")]
	[InlineData("data")]
	[InlineData("group")]
	[InlineData("bucket")]
	public void Parse_WithUnknownProperty_LatchesAsUnverifiable(string location)
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject resultRoot = CreateResultRoot();
		AntigravityUsageSafetyFailureReason expectedReason;

		switch (location)
		{
			case "event":
				commandRoot["unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputCommandEnvelopeInvalid;
				break;
			case "result":
				resultRoot["result"]!["unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputResultEnvelopeInvalid;
				break;
			case "usage":
				resultRoot["result"]!["usage"]!["unknown"] = 0;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputResultEnvelopeInvalid;
				break;
			case "command":
				commandRoot["command"]!["unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputCommandEnvelopeInvalid;
				break;
			case "data":
				commandRoot["command"]!["data"]!["unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputCommandEnvelopeInvalid;
				break;
			case "group":
				commandRoot["command"]!["data"]!["groups"]![0]!["unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputCommandEnvelopeInvalid;
				break;
			case "bucket":
				commandRoot["command"]!["data"]!["groups"]![0]!["buckets"]![0]![
					"unknown"] = true;
				expectedReason = AntigravityUsageSafetyFailureReason
					.OutputCommandEnvelopeInvalid;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(location));
		}

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			expectedReason);
	}

	[Fact]
	public void Parse_WithUnknownNonZeroTokenField_LatchesBeforeShapeCheck()
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["model_tokens"] = 1;

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			resultRoot: resultRoot));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			parsed.SafetyFailureReason);
		Assert.Equal(
			parsed.SafetyFailureReason,
			parsed.Result.SafetyFailureReason);
	}

	[Theory]
	[InlineData("nested_cost", 0.01)]
	[InlineData("model_turn_count", 1)]
	public void Parse_WithNestedUnknownSafetyField_LatchesBeforeShapeCheck(
		string propertyName,
		double value)
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["metadata"] = new JsonObject
		{
			["nested"] = new JsonArray(
				new JsonObject { [propertyName] = value })
		};

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			resultRoot: resultRoot));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			parsed.SafetyFailureReason);
		Assert.Equal(
			parsed.SafetyFailureReason,
			parsed.Result.SafetyFailureReason);
	}

	[Fact]
	public void Parse_WithOverflowingTokenNumber_LatchesConservatively()
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["usage"]!["total_tokens"] =
			JsonNode.Parse("1e10000");

		AntigravityOfficialPrintParseResult parsed = Parse(CreateOutput(
			resultRoot: resultRoot));

		Assert.True(parsed.RequiresSafetyLatch);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
	}

	[Theory]
	[InlineData("gemini-unknown")]
	[InlineData("gemini-weekly")]
	public void Parse_WithUnknownOrDuplicateBucket_LatchesAsUnverifiable(
		string replacementId)
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonArray buckets = commandRoot["command"]!["data"]!["groups"]![0]![
			"buckets"]!.AsArray();
		buckets[1]!["id"] = replacementId;
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid);
	}

	[Theory]
	[InlineData(-0.01)]
	[InlineData(1.01)]
	public void Parse_WithFractionOutOfRange_LatchesAsUnverifiable(
		double fraction)
	{
		JsonObject commandRoot = CreateCommandRoot();
		commandRoot["command"]!["data"]!["groups"]![0]!["buckets"]![0]![
			"remaining_fraction"] = fraction;
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] =
			commandRoot["command"]!.DeepClone();

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(commandRoot, resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid);
	}

	[Fact]
	public void Parse_WithCommandCopiesDifferent_LatchesAsUnverifiable()
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"]!["data"]!["groups"]![0]![
			"buckets"]![0]!["remaining_fraction"] = 0.79;

		AssertUnverifiableResponseLatched(
			Parse(CreateOutput(resultRoot: resultRoot)),
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer);
	}

	[Theory]
	[InlineData("")]
	[InlineData("{}")]
	[InlineData("{}\n{}\n{}")]
	[InlineData("not-json\n{}")]
	public void Parse_WithMalformedEventSequence_LatchesAsUnverifiable(
		string output)
	{
		AssertUnverifiableResponseLatched(
			Parse(output),
			AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid);
	}

	[Fact]
	public void Parse_WithTwoObjectLinesAndMalformedJson_ReportsJsonDiagnostic()
	{
		AssertUnverifiableResponseLatched(
			Parse("{not-json}\n{}"),
			AntigravityUsageSafetyFailureReason.OutputJsonInvalid);
	}

	[Fact]
	public void Parse_WithInvalidUtf8_LatchesAsUnverifiable()
	{
		AntigravityOfficialPrintParseResult parsed =
			AntigravityOfficialPrintEnvelopeParser.Parse(
				new byte[] { 0xC3, 0x28 },
				ObservedAt);

		AssertUnverifiableResponseLatched(
			parsed,
			AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid);
	}

	[Fact]
	public void Parse_WithTruncatedResultEnvelope_LatchesAsUnverifiable()
	{
		string output = CreateOutput();

		AssertUnverifiableResponseLatched(
			Parse(output[..^8]),
			AntigravityUsageSafetyFailureReason.OutputJsonInvalid);
	}

	private static AntigravityOfficialPrintParseResult Parse(string output)
	{
		return AntigravityOfficialPrintEnvelopeParser.Parse(
			Encoding.UTF8.GetBytes(output),
			ObservedAt);
	}

	private static void AssertUnverifiableResponseLatched(
		AntigravityOfficialPrintParseResult parsed,
		AntigravityUsageSafetyFailureReason expectedReason)
	{
		Assert.True(parsed.RequiresSafetyLatch);
		Assert.False(parsed.Result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			parsed.Result.FailureKind);
		Assert.Equal(
			expectedReason,
			parsed.SafetyFailureReason);
		Assert.Equal(
			parsed.SafetyFailureReason,
			parsed.Result.SafetyFailureReason);
	}

	private static string CreateOutput(
		JsonObject? commandRoot = null,
		JsonObject? resultRoot = null)
	{
		return string.Join(
			'\n',
			(commandRoot ?? CreateCommandRoot()).ToJsonString(),
			(resultRoot ?? CreateResultRoot()).ToJsonString()) + "\n";
	}

	private static JsonObject CreateCommandRoot()
	{
		return new JsonObject
		{
			["event"] = "command_result",
			["command"] = CreateCommand()
		};
	}

	private static JsonObject CreateArgonCommandRoot()
	{
		JsonObject commandRoot = CreateCommandRoot();
		JsonObject argonGroup = CreateGroup(
			"Gemini Argon",
			("gemini-argon-wtu-weekly", "Weekly Limit Remaining", "weekly", 0.9));
		JsonObject bucket = argonGroup["buckets"]![0]!.AsObject();
		bucket["description"] = "Argon quota";
		bucket["disabled"] = false;
		bucket["remaining_amount"] = 123L;
		commandRoot["command"]!["data"]!["groups"]!.AsArray().Add(argonGroup);
		return commandRoot;
	}

	private static JsonObject CreateMatchingResultRoot(JsonObject commandRoot)
	{
		JsonObject resultRoot = CreateResultRoot();
		resultRoot["result"]!["command"] = commandRoot["command"]!.DeepClone();
		return resultRoot;
	}

	private static JsonObject CreateResultRoot()
	{
		return new JsonObject
		{
			["event"] = "result",
			["result"] = new JsonObject
			{
				["conversation_id"] = string.Empty,
				["status"] = "SUCCESS",
				["response"] = "ignored public TSV response",
				["duration_seconds"] = 0.5,
				["num_turns"] = 0,
				["usage"] = new JsonObject
				{
					["input_tokens"] = 0,
					["output_tokens"] = 0,
					["thinking_tokens"] = 0,
					["cache_read_tokens"] = 0,
					["total_tokens"] = 0
				},
				["command"] = CreateCommand()
			}
		};
	}

	private static JsonObject CreateCommand()
	{
		return new JsonObject
		{
			["name"] = "usage",
			["data"] = new JsonObject
			{
				["description"] = "Usage limits",
				["groups"] = new JsonArray
				{
					CreateGroup(
						"Gemini Models",
						("gemini-weekly", "Weekly Limit Remaining", "weekly", 0.8),
						("gemini-5h", "Five Hour Limit Remaining", "5h", 0.6)),
					CreateGroup(
						"Claude and GPT models",
						("3p-weekly", "Weekly Limit Remaining", "weekly", 0.4),
						("3p-5h", "Five Hour Limit Remaining", "5h", 0.2))
				}
			}
		};
	}

	private static JsonObject CreateGroup(
		string groupName,
		params (string Id, string Name, string Window, double Fraction)[] values)
	{
		return new JsonObject
		{
			["name"] = groupName,
			["description"] = "Group",
			["buckets"] = new JsonArray(values.Select(value =>
				(JsonNode)new JsonObject
				{
					["id"] = value.Id,
					["name"] = value.Name,
					["window"] = value.Window,
					["remaining_fraction"] = value.Fraction,
					["reset_time"] = "2026-08-13T00:00:00Z"
				}).ToArray())
		};
	}
}
