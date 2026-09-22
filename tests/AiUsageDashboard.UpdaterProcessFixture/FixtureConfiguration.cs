using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.UpdaterProcessFixture;

internal sealed record FixtureConfiguration(
	string LocalApplicationDataDirectory,
	string TargetUpdaterPath,
	string ObservationDirectory,
	string ChildScheduledEventName,
	string ChildReleaseEventName,
	string ChildCompletedEventName,
	string ParentReleaseEventName,
	string PromoterStartedEventName)
{
	internal const string EnvironmentVariableName =
		"AI_USAGE_DASHBOARD_UPDATER_PROCESS_FIXTURE_CONFIG";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};

	internal static async Task<FixtureConfiguration> LoadAsync()
	{
		string configurationPath = Environment.GetEnvironmentVariable(
			EnvironmentVariableName) ?? throw new InvalidOperationException(
				$"Environment variable {EnvironmentVariableName} is missing.");
		FixtureConfiguration configuration = JsonSerializer.Deserialize<
			FixtureConfiguration>(
				await File.ReadAllBytesAsync(configurationPath),
				JsonOptions) ?? throw new InvalidDataException(
					"The updater process fixture configuration is empty.");
		configuration.Validate();
		return configuration;
	}

	private void Validate()
	{
		EnsureFullyQualified(LocalApplicationDataDirectory, nameof(LocalApplicationDataDirectory));
		EnsureFullyQualified(TargetUpdaterPath, nameof(TargetUpdaterPath));
		EnsureFullyQualified(ObservationDirectory, nameof(ObservationDirectory));
		ArgumentException.ThrowIfNullOrWhiteSpace(ChildScheduledEventName);
		ArgumentException.ThrowIfNullOrWhiteSpace(ChildReleaseEventName);
		ArgumentException.ThrowIfNullOrWhiteSpace(ChildCompletedEventName);
		ArgumentException.ThrowIfNullOrWhiteSpace(ParentReleaseEventName);
		ArgumentException.ThrowIfNullOrWhiteSpace(PromoterStartedEventName);
	}

	private static void EnsureFullyQualified(string path, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

		if (!Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException(
				$"Fixture path '{path}' must be fully qualified.",
				parameterName);
		}
	}
}
