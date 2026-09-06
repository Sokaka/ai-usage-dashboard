using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface IAntigravityOfficialExecutablePathResolver
{
	string? ResolveExecutablePath();
}

internal sealed class AntigravityOfficialExecutablePathResolver :
	IAntigravityOfficialExecutablePathResolver
{
	private readonly Func<string, string?> _getProcessEnvironmentVariable;
	private readonly Func<string, EnvironmentVariableTarget, string?>
		_getEnvironmentVariable;

	internal AntigravityOfficialExecutablePathResolver()
		: this(
			name => Environment.GetEnvironmentVariable(name),
			(name, target) => Environment.GetEnvironmentVariable(name, target))
	{
	}

	internal AntigravityOfficialExecutablePathResolver(
		Func<string, string?> getProcessEnvironmentVariable,
		Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable)
	{
		_getProcessEnvironmentVariable = getProcessEnvironmentVariable ??
			throw new ArgumentNullException(
				nameof(getProcessEnvironmentVariable));
		_getEnvironmentVariable = getEnvironmentVariable ??
			throw new ArgumentNullException(nameof(getEnvironmentVariable));
	}

	public string? ResolveExecutablePath()
	{
		string variableName =
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable;
		string? configuredPath = _getEnvironmentVariable(
			variableName,
			EnvironmentVariableTarget.User);

		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return configuredPath;
		}

		configuredPath = _getProcessEnvironmentVariable(variableName);
		return string.IsNullOrWhiteSpace(configuredPath)
			? null
			: configuredPath;
	}
}
