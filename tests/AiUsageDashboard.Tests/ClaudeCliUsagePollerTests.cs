using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeCliUsagePollerTests
{
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

		internal void Advance(TimeSpan elapsed)
		{
			_utcNow += elapsed;
		}
	}

	private const string AuthStatusJson =
		"{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\"," +
		"\"email\":\"claude@example.com\",\"orgId\":\"org-123\"," +
		"\"orgName\":\"Example Organization\",\"subscriptionType\":\"max\"}";
	private const string SupportedVersion = "2.1.220 (Claude Code)";
	private const string UsageText = """
		You are currently using your subscription to power your Claude Code usage

		Current session: 49% used · resets Jul 15, 11:30am (Asia/Taipei)
		Current week (all models): 84% used · resets Jul 20, 8am (Asia/Taipei)
		""";

	[Theory]
	[InlineData("max")]
	[InlineData("team")]
	public async Task PollAsync_WithSafeZeroTurnResult_ReturnsUsageText(
		string subscriptionType)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		List<ProcessStartInfo> capturedStartInfos = new();
		DateTimeOffset observedAt = new(2026, 7, 15, 2, 30, 0, TimeSpan.Zero);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				capturedStartInfos.Add(startInfo);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo, subscriptionType),
					string.Empty));
			},
			new FakeTimeProvider(observedAt));

		ClaudeUsagePollResult result = await poller.PollAsync(Guid.NewGuid());

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(observedAt, result.ObservedAt);
		Assert.Equal("claude@example.com", result.AccountIdentity);
		ClaudeSubscriptionContext subscriptionContext =
			Assert.IsType<ClaudeSubscriptionContext>(result.SubscriptionContext);
		Assert.Equal("org-123", subscriptionContext.SubscriptionScopeIdentity);
		Assert.Equal(subscriptionType, subscriptionContext.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			subscriptionContext.VerificationState);
		Assert.Equal(3, capturedStartInfos.Count);
		Assert.Equal(new[] { "--version" }, capturedStartInfos[0].ArgumentList);
		Assert.Equal(
			new[] { "auth", "status", "--json" },
			capturedStartInfos[1].ArgumentList);
		Assert.Equal(
			new[]
			{
				"-p",
				"/usage",
				"--output-format",
				"json",
				"--max-turns",
				"1",
				"--permission-mode",
				"dontAsk",
				"--no-session-persistence",
				"--safe-mode",
				"--tools",
				string.Empty
			},
			capturedStartInfos[2].ArgumentList);

		foreach (ProcessStartInfo startInfo in capturedStartInfos)
		{
			Assert.Equal(executablePath, startInfo.FileName);
			Assert.Equal(configDirectory, startInfo.WorkingDirectory);
			Assert.Equal(configDirectory, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
			Assert.Equal("1", startInfo.Environment["NO_COLOR"]);
			Assert.False(startInfo.UseShellExecute);
			Assert.True(startInfo.CreateNoWindow);
		}
	}

	[Fact]
	public async Task PollAsync_WhenManualLatchWasPersisted_NewPollerDoesNotStartAnyProcess()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller firstPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => Task.FromResult(
				new ClaudeCliUsagePoller.ProcessResult(
					0,
					startInfo.ArgumentList[0] == "-p"
						? unsafeJson
						: GetStandardOutput(startInfo),
					string.Empty)),
			new FakeTimeProvider(now),
			new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => firstPoller.PollAsync(accountId));
		Assert.False(firstFailure.CanRetryAutomatically);

		int restartedProcessCount = 0;
		ClaudeCliUsagePoller restartedPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref restartedProcessCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			new FakeTimeProvider(now + TimeSpan.FromDays(1)),
			new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

		ClaudeUsageSafetyException restartedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => restartedPoller.PollAsync(accountId));

		Assert.False(restartedFailure.CanRetryAutomatically);
		Assert.Equal(0, restartedProcessCount);
		Assert.Equal(firstFailure.AccountIdentity, restartedFailure.AccountIdentity);
	}

	[Fact]
	public async Task PollAsync_WhenAutomaticRetryIsNotDueAfterRestart_PreservesDeadline()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		ClaudeCliUsagePoller firstPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => startInfo.ArgumentList[0] == "-p"
				? throw new TimeoutException("usage timed out")
				: Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty)),
			new FakeTimeProvider(now),
			new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => firstPoller.PollAsync(accountId));

		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.Equal(
			"原始錯誤。",
			firstFailure.AppendVersionDiagnostic("原始錯誤。"));
		ClaudeUsageSafetyState? persistedAfterFailure = await new
			JsonClaudeUsageSafetyStateStore(_ => safetyStatePath)
			.LoadAsync(accountId);
		Assert.NotNull(persistedAfterFailure);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			persistedAfterFailure.RecoveryMode);
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			persistedAfterFailure.RetryNotBefore);

		int restartedProcessCount = 0;
		ClaudeCliUsagePoller restartedPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref restartedProcessCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			new FakeTimeProvider(now + TimeSpan.FromSeconds(30)),
			new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

		ClaudeUsageSafetyException restartedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => restartedPoller.PollAsync(accountId));

		Assert.True(restartedFailure.CanRetryAutomatically, restartedFailure.Message);
		Assert.Equal(
			firstFailure.AutomaticRetryNotBefore,
			restartedFailure.AutomaticRetryNotBefore);
		Assert.Equal(0, restartedProcessCount);
	}

	[Fact]
	public async Task PollAsync_LatchedUnverifiedFormatFailure_PreservesDiagnosticAfterRestart()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 15, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		ClaudeCliUsagePoller firstPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => Task.FromResult(
				new ClaudeCliUsagePoller.ProcessResult(
					0,
					startInfo.ArgumentList[0] == "-p"
						? string.Empty
						: GetStandardOutput(startInfo),
					string.Empty)),
			new FakeTimeProvider(now),
			store);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => firstPoller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => firstPoller.PollAsync(accountId));
		string diagnostic =
			firstFailure.AppendVersionDiagnostic("原始錯誤。");

		Assert.Contains("Claude Code 2.1.220", diagnostic, StringComparison.Ordinal);
		Assert.Contains("2.1.169", diagnostic, StringComparison.Ordinal);
		Assert.Equal(
			diagnostic,
			pendingFailure.AppendVersionDiagnostic("原始錯誤。"));
		ClaudeUsageSafetyState? persistedState = await store.LoadAsync(accountId);
		Assert.NotNull(persistedState);
		Assert.Equal("2.1.220", persistedState.DiagnosticCliVersion);

		int restartedProcessCount = 0;
		ClaudeCliUsagePoller restartedPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref restartedProcessCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			new FakeTimeProvider(now + TimeSpan.FromSeconds(30)),
			new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

		ClaudeUsageSafetyException restartedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => restartedPoller.PollAsync(accountId));

		Assert.Equal(
			diagnostic,
			restartedFailure.AppendVersionDiagnostic("原始錯誤。"));
		Assert.Equal(0, restartedProcessCount);
	}

	[Fact]
	public async Task PollAsync_ReferenceVersionFormatFailure_DoesNotPersistDiagnostic()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 20, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => Task.FromResult(
				new ClaudeCliUsagePoller.ProcessResult(
					0,
					startInfo.ArgumentList[0] switch
					{
						"--version" => "2.1.169 (Claude Code)",
						"-p" => string.Empty,
						_ => GetStandardOutput(startInfo)
					},
					string.Empty)),
			new FakeTimeProvider(now),
			store);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyState? persistedState = await store.LoadAsync(accountId);

		Assert.Contains("用量格式", failure.Message, StringComparison.Ordinal);
		Assert.Equal(
			"原始錯誤。",
			failure.AppendVersionDiagnostic("原始錯誤。"));
		Assert.NotNull(persistedState);
		Assert.Null(persistedState.DiagnosticCliVersion);
	}

	[Fact]
	public async Task PollAsync_AutomaticRetryFailsDifferently_PreservesFirstDiagnostic()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 25, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		int usageRunCount = 0;
		string versionOutput = "2.1.220 (Claude Code)";
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				if (startInfo.ArgumentList[0] == "-p")
				{
					if (Interlocked.Increment(ref usageRunCount) == 1)
					{
						return Task.FromResult(
							new ClaudeCliUsagePoller.ProcessResult(
								0,
								string.Empty,
								string.Empty));
					}

					throw new TimeoutException("usage timed out");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					startInfo.ArgumentList[0] == "--version"
						? versionOutput
						: GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			store);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		string firstDiagnostic =
			firstFailure.AppendVersionDiagnostic("原始錯誤。");
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		versionOutput = "2.1.230 (Claude Code)";

		ClaudeUsageSafetyException retryFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyState? persistedState = await store.LoadAsync(accountId);

		Assert.Equal(firstFailure.Message, retryFailure.Message);
		Assert.Equal(
			firstDiagnostic,
			retryFailure.AppendVersionDiagnostic("原始錯誤。"));
		Assert.Contains("2.1.220", firstDiagnostic, StringComparison.Ordinal);
		Assert.DoesNotContain("2.1.230", firstDiagnostic, StringComparison.Ordinal);
		Assert.NotNull(persistedState);
		Assert.Contains("2.1.220", persistedState.FailureReason, StringComparison.Ordinal);
		Assert.Equal("2.1.220", persistedState.DiagnosticCliVersion);
		Assert.Equal(2, usageRunCount);
	}

	[Fact]
	public async Task PollAsync_WhenAutomaticTransitionPrimaryWriteIsBlocked_NewPollerUsesDurableIntent()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 30, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentPath =
			safetyStatePath + ".transition-pending-v1.json";
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		FileStream? lockedState = null;

		try
		{
			ClaudeCliUsagePoller firstPoller = new(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) =>
				{
					if (startInfo.ArgumentList[0] == "-p")
					{
						lockedState = new FileStream(
							safetyStatePath,
							FileMode.Open,
							FileAccess.Read,
							FileShare.Read);
						throw new TimeoutException("usage timed out");
					}

					return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty));
				},
				new FakeTimeProvider(now),
				new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

			ClaudeUsageSafetyException firstFailure =
				await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
					() => firstPoller.PollAsync(accountId));
			Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
			Assert.True(File.Exists(transitionIntentPath));

			FakeTimeProvider restartedTimeProvider = new(
				now + TimeSpan.FromSeconds(30));
			int restartedProcessCount = 0;
			ClaudeCliUsagePoller restartedPoller = new(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) =>
				{
					Interlocked.Increment(ref restartedProcessCount);
					return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty));
				},
				restartedTimeProvider,
				new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

			ClaudeUsageSafetyException pendingFailure =
				await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
					() => restartedPoller.PollAsync(accountId));
			Assert.True(
				pendingFailure.CanRetryAutomatically,
				pendingFailure.ToString());
			Assert.Equal(firstFailure.AutomaticRetryNotBefore,
				pendingFailure.AutomaticRetryNotBefore);
			Assert.Equal(0, restartedProcessCount);

			lockedState!.Dispose();
			lockedState = null;
			restartedTimeProvider.Advance(TimeSpan.FromSeconds(30));

			ClaudeUsagePollResult recovered =
				await restartedPoller.PollAsync(accountId);
			Assert.Equal(UsageText, recovered.Output);
			Assert.Equal(3, restartedProcessCount);
			Assert.False(File.Exists(transitionIntentPath));
		}
		finally
		{
			lockedState?.Dispose();
		}
	}

	[Fact]
	public async Task PollAsync_WhenCommittedClearCannotDeletePrimary_NewPollerRetriesWithoutManualAction()
	{
		DateTimeOffset now = new(2026, 8, 15, 0, 45, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentPath = safetyStatePath + ".clear-pending-v1.json";
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		FileStream? lockedState = null;

		try
		{
			ClaudeCliUsagePoller firstPoller = new(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) =>
				{
					if (startInfo.ArgumentList[0] == "-p")
					{
						lockedState = new FileStream(
							safetyStatePath,
							FileMode.Open,
							FileAccess.Read,
							FileShare.Read);
					}

					return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty));
				},
				new FakeTimeProvider(now),
				new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

			ClaudeUsagePollResult firstResult =
				await firstPoller.PollAsync(accountId);
			Assert.Equal(UsageText, firstResult.Output);
			Assert.True(File.Exists(clearIntentPath));

			FakeTimeProvider restartedTimeProvider = new(now);
			int restartedUsageCount = 0;
			ClaudeCliUsagePoller restartedPoller = new(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) =>
				{
					if (startInfo.ArgumentList[0] == "-p")
					{
						Interlocked.Increment(ref restartedUsageCount);
					}

					return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty));
				},
				restartedTimeProvider,
				new JsonClaudeUsageSafetyStateStore(_ => safetyStatePath));

			ClaudeUsageSafetyException cleanupPending =
				await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
					() => restartedPoller.PollAsync(accountId));
			Assert.True(
				cleanupPending.CanRetryAutomatically,
				cleanupPending.ToString());
			Assert.Equal(0, restartedUsageCount);

			lockedState!.Dispose();
			lockedState = null;
			restartedTimeProvider.Advance(TimeSpan.FromMinutes(1));

			ClaudeUsagePollResult recovered =
				await restartedPoller.PollAsync(accountId);
			Assert.Equal(UsageText, recovered.Output);
			Assert.Equal(1, restartedUsageCount);
			Assert.False(File.Exists(clearIntentPath));
		}
		finally
		{
			lockedState?.Dispose();
		}
	}

	[Theory]
	[InlineData("Claude Code 2.1.220 回傳的零用量無法驗證。")]
	[InlineData("Claude Code 2.1.220 的 `/usage` 在完成零用量安全驗證前已取消。")]
	public async Task PollAsync_WhenLegacyRetryMarkerLacksQuiescenceProof_PreservesManualRevalidation(
		string failureReason)
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await WriteLegacySafetyStateAsync(
			safetyStatePath,
			automaticRevalidationAttemptCount: 3,
			failureReason);
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			timeProvider,
			store);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(failure.CanRetryAutomatically, failure.ToString());
		Assert.Null(failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? preservedState = await store.LoadAsync(accountId);
		Assert.NotNull(preservedState);
		Assert.Equal(3, preservedState.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			preservedState.RecoveryMode);
		Assert.Null(preservedState.RetryNotBefore);
		Assert.Equal(1, preservedState.LegacySourceSchemaVersion);
	}

	[Fact]
	public async Task PollAsync_WhenLegacyAutomaticRetryLacksQuiescenceProof_RequiresManualRevalidation()
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 10, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await WriteLegacySafetyStateAsync(
			safetyStatePath,
			automaticRevalidationAttemptCount: 1,
			failureReason:
				"Claude Code 2.1.220 的 `/usage` 在完成零用量安全驗證前已取消。",
			recoveryMode:
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			retryNotBefore: now - TimeSpan.FromMinutes(1));
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(failure.CanRetryAutomatically, failure.ToString());
		Assert.Null(failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? preservedState = await store.LoadAsync(accountId);
		Assert.NotNull(preservedState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			preservedState.RecoveryMode);
		Assert.Null(preservedState.RetryNotBefore);
		Assert.Equal(1, preservedState.LegacySourceSchemaVersion);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 2)]
	[InlineData(2, 4)]
	[InlineData(3, 4)]
	public async Task PollAsync_WhenLegacyExitCodeConflictMarkerWasPersisted_MigratesToAutomaticRetry(
		int attemptCount,
		int expectedDelayMinutes)
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 15, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await WriteLegacySafetyStateAsync(
			safetyStatePath,
			automaticRevalidationAttemptCount: attemptCount,
			failureReason:
				"Claude Code 2.1.220 的 `/usage` exit code 與安全結果互相衝突。");
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		DateTimeOffset expectedDeadline =
			now + TimeSpan.FromMinutes(expectedDelayMinutes);
		Assert.True(failure.CanRetryAutomatically, failure.ToString());
		Assert.Equal(expectedDeadline, failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? migratedState = await store.LoadAsync(accountId);
		Assert.NotNull(migratedState);
		Assert.Equal(attemptCount, migratedState.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			migratedState.RecoveryMode);
		Assert.Equal(expectedDeadline, migratedState.RetryNotBefore);
	}

	[Fact]
	public async Task PollAsync_WhenLegacyInterruptedManualMarkerWasPersisted_PreservesManualRevalidation()
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 25, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await WriteLegacySafetyStateAsync(
			safetyStatePath,
			automaticRevalidationAttemptCount: 0,
			failureReason:
				"Claude Code `/usage` 在完成零用量安全驗證前已中斷。");
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(failure.CanRetryAutomatically, failure.ToString());
		Assert.Null(failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? preservedState = await store.LoadAsync(accountId);
		Assert.NotNull(preservedState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			preservedState.RecoveryMode);
		Assert.Null(preservedState.RetryNotBefore);
		Assert.Null(preservedState.AttemptId);
		Assert.Null(preservedState.AttemptPhase);
		Assert.Equal(1, preservedState.LegacySourceSchemaVersion);
	}

	[Fact]
	public async Task PollAsync_WhenStartupCleanupAppliedLegacyInterruptedTransition_PreservesManualRevalidation()
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 27, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentPath =
			safetyStatePath + ".transition-pending-v1.json";
		JsonClaudeUsageSafetyStateStore startupStore = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await WriteLegacySafetyTransitionAsync(
			transitionIntentPath,
			accountId,
			"Claude Code `/usage` 在完成零用量安全驗證前已中斷。");

		await startupStore.RecoverPendingCleanupAsync(accountId);

		Assert.False(File.Exists(transitionIntentPath));
		using (JsonDocument primaryDocument = JsonDocument.Parse(
			await File.ReadAllTextAsync(safetyStatePath)))
		{
			Assert.Equal(
				3,
				primaryDocument.RootElement
					.GetProperty("schemaVersion")
					.GetInt32());
			Assert.Equal(
				1,
				primaryDocument.RootElement
					.GetProperty("legacySourceSchemaVersion")
					.GetInt32());
		}

		int processCount = 0;
		JsonClaudeUsageSafetyStateStore restartedStore = new(_ => safetyStatePath);
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			restartedStore);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(failure.CanRetryAutomatically, failure.ToString());
		Assert.Null(failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? preservedState =
			await restartedStore.LoadAsync(accountId);
		Assert.NotNull(preservedState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			preservedState.RecoveryMode);
		Assert.Equal(1, preservedState.LegacySourceSchemaVersion);
	}

	[Theory]
	[InlineData("Claude Code 2.1.220 的 `/usage` 可能產生用量，或結果需要人工確認。")]
	[InlineData("Claude Code 2.1.220 回傳的用量格式暫時無法辨識。")]
	public async Task PollAsync_WhenHardSafetyMarkerHasMaximumBackoffCount_PreservesManualRevalidation(
		string hardFailureReason)
	{
		DateTimeOffset now = new(2026, 8, 15, 1, 30, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 3,
				AccountIdentity: "claude@example.com",
				FailureReason: hardFailureReason,
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
				RetryNotBefore: null));
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(failure.CanRetryAutomatically);
		Assert.Null(failure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? persistedState = await store.LoadAsync(accountId);
		Assert.NotNull(persistedState);
		Assert.Equal(3, persistedState.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			persistedState.RecoveryMode);
		Assert.Equal(hardFailureReason, persistedState.FailureReason);
	}

	[Fact]
	public async Task PollAsync_WhenSafetyStatePrearmFails_DoesNotInvokeUsage()
	{
		DateTimeOffset now = new(2026, 8, 15, 2, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int processCount = 0;
		int usageProcessCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processCount);

				if (startInfo.ArgumentList[0] == "-p")
				{
					Interlocked.Increment(ref usageProcessCount);
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			new FailingPrearmSafetyStateStore());

		ClaudeUsageSafetyException exception =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(Guid.NewGuid()));

		Assert.True(exception.CanRetryAutomatically, exception.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			exception.AutomaticRetryNotBefore);
		Assert.Contains("上次 Claude 用量檢查的狀態", exception.Message, StringComparison.Ordinal);
		Assert.Equal(
			"原始錯誤。",
			exception.AppendVersionDiagnostic("原始錯誤。"));
		Assert.Equal(2, processCount);
		Assert.Equal(0, usageProcessCount);
	}

	[Fact]
	public async Task PollAsync_WhenSafetyStateLoadFailsTransiently_RetriesAndEventuallyPolls()
	{
		DateTimeOffset now = new(2026, 8, 15, 2, 30, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int processCount = 0;
		TransientLoadSafetyStateStore store = new();
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			store);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			firstFailure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		ClaudeUsagePollResult result = await poller.PollAsync(accountId);

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(2, store.LoadCallCount);
		Assert.Equal(3, processCount);
	}

	[Fact]
	public async Task PollAsync_WhenSafeUsageCompletes_ClearsPersistedAttemptMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState? observedAttemptState = null;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			async (startInfo, cancellationToken) =>
			{
				if (startInfo.ArgumentList[0] == "-p")
				{
					observedAttemptState = await store.LoadAsync(
						accountId,
						cancellationToken);
				}

				return new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty);
			},
			TimeProvider.System,
			store);

		ClaudeUsagePollResult result = await poller.PollAsync(accountId);

		Assert.Equal(UsageText, result.Output);
		Assert.NotNull(observedAttemptState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			observedAttemptState.RecoveryMode);
		Assert.Null(await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task PollAsync_WhenUnexpectedFailureEscapesAfterUsageStarts_NewPollerRequiresManualRevalidation()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		ClaudeCliUsagePoller firstPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => startInfo.ArgumentList[0] == "-p"
				? throw new ApplicationException("simulated process host crash")
				: Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty)),
			timeProvider,
			store);

		await Assert.ThrowsAsync<ApplicationException>(
			() => firstPoller.PollAsync(accountId));

		int restartedProcessCount = 0;
		ClaudeCliUsagePoller restartedPoller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref restartedProcessCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			store);

		ClaudeUsageSafetyException restartedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => restartedPoller.PollAsync(accountId));

		Assert.False(restartedFailure.CanRetryAutomatically);
		Assert.Contains("中斷", restartedFailure.Message, StringComparison.Ordinal);
		Assert.Null(restartedFailure.AutomaticRetryNotBefore);
		Assert.Contains(
			"重新檢查 Claude 用量",
			restartedFailure.Message,
			StringComparison.Ordinal);
		Assert.Equal(0, restartedProcessCount);
		ClaudeUsageSafetyState? recoveredState = await store.LoadAsync(accountId);
		Assert.NotNull(recoveredState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			recoveredState.RecoveryMode);
		Assert.Null(recoveredState.RetryNotBefore);
	}

	[Fact]
	public async Task PollAsync_WhenUnexpectedFailureEscapesAfterUsageStarts_SamePollerRequiresManualRevalidation()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processCount);

				if (startInfo.ArgumentList[0] == "-p")
				{
					throw new ApplicationException("simulated process host crash");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<ApplicationException>(
			() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException recovery =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(recovery.CanRetryAutomatically);
		Assert.Null(recovery.AutomaticRetryNotBefore);
		Assert.Equal(3, processCount);
	}

	[Fact]
	public async Task PollAsync_WhenUsageIsInterruptedAcrossRestarts_RemainsManual()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		int usageInvocationCount = 0;
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);

		ClaudeCliUsagePoller CreateCrashingPoller()
		{
			return new ClaudeCliUsagePoller(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) =>
				{
					if (startInfo.ArgumentList[0] == "-p")
					{
						Interlocked.Increment(ref usageInvocationCount);
						throw new ApplicationException("simulated usage crash");
					}

					return Task.FromResult(
						new ClaudeCliUsagePoller.ProcessResult(
							0,
							GetStandardOutput(startInfo),
							string.Empty));
				},
				timeProvider,
				store);
		}

		await Assert.ThrowsAsync<ApplicationException>(
			() => CreateCrashingPoller().PollAsync(accountId));

		ClaudeCliUsagePoller firstRestartedPoller = CreateCrashingPoller();
		ClaudeUsageSafetyException firstRecovery =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => firstRestartedPoller.PollAsync(accountId));
		Assert.False(firstRecovery.CanRetryAutomatically);
		Assert.Null(firstRecovery.AutomaticRetryNotBefore);
		Assert.Equal(
			"原始錯誤。",
			firstRecovery.AppendVersionDiagnostic("原始錯誤。"));

		timeProvider.Advance(TimeSpan.FromDays(1));
		ClaudeCliUsagePoller secondRestartedPoller = CreateCrashingPoller();
		ClaudeUsageSafetyException secondRecovery =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => secondRestartedPoller.PollAsync(accountId));
		Assert.False(secondRecovery.CanRetryAutomatically);
		Assert.Null(secondRecovery.AutomaticRetryNotBefore);
		Assert.Equal(
			"原始錯誤。",
			secondRecovery.AppendVersionDiagnostic("原始錯誤。"));
		Assert.Equal(1, usageInvocationCount);
		ClaudeUsageSafetyState? persistedState = await store.LoadAsync(accountId);
		Assert.NotNull(persistedState);
		Assert.Null(persistedState.DiagnosticCliVersion);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			persistedState.RecoveryMode);
		Assert.Null(persistedState.RetryNotBefore);
	}

	[Theory]
	[InlineData((int)ClaudeUsageSafetyAttemptPhase.Prepared)]
	[InlineData((int)ClaudeUsageSafetyAttemptPhase.StartedContained)]
	public async Task PollAsync_WhenContainedUsageWasInterruptedAcrossRestart_SchedulesAutomaticRetry(
		int rawAttemptPhase)
	{
		ClaudeUsageSafetyAttemptPhase attemptPhase =
			(ClaudeUsageSafetyAttemptPhase)rawAttemptPhase;
		DateTimeOffset now = new(2026, 8, 15, 8, 30, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 0,
				AccountIdentity: "claude@example.com",
				FailureReason:
					"Claude Code `/usage` 尚未確認是否產生用量就中斷。",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase: attemptPhase));
		int processCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref processCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);

		ClaudeUsageSafetyException recovery =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(recovery.CanRetryAutomatically, recovery.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			recovery.AutomaticRetryNotBefore);
		Assert.Equal(
			"原始錯誤。",
			recovery.AppendVersionDiagnostic("原始錯誤。"));
		Assert.Equal(0, processCount);
		ClaudeUsageSafetyState? recoveredState = await store.LoadAsync(accountId);
		Assert.NotNull(recoveredState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			recoveredState.RecoveryMode);
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			recoveredState.RetryNotBefore);
		Assert.Null(recoveredState.AttemptId);
		Assert.Null(recoveredState.AttemptPhase);
		Assert.Null(recoveredState.DiagnosticCliVersion);
	}

	[Fact]
	public async Task PollAsync_WithContainedRunner_PersistsPreparedThenStartedAndClearsOnSuccess()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 40, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState? preparedState = null;
		ClaudeUsageSafetyState? startedState = null;
		string? observedJobName = null;
		int ordinaryCommandCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref ordinaryCommandCount);
				Assert.NotEqual("-p", startInfo.ArgumentList[0]);
				return Task.FromResult(
					new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty));
			},
			new FakeTimeProvider(now),
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			store,
			containedUsageProcessRunner:
				async (startInfo, jobName, beforeResumeAsync, token) =>
				{
					Assert.Equal("-p", startInfo.ArgumentList[0]);
					observedJobName = jobName;
					preparedState = await store.LoadAsync(accountId, token);
					await beforeResumeAsync(token);
					startedState = await store.LoadAsync(accountId, token);
					return new ClaudeCliUsagePoller.ProcessResult(
						0,
						CreateResultJson(UsageText),
						string.Empty);
				});

		ClaudeUsagePollResult result = await poller.PollAsync(accountId);

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(2, ordinaryCommandCount);
		Assert.NotNull(preparedState);
		Assert.NotNull(preparedState.AttemptId);
		Assert.Equal(
			ClaudeUsageSafetyAttemptPhase.Prepared,
			preparedState.AttemptPhase);
		Assert.Equal(
			ClaudeCliUsagePoller.CreateContainedUsageJobName(
				preparedState.AttemptId.Value),
			observedJobName);
		Assert.NotNull(startedState);
		Assert.Equal(preparedState.AttemptId, startedState.AttemptId);
		Assert.Equal(
			ClaudeUsageSafetyAttemptPhase.StartedContained,
			startedState.AttemptPhase);
		Assert.Null(await store.LoadAsync(accountId));
	}

	[Fact]
	public void TranslateContainedUsageProcessFailure_WhenStartedCleanupIsUnconfirmed_PreservesRecoverySignal()
	{
		AntigravityOfficialPrintProcessRunException processFailure = new(
			wasProcessStarted: true,
			new TimeoutException("cleanup timed out"),
			wasTerminationConfirmed: false,
			quiescenceTask: Task.FromResult(false));

		Exception translated =
			ClaudeCliUsagePoller.TranslateContainedUsageProcessFailure(
				processFailure,
				CancellationToken.None);

		ClaudeCliUsagePoller.ContainedUsageCleanupUnconfirmedException
			cleanupFailure = Assert.IsType<
				ClaudeCliUsagePoller.ContainedUsageCleanupUnconfirmedException>(
					translated);
		Assert.Same(processFailure, cleanupFailure.InnerException);
	}

	[Fact]
	public async Task PollAsync_WhenContainedCleanupIsUnconfirmed_KeepsAttemptForRestartRecovery()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 45, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) => Task.FromResult(
				new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty)),
			new FakeTimeProvider(now),
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			store,
			containedUsageProcessRunner:
				async (_, _, beforeResumeAsync, token) =>
				{
					await beforeResumeAsync(token);
					throw new ClaudeCliUsagePoller
						.ContainedUsageCleanupUnconfirmedException(
							new TimeoutException("cleanup timed out"));
				});

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(failure.CanRetryAutomatically, failure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			failure.AutomaticRetryNotBefore);
		ClaudeUsageSafetyState? interruptedState =
			await store.LoadAsync(accountId);
		Assert.NotNull(interruptedState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			interruptedState.RecoveryMode);
		Assert.NotNull(interruptedState.AttemptId);
		Assert.Equal(
			ClaudeUsageSafetyAttemptPhase.StartedContained,
			interruptedState.AttemptPhase);

		int restartedProcessCount = 0;
		ClaudeCliUsagePoller restartedPoller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) =>
			{
				Interlocked.Increment(ref restartedProcessCount);
				throw new InvalidOperationException("process must not start");
			},
			new FakeTimeProvider(now),
			store);
		ClaudeUsageSafetyException recovery =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => restartedPoller.PollAsync(accountId));

		Assert.True(recovery.CanRetryAutomatically, recovery.ToString());
		Assert.Equal(0, restartedProcessCount);
		ClaudeUsageSafetyState? recoveredState = await store.LoadAsync(accountId);
		Assert.NotNull(recoveredState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			recoveredState.RecoveryMode);
		Assert.Null(recoveredState.AttemptId);
		Assert.Null(recoveredState.AttemptPhase);
	}

	[Fact]
	public async Task PollAsync_WhenContainedCleanupIsUnconfirmed_HoldsLeaseUntilPositiveRecovery()
	{
		DateTimeOffset now = new(2026, 8, 15, 8, 50, 0, TimeSpan.Zero);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		WindowsOfficialCliExecutableLease rawExecutableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));

		try
		{
			JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
			Guid accountId = Guid.NewGuid();
			int recoveryCallCount = 0;
			ClaudeCliUsagePoller poller = new(
				_ => configDirectory,
				() => executablePath,
				(startInfo, _) => Task.FromResult(
					new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty)),
				new FakeTimeProvider(now),
				new ClaudeAccountOperationGate(),
				TimeSpan.FromSeconds(5),
				store,
				containedUsageProcessRunner:
					async (_, _, beforeResumeAsync, token) =>
					{
						await beforeResumeAsync(token);
						throw new ClaudeCliUsagePoller
							.ContainedUsageCleanupUnconfirmedException(
								new TimeoutException("cleanup timed out"));
					},
				containedUsageAttemptRecovery: (_, _, _) =>
				{
					Interlocked.Increment(ref recoveryCallCount);
					return Task.FromResult(true);
				},
				protectedExecutableResolver: () => rawExecutableLease);

			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

			Assert.True(rawExecutableLease.IsProtected);
			Assert.Throws<IOException>(() =>
				File.WriteAllText(executablePath, "changed"));

			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

			Assert.Equal(1, recoveryCallCount);
			Assert.False(rawExecutableLease.IsProtected);
			File.WriteAllText(executablePath, "changed");
		}
		finally
		{
			rawExecutableLease.Dispose();
		}
	}

	private sealed class EmptyClaudeAccountBindingStore :
		IClaudeAccountBindingStore
	{
		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<IReadOnlyList<ClaudeAccountBinding>>(
				Array.Empty<ClaudeAccountBinding>());
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<ClaudeAccountBinding?>(null);
		}

		public Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Poller test binding store must remain read-only.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Poller test binding store must remain read-only.");
		}
	}

	private sealed class InMemoryClaudeAccountBindingStore :
		IClaudeAccountBindingStore
	{
		private readonly IReadOnlyDictionary<Guid, ClaudeAccountBinding> _bindings;

		internal int LoadAllCallCount { get; private set; }

		internal int LoadCallCount { get; private set; }

		internal InMemoryClaudeAccountBindingStore(
			params ClaudeAccountBinding[] bindings)
		{
			_bindings = bindings.ToDictionary(binding => binding.AccountId);
		}

		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadAllCallCount++;
			return Task.FromResult<IReadOnlyList<ClaudeAccountBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadCallCount++;
			_bindings.TryGetValue(accountId, out ClaudeAccountBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Poller test binding store must remain read-only.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Poller test binding store must remain read-only.");
		}
	}

	[Fact]
	public async Task PollAsync_WhenTrackedCleanupOutlivesFailure_HoldsExecutableLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		WindowsOfficialCliExecutableLease rawExecutableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		TaskCompletionSource cleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		try
		{
			ClaudeCliUsagePoller poller = new(
				_ => configDirectory,
				() => executablePath,
				(_, _) => throw new InvalidOperationException(
					"untracked runner must not run"),
				TimeProvider.System,
				new ClaudeAccountOperationGate(),
				TimeSpan.FromSeconds(5),
				protectedExecutableResolver: () => rawExecutableLease,
				trackedProcessRunner: (_, tracker, _) =>
				{
					tracker.Track(cleanupCompletion.Task);
					return Task.FromException<ClaudeCliUsagePoller.ProcessResult>(
						new IOException("process cleanup is still running"));
				});

			await Assert.ThrowsAsync<IOException>(
				() => poller.PollAsync(Guid.NewGuid()));

			Assert.True(rawExecutableLease.IsProtected);
			Assert.Throws<IOException>(() =>
				File.WriteAllText(executablePath, "changed"));

			cleanupCompletion.SetResult();
			Assert.True(SpinWait.SpinUntil(
				() => !rawExecutableLease.IsProtected,
				TimeSpan.FromSeconds(5)));
			File.WriteAllText(executablePath, "changed");
		}
		finally
		{
			cleanupCompletion.TrySetResult();
			rawExecutableLease.Dispose();
		}
	}

	[Fact]
	public async Task PollAsync_WhenProcessContainmentIsCompromised_RequiresRestartForLaterAttempts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		ClaudeAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int runnerCallCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) => throw new InvalidOperationException(
				"untracked runner must not run"),
			TimeProvider.System,
			operationGate,
			TimeSpan.FromSeconds(5),
			trackedProcessRunner: (_, tracker, _) =>
			{
				Interlocked.Increment(ref runnerCallCount);
				tracker.MarkContainmentCompromised();
				return Task.FromException<ClaudeCliUsagePoller.ProcessResult>(
					new IOException("process containment failed"));
			});

		ClaudeCliContainmentException firstException =
			await Assert.ThrowsAsync<ClaudeCliContainmentException>(() =>
				poller.PollAsync(accountId));
		ClaudeCliContainmentException secondException =
			await Assert.ThrowsAsync<ClaudeCliContainmentException>(() =>
				poller.PollAsync(accountId));

		Assert.Contains("重新啟動", firstException.Message, StringComparison.Ordinal);
		Assert.Contains("重新啟動", secondException.Message, StringComparison.Ordinal);
		Assert.Equal(1, Volatile.Read(ref runnerCallCount));
	}

	[Fact]
	public async Task PollAsync_WhenContainmentChangesWhileWaitingForGate_ThrowsContainmentInsteadOfTimeout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		ClaudeAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int processRunCount = 0;
		using IDisposable heldLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException("process must not run");
			},
			TimeProvider.System,
			operationGate,
			TimeSpan.FromMilliseconds(250));
		Task<ClaudeUsagePollResult> pollTask = poller.PollAsync(accountId);

		await Task.Delay(TimeSpan.FromMilliseconds(50));
		operationGate.MarkContainmentCompromised(accountId);

		ClaudeCliContainmentException exception =
			await Assert.ThrowsAsync<ClaudeCliContainmentException>(() => pollTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Contains("重新啟動", exception.Message, StringComparison.Ordinal);
		Assert.Equal(0, Volatile.Read(ref processRunCount));
	}

	[Fact]
	public async Task PollAsync_WhenInterruptedRecoveryPersistenceIsTransient_RetriesStoreOnly()
	{
		DateTimeOffset now = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int processCount = 0;
		TransientInterruptedRecoverySafetyStateStore store = new();
		Guid accountId = Guid.NewGuid();
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			store);

		ClaudeUsageSafetyException persistenceFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(
			persistenceFailure.CanRetryAutomatically,
			persistenceFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			persistenceFailure.AutomaticRetryNotBefore);
		Assert.Contains(
			"上次 Claude 用量檢查的狀態",
			persistenceFailure.Message,
			StringComparison.Ordinal);
		Assert.Equal(0, processCount);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		ClaudeUsageSafetyException manualFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.False(manualFailure.CanRetryAutomatically);
		Assert.Null(manualFailure.AutomaticRetryNotBefore);
		Assert.Equal(0, processCount);
		Assert.Equal(2, store.SaveCallCount);
		Assert.NotNull(store.PersistedState);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			store.PersistedState.RecoveryMode);
		Assert.Null(store.PersistedState.RetryNotBefore);
	}

	[Fact]
	public async Task PollAsync_WhenExecutableResolverBlocks_TimesOutWithoutStartingProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim resolverEntered = new();
		using ManualResetEventSlim releaseResolver = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		ClaudeAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int resolverCallCount = 0;
		int processRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() =>
			{
				Interlocked.Increment(ref resolverCallCount);
				resolverEntered.Set();
				releaseResolver.Wait();
				return executablePath;
			},
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					string.Empty,
					string.Empty));
			},
			TimeProvider.System,
			operationGate,
			TimeSpan.FromMilliseconds(250));
		Task<ClaudeUsagePollResult> pollTask = poller.PollAsync(accountId);

		try
		{
			Assert.True(resolverEntered.Wait(TimeSpan.FromSeconds(5)));
			await Assert.ThrowsAsync<TimeoutException>(() => pollTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
			await Assert.ThrowsAsync<TimeoutException>(() =>
				poller.PollAsync(accountId))
				.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, Volatile.Read(ref resolverCallCount));
			Assert.Equal(0, Volatile.Read(ref processRunCount));
		}
		finally
		{
			releaseResolver.Set();
		}

		using CancellationTokenSource gateReleaseTimeout = new(
			TimeSpan.FromSeconds(5));
		using IDisposable releasedLease = await operationGate.EnterAsync(
			accountId,
			gateReleaseTimeout.Token);
	}

	[Theory]
	[InlineData("\"num_turns\": 0", "\"num_turns\": 1")]
	[InlineData("\"num_turns\": 0", "\"num_turns\": 0,\n  \"queued_turn_count\": 1")]
	[InlineData("\"num_turns\": 0", "\"num_turns\": 0,\n  \"queued_turn_count\": -1")]
	[InlineData("\"total_cost_usd\": 0", "\"total_cost_usd\": 0.01")]
	[InlineData("\"input_tokens\": 0", "\"input_tokens\": 1")]
	[InlineData("\"spawned\": 0", "\"spawned\": 1")]
	[InlineData("\"background\": 0", "\"background\": 1")]
	[InlineData("\"by_type\": {}", "\"by_type\": {\"Explore\": 1}")]
	[InlineData("\"permission_denials\": []", "\"permission_denials\": [{}]")]
	public async Task PollAsync_WhenExplicitUsageActivityIsReported_RequiresManualRevalidation(
		string original,
		string replacement)
	{
		DateTimeOffset now = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			original,
			replacement,
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => SupportedVersion,
					"auth" => AuthStatusJson,
					_ => unsafeJson
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException exception =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		timeProvider.Advance(TimeSpan.FromDays(1));
		ClaudeUsageSafetyException disabledException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.Contains("2.1.220", exception.Message, StringComparison.Ordinal);
		Assert.Equal(exception.Message, disabledException.Message);
		Assert.Equal("claude@example.com", exception.AccountIdentity);
		Assert.Equal("claude@example.com", disabledException.AccountIdentity);
		Assert.False(exception.CanRetryAutomatically);
		Assert.False(disabledException.CanRetryAutomatically);
		Assert.Contains(
			"重新檢查 Claude 用量",
			disabledException.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, runCount);
	}

	[Theory]
	[InlineData("\"0\"")]
	[InlineData("null")]
	public async Task PollAsync_WhenQueuedTurnCountHasInvalidType_RetriesAutomatically(
		string queuedTurnCountJson)
	{
		DateTimeOffset now = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		string invalidJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0,",
			$"\"num_turns\": 0,\n  \"queued_turn_count\": {queuedTurnCountJson},",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => SupportedVersion,
					"auth" => AuthStatusJson,
					_ => invalidJson
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.True(pendingFailure.CanRetryAutomatically, pendingFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			pendingFailure.AutomaticRetryNotBefore);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PurgeAccountSafetyState_AfterSafetyFailure_AllowsFreshPollingState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		bool returnUnsafeUsage = true;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => SupportedVersion,
					"auth" => AuthStatusJson,
					_ => returnUnsafeUsage
						? unsafeJson
						: CreateResultJson(UsageText)
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(1, poller.CachedAccountSafetyStateCount);

		await poller.PurgeAccountSafetyStateAsync(
			accountId,
			ClaudeUsageSafetyStateClearPurpose.UserConfirmedReset);
		returnUnsafeUsage = false;

		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
		ClaudeUsagePollResult result = await poller.PollAsync(accountId);
		Assert.Equal(UsageText, result.Output);
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenDurableClearIsTransient_Retries()
	{
		TransientClearSafetyStateStore safetyStateStore = new(
			failuresBeforeSuccess: 2);
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => null,
			(_, _) => throw new InvalidOperationException("not used"),
			TimeProvider.System,
			safetyStateStore);

		await poller.PurgeAccountSafetyStateAsync(
			Guid.NewGuid(),
			ClaudeUsageSafetyStateClearPurpose.AccountRemoval);

		Assert.Equal(3, safetyStateStore.ClearCallCount);
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenRemovedStateIsCorrupt_CommitsClearIntent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(safetyStatePath, "{not-json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) => throw new InvalidOperationException("process must not start"),
			TimeProvider.System,
			store);

		await poller.PurgeAccountSafetyStateAsync(
			Guid.NewGuid(),
			ClaudeUsageSafetyStateClearPurpose.AccountRemoval);

		Assert.False(File.Exists(safetyStatePath));
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenCleanupRemainsPending_TreatsCommittedClearAsSuccess()
	{
		PendingClearSafetyStateStore safetyStateStore = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("not used"),
			(_, _) => throw new InvalidOperationException("not used"),
			TimeProvider.System,
			safetyStateStore);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(1, poller.CachedAccountSafetyStateCount);

		await poller.PurgeAccountSafetyStateAsync(
			accountId,
			ClaudeUsageSafetyStateClearPurpose.AccountRemoval);

		Assert.Equal(1, safetyStateStore.ClearCallCount);
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenContainedRecoveryIsUnconfirmed_PreservesIdentityFreeAttemptUntilRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 2,
				AccountIdentity: "removed@example.com",
				FailureReason:
					"Claude Code `/usage` 尚未確認是否產生用量就中斷。",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase:
					ClaudeUsageSafetyAttemptPhase.StartedContained));
		int recoveryCallCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) => throw new InvalidOperationException("process must not start"),
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			store,
			containedUsageAttemptRecovery: (observedAttemptId, timeout, _) =>
			{
				Assert.Equal(attemptId, observedAttemptId);
				Assert.True(timeout > TimeSpan.Zero);
				return Task.FromResult(
					Interlocked.Increment(ref recoveryCallCount) > 1);
			});

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PurgeAccountSafetyStateAsync(
					accountId,
					ClaudeUsageSafetyStateClearPurpose.AccountRemoval));

		Assert.True(failure.CanRetryAutomatically, failure.ToString());
		Assert.Equal(1, recoveryCallCount);
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
		ClaudeUsageSafetyState? retainedState = await store.LoadAsync(accountId);
		Assert.NotNull(retainedState);
		Assert.Null(retainedState.AccountIdentity);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			retainedState.RecoveryMode);
		Assert.Equal(attemptId, retainedState.AttemptId);
		Assert.Equal(
			ClaudeUsageSafetyAttemptPhase.StartedContained,
			retainedState.AttemptPhase);

		await poller.PurgeAccountSafetyStateAsync(
			accountId,
			ClaudeUsageSafetyStateClearPurpose.AccountRemoval);

		Assert.Equal(2, recoveryCallCount);
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
		Assert.Null(await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenLegacyAttemptHasNoId_RedactsIdentityBeforeClear()
	{
		FailingClearLegacyAttemptSafetyStateStore safetyStateStore = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) => throw new InvalidOperationException("process must not start"),
			TimeProvider.System,
			safetyStateStore);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<IOException>(
			() => poller.PurgeAccountSafetyStateAsync(
				accountId,
				ClaudeUsageSafetyStateClearPurpose.AccountRemoval));

		Assert.Equal(1, safetyStateStore.SaveCallCount);
		Assert.Equal(4, safetyStateStore.ClearCallCount);
		Assert.NotNull(safetyStateStore.PersistedState);
		Assert.Null(safetyStateStore.PersistedState.AccountIdentity);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			safetyStateStore.PersistedState.RecoveryMode);
		Assert.Null(safetyStateStore.PersistedState.AttemptId);
		Assert.Null(safetyStateStore.PersistedState.AttemptPhase);
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
	}

	[Fact]
	public async Task PurgeAccountSafetyStateAsync_WhenUserResetRecoveryIsUnconfirmed_PreservesAccountIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 1,
				AccountIdentity: "active@example.com",
				FailureReason: "interrupted contained attempt",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: attemptId,
				AttemptPhase:
					ClaudeUsageSafetyAttemptPhase.StartedContained));
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			() => throw new InvalidOperationException("process must not start"),
			(_, _) => throw new InvalidOperationException("process must not start"),
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			store,
			containedUsageAttemptRecovery: (_, _, _) => Task.FromResult(false));

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PurgeAccountSafetyStateAsync(
				accountId,
				ClaudeUsageSafetyStateClearPurpose.UserConfirmedReset));

		ClaudeUsageSafetyState? retainedState = await store.LoadAsync(accountId);
		Assert.NotNull(retainedState);
		Assert.Equal("active@example.com", retainedState.AccountIdentity);
		Assert.Equal(attemptId, retainedState.AttemptId);
		Assert.Equal(0, poller.CachedAccountSafetyStateCount);
	}

	[Fact]
	public async Task ResetUsageSafetyStateAsync_WhileUnsafePollIsRunning_WaitsAndAllowsFreshPoll()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		TaskCompletionSource<bool> usageEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseUsage = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		int usageRunCount = 0;
		ClaudeAccountOperationGate operationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			async (startInfo, _) =>
			{
				if (startInfo.ArgumentList[0] != "-p")
				{
					return new ClaudeCliUsagePoller.ProcessResult(
						0,
						GetStandardOutput(startInfo),
						string.Empty);
				}

				int usageRun = Interlocked.Increment(ref usageRunCount);

				if (usageRun == 1)
				{
					usageEntered.TrySetResult(true);
					await releaseUsage.Task;
				}

				return new ClaudeCliUsagePoller.ProcessResult(
					0,
					usageRun == 1
						? unsafeJson
						: CreateResultJson(UsageText),
					string.Empty);
			},
			TimeProvider.System,
			operationGate);
		ClaudeUsageProvider provider = new(
			poller,
			new NotConfiguredUsageProvider(ProviderKind.Claude));
		AccountRuntimeStatePurger runtimeStatePurger = new(
			operationGate,
			poller,
			provider,
			new CodexAccountOperationGate());
		Guid accountId = Guid.NewGuid();
		Task<ClaudeUsagePollResult> unsafePoll = poller.PollAsync(accountId);
		Task resetTask = Task.CompletedTask;

		try
		{
			await usageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			resetTask = runtimeStatePurger.ResetUsageSafetyStateAsync(
				accountId,
				ProviderKind.Claude);

			Assert.False(resetTask.IsCompleted);
		}
		finally
		{
			releaseUsage.TrySetResult(true);
		}

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(() => unsafePoll);
		await resetTask.WaitAsync(TimeSpan.FromSeconds(5));

		ClaudeUsagePollResult result = await poller.PollAsync(accountId)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(2, usageRunCount);
	}

	[Fact]
	public async Task PollAsync_WhenOneAccountLatches_DoesNotDisableAnotherAccount()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int usageRunCount = 0;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				string standardOutput = GetStandardOutput(startInfo);

				if (startInfo.ArgumentList[0] == "-p")
				{
					standardOutput = Interlocked.Increment(ref usageRunCount) == 1
						? unsafeJson
						: CreateResultJson(UsageText);
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(firstAccountId));
		ClaudeUsagePollResult result = await poller.PollAsync(secondAccountId);

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(2, usageRunCount);
	}

	[Fact]
	public async Task PollAsync_AfterSafetyFailure_RetainsFirstFailureReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int usageRunCount = 0;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"output_tokens\": 0",
			"\"output_tokens\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				string standardOutput = GetStandardOutput(startInfo);

				if (startInfo.ArgumentList[0] == "-p")
				{
					standardOutput = Interlocked.Increment(ref usageRunCount) == 1
						? unsafeJson
						: CreateResultJson(UsageText);
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException disabledException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.Equal(firstException.Message, disabledException.Message);
		Assert.False(firstException.CanRetryAutomatically);
		Assert.False(disabledException.CanRetryAutomatically);
		Assert.Contains(
			"可能產生用量",
			disabledException.Message,
			StringComparison.Ordinal);
		Assert.Contains(
			"重新檢查 Claude 用量",
			disabledException.Message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"重新啟動",
			disabledException.Message,
			StringComparison.Ordinal);
		Assert.Equal(1, usageRunCount);
	}

	[Fact]
	public async Task PollAsync_QueuedBehindSafetyFailure_ObservesLatch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		TaskCompletionSource<bool> usageEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseUsage = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int runCount = 0;
		int usageRunCount = 0;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			async (startInfo, _) =>
			{
				Interlocked.Increment(ref runCount);

				if (startInfo.ArgumentList[0] == "-p")
				{
					int usageRun = Interlocked.Increment(ref usageRunCount);

					if (usageRun == 1)
					{
						usageEntered.TrySetResult(true);
						await releaseUsage.Task;
					}

					return new ClaudeCliUsagePoller.ProcessResult(
						0,
						usageRun == 2
							? CreateResultJson(UsageText)
							: unsafeJson,
						string.Empty);
				}

				return new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty);
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();
		Task<ClaudeUsagePollResult> poll = poller.PollAsync(accountId);
		await usageEntered.Task;

		Task<ClaudeUsagePollResult> queuedPoll = poller.PollAsync(accountId);
		releaseUsage.TrySetResult(true);

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(() => poll);
		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(() => queuedPoll);
		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(accountId));

		Assert.Equal(1, usageRunCount);
		Assert.Equal(3, runCount);
	}

	[Theory]
	[InlineData(false, "claude.ai", "firstParty", "max")]
	[InlineData(true, "apiKey", "firstParty", "max")]
	[InlineData(true, "claude.ai", "bedrock", "max")]
	[InlineData(true, "claude.ai", "firstParty", "free")]
	public async Task PollAsync_WhenSubscriptionAuthenticationIsUnavailable_DoesNotRunUsage(
		bool loggedIn,
		string authMethod,
		string apiProvider,
		string subscriptionType)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		string authStatusJson = JsonSerializer.Serialize(new
		{
			loggedIn,
			authMethod,
			apiProvider,
			email = "claude@example.com",
			orgId = "org-123",
			orgName = "Example Organization",
			subscriptionType
		});
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] == "--version"
					? SupportedVersion
					: authStatusJson;
				int exitCode = (startInfo.ArgumentList[0] == "auth") && !loggedIn
					? 1
					: 0;
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					exitCode,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);

		ClaudeUsageNotConfiguredException exception =
			await Assert.ThrowsAsync<ClaudeUsageNotConfiguredException>(
				() => poller.PollAsync(Guid.NewGuid()));

		Assert.Equal(2, runCount);
		Assert.Equal(
			loggedIn ? "claude@example.com" : null,
			exception.AccountIdentity);
		Assert.Equal(
			loggedIn
				? UsageRecoveryAction.SwitchAccount
				: UsageRecoveryAction.ConnectAccount,
			exception.RecoveryAction);
	}

	[Fact]
	public async Task ProbeAsync_WhenSubscriptionClassificationSchemaIsUnavailable_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		string authStatusJson = AuthStatusJson.Replace(
			",\"subscriptionType\":\"max\"",
			string.Empty,
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] == "--version"
					? SupportedVersion
					: authStatusJson;
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => poller.ProbeAsync(Guid.NewGuid()));

		Assert.Contains("暫時", exception.Message, StringComparison.Ordinal);
		Assert.Equal(2, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenVerifiedEnterpriseUsageIsUnavailable_DoesNotRunUsage()
	{
		const string subscriptionType = "enterprise";
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		List<ProcessStartInfo> capturedStartInfos = new();
		string authStatusJson = JsonSerializer.Serialize(new
		{
			loggedIn = true,
			authMethod = "claude.ai",
			apiProvider = "firstParty",
			email = "claude@example.com",
			orgId = "org-123",
			orgName = "Example Organization",
			subscriptionType
		});
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				capturedStartInfos.Add(startInfo);
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => SupportedVersion,
					"auth" => authStatusJson,
					_ => CreateResultJson(UsageText)
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);

		ClaudeSubscriptionUsageUnavailableException exception =
			await Assert.ThrowsAsync<ClaudeSubscriptionUsageUnavailableException>(
				() => poller.PollAsync(Guid.NewGuid()));

		Assert.Equal(2, capturedStartInfos.Count);
		Assert.DoesNotContain(
			capturedStartInfos,
			startInfo => startInfo.ArgumentList.Contains("/usage"));
		Assert.Equal(subscriptionType, exception.SubscriptionContext.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			exception.SubscriptionContext.VerificationState);
		Assert.Equal(
			"org-123",
			exception.SubscriptionContext.SubscriptionScopeIdentity);
		CliVersionEvidence versionEvidence =
			Assert.IsType<CliVersionEvidence>(exception.VersionEvidence);
		Assert.Equal("2.1.220", versionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnverifiedAllowed,
			versionEvidence.Disposition);
	}

	[Fact]
	public async Task PollAsync_WhenVersionIsTooOld_DoesNotRunAuthOrUsage()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) =>
			{
				runCount++;
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					"2.1.168 (Claude Code)",
					string.Empty));
			},
			TimeProvider.System);

		ClaudeUsageNotConfiguredException exception =
			await Assert.ThrowsAsync<ClaudeUsageNotConfiguredException>(
				() => poller.PollAsync(Guid.NewGuid()));

		Assert.Contains("2.1.168", exception.Message, StringComparison.Ordinal);
		Assert.Contains("2.1.169", exception.Message, StringComparison.Ordinal);
		Assert.Equal(
			UsageRecoveryAction.InstallOrUpdate,
			exception.RecoveryAction);
		Assert.Equal(1, runCount);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Claude Code 2.1.210")]
	[InlineData("2.1.210")]
	[InlineData("2.1")]
	[InlineData("2.1.210-beta.1")]
	[InlineData("2.1.210\nextra")]
	[InlineData("02.1.169 (Claude Code)")]
	[InlineData("2.01.169 (Claude Code)")]
	[InlineData("2.1.0169 (Claude Code)")]
	public void EnsureSupportedVersion_WithUnknownFormat_FailsClosed(string output)
	{
		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.EnsureSupportedVersion(output));
	}

	[Theory]
	[InlineData("2.1.169 (Claude Code)")]
	[InlineData("2.1.210 (Claude Code)")]
	[InlineData("3.0.0 (Claude Code)")]
	public void EnsureSupportedVersion_WithSupportedVersion_ReturnsParsedVersion(string output)
	{
		Version version = ClaudeCliUsagePoller.EnsureSupportedVersion(output);

		Assert.True(version >= new Version(2, 1, 169));
	}

	[Fact]
	public async Task PollAsync_WhenAuthOutputIsEmpty_DoesNotLatchSafetyFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] == "--version"
					? SupportedVersion
					: string.Empty;
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<InvalidDataException>(
			() => poller.PollAsync(accountId));
		await Assert.ThrowsAsync<InvalidDataException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(4, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenUsageOutputIsEmpty_RetriesAutomatically()
	{
		DateTimeOffset now = new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				string standardOutput = startInfo.ArgumentList[0] switch
				{
					"--version" => SupportedVersion,
					"auth" => AuthStatusJson,
					_ => string.Empty
				};
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.True(pendingFailure.CanRetryAutomatically, pendingFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			pendingFailure.AutomaticRetryNotBefore);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenUnsafeUsageReturnsNonzeroExit_RequiresManualRevalidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		string unsafeJson = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0",
			"\"num_turns\": 1",
			StringComparison.Ordinal);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				bool isUsage = startInfo.ArgumentList[0] == "-p";
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					isUsage ? 1 : 0,
					isUsage ? unsafeJson : GetStandardOutput(startInfo),
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException blockedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.False(firstFailure.CanRetryAutomatically);
		Assert.False(blockedFailure.CanRetryAutomatically);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenUsageCannotBeVerified_LatchesSafetyFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;

				if (startInfo.ArgumentList[0] == "-p")
				{
					throw new TimeoutException("usage timed out");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(firstException.CanRetryAutomatically);
		Assert.True(pendingException.CanRetryAutomatically);
		Assert.Contains(
			"稍後會自動再試",
			pendingException.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenAutomaticRevalidationBecomesDue_RefreshesCurrentUsage()
	{
		DateTimeOffset now = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		int usageRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;

				if ((startInfo.ArgumentList[0] == "-p") &&
					(Interlocked.Increment(ref usageRunCount) == 1))
				{
					throw new TimeoutException("usage timed out");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException failure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.Equal(now + TimeSpan.FromMinutes(1), failure.AutomaticRetryNotBefore);

		timeProvider.Advance(TimeSpan.FromSeconds(59));
		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(3, runCount);

		timeProvider.Advance(TimeSpan.FromSeconds(1));
		ClaudeUsagePollResult result = await poller.PollAsync(accountId);

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(2, usageRunCount);
		Assert.Equal(6, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenAutomaticRevalidationPreflightFails_DoesNotConsumeAttempt()
	{
		DateTimeOffset now = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int usageRunCount = 0;
		int versionRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				if ((startInfo.ArgumentList[0] == "--version") &&
					(Interlocked.Increment(ref versionRunCount) == 2))
				{
					return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
						1,
						string.Empty,
						string.Empty));
				}

				if (startInfo.ArgumentList[0] == "-p")
				{
					Interlocked.Increment(ref usageRunCount);
					throw new TimeoutException("usage timed out");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
			() => poller.PollAsync(accountId));
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => poller.PollAsync(accountId));

		ClaudeUsageSafetyException retryFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.True(retryFailure.CanRetryAutomatically);
		Assert.Equal(
			now + TimeSpan.FromMinutes(3),
			retryFailure.AutomaticRetryNotBefore);
		Assert.Equal(2, usageRunCount);
	}

	[Fact]
	public async Task PollAsync_WhenAutomaticRevalidationBackoffSaturates_ContinuesRetrying()
	{
		DateTimeOffset now = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		string safetyStatePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		JsonClaudeUsageSafetyStateStore store = new(_ => safetyStatePath);
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;

				if (startInfo.ArgumentList[0] == "-p")
				{
					throw new TimeoutException("usage timed out");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider,
			store);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(firstFailure.CanRetryAutomatically);

		foreach (int delayMinutes in new[] { 1, 2 })
		{
			timeProvider.Advance(TimeSpan.FromMinutes(delayMinutes));
			ClaudeUsageSafetyException retryFailure =
				await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
					() => poller.PollAsync(accountId));
			Assert.True(retryFailure.CanRetryAutomatically);
		}

		timeProvider.Advance(TimeSpan.FromMinutes(4));
		ClaudeUsageSafetyException maximumBackoffFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(
			maximumBackoffFailure.CanRetryAutomatically,
			maximumBackoffFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(11),
			maximumBackoffFailure.AutomaticRetryNotBefore);
		Assert.DoesNotContain(
			"重新檢查 Claude 用量",
			maximumBackoffFailure.Message,
			StringComparison.Ordinal);
		ClaudeUsageSafetyState? maximumBackoffState =
			await store.LoadAsync(accountId);
		Assert.NotNull(maximumBackoffState);
		Assert.Equal(3, maximumBackoffState.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			maximumBackoffState.RecoveryMode);
		Assert.Equal(
			now + TimeSpan.FromMinutes(11),
			maximumBackoffState.RetryNotBefore);

		timeProvider.Advance(TimeSpan.FromMinutes(4));
		ClaudeUsageSafetyException saturatedFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(
			saturatedFailure.CanRetryAutomatically,
			saturatedFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(15),
			saturatedFailure.AutomaticRetryNotBefore);
		Assert.Equal(15, runCount);
		ClaudeUsageSafetyState? saturatedState = await store.LoadAsync(accountId);
		Assert.NotNull(saturatedState);
		Assert.Equal(3, saturatedState.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			saturatedState.RecoveryMode);
	}

	[Fact]
	public async Task PollAsync_WhenSafeUsageReturnsNonzeroExit_RetriesAutomatically()
	{
		DateTimeOffset now = new(2026, 8, 15, 11, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				bool isUsage = startInfo.ArgumentList[0] == "-p";
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					isUsage ? 1 : 0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.True(pendingFailure.CanRetryAutomatically, pendingFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			pendingFailure.AutomaticRetryNotBefore);
		Assert.DoesNotContain(
			"重新檢查 Claude 用量",
			pendingFailure.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenUsageIsCanceled_LatchesSafetyFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		TaskCompletionSource<bool> usageEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			async (startInfo, cancellationToken) =>
			{
				Interlocked.Increment(ref runCount);

				if (startInfo.ArgumentList[0] == "-p")
				{
					usageEntered.TrySetResult(true);
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}

				return new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty);
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();
		using CancellationTokenSource cancellationSource = new();
		Task<ClaudeUsagePollResult> poll = poller.PollAsync(
			accountId,
			cancellationSource.Token);
		await usageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellationSource.Cancel();

		OperationCanceledException cancellationException =
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
		ClaudeUsageSafetyException disabledException =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));

		Assert.Equal(
			cancellationSource.Token,
			cancellationException.CancellationToken);
		Assert.Equal("claude@example.com", disabledException.AccountIdentity);
		Assert.Contains(
			"稍後會自動再試",
			disabledException.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenAuthExitConflictsWithLoggedInJson_DoesNotLatchSafetyFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;
				bool isAuth = startInfo.ArgumentList[0] == "auth";
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					isAuth ? 1 : 0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<InvalidDataException>(
			() => poller.PollAsync(accountId));
		await Assert.ThrowsAsync<InvalidDataException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(4, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenVersionDecoderFails_DoesNotLatchSafetyFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) =>
			{
				runCount++;
				throw new DecoderFallbackException("Invalid UTF-8.");
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<DecoderFallbackException>(
			() => poller.PollAsync(accountId));
		await Assert.ThrowsAsync<DecoderFallbackException>(
			() => poller.PollAsync(accountId));
		Assert.Equal(2, runCount);
	}

	[Fact]
	public async Task PollAsync_WhenUsageDecoderFails_RetriesAutomatically()
	{
		DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
		FakeTimeProvider timeProvider = new(now);
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				runCount++;

				if (startInfo.ArgumentList[0] == "-p")
				{
					throw new DecoderFallbackException("Invalid UTF-8.");
				}

				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty));
			},
			timeProvider);
		Guid accountId = Guid.NewGuid();

		ClaudeUsageSafetyException firstFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		ClaudeUsageSafetyException pendingFailure =
			await Assert.ThrowsAsync<ClaudeUsageSafetyException>(
				() => poller.PollAsync(accountId));
		Assert.True(firstFailure.CanRetryAutomatically, firstFailure.ToString());
		Assert.True(pendingFailure.CanRetryAutomatically, pendingFailure.ToString());
		Assert.Equal(
			now + TimeSpan.FromMinutes(1),
			pendingFailure.AutomaticRetryNotBefore);
		Assert.Equal(3, runCount);
	}

	[Theory]
	[InlineData("\"total_cost_usd\": 0", "\"total_cost_usd\": 0.01")]
	[InlineData("\"total_cost_usd\": 0", "\"total_cost_usd\": 1e-400")]
	[InlineData("\"input_tokens\": 0", "\"input_tokens\": 1")]
	[InlineData("\"input_tokens\": 0", "\"input_tokens\": 1e-400")]
	[InlineData("\"is_error\": false", "\"is_error\": true")]
	[InlineData("\"cache_read_input_tokens\": 0", "\"cache_read_input_tokens\": \"0\"")]
	[InlineData("\"web_search_requests\": 0", "\"web_search_requests\": 1")]
	[InlineData("\"iterations\": []", "\"iterations\": [{}]")]
	[InlineData("\"service_tier\": \"standard\"", "\"service_tier\": 0")]
	[InlineData("\"modelUsage\": {}", "\"modelUsage\": []")]
	[InlineData("\"modelUsage\": {}", "\"modelUsage\": {\"model\": {\"inputTokens\": 1}}")]
	[InlineData("\"permission_denials\": []", "\"permission_denials\": [{}]")]
	[InlineData("\"permission_denials\": []", "\"permission_denials\": null")]
	public void ParseSafeResult_WithUnsafeUsage_FailsClosed(
		string original,
		string replacement)
	{
		string json = CreateResultJson(UsageText).Replace(
			original,
			replacement,
			StringComparison.Ordinal);

		Assert.ThrowsAny<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData("\"input_tokens\": 0,", "")]
	[InlineData("\"output_tokens\": 0,", "")]
	[InlineData("\"input_tokens\": 0", "\"input_tokens\": \"0\"")]
	[InlineData("\"output_tokens\": 0", "\"output_tokens\": false")]
	public void ParseSafeResult_WithMissingOrNonNumericRequiredToken_FailsClosed(
		string original,
		string replacement)
	{
		string json = CreateResultJson(UsageText).Replace(
			original,
			replacement,
			StringComparison.Ordinal);

		Assert.ThrowsAny<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData("input_tokens")]
	[InlineData("output_tokens")]
	[InlineData("cache_read_input_tokens")]
	[InlineData("cache_creation_input_tokens")]
	public void ParseSafeResult_WithMissingKnownTokenCounter_FailsClosed(
		string propertyName)
	{
		JsonObject root = JsonNode.Parse(CreateResultJson(UsageText))?.AsObject() ??
			throw new InvalidOperationException("Test fixture must be a JSON object.");
		JsonObject usage = root["usage"]?.AsObject() ??
			throw new InvalidOperationException("Test fixture must contain usage.");
		Assert.True(usage.Remove(propertyName));
		string json = root.ToJsonString();

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public async Task PollAsync_WhenVersionProcessFails_DoesNotLatchFormatFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) =>
			{
				runCount++;
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					1,
					string.Empty,
					"failed"));
			},
			TimeProvider.System);
		Guid accountId = Guid.NewGuid();

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => poller.PollAsync(accountId));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => poller.PollAsync(accountId));

		Assert.Equal(2, runCount);
	}

	[Fact]
	public async Task PollAsync_WithConcurrentAccounts_AllowsIndependentPolling()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		TaskCompletionSource<bool> firstCallEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseFirstCall = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int runCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			async (startInfo, _) =>
			{
				int currentRun = Interlocked.Increment(ref runCount);

				if (currentRun == 1)
				{
					firstCallEntered.SetResult(true);
					await releaseFirstCall.Task;
				}

				return new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo),
					string.Empty);
			},
			TimeProvider.System);

		Task<ClaudeUsagePollResult> firstPoll = poller.PollAsync(Guid.NewGuid());
		await firstCallEntered.Task;
		Task<ClaudeUsagePollResult> secondPoll = poller.PollAsync(Guid.NewGuid());
		ClaudeUsagePollResult secondResult;

		try
		{
			secondResult = await secondPoll.WaitAsync(TimeSpan.FromSeconds(1));
			Assert.False(firstPoll.IsCompleted);
		}
		finally
		{
			releaseFirstCall.TrySetResult(true);
		}

		await firstPoll;
		Assert.Equal(UsageText, secondResult.Output);
		Assert.Equal(6, runCount);
	}

	[Fact]
	public async Task PollBoundAsync_WhenBindingIsMissing_FailsBeforeRunningCli()
	{
		Guid accountId = Guid.NewGuid();
		int processRunCount = 0;
		InMemoryClaudeAccountBindingStore bindingStore = new();
		ClaudeCliUsagePoller poller = CreateBoundPoller(
			bindingStore,
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException("CLI must not run.");
			});

		await Assert.ThrowsAsync<ClaudeSubscriptionBindingMissingException>(
			() => poller.PollBoundAsync(
				accountId,
				Guid.NewGuid().ToString("N")));

		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, bindingStore.LoadAllCallCount);
		Assert.Equal(0, processRunCount);
	}

	[Fact]
	public async Task PollBoundAsync_WhenPublicBindingDoesNotMatch_FailsBeforeRunningCli()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"claude@example.com",
				"org-123",
				"max",
				"Example Organization");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		int processRunCount = 0;
		InMemoryClaudeAccountBindingStore bindingStore = new(binding);
		ClaudeCliUsagePoller poller = CreateBoundPoller(
			bindingStore,
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException("CLI must not run.");
			});

		await Assert.ThrowsAsync<ClaudeSubscriptionBindingInvalidException>(
			() => poller.PollBoundAsync(
				accountId,
				Guid.NewGuid().ToString("N")));

		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, bindingStore.LoadAllCallCount);
		Assert.Equal(0, processRunCount);
	}

	[Fact]
	public async Task PollBoundAsync_WhenPrivateBindingIsMalformed_FailsBeforeRunningCli()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"claude@example.com",
				"org-123",
				"max",
				"Example Organization");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		ClaudeAccountBinding malformedBinding = binding with
		{
			SaltBase64 = "not-base64"
		};
		int processRunCount = 0;
		InMemoryClaudeAccountBindingStore bindingStore = new(malformedBinding);
		ClaudeCliUsagePoller poller = CreateBoundPoller(
			bindingStore,
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException("CLI must not run.");
			});

		await Assert.ThrowsAsync<ClaudeSubscriptionBindingInvalidException>(
			() => poller.PollBoundAsync(
				accountId,
				ClaudeAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, bindingStore.LoadAllCallCount);
		Assert.Equal(0, processRunCount);
	}

	[Fact]
	public async Task PollBoundAsync_WhenPrivateBindingDocumentIsMalformed_RequiresRepairBeforeRunningCli()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		string bindingDirectory = Path.Combine(
			temporaryDirectory.Path,
			accountId.ToString("N"));
		string bindingPath = Path.Combine(
			bindingDirectory,
			"account-binding-v1.json");
		Directory.CreateDirectory(bindingDirectory);
		await File.WriteAllTextAsync(bindingPath, "{}");
		JsonClaudeAccountBindingStore bindingStore = new(
			_ => bindingPath,
			() => temporaryDirectory.Path);
		int processRunCount = 0;
		ClaudeCliUsagePoller poller = CreateBoundPoller(
			bindingStore,
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException("CLI must not run.");
			});

		await Assert.ThrowsAsync<ClaudeSubscriptionBindingInvalidException>(
			() => poller.PollBoundAsync(
				accountId,
				Guid.NewGuid().ToString("N")));

		Assert.Equal(0, processRunCount);
	}

	[Theory]
	[InlineData("max")]
	[InlineData("team")]
	public async Task PollBoundAsync_WithMatchingBinding_ValidatesEntitlementAndReturnsUsage(
		string subscriptionType)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"claude@example.com",
				"org-123",
				subscriptionType,
				"Example Organization");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		InMemoryClaudeAccountBindingStore bindingStore = new(binding);
		int processRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo, subscriptionType),
					string.Empty));
			},
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: bindingStore,
			bindingCommitGate: new ClaudeBindingCommitGate());

		ClaudeUsagePollResult result = await poller.PollBoundAsync(
			accountId,
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId));

		Assert.Equal(UsageText, result.Output);
		Assert.Equal(context, result.SubscriptionContext);
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(1, bindingStore.LoadAllCallCount);
		Assert.Equal(3, processRunCount);
	}

	[Theory]
	[InlineData("max")]
	[InlineData("team")]
	public async Task PollBoundAsync_WhenObservedEntitlementDiffers_ReportsDefiniteMismatchBeforeUsage(
		string subscriptionType)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "config");
		await File.WriteAllTextAsync(executablePath, string.Empty);
		Directory.CreateDirectory(configDirectory);
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext differentContext =
			ClaudeSubscriptionContext.CreateVerified(
				"claude@example.com",
				"org-other",
				subscriptionType,
				"Other Organization");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			differentContext);
		InMemoryClaudeAccountBindingStore bindingStore = new(binding);
		int processRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => configDirectory,
			() => executablePath,
			(startInfo, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				return Task.FromResult(new ClaudeCliUsagePoller.ProcessResult(
					0,
					GetStandardOutput(startInfo, subscriptionType),
					string.Empty));
			},
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: bindingStore,
			bindingCommitGate: new ClaudeBindingCommitGate());

		await Assert.ThrowsAsync<ClaudeSubscriptionScopeMismatchException>(
			() => poller.PollBoundAsync(
				accountId,
				ClaudeAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, bindingStore.LoadAllCallCount);
		Assert.Equal(2, processRunCount);
	}

	[Fact]
	public void Constructor_WithBindingStoreButNoSharedGate_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new ClaudeCliUsagePoller(
			_ => @"C:\claude-config",
			() => @"C:\claude.exe",
			(_, _) => throw new InvalidOperationException("CLI must not run."),
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: new EmptyClaudeAccountBindingStore()));
	}

	[Fact]
	public async Task PollAsync_WhenWaitingForAccountOperationGateIsCanceled_ReleasesGlobalBindingGate()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeAccountOperationGate operationGate = new();
		ClaudeBindingCommitGate bindingCommitGate = new();
		int processRunCount = 0;
		ClaudeCliUsagePoller poller = new(
			_ => @"C:\claude-config",
			() => @"C:\claude.exe",
			(_, _) =>
			{
				Interlocked.Increment(ref processRunCount);
				throw new InvalidOperationException(
					"Process runner must not be called while the account gate is held.");
			},
			TimeProvider.System,
			operationGate,
			TimeSpan.FromSeconds(5),
			accountBindingStore: new EmptyClaudeAccountBindingStore(),
			bindingCommitGate: bindingCommitGate);

		using IDisposable accountLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);
		using CancellationTokenSource pollCancellationSource = new();
		Task<ClaudeUsagePollResult> blockedPoll = poller.PollAsync(
			accountId,
			pollCancellationSource.Token);

		try
		{
			using CancellationTokenSource observationCancellationSource =
				new(TimeSpan.FromMilliseconds(100));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			{
				using IDisposable unexpectedLease = await bindingCommitGate.EnterAsync(
					observationCancellationSource.Token);
			});
		}
		finally
		{
			pollCancellationSource.Cancel();
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedPoll);

		using IDisposable bindingLease = await bindingCommitGate
			.EnterAsync(CancellationToken.None)
			.AsTask()
			.WaitAsync(TimeSpan.FromSeconds(1));
		Assert.Equal(0, processRunCount);
	}

	[Fact]
	public void ParseSafeResult_WithDuplicateProperty_FailsClosed()
	{
		string duplicateRootProperty = CreateResultJson(UsageText).Replace(
			"\"type\": \"result\",",
			"\"type\": \"result\",\n  \"type\": \"result\",",
			StringComparison.Ordinal);
		string duplicateNestedProperty = CreateResultJson(UsageText).Replace(
			"\"input_tokens\": 0,",
			"\"input_tokens\": 0,\n    \"input_tokens\": 0,",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(
				duplicateRootProperty,
				DateTimeOffset.UtcNow));
		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(
				duplicateNestedProperty,
				DateTimeOffset.UtcNow));
	}

	[Fact]
	public void ParseSafeResult_WithUnknownUsageMetadata_FailsClosed()
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"speed\": \"standard\"",
			"\"speed\": \"standard\",\n    \"unknown_metadata\": \"value\"",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public void ParseSafeResult_WithoutSubagentStats_RemainsCompatible()
	{
		JsonObject root = JsonNode.Parse(CreateResultJson(UsageText))?.AsObject() ??
			throw new InvalidOperationException("Test fixture must be a JSON object.");
		Assert.True(root.Remove("subagent_stats"));

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			root.ToJsonString(),
			DateTimeOffset.UtcNow);

		Assert.Equal(UsageText, result.Output);
	}

	[Theory]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("\"zero\"")]
	[InlineData("{\"spawned\":\"0\"}")]
	public void ParseSafeResult_WithMalformedSubagentStats_FailsClosed(
		string subagentStatsJson)
	{
		JsonObject root = JsonNode.Parse(CreateResultJson(UsageText))?.AsObject() ??
			throw new InvalidOperationException("Test fixture must be a JSON object.");
		root["subagent_stats"] = JsonNode.Parse(subagentStatsJson);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(
				root.ToJsonString(),
				DateTimeOffset.UtcNow));
	}

	private sealed class TransientLoadSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		internal int LoadCallCount { get; private set; }

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			LoadCallCount++;

			if (LoadCallCount == 1)
			{
				throw new IOException("safety state unavailable");
			}

			return Task.FromResult<ClaudeUsageSafetyState?>(null);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}
	}

	private sealed class FailingPrearmSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<ClaudeUsageSafetyState?>(null);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			throw new IOException("safety state unavailable");
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}
	}

	private sealed class TransientInterruptedRecoverySafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		internal int SaveCallCount { get; private set; }

		internal ClaudeUsageSafetyState? PersistedState { get; private set; } = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted usage attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null);

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(PersistedState);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			SaveCallCount++;

			if (SaveCallCount == 1)
			{
				throw new IOException("safety state unavailable");
			}

			PersistedState = state;
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}
	}

	private sealed class TransientClearSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		private readonly int _failuresBeforeSuccess;

		internal int ClearCallCount { get; private set; }

		internal TransientClearSafetyStateStore(int failuresBeforeSuccess)
		{
			_failuresBeforeSuccess = failuresBeforeSuccess;
		}

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<ClaudeUsageSafetyState?>(null);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			ClearCallCount++;

			if (ClearCallCount <= _failuresBeforeSuccess)
			{
				throw new IOException("transient clear failure");
			}

			return Task.CompletedTask;
		}
	}

	private sealed class PendingClearSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		internal int ClearCallCount { get; private set; }

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<ClaudeUsageSafetyState?>(new(
				AutomaticRevalidationAttemptCount: 3,
				AccountIdentity: "removed@example.com",
				FailureReason: "manual safety latch",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
				RetryNotBefore: null));
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			ClearCallCount++;
			throw new ClaudeUsageSafetyStateCleanupPendingException(
				"cleanup pending");
		}
	}

	private sealed class FailingClearLegacyAttemptSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		internal int SaveCallCount { get; private set; }

		internal int ClearCallCount { get; private set; }

		internal ClaudeUsageSafetyState? PersistedState { get; private set; } =
			new(
				AutomaticRevalidationAttemptCount: 1,
				AccountIdentity: "removed@example.com",
				FailureReason: "legacy interrupted attempt",
				RecoveryMode:
					ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				LegacySourceSchemaVersion: 1);

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(PersistedState);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			SaveCallCount++;
			PersistedState = state;
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			ClearCallCount++;
			throw new IOException("clear unavailable");
		}
	}

	[Theory]
	[InlineData("1")]
	[InlineData("-1")]
	[InlineData("\"0\"")]
	[InlineData("null")]
	[InlineData("[]")]
	public void ParseSafeResult_WithUnsafeOutputTokenDetails_FailsClosed(
		string outputTokenDetailsValue)
	{
		JsonObject root = JsonNode.Parse(CreateResultJson(UsageText))?.AsObject() ??
			throw new InvalidOperationException("Test fixture must be a JSON object.");
		JsonObject usage = root["usage"]?.AsObject() ??
			throw new InvalidOperationException("Test fixture must contain usage.");
		usage["output_tokens_details"] = JsonNode.Parse(outputTokenDetailsValue);
		string json = root.ToJsonString();

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public void ParseSafeResult_WithNonZeroThinkingTokens_FailsClosed()
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"thinking_tokens\": 0",
			"\"thinking_tokens\": 1",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public void ParseSafeResult_WithUnknownRootProperty_FailsClosed()
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"type\": \"result\",",
			"\"type\": \"result\",\n  \"unknown_root\": 0,",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public void ParseSafeResult_WithZeroQueuedTurnCount_Succeeds()
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0,",
			"\"num_turns\": 0,\n  \"queued_turn_count\": 0,",
			StringComparison.Ordinal);

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			json,
			DateTimeOffset.UtcNow);

		Assert.Equal(UsageText, result.Output);
	}

	[Theory]
	[InlineData("1")]
	[InlineData("-1")]
	[InlineData("\"0\"")]
	[InlineData("null")]
	public void ParseSafeResult_WithInvalidQueuedTurnCount_FailsClosed(
		string queuedTurnCountJson)
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"num_turns\": 0,",
			$"\"num_turns\": 0,\n  \"queued_turn_count\": {queuedTurnCountJson},",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData("\"duration_ms\": 12", "\"duration_ms\": -1")]
	[InlineData("\"duration_api_ms\": 8", "\"duration_api_ms\": 1.5")]
	[InlineData("\"session_id\": \"session-test\"", "\"session_id\": \"\"")]
	[InlineData("\"session_id\": \"session-test\"", "\"session_id\": null")]
	[InlineData(
		"\"uuid\": \"01234567-89ab-cdef-0123-456789abcdef\"",
		"\"uuid\": \"not-a-uuid\"")]
	[InlineData("\"stop_reason\": null", "\"stop_reason\": \"end_turn\"")]
	[InlineData(
		"\"terminal_reason\": \"completed\"",
		"\"terminal_reason\": \"error\"")]
	[InlineData(
		"\"fast_mode_state\": \"off\"",
		"\"fast_mode_state\": null")]
	[InlineData(
		"\"fast_mode_disabled_reason\": \"preference\"",
		"\"fast_mode_disabled_reason\": \"\"")]
	[InlineData(
		"\"fast_mode_disabled_reason\": \"preference\"",
		"\"fast_mode_disabled_reason\": null")]
	public void ParseSafeResult_WithInvalidStandardEnvelopeMetadata_FailsClosed(
		string original,
		string replacement)
	{
		string json = CreateResultJson(UsageText).Replace(
			original,
			replacement,
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData("off")]
	[InlineData("cooldown")]
	[InlineData("on")]
	[InlineData("future_state")]
	public void ParseSafeResult_WithBoundedFastModeState_Succeeds(string value)
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"fast_mode_state\": \"off\"",
			$"\"fast_mode_state\": {JsonSerializer.Serialize(value)}",
			StringComparison.Ordinal);

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			json,
			DateTimeOffset.UtcNow);

		Assert.Equal(UsageText, result.Output);
	}

	[Theory]
	[InlineData("free")]
	[InlineData("preference")]
	[InlineData("extra_usage_disabled")]
	[InlineData("network_error")]
	[InlineData("unknown")]
	[InlineData("not_first_party")]
	[InlineData("disabled_by_env")]
	[InlineData("model_not_allowed")]
	[InlineData("sdk_opt_in_required")]
	[InlineData("pending")]
	[InlineData("future_reason")]
	public void ParseSafeResult_WithBoundedFastModeDisabledReason_Succeeds(
		string value)
	{
		string json = CreateResultJson(UsageText).Replace(
			"\"fast_mode_disabled_reason\": \"preference\"",
			$"\"fast_mode_disabled_reason\": {JsonSerializer.Serialize(value)}",
			StringComparison.Ordinal);

		ClaudeUsagePollResult result = ClaudeCliUsagePoller.ParseSafeResult(
			json,
			DateTimeOffset.UtcNow);

		Assert.Equal(UsageText, result.Output);
	}

	[Theory]
	[InlineData("fast_mode_state", "off")]
	[InlineData("fast_mode_disabled_reason", "preference")]
	public void ParseSafeResult_WithOversizedFastModeMetadata_FailsClosed(
		string propertyName,
		string originalValue)
	{
		string oversizedValue = new('x', 129);
		string json = CreateResultJson(UsageText).Replace(
			$"\"{propertyName}\": \"{originalValue}\"",
			$"\"{propertyName}\": {JsonSerializer.Serialize(oversizedValue)}",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSafeResult(json, DateTimeOffset.UtcNow));
	}

	[Fact]
	public void EnsureSubscriptionAuthentication_WithDuplicateProperty_FailsClosed()
	{
		string json =
			"{\"loggedIn\":true,\"loggedIn\":true," +
			"\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\"," +
			"\"email\":\"claude@example.com\",\"orgId\":\"org-123\"," +
			"\"orgName\":\"Example Organization\",\"subscriptionType\":\"max\"}";

		Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.EnsureSubscriptionAuthentication(json));
	}

	[Fact]
	public void EnsureSubscriptionAuthentication_WithoutEmail_IsRetryablePollingFailure()
	{
		string json =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"orgId\":\"org-123\"," +
			"\"orgName\":\"Example Organization\",\"subscriptionType\":\"pro\"}";

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.EnsureSubscriptionAuthentication(json));

		Assert.Contains("暫時", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("連接", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("null")]
	[InlineData("42")]
	[InlineData("\"bad\\nemail\"")]
	public void EnsureSubscriptionAuthentication_WithInvalidEmail_IsRetryablePollingFailure(
		string emailJson)
	{
		string json =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"subscriptionType\":\"max\"," +
			"\"orgId\":\"org-123\",\"orgName\":\"Example Organization\"," +
			$"\"email\":{emailJson}}}";

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.EnsureSubscriptionAuthentication(json));

		Assert.Contains("暫時", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("連接", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ParseSubscriptionContext_WithCanonicalFields_ReturnsVerifiedContext()
	{
		string json =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"email\":\" Claude@Example.COM \"," +
			"\"orgId\":\" org-123 \",\"orgName\":\" Example Organization \"," +
			"\"subscriptionType\":\"max\"}";

		ClaudeSubscriptionContext context =
			ClaudeCliUsagePoller.ParseSubscriptionContext(json);

		Assert.Equal("claude@example.com", context.AccountIdentity);
		Assert.Equal("org-123", context.SubscriptionScopeIdentity);
		Assert.Equal("Example Organization", context.SubscriptionScopeDisplayName);
		Assert.Equal("max", context.PlanTier);
		Assert.Equal(
			ClaudeSubscriptionContext.EntitlementKeyVersion,
			context.ProviderDefinedEntitlementKey.Version);
		Assert.Equal(
			context.AccountIdentity,
			context.ProviderDefinedEntitlementKey.AccountIdentity);
		Assert.Equal(
			context.SubscriptionScopeIdentity,
			context.ProviderDefinedEntitlementKey.SubscriptionScopeIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			context.VerificationState);
	}

	[Theory]
	[InlineData("authMethod", "")]
	[InlineData("authMethod", "null")]
	[InlineData("authMethod", "42")]
	[InlineData("authMethod", "\"\"")]
	[InlineData("authMethod", "\"bad\\nvalue\"")]
	[InlineData("apiProvider", "")]
	[InlineData("apiProvider", "null")]
	[InlineData("apiProvider", "42")]
	[InlineData("apiProvider", "\"\"")]
	[InlineData("apiProvider", "\"bad\\nvalue\"")]
	[InlineData("subscriptionType", "")]
	[InlineData("subscriptionType", "null")]
	[InlineData("subscriptionType", "42")]
	[InlineData("subscriptionType", "\"\"")]
	[InlineData("subscriptionType", "\"bad\\nvalue\"")]
	public void ParseSubscriptionContext_WithUnavailableClassificationField_IsRetryable(
		string propertyName,
		string replacementJson)
	{
		JsonObject authStatus = JsonNode.Parse(AuthStatusJson)!.AsObject();
		if (replacementJson.Length == 0)
		{
			authStatus.Remove(propertyName);
		}
		else
		{
			authStatus[propertyName] = JsonNode.Parse(replacementJson);
		}

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSubscriptionContext(
				authStatus.ToJsonString()));

		Assert.Contains("暫時", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ParseSubscriptionContext_WithoutOrganizationId_FailsClosedWithScopeMarker()
	{
		string json =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"email\":\"claude@example.com\"," +
			"\"orgName\":\"Example Organization\",\"subscriptionType\":\"max\"}";

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			ClaudeCliUsagePoller.ParseSubscriptionContext(json));

		Assert.True(ClaudeCliUsagePoller.IsSubscriptionScopeUnavailable(exception));
		Assert.False(ClaudeCliUsagePoller.IsAccountIdentityUnavailable(exception));
	}

	[Fact]
	public void ParseSubscriptionContext_WithSameEmailAndDifferentOrganizations_ReturnsDistinctContexts()
	{
		const string firstJson =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"email\":\"claude@example.com\"," +
			"\"orgId\":\"org-a\",\"orgName\":\"Organization A\"," +
			"\"subscriptionType\":\"max\"}";
		const string secondJson =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"email\":\"claude@example.com\"," +
			"\"orgId\":\"org-b\",\"orgName\":\"Organization B\"," +
			"\"subscriptionType\":\"max\"}";

		ClaudeSubscriptionContext firstContext =
			ClaudeCliUsagePoller.ParseSubscriptionContext(firstJson);
		ClaudeSubscriptionContext secondContext =
			ClaudeCliUsagePoller.ParseSubscriptionContext(secondJson);

		Assert.Equal(firstContext.AccountIdentity, secondContext.AccountIdentity);
		Assert.NotEqual(
			firstContext.SubscriptionScopeIdentity,
			secondContext.SubscriptionScopeIdentity);
		Assert.NotEqual(
			firstContext.ProviderDefinedEntitlementKey,
			secondContext.ProviderDefinedEntitlementKey);
	}

	[Fact]
	public void CreateStartInfo_WithRelativePath_RejectsShellResolution()
	{
		Assert.Throws<ArgumentException>(() => ClaudeCliUsagePoller.CreateStartInfo(
			"claude.exe",
			Path.GetFullPath("config")));
		Assert.Throws<ArgumentException>(() =>
			ClaudeCliUsagePoller.CreateAuthStatusStartInfo(
				"claude.exe",
				Path.GetFullPath("config")));
		Assert.Throws<ArgumentException>(() =>
			ClaudeCliUsagePoller.CreateVersionStartInfo(
				"claude.exe",
				Path.GetFullPath("config")));
	}

	private static Task WriteLegacySafetyStateAsync(
		string filePath,
		int automaticRevalidationAttemptCount,
		string failureReason,
		ClaudeUsageSafetyRecoveryMode recoveryMode =
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
		DateTimeOffset? retryNotBefore = null)
	{
		string json = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			automaticRevalidationAttemptCount,
			accountIdentity = "claude@example.com",
			failureReason,
			recoveryMode = recoveryMode.ToString(),
			retryNotBefore
		});
		return File.WriteAllTextAsync(filePath, json);
	}

	private static Task WriteLegacySafetyTransitionAsync(
		string filePath,
		Guid accountId,
		string failureReason)
	{
		string json = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			accountId,
			automaticRevalidationAttemptCount = 0,
			accountIdentity = "claude@example.com",
			failureReason,
			recoveryMode =
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired.ToString(),
			retryNotBefore = (DateTimeOffset?)null
		});
		return File.WriteAllTextAsync(filePath, json);
	}

	private static string CreateResultJson(string result)
	{
		return $$"""
			{
			  "type": "result",
			  "subtype": "success",
			  "is_error": false,
			  "num_turns": 0,
			  "total_cost_usd": 0,
			  "duration_ms": 12,
			  "duration_api_ms": 8,
			  "session_id": "session-test",
			  "uuid": "01234567-89ab-cdef-0123-456789abcdef",
			  "stop_reason": null,
			  "terminal_reason": "completed",
			  "fast_mode_state": "off",
			  "fast_mode_disabled_reason": "preference",
			  "usage": {
			    "input_tokens": 0,
			    "output_tokens": 0,
			    "output_tokens_details": {
			      "thinking_tokens": 0
			    },
			    "cache_read_input_tokens": 0,
			    "cache_creation_input_tokens": 0,
			    "server_tool_use": {
			      "web_search_requests": 0
			    },
			    "service_tier": "standard",
			    "cache_creation": {
			      "ephemeral_5m_input_tokens": 0,
			      "ephemeral_1h_input_tokens": 0
			    },
			    "inference_geo": "not_available",
			    "iterations": [],
			    "speed": "standard"
			  },
			  "modelUsage": {},
			  "subagent_stats": {
			    "spawned": 0,
			    "requested": {
			      "background": 0,
			      "foreground": 0,
			      "unset": 0
			    },
			    "started_in_background": 0,
			    "by_type": {},
			    "max_depth": 0,
			    "spawned_by_subagents": 0,
			    "completed": 0,
			    "failed": 0,
			    "killed": {
			      "parent": 0,
			      "user": 0,
			      "system": 0
			    },
			    "refused": {
			      "depth_limit": 0,
			      "concurrency_limit": 0,
			      "budget": 0
			    }
			  },
			  "permission_denials": [],
			  "result": {{JsonSerializer.Serialize(result)}}
			}
			""";
	}

	private static ClaudeCliUsagePoller CreateBoundPoller(
		IClaudeAccountBindingStore bindingStore,
		Func<
			ProcessStartInfo,
			CancellationToken,
			Task<ClaudeCliUsagePoller.ProcessResult>> processRunner)
	{
		return new ClaudeCliUsagePoller(
			_ => @"C:\claude-config",
			() => @"C:\claude.exe",
			processRunner,
			TimeProvider.System,
			new ClaudeAccountOperationGate(),
			TimeSpan.FromSeconds(5),
			accountBindingStore: bindingStore,
			bindingCommitGate: new ClaudeBindingCommitGate());
	}

	private static string GetStandardOutput(
		ProcessStartInfo startInfo,
		string subscriptionType = "max")
	{
		return startInfo.ArgumentList[0] switch
		{
			"--version" => SupportedVersion,
			"auth" => AuthStatusJson.Replace(
				"\"subscriptionType\":\"max\"",
				$"\"subscriptionType\":\"{subscriptionType}\"",
				StringComparison.Ordinal),
			_ => CreateResultJson(UsageText)
		};
	}
}
