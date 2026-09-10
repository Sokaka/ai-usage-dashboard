using System.Diagnostics;
using System.IO;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokAccountLoginTests
{
	private sealed class StubExecutableValidator : IGrokCliExecutableValidator
	{
		private readonly WindowsOfficialCliExecutableLease? _executableLease;
		private readonly Func<string> _handler;

		internal int ResolveCount { get; private set; }

		internal StubExecutableValidator(
			Func<string> handler,
			WindowsOfficialCliExecutableLease? executableLease = null)
		{
			_handler = handler;
			_executableLease = executableLease;
		}

		public Task<GrokValidatedExecutable> ResolveAndValidateAsync(
			CancellationToken cancellationToken = default,
			ProviderProcessOperationTracker? operationTracker = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ResolveCount++;
			return Task.FromResult(new GrokValidatedExecutable(
				_handler(),
				CliVersionPolicies.AssessGrok(
					new GrokExecutableVersion(1, 0, 3)),
				_executableLease));
		}

		public Task<GrokValidatedExecutable> ValidateAsync(
			string executablePath,
			CancellationToken cancellationToken = default,
			ProviderProcessOperationTracker? operationTracker = null)
		{
			throw new InvalidOperationException("Direct validation is not used.");
		}
	}

	[Fact]
	public async Task LoginAsync_UsesValidatedExecutablePrivateHomeAndInteractiveOAuthCommand()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		string executablePath = Path.Combine(testRoot, "bin", "grok.exe");
		string homeDirectory = Path.Combine(testRoot, "grok", "account", "home");
		string workingDirectory = Path.Combine(
			testRoot,
			"grok",
			"account",
			"blank-workspace");
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		StubExecutableValidator validator = new(
			() => executablePath,
			executableLease);
		ProcessStartInfo? observedStartInfo = null;
		(string Home, string Working)? preparedDirectories = null;
		GrokAccountLogin login = new(
			validator,
			new GrokAccountOperationGate(),
			new GrokProcessContainmentState(),
			id => id == accountId ? homeDirectory : throw new InvalidOperationException(),
			id => id == accountId ? workingDirectory : throw new InvalidOperationException(),
			(home, working) => preparedDirectories = (home, working),
			(startInfo, _, _) =>
			{
				using WindowsOfficialCliExecutableLease activeLease =
					executableLease.Duplicate();
				observedStartInfo = startInfo;
				return Task.FromResult(0);
			},
			TimeSpan.FromMinutes(1));

		await login.LoginAsync(accountId);

		Assert.Equal(1, validator.ResolveCount);
		Assert.NotNull(preparedDirectories);
		Assert.Equal(homeDirectory, preparedDirectories.Value.Home);
		Assert.Equal(workingDirectory, preparedDirectories.Value.Working);
		Assert.NotNull(observedStartInfo);
		Assert.Equal(executablePath, observedStartInfo.FileName);
		Assert.Equal(workingDirectory, observedStartInfo.WorkingDirectory);
		Assert.False(observedStartInfo.UseShellExecute);
		Assert.False(observedStartInfo.CreateNoWindow);
		Assert.False(observedStartInfo.RedirectStandardOutput);
		Assert.False(observedStartInfo.RedirectStandardError);
		Assert.False(observedStartInfo.RedirectStandardInput);
		Assert.Equal(
			new[] { "--no-auto-update", "login", "--oauth" },
			observedStartInfo.ArgumentList);
		Assert.Equal(homeDirectory, observedStartInfo.Environment["GROK_HOME"]);
		Assert.Equal(homeDirectory, observedStartInfo.Environment["HOME"]);
		Assert.Equal(homeDirectory, observedStartInfo.Environment["USERPROFILE"]);
		Assert.Equal(
			Path.Combine(homeDirectory, "tmp"),
			observedStartInfo.Environment["TEMP"]);
		Assert.False(observedStartInfo.Environment.ContainsKey("XAI_API_KEY"));
		Assert.Equal(12, observedStartInfo.Environment.Count);
		Assert.Throws<ObjectDisposedException>(() => executableLease.Duplicate());
	}

	[Fact]
	public async Task LoginAsync_RevalidatesExecutableForEveryAttempt()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => Path.Combine(testRoot, "bin", "grok.exe"));
		GrokAccountLogin login = CreateLogin(
			accountId,
			testRoot,
			validator,
			(_, _) => Task.FromResult(0));

		await login.LoginAsync(accountId);
		await login.LoginAsync(accountId);

		Assert.Equal(2, validator.ResolveCount);
	}

	[Fact]
	public async Task LoginAsync_WhenExecutableIsMissing_PreservesInstallSignal()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => throw new GrokCliNotFoundException("safe typed reason"));
		bool didRunProcess = false;
		GrokAccountLogin login = CreateLogin(
			accountId,
			testRoot,
			validator,
			(_, _) =>
			{
				didRunProcess = true;
				return Task.FromResult(0);
			});

		await Assert.ThrowsAsync<GrokCliNotFoundException>(
			() => login.LoginAsync(accountId));

		Assert.False(didRunProcess);
	}

	[Fact]
	public async Task LoginAsync_WhenProcessExitsNonzero_UsesConservativeFailure()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => Path.Combine(testRoot, "bin", "grok.exe"));
		GrokAccountLogin login = CreateLogin(
			accountId,
			testRoot,
			validator,
			(_, _) => Task.FromResult(7));

		GrokAccountLoginException exception =
			await Assert.ThrowsAsync<GrokAccountLoginException>(
				() => login.LoginAsync(accountId));

		Assert.Contains("未完成", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LoginAsync_WhenDeadlineExpires_ReportsTimeout()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => Path.Combine(testRoot, "bin", "grok.exe"));
		GrokAccountLogin login = CreateLogin(
			accountId,
			testRoot,
			validator,
			async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return 0;
			},
			TimeSpan.FromMilliseconds(20));

		GrokAccountLoginException exception =
			await Assert.ThrowsAsync<GrokAccountLoginException>(
				() => login.LoginAsync(accountId));

		Assert.Contains("逾時", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LoginAsync_WhenRunnerStartBlocks_TimesOutWithoutHoldingCallerAndRetainsGateUntilCleanup()
	{
		TimeSpan operationWatchdogTimeout = TimeSpan.FromSeconds(10);
		TimeSpan fallbackReleaseTimeout = TimeSpan.FromSeconds(30);
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => Path.Combine(testRoot, "bin", "grok.exe"));
		GrokAccountOperationGate operationGate = new();
		using ManualResetEventSlim releaseStart = new(initialState: false);
		TaskCompletionSource runnerEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cleanupObserved = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		GrokAccountLogin login = new(
			validator,
			operationGate,
			new GrokProcessContainmentState(),
			_ => Path.Combine(testRoot, "grok", "account", "home"),
			_ => Path.Combine(testRoot, "grok", "account", "blank-workspace"),
			(_, _) => { },
			(_, _, cancellationToken) =>
			{
				runnerEntered.TrySetResult();
				releaseStart.Wait();

				try
				{
					cancellationToken.ThrowIfCancellationRequested();
					return Task.FromResult(0);
				}
				finally
				{
					cleanupObserved.TrySetResult();
				}
			},
			TimeSpan.FromMilliseconds(100));
		using CancellationTokenSource fallbackRelease = new(fallbackReleaseTimeout);
		using CancellationTokenRegistration fallbackRegistration =
			fallbackRelease.Token.Register(releaseStart.Set);
		Stopwatch invocationStopwatch = Stopwatch.StartNew();

		Task loginTask = login.LoginAsync(accountId);

		invocationStopwatch.Stop();
		Task<GrokAccountLoginException> loginFailureTask =
			Assert.ThrowsAsync<GrokAccountLoginException>(() => loginTask);
		// 例外由主要斷言驗證；cleanup 等待同一工作，並在較早的斷言失敗時觀察其錯誤。
		Task loginCompletion = loginFailureTask.ContinueWith(
			completedTask => _ = completedTask.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		try
		{
			Assert.True(
				invocationStopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
				$"LoginAsync synchronously held its caller for {invocationStopwatch.Elapsed}.");
			await runnerEntered.Task.WaitAsync(operationWatchdogTimeout);
			GrokAccountLoginException exception =
				await loginFailureTask.WaitAsync(operationWatchdogTimeout);
			Assert.Contains("逾時", exception.Message, StringComparison.Ordinal);

			using (CancellationTokenSource blockedGateTimeout =
				new(TimeSpan.FromMilliseconds(100)))
			{
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
					operationGate.EnterAsync(
						accountId,
						blockedGateTimeout.Token).AsTask());
			}

			releaseStart.Set();
			await cleanupObserved.Task.WaitAsync(operationWatchdogTimeout);
			using CancellationTokenSource releasedGateTimeout =
				new(operationWatchdogTimeout);
			using IDisposable releasedLease = await operationGate.EnterAsync(
				accountId,
				releasedGateTimeout.Token);
		}
		finally
		{
			releaseStart.Set();
			Task runnerCleanup = runnerEntered.Task.IsCompletedSuccessfully
				? cleanupObserved.Task
				: Task.CompletedTask;
			await Task.WhenAll(loginCompletion, runnerCleanup)
				.WaitAsync(operationWatchdogTimeout);
		}
	}

	[Fact]
	public async Task LoginAsync_WhenContainmentIsAlreadyCompromised_RejectsBeforeValidationAndLaunch()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		StubExecutableValidator validator = new(
			() => Path.Combine(testRoot, "bin", "grok.exe"));
		GrokProcessContainmentState containmentState = new();
		containmentState.MarkCompromised();
		bool didRunProcess = false;
		GrokAccountLogin login = CreateLogin(
			accountId,
			testRoot,
			validator,
			(_, _) =>
			{
				didRunProcess = true;
				return Task.FromResult(0);
			},
			containmentState: containmentState);

		GrokProcessContainmentException exception =
			await Assert.ThrowsAsync<GrokProcessContainmentException>(
				() => login.LoginAsync(accountId));

		Assert.Contains("Restart AI Usage", exception.Message, StringComparison.Ordinal);
		Assert.Equal(0, validator.ResolveCount);
		Assert.False(didRunProcess);
	}

	[Fact]
	public async Task LoginAsync_WhenLaunchReportsContainmentFailure_PreservesTypedFailureAndPoisonsAcpLaunches()
	{
		Guid accountId = Guid.NewGuid();
		string testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		string executablePath = Path.Combine(testRoot, "bin", "grok.exe");
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		StubExecutableValidator validator = new(
			() => executablePath,
			executableLease);
		GrokProcessContainmentState containmentState = new();
		GrokAccountLogin login = new(
			validator,
			new GrokAccountOperationGate(),
			containmentState,
			_ => Path.Combine(testRoot, "grok", "account", "home"),
			_ => Path.Combine(testRoot, "grok", "account", "blank-workspace"),
			(_, _) => { },
			(_, observedState, _) =>
			{
				Assert.Same(containmentState, observedState);
				observedState.MarkCompromised();
				throw new GrokProcessContainmentException("typed containment failure");
			},
			TimeSpan.FromMinutes(1));

		GrokProcessContainmentException loginException =
			await Assert.ThrowsAsync<GrokProcessContainmentException>(
				() => login.LoginAsync(accountId));

		Assert.Equal("typed containment failure", loginException.Message);
		Assert.Equal(1, containmentState.QuarantinedExecutableLeaseCount);
		using WindowsOfficialCliExecutableLease quarantinedLease =
			executableLease.Duplicate();
		WindowsGrokAcpProcessFactory factory = new(containmentState);
		GrokAcpLaunchOptions invalidOptions = new(
			"C:\\missing-grok.exe",
			"C:\\missing-home",
			"C:\\missing-workspace",
			GrokContainedCommand.AcpStdio);
		await Assert.ThrowsAsync<GrokProcessContainmentException>(() =>
			factory.StartAsync(invalidOptions, CancellationToken.None));
	}

	[Fact]
	public async Task RunProcessAsync_WhenCancelled_KillsAndPositivelyConfirmsNativeRoot()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		string fixturePath = Path.Combine(
			AppContext.BaseDirectory,
			"AiUsageDashboard.TerminalFixture.exe");
		Assert.True(File.Exists(fixturePath));
		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = true,
			FileName = fixturePath,
			RedirectStandardError = false,
			RedirectStandardInput = false,
			RedirectStandardOutput = false,
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory
		};
		startInfo.ArgumentList.Add("silent-hang");
		GrokProcessContainmentState containmentState = new();
		using CancellationTokenSource cancellationSource =
			new(TimeSpan.FromMilliseconds(200));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			GrokAccountLogin.RunProcessAsync(
				startInfo,
				containmentState,
				cancellationSource.Token));

		Assert.False(containmentState.IsCompromised);
	}

	private static GrokAccountLogin CreateLogin(
		Guid accountId,
		string testRoot,
		IGrokCliExecutableValidator validator,
		Func<ProcessStartInfo, CancellationToken, Task<int>> processRunner,
		TimeSpan? timeout = null,
		GrokProcessContainmentState? containmentState = null)
	{
		return new GrokAccountLogin(
			validator,
			new GrokAccountOperationGate(),
			containmentState ?? new GrokProcessContainmentState(),
			id => id == accountId
				? Path.Combine(testRoot, "grok", "account", "home")
				: throw new InvalidOperationException(),
			id => id == accountId
				? Path.Combine(testRoot, "grok", "account", "blank-workspace")
				: throw new InvalidOperationException(),
			(_, _) => { },
			(startInfo, _, cancellationToken) =>
				processRunner(startInfo, cancellationToken),
			timeout ?? TimeSpan.FromMinutes(1));
	}
}
