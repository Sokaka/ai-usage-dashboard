using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface IAntigravityOfficialExecutablePathResolver
{
	string? ResolveExecutablePath();
}

internal sealed class AntigravityOfficialExecutablePathResolver :
	IAntigravityOfficialExecutablePathResolver
{
	private readonly AntigravityApprovedSourceResolver _approvedSourceResolver;

	internal AntigravityOfficialExecutablePathResolver()
		: this(
			JsonAntigravityApprovedSourceStore.CreateDefault(),
			name => Environment.GetEnvironmentVariable(name),
			(name, target) => Environment.GetEnvironmentVariable(name, target))
	{
	}

	internal AntigravityOfficialExecutablePathResolver(
		IAntigravityApprovedSourceStore approvedSourceStore,
		Func<string, string?> getProcessEnvironmentVariable,
		Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable)
	{
		_approvedSourceResolver = new AntigravityApprovedSourceResolver(
			approvedSourceStore,
			getProcessEnvironmentVariable,
			getEnvironmentVariable);
	}

	public string? ResolveExecutablePath()
	{
		AntigravityApprovedSource? source = _approvedSourceResolver.Resolve();
		return source?.SourceKind ==
			AntigravityMachineSetupSourceKind.OfficialPrint
				? source.Path
				: null;
	}
}
