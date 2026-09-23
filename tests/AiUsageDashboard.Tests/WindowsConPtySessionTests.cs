using System.Diagnostics;
using System.Reflection;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

[Trait("Category", "WindowsIntegration")]
public sealed class WindowsConPtySessionTests
{
	private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
	private static readonly UTF8Encoding StrictUtf8Encoding = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);

	[Fact]
	public async Task StartAsync_WithEchoFixture_ExchangesTerminalInputAndOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "echo"));

		try
		{
			await session.WriteInputAsync(
				StrictUtf8Encoding.GetBytes("hello from conpty\r"));
			int exitCode = await session.WaitForExitAsync().WaitAsync(TestTimeout);

			Assert.Equal(0, exitCode);
		}
		finally
		{
			await session.DisposeAsync();
		}

		ConPtyOutputSnapshot snapshot = session.GetOutputSnapshot();
		string output = StrictUtf8Encoding.GetString(snapshot.SavedBytes.Span);
		Assert.Contains("READY", output, StringComparison.Ordinal);
		Assert.Contains("ECHO:hello from conpty", output, StringComparison.Ordinal);
		Assert.False(snapshot.IsSavedByteLimitExceeded);
		Assert.Null(snapshot.ReadFailure);
	}

	[Fact]
	public async Task StartAsync_WhenCreateProcessReturns_IsAlreadyInJob()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const string injectedFailure =
			"Injected failure immediately after ConPTY CreateProcess returned.";
		uint activeProcessCount = 0;
		Process? createdProcess = null;

		try
		{
			InvalidOperationException exception =
				await Assert.ThrowsAsync<InvalidOperationException>(async () =>
					await WindowsConPtySession.StartAsync(
						CreateRequest(temporaryDirectory.Path, "hang"),
						afterProcessCreated: (processId, job) =>
						{
							createdProcess = Process.GetProcessById(
								checked((int)processId));
							_ = createdProcess.Handle;
							activeProcessCount = job.GetActiveProcessCount();
							throw new InvalidOperationException(injectedFailure);
						}));

			Assert.Equal(injectedFailure, exception.Message);
			Assert.Equal(1u, activeProcessCount);
			Assert.NotNull(createdProcess);
			await createdProcess.WaitForExitAsync().WaitAsync(TestTimeout);
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
							TestTimeout);
					}
				}
				finally
				{
					createdProcess.Dispose();
				}
			}
		}
	}

	[Fact]
	public async Task StartAsync_WithRawUsageFixture_AfterInputModeRequestsAcceptsExactBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "raw-usage"));

		try
		{
			await WaitForOutputContainingAsync(session, "READY", TestTimeout);
			await WaitForOutputContainingAsync(
				session,
				"\u001b[?9001h",
				TestTimeout);
			await WaitForOutputContainingAsync(
				session,
				"\u001b[2 q",
				TestTimeout);
			await WaitForOutputContainingAsync(
				session,
				"\u001b[39X",
				TestTimeout);
			await WaitForOutputContainingAsync(
				session,
				"\u001b[>4;2m",
				TestTimeout);
			await WaitForOutputContainingAsync(
				session,
				"\u001b[=1;1u",
				TestTimeout);
			string readyWithInputModeRequests = await WaitForOutputContainingAsync(
				session,
				"\u001b[?u",
				TestTimeout);
			Assert.Contains(
				"READY",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[?9001h",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[2 q",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[39X",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[>4;2m",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[=1;1u",
				readyWithInputModeRequests,
				StringComparison.Ordinal);
			Assert.Contains(
				"\u001b[?u",
				readyWithInputModeRequests,
				StringComparison.Ordinal);

			byte[] usageCommand = StrictUtf8Encoding.GetBytes("/usage\r");
			Assert.Equal(7, usageCommand.Length);
			await session.WriteInputAsync(usageCommand);
			int exitCode = await session.WaitForExitAsync().WaitAsync(TestTimeout);

			Assert.Equal(0, exitCode);
		}
		finally
		{
			await session.DisposeAsync();
		}

		ConPtyOutputSnapshot snapshot = session.GetOutputSnapshot();
		string output = StrictUtf8Encoding.GetString(snapshot.SavedBytes.Span);
		Assert.Contains("RAW-OK", output, StringComparison.Ordinal);
		Assert.False(snapshot.IsSavedByteLimitExceeded);
		Assert.Null(snapshot.ReadFailure);
	}

	[Fact]
	public async Task StartAsync_WithSupportedViewport_DrainsVtOutputAndSupportsSnapshotOffsets()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "vt", columns: 120));

		try
		{
			int exitCode = await session.WaitForExitAsync().WaitAsync(TestTimeout);
			Assert.Equal(0, exitCode);
		}
		finally
		{
			await session.DisposeAsync();
		}

		ConPtyOutputSnapshot fullSnapshot = session.GetOutputSnapshot();
		string output = StrictUtf8Encoding.GetString(fullSnapshot.SavedBytes.Span);
		Assert.Contains("SYNTHETIC AGY MODEL QUOTAS", output, StringComparison.Ordinal);
		Assert.Contains("測試", output, StringComparison.Ordinal);
		Assert.Contains("progress:100%", output, StringComparison.Ordinal);
		Assert.False(fullSnapshot.IsSavedByteLimitExceeded);
		Assert.Null(fullSnapshot.ReadFailure);

		int savedByteOffset = fullSnapshot.SavedByteCount / 2;
		ConPtyOutputSnapshot unreadSnapshot = session.GetOutputSnapshot(
			savedByteOffset);

		Assert.Equal(0, fullSnapshot.SavedByteOffset);
		Assert.Equal(
			fullSnapshot.SavedBytes.Length,
			fullSnapshot.SavedByteCount);
		Assert.Equal(savedByteOffset, unreadSnapshot.SavedByteOffset);
		Assert.Equal(
			fullSnapshot.SavedByteCount,
			unreadSnapshot.SavedByteCount);
		Assert.Equal(
			fullSnapshot.SavedBytes[savedByteOffset..].ToArray(),
			unreadSnapshot.SavedBytes.ToArray());
		Assert.Equal(
			fullSnapshot.TotalBytesRead,
			unreadSnapshot.TotalBytesRead);
		Assert.Equal(
			fullSnapshot.IsSavedByteLimitExceeded,
			unreadSnapshot.IsSavedByteLimitExceeded);
		Assert.Same(fullSnapshot.ReadFailure, unreadSnapshot.ReadFailure);
	}

	[Fact]
	public async Task RedirectedVersionProbe_WithFixture_RendersSingleVisibleVersionLine()
	{
		RedirectedAntigravityCliVersionProbe probe = new();

		string version = await probe.ProbeAsync(
			GetFixtureExecutablePath(),
			CancellationToken.None).WaitAsync(TestTimeout);

		Assert.Equal("fixture-1.2.3", version);
	}

	[Fact]
	public async Task StartAsync_WhenSavedOutputLimitIsExceeded_ContinuesDrainingUntilExit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const int maximumSavedOutputBytes = 4096;
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(
				temporaryDirectory.Path,
				"oversize",
				maximumSavedOutputBytes: maximumSavedOutputBytes));

		try
		{
			int exitCode = await session.WaitForExitAsync().WaitAsync(TestTimeout);
			Assert.Equal(0, exitCode);
		}
		finally
		{
			await session.DisposeAsync();
		}

		ConPtyOutputSnapshot snapshot = session.GetOutputSnapshot();
		Assert.Equal(maximumSavedOutputBytes, snapshot.SavedBytes.Length);
		Assert.True(snapshot.IsSavedByteLimitExceeded);
		Assert.True(snapshot.TotalBytesRead > maximumSavedOutputBytes);
		Assert.Null(snapshot.ReadFailure);
	}

	[Fact]
	public async Task StartAsync_WithNonZeroExitCode_PreservesExitCode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "unknown-mode"));

		try
		{
			int exitCode = await session.WaitForExitAsync().WaitAsync(TestTimeout);

			Assert.Equal(2, exitCode);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	[Fact]
	public async Task CompleteProcessExit_WhenReaderThrows_RecordsFailure()
	{
		TaskCompletionSource<int> exitCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		WindowsConPtySession.CompleteProcessExit(
			exitCompletion,
			() => throw new ObjectDisposedException("processHandle"));

		await Assert.ThrowsAsync<ObjectDisposedException>(
			() => exitCompletion.Task);
	}

	[Fact]
	public async Task DisposeAsync_WhenTerminationStepFails_StillClosesLaterResources()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TimeSpan cleanupTimeout = TimeSpan.FromMilliseconds(250);
		InvalidOperationException expected = new(
			"Injected termination failure.");
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(
				temporaryDirectory.Path,
				"hang",
				cleanupTimeout: cleanupTimeout),
			disposeTerminationFailureFactory: () => expected);
		using Process process = Process.GetProcessById(session.ProcessId);
		await WaitForOutputContainingAsync(session, "PID:", TestTimeout);
		Stopwatch stopwatch = Stopwatch.StartNew();

		Exception? exception = await Record.ExceptionAsync(
			() => session.DisposeAsync().AsTask());
		stopwatch.Stop();

		Assert.Same(expected, exception);
		Assert.True(
			stopwatch.Elapsed <= cleanupTimeout + TimeSpan.FromSeconds(1),
			$"Cleanup took {stopwatch.Elapsed}, exceeding the shared cleanup budget.");
		await process.WaitForExitAsync().WaitAsync(TestTimeout);
		Assert.True(process.HasExited);
		Assert.True(session.IsProcessHandleClosed);
		await session.OutputPumpCompletion.WaitAsync(TestTimeout);
		await WaitForConditionAsync(
			() => session.IsPseudoConsoleClosed,
			TestTimeout);
		Assert.True(session.IsPseudoConsoleClosed);
		await Assert.ThrowsAsync<ObjectDisposedException>(
			() => session.WaitForExitAsync());
	}

	[Fact]
	public async Task DisposeAsync_WhenLateCleanupStartsAfterDeadline_DoesNotResetBudget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TimeSpan cleanupTimeout = TimeSpan.FromMilliseconds(250);
		InvalidOperationException expected = new(
			"Injected termination failure after the cleanup deadline.");
		WindowsProcessJob? job = null;
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(
				temporaryDirectory.Path,
				"hang",
				cleanupTimeout: cleanupTimeout),
			disposeTerminationFailureFactory: () =>
			{
				Stopwatch budgetExhauster = Stopwatch.StartNew();

				while (budgetExhauster.Elapsed <=
					cleanupTimeout + TimeSpan.FromMilliseconds(25))
				{
					Thread.Sleep(TimeSpan.FromMilliseconds(10));
				}

				(job ?? throw new InvalidOperationException(
					"The test Job Object was not captured.")).Dispose();
				return expected;
			});
		job = Assert.IsType<WindowsProcessJob>(
			typeof(WindowsConPtySession)
				.GetField(
					"_job",
					BindingFlags.Instance | BindingFlags.NonPublic)
				?.GetValue(session));
		using Process process = Process.GetProcessById(session.ProcessId);
		await WaitForOutputContainingAsync(session, "PID:", TestTimeout);

		Exception? disposeException = await Record.ExceptionAsync(
			() => session.DisposeAsync().AsTask());

		Assert.Same(expected, disposeException);
		Assert.True(
			session.QuiescenceTask.IsCompleted,
			"Late cleanup restarted its timeout after the foreground cleanup " +
				"budget was exhausted.");
		await Assert.ThrowsAsync<TimeoutException>(
			() => session.QuiescenceTask);
		await process.WaitForExitAsync().WaitAsync(TestTimeout);
		Assert.True(process.HasExited);
		Assert.True(session.IsProcessHandleClosed);
	}

	[Fact]
	public async Task DisposeAsync_WhenTerminationStepFails_CompletesQuiescenceAfterWholeJobTreeExits()
	{
		using TemporaryDirectory temporaryDirectory = new();
		InvalidOperationException expected = new(
			"Injected termination failure.");
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(
				temporaryDirectory.Path,
				"spawn-child-and-hang",
				cleanupTimeout: TimeSpan.FromSeconds(2)),
			disposeTerminationFailureFactory: () => expected);
		Process? rootProcess = null;
		Process? childProcess = null;

		try
		{
			string output = await WaitForOutputContainingAsync(
				session,
				";CHILD:",
				TestTimeout);
			(int rootProcessId, int childProcessId) = ParseProcessIds(output);
			rootProcess = Process.GetProcessById(rootProcessId);
			childProcess = Process.GetProcessById(childProcessId);

			Exception? exception = await Record.ExceptionAsync(
				() => session.DisposeAsync().AsTask());
			await session.QuiescenceTask.WaitAsync(TestTimeout);
			await Task.WhenAll(
				rootProcess.WaitForExitAsync(),
				childProcess.WaitForExitAsync()).WaitAsync(TestTimeout);

			Assert.Same(expected, exception);
			Assert.True(rootProcess.HasExited);
			Assert.True(childProcess.HasExited);
		}
		finally
		{
			rootProcess?.Dispose();
			childProcess?.Dispose();
		}
	}

	[Fact]
	public async Task PositiveEmptyConfirmation_WhenQueriesKeepFailing_IsBounded()
	{
		TimeSpan timeout = TimeSpan.FromMilliseconds(75);
		Stopwatch stopwatch = Stopwatch.StartNew();

		bool confirmed = await WindowsProcessJob
			.WaitForPositiveEmptyConfirmationCoreAsync(
				() => throw new InvalidOperationException(
					"Injected persistent query failure."),
				timeout)
			.WaitAsync(TestTimeout);
		stopwatch.Stop();

		Assert.False(confirmed);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(2),
			$"Bounded positive confirmation took {stopwatch.Elapsed}.");
	}

	[Fact]
	public async Task WaitForExitAsync_WhenCallerTimesOut_TerminateStopsTheContainedProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "hang"));
		using Process rootProcess = Process.GetProcessById(session.ProcessId);

		try
		{
			await WaitForOutputContainingAsync(session, "PID:", TestTimeout);
			using CancellationTokenSource timeoutSource = new(
				TimeSpan.FromMilliseconds(150));

			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => session.WaitForExitAsync(timeoutSource.Token));
			await session.TerminateAsync().AsTask().WaitAsync(TestTimeout);
			await rootProcess.WaitForExitAsync().WaitAsync(TestTimeout);

			Assert.True(rootProcess.HasExited);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	[Fact]
	public async Task TerminateAsync_WithSpawnedGrandchild_StopsTheWholeJobTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		WindowsConPtySession session = await WindowsConPtySession.StartAsync(
			CreateRequest(temporaryDirectory.Path, "spawn-child-and-hang"));
		Process? rootProcess = null;
		Process? childProcess = null;

		try
		{
			string output = await WaitForOutputContainingAsync(
				session,
				";CHILD:",
				TestTimeout);
			(int rootProcessId, int childProcessId) = ParseProcessIds(output);
			Assert.Equal(session.ProcessId, rootProcessId);
			rootProcess = Process.GetProcessById(rootProcessId);
			childProcess = Process.GetProcessById(childProcessId);

			await session.TerminateAsync().AsTask().WaitAsync(TestTimeout);
			await Task.WhenAll(
				rootProcess.WaitForExitAsync(),
				childProcess.WaitForExitAsync())
				.WaitAsync(TestTimeout);

			Assert.True(rootProcess.HasExited);
			Assert.True(childProcess.HasExited);
		}
		finally
		{
			rootProcess?.Dispose();
			childProcess?.Dispose();
			await session.DisposeAsync();
		}
	}

	[Fact]
	public async Task StartAsync_WithRelativeExecutablePath_RejectsShellResolution()
	{
		using TemporaryDirectory temporaryDirectory = new();
		ConPtyStartRequest request = new(
			"AiUsageDashboard.TerminalFixture.exe",
			new[] { "echo" },
			temporaryDirectory.Path,
			CreateEnvironmentAllowlist(),
			120,
			30,
			64 * 1024,
			CleanupTimeout);

		await Assert.ThrowsAsync<ArgumentException>(async () =>
			await WindowsConPtySession.StartAsync(request));
	}

	private static IReadOnlyDictionary<string, string> CreateEnvironmentAllowlist()
	{
		string[] allowedNames =
		{
			"DOTNET_MULTILEVEL_LOOKUP",
			"DOTNET_ROOT",
			"DOTNET_ROOT(x86)",
			"HOME",
			"SystemRoot",
			"TEMP",
			"TMP",
			"USERPROFILE",
			"WINDIR"
		};
		Dictionary<string, string> environment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (string name in allowedNames)
		{
			string? value = Environment.GetEnvironmentVariable(name);

			if (value is not null)
			{
				environment.Add(name, value);
			}
		}

		return environment;
	}

	private static ConPtyStartRequest CreateRequest(
		string workingDirectory,
		string mode,
		short columns = 120,
		int maximumSavedOutputBytes = 2 * 1024 * 1024,
		TimeSpan? cleanupTimeout = null)
	{
		return new ConPtyStartRequest(
			GetFixtureExecutablePath(),
			new[] { mode },
			workingDirectory,
			CreateEnvironmentAllowlist(),
			columns,
			30,
			maximumSavedOutputBytes,
			cleanupTimeout ?? CleanupTimeout);
	}

	private static string GetFixtureExecutablePath()
	{
		Assembly fixtureAssembly = Assembly.Load(
			new AssemblyName("AiUsageDashboard.TerminalFixture"));
		string executablePath = Path.ChangeExtension(
			fixtureAssembly.Location,
			".exe");

		if (!File.Exists(executablePath))
		{
			throw new FileNotFoundException(
				"The terminal fixture apphost was not copied to the test output.",
				executablePath);
		}

		return executablePath;
	}

	private static async Task WaitForConditionAsync(
		Func<bool> condition,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		while (!condition())
		{
			await Task.Delay(20, timeoutSource.Token);
		}
	}

	private static (int RootProcessId, int ChildProcessId) ParseProcessIds(
		string output)
	{
		string processLine = output
			.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
			.Last(line => line.Contains("ROOT:", StringComparison.Ordinal) &&
				line.Contains(";CHILD:", StringComparison.Ordinal));
		int rootMarker = processLine.LastIndexOf("ROOT:", StringComparison.Ordinal);
		int childMarker = processLine.IndexOf(
			";CHILD:",
			rootMarker,
			StringComparison.Ordinal);
		string rootText = processLine[
			(rootMarker + "ROOT:".Length)..childMarker];
		string childText = processLine[
			(childMarker + ";CHILD:".Length)..].Trim();

		return (int.Parse(rootText), int.Parse(childText));
	}

	private static async Task<string> WaitForOutputContainingAsync(
		WindowsConPtySession session,
		string expected,
		TimeSpan timeout)
	{
		using CancellationTokenSource timeoutSource = new(timeout);

		try
		{
			while (true)
			{
				timeoutSource.Token.ThrowIfCancellationRequested();
				ConPtyOutputSnapshot snapshot = session.GetOutputSnapshot();

				if (snapshot.ReadFailure is not null)
				{
					throw new InvalidOperationException(
						"The pseudoconsole output drain failed.",
						snapshot.ReadFailure);
				}

				string output = Encoding.UTF8.GetString(snapshot.SavedBytes.Span);

				if (output.Contains(expected, StringComparison.Ordinal))
				{
					return output;
				}

				await Task.Delay(20, timeoutSource.Token);
			}
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Timed out waiting for terminal output containing '{expected}'.");
		}
	}
}
