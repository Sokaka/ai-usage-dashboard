using System.Globalization;
using System.IO;
using System.Text.Json;

using AiUsageDashboard.Core.Claude;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal sealed class ClaudeStatusLineUsageProvider :
	IUsageProvider,
	IClaudeStatusLineFallbackControl
{
	private static readonly byte[] DisabledMarkerContent = "disabled-v1"u8.ToArray();
	private static readonly Version MinimumRateLimitVersion = new(2, 1, 80);
	private static readonly TimeSpan CaptureStaleAfter = TimeSpan.FromMinutes(2);
	private readonly Func<Guid, string> _captureFilePathResolver;
	private readonly Func<Guid, string> _disabledMarkerFilePathResolver;
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider => ProviderKind.Claude;

	public TimeSpan MinimumRefreshInterval => TimeSpan.FromSeconds(10);

	public ClaudeStatusLineUsageProvider(
		Func<Guid, string> captureFilePathResolver,
		TimeProvider? timeProvider = null)
		: this(
			captureFilePathResolver,
			accountId => $"{captureFilePathResolver(accountId)}.fallback-disabled-v1",
			timeProvider)
	{
	}

	public ClaudeStatusLineUsageProvider(
		Func<Guid, string> captureFilePathResolver,
		Func<Guid, string> disabledMarkerFilePathResolver,
		TimeProvider? timeProvider = null)
	{
		_captureFilePathResolver = captureFilePathResolver ??
			throw new ArgumentNullException(nameof(captureFilePathResolver));
		_disabledMarkerFilePathResolver = disabledMarkerFilePathResolver ??
			throw new ArgumentNullException(nameof(disabledMarkerFilePathResolver));
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task DisableAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		string markerFilePath = Path.GetFullPath(
			_disabledMarkerFilePathResolver(accountId));

		if (File.Exists(markerFilePath))
		{
			return;
		}

		string? markerDirectory = Path.GetDirectoryName(markerFilePath);

		if (string.IsNullOrWhiteSpace(markerDirectory))
		{
			throw new InvalidOperationException("Claude fallback marker 路徑無效。");
		}

		Directory.CreateDirectory(markerDirectory);
		await using FileStream markerStream = new(
			markerFilePath,
			FileMode.OpenOrCreate,
			FileAccess.Write,
			FileShare.Read,
			bufferSize: 128,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		markerStream.SetLength(0);
		await markerStream.WriteAsync(DisabledMarkerContent, cancellationToken);
		await markerStream.FlushAsync(cancellationToken);
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Claude)
		{
			throw new ArgumentException(
				"Claude provider 只能讀取 Claude 帳號。",
				nameof(account));
		}

		DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
		ClaudeStatusLineCapture? capture;

		try
		{
			if (IsFallbackDisabled(account.Id))
			{
				return CreateDisabledSnapshot(account, fetchedAt);
			}

			ClaudeStatusLineCaptureStore store = new(
				_captureFilePathResolver(account.Id),
				_timeProvider);
			capture = await store.ReadAsync(cancellationToken);

			if (IsFallbackDisabled(account.Id))
			{
				return CreateDisabledSnapshot(account, fetchedAt);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is JsonException) ||
			(exception is InvalidDataException) ||
			(exception is NotSupportedException))
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				"暫時無法讀取 Claude 用量，稍後會自動再試。");
		}

		if (capture is null)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"尚未收到 Claude 用量資料；請使用卡片上的「連接 Claude 帳號」。");
		}

		if (IsKnownUnsupportedVersion(capture.ClaudeVersion))
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				"這個 Claude Code 版本無法提供用量資料，請更新至 2.1.80 或更新版本。");
		}

		IReadOnlyList<UsageMetric> metrics = CreateMetrics(capture);

		if (metrics.Count == 0)
		{
			string message = capture.HasCurrentUsage
				? "目前登入的帳號沒有提供 claude.ai Pro／Max 用量資料。"
				: "正在準備 Claude 用量資料，稍後再試。";
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				message,
				capture.CapturedAt);
		}

		DateTimeOffset staleAfter = AddWithoutOverflow(
			capture.CapturedAt,
			CaptureStaleAfter);
		bool isStale = fetchedAt >= staleAfter;
		return new UsageSnapshot(
			account,
			metrics,
			SourceTrust.Official,
			isStale ? SnapshotStatus.Stale : SnapshotStatus.Ready,
			fetchedAt,
			capture.CapturedAt,
			staleAfter,
			isStale
				? $"顯示上次確認的 Claude 用量 · 更新於 {capture.CapturedAt.ToLocalTime():MM/dd HH:mm}"
				: null);
	}

	private static DateTimeOffset AddWithoutOverflow(
		DateTimeOffset value,
		TimeSpan duration)
	{
		DateTimeOffset utcValue = value.ToUniversalTime();

		if (duration.Ticks > (DateTimeOffset.MaxValue.Ticks - utcValue.Ticks))
		{
			return DateTimeOffset.MaxValue;
		}

		return utcValue + duration;
	}

	private static void AddMetric(
		ICollection<UsageMetric> metrics,
		string key,
		string label,
		ClaudeRateLimitWindow? window)
	{
		if (window is null)
		{
			return;
		}

		string percentage = window.UsedPercentage.ToString(
			"0.##",
			CultureInfo.InvariantCulture);
		metrics.Add(new UsageMetric(
			key,
			label,
			window.UsedPercentage,
			$"已使用 {percentage}%",
			window.ResetsAt));
	}

	private static UsageSnapshot CreateEmptySnapshot(
		AccountProfile account,
		SnapshotStatus status,
		DateTimeOffset fetchedAt,
		string message,
		DateTimeOffset? observedAt = null)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			status,
			fetchedAt,
			observedAt,
			Error: message);
	}

	private static UsageSnapshot CreateDisabledSnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt)
	{
		return CreateEmptySnapshot(
			account,
			SnapshotStatus.NotConfigured,
			fetchedAt,
			"正在確認 Claude 帳號，暫時不顯示先前未連接帳號的用量。");
	}

	private static IReadOnlyList<UsageMetric> CreateMetrics(
		ClaudeStatusLineCapture capture)
	{
		List<UsageMetric> metrics = new(2);
		AddMetric(metrics, "five_hour", "5小時用量", capture.FiveHour);
		AddMetric(metrics, "seven_day", "週用量", capture.SevenDay);
		return metrics;
	}

	private static bool IsKnownUnsupportedVersion(string? versionText)
	{
		if (string.IsNullOrWhiteSpace(versionText))
		{
			return false;
		}

		int suffixIndex = versionText.IndexOfAny(new[] { '-', '+' });
		string numericVersion = suffixIndex >= 0
			? versionText[..suffixIndex]
			: versionText;
		return Version.TryParse(numericVersion, out Version? version) &&
			(version < MinimumRateLimitVersion);
	}

	private bool IsFallbackDisabled(Guid accountId)
	{
		return File.Exists(Path.GetFullPath(
			_disabledMarkerFilePathResolver(accountId)));
	}
}
