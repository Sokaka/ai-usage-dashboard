using System.Diagnostics;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeUltraWeeklyIntegrationTests
{
	private sealed class FixedTimeProvider : TimeProvider
	{
		public override DateTimeOffset GetUtcNow()
		{
			return ObservedAt;
		}
	}

	private sealed class RecordingBoundPoller : IClaudeUsagePoller
	{
		private readonly IClaudeUsagePoller _poller;

		internal ClaudeUsagePollResult? Result { get; private set; }

		internal string? ExpectedPublicBindingIdentity { get; private set; }

		internal RecordingBoundPoller(IClaudeUsagePoller poller)
		{
			_poller = poller;
		}

		public Task<ClaudeUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				$"The Claude integration provider must poll account {accountId} with its binding.");
		}

		public async Task<ClaudeUsagePollResult> PollBoundAsync(
			Guid accountId,
			string expectedPublicBindingIdentity,
			CancellationToken cancellationToken = default)
		{
			ExpectedPublicBindingIdentity = expectedPublicBindingIdentity;
			ClaudeUsagePollResult result = await _poller.PollBoundAsync(
				accountId,
				expectedPublicBindingIdentity,
				cancellationToken);
			Result = result;
			return result;
		}
	}

	private const string AuthStatusJson = """
		{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"claude@example.com","orgId":"org-test","orgName":"Synthetic Organization","subscriptionType":"max"}
		""";
	private const string UltraMetadataJson = """
		{
		  "claudeAiOauth": {
		    "subscriptionType": "max",
		    "rateLimitTier": "default_claude_max_20x"
		  }
		}
		""";
	private const string WeeklyUsageResultJson = """
		{
		  "type": "result",
		  "subtype": "success",
		  "is_error": false,
		  "num_turns": 0,
		  "total_cost_usd": 0,
		  "usage": {
		    "input_tokens": 0,
		    "output_tokens": 0,
		    "cache_read_input_tokens": 0,
		    "cache_creation_input_tokens": 0
		  },
		  "result": "You are currently using your subscription to power your Claude Code usage\nCurrent week (all models): 31% used · resets Nov 3, 1am (UTC)\nCurrent week (Fable): 47% used · resets Nov 4, 1am (UTC)"
		}
		""";
	private static readonly Guid AccountId =
		new("6b8d96cb-67de-4474-af1a-19a6b14162f2");
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task GetUsageAndReload_WithUltraWeeklyOnlyResult_PreservesBindingAndReportedMetrics()
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		using TemporaryDirectory temporaryDirectory = new();
		string accountDirectory = Path.Combine(temporaryDirectory.Path, AccountId.ToString("N"));
		string configDirectory = Path.Combine(accountDirectory, "config");
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string bindingPath = Path.Combine(accountDirectory, "account-binding-v1.json");
		string cachePath = Path.Combine(accountDirectory, "usage.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(configDirectory, ".credentials.json"),
			UltraMetadataJson);
		ClaudeSubscriptionContext coarseContext =
			ClaudeCliUsagePoller.ParseSubscriptionContext(AuthStatusJson);
		JsonClaudeAccountBindingStore bindingStore = new(
			accountId => Path.Combine(
				temporaryDirectory.Path,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => temporaryDirectory.Path);
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(AccountId, coarseContext);
		await bindingStore.SaveAsync(binding);
		byte[] originalBindingBytes = await File.ReadAllBytesAsync(bindingPath);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId);
		AccountProfile account = new(
			AccountId,
			ProviderKind.Claude,
			"Synthetic Claude account",
			ProviderAccountIdentity: publicBindingIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		List<ProcessStartInfo> capturedStartInfos = new();
		FixedTimeProvider timeProvider = new();
		RecordingBoundPoller poller = new(CreatePoller(
			configDirectory, executablePath, capturedStartInfos, timeProvider, bindingStore));
		ClaudeStatusLineUsageProvider fallback = new(
			_ => Path.Combine(accountDirectory, "statusline-v1.json"),
			_ => Path.Combine(accountDirectory, "statusline-fallback-disabled-v1"),
			timeProvider);
		ClaudeUsageProvider provider = new(poller, fallback, timeProvider, bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal(SourceTrust.OfficialExperimental, snapshot.SourceTrust);
		Assert.Equal("ultra", snapshot.PlanTier);
		Assert.Equal(SubscriptionVerificationState.Verified, snapshot.SubscriptionVerificationState);
		Assert.Equal(publicBindingIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(publicBindingIdentity, poller.ExpectedPublicBindingIdentity);
		ClaudeUsagePollResult pollResult = Assert.IsType<ClaudeUsagePollResult>(poller.Result);
		ClaudeSubscriptionContext polledContext =
			Assert.IsType<ClaudeSubscriptionContext>(pollResult.SubscriptionContext);
		Assert.Equal("ultra", pollResult.PlanDisplayName);
		Assert.Equal("max", polledContext.PlanTier);
		Assert.Equal(coarseContext, polledContext);
		Assert.Equal(coarseContext.ProviderDefinedEntitlementKey, polledContext.ProviderDefinedEntitlementKey);
		Assert.True(binding.MatchesEntitlement(polledContext));
		Assert.Equal(coarseContext.AccountIdentity, snapshot.ProviderAccountDisplayIdentity);
		AssertWeeklyOnlyMetrics(snapshot.Metrics);
		AssertExistingCommands(capturedStartInfos, executablePath, configDirectory);
		JsonUsageSnapshotStore store = new((_, _) => cachePath, timeProvider);
		await store.SaveAsync(snapshot);

		using (JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)))
		{
			Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
			Assert.Equal("ultra", document.RootElement.GetProperty("planTier").GetString());
			Assert.Equal(publicBindingIdentity,
				document.RootElement.GetProperty("providerAccountIdentity").GetString());
			Assert.Equal(2, document.RootElement.GetProperty("metrics").GetArrayLength());
		}

		JsonUsageSnapshotStore reloadedStore = new((_, _) => cachePath, timeProvider);
		UsageSnapshot loaded = Assert.IsType<UsageSnapshot>(await reloadedStore.LoadAsync(account));
		Assert.Equal(SnapshotStatus.Stale, loaded.Status);
		Assert.Equal("ultra", loaded.PlanTier);
		Assert.Equal(publicBindingIdentity, loaded.ProviderAccountIdentity);
		Assert.Equal(snapshot.Metrics, loaded.Metrics);
		AssertWeeklyOnlyMetrics(loaded.Metrics);
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.ApplySnapshot(loaded);

		Assert.Equal(new[] { "Weekly usage · All models", "Weekly usage · Fable" },
			viewModel.UsageMetrics.Select(metric => metric.Label));
		Assert.Equal(loaded.Metrics.Select(metric => metric.Key),
			viewModel.UsageMetrics.Select(metric => metric.Key));
		Assert.Equal("Used 31%", viewModel.PrimaryText);
		Assert.Equal("ultra", viewModel.SubscriptionPlanDisplayText);
		AssertWeeklyOnlyMetrics(Assert.IsType<UsageSnapshot>(viewModel.CurrentSnapshot).Metrics);
		AssertWeeklyOnlyMetrics(snapshot.Metrics);
		Assert.Equal(binding, await bindingStore.LoadAsync(AccountId));
		Assert.Equal(originalBindingBytes, await File.ReadAllBytesAsync(bindingPath));
	}

	private static ClaudeCliUsagePoller CreatePoller(
		string configDirectory,
		string executablePath,
		List<ProcessStartInfo> capturedStartInfos,
		TimeProvider timeProvider,
		IClaudeAccountBindingStore bindingStore)
	{
		return new ClaudeCliUsagePoller(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				capturedStartInfos.Add(startInfo);
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => "2.1.314 (Claude Code)",
					"auth" => AuthStatusJson,
					"-p" => WeeklyUsageResultJson,
					_ => throw new InvalidOperationException(
						$"Unexpected Claude integration command: {startInfo.ArgumentList[0]}.")
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0, standardOutput, string.Empty));
			},
			timeProvider,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: bindingStore,
			bindingCommitGate: new ClaudeBindingCommitGate());
	}

	private static void AssertWeeklyOnlyMetrics(IReadOnlyList<UsageMetric> metrics)
	{
		Assert.Equal(2, metrics.Count);
		Assert.Equal(new[]
		{
			"claude.rate_limit.seven_day.all_models",
			"claude.rate_limit.seven_day.bucket.ZmFibGU"
		}, metrics.Select(metric => metric.Key));
		Assert.Equal(new double?[] { 31, 47 }, metrics.Select(metric => metric.UsedPercent));
		Assert.All(metrics, metric => Assert.NotNull(metric.ResetsAt));
	}

	private static void AssertExistingCommands(
		IReadOnlyList<ProcessStartInfo> capturedStartInfos,
		string executablePath,
		string configDirectory)
	{
		Assert.Collection(
			capturedStartInfos,
			startInfo => Assert.Equal(new[] { "--version" }, startInfo.ArgumentList),
			startInfo => Assert.Equal(new[] { "auth", "status", "--json" }, startInfo.ArgumentList),
			startInfo => Assert.Equal(new[]
			{
				"-p", "/usage", "--output-format", "json", "--max-turns", "1",
				"--permission-mode", "dontAsk", "--no-session-persistence", "--safe-mode",
				"--tools", string.Empty
			}, startInfo.ArgumentList));
		Assert.All(capturedStartInfos, startInfo =>
		{
			Assert.Equal(executablePath, startInfo.FileName);
			Assert.Equal(configDirectory, startInfo.WorkingDirectory);
			Assert.Equal(configDirectory, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
		});
	}
}
