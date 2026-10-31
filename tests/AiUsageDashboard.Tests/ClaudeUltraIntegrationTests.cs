using System.Diagnostics;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeUltraIntegrationTests
{
	private sealed class FixedTimeProvider : TimeProvider
	{
		public override DateTimeOffset GetUtcNow()
		{
			return ObservedAt;
		}
	}

	private const string AuthStatusJson = """
		{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"claude@example.com","orgId":"org-test","orgName":"Synthetic Organization","subscriptionType":"max"}
		""";
	private const string UltraMetadataJson = """
		{
		  "oauthAccount": {
		    "emailAddress": "claude@example.com",
		    "organizationUuid": "org-test",
		    "organizationType": "claude_max",
		    "organizationRateLimitTier": "default_claude_max_20x"
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

	[Theory]
	[InlineData(true, "ultra")]
	[InlineData(false, "max")]
	public async Task PollAndProbe_WithMatchingPlanMetadata_PreserveEntitlementAndExistingCommands(
		bool includesMetadata,
		string expectedPlanTier)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		if (includesMetadata)
		{
			await File.WriteAllTextAsync(
				Path.Combine(configDirectory, ".claude.json"),
				UltraMetadataJson);
		}

		List<ProcessStartInfo> capturedStartInfos = new();
		ClaudeCliUsagePoller poller = CreatePoller(
			configDirectory,
			executablePath,
			capturedStartInfos);
		ClaudeSubscriptionContext coarseContext =
			ClaudeCliUsagePoller.ParseSubscriptionContext(AuthStatusJson);

		ClaudeUsagePollResult result = await poller.PollAsync(AccountId);
		ClaudeSubscriptionObservation observation = await poller.ProbeAsync(AccountId);

		Assert.NotNull(result.SubscriptionContext);
		Assert.Equal(expectedPlanTier, result.SubscriptionContext.PlanTier);
		Assert.Equal(expectedPlanTier, observation.Context.PlanTier);
		Assert.Equal("max", coarseContext.PlanTier);
		Assert.Equal(
			coarseContext.ProviderDefinedEntitlementKey,
			result.SubscriptionContext.ProviderDefinedEntitlementKey);
		Assert.Equal(
			coarseContext.ProviderDefinedEntitlementKey,
			observation.Context.ProviderDefinedEntitlementKey);
		Assert.Equal(ObservedAt, result.ObservedAt);
		Assert.Collection(
			capturedStartInfos,
			startInfo => Assert.Equal(new[] { "--version" }, startInfo.ArgumentList),
			startInfo => Assert.Equal(new[] { "auth", "status", "--json" }, startInfo.ArgumentList),
			startInfo => Assert.Equal(
				new[]
				{
					"-p", "/usage", "--output-format", "json", "--max-turns", "1",
					"--permission-mode", "dontAsk", "--no-session-persistence", "--safe-mode",
					"--tools", string.Empty
				},
				startInfo.ArgumentList),
			startInfo => Assert.Equal(new[] { "--version" }, startInfo.ArgumentList),
			startInfo => Assert.Equal(new[] { "auth", "status", "--json" }, startInfo.ArgumentList));
		Assert.All(capturedStartInfos, startInfo =>
		{
			Assert.Equal(executablePath, startInfo.FileName);
			Assert.Equal(configDirectory, startInfo.WorkingDirectory);
			Assert.Equal(configDirectory, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
		});
	}

	[Fact]
	public async Task GetUsageAndCache_WithUltraWeeklyOnlyResult_RestoresOnlyReportedMetrics()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountDirectory = Path.Combine(temporaryDirectory.Path, AccountId.ToString("N"));
		string configDirectory = Path.Combine(accountDirectory, "config");
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string cachePath = Path.Combine(accountDirectory, "usage.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		await File.WriteAllTextAsync(Path.Combine(configDirectory, ".claude.json"), UltraMetadataJson);
		JsonClaudeAccountBindingStore bindingStore = new(
			accountId => Path.Combine(
				temporaryDirectory.Path,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => temporaryDirectory.Path);
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			ClaudeCliUsagePoller.ParseSubscriptionContext(AuthStatusJson));
		await bindingStore.SaveAsync(binding);
		AccountProfile account = new(
			AccountId,
			ProviderKind.Claude,
			"合成 Claude 帳號",
			ProviderAccountIdentity: ClaudeAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId),
			HasAcceptedClaudeQuotaRisk: true);
		List<ProcessStartInfo> capturedStartInfos = new();
		ClaudeCliUsagePoller poller = CreatePoller(
			configDirectory,
			executablePath,
			capturedStartInfos,
			bindingStore);
		FixedTimeProvider timeProvider = new();
		ClaudeStatusLineUsageProvider fallback = new(
			_ => Path.Combine(accountDirectory, "statusline-v1.json"),
			_ => Path.Combine(accountDirectory, "statusline-fallback-disabled-v1"),
			timeProvider);
		ClaudeUsageProvider provider = new(poller, fallback, timeProvider, bindingStore);

		UsageSnapshot snapshot = await provider.GetUsageAsync(account, CancellationToken.None);

		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Equal("ultra", snapshot.PlanTier);
		Assert.Equal(SubscriptionVerificationState.Verified, snapshot.SubscriptionVerificationState);
		Assert.Equal(2, snapshot.Metrics.Count);
		Assert.DoesNotContain(snapshot.Metrics, metric => metric.Key == "claude.rate_limit.five_hour");
		Assert.Equal(3, capturedStartInfos.Count);
		JsonUsageSnapshotStore store = new((_, _) => cachePath, timeProvider);
		await store.SaveAsync(snapshot);

		using (JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)))
		{
			Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
			Assert.Equal("ultra", document.RootElement.GetProperty("planTier").GetString());
			Assert.Equal(2, document.RootElement.GetProperty("metrics").GetArrayLength());
		}

		UsageSnapshot? loaded = await store.LoadAsync(account);

		Assert.NotNull(loaded);
		Assert.Equal(SnapshotStatus.Stale, loaded.Status);
		Assert.Equal("ultra", loaded.PlanTier);
		Assert.Equal(snapshot.Metrics, loaded.Metrics);
		Assert.DoesNotContain(loaded.Metrics, metric => metric.Key == "claude.rate_limit.five_hour");
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.ApplySnapshot(loaded);
		Assert.Equal(new[] { "週用量 · 所有模型", "週用量 · Fable" },
			viewModel.UsageMetrics.Select(metric => metric.Label));
		Assert.Equal("已使用 31%", viewModel.PrimaryText);
		Assert.Equal(2, viewModel.CurrentSnapshot?.Metrics.Count);
	}

	private static ClaudeCliUsagePoller CreatePoller(
		string configDirectory,
		string executablePath,
		List<ProcessStartInfo> capturedStartInfos,
		IClaudeAccountBindingStore? bindingStore = null)
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
					0,
					standardOutput,
					string.Empty));
			},
			new FixedTimeProvider(),
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: bindingStore,
			bindingCommitGate: bindingStore is null ? null : new ClaudeBindingCommitGate());
	}
}
