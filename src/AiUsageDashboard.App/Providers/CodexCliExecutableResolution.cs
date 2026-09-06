namespace AiUsageDashboard.App.Providers;

internal sealed record CodexCliExecutableResolution(
	string ExecutablePath,
	CliVersionEvidence? VersionEvidence,
	WindowsOfficialCliExecutableLease? ExecutableLease = null);
