using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class GrokUsageProviderTests
{
	private const string PrincipalId = "opaque-grok-principal";
	private const string PrincipalType = "consumer";

	private sealed class FakeBindingStore : IGrokAccountBindingStore
	{
		private readonly GrokAccountBinding? _binding;
		private readonly IReadOnlyList<GrokAccountBinding> _bindings;
		private readonly Exception? _loadException;

		internal int LoadAllCallCount { get; private set; }

		internal int LoadCallCount { get; private set; }

		internal FakeBindingStore(
			GrokAccountBinding? binding,
			Exception? loadException = null,
			IReadOnlyList<GrokAccountBinding>? bindings = null)
		{
			_binding = binding;
			_loadException = loadException;
			_bindings = bindings ?? (binding is null
				? Array.Empty<GrokAccountBinding>()
				: new[] { binding });
		}

		public Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadAllCallCount++;
			return Task.FromResult(_bindings);
		}

		public Task<GrokAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			LoadCallCount++;

			if (_loadException is not null)
			{
				return Task.FromException<GrokAccountBinding?>(_loadException);
			}

			return Task.FromResult(_binding);
		}

		public Task SaveAsync(
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class FakePoller : IGrokUsagePoller
	{
		private readonly Func<Guid, CancellationToken, Task<GrokUsagePollResult>>
			_poll;

		internal int BoundCallCount { get; private set; }

		internal int CallCount => BoundCallCount + UnboundCallCount;

		internal GrokAccountBinding? LastBinding { get; private set; }

		internal int UnboundCallCount { get; private set; }

		internal FakePoller(
			Func<Guid, CancellationToken, Task<GrokUsagePollResult>> poll)
		{
			_poll = poll;
		}

		public Task<GrokUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			UnboundCallCount++;
			return _poll(accountId, cancellationToken);
		}

		public Task<GrokUsagePollResult> PollBoundAsync(
			Guid accountId,
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			BoundCallCount++;
			LastBinding = binding;
			return _poll(accountId, cancellationToken);
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
	public async Task GetUsageAsync_WithMatchingBinding_ReturnsOfficialWeeklyMetric()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		DateTimeOffset observedAt = now - TimeSpan.FromSeconds(2);
		DateTimeOffset resetsAt = now + TimeSpan.FromDays(3);
		GrokAccountBinding binding = CreateBinding();
		AccountProfile account = CreateAccount(binding);
		FakeBindingStore bindingStore = new(binding);
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(37.25, now - TimeSpan.FromDays(4), resetsAt),
			observedAt,
			"person@example.com",
			CliVersionPolicies.AssessGrok(new GrokExecutableVersion(1, 0, 4)));
		GrokUsageProvider provider = new(
			poller,
			bindingStore,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(ProviderKind.Grok, provider.Provider);
		Assert.Equal(TimeSpan.FromMinutes(15), provider.MinimumRefreshInterval);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal(now, snapshot.FetchedAt);
		Assert.Equal(observedAt, snapshot.ObservedAt);
		Assert.Equal(now + TimeSpan.FromMinutes(30), snapshot.StaleAfter);
		Assert.Equal(binding.PublicBindingId.ToString("N"), snapshot.ProviderAccountIdentity);
		Assert.Equal(
			"person@example.com",
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Null(snapshot.Error);
		Assert.Equal(UsageRecoveryAction.None, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Collection(
			snapshot.Metrics,
			metric =>
			{
				Assert.Equal("grok.weekly", metric.Key);
				Assert.Equal("週用量", metric.Label);
				Assert.Equal(37.25, metric.UsedPercent);
				Assert.Equal("已使用 37.25%", metric.DisplayValue);
				Assert.Equal(resetsAt, metric.ResetsAt);
			});
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(1, poller.CallCount);
		Assert.Equal(1, poller.BoundCallCount);
		Assert.Equal(0, poller.UnboundCallCount);
		Assert.Equal(binding, poller.LastBinding);
	}

	[Fact]
	public async Task GetUsageAsync_WhenUsageIsUnsupported_PreservesVerifiedBindingWithoutMetrics()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = CreatePoller(
			weeklyUsage: null,
			now,
			"person@example.com",
			usageAvailability: GrokUsageAvailability.Unsupported);
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(
			binding.PublicBindingId.ToString("N"),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			"person@example.com",
			snapshot.ProviderAccountDisplayIdentity);
		Assert.Contains("不會猜測用量", snapshot.Error, StringComparison.Ordinal);
		Assert.Equal(1, poller.BoundCallCount);
		Assert.Equal(0, poller.UnboundCallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenUsageProbeIsTransient_PreservesVerifiedBindingWithoutMetrics()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = CreatePoller(
			weeklyUsage: null,
			now,
			"person@example.com",
			usageAvailability: GrokUsageAvailability.TransientProbeError);
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(
			binding.PublicBindingId.ToString("N"),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(
			"person@example.com",
			snapshot.ProviderAccountDisplayIdentity);
	}

	[Fact]
	public async Task GetUsageAsync_WhenAuthenticationIsRequired_DoesNotTrustObservedEmail()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		const string untrustedEmail = "expired-profile@example.com";
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = CreatePoller(
			weeklyUsage: null,
			now,
			untrustedEmail,
			usageAvailability: GrokUsageAvailability.AuthenticationRequired,
			currentAuthUsability: GrokCurrentAuthUsability.Unusable);
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			UsageRecoveryAction.ConnectAccount,
			snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(
			binding.PublicBindingId.ToString("N"),
			snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.DoesNotContain(untrustedEmail, snapshot.Error ?? string.Empty);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBindingIsRejectedByPoller_RequiresSwitchWithoutPollingLeak()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = new((_, _) =>
			Task.FromException<GrokUsagePollResult>(
				new GrokPrincipalBindingInvalidException()));
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
		Assert.DoesNotContain(PrincipalId, snapshot.Error ?? string.Empty);
	}

	[Fact]
	public async Task GetUsageAsync_WhenUnverifiedVersionReturnsInvalidUsage_AppendsDiagnostic()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		GrokUsageProvider provider = new(
			CreatePoller(
				new GrokWeeklyUsage(25, now - TimeSpan.FromDays(7), now),
				now,
				versionEvidence: CliVersionPolicies.AssessGrok(
					new GrokExecutableVersion(1, 0, 4))),
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Contains("暫時無法讀取 Grok 用量", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("偵測到 Grok Build CLI 1.0.4", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("可能是原因之一", snapshot.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPercentIsMissing_ReturnsReadyUnknownMetric()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		GrokUsageProvider provider = new(
			CreatePoller(new GrokWeeklyUsage(null, now, now + TimeSpan.FromDays(7)), now),
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		UsageMetric metric = Assert.Single(snapshot.Metrics);
		Assert.Null(metric.UsedPercent);
		Assert.Equal("尚未提供用量", metric.DisplayValue);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public async Task GetUsageAsync_WhenResetIsNotFuture_ReturnsRetryWithoutMetrics(
		int resetOffsetSeconds)
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		GrokUsageProvider provider = new(
			CreatePoller(
				new GrokWeeklyUsage(
					25,
					now - TimeSpan.FromDays(7),
					now + TimeSpan.FromSeconds(resetOffsetSeconds)),
				now),
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			binding.PublicBindingId.ToString("N"),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBindingIsCorrupt_RequiresReconnectWithoutPolling()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakeBindingStore bindingStore = new(
			binding,
			new InvalidDataException("secret corrupt binding detail"));
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		GrokUsageProvider provider = new(
			poller,
			bindingStore,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.DoesNotContain("secret", snapshot.Error ?? string.Empty);
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, poller.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBindingIsMissing_RequiresConnectionWithoutPolling()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		FakeBindingStore bindingStore = new(null);
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		GrokUsageProvider provider = new(
			poller,
			bindingStore,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Grok, "Grok"),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, poller.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBindingBelongsToAnotherCard_RequiresSwitchWithoutPolling()
	{
		DateTimeOffset now = new(2026, 8, 29, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: binding.PublicBindingId.ToString("N"));
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Empty(snapshot.Metrics);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Equal(0, poller.CallCount);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("00000000000000000000000000000000")]
	[InlineData("NOT-A-CANONICAL-BINDING-ID")]
	public async Task GetUsageAsync_WhenProfileBindingIsMissingOrDifferent_RequiresSwitchWithoutPolling(
		string? profileIdentity)
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));
		AccountProfile account = new(
			binding.AccountId,
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: profileIdentity);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Null(snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(0, poller.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenProfileBindingUsesUppercase_IsRejectedAsNonCanonical()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		binding = binding with
		{
			PublicBindingId = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab")
		};
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));
		AccountProfile account = CreateAccount(binding) with
		{
			ProviderAccountIdentity = binding.PublicBindingId
				.ToString("N")
				.ToUpperInvariant()
		};

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(0, poller.CallCount);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPrincipalDoesNotMatch_RequiresSwitchWithoutLeakingPrincipal()
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = new((_, _) =>
			Task.FromException<GrokUsagePollResult>(
				new GrokPrincipalMismatchException()));
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			binding.PublicBindingId.ToString("N"),
			snapshot.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.DoesNotContain(PrincipalId, snapshot.Error ?? string.Empty);
		Assert.DoesNotContain("CLI 版本診斷", snapshot.Error ?? string.Empty);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			snapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task GetUsageAsync_WhenPrincipalIsOwnedByAnotherBinding_FailsClosed()
	{
		DateTimeOffset now = new(2026, 8, 30, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		GrokAccountBinding duplicateBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			PrincipalType,
			PrincipalId);
		FakeBindingStore bindingStore = new(
			binding,
			bindings: new[] { binding, duplicateBinding });
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(10, now, now + TimeSpan.FromDays(7)),
			now);
		GrokUsageProvider provider = new(
			poller,
			bindingStore,
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.SwitchAccount, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.DefiniteScopeMismatch,
			snapshot.SubscriptionVerificationState);
		Assert.Equal(1, poller.BoundCallCount);
		Assert.Equal(1, bindingStore.LoadAllCallCount);
		Assert.DoesNotContain(PrincipalId, snapshot.Error ?? string.Empty);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("untrusted")]
	[InlineData("compatibility")]
	public async Task GetUsageAsync_WhenCliCannotBeUsed_RequiresInstallOrUpdate(
		string failureKind)
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync(failureKind);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.InstallOrUpdate, snapshot.RecoveryAction);
	}

	[Theory]
	[InlineData("authentication")]
	[InlineData("not-configured")]
	public async Task GetUsageAsync_WhenAuthenticationIsRequired_RequiresConnection(
		string failureKind)
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync(failureKind);

		Assert.Equal(SnapshotStatus.NotConfigured, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			snapshot.SubscriptionVerificationState);
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
	}

	[Fact]
	public async Task GetUsageAsync_WhenBillingIsUnsupported_RetriesWithoutGuessingMetrics()
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync("unsupported-billing");

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			snapshot.SubscriptionVerificationState);
		Assert.DoesNotContain("secret", snapshot.Error ?? string.Empty);
	}

	[Fact]
	public async Task GetUsageAsync_WhenContainmentCannotBeConfirmed_RequiresApplicationRestart()
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync("containment");

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(
			UsageRecoveryAction.RestartApplication,
			snapshot.RecoveryAction);
	}

	[Theory]
	[InlineData("transient")]
	[InlineData("unknown")]
	[InlineData("schema")]
	[InlineData("invalid-data")]
	[InlineData("io")]
	[InlineData("timeout")]
	public async Task GetUsageAsync_WhenFailureCanRecover_ReturnsRetry(
		string failureKind)
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync(failureKind);

		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(
			SubscriptionVerificationState.TransientProbeError,
			snapshot.SubscriptionVerificationState);
		Assert.True(GrokAccountBinding.TryNormalizePublicBindingIdentity(
			snapshot.ProviderAccountIdentity,
			out _));
		Assert.Null(snapshot.ProviderAccountDisplayIdentity);
	}

	[Theory]
	[InlineData("compatibility")]
	[InlineData("unknown")]
	[InlineData("schema")]
	[InlineData("invalid-data")]
	public async Task GetUsageAsync_WhenUnverifiedCliFailureIsRelevant_AppendsDiagnostic(
		string failureKind)
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync(
			failureKind,
			CliVersionPolicies.AssessGrok(new GrokExecutableVersion(1, 0, 4)));

		Assert.Contains("偵測到 Grok Build CLI 1.0.4", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("相容性基準為 1.0.3", snapshot.Error, StringComparison.Ordinal);
		Assert.Contains("可能是原因之一", snapshot.Error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("untrusted")]
	[InlineData("authentication")]
	[InlineData("not-configured")]
	[InlineData("unsupported-billing")]
	[InlineData("transient")]
	[InlineData("io")]
	[InlineData("containment")]
	[InlineData("timeout")]
	public async Task GetUsageAsync_WhenFailureIsNotVersionRelevant_DoesNotAppendDiagnostic(
		string failureKind)
	{
		UsageSnapshot snapshot = await GetFailureSnapshotAsync(
			failureKind,
			CliVersionPolicies.AssessGrok(new GrokExecutableVersion(1, 0, 4)));

		Assert.DoesNotContain("CLI 版本診斷", snapshot.Error ?? string.Empty);
	}

	[Fact]
	public async Task GetUsageAsync_WhenCallerCancels_PropagatesCancellation()
	{
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = new((_, token) =>
			Task.FromCanceled<GrokUsagePollResult>(token));
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.GetUsageAsync(
				CreateAccount(binding),
				cancellationSource.Token));
	}

	[Fact]
	public async Task GetUsageAsync_WithWrongProvider_ThrowsBeforeLoadingOrPolling()
	{
		GrokAccountBinding binding = CreateBinding();
		FakeBindingStore bindingStore = new(binding);
		FakePoller poller = CreatePoller(
			new GrokWeeklyUsage(
				10,
				DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow + TimeSpan.FromDays(7)),
			DateTimeOffset.UtcNow);
		GrokUsageProvider provider = new(poller, bindingStore);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			provider.GetUsageAsync(account, CancellationToken.None));

		Assert.Equal("account", exception.ParamName);
		Assert.Equal(0, bindingStore.LoadCallCount);
		Assert.Equal(0, poller.CallCount);
	}

	private static AccountProfile CreateAccount(GrokAccountBinding binding)
	{
		return new AccountProfile(
			binding.AccountId,
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: binding.PublicBindingId.ToString("N"));
	}

	private static GrokAccountBinding CreateBinding()
	{
		return GrokAccountBinding.Create(
			Guid.NewGuid(),
			PrincipalType,
			PrincipalId);
	}

	private static FakePoller CreatePoller(
		GrokWeeklyUsage? weeklyUsage,
		DateTimeOffset observedAt,
		string? accountEmail = null,
		CliVersionEvidence? versionEvidence = null,
		GrokUsageAvailability usageAvailability =
			GrokUsageAvailability.Available,
		GrokCurrentAuthUsability currentAuthUsability =
			GrokCurrentAuthUsability.Usable)
	{
		return new FakePoller((_, _) => Task.FromResult(
			new GrokUsagePollResult(
				new GrokPrincipal(PrincipalType, PrincipalId, accountEmail),
				weeklyUsage,
				observedAt,
				versionEvidence,
				usageAvailability,
				currentAuthUsability)));
	}

	private static Exception CreateFailure(
		string failureKind,
		CliVersionEvidence? versionEvidence = null)
	{
		Exception exception = failureKind switch
		{
			"missing" => new GrokCliNotFoundException("secret executable path"),
			"untrusted" => new GrokCliUntrustedException("secret signer detail"),
			"compatibility" => new GrokAcpFailureException(
				"secret-method",
				GrokAcpFailureCategory.Compatibility),
			"authentication" => new GrokAcpFailureException(
				"secret-method",
				GrokAcpFailureCategory.Authentication),
			"not-configured" => new GrokUsageNotConfiguredException(
				"secret principal"),
			"unsupported-billing" => new GrokUnsupportedBillingException(
				"secret billing detail"),
			"transient" => new GrokAcpFailureException(
				"secret-method",
				GrokAcpFailureCategory.Transient),
			"unknown" => new GrokAcpFailureException(
				"secret-method",
				GrokAcpFailureCategory.Unknown),
			"schema" => new GrokUsageSchemaException("secret response"),
			"invalid-data" => new InvalidDataException("secret poll result"),
			"io" => new IOException("secret path"),
			"containment" => new GrokProcessContainmentException(
				"secret containment detail"),
			"timeout" => new TimeoutException("secret timeout"),
			_ => throw new ArgumentOutOfRangeException(nameof(failureKind))
		};

		if (versionEvidence is not null)
		{
			exception.AttachVersionEvidence(versionEvidence);
		}

		return exception;
	}

	private static async Task<UsageSnapshot> GetFailureSnapshotAsync(
		string failureKind,
		CliVersionEvidence? versionEvidence = null)
	{
		DateTimeOffset now = new(2026, 8, 18, 1, 30, 0, TimeSpan.Zero);
		GrokAccountBinding binding = CreateBinding();
		FakePoller poller = new((_, _) =>
			Task.FromException<GrokUsagePollResult>(CreateFailure(
				failureKind,
				versionEvidence)));
		GrokUsageProvider provider = new(
			poller,
			new FakeBindingStore(binding),
			new FakeTimeProvider(now));

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			CreateAccount(binding),
			CancellationToken.None);

		Assert.DoesNotContain("secret", snapshot.Error ?? string.Empty);
		return snapshot;
	}
}
