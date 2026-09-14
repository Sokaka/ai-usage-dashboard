#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.Text.Json;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

using GitHub.Copilot.Rpc;

namespace AiUsageDashboard.Tests;

public sealed class CopilotSdkQuotaIsolationTests
{
	private sealed class SubscriptionTestTimeProvider : TimeProvider
	{
		public override DateTimeOffset GetUtcNow()
		{
			return DateTimeOffset.Parse("2026-09-01T12:00:00Z");
		}
	}

	private sealed record FactoryCall(
		string HomeDirectory,
		string AccessToken,
		FakeSdkClient Client);

	private sealed class DisposalObservedFileStream : FileStream
	{
		private readonly TaskCompletionSource<bool> _disposed = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal Task Disposed => _disposed.Task;

		internal DisposalObservedFileStream(string path)
			: base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
		{
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (disposing)
			{
				_disposed.TrySetResult(true);
			}
		}
	}

	private sealed class FakeCredentialStore : ICopilotCredentialStore
	{
		private readonly IReadOnlyDictionary<Guid, CopilotStoredCredential>
			_credentials;

		internal FakeCredentialStore(
			IReadOnlyDictionary<Guid, CopilotStoredCredential> credentials)
		{
			_credentials = credentials;
		}

		public CopilotStoredCredential? Read(Guid accountId)
		{
			return _credentials.GetValueOrDefault(accountId);
		}

		public void Stage(Guid accountId, CopilotStoredCredential credential)
		{
			throw new NotSupportedException();
		}

		public bool CommitStaged(
			Guid accountId,
			string expectedProviderAccountIdentity)
		{
			throw new NotSupportedException();
		}

		public void RecoverStaged(
			Guid accountId,
			string? expectedProviderAccountIdentity)
		{
		}

		public void DiscardStaged(Guid accountId)
		{
			throw new NotSupportedException();
		}

		public void Delete(Guid accountId)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class FakeGitHubUserClient : ICopilotGitHubUserClient
	{
		private readonly IReadOnlyDictionary<string, CopilotAccountIdentity>
			_identitiesByToken;

		internal FakeGitHubUserClient(
			IReadOnlyDictionary<string, CopilotAccountIdentity> identitiesByToken)
		{
			_identitiesByToken = identitiesByToken;
		}

		public Task<CopilotAccountIdentity> GetAuthenticatedUserAsync(
			string host,
			string accessToken,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_identitiesByToken[accessToken]);
		}
	}

	private sealed class ParallelStartBarrier
	{
		private readonly int _expectedArrivalCount;
		private readonly TaskCompletionSource<bool> _allArrived = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _arrivalCount;

		internal int ArrivalCount => Volatile.Read(ref _arrivalCount);

		internal ParallelStartBarrier(int expectedArrivalCount)
		{
			_expectedArrivalCount = expectedArrivalCount;
		}

		internal async Task ArriveAndWaitAsync(
			CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _arrivalCount) == _expectedArrivalCount)
			{
				_allArrived.TrySetResult(true);
			}

			await _allArrived.Task.WaitAsync(cancellationToken);
		}
	}

	private sealed class FakeSdkClient : ICopilotSdkClient
	{
		private static readonly IReadOnlyDictionary<string, CopilotSdkQuotaValue>
			DefaultQuota = new Dictionary<string, CopilotSdkQuotaValue>(
				StringComparer.Ordinal)
			{
				["premium_interactions"] = new CopilotSdkQuotaValue(
					300,
					25,
					91.67,
					DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
					IsUnlimitedEntitlement: false,
					OverageAllowedWithExhaustedQuota: false,
					UsageAllowedWithExhaustedQuota: false)
			};

		private readonly AccountGetCurrentAuthResult? _currentAuth;
		private readonly Func<ValueTask> _disposeAction;
		private readonly Func<Task> _forceStopAction;
		private readonly IReadOnlyDictionary<string, CopilotSdkQuotaValue> _quota;
		private readonly Func<CancellationToken, Task> _startAction;
		private readonly Func<Task> _stopAction;
		private readonly Func<string, string, CancellationToken, Task<CopilotSdkSubscriptionMetadata?>>?
			_subscriptionAction;
		private int _startCount;

		internal int StartCount => Volatile.Read(ref _startCount);

		internal FakeSdkClient(
			Func<CancellationToken, Task>? startAction = null,
			AccountGetCurrentAuthResult? currentAuth = null,
			IReadOnlyDictionary<string, CopilotSdkQuotaValue>? quota = null,
			Func<Task>? stopAction = null,
			Func<Task>? forceStopAction = null,
			Func<ValueTask>? disposeAction = null,
			Func<string, string, CancellationToken, Task<CopilotSdkSubscriptionMetadata?>>?
				subscriptionAction = null)
		{
			_currentAuth = currentAuth;
			_disposeAction = disposeAction ?? (static () => ValueTask.CompletedTask);
			_forceStopAction = forceStopAction ?? (static () => Task.CompletedTask);
			_quota = quota ?? DefaultQuota;
			_startAction = startAction ?? (static _ => Task.CompletedTask);
			_stopAction = stopAction ?? (static () => Task.CompletedTask);
			_subscriptionAction = subscriptionAction;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _startCount);
			return _startAction(cancellationToken);
		}

		public Task<CopilotSdkSubscriptionMetadata?>
			GetSubscriptionMetadataAsync(
			string expectedHost,
			string expectedLogin,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_subscriptionAction is not null)
			{
				return _subscriptionAction(expectedHost, expectedLogin, cancellationToken);
			}
			return Task.FromResult(
				ReadSubscriptionMetadata(
					_currentAuth,
					expectedHost,
					expectedLogin));
		}

		public Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>>
			GetQuotaAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_quota);
		}

		public Task StopAsync()
		{
			return _stopAction();
		}

		public Task ForceStopAsync()
		{
			return _forceStopAction();
		}

		public ValueTask DisposeAsync()
		{
			return _disposeAction();
		}
	}

	[Fact]
	public void TryGetMatchingSubscriptionMetadata_PrefersQuotaBillingFlag()
	{
		const string CurrentAuthJson = """
			{
			  "authErrors": [],
			  "authInfo": {
			    "type": "user",
			    "host": "github.com",
			    "login": "credits-user",
			    "copilotUser": {
			      "login": "credits-user",
			      "copilot_plan": "individual",
			      "token_based_billing": false,
			      "quota_snapshots": {
			        "premium_interactions": {
			          "token_based_billing": true
			        }
			      }
			    }
			  }
			}
			""";
		AccountGetCurrentAuthResult currentAuth =
			Assert.IsType<AccountGetCurrentAuthResult>(
				JsonSerializer.Deserialize<AccountGetCurrentAuthResult>(
					CurrentAuthJson));

		CopilotSdkSubscriptionMetadata metadata = Assert.IsType<
			CopilotSdkSubscriptionMetadata>(
			ReadSubscriptionMetadata(
				currentAuth,
				"github.com",
				"credits-user"));

		Assert.Equal("individual", metadata.PlanTier);
		Assert.True(metadata.IsTokenBasedBilling);
	}

	[Fact]
	public async Task GetAccountQuotaAsync_PassesEachCardsCredentialToItsOwnFactory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		CopilotAccountIdentity firstPrincipal = CreatePrincipal(
			"NODE_FIRST",
			"first-login",
			1001);
		CopilotAccountIdentity secondPrincipal = CreatePrincipal(
			"NODE_SECOND",
			"second-login",
			1002);
		const string firstToken = "synthetic-first-account-token";
		const string secondToken = "synthetic-second-account-token";
		ConcurrentBag<FactoryCall> factoryCalls = new();
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential>
			{
				[firstAccountId] = CreateCredential(firstPrincipal, firstToken),
				[secondAccountId] = CreateCredential(secondPrincipal, secondToken)
			},
			new Dictionary<string, CopilotAccountIdentity>(StringComparer.Ordinal)
			{
				[firstToken] = firstPrincipal,
				[secondToken] = secondPrincipal
			},
			(homeDirectory, accessToken) =>
			{
				FakeSdkClient sdkClient = new();
				factoryCalls.Add(new FactoryCall(
					homeDirectory,
					accessToken,
					sdkClient));
				return sdkClient;
			});

		CopilotUsageReport firstReport = await client.GetAccountQuotaAsync(
			firstAccountId,
			CopilotAccountIdentityRules.Create(firstPrincipal));
		CopilotUsageReport secondReport = await client.GetAccountQuotaAsync(
			secondAccountId,
			CopilotAccountIdentityRules.Create(secondPrincipal));

		FactoryCall firstCall = Assert.Single(factoryCalls,
			call => string.Equals(
				call.AccessToken,
				firstToken,
				StringComparison.Ordinal));
		FactoryCall secondCall = Assert.Single(factoryCalls,
			call => string.Equals(
				call.AccessToken,
				secondToken,
				StringComparison.Ordinal));
		Assert.Equal(
			GetExpectedHome(temporaryDirectory.Path, firstAccountId),
			firstCall.HomeDirectory);
		Assert.Equal(
			GetExpectedHome(temporaryDirectory.Path, secondAccountId),
			secondCall.HomeDirectory);
		Assert.DoesNotContain(secondToken, firstCall.AccessToken);
		Assert.DoesNotContain(firstToken, secondCall.AccessToken);
		Assert.Equal(firstPrincipal, firstReport.Account);
		Assert.Equal(secondPrincipal, secondReport.Account);
		Assert.Equal(1, firstCall.Client.StartCount);
		Assert.Equal(1, secondCall.Client.StartCount);
	}

	[Fact]
	public async Task GetUsageAsync_FromBusinessCurrentAuth_ProjectsVerifiedPlanAndIdentity()
	{
		const string CurrentAuthJson = """
			{
			  "authErrors": [],
			  "authInfo": {
			    "type": "user",
			    "host": "github.com",
			    "login": "business-user",
				    "copilotUser": {
				      "login": "business-user",
				      "copilot_plan": "business",
				      "token_based_billing": true
				    }
			  }
			}
			""";
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_BUSINESS",
			"business-user",
			1003);
		const string AccessToken = "synthetic-business-token";
		AccountProfile account = new(
			accountId,
			ProviderKind.Copilot,
			string.Empty,
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(principal));
		AccountGetCurrentAuthResult currentAuth = Assert.IsType<AccountGetCurrentAuthResult>(
			JsonSerializer.Deserialize<AccountGetCurrentAuthResult>(CurrentAuthJson));
		IReadOnlyDictionary<string, CopilotSdkQuotaValue> organizationQuota =
			new Dictionary<string, CopilotSdkQuotaValue>(StringComparer.Ordinal)
			{
				["premium_interactions"] = new CopilotSdkQuotaValue(
					0,
					0,
					100,
					null,
					IsUnlimitedEntitlement: true,
					OverageAllowedWithExhaustedQuota: true,
					UsageAllowedWithExhaustedQuota: false),
				["chat"] = new CopilotSdkQuotaValue(
					0,
					0,
					100,
					null,
					IsUnlimitedEntitlement: true,
					OverageAllowedWithExhaustedQuota: false,
					UsageAllowedWithExhaustedQuota: false),
				["completions"] = new CopilotSdkQuotaValue(
					-1,
					0,
					100,
					null,
					IsUnlimitedEntitlement: true,
					OverageAllowedWithExhaustedQuota: false,
					UsageAllowedWithExhaustedQuota: false)
			};
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential>
			{
				[accountId] = CreateCredential(principal, AccessToken)
			},
			new Dictionary<string, CopilotAccountIdentity>(StringComparer.Ordinal)
			{
				[AccessToken] = principal
			},
			(_, _) => new FakeSdkClient(
				currentAuth: currentAuth,
				quota: organizationQuota));
		CopilotUsageProvider provider = new(client);

		UsageSnapshot snapshot = await provider.GetUsageAsync(
			account,
			CancellationToken.None);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.ApplyLiveSnapshot(snapshot);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			snapshot.SubscriptionVerificationState);
		Assert.Equal("Business", snapshot.PlanTier);
		Assert.Equal("@business-user", snapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(account.ProviderAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(account.ProviderAccountIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal("Business", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("GitHub Copilot · Business", viewModel.AccountHeaderText);
		Assert.Equal(" · Business", viewModel.AccountHeaderSuffixText);
		Assert.Equal("@business-user", viewModel.AccountCardDisplayText);
		Assert.Equal(
			"@business-user · 方案 Business",
			viewModel.AccountDisplayText);
		UsageMetricViewModel premium = viewModel.UsageMetrics.Single(metric =>
			metric.Key == "copilot-quota-premium-interactions");
		Assert.Equal("AI Credits", premium.Label);
		Assert.Equal(
			"已使用 0；上限由共用額度與預算控制",
			premium.DisplayValue);
		Assert.Equal(premium.DisplayValue, premium.ToolTipValue);
		Assert.DoesNotContain(
			viewModel.UsageMetrics,
			metric => metric.Key == "copilot-quota-chat");
		UsageMetricViewModel completions = viewModel.UsageMetrics.Single(metric =>
			metric.Key == "copilot-quota-completions");
		Assert.Equal("已使用 0 次（無上限）", completions.DisplayValue);
		Assert.Equal("已使用 0 次（無上限）", completions.ToolTipValue);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetUsageAsync_WithOldOrCredentialFreeResponse_PreservesExistingAccountAndCache(
		bool includesToken)
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		CopilotAccountIdentity principal = CreatePrincipal("NODE_UPGRADE", "upgrade-user", 1007);
		CopilotStoredCredential credential = CreateCredential(principal, "synthetic-upgrade-token");
		string settingsPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string settingsFixture = JsonSerializer.Serialize(new
		{
			schemaVersion = 7,
			accounts = new[]
			{
				new
				{
					id = accountId, provider = "Copilot", displayName = "工作帳號",
					isEnabled = true, providerAccountIdentity = credential.ProviderAccountIdentity,
					hasAcceptedClaudeQuotaRisk = false, showSubscriptionContext = true
				},
				new
				{
					id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
					provider = "Copilot", displayName = "第二張卡片", isEnabled = false,
					providerAccountIdentity = CopilotAccountIdentityRules.Create(
						CreatePrincipal("NODE_SECOND_UPGRADE", "second-upgrade-user", 1010)),
					hasAcceptedClaudeQuotaRisk = false, showSubscriptionContext = false
				}
			}
		});
		await File.WriteAllTextAsync(settingsPath, settingsFixture);
		JsonAccountProfileStore accountStore = new(settingsPath);
		var loadedAccounts = await accountStore.LoadAsync();
		Assert.True(loadedAccounts.CanSave);
		Assert.Equal(AiUsageDashboard.Core.Persistence.AccountProfileLoadStatus.Loaded, loadedAccounts.Status);
		Assert.Equal(2, loadedAccounts.Accounts.Count);
		AccountProfile account = loadedAccounts.Accounts[0];
		string cachePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		string cacheFixture = JsonSerializer.Serialize(new
		{
			schemaVersion = 3, accountId, provider = "Copilot", sourceTrust = "OfficialExperimental",
			fetchedAt = "2026-09-01T11:59:00Z", observedAt = "2026-09-01T11:59:00Z",
			staleAfter = "2026-09-01T12:02:00Z",
			providerAccountIdentity = credential.ProviderAccountIdentity,
			providerAccountDisplayIdentity = "@upgrade-user", subscriptionScopeDisplayName = (string?)null,
			planTier = (string?)null, subscriptionVerificationState = "Verified",
			metrics = new[]
			{
				new
				{
					key = "copilot-quota-premium-interactions", label = "Premium usage", usedPercent = 8.33,
					displayValue = "25 / 300 次", resetsAt = "2026-10-01T00:00:00Z", resetDisplayValue = (string?)null
				}
			}
		});
		await File.WriteAllTextAsync(cachePath, cacheFixture);
		JsonUsageSnapshotStore cache = new((_, _) => cachePath, new SubscriptionTestTimeProvider());
		UsageSnapshot cached = Assert.IsType<UsageSnapshot>(await cache.LoadAsync(account));
		Assert.Equal(SnapshotStatus.Stale, cached.Status);
		Assert.Equal("Premium usage", Assert.Single(cached.Metrics).Label);
		Assert.Null(cached.PlanTier);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.ApplyLiveSnapshot(cached);

		Dictionary<string, object?> auth = new()
		{
			["type"] = "token", ["host"] = "https://github.com",
			["copilotUser"] = new
			{
				login = "upgrade-user", copilot_plan = "individual", token_based_billing = true
			}
		};
		if (includesToken)
		{
			auth["token"] = "synthetic-old-response-token";
		}
		JsonElement currentAuth = JsonSerializer.SerializeToElement(new { authInfo = auth });
		Dictionary<Guid, CopilotStoredCredential> credentials = new() { [accountId] = credential };
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path, credentials,
			new Dictionary<string, CopilotAccountIdentity> { [credential.AccessToken] = principal },
			(_, accessToken) =>
			{
				Assert.Equal(credential.AccessToken, accessToken);
				return new FakeSdkClient(subscriptionAction: (host, login, _) =>
					Task.FromResult(CopilotSubscriptionMetadataReader.Read(currentAuth, host, login)));
			});
		UsageSnapshot refreshed = await new CopilotUsageProvider(client).GetUsageAsync(account, CancellationToken.None);
		viewModel.ApplyLiveSnapshot(refreshed);

		Assert.Equal(SnapshotStatus.Ready, refreshed.Status);
		Assert.Equal("Pro", refreshed.PlanTier);
		Assert.Equal(SubscriptionVerificationState.Verified, refreshed.SubscriptionVerificationState);
		Assert.Equal(credential.ProviderAccountIdentity, refreshed.ProviderAccountIdentity);
		Assert.Equal("AI Credits", Assert.Single(viewModel.UsageMetrics).Label);
		Assert.Equal("Pro", viewModel.SubscriptionPlanDisplayText);
		Assert.Empty(viewModel.NoticeText);
		Assert.Same(credential, credentials[accountId]);
		Assert.Equal(settingsFixture, await File.ReadAllTextAsync(settingsPath));
		await accountStore.SaveAsync(loadedAccounts.Accounts);
		Assert.Equal(loadedAccounts.Accounts, (await new JsonAccountProfileStore(settingsPath).LoadAsync()).Accounts);
		await cache.SaveAsync(refreshed);
		UsageSnapshot reloaded = Assert.IsType<UsageSnapshot>(await cache.LoadAsync(account));
		Assert.Equal("AI Credits", Assert.Single(reloaded.Metrics).Label);
		Assert.Equal("Pro", reloaded.PlanTier);
		Assert.Equal(account, reloaded.Account);
		Assert.DoesNotContain(credential.AccessToken, await File.ReadAllTextAsync(cachePath));
		Assert.DoesNotContain("synthetic-old-response-token", await File.ReadAllTextAsync(cachePath));
	}

	[Theory]
	[InlineData("missing", "未提供", true)]
	[InlineData("json", "格式或帳號無法確認", true)]
	[InlineData("identity", "格式或帳號無法確認", true)]
	[InlineData("timeout", "逾時", true)]
	[InlineData("contract", "介面不相容", true)]
	[InlineData("rpc", "暫時無法讀取", true)]
	[InlineData("sync", "暫時無法讀取", true)]
	[InlineData("rpc", "診斷紀錄無法寫入", false)]
	public async Task GetUsageAsync_WhenSubscriptionFails_KeepsQuotaAndShowsSafeDiagnostic(
		string failureKind, string expectedNotice, bool diagnosticWriteSucceeds)
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
		CopilotAccountIdentity principal = CreatePrincipal("NODE_WARNING", "warning-user", 1008);
		CopilotStoredCredential credential = CreateCredential(principal, "synthetic-private-token");
		AccountProfile account = new(accountId, ProviderKind.Copilot, "工作帳號",
			ProviderAccountIdentity: credential.ProviderAccountIdentity);
		const string privateMessage = "synthetic-sensitive-response-and-login";
		Exception? failure = failureKind switch
		{
			"missing" => null,
			"json" => new JsonException(privateMessage),
			"identity" => new InvalidDataException(privateMessage),
			"timeout" => new TimeoutException(privateMessage),
			"contract" => new NotSupportedException(privateMessage),
			"rpc" or "sync" => new IOException(privateMessage),
			_ => throw new ArgumentException("Unknown synthetic failure kind.", nameof(failureKind))
		};
		int diagnosticCount = 0;
		string diagnosticPath = Path.Combine(temporaryDirectory.Path, "diagnostics.log");
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential> { [accountId] = credential },
			new Dictionary<string, CopilotAccountIdentity> { [credential.AccessToken] = principal },
			(_, _) => new FakeSdkClient(subscriptionAction: (_, _, _) =>
			{
				if ((failureKind == "sync") && (failure is not null))
				{
					throw failure;
				}
				return failure is null
					? Task.FromResult<CopilotSdkSubscriptionMetadata?>(null)
					: Task.FromException<CopilotSdkSubscriptionMetadata?>(failure);
			}),
			reportSubscriptionDiagnostic: (summary, exception) =>
			{
				diagnosticCount++;
				Assert.Same(failure, exception);
				return diagnosticWriteSucceeds && AppDiagnostics.TryWrite(
					diagnosticPath, "copilot-subscription", summary, exception,
					new SubscriptionTestTimeProvider().GetUtcNow()).WasWritten;
			});

		UsageSnapshot snapshot = await new CopilotUsageProvider(client).GetUsageAsync(account, CancellationToken.None);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.ApplyLiveSnapshot(snapshot);

		Assert.Equal(1, diagnosticCount);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal("Premium usage", Assert.Single(viewModel.UsageMetrics).Label);
		Assert.Null(snapshot.PlanTier);
		Assert.Equal(credential.ProviderAccountIdentity, snapshot.ProviderAccountIdentity);
		Assert.Contains(expectedNotice, viewModel.NoticeText);
		Assert.DoesNotContain(privateMessage, viewModel.NoticeText);
		if (diagnosticWriteSucceeds)
		{
			string diagnostic = await File.ReadAllTextAsync(diagnosticPath);
			Assert.Contains("copilot-subscription", diagnostic);
			Assert.DoesNotContain(privateMessage, diagnostic);
			Assert.DoesNotContain(credential.AccessToken, diagnostic);
			Assert.DoesNotContain(principal.Login, diagnostic);
		}
		viewModel.ApplyLiveSnapshot(snapshot with { Error = null });
		Assert.Empty(viewModel.NoticeText);
	}

	[Fact]
	public async Task GetAccountQuotaAsync_WhenCallerCancelsSubscription_PropagatesCancellation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using CancellationTokenSource cancellation = new();
		Guid accountId = Guid.Parse("44444444-4444-4444-4444-444444444444");
		CopilotAccountIdentity principal = CreatePrincipal("NODE_CANCEL", "cancel-user", 1009);
		CopilotStoredCredential credential = CreateCredential(principal, "synthetic-cancel-token");
		int diagnosticCount = 0;
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential> { [accountId] = credential },
			new Dictionary<string, CopilotAccountIdentity> { [credential.AccessToken] = principal },
			(_, _) => new FakeSdkClient(subscriptionAction: (_, _, token) =>
			{
				cancellation.Cancel();
				return Task.FromCanceled<CopilotSdkSubscriptionMetadata?>(token);
			}),
			reportSubscriptionDiagnostic: (_, _) => { diagnosticCount++; return true; });

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAccountQuotaAsync(
			accountId, credential.ProviderAccountIdentity, cancellation.Token));
		Assert.Equal(0, diagnosticCount);
	}

	[Fact]
	public void SanitizedSdkEnvironment_DropsAmbientCredentialVariables()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Dictionary<string, string?> ambientEnvironment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			["COPILOT_GITHUB_TOKEN"] = "ambient-copilot-token",
			["GH_TOKEN"] = "ambient-gh-token",
			["GITHUB_TOKEN"] = "ambient-github-token",
			["HOME"] = @"C:\ambient-home",
			["HTTPS_PROXY"] = "https://proxy.example.test:8443"
		};

		IReadOnlyDictionary<string, string> environment =
			CopilotProcessEnvironment.CreateSanitizedEnvironment(
				ambientEnvironment,
				temporaryDirectory.Path);

		Assert.False(environment.ContainsKey("COPILOT_GITHUB_TOKEN"));
		Assert.False(environment.ContainsKey("GH_TOKEN"));
		Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
		Assert.Equal(
			Path.GetFullPath(temporaryDirectory.Path),
			environment["HOME"]);
		Assert.Equal(
			"https://proxy.example.test:8443",
			environment["HTTPS_PROXY"]);
	}

	[Fact]
	public async Task GetAccountQuotaAsync_WhenPrincipalSwaps_FailsBeforeSdkStarts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity storedPrincipal = CreatePrincipal(
			"NODE_STORED",
			"stored-login",
			1001);
		CopilotAccountIdentity swappedPrincipal = CreatePrincipal(
			"NODE_SWAPPED",
			"swapped-login",
			1002);
		const string accessToken = "synthetic-swapped-account-token";
		FakeSdkClient sdkClient = new();
		int factoryInvocationCount = 0;
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential>
			{
				[accountId] = CreateCredential(storedPrincipal, accessToken)
			},
			new Dictionary<string, CopilotAccountIdentity>(StringComparer.Ordinal)
			{
				[accessToken] = swappedPrincipal
			},
			(_, _) =>
			{
				Interlocked.Increment(ref factoryInvocationCount);
				return sdkClient;
			});

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				client.GetAccountQuotaAsync(
					accountId,
					CopilotAccountIdentityRules.Create(storedPrincipal)));

		Assert.Equal(CopilotFailureKind.AccountMismatch, failure.Kind);
		Assert.Equal(0, Volatile.Read(ref factoryInvocationCount));
		Assert.Equal(0, sdkClient.StartCount);
	}

	[Fact]
	public async Task GetAccountQuotaAsync_ForDifferentAccounts_CanRunInParallel()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		CopilotAccountIdentity firstPrincipal = CreatePrincipal(
			"NODE_FIRST",
			"first-login",
			1001);
		CopilotAccountIdentity secondPrincipal = CreatePrincipal(
			"NODE_SECOND",
			"second-login",
			1002);
		const string firstToken = "synthetic-first-parallel-token";
		const string secondToken = "synthetic-second-parallel-token";
		ParallelStartBarrier startBarrier = new(expectedArrivalCount: 2);
		CopilotSdkQuotaClient client = CreateClient(
			temporaryDirectory.Path,
			new Dictionary<Guid, CopilotStoredCredential>
			{
				[firstAccountId] = CreateCredential(firstPrincipal, firstToken),
				[secondAccountId] = CreateCredential(secondPrincipal, secondToken)
			},
			new Dictionary<string, CopilotAccountIdentity>(StringComparer.Ordinal)
			{
				[firstToken] = firstPrincipal,
				[secondToken] = secondPrincipal
			},
			(_, _) => new FakeSdkClient(startBarrier.ArriveAndWaitAsync),
			operationTimeout: TimeSpan.FromSeconds(5));

		Task<CopilotUsageReport> firstTask = client.GetAccountQuotaAsync(
			firstAccountId,
			CopilotAccountIdentityRules.Create(firstPrincipal));
		Task<CopilotUsageReport> secondTask = client.GetAccountQuotaAsync(
			secondAccountId,
			CopilotAccountIdentityRules.Create(secondPrincipal));
		CopilotUsageReport[] reports = await Task.WhenAll(firstTask, secondTask)
			.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(2, startBarrier.ArrivalCount);
		Assert.Contains(reports, report => report.Account == firstPrincipal);
		Assert.Contains(reports, report => report.Account == secondPrincipal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetAccountQuotaAsync_WhenLocalCliUnavailable_PreservesCredentialAndCanRetry(
		bool resolverThrows)
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal("NODE_LOCAL", "local-user", 1004);
		CopilotStoredCredential credential = CreateCredential(principal, "synthetic-local-token");
		FakeCredentialStore credentialStore = new(new Dictionary<Guid, CopilotStoredCredential>
		{
			[accountId] = credential
		});
		IOException resolutionFailure = new("Synthetic invalid local CLI signature.");
		WindowsOfficialCliExecutableLease? executableLease = null;
		int factoryCalls = 0;
		CopilotSdkQuotaClient client = new(
			id => GetExpectedHome(temporaryDirectory.Path, id),
			new CopilotAccountOperationGate(),
			credentialStore,
			new FakeGitHubUserClient(new Dictionary<string, CopilotAccountIdentity>
			{
				[credential.AccessToken] = principal
			}),
			() => executableLease ?? (resolverThrows ? throw resolutionFailure : null),
			(homeDirectory, accessToken, executablePath) =>
			{
				factoryCalls++;
				Assert.Equal(GetExpectedHome(temporaryDirectory.Path, accountId), homeDirectory);
				Assert.Equal(credential.AccessToken, accessToken);
				Assert.Equal(executableLease!.ExecutablePath, executablePath);
				Assert.True(executableLease.IsProtected);
				return new FakeSdkClient();
			});

		CopilotClientException failure = await Assert.ThrowsAsync<CopilotClientException>(() =>
			client.GetAccountQuotaAsync(accountId, credential.ProviderAccountIdentity));

		Assert.Equal(CopilotFailureKind.RuntimeUnavailable, failure.Kind);
		Assert.Contains("https://docs.github.com/", failure.Message, StringComparison.Ordinal);
		Assert.Equal(resolverThrows ? resolutionFailure : null, failure.InnerException);
		Assert.Same(credential, credentialStore.Read(accountId));
		Assert.Equal(0, factoryCalls);

		using WindowsOfficialCliExecutableLease availableLease = CreateExecutableLease(temporaryDirectory.Path);
		executableLease = availableLease;
		CopilotUsageReport report = await client.GetAccountQuotaAsync(
			accountId,
			credential.ProviderAccountIdentity);

		Assert.Equal(principal, report.Account);
		Assert.Equal(1, factoryCalls);
		Assert.Same(credential, credentialStore.Read(accountId));
		Assert.False(availableLease.IsProtected);
	}

	[Fact]
	public async Task GetAccountQuotaAsync_WhenResolverBlocks_CancelsWithoutHoldingCallerAndReleasesLateLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim releaseResolver = new(false);
		using CancellationTokenSource callerCancellation = new();
		TaskCompletionSource<int> resolverEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<int> invocationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> resolverExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
		string executablePath = Path.Combine(temporaryDirectory.Path, "synthetic-copilot.exe");
		File.WriteAllBytes(executablePath, [0x4d, 0x5a]);
		using DisposalObservedFileStream executableStream = new(executablePath);
		using WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(executablePath, executableStream);
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal("NODE_RESOLVER", "resolver-user", 1007);
		CopilotStoredCredential credential = CreateCredential(principal, "synthetic-resolver-token");
		FakeCredentialStore credentialStore = new(new Dictionary<Guid, CopilotStoredCredential>
		{
			[accountId] = credential
		});
		CopilotAccountOperationGate accountGate = new();
		FakeSdkClient sdkClient = new();
		int factoryCalls = 0;
		CopilotSdkQuotaClient client = new(
			id => GetExpectedHome(temporaryDirectory.Path, id),
			accountGate,
			credentialStore,
			new FakeGitHubUserClient(new Dictionary<string, CopilotAccountIdentity>
			{
				[credential.AccessToken] = principal
			}),
			() =>
			{
				resolverEntered.TrySetResult(Environment.CurrentManagedThreadId);
				try
				{
					releaseResolver.Wait();
					return executableLease;
				}
				finally
				{
					resolverExited.TrySetResult(true);
				}
			},
			(_, _, _) =>
			{
				Interlocked.Increment(ref factoryCalls);
				return sdkClient;
			});
		Task<CopilotUsageReport> request = Task.Factory.StartNew(
			() =>
			{
				int callerThread = Environment.CurrentManagedThreadId;
				Task<CopilotUsageReport> pending = client.GetAccountQuotaAsync(
					accountId,
					credential.ProviderAccountIdentity,
					callerCancellation.Token);
				invocationReturned.TrySetResult(callerThread);
				return pending;
			},
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default).Unwrap();

		try
		{
			int resolverThread = await resolverEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			int callerThread = await invocationReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.NotEqual(callerThread, resolverThread);
			Assert.False(releaseResolver.IsSet);
			Assert.False(request.IsCompleted);
			Assert.Equal(0, Volatile.Read(ref factoryCalls));

			callerCancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
			Assert.False(releaseResolver.IsSet);
			Assert.False(resolverExited.Task.IsCompleted);
			Assert.True(executableLease.IsProtected);
			Assert.Same(credential, credentialStore.Read(accountId));
			using (CancellationTokenSource gateDeadline = new(TimeSpan.FromSeconds(10)))
			using (IDisposable reacquiredGate = await accountGate.EnterAsync(accountId, gateDeadline.Token))
			{
				Assert.Equal(0, sdkClient.StartCount);
			}

			releaseResolver.Set();
			await executableStream.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(executableLease.IsProtected);
			Assert.Equal(0, Volatile.Read(ref factoryCalls));
			Assert.Equal(0, sdkClient.StartCount);
			using FileStream writable = new(executablePath, FileMode.Open, FileAccess.Write, FileShare.None);
		}
		finally
		{
			callerCancellation.Cancel();
			releaseResolver.Set();
			try
			{
				await request.WaitAsync(TimeSpan.FromSeconds(10));
			}
			catch (OperationCanceledException)
			{
				// 測試取消後仍觀察 request，並先釋放 resolver gate 再回收 fixture。
			}
			if (resolverEntered.Task.IsCompleted)
			{
				await resolverExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
		}
	}

	[Fact]
	public async Task ProbeTokenWithLifecycleAsync_WhenFactoryFails_ReleasesExecutableLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using WindowsOfficialCliExecutableLease executableLease = CreateExecutableLease(temporaryDirectory.Path);
		CopilotAccountIdentity principal = CreatePrincipal("NODE_FACTORY", "factory-user", 1005);
		InvalidOperationException factoryFailure = new("Synthetic SDK construction failure.");
		CopilotSdkQuotaClient client = CreateRuntimeClient(
			temporaryDirectory.Path,
			principal,
			() => executableLease,
			(_, _, _) => throw factoryFailure);

		var result = await client.ProbeTokenWithLifecycleAsync(
			Guid.NewGuid(),
			"synthetic-runtime-token",
			"github.com",
			CancellationToken.None);
		await result.LifecycleCompletion;

		Assert.Same(factoryFailure, result.Failure);
		Assert.False(executableLease.IsProtected);
		using FileStream write = new(executableLease.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.None);
	}

	[Theory]
	[InlineData("Start")]
	[InlineData("Stop")]
	[InlineData("ForceStop")]
	[InlineData("Dispose")]
	public async Task ProbeTokenWithLifecycleAsync_WhenLifecycleOutlivesDeadline_HoldsExecutableLease(
		string delayedOperation)
	{
		using TemporaryDirectory temporaryDirectory = new();
		using WindowsOfficialCliExecutableLease executableLease = CreateExecutableLease(temporaryDirectory.Path);
		using CancellationTokenSource callerCancellation = new();
		TaskCompletionSource<bool> enteredDelayedOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseDelayedOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CopilotAccountIdentity principal = CreatePrincipal("NODE_DELAYED", "delayed-user", 1006);
		Task RunLifecycleOperation(string operation)
		{
			if (operation == delayedOperation)
			{
				enteredDelayedOperation.TrySetResult(true);
				return releaseDelayedOperation.Task;
			}

			return Task.CompletedTask;
		}

		FakeSdkClient sdkClient = new(
			startAction: _ => RunLifecycleOperation("Start"),
			stopAction: () => delayedOperation == "ForceStop"
				? Task.FromException(new IOException("Synthetic stop failure."))
				: RunLifecycleOperation("Stop"),
			forceStopAction: () => RunLifecycleOperation("ForceStop"),
			disposeAction: () => new ValueTask(RunLifecycleOperation("Dispose")));
		CopilotSdkQuotaClient client = CreateRuntimeClient(
			temporaryDirectory.Path,
			principal,
			() => executableLease,
			(_, _, executablePath) =>
			{
				Assert.Equal(executableLease.ExecutablePath, executablePath);
				Assert.True(executableLease.IsProtected);
				return sdkClient;
			});
		var probe = client.ProbeTokenWithLifecycleAsync(
			Guid.NewGuid(),
			"synthetic-runtime-token",
			"github.com",
			callerCancellation.Token);

		try
		{
			await enteredDelayedOperation.Task.WaitAsync(TimeSpan.FromSeconds(10));
			if (delayedOperation == "Start")
			{
				callerCancellation.Cancel();
			}

			var result = await probe.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.NotNull(result.Failure);
			Assert.False(result.LifecycleCompletion.IsCompleted);
			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream write = new(executableLease.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
			});

			if (delayedOperation == "Start")
			{
				releaseDelayedOperation.TrySetException(new IOException("Synthetic late start failure."));
			}
			else
			{
				releaseDelayedOperation.TrySetResult(true);
			}
			await result.LifecycleCompletion.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(executableLease.IsProtected);
			using FileStream writable = new(executableLease.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.None);
		}
		finally
		{
			callerCancellation.Cancel();
			releaseDelayedOperation.TrySetResult(true);
			var result = await probe.WaitAsync(TimeSpan.FromSeconds(10));
			await result.LifecycleCompletion.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	private static CopilotSdkQuotaClient CreateRuntimeClient(
		string testRoot,
		CopilotAccountIdentity principal,
		Func<WindowsOfficialCliExecutableLease?> executableResolver,
		Func<string, string, string, ICopilotSdkClient> clientFactory)
	{
		return new CopilotSdkQuotaClient(
			accountId => GetExpectedHome(testRoot, accountId),
			new CopilotAccountOperationGate(),
			new FakeCredentialStore(new Dictionary<Guid, CopilotStoredCredential>()),
			new FakeGitHubUserClient(new Dictionary<string, CopilotAccountIdentity>
			{
				["synthetic-runtime-token"] = principal
			}),
			executableResolver,
			clientFactory,
			operationTimeout: TimeSpan.FromSeconds(10),
			cleanupTimeout: TimeSpan.FromMilliseconds(30));
	}

	private static WindowsOfficialCliExecutableLease CreateExecutableLease(string testRoot)
	{
		string executablePath = Path.Combine(testRoot, "synthetic-copilot.exe");
		File.WriteAllBytes(executablePath, [0x4d, 0x5a]);
		return WindowsOfficialCliExecutableLease.CreateProtected(
			executablePath,
			new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read));
	}

	private static CopilotSdkQuotaClient CreateClient(
		string testRoot,
		IReadOnlyDictionary<Guid, CopilotStoredCredential> credentials,
		IReadOnlyDictionary<string, CopilotAccountIdentity> identitiesByToken,
		Func<string, string, ICopilotSdkClient> clientFactory,
		TimeSpan? operationTimeout = null,
		Func<string, Exception?, bool>? reportSubscriptionDiagnostic = null)
	{
		return new CopilotSdkQuotaClient(
			accountId => GetExpectedHome(testRoot, accountId),
			new CopilotAccountOperationGate(),
			new FakeCredentialStore(credentials),
			new FakeGitHubUserClient(identitiesByToken),
			clientFactory,
			utcNow: () => DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
			operationTimeout: operationTimeout ?? TimeSpan.FromSeconds(10),
			cleanupTimeout: TimeSpan.FromSeconds(2),
			planProbeTimeout: TimeSpan.FromSeconds(2),
			reportSubscriptionDiagnostic: reportSubscriptionDiagnostic);
	}

	private static CopilotSdkSubscriptionMetadata? ReadSubscriptionMetadata(
		AccountGetCurrentAuthResult? currentAuth,
		string? expectedHost,
		string? expectedLogin)
	{
		return currentAuth is null
			? null
			: CopilotSubscriptionMetadataReader.Read(
				JsonSerializer.SerializeToElement(currentAuth),
				expectedHost,
				expectedLogin);
	}

	private static CopilotStoredCredential CreateCredential(
		CopilotAccountIdentity principal,
		string accessToken)
	{
		return new CopilotStoredCredential(
			CopilotAccountIdentityRules.Create(principal),
			accessToken);
	}

	private static CopilotAccountIdentity CreatePrincipal(
		string nodeId,
		string login,
		long databaseId)
	{
		return new CopilotAccountIdentity(
			"github.com",
			nodeId,
			databaseId,
			login);
	}

	private static string GetExpectedHome(string testRoot, Guid accountId)
	{
		return Path.GetFullPath(Path.Combine(
			testRoot,
			accountId.ToString("N"),
			"home"));
	}
}
