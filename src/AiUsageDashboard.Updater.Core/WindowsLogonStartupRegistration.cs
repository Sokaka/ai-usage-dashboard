using Microsoft.Win32;

namespace AiUsageDashboard.Updater.Core;

internal enum LogonStartupRegistrationState
{
	Absent,
	ExactMatch,
	Conflict
}

internal static class WindowsLogonStartupRegistrationContract
{
	internal const int MaximumCommandLength = 260;
	internal const string RegistrySubKeyPath =
		@"Software\Microsoft\Windows\CurrentVersion\Run";
	internal const string ValueName = "AiUsageDashboard";
	internal const string StartupArgument = "--startup";
	internal const string ApplicationFileName = "AiUsageDashboard.App.exe";

	internal static string GetCanonicalInstallRoot()
	{
		return GetCanonicalInstallRoot(GetLocalApplicationDataDirectory());
	}

	internal static string GetCanonicalInstallRoot(
		string localApplicationDataDirectory)
	{
		return Path.Combine(
			NormalizeDirectory(localApplicationDataDirectory),
			"Programs",
			"AiUsageDashboard");
	}

	internal static string GetCanonicalExecutablePath()
	{
		return GetCanonicalExecutablePath(GetLocalApplicationDataDirectory());
	}

	internal static string GetCanonicalExecutablePath(
		string localApplicationDataDirectory)
	{
		return Path.Combine(
			GetCanonicalInstallRoot(localApplicationDataDirectory),
			"current",
			"app",
			ApplicationFileName);
	}

	internal static string CreateExpectedCommand()
	{
		return CreateExpectedCommand(GetLocalApplicationDataDirectory());
	}

	internal static string CreateExpectedCommand(
		string localApplicationDataDirectory)
	{
		string executablePath = GetCanonicalExecutablePath(
			localApplicationDataDirectory);

		if (executablePath.Contains('"', StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"The Windows logon startup executable path cannot contain a quote.",
				nameof(localApplicationDataDirectory));
		}

		string command = $"\"{executablePath}\" {StartupArgument}";

		if (command.Length > MaximumCommandLength)
		{
			throw new ArgumentException(
				$"The Windows logon startup command cannot exceed " +
					$"{MaximumCommandLength} characters.",
				nameof(localApplicationDataDirectory));
		}

		return command;
	}

	internal static bool IsCanonicalInstallRoot(string installRoot)
	{
		return IsCanonicalInstallRoot(
			installRoot,
			GetLocalApplicationDataDirectory());
	}

	internal static bool IsCanonicalInstallRoot(
		string? installRoot,
		string localApplicationDataDirectory)
	{
		return IsSamePath(
			installRoot,
			GetCanonicalInstallRoot(localApplicationDataDirectory),
			isDirectory: true);
	}

	internal static bool IsCanonicalExecutablePath(string? executablePath)
	{
		return IsCanonicalExecutablePath(
			executablePath,
			GetLocalApplicationDataDirectory());
	}

	internal static bool IsCanonicalExecutablePath(
		string? executablePath,
		string localApplicationDataDirectory)
	{
		return IsSamePath(
			executablePath,
			GetCanonicalExecutablePath(localApplicationDataDirectory),
			isDirectory: false);
	}

	private static string GetLocalApplicationDataDirectory()
	{
		string localApplicationDataDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
		{
			throw new InvalidOperationException(
				"The current user's local application data directory is unavailable.");
		}

		return localApplicationDataDirectory;
	}

	private static bool IsSamePath(
		string? candidatePath,
		string expectedPath,
		bool isDirectory)
	{
		if (string.IsNullOrWhiteSpace(candidatePath))
		{
			return false;
		}

		try
		{
			string normalizedCandidate = isDirectory
				? NormalizeDirectory(candidatePath)
				: NormalizeFile(candidatePath);
			return string.Equals(
				normalizedCandidate,
				expectedPath,
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (
			exception is ArgumentException or
				NotSupportedException or
				PathTooLongException)
		{
			return false;
		}
	}

	private static string NormalizeDirectory(string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

		if (!Path.IsPathFullyQualified(directoryPath))
		{
			throw new ArgumentException(
				"The local application data directory must be fully qualified.",
				nameof(directoryPath));
		}

		return Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(directoryPath));
	}

	private static string NormalizeFile(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		if (!Path.IsPathFullyQualified(filePath))
		{
			throw new ArgumentException(
				"The executable path must be fully qualified.",
				nameof(filePath));
		}

		return Path.GetFullPath(filePath);
	}
}

internal interface ICurrentUserLogonStartupRegistrationStore
{
	// Windows Registry 沒有 value-level compare-and-mutate。呼叫端必須先序列化
	// 本產品的 writer；mutation 後的回傳值只是重新觀察到的狀態，不保證可
	// 對抗同時寫入同名 value 的外部程序。
	LogonStartupRegistrationState Query(string expectedCommand);

	LogonStartupRegistrationState Enable(string expectedCommand);

	LogonStartupRegistrationState Overwrite(string expectedCommand);

	LogonStartupRegistrationState RemoveIfMatches(string expectedCommand);
}

internal sealed class CurrentUserLogonStartupRegistryStore :
	ICurrentUserLogonStartupRegistrationStore
{
	private readonly Func<RegistryKey> _openCurrentUser;
	private readonly string _subKeyPath;

	internal CurrentUserLogonStartupRegistryStore(
		string subKeyPath =
			WindowsLogonStartupRegistrationContract.RegistrySubKeyPath)
		: this(subKeyPath, OpenCurrentUser)
	{
	}

	internal CurrentUserLogonStartupRegistryStore(
		string subKeyPath,
		Func<RegistryKey> openCurrentUser)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
		_subKeyPath = subKeyPath;
		_openCurrentUser = openCurrentUser ??
			throw new ArgumentNullException(nameof(openCurrentUser));
	}

	public LogonStartupRegistrationState Query(string expectedCommand)
	{
		ValidateExpectedCommand(expectedCommand);
		using RegistryKey currentUser = _openCurrentUser();
		using RegistryKey? key = currentUser.OpenSubKey(
			_subKeyPath,
			writable: false);
		return Query(key, expectedCommand);
	}

	public LogonStartupRegistrationState Enable(string expectedCommand)
	{
		ValidateExpectedCommand(expectedCommand);
		using RegistryKey currentUser = _openCurrentUser();
		using RegistryKey key = currentUser.CreateSubKey(
			_subKeyPath,
			writable: true) ?? throw new InvalidOperationException(
				"Windows did not create the logon startup registration key.");
		LogonStartupRegistrationState state = Query(key, expectedCommand);

		if (state == LogonStartupRegistrationState.Absent)
		{
			WriteExpectedCommand(key, expectedCommand);
		}

		if (state == LogonStartupRegistrationState.Conflict)
		{
			return state;
		}

		return Query(key, expectedCommand);
	}

	public LogonStartupRegistrationState Overwrite(string expectedCommand)
	{
		ValidateExpectedCommand(expectedCommand);
		using RegistryKey currentUser = _openCurrentUser();
		using RegistryKey key = currentUser.CreateSubKey(
			_subKeyPath,
			writable: true) ?? throw new InvalidOperationException(
				"Windows did not create the logon startup registration key.");
		WriteExpectedCommand(key, expectedCommand);
		return Query(key, expectedCommand);
	}

	public LogonStartupRegistrationState RemoveIfMatches(
		string expectedCommand)
	{
		ValidateExpectedCommand(expectedCommand);
		using RegistryKey currentUser = _openCurrentUser();
		using RegistryKey? key = currentUser.OpenSubKey(
			_subKeyPath,
			writable: true);
		LogonStartupRegistrationState state = Query(key, expectedCommand);

		if (state != LogonStartupRegistrationState.ExactMatch)
		{
			return state;
		}

		key!.DeleteValue(
			WindowsLogonStartupRegistrationContract.ValueName,
			throwOnMissingValue: false);
		return Query(key, expectedCommand);
	}

	private static RegistryKey OpenCurrentUser()
	{
		return RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
	}

	private static LogonStartupRegistrationState Query(
		RegistryKey? key,
		string expectedCommand)
	{
		if (key is null)
		{
			return LogonStartupRegistrationState.Absent;
		}

		object? value = key.GetValue(
			WindowsLogonStartupRegistrationContract.ValueName,
			defaultValue: null,
			RegistryValueOptions.DoNotExpandEnvironmentNames);

		if (value is null)
		{
			return LogonStartupRegistrationState.Absent;
		}

		return (key.GetValueKind(
				WindowsLogonStartupRegistrationContract.ValueName) ==
				RegistryValueKind.String) &&
			(value is string command) &&
			string.Equals(
				command,
				expectedCommand,
				StringComparison.Ordinal)
			? LogonStartupRegistrationState.ExactMatch
			: LogonStartupRegistrationState.Conflict;
	}

	private static void ValidateExpectedCommand(string expectedCommand)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommand);

		if (expectedCommand.Length >
			WindowsLogonStartupRegistrationContract.MaximumCommandLength)
		{
			throw new ArgumentException(
				$"The Windows logon startup command cannot exceed " +
					$"{WindowsLogonStartupRegistrationContract.MaximumCommandLength} " +
					"characters.",
				nameof(expectedCommand));
		}
	}

	private static void WriteExpectedCommand(
		RegistryKey key,
		string expectedCommand)
	{
		key.SetValue(
			WindowsLogonStartupRegistrationContract.ValueName,
			expectedCommand,
			RegistryValueKind.String);
	}
}
