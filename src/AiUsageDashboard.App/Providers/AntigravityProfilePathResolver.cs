namespace AiUsageDashboard.App.Providers;

internal interface IAntigravityProfilePathResolver
{
	string? ResolveProfilePath();
}

internal sealed class AntigravityProfilePathResolver :
	IAntigravityProfilePathResolver
{
	internal const string EnvironmentVariableName =
		"AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE";
	private readonly Func<string, string?> _getProcessEnvironmentVariable;
	private readonly Func<string, EnvironmentVariableTarget, string?>
		_getEnvironmentVariable;

	internal AntigravityProfilePathResolver()
		: this(
			name => Environment.GetEnvironmentVariable(name),
			(name, target) => Environment.GetEnvironmentVariable(name, target))
	{
	}

	internal AntigravityProfilePathResolver(
		Func<string, string?> getProcessEnvironmentVariable,
		Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable)
	{
		_getProcessEnvironmentVariable = getProcessEnvironmentVariable ??
			throw new ArgumentNullException(
				nameof(getProcessEnvironmentVariable));
		_getEnvironmentVariable = getEnvironmentVariable ??
			throw new ArgumentNullException(nameof(getEnvironmentVariable));
	}

	public string? ResolveProfilePath()
	{
		string? configuredPath = _getEnvironmentVariable(
			EnvironmentVariableName,
			EnvironmentVariableTarget.User);
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return configuredPath;
		}

		configuredPath = _getProcessEnvironmentVariable(
			EnvironmentVariableName);
		return string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath;
	}
}
