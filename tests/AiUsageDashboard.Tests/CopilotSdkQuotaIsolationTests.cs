#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.Text.Json;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

using GitHub.Copilot.Rpc;

namespace AiUsageDashboard.Tests;

public sealed class CopilotSdkQuotaIsolationTests
{
	private sealed record FactoryCall(
		string HomeDirectory,
		string AccessToken,
		FakeSdkClient Client);

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
		private readonly IReadOnlyDictionary<string, CopilotSdkQuotaValue> _quota;
		private readonly Func<CancellationToken, Task> _startAction;
		private int _startCount;

		internal int StartCount => Volatile.Read(ref _startCount);

		internal FakeSdkClient(
			Func<CancellationToken, Task>? startAction = null,
			AccountGetCurrentAuthResult? currentAuth = null,
			IReadOnlyDictionary<string, CopilotSdkQuotaValue>? quota = null)
		{
			_currentAuth = currentAuth;
			_quota = quota ?? DefaultQuota;
			_startAction = startAction ?? (static _ => Task.CompletedTask);
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
			return Task.FromResult(
				CopilotSdkQuotaClient.TryGetMatchingSubscriptionMetadata(
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
			return Task.CompletedTask;
		}

		public Task ForceStopAsync()
		{
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
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
			CopilotSdkQuotaClient.TryGetMatchingSubscriptionMetadata(
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

	private static CopilotSdkQuotaClient CreateClient(
		string testRoot,
		IReadOnlyDictionary<Guid, CopilotStoredCredential> credentials,
		IReadOnlyDictionary<string, CopilotAccountIdentity> identitiesByToken,
		Func<string, string, ICopilotSdkClient> clientFactory,
		TimeSpan? operationTimeout = null)
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
			planProbeTimeout: TimeSpan.FromSeconds(2));
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
