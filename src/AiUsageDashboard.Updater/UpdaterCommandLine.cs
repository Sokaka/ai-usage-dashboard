using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal enum UpdaterCommand
{
	UpdateOnline,
	ApplyLocal,
	Uninstall
}

internal sealed record UpdaterCommandLineOptions(
	UpdaterCommand Command,
	string? PackagePath,
	string? Sha256Path,
	Uri? FeedUri,
	string Channel,
	string InstallRoot,
	string MaintenanceRoot,
	string UserDataRoot,
	bool ShouldRestart,
	bool ShouldRefreshUpdater,
	bool ShouldRegisterInstalledApp,
	bool IsUninstallConfirmed,
	bool ShouldNotifyUser)
{
	internal bool ShouldPromptForLicenses { get; init; }
}

internal sealed class UpdaterCommandLineException : Exception
{
	internal UpdaterCommandLineException(string message)
		: base(message)
	{
	}
}

internal static class UpdaterCommandLine
{
	internal const string Usage =
		"Usage:\n" +
		"  AiUsageDashboard.Updater\n" +
		"  AiUsageDashboard.Updater update-online " +
		"[--feed-url <https-url>] [--install-root <path>] [--no-restart] [--prompt-for-licenses]\n" +
		"  AiUsageDashboard.Updater apply-local " +
		"--package <zip> --sha256 <sidecar> " +
		"[--install-root <path>] [--no-restart]\n" +
		"  AiUsageDashboard.Updater uninstall --confirm " +
		"[--install-root <path>] [--notify]";

	internal static UpdaterCommandLineOptions Parse(
		IReadOnlyList<string> arguments)
	{
		UpdaterBuildDefaults defaults = UpdaterBuildDefaults.Load();
		string localApplicationDataDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		return Parse(
			arguments,
			localApplicationDataDirectory,
			Environment.CurrentDirectory,
			defaults.FeedUri,
			defaults.Channel);
	}

	internal static UpdaterCommandLineOptions Parse(
		IReadOnlyList<string> arguments,
		string localApplicationDataDirectory,
		string currentDirectory,
		Uri defaultFeedUri,
		string defaultChannel)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentException.ThrowIfNullOrWhiteSpace(
			localApplicationDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
		ArgumentNullException.ThrowIfNull(defaultFeedUri);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultChannel);

		if (!defaultFeedUri.IsAbsoluteUri)
		{
			throw new ArgumentException(
				"Default update feed URI must be absolute.",
				nameof(defaultFeedUri));
		}

		bool isImplicitOnline = arguments.Count == 0;
		UpdaterCommand command = isImplicitOnline
			? UpdaterCommand.UpdateOnline
			: ParseCommand(arguments[0]);
		int firstOptionIndex = isImplicitOnline ? 0 : 1;
		string? packagePath = null;
		string? sha256Path = null;
		string? feedUrl = null;
		string? installRoot = null;
		bool shouldRestart = true;
		bool shouldRefreshUpdater = true;
		bool hasNoRestart = false;
		bool hasSkipUpdaterRefresh = false;
		bool hasUninstallConfirmation = false;
		bool hasUninstallNotification = false;
		bool shouldPromptForLicenses = false;

		for (int index = firstOptionIndex; index < arguments.Count; index++)
		{
			string option = arguments[index];

			switch (option)
			{
				case "--package" when command == UpdaterCommand.ApplyLocal:
					packagePath = ReadSingleValue(
						arguments,
						ref index,
						option,
						packagePath);
					break;
				case "--sha256" when command == UpdaterCommand.ApplyLocal:
					sha256Path = ReadSingleValue(
						arguments,
						ref index,
						option,
						sha256Path);
					break;
				case "--feed-url" when command == UpdaterCommand.UpdateOnline:
					feedUrl = ReadSingleValue(
						arguments,
						ref index,
						option,
						feedUrl);
					break;
				case "--install-root":
					installRoot = ReadSingleValue(
						arguments,
						ref index,
						option,
						installRoot);
					break;
				case "--no-restart" when command is
					UpdaterCommand.UpdateOnline or UpdaterCommand.ApplyLocal:
					if (hasNoRestart)
					{
						throw new UpdaterCommandLineException(
							"Option --no-restart may only be specified once.");
					}

					hasNoRestart = true;
					shouldRestart = false;
					break;
				case "--skip-updater-refresh" when
					command == UpdaterCommand.UpdateOnline:
					if (hasSkipUpdaterRefresh)
					{
						throw new UpdaterCommandLineException(
							"Option --skip-updater-refresh may only be specified once.");
					}

					hasSkipUpdaterRefresh = true;
					shouldRefreshUpdater = false;
					break;
				case "--prompt-for-licenses" when command == UpdaterCommand.UpdateOnline:
					if (shouldPromptForLicenses)
					{
						throw new UpdaterCommandLineException(
							"Option --prompt-for-licenses may only be specified once.");
					}

					shouldPromptForLicenses = true;
					break;
				case "--confirm" when command == UpdaterCommand.Uninstall:
					if (hasUninstallConfirmation)
					{
						throw new UpdaterCommandLineException(
							"Option --confirm may only be specified once.");
					}

					hasUninstallConfirmation = true;
					break;
				case "--notify" when command == UpdaterCommand.Uninstall:
					if (hasUninstallNotification)
					{
						throw new UpdaterCommandLineException(
							"Option --notify may only be specified once.");
					}

					hasUninstallNotification = true;
					break;
				default:
					throw new UpdaterCommandLineException(
						$"Unknown option '{option}'.");
			}
		}

		if ((command == UpdaterCommand.ApplyLocal) && (packagePath is null))
		{
			throw new UpdaterCommandLineException(
				"Required option --package is missing.");
		}

		if ((command == UpdaterCommand.ApplyLocal) && (sha256Path is null))
		{
			throw new UpdaterCommandLineException(
				"Required option --sha256 is missing.");
		}

		if ((command == UpdaterCommand.Uninstall) &&
			!hasUninstallConfirmation)
		{
			throw new UpdaterCommandLineException(
				"The uninstall command requires --confirm.");
		}

		Uri? feedUri = command == UpdaterCommand.UpdateOnline
			? ParseFeedUri(feedUrl, defaultFeedUri)
			: null;
		string defaultInstallRoot =
			WindowsLogonStartupRegistrationContract.GetCanonicalInstallRoot(
				localApplicationDataDirectory);
		string maintenanceRoot = Path.Combine(
			localApplicationDataDirectory,
			"Programs",
			"AiUsageDashboardUpdater");
		string userDataRoot = Path.Combine(
			localApplicationDataDirectory,
			"AiUsageDashboard");

		try
		{
			string resolvedInstallRoot = Path.GetFullPath(
				installRoot ?? defaultInstallRoot,
				currentDirectory);
			return new UpdaterCommandLineOptions(
				command,
				packagePath is null
					? null
					: Path.GetFullPath(packagePath, currentDirectory),
				sha256Path is null
					? null
					: Path.GetFullPath(sha256Path, currentDirectory),
				feedUri,
				defaultChannel,
				resolvedInstallRoot,
				Path.GetFullPath(maintenanceRoot, currentDirectory),
				Path.GetFullPath(userDataRoot, currentDirectory),
				(command != UpdaterCommand.Uninstall) && shouldRestart,
				(command == UpdaterCommand.UpdateOnline) &&
					shouldRefreshUpdater,
				WindowsLogonStartupRegistrationContract.IsCanonicalInstallRoot(
					resolvedInstallRoot,
					localApplicationDataDirectory),
				hasUninstallConfirmation,
				isImplicitOnline || hasUninstallNotification)
			{
				ShouldPromptForLicenses = isImplicitOnline || shouldPromptForLicenses
			};
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				NotSupportedException or
				PathTooLongException)
		{
			throw new UpdaterCommandLineException(
				$"A command-line path is invalid: {exception.Message}");
		}
	}

	private static UpdaterCommand ParseCommand(string command)
	{
		return command switch
		{
			"update" or "update-online" => UpdaterCommand.UpdateOnline,
			"apply-local" => UpdaterCommand.ApplyLocal,
			"uninstall" => UpdaterCommand.Uninstall,
			_ => throw new UpdaterCommandLineException(
				$"Unknown command '{command}'.")
		};
	}

	private static Uri ParseFeedUri(string? value, Uri defaultFeedUri)
	{
		if (value is null)
		{
			return defaultFeedUri;
		}

		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? feedUri) ||
			!string.IsNullOrEmpty(feedUri.UserInfo) ||
			!string.IsNullOrEmpty(feedUri.Fragment))
		{
			throw new UpdaterCommandLineException(
				"Option --feed-url requires an absolute URL without credentials or a fragment.");
		}

		return feedUri;
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
