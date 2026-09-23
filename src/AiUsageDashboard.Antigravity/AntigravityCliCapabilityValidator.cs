using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityCliCapabilityFailureReason
{
	None,
	InvalidExecutablePath,
	ExecutableNotFound,
	ReparsePoint,
	InspectionFailed,
	ExecutableChanged,
	AuthenticodeNotTrusted,
	FingerprintNotAllowlisted,
	VersionProbeFailed,
	CliVersionNotAllowlisted
}

internal enum AntigravityCliVersionProbeFailureReason
{
	None,
	StartFailed,
	WaitFailed,
	TimedOut,
	OutputReadFailed,
	OutputLimitExceeded,
	NonZeroExit,
	InvalidUtf8,
	MalformedOutput,
	Unexpected
}

internal sealed class AntigravityCliVersionProbeException : Exception
{
	internal AntigravityCliVersionProbeFailureReason FailureReason { get; }

	internal AntigravityCliVersionProbeException(
		AntigravityCliVersionProbeFailureReason failureReason,
		Exception? innerException = null)
		: base("The AGY CLI version probe failed closed.", innerException)
	{
		if (failureReason == AntigravityCliVersionProbeFailureReason.None)
		{
			throw new ArgumentOutOfRangeException(nameof(failureReason));
		}

		FailureReason = failureReason;
	}
}

internal sealed record AntigravityExecutableFileState(
	long Length,
	DateTime CreationTimeUtc,
	DateTime LastWriteTimeUtc,
	FileAttributes Attributes,
	bool HasReparsePoint);

internal sealed record AntigravityExecutableInspection(
	string? FileVersion,
	string? ProductVersion,
	string Sha256,
	int WinVerifyTrustStatus,
	string? SignerSubject,
	string? SignerThumbprint);

internal sealed record AntigravityCliFingerprint(
	string AbsolutePath,
	string CliVersion,
	string FileVersion,
	string ProductVersion,
	string Sha256,
	int WinVerifyTrustStatus,
	string SignerSubject,
	string SignerThumbprint);

internal sealed class AntigravityExecutableLease : IDisposable
{
	private FileStream? _stream;

	internal string AbsolutePath { get; }

	internal bool IsDisposed => Volatile.Read(ref _stream) is null;

	private AntigravityExecutableLease(
		string absolutePath,
		FileStream stream)
	{
		AbsolutePath = absolutePath;
		_stream = stream;
	}

	internal static AntigravityExecutableLease Acquire(string absolutePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
		FileStream stream = new(
			absolutePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 1,
			FileOptions.RandomAccess);
		return new AntigravityExecutableLease(absolutePath, stream);
	}

	internal AntigravityExecutableLease Transfer()
	{
		FileStream stream = Interlocked.Exchange(ref _stream, null) ??
			throw new ObjectDisposedException(nameof(AntigravityExecutableLease));
		return new AntigravityExecutableLease(AbsolutePath, stream);
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref _stream, null)?.Dispose();
	}
}

internal sealed class AntigravityCliCapabilityValidationResult : IDisposable
{
	private AntigravityExecutableLease? _executableLease;

	internal bool IsSupported { get; }

	internal AntigravityCliCapabilityFailureReason FailureReason { get; }

	internal string Message { get; }

	internal AntigravityCliFingerprint? ObservedFingerprint { get; }

	internal AntigravityCliVersionProbeFailureReason? VersionProbeFailureReason
	{
		get;
	}

	internal AntigravityExecutableLease? ExecutableLease =>
		Volatile.Read(ref _executableLease);

	internal AntigravityCliCapabilityValidationResult(
		bool isSupported,
		AntigravityCliCapabilityFailureReason failureReason,
		string message,
		AntigravityCliFingerprint? observedFingerprint = null,
		AntigravityExecutableLease? executableLease = null,
		AntigravityCliVersionProbeFailureReason? versionProbeFailureReason = null)
	{
		if (isSupported != (executableLease is not null))
		{
			throw new ArgumentException(
				"A supported AGY capability result must exclusively own an executable lease.",
				nameof(executableLease));
		}

		IsSupported = isSupported;
		FailureReason = failureReason;
		Message = message;
		ObservedFingerprint = observedFingerprint;
		VersionProbeFailureReason = versionProbeFailureReason;
		_executableLease = executableLease;
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref _executableLease, null)?.Dispose();
	}
}

internal interface IAntigravityExecutableInspector
{
	AntigravityExecutableFileState CaptureFileState(string absolutePath);

	Task<AntigravityExecutableInspection> InspectAsync(
		string absolutePath,
		CancellationToken cancellationToken);

	Task<AntigravityExecutableInspection> InspectProvenanceAsync(
		string absolutePath,
		CancellationToken cancellationToken)
	{
		// Test doubles and alternate inspectors keep their existing behavior.
		// The Windows implementation overrides this path so the official-print
		// policy can validate Authenticode without hashing an executable whose
		// digest is not part of that policy.
		return InspectAsync(absolutePath, cancellationToken);
	}
}

internal interface IAntigravityCliVersionProbe
{
	Task<string> ProbeAsync(
		string absolutePath,
		CancellationToken cancellationToken);
}

internal sealed class AntigravityCliCapabilityValidator
{
	internal const string AbsentVersionSentinel = "<absent>";

	private const int MaximumCliVersionLength = 256;
	private const int MaximumFileVersionLength = 256;
	private const int MaximumSignerSubjectLength = 2048;
	private const int Sha1ThumbprintLength = 40;
	private const int Sha256Length = 64;
	private const int WinTrustSuccess = 0;
	private readonly IReadOnlyList<AntigravityCliFingerprint> _allowlist;
	private readonly IAntigravityExecutableInspector _executableInspector;
	private readonly IAntigravityCliVersionProbe _versionProbe;

	internal AntigravityCliCapabilityValidator(
		IEnumerable<AntigravityCliFingerprint> allowlist)
		: this(
			allowlist,
			new WindowsAntigravityExecutableInspector(),
			new RedirectedAntigravityCliVersionProbe())
	{
	}

	internal AntigravityCliCapabilityValidator(
		IEnumerable<AntigravityCliFingerprint> allowlist,
		IAntigravityExecutableInspector executableInspector,
		IAntigravityCliVersionProbe versionProbe)
	{
		ArgumentNullException.ThrowIfNull(allowlist);
		_executableInspector = executableInspector ??
			throw new ArgumentNullException(nameof(executableInspector));
		_versionProbe = versionProbe ??
			throw new ArgumentNullException(nameof(versionProbe));
		_allowlist = allowlist
			.Select(NormalizeAllowlistFingerprint)
			.ToArray();
	}

	internal async Task<AntigravityCliCapabilityValidationResult> ValidateAsync(
		string executablePath,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryNormalizeExecutablePath(executablePath, out string absolutePath))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InvalidExecutablePath,
				"The AGY executable must be an absolute .exe path.");
		}

		if (!File.Exists(absolutePath))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ExecutableNotFound,
				"The AGY executable does not exist.");
		}

		IReadOnlyList<AntigravityCliFingerprint> pathCandidates = _allowlist
			.Where(candidate => string.Equals(
				candidate.AbsolutePath,
				absolutePath,
				StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (pathCandidates.Count == 0)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
				"The AGY executable path is not allowlisted.");
		}

		AntigravityExecutableLease acquiredLease;

		try
		{
			acquiredLease = AntigravityExecutableLease.Acquire(absolutePath);
		}
		catch (FileNotFoundException)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ExecutableNotFound,
				"The AGY executable disappeared before it could be leased.");
		}
		catch (DirectoryNotFoundException)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ExecutableNotFound,
				"The AGY executable disappeared before it could be leased.");
		}
		catch (Exception exception) when (IsFileInspectionFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable could not be leased against mutation.");
		}

		using AntigravityExecutableLease pendingLease = acquiredLease;

		AntigravityExecutableFileState stateBeforeInspection;

		try
		{
			stateBeforeInspection = _executableInspector.CaptureFileState(absolutePath);
		}
		catch (Exception exception) when (IsFileInspectionFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable file state could not be inspected.");
		}

		if (stateBeforeInspection.HasReparsePoint ||
			((stateBeforeInspection.Attributes & FileAttributes.ReparsePoint) != 0))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ReparsePoint,
				"Reparse points and executable wrappers are not accepted.");
		}

		AntigravityExecutableInspection inspection;

		try
		{
			inspection = await _executableInspector.InspectAsync(
				absolutePath,
				cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (IsFileInspectionFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable fingerprint could not be inspected.");
		}

		AntigravityExecutableFileState stateAfterInspection;

		try
		{
			stateAfterInspection = _executableInspector.CaptureFileState(absolutePath);
		}
		catch (Exception exception) when (IsFileInspectionFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable file state could not be rechecked.");
		}

		if (stateAfterInspection.HasReparsePoint ||
			((stateAfterInspection.Attributes & FileAttributes.ReparsePoint) != 0))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ReparsePoint,
				"The AGY executable became a reparse point during inspection.");
		}

		if (stateBeforeInspection != stateAfterInspection)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ExecutableChanged,
				"The AGY executable changed during fingerprint inspection.");
		}

		if (!TryNormalizeInspection(
			inspection,
			out AntigravityExecutableInspection normalizedInspection))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable fingerprint is incomplete or malformed.");
		}

		if (normalizedInspection.WinVerifyTrustStatus != WinTrustSuccess)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.AuthenticodeNotTrusted,
				$"WinVerifyTrust rejected the AGY executable with status 0x{unchecked((uint)normalizedInspection.WinVerifyTrustStatus):X8}.");
		}

		IReadOnlyList<AntigravityCliFingerprint> metadataCandidates = pathCandidates
			.Where(candidate => MatchesInspectedMetadata(
				candidate,
				normalizedInspection))
			.ToArray();

		if (metadataCandidates.Count == 0)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
				"The AGY executable fingerprint is not allowlisted.");
		}

		string cliVersionOutput;

		try
		{
			cliVersionOutput = await _versionProbe.ProbeAsync(
				absolutePath,
				cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (AntigravityCliVersionProbeException exception)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.VersionProbeFailed,
				"The allowlisted AGY executable did not return a verifiable CLI version.",
				versionProbeFailureReason: exception.FailureReason);
		}
		catch (Exception exception) when (IsVersionProbeFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.VersionProbeFailed,
				"The allowlisted AGY executable did not return a verifiable CLI version.",
				versionProbeFailureReason:
					AntigravityCliVersionProbeFailureReason.Unexpected);
		}

		AntigravityExecutableFileState stateAfterProbe;

		try
		{
			stateAfterProbe = _executableInspector.CaptureFileState(absolutePath);
		}
		catch (Exception exception) when (IsFileInspectionFailure(exception))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.InspectionFailed,
				"The AGY executable file state could not be checked after the version probe.");
		}

		if (stateAfterProbe.HasReparsePoint ||
			((stateAfterProbe.Attributes & FileAttributes.ReparsePoint) != 0) ||
			(stateAfterInspection != stateAfterProbe))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.ExecutableChanged,
				"The AGY executable changed while its CLI version was being probed.");
		}

		if (!TryNormalizeRequiredValue(
			cliVersionOutput,
			MaximumCliVersionLength,
			out string cliVersion))
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.VersionProbeFailed,
				"The AGY CLI version output is empty or malformed.",
				versionProbeFailureReason:
					AntigravityCliVersionProbeFailureReason.MalformedOutput);
		}

		AntigravityCliFingerprint? match = metadataCandidates.FirstOrDefault(
			candidate => string.Equals(
				candidate.CliVersion,
				cliVersion,
				StringComparison.Ordinal));
		AntigravityCliFingerprint observedFingerprint = new(
			absolutePath,
			cliVersion,
			normalizedInspection.FileVersion ?? AbsentVersionSentinel,
			normalizedInspection.ProductVersion ?? AbsentVersionSentinel,
			normalizedInspection.Sha256,
			normalizedInspection.WinVerifyTrustStatus,
			normalizedInspection.SignerSubject!,
			normalizedInspection.SignerThumbprint!);

		if (match is null)
		{
			return Unsupported(
				AntigravityCliCapabilityFailureReason.CliVersionNotAllowlisted,
				"The AGY CLI version is not allowlisted.",
				observedFingerprint);
		}

		return new AntigravityCliCapabilityValidationResult(
			true,
			AntigravityCliCapabilityFailureReason.None,
			"The AGY executable exactly matches an allowlisted capability fingerprint.",
			observedFingerprint,
			pendingLease.Transfer());
	}

	private static bool IsFileInspectionFailure(Exception exception)
	{
		return (exception is ArgumentException) ||
			(exception is CryptographicException) ||
			(exception is FileNotFoundException) ||
			(exception is IOException) ||
			(exception is NotSupportedException) ||
			(exception is PlatformNotSupportedException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception);
	}

	private static bool IsVersionProbeFailure(Exception exception)
	{
		return (exception is ArgumentException) ||
			(exception is DecoderFallbackException) ||
			(exception is IOException) ||
			(exception is InvalidDataException) ||
			(exception is InvalidOperationException) ||
			(exception is NotSupportedException) ||
			(exception is OperationCanceledException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception);
	}

	private static bool MatchesInspectedMetadata(
		AntigravityCliFingerprint candidate,
		AntigravityExecutableInspection inspection)
	{
		return MatchesInspectedVersion(
				candidate.FileVersion,
				inspection.FileVersion) &&
			MatchesInspectedVersion(
				candidate.ProductVersion,
				inspection.ProductVersion) &&
			string.Equals(
				candidate.Sha256,
				inspection.Sha256,
				StringComparison.Ordinal) &&
			(candidate.WinVerifyTrustStatus == inspection.WinVerifyTrustStatus) &&
			string.Equals(
				candidate.SignerSubject,
				inspection.SignerSubject,
				StringComparison.Ordinal) &&
			string.Equals(
				candidate.SignerThumbprint,
				inspection.SignerThumbprint,
				StringComparison.Ordinal);
	}

	private static bool MatchesInspectedVersion(
		string allowlistedVersion,
		string? inspectedVersion)
	{
		if (string.Equals(
			allowlistedVersion,
			AbsentVersionSentinel,
			StringComparison.Ordinal))
		{
			return inspectedVersion is null;
		}

		return string.Equals(
			allowlistedVersion,
			inspectedVersion,
			StringComparison.Ordinal);
	}

	private static AntigravityCliFingerprint NormalizeAllowlistFingerprint(
		AntigravityCliFingerprint fingerprint)
	{
		ArgumentNullException.ThrowIfNull(fingerprint);

		if (!TryNormalizeExecutablePath(
			fingerprint.AbsolutePath,
			out string absolutePath) ||
			!TryNormalizeRequiredValue(
				fingerprint.CliVersion,
				MaximumCliVersionLength,
				out string cliVersion) ||
			!TryNormalizeAllowlistedVersion(
				fingerprint.FileVersion,
				out string fileVersion) ||
			!TryNormalizeAllowlistedVersion(
				fingerprint.ProductVersion,
				out string productVersion) ||
			!TryNormalizeHex(
				fingerprint.Sha256,
				Sha256Length,
				out string sha256) ||
			(fingerprint.WinVerifyTrustStatus != WinTrustSuccess) ||
			!TryNormalizeRequiredValue(
				fingerprint.SignerSubject,
				MaximumSignerSubjectLength,
				out string signerSubject) ||
			!TryNormalizeHex(
				fingerprint.SignerThumbprint,
				Sha1ThumbprintLength,
				out string signerThumbprint))
		{
			throw new ArgumentException(
				"Every AGY capability allowlist entry must be a complete trusted fingerprint.",
				nameof(fingerprint));
		}

		return new AntigravityCliFingerprint(
			absolutePath,
			cliVersion,
			fileVersion,
			productVersion,
			sha256,
			fingerprint.WinVerifyTrustStatus,
			signerSubject,
			signerThumbprint);
	}

	private static bool TryNormalizeAllowlistedVersion(
		string? value,
		out string normalized)
	{
		normalized = value?.Trim() ?? string.Empty;

		if (string.Equals(
			normalized,
			AbsentVersionSentinel,
			StringComparison.Ordinal))
		{
			return true;
		}

		return TryNormalizeRequiredValue(
			value,
			MaximumFileVersionLength,
			out normalized);
	}

	private static bool TryNormalizeExecutablePath(
		string? executablePath,
		out string absolutePath)
	{
		absolutePath = string.Empty;

		if (string.IsNullOrWhiteSpace(executablePath) ||
			!Path.IsPathFullyQualified(executablePath) ||
			!string.Equals(
				Path.GetExtension(executablePath),
				".exe",
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		try
		{
			absolutePath = Path.GetFullPath(executablePath);
			return Path.IsPathFullyQualified(absolutePath);
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			return false;
		}
	}

	private static bool TryNormalizeHex(
		string? value,
		int requiredLength,
		out string normalized)
	{
		normalized = new string(
			(value ?? string.Empty)
				.Where(character => !char.IsWhiteSpace(character))
				.ToArray())
			.ToUpperInvariant();

		return (normalized.Length == requiredLength) &&
			normalized.All(Uri.IsHexDigit);
	}

	private static bool TryNormalizeInspection(
		AntigravityExecutableInspection? inspection,
		out AntigravityExecutableInspection normalized)
	{
		normalized = new AntigravityExecutableInspection(
			null,
			null,
			string.Empty,
			-1,
			null,
			null);

		if ((inspection is null) ||
			!TryNormalizeInspectedVersion(
				inspection.FileVersion,
				out string? fileVersion) ||
			!TryNormalizeInspectedVersion(
				inspection.ProductVersion,
				out string? productVersion) ||
			!TryNormalizeHex(
				inspection.Sha256,
				Sha256Length,
				out string sha256) ||
			!TryNormalizeRequiredValue(
				inspection.SignerSubject,
				MaximumSignerSubjectLength,
				out string signerSubject) ||
			!TryNormalizeHex(
				inspection.SignerThumbprint,
				Sha1ThumbprintLength,
				out string signerThumbprint))
		{
			return false;
		}

		normalized = new AntigravityExecutableInspection(
			fileVersion,
			productVersion,
			sha256,
			inspection.WinVerifyTrustStatus,
			signerSubject,
			signerThumbprint);
		return true;
	}

	private static bool TryNormalizeInspectedVersion(
		string? value,
		out string? normalized)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			normalized = null;
			return true;
		}

		bool isValid = TryNormalizeRequiredValue(
			value,
			MaximumFileVersionLength,
			out string requiredVersion);
		normalized = isValid ? requiredVersion : null;
		return isValid;
	}

	private static bool TryNormalizeRequiredValue(
		string? value,
		int maximumLength,
		out string normalized)
	{
		normalized = value?.Trim() ?? string.Empty;
		return (normalized.Length > 0) &&
			(normalized.Length <= maximumLength) &&
			!normalized.Any(char.IsControl);
	}

	private static AntigravityCliCapabilityValidationResult Unsupported(
		AntigravityCliCapabilityFailureReason failureReason,
		string message,
		AntigravityCliFingerprint? observedFingerprint = null,
		AntigravityCliVersionProbeFailureReason? versionProbeFailureReason = null)
	{
		return new AntigravityCliCapabilityValidationResult(
			false,
			failureReason,
			message,
			observedFingerprint,
			versionProbeFailureReason: versionProbeFailureReason);
	}
}

internal sealed class WindowsAntigravityExecutableInspector : IAntigravityExecutableInspector
{
	private readonly IWindowsAuthenticodeInspector _authenticodeInspector;

	internal WindowsAntigravityExecutableInspector()
		: this(new WindowsAuthenticodeInspector())
	{
	}

	internal WindowsAntigravityExecutableInspector(
		IWindowsAuthenticodeInspector authenticodeInspector)
	{
		_authenticodeInspector = authenticodeInspector ??
			throw new ArgumentNullException(nameof(authenticodeInspector));
	}

	public AntigravityExecutableFileState CaptureFileState(string absolutePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
		FileInfo file = new(absolutePath);
		file.Refresh();

		if (!file.Exists)
		{
			throw new FileNotFoundException(
				"The executable does not exist.",
				absolutePath);
		}

		FileAttributes attributes = file.Attributes;

		if ((attributes & FileAttributes.Directory) != 0)
		{
			throw new InvalidDataException("The executable path refers to a directory.");
		}

		return new AntigravityExecutableFileState(
			file.Length,
			file.CreationTimeUtc,
			file.LastWriteTimeUtc,
			attributes,
			ContainsReparsePoint(absolutePath));
	}

	public async Task<AntigravityExecutableInspection> InspectAsync(
		string absolutePath,
		CancellationToken cancellationToken)
	{
		return await InspectCoreAsync(
			absolutePath,
			includeSha256: true,
			cancellationToken);
	}

	public async Task<AntigravityExecutableInspection> InspectProvenanceAsync(
		string absolutePath,
		CancellationToken cancellationToken)
	{
		return await InspectCoreAsync(
			absolutePath,
			includeSha256: false,
			cancellationToken);
	}

	private async Task<AntigravityExecutableInspection> InspectCoreAsync(
		string absolutePath,
		bool includeSha256,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
		cancellationToken.ThrowIfCancellationRequested();
		FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(absolutePath);
		string sha256 = string.Empty;

		if (includeSha256)
		{
			await using FileStream stream = new(
				absolutePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				8192,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			byte[] hash = await SHA256.HashDataAsync(
				stream,
				cancellationToken);
			sha256 = Convert.ToHexString(hash);
		}

		WindowsAuthenticodeInspection authenticode =
			_authenticodeInspector.Inspect(absolutePath);
		return new AntigravityExecutableInspection(
			versionInfo.FileVersion,
			versionInfo.ProductVersion,
			sha256,
			authenticode.WinVerifyTrustStatus,
			authenticode.SignerSubject,
			authenticode.SignerThumbprint);
	}

	private static bool ContainsReparsePoint(string absolutePath)
	{
		string root = Path.GetPathRoot(absolutePath) ??
			throw new ArgumentException("The executable path has no root.", nameof(absolutePath));
		string relativePath = Path.GetRelativePath(root, absolutePath);
		string currentPath = root;

		foreach (string segment in relativePath.Split(
			new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
			StringSplitOptions.RemoveEmptyEntries))
		{
			currentPath = Path.Combine(currentPath, segment);
			FileAttributes attributes = File.GetAttributes(currentPath);

			if ((attributes & FileAttributes.ReparsePoint) != 0)
			{
				return true;
			}
		}

		return false;
	}
}

internal static class AntigravityCliVersionOutputRenderer
{
	private const int Columns = 80;
	private const int MaximumOutputBytes = 64 * 1024;
	private const int Rows = 25;
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);

	internal static string Render(ReadOnlySpan<byte> rawOutput)
	{
		try
		{
			_ = StrictUtf8Encoding.GetCharCount(rawOutput);
		}
		catch (DecoderFallbackException)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.InvalidUtf8);
		}

		try
		{
			TerminalScreenState terminal = new(
				Columns,
				Rows,
				MaximumOutputBytes);
			terminal.Feed(rawOutput);
			terminal.Complete();
			TerminalScreenSnapshot snapshot = terminal.Capture();

			if (snapshot.IsAlternateScreen ||
				snapshot.IsBracketedPasteEnabled ||
				snapshot.IsFocusReportingEnabled ||
				snapshot.IsWin32InputModeEnabled ||
				snapshot.HasObservedDecscusr ||
				(snapshot.CursorStyle != 0) ||
				snapshot.HasObservedKittyKeyboardFlagsQuery ||
				(snapshot.KittyKeyboardFlags != 0) ||
				(snapshot.ModifyOtherKeysLevel != 0) ||
				snapshot.HadVisibleContentDiscarded ||
				snapshot.HadTerminalReset ||
				snapshot.HasEverEnteredAlternateScreen ||
				snapshot.HasEverEnabledBracketedPaste)
			{
				throw new AntigravityCliVersionProbeException(
					AntigravityCliVersionProbeFailureReason.MalformedOutput);
			}

			string renderedVersion = snapshot.Lines[0].TrimEnd(' ');
			bool hasAdditionalVisibleLine = snapshot.Lines
				.Skip(1)
				.Any(line => line.TrimEnd(' ').Length > 0);

			if ((renderedVersion.Length == 0) ||
				char.IsWhiteSpace(renderedVersion[0]) ||
				char.IsWhiteSpace(renderedVersion[^1]) ||
				hasAdditionalVisibleLine)
			{
				throw new AntigravityCliVersionProbeException(
					AntigravityCliVersionProbeFailureReason.MalformedOutput);
			}

			return renderedVersion;
		}
		catch (AntigravityCliVersionProbeException)
		{
			throw;
		}
		catch
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.MalformedOutput);
		}
	}
}

internal static class AntigravityCliProcessEnvironment
{
	private static readonly string[] VariableAllowlist =
	{
		"APPDATA",
		"HOMEDRIVE",
		"HOMEPATH",
		"LOCALAPPDATA",
		"ProgramData",
		"ProgramFiles",
		"ProgramFiles(x86)",
		"ProgramW6432",
		"SystemRoot",
		"TEMP",
		"TMP",
		"USERPROFILE",
		"WINDIR"
	};

	internal static IReadOnlyDictionary<string, string> BuildAllowlist()
	{
		Dictionary<string, string> environment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (string name in VariableAllowlist)
		{
			string? value = Environment.GetEnvironmentVariable(name);

			if (!string.IsNullOrEmpty(value))
			{
				environment.Add(name, value);
			}
		}

		environment["NO_COLOR"] = "1";
		environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
		return environment;
	}
}

internal sealed class RedirectedAntigravityCliVersionProbe :
	IAntigravityCliVersionProbe
{
	private const int MaximumOutputBytes = 64 * 1024;
	private readonly WindowsAntigravityOfficialPrintProcessRunner _processRunner;

	internal RedirectedAntigravityCliVersionProbe()
		: this(new WindowsAntigravityOfficialPrintProcessRunner(
			AntigravityOfficialPrintTiming.CapabilityCommandTimeout,
			AntigravityOfficialPrintTiming.CapabilityCleanupTimeout,
			activeProcessLimit:
				WindowsAntigravityOfficialPrintProcessRunner
					.ProductionActiveProcessLimit))
	{
	}

	internal RedirectedAntigravityCliVersionProbe(
		WindowsAntigravityOfficialPrintProcessRunner processRunner)
	{
		_processRunner = processRunner ??
			throw new ArgumentNullException(nameof(processRunner));
	}

	public async Task<string> ProbeAsync(
		string absolutePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ProcessStartInfo startInfo;

		try
		{
			startInfo = CreateStartInfo(absolutePath);
		}
		catch (Exception exception)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.StartFailed,
				exception);
		}

		AntigravityOfficialPrintProcessResult processResult;

		try
		{
			processResult = await _processRunner.RunAsync(
				startInfo,
				WindowsAntigravityOfficialPrintProcessRunner
					.CreateAttemptJobName(Guid.NewGuid().ToString("N")),
				beforeStartAsync: null,
				cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (AntigravityOfficialPrintProcessRunException exception)
			when (exception.InnerException is TimeoutException)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.TimedOut,
				exception);
		}
		catch (AntigravityOfficialPrintProcessRunException exception)
			when ((exception.InnerException is OperationCanceledException) &&
				cancellationToken.IsCancellationRequested)
		{
			cancellationToken.ThrowIfCancellationRequested();
			throw;
		}
		catch (AntigravityOfficialPrintProcessRunException exception)
			when (!exception.WasProcessStarted)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.StartFailed,
				exception);
		}
		catch (AntigravityOfficialPrintProcessRunException exception)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.WaitFailed,
				exception);
		}
		catch (Exception exception)
		{
			throw new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.StartFailed,
				exception);
		}

		using (processResult)
		{
			if (processResult.StdoutLimitExceeded ||
				processResult.StderrLimitExceeded ||
				(processResult.Stdout.Length > MaximumOutputBytes))
			{
				throw new AntigravityCliVersionProbeException(
					AntigravityCliVersionProbeFailureReason.OutputLimitExceeded);
			}

			if (processResult.ExitCode != 0)
			{
				throw new AntigravityCliVersionProbeException(
					AntigravityCliVersionProbeFailureReason.NonZeroExit);
			}

			if (processResult.HasStderr)
			{
				throw new AntigravityCliVersionProbeException(
					AntigravityCliVersionProbeFailureReason.MalformedOutput);
			}

			return AntigravityCliVersionOutputRenderer.Render(
				processResult.Stdout.Span);
		}
	}

	internal static ProcessStartInfo CreateStartInfo(string absolutePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
		string? workingDirectory = Path.GetDirectoryName(absolutePath);

		if (string.IsNullOrWhiteSpace(workingDirectory) ||
			!Path.IsPathFullyQualified(absolutePath))
		{
			throw new ArgumentException(
				"The AGY version-probe executable path is invalid.",
				nameof(absolutePath));
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = absolutePath,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("--version");
		startInfo.Environment.Clear();

		foreach ((string name, string value) in
			AntigravityCliProcessEnvironment.BuildAllowlist())
		{
			startInfo.Environment[name] = value;
		}

		return startInfo;
	}
}
