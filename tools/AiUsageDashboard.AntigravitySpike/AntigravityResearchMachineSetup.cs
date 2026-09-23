namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityResearchMachineSetup
{
	private const string AutoUpdateEnvironmentVariable =
		"AGY_CLI_DISABLE_AUTO_UPDATE";
	private static readonly string[] CopiedEnvironmentVariableNames =
	{
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

	internal static IReadOnlyDictionary<string, string>
		BuildCaptureEnvironment(string userProfile)
	{
		Dictionary<string, string> environment = new(
			StringComparer.OrdinalIgnoreCase);
		foreach (string name in CopiedEnvironmentVariableNames)
		{
			string? value = Environment.GetEnvironmentVariable(name);
			if (value is not null)
			{
				environment[name] = value;
			}
		}

		environment["USERPROFILE"] = userProfile;
		environment[AutoUpdateEnvironmentVariable] = "true";
		return environment;
	}

	internal static bool IsSafePromptCalibration(
		AntigravityLiveR0Report report)
	{
		return report.IsCaptureSuccessful &&
			(report.Mode == AntigravityLiveR0Mode.CalibratePrompt) &&
			!report.ExistingProcessDetected &&
			(report.ExistingProcessGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.CapabilityGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.PromptGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.UsageCaptureGate ==
				AntigravityLiveR0GateStatus.NotApplicable) &&
			(report.SettingsGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.CleanupGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.IdentityGate == AntigravityLiveR0GateStatus.NotVerified) &&
			(report.ModelInvocationGate ==
				AntigravityLiveR0GateStatus.NotVerified) &&
			(report.InputWriteAttemptCount == 0) &&
			(report.InputWriteCount == 0) &&
			(report.FailureReasons.Count == 0) &&
			(report.PromptCapture is not null) &&
			AntigravityUsageR1SchemaParser.IsFingerprint(
				report.PromptExactLocalFingerprint);
	}
}
