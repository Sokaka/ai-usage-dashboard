using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeUsageProviderTests
{
	private sealed class FakeClaudeUsagePoller : IClaudeUsagePoller
	{
		private readonly Func<Guid, CancellationToken, Task<ClaudeUsagePollResult>> _poll;

		internal int CallCount { get; private set; }

		internal List<string> ExpectedPublicBindingIdentities { get; } = new();

		internal FakeClaudeUsagePoller(
			Func<Guid, CancellationToken, Task<ClaudeUsagePollResult>> poll)
		{
			_poll = poll;
		}

		internal FakeClaudeUsagePoller(
			string output,
			DateTimeOffset observedAt,
			ClaudeSubscriptionContext subscriptionContext)
			: this((_, _) => Task.FromResult(
				new ClaudeUsagePollResult(
					output,
					observedAt,
					SubscriptionContext: subscriptionContext)))
		{
		}

		public Task<ClaudeUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			return _poll(accountId, cancellationToken);
		}

		public Task<ClaudeUsagePollResult> PollBoundAsync(
			Guid accountId,
			string expectedPublicBindingIdentity,
			CancellationToken cancellationToken = default)
		{
			ExpectedPublicBindingIdentities.Add(expectedPublicBindingIdentity);
			CallCount++;
			return _poll(accountId, cancellationToken);
		}
	}

	private sealed class FakeClaudeAccountBindingStore :
		IClaudeAccountBindingStore
	{
		private readonly ClaudeAccountBinding? _binding;

		internal int LoadCallCount { get; private set; }

		internal FakeClaudeAccountBindingStore(ClaudeAccountBinding? binding)
		{
			_binding = binding;
		}

		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Usage provider must not enumerate Claude bindings.");
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadCallCount++;

			return Task.FromResult(
				_binding?.AccountId == accountId ? _binding : null);
		}

		public Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Usage provider must not write Claude bindings.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Usage provider must not delete Claude bindings.");
		}
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		internal void Advance(TimeSpan duration)
		{
			_utcNow += duration;
		}
	}

	private sealed class FakeUsageProvider :
		IUsageProvider,
		IClaudeStatusLineFallbackControl
	{
		private readonly Func<Guid, CancellationToken, Task>? _disable;
		private readonly Func<AccountProfile, CancellationToken, Task<UsageSnapshot>> _getUsage;

		public ProviderKind Provider => ProviderKind.Claude;

		public TimeSpan MinimumRefreshInterval => TimeSpan.FromSeconds(10);

		internal int CallCount { get; private set; }

		internal List<Guid> DisabledAccountIds { get; } = new();

		internal FakeUsageProvider(
			Func<AccountProfile, CancellationToken, Task<UsageSnapshot>> getUsage,
			Func<Guid, CancellationToken, Task>? disable = null)
		{
			_getUsage = getUsage;
			_disable = disable;
		}

		public Task DisableAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			if (_disable is not null)
			{
				return _disable(accountId, cancellationToken);
			}

			DisabledAccountIds.Add(accountId);
			return Task.CompletedTask;
		}

		public Task<UsageSnapshot> GetUsageAsync(
			AccountProfile account,
			CancellationToken cancellationToken)
		{
			CallCount++;
			return _getUsage(account, cancellationToken);
		}
	}

	private const string UsageOutput = """
		You are currently using your subscription to power your Claude Code usage

		Current session: 49% used · resets Jul 15, 11:30am (Asia/Taipei)
		Current week (all models): 84% used · resets Jul 20, 8am (Asia/Taipei)
		Current week (Fable): 83% used · resets Jul 20, 8am (Asia/Taipei)
		""";
	private const string SubscriptionGuardOnlyOutput =
		"You are currently using your subscription to power your Claude Code usage";
	private const string StaleUsageOutput = """
		You are currently using your subscription to power your Claude Code usage

		Showing last-known usage because current usage is temporarily unavailable
		Current session: 49% used · resets Jul 15, 11:30am (Asia/Taipei)
		Current week (all models): 84% used · resets Jul 20, 8am (Asia/Taipei)
		""";

	[Fact]
	public async Task GetUsageAsync_WithoutQuotaRiskConsent_DoesNotPollOrReadFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount() with
		{
			HasAcceptedClaudeQuotaRisk = false
		};
		FakeClaudeUsagePoller poller = new((_, _) =>
			throw new InvalidOperationException("Claude poller must not be called."));
		FakeUsageProvider fallback = new((_, _) =>
			throw new InvalidOperationException("Fallback must not be called."));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Contains("確認 Claude 用量讀取", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("不會重新登入", snapshot.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("連接 Claude 帳號", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(0, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingSucceeds_DoesNotCallFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				now,
				"claude@example.com",
				CliVersionPolicies.AssessClaude(new Version(2, 1, 239)))));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(3, snapshot.Metrics.Count);
		Assert.Equal(49, snapshot.Metrics[0].UsedPercent);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.Error);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
		Assert.Equal(new[] { account.Id }, fallback.DisabledAccountIds);
		Assert.Equal(TimeSpan.FromSeconds(10), provider.MinimumRefreshInterval);
	}

	[Theory]
	[InlineData("max")]
	[InlineData("team")]
	public async Task GetUsageAsync_WithMatchingPrivateBinding_ReturnsVerifiedOpaqueSubscriptionContext(
		string planTier)
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext context) = CreatePrivateBindingFixture(planTier);
		FakeClaudeUsagePoller poller = new(UsageOutput, now, context);
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(publicBindingIdentity, snapshot.ProviderAccountIdentity);
		Assert.NotEqual(
			context.AccountIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			context.AccountIdentity,
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			context.SubscriptionScopeDisplayName,
			snapshot.SubscriptionScopeDisplayName);
		Assert.Equal(context.PlanTier, snapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(3, snapshot.Metrics.Count);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(
			new[] { publicBindingIdentity },
			poller.ExpectedPublicBindingIdentities);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBoundSafetyFailureContainsOnlyEmail_KeepsOpaqueIdentityAndDisplayEmail()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 5, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext context) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageSafetyException(
					"Claude 用量檢查會自動再試。",
					context.AccountIdentity,
					now + TimeSpan.FromMinutes(1))));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(publicBindingIdentity, snapshot.ProviderAccountIdentity);
		Assert.NotEqual(
			context.AccountIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			context.AccountIdentity,
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Null(snapshot.SubscriptionScopeDisplayName);
		Assert.Null(snapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBoundSafetyFailureContainsVerifiedContext_PreservesContextWithOpaqueIdentity()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 6, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext context) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageSafetyException(
					"Claude 用量檢查會自動再試。",
					context.AccountIdentity,
					now + TimeSpan.FromMinutes(1),
					context)));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(publicBindingIdentity, snapshot.ProviderAccountIdentity);
		Assert.NotEqual(
			context.AccountIdentity,
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			context.AccountIdentity,
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			context.SubscriptionScopeDisplayName,
			snapshot.SubscriptionScopeDisplayName);
		Assert.Equal(context.PlanTier, snapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WithoutPrivateBinding_FailsClosed()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);
		Guid publicBindingId = new("2f30ed1e-9a3b-48a1-b408-e78c3c9ddf64");
		AccountProfile account = CreateAccount() with
		{
			ProviderAccountIdentity =
				ClaudeAccountBinding.CreatePublicBindingIdentity(publicBindingId)
		};
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeSubscriptionBindingMissingException()));
		FakeClaudeAccountBindingStore bindingStore = new(binding: null);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			UsageRecoveryAction.ConnectAccount,
			snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(
			"Claude 訂閱連接資料不完整；請重新連接原本的帳號，確認訂閱範圍。",
			snapshot.Error);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("owner@example.com")]
	[InlineData("b65cf956-5b94-475c-aac3-0826b9a34be1")]
	[InlineData("B65CF9565B94475CAAC30826B9A34BE1")]
	[InlineData("not-a-confirmed-subscription")]
	public async Task GetUsageAsync_WithoutCanonicalBindingIdentity_RequiresConfirmationWithoutPollingOrChangingAccount(
		string? legacyIdentity)
	{
		DateTimeOffset now = new(2026, 9, 5, 4, 0, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount() with
		{
			ProviderAccountIdentity = legacyIdentity
		};
		FakeClaudeUsagePoller poller = new((_, _) =>
			throw new InvalidOperationException("Unconfirmed subscription must not be polled."));
		FakeClaudeAccountBindingStore bindingStore = new(binding: null);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(UsageRecoveryAction.ConfirmSubscription, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(
			"尚未確認這張卡片的 Claude 訂閱。請使用原本的帳號，確認組織與訂閱方案。",
			snapshot.Error);
		Assert.Empty(snapshot.Metrics);
		Assert.Same(account, snapshot.Account);
		Assert.Equal(legacyIdentity, account.ProviderAccountIdentity);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(0, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
		Assert.Empty(fallback.DisabledAccountIds);
	}

	[Theory]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(UnauthorizedAccessException))]
	public async Task GetUsageAsync_WhenBoundPollingCannotReadConnection_ReturnsRetryWithoutRequestingConfirmation(
		Type failureType)
	{
		DateTimeOffset now = new(2026, 9, 5, 4, 5, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext _) = CreatePrivateBindingFixture();
		Exception failure = Assert.IsAssignableFrom<Exception>(
			Activator.CreateInstance(failureType, "Cannot read private connection file."));
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(failure));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			new FakeClaudeAccountBindingStore(binding));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(account.ProviderAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Empty(snapshot.Metrics);
		Assert.DoesNotContain("損壞", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPrivateBindingEntitlementDoesNotMatch_ReturnsDefiniteScopeMismatch()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext _) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeSubscriptionScopeMismatchException()));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId),
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.Null(snapshot.SubscriptionScopeDisplayName);
		Assert.Null(snapshot.PlanTier);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPrivateBindingScopeIsTemporarilyUnavailable_ReturnsTransientProbeError()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext _) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				now,
				AccountIdentity: "owner@example.com")));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBoundAuthStatusSchemaIsUnavailable_ReturnsTransientProbeError()
	{
		DateTimeOffset now = new(2026, 8, 29, 3, 5, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext _) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new InvalidDataException(
					"Claude `auth status` 暫時缺少可驗證的訂閱資訊。")));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenVerifiedEnterpriseUsageIsUnavailable_DoesNotGuessQuota()
	{
		const string planTier = "enterprise";
		DateTimeOffset now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext context) =
			CreatePrivateBindingFixture(planTier);
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeSubscriptionUsageUnavailableException(context)));
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			context.AccountIdentity,
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			context.SubscriptionScopeDisplayName,
			snapshot.SubscriptionScopeDisplayName);
		Assert.Equal(planTier, snapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			snapshot.SubscriptionVerificationState);
		Assert.Contains("不會猜測用量", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenFallbackDisableFails_DoesNotBlameCliVersion()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				now,
				"claude@example.com",
				CliVersionPolicies.AssessClaude(new Version(2, 1, 239)))));
		FakeUsageProvider fallback = new(
			(profile, _) => Task.FromResult(CreateFallbackSnapshot(profile, now)),
			(_, _) => Task.FromException(new IOException("local failure")));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.DoesNotContain(
			"CLI 版本診斷",
			snapshot.Error ?? string.Empty,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetUsageAsync_WithinPollingInterval_ReusesPerAccountPollingResult()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				now,
				"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		await provider.GetUsageAsync(account, CancellationToken.None);
		await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(1, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_AfterPollingInterval_PollsSameAccountAgain()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				timeProvider.GetUtcNow(),
				"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, timeProvider.GetUtcNow())));
		ClaudeUsageProvider provider = new(poller, fallback, timeProvider);

		UsageSnapshot first = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
		UsageSnapshot second = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, first.Status);
		Assert.Equal(SnapshotStatus.Ready, second.Status);
		Assert.Equal(2, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task Invalidate_ClearsPollingResult()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				now,
				"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		await provider.GetUsageAsync(account, CancellationToken.None);
		provider.Invalidate(account.Id);
		await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(2, poller.CallCount);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenNewAuthenticationHasNoIdentity_PreservesOldIdentityAndRetries()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		AccountProfile account = CreateAccount();
		int pollCount = 0;
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				UsageOutput,
				timeProvider.GetUtcNow(),
				Interlocked.Increment(ref pollCount) == 1
					? "first@example.com"
					: null)));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Official,
				SnapshotStatus.NotConfigured,
				timeProvider.GetUtcNow())));
		ClaudeUsageProvider provider = new(poller, fallback, timeProvider);

		UsageSnapshot first = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
		UsageSnapshot second = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal("first@example.com", first.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, second.Status);
		Assert.Equal("first@example.com", second.ProviderAccountIdentity);
		Assert.DoesNotContain("連接 Claude 帳號", second.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, second.RecoveryAction);
	}

	[Theory]
	[InlineData(
		true,
		"找不到 Claude Code CLI",
		UsageRecoveryAction.InstallOrUpdate)]
	[InlineData(
		false,
		"尚未連接 Claude 帳號",
		UsageRecoveryAction.ConnectAccount)]
	public async Task GetUsageAsync_WhenSetupIsMissing_UsesGuiRecoveryMessage(
		bool isCliMissing,
		string expectedMessage,
		UsageRecoveryAction expectedRecoveryAction)
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		Exception failure = isCliMissing
			? new FileNotFoundException()
			: new DirectoryNotFoundException();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(failure));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Official,
				SnapshotStatus.NotConfigured,
				now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Contains(expectedMessage, snapshot.Error, StringComparison.Ordinal);
		if (isCliMissing)
		{
			Assert.DoesNotContain("連接 Claude 帳號", snapshot.Error, StringComparison.Ordinal);
			Assert.Contains("自動再試", snapshot.Error, StringComparison.Ordinal);
		}
		else
		{
			Assert.Contains("連接 Claude 帳號", snapshot.Error, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("啟動指令", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(expectedRecoveryAction, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenClaudeCliIsUntrusted_ReturnsOfficialReinstallError()
	{
		DateTimeOffset now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeCliUntrustedException("Untrusted Claude CLI.")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Official,
				SnapshotStatus.NotConfigured,
				now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Contains("Anthropic 官方來源", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(
			UsageRecoveryAction.InstallOrUpdate,
			snapshot.RecoveryAction);
	}

	[Fact]
	public async Task CompleteAccountLoginAsync_DisablesFallbackAndSeedsTransientFailureIdentity()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new TimeoutException("temporary")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Official,
				SnapshotStatus.NotConfigured,
				now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		await provider.CompleteAccountLoginAsync(
			account.Id,
			"claude@example.com");
		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(new[] { account.Id }, fallback.DisabledAccountIds);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(1, provider.CachedAccountIdentitySeedCount);

		provider.PurgeAccountState(account.Id);

		Assert.Equal(0, provider.CachedAccountIdentitySeedCount);
	}

	[Fact]
	public async Task CompleteAccountLoginAsync_DoesNotClearSafetyLatch()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageSafetyException(
					"Claude 用量檢查已停止；請重新檢查 Claude 用量。",
					"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot beforeLogin = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		await provider.CompleteAccountLoginAsync(
			account.Id,
			"claude@example.com");
		UsageSnapshot afterLogin = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(
			UsageRecoveryAction.RevalidateUsage,
			beforeLogin.RecoveryAction);
		Assert.Equal(
			UsageRecoveryAction.RevalidateUsage,
			afterLogin.RecoveryAction);
		Assert.Equal(2, poller.CallCount);
		Assert.Equal(new[] { account.Id }, fallback.DisabledAccountIds);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingIsNotConfigured_ReturnsFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new FileNotFoundException("claude.exe")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Official, snapshot.SourceTrust);
		Assert.Equal(25, Assert.Single(snapshot.Metrics).UsedPercent);
		Assert.Equal(UsageRecoveryAction.None, snapshot.RecoveryAction);
		Assert.Equal(1, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingFailsAndFallbackIsStale_PreservesRecovery()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new FileNotFoundException("claude.exe")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now, SnapshotStatus.Stale)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Official, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Stale, snapshot.Status);
		Assert.Equal(25, Assert.Single(snapshot.Metrics).UsedPercent);
		Assert.Contains("找不到 Claude Code CLI", snapshot.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("連接 Claude 帳號", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("自動再試", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(
			UsageRecoveryAction.InstallOrUpdate,
			snapshot.RecoveryAction);
		Assert.Equal(1, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenProcessContainmentFails_RequiresRestartAndSkipsFallback()
	{
		DateTimeOffset now = new(2026, 8, 25, 4, 0, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeCliContainmentException(
					new IOException("process containment failed"))));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(
			UsageRecoveryAction.RestartApplication,
			snapshot.RecoveryAction);
		Assert.Contains("重新啟動", snapshot.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("自動再試", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenUsageSafetyCanRetryAutomatically_ReturnsRetry()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageSafetyException(
					"Claude 用量檢查會自動再試。",
					"claude@example.com",
					now + TimeSpan.FromMinutes(1))));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now, SnapshotStatus.Stale)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			snapshot.RetryNotBefore);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenUsageSafetyCannotRetryAutomatically_RequiresRevalidation()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageSafetyException(
					"Claude 用量輪詢已停用。",
					"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now, SnapshotStatus.Stale)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(
			UsageRecoveryAction.RevalidateUsage,
			snapshot.RecoveryAction);
		Assert.Null(snapshot.RetryNotBefore);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenSubscriptionIsUnsupported_DoesNotUseUnboundFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageNotConfiguredException(
					"Claude Code 訂閱不支援用量讀取。",
					UsageRecoveryAction.SwitchAccount,
					"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now, SnapshotStatus.Stale)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenSubscriptionLoginIsMissing_PreservesReason()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeUsageNotConfiguredException(
					"Claude Code 帳號尚未登入。",
					UsageRecoveryAction.ConnectAccount)));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.Official,
				SnapshotStatus.NotConfigured,
				now,
				Error: "尚未收到 statusline capture。")));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal("Claude Code 帳號尚未登入。", snapshot.Error);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPrivateBindingIsMalformed_RequiresSwitchAccount()
	{
		DateTimeOffset now = new(2026, 8, 30, 3, 0, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext _) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				new ClaudeSubscriptionBindingInvalidException()));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			new FakeClaudeAccountBindingStore(binding));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Contains("訂閱連接資料無效", snapshot.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetUsageAsync_WhenAuthenticatedIdentityIsTemporarilyMissing_ReturnsErrorRetry()
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromException<ClaudeUsagePollResult>(
				ClaudeCliUsagePoller.CreateAccountIdentityUnavailableException(
					"Claude Code 已登入有效訂閱，但帳號識別暫時缺失。")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.NotEqual(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingReturnsSubscriptionGuardOnly_ExplainsAutomaticRetry()
	{
		DateTimeOffset now = new(2026, 8, 22, 4, 27, 44, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				SubscriptionGuardOnlyOutput,
				now,
				"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(
			"Claude 暫時未回傳用量額度；AI Usage 稍後會自動再試，不需要操作。",
			snapshot.Error);
		Assert.DoesNotContain("訂閱", snapshot.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("帳務", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBoundPollingReturnsSubscriptionGuardOnly_MarksTransientProbeError()
	{
		DateTimeOffset now = new(2026, 8, 29, 4, 30, 0, TimeSpan.Zero);
		(AccountProfile account, ClaudeAccountBinding binding,
			ClaudeSubscriptionContext context) = CreatePrivateBindingFixture();
		FakeClaudeUsagePoller poller = new(
			SubscriptionGuardOnlyOutput,
			now,
			context);
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now),
			new FakeClaudeAccountBindingStore(binding));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(context.AccountIdentity, snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(context.PlanTier, snapshot.PlanTier);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingFormatChanges_DoesNotUseUnboundFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				"unsupported output",
				now,
				"claude@example.com",
				CliVersionPolicies.AssessClaude(new Version(2, 1, 239)))));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Unavailable, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Contains("Claude Code 2.1.239", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("相容性基準為 2.1.169", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("不代表不支援", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCallerCancels_DoesNotCallFallback()
	{
		AccountProfile account = CreateAccount();
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		FakeClaudeUsagePoller poller = new((_, token) =>
			Task.FromCanceled<ClaudeUsagePollResult>(token));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, DateTimeOffset.UtcNow)));
		ClaudeUsageProvider provider = new(poller, fallback);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.GetUsageAsync(account, cancellationSource.Token));
		Assert.Equal(0, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollerCancelsInternally_ReturnsFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		using CancellationTokenSource pollingCancellationSource = new();
		pollingCancellationSource.Cancel();
		FakeClaudeUsagePoller poller = new((_, _) =>
			Task.FromCanceled<ClaudeUsagePollResult>(pollingCancellationSource.Token));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(profile, now)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.Official, snapshot.SourceTrust);
		Assert.Equal(1, fallback.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPollingReturnsLastKnown_DoesNotUseUnboundFallback()
	{
		DateTimeOffset now = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		AccountProfile account = CreateAccount();
		FakeClaudeUsagePoller poller = new((_, _) => Task.FromResult(
			new ClaudeUsagePollResult(
				StaleUsageOutput,
				now,
				"claude@example.com")));
		FakeUsageProvider fallback = new((profile, _) => Task.FromResult(
			CreateFallbackSnapshot(
				profile,
				now - TimeSpan.FromMinutes(1),
				SnapshotStatus.Stale)));
		ClaudeUsageProvider provider = new(
			poller,
			fallback,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Stale, snapshot.Status);
		Assert.Equal("claude@example.com", snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.None, snapshot.RecoveryAction);
		Assert.Equal(0, fallback.CallCount);
	}

	private static AccountProfile CreateAccount()
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Max",
			HasAcceptedClaudeQuotaRisk: true);
	}

	private static (
		AccountProfile Account,
		ClaudeAccountBinding Binding,
		ClaudeSubscriptionContext Context) CreatePrivateBindingFixture(
			string planTier = "max")
	{
		AccountProfile account = CreateAccount();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"Owner@Example.com",
				"organization:acme-company",
				planTier,
				"Acme Company");
		Guid publicBindingId = new("b65cf956-5b94-475c-aac3-0826b9a34be1");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			account.Id,
			publicBindingId,
			context);

		return (
			account with
			{
				ProviderAccountIdentity =
					ClaudeAccountBinding.CreatePublicBindingIdentity(
						publicBindingId)
			},
			binding,
			context);
	}

	private static UsageSnapshot CreateFallbackSnapshot(
		AccountProfile account,
		DateTimeOffset now,
		SnapshotStatus status = SnapshotStatus.Ready)
	{
		return new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("five_hour", "5 小時", 25, "已使用 25%")
			},
			SourceTrust.Official,
			status,
			now,
			now,
			now + TimeSpan.FromMinutes(2));
	}
}
