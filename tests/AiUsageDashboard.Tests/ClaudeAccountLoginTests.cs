using System.Collections.Concurrent;
using System.Diagnostics;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeAccountLoginTests
{
	private sealed class ControlledDeadlineTimeProvider : TimeProvider
	{
		private readonly ConcurrentDictionary<TimeSpan, ITimer> _latestTimers = new();

		public override ITimer CreateTimer(
			TimerCallback callback,
			object? state,
			TimeSpan dueTime,
			TimeSpan period)
		{
			Assert.Equal(Timeout.InfiniteTimeSpan, period);
			ITimer timer = TimeProvider.System.CreateTimer(
				callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			_latestTimers[dueTime] = timer;
			return timer;
		}

		internal void ExpireDeadline(TimeSpan timeout)
		{
			Assert.True(_latestTimers.TryGetValue(timeout, out ITimer? timer));
			Assert.NotNull(timer);
			Assert.True(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
		}
	}

	private const string AuthStatusJson =
		"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
		"\"apiProvider\":\"firstParty\",\"email\":\"claude@example.com\"," +
		"\"orgId\":\"org-123\",\"orgName\":\"Example Organization\"," +
		"\"subscriptionType\":\"max\"}";
	private const string SupportedVersion = "2.1.220 (Claude Code)";
	private static readonly TimeSpan AsyncWatchdogTimeout =
		TimeSpan.FromSeconds(15);

	[Fact]
	public async Task LoginAsync_WhenLoginCompletes_UsesIsolatedOfficialFlowAndReturnsIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string configDirectory = Path.Combine(temporaryDirectory.Path, "claude-config");
		List<ProcessStartInfo> startInfos = new();
		ClaudeAccountLogin login = CreateLogin(
			configDirectory,
			executablePath,
			(startInfo, _) =>
			{
				startInfos.Add(startInfo);
				bool isLogin = startInfo.ArgumentList.SequenceEqual(
					new[] { "auth", "login", "--claudeai" });
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					isLogin
						? "https://claude.ai/oauth/authorize?secret=must-not-escape"
						: GetStandardOutput(startInfo, AuthStatusJson),
					"oauth-refresh-token=must-not-escape"));
			});

		ClaudeAccountLoginResult result = await login.LoginAsync(Guid.NewGuid());

		Assert.Equal("claude@example.com", result.AccountIdentity);
		ClaudeSubscriptionContext subscriptionContext =
			Assert.IsType<ClaudeSubscriptionContext>(result.SubscriptionContext);
		Assert.Equal("org-123", subscriptionContext.SubscriptionScopeIdentity);
		Assert.Equal(
			"Example Organization",
			subscriptionContext.SubscriptionScopeDisplayName);
		CliVersionEvidence versionEvidence =
			Assert.IsType<CliVersionEvidence>(result.VersionEvidence);
		Assert.Equal("2.1.220", versionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnverifiedAllowed,
			versionEvidence.Disposition);
		Assert.True(Directory.Exists(configDirectory));
		Assert.Equal(3, startInfos.Count);
		Assert.Equal(
			new[] { "auth", "login", "--claudeai" },
			startInfos[0].ArgumentList);
		Assert.Equal(new[] { "--version" }, startInfos[1].ArgumentList);
		Assert.Equal(
			new[] { "auth", "status", "--json" },
			startInfos[2].ArgumentList);

		foreach (ProcessStartInfo startInfo in startInfos)
		{
			Assert.Equal(executablePath, startInfo.FileName);
			Assert.Equal(configDirectory, startInfo.WorkingDirectory);
			Assert.Equal(
				configDirectory,
				startInfo.Environment["CLAUDE_CONFIG_DIR"]);
			Assert.Equal("1", startInfo.Environment["NO_COLOR"]);
			Assert.False(startInfo.UseShellExecute);
		}

		Assert.False(startInfos[0].CreateNoWindow);
		Assert.False(startInfos[0].RedirectStandardInput);
		Assert.False(startInfos[0].RedirectStandardOutput);
		Assert.False(startInfos[0].RedirectStandardError);

		foreach (ProcessStartInfo startInfo in startInfos.Skip(1))
		{
			Assert.True(startInfo.CreateNoWindow);
			Assert.True(startInfo.RedirectStandardInput);
			Assert.True(startInfo.RedirectStandardOutput);
			Assert.True(startInfo.RedirectStandardError);
		}
	}

	[Fact]
	public async Task LoginAsync_WhenVersionIsUnsupported_FailsClosedBeforeAuthStatus()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		List<ProcessStartInfo> startInfos = new();
		ClaudeAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "claude-config"),
			executablePath,
			(startInfo, _) =>
			{
				startInfos.Add(startInfo);
				string standardOutput = startInfo.ArgumentList.SequenceEqual(
					new[] { "--version" })
					? "2.1.168 (Claude Code)"
					: string.Empty;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					standardOutput,
					string.Empty));
			});

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.Contains("版本不支援", exception.Message, StringComparison.Ordinal);
		Assert.Equal(2, startInfos.Count);
		Assert.Equal(
			new[] { "auth", "login", "--claudeai" },
			startInfos[0].ArgumentList);
		Assert.Equal(new[] { "--version" }, startInfos[1].ArgumentList);
		Assert.DoesNotContain(
			startInfos,
			startInfo => startInfo.ArgumentList.SequenceEqual(
				new[] { "auth", "status", "--json" }));
	}

	[Fact]
	public async Task LoginAsync_WhenCliIsUntrusted_ExplainsOfficialRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		int processRunnerCallCount = 0;
		ClaudeAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "claude-config"),
			() => throw new ClaudeCliUntrustedException(
				"Untrusted resolver detail."),
			(_, _) =>
			{
				processRunnerCallCount++;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					string.Empty,
					string.Empty));
			},
			new ClaudeAccountOperationGate());

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.Contains("Anthropic 官方來源", exception.Message);
		Assert.DoesNotContain("獨立設定目錄", exception.Message);
		Assert.Equal(0, processRunnerCallCount);
	}

	[Fact]
	public void CreateStartInfo_SeparatesInteractiveLoginFromCapturedStatusVerification()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(temporaryDirectory.Path, "claude.exe");
		string configDirectory = Path.Combine(temporaryDirectory.Path, "claude-config");

		ProcessStartInfo loginStartInfo = ClaudeAccountLogin.CreateLoginStartInfo(
			executablePath,
			configDirectory);
		ProcessStartInfo statusStartInfo = ClaudeAccountLogin.CreateAuthStatusStartInfo(
			executablePath,
			configDirectory);

		Assert.Equal(
			new[] { "auth", "login", "--claudeai" },
			loginStartInfo.ArgumentList);
		Assert.Equal(executablePath, loginStartInfo.FileName);
		Assert.False(loginStartInfo.UseShellExecute);
		Assert.False(loginStartInfo.CreateNoWindow);
		Assert.False(loginStartInfo.RedirectStandardInput);
		Assert.False(loginStartInfo.RedirectStandardOutput);
		Assert.False(loginStartInfo.RedirectStandardError);

		Assert.Equal(
			new[] { "auth", "status", "--json" },
			statusStartInfo.ArgumentList);
		Assert.Equal(executablePath, statusStartInfo.FileName);
		Assert.False(statusStartInfo.UseShellExecute);
		Assert.True(statusStartInfo.CreateNoWindow);
		Assert.True(statusStartInfo.RedirectStandardInput);
		Assert.True(statusStartInfo.RedirectStandardOutput);
		Assert.True(statusStartInfo.RedirectStandardError);

		Assert.Equal(
			configDirectory,
			loginStartInfo.Environment["CLAUDE_CONFIG_DIR"]);
		Assert.Equal(
			configDirectory,
			statusStartInfo.Environment["CLAUDE_CONFIG_DIR"]);
	}

	[Fact]
	public async Task LoginAsync_WhenLoginFails_DoesNotExposeProcessOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		const string authorizationUrl =
			"https://claude.ai/oauth/authorize?code=private-login-code";
		const string refreshToken = "private-refresh-token";
		ClaudeAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "claude-config"),
			executablePath,
			(_, _) => Task.FromResult(new ClaudeAccountLogin.ProcessResult(
				1,
				authorizationUrl,
				refreshToken)));

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.DoesNotContain(authorizationUrl, exception.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain(refreshToken, exception.ToString(), StringComparison.Ordinal);
		Assert.Null(exception.InnerException);
	}

	[Fact]
	public async Task LoginAsync_WhenTrackedCleanupOutlivesFailure_HoldsExecutableLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string configDirectory = Path.Combine(
			temporaryDirectory.Path,
			"claude-config");
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
			ClaudeAccountLogin login = new(
				_ => configDirectory,
				() => executablePath,
				(_, _) => throw new InvalidOperationException(
					"untracked runner must not run"),
				new ClaudeAccountOperationGate(),
				TimeSpan.FromSeconds(5),
				TimeSpan.FromSeconds(5),
				(_, _, _) => Task.CompletedTask,
				protectedExecutableResolver: () => rawExecutableLease,
				trackedProcessRunner: (_, tracker, _) =>
				{
					tracker.Track(cleanupCompletion.Task);
					return Task.FromException<ClaudeAccountLogin.ProcessResult>(
						new IOException("process cleanup is still running"));
				});

			await Assert.ThrowsAsync<ClaudeAccountLoginException>(
				() => login.LoginAsync(Guid.NewGuid()));

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
	public async Task LoginAsync_WhenProcessContainmentIsCompromised_RequiresRestartForLaterAttempts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string configDirectory = Path.Combine(
			temporaryDirectory.Path,
			"claude-config");
		ClaudeAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int runnerCallCount = 0;
		ClaudeAccountLogin login = new(
			_ => configDirectory,
			() => executablePath,
			(_, _) => throw new InvalidOperationException(
				"untracked runner must not run"),
			operationGate,
			TimeSpan.FromSeconds(5),
			TimeSpan.FromSeconds(5),
			(_, _, _) => Task.CompletedTask,
			trackedProcessRunner: (_, tracker, _) =>
			{
				Interlocked.Increment(ref runnerCallCount);
				tracker.MarkContainmentCompromised();
				return Task.FromException<ClaudeAccountLogin.ProcessResult>(
					new IOException("process containment failed"));
			});

		ClaudeAccountLoginException firstException =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(accountId));
		ClaudeAccountLoginException secondException =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(accountId));

		Assert.Contains("重新啟動", firstException.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("再試一次", firstException.Message, StringComparison.Ordinal);
		Assert.Contains("重新啟動", secondException.Message, StringComparison.Ordinal);
		Assert.IsType<ClaudeCliContainmentException>(secondException.InnerException);
		Assert.Equal(1, Volatile.Read(ref runnerCallCount));
	}

	[Fact]
	public async Task OperationGate_WhenAccountContainmentIsCompromised_RemainsScopedAfterPurge()
	{
		ClaudeAccountOperationGate operationGate = new();
		Guid compromisedAccountId = Guid.NewGuid();
		Guid healthyAccountId = Guid.NewGuid();
		operationGate.MarkContainmentCompromised(compromisedAccountId);

		operationGate.RequestPurge(compromisedAccountId);

		await Assert.ThrowsAsync<ClaudeCliContainmentException>(() =>
			operationGate.EnterAsync(
				compromisedAccountId,
				CancellationToken.None).AsTask());
		using IDisposable healthyLease = await operationGate.EnterAsync(
			healthyAccountId,
			CancellationToken.None);
	}

	[Theory]
	[InlineData("not-json")]
	[InlineData("{\"loggedIn\":false}")]
	public async Task LoginAsync_WhenStatusCannotBeStrictlyValidated_UsesSanitizedError(
		string statusOutput)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		int callCount = 0;
		ClaudeAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "claude-config"),
			executablePath,
			(startInfo, _) =>
			{
				callCount++;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					GetStandardOutput(startInfo, statusOutput),
					"private-error"));
			});

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.DoesNotContain(statusOutput, exception.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("private-error", exception.ToString(), StringComparison.Ordinal);
		Assert.Null(exception.InnerException);
		Assert.Equal(3, callCount);
	}

	[Fact]
	public async Task LoginAsync_WhenStatusHasNoEmail_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		const string statusWithoutEmail =
			"{\"loggedIn\":true,\"authMethod\":\"claude.ai\"," +
			"\"apiProvider\":\"firstParty\",\"orgId\":\"org-123\"," +
			"\"orgName\":\"Example Organization\",\"subscriptionType\":\"pro\"}";
		int callCount = 0;
		ClaudeAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "claude-config"),
			executablePath,
			(startInfo, _) =>
			{
				callCount++;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					GetStandardOutput(startInfo, statusWithoutEmail),
					string.Empty));
			});

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.Contains("無法確認目前登入的帳號", exception.Message, StringComparison.Ordinal);
		Assert.Equal(3, callCount);
	}

	[Fact]
	public async Task LoginAsync_WhenCallerCancels_PropagatesCancellation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		ClaudeAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "claude-config"),
			executablePath,
			async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return new ClaudeAccountLogin.ProcessResult(0, string.Empty, string.Empty);
			});
		using CancellationTokenSource cancellationSource = new(
			TimeSpan.FromMilliseconds(100));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await login
				.LoginAsync(Guid.NewGuid(), cancellationSource.Token)
				.WaitAsync(AsyncWatchdogTimeout));
	}

	[Fact]
	public async Task LoginAsync_WhenLoginTimesOut_UsesSanitizedTimeout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		ClaudeAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "claude-config"),
			() => executablePath,
			async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return new ClaudeAccountLogin.ProcessResult(0, string.Empty, string.Empty);
			},
			new ClaudeAccountOperationGate(),
			TimeSpan.FromMilliseconds(50),
			TimeSpan.FromSeconds(1));

		ClaudeAccountLoginException exception =
			await Assert.ThrowsAsync<ClaudeAccountLoginException>(() =>
				login.LoginAsync(Guid.NewGuid()));

		Assert.Contains("登入逾時", exception.Message, StringComparison.Ordinal);
		Assert.Null(exception.InnerException);
	}

	[Fact]
	public async Task LoginAsync_WhenProcessRunnerBlocksSynchronously_StillHonorsTimeout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim releaseRunner = new();
		using CancellationTokenSource cleanupSource = new();
		TaskCompletionSource runnerEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource runnerExited = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TimeSpan loginTimeout = TimeSpan.FromMilliseconds(250);
		TimeSpan statusTimeout = TimeSpan.FromSeconds(1);
		ControlledDeadlineTimeProvider timeProvider = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		ClaudeAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int runnerCallCount = 0;
		ClaudeAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "claude-config"),
			() => executablePath,
			(_, _) =>
			{
				Interlocked.Increment(ref runnerCallCount);
				try
				{
					runnerEntered.SetResult();
					releaseRunner.Wait();
					return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
						0,
						string.Empty,
						string.Empty));
				}
				finally
				{
					runnerExited.TrySetResult();
				}
			},
			operationGate,
			loginTimeout,
			statusTimeout,
			(_, _, _) => Task.CompletedTask,
			timeProvider: timeProvider);
		Task<Task<ClaudeAccountLoginResult>> invocation = Task.Factory.StartNew(
			() => login.LoginAsync(accountId, cleanupSource.Token),
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
		Task<ClaudeAccountLoginResult>? blockedLoginTask = null;

		try
		{
			Task<ClaudeAccountLoginResult> loginTask =
				await invocation.WaitAsync(AsyncWatchdogTimeout);
			await runnerEntered.Task.WaitAsync(AsyncWatchdogTimeout);
			Assert.False(loginTask.IsCompleted);
			// 先確認同步 runner 已進入，再觸發 deadline，避免排程延遲取消尚未啟動的工作。
			timeProvider.ExpireDeadline(loginTimeout);
			ClaudeAccountLoginException exception =
				await Assert.ThrowsAsync<ClaudeAccountLoginException>(() => loginTask)
					.WaitAsync(AsyncWatchdogTimeout);
			Assert.Contains("登入逾時", exception.Message, StringComparison.Ordinal);
			Assert.Null(exception.InnerException);
			Assert.False(runnerExited.Task.IsCompleted);

			blockedLoginTask = login.LoginAsync(accountId, cleanupSource.Token);
			Assert.False(blockedLoginTask.IsCompleted);
			timeProvider.ExpireDeadline(statusTimeout);
			ClaudeAccountLoginException blockedException =
				await Assert.ThrowsAsync<ClaudeAccountLoginException>(() => blockedLoginTask)
					.WaitAsync(AsyncWatchdogTimeout);
			Assert.Contains(
				"等待前一個",
				blockedException.Message,
				StringComparison.Ordinal);
			Assert.Equal(1, Volatile.Read(ref runnerCallCount));
		}
		finally
		{
			releaseRunner.Set();
			cleanupSource.Cancel();
			Task<ClaudeAccountLoginResult> loginTask =
				await invocation.WaitAsync(AsyncWatchdogTimeout);
			// 保留原斷言失敗，同時觀察釋放後的工作；watchdog 逾時仍須回報。
			await Record.ExceptionAsync(() => loginTask).WaitAsync(AsyncWatchdogTimeout);
			if (blockedLoginTask is not null)
			{
				await Record.ExceptionAsync(() => blockedLoginTask)
					.WaitAsync(AsyncWatchdogTimeout);
			}

			using CancellationTokenSource gateReleaseTimeout = new(AsyncWatchdogTimeout);
			using IDisposable releasedLease = await operationGate.EnterAsync(
				accountId,
				gateReleaseTimeout.Token);
			if (runnerEntered.Task.IsCompleted)
			{
				await runnerExited.Task.WaitAsync(AsyncWatchdogTimeout);
			}
		}
	}

	[Fact]
	public async Task LoginAsync_WhenAccountOperationIsActive_WaitsBeforeStartingProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		ClaudeAccountOperationGate gate = new();
		Guid accountId = Guid.NewGuid();
		int callCount = 0;
		ClaudeAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "claude-config"),
			() => executablePath,
			(startInfo, _) =>
			{
				callCount++;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					GetStandardOutput(startInfo, AuthStatusJson),
					string.Empty));
			},
			gate);
		IDisposable activeOperation = await gate.EnterAsync(
			accountId,
			CancellationToken.None);
		Task<ClaudeAccountLoginResult> loginTask = login.LoginAsync(accountId);

		Assert.False(loginTask.IsCompleted);
		Assert.Equal(0, Volatile.Read(ref callCount));

		activeOperation.Dispose();
		ClaudeAccountLoginResult result = await loginTask.WaitAsync(
			AsyncWatchdogTimeout);
		Assert.Equal("claude@example.com", result.AccountIdentity);
		Assert.Equal(3, callCount);
	}

	[Fact]
	public async Task LoginAsync_WhenVerified_LinearizesCompletionBeforeReleasingAccountGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		ClaudeAccountOperationGate gate = new();
		Guid accountId = Guid.NewGuid();
		TaskCompletionSource<bool> completionEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int callCount = 0;
		string? completedIdentity = null;
		ClaudeAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "claude-config"),
			() => executablePath,
			(startInfo, _) =>
			{
				callCount++;
				return Task.FromResult(new ClaudeAccountLogin.ProcessResult(
					0,
					GetStandardOutput(startInfo, AuthStatusJson),
					string.Empty));
			},
			gate,
			async (completedAccountId, accountIdentity, _) =>
			{
				Assert.Equal(accountId, completedAccountId);
				completedIdentity = accountIdentity;
				completionEntered.TrySetResult(true);
				await releaseCompletion.Task;
			});
		Task<ClaudeAccountLoginResult> loginTask = login.LoginAsync(accountId);
		Task<IDisposable>? competingOperation = null;
		ClaudeAccountLoginResult result = null!;
		bool completionWasEntered = false;
		try
		{
			await completionEntered.Task.WaitAsync(AsyncWatchdogTimeout);
			completionWasEntered = true;
			competingOperation = gate.EnterAsync(
				accountId,
				CancellationToken.None).AsTask();

			Assert.False(competingOperation.IsCompleted);
			Assert.False(loginTask.IsCompleted);
		}
		finally
		{
			releaseCompletion.TrySetResult(true);

			if (completionWasEntered)
			{
				result = await loginTask.WaitAsync(AsyncWatchdogTimeout);

				if (competingOperation is not null)
				{
					using IDisposable operation =
						await competingOperation.WaitAsync(
							AsyncWatchdogTimeout);
				}
			}
		}

		Assert.Equal("claude@example.com", completedIdentity);
		Assert.Equal("claude@example.com", result.AccountIdentity);
	}

	[Fact]
	public void Apply_WhenSensitiveVariablesExist_RemovesThemAndPinsAccountDirectory()
	{
		string configDirectory = Path.GetFullPath("claude-config");
		ProcessStartInfo startInfo = new();
		string[] sensitiveVariables =
		{
			"ANTHROPIC_API_KEY",
			"ANTHROPIC_AUTH_TOKEN",
			"CLAUDE_CODE_OAUTH_TOKEN",
			"CLAUDE_CODE_OAUTH_REFRESH_TOKEN",
			"CLAUDE_CODE_OAUTH_SCOPES",
			"OAUTH_REFRESH_TOKEN",
			"OAUTH_SCOPES",
			"NODE_OPTIONS"
		};

		foreach (string variableName in sensitiveVariables)
		{
			startInfo.Environment[variableName] = "private-value";
		}

		ClaudeProcessEnvironment.Apply(startInfo, configDirectory);

		foreach (string variableName in sensitiveVariables)
		{
			Assert.False(startInfo.Environment.ContainsKey(variableName));
		}

		Assert.Equal(configDirectory, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
		Assert.Equal("1", startInfo.Environment["NO_COLOR"]);
	}

	[Fact]
	public async Task EnterAsync_WithSameAccount_SerializesOperations()
	{
		ClaudeAccountOperationGate gate = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		IDisposable firstLease = await gate.EnterAsync(
			firstAccountId,
			CancellationToken.None);
		Task<IDisposable> blockedLeaseTask = gate.EnterAsync(
			firstAccountId,
			CancellationToken.None).AsTask();

		Assert.False(blockedLeaseTask.IsCompleted);
		using IDisposable otherAccountLease = await gate.EnterAsync(
			secondAccountId,
			CancellationToken.None);

		firstLease.Dispose();
		using IDisposable blockedLease = await blockedLeaseTask.WaitAsync(
			AsyncWatchdogTimeout);
	}

	private static ClaudeAccountLogin CreateLogin(
		string configDirectory,
		string executablePath,
		Func<ProcessStartInfo, CancellationToken, Task<ClaudeAccountLogin.ProcessResult>>
			processRunner)
	{
		return new ClaudeAccountLogin(
			_ => configDirectory,
			() => executablePath,
			processRunner,
			new ClaudeAccountOperationGate());
	}

	private static string GetStandardOutput(
		ProcessStartInfo startInfo,
		string authStatusJson)
	{
		if (startInfo.ArgumentList.SequenceEqual(new[] { "--version" }))
		{
			return SupportedVersion;
		}

		return startInfo.ArgumentList.SequenceEqual(
			new[] { "auth", "status", "--json" })
			? authStatusJson
			: string.Empty;
	}

	private static string CreateExecutable(string directory)
	{
		string executablePath = Path.Combine(directory, "claude.exe");
		File.WriteAllBytes(executablePath, Array.Empty<byte>());
		return executablePath;
	}
}
