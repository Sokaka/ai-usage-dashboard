using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityLiveR0RunnerTests
{
	private sealed class AdvancingTimeProvider : TimeProvider
	{
		private sealed class OneShotTimer : ITimer
		{
			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				return false;
			}

			public void Dispose()
			{
			}

			public ValueTask DisposeAsync()
			{
				return ValueTask.CompletedTask;
			}
		}

		private readonly object _sync = new();
		private readonly TimeSpan _utcAdjustmentPerTimer;
		private long _timestamp;
		private DateTimeOffset _utcNow =
			new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		internal AdvancingTimeProvider(
			TimeSpan utcAdjustmentPerTimer = default)
		{
			_utcAdjustmentPerTimer = utcAdjustmentPerTimer;
		}

		public override long GetTimestamp()
		{
			lock (_sync)
			{
				return _timestamp;
			}
		}

		public override DateTimeOffset GetUtcNow()
		{
			lock (_sync)
			{
				return _utcNow;
			}
		}

		public override ITimer CreateTimer(
			TimerCallback callback,
			object? state,
			TimeSpan dueTime,
			TimeSpan period)
		{
			ArgumentNullException.ThrowIfNull(callback);

			if (dueTime == Timeout.InfiniteTimeSpan)
			{
				return new OneShotTimer();
			}

			lock (_sync)
			{
				TimeSpan elapsed = dueTime > TimeSpan.Zero
					? dueTime
					: TimeSpan.Zero;
				_timestamp += elapsed.Ticks;
				_utcNow += elapsed + _utcAdjustmentPerTimer;
			}

			_ = Task.Run(() => callback(state));
			return new OneShotTimer();
		}
	}

	private sealed class FakeCapabilityValidator :
		IAntigravityLiveCapabilityValidator
	{
		private readonly AntigravityCliCapabilityFailureReason
			_capabilityFailureReason;
		private readonly bool _isSupported;
		private readonly Action? _onValidate;
		private readonly AntigravityCliVersionProbeFailureReason?
			_versionProbeFailureReason;

		internal int CallCount { get; private set; }

		internal FakeCapabilityValidator(
			bool isSupported = true,
			AntigravityCliCapabilityFailureReason capabilityFailureReason =
				AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			AntigravityCliVersionProbeFailureReason?
				versionProbeFailureReason = null,
			Action? onValidate = null)
		{
			_isSupported = isSupported;
			_capabilityFailureReason = capabilityFailureReason;
			_versionProbeFailureReason = versionProbeFailureReason;
			_onValidate = onValidate;
		}

		public Task<AntigravityCliCapabilityValidationResult> ValidateAsync(
			AntigravityCliFingerprint expectedFingerprint,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			_onValidate?.Invoke();

			if (!_isSupported)
			{
				return Task.FromResult(
					new AntigravityCliCapabilityValidationResult(
						false,
						_capabilityFailureReason,
						"Synthetic rejection.",
						versionProbeFailureReason:
							_versionProbeFailureReason));
			}

			AntigravityExecutableLease lease =
				AntigravityExecutableLease.Acquire(
					expectedFingerprint.AbsolutePath);
			return Task.FromResult(
				new AntigravityCliCapabilityValidationResult(
					true,
					AntigravityCliCapabilityFailureReason.None,
					"Synthetic support.",
					expectedFingerprint,
					lease));
		}
	}

	private sealed class FakeExistingProcessGate :
		IAntigravityExistingProcessGate
	{
		private readonly Action<int>? _onCheck;
		private readonly Queue<AntigravityExistingProcessGateResult> _results;

		internal List<int?> AllowedProcessIds { get; } = new();

		internal int CallCount => AllowedProcessIds.Count;

		internal FakeExistingProcessGate(
			params AntigravityExistingProcessGateResult[] results)
			: this(null, results)
		{
		}

		internal FakeExistingProcessGate(
			Action<int>? onCheck,
			params AntigravityExistingProcessGateResult[] results)
		{
			_onCheck = onCheck;
			_results = new Queue<AntigravityExistingProcessGateResult>(results);
		}

		public ValueTask<AntigravityExistingProcessGateResult> CheckAsync(
			string absoluteExecutablePath,
			int? allowedProcessId,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AllowedProcessIds.Add(allowedProcessId);
			_onCheck?.Invoke(AllowedProcessIds.Count);
			AntigravityExistingProcessGateResult result = _results.Count == 0
				? AntigravityExistingProcessGateResult.Clear
				: _results.Dequeue();
			return ValueTask.FromResult(result);
		}
	}

	private sealed class FakeSession : IConPtySession
	{
		private readonly TaskCompletionSource<int> _exitCompletion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly Action? _onDispose;
		private readonly Action<int, FakeSession>? _onGetOutputSnapshot;
		private readonly Action? _onTerminate;
		private readonly Action? _onWriteInput;
		private readonly Exception? _disposeException;
		private readonly Exception? _terminateException;
		private readonly byte[] _usageOutput;
		private readonly Exception? _writeInputException;
		private byte[] _currentOutput;

		public int ProcessId { get; } = 43127;

		internal bool IsDisposed { get; private set; }

		internal bool IsTerminated { get; private set; }

		internal int OutputSnapshotCallCount { get; private set; }

		internal List<int> OutputSnapshotOffsets { get; } = new();

		internal int DisposeCallCount { get; private set; }

		internal int TerminateCallCount { get; private set; }

		internal int WaitForExitCallCount { get; private set; }

		internal int WriteInputCallCount { get; private set; }

		internal List<byte[]> Writes { get; } = new();

		internal FakeSession(
			byte[] promptOutput,
			byte[] usageOutput,
			Action? onWriteInput = null,
			Exception? writeInputException = null,
			Action? onTerminate = null,
			Exception? terminateException = null,
			Action? onDispose = null,
			Exception? disposeException = null,
			Action<int, FakeSession>? onGetOutputSnapshot = null)
		{
			_currentOutput = promptOutput;
			_usageOutput = usageOutput;
			_onWriteInput = onWriteInput;
			_writeInputException = writeInputException;
			_onTerminate = onTerminate;
			_terminateException = terminateException;
			_onDispose = onDispose;
			_disposeException = disposeException;
			_onGetOutputSnapshot = onGetOutputSnapshot;
		}

		internal void SetOutput(byte[] output)
		{
			_currentOutput = output.ToArray();
		}

		public ConPtyOutputSnapshot GetOutputSnapshot(int savedByteOffset = 0)
		{
			OutputSnapshotCallCount++;
			OutputSnapshotOffsets.Add(savedByteOffset);
			_onGetOutputSnapshot?.Invoke(OutputSnapshotCallCount, this);
			byte[] copy = _currentOutput[savedByteOffset..];
			return new ConPtyOutputSnapshot(
				copy,
				savedByteOffset,
				_currentOutput.Length,
				_currentOutput.Length,
				false,
				null);
		}

		public ValueTask WriteInputAsync(
			ReadOnlyMemory<byte> input,
			CancellationToken cancellationToken = default)
		{
			WriteInputCallCount++;
			cancellationToken.ThrowIfCancellationRequested();
			Writes.Add(input.ToArray());
			_onWriteInput?.Invoke();

			if (_writeInputException is not null)
			{
				throw _writeInputException;
			}

			_currentOutput = _usageOutput;
			return ValueTask.CompletedTask;
		}

		public Task<int> WaitForExitAsync(
			CancellationToken cancellationToken = default)
		{
			WaitForExitCallCount++;
			return _exitCompletion.Task.WaitAsync(cancellationToken);
		}

		public ValueTask TerminateAsync(
			CancellationToken cancellationToken = default)
		{
			TerminateCallCount++;
			cancellationToken.ThrowIfCancellationRequested();
			_onTerminate?.Invoke();

			if (_terminateException is not null)
			{
				throw _terminateException;
			}

			IsTerminated = true;
			_exitCompletion.TrySetResult(0);
			return ValueTask.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			DisposeCallCount++;
			_onDispose?.Invoke();

			if (_disposeException is not null)
			{
				throw _disposeException;
			}

			IsDisposed = true;
			_onTerminate?.Invoke();

			if (_terminateException is not null)
			{
				throw _terminateException;
			}

			IsTerminated = true;
			_exitCompletion.TrySetResult(0);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeSettingsSentinelPolicy :
		IAntigravityLiveSettingsSentinelPolicy
	{
		private readonly bool _isAllowed;
		private readonly Func<int, bool>? _isAllowedAtCall;

		internal List<string> AbsoluteSentinelPaths { get; } = new();

		internal int CallCount => AbsoluteSentinelPaths.Count;

		internal FakeSettingsSentinelPolicy(bool isAllowed = true)
		{
			_isAllowed = isAllowed;
		}

		internal FakeSettingsSentinelPolicy(Func<int, bool> isAllowedAtCall)
		{
			_isAllowed = true;
			_isAllowedAtCall = isAllowedAtCall ??
				throw new ArgumentNullException(nameof(isAllowedAtCall));
		}

		public bool IsAllowed(
			string absoluteSentinelPath,
			IReadOnlyDictionary<string, string> environment)
		{
			AbsoluteSentinelPaths.Add(absoluteSentinelPath);
			return _isAllowedAtCall?.Invoke(CallCount) ?? _isAllowed;
		}
	}

	private sealed class FakeSessionFactory :
		IAntigravityLiveConPtySessionFactory
	{
		private readonly FakeSession _session;

		internal int CallCount { get; private set; }

		internal ConPtyStartRequest? Request { get; private set; }

		internal FakeSessionFactory(FakeSession session)
		{
			_session = session;
		}

		public ValueTask<IConPtySession> StartAsync(
			ConPtyStartRequest request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			Request = request;
			return ValueTask.FromResult<IConPtySession>(_session);
		}
	}

	private static readonly byte[] ExactFingerprintKey =
		Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

	[Fact]
	public async Task RunAsync_WhenExistingProcessIsDetected_DoesNotProbeOrStart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Detected);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.True(report.ExistingProcessDetected);
		Assert.Contains(
			AntigravityLiveR0FailureReason.ExistingProcessDetected,
			report.FailureReasons);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WhenCapabilityIsRejected_ReportsSafeValidatorReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new(isSupported: false);
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(
			(AntigravityCliCapabilityFailureReason?)
				AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			report.CapabilityFailureReason);
		Assert.Contains(
			AntigravityLiveR0FailureReason.CapabilityRejected,
			report.FailureReasons);
		Assert.Equal(1, capability.CallCount);
		Assert.Equal(1, processGate.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WhenVersionProbeFails_ReportsFixedSubreason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new(
			isSupported: false,
			capabilityFailureReason:
				AntigravityCliCapabilityFailureReason.VersionProbeFailed,
			versionProbeFailureReason:
				AntigravityCliVersionProbeFailureReason.TimedOut);
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(
			(AntigravityCliCapabilityFailureReason?)
				AntigravityCliCapabilityFailureReason.VersionProbeFailed,
			report.CapabilityFailureReason);
		Assert.Equal(
			(AntigravityCliVersionProbeFailureReason?)
				AntigravityCliVersionProbeFailureReason.TimedOut,
			report.VersionProbeFailureReason);
		Assert.Contains(
			AntigravityLiveR0FailureReason.CapabilityRejected,
			report.FailureReasons);
		Assert.Equal(1, capability.CallCount);
		Assert.Equal(1, processGate.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WithUnknownMode_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			(AntigravityLiveR0Mode)int.MaxValue,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WithNoSettingsSentinel_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory) with
		{
			NonCredentialSettingsFiles = Array.Empty<string>()
		};
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WithZeroStableDuration_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory) with
		{
			StableScreenDuration = TimeSpan.Zero
		};
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_ObservePrompt_NeverWritesInputAndReturnsOnlyRedactedCapture()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeExistingProcessGate processGate = CreateClearProcessGate();
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Empty(session.Writes);
		Assert.NotNull(report.PromptCapture);
		Assert.Matches("^[0-9A-F]{64}$", report.PromptExactLocalFingerprint);
		Assert.Equal(AntigravityLiveR0GateStatus.NotVerified, report.IdentityGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.ModelInvocationGate);
		Assert.False(report.IsR0Go);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
		Assert.Equal(0, session.TerminateCallCount);
		Assert.Equal(1, session.WaitForExitCallCount);
		Assert.Equal(1, session.DisposeCallCount);
		Assert.Equal(0, Assert.Single(session.OutputSnapshotOffsets.Take(1)));
		Assert.Contains(
			session.OutputSnapshotOffsets.Skip(1),
			offset => offset > 0);
		Assert.Equal("true", factory.Request?.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
		Assert.Equal(
			new int?[] { null, null, session.ProcessId },
			processGate.AllowedProcessIds);
	}

	[Fact]
	public async Task RunAsync_CalibratePrompt_IgnoresPinnedPromptAndNeverWritesInput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] pinnedPrompt = CreatePromptOutput("PINNED PROMPT");
		const string ActualPromptMarker = "PRIVATE-CALIBRATION-PROMPT";
		byte[] actualPrompt = CreatePromptOutput(ActualPromptMarker);
		(string pinnedCoarse, string pinnedExact) = ComputeFingerprints(
			pinnedPrompt,
			120,
			30);
		(string actualCoarse, string actualExact) = ComputeFingerprints(
			actualPrompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			pinnedCoarse,
			pinnedExact);
		FakeSession session = new(
			actualPrompt,
			CreateUsageOutput(actualPrompt));
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			factory,
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CalibratePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0Mode.CalibratePrompt, report.Mode);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotApplicable,
			report.UsageCaptureGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.SettingsGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.CleanupGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.IdentityGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.ModelInvocationGate);
		Assert.False(report.IsR0Go);
		Assert.Equal(0, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
		Assert.Equal(
			actualCoarse,
			Assert.IsType<AntigravityRedactedStructuralCapture>(
				report.PromptCapture).StructuralFingerprint);
		Assert.Equal(actualExact, report.PromptExactLocalFingerprint);
		Assert.NotEqual(pinnedExact, report.PromptExactLocalFingerprint);
		Assert.Null(report.UsageCapture);
		Assert.Null(report.UsageExactLocalFingerprint);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);

		JsonSerializerOptions jsonOptions = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		string serializedReport = JsonSerializer.Serialize(report, jsonOptions);
		Assert.Contains(actualCoarse, serializedReport, StringComparison.Ordinal);
		Assert.Contains(actualExact, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			ActualPromptMarker,
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Convert.ToBase64String(actualPrompt),
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			profile.NonCredentialSettingsFiles.Single(),
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			Convert.ToHexString(ExactFingerprintKey),
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Convert.ToBase64String(ExactFingerprintKey),
			serializedReport,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunAsync_CalibratePrompt_WithForwardClockJump_UsesMonotonicTiming()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] blankStartup = CreatePromptOutput(string.Empty);
		byte[] actualPrompt = CreatePromptOutput("PROMPT READY AFTER BLANK");
		(string actualCoarse, string actualExact) = ComputeFingerprints(
			actualPrompt,
			120,
			40);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptTimeout: TimeSpan.FromMilliseconds(100),
			stableScreenDuration: TimeSpan.FromMilliseconds(10),
			pollInterval: TimeSpan.FromMilliseconds(10)) with
		{
			Rows = 40
		};
		FakeSession session = new(
			blankStartup,
			actualPrompt,
			onGetOutputSnapshot: (callCount, currentSession) =>
			{
				if (callCount == 3)
				{
					currentSession.SetOutput(actualPrompt);
				}
			});
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			new AdvancingTimeProvider(TimeSpan.FromDays(1)),
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CalibratePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(4, session.OutputSnapshotCallCount);
		Assert.Empty(session.Writes);
		AntigravityRedactedStructuralCapture capture = Assert.IsType<
			AntigravityRedactedStructuralCapture>(report.PromptCapture);
		Assert.NotEmpty(capture.NonEmptyLines);
		Assert.Equal(actualCoarse, capture.StructuralFingerprint);
		Assert.Equal(actualExact, report.PromptExactLocalFingerprint);
	}

	[Fact]
	public async Task RunAsync_CalibratePrompt_WithBlankOnlyStartup_TimesOutFailClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] blankStartup = CreatePromptOutput(string.Empty);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptTimeout: TimeSpan.FromMilliseconds(30),
			stableScreenDuration: TimeSpan.FromMilliseconds(10),
			pollInterval: TimeSpan.FromMilliseconds(10)) with
		{
			Rows = 40
		};
		FakeSession session = new(blankStartup, blankStartup);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			new AdvancingTimeProvider(),
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CalibratePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
			report.FailureReasons);
		Assert.Null(report.PromptCapture);
		Assert.Null(report.PromptExactLocalFingerprint);
		Assert.Equal(0, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Empty(session.Writes);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
	}

	[Fact]
	public async Task RunAsync_ObservePrompt_WithPinnedMismatchStillFailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] pinnedPrompt = CreatePromptOutput("PINNED PROMPT");
		byte[] actualPrompt = CreatePromptOutput("CURRENT PROMPT");
		(string pinnedCoarse, string pinnedExact) = ComputeFingerprints(
			pinnedPrompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			pinnedCoarse,
			pinnedExact,
			promptTimeout: TimeSpan.FromMilliseconds(80));
		FakeSession session = new(
			actualPrompt,
			CreateUsageOutput(actualPrompt));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
			report.FailureReasons);
		Assert.Null(report.PromptCapture);
		Assert.Null(report.PromptExactLocalFingerprint);
		Assert.Equal(0, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
	}

	[Theory]
	[InlineData(
		"unsupported-csi",
		"UnsupportedControlSequence",
		"RAW-CSI-SENTINEL")]
	[InlineData(
		"unsupported-csi-greater-than",
		"UnsupportedControlSequence",
		"RAW-CSI-GREATER-THAN-SENTINEL")]
	[InlineData(
		"unsupported-csi-semicolon",
		"UnsupportedControlSequence",
		"RAW-CSI-SEMICOLON-SENTINEL")]
	[InlineData(
		"unsupported-csi-colon-empty",
		"UnsupportedControlSequence",
		"RAW-CSI-COLON-EMPTY-SENTINEL")]
	[InlineData(
		"unsupported-csi-other-byte",
		"UnsupportedControlSequence",
		"RAW-CSI-OTHER-BYTE-SENTINEL")]
	[InlineData(
		"unsupported-unicode",
		"UnsupportedUnicodeRune",
		"RAW-UNICODE-SENTINEL")]
	[InlineData(
		"invalid-utf8",
		"InvalidUtf8",
		"RAW-UTF8-SENTINEL")]
	public async Task RunAsync_WhenTerminalCaptureFails_ReportsOnlyRedactedCategory(
		string failureCase,
		string expectedCategoryName,
		string rawMarker)
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityTerminalCaptureFailureReason expectedCategory =
			Enum.Parse<AntigravityTerminalCaptureFailureReason>(
				expectedCategoryName);
		byte[] rawOutput = failureCase switch
		{
			"unsupported-csi" => Encoding.UTF8.GetBytes(
				rawMarker + "\u001b[?9999h"),
			"unsupported-csi-greater-than" => Encoding.UTF8.GetBytes(
				rawMarker + "\u001b[>0c"),
			"unsupported-csi-semicolon" => Encoding.UTF8.GetBytes(
				rawMarker + "\u001b[>4;3m"),
			"unsupported-csi-colon-empty" => Encoding.UTF8.GetBytes(
				rawMarker + "\u001b[38:2::1:2:3m"),
			"unsupported-csi-other-byte" => Encoding.UTF8.GetBytes(
				rawMarker + "\u001b[?4$h"),
			"unsupported-unicode" => Encoding.UTF8.GetBytes(
				rawMarker + "\u0301"),
			"invalid-utf8" => Encoding.UTF8.GetBytes(rawMarker)
				.Concat(new byte[] { 0xC3, 0x28 })
				.ToArray(),
			_ => throw new ArgumentOutOfRangeException(nameof(failureCase))
		};
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeSession session = new(rawOutput, rawOutput);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.TerminalCaptureFailed,
			report.FailureReasons);
		Assert.Equal(expectedCategory, report.TerminalCaptureFailureReason);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Null(report.PromptCapture);
		Assert.Null(report.PromptExactLocalFingerprint);
		Assert.Empty(session.Writes);

		if (failureCase.StartsWith("unsupported-csi", StringComparison.Ordinal))
		{
			Assert.NotNull(report.TerminalControlDiagnostic);
			AntigravityTerminalControlDiagnostic diagnostic =
				report.TerminalControlDiagnostic!;
			Assert.Equal(
				AntigravityTerminalControlSequenceFamily.Csi,
				diagnostic.Family);

			if (failureCase == "unsupported-csi")
			{
				Assert.Equal((int)'h', diagnostic.FinalByteCode);
				Assert.True(diagnostic.IsPrivate);
				Assert.Equal((int)'?', diagnostic.PrivateMarkerByteCode);
				Assert.Equal(new[] { 9999 }, diagnostic.NumericParameters);
				Assert.False(diagnostic.HasUnparsedParameterBytes);
				Assert.False(diagnostic.UsesColonSeparators);
				Assert.False(diagnostic.HasEmptyParameterFields);
			}
			else if (failureCase == "unsupported-csi-greater-than")
			{
				Assert.Equal((int)'c', diagnostic.FinalByteCode);
				Assert.True(diagnostic.IsPrivate);
				Assert.Equal((int)'>', diagnostic.PrivateMarkerByteCode);
				Assert.Equal(new[] { 0 }, diagnostic.NumericParameters);
				Assert.False(diagnostic.HasUnparsedParameterBytes);
				Assert.False(diagnostic.UsesColonSeparators);
				Assert.False(diagnostic.HasEmptyParameterFields);
			}
			else if (failureCase == "unsupported-csi-semicolon")
			{
				Assert.Equal((int)'m', diagnostic.FinalByteCode);
				Assert.True(diagnostic.IsPrivate);
				Assert.Equal((int)'>', diagnostic.PrivateMarkerByteCode);
				Assert.Equal(new[] { 4, 3 }, diagnostic.NumericParameters);
				Assert.False(diagnostic.HasUnparsedParameterBytes);
				Assert.False(diagnostic.UsesColonSeparators);
				Assert.False(diagnostic.HasEmptyParameterFields);
			}
			else if (failureCase == "unsupported-csi-colon-empty")
			{
				Assert.Equal((int)'m', diagnostic.FinalByteCode);
				Assert.False(diagnostic.IsPrivate);
				Assert.Null(diagnostic.PrivateMarkerByteCode);
				Assert.Equal(
					new[] { 38, 2, 1, 2, 3 },
					diagnostic.NumericParameters);
				Assert.False(diagnostic.HasUnparsedParameterBytes);
				Assert.True(diagnostic.UsesColonSeparators);
				Assert.True(diagnostic.HasEmptyParameterFields);
			}
			else
			{
				Assert.Equal((int)'h', diagnostic.FinalByteCode);
				Assert.True(diagnostic.IsPrivate);
				Assert.Equal((int)'?', diagnostic.PrivateMarkerByteCode);
				Assert.Empty(diagnostic.NumericParameters);
				Assert.True(diagnostic.HasUnparsedParameterBytes);
				Assert.False(diagnostic.UsesColonSeparators);
				Assert.False(diagnostic.HasEmptyParameterFields);
			}
		}
		else
		{
			Assert.Null(report.TerminalControlDiagnostic);
		}

		JsonSerializerOptions jsonOptions = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		string serializedReport = JsonSerializer.Serialize(report, jsonOptions);
		Assert.Contains(
			$"\"TerminalCaptureFailureReason\":\"{expectedCategory}\"",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(rawMarker, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			Convert.ToBase64String(rawOutput),
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain("?9999h", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(">0c", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(">4;3m", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"38:2::1:2:3m",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain("?4$h", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"\\u001b",
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"Unsupported or malformed terminal control sequence",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"The terminal stream contains an unsupported Unicode rune.",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"The terminal stream contains invalid UTF-8.",
			serializedReport,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenSameShapeHasDifferentExactScreen_DoesNotWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] expectedPrompt = CreatePromptOutput("PROMPT-A");
		byte[] actualPrompt = CreatePromptOutput("PROMPT-B");
		(string coarse, string exact) = ComputeFingerprints(
			expectedPrompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact,
			promptTimeout: TimeSpan.FromMilliseconds(80));
		FakeSession session = new(
			actualPrompt,
			CreateUsageOutput(actualPrompt));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
			report.FailureReasons);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Empty(session.Writes);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WithExactPrompt_WritesOnlyUsageOnceAndCleansUp()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, CreateUsageOutput(prompt));
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			factory,
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteCount);
		byte[] write = Assert.Single(session.Writes);
		Assert.Equal(Encoding.UTF8.GetBytes("/usage\r"), write);
		Assert.NotNull(report.UsageCapture);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.UsageCaptureGate);
		Assert.False(report.IsR0Go);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
		Assert.Equal("true", factory.Request?.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenProcessAppearsAfterFinalPromptValidation_DoesNotWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, CreateUsageOutput(prompt));
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Detected);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.True(report.ExistingProcessDetected);
		Assert.Contains(
			AntigravityLiveR0FailureReason.ExistingProcessDetected,
			report.FailureReasons);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
		Assert.Equal(
			new int?[]
			{
				null,
				null,
				session.ProcessId,
				session.ProcessId,
				session.ProcessId
			},
			processGate.AllowedProcessIds);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenCoarseShapeIsUnchanged_AcceptsDifferentExactScreen()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "USAGE---");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Single(session.Writes);
		Assert.NotNull(report.PromptCapture);
		Assert.NotNull(report.UsageCapture);
		Assert.Equal(
			report.PromptCapture.StructuralFingerprint,
			report.UsageCapture.StructuralFingerprint);
		Assert.Matches("^[0-9A-F]{64}$", report.UsageExactLocalFingerprint);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.UsageCaptureGate);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WithPinnedExactUsage_PassesUsageGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "USAGE---");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, string usageExact) = ComputeFingerprints(
			usage,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact) with
		{
			ExpectedUsageStructuralFingerprint = usageCoarse,
			ExpectedExactUsageFingerprint = usageExact
		};
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.True(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Single(session.Writes);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Passed,
			report.UsageCaptureGate);
		Assert.Equal(usageExact, report.UsageExactLocalFingerprint);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenOnlyExactUsagePinMismatches_ReportsStableExactDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "PRIVATE-EXACT-MISMATCH");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, string usageExact) = ComputeFingerprints(
			usage,
			120,
			30);
		string wrongUsageExact = new('7', 64);
		Assert.NotEqual(usageExact, wrongUsageExact);
		Assert.NotEqual(promptExact, wrongUsageExact);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: TimeSpan.FromMilliseconds(60),
			stableScreenDuration: TimeSpan.FromMilliseconds(2),
			pollInterval: TimeSpan.FromMilliseconds(5)) with
		{
			ExpectedUsageStructuralFingerprint = usageCoarse,
			ExpectedExactUsageFingerprint = wrongUsageExact
		};
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteAttemptCount);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.UsageCaptureGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.UsageFingerprintNotObserved,
			report.FailureReasons);
		Assert.Equal(
			AntigravityTerminalCaptureFailureReason.None,
			report.TerminalCaptureFailureReason);
		AntigravityUsageTimeoutDiagnostic diagnostic =
			Assert.IsType<AntigravityUsageTimeoutDiagnostic>(
				report.UsageTimeoutDiagnostic);
		Assert.Equal(
			AntigravityUsageTimeoutClassification.StableExactMismatch,
			diagnostic.Classification);
		Assert.True(diagnostic.HasPostWriteOutput);
		Assert.True(diagnostic.HasObservedNonPromptCandidate);
		Assert.True(
			diagnostic.HasObservedExpectedStructuralFingerprint);
		Assert.False(diagnostic.HasObservedExpectedExactFingerprint);
		Assert.True(
			diagnostic.LastStableMismatchMatchesStructuralFingerprint);
		Assert.False(
			diagnostic.LastStableMismatchMatchesExactFingerprint);
		Assert.Equal(1, diagnostic.CappedDistinctNonPromptCandidateCount);
		Assert.Equal(
			usageCoarse,
			diagnostic.LastStableMismatchCapture?.StructuralFingerprint);
		Assert.Equal(
			usageExact,
			diagnostic.LastStableMismatchExactLocalFingerprint);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenStructuralUsagePinMismatches_ReportsStableStructuralDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "PRIVATE-STRUCTURAL-MISMATCH");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, string usageExact) = ComputeFingerprints(
			usage,
			120,
			30);
		string wrongUsageCoarse = new('8', 64);
		Assert.NotEqual(usageCoarse, wrongUsageCoarse);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: TimeSpan.FromMilliseconds(60),
			stableScreenDuration: TimeSpan.FromMilliseconds(2),
			pollInterval: TimeSpan.FromMilliseconds(5)) with
		{
			ExpectedUsageStructuralFingerprint = wrongUsageCoarse,
			ExpectedExactUsageFingerprint = usageExact
		};
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));
		AntigravityUsageTimeoutDiagnostic diagnostic =
			Assert.IsType<AntigravityUsageTimeoutDiagnostic>(
				report.UsageTimeoutDiagnostic);

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityUsageTimeoutClassification.StableStructuralMismatch,
			diagnostic.Classification);
		Assert.False(
			diagnostic.HasObservedExpectedStructuralFingerprint);
		Assert.True(diagnostic.HasObservedExpectedExactFingerprint);
		Assert.False(
			diagnostic.LastStableMismatchMatchesStructuralFingerprint);
		Assert.True(diagnostic.LastStableMismatchMatchesExactFingerprint);
		Assert.Equal(
			usageCoarse,
			diagnostic.LastStableMismatchCapture?.StructuralFingerprint);
		Assert.Equal(usageExact, diagnostic.LastNonPromptExactLocalFingerprint);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenNoOutputArrivesAfterWrite_ReportsPromptWithoutNewBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE-PROMPT-STILL-VISIBLE");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: TimeSpan.FromMilliseconds(50),
			stableScreenDuration: TimeSpan.FromMilliseconds(2),
			pollInterval: TimeSpan.FromMilliseconds(5)) with
		{
			ExpectedUsageStructuralFingerprint = new string('8', 64),
			ExpectedExactUsageFingerprint = new string('9', 64)
		};
		FakeSession session = new(prompt, prompt);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));
		AntigravityUsageTimeoutDiagnostic diagnostic =
			Assert.IsType<AntigravityUsageTimeoutDiagnostic>(
				report.UsageTimeoutDiagnostic);

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityUsageTimeoutClassification.NoPostWriteOutput,
			diagnostic.Classification);
		Assert.False(diagnostic.HasPostWriteOutput);
		Assert.True(diagnostic.HasObservedPrompt);
		Assert.False(diagnostic.HasObservedNonPromptCandidate);
		Assert.Equal(
			diagnostic.BaselineTotalBytesRead,
			diagnostic.LastTotalBytesRead);
		Assert.Equal(
			diagnostic.BaselineSavedByteCount,
			diagnostic.LastSavedByteCount);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Single(session.Writes);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenPromptIsFollowedByAtomicPending_ReportsLastAtomicObservation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE-PROMPT-BEFORE-ATOMIC");
		byte[] atomicPending = prompt
			.Concat(Encoding.UTF8.GetBytes("\u001b[?2026h"))
			.ToArray();
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: TimeSpan.FromMilliseconds(50),
			stableScreenDuration: TimeSpan.FromMilliseconds(2),
			pollInterval: TimeSpan.FromMilliseconds(5)) with
		{
			ExpectedUsageStructuralFingerprint = new string('8', 64),
			ExpectedExactUsageFingerprint = new string('9', 64)
		};
		int postWriteSnapshotCount = 0;
		FakeSession session = new(
			prompt,
			prompt,
			onGetOutputSnapshot: (_, currentSession) =>
			{
				if (currentSession.WriteInputCallCount != 1)
				{
					return;
				}

				postWriteSnapshotCount++;

				if (postWriteSnapshotCount == 2)
				{
					currentSession.SetOutput(atomicPending);
				}
			});
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));
		AntigravityUsageTimeoutDiagnostic diagnostic =
			Assert.IsType<AntigravityUsageTimeoutDiagnostic>(
				report.UsageTimeoutDiagnostic);

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityUsageTimeoutClassification.AtomicPending,
			diagnostic.Classification);
		Assert.Equal(
			AntigravityUsageTimeoutObservationKind.AtomicPending,
			diagnostic.LastObservationKind);
		Assert.True(diagnostic.HasPostWriteOutput);
		Assert.True(diagnostic.HasObservedPrompt);
		Assert.True(diagnostic.HasObservedAtomicPending);
		Assert.True(diagnostic.AtomicPendingObservationCount > 0);
		Assert.True(
			diagnostic.LastTotalBytesRead >
				diagnostic.BaselineTotalBytesRead);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Single(session.Writes);
	}

	[Fact]
	public void CaptureStabilityTracker_NullObservationResetsExpectedStability()
	{
		AntigravityCaptureStabilityTracker tracker = new();
		TimeSpan startedAt = TimeSpan.Zero;
		TimeSpan stableDuration = TimeSpan.FromSeconds(10);

		Assert.False(tracker.Observe("candidate", startedAt, stableDuration));
		Assert.False(tracker.Observe(
			null,
			startedAt + TimeSpan.FromSeconds(9),
			stableDuration));
		Assert.False(tracker.Observe(
			"candidate",
			startedAt + TimeSpan.FromSeconds(10),
			stableDuration));
		Assert.False(tracker.Observe(
			"candidate",
			startedAt + TimeSpan.FromSeconds(19),
			stableDuration));
		Assert.True(tracker.Observe(
			"candidate",
			startedAt + TimeSpan.FromSeconds(20),
			stableDuration));
	}

	[Fact]
	public async Task RunAsync_UsageTimeoutDiagnosticJson_DoesNotContainRawTerminalMarkers()
	{
		const string RawMarker = "PRIVATE-USAGE-TIMEOUT-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, RawMarker);
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, _) = ComputeFingerprints(usage, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: TimeSpan.FromMilliseconds(50),
			stableScreenDuration: TimeSpan.FromMilliseconds(2),
			pollInterval: TimeSpan.FromMilliseconds(5)) with
		{
			ExpectedUsageStructuralFingerprint = usageCoarse,
			ExpectedExactUsageFingerprint = new string('6', 64)
		};
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));
		JsonSerializerOptions jsonOptions = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		string serializedReport = JsonSerializer.Serialize(report, jsonOptions);

		Assert.NotNull(report.UsageTimeoutDiagnostic);
		Assert.DoesNotContain(RawMarker, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			Convert.ToBase64String(usage),
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain("PROMPT-A", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"\\u001b",
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RunAsync_CaptureUsage_WithOnlyOneUsagePin_FailsBeforeAnyDownstreamAction(
		bool includeStructuralPin)
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "USAGE---");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, string usageExact) = ComputeFingerprints(
			usage,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact) with
		{
			ExpectedUsageStructuralFingerprint = includeStructuralPin
				? usageCoarse
				: null,
			ExpectedExactUsageFingerprint = includeStructuralPin
				? null
				: usageExact
		};
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = new(prompt, usage);
		FakeSessionFactory factory = new(session);
		FakeSettingsSentinelPolicy settingsPolicy = new();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			settingsPolicy);

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(0, settingsPolicy.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenUsageExactEqualsPromptExact_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] usage = CreateUsageOutput(prompt, "USAGE---");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		(string usageCoarse, _) = ComputeFingerprints(usage, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact) with
		{
			ExpectedUsageStructuralFingerprint = usageCoarse,
			ExpectedExactUsageFingerprint = promptExact
		};
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = new(prompt, usage);
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenPromptChangesDuringPostGateRecheck_DoesNotWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] changedPrompt = CreateUsageOutput(prompt, "CHANGED-");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, CreateUsageOutput(prompt));
		FakeExistingProcessGate processGate = new(
			callCount =>
			{
				if (callCount == 5)
				{
					session.SetOutput(changedPrompt);
				}
			},
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
			report.FailureReasons);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
	}

	[Theory]
	[InlineData("\u001b[>4;2m")]
	[InlineData("\u001b[=1;1u")]
	[InlineData("\u001b[?u")]
	public async Task RunAsync_CaptureUsage_WhenInputModeChangesDuringPostGateRecheck_DoesNotWrite(
		string inputModeSequence)
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT-A");
		byte[] changedPrompt = prompt
			.Concat(Encoding.UTF8.GetBytes(inputModeSequence))
			.ToArray();
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, CreateUsageOutput(prompt));
		FakeExistingProcessGate processGate = new(
			callCount =>
			{
				if (callCount == 5)
				{
					session.SetOutput(changedPrompt);
				}
			},
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.PromptGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
			report.FailureReasons);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
		Assert.True(session.IsTerminated);
		Assert.True(session.IsDisposed);
	}

	[Fact]
	public async Task RunAsync_WhenSettingsDriftAfterCapability_FailsBeforeSessionStart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		string settingsPath = profile.NonCredentialSettingsFiles.Single();
		FakeCapabilityValidator capability = new(
			onValidate: () => File.AppendAllText(settingsPath, " "));
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsDrift,
			report.FailureReasons);
		Assert.Equal(
			AntigravitySettingsDriftStage.AfterCapability,
			report.FirstSettingsDriftStage);
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Length));
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Sha256));
		Assert.Equal(0, factory.CallCount);
		Assert.Equal(0, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenSettingsDriftImmediatelyBeforeUsage_BlocksInput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		string settingsPath = profile.NonCredentialSettingsFiles.Single();
		FakeSession session = new(prompt, CreateUsageOutput(prompt));
		FakeExistingProcessGate processGate = new(
			callCount =>
			{
				if (callCount == 4)
				{
					File.AppendAllText(settingsPath, " ");
				}
			},
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			processGate,
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			report.UsageCaptureGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.SettingsGate);
		Assert.Equal(
			AntigravitySettingsDriftStage.ImmediatelyBeforeUsage,
			report.FirstSettingsDriftStage);
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Length));
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Sha256));
		Assert.Equal(0, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(0, session.WriteInputCallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenWritePartiallyFails_ReportsAttemptWithoutCompletionOrRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt),
			writeInputException: new IOException("Synthetic partial write."));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(1, session.WriteInputCallCount);
		Assert.Single(session.Writes);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.UsageCaptureGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.UsageCaptureNotObserved,
			report.FailureReasons);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenWriteIsCancelledAfterAttempt_FailsUsageGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt),
			writeInputException: new OperationCanceledException(
				"Synthetic cancellation after attempt."));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(1, report.InputWriteAttemptCount);
		Assert.Equal(0, report.InputWriteCount);
		Assert.Equal(1, session.WriteInputCallCount);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.UsageCaptureGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.Cancelled,
			report.FailureReasons);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenUsageParserFails_PreservesPromptGateAndFailsUsageGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		byte[] unsupportedUsage = prompt
			.Concat(Encoding.UTF8.GetBytes("\u001b[?9999h"))
			.ToArray();
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(prompt, unsupportedUsage);
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.UsageCaptureGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.SettingsGate);
		Assert.Equal(1, report.InputWriteAttemptCount);
		Assert.Equal(1, report.InputWriteCount);
		Assert.Contains(
			AntigravityLiveR0FailureReason.TerminalCaptureFailed,
			report.FailureReasons);
		Assert.Equal(
			AntigravityTerminalCaptureFailureReason.UnsupportedControlSequence,
			report.TerminalCaptureFailureReason);
		Assert.Equal(
			AntigravitySettingsDriftStage.None,
			report.FirstSettingsDriftStage);
	}

	[Fact]
	public async Task RunAsync_CaptureUsage_WhenTerminalFailsAndSettingsDrift_ReportsBothFailures()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		byte[] unsupportedUsage = prompt
			.Concat(Encoding.UTF8.GetBytes("\u001b[?9999h"))
			.ToArray();
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		string settingsPath = profile.NonCredentialSettingsFiles.Single();
		FakeSession session = new(
			prompt,
			unsupportedUsage,
			onWriteInput: () => File.AppendAllText(settingsPath, " "));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, report.PromptGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.UsageCaptureGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.TerminalCaptureFailed,
			report.FailureReasons);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsDrift,
			report.FailureReasons);
		Assert.Equal(
			AntigravitySettingsDriftStage.AfterUsageOrCaptureFailure,
			report.FirstSettingsDriftStage);
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Length));
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Sha256));
	}

	[Fact]
	public async Task RunAsync_WhenCleanupFailsAndSettingsDrift_FinalSnapshotStillReportsBoth()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		string settingsPath = profile.NonCredentialSettingsFiles.Single();
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt),
			onTerminate: () => File.AppendAllText(settingsPath, " "),
			terminateException: new IOException("Synthetic cleanup failure."));
		AntigravityLiveR0Runner runner = new(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			TimeProvider.System,
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.CleanupGate);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.CleanupFailed,
			report.FailureReasons);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsDrift,
			report.FailureReasons);
		Assert.Equal(
			AntigravitySettingsDriftStage.AfterCleanup,
			report.FirstSettingsDriftStage);
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Length));
		Assert.True(report.ChangedDimensions.HasFlag(
			AntigravitySettingsChangedDimensions.Sha256));
	}

	[Fact]
	public async Task RunAsync_WhenSettingsPolicyFailsAtCheckpoint_FailsClosedAsInspectionFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new();
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeSettingsSentinelPolicy settingsPolicy = new(
			callCount => callCount <= 3);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			CreateClearProcessGate(),
			TimeProvider.System,
			settingsPolicy);

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, report.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsInspectionFailed,
			report.FailureReasons);
		Assert.Equal(
			AntigravitySettingsDriftStage.None,
			report.FirstSettingsDriftStage);
		Assert.Equal(AntigravitySettingsChangedDimensions.None, report.ChangedDimensions);
		Assert.Equal(1, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.True(settingsPolicy.CallCount >= 4);
	}

	[Fact]
	public async Task RunAsync_WhenSettingsPolicyRejects_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeSettingsSentinelPolicy settingsPolicy = new(isAllowed: false);
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			settingsPolicy);

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(1, settingsPolicy.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunAsync_WhenSettingsSentinelDoesNotExist_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		File.Delete(profile.NonCredentialSettingsFiles.Single());
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeSettingsSentinelPolicy settingsPolicy = new();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			settingsPolicy);

		AntigravityLiveR0Report report = await runner.RunAsync(
			AntigravityLiveR0Mode.ObservePrompt,
			profile,
			new AntigravityLiveR0Consent(true));

		Assert.False(report.IsCaptureSuccessful);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			report.FailureReasons);
		Assert.Equal(1, settingsPolicy.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunR1Async_WithReviewedSemanticSinglePage_ReturnsSafeUsageAndWritesOnce()
	{
		const string Identity = "PRIVATE.R1.IDENTITY.SENTINEL@EXAMPLE.INVALID";
		const string Reset = "2026-07-17T12:34:56.0000000+00:00";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			Identity,
			"single",
			$"Gemini Synthetic Flash | 17% | 83% | {Reset}",
			"Gemini Synthetic Pro | 40% | 60% | -");
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen);
		byte[] calibratedUsage = CreateR1UsageOutput(prompt, reviewedScreen);
		byte[] observedUsage = CreateR1UsageOutput(
			prompt,
			reviewedScreen,
			terminalSuffix: "\u001b[20;50H");
		(string calibratedCoarse, string calibratedExact) = ComputeFingerprints(
			calibratedUsage,
			120,
			30);
		(string observedCoarse, string observedExact) = ComputeFingerprints(
			observedUsage,
			120,
			30);
		FakeSession session = new(prompt, observedUsage);
		AntigravityLiveR0Runner runner = CreateR1Runner(session);

		AntigravityLiveR1RunResult result = await runner.RunR1Async(
			profile,
			new AntigravityLiveR1Consent(true));
		AntigravityLiveR0Report safety = result.Report.SafetyReport;
		AntigravityUsageResult usage = Assert.IsType<AntigravityUsageResult>(
			result.Usage);
		JsonSerializerOptions jsonOptions = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		string serializedReport = JsonSerializer.Serialize(
			result.Report,
			jsonOptions);

		Assert.Equal(calibratedCoarse, observedCoarse);
		Assert.NotEqual(calibratedExact, observedExact);
		Assert.Null(profile.CaptureProfile.ExpectedUsageStructuralFingerprint);
		Assert.Null(profile.CaptureProfile.ExpectedExactUsageFingerprint);
		Assert.True(result.Report.IsCaptureSuccessful);
		Assert.True(safety.IsCaptureSuccessful);
		Assert.False(result.Report.IsR1Go);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Passed,
			safety.UsageCaptureGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			safety.IdentityGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			safety.ModelInvocationGate);
		Assert.Equal(1, safety.InputWriteAttemptCount);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Equal(observedExact, safety.UsageExactLocalFingerprint);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
		Assert.Equal(Identity.ToLowerInvariant(), usage.AccountIdentity);
		Assert.Collection(
			usage.Rows,
			row =>
			{
				Assert.Equal("gemini.synthetic.flash", row.StableModelId);
				Assert.Equal(17, row.UsedPercent);
				Assert.Equal(83, row.RemainingPercent);
				Assert.Equal(
					DateTimeOffset.Parse(Reset),
					row.ResetsAt);
			},
			row =>
			{
				Assert.Equal("gemini.synthetic.pro", row.StableModelId);
				Assert.Equal(40, row.UsedPercent);
				Assert.Equal(60, row.RemainingPercent);
				Assert.Null(row.ResetsAt);
			});
		Assert.NotNull(result.Report.UsageSemanticCapture);
		Assert.DoesNotContain(
			Identity,
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(Reset, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AccountIdentity",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"UsedPercent",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"RemainingPercent",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ResetsAt",
			serializedReport,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunR1Async_WhenAfterUsageSettingsCheckpointFails_DropsSemanticUsage()
	{
		const string Identity =
			"PRIVATE.R1.AFTER-USAGE.IDENTITY@EXAMPLE.INVALID";
		const string QuotaRow =
			"Gemini Synthetic Flash | 31% | 69% | 2026-07-17T13:31:00.0000000+00:00";
		const string Reset = "2026-07-17T13:31:00.0000000+00:00";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			Identity,
			"single",
			QuotaRow,
			"Gemini Synthetic Pro | 42% | 58% | -");
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen);
		string settingsPath =
			profile.CaptureProfile.NonCredentialSettingsFiles.Single();
		FakeSession session = new(
			prompt,
			CreateR1UsageOutput(prompt, reviewedScreen),
			onWriteInput: () => File.AppendAllText(settingsPath, " "));

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));
		AntigravityLiveR0Report safety = result.Report.SafetyReport;

		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.False(safety.IsCaptureSuccessful);
		Assert.NotNull(safety.UsageCapture);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.DoesNotContain(
			AntigravityLiveR0FailureReason.UsageSemanticLayoutNotObserved,
			safety.FailureReasons);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, safety.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsDrift,
			safety.FailureReasons);
		Assert.Equal(
			AntigravitySettingsDriftStage.AfterUsageOrCaptureFailure,
			safety.FirstSettingsDriftStage);
		AssertR1FailedResultDoesNotExposeUsage(
			result,
			Identity,
			QuotaRow,
			Reset);
	}

	[Fact]
	public async Task RunR1Async_WhenCleanupFails_DropsSemanticUsage()
	{
		const string Identity =
			"PRIVATE.R1.CLEANUP.IDENTITY@EXAMPLE.INVALID";
		const string QuotaRow =
			"Gemini Synthetic Flash | 37% | 63% | 2026-07-17T14:37:00.0000000+00:00";
		const string Reset = "2026-07-17T14:37:00.0000000+00:00";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			Identity,
			"single",
			QuotaRow,
			"Gemini Synthetic Pro | 48% | 52% | -");
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen);
		FakeSession session = new(
			prompt,
			CreateR1UsageOutput(prompt, reviewedScreen),
			terminateException:
				new IOException("Synthetic R1 cleanup failure."));

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));
		AntigravityLiveR0Report safety = result.Report.SafetyReport;

		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.False(safety.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Passed,
			safety.UsageCaptureGate);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Equal(AntigravityLiveR0GateStatus.Failed, safety.CleanupGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.CleanupFailed,
			safety.FailureReasons);
		AssertR1FailedResultDoesNotExposeUsage(
			result,
			Identity,
			QuotaRow,
			Reset);
	}

	[Fact]
	public async Task RunR1Async_WithCoarseEquivalentWrongLiteral_FailsSemanticGateWithoutRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			"reviewed@example.invalid",
			"single",
			"Gemini Synthetic Flash | 10% | 90% | -",
			"Gemini Synthetic Pro | 20% | 80% | -");
		string[] wrongScreen = (string[])reviewedScreen.Clone();
		wrongScreen[4] = "Model IX | Used | Remaining | Reset";
		byte[] reviewedUsage = CreateR1UsageOutput(prompt, reviewedScreen);
		byte[] wrongUsage = CreateR1UsageOutput(prompt, wrongScreen);
		(string reviewedCoarse, _) = ComputeFingerprints(reviewedUsage, 120, 30);
		(string wrongCoarse, _) = ComputeFingerprints(wrongUsage, 120, 30);
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen,
			usageTimeout: TimeSpan.FromMilliseconds(50));
		FakeSession session = new(prompt, wrongUsage);

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));

		Assert.Equal(reviewedScreen[4].Length, wrongScreen[4].Length);
		Assert.Equal(reviewedCoarse, wrongCoarse);
		AssertR1SemanticFailure(result, session);
	}

	[Fact]
	public async Task RunR1Async_WithUnreviewedExtraChrome_FailsSemanticGateWithoutRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			"reviewed@example.invalid",
			"single",
			"Gemini Synthetic Flash | 10% | 90% | -",
			"Gemini Synthetic Pro | 20% | 80% | -");
		string[] unreviewedScreen = (string[])reviewedScreen.Clone();
		unreviewedScreen[^1] = "UNREVIEWED EXTRA CHROME";
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen,
			usageTimeout: TimeSpan.FromMilliseconds(50));
		FakeSession session = new(
			prompt,
			CreateR1UsageOutput(prompt, unreviewedScreen));

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));

		AssertR1SemanticFailure(result, session);
	}

	[Fact]
	public async Task RunR1Async_WithTopPage_FailsClosedWithoutNavigationWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] reviewedScreen = CreateR1Screen(
			"reviewed@example.invalid",
			"single",
			"Gemini Synthetic Flash | 10% | 90% | -",
			"Gemini Synthetic Pro | 20% | 80% | -");
		string[] topScreen = CreateR1Screen(
			"reviewed@example.invalid",
			"top",
			"Gemini Synthetic Flash | 10% | 90% | -",
			"Gemini Synthetic Pro | 20% | 80% | -");
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			reviewedScreen,
			usageTimeout: TimeSpan.FromMilliseconds(50));
		FakeSession session = new(
			prompt,
			CreateR1UsageOutput(prompt, topScreen));

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));

		AssertR1SemanticFailure(result, session);
		Assert.DoesNotContain(
			session.Writes,
			write => !string.Equals(
				Encoding.UTF8.GetString(write),
				"/usage\r",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunR1Async_WhenDynamicValuesChangeDuringPolling_ResetsSemanticStability()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 PROMPT READY");
		string[] firstScreen = CreateR1Screen(
			"first@example.invalid",
			"single",
			"Gemini Synthetic Flash | 10% | 90% | -",
			"Gemini Synthetic Pro | 20% | 80% | -");
		string[] secondScreen = CreateR1Screen(
			"second@example.invalid",
			"single",
			"Gemini Synthetic Flash | 30% | 70% | -",
			"Gemini Synthetic Pro | 40% | 60% | -");
		string[] finalScreen = CreateR1Screen(
			"final@example.invalid",
			"single",
			"Gemini Synthetic Flash | 55% | 45% | -",
			"Gemini Synthetic Pro | 65% | 35% | -");
		AntigravityLiveR1Profile profile = CreateR1Profile(
			temporaryDirectory,
			prompt,
			firstScreen,
			usageTimeout: TimeSpan.FromMilliseconds(200),
			stableScreenDuration: TimeSpan.FromMilliseconds(20),
			pollInterval: TimeSpan.FromMilliseconds(5));
		byte[] initialOutput = CreateR1UsageOutput(prompt, firstScreen);
		byte[] secondOutput = AppendR1UsageFrame(initialOutput, secondScreen);
		byte[] thirdOutput = AppendR1UsageFrame(secondOutput, finalScreen);
		byte[] fourthOutput = AppendR1UsageFrame(thirdOutput, secondScreen);
		byte[] finalOutput = AppendR1UsageFrame(fourthOutput, finalScreen);
		byte[][] transitions =
		{
			secondOutput,
			thirdOutput,
			fourthOutput,
			finalOutput
		};
		int postWriteSnapshotCount = 0;
		FakeSession session = new(
			prompt,
			initialOutput,
			onGetOutputSnapshot: (_, currentSession) =>
			{
				if (currentSession.WriteInputCallCount != 1)
				{
					return;
				}

				postWriteSnapshotCount++;
				int transitionIndex = postWriteSnapshotCount - 2;

				if ((transitionIndex >= 0) &&
					(transitionIndex < transitions.Length))
				{
					currentSession.SetOutput(transitions[transitionIndex]);
				}
			});

		AntigravityLiveR1RunResult result = await CreateR1Runner(session)
			.RunR1Async(profile, new AntigravityLiveR1Consent(true));
		AntigravityUsageResult usage = Assert.IsType<AntigravityUsageResult>(
			result.Usage);

		Assert.True(result.Report.IsCaptureSuccessful);
		Assert.True(postWriteSnapshotCount >= 9);
		Assert.Equal("final@example.invalid", usage.AccountIdentity);
		Assert.Equal(55, usage.Rows[0].UsedPercent);
		Assert.Equal(65, usage.Rows[1].UsedPercent);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
	}

	[Fact]
	public async Task RunR1SectionAsync_WithReviewedFullPage_ReturnsFourWindowsAndSafeReport()
	{
		const string Identity =
			"PRIVATE.SECTION.IDENTITY.SENTINEL@EXAMPLE.INVALID";
		const string Credits = "987654321";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		string[] lines = CreateR1SectionLines(Identity, Credits);
		AntigravityLiveR1SectionProfile profile = CreateR1SectionProfile(
			temporaryDirectory,
			prompt,
			lines);
		FakeSession session = new(
			prompt,
			CreateR1SectionUsageOutput(prompt, lines));

		AntigravityLiveR1SectionRunResult result =
			await CreateR1Runner(session).RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true));
		AntigravityUsageR1SectionPage page =
			Assert.IsType<AntigravityUsageR1SectionPage>(result.Page);
		AntigravityLiveR0Report safety = result.Report.SafetyReport;
		string serializedReport = JsonSerializer.Serialize(result.Report);

		Assert.True(result.Report.IsCaptureSuccessful);
		Assert.False(result.Report.IsR1Go);
		Assert.True(safety.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0GateStatus.Passed, safety.UsageCaptureGate);
		Assert.Equal(AntigravityLiveR0GateStatus.NotVerified, safety.IdentityGate);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			safety.ModelInvocationGate);
		Assert.Equal(1, safety.InputWriteAttemptCount);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
		Assert.Equal(Identity.ToLowerInvariant(), page.AccountIdentity);
		Assert.Equal(2, page.Sections.Count);
		Assert.All(page.Sections, section => Assert.Equal(2, section.Windows.Count));
		Assert.Equal(4, result.Report.UsageSemanticCapture!.WindowCount);
		Assert.DoesNotContain(
			Identity,
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(Credits, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("80.00", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("2h 30m", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AccountIdentity",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain("RemainingPercent", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("ResetsIn", serializedReport, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunR1SectionAsync_WhenRequiredSettingsBaselineMismatches_FailsBeforeSessionStart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		string[] lines = CreateR1SectionLines(
			"reviewed@example.invalid",
			"123");
		AntigravityLiveR1SectionProfile profile = CreateR1SectionProfile(
			temporaryDirectory,
			prompt,
			lines);
		FakeSession session = new(
			prompt,
			CreateR1SectionUsageOutput(prompt, lines));

		AntigravityLiveR1SectionRunResult result =
			await CreateR1Runner(session).RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true),
				CancellationToken.None,
				new string('D', 64));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Page);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsBaselineContractMismatch,
			result.Report.SafetyReport.FailureReasons);
		Assert.Equal(0, result.Report.SafetyReport.InputWriteAttemptCount);
		Assert.Equal(0, result.Report.SafetyReport.InputWriteCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunR1SectionAsync_WithProgrammaticallyConstructedPartialSpec_FailsBeforeAnyDownstreamAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		string[] lines = CreateR1SectionLines(
			"reviewed@example.invalid",
			"123");
		AntigravityLiveR1SectionProfile validProfile =
			CreateR1SectionProfile(temporaryDirectory, prompt, lines);
		AntigravityUsageR1SectionSpec spec = CreateR1SectionSpec(
			withPagination: false);
		AntigravityUsageR1SectionSpec partial = spec with
		{
			Lines = spec.Lines.Take(spec.Lines.Count - 1).ToArray()
		};
		AntigravityUsageR1SectionLayout layout = new(
			partial,
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				partial),
			new[] { new string('A', 64) }.ToFrozenSet(StringComparer.Ordinal),
			new string('B', 64),
			new string('C', 64));
		AntigravityLiveR1SectionProfile profile = new(
			validProfile.CaptureProfile,
			layout);
		FakeCapabilityValidator capability = new();
		FakeExistingProcessGate processGate = new(
			AntigravityExistingProcessGateResult.Clear);
		FakeSession session = new(
			prompt,
			CreateR1SectionUsageOutput(prompt, lines));
		FakeSessionFactory factory = new(session);
		FakeSettingsSentinelPolicy settingsPolicy = new();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			TimeProvider.System,
			settingsPolicy);

		AntigravityLiveR1SectionRunResult result =
			await runner.RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Page);
		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			result.Report.SafetyReport.FailureReasons);
		Assert.Equal(0, settingsPolicy.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Empty(session.Writes);
	}

	[Theory]
	[InlineData("literal")]
	[InlineData("meter")]
	[InlineData("availability")]
	[InlineData("partial")]
	public async Task RunR1SectionAsync_WithUnreviewedPage_FailsWithoutRetryOrNavigation(
		string mutation)
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		bool withPagination = string.Equals(
			mutation,
			"partial",
			StringComparison.Ordinal);
		string[] reviewedLines = CreateR1SectionLines(
			"reviewed@example.invalid",
			"123",
			withPagination);
		AntigravityLiveR1SectionProfile profile = CreateR1SectionProfile(
			temporaryDirectory,
			prompt,
			reviewedLines,
			withPagination,
			usageTimeout: TimeSpan.FromMilliseconds(50));
		string[] observedLines = (string[])reviewedLines.Clone();

		switch (mutation)
		{
			case "literal":
				observedLines[1] = "SYNTHETIC SECTION CHROMF";
				break;
			case "meter":
				observedLines[5] = $"  {R1SectionMeter(7)} 80.00%";
				break;
			case "availability":
				observedLines[9] = "  quota waiting";
				break;
			case "partial":
				observedLines[17] = "[1-20 of 30 lines]";
				break;
			default:
				throw new InvalidOperationException();
		}

		FakeSession session = new(
			prompt,
			CreateR1SectionUsageOutput(prompt, observedLines));
		AntigravityLiveR1SectionRunResult result =
			await CreateR1Runner(session).RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true));

		AssertR1SectionSemanticFailure(result, session);
		Assert.DoesNotContain(
			session.Writes,
			write => !string.Equals(
				Encoding.UTF8.GetString(write),
				"/usage\r",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunR1SectionAsync_WhenDynamicValuesChangeDuringPolling_ResetsSemanticStability()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		string[] firstLines = CreateR1SectionLines(
			"first@example.invalid",
			"111",
			remainingPercent: 80);
		string[] secondLines = CreateR1SectionLines(
			"second@example.invalid",
			"222",
			remainingPercent: 70);
		string[] finalLines = CreateR1SectionLines(
			"final@example.invalid",
			"333",
			remainingPercent: 60);
		AntigravityLiveR1SectionProfile profile = CreateR1SectionProfile(
			temporaryDirectory,
			prompt,
			firstLines,
			usageTimeout: TimeSpan.FromMilliseconds(200),
			stableScreenDuration: TimeSpan.FromMilliseconds(20),
			pollInterval: TimeSpan.FromMilliseconds(5));
		byte[] firstOutput = CreateR1SectionUsageOutput(prompt, firstLines);
		byte[] secondOutput = AppendR1SectionUsageFrame(firstOutput, secondLines);
		byte[] thirdOutput = AppendR1SectionUsageFrame(secondOutput, finalLines);
		byte[] fourthOutput = AppendR1SectionUsageFrame(thirdOutput, secondLines);
		byte[] finalOutput = AppendR1SectionUsageFrame(fourthOutput, finalLines);
		byte[][] transitions =
		{
			secondOutput,
			thirdOutput,
			fourthOutput,
			finalOutput
		};
		int postWriteSnapshotCount = 0;
		FakeSession session = new(
			prompt,
			firstOutput,
			onGetOutputSnapshot: (_, currentSession) =>
			{
				if (currentSession.WriteInputCallCount != 1)
				{
					return;
				}

				postWriteSnapshotCount++;
				int transitionIndex = postWriteSnapshotCount - 2;

				if ((transitionIndex >= 0) &&
					(transitionIndex < transitions.Length))
				{
					currentSession.SetOutput(transitions[transitionIndex]);
				}
			});

		AntigravityLiveR1SectionRunResult result =
			await CreateR1Runner(session).RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true));
		AntigravityUsageR1SectionPage page =
			Assert.IsType<AntigravityUsageR1SectionPage>(result.Page);

		Assert.True(result.Report.IsCaptureSuccessful);
		Assert.True(postWriteSnapshotCount >= 9);
		Assert.Equal("final@example.invalid", page.AccountIdentity);
		Assert.Equal(
			60,
			page.Sections[0].Windows[0].Capacity.RemainingPercent);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
	}

	[Theory]
	[InlineData("settings")]
	[InlineData("cleanup")]
	[InlineData("cancel")]
	public async Task RunR1SectionAsync_WhenSafetyFails_DropsPageAndSemanticCapture(
		string failure)
	{
		const string Identity =
			"PRIVATE.SECTION.FAILURE.IDENTITY@EXAMPLE.INVALID";
		const string Credits = "654321987";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("R1 SECTION PROMPT READY");
		string[] lines = CreateR1SectionLines(Identity, Credits);
		AntigravityLiveR1SectionProfile profile = CreateR1SectionProfile(
			temporaryDirectory,
			prompt,
			lines);
		string settingsPath =
			profile.CaptureProfile.NonCredentialSettingsFiles.Single();
		FakeSession session = new(
			prompt,
			CreateR1SectionUsageOutput(prompt, lines),
			onWriteInput: failure == "settings"
				? () => File.AppendAllText(settingsPath, " ")
				: null,
			writeInputException: failure == "cancel"
				? new OperationCanceledException("Synthetic cancellation.")
				: null,
			terminateException: failure == "cleanup"
				? new IOException("Synthetic cleanup failure.")
				: null);

		AntigravityLiveR1SectionRunResult result =
			await CreateR1Runner(session).RunR1SectionAsync(
				profile,
				new AntigravityLiveR1SectionConsent(true));
		string serializedReport = JsonSerializer.Serialize(result.Report);

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Page);
		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.DoesNotContain(
			Identity,
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(Credits, serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("80.00", serializedReport, StringComparison.Ordinal);
		Assert.DoesNotContain("2h 30m", serializedReport, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WithStableUsage_ReturnsMemoryOnlySnapshotAndSafeReport()
	{
		const string Identity =
			"PRIVATE.CALIBRATION.IDENTITY.SENTINEL@EXAMPLE.INVALID";
		const string UsageLine =
			"PRIVATE CALIBRATION QUOTA 17% RESET 2026-07-17T18:00:00Z";
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE CALIBRATION PROMPT");
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			stableScreenDuration: TimeSpan.FromMilliseconds(5),
			pollInterval: TimeSpan.FromMilliseconds(5));
		byte[] usage = CreateUsageOutput(
			prompt,
			$"{Identity}\r\n{UsageLine}");
		FakeSession session = new(prompt, usage);
		AntigravityLiveR0Runner runner = CreateR1Runner(session);
		string settingsBaselineFingerprint =
			await runner
				.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
					profile);

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await runner.RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true));
		TerminalScreenSnapshot snapshot =
			Assert.IsType<TerminalScreenSnapshot>(result.Snapshot);
		AntigravityLiveR0Report safety = result.Report.SafetyReport;
		string serializedReport = JsonSerializer.Serialize(result.Report);
		string serializedRawResult = JsonSerializer.Serialize(result);

		Assert.True(result.Report.IsCaptureSuccessful);
		Assert.True(safety.IsCaptureSuccessful);
		Assert.Equal(AntigravityLiveR0Mode.CaptureUsage, safety.Mode);
		Assert.Equal(1, safety.InputWriteAttemptCount);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Equal(
			AntigravityLiveR0GateStatus.NotVerified,
			safety.UsageCaptureGate);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
		Assert.Contains(
			snapshot.Lines,
			line => line.Contains(Identity, StringComparison.Ordinal));
		Assert.Contains(
			snapshot.Lines,
			line => line.Contains(UsageLine, StringComparison.Ordinal));
		Assert.Equal(
			AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile,
				snapshot.IsAlternateScreen,
				settingsBaselineFingerprint),
			result.Report.CaptureContractFingerprint);
		Assert.Matches(
			"^[0-9A-F]{64}$",
			result.Report.CaptureContractFingerprint!);
		Assert.DoesNotContain(
			Identity,
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			UsageLine,
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Identity,
			serializedRawResult,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"Snapshot",
			serializedRawResult,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WithoutIndependentConsent_DoesNotStart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		FakeCapabilityValidator capability = new();
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeExistingProcessGate processGate = CreateClearProcessGate();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			new AdvancingTimeProvider(),
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await runner.RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(false));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Contains(
			AntigravityLiveR0FailureReason.ConsentMissing,
			result.Report.SafetyReport.FailureReasons);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Empty(session.Writes);
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task RunR1PrivateCalibrationCaptureAsync_WithAnyLegacyUsagePin_RejectsProfileBeforeStart(
		bool hasStructuralPin,
		bool hasExactPin)
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory) with
		{
			ExpectedUsageStructuralFingerprint = hasStructuralPin
				? new string('C', 64)
				: null,
			ExpectedExactUsageFingerprint = hasExactPin
				? new string('D', 64)
				: null
		};
		FakeCapabilityValidator capability = new();
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeExistingProcessGate processGate = CreateClearProcessGate();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			new AdvancingTimeProvider(),
			new FakeSettingsSentinelPolicy());

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await runner.RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Contains(
			AntigravityLiveR0FailureReason.InvalidProfile,
			result.Report.SafetyReport.FailureReasons);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WhenCancelledAfterWrite_DropsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE CALIBRATION PROMPT");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt),
			writeInputException: new OperationCanceledException(
				"Synthetic private calibration cancellation."));

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await CreateR1Runner(session).RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Equal(1, result.Report.SafetyReport.InputWriteAttemptCount);
		Assert.Equal(0, result.Report.SafetyReport.InputWriteCount);
		Assert.Contains(
			AntigravityLiveR0FailureReason.Cancelled,
			result.Report.SafetyReport.FailureReasons);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WhenSettingsDriftAfterUsage_DropsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE CALIBRATION PROMPT");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		string settingsPath = profile.NonCredentialSettingsFiles.Single();
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt, "PRIVATE DRIFT USAGE"),
			onWriteInput: () => File.AppendAllText(settingsPath, " "));

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await CreateR1Runner(session).RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Equal(1, result.Report.SafetyReport.InputWriteCount);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Failed,
			result.Report.SafetyReport.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsDrift,
			result.Report.SafetyReport.FailureReasons);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WhenCleanupFails_DropsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE CALIBRATION PROMPT");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeSession session = new(
			prompt,
			CreateUsageOutput(prompt, "PRIVATE CLEANUP USAGE"),
			terminateException: new IOException(
				"Synthetic private calibration cleanup failure."));

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await CreateR1Runner(session).RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true));

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Equal(1, result.Report.SafetyReport.InputWriteCount);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Failed,
			result.Report.SafetyReport.CleanupGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.CleanupFailed,
			result.Report.SafetyReport.FailureReasons);
	}

	[Fact]
	public void PrivateCalibrationCaptureContract_IsCanonicalAndChangeSensitive()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR0Profile profile = CreateProfile(temporaryDirectory);
		string settingsBaselineFingerprint = new('9', 64);
		string baseline =
			AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile,
				isAlternateScreen: true,
				settingsBaselineFingerprint);
		Dictionary<string, string> reorderedEnvironment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (KeyValuePair<string, string> pair in
			profile.Environment.Reverse())
		{
			reorderedEnvironment.Add(pair.Key, pair.Value);
		}

		Assert.Equal(
			baseline,
			AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile with { Environment = reorderedEnvironment },
				isAlternateScreen: true,
				settingsBaselineFingerprint));
		Assert.Matches("^[0-9A-F]{64}$", baseline);

		AntigravityCliFingerprint executable = profile.ExecutableFingerprint;
		AntigravityLiveR0Profile[] variants =
		{
			profile with { Id = profile.Id + ".changed" },
			profile with
			{
				ExecutableFingerprint = executable with
				{
					AbsolutePath = executable.AbsolutePath + ".changed"
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					CliVersion = executable.CliVersion + ".changed"
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					FileVersion = executable.FileVersion + ".changed"
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					ProductVersion = executable.ProductVersion + ".changed"
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					Sha256 = new string('C', 64)
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					WinVerifyTrustStatus = 1
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					SignerSubject = executable.SignerSubject + ".changed"
				}
			},
			profile with
			{
				ExecutableFingerprint = executable with
				{
					SignerThumbprint = new string('D', 40)
				}
			},
			profile with
			{
				WorkingDirectory = profile.WorkingDirectory + ".changed"
			},
			profile with
			{
				Environment = new Dictionary<string, string>(
					profile.Environment,
					StringComparer.OrdinalIgnoreCase)
				{
					["TERM"] = "changed"
				}
			},
			profile with { Columns = (short)(profile.Columns + 1) },
			profile with { Rows = (short)(profile.Rows + 1) },
			profile with
			{
				ExpectedPromptStructuralFingerprint = new string('E', 64)
			},
			profile with
			{
				ExpectedExactPromptFingerprint = new string('F', 64)
			},
			profile with
			{
				ExactPromptFingerprintHmacKey = new byte[32]
			},
			profile with
			{
				NonCredentialSettingsFiles = new[]
				{
					profile.NonCredentialSettingsFiles.Single() + ".changed"
				}
			},
			profile with
			{
				MaximumSavedOutputBytes =
					profile.MaximumSavedOutputBytes + 1
			},
			profile with
			{
				PromptTimeout = profile.PromptTimeout + TimeSpan.FromTicks(1)
			},
			profile with
			{
				UsageTimeout = profile.UsageTimeout + TimeSpan.FromTicks(1)
			},
			profile with
			{
				StableScreenDuration =
					profile.StableScreenDuration + TimeSpan.FromTicks(1)
			},
			profile with
			{
				PollInterval = profile.PollInterval + TimeSpan.FromTicks(1)
			},
			profile with
			{
				CleanupTimeout = profile.CleanupTimeout + TimeSpan.FromTicks(1)
			}
		};

		foreach (AntigravityLiveR0Profile variant in variants)
		{
			Assert.NotEqual(
				baseline,
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					variant,
					isAlternateScreen: true,
					settingsBaselineFingerprint));
		}

		Assert.NotEqual(
			baseline,
			AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile,
				isAlternateScreen: false,
				settingsBaselineFingerprint));
		Assert.NotEqual(
			baseline,
			AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile,
				isAlternateScreen: true,
				new string('8', 64)));
	}

	[Fact]
	public async Task PrivateCalibrationSettingsBaseline_WhenFileChanges_ChangesKeyedFingerprint()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE SETTINGS BASELINE PROMPT");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		AntigravityLiveR0Runner runner = CreateR1Runner(CreateSession());
		string first = await runner
			.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(profile);
		File.AppendAllText(
			profile.NonCredentialSettingsFiles.Single(),
			" changed");
		string second = await runner
			.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(profile);

		Assert.Matches("^[0-9A-F]{64}$", first);
		Assert.Matches("^[0-9A-F]{64}$", second);
		Assert.NotEqual(first, second);
	}

	[Fact]
	public async Task RunR1PrivateCalibrationCaptureAsync_WhenRequiredSettingsBaselineChanges_FailsBeforeLiveStart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] prompt = CreatePromptOutput("PRIVATE SETTINGS RACE PROMPT");
		(string coarse, string exact) = ComputeFingerprints(prompt, 120, 30);
		AntigravityLiveR0Profile profile = CreateProfile(
			temporaryDirectory,
			coarse,
			exact);
		FakeCapabilityValidator capability = new();
		FakeSession session = CreateSession();
		FakeSessionFactory factory = new(session);
		FakeExistingProcessGate processGate = CreateClearProcessGate();
		AntigravityLiveR0Runner runner = new(
			capability,
			factory,
			processGate,
			new AdvancingTimeProvider(),
			new FakeSettingsSentinelPolicy());
		string requiredSettingsBaselineFingerprint = await runner
			.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(profile);
		File.AppendAllText(
			profile.NonCredentialSettingsFiles.Single(),
			" changed-after-preflight");

		AntigravityLiveR1PrivateCalibrationCaptureResult result =
			await runner.RunR1PrivateCalibrationCaptureAsync(
				profile,
				new AntigravityLiveR1PrivateCalibrationCaptureConsent(true),
				CancellationToken.None,
				requiredSettingsBaselineFingerprint);

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Report.CaptureContractFingerprint);
		Assert.Null(result.Snapshot);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Failed,
			result.Report.SafetyReport.SettingsGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.SettingsBaselineContractMismatch,
			result.Report.SafetyReport.FailureReasons);
		Assert.Equal(0, capability.CallCount);
		Assert.Equal(0, factory.CallCount);
		Assert.Equal(0, processGate.CallCount);
		Assert.Empty(session.Writes);
	}

	[Fact]
	public void WindowsSettingsSentinelPolicy_WithExactCurrentUserPathAndProfile_Accepts()
	{
		string userProfile = Path.GetFullPath(Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile));
		string settingsPath = Path.GetFullPath(Path.Combine(
			userProfile,
			".gemini",
			"antigravity-cli",
			"settings.json"));
		WindowsAntigravityLiveSettingsSentinelPolicy policy = new();

		bool isAllowed = policy.IsAllowed(
			settingsPath,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["USERPROFILE"] = userProfile
			});

		Assert.True(isAllowed);
	}

	[Fact]
	public void WindowsSettingsSentinelPolicy_WithDummyPath_Rejects()
	{
		string userProfile = Path.GetFullPath(Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile));
		string dummyPath = Path.GetFullPath(Path.Combine(
			userProfile,
			".gemini",
			"antigravity-cli",
			"dummy.json"));
		WindowsAntigravityLiveSettingsSentinelPolicy policy = new();

		bool isAllowed = policy.IsAllowed(
			dummyPath,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["USERPROFILE"] = userProfile
			});

		Assert.False(isAllowed);
	}

	[Fact]
	public void WindowsSettingsSentinelPolicy_WithMismatchedHome_Rejects()
	{
		string userProfile = Path.GetFullPath(Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile));
		string settingsPath = Path.GetFullPath(Path.Combine(
			userProfile,
			".gemini",
			"antigravity-cli",
			"settings.json"));
		WindowsAntigravityLiveSettingsSentinelPolicy policy = new();

		bool isAllowed = policy.IsAllowed(
			settingsPath,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["USERPROFILE"] = userProfile,
				["HOME"] = Path.Combine(userProfile, "different-home")
			});

		Assert.False(isAllowed);
	}

	[Fact]
	public void WindowsSettingsSentinelPolicy_WithoutUserProfile_Rejects()
	{
		string userProfile = Path.GetFullPath(Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile));
		string settingsPath = Path.GetFullPath(Path.Combine(
			userProfile,
			".gemini",
			"antigravity-cli",
			"settings.json"));
		WindowsAntigravityLiveSettingsSentinelPolicy policy = new();

		bool isAllowed = policy.IsAllowed(
			settingsPath,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

		Assert.False(isAllowed);
	}

	private static void AssertR1SemanticFailure(
		AntigravityLiveR1RunResult result,
		FakeSession session)
	{
		AntigravityLiveR0Report safety = result.Report.SafetyReport;

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.False(result.Report.IsR1Go);
		Assert.False(safety.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Failed,
			safety.UsageCaptureGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.UsageSemanticLayoutNotObserved,
			safety.FailureReasons);
		Assert.Equal(1, safety.InputWriteAttemptCount);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.Null(result.Usage);
		Assert.Equal(1, session.WriteInputCallCount);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
	}

	private static void AssertR1SectionSemanticFailure(
		AntigravityLiveR1SectionRunResult result,
		FakeSession session)
	{
		AntigravityLiveR0Report safety = result.Report.SafetyReport;

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.False(result.Report.IsR1Go);
		Assert.False(safety.IsCaptureSuccessful);
		Assert.Equal(
			AntigravityLiveR0GateStatus.Failed,
			safety.UsageCaptureGate);
		Assert.Contains(
			AntigravityLiveR0FailureReason.UsageSemanticLayoutNotObserved,
			safety.FailureReasons);
		Assert.Equal(1, safety.InputWriteAttemptCount);
		Assert.Equal(1, safety.InputWriteCount);
		Assert.Null(result.Report.UsageSemanticCapture);
		Assert.Null(result.Page);
		Assert.Equal(1, session.WriteInputCallCount);
		Assert.Single(session.Writes);
		Assert.Equal("/usage\r", Encoding.UTF8.GetString(session.Writes[0]));
	}

	private static void AssertR1FailedResultDoesNotExposeUsage(
		AntigravityLiveR1RunResult result,
		string identity,
		string quotaRow,
		string reset)
	{
		JsonSerializerOptions jsonOptions = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		string serializedReport = JsonSerializer.Serialize(
			result.Report,
			jsonOptions);

		Assert.False(result.Report.IsCaptureSuccessful);
		Assert.Null(result.Usage);
		Assert.DoesNotContain(
			identity,
			serializedReport,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			quotaRow,
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			reset,
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AccountIdentity",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"UsedPercent",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"RemainingPercent",
			serializedReport,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ResetsAt",
			serializedReport,
			StringComparison.Ordinal);
	}

	private static byte[] AppendR1UsageFrame(
		byte[] cumulativeOutput,
		IReadOnlyList<string> screenLines,
		string terminalSuffix = "")
	{
		byte[] frame = Encoding.UTF8.GetBytes(
			"\u001b[2J\u001b[H" +
			string.Join("\r\n", screenLines) +
			terminalSuffix);
		return cumulativeOutput.Concat(frame).ToArray();
	}

	private static byte[] AppendR1SectionUsageFrame(
		byte[] cumulativeOutput,
		IReadOnlyList<string> screenLines)
	{
		byte[] frame = Encoding.UTF8.GetBytes(
			"\u001b[2J\u001b[H" + string.Join("\r\n", screenLines));
		return cumulativeOutput.Concat(frame).ToArray();
	}

	private static byte[] CreateR1SectionUsageOutput(
		byte[] promptOutput,
		IReadOnlyList<string> screenLines)
	{
		return AppendR1SectionUsageFrame(promptOutput, screenLines);
	}

	private static AntigravityLiveR1SectionProfile CreateR1SectionProfile(
		TemporaryDirectory temporaryDirectory,
		byte[] prompt,
		IReadOnlyList<string> reviewedLines,
		bool withPagination = false,
		TimeSpan? usageTimeout = null,
		TimeSpan? stableScreenDuration = null,
		TimeSpan? pollInterval = null)
	{
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		AntigravityLiveR0Profile captureProfile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: usageTimeout ?? TimeSpan.FromMilliseconds(100),
			stableScreenDuration:
				stableScreenDuration ?? TimeSpan.FromMilliseconds(5),
			pollInterval: pollInterval ?? TimeSpan.FromMilliseconds(5));
		AntigravityUsageR1SectionSpec spec =
			CreateR1SectionSpec(withPagination);
		AntigravityUsageR1SectionParseResult calibration =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CaptureTerminal(CreateR1SectionUsageOutput(prompt, reviewedLines)),
				spec);
		AntigravityUsageR1SectionLayoutFile layoutFile = new(
			AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			spec,
			calibration.Capture.SchemaFingerprint,
			new[] { calibration.Capture.PageFingerprint },
			new string('B', 64),
			new string('C', 64));
		return new AntigravityLiveR1SectionProfile(
			captureProfile,
			layoutFile.ToLayout());
	}

	private static AntigravityUsageR1SectionSpec CreateR1SectionSpec(
		bool withPagination)
	{
		string[] renderedLines = CreateR1SectionLines(
			"reviewed@example.invalid",
			"123",
			withPagination);
		AntigravityUsageR1SectionLineRule[] lines = Enumerable
			.Range(0, 30)
			.Select(index => R1SectionFixedLine(index, renderedLines[index]))
			.ToArray();
		lines[0] = R1SectionOpaqueLine(0, "cwd=", " | ready");
		lines[2] = R1SectionIdentityLine(2);
		lines[3] = R1SectionOwnedFixedLine(
			3,
			AntigravityUsageR1SectionLineRole.SectionHeading,
			"gemini",
			null,
			"Gemini");
		lines[4] = R1SectionOwnedFixedLine(
			4,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"gemini",
			"gemini.weekly",
			"Weekly limit");
		lines[5] = R1SectionCapacityLine(5, "gemini", "gemini.weekly");
		lines[6] = R1SectionResetLine(6, "gemini", "gemini.weekly");
		lines[7] = R1SectionOwnedFixedLine(
			7,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"gemini",
			"gemini.rolling-5h",
			"5-hour limit");
		lines[8] = R1SectionCapacityLine(
			8,
			"gemini",
			"gemini.rolling-5h");
		lines[9] = R1SectionResetLine(
			9,
			"gemini",
			"gemini.rolling-5h");
		lines[10] = R1SectionOwnedFixedLine(
			10,
			AntigravityUsageR1SectionLineRole.SectionHeading,
			"claude",
			null,
			"Claude");
		lines[11] = R1SectionOwnedFixedLine(
			11,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"claude",
			"claude.weekly",
			"Weekly limit");
		lines[12] = R1SectionCapacityLine(12, "claude", "claude.weekly");
		lines[13] = R1SectionResetLine(13, "claude", "claude.weekly");
		lines[14] = R1SectionOwnedFixedLine(
			14,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"claude",
			"claude.rolling-5h",
			"5-hour limit");
		lines[15] = R1SectionCapacityLine(
			15,
			"claude",
			"claude.rolling-5h");
		lines[16] = R1SectionResetLine(
			16,
			"claude",
			"claude.rolling-5h");
		lines[17] = withPagination
			? R1SectionPaginationLine(17)
			: R1SectionFixedLine(17, "FULL PAGE");
		lines[18] = R1SectionUnsignedAmountLine(18, "credits=");
		lines[19] = R1SectionOpaqueLine(19, "footer=", " | done");

		return new AntigravityUsageR1SectionSpec(
			2,
			"synthetic-live-r1-section-v2",
			120,
			30,
			true,
			new[]
			{
				R1SectionRule("gemini", "Gemini", 0),
				R1SectionRule("claude", "Claude", 1)
			},
			lines,
			AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible,
			withPagination
				? AntigravityUsageR1SectionPaginationMode.Counter
				: AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible);
	}

	private static AntigravityUsageR1SectionRule R1SectionRule(
		string id,
		string renderedLiteral,
		int order)
	{
		return new AntigravityUsageR1SectionRule(
			id,
			renderedLiteral,
			order,
			new[]
			{
				new AntigravityUsageR1SectionWindowRule(
					$"{id}.weekly",
					"Weekly limit",
					AntigravityUsageR1SectionWindowKind.Weekly,
					null,
					0,
					AntigravityUsageR1SectionCapacityPolicy.Percentage,
					AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability),
				new AntigravityUsageR1SectionWindowRule(
					$"{id}.rolling-5h",
					"5-hour limit",
					AntigravityUsageR1SectionWindowKind.RollingHours,
					5,
					1,
					AntigravityUsageR1SectionCapacityPolicy.Percentage,
					AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability)
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionFixedLine(
		int row,
		string literal)
	{
		return R1SectionOwnedFixedLine(
			row,
			AntigravityUsageR1SectionLineRole.Fixed,
			null,
			null,
			literal);
	}

	private static AntigravityUsageR1SectionLineRule R1SectionOwnedFixedLine(
		int row,
		AntigravityUsageR1SectionLineRole role,
		string? sectionId,
		string? windowId,
		string literal)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			role,
			sectionId,
			windowId,
			new[]
			{
				R1SectionPattern(
					$"section-line-{row:00}-exact",
					new AntigravityUsageR1SectionLiteralToken(literal))
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionOpaqueLine(
		int row,
		string prefix,
		string suffix)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.OuterChrome,
			null,
			null,
			new[]
			{
				R1SectionPattern(
					$"section-line-{row:00}-opaque",
					new AntigravityUsageR1SectionLiteralToken(prefix),
					new AntigravityUsageR1SectionOpaqueToken(200),
					new AntigravityUsageR1SectionLiteralToken(suffix))
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionIdentityLine(
		int row)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.Identity,
			null,
			null,
			new[]
			{
				R1SectionPattern(
					"section-account-identity",
					new AntigravityUsageR1SectionLiteralToken("Account: ["),
					new AntigravityUsageR1SectionIdentityToken(),
					new AntigravityUsageR1SectionLiteralToken("]"))
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionCapacityLine(
		int row,
		string sectionId,
		string windowId)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.Capacity,
			sectionId,
			windowId,
			new[]
			{
				R1SectionPattern(
					$"{windowId}-meter",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionProgressMeterToken(
						new AntigravityUsageR1SectionProgressMeterRule(
							10,
							"#",
							".",
							AntigravityUsageR1SectionPercentMeaning.Remaining,
							AntigravityUsageR1SectionMeterRounding.Nearest)),
					new AntigravityUsageR1SectionLiteralToken(" "),
					new AntigravityUsageR1SectionPercentToken(
						AntigravityUsageR1SectionPercentMeaning.Remaining,
						2))
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionResetLine(
		int row,
		string sectionId,
		string windowId)
	{
		int maximumTotalMinutes = windowId.EndsWith(
			".weekly",
			StringComparison.Ordinal)
			? 168 * 60
			: 5 * 60;
		AntigravityUsageR1SectionCountdownRule countdownRule = new(
			" ",
			new[]
			{
				new AntigravityUsageR1SectionCountdownUnitRule(
					AntigravityUsageR1SectionCountdownUnit.Hours,
					"h",
					"h",
					0),
				new AntigravityUsageR1SectionCountdownUnitRule(
					AntigravityUsageR1SectionCountdownUnit.Minutes,
					"m",
					"m",
					1)
			},
			maximumTotalMinutes);

		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.Reset,
			sectionId,
			windowId,
			new[]
			{
				R1SectionPattern(
					$"{windowId}-countdown",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionPercentToken(
						AntigravityUsageR1SectionPercentMeaning.Remaining,
						0),
					new AntigravityUsageR1SectionLiteralToken(
						" remaining; refreshes in "),
					new AntigravityUsageR1SectionCountdownToken(countdownRule)),
				R1SectionPattern(
					$"{windowId}-available",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionAvailabilityToken(
						new[]
						{
							new AntigravityUsageR1SectionAvailabilityRule(
								"quota_available",
								"quota available")
						}))
			});
	}

	private static AntigravityUsageR1SectionLineRule
		R1SectionUnsignedAmountLine(int row, string prefix)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.NonQuotaDynamic,
			null,
			null,
			new[]
			{
				R1SectionPattern(
					$"section-line-{row:00}-amount",
					new AntigravityUsageR1SectionLiteralToken(prefix),
					new AntigravityUsageR1SectionUnsignedAmountToken(1, 9))
			});
	}

	private static AntigravityUsageR1SectionLineRule R1SectionPaginationLine(
		int row)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"section-line-{row:00}",
			AntigravityUsageR1SectionLineRole.Pagination,
			null,
			null,
			new[]
			{
				R1SectionPattern(
					"section-pagination",
					new AntigravityUsageR1SectionLiteralToken("["),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.Start),
					new AntigravityUsageR1SectionLiteralToken("-"),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.End),
					new AntigravityUsageR1SectionLiteralToken(" of "),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.Total),
					new AntigravityUsageR1SectionLiteralToken(" lines]"))
			});
	}

	private static AntigravityUsageR1SectionLinePattern R1SectionPattern(
		string patternId,
		params AntigravityUsageR1SectionToken[] tokens)
	{
		return new AntigravityUsageR1SectionLinePattern(patternId, tokens);
	}

	private static string[] CreateR1SectionLines(
		string identity,
		string credits,
		bool withPagination = false,
		int remainingPercent = 80)
	{
		string capacity =
			$"  {R1SectionMeter(remainingPercent / 10)} {remainingPercent}.00%";
		string[] lines = Enumerable.Repeat(string.Empty, 30).ToArray();
		lines[0] = "cwd=C:\\private\\sentinel | ready";
		lines[1] = "SYNTHETIC SECTION CHROME";
		lines[2] = $"Account: [{identity}]";
		lines[3] = "Gemini";
		lines[4] = "Weekly limit";
		lines[5] = capacity;
		lines[6] = $"  {remainingPercent}% remaining; refreshes in 2h 30m";
		lines[7] = "5-hour limit";
		lines[8] = capacity;
		lines[9] = "  quota available";
		lines[10] = "Claude";
		lines[11] = "Weekly limit";
		lines[12] = capacity;
		lines[13] = "  quota available";
		lines[14] = "5-hour limit";
		lines[15] = capacity;
		lines[16] = "  quota available";
		lines[17] = withPagination
			? "[1-30 of 30 lines]"
			: "FULL PAGE";
		lines[18] = $"credits={credits}";
		lines[19] = "footer=private-session | done";
		return lines;
	}

	private static string R1SectionMeter(int filledCells)
	{
		return string.Concat(
			new string('#', filledCells),
			new string('.', 10 - filledCells));
	}

	private static AntigravityLiveR1Profile CreateR1Profile(
		TemporaryDirectory temporaryDirectory,
		byte[] prompt,
		IReadOnlyList<string> reviewedScreen,
		TimeSpan? usageTimeout = null,
		TimeSpan? stableScreenDuration = null,
		TimeSpan? pollInterval = null)
	{
		(string promptCoarse, string promptExact) = ComputeFingerprints(
			prompt,
			120,
			30);
		AntigravityLiveR0Profile captureProfile = CreateProfile(
			temporaryDirectory,
			promptCoarse,
			promptExact,
			usageTimeout: usageTimeout ?? TimeSpan.FromMilliseconds(100),
			stableScreenDuration:
				stableScreenDuration ?? TimeSpan.FromMilliseconds(5),
			pollInterval: pollInterval ?? TimeSpan.FromMilliseconds(5));
		AntigravityUsageR1ReviewedSpec spec = CreateR1ReviewedSpec();
		AntigravityUsageR1ParseResult calibration =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CaptureTerminal(CreateR1UsageOutput(prompt, reviewedScreen)),
				spec);
		AntigravityUsageR1ReviewedLayoutFile reviewedLayoutFile = new(
			AntigravityUsageR1SchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			spec,
			calibration.Capture.SchemaFingerprint,
			new[] { calibration.Capture.PageFingerprint });
		return new AntigravityLiveR1Profile(
			captureProfile,
			reviewedLayoutFile.ToLayout());
	}

	private static AntigravityUsageR1ReviewedSpec CreateR1ReviewedSpec()
	{
		return new AntigravityUsageR1ReviewedSpec(
			1,
			"synthetic-live-r1",
			120,
			30,
			true,
			new[] { "SYNTHETIC R1 CHROME" },
			"SYNTHETIC R1 AGY MODEL QUOTAS",
			"Account: ",
			"Page: ",
			"Model ID | Used | Remaining | Reset",
			"END SYNTHETIC R1 MODEL QUOTAS",
			" | ",
			new[] { "SYNTHETIC R1 FOOTER CHROME" },
			new[]
			{
				new AntigravityUsageR1ModelRule(
					"gemini.synthetic.flash",
					"Gemini Synthetic Flash",
					true,
					0),
				new AntigravityUsageR1ModelRule(
					"gemini.synthetic.pro",
					"Gemini Synthetic Pro",
					true,
					1)
			},
			AntigravityUsageR1PaginationPolicy.RequireSinglePage);
	}

	private static AntigravityLiveR0Runner CreateR1Runner(FakeSession session)
	{
		return new AntigravityLiveR0Runner(
			new FakeCapabilityValidator(),
			new FakeSessionFactory(session),
			CreateClearProcessGate(),
			new AdvancingTimeProvider(),
			new FakeSettingsSentinelPolicy());
	}

	private static string[] CreateR1Screen(
		string identity,
		string pageKind,
		string firstModelRow,
		string secondModelRow)
	{
		List<string> lines = new()
		{
			"SYNTHETIC R1 CHROME",
			"SYNTHETIC R1 AGY MODEL QUOTAS",
			$"Account: {identity}",
			$"Page: {pageKind}",
			"Model ID | Used | Remaining | Reset",
			firstModelRow,
			secondModelRow,
			"END SYNTHETIC R1 MODEL QUOTAS",
			"SYNTHETIC R1 FOOTER CHROME"
		};
		lines.AddRange(Enumerable.Repeat(string.Empty, 30 - lines.Count));
		return lines.ToArray();
	}

	private static byte[] CreateR1UsageOutput(
		byte[] promptOutput,
		IReadOnlyList<string> screenLines,
		string terminalSuffix = "")
	{
		return AppendR1UsageFrame(
			promptOutput,
			screenLines,
			terminalSuffix);
	}

	private static TerminalScreenSnapshot CaptureTerminal(byte[] output)
	{
		TerminalScreenState terminal = new(120, 30, 64 * 1024);
		terminal.Feed(output);
		return terminal.Capture();
	}

	private static FakeExistingProcessGate CreateClearProcessGate()
	{
		return new FakeExistingProcessGate(
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear,
			AntigravityExistingProcessGateResult.Clear);
	}

	private static AntigravityLiveR0Profile CreateProfile(
		TemporaryDirectory temporaryDirectory,
		string expectedPromptStructuralFingerprint = "",
		string expectedExactPromptFingerprint = "",
		TimeSpan? promptTimeout = null,
		TimeSpan? usageTimeout = null,
		TimeSpan? stableScreenDuration = null,
		TimeSpan? pollInterval = null)
	{
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A });
		string settingsPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"settings.json"));
		File.WriteAllText(settingsPath, "{}");
		AntigravityCliFingerprint fingerprint = new(
			Path.GetFullPath(executablePath),
			"1.1.3",
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			new string('A', 64),
			0,
			"CN=Synthetic",
			new string('B', 40));
		return new AntigravityLiveR0Profile(
			"synthetic-live-r0",
			fingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["AGY_CLI_DISABLE_AUTO_UPDATE"] = "false",
				["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ??
					"C:\\Windows",
				["USERPROFILE"] = temporaryDirectory.Path
			},
			120,
			30,
			expectedPromptStructuralFingerprint,
			ExactFingerprintKey,
			expectedExactPromptFingerprint,
			null,
			new[]
			{
				settingsPath
			},
			64 * 1024,
			promptTimeout ?? TimeSpan.FromSeconds(1),
			usageTimeout ?? TimeSpan.FromSeconds(1),
			stableScreenDuration ?? TimeSpan.FromMilliseconds(1),
			pollInterval ?? TimeSpan.FromMilliseconds(10),
			TimeSpan.FromSeconds(1));
	}

	private static FakeSession CreateSession()
	{
		byte[] prompt = CreatePromptOutput("PROMPT READY");
		return new FakeSession(prompt, CreateUsageOutput(prompt));
	}

	private static byte[] CreatePromptOutput(string text)
	{
		return Encoding.UTF8.GetBytes(
			"\u001b[?1049h\u001b[2J\u001b[H" + text);
	}

	private static byte[] CreateUsageOutput(
		byte[] promptOutput,
		string text = "USAGE PANEL IS LONGER")
	{
		byte[] usage = Encoding.UTF8.GetBytes(
			"\u001b[2J\u001b[H" + text);
		return promptOutput.Concat(usage).ToArray();
	}

	private static (string Coarse, string Exact) ComputeFingerprints(
		byte[] output,
		short columns,
		short rows)
	{
		TerminalScreenState terminal = new(columns, rows, 64 * 1024);
		terminal.Feed(output);
		TerminalScreenSnapshot snapshot = terminal.Capture();
		return (
			AntigravityRedactedStructuralCapture.Create(snapshot)
				.StructuralFingerprint,
			AntigravityLocalExactScreenFingerprint.Compute(
				snapshot,
				ExactFingerprintKey));
	}
}
