using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityOfficialPrintUsageClientTests
{
	private sealed class ThrowingCaptureStream : Stream
	{
		internal Memory<byte> LastReadBuffer { get; private set; }

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			LastReadBuffer = buffer;
			buffer.Span.Fill(0xA5);
			throw new IOException("Synthetic read failure.");
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) =>
			throw new NotSupportedException();

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();
	}

	private sealed class FakeCapabilityValidator
	{
		private readonly bool _isSupported;
		private readonly string _cliVersion;

		internal int CallCount { get; private set; }

		internal FakeCapabilityValidator(
			bool isSupported,
			string cliVersion = "1.1.12")
		{
			_isSupported = isSupported;
			_cliVersion = cliVersion;
		}

		internal Task<AntigravityOfficialPrintCapabilityValidationResult>
			ValidateAsync(
				string executablePath,
				CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;

			if (!_isSupported)
			{
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: false));
			}

			return Task.FromResult(
				new AntigravityOfficialPrintCapabilityValidationResult(
					isSupported: true,
					cliVersion: _cliVersion,
					executableLease:
						AntigravityExecutableLease.Acquire(executablePath),
					versionAssessment:
						AntigravityOfficialPrintCapabilityValidator
							.AssessVersion(_cliVersion)));
		}
	}

	private sealed class FakeProcessRunner :
		IAntigravityOfficialPrintProcessRunner
	{
		private readonly Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintProcessResult>> _runAsync;
		private readonly Func<string, CancellationToken, Task<bool>>
			_tryRecoverInterruptedAttemptAsync;

		internal int CallCount { get; private set; }

		internal int RecoveryCallCount { get; private set; }

		internal string? LastExecutablePath { get; private set; }

		internal string? LastRecoveryAttemptId { get; private set; }

		internal CancellationToken LastCancellationToken { get; private set; }

		internal FakeProcessRunner(
			Func<
				string,
				CancellationToken,
				Task<AntigravityOfficialPrintProcessResult>> runAsync,
			Func<string, CancellationToken, Task<bool>>?
				tryRecoverInterruptedAttemptAsync = null)
		{
			_runAsync = runAsync;
			_tryRecoverInterruptedAttemptAsync =
				tryRecoverInterruptedAttemptAsync ??
				((_, _) => Task.FromResult(false));
		}

		public Task<AntigravityOfficialPrintProcessResult> RunAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			CallCount++;
			LastExecutablePath = executablePath;
			LastCancellationToken = cancellationToken;
			return _runAsync(executablePath, cancellationToken);
		}

		public Task<bool> TryRecoverInterruptedAttemptAsync(
			string attemptId,
			CancellationToken cancellationToken)
		{
			RecoveryCallCount++;
			LastRecoveryAttemptId = attemptId;
			return _tryRecoverInterruptedAttemptAsync(
				attemptId,
				cancellationToken);
		}
	}

	private sealed class FakeSafetyStateStore :
		IAntigravityOfficialPrintSafetyStateStore
	{
		internal int LoadCallCount { get; private set; }

		internal int SaveCallCount { get; private set; }

		internal int ClearCallCount { get; private set; }

		internal List<AntigravityOfficialPrintSafetyState> SavedStates { get; } =
			new();

		internal bool ThrowOnSave { get; init; }

		internal Func<int, bool>? ShouldThrowOnSave { get; init; }

		internal Action<
			int,
			AntigravityOfficialPrintSafetyState>? AfterSave { get; init; }

		internal AntigravityOfficialPrintSafetyState? State { get; set; }

		public Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadCallCount++;
			return Task.FromResult(State);
		}

		public Task SaveAsync(
			AntigravityOfficialPrintSafetyState state,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SaveCallCount++;

			if (ThrowOnSave || (ShouldThrowOnSave?.Invoke(SaveCallCount) == true))
			{
				throw new IOException("Synthetic safety-state save failure.");
			}

			SavedStates.Add(state);
			State = state;
			AfterSave?.Invoke(SaveCallCount, state);
			return Task.CompletedTask;
		}

		public Task ClearAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ClearCallCount++;
			State = null;
			return Task.CompletedTask;
		}
	}

	private sealed class SerializingSafetyStateStore :
		IAntigravityOfficialPrintSafetyStateStore,
		IAntigravityOfficialPrintExecutionGate
	{
		private sealed class ExecutionLease : IDisposable
		{
			private Action? _release;
			private readonly bool _throwAfterRelease;

			internal ExecutionLease(
				Action release,
				bool throwAfterRelease)
			{
				_release = release;
				_throwAfterRelease = throwAfterRelease;
			}

			public void Dispose()
			{
				Action? release = Interlocked.Exchange(ref _release, null);

				if (release is null)
				{
					return;
				}

				release();

				if (_throwAfterRelease)
				{
					throw new IOException(
						"Synthetic execution-lease disposal failure.");
				}
			}
		}

		private readonly SemaphoreSlim _executionGate = new(1, 1);
		private int _acquisitionAttemptCount;
		private int _executionLeaseHeld;
		private int _releaseCallCount;
		private int _throwOnNextExecutionLeaseDispose;

		internal int AcquisitionAttemptCount =>
			Volatile.Read(ref _acquisitionAttemptCount);

		internal int AcquireCallCount { get; private set; }

		internal bool IsExecutionLeaseHeld =>
			Volatile.Read(ref _executionLeaseHeld) != 0;

		internal int ReleaseCallCount => Volatile.Read(ref _releaseCallCount);

		internal Func<
			CancellationToken,
			Task<AntigravityOfficialPrintSafetyState?>>? LoadOperation {
			get;
			set;
		}

		internal AntigravityOfficialPrintSafetyState? State { get; set; }

		internal void ThrowOnNextExecutionLeaseDispose()
		{
			Interlocked.Exchange(
				ref _throwOnNextExecutionLeaseDispose,
				1);
		}

		public async Task<IDisposable> AcquireExecutionLeaseAsync(
			CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _acquisitionAttemptCount);
			await _executionGate.WaitAsync(cancellationToken);
			AcquireCallCount++;
			Interlocked.Exchange(ref _executionLeaseHeld, 1);
			bool throwAfterRelease = Interlocked.Exchange(
				ref _throwOnNextExecutionLeaseDispose,
				0) != 0;
			return new ExecutionLease(
				() =>
				{
					Interlocked.Exchange(ref _executionLeaseHeld, 0);
					Interlocked.Increment(ref _releaseCallCount);
					_executionGate.Release();
				},
				throwAfterRelease);
		}

		public Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
			CancellationToken cancellationToken)
		{
			return LoadOperation?.Invoke(cancellationToken) ??
				Task.FromResult(State);
		}

		public Task SaveAsync(
			AntigravityOfficialPrintSafetyState state,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			State = state;
			return Task.CompletedTask;
		}

		public Task ClearAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			State = null;
			return Task.CompletedTask;
		}
	}

	private sealed class UnavailableExecutionGateStore :
		IAntigravityOfficialPrintSafetyStateStore,
		IAntigravityOfficialPrintExecutionGate
	{
		public Task<IDisposable> AcquireExecutionLeaseAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromException<IDisposable>(
				new UnauthorizedAccessException(
					"Synthetic execution-gate access failure."));
		}

		public Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException(
				"Safety state must not be read without the execution gate.");

		public Task SaveAsync(
			AntigravityOfficialPrintSafetyState state,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException(
				"Safety state must not be written without the execution gate.");

		public Task ClearAsync(CancellationToken cancellationToken) =>
			throw new InvalidOperationException(
				"Safety state must not be cleared without the execution gate.");
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _utcNow;

		internal FixedTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow() => _utcNow;
	}

	private sealed class MutableTimeProvider : TimeProvider
	{
		internal DateTimeOffset UtcNow { get; set; }

		internal MutableTimeProvider(DateTimeOffset utcNow)
		{
			UtcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow() => UtcNow;
	}

	private static readonly DateTimeOffset ObservedAt =
		new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan WindowsFixtureTimeout =
		TimeSpan.FromSeconds(15);

	[Fact]
	public void CreateAttemptJobName_WithValidAttemptId_ReturnsScopedDeterministicName()
	{
		const string attemptId = "8f6266fdd8fb4e66ae0f8973b03b9c46";

		string jobName =
			WindowsAntigravityOfficialPrintProcessRunner.CreateAttemptJobName(
				attemptId);

		Assert.Equal(
			"Local\\AiUsageDashboard.Antigravity." + attemptId,
			jobName);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-an-attempt")]
	[InlineData("8f6266fd-d8fb-4e66-ae0f-8973b03b9c46")]
	public void CreateAttemptJobName_WithInvalidAttemptId_RejectsName(
		string attemptId)
	{
		Assert.Throws<ArgumentException>(() =>
			WindowsAntigravityOfficialPrintProcessRunner.CreateAttemptJobName(
				attemptId));
	}

	[Fact]
	public void BuildCreationFlags_DoesNotRequestSuspendedStart()
	{
		const uint createSuspended = 0x00000004;
		uint creationFlags =
			WindowsAntigravityOfficialPrintProcessRunner.BuildCreationFlags();

		Assert.Equal(0u, creationFlags & createSuspended);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WithSingleProcessLimit_DeniesDescendantLaunch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(temporaryDirectory.Path, "spawn-child-and-hang.mode"),
			string.Empty);
		string processIdPath = Path.Combine(
			temporaryDirectory.Path,
			"runner-pids.txt");
		WindowsAntigravityOfficialPrintProcessRunner runner = new();

		using AntigravityOfficialPrintProcessResult result =
			await runner.RunAsync(executablePath, CancellationToken.None);

		Assert.NotEqual(0, result.ExitCode);
		Assert.True(result.HasStderr);
		Assert.False(File.Exists(processIdPath));

		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task TryRecoverInterruptedAttemptAsync_WhenNamedJobDoesNotExist_ReturnsTrue()
	{
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5));

		bool recovered = await runner.TryRecoverInterruptedAttemptAsync(
			Guid.NewGuid().ToString("N"),
			CancellationToken.None);

		Assert.True(recovered);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenCreateProcessReturns_IsAlreadyInNamedJob()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(temporaryDirectory.Path, "spawn-child-and-hang.mode"),
			string.Empty);
		string attemptId = Guid.NewGuid().ToString("N");
		string jobName =
			WindowsAntigravityOfficialPrintProcessRunner.CreateAttemptJobName(
				attemptId);
		const string injectedFailure =
			"Injected failure immediately after CreateProcess returned.";
		bool openedNamedJob = false;
		uint activeProcessCount = 0;
		bool preStartCompleted = false;
		Process? createdProcess = null;
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5),
			afterProcessCreated: processId =>
			{
				Assert.True(preStartCompleted);
				createdProcess = Process.GetProcessById(checked((int)processId));
				_ = createdProcess.Handle;
				openedNamedJob = WindowsProcessJob.TryOpenExisting(
					jobName,
					out WindowsProcessJob? observedJob);

				if (openedNamedJob)
				{
					using WindowsProcessJob job = observedJob!;
					activeProcessCount = job.GetActiveProcessCount();
				}

				throw new InvalidOperationException(injectedFailure);
			});

		try
		{
			AntigravityOfficialPrintProcessRunException exception =
				await Assert.ThrowsAsync<
					AntigravityOfficialPrintProcessRunException>(() =>
						runner.RunAsync(
							executablePath,
							attemptId,
							_ =>
							{
								preStartCompleted = true;
								return Task.CompletedTask;
							},
							CancellationToken.None));
			await exception.QuiescenceTask.WaitAsync(WindowsFixtureTimeout);
			bool recovered = await runner.TryRecoverInterruptedAttemptAsync(
				attemptId,
				CancellationToken.None);

			Assert.True(exception.WasProcessStarted);
			Assert.True(exception.WasTerminationConfirmed);
			InvalidOperationException inner =
				Assert.IsType<InvalidOperationException>(
					exception.InnerException);
			Assert.Equal(injectedFailure, inner.Message);
			Assert.True(openedNamedJob);
			Assert.True(activeProcessCount >= 1);
			Assert.True(recovered);
			Assert.NotNull(createdProcess);
			await createdProcess.WaitForExitAsync().WaitAsync(
				WindowsFixtureTimeout);
			Assert.True(createdProcess.HasExited);
		}
		finally
		{
			if (createdProcess is not null)
			{
				try
				{
					if (!createdProcess.HasExited)
					{
						createdProcess.Kill(entireProcessTree: true);
						await createdProcess.WaitForExitAsync().WaitAsync(
							WindowsFixtureTimeout);
					}
				}
				finally
				{
					createdProcess.Dispose();
				}
			}
		}

		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenInjectedPreStartCallbackFails_DoesNotCreateProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(temporaryDirectory.Path, "record-start.mode"),
			string.Empty);
		string startMarkerPath = Path.Combine(
			temporaryDirectory.Path,
			"fixture-started.txt");
		ProcessStartInfo startInfo =
			WindowsAntigravityOfficialPrintProcessRunner.CreateStartInfo(
				executablePath);
		string jobName =
			$"Local\\AiUsageDashboard.Tests.{Guid.NewGuid():N}";
		const string injectedFailure =
			"Injected failure for the caller-supplied process start info.";
		bool perAttemptCallbackCompleted = false;
		bool processCreated = false;
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5),
			beforeStartAsync: _ =>
			{
				Assert.True(perAttemptCallbackCompleted);
				throw new InvalidOperationException(injectedFailure);
			},
			afterProcessCreated: _ => processCreated = true);

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				runner.RunAsync(
					startInfo,
					jobName,
					_ =>
					{
						perAttemptCallbackCompleted = true;
						return Task.CompletedTask;
					},
					CancellationToken.None));

		Assert.Equal(injectedFailure, exception.Message);
		Assert.True(perAttemptCallbackCompleted);
		Assert.False(processCreated);
		Assert.False(File.Exists(startMarkerPath));
		Assert.False(WindowsProcessJob.TryOpenExisting(
			jobName,
			out WindowsProcessJob? remainingJob));
		Assert.Null(remainingJob);

		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenCreateFailsAfterPreStartCallback_RecoveryConfirmsNoProcessTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"missing-agy.exe");
		string attemptId = Guid.NewGuid().ToString("N");
		string jobName =
			WindowsAntigravityOfficialPrintProcessRunner.CreateAttemptJobName(
				attemptId);
		bool preStartCompleted = false;
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5));

		Win32Exception exception = await Assert.ThrowsAsync<Win32Exception>(() =>
			runner.RunAsync(
				executablePath,
				attemptId,
				_ =>
				{
					preStartCompleted = true;
					return Task.CompletedTask;
				},
				CancellationToken.None));
		bool recovered = await runner.TryRecoverInterruptedAttemptAsync(
			attemptId,
			CancellationToken.None);

		Assert.True(preStartCompleted);
		Assert.NotEqual(0, exception.NativeErrorCode);
		Assert.True(recovered);
		Assert.False(WindowsProcessJob.TryOpenExisting(
			jobName,
			out WindowsProcessJob? remainingJob));
		Assert.Null(remainingJob);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenCanceledAtPerAttemptBoundary_DoesNotExecuteFixture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(temporaryDirectory.Path, "record-start.mode"),
			string.Empty);
		string startMarkerPath = Path.Combine(
			temporaryDirectory.Path,
			"fixture-started.txt");
		TaskCompletionSource reachedBeforeStart = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseBeforeStart = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5));
		using CancellationTokenSource cancellationSource = new();
		Task<AntigravityOfficialPrintProcessResult> run = runner.RunAsync(
			executablePath,
			Guid.NewGuid().ToString("N"),
			async _ =>
			{
				reachedBeforeStart.TrySetResult();
				await releaseBeforeStart.Task;
			},
			cancellationSource.Token);
		await reachedBeforeStart.Task.WaitAsync(WindowsFixtureTimeout);

		cancellationSource.Cancel();
		releaseBeforeStart.TrySetResult();
		OperationCanceledException exception =
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

		Assert.Equal(cancellationSource.Token, exception.CancellationToken);
		Assert.False(File.Exists(startMarkerPath));
		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenRootExitsBeforeChild_WaitsForWholeJobTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(
				temporaryDirectory.Path,
				"spawn-child-then-exit.mode"),
			string.Empty);
		string processIdPath = Path.Combine(
			temporaryDirectory.Path,
			"runner-pids.txt");
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5));
		Stopwatch stopwatch = Stopwatch.StartNew();
		Task<AntigravityOfficialPrintProcessResult> run = runner.RunAsync(
			executablePath,
			CancellationToken.None);
		await WaitForFileOrRunCompletionAsync(
			processIdPath,
			run,
			WindowsFixtureTimeout);
		(int _, int childProcessId) = ParseRunnerProcessIds(processIdPath);
		using Process childProcess = Process.GetProcessById(childProcessId);

		using AntigravityOfficialPrintProcessResult result = await run.WaitAsync(
			WindowsFixtureTimeout);
		stopwatch.Stop();
		await childProcess.WaitForExitAsync().WaitAsync(WindowsFixtureTimeout);

		Assert.Equal(0, result.ExitCode);
		Assert.Equal("fixture-output\r\n", Encoding.UTF8.GetString(result.Stdout.Span));
		Assert.True(childProcess.HasExited);
		Assert.True(
			stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400),
			$"The runner returned in {stopwatch.Elapsed} before the descendant could exit.");
		childProcess.Dispose();
		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenTimedOut_TerminatesAndConfirmsWholeJobTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(
				temporaryDirectory.Path,
				"spawn-child-and-hang.mode"),
			string.Empty);
		string processIdPath = Path.Combine(
			temporaryDirectory.Path,
			"runner-pids.txt");
		WindowsAntigravityOfficialPrintProcessRunner runner = new(
			// A hosted Windows runner can take more than a second to cold-start
			// the managed apphost. This test verifies bounded timeout cleanup,
			// not sub-second startup latency.
			commandTimeout: TimeSpan.FromSeconds(5),
			cleanupTimeout: TimeSpan.FromSeconds(5));
		Task<AntigravityOfficialPrintProcessResult> run = runner.RunAsync(
			executablePath,
			CancellationToken.None);
		await WaitForFileOrRunCompletionAsync(
			processIdPath,
			run,
			WindowsFixtureTimeout);
		(int rootProcessId, int childProcessId) =
			ParseRunnerProcessIds(processIdPath);
		using Process rootProcess = Process.GetProcessById(rootProcessId);
		using Process childProcess = Process.GetProcessById(childProcessId);

		AntigravityOfficialPrintProcessRunException exception =
			await Assert.ThrowsAsync<AntigravityOfficialPrintProcessRunException>(
				() => run);
		await exception.QuiescenceTask.WaitAsync(WindowsFixtureTimeout);
		await Task.WhenAll(
			rootProcess.WaitForExitAsync(),
			childProcess.WaitForExitAsync()).WaitAsync(WindowsFixtureTimeout);

		Assert.True(exception.WasProcessStarted);
		Assert.True(exception.WasTerminationConfirmed);
		Assert.IsType<TimeoutException>(exception.InnerException);
		Assert.True(rootProcess.HasExited);
		Assert.True(childProcess.HasExited);
		rootProcess.Dispose();
		childProcess.Dispose();
		await DeleteFixtureImageAfterReleaseAsync(
			Path.Combine(
				temporaryDirectory.Path,
				"AiUsageDashboard.TerminalFixture.dll"),
			WindowsFixtureTimeout);
		await DeleteFixtureImageAfterReleaseAsync(
			executablePath,
			WindowsFixtureTimeout);
		await DeleteTemporaryFixtureDirectoryAsync(
			temporaryDirectory.Path,
			WindowsFixtureTimeout);
		temporaryDirectory.MarkAsRemovedExternally();
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task ProcessRunner_WhenOwningHostIsHardKilled_KillOnCloseTerminatesWholeTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CopyTerminalFixture(
			temporaryDirectory.Path,
			"AiUsageDashboard.TerminalFixture.exe");
		File.WriteAllText(
			Path.Combine(
				temporaryDirectory.Path,
				"spawn-child-and-hang.mode"),
			string.Empty);
		string processIdPath = Path.Combine(
			temporaryDirectory.Path,
			"runner-pids.txt");
		string rootProcessIdPath = Path.Combine(
			temporaryDirectory.Path,
			"runner-root-pid.txt");
		string attemptId = Guid.NewGuid().ToString("N");
		string jobName = WindowsAntigravityOfficialPrintProcessRunner
			.CreateAttemptJobName(attemptId);
		string hostExecutablePath = Path.Combine(
			AppContext.BaseDirectory,
			"AiUsageDashboard.AntigravitySpike.exe");
		Assert.True(
			File.Exists(hostExecutablePath),
			$"The independent crash host was not built at {hostExecutablePath}.");
		ProcessStartInfo hostStartInfo = new(hostExecutablePath)
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		hostStartInfo.ArgumentList.Add("__test-job-crash-host");
		hostStartInfo.ArgumentList.Add(executablePath);
		hostStartInfo.ArgumentList.Add(attemptId);
		hostStartInfo.ArgumentList.Add(rootProcessIdPath);
		using Process hostProcess = new() { StartInfo = hostStartInfo };
		Assert.True(hostProcess.Start());
		Task<string> hostErrorTask = hostProcess.StandardError.ReadToEndAsync();
		Process? rootProcess = null;
		Process? childProcess = null;

		try
		{
			await WaitForFileOrProcessExitAsync(
				processIdPath,
				hostProcess,
				hostErrorTask,
				WindowsFixtureTimeout);
			(int rootProcessId, int childProcessId) =
				ParseRunnerProcessIds(processIdPath);
			rootProcess = Process.GetProcessById(rootProcessId);
			childProcess = Process.GetProcessById(childProcessId);
			Assert.False(rootProcess.HasExited);
			Assert.False(childProcess.HasExited);

			Assert.True(
				WindowsProcessJob.TryOpenExisting(
					jobName,
					out WindowsProcessJob? observedJob));
			using (observedJob)
			{
				Assert.True(
					observedJob!.GetActiveProcessCount() >= 2u,
					"The named Job must contain at least the fixture root and child.");
			}

			// This must kill only the owner. The fixture tree must terminate
			// because the last kill-on-close Job handle disappears, not because
			// Process.Kill recursively walked the host's descendants.
			hostProcess.Kill(entireProcessTree: false);
			await hostProcess.WaitForExitAsync().WaitAsync(WindowsFixtureTimeout);
			await Task.WhenAll(
				rootProcess.WaitForExitAsync(),
				childProcess.WaitForExitAsync()).WaitAsync(WindowsFixtureTimeout);

			Assert.True(rootProcess.HasExited);
			Assert.True(childProcess.HasExited);
			await WaitUntilAsync(
				() => !TryOpenAndDisposeJob(jobName),
				WindowsFixtureTimeout);
		}
		finally
		{
			await TerminateFixtureProcessAsync(
				hostProcess,
				entireProcessTree: false,
				WindowsFixtureTimeout);

			if (rootProcess is null &&
				TryReadProcessId(rootProcessIdPath, out int observedRootProcessId))
			{
				rootProcess = TryGetProcessById(observedRootProcessId);
			}

			if ((rootProcess is null || childProcess is null) &&
				File.Exists(processIdPath))
			{
				(int rootProcessId, int childProcessId) =
					ParseRunnerProcessIds(processIdPath);
				rootProcess ??= TryGetProcessById(rootProcessId);
				childProcess ??= TryGetProcessById(childProcessId);
			}

			await TerminateFixtureProcessAsync(
				rootProcess,
				entireProcessTree: true,
				WindowsFixtureTimeout);
			await TerminateFixtureProcessAsync(
				childProcess,
				entireProcessTree: true,
				WindowsFixtureTimeout);
			rootProcess?.Dispose();
			childProcess?.Dispose();
			await hostErrorTask.WaitAsync(WindowsFixtureTimeout);
			await DeleteFixtureImageAfterReleaseAsync(
				Path.Combine(
					temporaryDirectory.Path,
					"AiUsageDashboard.TerminalFixture.dll"),
				WindowsFixtureTimeout);
			await DeleteFixtureImageAfterReleaseAsync(
				executablePath,
				WindowsFixtureTimeout);
			await DeleteTemporaryFixtureDirectoryAsync(
				temporaryDirectory.Path,
				WindowsFixtureTimeout);
			temporaryDirectory.MarkAsRemovedExternally();
		}
	}

	[Fact]
	public async Task ProcessRunnerPositiveExitObserver_WhenQueriesKeepFailing_ReturnsWithinBound()
	{
		TimeSpan timeout = TimeSpan.FromMilliseconds(75);
		Stopwatch stopwatch = Stopwatch.StartNew();

		bool confirmed = await WindowsAntigravityOfficialPrintProcessRunner
			.WaitForPositiveProcessExitCoreAsync(
				() => throw new InvalidOperationException(
					"Injected persistent process-wait failure."),
				timeout)
			.WaitAsync(TimeSpan.FromSeconds(2));
		stopwatch.Stop();

		Assert.False(confirmed);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(2),
			$"Bounded positive process-exit observation took {stopwatch.Elapsed}.");
	}

	[Fact]
	public void OfficialPrintTiming_KeepsUsageTimeoutBoundedWithCompleteCaptureBudget()
	{
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			AntigravityOfficialPrintTiming.UsageCommandTimeout);
		Assert.Equal(
			AntigravityOfficialPrintTiming.ExecutionGateTimeout +
			AntigravityOfficialPrintTiming.SafetyStateOperationTimeout +
			AntigravityOfficialPrintTiming.CapabilityValidationTimeout +
			AntigravityOfficialPrintTiming.SafetyStateOperationTimeout +
			AntigravityOfficialPrintTiming.UsageCommandTimeout +
			AntigravityOfficialPrintTiming.UsageCleanupTimeout +
			AntigravityOfficialPrintTiming.SafetyStateOperationTimeout,
			AntigravityOfficialPrintTiming.MaximumCaptureDuration);
	}

	[Fact]
	public void CreateStartInfo_UsesFixedArgumentsWithoutShellOrCommandLookupPath()
	{
		string executablePath = Path.GetFullPath(Path.Combine(
			Path.GetTempPath(),
			"agy-official-print-start-info",
			"agy.exe"));

		ProcessStartInfo startInfo =
			WindowsAntigravityOfficialPrintProcessRunner.CreateStartInfo(
				executablePath);

		Assert.Equal(executablePath, startInfo.FileName);
		Assert.Equal(
			Path.GetDirectoryName(executablePath),
			startInfo.WorkingDirectory);
		Assert.Equal(
			new[] { "-p", "/usage", "--output-format", "stream-json" },
			startInfo.ArgumentList);
		Assert.Empty(startInfo.Arguments);
		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.CreateNoWindow);
		Assert.True(startInfo.RedirectStandardInput);
		Assert.True(startInfo.RedirectStandardOutput);
		Assert.True(startInfo.RedirectStandardError);
		Assert.Equal("1", startInfo.Environment["NO_COLOR"]);
		Assert.Equal(
			"true",
			startInfo.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
		Assert.DoesNotContain(
			startInfo.Environment.Keys,
			name => string.Equals(
				name,
				"PATH",
				StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(
			startInfo.Environment.Keys,
			name => string.Equals(
				name,
				"COMSPEC",
				StringComparison.OrdinalIgnoreCase));

		IReadOnlyDictionary<string, string> sharedAllowlist =
			AntigravityCliProcessEnvironment.BuildAllowlist();
		Assert.DoesNotContain(
			sharedAllowlist.Keys,
			name => string.Equals(
				name,
				"PATH",
				StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(
			sharedAllowlist.Keys,
			name => string.Equals(
				name,
				"COMSPEC",
				StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task CaptureAsync_WhenProvenanceFails_DoesNotStartProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: false);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			second.FailureKind);
		Assert.Null(first.OfficialPrintVersionAssessment);
		Assert.Null(second.OfficialPrintVersionAssessment);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithInvalidExecutablePath_DoesNotLatchOrValidate()
	{
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync("agy.exe", CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync("agy.exe", CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			second.FailureKind);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithValidOfficialEnvelope_ReturnsUsage()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			result.AccountIdentity);
		Assert.Equal(4, result.Windows.Count);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
		Assert.Equal(executablePath, runner.LastExecutablePath);
	}

	[Fact]
	public async Task CaptureAsync_WithValidEnvelopeAndBoundedStderr_ReturnsUsage()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(
			() => CreateOutput(),
			hasStderr: true);
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.None,
			result.FailureKind);
		Assert.Equal(4, result.Windows.Count);
	}

	[Fact]
	public async Task CaptureAsync_WhenStderrLimitIsExceeded_SchedulesAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(
			() => CreateOutput(),
			hasStderr: true,
			stderrLimitExceeded: true);
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.StderrTooLarge,
			first.SafetyFailureReason);
		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenStdoutLimitIsExceeded_SchedulesAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(
			() => CreateOutput(),
			stdoutLimitExceeded: true);
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.StdoutTooLarge,
			first.SafetyFailureReason);
		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithValidEnvelope_ZeroesRunnerOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		byte[] rawOutput = Encoding.UTF8.GetBytes(CreateOutput());
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				rawOutput,
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.All(rawOutput, value => Assert.Equal(0, value));
	}

	[Fact]
	public async Task ReadBoundedAsync_WhenReadFails_ZeroesScratchBuffer()
	{
		using ThrowingCaptureStream stream = new();

		await Assert.ThrowsAsync<IOException>(() =>
			WindowsAntigravityOfficialPrintProcessRunner.ReadBoundedAsync(
				stream,
				maximumSavedBytes: 1024,
				saveBytes: true,
				CancellationToken.None));

		Assert.False(stream.LastReadBuffer.IsEmpty);
		Assert.True(stream.LastReadBuffer.Span.ToArray().All(value => value == 0));
	}

	[Theory]
	[InlineData(8, false)]
	[InlineData(9, true)]
	public async Task ReadBoundedAsync_WithoutSavingBytes_StillEnforcesLimit(
		int byteCount,
		bool expectedLimitExceeded)
	{
		using MemoryStream stream = new(new byte[byteCount]);

		WindowsAntigravityOfficialPrintProcessRunner.BoundedReadResult result =
			await WindowsAntigravityOfficialPrintProcessRunner.ReadBoundedAsync(
				stream,
				maximumSavedBytes: 8,
				saveBytes: false,
				CancellationToken.None);

		Assert.True(result.HadAnyBytes);
		Assert.Equal(expectedLimitExceeded, result.LimitExceeded);
		Assert.Empty(result.Bytes);
	}

	[Fact]
	public async Task CaptureAsync_WithNonZeroUsageCounter_LatchesPathBeforeSecondRun()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(
			() => CreateOutput(totalTokens: 1));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			first.SafetyFailureReason);
		Assert.False(first.AutomaticRevalidationPending);
		Assert.False(second.AutomaticRevalidationPending);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithUnverifiableShape_SchedulesAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => "{}\n{}\n");
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
			first.SafetyFailureReason);
		Assert.Equal(
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed,
			first.OfficialPrintVersionAssessment?.Kind);
		Assert.Equal(
			"1.1.12",
			first.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.Equal(
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed,
			second.OfficialPrintVersionAssessment?.Kind);
		Assert.Equal(
			"1.1.12",
			second.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithReferenceVersionSchemaFailure_DoesNotAttachVersionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(
			isSupported: true,
			cliVersion: "1.1.11");
		FakeProcessRunner runner = CreateOutputRunner(() => "{}\n{}\n");
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(
				executablePath,
				CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(
				executablePath,
				CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
			first.SafetyFailureReason);
		Assert.Null(first.OfficialPrintVersionAssessment);
		Assert.Null(second.OfficialPrintVersionAssessment);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithTruncatedResult_SchedulesAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() =>
		{
			string output = CreateOutput();
			return output[..^8];
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.OutputJsonInvalid,
			first.SafetyFailureReason);
		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenRunnerFailsBeforeStart_DoesNotLatch()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: false,
					new IOException("Synthetic pre-start failure."))));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.CaptureRejected,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.CaptureRejected,
			second.FailureKind);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryFailsBeforeStart_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true,
			AttemptId: "c15500424ca84361a082ef8b4ecfdb33");
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: false,
					new IOException("Synthetic pre-start failure."))));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult failed =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(failed.AutomaticRevalidationPending);
		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			failed.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryCapabilityValidationIsCallerCanceled_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		MutableTimeProvider timeProvider = new(ObservedAt);
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true,
			AttemptId: "c15500424ca84361a082ef8b4ecfdb33");
		FakeSafetyStateStore store = new() { State = pendingState };
		int validationCount = 0;
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = new(
			(_, cancellationToken) =>
			{
				if (Interlocked.Increment(ref validationCount) == 1)
				{
					cancellationSource.Cancel();
					return Task.FromException<
						AntigravityOfficialPrintCapabilityValidationResult>(
							new OperationCanceledException(cancellationToken));
				}

				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						cliVersion: "1.1.12",
						executableLease:
							AntigravityExecutableLease.Acquire(executablePath)));
			},
			runner,
			timeProvider,
			store);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(executablePath, cancellationSource.Token));
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(pendingState.Reason, coolingDown.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(pendingState.AttemptId, store.State.AttemptId);
		Assert.Equal(1, validationCount);
		Assert.Equal(0, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, validationCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryRawRunnerCancellationOccursBeforeStart_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		MutableTimeProvider timeProvider = new(ObservedAt);
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true,
			AttemptId: "c15500424ca84361a082ef8b4ecfdb33");
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				cancellationSource.Cancel();
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new OperationCanceledException(cancellationToken));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(executablePath, cancellationSource.Token));
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(pendingState.Reason, coolingDown.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(pendingState.AttemptId, store.State.AttemptId);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryRawRunnerFailureOccursBeforeStart_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true,
			AttemptId: "c15500424ca84361a082ef8b4ecfdb33");
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new IOException("Synthetic raw pre-start failure."));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult failed =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(failed.AutomaticRevalidationPending);
		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(pendingState.Reason, failed.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(pendingState.AttemptId, store.State.AttemptId);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenCallerCancelsBeforeProcessCreation_PropagatesCancellationWithoutLatchAndRetainsLeases()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		TaskCompletionSource processQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new();
		AntigravityExecutableLease executableLease =
			AntigravityExecutableLease.Acquire(executablePath);
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			cancellationSource.Cancel();
			return Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: false,
					new OperationCanceledException(cancellationToken),
					wasTerminationConfirmed: false,
					quiescenceTask: processQuiescence.Task));
		});
		AntigravityOfficialPrintUsageClient client = new(
			(_, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						cliVersion: "1.1.12",
						executableLease: executableLease));
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);

		OperationCanceledException exception =
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(executablePath, cancellationSource.Token));

		Assert.Equal(cancellationSource.Token, exception.CancellationToken);
		Assert.Null(store.State);
		Assert.False(executableLease.IsDisposed);

		Task<IDisposable> externalAcquisition =
			store.AcquireExecutionLeaseAsync(CancellationToken.None);
		Task firstCompletion = await Task.WhenAny(
			externalAcquisition,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(externalAcquisition, firstCompletion);

		processQuiescence.TrySetResult();
		using IDisposable externalLease = await externalAcquisition.WaitAsync(
			TimeSpan.FromSeconds(5));

		Assert.True(executableLease.IsDisposed);
		Assert.True(store.IsExecutionLeaseHeld);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenRunnerFailsAfterStart_LatchesBeforeRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new IOException("Synthetic post-start failure."),
					wasTerminationConfirmed: true)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
			first.SafetyFailureReason);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenStartedProcessIsCanceled_LatchesAndPropagatesCancellation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			cancellationSource.Cancel();
			return Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new OperationCanceledException(cancellationToken),
					wasTerminationConfirmed: true));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		OperationCanceledException exception =
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(executablePath, cancellationSource.Token));
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(cancellationSource.Token, exception.CancellationToken);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.CanceledAfterStart,
			second.SafetyFailureReason);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
		Assert.Equal(
			cancellationSource.Token,
			runner.LastCancellationToken);
	}

	[Fact]
	public async Task CaptureAsync_WithConfirmedCancellation_AutomaticallyRetriesAfterCooldown()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		MutableTimeProvider timeProvider = new(ObservedAt);
		SerializingSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				cancellationSource.Cancel();
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new OperationCanceledException(cancellationToken),
						wasTerminationConfirmed: true));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(executablePath, cancellationSource.Token));
		await WaitUntilAsync(
			() => store.State?.AutomaticRevalidationAllowed == true,
			TimeSpan.FromSeconds(5));

		Assert.NotNull(store.State);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.CanceledAfterStart,
			store.State.Reason);
		Assert.True(Guid.TryParseExact(store.State.AttemptId, "N", out _));
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);

		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(1, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithLatePositiveQuiescence_UpgradesTimeoutForAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource positiveQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		MutableTimeProvider timeProvider = new(ObservedAt);
		SerializingSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new TimeoutException("Synthetic timeout."),
						wasTerminationConfirmed: false,
						quiescenceTask: positiveQuiescence.Task));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult initialFailure =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
			initialFailure.SafetyFailureReason);
		Assert.False(initialFailure.AutomaticRevalidationPending);
		Assert.False(store.State!.AutomaticRevalidationAllowed);

		timeProvider.UtcNow += TimeSpan.FromMinutes(10);
		positiveQuiescence.SetResult();
		await WaitUntilAsync(
			() => store.State?.AutomaticRevalidationAllowed == true,
			TimeSpan.FromSeconds(5));

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			store.State!.Reason);
		Assert.True(Guid.TryParseExact(store.State.AttemptId, "N", out _));
		Assert.Equal(timeProvider.UtcNow, store.State.LatchedAtUtc);
		Assert.Equal(1, runner.CallCount);
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(1, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenOldQuiescenceArrivesAfterManualRetry_DoesNotRestoreOldMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource positiveQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new TimeoutException("Synthetic timeout."),
						wasTerminationConfirmed: false,
						quiescenceTask: positiveQuiescence.Task));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult initialFailure =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
			initialFailure.SafetyFailureReason);

		Task<AntigravityProductionUsageResult> manualRetry =
			client.RevalidateAsync(
				executablePath,
				() => { },
				CancellationToken.None);
		await WaitUntilAsync(
			() => store.AcquisitionAttemptCount >= 2,
			TimeSpan.FromSeconds(5));

		positiveQuiescence.SetResult();
		AntigravityProductionUsageResult manualResult = await manualRetry;

		Assert.True(manualResult.IsSuccessful);
		Assert.Null(store.State);
		await WaitUntilAsync(
			() => store.ReleaseCallCount >= 3,
			TimeSpan.FromSeconds(5));
		Assert.Null(store.State);
		Assert.Equal(2, runner.CallCount);

		AntigravityProductionUsageResult nextScheduledRefresh =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(nextScheduledRefresh.IsSuccessful);
		Assert.Equal(3, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenCallerCancelsBlockingSafetyLoad_HoldsExecutionGateUntilLoadQuiesces()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource loadStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<AntigravityOfficialPrintSafetyState?> releaseLoad =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new()
		{
			LoadOperation = _ =>
			{
				loadStarted.TrySetResult();
				return releaseLoad.Task;
			}
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		using CancellationTokenSource cancellationSource = new();

		Task<AntigravityProductionUsageResult> capture = client.CaptureAsync(
			executablePath,
			cancellationSource.Token);
		await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellationSource.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);

		Task<IDisposable> externalAcquisition =
			store.AcquireExecutionLeaseAsync(CancellationToken.None);
		Task firstCompletion = await Task.WhenAny(
			externalAcquisition,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(externalAcquisition, firstCompletion);

		releaseLoad.TrySetResult(null);
		using IDisposable externalLease = await externalAcquisition.WaitAsync(
			TimeSpan.FromSeconds(5));

		Assert.True(store.IsExecutionLeaseHeld);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenCallerCancelsBlockingCapability_HoldsExecutionGateAndDisposesLateResult()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource validationStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<AntigravityOfficialPrintCapabilityValidationResult>
			releaseValidation = new(
				TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new();
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = new(
			(_, _) =>
			{
				validationStarted.TrySetResult();
				return releaseValidation.Task;
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);
		using CancellationTokenSource cancellationSource = new();

		Task<AntigravityProductionUsageResult> capture = client.CaptureAsync(
			executablePath,
			cancellationSource.Token);
		await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellationSource.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);

		Task<IDisposable> externalAcquisition =
			store.AcquireExecutionLeaseAsync(CancellationToken.None);
		Task firstCompletion = await Task.WhenAny(
			externalAcquisition,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(externalAcquisition, firstCompletion);

		AntigravityExecutableLease lateExecutableLease =
			AntigravityExecutableLease.Acquire(executablePath);
		releaseValidation.TrySetResult(
			new AntigravityOfficialPrintCapabilityValidationResult(
				isSupported: true,
				cliVersion: "1.1.12",
				executableLease: lateExecutableLease));
		using IDisposable externalLease = await externalAcquisition.WaitAsync(
			TimeSpan.FromSeconds(5));

		Assert.True(lateExecutableLease.IsDisposed);
		Assert.True(store.IsExecutionLeaseHeld);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenStartedProcessTerminationIsUnconfirmed_HoldsExecutionGateUntilQuiescence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource processQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new IOException("Synthetic post-start failure."),
					wasTerminationConfirmed: false,
					quiescenceTask: processQuiescence.Task)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
			result.SafetyFailureReason);

		Task<IDisposable> externalAcquisition =
			store.AcquireExecutionLeaseAsync(CancellationToken.None);
		Task firstCompletion = await Task.WhenAny(
			externalAcquisition,
			Task.Delay(TimeSpan.FromMilliseconds(100)));
		Assert.NotSame(externalAcquisition, firstCompletion);

		processQuiescence.TrySetResult();
		await WaitForExclusiveReadAsync(
			executablePath,
			TimeSpan.FromSeconds(5));
		using IDisposable externalLease = await externalAcquisition.WaitAsync(
			TimeSpan.FromSeconds(5));

		Assert.True(store.IsExecutionLeaseHeld);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenContainedCleanupFaults_ReleasesExecutableAndExecutionLeases()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource processQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		SerializingSafetyStateStore store = new();
		AntigravityExecutableLease executableLease =
			AntigravityExecutableLease.Acquire(executablePath);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new IOException("Synthetic post-containment failure."),
					wasTerminationConfirmed: false,
					quiescenceTask: processQuiescence.Task)));
		AntigravityOfficialPrintUsageClient client = new(
			(_, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						cliVersion: "1.1.12",
						executableLease: executableLease));
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.False(executableLease.IsDisposed);
		Assert.True(store.IsExecutionLeaseHeld);

		processQuiescence.TrySetException(
			new IOException("Synthetic bounded cleanup completion failure."));
		await WaitUntilAsync(
			() => executableLease.IsDisposed && !store.IsExecutionLeaseHeld,
			TimeSpan.FromSeconds(5));

		Assert.True(executableLease.IsDisposed);
		Assert.False(store.IsExecutionLeaseHeld);
		Assert.Equal(1, store.ReleaseCallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenContainedCleanupCompletesWithoutPositiveEvidence_ReleasesLeasesWithoutPromotingAutomaticRevalidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		SerializingSafetyStateStore store = new();
		AntigravityExecutableLease executableLease =
			AntigravityExecutableLease.Acquire(executablePath);
		Task missingPositiveEvidence = Task.FromException(
			new InvalidOperationException(
				"Synthetic missing positive quiescence evidence."));
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic timeout."),
					wasTerminationConfirmed: false,
					quiescenceTask: Task.CompletedTask,
					positiveQuiescenceTask: missingPositiveEvidence)));
		AntigravityOfficialPrintUsageClient client = new(
			(_, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(
					new AntigravityOfficialPrintCapabilityValidationResult(
						isSupported: true,
						cliVersion: "1.1.12",
						executableLease: executableLease));
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		await WaitUntilAsync(
			() => executableLease.IsDisposed && !store.IsExecutionLeaseHeld,
			TimeSpan.FromSeconds(5));
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
			result.SafetyFailureReason);
		Assert.False(result.AutomaticRevalidationPending);
		Assert.True(executableLease.IsDisposed);
		Assert.False(store.IsExecutionLeaseHeld);
		Assert.NotNull(store.State);
		Assert.False(store.State.AutomaticRevalidationAllowed);
		Assert.Equal(1, store.ReleaseCallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenExecutionLeaseDisposeThrows_ReleasesLocalCaptureGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		SerializingSafetyStateStore store = new();
		store.ThrowOnNextExecutionLeaseDispose();
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		await Assert.ThrowsAsync<IOException>(() =>
			client.CaptureAsync(executablePath, CancellationToken.None));
		AntigravityProductionUsageResult second = await client.CaptureAsync(
				executablePath,
				CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(second.IsSuccessful);
		Assert.Equal(2, store.AcquireCallCount);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Theory]
	[InlineData(1, false, false, AntigravityUsageSafetyFailureReason.NonZeroExit)]
	[InlineData(0, true, false, AntigravityUsageSafetyFailureReason.StdoutTooLarge)]
	[InlineData(0, false, true, AntigravityUsageSafetyFailureReason.StderrTooLarge)]
	public async Task CaptureAsync_WhenProcessResultIsUnsafe_ClassifiesReason(
		int exitCode,
		bool stdoutLimitExceeded,
		bool stderrLimitExceeded,
		AntigravityUsageSafetyFailureReason expectedReason)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded,
				hasStderr: stderrLimitExceeded,
				stderrLimitExceeded)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Equal(expectedReason, result.SafetyFailureReason);
		Assert.True(result.AutomaticRevalidationPending);

		if (expectedReason ==
			AntigravityUsageSafetyFailureReason.NonZeroExit)
		{
			Assert.Equal(
				AntigravityOfficialPrintVersionAssessmentKind
					.UnverifiedAllowed,
				result.OfficialPrintVersionAssessment?.Kind);
			Assert.Equal(
				"1.1.12",
				result.OfficialPrintVersionAssessment?.DetectedVersion);
		}
		else
		{
			Assert.Null(result.OfficialPrintVersionAssessment);
		}
	}

	[Fact]
	public async Task CaptureAsync_WhenNonZeroExitCompletedOutputContainsUsageActivity_PrioritizesUsageActivityHardLatch()
	{
		await AssertCompletedUsageActivityTakesPriorityAsync(
			exitCode: 1,
			hasStderr: false,
			stderrLimitExceeded: false);
	}

	[Fact]
	public async Task CaptureAsync_WhenStderrTooLargeCompletedOutputContainsUsageActivity_PrioritizesUsageActivityHardLatch()
	{
		await AssertCompletedUsageActivityTakesPriorityAsync(
			exitCode: 0,
			hasStderr: true,
			stderrLimitExceeded: true);
	}

	[Fact]
	public async Task CaptureAsync_WithNonZeroExit_RetriesAutomaticallyAfterCooldown()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			int exitCode = Interlocked.Increment(ref runCount) == 1 ? 1 : 0;
			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult failed =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(failed.AutomaticRevalidationPending);
		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.NotNull(store.State);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			store.State.Reason);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticNonZeroExitRepeats_RemainsPending()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 1,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult repeated =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(repeated.AutomaticRevalidationPending);
		Assert.True(blocked.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			repeated.SafetyFailureReason);
		Assert.Equal(repeated.SafetyFailureReason, blocked.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenRecoverableOutcomePrimarySaveIsBlocked_NewClientUsesJournalAndRetries()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string stateFilePath = Path.Combine(
			temporaryDirectory.Path,
			"state",
			"official-print-safety-v1.json");
		MutableTimeProvider timeProvider = new(ObservedAt);
		JsonAntigravityOfficialPrintSafetyStateStore firstStore = new(
			stateFilePath);
		FileStream? primaryLock = null;
		FakeProcessRunner firstRunner = new((_, _) =>
		{
			primaryLock = new FileStream(
				stateFilePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			return Task.FromResult(
				new AntigravityOfficialPrintProcessResult(
					exitCode: 1,
					Encoding.UTF8.GetBytes(CreateOutput()),
					stdoutLimitExceeded: false,
					hasStderr: false,
					stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient firstClient = CreateClient(
			new FakeCapabilityValidator(isSupported: true),
			firstRunner,
			firstStore,
			timeProvider);

		try
		{
			AntigravityProductionUsageResult first = await firstClient.CaptureAsync(
				executablePath,
				CancellationToken.None);

			Assert.Equal(
				AntigravityUsageSafetyFailureReason.NonZeroExit,
				first.SafetyFailureReason);
			Assert.True(first.AutomaticRevalidationPending);
			Assert.True(File.Exists(stateFilePath + ".journal"));
			Assert.Contains(
				"\"reason\": \"AttemptInterrupted\"",
				await File.ReadAllTextAsync(stateFilePath),
				StringComparison.Ordinal);
		}
		finally
		{
			if (primaryLock is not null)
			{
				await primaryLock.DisposeAsync();
			}
		}

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		JsonAntigravityOfficialPrintSafetyStateStore restartedStore = new(
			stateFilePath);
		FakeCapabilityValidator restartedValidator = new(isSupported: true);
		FakeProcessRunner restartedRunner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient restartedClient = CreateClient(
			restartedValidator,
			restartedRunner,
			restartedStore,
			timeProvider);

		AntigravityProductionUsageResult recovered =
			await restartedClient.CaptureAsync(
				executablePath,
				CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Equal(1, restartedValidator.CallCount);
		Assert.Equal(1, restartedRunner.CallCount);
		Assert.Null(await new
			JsonAntigravityOfficialPrintSafetyStateStore(stateFilePath)
			.LoadAsync(CancellationToken.None));
	}

	[Fact]
	public async Task CaptureAsync_WhenSuccessfulClearPrimaryDeleteIsBlocked_NewClientUsesClearJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string stateFilePath = Path.Combine(
			temporaryDirectory.Path,
			"state",
			"official-print-safety-v1.json");
		JsonAntigravityOfficialPrintSafetyStateStore firstStore = new(
			stateFilePath);
		FileStream? primaryLock = null;
		FakeProcessRunner firstRunner = new((_, _) =>
		{
			primaryLock = new FileStream(
				stateFilePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			return Task.FromResult(
				new AntigravityOfficialPrintProcessResult(
					exitCode: 0,
					Encoding.UTF8.GetBytes(CreateOutput()),
					stdoutLimitExceeded: false,
					hasStderr: false,
					stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient firstClient = CreateClient(
			new FakeCapabilityValidator(isSupported: true),
			firstRunner,
			firstStore);

		try
		{
			AntigravityProductionUsageResult first = await firstClient.CaptureAsync(
				executablePath,
				CancellationToken.None);

			Assert.True(first.IsSuccessful);
			Assert.True(File.Exists(stateFilePath));
			Assert.True(File.Exists(stateFilePath + ".journal"));
			Assert.Null(await new
				JsonAntigravityOfficialPrintSafetyStateStore(stateFilePath)
				.LoadAsync(CancellationToken.None));
		}
		finally
		{
			if (primaryLock is not null)
			{
				await primaryLock.DisposeAsync();
			}
		}

		FakeCapabilityValidator restartedValidator = new(isSupported: true);
		FakeProcessRunner restartedRunner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient restartedClient = CreateClient(
			restartedValidator,
			restartedRunner,
			new JsonAntigravityOfficialPrintSafetyStateStore(stateFilePath));

		AntigravityProductionUsageResult restarted =
			await restartedClient.CaptureAsync(
				executablePath,
				CancellationToken.None);

		Assert.True(restarted.IsSuccessful);
		Assert.Equal(1, restartedValidator.CallCount);
		Assert.Equal(1, restartedRunner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithMalformedDurableJournal_FailsClosedWithoutRunningProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string stateFilePath = Path.Combine(
			temporaryDirectory.Path,
			"state",
			"official-print-safety-v1.json");
		JsonAntigravityOfficialPrintSafetyStateStore seedStore = new(
			stateFilePath);
		await seedStore.SaveAsync(
			new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt,
				AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46"),
			CancellationToken.None);
		await File.WriteAllTextAsync(
			stateFilePath + ".journal",
			"{\"schemaVersion\":1,");
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException(
				"A malformed journal must prevent process execution."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			new JsonAntigravityOfficialPrintSafetyStateStore(stateFilePath));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.Unknown,
			result.SafetyFailureReason);
		Assert.False(result.AutomaticRevalidationPending);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Theory]
	[InlineData(AntigravityUsageSafetyFailureReason.NonZeroExit)]
	[InlineData(AntigravityUsageSafetyFailureReason.EmptyOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.StdoutTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.StderrTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.UnverifiableOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputJsonInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer)]
	public async Task CaptureAsync_WithExhaustedSchemaV3CompletedOutput_MigratesToAutomaticRetry(
		AntigravityUsageSafetyFailureReason reason)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				reason,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: false,
				HasAttemptedAutomaticRevalidation: true,
				AttemptId: "f15500424ca84361a082ef8b4ecfdb36")
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenNonZeroExitRetryIsCanceledBeforeStart_PreservesRecoveryEpisode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		MutableTimeProvider timeProvider = new(ObservedAt);
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "d15500424ca84361a082ef8b4ecfdb34");
		FakeSafetyStateStore store = new() { State = originalState };
		FakeCapabilityValidator firstValidator = new(isSupported: true);
		FakeProcessRunner canceledRunner = new((_, cancellationToken) =>
		{
			cancellationSource.Cancel();
			return Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: false,
					new OperationCanceledException(cancellationToken),
					wasTerminationConfirmed: false));
		});
		AntigravityOfficialPrintUsageClient firstClient = CreateClient(
			firstValidator,
			canceledRunner,
			store,
			timeProvider);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			firstClient.CaptureAsync(
				executablePath,
				cancellationSource.Token));

		Assert.NotNull(store.State);
		Assert.Equal(originalState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(originalState.AttemptId, store.State.AttemptId);
		Assert.Equal(1, canceledRunner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;

		FakeCapabilityValidator restartedValidator = new(isSupported: true);
		FakeProcessRunner repeatedRunner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 1,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));
		AntigravityOfficialPrintUsageClient restartedClient = CreateClient(
			restartedValidator,
			repeatedRunner,
			store,
			timeProvider);

		AntigravityProductionUsageResult repeated =
			await restartedClient.CaptureAsync(
				executablePath,
				CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await restartedClient.CaptureAsync(
				executablePath,
				CancellationToken.None);

		Assert.True(repeated.AutomaticRevalidationPending);
		Assert.True(blocked.AutomaticRevalidationPending);
		Assert.NotNull(store.State);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, restartedValidator.CallCount);
		Assert.Equal(1, repeatedRunner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenNonZeroExitRetryTimesOut_ContinuesUntilSuccess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			int currentRun = Interlocked.Increment(ref runCount);
			if (currentRun == 1)
			{
				return Task.FromResult(
					new AntigravityOfficialPrintProcessResult(
						exitCode: 1,
						Encoding.UTF8.GetBytes(CreateOutput()),
						stdoutLimitExceeded: false,
						hasStderr: false,
						stderrLimitExceeded: false));
			}

			if (currentRun == 2)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new TimeoutException("Synthetic timeout."),
						wasTerminationConfirmed: true));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult timedOut =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(timedOut.AutomaticRevalidationPending);
		Assert.True(blocked.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			timedOut.SafetyFailureReason);
		Assert.Equal(timedOut.SafetyFailureReason, blocked.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(3, validator.CallCount);
		Assert.Equal(3, runner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WhenNonZeroExitRepeats_RearmsAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.NonZeroExit,
				ObservedAt - TimeSpan.FromMinutes(2),
				AttemptId: "e15500424ca84361a082ef8b4ecfdb35")
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 1,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		int startedCount = 0;

		AntigravityProductionUsageResult repeated =
			await client.RevalidateAsync(
				executablePath,
				() => startedCount++,
				CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(repeated.AutomaticRevalidationPending);
		Assert.True(blocked.AutomaticRevalidationPending);
		Assert.Equal(1, startedCount);
		Assert.NotNull(store.State);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			store.State.Reason);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchWrappedPreStartFailure_RestoresOriginalMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			ObservedAt - TimeSpan.FromMinutes(5),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "f15500424ca84361a082ef8b4ecfdb36");
		FakeSafetyStateStore store = new() { State = originalState };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: false,
					new IOException("Synthetic wrapped pre-start failure."))));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		int startedCount = 0;

		AntigravityProductionUsageResult revalidationFailure =
			await client.RevalidateAsync(
				executablePath,
				() => startedCount++,
				CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.CaptureRejected,
			revalidationFailure.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			blocked.FailureKind);
		Assert.Equal(originalState.Reason, blocked.SafetyFailureReason);
		Assert.False(blocked.AutomaticRevalidationPending);
		Assert.Equal(originalState, store.State);
		Assert.Equal(1, startedCount);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchRawPreStartCancellation_RestoresOriginalMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		using CancellationTokenSource cancellationSource = new();
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			ObservedAt - TimeSpan.FromMinutes(5),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "f15500424ca84361a082ef8b4ecfdb36");
		FakeSafetyStateStore store = new() { State = originalState };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			cancellationSource.Cancel();
			return Task.FromException<AntigravityOfficialPrintProcessResult>(
				new OperationCanceledException(cancellationToken));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		int startedCount = 0;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.RevalidateAsync(
				executablePath,
				() => startedCount++,
				cancellationSource.Token));
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			blocked.FailureKind);
		Assert.Equal(originalState.Reason, blocked.SafetyFailureReason);
		Assert.False(blocked.AutomaticRevalidationPending);
		Assert.Equal(originalState, store.State);
		Assert.Equal(1, startedCount);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchConfirmedTimeout_PreservesExactOriginalMarker()
	{
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic confirmed timeout."),
					wasTerminationConfirmed: true)));

		await AssertHardUsageActivityLatchPreservedAfterStartedFailureAsync(
			runner,
			CancellationToken.None,
			expectCancellation: false);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchStartedCancellation_PreservesExactOriginalMarker()
	{
		using CancellationTokenSource cancellationSource = new();
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			cancellationSource.Cancel();
			return Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new OperationCanceledException(cancellationToken),
					wasTerminationConfirmed: true));
		});

		await AssertHardUsageActivityLatchPreservedAfterStartedFailureAsync(
			runner,
			cancellationSource.Token,
			expectCancellation: true);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchNonZeroExit_PreservesExactOriginalMarker()
	{
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 1,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));

		await AssertHardUsageActivityLatchPreservedAfterStartedFailureAsync(
			runner,
			CancellationToken.None,
			expectCancellation: false);
	}

	[Fact]
	public async Task RevalidateAsync_WhenHardLatchInvalidOutput_PreservesExactOriginalMarker()
	{
		FakeProcessRunner runner = CreateOutputRunner(() =>
		{
			string output = CreateOutput();
			return output[..^8];
		});

		await AssertHardUsageActivityLatchPreservedAfterStartedFailureAsync(
			runner,
			CancellationToken.None,
			expectCancellation: false);
	}

	[Fact]
	public async Task CaptureAsync_WithEmptyOutput_ClassifiesReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) => Task.FromResult(
			new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Array.Empty<byte>(),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.EmptyOutput,
			result.SafetyFailureReason);
		Assert.Equal(
			"1.1.12",
			result.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.True(result.AutomaticRevalidationPending);
	}

	[Fact]
	public async Task CaptureAsync_WhenStartedProcessTimesOut_ClassifiesReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic timeout."),
					wasTerminationConfirmed: true)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			result.SafetyFailureReason);
		Assert.Null(result.OfficialPrintVersionAssessment);
		Assert.True(result.AutomaticRevalidationPending);
	}

	[Fact]
	public async Task CaptureAsync_WhileTimeoutTerminationRemainsUnconfirmed_DoesNotAutoRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		TaskCompletionSource positiveQuiescence = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic timeout."),
					wasTerminationConfirmed: false,
					quiescenceTask: positiveQuiescence.Task)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		try
		{
			AntigravityProductionUsageResult first =
				await client.CaptureAsync(executablePath, CancellationToken.None);
			AntigravityProductionUsageResult second =
				await client.CaptureAsync(executablePath, CancellationToken.None);

			Assert.Equal(
				AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart,
				first.SafetyFailureReason);
			Assert.False(first.AutomaticRevalidationPending);
			Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
			Assert.Equal(1, validator.CallCount);
			Assert.Equal(1, runner.CallCount);
		}
		finally
		{
			positiveQuiescence.TrySetResult();
			await WaitForExclusiveReadAsync(
				executablePath,
				TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task CaptureAsync_WithRecoverableTimeout_WaitsThenRetriesAfterCooldown()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			if (Interlocked.Increment(ref runCount) == 1)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new TimeoutException("Synthetic timeout."),
						wasTerminationConfirmed: true));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		AntigravityProductionUsageResult timedOut =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult coolingDown =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(timedOut.AutomaticRevalidationPending);
		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(2, runner.CallCount);
		Assert.Equal(5, store.SaveCallCount);
		Assert.Equal(1, store.ClearCallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithPreparedInterruptedMarkerBeforeCooldown_DoesNotRecoverOrRun()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		const string interruptedAttemptId =
			"8f6266fdd8fb4e66ae0f8973b03b9c46";
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			ObservedAt,
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: interruptedAttemptId);
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, _) => throw new InvalidOperationException(
				"The process must not start during cooldown."),
			(_, _) => Task.FromResult(true));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			new FixedTimeProvider(ObservedAt));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			result.SafetyFailureReason);
		Assert.True(result.AutomaticRevalidationPending);
		Assert.Equal(pendingState, store.State);
		Assert.Equal(0, runner.RecoveryCallCount);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithDuePreparedInterruptedMarker_RecoversThenRunsAtStartedBoundary()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		const string interruptedAttemptId =
			"8f6266fdd8fb4e66ae0f8973b03b9c46";
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true,
				HasAttemptedAutomaticRevalidation: false,
				AttemptId: interruptedAttemptId)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.NotNull(store.State);
				Assert.Equal(
					AntigravityUsageSafetyFailureReason.AttemptInterrupted,
					store.State.Reason);
				Assert.False(store.State.AutomaticRevalidationAllowed);
				return Task.FromResult(
					new AntigravityOfficialPrintProcessResult(
						exitCode: 0,
						Encoding.UTF8.GetBytes(CreateOutput()),
						stdoutLimitExceeded: false,
						hasStderr: false,
						stderrLimitExceeded: false));
			},
			(attemptId, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(interruptedAttemptId, attemptId);
				return Task.FromResult(true);
			});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			new FixedTimeProvider(ObservedAt));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, runner.RecoveryCallCount);
		Assert.Equal(interruptedAttemptId, runner.LastRecoveryAttemptId);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
		Assert.Collection(
			store.SavedStates,
			prepared =>
			{
				Assert.Equal(
					AntigravityUsageSafetyFailureReason.AttemptInterrupted,
					prepared.Reason);
				Assert.True(prepared.AutomaticRevalidationAllowed);
				Assert.NotNull(prepared.AttemptId);
				Assert.NotEqual(interruptedAttemptId, prepared.AttemptId);
			},
			started =>
			{
				Assert.Equal(
					AntigravityUsageSafetyFailureReason.AttemptInterrupted,
					started.Reason);
				Assert.False(started.AutomaticRevalidationAllowed);
				Assert.Equal(
					store.SavedStates[0].AttemptId,
					started.AttemptId);
			});
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CaptureAsync_WhenPreparedInterruptedRecoveryDoesNotComplete_ReschedulesWithoutRunning(
		bool throwDuringRecovery)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		const string interruptedAttemptId =
			"8f6266fdd8fb4e66ae0f8973b03b9c46";
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true,
				HasAttemptedAutomaticRevalidation: false,
				AttemptId: interruptedAttemptId)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, _) => throw new InvalidOperationException(
				"The process must not start after failed recovery."),
			(_, _) => throwDuringRecovery
				? Task.FromException<bool>(
					new IOException("Synthetic interrupted-attempt recovery failure."))
				: Task.FromResult(false));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			new FixedTimeProvider(ObservedAt));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			result.SafetyFailureReason);
		Assert.True(result.AutomaticRevalidationPending);
		Assert.NotNull(store.State);
		Assert.Equal(interruptedAttemptId, store.State.AttemptId);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.Equal(1, runner.RecoveryCallCount);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CaptureAsync_WithStartedInterruptedMarkerAndUnconfirmedRecovery_RemainsManual(
		bool throwDuringRecovery)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		const string interruptedAttemptId =
			"8f6266fdd8fb4e66ae0f8973b03b9c46";
		AntigravityOfficialPrintSafetyState manualState = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: interruptedAttemptId);
		FakeSafetyStateStore store = new() { State = manualState };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, _) => throw new InvalidOperationException(
				"The process must not start without confirmed recovery."),
			(_, _) => throwDuringRecovery
				? Task.FromException<bool>(
					new IOException("Synthetic interrupted-attempt recovery failure."))
				: Task.FromResult(false));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			new FixedTimeProvider(ObservedAt));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			result.SafetyFailureReason);
		Assert.False(result.AutomaticRevalidationPending);
		Assert.Equal(manualState, store.State);
		Assert.Equal(1, runner.RecoveryCallCount);
		Assert.Equal(interruptedAttemptId, runner.LastRecoveryAttemptId);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithStartedPrimaryAndNoRecoveryJournal_RecoversAcrossClientRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		const string interruptedAttemptId =
			"8f6266fdd8fb4e66ae0f8973b03b9c46";
		string stateFilePath = Path.Combine(
			temporaryDirectory.Path,
			"state",
			"official-print-safety-v1.json");
		JsonAntigravityOfficialPrintSafetyStateStore seedStore = new(
			stateFilePath);
		await seedStore.SaveAsync(
			new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: false,
				HasAttemptedAutomaticRevalidation: true,
				AttemptId: interruptedAttemptId),
			CancellationToken.None);
		string journalFilePath = stateFilePath + ".journal";

		if (File.Exists(journalFilePath))
		{
			File.Delete(journalFilePath);
		}

		MutableTimeProvider timeProvider = new(ObservedAt);
		JsonAntigravityOfficialPrintSafetyStateStore recoveryStore = new(
			stateFilePath);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, _) => Task.FromResult(
				new AntigravityOfficialPrintProcessResult(
					exitCode: 0,
					Encoding.UTF8.GetBytes(CreateOutput()),
					stdoutLimitExceeded: false,
					hasStderr: false,
					stderrLimitExceeded: false)),
			(attemptId, cancellationToken) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Assert.Equal(interruptedAttemptId, attemptId);
				return Task.FromResult(true);
			});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			recoveryStore,
			timeProvider);

		AntigravityProductionUsageResult pending = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);
		AntigravityOfficialPrintSafetyState? promotedState =
			await recoveryStore.LoadAsync(CancellationToken.None);
		timeProvider.UtcNow += TimeSpan.FromMinutes(2);
		AntigravityProductionUsageResult recovered = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			pending.SafetyFailureReason);
		Assert.True(pending.AutomaticRevalidationPending);
		Assert.NotNull(promotedState);
		Assert.True(promotedState.AutomaticRevalidationAllowed);
		Assert.False(promotedState.HasAttemptedAutomaticRevalidation);
		Assert.Equal(interruptedAttemptId, promotedState.AttemptId);
		Assert.Equal(ObservedAt, promotedState.LatchedAtUtc);
		Assert.True(recovered.IsSuccessful);
		Assert.Null(await recoveryStore.LoadAsync(CancellationToken.None));
		Assert.Equal(2, runner.RecoveryCallCount);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenStartedInterruptedRecoveryCannotBePersisted_DoesNotRun()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState manualState = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46");
		FakeSafetyStateStore store = new()
		{
			State = manualState,
			ThrowOnSave = true
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new(
			(_, _) => throw new InvalidOperationException(
				"The process must not start without a durable promotion."),
			(_, _) => Task.FromResult(true));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			new FixedTimeProvider(ObservedAt));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			executablePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
			result.SafetyFailureReason);
		Assert.True(result.AutomaticRevalidationPending);
		Assert.Equal(manualState, store.State);
		Assert.Equal(1, store.SaveCallCount);
		Assert.Equal(1, runner.RecoveryCallCount);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryTimesOutAgain_SchedulesAnotherRetryAcrossClientRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator firstValidator = new(isSupported: true);
		FakeProcessRunner firstRunner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic timeout."),
					wasTerminationConfirmed: true)));
		AntigravityOfficialPrintUsageClient firstClient = CreateClient(
			firstValidator,
			firstRunner,
			store,
			timeProvider);

		AntigravityProductionUsageResult first =
			await firstClient.CaptureAsync(executablePath, CancellationToken.None);
		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult automaticRetry =
			await firstClient.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(automaticRetry.AutomaticRevalidationPending);
		Assert.NotNull(store.State);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			store.State.Reason);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);

		FakeCapabilityValidator secondValidator = new(isSupported: true);
		FakeProcessRunner secondRunner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient secondClient = CreateClient(
			secondValidator,
			secondRunner,
			store,
			timeProvider);

		AntigravityProductionUsageResult coolingDown =
			await secondClient.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(coolingDown.AutomaticRevalidationPending);
		Assert.Equal(0, secondValidator.CallCount);
		Assert.Equal(0, secondRunner.CallCount);
		Assert.Equal(2, firstValidator.CallCount);
		Assert.Equal(2, firstRunner.CallCount);

		timeProvider.UtcNow +=
			AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		AntigravityProductionUsageResult recovered =
			await secondClient.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, secondValidator.CallCount);
		Assert.Equal(1, secondRunner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithExhaustedSchemaV3Timeout_MigratesToAutomaticRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: false,
				HasAttemptedAutomaticRevalidation: true,
				AttemptId: "3f17be1300b24e5e8c49649e4df99b64")
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithLegacyExhaustedV2Timeout_ResumesAutomaticRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true,
				HasAttemptedAutomaticRevalidation: true)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
		Assert.Contains(
			store.SavedStates,
			state =>
				state.Reason ==
					AntigravityUsageSafetyFailureReason.AttemptInterrupted &&
				!state.AutomaticRevalidationAllowed &&
				state.HasAttemptedAutomaticRevalidation);
	}

	[Fact]
	public async Task CaptureAsync_WithRepeatedConfirmedTimeouts_ContinuesAutomaticallyUntilSuccess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		MutableTimeProvider timeProvider = new(ObservedAt);
		FakeSafetyStateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		int runCount = 0;
		FakeProcessRunner runner = new((_, _) =>
		{
			if (Interlocked.Increment(ref runCount) <= 3)
			{
				return Task.FromException<AntigravityOfficialPrintProcessResult>(
					new AntigravityOfficialPrintProcessRunException(
						wasProcessStarted: true,
						new TimeoutException("Synthetic timeout."),
						wasTerminationConfirmed: true));
			}

			return Task.FromResult(new AntigravityOfficialPrintProcessResult(
				exitCode: 0,
				Encoding.UTF8.GetBytes(CreateOutput()),
				stdoutLimitExceeded: false,
				hasStderr: false,
				stderrLimitExceeded: false));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			timeProvider);

		for (int attempt = 0; attempt < 3; attempt++)
		{
			AntigravityProductionUsageResult timedOut =
				await client.CaptureAsync(executablePath, CancellationToken.None);

			Assert.True(timedOut.AutomaticRevalidationPending);
			Assert.NotNull(store.State);
			Assert.Equal(
				AntigravityUsageSafetyFailureReason.TimedOut,
				store.State.Reason);
			Assert.True(store.State.AutomaticRevalidationAllowed);
			Assert.False(store.State.HasAttemptedAutomaticRevalidation);
			timeProvider.UtcNow +=
				AntigravityOfficialPrintTiming.AutomaticRevalidationDelay;
		}

		AntigravityProductionUsageResult recovered =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(recovered.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(4, validator.CallCount);
		Assert.Equal(4, runner.CallCount);
		Assert.Equal(
			4,
			store.SavedStates.Count(state =>
				state.Reason ==
					AntigravityUsageSafetyFailureReason.AttemptInterrupted &&
				!state.AutomaticRevalidationAllowed));
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryMarkerSaveFails_RetriesStoreWithoutRunningProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true),
			ThrowOnSave = true
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
			result.SafetyFailureReason);
		Assert.True(result.AutomaticRevalidationPending);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryPreflightIsUnsupported_ReturnsProvenanceFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState pendingState = new(
			AntigravityUsageSafetyFailureReason.TimedOut,
			ObservedAt - TimeSpan.FromMinutes(2),
			AutomaticRevalidationAllowed: true);
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeCapabilityValidator validator = new(isSupported: false);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.False(result.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.None,
			result.SafetyFailureReason);
		Assert.Null(store.State);
		Assert.Equal(1, store.SaveCallCount);
		Assert.Equal(1, store.ClearCallCount);
		Assert.Equal(0, runner.CallCount);

		AntigravityProductionUsageResult repeated =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			repeated.FailureKind);
		Assert.Equal(2, validator.CallCount);
		Assert.Equal(1, store.SaveCallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryPreflightThrows_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState pendingState = new(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true);
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		int validationCount = 0;
		AntigravityOfficialPrintUsageClient client = new(
			(_, _) =>
			{
				validationCount++;
				throw new IOException("Synthetic capability failure.");
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			result.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, validationCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenAutomaticRetryPreflightReturnsNull_RestoresPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState pendingState = new(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt - TimeSpan.FromMinutes(2),
				AutomaticRevalidationAllowed: true);
		FakeSafetyStateStore store = new() { State = pendingState };
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		int validationCount = 0;
		AntigravityOfficialPrintUsageClient client = new(
			(_, _) =>
			{
				validationCount++;
				return Task.FromResult<
					AntigravityOfficialPrintCapabilityValidationResult>(null!);
			},
			runner,
			new FixedTimeProvider(ObservedAt),
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(result.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			result.SafetyFailureReason);
		Assert.NotNull(store.State);
		Assert.Equal(pendingState.Reason, store.State.Reason);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, validationCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithFutureAutomaticRetryTimestamp_RebasesPendingRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.TimedOut,
				ObservedAt + TimeSpan.FromDays(1),
				AutomaticRevalidationAllowed: true)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.True(first.AutomaticRevalidationPending);
		Assert.True(second.AutomaticRevalidationPending);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			first.SafetyFailureReason);
		Assert.Equal(ObservedAt, store.State!.LatchedAtUtc);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, store.SaveCallCount);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithFutureVersionSensitiveRetry_PreservesVersionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
				ObservedAt + TimeSpan.FromDays(1),
				AutomaticRevalidationAllowed: true,
				HasAttemptedAutomaticRevalidation: false,
				AttemptId: "6f6266fdd8fb4e66ae0f8973b03b9c44",
				DetectedCliVersion: "1.1.12")
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult second =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			"1.1.12",
			first.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.Equal(
			"1.1.12",
			second.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.Equal("1.1.12", store.State!.DetectedCliVersion);
		Assert.Equal(ObservedAt, store.State.LatchedAtUtc);
		Assert.Equal(1, store.SaveCallCount);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WithReferenceMetadataLatch_DoesNotAttachDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
				ObservedAt,
				AutomaticRevalidationAllowed: true,
				HasAttemptedAutomaticRevalidation: false,
				AttemptId: "6f6266fdd8fb4e66ae0f8973b03b9c44",
				DetectedCliVersion: "1.1.11")
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
			result.SafetyFailureReason);
		Assert.Null(result.OfficialPrintVersionAssessment);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WhenManualRetryTimesOut_SchedulesBackgroundRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			Task.FromException<AntigravityOfficialPrintProcessResult>(
				new AntigravityOfficialPrintProcessRunException(
					wasProcessStarted: true,
					new TimeoutException("Synthetic timeout."),
					wasTerminationConfirmed: true)));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		int startedCount = 0;

		AntigravityProductionUsageResult result = await client.RevalidateAsync(
			executablePath,
			() => startedCount++,
			CancellationToken.None);

		Assert.True(result.AutomaticRevalidationPending);
		Assert.NotNull(store.State);
		Assert.True(store.State.AutomaticRevalidationAllowed);
		Assert.False(store.State.HasAttemptedAutomaticRevalidation);
		Assert.Equal(1, startedCount);
	}

	[Fact]
	public async Task CaptureAsync_WithPersistentMarker_BlocksNewClientBeforeValidationOrRun()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string stateFilePath = Path.Combine(
			temporaryDirectory.Path,
			"state",
			"official-print-safety-v1.json");
		JsonAntigravityOfficialPrintSafetyStateStore firstStore = new(
			stateFilePath);
		FakeCapabilityValidator firstValidator = new(isSupported: true);
		FakeProcessRunner firstRunner = CreateOutputRunner(() => "{}\n{}\n");
		AntigravityOfficialPrintUsageClient firstClient = CreateClient(
			firstValidator,
			firstRunner,
			firstStore);

		AntigravityProductionUsageResult first =
			await firstClient.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid,
			first.SafetyFailureReason);
		AntigravityOfficialPrintSafetyState? persistedState =
			await firstStore.LoadAsync(CancellationToken.None);
		Assert.NotNull(persistedState);
		Assert.Equal("1.1.12", persistedState.DetectedCliVersion);

		FakeCapabilityValidator secondValidator = new(isSupported: true);
		FakeProcessRunner secondRunner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient secondClient = CreateClient(
			secondValidator,
			secondRunner,
			new JsonAntigravityOfficialPrintSafetyStateStore(stateFilePath));

		AntigravityProductionUsageResult second =
			await secondClient.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(first.SafetyFailureReason, second.SafetyFailureReason);
		Assert.Equal(
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed,
			second.OfficialPrintVersionAssessment?.Kind);
		Assert.Equal(
			"1.1.12",
			second.OfficialPrintVersionAssessment?.DetectedVersion);
		Assert.Equal(0, secondValidator.CallCount);
		Assert.Equal(0, secondRunner.CallCount);
	}

	[Fact]
	public async Task RevalidateAsync_WithPersistentMarker_RunsOnceAndSuccessClearsMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new()
		{
			State = new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				ObservedAt)
		};
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = CreateOutputRunner(() => CreateOutput());
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult revalidated =
			await client.RevalidateAsync(
				executablePath,
				() => { },
				CancellationToken.None);

		Assert.Equal(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			blocked.SafetyFailureReason);
		Assert.True(revalidated.IsSuccessful);
		Assert.Null(store.State);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
		Assert.Equal(2, store.SaveCallCount);
		Assert.Equal(1, store.ClearCallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenSafetyStateSaveFails_DoesNotStartProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeSafetyStateStore store = new() { ThrowOnSave = true };
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
			result.SafetyFailureReason);
		Assert.Null(result.OfficialPrintVersionAssessment);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, store.SaveCallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenExecutionGateContentionTimesOut_ReportsExecutionBusy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		SerializingSafetyStateStore store = new();
		using IDisposable heldLease =
			await store.AcquireExecutionLeaseAsync(CancellationToken.None);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store,
			executionGateTimeout: TimeSpan.FromMilliseconds(50));

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.ExecutionBusy,
			result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.None,
			result.SafetyFailureReason);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenExecutionGateIsUnavailable_FailsClosedWithoutBusyRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		UnavailableExecutionGateStore store = new();
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, _) =>
			throw new InvalidOperationException("The process must not start."));
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);

		AntigravityProductionUsageResult result =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable,
			result.SafetyFailureReason);
		Assert.Equal(0, validator.CallCount);
		Assert.Equal(0, runner.CallCount);
	}

	private static string CopyTerminalFixture(
		string destinationDirectory,
		string executableFileName)
	{
		Assembly fixtureAssembly = Assembly.Load(
			new AssemblyName("AiUsageDashboard.TerminalFixture"));
		string sourceDirectory = Path.GetDirectoryName(fixtureAssembly.Location) ??
			throw new InvalidOperationException(
				"The terminal fixture output directory is unavailable.");
		string sourceExecutablePath = Path.ChangeExtension(
			fixtureAssembly.Location,
			".exe");

		foreach (string sourcePath in Directory.EnumerateFiles(
			sourceDirectory,
			"AiUsageDashboard.TerminalFixture.*",
			SearchOption.TopDirectoryOnly))
		{
			string destinationPath = Path.Combine(
				destinationDirectory,
				Path.GetFileName(sourcePath));
			File.Copy(sourcePath, destinationPath, overwrite: true);
		}

		string executablePath = Path.Combine(
			destinationDirectory,
			executableFileName);
		File.Copy(sourceExecutablePath, executablePath, overwrite: true);
		return executablePath;
	}

	private static (int RootProcessId, int ChildProcessId)
		ParseRunnerProcessIds(string processIdPath)
	{
		string[] parts = File.ReadAllText(processIdPath).Split(';');

		if ((parts.Length != 2) ||
			!int.TryParse(parts[0], out int rootProcessId) ||
			!int.TryParse(parts[1], out int childProcessId))
		{
			throw new InvalidDataException(
				"The official runner fixture process IDs were malformed.");
		}

		return (rootProcessId, childProcessId);
	}

	private static async Task WaitUntilAsync(
		Func<bool> condition,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (!condition())
		{
			await Task.Delay(
				TimeSpan.FromMilliseconds(10),
				timeoutSource.Token);
		}
	}

	private static async Task WaitForFileAsync(
		string path,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (!File.Exists(path))
		{
			await Task.Delay(
				TimeSpan.FromMilliseconds(20),
				timeoutSource.Token);
		}
	}

	private static async Task WaitForFileOrRunCompletionAsync(
		string path,
		Task<AntigravityOfficialPrintProcessResult> run,
		TimeSpan timeout)
	{
		Task file = WaitForFileAsync(path, timeout);
		Task completed = await Task.WhenAny(file, run);

		if (ReferenceEquals(completed, run))
		{
			using AntigravityOfficialPrintProcessResult _ = await run;
			throw new InvalidOperationException(
				"The official runner completed before its fixture marker appeared.");
		}

		await file;
	}

	private static async Task WaitForFileOrProcessExitAsync(
		string path,
		Process process,
		Task<string> errorTask,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (!File.Exists(path))
		{
			if (process.HasExited)
			{
				string error = await errorTask.WaitAsync(timeout);
				throw new InvalidOperationException(
					"The independent crash host exited before its fixture marker " +
					$"appeared. ExitCode={process.ExitCode}; stderr={error}");
			}

			await Task.Delay(
				TimeSpan.FromMilliseconds(20),
				timeoutSource.Token);
		}
	}

	private static bool TryOpenAndDisposeJob(string jobName)
	{
		bool exists = WindowsProcessJob.TryOpenExisting(
			jobName,
			out WindowsProcessJob? job);
		job?.Dispose();
		return exists;
	}

	private static Process? TryGetProcessById(int processId)
	{
		try
		{
			return Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	private static bool TryReadProcessId(string path, out int processId)
	{
		processId = 0;
		return File.Exists(path) &&
			int.TryParse(
				File.ReadAllText(path),
				out processId) &&
			processId > 0;
	}

	private static async Task TerminateFixtureProcessAsync(
		Process? process,
		bool entireProcessTree,
		TimeSpan timeout)
	{
		if (process is null || process.HasExited)
		{
			return;
		}

		process.Kill(entireProcessTree);
		await process.WaitForExitAsync().WaitAsync(timeout);
	}

	private static async Task WaitForExclusiveReadAsync(
		string path,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (true)
		{
			try
			{
				using FileStream stream = new(
					path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.None);
				return;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(
					TimeSpan.FromMilliseconds(20),
					timeoutSource.Token);
			}
		}
	}

	private static async Task DeleteFixtureImageAfterReleaseAsync(
		string path,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (true)
		{
			try
			{
				File.Delete(path);
				return;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(
					TimeSpan.FromMilliseconds(20),
					timeoutSource.Token);
			}
		}
	}

	private static async Task DeleteTemporaryFixtureDirectoryAsync(
		string path,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (true)
		{
			if (!Directory.Exists(path))
			{
				// A just-exited Windows image can briefly make a delete-pending
				// directory look absent before the final handle state settles.
				await Task.Delay(
					TimeSpan.FromMilliseconds(500),
					timeoutSource.Token);

				if (!Directory.Exists(path))
				{
					return;
				}
			}

			try
			{
				Directory.Delete(path, recursive: true);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(
					TimeSpan.FromMilliseconds(20),
					timeoutSource.Token);
			}
		}
	}

	private static async Task AssertCompletedUsageActivityTakesPriorityAsync(
		int exitCode,
		bool hasStderr,
		bool stderrLimitExceeded)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeCapabilityValidator validator = new(isSupported: true);
		FakeProcessRunner runner = new((_, cancellationToken) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(
				new AntigravityOfficialPrintProcessResult(
					exitCode,
					Encoding.UTF8.GetBytes(CreateOutput(totalTokens: 1)),
					stdoutLimitExceeded: false,
					hasStderr,
					stderrLimitExceeded));
		});
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner);

		AntigravityProductionUsageResult first =
			await client.CaptureAsync(executablePath, CancellationToken.None);
		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			blocked.FailureKind);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			first.SafetyFailureReason);
		Assert.Null(first.OfficialPrintVersionAssessment);
		Assert.Null(blocked.OfficialPrintVersionAssessment);
		Assert.Equal(first.SafetyFailureReason, blocked.SafetyFailureReason);
		Assert.False(first.AutomaticRevalidationPending);
		Assert.False(blocked.AutomaticRevalidationPending);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	private static async Task
		AssertHardUsageActivityLatchPreservedAfterStartedFailureAsync(
			FakeProcessRunner runner,
			CancellationToken cancellationToken,
			bool expectCancellation)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.UsageActivityDetected,
			ObservedAt - TimeSpan.FromMinutes(7),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "a15500424ca84361a082ef8b4ecfdb31");
		FakeSafetyStateStore store = new() { State = originalState };
		FakeCapabilityValidator validator = new(isSupported: true);
		AntigravityOfficialPrintUsageClient client = CreateClient(
			validator,
			runner,
			store);
		int startedCount = 0;

		if (expectCancellation)
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				client.RevalidateAsync(
					executablePath,
					() => startedCount++,
					cancellationToken));
		}
		else
		{
			AntigravityProductionUsageResult revalidationFailure =
				await client.RevalidateAsync(
					executablePath,
					() => startedCount++,
					cancellationToken);

			Assert.Equal(
				AntigravityProductionUsageFailureKind.SafetyLatched,
				revalidationFailure.FailureKind);
			Assert.Equal(originalState.Reason, revalidationFailure.SafetyFailureReason);
			Assert.False(revalidationFailure.AutomaticRevalidationPending);
		}

		Assert.Equal(originalState, store.State);

		AntigravityProductionUsageResult blocked =
			await client.CaptureAsync(executablePath, CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			blocked.FailureKind);
		Assert.Equal(originalState.Reason, blocked.SafetyFailureReason);
		Assert.False(blocked.AutomaticRevalidationPending);
		Assert.Equal(originalState, store.State);
		Assert.Equal(1, startedCount);
		Assert.Equal(1, validator.CallCount);
		Assert.Equal(1, runner.CallCount);
	}

	private static AntigravityOfficialPrintUsageClient CreateClient(
		FakeCapabilityValidator validator,
		FakeProcessRunner runner,
		IAntigravityOfficialPrintSafetyStateStore? safetyStateStore = null,
		TimeProvider? timeProvider = null,
		TimeSpan? executionGateTimeout = null)
	{
		return new AntigravityOfficialPrintUsageClient(
			validator.ValidateAsync,
			runner,
			timeProvider ?? new FixedTimeProvider(ObservedAt),
			safetyStateStore,
			executionGateTimeout);
	}

	private static FakeProcessRunner CreateOutputRunner(
		Func<string> createOutput,
		bool hasStderr = false,
		bool stderrLimitExceeded = false,
		bool stdoutLimitExceeded = false)
	{
		return new FakeProcessRunner((_, cancellationToken) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(
				new AntigravityOfficialPrintProcessResult(
					exitCode: 0,
					Encoding.UTF8.GetBytes(createOutput()),
					stdoutLimitExceeded,
					hasStderr,
					stderrLimitExceeded));
		});
	}

	private static string CreateExecutable(
		TemporaryDirectory temporaryDirectory)
	{
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A });
		return Path.GetFullPath(executablePath);
	}

	private static string CreateOutput(long totalTokens = 0)
	{
		JsonObject command = CreateCommand();
		JsonObject commandEvent = new()
		{
			["event"] = "command_result",
			["command"] = command.DeepClone()
		};
		JsonObject resultEvent = new()
		{
			["event"] = "result",
			["result"] = new JsonObject
			{
				["conversation_id"] = string.Empty,
				["status"] = "SUCCESS",
				["response"] = "public usage response",
				["duration_seconds"] = 0.5,
				["num_turns"] = 0,
				["usage"] = new JsonObject
				{
					["input_tokens"] = 0,
					["output_tokens"] = 0,
					["thinking_tokens"] = 0,
					["cache_read_tokens"] = 0,
					["total_tokens"] = totalTokens
				},
				["command"] = command.DeepClone()
			}
		};

		return string.Join(
			'\n',
			commandEvent.ToJsonString(),
			resultEvent.ToJsonString()) + "\n";
	}

	private static JsonObject CreateCommand()
	{
		return new JsonObject
		{
			["name"] = "usage",
			["data"] = new JsonObject
			{
				["description"] = "Usage limits",
				["groups"] = new JsonArray
				{
					CreateGroup(
						"Gemini Models",
						("gemini-weekly", "Weekly Limit Remaining", "weekly", 0.8),
						("gemini-5h", "Five Hour Limit Remaining", "5h", 0.6)),
					CreateGroup(
						"Claude and GPT models",
						("3p-weekly", "Weekly Limit Remaining", "weekly", 0.4),
						("3p-5h", "Five Hour Limit Remaining", "5h", 0.2))
				}
			}
		};
	}

	private static JsonObject CreateGroup(
		string groupName,
		params (string Id, string Name, string Window, double Fraction)[] values)
	{
		return new JsonObject
		{
			["name"] = groupName,
			["description"] = "Group",
			["buckets"] = new JsonArray(values.Select(value =>
				(JsonNode)new JsonObject
				{
					["id"] = value.Id,
					["name"] = value.Name,
					["window"] = value.Window,
					["remaining_fraction"] = value.Fraction,
					["reset_time"] = "2026-08-13T00:00:00Z"
				}).ToArray())
		};
	}
}
