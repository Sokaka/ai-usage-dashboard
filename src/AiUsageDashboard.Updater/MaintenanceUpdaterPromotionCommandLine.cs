using System.Globalization;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record MaintenanceUpdaterPromotionOptions(
	string InstallRoot,
	string MaintenanceRoot,
	string SourceSha256,
	string ExpectedCanonicalSha256,
	ExactProcessIdentity ParentIdentity);

internal static class MaintenanceUpdaterPromotionCommandLine
{
	internal const string Command = "promote-maintenance-updater";

	internal static bool IsCommand(IReadOnlyList<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		return (arguments.Count > 0) &&
			string.Equals(arguments[0], Command, StringComparison.Ordinal);
	}

	internal static MaintenanceUpdaterPromotionOptions Parse(
		IReadOnlyList<string> arguments)
	{
		string localApplicationDataDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		return Parse(arguments, localApplicationDataDirectory);
	}

	internal static MaintenanceUpdaterPromotionOptions Parse(
		IReadOnlyList<string> arguments,
		string localApplicationDataDirectory)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentException.ThrowIfNullOrWhiteSpace(
			localApplicationDataDirectory);

		if (!IsCommand(arguments))
		{
			throw new UpdaterCommandLineException(
				"The maintenance updater promotion command is missing.");
		}

		string? sourceSha256 = null;
		string? expectedCanonicalSha256 = null;
		string? parentProcessId = null;
		string? parentProcessStartTimeUtcTicks = null;

		for (int index = 1; index < arguments.Count; index++)
		{
			string option = arguments[index];

			switch (option)
			{
				case "--source-sha256":
					sourceSha256 = ReadSingleValue(
						arguments,
						ref index,
						option,
						sourceSha256);
					break;
				case "--expected-canonical-sha256":
					expectedCanonicalSha256 = ReadSingleValue(
						arguments,
						ref index,
						option,
						expectedCanonicalSha256);
					break;
				case "--parent-process-id":
					parentProcessId = ReadSingleValue(
						arguments,
						ref index,
						option,
						parentProcessId);
					break;
				case "--parent-process-start-time-utc-ticks":
					parentProcessStartTimeUtcTicks = ReadSingleValue(
						arguments,
						ref index,
						option,
						parentProcessStartTimeUtcTicks);
					break;
				default:
					throw new UpdaterCommandLineException(
						$"Unknown promotion option '{option}'.");
			}
		}

		if (sourceSha256 is null || expectedCanonicalSha256 is null ||
			parentProcessId is null ||
			parentProcessStartTimeUtcTicks is null)
		{
			throw new UpdaterCommandLineException(
				"The maintenance updater promotion command is incomplete.");
		}

		if (!int.TryParse(
				parentProcessId,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int parsedProcessId) ||
			(parsedProcessId <= 0))
		{
			throw new UpdaterCommandLineException(
				"Option --parent-process-id requires a positive integer.");
		}

		if (!long.TryParse(
				parentProcessStartTimeUtcTicks,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out long parsedStartTimeUtcTicks) ||
			(parsedStartTimeUtcTicks <= 0))
		{
			throw new UpdaterCommandLineException(
				"Option --parent-process-start-time-utc-ticks requires a " +
				"positive integer.");
		}

		string maintenanceRoot = MaintenanceUpdaterPathContract.GetCanonicalRoot(
			localApplicationDataDirectory);
		string installRoot = WindowsLogonStartupRegistrationContract
			.GetCanonicalInstallRoot(localApplicationDataDirectory);

		try
		{
			_ = ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				sourceSha256);
			_ = ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				expectedCanonicalSha256);
		}
		catch (ArgumentException exception)
		{
			throw new UpdaterCommandLineException(
				"The maintenance updater promotion hash is invalid.",
				exception);
		}

		return new MaintenanceUpdaterPromotionOptions(
			Path.GetFullPath(installRoot),
			Path.GetFullPath(maintenanceRoot),
			sourceSha256.ToLowerInvariant(),
			expectedCanonicalSha256.ToLowerInvariant(),
			new ExactProcessIdentity(
				parsedProcessId,
				parsedStartTimeUtcTicks));
	}

	private static string ReadSingleValue(
		IReadOnlyList<string> arguments,
		ref int optionIndex,
		string option,
		string? existingValue)
	{
		if (existingValue is not null)
		{
			throw new UpdaterCommandLineException(
				$"Option {option} may only be specified once.");
		}

		int valueIndex = optionIndex + 1;
		if ((valueIndex >= arguments.Count) ||
			string.IsNullOrWhiteSpace(arguments[valueIndex]) ||
			arguments[valueIndex].StartsWith("--", StringComparison.Ordinal))
		{
			throw new UpdaterCommandLineException(
				$"Option {option} requires one value.");
		}

		optionIndex = valueIndex;
		return arguments[valueIndex];
	}
}
