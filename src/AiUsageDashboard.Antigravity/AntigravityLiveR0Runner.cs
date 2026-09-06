using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal interface IAntigravityLiveCapabilityValidator
{
	Task<AntigravityCliCapabilityValidationResult> ValidateAsync(
		AntigravityCliFingerprint expectedFingerprint,
		CancellationToken cancellationToken);
}

internal interface IAntigravityLiveConPtySessionFactory
{
	ValueTask<IConPtySession> StartAsync(
		ConPtyStartRequest request,
		CancellationToken cancellationToken);
}

internal interface IAntigravityExistingProcessGate
{
	ValueTask<AntigravityExistingProcessGateResult> CheckAsync(
		string absoluteExecutablePath,
		int? allowedProcessId,
		CancellationToken cancellationToken);
}

internal interface IAntigravityLiveSettingsSentinelPolicy
{
	bool IsAllowed(
		string absoluteSentinelPath,
		IReadOnlyDictionary<string, string> environment);
}

internal sealed class DefaultAntigravityLiveCapabilityValidator :
	IAntigravityLiveCapabilityValidator
{
	public Task<AntigravityCliCapabilityValidationResult> ValidateAsync(
		AntigravityCliFingerprint expectedFingerprint,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expectedFingerprint);
		AntigravityCliCapabilityValidator validator = new(
			new[] { expectedFingerprint });
		return validator.ValidateAsync(
			expectedFingerprint.AbsolutePath,
			cancellationToken);
	}
}

internal sealed class WindowsAntigravityLiveConPtySessionFactory :
	IAntigravityLiveConPtySessionFactory
{
	public async ValueTask<IConPtySession> StartAsync(
		ConPtyStartRequest request,
		CancellationToken cancellationToken)
	{
		return await WindowsConPtySession.StartAsync(request, cancellationToken);
	}
}

internal sealed class WindowsAntigravityExistingProcessGate :
	IAntigravityExistingProcessGate
{
	public ValueTask<AntigravityExistingProcessGateResult> CheckAsync(
		string absoluteExecutablePath,
		int? allowedProcessId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absoluteExecutablePath);
		cancellationToken.ThrowIfCancellationRequested();
		string targetPath;

		try
		{
			targetPath = Path.GetFullPath(absoluteExecutablePath);
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			return ValueTask.FromResult(
				AntigravityExistingProcessGateResult.Indeterminate);
		}

		string processName = Path.GetFileNameWithoutExtension(targetPath);
		Process[] processes;

		try
		{
			processes = Process.GetProcessesByName(processName);
		}
		catch (Exception exception) when (
			(exception is InvalidOperationException) ||
			(exception is NotSupportedException) ||
			(exception is PlatformNotSupportedException) ||
			(exception is Win32Exception))
		{
			return ValueTask.FromResult(
				AntigravityExistingProcessGateResult.Indeterminate);
		}

		try
		{
			foreach (Process process in processes)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (allowedProcessId.HasValue &&
					(process.Id == allowedProcessId.Value))
				{
					continue;
				}

				try
				{
					string? candidatePath = process.MainModule?.FileName;

					if (candidatePath is null)
					{
						return ValueTask.FromResult(
							AntigravityExistingProcessGateResult.Indeterminate);
					}

					if (string.Equals(
						Path.GetFullPath(candidatePath),
						targetPath,
						StringComparison.OrdinalIgnoreCase))
					{
						return ValueTask.FromResult(
							AntigravityExistingProcessGateResult.Detected);
					}
				}
				catch (InvalidOperationException)
				{
					// A same-name process that exited during inspection is no longer a blocker.
				}
				catch (Exception exception) when (
					(exception is NotSupportedException) ||
					(exception is PlatformNotSupportedException) ||
					(exception is Win32Exception))
				{
					return ValueTask.FromResult(
						AntigravityExistingProcessGateResult.Indeterminate);
				}
			}

			return ValueTask.FromResult(
				AntigravityExistingProcessGateResult.Clear);
		}
		finally
		{
			foreach (Process process in processes)
			{
				process.Dispose();
			}
		}
	}
}

internal sealed class WindowsAntigravityLiveSettingsSentinelPolicy :
	IAntigravityLiveSettingsSentinelPolicy
{
	public bool IsAllowed(
		string absoluteSentinelPath,
		IReadOnlyDictionary<string, string> environment)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absoluteSentinelPath);
		ArgumentNullException.ThrowIfNull(environment);

		try
		{
			if (!Path.IsPathFullyQualified(absoluteSentinelPath))
			{
				return false;
			}

			string currentUserProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(currentUserProfile))
			{
				return false;
			}

			string normalizedUserProfile = Path.GetFullPath(currentUserProfile);
			string expectedSentinelPath = Path.GetFullPath(Path.Combine(
				normalizedUserProfile,
				".gemini",
				"antigravity-cli",
				"settings.json"));
			string[] configuredUserProfiles = environment
				.Where(pair => string.Equals(
					pair.Key,
					"USERPROFILE",
					StringComparison.OrdinalIgnoreCase))
				.Select(pair => pair.Value)
				.ToArray();

			if ((configuredUserProfiles.Length != 1) ||
				!MatchesPath(
					configuredUserProfiles[0],
					normalizedUserProfile))
			{
				return false;
			}

			string[] configuredHomes = environment
				.Where(pair => string.Equals(
					pair.Key,
					"HOME",
					StringComparison.OrdinalIgnoreCase))
				.Select(pair => pair.Value)
				.ToArray();

			if ((configuredHomes.Length > 1) ||
				((configuredHomes.Length == 1) &&
					!string.IsNullOrWhiteSpace(configuredHomes[0]) &&
					!MatchesPath(
						configuredHomes[0],
						normalizedUserProfile)))
			{
				return false;
			}

			return string.Equals(
				Path.GetFullPath(absoluteSentinelPath),
				expectedSentinelPath,
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			return false;
		}
	}

	private static bool MatchesPath(string candidate, string expected)
	{
		return !string.IsNullOrWhiteSpace(candidate) &&
			Path.IsPathFullyQualified(candidate) &&
			string.Equals(
				Path.GetFullPath(candidate),
				expected,
				StringComparison.OrdinalIgnoreCase);
	}
}

internal sealed class AntigravityCaptureStabilityTracker
{
	private string? _candidateFingerprint;
	private TimeSpan? _stableSince;

	internal bool Observe(
		string? candidateFingerprint,
		TimeSpan observedElapsed,
		TimeSpan stableDuration)
	{
		if (candidateFingerprint is null)
		{
			Reset();
			return false;
		}

		if (!string.Equals(
			_candidateFingerprint,
			candidateFingerprint,
			StringComparison.Ordinal))
		{
			_candidateFingerprint = candidateFingerprint;
			_stableSince = observedElapsed;
		}

		return _stableSince.HasValue &&
			((observedElapsed - _stableSince.Value) >= stableDuration);
	}

	internal void Reset()
	{
		_candidateFingerprint = null;
		_stableSince = null;
	}
}

internal sealed class AntigravityLiveR0Runner
{
	private static readonly IReadOnlySet<string> AllowedEnvironmentVariableNames =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"AGY_CLI_DISABLE_AUTO_UPDATE",
			"APPDATA",
			"COLORTERM",
			"HOME",
			"HOMEDRIVE",
			"HOMEPATH",
			"LANG",
			"LOCALAPPDATA",
			"ProgramData",
			"ProgramFiles",
			"ProgramFiles(x86)",
			"ProgramW6432",
			"SystemRoot",
			"TEMP",
			"TERM",
			"TMP",
			"USERPROFILE",
			"WINDIR"
		};

	private sealed class ControlFailureException : Exception
	{
		internal AntigravityLiveR0FailureReason Reason { get; }

		internal AntigravityTerminalCaptureFailureReason
			TerminalCaptureFailureReason { get; }

		internal AntigravityTerminalControlDiagnostic? TerminalControlDiagnostic
		{
			get;
		}

		internal ControlFailureException(
			AntigravityLiveR0FailureReason reason,
			AntigravityTerminalCaptureFailureReason terminalCaptureFailureReason =
				AntigravityTerminalCaptureFailureReason.None,
			AntigravityTerminalControlDiagnostic? terminalControlDiagnostic = null)
			: base("The AGY live R0 operation failed closed.")
		{
			Reason = reason;
			TerminalCaptureFailureReason = terminalCaptureFailureReason;
			TerminalControlDiagnostic = terminalControlDiagnostic;
		}
	}

	private sealed record OutputObservation(
		TerminalScreenSnapshot Snapshot,
		AntigravityRedactedStructuralCapture RedactedCapture,
		string ExactLocalFingerprint,
		long TotalBytesRead,
		int SavedByteCount);

	private sealed record RunCoreResult(
		AntigravityLiveR0Report Report,
		AntigravityUsageR1ParseResult? SemanticUsage,
		AntigravityUsageR1SectionParseResult? SectionSemanticUsage,
		TerminalScreenSnapshot? UsageSnapshot,
		string? SettingsBaselineFingerprint);

	private sealed class UsageTimeoutDiagnosticBuilder
	{
		private const int MaximumDistinctCandidateCount = 16;
		private readonly long _baselineTotalBytesRead;
		private readonly int _baselineSavedByteCount;
		private readonly HashSet<string> _distinctNonPromptCandidates =
			new(StringComparer.Ordinal);
		private readonly string? _expectedExactFingerprint;
		private readonly string? _expectedStructuralFingerprint;
		private readonly Func<OutputObservation, string?>?
			_acceptedCandidateFingerprintFactory;
		private readonly AntigravityCaptureStabilityTracker
			_mismatchStability = new();
		private readonly string _rejectedExactFingerprint;
		private bool _hasObservedAtomicPending;
		private bool _hasObservedExpectedExactFingerprint;
		private bool _hasObservedExpectedSemanticLayout;
		private bool _hasObservedExpectedStructuralFingerprint;
		private bool _hasObservedNonPromptCandidate;
		private bool _hasObservedPrompt;
		private bool _isCurrentMismatchStable;
		private bool _lastCandidateMatchesExact;
		private bool _lastCandidateMatchesSemanticLayout;
		private bool _lastCandidateMatchesStructural;
		private AntigravityUsageTimeoutObservationKind _lastObservationKind;
		private AntigravityUsageTimeoutClassification
			_lastStableMismatchClassification;
		private long _lastTotalBytesRead;
		private int _lastSavedByteCount;

		internal int AtomicPendingObservationCount { get; private set; }

		internal AntigravityRedactedStructuralCapture? LastNonPromptCapture
		{
			get;
			private set;
		}

		internal string? LastNonPromptExactLocalFingerprint
		{
			get;
			private set;
		}

		internal AntigravityRedactedStructuralCapture?
			LastStableMismatchCapture { get; private set; }

		internal string? LastStableMismatchExactLocalFingerprint
		{
			get;
			private set;
		}

		internal bool LastStableMismatchMatchesExactFingerprint
		{
			get;
			private set;
		}

		internal bool LastStableMismatchMatchesStructuralFingerprint
		{
			get;
			private set;
		}

		internal bool LastStableMismatchMatchesSemanticLayout
		{
			get;
			private set;
		}

		internal int NonPromptCandidateCount { get; private set; }

		internal int ObservationCount { get; private set; }

		internal bool MatchedTooLate { get; private set; }

		internal UsageTimeoutDiagnosticBuilder(
			OutputObservation baseline,
			string? expectedStructuralFingerprint,
			string? expectedExactFingerprint,
			string rejectedExactFingerprint,
			Func<OutputObservation, string?>?
				acceptedCandidateFingerprintFactory)
		{
			_baselineTotalBytesRead = baseline.TotalBytesRead;
			_baselineSavedByteCount = baseline.SavedByteCount;
			_lastTotalBytesRead = baseline.TotalBytesRead;
			_lastSavedByteCount = baseline.SavedByteCount;
			_expectedStructuralFingerprint = expectedStructuralFingerprint;
			_expectedExactFingerprint = expectedExactFingerprint;
			_rejectedExactFingerprint = rejectedExactFingerprint;
			_acceptedCandidateFingerprintFactory =
				acceptedCandidateFingerprintFactory;
		}

		internal AntigravityUsageTimeoutDiagnostic Build(bool matchedTooLate)
		{
			bool hasPostWriteOutput =
				(_lastTotalBytesRead > _baselineTotalBytesRead) ||
				(_lastSavedByteCount > _baselineSavedByteCount);
			AntigravityUsageTimeoutClassification classification;

			if (matchedTooLate)
			{
				classification =
					AntigravityUsageTimeoutClassification.MatchedTooLate;
			}
			else if (_lastStableMismatchClassification !=
				AntigravityUsageTimeoutClassification.None)
			{
				classification = _lastStableMismatchClassification;
			}
			else if (!hasPostWriteOutput)
			{
				classification = AntigravityUsageTimeoutClassification
					.NoPostWriteOutput;
			}
			else if (_lastObservationKind ==
				AntigravityUsageTimeoutObservationKind.AtomicPending)
			{
				classification = AntigravityUsageTimeoutClassification
					.AtomicPending;
			}
			else if (_hasObservedNonPromptCandidate)
			{
				classification = AntigravityUsageTimeoutClassification
					.UnstableNonPromptCandidate;
			}
			else if (_hasObservedPrompt)
			{
				classification = AntigravityUsageTimeoutClassification
					.PromptStillVisible;
			}
			else
			{
				classification = AntigravityUsageTimeoutClassification.None;
			}

			return new AntigravityUsageTimeoutDiagnostic(
				classification,
				_lastObservationKind,
				hasPostWriteOutput,
				_hasObservedPrompt,
				_hasObservedNonPromptCandidate,
				_hasObservedAtomicPending,
				_hasObservedExpectedStructuralFingerprint,
				_hasObservedExpectedExactFingerprint,
				ObservationCount,
				AtomicPendingObservationCount,
				NonPromptCandidateCount,
				_distinctNonPromptCandidates.Count,
				_baselineTotalBytesRead,
				_lastTotalBytesRead,
				_baselineSavedByteCount,
				_lastSavedByteCount,
				LastNonPromptCapture,
				LastNonPromptExactLocalFingerprint,
				LastStableMismatchMatchesStructuralFingerprint,
				LastStableMismatchMatchesExactFingerprint,
				LastStableMismatchCapture,
				LastStableMismatchExactLocalFingerprint,
				_hasObservedExpectedSemanticLayout,
				LastStableMismatchMatchesSemanticLayout);
		}

		internal void Observe(
			OutputObservation? observation,
			long lastTotalBytesRead,
			int lastSavedByteCount,
			TimeSpan observedElapsed,
			TimeSpan stableDuration)
		{
			_lastTotalBytesRead = lastTotalBytesRead;
			_lastSavedByteCount = lastSavedByteCount;

			if (observation is null)
			{
				_lastObservationKind =
					AntigravityUsageTimeoutObservationKind.AtomicPending;
				_hasObservedAtomicPending = true;
				AtomicPendingObservationCount++;
				ResetMismatchStability();
				return;
			}

			ObservationCount++;

			if (string.Equals(
				observation.ExactLocalFingerprint,
				_rejectedExactFingerprint,
				StringComparison.Ordinal))
			{
				_lastObservationKind =
					AntigravityUsageTimeoutObservationKind.Prompt;
				_hasObservedPrompt = true;
				ResetMismatchStability();
				return;
			}

			_lastObservationKind =
				AntigravityUsageTimeoutObservationKind.NonPromptCandidate;
			_hasObservedNonPromptCandidate = true;
			NonPromptCandidateCount++;
			LastNonPromptCapture = observation.RedactedCapture;
			LastNonPromptExactLocalFingerprint =
				observation.ExactLocalFingerprint;
			_lastCandidateMatchesStructural =
				(_expectedStructuralFingerprint is null) ||
				string.Equals(
					observation.RedactedCapture.StructuralFingerprint,
					_expectedStructuralFingerprint,
					StringComparison.Ordinal);
			_lastCandidateMatchesExact =
				(_expectedExactFingerprint is null) ||
				string.Equals(
					observation.ExactLocalFingerprint,
					_expectedExactFingerprint,
					StringComparison.Ordinal);
			_lastCandidateMatchesSemanticLayout =
				(_acceptedCandidateFingerprintFactory is null) ||
				(_acceptedCandidateFingerprintFactory(observation) is not null);
			_hasObservedExpectedStructuralFingerprint |=
				(_expectedStructuralFingerprint is not null) &&
				_lastCandidateMatchesStructural;
			_hasObservedExpectedExactFingerprint |=
				(_expectedExactFingerprint is not null) &&
				_lastCandidateMatchesExact;
			_hasObservedExpectedSemanticLayout |=
				(_acceptedCandidateFingerprintFactory is not null) &&
				_lastCandidateMatchesSemanticLayout;
			string candidateFingerprint = string.Concat(
				observation.RedactedCapture.StructuralFingerprint,
				observation.ExactLocalFingerprint);

			if (_distinctNonPromptCandidates.Count <
				MaximumDistinctCandidateCount)
			{
				_distinctNonPromptCandidates.Add(candidateFingerprint);
			}

			if (_lastCandidateMatchesStructural &&
				_lastCandidateMatchesExact &&
				_lastCandidateMatchesSemanticLayout)
			{
				ResetMismatchStability();
				return;
			}

			_isCurrentMismatchStable = _mismatchStability.Observe(
				candidateFingerprint,
				observedElapsed,
				stableDuration);

			if (_isCurrentMismatchStable)
			{
				_lastStableMismatchClassification =
					!_lastCandidateMatchesSemanticLayout
						? AntigravityUsageTimeoutClassification
							.StableSemanticMismatch
						: _lastCandidateMatchesStructural
						? AntigravityUsageTimeoutClassification
							.StableExactMismatch
						: AntigravityUsageTimeoutClassification
							.StableStructuralMismatch;
				LastStableMismatchMatchesStructuralFingerprint =
					_lastCandidateMatchesStructural;
				LastStableMismatchMatchesExactFingerprint =
					_lastCandidateMatchesExact;
				LastStableMismatchMatchesSemanticLayout =
					_lastCandidateMatchesSemanticLayout;
				LastStableMismatchCapture = observation.RedactedCapture;
				LastStableMismatchExactLocalFingerprint =
					observation.ExactLocalFingerprint;
			}
		}

		internal void MarkMatchedTooLate()
		{
			MatchedTooLate = true;
		}

		private void ResetMismatchStability()
		{
			_mismatchStability.Reset();
			_isCurrentMismatchStable = false;
		}
	}

	private sealed class OutputObserver
	{
		private const int MaximumDiagnosticNumericParameter = 1_000_000;
		private const int MaximumDiagnosticNumericParameterCount = 32;
		private const string AtomicUpdatePendingMessage =
			"The terminal screen is between atomic updates.";
		private const string InvalidSnapshotDimensionsMessage =
			"The terminal snapshot dimensions are invalid.";
		private const string InvalidSnapshotLineMessage =
			"The terminal snapshot contains an invalid line.";
		private const string UnsupportedControlCharacterMessage =
			"The terminal stream contains an unsupported control character.";
		private const string UnsupportedControlSequencePrefix =
			"Unsupported or malformed terminal control sequence:";
		private const string UnsupportedUnicodeRuneMessage =
			"The terminal stream contains an unsupported Unicode rune.";
		private static readonly IReadOnlyList<int> EmptyNumericParameters =
			Array.Empty<int>();
		private readonly int _maximumOutputBytes;
		private readonly TerminalScreenState _terminal;
		private int _fedByteCount;

		internal int LastSavedByteCount { get; private set; }

		internal long LastTotalBytesRead { get; private set; }

		internal OutputObserver(
			short columns,
			short rows,
			int maximumOutputBytes)
		{
			_maximumOutputBytes = maximumOutputBytes;
			_terminal = new TerminalScreenState(
				columns,
				rows,
				maximumOutputBytes);
		}

		internal OutputObservation? TryCapture(
			IConPtySession session,
			ReadOnlySpan<byte> exactFingerprintHmacKey)
		{
			ConPtyOutputSnapshot output;

			try
			{
				output = session.GetOutputSnapshot(_fedByteCount);
			}
			catch
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.OutputReadFailed);
			}

			if (output.ReadFailure is not null)
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.OutputReadFailed);
			}

			if (output.IsSavedByteLimitExceeded ||
				(output.TotalBytesRead > _maximumOutputBytes) ||
				(output.SavedByteCount > _maximumOutputBytes))
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.OutputLimitExceeded);
			}

			if ((output.SavedByteOffset != _fedByteCount) ||
				(output.SavedByteCount < output.SavedByteOffset) ||
				(output.SavedBytes.Length !=
					(output.SavedByteCount - output.SavedByteOffset)) ||
				(output.TotalBytesRead < output.SavedByteCount))
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.OutputReadFailed);
			}

			LastTotalBytesRead = output.TotalBytesRead;
			LastSavedByteCount = output.SavedByteCount;

			if (output.SavedBytes.Length > 0)
			{
				try
				{
					_terminal.Feed(output.SavedBytes.Span);
					_fedByteCount = output.SavedByteCount;
				}
				catch (Exception exception)
				{
					throw CreateTerminalCaptureFailure(exception);
				}
			}

			try
			{
				TerminalScreenSnapshot terminalSnapshot = _terminal.Capture();
				return new OutputObservation(
					terminalSnapshot,
					AntigravityRedactedStructuralCapture.Create(terminalSnapshot),
					AntigravityLocalExactScreenFingerprint.Compute(
						terminalSnapshot,
						exactFingerprintHmacKey),
					output.TotalBytesRead,
					output.SavedByteCount);
			}
			catch (InvalidOperationException exception) when (string.Equals(
				exception.Message,
				AtomicUpdatePendingMessage,
				StringComparison.Ordinal))
			{
				return null;
			}
			catch (Exception exception)
			{
				throw CreateTerminalCaptureFailure(exception);
			}
		}

		private static AntigravityTerminalCaptureFailureReason
			ClassifyTerminalCaptureFailure(Exception exception)
		{
			if (exception is DecoderFallbackException)
			{
				return AntigravityTerminalCaptureFailureReason.InvalidUtf8;
			}

			if (exception is ArgumentOutOfRangeException)
			{
				return AntigravityTerminalCaptureFailureReason
					.UnsupportedScreenOperation;
			}

			if (exception is not InvalidDataException invalidDataException)
			{
				return AntigravityTerminalCaptureFailureReason.Unexpected;
			}

			string message = invalidDataException.Message;

			if (message.StartsWith(
				UnsupportedControlSequencePrefix,
				StringComparison.Ordinal) ||
				string.Equals(
					message,
					UnsupportedControlCharacterMessage,
					StringComparison.Ordinal) ||
				string.Equals(
					message,
					"Terminal control sequence exceeded the allowed length.",
					StringComparison.Ordinal) ||
				string.Equals(
					message,
					"Terminal OSC payload exceeded the allowed length.",
					StringComparison.Ordinal) ||
				string.Equals(
					message,
					"The terminal stream ended inside a control sequence.",
					StringComparison.Ordinal))
			{
				return AntigravityTerminalCaptureFailureReason
					.UnsupportedControlSequence;
			}

			if (string.Equals(
				message,
				UnsupportedUnicodeRuneMessage,
				StringComparison.Ordinal))
			{
				return AntigravityTerminalCaptureFailureReason
					.UnsupportedUnicodeRune;
			}

			if (message.StartsWith(
				"Unsupported erase-display mode:",
				StringComparison.Ordinal) ||
				message.StartsWith(
					"Unsupported erase-line mode:",
					StringComparison.Ordinal) ||
				string.Equals(
					message,
					"The terminal viewport cannot display a double-width rune.",
					StringComparison.Ordinal))
			{
				return AntigravityTerminalCaptureFailureReason
					.UnsupportedScreenOperation;
			}

			if (string.Equals(
				message,
				InvalidSnapshotDimensionsMessage,
				StringComparison.Ordinal) ||
				string.Equals(
					message,
					InvalidSnapshotLineMessage,
					StringComparison.Ordinal))
			{
				return AntigravityTerminalCaptureFailureReason.InvalidSnapshot;
			}

			return AntigravityTerminalCaptureFailureReason.Unexpected;
		}

		private static ControlFailureException CreateTerminalCaptureFailure(
			Exception exception)
		{
			AntigravityTerminalCaptureFailureReason failureReason =
				ClassifyTerminalCaptureFailure(exception);
			return new ControlFailureException(
				AntigravityLiveR0FailureReason.TerminalCaptureFailed,
				failureReason,
				TryCreateControlDiagnostic(exception, failureReason));
		}

		private static bool IsCsiFinalByte(char value)
		{
			return (value >= 0x40) && (value <= 0x7E);
		}

		private static bool IsCsiPrivateMarker(char value)
		{
			return (value == '?') ||
				(value == '>') ||
				(value == '<') ||
				(value == '=');
		}

		private static AntigravityTerminalControlDiagnostic ParseControlDiagnostic(
			ReadOnlySpan<char> descriptor)
		{
			if (descriptor.SequenceEqual("CSI".AsSpan()))
			{
				return ParseCsiDiagnostic(ReadOnlySpan<char>.Empty);
			}

			if (descriptor.StartsWith(
				"CSI ".AsSpan(),
				StringComparison.Ordinal))
			{
				return ParseCsiDiagnostic(descriptor[4..]);
			}

			if (descriptor.SequenceEqual("OSC".AsSpan()))
			{
				return ParseOscDiagnostic(ReadOnlySpan<char>.Empty);
			}

			if (descriptor.StartsWith(
				"OSC ".AsSpan(),
				StringComparison.Ordinal))
			{
				return ParseOscDiagnostic(descriptor[4..]);
			}

			if (descriptor.SequenceEqual("ESC".AsSpan()))
			{
				return ParseEscapeDiagnostic(ReadOnlySpan<char>.Empty);
			}

			if (descriptor.StartsWith(
				"ESC ".AsSpan(),
				StringComparison.Ordinal))
			{
				return ParseEscapeDiagnostic(descriptor[4..]);
			}

			return new AntigravityTerminalControlDiagnostic(
				AntigravityTerminalControlSequenceFamily.Other,
				FinalByteCode: null,
				IsPrivate: false,
				EmptyNumericParameters,
				HasUnparsedParameterBytes: descriptor.Length > 0);
		}

		private static AntigravityTerminalControlDiagnostic ParseCsiDiagnostic(
			ReadOnlySpan<char> content)
		{
			int? finalByteCode = null;
			ReadOnlySpan<char> body = content;

			if ((content.Length > 0) && IsCsiFinalByte(content[^1]))
			{
				finalByteCode = content[^1];
				body = content[..^1];
			}

			int? privateMarkerByteCode =
				(body.Length > 0) && IsCsiPrivateMarker(body[0])
					? body[0]
					: null;
			ReadOnlySpan<char> parameterBytes = privateMarkerByteCode.HasValue
				? body[1..]
				: body;

			if (!TryParseNumericParameters(
				parameterBytes,
				out IReadOnlyList<int> numericParameters,
				out bool usesColonSeparators,
				out bool hasEmptyParameterFields))
			{
				return new AntigravityTerminalControlDiagnostic(
					AntigravityTerminalControlSequenceFamily.Csi,
					finalByteCode,
					privateMarkerByteCode.HasValue,
					EmptyNumericParameters,
					HasUnparsedParameterBytes: true,
					privateMarkerByteCode,
					usesColonSeparators,
					hasEmptyParameterFields);
			}

			return new AntigravityTerminalControlDiagnostic(
				AntigravityTerminalControlSequenceFamily.Csi,
				finalByteCode,
				privateMarkerByteCode.HasValue,
				numericParameters,
				HasUnparsedParameterBytes: false,
				privateMarkerByteCode,
				usesColonSeparators,
				hasEmptyParameterFields);
		}

		private static AntigravityTerminalControlDiagnostic ParseEscapeDiagnostic(
			ReadOnlySpan<char> content)
		{
			bool hasProtocolByte =
				(content.Length == 1) && (content[0] <= 0x7F);
			return new AntigravityTerminalControlDiagnostic(
				AntigravityTerminalControlSequenceFamily.Escape,
				hasProtocolByte ? content[0] : null,
				IsPrivate: false,
				EmptyNumericParameters,
				HasUnparsedParameterBytes:
					(content.Length > 0) && !hasProtocolByte);
		}

		private static AntigravityTerminalControlDiagnostic ParseOscDiagnostic(
			ReadOnlySpan<char> content)
		{
			if (TryParseDecimal(content, out int command))
			{
				return new AntigravityTerminalControlDiagnostic(
					AntigravityTerminalControlSequenceFamily.Osc,
					FinalByteCode: null,
					IsPrivate: false,
					Array.AsReadOnly(new[] { command }),
					HasUnparsedParameterBytes: false);
			}

			return new AntigravityTerminalControlDiagnostic(
				AntigravityTerminalControlSequenceFamily.Osc,
				FinalByteCode: null,
				IsPrivate: false,
				EmptyNumericParameters,
				HasUnparsedParameterBytes: content.Length > 0);
		}

		private static bool TryCreateControlDiagnostic(
			Exception exception,
			AntigravityTerminalCaptureFailureReason failureReason,
			out AntigravityTerminalControlDiagnostic? diagnostic)
		{
			diagnostic = null;

			if ((failureReason != AntigravityTerminalCaptureFailureReason
					.UnsupportedControlSequence) ||
				(exception is not InvalidDataException invalidDataException))
			{
				return false;
			}

			string message = invalidDataException.Message;

			if (!message.StartsWith(
				UnsupportedControlSequencePrefix,
				StringComparison.Ordinal) ||
				(message.Length <= (UnsupportedControlSequencePrefix.Length + 2)) ||
				(message[UnsupportedControlSequencePrefix.Length] != ' ') ||
				(message[^1] != '.'))
			{
				return false;
			}

			ReadOnlySpan<char> descriptor = message.AsSpan(
				UnsupportedControlSequencePrefix.Length + 1,
				message.Length - UnsupportedControlSequencePrefix.Length - 2);
			diagnostic = ParseControlDiagnostic(descriptor);
			return true;
		}

		private static AntigravityTerminalControlDiagnostic?
			TryCreateControlDiagnostic(
				Exception exception,
				AntigravityTerminalCaptureFailureReason failureReason)
		{
			return TryCreateControlDiagnostic(
				exception,
				failureReason,
				out AntigravityTerminalControlDiagnostic? diagnostic)
				? diagnostic
				: null;
		}

		private static bool TryParseDecimal(
			ReadOnlySpan<char> value,
			out int parsedValue)
		{
			parsedValue = 0;

			if (value.Length == 0)
			{
				return false;
			}

			foreach (char character in value)
			{
				int digit = character - '0';

				if ((digit < 0) || (digit > 9) ||
					(parsedValue > ((int.MaxValue - digit) / 10)))
				{
					parsedValue = 0;
					return false;
				}

				parsedValue = (parsedValue * 10) + digit;
			}

			return true;
		}

		private static bool TryParseNumericParameters(
			ReadOnlySpan<char> body,
			out IReadOnlyList<int> numericParameters,
			out bool usesColonSeparators,
			out bool hasEmptyParameterFields)
		{
			numericParameters = EmptyNumericParameters;
			usesColonSeparators = false;
			hasEmptyParameterFields = false;

			if (body.Length == 0)
			{
				return true;
			}

			bool hasInvalidParameterByte = false;

			for (int index = 0; index < body.Length; index++)
			{
				char value = body[index];
				bool isColonSeparator = value == ':';
				bool isSeparator = isColonSeparator || (value == ';');

				usesColonSeparators |= isColonSeparator;

				if (isSeparator)
				{
					if ((index == 0) ||
						(body[index - 1] == ':') ||
						(body[index - 1] == ';'))
					{
						hasEmptyParameterFields = true;
					}
				}
				else if ((value < '0') || (value > '9'))
				{
					hasInvalidParameterByte = true;
				}
			}

			if ((body[^1] == ':') || (body[^1] == ';'))
			{
				hasEmptyParameterFields = true;
			}

			if (hasInvalidParameterByte)
			{
				return false;
			}

			List<int> values = new();
			int parameterStart = 0;

			for (int index = 0; index <= body.Length; index++)
			{
				if ((index < body.Length) &&
					(body[index] != ';') &&
					(body[index] != ':'))
				{
					continue;
				}

				ReadOnlySpan<char> parameter = body[parameterStart..index];

				if (parameter.Length == 0)
				{
					parameterStart = index + 1;
					continue;
				}

				if ((values.Count >= MaximumDiagnosticNumericParameterCount) ||
					!TryParseDecimal(parameter, out int value) ||
					(value > MaximumDiagnosticNumericParameter))
				{
					return false;
				}

				values.Add(value);
				parameterStart = index + 1;
			}

			numericParameters = Array.AsReadOnly(values.ToArray());
			return true;
		}
	}

	private sealed class ReportState
	{
		private readonly HashSet<AntigravityLiveR0FailureReason> _failureReasons = new();

		internal bool ExistingProcessDetected { get; set; }

		internal AntigravityLiveR0GateStatus ExistingProcessGate { get; set; } =
			AntigravityLiveR0GateStatus.NotVerified;

		internal AntigravityLiveR0GateStatus CapabilityGate { get; set; } =
			AntigravityLiveR0GateStatus.NotVerified;

		internal AntigravityCliCapabilityFailureReason? CapabilityFailureReason
		{
			get;
			set;
		}

		internal AntigravityUsageTimeoutDiagnostic? UsageTimeoutDiagnostic
		{
			get;
			set;
		}

		internal AntigravityCliVersionProbeFailureReason? VersionProbeFailureReason
		{
			get;
			set;
		}

		internal AntigravityLiveR0GateStatus PromptGate { get; set; } =
			AntigravityLiveR0GateStatus.NotVerified;

		internal AntigravityLiveR0GateStatus UsageCaptureGate { get; set; }

		internal AntigravityLiveR0GateStatus SettingsGate { get; set; } =
			AntigravityLiveR0GateStatus.NotVerified;

		internal AntigravityLiveR0GateStatus CleanupGate { get; set; } =
			AntigravityLiveR0GateStatus.NotApplicable;

		internal AntigravitySettingsChangedDimensions ChangedDimensions
		{
			get;
			private set;
		}

		internal AntigravitySettingsDriftStage FirstSettingsDriftStage
		{
			get;
			private set;
		}

		internal int InputWriteAttemptCount { get; set; }

		internal int InputWriteCount { get; set; }

		internal string? PromptExactLocalFingerprint { get; set; }

		internal string? UsageExactLocalFingerprint { get; set; }

		internal AntigravityTerminalCaptureFailureReason
			TerminalCaptureFailureReason { get; set; }

		internal AntigravityTerminalControlDiagnostic? TerminalControlDiagnostic
		{
			get;
			set;
		}

		internal AntigravityRedactedStructuralCapture? PromptCapture { get; set; }

		internal AntigravityUsageR1ParseResult? SemanticUsage { get; set; }

		internal AntigravityUsageR1SectionParseResult? SectionSemanticUsage
		{
			get;
			set;
		}

		internal TerminalScreenSnapshot? UsageSnapshot { get; set; }

		internal AntigravityRedactedStructuralCapture? UsageCapture { get; set; }

		internal ReportState(AntigravityLiveR0Mode mode)
		{
			UsageCaptureGate = mode switch
			{
				AntigravityLiveR0Mode.ObservePrompt or
				AntigravityLiveR0Mode.CalibratePrompt =>
					AntigravityLiveR0GateStatus.NotApplicable,
				_ => AntigravityLiveR0GateStatus.NotVerified
			};
		}

		internal void AddFailure(AntigravityLiveR0FailureReason reason)
		{
			if (reason != AntigravityLiveR0FailureReason.None)
			{
				_failureReasons.Add(reason);
			}
		}

		internal void LatchSettingsDrift(
			AntigravitySettingsDriftStage stage,
			AntigravitySettingsChangedDimensions changedDimensions)
		{
			if (FirstSettingsDriftStage != AntigravitySettingsDriftStage.None)
			{
				return;
			}

			FirstSettingsDriftStage = stage;
			ChangedDimensions = changedDimensions;
		}

		internal AntigravityLiveR0Report Build(AntigravityLiveR0Mode mode)
		{
			bool hasRequiredCapture =
				(PromptCapture is not null) &&
				(mode switch
				{
					AntigravityLiveR0Mode.ObservePrompt or
					AntigravityLiveR0Mode.CalibratePrompt => true,
					AntigravityLiveR0Mode.CaptureUsage =>
						UsageCapture is not null,
					_ => false
				});
			bool isCaptureSuccessful =
				(_failureReasons.Count == 0) &&
				hasRequiredCapture &&
				(ExistingProcessGate == AntigravityLiveR0GateStatus.Passed) &&
				(CapabilityGate == AntigravityLiveR0GateStatus.Passed) &&
				(PromptGate == AntigravityLiveR0GateStatus.Passed) &&
				(CleanupGate == AntigravityLiveR0GateStatus.Passed) &&
				(SettingsGate == AntigravityLiveR0GateStatus.Passed);
			ReadOnlyCollection<AntigravityLiveR0FailureReason> reasons =
				Array.AsReadOnly(_failureReasons
					.OrderBy(reason => reason)
					.ToArray());
			return new AntigravityLiveR0Report(
				mode,
				isCaptureSuccessful,
				IsR0Go: false,
				ExistingProcessDetected,
				ExistingProcessGate,
				CapabilityGate,
				PromptGate,
				UsageCaptureGate,
				SettingsGate,
				CleanupGate,
				AntigravityLiveR0GateStatus.NotVerified,
				AntigravityLiveR0GateStatus.NotVerified,
				InputWriteCount,
				PromptExactLocalFingerprint,
				PromptCapture,
				UsageCapture,
				reasons,
				UsageExactLocalFingerprint,
				CapabilityFailureReason,
				VersionProbeFailureReason,
				TerminalCaptureFailureReason,
				TerminalControlDiagnostic,
				InputWriteAttemptCount,
				FirstSettingsDriftStage,
				ChangedDimensions,
				UsageTimeoutDiagnostic);
		}
	}

	private static readonly byte[] UsageCommandBytes =
		Encoding.UTF8.GetBytes("/usage\r");
	private readonly IAntigravityLiveCapabilityValidator _capabilityValidator;
	private readonly IAntigravityLiveConPtySessionFactory _sessionFactory;
	private readonly IAntigravityExistingProcessGate _existingProcessGate;
	private readonly IAntigravityLiveSettingsSentinelPolicy _settingsSentinelPolicy;
	private readonly TimeProvider _timeProvider;

	internal AntigravityLiveR0Runner()
		: this(
			new DefaultAntigravityLiveCapabilityValidator(),
			new WindowsAntigravityLiveConPtySessionFactory(),
			new WindowsAntigravityExistingProcessGate(),
			TimeProvider.System)
	{
	}

	internal AntigravityLiveR0Runner(
		IAntigravityLiveCapabilityValidator capabilityValidator,
		IAntigravityLiveConPtySessionFactory sessionFactory,
		IAntigravityExistingProcessGate existingProcessGate,
		TimeProvider timeProvider)
		: this(
			capabilityValidator,
			sessionFactory,
			existingProcessGate,
			timeProvider,
			new WindowsAntigravityLiveSettingsSentinelPolicy())
	{
	}

	internal AntigravityLiveR0Runner(
		IAntigravityLiveCapabilityValidator capabilityValidator,
		IAntigravityLiveConPtySessionFactory sessionFactory,
		IAntigravityExistingProcessGate existingProcessGate,
		TimeProvider timeProvider,
		IAntigravityLiveSettingsSentinelPolicy settingsSentinelPolicy)
	{
		_capabilityValidator = capabilityValidator ??
			throw new ArgumentNullException(nameof(capabilityValidator));
		_sessionFactory = sessionFactory ??
			throw new ArgumentNullException(nameof(sessionFactory));
		_existingProcessGate = existingProcessGate ??
			throw new ArgumentNullException(nameof(existingProcessGate));
		_timeProvider = timeProvider ??
			throw new ArgumentNullException(nameof(timeProvider));
		_settingsSentinelPolicy = settingsSentinelPolicy ??
			throw new ArgumentNullException(nameof(settingsSentinelPolicy));
	}

	internal async Task<AntigravityLiveR0Report> RunAsync(
		AntigravityLiveR0Mode mode,
		AntigravityLiveR0Profile profile,
		AntigravityLiveR0Consent consent,
		CancellationToken cancellationToken = default)
	{
		RunCoreResult result = await RunCoreAsync(
			mode,
			profile,
			consent,
			semanticUsageLayout: null,
			sectionSemanticUsageLayout: null,
			requiredSettingsBaselineFingerprint: null,
			cancellationToken);
		return result.Report;
	}

	internal async Task<AntigravityLiveR1RunResult> RunR1Async(
		AntigravityLiveR1Profile? profile,
		AntigravityLiveR1Consent? consent,
		CancellationToken cancellationToken = default)
	{
		if (profile is null)
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1Result(invalidState.Build(
				AntigravityLiveR0Mode.CaptureUsage), semanticUsage: null);
		}

		AntigravityLiveR0Consent coreConsent = new(
			(consent is not null) && consent.IsExplicitlyGranted);

		if (!coreConsent.IsExplicitlyGranted)
		{
			ReportState consentState = new(AntigravityLiveR0Mode.CaptureUsage);
			consentState.AddFailure(
				AntigravityLiveR0FailureReason.ConsentMissing);
			return BuildR1Result(consentState.Build(
				AntigravityLiveR0Mode.CaptureUsage), semanticUsage: null);
		}

		if (!IsValidR1Profile(profile))
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1Result(invalidState.Build(
				AntigravityLiveR0Mode.CaptureUsage), semanticUsage: null);
		}

		RunCoreResult result = await RunCoreAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile.CaptureProfile,
			coreConsent,
			profile.UsageLayout,
			sectionSemanticUsageLayout: null,
			requiredSettingsBaselineFingerprint: null,
			cancellationToken);
		return BuildR1Result(result.Report, result.SemanticUsage);
	}

	internal async Task<AntigravityLiveR1SectionRunResult> RunR1SectionAsync(
		AntigravityLiveR1SectionProfile? profile,
		AntigravityLiveR1SectionConsent? consent,
		CancellationToken cancellationToken = default,
		string? requiredSettingsBaselineFingerprint = null)
	{
		if (profile is null)
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1SectionResult(
				invalidState.Build(AntigravityLiveR0Mode.CaptureUsage),
				sectionSemanticUsage: null);
		}

		AntigravityLiveR0Consent coreConsent = new(
			(consent is not null) && consent.IsExplicitlyGranted);

		if (!coreConsent.IsExplicitlyGranted)
		{
			ReportState consentState = new(AntigravityLiveR0Mode.CaptureUsage);
			consentState.AddFailure(
				AntigravityLiveR0FailureReason.ConsentMissing);
			return BuildR1SectionResult(
				consentState.Build(AntigravityLiveR0Mode.CaptureUsage),
				sectionSemanticUsage: null);
		}

		if (!IsValidR1SectionProfile(profile) ||
			((requiredSettingsBaselineFingerprint is not null) &&
			 !AntigravityUsageR1SchemaParser.IsFingerprint(
				requiredSettingsBaselineFingerprint)))
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1SectionResult(
				invalidState.Build(AntigravityLiveR0Mode.CaptureUsage),
				sectionSemanticUsage: null);
		}

		RunCoreResult result = await RunCoreAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile.CaptureProfile,
			coreConsent,
			semanticUsageLayout: null,
			profile.UsageLayout,
			requiredSettingsBaselineFingerprint,
			cancellationToken);
		return BuildR1SectionResult(
			result.Report,
			result.SectionSemanticUsage);
	}

	internal async Task<AntigravityLiveR1PrivateCalibrationCaptureResult>
		RunR1PrivateCalibrationCaptureAsync(
			AntigravityLiveR0Profile? profile,
			AntigravityLiveR1PrivateCalibrationCaptureConsent? consent,
			CancellationToken cancellationToken = default,
			string? requiredSettingsBaselineFingerprint = null)
	{
		if (profile is null)
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1PrivateCalibrationCaptureResult(
				profile: null,
				invalidState.Build(AntigravityLiveR0Mode.CaptureUsage),
				usageSnapshot: null,
				settingsBaselineFingerprint: null);
		}

		if ((consent is null) || !consent.IsExplicitlyGranted)
		{
			ReportState consentState = new(AntigravityLiveR0Mode.CaptureUsage);
			consentState.AddFailure(
				AntigravityLiveR0FailureReason.ConsentMissing);
			return BuildR1PrivateCalibrationCaptureResult(
				profile,
				consentState.Build(AntigravityLiveR0Mode.CaptureUsage),
				usageSnapshot: null,
				settingsBaselineFingerprint: null);
		}

		if (!IsValidR1PrivateCalibrationCaptureProfile(profile) ||
			((requiredSettingsBaselineFingerprint is not null) &&
			 !AntigravityUsageR1SchemaParser.IsFingerprint(
				requiredSettingsBaselineFingerprint)))
		{
			ReportState invalidState = new(AntigravityLiveR0Mode.CaptureUsage);
			invalidState.AddFailure(
				AntigravityLiveR0FailureReason.InvalidProfile);
			return BuildR1PrivateCalibrationCaptureResult(
				profile,
				invalidState.Build(AntigravityLiveR0Mode.CaptureUsage),
				usageSnapshot: null,
				settingsBaselineFingerprint: null);
		}

		RunCoreResult result = await RunCoreAsync(
			AntigravityLiveR0Mode.CaptureUsage,
			profile,
			new AntigravityLiveR0Consent(true),
			semanticUsageLayout: null,
			sectionSemanticUsageLayout: null,
			requiredSettingsBaselineFingerprint,
			cancellationToken);
		return BuildR1PrivateCalibrationCaptureResult(
			profile,
			result.Report,
			result.UsageSnapshot,
			result.SettingsBaselineFingerprint);
	}

	internal async Task<string>
		CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
			AntigravityLiveR0Profile profile,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);

		if (!IsValidR1PrivateCalibrationCaptureProfile(profile))
		{
			throw new InvalidDataException();
		}

		IReadOnlyList<string> settingsFiles = profile.NonCredentialSettingsFiles
			.Select(Path.GetFullPath)
			.ToArray();
		IReadOnlyList<AntigravityLiveSettingsFileState> settingsStates =
			await CaptureValidatedSettingsStateAsync(
				profile,
				settingsFiles,
				cancellationToken);
		return AntigravityLiveR1PrivateCalibrationCaptureContract
			.ComputeSettingsBaselineFingerprint(profile, settingsStates);
	}

	private async Task<RunCoreResult> RunCoreAsync(
		AntigravityLiveR0Mode mode,
		AntigravityLiveR0Profile profile,
		AntigravityLiveR0Consent consent,
		AntigravityUsageR1Layout? semanticUsageLayout,
		AntigravityUsageR1SectionLayout? sectionSemanticUsageLayout,
		string? requiredSettingsBaselineFingerprint,
		CancellationToken cancellationToken)
	{
		ReportState state = new(mode);

		if ((semanticUsageLayout is not null) &&
			(sectionSemanticUsageLayout is not null))
		{
			state.AddFailure(AntigravityLiveR0FailureReason.InvalidProfile);
			return new RunCoreResult(
				state.Build(mode),
				SemanticUsage: null,
				SectionSemanticUsage: null,
				UsageSnapshot: null,
				SettingsBaselineFingerprint: null);
		}

		if ((consent is null) || !consent.IsExplicitlyGranted)
		{
			state.AddFailure(AntigravityLiveR0FailureReason.ConsentMissing);
			return new RunCoreResult(
				state.Build(mode),
				SemanticUsage: null,
				SectionSemanticUsage: null,
				UsageSnapshot: null,
				SettingsBaselineFingerprint: null);
		}

		if (!IsValidProfile(mode, profile))
		{
			state.AddFailure(AntigravityLiveR0FailureReason.InvalidProfile);
			return new RunCoreResult(
				state.Build(mode),
				SemanticUsage: null,
				SectionSemanticUsage: null,
				UsageSnapshot: null,
				SettingsBaselineFingerprint: null);
		}

		IReadOnlyList<string> settingsFiles = profile.NonCredentialSettingsFiles
			.Select(Path.GetFullPath)
			.ToArray();
		IReadOnlyList<AntigravityLiveSettingsFileState>? settingsBefore = null;
		string? settingsBaselineFingerprint = null;
		AntigravityCliCapabilityValidationResult? capability = null;
		IConPtySession? session = null;

		try
		{
			try
			{
				settingsBefore = await CaptureValidatedSettingsStateAsync(
					profile,
					settingsFiles,
					cancellationToken);
				settingsBaselineFingerprint =
					AntigravityLiveR1PrivateCalibrationCaptureContract
						.ComputeSettingsBaselineFingerprint(
							profile,
							settingsBefore);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				state.SettingsGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.SettingsInspectionFailed);
			}

			if ((requiredSettingsBaselineFingerprint is not null) &&
				!string.Equals(
					requiredSettingsBaselineFingerprint,
					settingsBaselineFingerprint,
					StringComparison.Ordinal))
			{
				state.SettingsGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason
						.SettingsBaselineContractMismatch);
			}

			await RequireNoExistingProcessAsync(
				profile.ExecutableFingerprint.AbsolutePath,
				allowedProcessId: null,
				state,
				cancellationToken);
			AntigravityLiveR0FailureReason afterCapabilitySettingsFailure =
				AntigravityLiveR0FailureReason.None;

			try
			{
				capability = await _capabilityValidator.ValidateAsync(
					profile.ExecutableFingerprint,
					cancellationToken);
				state.CapabilityFailureReason = capability.FailureReason;
				state.VersionProbeFailureReason =
					capability.VersionProbeFailureReason;
			}
			finally
			{
				afterCapabilitySettingsFailure =
					await EvaluateSettingsCheckpointAsync(
						profile,
						settingsFiles,
						settingsBefore!,
						AntigravitySettingsDriftStage.AfterCapability,
						state,
						CancellationToken.None);
			}

			ThrowIfSettingsCheckpointFailed(
				afterCapabilitySettingsFailure);

			if (!capability.IsSupported)
			{
				state.CapabilityGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.CapabilityRejected);
			}

			if ((capability.ExecutableLease is null) ||
				capability.ExecutableLease.IsDisposed ||
				(capability.ObservedFingerprint is null) ||
				!MatchesFingerprint(
					profile.ExecutableFingerprint,
					capability.ObservedFingerprint) ||
				!string.Equals(
					capability.ExecutableLease.AbsolutePath,
					profile.ExecutableFingerprint.AbsolutePath,
					StringComparison.OrdinalIgnoreCase))
			{
				state.CapabilityGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.CapabilityFingerprintMismatch);
			}

			state.CapabilityGate = AntigravityLiveR0GateStatus.Passed;
			await RequireNoExistingProcessAsync(
				profile.ExecutableFingerprint.AbsolutePath,
				allowedProcessId: null,
				state,
				cancellationToken);
			Dictionary<string, string> environment = new(
				profile.Environment,
				StringComparer.OrdinalIgnoreCase)
			{
				["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
			};
			ConPtyStartRequest request = new(
				profile.ExecutableFingerprint.AbsolutePath,
				Array.Empty<string>(),
				profile.WorkingDirectory,
				environment,
				profile.Columns,
				profile.Rows,
				profile.MaximumSavedOutputBytes,
				profile.CleanupTimeout);

			try
			{
				session = await _sessionFactory.StartAsync(
					request,
					cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.SessionStartFailed);
			}

			state.CleanupGate = AntigravityLiveR0GateStatus.NotVerified;
			await RequireNoExistingProcessAsync(
				profile.ExecutableFingerprint.AbsolutePath,
				session.ProcessId,
				state,
				cancellationToken);
			OutputObserver observer = new(
				profile.Columns,
				profile.Rows,
				profile.MaximumSavedOutputBytes);
			bool isPromptCalibration =
				mode == AntigravityLiveR0Mode.CalibratePrompt;
			OutputObservation promptObservation = await WaitForStableCaptureAsync(
				session,
				observer,
				isPromptCalibration || string.IsNullOrWhiteSpace(
					profile.ExpectedPromptStructuralFingerprint)
					? null
					: profile.ExpectedPromptStructuralFingerprint,
				isPromptCalibration || string.IsNullOrWhiteSpace(
					profile.ExpectedExactPromptFingerprint)
					? null
					: profile.ExpectedExactPromptFingerprint,
				rejectedFingerprint: null,
				rejectedExactFingerprint: null,
				profile.ExactPromptFingerprintHmacKey,
				profile.PromptTimeout,
				profile.StableScreenDuration,
				profile.PollInterval,
				AntigravityLiveR0FailureReason.PromptFingerprintNotObserved,
				usageTimeoutDiagnosticBuilder: null,
				acceptedCandidateFingerprintFactory: isPromptCalibration
					? TryComputePromptCalibrationCandidateFingerprint
					: null,
				cancellationToken);
			state.PromptCapture = promptObservation.RedactedCapture;
			state.PromptExactLocalFingerprint =
				promptObservation.ExactLocalFingerprint;
			state.PromptGate = AntigravityLiveR0GateStatus.Passed;
			ThrowIfSettingsCheckpointFailed(
				await EvaluateSettingsCheckpointAsync(
					profile,
					settingsFiles,
					settingsBefore!,
					AntigravitySettingsDriftStage.AfterPrompt,
					state,
					cancellationToken));

			if (mode == AntigravityLiveR0Mode.CaptureUsage)
			{
				await RequireNoExistingProcessAsync(
					profile.ExecutableFingerprint.AbsolutePath,
					session.ProcessId,
					state,
					cancellationToken);
				OutputObservation? finalPromptObservation = observer.TryCapture(
					session,
					profile.ExactPromptFingerprintHmacKey.Span);

				if ((finalPromptObservation is null) ||
					!string.Equals(
						finalPromptObservation.RedactedCapture.StructuralFingerprint,
						state.PromptCapture.StructuralFingerprint,
						StringComparison.Ordinal) ||
					!string.Equals(
						finalPromptObservation.ExactLocalFingerprint,
						state.PromptExactLocalFingerprint,
						StringComparison.Ordinal))
				{
					state.PromptGate = AntigravityLiveR0GateStatus.Failed;
					throw new ControlFailureException(
						AntigravityLiveR0FailureReason.PromptFingerprintNotObserved);
				}

				ThrowIfSettingsCheckpointFailed(
					await EvaluateSettingsCheckpointAsync(
						profile,
						settingsFiles,
						settingsBefore!,
						AntigravitySettingsDriftStage.ImmediatelyBeforeUsage,
						state,
						cancellationToken));

				await RequireNoExistingProcessAsync(
					profile.ExecutableFingerprint.AbsolutePath,
					session.ProcessId,
					state,
					cancellationToken);
				OutputObservation? writePromptObservation = observer.TryCapture(
					session,
					profile.ExactPromptFingerprintHmacKey.Span);

				if ((writePromptObservation is null) ||
					!string.Equals(
						writePromptObservation.RedactedCapture.StructuralFingerprint,
						state.PromptCapture.StructuralFingerprint,
						StringComparison.Ordinal) ||
					!string.Equals(
						writePromptObservation.ExactLocalFingerprint,
						state.PromptExactLocalFingerprint,
						StringComparison.Ordinal))
				{
					state.PromptGate = AntigravityLiveR0GateStatus.Failed;
					throw new ControlFailureException(
						AntigravityLiveR0FailureReason.PromptFingerprintNotObserved);
				}

				AntigravityLiveR0FailureReason afterUsageSettingsFailure =
					AntigravityLiveR0FailureReason.None;

				try
				{
					try
					{
						state.InputWriteAttemptCount = 1;
						await session.WriteInputAsync(
							UsageCommandBytes,
							cancellationToken);
						state.InputWriteCount = 1;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch
					{
						throw new ControlFailureException(
							AntigravityLiveR0FailureReason.UsageCaptureNotObserved);
					}

					bool hasSemanticUsageLayout =
						(semanticUsageLayout is not null) ||
						(sectionSemanticUsageLayout is not null);
					AntigravityLiveR0FailureReason usageTimeoutReason =
						hasSemanticUsageLayout
							? AntigravityLiveR0FailureReason
								.UsageSemanticLayoutNotObserved
							: profile.ExpectedUsageStructuralFingerprint is null
							? AntigravityLiveR0FailureReason.UsageCaptureNotObserved
							: AntigravityLiveR0FailureReason
								.UsageFingerprintNotObserved;
					Func<OutputObservation, string?>?
						acceptedCandidateFingerprintFactory =
							semanticUsageLayout is not null
								? observation =>
									TryComputeSemanticCandidateFingerprint(
										observation,
										semanticUsageLayout,
										profile.ExactPromptFingerprintHmacKey.Span)
								: sectionSemanticUsageLayout is not null
									? observation =>
										TryComputeSectionSemanticCandidateFingerprint(
											observation,
											sectionSemanticUsageLayout,
											profile.ExactPromptFingerprintHmacKey.Span)
									: null;
					UsageTimeoutDiagnosticBuilder usageTimeoutDiagnostics = new(
						writePromptObservation,
						profile.ExpectedUsageStructuralFingerprint,
						profile.ExpectedExactUsageFingerprint,
						state.PromptExactLocalFingerprint!,
						acceptedCandidateFingerprintFactory);
					OutputObservation usageObservation;

					try
					{
						usageObservation = await WaitForStableCaptureAsync(
							session,
							observer,
							profile.ExpectedUsageStructuralFingerprint,
							expectedExactFingerprint:
								profile.ExpectedExactUsageFingerprint,
							rejectedFingerprint: null,
							rejectedExactFingerprint:
								state.PromptExactLocalFingerprint,
							profile.ExactPromptFingerprintHmacKey,
							profile.UsageTimeout,
							profile.StableScreenDuration,
							profile.PollInterval,
							usageTimeoutReason,
							usageTimeoutDiagnostics,
							acceptedCandidateFingerprintFactory,
							cancellationToken);
					}
					catch (ControlFailureException exception) when (
						exception.Reason == usageTimeoutReason)
					{
						state.UsageTimeoutDiagnostic =
							usageTimeoutDiagnostics.Build(
								usageTimeoutDiagnostics.MatchedTooLate);
						throw;
					}

					state.UsageCapture = usageObservation.RedactedCapture;
					state.UsageExactLocalFingerprint =
						usageObservation.ExactLocalFingerprint;
					state.UsageSnapshot = usageObservation.Snapshot;

					if (semanticUsageLayout is not null)
					{
						try
						{
							state.SemanticUsage =
								AntigravityUsageR1SchemaParser.ParseReviewedPage(
									usageObservation.Snapshot,
									semanticUsageLayout);
							state.UsageCaptureGate =
								AntigravityLiveR0GateStatus.Passed;
						}
						catch (Exception exception) when (
							(exception is InvalidDataException) ||
							(exception is ArgumentException) ||
							(exception is InvalidOperationException))
						{
							state.UsageCaptureGate =
								AntigravityLiveR0GateStatus.Failed;
							throw new ControlFailureException(
								AntigravityLiveR0FailureReason
									.UsageSemanticLayoutNotObserved);
						}
					}
					else if (sectionSemanticUsageLayout is not null)
					{
						try
						{
							state.SectionSemanticUsage =
								AntigravityUsageR1SectionSchemaParser
									.ParseReviewedPage(
										usageObservation.Snapshot,
										sectionSemanticUsageLayout);
							state.UsageCaptureGate =
								AntigravityLiveR0GateStatus.Passed;
						}
						catch (Exception exception) when (
							(exception is InvalidDataException) ||
							(exception is ArgumentException) ||
							(exception is InvalidOperationException))
						{
							state.UsageCaptureGate =
								AntigravityLiveR0GateStatus.Failed;
							throw new ControlFailureException(
								AntigravityLiveR0FailureReason
									.UsageSemanticLayoutNotObserved);
						}
					}
					else
					{
						state.UsageCaptureGate =
							profile.ExpectedExactUsageFingerprint is null
								? AntigravityLiveR0GateStatus.NotVerified
								: AntigravityLiveR0GateStatus.Passed;
					}
				}
				finally
				{
					afterUsageSettingsFailure =
						await EvaluateSettingsCheckpointAsync(
							profile,
							settingsFiles,
							settingsBefore!,
							AntigravitySettingsDriftStage.AfterUsageOrCaptureFailure,
							state,
							CancellationToken.None);
				}

				ThrowIfSettingsCheckpointFailed(
					afterUsageSettingsFailure);
			}
		}
		catch (OperationCanceledException)
		{
			state.AddFailure(AntigravityLiveR0FailureReason.Cancelled);
			MarkUsageFailedIfAttempted(mode, state);
		}
		catch (ControlFailureException exception)
		{
			state.AddFailure(exception.Reason);
			state.TerminalCaptureFailureReason =
				exception.TerminalCaptureFailureReason;
			state.TerminalControlDiagnostic =
				exception.TerminalControlDiagnostic;

			bool isPromptFingerprintFailure =
				exception.Reason ==
					AntigravityLiveR0FailureReason.PromptFingerprintNotObserved;
			bool isTerminalOrOutputFailure =
				(exception.Reason ==
					AntigravityLiveR0FailureReason.TerminalCaptureFailed) ||
				(exception.Reason ==
					AntigravityLiveR0FailureReason.OutputLimitExceeded) ||
				(exception.Reason ==
					AntigravityLiveR0FailureReason.OutputReadFailed);

			if (isPromptFingerprintFailure ||
				(isTerminalOrOutputFailure &&
					(state.InputWriteAttemptCount == 0)))
			{
				state.PromptGate = AntigravityLiveR0GateStatus.Failed;
			}

			MarkUsageFailedIfAttempted(mode, state);
		}
		catch
		{
			state.AddFailure(AntigravityLiveR0FailureReason.UnexpectedFailure);
			MarkUsageFailedIfAttempted(mode, state);
		}
		finally
		{
			if (session is not null)
			{
				bool cleanupSucceeded = true;

				try
				{
					await session.DisposeAsync();
				}
				catch
				{
					cleanupSucceeded = false;
				}

				state.CleanupGate = cleanupSucceeded
					? AntigravityLiveR0GateStatus.Passed
					: AntigravityLiveR0GateStatus.Failed;

				if (!cleanupSucceeded)
				{
					state.AddFailure(
						AntigravityLiveR0FailureReason.CleanupFailed);
				}
			}

			capability?.Dispose();

			if (settingsBefore is not null)
			{
				_ = await EvaluateSettingsCheckpointAsync(
					profile,
					settingsFiles,
					settingsBefore,
					AntigravitySettingsDriftStage.AfterCleanup,
					state,
					CancellationToken.None);
			}
		}

		return new RunCoreResult(
			state.Build(mode),
			state.SemanticUsage,
			state.SectionSemanticUsage,
			state.UsageSnapshot,
			settingsBaselineFingerprint);
	}

	private async Task<IReadOnlyList<AntigravityLiveSettingsFileState>>
		CaptureValidatedSettingsStateAsync(
			AntigravityLiveR0Profile profile,
			IReadOnlyList<string> absolutePaths,
			CancellationToken cancellationToken)
	{
		EnsureSettingsSentinelsAreAllowed(profile, absolutePaths);
		IReadOnlyList<AntigravityLiveSettingsFileState> states =
			await CaptureSettingsStateAsync(absolutePaths, cancellationToken);
		EnsureSettingsSentinelsAreAllowed(profile, absolutePaths);
		return states;
	}

	private async Task<AntigravityLiveR0FailureReason>
		EvaluateSettingsCheckpointAsync(
			AntigravityLiveR0Profile profile,
			IReadOnlyList<string> absolutePaths,
			IReadOnlyList<AntigravityLiveSettingsFileState> baseline,
			AntigravitySettingsDriftStage stage,
			ReportState state,
			CancellationToken cancellationToken)
	{
		IReadOnlyList<AntigravityLiveSettingsFileState> current;

		try
		{
			current = await CaptureValidatedSettingsStateAsync(
				profile,
				absolutePaths,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			state.SettingsGate = AntigravityLiveR0GateStatus.Failed;
			state.AddFailure(
				AntigravityLiveR0FailureReason.SettingsInspectionFailed);
			return AntigravityLiveR0FailureReason.SettingsInspectionFailed;
		}

		if (baseline.SequenceEqual(current))
		{
			if (state.SettingsGate != AntigravityLiveR0GateStatus.Failed)
			{
				state.SettingsGate = AntigravityLiveR0GateStatus.Passed;
			}

			return AntigravityLiveR0FailureReason.None;
		}

		AntigravitySettingsChangedDimensions changedDimensions =
			GetChangedSettingsDimensions(baseline, current);
		state.SettingsGate = AntigravityLiveR0GateStatus.Failed;
		state.LatchSettingsDrift(stage, changedDimensions);
		state.AddFailure(AntigravityLiveR0FailureReason.SettingsDrift);
		return AntigravityLiveR0FailureReason.SettingsDrift;
	}

	private void EnsureSettingsSentinelsAreAllowed(
		AntigravityLiveR0Profile profile,
		IReadOnlyList<string> absolutePaths)
	{
		foreach (string absolutePath in absolutePaths)
		{
			if (!_settingsSentinelPolicy.IsAllowed(
					absolutePath,
					profile.Environment) ||
				!IsExistingRegularNonReparseFile(absolutePath))
			{
				throw new IOException(
					"A settings sentinel no longer satisfies the pinned path policy.");
			}
		}
	}

	private static AntigravitySettingsChangedDimensions
		GetChangedSettingsDimensions(
			IReadOnlyList<AntigravityLiveSettingsFileState> baseline,
			IReadOnlyList<AntigravityLiveSettingsFileState> current)
	{
		if (baseline.Count != current.Count)
		{
			return AntigravitySettingsChangedDimensions.AbsolutePath |
				AntigravitySettingsChangedDimensions.Exists |
				AntigravitySettingsChangedDimensions.Length |
				AntigravitySettingsChangedDimensions.CreationTimeUtc |
				AntigravitySettingsChangedDimensions.LastWriteTimeUtc |
				AntigravitySettingsChangedDimensions.Attributes |
				AntigravitySettingsChangedDimensions.Sha256;
		}

		AntigravitySettingsChangedDimensions dimensions =
			AntigravitySettingsChangedDimensions.None;

		for (int index = 0; index < baseline.Count; index++)
		{
			AntigravityLiveSettingsFileState expected = baseline[index];
			AntigravityLiveSettingsFileState observed = current[index];

			if (!string.Equals(
				expected.AbsolutePath,
				observed.AbsolutePath,
				StringComparison.Ordinal))
			{
				dimensions |= AntigravitySettingsChangedDimensions.AbsolutePath;
			}

			if (expected.Exists != observed.Exists)
			{
				dimensions |= AntigravitySettingsChangedDimensions.Exists;
			}

			if (expected.Length != observed.Length)
			{
				dimensions |= AntigravitySettingsChangedDimensions.Length;
			}

			if (expected.CreationTimeUtc != observed.CreationTimeUtc)
			{
				dimensions |=
					AntigravitySettingsChangedDimensions.CreationTimeUtc;
			}

			if (expected.LastWriteTimeUtc != observed.LastWriteTimeUtc)
			{
				dimensions |=
					AntigravitySettingsChangedDimensions.LastWriteTimeUtc;
			}

			if (expected.Attributes != observed.Attributes)
			{
				dimensions |= AntigravitySettingsChangedDimensions.Attributes;
			}

			if (!string.Equals(
				expected.Sha256,
				observed.Sha256,
				StringComparison.Ordinal))
			{
				dimensions |= AntigravitySettingsChangedDimensions.Sha256;
			}
		}

		return dimensions;
	}

	private static void ThrowIfSettingsCheckpointFailed(
		AntigravityLiveR0FailureReason failureReason)
	{
		if (failureReason != AntigravityLiveR0FailureReason.None)
		{
			throw new ControlFailureException(failureReason);
		}
	}

	private static void MarkUsageFailedIfAttempted(
		AntigravityLiveR0Mode mode,
		ReportState state)
	{
		if ((mode == AntigravityLiveR0Mode.CaptureUsage) &&
			(state.InputWriteAttemptCount > 0) &&
			(state.PromptGate == AntigravityLiveR0GateStatus.Passed) &&
			(state.UsageCapture is null))
		{
			state.UsageCaptureGate = AntigravityLiveR0GateStatus.Failed;
		}
	}

	private static async Task<IReadOnlyList<AntigravityLiveSettingsFileState>>
		CaptureSettingsStateAsync(
			IReadOnlyList<string> absolutePaths,
			CancellationToken cancellationToken)
	{
		List<AntigravityLiveSettingsFileState> states = new(
			absolutePaths.Count);

		foreach (string absolutePath in absolutePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!File.Exists(absolutePath))
			{
				states.Add(new AntigravityLiveSettingsFileState(
					absolutePath,
					false,
					0,
					default,
					default,
					0,
					null));
				continue;
			}

			FileInfo before = new(absolutePath);
			before.Refresh();

			if (!before.Exists ||
				((before.Attributes & FileAttributes.Directory) != 0) ||
				((before.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				throw new IOException(
					"A settings sentinel path is not a regular file.");
			}

			string sha256;

			await using (FileStream stream = new(
				absolutePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				8192,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);

				try
				{
					sha256 = Convert.ToHexString(hash);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(hash);
				}
			}

			FileInfo after = new(absolutePath);
			after.Refresh();

			if (!after.Exists ||
				(before.Length != after.Length) ||
				(before.CreationTimeUtc != after.CreationTimeUtc) ||
				(before.LastWriteTimeUtc != after.LastWriteTimeUtc) ||
				(before.Attributes != after.Attributes))
			{
				throw new IOException(
					"A settings sentinel changed while it was inspected.");
			}

			states.Add(new AntigravityLiveSettingsFileState(
				absolutePath,
				true,
				after.Length,
				after.CreationTimeUtc,
				after.LastWriteTimeUtc,
				after.Attributes,
				sha256));
		}

		return states.AsReadOnly();
	}

	private static bool IsHexFingerprint(string? value)
	{
		return (value?.Length == 64) && value.All(Uri.IsHexDigit);
	}

	private static AntigravityLiveR1RunResult BuildR1Result(
		AntigravityLiveR0Report safetyReport,
		AntigravityUsageR1ParseResult? semanticUsage)
	{
		bool isCaptureSuccessful =
			safetyReport.IsCaptureSuccessful &&
			(safetyReport.UsageCaptureGate ==
				AntigravityLiveR0GateStatus.Passed) &&
			(semanticUsage is not null);
		AntigravityUsageResult? usage = isCaptureSuccessful
			? new AntigravityUsageResult(
				semanticUsage!.Page.LayoutId,
				semanticUsage.Page.AccountIdentity,
				semanticUsage.Page.Rows)
			: null;
		AntigravityLiveR1Report report = new(
			isCaptureSuccessful,
			IsR1Go: false,
			safetyReport,
			isCaptureSuccessful ? semanticUsage!.Capture : null);
		return new AntigravityLiveR1RunResult(report, usage);
	}

	private static AntigravityLiveR1SectionRunResult BuildR1SectionResult(
		AntigravityLiveR0Report safetyReport,
		AntigravityUsageR1SectionParseResult? sectionSemanticUsage)
	{
		bool isCaptureSuccessful =
			safetyReport.IsCaptureSuccessful &&
			(safetyReport.UsageCaptureGate ==
				AntigravityLiveR0GateStatus.Passed) &&
			(sectionSemanticUsage is not null);
		AntigravityLiveR1SectionReport report = new(
			isCaptureSuccessful,
			IsR1Go: false,
			safetyReport,
			isCaptureSuccessful ? sectionSemanticUsage!.Capture : null);
		return new AntigravityLiveR1SectionRunResult(
			report,
			isCaptureSuccessful ? sectionSemanticUsage!.Page : null);
	}

	private static AntigravityLiveR1PrivateCalibrationCaptureResult
		BuildR1PrivateCalibrationCaptureResult(
			AntigravityLiveR0Profile? profile,
			AntigravityLiveR0Report safetyReport,
			TerminalScreenSnapshot? usageSnapshot,
			string? settingsBaselineFingerprint)
	{
		bool isCaptureSuccessful =
			(profile is not null) &&
			safetyReport.IsCaptureSuccessful &&
			(safetyReport.InputWriteAttemptCount == 1) &&
			(safetyReport.InputWriteCount == 1) &&
			(usageSnapshot is not null) &&
			AntigravityUsageR1SchemaParser.IsFingerprint(
				settingsBaselineFingerprint);
		string? captureContractFingerprint = isCaptureSuccessful
			? AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
				profile!,
				usageSnapshot!.IsAlternateScreen,
				settingsBaselineFingerprint!)
			: null;
		AntigravityLiveR1PrivateCalibrationCaptureReport report = new(
			isCaptureSuccessful,
			captureContractFingerprint,
			safetyReport);
		return new AntigravityLiveR1PrivateCalibrationCaptureResult(
			report,
			isCaptureSuccessful ? usageSnapshot : null);
	}

	private bool IsValidR1PrivateCalibrationCaptureProfile(
		AntigravityLiveR0Profile profile)
	{
		return
			(profile.ExpectedUsageStructuralFingerprint is null) &&
			(profile.ExpectedExactUsageFingerprint is null) &&
			IsValidProfile(AntigravityLiveR0Mode.CaptureUsage, profile);
	}

	private bool IsValidR1Profile(AntigravityLiveR1Profile profile)
	{
		try
		{
			AntigravityLiveR0Profile captureProfile = profile.CaptureProfile;
			AntigravityUsageR1Layout usageLayout = profile.UsageLayout;
			return
				(captureProfile.ExpectedUsageStructuralFingerprint is null) &&
				(captureProfile.ExpectedExactUsageFingerprint is null) &&
				(captureProfile.Columns == usageLayout.Spec.Columns) &&
				(captureProfile.Rows == usageLayout.Spec.Rows) &&
				AntigravityUsageR1SchemaParser.IsFingerprint(
					usageLayout.SchemaFingerprint) &&
				(usageLayout.ExpectedPageFingerprints.Count == 1) &&
				string.Equals(
					usageLayout.SchemaFingerprint,
					AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(
						usageLayout.Spec),
					StringComparison.Ordinal) &&
				IsValidProfile(
					AntigravityLiveR0Mode.CaptureUsage,
					captureProfile);
		}
		catch
		{
			return false;
		}
	}

	private bool IsValidR1SectionProfile(
		AntigravityLiveR1SectionProfile profile)
	{
		try
		{
			AntigravityLiveR0Profile captureProfile = profile.CaptureProfile;
			AntigravityUsageR1SectionLayout usageLayout = profile.UsageLayout;
			AntigravityUsageR1SectionSpec frozen =
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
					usageLayout.Spec);
			return
				(captureProfile.ExpectedUsageStructuralFingerprint is null) &&
				(captureProfile.ExpectedExactUsageFingerprint is null) &&
				(captureProfile.Columns == frozen.Columns) &&
				(captureProfile.Rows == frozen.Rows) &&
				AntigravityUsageR1SchemaParser.IsFingerprint(
					usageLayout.SchemaFingerprint) &&
				(usageLayout.ExpectedPageFingerprints.Count == 1) &&
				usageLayout.ExpectedPageFingerprints.All(
					AntigravityUsageR1SchemaParser.IsFingerprint) &&
				string.Equals(
					usageLayout.SchemaFingerprint,
					AntigravityUsageR1SectionSchemaParser
						.ComputeSchemaFingerprint(frozen),
					StringComparison.Ordinal) &&
				IsValidProfile(
					AntigravityLiveR0Mode.CaptureUsage,
					captureProfile);
		}
		catch
		{
			return false;
		}
	}

	private bool IsValidProfile(
		AntigravityLiveR0Mode mode,
		AntigravityLiveR0Profile? profile)
	{
		if ((mode != AntigravityLiveR0Mode.ObservePrompt) &&
			(mode != AntigravityLiveR0Mode.CaptureUsage) &&
			(mode != AntigravityLiveR0Mode.CalibratePrompt))
		{
			return false;
		}

		bool requiresFixedPrompt = mode == AntigravityLiveR0Mode.CaptureUsage;

		if ((profile is null) ||
			string.IsNullOrWhiteSpace(profile.Id) ||
			(profile.ExecutableFingerprint is null) ||
			!Path.IsPathFullyQualified(
				profile.ExecutableFingerprint.AbsolutePath) ||
			!Path.IsPathFullyQualified(profile.WorkingDirectory) ||
			!Directory.Exists(profile.WorkingDirectory) ||
			(profile.Environment is null) ||
			(profile.Columns <= 0) ||
			(profile.Rows <= 0) ||
			(profile.ExactPromptFingerprintHmacKey.Length < 32) ||
			(requiresFixedPrompt &&
				!IsHexFingerprint(profile.ExpectedPromptStructuralFingerprint)) ||
			(!requiresFixedPrompt &&
				!string.IsNullOrWhiteSpace(
					profile.ExpectedPromptStructuralFingerprint) &&
				!IsHexFingerprint(profile.ExpectedPromptStructuralFingerprint)) ||
			(requiresFixedPrompt &&
				!IsHexFingerprint(profile.ExpectedExactPromptFingerprint)) ||
			(!requiresFixedPrompt &&
				!string.IsNullOrWhiteSpace(
					profile.ExpectedExactPromptFingerprint) &&
				!IsHexFingerprint(profile.ExpectedExactPromptFingerprint)) ||
			((profile.ExpectedUsageStructuralFingerprint is not null) &&
				!IsHexFingerprint(profile.ExpectedUsageStructuralFingerprint)) ||
			((profile.ExpectedExactUsageFingerprint is not null) &&
				!IsHexFingerprint(profile.ExpectedExactUsageFingerprint)) ||
			((profile.ExpectedUsageStructuralFingerprint is null) !=
				(profile.ExpectedExactUsageFingerprint is null)) ||
			((profile.ExpectedExactUsageFingerprint is not null) &&
				string.Equals(
					profile.ExpectedExactUsageFingerprint,
					profile.ExpectedExactPromptFingerprint,
					StringComparison.Ordinal)) ||
			(profile.NonCredentialSettingsFiles is null) ||
			(profile.NonCredentialSettingsFiles.Count != 1) ||
			(profile.MaximumSavedOutputBytes <= 0) ||
			(profile.PromptTimeout <= TimeSpan.Zero) ||
			(profile.UsageTimeout <= TimeSpan.Zero) ||
			(profile.StableScreenDuration <= TimeSpan.Zero) ||
			(profile.StableScreenDuration > profile.PromptTimeout) ||
			(profile.StableScreenDuration > profile.UsageTimeout) ||
			(profile.PollInterval <= TimeSpan.Zero) ||
			(profile.CleanupTimeout <= TimeSpan.Zero))
		{
			return false;
		}

		foreach ((string name, string value) in profile.Environment)
		{
			if (!AllowedEnvironmentVariableNames.Contains(name) ||
				string.IsNullOrWhiteSpace(name) ||
				(name.Contains('=') || name.Contains('\0')) ||
				(value is null) ||
				value.Contains('\0'))
			{
				return false;
			}
		}

		HashSet<string> settingsPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string path in profile.NonCredentialSettingsFiles)
		{
			if (string.IsNullOrWhiteSpace(path) ||
				!Path.IsPathFullyQualified(path))
			{
				return false;
			}

			try
			{
				if (!settingsPaths.Add(Path.GetFullPath(path)))
				{
					return false;
				}
			}
			catch (Exception exception) when (
				(exception is ArgumentException) ||
				(exception is NotSupportedException) ||
				(exception is PathTooLongException))
			{
				return false;
			}
		}

		string settingsPath = settingsPaths.Single();

		try
		{
			if (!_settingsSentinelPolicy.IsAllowed(
				settingsPath,
				profile.Environment))
			{
				return false;
			}
		}
		catch
		{
			return false;
		}

		if (!IsExistingRegularNonReparseFile(settingsPath))
		{
			return false;
		}

		return true;
	}

	private static bool IsExistingRegularNonReparseFile(
		string absolutePath)
	{
		try
		{
			if (!Path.IsPathFullyQualified(absolutePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absolutePath);
			string? root = Path.GetPathRoot(fullPath);

			if (string.IsNullOrWhiteSpace(root))
			{
				return false;
			}

			FileAttributes rootAttributes = File.GetAttributes(root);

			if (((rootAttributes & FileAttributes.Directory) == 0) ||
				((rootAttributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			string relativePath = Path.GetRelativePath(root, fullPath);
			string[] components = relativePath.Split(
				new[]
				{
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar
				},
				StringSplitOptions.RemoveEmptyEntries);

			if ((components.Length == 0) ||
				components.Any(component =>
					(component == ".") || (component == "..")))
			{
				return false;
			}

			string currentPath = root;

			for (int index = 0; index < components.Length; index++)
			{
				currentPath = Path.Combine(currentPath, components[index]);
				FileAttributes attributes = File.GetAttributes(currentPath);
				bool isDirectory =
					(attributes & FileAttributes.Directory) != 0;

				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}

				bool isFileComponent = index == (components.Length - 1);

				if ((isFileComponent && isDirectory) ||
					(!isFileComponent && !isDirectory))
				{
					return false;
				}
			}

			return File.Exists(fullPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool MatchesFingerprint(
		AntigravityCliFingerprint expected,
		AntigravityCliFingerprint observed)
	{
		return string.Equals(
				expected.AbsolutePath,
				observed.AbsolutePath,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(expected.CliVersion, observed.CliVersion, StringComparison.Ordinal) &&
			string.Equals(expected.FileVersion, observed.FileVersion, StringComparison.Ordinal) &&
			string.Equals(expected.ProductVersion, observed.ProductVersion, StringComparison.Ordinal) &&
			string.Equals(expected.Sha256, observed.Sha256, StringComparison.Ordinal) &&
			(expected.WinVerifyTrustStatus == observed.WinVerifyTrustStatus) &&
			string.Equals(expected.SignerSubject, observed.SignerSubject, StringComparison.Ordinal) &&
			string.Equals(expected.SignerThumbprint, observed.SignerThumbprint, StringComparison.Ordinal);
	}

	private async Task RequireNoExistingProcessAsync(
		string absoluteExecutablePath,
		int? allowedProcessId,
		ReportState state,
		CancellationToken cancellationToken)
	{
		AntigravityExistingProcessGateResult result;

		try
		{
			result = await _existingProcessGate.CheckAsync(
				absoluteExecutablePath,
				allowedProcessId,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			result = AntigravityExistingProcessGateResult.Indeterminate;
		}

		switch (result)
		{
			case AntigravityExistingProcessGateResult.Clear:
				state.ExistingProcessGate = AntigravityLiveR0GateStatus.Passed;
				return;
			case AntigravityExistingProcessGateResult.Detected:
				state.ExistingProcessDetected = true;
				state.ExistingProcessGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.ExistingProcessDetected);
			default:
				state.ExistingProcessGate = AntigravityLiveR0GateStatus.Failed;
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.ExistingProcessInspectionFailed);
		}
	}

	private static string? TryComputeSemanticCandidateFingerprint(
		OutputObservation observation,
		AntigravityUsageR1Layout layout,
		ReadOnlySpan<byte> hmacKey)
	{
		try
		{
			AntigravityUsageR1ParseResult result =
				AntigravityUsageR1SchemaParser.ParseReviewedPage(
					observation.Snapshot,
					layout);
			return AntigravityUsageR1SchemaParser.ComputeStabilityFingerprint(
				result,
				hmacKey);
		}
		catch (Exception exception) when (
			(exception is InvalidDataException) ||
			(exception is ArgumentException) ||
			(exception is InvalidOperationException))
		{
			return null;
		}
	}

	private static string? TryComputeSectionSemanticCandidateFingerprint(
		OutputObservation observation,
		AntigravityUsageR1SectionLayout layout,
		ReadOnlySpan<byte> hmacKey)
	{
		try
		{
			AntigravityUsageR1SectionParseResult result =
				AntigravityUsageR1SectionSchemaParser.ParseReviewedPage(
					observation.Snapshot,
					layout);
			return AntigravityUsageR1SectionSchemaParser
				.ComputeStabilityFingerprint(result, hmacKey);
		}
		catch (Exception exception) when (
			(exception is InvalidDataException) ||
			(exception is ArgumentException) ||
			(exception is InvalidOperationException))
		{
			return null;
		}
	}

	private static string? TryComputePromptCalibrationCandidateFingerprint(
		OutputObservation observation)
	{
		if (observation.RedactedCapture.NonEmptyLines.Count == 0)
		{
			return null;
		}

		return string.Concat(
			observation.RedactedCapture.StructuralFingerprint,
			observation.ExactLocalFingerprint);
	}

	private async Task<OutputObservation>
		WaitForStableCaptureAsync(
			IConPtySession session,
			OutputObserver observer,
			string? expectedFingerprint,
			string? expectedExactFingerprint,
			string? rejectedFingerprint,
			string? rejectedExactFingerprint,
			ReadOnlyMemory<byte> exactFingerprintHmacKey,
			TimeSpan timeout,
			TimeSpan stableDuration,
			TimeSpan pollInterval,
			AntigravityLiveR0FailureReason timeoutReason,
			UsageTimeoutDiagnosticBuilder? usageTimeoutDiagnosticBuilder,
			Func<OutputObservation, string?>?
				acceptedCandidateFingerprintFactory,
			CancellationToken cancellationToken)
	{
		long startedTimestamp = _timeProvider.GetTimestamp();
		AntigravityCaptureStabilityTracker stabilityTracker = new();
		Task<int> exitTask;

		try
		{
			exitTask = session.WaitForExitAsync(CancellationToken.None);
		}
		catch
		{
			throw new ControlFailureException(
				AntigravityLiveR0FailureReason.ProcessExitedBeforeCapture);
		}

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (exitTask.IsCompleted)
			{
				throw new ControlFailureException(
					AntigravityLiveR0FailureReason.ProcessExitedBeforeCapture);
			}

			OutputObservation? observation = observer.TryCapture(
				session,
				exactFingerprintHmacKey.Span);
			TimeSpan elapsed = _timeProvider.GetElapsedTime(
				startedTimestamp,
				_timeProvider.GetTimestamp());
			usageTimeoutDiagnosticBuilder?.Observe(
				observation,
				observer.LastTotalBytesRead,
				observer.LastSavedByteCount,
				elapsed,
				stableDuration);
			string? expectedCandidateFingerprint = null;

			if ((observation is not null) &&
				(acceptedCandidateFingerprintFactory is not null))
			{
				expectedCandidateFingerprint =
					acceptedCandidateFingerprintFactory(observation);
			}
			else if (observation is not null)
			{
				bool isExpected =
					((expectedFingerprint is null) ||
						string.Equals(
							observation.RedactedCapture.StructuralFingerprint,
							expectedFingerprint,
							StringComparison.Ordinal)) &&
					((expectedExactFingerprint is null) ||
						string.Equals(
							observation.ExactLocalFingerprint,
							expectedExactFingerprint,
							StringComparison.Ordinal)) &&
					((rejectedFingerprint is null) ||
						!string.Equals(
							observation.RedactedCapture.StructuralFingerprint,
							rejectedFingerprint,
							StringComparison.Ordinal)) &&
					((rejectedExactFingerprint is null) ||
						!string.Equals(
							observation.ExactLocalFingerprint,
							rejectedExactFingerprint,
							StringComparison.Ordinal));

				if (isExpected)
				{
					expectedCandidateFingerprint = string.Concat(
						observation.RedactedCapture.StructuralFingerprint,
						observation.ExactLocalFingerprint);
				}
			}

			bool isStable = stabilityTracker.Observe(
				expectedCandidateFingerprint,
				elapsed,
				stableDuration);

			if (isStable && (observation is not null))
			{
				if (elapsed > timeout)
				{
					usageTimeoutDiagnosticBuilder?.MarkMatchedTooLate();
					throw new ControlFailureException(timeoutReason);
				}

				return observation;
			}

			if (elapsed >= timeout)
			{
				throw new ControlFailureException(timeoutReason);
			}

			TimeSpan remaining = timeout - elapsed;
			await Task.Delay(
				remaining < pollInterval ? remaining : pollInterval,
				_timeProvider,
				cancellationToken);
		}
	}
}
