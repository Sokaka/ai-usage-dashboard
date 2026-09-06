using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AiUsageDashboard.App.Providers;

internal enum CliVersionDisposition
{
	Reference,
	UnverifiedAllowed,
	UnknownAllowed
}

internal sealed record CliVersionEvidence(
	string CliName,
	string? DetectedVersion,
	string ReferenceVersion,
	CliVersionDisposition Disposition)
{
	internal bool ShouldAppendFailureDiagnostic =>
		Disposition is CliVersionDisposition.UnverifiedAllowed or
			CliVersionDisposition.UnknownAllowed;

	internal string AppendFailureDiagnostic(string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);

		if (!ShouldAppendFailureDiagnostic)
		{
			return message;
		}

		string detectedVersion = string.IsNullOrWhiteSpace(DetectedVersion)
			? $"無法辨識 {CliName} 版號"
			: $"偵測到 {CliName} {DetectedVersion}";
		return $"{message} CLI 版本診斷：{detectedVersion}；本版相容性基準為 {ReferenceVersion}。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。";
	}
}

internal static class CliVersionPolicies
{
	internal const string ClaudeReferenceVersion = "2.1.169";
	internal const string CodexReferenceVersion = "0.144.1";
	internal const string GrokReferenceVersion = "1.0.3";
	internal static readonly Version ClaudeMinimumCapabilityVersion =
		new(2, 1, 169);

	internal static CliVersionEvidence AssessClaude(Version version)
	{
		ArgumentNullException.ThrowIfNull(version);
		return new CliVersionEvidence(
			"Claude Code",
			version.ToString(3),
			ClaudeReferenceVersion,
			version.ToString(3) == ClaudeReferenceVersion
				? CliVersionDisposition.Reference
				: CliVersionDisposition.UnverifiedAllowed);
	}

	internal static bool TryAssessClaudeDiagnostic(
		string? detectedVersion,
		[NotNullWhen(true)] out CliVersionEvidence? evidence)
	{
		evidence = null;

		if (!TryParseCanonicalThreePartVersion(
				detectedVersion,
				out Version? version) ||
			(version < ClaudeMinimumCapabilityVersion))
		{
			return false;
		}

		CliVersionEvidence assessment = AssessClaude(version);

		if (!assessment.ShouldAppendFailureDiagnostic)
		{
			return false;
		}

		evidence = assessment;
		return true;
	}

	internal static bool TryParseCanonicalThreePartVersion(
		string? versionText,
		[NotNullWhen(true)] out Version? version)
	{
		version = null;

		if (string.IsNullOrEmpty(versionText))
		{
			return false;
		}

		string[] components = versionText.Split('.');

		if ((components.Length != 3) ||
			!TryParseCanonicalComponent(components[0], out int major) ||
			!TryParseCanonicalComponent(components[1], out int minor) ||
			!TryParseCanonicalComponent(components[2], out int patch))
		{
			return false;
		}

		version = new Version(major, minor, patch);
		return true;
	}

	internal static CliVersionEvidence AssessCodex(Version version)
	{
		ArgumentNullException.ThrowIfNull(version);
		return new CliVersionEvidence(
			"Codex CLI",
			version.ToString(3),
			CodexReferenceVersion,
			version.ToString(3) == CodexReferenceVersion
				? CliVersionDisposition.Reference
				: CliVersionDisposition.UnverifiedAllowed);
	}

	internal static CliVersionEvidence AssessGrok(
		GrokExecutableVersion? version)
	{
		string? detectedVersion = version is null
			? null
			: $"{version.Major}.{version.Minor}.{version.Patch}";
		return new CliVersionEvidence(
			"Grok Build CLI",
			detectedVersion,
			GrokReferenceVersion,
			detectedVersion is null
				? CliVersionDisposition.UnknownAllowed
				: detectedVersion == GrokReferenceVersion
					? CliVersionDisposition.Reference
					: CliVersionDisposition.UnverifiedAllowed);
	}

	private static bool TryParseCanonicalComponent(
		string component,
		out int value)
	{
		value = 0;

		if ((component.Length == 0) ||
			((component.Length > 1) && (component[0] == '0')))
		{
			return false;
		}

		foreach (char character in component)
		{
			if (!char.IsAsciiDigit(character))
			{
				return false;
			}
		}

		return int.TryParse(
			component,
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out value);
	}
}

internal static class CliVersionEvidenceExtensions
{
	private const string ExceptionDataKey =
		"AiUsageDashboard.CliVersionEvidence";

	internal static void AttachVersionEvidence(
		this Exception exception,
		CliVersionEvidence evidence)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentNullException.ThrowIfNull(evidence);
		exception.Data[ExceptionDataKey] = evidence;
	}

	internal static string AppendVersionDiagnostic(
		this Exception exception,
		string message)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		Exception? current = exception;

		for (int depth = 0; (depth < 8) && (current is not null); depth++)
		{
			if (current.Data[ExceptionDataKey] is CliVersionEvidence evidence)
			{
				return evidence.AppendFailureDiagnostic(message);
			}

			current = current.InnerException;
		}

		return message;
	}
}
