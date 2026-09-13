using System.Diagnostics;
using System.Text.Json.Nodes;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeCliUsageCompatibilityTests
{
	private sealed class FixedTimeProvider : TimeProvider
	{
		public override DateTimeOffset GetUtcNow()
		{
			return ObservedAt;
		}
	}

	private const string AuthStatusJson = """
		{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"claude@example.com","orgId":"org-test","orgName":"Example Organization","subscriptionType":"max"}
		""";
	private const string UsageText =
		"You are currently using your subscription to power your Claude Code usage\n\n" +
		"Current session: 12% used · resets Sep 11, 2pm (Asia/Taipei)\n" +
		"Current week (all models): 34% used · resets Sep 12, 1am (Asia/Taipei)";
	// 由 Claude Code 2.1.268 的 /usage 實際回應去識別而來。
	private const string Claude21268ResultJson = """
		{
		  "is_error": false,
		  "duration_api_ms": 0,
		  "num_turns": 0,
		  "stop_reason": null,
		  "session_id": "session-test",
		  "total_cost_usd": 0,
		  "usage": {
		    "output_tokens_details": {
		      "thinking_tokens": 0
		    },
		    "input_tokens": 0,
		    "cache_creation_input_tokens": 0,
		    "cache_read_input_tokens": 0,
		    "output_tokens": 0,
		    "server_tool_use": {
		      "web_search_requests": 0,
		      "web_fetch_requests": 0
		    },
		    "service_tier": "standard",
		    "cache_creation": {
		      "ephemeral_1h_input_tokens": 0,
		      "ephemeral_5m_input_tokens": 0
		    },
		    "inference_geo": "",
		    "iterations": [],
		    "speed": "standard"
		  },
		  "modelUsage": {},
		  "permission_denials": [],
		  "fast_mode_state": "off",
		  "fast_mode_disabled_reason": "sdk_opt_in_required",
		  "subagent_stats": {
		    "spawned": 0,
		    "requested": {
		      "background": 0,
		      "foreground": 0,
		      "unset": 0
		    },
		    "started_in_background": 0,
		    "max_depth": 0,
		    "spawned_by_subagents": 0,
		    "completed": 0,
		    "failed": 0,
		    "killed": {
		      "parent": 0,
		      "user": 0,
		      "system": 0
		    },
		    "refused": {
		      "depth_limit": 0,
		      "concurrency_limit": 0,
		      "budget": 0
		    },
		    "by_type": {}
		  },
		  "subtype": "success",
		  "result": "You are currently using your subscription to power your Claude Code usage\n\nCurrent session: 12% used · resets Sep 11, 2pm (Asia/Taipei)\nCurrent week (all models): 34% used · resets Sep 12, 1am (Asia/Taipei)",
		  "local_command": "usage",
		  "type": "result",
		  "duration_ms": 310,
		  "uuid": "01234567-89ab-cdef-0123-456789abcdef",
		  "queued_turn_count": 0,
		  "result_index": 0
		}
		""";
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 9, 11, 2, 30, 0, TimeSpan.Zero);

	[Fact]
	public void ParseSafeResult_WithClaude21268Envelope_ReturnsUsage()
	{
		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			Claude21268ResultJson,
			ObservedAt);

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(ObservedAt, result.ObservedAt);
	}

	[Theory]
	[InlineData("local_command")]
	[InlineData("result_index")]
	public void ParseSafeResult_WithOneCommandMetadataFieldAbsent_ReturnsUsage(
		string absentProperty)
	{
		JsonObject root = CreateResult();
		Assert.True(root.Remove(absentProperty));

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			root.ToJsonString(),
			ObservedAt);

		Assert.Equal(UsageText, result.Output);
	}

	[Fact]
	public void ParseSafeResult_WithoutCommandMetadata_RemainsCompatible()
	{
		JsonObject root = CreateResult();
		Assert.True(root.Remove("local_command"));
		Assert.True(root.Remove("result_index"));

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			root.ToJsonString(),
			ObservedAt);

		Assert.Equal(UsageText, result.Output);
	}

	[Theory]
	[InlineData("local_command", "\"status\"")]
	[InlineData("local_command", "\"/usage\"")]
	[InlineData("local_command", "\"Usage\"")]
	[InlineData("local_command", "\"\"")]
	[InlineData("local_command", "null")]
	[InlineData("local_command", "0")]
	[InlineData("local_command", "false")]
	[InlineData("local_command", "{}")]
	[InlineData("local_command", "[]")]
	[InlineData("result_index", "1")]
	[InlineData("result_index", "-1")]
	[InlineData("result_index", "0.5")]
	[InlineData("result_index", "1e-400")]
	[InlineData("result_index", "\"0\"")]
	[InlineData("result_index", "null")]
	[InlineData("result_index", "false")]
	[InlineData("result_index", "{}")]
	[InlineData("result_index", "[]")]
	public void ParseSafeResult_WithInvalidCommandMetadata_RejectsResult(
		string propertyName,
		string invalidValueJson)
	{
		JsonObject root = CreateResult();
		root[propertyName] = JsonNode.Parse(invalidValueJson);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(root.ToJsonString(), ObservedAt));
	}

	[Theory]
	[InlineData("input_tokens")]
	[InlineData("output_tokens")]
	[InlineData("cache_creation_input_tokens")]
	[InlineData("cache_read_input_tokens")]
	public void ParseSafeResult_WithNewMetadataAndNonZeroTokens_RejectsResult(
		string tokenPropertyName)
	{
		JsonObject root = CreateResult();
		JsonObject usage = root["usage"]?.AsObject() ??
			throw new InvalidOperationException(
				"Claude 2.1.268 compatibility fixture must contain usage.");
		usage[tokenPropertyName] = 1;

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(root.ToJsonString(), ObservedAt));
	}

	[Fact]
	public void ParseSafeResult_WithNewMetadataAndNonZeroCost_RejectsResult()
	{
		JsonObject root = CreateResult();
		root["total_cost_usd"] = 0.01m;

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(root.ToJsonString(), ObservedAt));
	}

	[Theory]
	[InlineData("2.1.268", true)]
	[InlineData("2.1.267", false)]
	[InlineData("2.1.169", false)]
	public async Task PollAsync_WithNewOrLegacyEnvelope_ReturnsUsage(
		string version,
		bool includesCommandMetadata)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonObject root = CreateResult();

		if (!includesCommandMetadata)
		{
			Assert.True(root.Remove("local_command"));
			Assert.True(root.Remove("result_index"));
		}

		List<ProcessStartInfo> capturedStartInfos = new();
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				capturedStartInfos.Add(startInfo);
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => $"{version} (Claude Code)",
					"auth" => AuthStatusJson,
					"-p" => root.ToJsonString(),
					_ => throw new InvalidOperationException(
						$"Unexpected Claude compatibility probe: {startInfo.ArgumentList[0]}.")
				};

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			new FixedTimeProvider());

		ClaudeUsagePollResult result = await poller.PollAsync(Guid.NewGuid());

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(ObservedAt, result.ObservedAt);
		Assert.Equal("claude@example.com", result.AccountIdentity);
		Assert.Equal(version, result.VersionEvidence?.DetectedVersion);
		ClaudeUsageTextParser.Result quotas = ClaudeUsageTextParser.Parse(
			result.Output,
			result.ObservedAt);
		Assert.Equal(2, quotas.Metrics.Count);
		Assert.False(quotas.IsStale);
		Assert.Equal(3, capturedStartInfos.Count);
		Assert.Equal(new[] { "--version" }, capturedStartInfos[0].ArgumentList);
		Assert.Equal(
			new[] { "auth", "status", "--json" },
			capturedStartInfos[1].ArgumentList);
		Assert.Equal("/usage", capturedStartInfos[2].ArgumentList[1]);
	}

	private static JsonObject CreateResult()
	{
		return JsonNode.Parse(Claude21268ResultJson)?.AsObject() ??
			throw new InvalidOperationException(
				"Claude 2.1.268 compatibility fixture must be a JSON object.");
	}
}
