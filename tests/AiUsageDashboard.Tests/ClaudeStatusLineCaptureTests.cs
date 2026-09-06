using System.Text.Json;

using AiUsageDashboard.ClaudeCapture;
using AiUsageDashboard.Core.Claude;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeStatusLineCaptureTests
{
	private sealed class FakeTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}
	}

	private static readonly DateTimeOffset CapturedAt =
		new(2026, 7, 15, 6, 30, 0, TimeSpan.Zero);

	[Fact]
	public async Task CaptureInput_WhenLargerThanLimit_StopsReadingAtBoundary()
	{
		byte[] oversizedInput = new byte[Program.MaximumInputBytes * 2];
		using MemoryStream stream = new(oversizedInput, writable: false);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			Program.ReadBoundedInputAsync(stream, CancellationToken.None));

		Assert.InRange(
			stream.Position,
			Program.MaximumInputBytes + 1,
			Program.MaximumInputBytes + 8192);
	}

	[Fact]
	public void Parse_WhenInputIsFull_ExtractsOfficialRateLimitFields()
	{
		ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(
			CreateFullInput(),
			CapturedAt);

		Assert.Equal(ClaudeStatusLineCapture.CurrentSchemaVersion, capture.SchemaVersion);
		Assert.Equal(CapturedAt, capture.CapturedAt);
		Assert.Equal("2.1.80", capture.ClaudeVersion);
		Assert.Equal(
			new ClaudeStatusLineModel("claude-opus-4-8", "Opus"),
			capture.Model);
		Assert.True(capture.HasCurrentUsage);
		Assert.Equal(23.5, capture.FiveHour?.UsedPercentage);
		Assert.Equal(
			DateTimeOffset.FromUnixTimeSeconds(1738425600),
			capture.FiveHour?.ResetsAt);
		Assert.Equal(41.2, capture.SevenDay?.UsedPercentage);
		Assert.Equal(
			DateTimeOffset.FromUnixTimeSeconds(1738857600),
			capture.SevenDay?.ResetsAt);
	}

	[Fact]
	public void Parse_WhenOnlyOneWindowAndNoResetExist_PreservesPartialUsage()
	{
		const string json = """
			{
			  "model": {
			    "display_name": "Sonnet"
			  },
			  "context_window": {
			    "current_usage": null
			  },
			  "rate_limits": {
			    "five_hour": {
			      "used_percentage": 0
			    }
			  }
			}
			""";

		ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(json, CapturedAt);

		Assert.Null(capture.ClaudeVersion);
		Assert.Equal(string.Empty, capture.Model?.Id);
		Assert.Equal("Sonnet", capture.Model?.DisplayName);
		Assert.False(capture.HasCurrentUsage);
		Assert.Equal(0, capture.FiveHour?.UsedPercentage);
		Assert.Null(capture.FiveHour?.ResetsAt);
		Assert.Null(capture.SevenDay);
	}

	[Fact]
	public void Parse_WhenRateLimitsAreMissing_ReturnsAValidEmptyCapture()
	{
		const string json = """
			{
			  "context_window": {
			    "current_usage": null
			  },
			  "unknown_future_field": {
			    "nested": true
			  }
			}
			""";

		ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(json, CapturedAt);

		Assert.Null(capture.ClaudeVersion);
		Assert.Null(capture.Model);
		Assert.False(capture.HasCurrentUsage);
		Assert.Null(capture.FiveHour);
		Assert.Null(capture.SevenDay);
		Assert.Equal(
			"[Claude] waiting for first response",
			ClaudeStatusLineFormatter.Format(capture));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{\"used_percentage\":-0.01}")]
	[InlineData("{\"used_percentage\":100.01}")]
	[InlineData("{\"used_percentage\":\"23.5\"}")]
	[InlineData("{\"used_percentage\":23.5,\"resets_at\":0}")]
	[InlineData("{\"used_percentage\":23.5,\"resets_at\":\"1738425600\"}")]
	[InlineData("{\"used_percentage\":23.5,\"resets_at\":999999999999999999999}")]
	public void Parse_WhenRateLimitWindowIsInvalid_Throws(string windowJson)
	{
		string json = $"{{\"rate_limits\":{{\"five_hour\":{windowJson}}}}}";

		Assert.Throws<JsonException>(() =>
			ClaudeStatusLineParser.Parse(json, CapturedAt));
	}

	[Fact]
	public async Task Store_WriteAndReadAsync_RoundTripsOnlyAllowlistedFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		ClaudeStatusLineCaptureStore store = new(filePath);
		ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(
			CreateFullInput(),
			CapturedAt);

		await store.WriteAsync(capture);
		ClaudeStatusLineCapture? loaded = await store.ReadAsync();

		Assert.Equal(capture, loaded);

		string storedJson = await File.ReadAllTextAsync(filePath);
		using JsonDocument document = JsonDocument.Parse(storedJson);
		Assert.Equal(
			new[]
			{
				"capturedAt",
				"claudeVersion",
				"fiveHour",
				"hasCurrentUsage",
				"model",
				"schemaVersion",
				"sevenDay"
			},
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());

		string normalizedJson = storedJson.ToLowerInvariant();
		Assert.DoesNotContain("cwd", normalizedJson);
		Assert.DoesNotContain("transcript", normalizedJson);
		Assert.DoesNotContain("session_id", normalizedJson);
		Assert.DoesNotContain("total_cost", normalizedJson);
		Assert.DoesNotContain("context_window", normalizedJson);
		Assert.DoesNotContain("input_tokens", normalizedJson);
		Assert.DoesNotContain("sensitive-project", normalizedJson);
		Assert.DoesNotContain("secret-session", normalizedJson);
	}

	[Fact]
	public async Task Store_ReadAsync_WhenWindowOmitsUsedPercentage_RejectsCapture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		await File.WriteAllTextAsync(filePath, $$"""
			{
			  "schemaVersion": 1,
			  "capturedAt": "{{CapturedAt:O}}",
			  "claudeVersion": "2.1.80",
			  "hasCurrentUsage": false,
			  "fiveHour": {
			    "resetsAt": null
			  }
			}
			""");
		ClaudeStatusLineCaptureStore store = new(filePath);

		await Assert.ThrowsAsync<JsonException>(async () => await store.ReadAsync());
	}

	[Fact]
	public async Task Store_ReadAsync_WhenFileExceedsLimit_RejectsBeforeDeserializing()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		await File.WriteAllBytesAsync(filePath, new byte[(1024 * 1024) + 1]);
		ClaudeStatusLineCaptureStore store = new(filePath);

		await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
	}

	[Fact]
	public async Task Store_WriteAsync_WhenOlderCaptureArrivesLater_PreservesNewerCapture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		ClaudeStatusLineCaptureStore store = new(filePath);
		ClaudeStatusLineCapture newerCapture = CreateCapture(
			CapturedAt + TimeSpan.FromMinutes(1),
			80);
		ClaudeStatusLineCapture olderCapture = CreateCapture(CapturedAt, 10);

		await store.WriteAsync(newerCapture);
		await store.WriteAsync(olderCapture);
		ClaudeStatusLineCapture? loaded = await store.ReadAsync();

		Assert.Equal(newerCapture, loaded);
	}

	[Fact]
	public async Task Store_WriteAsync_WhenExistingCaptureIsWithinClockSkew_PreservesNewerCapture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		ClaudeStatusLineCaptureStore store = new(
			filePath,
			new FakeTimeProvider(CapturedAt));
		ClaudeStatusLineCapture newerCapture = CreateCapture(
			CapturedAt + TimeSpan.FromMinutes(4),
			80);
		ClaudeStatusLineCapture olderCapture = CreateCapture(CapturedAt, 10);

		await store.WriteAsync(newerCapture);
		await store.WriteAsync(olderCapture);
		ClaudeStatusLineCapture? loaded = await store.ReadAsync();

		Assert.Equal(newerCapture, loaded);
	}

	[Fact]
	public async Task Store_WriteAsync_WhenExistingCaptureIsTooFarFuture_ReplacesIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		DateTimeOffset farFuture = DateTimeOffset.MaxValue;
		ClaudeStatusLineCapture futureCapture = CreateCapture(farFuture, 80);
		ClaudeStatusLineCapture currentCapture = CreateCapture(CapturedAt, 10);
		await new ClaudeStatusLineCaptureStore(
			filePath,
			new FakeTimeProvider(farFuture)).WriteAsync(futureCapture);
		ClaudeStatusLineCaptureStore currentStore = new(
			filePath,
			new FakeTimeProvider(CapturedAt));

		await currentStore.WriteAsync(currentCapture);
		ClaudeStatusLineCapture? loaded = await currentStore.ReadAsync();

		Assert.Equal(currentCapture, loaded);
	}

	[Fact]
	public async Task Store_WriteAsync_WhenWritersAreConcurrent_PreservesNewestCapture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "claude-statusline.json");
		ClaudeStatusLineCapture[] captures = Enumerable.Range(0, 12)
			.Select(index => CreateCapture(
				CapturedAt + TimeSpan.FromSeconds(index),
				index))
			.OrderByDescending(capture => capture.CapturedAt)
			.ToArray();
		Task[] writes = captures
			.Select(async capture =>
			{
				await Task.Yield();
				await new ClaudeStatusLineCaptureStore(filePath).WriteAsync(capture);
			})
			.ToArray();

		await Task.WhenAll(writes);
		ClaudeStatusLineCapture? loaded =
			await new ClaudeStatusLineCaptureStore(filePath).ReadAsync();

		Assert.Equal(captures.Max(capture => capture.CapturedAt), loaded?.CapturedAt);
		Assert.Equal(11, loaded?.FiveHour?.UsedPercentage);
	}

	[Fact]
	public void Format_WhenWindowsArePresent_ReturnsOneSafeLine()
	{
		ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(
			CreateFullInput(),
			CapturedAt);

		string formatted = ClaudeStatusLineFormatter.Format(capture);

		Assert.Equal("[Claude] 5h: 23.5% | 7d: 41.2%", formatted);
		Assert.DoesNotContain('\r', formatted);
		Assert.DoesNotContain('\n', formatted);
		Assert.DoesNotContain("sensitive", formatted, StringComparison.OrdinalIgnoreCase);
	}

	private static string CreateFullInput()
	{
		return """
			{
			  "cwd": "C:/sensitive-project",
			  "session_id": "secret-session",
			  "transcript_path": "C:/private/transcript.jsonl",
			  "version": "2.1.80",
			  "model": {
			    "id": "claude-opus-4-8",
			    "display_name": "Opus"
			  },
			  "cost": {
			    "total_cost_usd": 12.34
			  },
			  "context_window": {
			    "total_input_tokens": 15500,
			    "current_usage": {
			      "input_tokens": 8500,
			      "output_tokens": 1200
			    }
			  },
			  "rate_limits": {
			    "five_hour": {
			      "used_percentage": 23.5,
			      "resets_at": 1738425600
			    },
			    "seven_day": {
			      "used_percentage": 41.2,
			      "resets_at": 1738857600
			    }
			  },
			  "future_field": "ignored"
			}
			""";
	}

	private static ClaudeStatusLineCapture CreateCapture(
		DateTimeOffset capturedAt,
		double usedPercentage)
	{
		return new ClaudeStatusLineCapture(
			ClaudeStatusLineCapture.CurrentSchemaVersion,
			capturedAt,
			"2.1.80",
			new ClaudeStatusLineModel("claude-test", "Claude Test"),
			HasCurrentUsage: true,
			new ClaudeRateLimitWindow(usedPercentage, null),
			SevenDay: null);
	}
}
