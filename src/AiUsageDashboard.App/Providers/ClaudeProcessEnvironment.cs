using System.Diagnostics;

namespace AiUsageDashboard.App.Providers;

internal static class ClaudeProcessEnvironment
{
	private static readonly string[] ScrubbedEnvironmentVariables =
	{
		"ANTHROPIC_API_KEY",
		"ANTHROPIC_AUTH_TOKEN",
		"ANTHROPIC_BASE_URL",
		"ANTHROPIC_OAUTH_TOKEN",
		"ANTHROPIC_OAUTH_REFRESH_TOKEN",
		"ANTHROPIC_OAUTH_SCOPES",
		"CLAUDE_CODE_OAUTH_TOKEN",
		"CLAUDE_CODE_OAUTH_REFRESH_TOKEN",
		"CLAUDE_CODE_OAUTH_SCOPES",
		"CLAUDE_CODE_OAUTH_CLIENT_ID",
		"CLAUDE_CODE_OAUTH_CLIENT_SECRET",
		"CLAUDE_CODE_USE_BEDROCK",
		"CLAUDE_CODE_USE_FOUNDRY",
		"CLAUDE_CODE_USE_MANTLE",
		"CLAUDE_CODE_USE_VERTEX",
		"OAUTH_ACCESS_TOKEN",
		"OAUTH_REFRESH_TOKEN",
		"OAUTH_SCOPES",
		"OAUTH_CLIENT_ID",
		"OAUTH_CLIENT_SECRET",
		"NODE_OPTIONS"
	};

	internal static void Apply(
		ProcessStartInfo startInfo,
		string configDirectory)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

		foreach (string variableName in ScrubbedEnvironmentVariables)
		{
			startInfo.Environment.Remove(variableName);
		}

		startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
		startInfo.Environment["NO_COLOR"] = "1";
	}
}
