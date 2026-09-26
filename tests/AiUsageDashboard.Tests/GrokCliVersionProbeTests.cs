using System.Text;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokCliVersionProbeTests
{
	private sealed class ControlledDeadlineTimeProvider : TimeProvider
	{
		private ITimer? _timer;

		public override ITimer CreateTimer(
			TimerCallback callback,
			object? state,
			TimeSpan dueTime,
			TimeSpan period)
		{
			Assert.Null(_timer);
			Assert.Equal(TimeSpan.FromMilliseconds(100), dueTime);
			Assert.Equal(Timeout.InfiniteTimeSpan, period);
			_timer = TimeProvider.System.CreateTimer(
				callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			return _timer;
		}

		internal void ExpireDeadline()
		{
			Assert.NotNull(_timer);
			Assert.True(_timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
		}
	}

	private sealed class FakeProcess : IGrokAcpProcess
	{
		private readonly TaskCompletionSource _disposeCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly GrokProcessContainmentException? _disposeException;
		private readonly Func<CancellationToken, Task<int>> _waitHandler;
		private readonly MemoryStream _standardError;
		private readonly MemoryStream _standardInput = new();
		private readonly MemoryStream _standardOutput;

		internal int DisposeCallCount { get; private set; }

		internal int TerminateCallCount { get; private set; }

		internal bool WasStandardInputClosedAtWait { get; private set; }

		internal Task DisposeCompletion => _disposeCompletion.Task;

		public Stream StandardError => _standardError;

		public Stream StandardInput => _standardInput;

		public Stream StandardOutput => _standardOutput;

		internal FakeProcess(
			byte[] standardOutput,
			byte[]? standardError = null,
			Func<CancellationToken, Task<int>>? waitHandler = null,
			GrokProcessContainmentException? disposeException = null)
		{
			_standardOutput = new MemoryStream(standardOutput);
			_standardError = new MemoryStream(
				standardError ?? Array.Empty<byte>());
			_waitHandler = waitHandler ?? (_ => Task.FromResult(0));
			_disposeException = disposeException;
		}

		public ValueTask DisposeAsync()
		{
			DisposeCallCount++;
			_standardInput.Dispose();
			_standardOutput.Dispose();
			_standardError.Dispose();
			_disposeCompletion.TrySetResult();
			return _disposeException is null
				? ValueTask.CompletedTask
				: ValueTask.FromException(_disposeException);
		}

		public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
		{
			WasStandardInputClosedAtWait = !_standardInput.CanWrite;
			return _waitHandler(cancellationToken);
		}

		public Task TerminateTreeAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TerminateCallCount++;
			return Task.CompletedTask;
		}
	}

	private sealed class FakeProcessFactory : IGrokAcpProcessFactory
	{
		private readonly IGrokAcpProcess _process;

		internal GrokAcpLaunchOptions? ObservedOptions { get; private set; }

		internal FakeProcessFactory(IGrokAcpProcess process)
		{
			_process = process;
		}

		public Task<IGrokAcpProcess> StartAsync(
			GrokAcpLaunchOptions options,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObservedOptions = options;
			return Task.FromResult(_process);
		}
	}

	private sealed class BlockingProcessFactory : IGrokAcpProcessFactory
	{
		private readonly IGrokAcpProcess _process;
		private readonly ManualResetEventSlim _releaseStart;

		internal TaskCompletionSource StartEntered { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal BlockingProcessFactory(
			IGrokAcpProcess process,
			ManualResetEventSlim releaseStart)
		{
			_process = process;
			_releaseStart = releaseStart;
		}

		public Task<IGrokAcpProcess> StartAsync(
			GrokAcpLaunchOptions options,
			CancellationToken cancellationToken)
		{
			StartEntered.TrySetResult();
			_releaseStart.Wait();
			return Task.FromResult(_process);
		}
	}

	[Fact]
	public async Task ReadVersionAsync_WithOfficialOutput_UsesIsolatedContainedCommand()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"bin",
			"grok.exe");
		string scratchRoot = Path.Combine(
			temporaryDirectory.Path,
			"version-probe");
		FakeProcess process = new(
			Encoding.UTF8.GetBytes("grok 1.0.41 (4220f3b224a6) [stable]\r\n"));
		FakeProcessFactory processFactory = new(process);
		(string Home, string Working)? prepared = null;
		string? cleaned = null;
		GrokCliVersionProbe probe = CreateProbe(
			processFactory,
			scratchRoot,
			(home, working) => prepared = (home, working),
			path => cleaned = path);

		GrokExecutableVersion? version = await probe.ReadVersionAsync(
			executablePath);

		Assert.Equal(new GrokExecutableVersion(1, 0, 41), version);
		Assert.NotNull(processFactory.ObservedOptions);
		Assert.Equal(
			Path.GetFullPath(executablePath),
			processFactory.ObservedOptions.ExecutablePath);
		Assert.Equal(
			GrokContainedCommand.Version,
			processFactory.ObservedOptions.Command);
		Assert.NotNull(prepared);
		Assert.Equal(prepared.Value.Home, processFactory.ObservedOptions.HomeDirectory);
		Assert.Equal(
			prepared.Value.Working,
			processFactory.ObservedOptions.WorkingDirectory);
		Assert.Equal(
			Path.GetDirectoryName(prepared.Value.Home),
			cleaned);
		Assert.True(process.WasStandardInputClosedAtWait);
		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task ReadVersionAsync_WhenOutputExceedsBound_ReturnsUnknownVersion()
	{
		using TemporaryDirectory temporaryDirectory = new();
		FakeProcess process = new(new byte[1025]);
		GrokCliVersionProbe probe = CreateProbe(
			new FakeProcessFactory(process),
			Path.Combine(temporaryDirectory.Path, "version-probe"));

		GrokExecutableVersion? version = await probe.ReadVersionAsync(
			Path.Combine(temporaryDirectory.Path, "grok.exe"));

		Assert.Null(version);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task ReadVersionAsync_WhenOutputExceedsBoundAndDisposeCannotConfirmContainment_PropagatesContainmentFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		GrokProcessContainmentException expectedException = new(
			"Unable to confirm Grok process containment.",
			new IOException("Simulated containment failure."));
		FakeProcess process = new(
			new byte[1025],
			disposeException: expectedException);
		GrokCliVersionProbe probe = CreateProbe(
			new FakeProcessFactory(process),
			Path.Combine(temporaryDirectory.Path, "version-probe"));

		GrokProcessContainmentException actualException =
			await Assert.ThrowsAsync<GrokProcessContainmentException>(
				() => probe.ReadVersionAsync(
					Path.Combine(temporaryDirectory.Path, "grok.exe")));

		Assert.Same(expectedException, actualException);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task ReadVersionAsync_WhenDeadlineExpires_ReportsBoundedFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		FakeProcess process = new(
			Array.Empty<byte>(),
			waitHandler: async cancellationToken =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return 0;
			});
		GrokCliVersionProbe probe = CreateProbe(
			new FakeProcessFactory(process),
			Path.Combine(temporaryDirectory.Path, "version-probe"),
			probeTimeout: TimeSpan.FromMilliseconds(20));

		GrokCliVersionProbeException exception =
			await Assert.ThrowsAsync<GrokCliVersionProbeException>(
				() => probe.ReadVersionAsync(
					Path.Combine(temporaryDirectory.Path, "grok.exe")));

		Assert.Contains("deadline", exception.Message, StringComparison.Ordinal);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task ReadVersionAsync_WhenProcessStartBlocks_TimesOutWithoutHoldingCallerAndDefersScratchCleanup()
	{
		TimeSpan testTimeout = TimeSpan.FromSeconds(5);
		using TemporaryDirectory temporaryDirectory = new();
		FakeProcess process = new(
			Encoding.UTF8.GetBytes("grok 1.0.4 (d846eb93d9)\r\n"));
		using ManualResetEventSlim releaseStart = new(initialState: false);
		BlockingProcessFactory processFactory = new(process, releaseStart);
		TaskCompletionSource scratchCleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int disposeCountAtScratchCleanup = -1;
		ControlledDeadlineTimeProvider timeProvider = new();
		GrokCliVersionProbe probe = CreateProbe(
			processFactory,
			Path.Combine(temporaryDirectory.Path, "version-probe"),
			cleanupScratchDirectory: _ =>
			{
				disposeCountAtScratchCleanup = process.DisposeCallCount;
				scratchCleanupCompletion.TrySetResult();
			},
			probeTimeout: TimeSpan.FromMilliseconds(100),
			timeProvider: timeProvider);
		Task<Task<GrokExecutableVersion?>> invocation = Task.Factory.StartNew(
			() => probe.ReadVersionAsync(
				Path.Combine(temporaryDirectory.Path, "grok.exe")),
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);

		try
		{
			Task<GrokExecutableVersion?> versionTask = await invocation.WaitAsync(testTimeout);
			await processFactory.StartEntered.Task.WaitAsync(testTimeout);
			Assert.False(versionTask.IsCompleted);
			// 先確認 StartAsync 已阻塞，再觸發 deadline，避免排程延遲先取消尚未啟動的工作。
			timeProvider.ExpireDeadline();
			GrokCliVersionProbeException exception =
				await Assert.ThrowsAsync<GrokCliVersionProbeException>(() => versionTask)
					.WaitAsync(testTimeout);
			Assert.Contains("deadline", exception.Message, StringComparison.Ordinal);
			Assert.False(scratchCleanupCompletion.Task.IsCompleted);
		}
		finally
		{
			releaseStart.Set();
			Task<GrokExecutableVersion?> versionTask = await invocation.WaitAsync(testTimeout);
			// 保留原斷言失敗，同時觀察釋放後的工作，避免它離開測試範圍。
			await Record.ExceptionAsync(() => versionTask);
			await process.DisposeCompletion.WaitAsync(testTimeout);
			await scratchCleanupCompletion.Task.WaitAsync(testTimeout);
		}

		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(1, process.DisposeCallCount);
		Assert.Equal(1, disposeCountAtScratchCleanup);
	}

	[Theory]
	[InlineData("grok 1.0.3 (abcdef0)", 1, 0, 3)]
	[InlineData("grok 12.34.56 (0123456789abcdef)\n", 12, 34, 56)]
	[InlineData("grok 1.0.41 (4220f3b224a6) [stable]", 1, 0, 41)]
	[InlineData("grok 1.0.41 (4220f3b224a6) [stable]\r\n", 1, 0, 41)]
	public void ParseVersionOutput_WithExactOfficialShape_ReturnsVersion(
		string output,
		int major,
		int minor,
		int patch)
	{
		GrokExecutableVersion? version = GrokCliVersionProbe.ParseVersionOutput(
			Encoding.UTF8.GetBytes(output));

		Assert.Equal(new GrokExecutableVersion(major, minor, patch), version);
	}

	[Theory]
	[InlineData("")]
	[InlineData("grok 1.0.4")]
	[InlineData("grok 1.0.4 (D846EB9)")]
	[InlineData("grok 01.0.4 (d846eb9)")]
	[InlineData("grok 1.0.4 (d846eb9)\nextra")]
	[InlineData("grok 1.0.4 (d846eb9)\n\n")]
	[InlineData("prefix grok 1.0.4 (d846eb9)")]
	[InlineData("grok 999999999999999999999.0.4 (d846eb9)")]
	[InlineData("grok 1.0.41 (4220f3b224a6) [beta]")]
	[InlineData("grok 1.0.41 (4220f3b224a6) [STABLE]")]
	[InlineData("grok 1.0.41 (4220f3b224a6) [stable]extra")]
	[InlineData("grok 1.0.41 (4220f3b224a6) [stable] [stable]")]
	[InlineData("grok 1.0.41 (4220f3b224a6)  [stable]")]
	[InlineData("grok 1.0.41 (4220f3b224a6) [stable]\nextra")]
	[InlineData("grok 1.0.41 (D220f3b224a6) [stable]")]
	public void ParseVersionOutput_WithUnexpectedShape_ReturnsNull(string output)
	{
		Assert.Null(GrokCliVersionProbe.ParseVersionOutput(
			Encoding.UTF8.GetBytes(output)));
	}

	[Fact]
	public void BuildCommandLine_ForVersion_UsesOnlyClosedVersionArguments()
	{
		string executablePath = "C:\\Program Files\\Grok\\grok.exe";

		string commandLine = WindowsGrokAcpProcessFactory.BuildCommandLine(
			executablePath,
			GrokContainedCommand.Version).ToString();

		Assert.Equal(
			"\"C:\\Program Files\\Grok\\grok.exe\" --no-auto-update --version",
			commandLine);
	}

	private static GrokCliVersionProbe CreateProbe(
		IGrokAcpProcessFactory processFactory,
		string scratchRoot,
		Action<string, string>? prepareDirectories = null,
		Action<string>? cleanupScratchDirectory = null,
		TimeSpan? probeTimeout = null,
		TimeProvider? timeProvider = null)
	{
		return new GrokCliVersionProbe(
			processFactory,
			() => scratchRoot,
			prepareDirectories ?? ((_, _) => { }),
			cleanupScratchDirectory ?? (_ => { }),
			probeTimeout ?? TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1),
			timeProvider);
	}
}
