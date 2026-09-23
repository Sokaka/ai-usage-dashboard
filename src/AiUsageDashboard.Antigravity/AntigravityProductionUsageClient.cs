using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

#if ANTIGRAVITY_PRODUCTION
public interface IAntigravityProductionUsageClient
{
	Task<AntigravityProductionUsageResult> CaptureAsync(
		string profilePath,
		CancellationToken cancellationToken);
}

public enum AntigravityProductionUsageFailureKind
{
	None,
	ProfileRejected,
	ProvenanceRejected,
	CaptureRejected,
	UsageShapeRejected,
	SafetyLatched,
	UnexpectedFailure,
	ExecutionBusy
}

public enum AntigravityUsageSafetyFailureReason
{
	None,
	Unknown,
	AttemptInterrupted,
	TimedOut,
	CanceledAfterStart,
	ProcessFailedAfterStart,
	NonZeroExit,
	EmptyOutput,
	StdoutTooLarge,
	StderrTooLarge,
	UnverifiableOutput,
	OutputEventSequenceInvalid,
	OutputJsonInvalid,
	OutputHasDuplicateProperties,
	OutputCommandEnvelopeInvalid,
	OutputResultEnvelopeInvalid,
	OutputCommandCopiesDiffer,
	UsageActivityDetected,
	SafetyStateUnavailable
}

internal static class AntigravityAutomaticRecoveryPolicy
{
	internal static bool IsRecoverableReason(
		AntigravityUsageSafetyFailureReason reason)
	{
		return reason is
			AntigravityUsageSafetyFailureReason.AttemptInterrupted or
			AntigravityUsageSafetyFailureReason.TimedOut or
			AntigravityUsageSafetyFailureReason.CanceledAfterStart or
			AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart or
			AntigravityUsageSafetyFailureReason.SafetyStateUnavailable ||
			IsCompletedOutputReason(reason);
	}

	internal static bool CanMigrateCurrentSchemaMarker(
		AntigravityUsageSafetyFailureReason reason)
	{
		// These reasons are only persisted after the current runner has
		// positively observed process-tree quiescence. Older finite-retry builds
		// may have cleared their recovery flags even though that evidence is
		// still represented by the schema-v3-or-later attempt identifier.
		return reason is
			AntigravityUsageSafetyFailureReason.TimedOut ||
			IsCompletedOutputReason(reason);
	}

	private static bool IsCompletedOutputReason(
		AntigravityUsageSafetyFailureReason reason)
	{
		return reason is
			AntigravityUsageSafetyFailureReason.NonZeroExit or
			AntigravityUsageSafetyFailureReason.EmptyOutput or
			AntigravityUsageSafetyFailureReason.StdoutTooLarge or
			AntigravityUsageSafetyFailureReason.StderrTooLarge or
			AntigravityUsageSafetyFailureReason.UnverifiableOutput or
			AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid or
			AntigravityUsageSafetyFailureReason.OutputJsonInvalid or
			AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties or
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid or
			AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid or
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer;
	}
}

public sealed record AntigravityProductionUsageWindow(
	string StableWindowId,
	decimal RemainingPercent,
	TimeSpan? ResetsIn,
	bool IsAvailable);

public sealed class AntigravityProductionUsageResult
{
	public string? AccountIdentity { get; }

	public bool IsSuccessful { get; }

	public AntigravityProductionUsageFailureKind FailureKind { get; }

	public AntigravityUsageSafetyFailureReason SafetyFailureReason { get; }

	public bool AutomaticRevalidationPending { get; }

	public IReadOnlyList<AntigravityProductionUsageWindow> Windows { get; }

	internal AntigravityOfficialPrintVersionAssessment?
		OfficialPrintVersionAssessment
	{
		get;
	}

	internal AntigravityProductionUsageResult(
		bool isSuccessful,
		AntigravityProductionUsageFailureKind failureKind,
		string? accountIdentity,
		IEnumerable<AntigravityProductionUsageWindow> windows,
		AntigravityUsageSafetyFailureReason safetyFailureReason =
			AntigravityUsageSafetyFailureReason.None,
		bool automaticRevalidationPending = false,
		AntigravityOfficialPrintVersionAssessment?
			officialPrintVersionAssessment = null)
	{
		ArgumentNullException.ThrowIfNull(windows);
		AntigravityProductionUsageWindow[] copy = windows.ToArray();
		AntigravityUsageSafetyFailureReason normalizedSafetyFailureReason =
			(failureKind == AntigravityProductionUsageFailureKind.SafetyLatched) &&
			(safetyFailureReason == AntigravityUsageSafetyFailureReason.None)
				? AntigravityUsageSafetyFailureReason.Unknown
				: safetyFailureReason;

		if (!Enum.IsDefined(failureKind) ||
			!Enum.IsDefined(normalizedSafetyFailureReason) ||
			((officialPrintVersionAssessment is not null) &&
			 ((officialPrintVersionAssessment.Kind !=
				AntigravityOfficialPrintVersionAssessmentKind
					.UnverifiedAllowed) ||
			  !CanIncludeOfficialPrintVersionDiagnostic(
					failureKind,
					normalizedSafetyFailureReason))) ||
			(isSuccessful &&
			 ((failureKind != AntigravityProductionUsageFailureKind.None) ||
			  (normalizedSafetyFailureReason !=
				AntigravityUsageSafetyFailureReason.None) ||
			  !IsNormalizedAccountIdentity(accountIdentity) ||
			  (copy.Length != 4))) ||
			(automaticRevalidationPending &&
			 (!IsTransientSafetyFailure(normalizedSafetyFailureReason) ||
			  (failureKind !=
				AntigravityProductionUsageFailureKind.SafetyLatched))) ||
			(!isSuccessful &&
			 ((failureKind == AntigravityProductionUsageFailureKind.None) ||
			  ((failureKind ==
					AntigravityProductionUsageFailureKind.SafetyLatched) !=
			   (normalizedSafetyFailureReason !=
					AntigravityUsageSafetyFailureReason.None)) ||
			  (accountIdentity is not null) ||
			  (copy.Length != 0))))
		{
			throw new ArgumentException(
				"The AGY production usage result shape is invalid.");
		}

		AccountIdentity = accountIdentity;
		IsSuccessful = isSuccessful;
		FailureKind = failureKind;
		SafetyFailureReason = normalizedSafetyFailureReason;
		AutomaticRevalidationPending = automaticRevalidationPending;
		Windows = new ReadOnlyCollection<AntigravityProductionUsageWindow>(copy);
		OfficialPrintVersionAssessment = officialPrintVersionAssessment;
	}

	internal static bool CanIncludeOfficialPrintVersionDiagnostic(
		AntigravityProductionUsageFailureKind failureKind,
		AntigravityUsageSafetyFailureReason safetyFailureReason)
	{
		return (failureKind ==
				AntigravityProductionUsageFailureKind.SafetyLatched) &&
			AntigravityOfficialPrintVersionDiagnosticPolicy.CanInclude(
				safetyFailureReason);
	}

	internal AntigravityProductionUsageResult
		WithOfficialPrintVersionAssessment(
			AntigravityOfficialPrintVersionAssessment versionAssessment)
	{
		ArgumentNullException.ThrowIfNull(versionAssessment);
		return new AntigravityProductionUsageResult(
			IsSuccessful,
			FailureKind,
			AccountIdentity,
			Windows,
			SafetyFailureReason,
			AutomaticRevalidationPending,
			versionAssessment);
	}

	private static bool IsTransientSafetyFailure(
		AntigravityUsageSafetyFailureReason reason)
	{
		return AntigravityAutomaticRecoveryPolicy.IsRecoverableReason(reason);
	}

	private static bool IsNormalizedAccountIdentity(string? accountIdentity)
	{
		if (string.IsNullOrWhiteSpace(accountIdentity))
		{
			return false;
		}

		string normalized = accountIdentity.Trim()
			.Normalize(System.Text.NormalizationForm.FormKC)
			.ToLowerInvariant();
		return (normalized.Length <= 320) &&
			!normalized.Any(char.IsControl) &&
			string.Equals(
				accountIdentity,
				normalized,
				StringComparison.Ordinal);
	}
}
#endif

#if !ANTIGRAVITY_PRODUCTION
internal sealed record AntigravityProductionUsageCoreResult(
	AntigravityProductionUsageFailureKind FailureKind,
	AntigravityLiveR1SectionRunResult? RunResult);

public sealed class AntigravityProductionUsageClient :
	IAntigravityProductionUsageClient
{
	private sealed record ExpectedWindow(
		string StableWindowId,
		AntigravityUsageR1SectionWindowKind WindowKind,
		int? WindowHours);

	private const int MaximumProfileBytes = 1024 * 1024;
	private const string AvailableStatusId = "quota_available";
	private static readonly IReadOnlyList<AntigravityProductionUsageWindow>
		EmptyWindows = Array.AsReadOnly(
			Array.Empty<AntigravityProductionUsageWindow>());
	private static readonly ExpectedWindow[] ExpectedWindows =
	{
		new(
			"agy.gemini.weekly",
			AntigravityUsageR1SectionWindowKind.Weekly,
			WindowHours: null),
		new(
			"agy.gemini.rolling-5h",
			AntigravityUsageR1SectionWindowKind.RollingHours,
			WindowHours: 5),
		new(
			"agy.claude.weekly",
			AntigravityUsageR1SectionWindowKind.Weekly,
			WindowHours: null),
		new(
			"agy.claude.rolling-5h",
			AntigravityUsageR1SectionWindowKind.RollingHours,
			WindowHours: 5)
	};
	private readonly Func<
		string,
		CancellationToken,
		Task<AntigravityProductionUsageCoreResult>> _captureCoreAsync;
	private readonly ConcurrentDictionary<string, byte> _safetyLatchedProfiles =
		new(StringComparer.OrdinalIgnoreCase);

	public AntigravityProductionUsageClient()
		: this(CaptureCoreAsync)
	{
	}

	internal AntigravityProductionUsageClient(
		Func<
			string,
			CancellationToken,
			Task<AntigravityProductionUsageCoreResult>> captureCoreAsync)
	{
		ArgumentNullException.ThrowIfNull(captureCoreAsync);
		_captureCoreAsync = captureCoreAsync;
	}

	public async Task<AntigravityProductionUsageResult> CaptureAsync(
		string profilePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryNormalizeProfilePath(profilePath, out string normalizedProfilePath))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.ProfileRejected);
		}

		if (_safetyLatchedProfiles.ContainsKey(normalizedProfilePath))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched);
		}

		AntigravityProductionUsageCoreResult coreResult;

		try
		{
			coreResult = await _captureCoreAsync(
				normalizedProfilePath,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return Failure(
				AntigravityProductionUsageFailureKind.UnexpectedFailure);
		}

		bool didAttemptInputWrite = DidAttemptInputWrite(coreResult);
		AntigravityProductionUsageResult result;

		if ((coreResult is null) ||
			!Enum.IsDefined(coreResult.FailureKind))
		{
			result = Failure(
				AntigravityProductionUsageFailureKind.UnexpectedFailure);
		}
		else if (coreResult.FailureKind !=
			AntigravityProductionUsageFailureKind.None)
		{
			result = Failure(coreResult.FailureKind);
		}
		else if (coreResult.RunResult is null)
		{
			result = Failure(
				AntigravityProductionUsageFailureKind.UnexpectedFailure);
		}
		else
		{
			try
			{
				result = ConvertRunResult(coreResult.RunResult);
			}
			catch
			{
				result = Failure(
					AntigravityProductionUsageFailureKind.UnexpectedFailure);
			}
		}

		if (!result.IsSuccessful && didAttemptInputWrite)
		{
			_safetyLatchedProfiles.TryAdd(normalizedProfilePath, 0);
			result = Failure(
				AntigravityProductionUsageFailureKind.SafetyLatched);
		}

		cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	private static async Task<AntigravityProductionUsageCoreResult>
		CaptureCoreAsync(
			string profilePath,
			CancellationToken cancellationToken)
	{
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			AntigravityLiveR1SectionProfile profile;
			AntigravityLiveR1SectionProfileFile profileFile;
			AntigravityReviewedPackageContract reviewedContract;

			try
			{
				profileBytes = await AntigravityLiveR0PrivateProfileFile
					.ReadBoundedAsync(
						profilePath,
						MaximumProfileBytes,
						cancellationToken);
				profileFile = AntigravityLiveR1SectionProfileJson.Deserialize(
					profileBytes);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.ProfileRejected,
					RunResult: null);
			}
			finally
			{
				if (profileBytes is not null)
				{
					CryptographicOperations.ZeroMemory(profileBytes);
					profileBytes = null;
				}
			}

			try
			{
				AntigravityReviewedPackageContract? matchingContract =
					AntigravityReviewedPackageManifestCatalog
						.FindMatchingContract(profileFile);

				if (matchingContract is null)
				{
					return new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind
							.ProvenanceRejected,
						RunResult: null);
				}

				reviewedContract = matchingContract;
			}
			catch
			{
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.ProvenanceRejected,
					RunResult: null);
			}

			try
			{
				(AntigravityLiveR1SectionProfile loadedProfile,
					byte[] loadedHmacKey) = await profileFile.ToProfileAsync(
						profilePath,
						cancellationToken);
				profile = loadedProfile;
				hmacKey = loadedHmacKey;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.ProfileRejected,
					RunResult: null);
			}

			AntigravityLiveR0Runner runner = new();
			string settingsBaselineFingerprint;

			try
			{
				settingsBaselineFingerprint = await runner
					.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
						profile.CaptureProfile,
						cancellationToken);
				string currentCaptureContractFingerprint =
					AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
						profile.CaptureProfile,
						profile.UsageLayout.Spec.IsAlternateScreen,
						settingsBaselineFingerprint);

				if (!string.Equals(
						profile.UsageLayout.CaptureContractFingerprint,
						currentCaptureContractFingerprint,
						StringComparison.Ordinal) ||
					!AntigravityReviewedPackageLocalBinding.Matches(
						reviewedContract.ContractFingerprint,
						currentCaptureContractFingerprint,
						hmacKey,
						profile.UsageLayout.ApprovedDraftFingerprint))
				{
					return new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.ProvenanceRejected,
						RunResult: null);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.ProvenanceRejected,
					RunResult: null);
			}

			try
			{
				AntigravityLiveR1SectionRunResult runResult =
					await runner.RunR1SectionAsync(
						profile,
						new AntigravityLiveR1SectionConsent(true),
						cancellationToken,
						settingsBaselineFingerprint);
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.None,
					runResult);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.UnexpectedFailure,
					RunResult: null);
			}
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static AntigravityProductionUsageResult ConvertRunResult(
		AntigravityLiveR1SectionRunResult runResult)
	{
		AntigravityLiveR1SectionReport report = runResult.Report;
		AntigravityLiveR0Report safety = report.SafetyReport;

		if (safety.FailureReasons.Contains(
			AntigravityLiveR0FailureReason.SettingsBaselineContractMismatch))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.ProvenanceRejected);
		}

		if (!IsSafeSuccessfulCapture(report, safety) ||
			(runResult.Page is null))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.CaptureRejected);
		}

		if (!TryConvertWindows(
			runResult.Page!,
			report.UsageSemanticCapture!,
			out IReadOnlyList<AntigravityProductionUsageWindow> windows))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.UsageShapeRejected);
		}

		if (!TryGetAccountIdentity(
			runResult.Page!.AccountIdentity,
			out string? accountIdentity))
		{
			return Failure(
				AntigravityProductionUsageFailureKind.UsageShapeRejected);
		}

		return new AntigravityProductionUsageResult(
			isSuccessful: true,
			AntigravityProductionUsageFailureKind.None,
			accountIdentity,
			windows);
	}

	private static bool TryGetAccountIdentity(
		string? observedIdentity,
		out string? accountIdentity)
	{
		accountIdentity = null;

		if (observedIdentity is null)
		{
			return false;
		}

		try
		{
			string normalized = AntigravityIdentityBindingGate
				.NormalizeIdentity(observedIdentity);

			if (!string.Equals(
				observedIdentity,
				normalized,
				StringComparison.Ordinal))
			{
				return false;
			}

			accountIdentity = normalized;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsSafeSuccessfulCapture(
		AntigravityLiveR1SectionReport report,
		AntigravityLiveR0Report safety)
	{
		return
			report.IsCaptureSuccessful &&
			safety.IsCaptureSuccessful &&
			(safety.Mode == AntigravityLiveR0Mode.CaptureUsage) &&
			!safety.ExistingProcessDetected &&
			(safety.ExistingProcessGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.CapabilityGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.PromptGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.UsageCaptureGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.SettingsGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.CleanupGate == AntigravityLiveR0GateStatus.Passed) &&
			(safety.IdentityGate == AntigravityLiveR0GateStatus.NotVerified) &&
			(safety.ModelInvocationGate ==
				AntigravityLiveR0GateStatus.NotVerified) &&
			(safety.InputWriteAttemptCount == 1) &&
			(safety.InputWriteCount == 1) &&
			(safety.FailureReasons.Count == 0) &&
			(report.UsageSemanticCapture is not null);
	}

	private static bool TryConvertWindows(
		AntigravityUsageR1SectionPage page,
		AntigravityUsageR1SectionSemanticCapture capture,
		out IReadOnlyList<AntigravityProductionUsageWindow> windows)
	{
		windows = EmptyWindows;

		if ((page.Pagination is not null) ||
			(page.Sections is null) ||
			(page.Sections.Count != 2) ||
			(capture.PaginationPolicy !=
				AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible) ||
			(capture.SectionCount != 2) ||
			(capture.WindowCount != 4) ||
			!string.Equals(page.LayoutId, capture.LayoutId, StringComparison.Ordinal) ||
			!string.Equals(
				page.PageFingerprint,
				capture.PageFingerprint,
				StringComparison.Ordinal))
		{
			return false;
		}

		string[] sectionIds = page.Sections
			.Select(section => section.StableSectionId)
			.ToArray();

		if ((sectionIds.Distinct(StringComparer.Ordinal).Count() != 2) ||
			(capture.StableSectionIds.Count != 2) ||
			!sectionIds.ToHashSet(StringComparer.Ordinal).SetEquals(
				capture.StableSectionIds))
		{
			return false;
		}

		AntigravityUsageR1SectionQuotaWindow[] observedWindows = page.Sections
			.SelectMany(section => section.Windows)
			.ToArray();

		if ((observedWindows.Length != 4) ||
			page.Sections.Any(section => section.Windows.Count != 2) ||
			(observedWindows
				.Select(window => window.StableWindowId)
				.Distinct(StringComparer.Ordinal)
				.Count() != 4) ||
			(capture.StableWindowIds.Count != 4))
		{
			return false;
		}

		Dictionary<string, AntigravityUsageR1SectionQuotaWindow> byId =
			observedWindows.ToDictionary(
				window => window.StableWindowId,
				StringComparer.Ordinal);
		HashSet<string> expectedIds = ExpectedWindows
			.Select(expected => expected.StableWindowId)
			.ToHashSet(StringComparer.Ordinal);

		if (!byId.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds) ||
			!capture.StableWindowIds.ToHashSet(StringComparer.Ordinal)
				.SetEquals(expectedIds))
		{
			return false;
		}

		List<AntigravityProductionUsageWindow> converted = new(4);

		foreach (ExpectedWindow expected in ExpectedWindows)
		{
			AntigravityUsageR1SectionQuotaWindow observed =
				byId[expected.StableWindowId];
			AntigravityUsageR1SectionCapacity capacity = observed.Capacity;
			AntigravityUsageR1SectionReset reset = observed.Reset;

			if ((observed.WindowKind != expected.WindowKind) ||
				(observed.WindowHours != expected.WindowHours) ||
				!capacity.RemainingPercent.HasValue ||
				!capacity.UsedPercent.HasValue ||
				(capacity.AvailabilityStatusId is not null) ||
				(capacity.RemainingPercent.Value < 0m) ||
				(capacity.RemainingPercent.Value > 100m) ||
				((capacity.RemainingPercent.Value +
				  capacity.UsedPercent.Value) != 100m))
			{
				return false;
			}

			bool isAvailable;

			if (reset.ResetsIn.HasValue)
			{
				if ((reset.ResetsIn.Value < TimeSpan.Zero) ||
					(reset.AvailabilityStatusId is not null))
				{
					return false;
				}

				isAvailable = false;
			}
			else
			{
				if (!string.Equals(
					reset.AvailabilityStatusId,
					AvailableStatusId,
					StringComparison.Ordinal))
				{
					return false;
				}

				isAvailable = true;
			}

			converted.Add(new AntigravityProductionUsageWindow(
				expected.StableWindowId,
				capacity.RemainingPercent.Value,
				reset.ResetsIn,
				isAvailable));
		}

		windows = new ReadOnlyCollection<AntigravityProductionUsageWindow>(
			converted);
		return true;
	}

	private static AntigravityProductionUsageResult Failure(
		AntigravityProductionUsageFailureKind failureKind)
	{
		return new AntigravityProductionUsageResult(
			isSuccessful: false,
			failureKind,
			accountIdentity: null,
			EmptyWindows);
	}

	private static bool DidAttemptInputWrite(
		AntigravityProductionUsageCoreResult? coreResult)
	{
		return (coreResult?.RunResult?.Report.SafetyReport
			.InputWriteAttemptCount ?? 0) > 0;
	}

	private static bool TryNormalizeProfilePath(
		string? profilePath,
		out string normalizedProfilePath)
	{
		normalizedProfilePath = string.Empty;

		if (string.IsNullOrWhiteSpace(profilePath))
		{
			return false;
		}

		try
		{
			if (!Path.IsPathFullyQualified(profilePath))
			{
				return false;
			}

			normalizedProfilePath = Path.GetFullPath(profilePath);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
#endif
