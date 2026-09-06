using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Claude;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeStatusLineUsageProviderTests
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

	[Fact]
	public async Task GetUsageAsync_WithRateLimits_ReturnsOfficialMetrics()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		ClaudeStatusLineCapture capture = CreateCapture(
			capturedAt,
			"2.1.80",
			new ClaudeRateLimitWindow(23.5, capturedAt + TimeSpan.FromHours(1)),
			new ClaudeRateLimitWindow(41.25, capturedAt + TimeSpan.FromDays(1)));
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(capture);
		AccountProfile account = CreateAccount();
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(capturedAt + TimeSpan.FromMinutes(1)));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.Official, snapshot.SourceTrust);
		Assert.Equal(capturedAt, snapshot.ObservedAt);
		Assert.Collection(
			snapshot.Metrics,
			metric =>
			{
				Assert.Equal("five_hour", metric.Key);
				Assert.Equal(23.5, metric.UsedPercent);
				Assert.Equal("已使用 23.5%", metric.DisplayValue);
			},
			metric =>
			{
				Assert.Equal("seven_day", metric.Key);
				Assert.Equal(41.25, metric.UsedPercent);
			});
	}

	[Fact]
	public async Task GetUsageAsync_WhenCaptureIsOld_ReturnsStaleMetrics()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(CreateCapture(
			capturedAt,
			"2.1.80",
			new ClaudeRateLimitWindow(75, null),
			null));
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(capturedAt + TimeSpan.FromMinutes(3)));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Stale, snapshot.Status);
		Assert.Equal(75, Assert.Single(snapshot.Metrics).UsedPercent);
		Assert.Equal(
			$"顯示上次確認的 Claude 用量 · 更新於 {capturedAt.ToLocalTime():MM/dd HH:mm}",
			snapshot.Error);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCaptureIsTooFarFuture_ReturnsError()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset utcNow = new(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);
		DateTimeOffset farFuture = utcNow + TimeSpan.FromMinutes(6);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		await new ClaudeStatusLineCaptureStore(
			captureFilePath,
			new FakeTimeProvider(farFuture)).WriteAsync(CreateCapture(
				farFuture,
				"2.1.80",
				new ClaudeRateLimitWindow(75, null),
				null));
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(utcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCaptureAtMaximumValueIsWithinClockSkew_DoesNotOverflow()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = DateTimeOffset.MaxValue;
		DateTimeOffset utcNow = capturedAt - TimeSpan.FromMinutes(1);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		await new ClaudeStatusLineCaptureStore(
			captureFilePath,
			new FakeTimeProvider(capturedAt)).WriteAsync(CreateCapture(
				capturedAt,
				"2.1.80",
				new ClaudeRateLimitWindow(75, null),
				null));
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(utcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(DateTimeOffset.MaxValue, snapshot.StaleAfter);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCaptureOffsetIsNearUtcMaximum_DoesNotOverflow()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(
			9999,
			12,
			31,
			22,
			59,
			0,
			TimeSpan.FromHours(-1));
		DateTimeOffset utcNow = capturedAt - TimeSpan.FromMinutes(1);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		await new ClaudeStatusLineCaptureStore(
			captureFilePath,
			new FakeTimeProvider(capturedAt)).WriteAsync(CreateCapture(
				capturedAt,
				"2.1.80",
				new ClaudeRateLimitWindow(75, null),
				null));
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(utcNow));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(DateTimeOffset.MaxValue, snapshot.StaleAfter);
	}

	[Fact]
	public async Task GetUsageAsync_WithoutRateLimits_DoesNotUseSessionUsageAsQuota()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(2026, 7, 15, 3, 0, 0, TimeSpan.Zero);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		ClaudeStatusLineCapture capture = CreateCapture(
			capturedAt,
			"2.1.80",
			null,
			null) with
		{
			HasCurrentUsage = true
		};
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(capture);
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(capturedAt));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Contains("Pro／Max", snapshot.Error);
	}

	[Fact]
	public async Task GetUsageAsync_WithOldClaudeVersion_RequestsUpgrade()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(CreateCapture(
			capturedAt,
			"2.1.79",
			new ClaudeRateLimitWindow(10, null),
			null));
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			new FakeTimeProvider(capturedAt));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Contains("2.1.80", snapshot.Error);
	}

	[Fact]
	public async Task DisableAsync_WhenOldCliRewritesCapture_RemainsFailClosedAcrossInstances()
	{
		using TemporaryDirectory temporaryDirectory = new();
		DateTimeOffset capturedAt = new(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);
		string captureFilePath = Path.Combine(temporaryDirectory.Path, "statusline-v1.json");
		string markerFilePath = Path.Combine(
			temporaryDirectory.Path,
			"statusline-fallback-disabled-v1");
		AccountProfile account = CreateAccount();
		ClaudeStatusLineUsageProvider provider = new(
			_ => captureFilePath,
			_ => markerFilePath,
			new FakeTimeProvider(capturedAt));
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(CreateCapture(
			capturedAt,
			"2.1.80",
			new ClaudeRateLimitWindow(10, null),
			null));

		await provider.DisableAsync(account.Id);
		await new ClaudeStatusLineCaptureStore(captureFilePath).WriteAsync(CreateCapture(
			capturedAt + TimeSpan.FromMinutes(1),
			"2.1.80",
			new ClaudeRateLimitWindow(90, null),
			null));
		ClaudeStatusLineUsageProvider restartedProvider = new(
			_ => captureFilePath,
			_ => markerFilePath,
			new FakeTimeProvider(capturedAt + TimeSpan.FromMinutes(1)));

		UsageSnapshot snapshot = await restartedProvider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.True(File.Exists(markerFilePath));
		Assert.DoesNotContain("@", await File.ReadAllTextAsync(markerFilePath));
		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Contains("正在確認 Claude 帳號", snapshot.Error);
	}

	private static AccountProfile CreateAccount()
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Pro");
	}

	private static ClaudeStatusLineCapture CreateCapture(
		DateTimeOffset capturedAt,
		string version,
		ClaudeRateLimitWindow? fiveHour,
		ClaudeRateLimitWindow? sevenDay)
	{
		return new ClaudeStatusLineCapture(
			ClaudeStatusLineCapture.CurrentSchemaVersion,
			capturedAt,
			version,
			new ClaudeStatusLineModel("claude-test", "Claude Test"),
			HasCurrentUsage: false,
			fiveHour,
			sevenDay);
	}
}
