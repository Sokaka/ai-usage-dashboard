using System.IO;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityOfficialPrintVersionAssessmentKind
{
	Reference,
	UnverifiedAllowed,
	MissingRequiredCapability,
	SafetyBoundaryRejected
}

internal sealed record AntigravityOfficialPrintVersionAssessment(
	string? DetectedVersion,
	AntigravityOfficialPrintVersionAssessmentKind Kind)
{
	internal bool IsExecutionAllowed =>
		Kind is
			AntigravityOfficialPrintVersionAssessmentKind.Reference or
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed;
}

internal static class AntigravityOfficialPrintVersionDiagnosticPolicy
{
	internal static bool CanInclude(
		AntigravityUsageSafetyFailureReason reason)
	{
		return reason is
			AntigravityUsageSafetyFailureReason.NonZeroExit or
			AntigravityUsageSafetyFailureReason.EmptyOutput or
			AntigravityUsageSafetyFailureReason.UnverifiableOutput or
			AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid or
			AntigravityUsageSafetyFailureReason.OutputJsonInvalid or
			AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties or
			AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid or
			AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid or
			AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer;
	}

	internal static string? SelectDetectedVersionForPersistence(
		AntigravityUsageSafetyFailureReason reason,
		AntigravityOfficialPrintVersionAssessment? assessment)
	{
		if (!CanInclude(reason) ||
			(assessment?.Kind !=
				AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed))
		{
			return null;
		}

		AntigravityOfficialPrintVersionAssessment normalizedAssessment =
			AntigravityOfficialPrintCapabilityValidator.AssessVersion(
				assessment.DetectedVersion);
		return normalizedAssessment.Kind ==
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed
				? normalizedAssessment.DetectedVersion
				: null;
	}

	internal static AntigravityOfficialPrintVersionAssessment?
		AssessPersistedVersion(
			AntigravityUsageSafetyFailureReason reason,
			string? detectedVersion)
	{
		if ((detectedVersion is null) || !CanInclude(reason))
		{
			return null;
		}

		AntigravityOfficialPrintVersionAssessment assessment =
			AntigravityOfficialPrintCapabilityValidator.AssessVersion(
				detectedVersion);
		return assessment.IsExecutionAllowed ? assessment : null;
	}

	internal static bool IsValidPersistedState(
		AntigravityUsageSafetyFailureReason reason,
		string? detectedVersion)
	{
		return (detectedVersion is null) ||
			(AssessPersistedVersion(reason, detectedVersion) is not null);
	}
}

internal sealed class AntigravityOfficialPrintCapabilityValidationResult :
	IDisposable
{
	private AntigravityExecutableLease? _executableLease;

	internal bool IsSupported { get; }

	internal string? CliVersion { get; }

	internal AntigravityOfficialPrintVersionAssessment? VersionAssessment
	{
		get;
	}

	internal AntigravityExecutableLease? ExecutableLease =>
		Volatile.Read(ref _executableLease);

	internal AntigravityOfficialPrintCapabilityValidationResult(
		bool isSupported,
		string? cliVersion = null,
		AntigravityExecutableLease? executableLease = null,
		AntigravityOfficialPrintVersionAssessment? versionAssessment = null)
	{
		if (isSupported !=
			(!string.IsNullOrWhiteSpace(cliVersion) &&
			 (executableLease is not null)))
		{
			throw new ArgumentException(
				"A supported official AGY print capability must own a version and executable lease.");
		}

		if ((versionAssessment is not null) &&
			(isSupported != versionAssessment.IsExecutionAllowed))
		{
			throw new ArgumentException(
				"The official AGY version assessment must match the capability result.",
				nameof(versionAssessment));
		}

		if (isSupported &&
			(versionAssessment is not null) &&
			!string.Equals(
				cliVersion,
				versionAssessment.DetectedVersion,
				StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"The official AGY version assessment must describe the supported CLI version.",
				nameof(versionAssessment));
		}

		IsSupported = isSupported;
		CliVersion = cliVersion;
		VersionAssessment = versionAssessment;
		_executableLease = executableLease;
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref _executableLease, null)?.Dispose();
	}

	internal Task RetainExecutableLeaseUntil(Task containmentCompletionTask)
	{
		ArgumentNullException.ThrowIfNull(containmentCompletionTask);
		AntigravityExecutableLease executableLease = Interlocked.Exchange(
			ref _executableLease,
			null) ?? throw new ObjectDisposedException(GetType().Name);
		return DisposeExecutableLeaseAfterContainmentAsync(
			executableLease,
			containmentCompletionTask);
	}

	private static async Task
		DisposeExecutableLeaseAfterContainmentAsync(
			AntigravityExecutableLease executableLease,
			Task containmentCompletionTask)
	{
		try
		{
			await containmentCompletionTask;
		}
		catch
		{
			// Bounded cleanup faults only after its kill-on-close job and owned
			// handles have been disposed. Completion, rather than success, is
			// therefore the executable-lease release condition.
		}
		finally
		{
			executableLease.Dispose();
		}
	}
}

internal sealed class AntigravityOfficialPrintCapabilityValidator
{
	internal const string ReferenceVersion = "1.1.11";
	internal const string RequiredSignerSubject =
		"CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US, SERIALNUMBER=3582691, OID.2.5.4.15=Private Organization, OID.1.3.6.1.4.1.311.60.2.1.2=Delaware, OID.1.3.6.1.4.1.311.60.2.1.3=US";
	internal const string RequiredSignerThumbprint =
		"607A3EDAA64933E94422FC8F0C80388E0590986C";

	private readonly IAntigravityExecutableInspector _executableInspector;
	private readonly IAntigravityCliVersionProbe _versionProbe;

	internal AntigravityOfficialPrintCapabilityValidator()
		: this(
			new WindowsAntigravityExecutableInspector(),
			new ConPtyAntigravityCliVersionProbe())
	{
	}

	internal AntigravityOfficialPrintCapabilityValidator(
		IAntigravityExecutableInspector executableInspector,
		IAntigravityCliVersionProbe versionProbe)
	{
		_executableInspector = executableInspector ??
			throw new ArgumentNullException(nameof(executableInspector));
		_versionProbe = versionProbe ??
			throw new ArgumentNullException(nameof(versionProbe));
	}

	internal async Task<AntigravityOfficialPrintCapabilityValidationResult>
		ValidateAsync(
			string executablePath,
			CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryNormalizeExecutablePath(
			executablePath,
			out string absolutePath) ||
			!File.Exists(absolutePath))
		{
			return Unsupported();
		}

		AntigravityExecutableLease acquiredLease;

		try
		{
			acquiredLease = AntigravityExecutableLease.Acquire(absolutePath);
		}
		catch (Exception exception) when (IsFileFailure(exception))
		{
			return Unsupported();
		}

		using AntigravityExecutableLease pendingLease = acquiredLease;
		AntigravityExecutableFileState stateBefore;
		AntigravityExecutableInspection inspection;
		AntigravityExecutableFileState stateAfterInspection;

		try
		{
			stateBefore = _executableInspector.CaptureFileState(absolutePath);

			if (HasReparsePoint(stateBefore))
			{
				return Unsupported();
			}

			inspection = await _executableInspector.InspectProvenanceAsync(
				absolutePath,
				cancellationToken);
			stateAfterInspection =
				_executableInspector.CaptureFileState(absolutePath);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return Unsupported();
		}

		if (HasReparsePoint(stateAfterInspection) ||
			(stateBefore != stateAfterInspection) ||
			(inspection is null) ||
			(inspection.WinVerifyTrustStatus != 0) ||
			!string.Equals(
				inspection.SignerSubject,
				RequiredSignerSubject,
				StringComparison.Ordinal) ||
			!string.Equals(
				inspection.SignerThumbprint,
				RequiredSignerThumbprint,
				StringComparison.OrdinalIgnoreCase))
		{
			return Unsupported();
		}

		string cliVersion;

		try
		{
			cliVersion = await _versionProbe.ProbeAsync(
				absolutePath,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return Unsupported();
		}

		AntigravityExecutableFileState stateAfterProbe;

		try
		{
			stateAfterProbe =
				_executableInspector.CaptureFileState(absolutePath);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return Unsupported();
		}

		if (HasReparsePoint(stateAfterProbe) ||
			(stateAfterProbe != stateAfterInspection))
		{
			return Unsupported();
		}

		AntigravityOfficialPrintVersionAssessment versionAssessment =
			AssessVersion(cliVersion);

		if (!versionAssessment.IsExecutionAllowed)
		{
			return Unsupported(versionAssessment);
		}

		return new AntigravityOfficialPrintCapabilityValidationResult(
			isSupported: true,
			cliVersion,
			pendingLease.Transfer(),
			versionAssessment);
	}

	internal static AntigravityOfficialPrintVersionAssessment AssessVersion(
		string? version)
	{
		if (string.IsNullOrEmpty(version) ||
			!string.Equals(version, version.Trim(), StringComparison.Ordinal))
		{
			return new AntigravityOfficialPrintVersionAssessment(
				version,
				AntigravityOfficialPrintVersionAssessmentKind
					.SafetyBoundaryRejected);
		}

		string[] parts = version.Split('.', StringSplitOptions.None);

		if ((parts.Length != 3) ||
			!TryParseCanonicalComponent(parts[0], out int major) ||
			!TryParseCanonicalComponent(parts[1], out int minor) ||
			!TryParseCanonicalComponent(parts[2], out int patch))
		{
			return new AntigravityOfficialPrintVersionAssessment(
				version,
				AntigravityOfficialPrintVersionAssessmentKind
					.SafetyBoundaryRejected);
		}

		if ((major < 1) ||
			((major == 1) &&
			 ((minor < 1) || ((minor == 1) && (patch < 11)))))
		{
			return new AntigravityOfficialPrintVersionAssessment(
				version,
				AntigravityOfficialPrintVersionAssessmentKind
					.MissingRequiredCapability);
		}

		if (major > 1)
		{
			return new AntigravityOfficialPrintVersionAssessment(
				version,
				AntigravityOfficialPrintVersionAssessmentKind
					.SafetyBoundaryRejected);
		}

		AntigravityOfficialPrintVersionAssessmentKind kind =
			string.Equals(version, ReferenceVersion, StringComparison.Ordinal)
				? AntigravityOfficialPrintVersionAssessmentKind.Reference
				: AntigravityOfficialPrintVersionAssessmentKind
					.UnverifiedAllowed;
		return new AntigravityOfficialPrintVersionAssessment(version, kind);
	}

	internal static bool IsSupportedVersion(string? version)
	{
		return AssessVersion(version).IsExecutionAllowed;
	}

	private static bool TryParseCanonicalComponent(
		string component,
		out int value)
	{
		value = 0;

		if ((component.Length == 0) ||
			((component.Length > 1) && (component[0] == '0')) ||
			component.Any(character => !char.IsAsciiDigit(character)))
		{
			return false;
		}

		return int.TryParse(
			component,
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out value);
	}

	private static bool TryNormalizeExecutablePath(
		string? executablePath,
		out string absolutePath)
	{
		absolutePath = string.Empty;

		try
		{
			if (string.IsNullOrWhiteSpace(executablePath) ||
				!Path.IsPathFullyQualified(executablePath) ||
				!string.Equals(
					Path.GetExtension(executablePath),
					".exe",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string normalized = Path.GetFullPath(executablePath);
			string? directory = Path.GetDirectoryName(normalized);

			if ((directory is null) ||
				!AntigravityMachineSetupService
					.IsLocalExecutableSearchDirectory(directory))
			{
				return false;
			}

			absolutePath = normalized;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool HasReparsePoint(
		AntigravityExecutableFileState state)
	{
		return state.HasReparsePoint ||
			((state.Attributes & FileAttributes.ReparsePoint) != 0);
	}

	private static bool IsFileFailure(Exception exception)
	{
		return exception is IOException or
			UnauthorizedAccessException or
			ArgumentException or
			NotSupportedException;
	}

	private static AntigravityOfficialPrintCapabilityValidationResult
		Unsupported(
			AntigravityOfficialPrintVersionAssessment? versionAssessment = null)
	{
		return new AntigravityOfficialPrintCapabilityValidationResult(
			isSupported: false,
			versionAssessment: versionAssessment);
	}
}
