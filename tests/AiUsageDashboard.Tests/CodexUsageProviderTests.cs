using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class CodexUsageProviderTests
{
	private const string ValidAccountIdentity = "codex@example.com";
	private const string ValidPublicBindingIdentity =
		"11111111111111111111111111111111";
	private static readonly Guid ValidWorkspaceId =
		Guid.Parse("22222222-2222-2222-2222-222222222222");

	private sealed class FakeCodexUsagePoller : ICodexUsagePoller
	{
		private readonly Func<Guid, CancellationToken, Task<CodexUsagePollResult>> _poll;

		internal int CallCount { get; private set; }
		internal int RawCallCount { get; private set; }
		internal int BoundCallCount { get; private set; }

		internal string? LastExpectedPublicBindingIdentity { get; private set; }

		internal FakeCodexUsagePoller(
			Func<Guid, CancellationToken, Task<CodexUsagePollResult>> poll)
		{
			_poll = poll;
		}

		public async Task<CodexUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			RawCallCount++;
			return await _poll(accountId, cancellationToken);
		}

		public async Task<CodexUsagePollResult> PollBoundAsync(
			Guid accountId,
			string expectedPublicBindingIdentity,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			BoundCallCount++;
			LastExpectedPublicBindingIdentity = expectedPublicBindingIdentity;
			CodexUsagePollResult result = await _poll(
				accountId,
				cancellationToken);
			return result with
			{
				PublicBindingIdentity = expectedPublicBindingIdentity,
				WorkspaceId = ValidWorkspaceId
			};
		}
	}

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
	public async Task GetUsageAsync_WithPrimaryAndSecondaryWindows_ReturnsOfficialMetrics()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		DateTimeOffset observedAt = now - TimeSpan.FromSeconds(5);
		DateTimeOffset primaryReset = now + TimeSpan.FromHours(1);
		DateTimeOffset secondaryReset = now + TimeSpan.FromDays(1);
		DateTimeOffset creditExpiry = now + TimeSpan.FromDays(3);
		AccountProfile account = CreateAccount();
		FakeCodexUsagePoller poller = new((accountId, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				new[]
				{
					new CodexRateLimitBucket(
						"codex",
						"Codex",
						new CodexRateLimitWindow(23, 300, primaryReset),
						new CodexRateLimitWindow(41, 10080, secondaryReset))
				},
				2,
				observedAt,
				ValidAccountIdentity,
				NextResetCreditExpiresAt: creditExpiry)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(ProviderKind.Codex, provider.Provider);
		Assert.Equal(TimeSpan.FromMinutes(1), provider.MinimumRefreshInterval);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Equal(observedAt, snapshot.ObservedAt);
		Assert.Equal(observedAt + TimeSpan.FromMinutes(2), snapshot.StaleAfter);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			ValidAccountIdentity,
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal("plus", snapshot.PlanTier);
		Assert.Null(snapshot.SubscriptionScopeDisplayName);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Collection(
			snapshot.Metrics,
			metric =>
			{
				Assert.Equal("codex:codex:primary", metric.Key);
				Assert.Equal(23, metric.UsedPercent);
				Assert.Equal(primaryReset, metric.ResetsAt);
			},
			metric =>
			{
				Assert.Equal("codex:codex:secondary", metric.Key);
				Assert.Equal(41, metric.UsedPercent);
				Assert.Equal(secondaryReset, metric.ResetsAt);
			},
			metric =>
			{
				Assert.Equal("codex:rate_limit_reset_credits", metric.Key);
				Assert.Equal("可用重置次數", metric.Label);
				Assert.Null(metric.UsedPercent);
				Assert.Equal("2 次", metric.DisplayValue);
				Assert.Equal(creditExpiry, metric.ResetsAt);
			});
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(
			ValidPublicBindingIdentity,
			poller.LastExpectedPublicBindingIdentity);
	}

	[Fact]
	public async Task GetUsageAsync_WithZeroResetCredits_DoesNotShowExpiry()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			CreatePollResult(now) with
			{
				AvailableResetCredits = 0,
				NextResetCreditExpiresAt = now.AddDays(1)
			}));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		UsageMetric resetCredits = Assert.Single(snapshot.Metrics, metric =>
			metric.Key == "codex:rate_limit_reset_credits");
		Assert.Equal("0 次", resetCredits.DisplayValue);
		Assert.Null(resetCredits.ResetsAt);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task GetUsageAsync_WithValidMetricsButMissingAccountIdentity_FailsTransiently(
		string? accountIdentity)
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				new[]
				{
					new CodexRateLimitBucket(
						"codex",
						"Codex",
						window,
						null)
				},
				null,
				now,
				accountIdentity,
				CliVersionPolicies.AssessCodex(new Version(0, 145, 0)))));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.Contains("暫時無法讀取", snapshot.Error);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Null(snapshot.RetryNotBefore);
	}

	[Theory]
	[InlineData("Codex 已登入 ChatGPT，但 AI Usage 無法確認目前登入的帳號。請改用其他 ChatGPT 帳號重新連接。", "無法確認目前登入的帳號")]
	[InlineData("Codex 目前的登入方式不會使用 ChatGPT 帳號，因此沒有可讀取的 ChatGPT 訂閱用量。請在 Codex 切換成 ChatGPT 登入，再重新連接帳號。", "ChatGPT 登入")]
	public async Task GetUsageAsync_WhenAccountActionIsRequired_ReturnsConnectAccountSnapshot(
		string message,
		string expectedMessagePart)
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexUsageAccountActionRequiredException(message)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(message, snapshot.Error);
		Assert.Contains(expectedMessagePart, snapshot.Error);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Null(snapshot.RetryNotBefore);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("11111111-1111-1111-1111-111111111111")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	public async Task GetUsageAsync_WithoutUsableBinding_DoesNotPoll(
		string? providerAccountIdentity)
	{
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			CreatePollResult(DateTimeOffset.UtcNow)));
		CodexUsageProvider provider = new(poller);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex Plus",
			ProviderAccountIdentity: providerAccountIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Equal(0, poller.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithRawAccountIdentity_PollsAccountLevel()
	{
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			CreatePollResult(observedAt) with
			{
				AccountIdentity = "CODEX@EXAMPLE.COM"
			}));
		CodexUsageProvider provider = new(poller);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex Plus",
			ProviderAccountIdentity: ValidAccountIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(ValidAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal("CODEX@EXAMPLE.COM", snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(1, poller.RawCallCount);
		Assert.Equal(0, poller.BoundCallCount);
		Assert.Null(poller.LastExpectedPublicBindingIdentity);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRawAccountIdentityDiffers_FailsClosed()
	{
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			CreatePollResult(DateTimeOffset.UtcNow) with
			{
				AccountIdentity = "other@example.com"
			}));
		CodexUsageProvider provider = new(poller);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex Plus",
			ProviderAccountIdentity: ValidAccountIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(ValidAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(1, poller.RawCallCount);
		Assert.Equal(0, poller.BoundCallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenRawAccountPollFails_PreservesAccountBinding()
	{
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(new IOException("busy")));
		CodexUsageProvider provider = new(poller);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex Plus",
			ProviderAccountIdentity: ValidAccountIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(ValidAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(1, poller.RawCallCount);
		Assert.Equal(0, poller.BoundCallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenWorkspaceDiffers_FailsClosedWithoutNewIdentity()
	{
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexWorkspaceMismatchException()));
		CodexUsageProvider provider = new(poller);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			snapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task GetUsageAsync_WhenWorkspaceConfigurationIsInvalid_PreservesOpaqueBindingOnly()
	{
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexWorkspaceConfigurationException(
					"forced workspace is missing")));
		CodexUsageProvider provider = new(poller);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.DoesNotContain("forced workspace", snapshot.Error);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task GetUsageAsync_WhenWorkspaceBindingIsInvalid_RequiresReconnect()
	{
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexWorkspaceBindingInvalidException()));
		CodexUsageProvider provider = new(poller);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ValidPublicBindingIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.Contains("連接資料已損壞", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task GetUsageAsync_WithMultipleBuckets_OrdersCodexFirstThenByLimitId()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				new[]
				{
					new CodexRateLimitBucket("zeta", "Zeta", window, null),
					new CodexRateLimitBucket("codex", "Codex", window, null),
					new CodexRateLimitBucket("alpha", "Alpha", window, null)
				},
				null,
				now,
				ValidAccountIdentity)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(
			new[]
			{
				"codex:codex:primary",
				"codex:alpha:primary",
				"codex:zeta:primary"
			},
			snapshot.Metrics.Select(metric => metric.Key));
	}

	[Fact]
	public async Task GetUsageAsync_With32DoubleWindowBuckets_Returns64Metrics()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		CodexRateLimitBucket[] buckets = Enumerable.Range(0, 32)
			.Select(index => new CodexRateLimitBucket(
				$"bucket-{index}",
				$"Bucket {index}",
				window,
				window))
			.ToArray();
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				buckets,
				null,
				now,
				ValidAccountIdentity)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(64, snapshot.Metrics.Count);
	}

	[Fact]
	public async Task GetUsageAsync_WhenMoreThan32BucketsAreReturned_FailsClosed()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		CodexRateLimitBucket[] buckets = Enumerable.Range(0, 33)
			.Select(index => new CodexRateLimitBucket(
				$"bucket-{index}",
				null,
				window,
				null))
			.ToArray();
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				buckets,
				null,
				now,
				ValidAccountIdentity)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenResetCreditsWouldCreate65Metrics_FailsClosed()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		CodexRateLimitBucket[] buckets = Enumerable.Range(0, 32)
			.Select(index => new CodexRateLimitBucket(
				$"bucket-{index}",
				$"Bucket {index}",
				window,
				window))
			.ToArray();
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				buckets,
				1,
				now,
				ValidAccountIdentity)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenMetricTextContainsControlCharacter_FailsClosed()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexRateLimitWindow window = new(10, 300, null);
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				new[]
				{
					new CodexRateLimitBucket(
						"codex\u0007usage",
						"Codex",
						window,
						null)
				},
				null,
				now,
				ValidAccountIdentity)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCodexIsNotConfigured_ReturnsNotConfiguredSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		const string message = "Codex login is required.";
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexUsageNotConfiguredException(message)));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Equal(message, snapshot.Error);
		Assert.Equal(
			UsageRecoveryAction.ConnectAccount,
			snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCodexCliIsMissing_ReturnsInstallOrRepairError()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexCliNotFoundException("Codex CLI is missing.")));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains("安裝或修復", snapshot.Error);
		Assert.Equal(
			UsageRecoveryAction.InstallOrUpdate,
			snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCodexCliIsUntrusted_ReturnsOfficialReinstallError()
	{
		DateTimeOffset now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexCliUntrustedException("Untrusted Codex CLI.")));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains("OpenAI 官方來源", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(
			UsageRecoveryAction.InstallOrUpdate,
			snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCodexVersionProbeFails_ReturnsRetryError()
	{
		DateTimeOffset now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexCliProbeException(
					"Probe failed.",
					new TimeoutException("Probe timed out."))));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains("自動再試", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCodexContainmentFails_RequiresRestart()
	{
		DateTimeOffset now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new CodexCliContainmentException(
					"Containment failed.",
					new InvalidOperationException("Job query failed."))));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains("重新啟動", snapshot.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("自動再試", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(
			UsageRecoveryAction.RestartApplication,
			snapshot.RecoveryAction);
	}

	[Theory]
	[InlineData(
		nameof(CodexAppServerFailureCategory.Authentication),
		SnapshotStatus.NotConfigured,
		"重新連接",
		nameof(UsageRecoveryAction.ConnectAccount),
		false)]
	[InlineData(
		nameof(CodexAppServerFailureCategory.Compatibility),
		SnapshotStatus.Error,
		"透過 Codex 讀取用量",
		nameof(UsageRecoveryAction.InstallOrUpdate),
		true)]
	[InlineData(
		nameof(CodexAppServerFailureCategory.Transient),
		SnapshotStatus.Error,
		"自動再試",
		nameof(UsageRecoveryAction.Retry),
		false)]
	[InlineData(
		nameof(CodexAppServerFailureCategory.Unknown),
		SnapshotStatus.Error,
		"無法辨識",
		nameof(UsageRecoveryAction.Retry),
		true)]
	public async Task GetUsageAsync_WhenAppServerReturnsFailure_MapsCategory(
		string categoryName,
		SnapshotStatus expectedStatus,
		string expectedMessage,
		string expectedRecoveryActionName,
		bool expectsVersionDiagnostic)
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		CodexAppServerFailureCategory category =
			Enum.Parse<CodexAppServerFailureCategory>(categoryName);
		UsageRecoveryAction expectedRecoveryAction =
			Enum.Parse<UsageRecoveryAction>(expectedRecoveryActionName);
		CodexAppServerFailureException failure = new(
			"account/rateLimits/read",
			-32603,
			"Server message",
			category);
		failure.AttachVersionEvidence(CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0)));
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(failure));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(expectedStatus, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains(expectedMessage, snapshot.Error);
		if (category == CodexAppServerFailureCategory.Unknown)
		{
			Assert.Contains("自動再試", snapshot.Error, StringComparison.Ordinal);
			Assert.Contains("不需要操作", snapshot.Error, StringComparison.Ordinal);
			Assert.DoesNotContain("請更新", snapshot.Error, StringComparison.Ordinal);
		}
		if (expectsVersionDiagnostic)
		{
			Assert.Contains("Codex CLI 0.145.0", snapshot.Error, StringComparison.Ordinal);
			Assert.Contains("不代表不支援", snapshot.Error, StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain("CLI 版本診斷", snapshot.Error, StringComparison.Ordinal);
		}
		Assert.Equal(expectedRecoveryAction, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollerThrowsInvalidOperationException_PropagatesFailure()
	{
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(
				new InvalidOperationException("Unexpected programming failure.")));
		CodexUsageProvider provider = new(poller);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.GetUsageAsync(CreateAccount(), CancellationToken.None));
	}

	[Fact]
	public async Task GetUsageAsync_WithOnlyResetCredits_ReturnsErrorSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			new CodexUsagePollResult(
				"plus",
				Array.Empty<CodexRateLimitBucket>(),
				2,
				now,
				ValidAccountIdentity,
				CliVersionPolicies.AssessCodex(new Version(0, 145, 0)))));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Contains("Codex CLI 0.145.0", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingFailsTemporarily_ReturnsErrorSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		TimeoutException failure = new("app-server timed out");
		failure.AttachVersionEvidence(CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0)));
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromException<CodexUsagePollResult>(failure));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.NotNull(snapshot.Error);
		Assert.Contains("Codex CLI 0.145.0", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCallerCancels_PropagatesCancellation()
	{
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		FakeCodexUsagePoller poller = new((_, token) =>
			Task.FromCanceled<CodexUsagePollResult>(token));
		CodexUsageProvider provider = new(poller);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.GetUsageAsync(CreateAccount(), cancellationSource.Token));
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollerCancelsInternally_ReturnsErrorSnapshot()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		using CancellationTokenSource pollingCancellationSource = new();
		pollingCancellationSource.Cancel();
		FakeCodexUsagePoller poller = new((_, _) =>
			Task.FromCanceled<CodexUsagePollResult>(pollingCancellationSource.Token));
		CodexUsageProvider provider = new(poller, new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WithWrongProvider_ThrowsArgumentException()
	{
		FakeCodexUsagePoller poller = new((_, _) => Task.FromResult(
			CreatePollResult(DateTimeOffset.UtcNow)));
		CodexUsageProvider provider = new(poller);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Max");

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			provider.GetUsageAsync(account, CancellationToken.None));

		Assert.Equal("account", exception.ParamName);
		Assert.Equal(0, poller.CallCount);
	}

	private static AccountProfile CreateAccount()
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex Plus",
			ProviderAccountIdentity: ValidPublicBindingIdentity);
	}

	private static CodexUsagePollResult CreatePollResult(DateTimeOffset observedAt)
	{
		return new CodexUsagePollResult(
			"plus",
			new[]
			{
				new CodexRateLimitBucket(
					"codex",
					"Codex",
					new CodexRateLimitWindow(10, 300, null),
					null)
			},
			null,
			observedAt,
			ValidAccountIdentity);
	}
}
