namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityLiveR0Mode
{
	ObservePrompt,
	CaptureUsage,
	CalibratePrompt
}

internal enum AntigravityLiveR0GateStatus
{
	NotApplicable,
	NotVerified,
	Passed,
	Failed
}

internal enum AntigravitySettingsDriftStage
{
	None,
	AfterCapability,
	AfterPrompt,
	ImmediatelyBeforeUsage,
	AfterUsageOrCaptureFailure,
	AfterCleanup
}

[Flags]
internal enum AntigravitySettingsChangedDimensions
{
	None = 0,
	AbsolutePath = 1 << 0,
	Exists = 1 << 1,
	Length = 1 << 2,
	CreationTimeUtc = 1 << 3,
	LastWriteTimeUtc = 1 << 4,
	Attributes = 1 << 5,
	Sha256 = 1 << 6
}

internal enum AntigravityLiveR0FailureReason
{
	None,
	ConsentMissing,
	InvalidProfile,
	SettingsInspectionFailed,
	ExistingProcessDetected,
	ExistingProcessInspectionFailed,
	CapabilityRejected,
	CapabilityFingerprintMismatch,
	SessionStartFailed,
	OutputLimitExceeded,
	OutputReadFailed,
	TerminalCaptureFailed,
	ProcessExitedBeforeCapture,
	PromptFingerprintNotObserved,
	UsageCaptureNotObserved,
	UsageFingerprintNotObserved,
	UsageSemanticLayoutNotObserved,
	Cancelled,
	CleanupFailed,
	SettingsDrift,
	UnexpectedFailure,
	SettingsBaselineContractMismatch
}

internal enum AntigravityTerminalCaptureFailureReason
{
	None,
	InvalidUtf8,
	UnsupportedControlSequence,
	UnsupportedUnicodeRune,
	UnsupportedScreenOperation,
	InvalidSnapshot,
	Unexpected
}

internal enum AntigravityTerminalControlSequenceFamily
{
	Csi,
	Osc,
	Escape,
	Other
}

internal enum AntigravityUsageTimeoutClassification
{
	None,
	NoPostWriteOutput,
	PromptStillVisible,
	StableStructuralMismatch,
	StableExactMismatch,
	StableSemanticMismatch,
	UnstableNonPromptCandidate,
	AtomicPending,
	MatchedTooLate
}

internal enum AntigravityUsageTimeoutObservationKind
{
	None,
	Prompt,
	NonPromptCandidate,
	AtomicPending
}

internal sealed record AntigravityTerminalControlDiagnostic(
	AntigravityTerminalControlSequenceFamily Family,
	int? FinalByteCode,
	bool IsPrivate,
	IReadOnlyList<int> NumericParameters,
	bool HasUnparsedParameterBytes,
	int? PrivateMarkerByteCode = null,
	bool UsesColonSeparators = false,
	bool HasEmptyParameterFields = false);

internal sealed record AntigravityUsageTimeoutDiagnostic(
	AntigravityUsageTimeoutClassification Classification,
	AntigravityUsageTimeoutObservationKind LastObservationKind,
	bool HasPostWriteOutput,
	bool HasObservedPrompt,
	bool HasObservedNonPromptCandidate,
	bool HasObservedAtomicPending,
	bool HasObservedExpectedStructuralFingerprint,
	bool HasObservedExpectedExactFingerprint,
	int ObservationCount,
	int AtomicPendingObservationCount,
	int NonPromptCandidateCount,
	int CappedDistinctNonPromptCandidateCount,
	long BaselineTotalBytesRead,
	long LastTotalBytesRead,
	int BaselineSavedByteCount,
	int LastSavedByteCount,
	AntigravityRedactedStructuralCapture? LastNonPromptCapture,
	string? LastNonPromptExactLocalFingerprint,
	bool LastStableMismatchMatchesStructuralFingerprint,
	bool LastStableMismatchMatchesExactFingerprint,
	AntigravityRedactedStructuralCapture? LastStableMismatchCapture,
	string? LastStableMismatchExactLocalFingerprint,
	bool HasObservedExpectedSemanticLayout = false,
	bool LastStableMismatchMatchesSemanticLayout = false);

internal enum AntigravityExistingProcessGateResult
{
	Clear,
	Detected,
	Indeterminate
}

internal sealed record AntigravityLiveR0Consent(bool IsExplicitlyGranted);

internal sealed record AntigravityLiveR0Profile(
	string Id,
	AntigravityCliFingerprint ExecutableFingerprint,
	string WorkingDirectory,
	IReadOnlyDictionary<string, string> Environment,
	short Columns,
	short Rows,
	string ExpectedPromptStructuralFingerprint,
	ReadOnlyMemory<byte> ExactPromptFingerprintHmacKey,
	string ExpectedExactPromptFingerprint,
	string? ExpectedUsageStructuralFingerprint,
	IReadOnlyList<string> NonCredentialSettingsFiles,
	int MaximumSavedOutputBytes,
	TimeSpan PromptTimeout,
	TimeSpan UsageTimeout,
	TimeSpan StableScreenDuration,
	TimeSpan PollInterval,
	TimeSpan CleanupTimeout,
	string? ExpectedExactUsageFingerprint = null);

internal sealed record AntigravityLiveR0Report(
	AntigravityLiveR0Mode Mode,
	bool IsCaptureSuccessful,
	bool IsR0Go,
	bool ExistingProcessDetected,
	AntigravityLiveR0GateStatus ExistingProcessGate,
	AntigravityLiveR0GateStatus CapabilityGate,
	AntigravityLiveR0GateStatus PromptGate,
	AntigravityLiveR0GateStatus UsageCaptureGate,
	AntigravityLiveR0GateStatus SettingsGate,
	AntigravityLiveR0GateStatus CleanupGate,
	AntigravityLiveR0GateStatus IdentityGate,
	AntigravityLiveR0GateStatus ModelInvocationGate,
	int InputWriteCount,
	string? PromptExactLocalFingerprint,
	AntigravityRedactedStructuralCapture? PromptCapture,
	AntigravityRedactedStructuralCapture? UsageCapture,
	IReadOnlyList<AntigravityLiveR0FailureReason> FailureReasons,
	string? UsageExactLocalFingerprint = null,
	AntigravityCliCapabilityFailureReason? CapabilityFailureReason = null,
	AntigravityCliVersionProbeFailureReason? VersionProbeFailureReason = null,
	AntigravityTerminalCaptureFailureReason TerminalCaptureFailureReason =
		AntigravityTerminalCaptureFailureReason.None,
	AntigravityTerminalControlDiagnostic? TerminalControlDiagnostic = null,
	int InputWriteAttemptCount = 0,
	AntigravitySettingsDriftStage FirstSettingsDriftStage =
		AntigravitySettingsDriftStage.None,
	AntigravitySettingsChangedDimensions ChangedDimensions =
		AntigravitySettingsChangedDimensions.None,
	AntigravityUsageTimeoutDiagnostic? UsageTimeoutDiagnostic = null);
