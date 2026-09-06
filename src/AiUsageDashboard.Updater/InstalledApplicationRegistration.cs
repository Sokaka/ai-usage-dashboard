using Microsoft.Win32;

namespace AiUsageDashboard.Updater;

internal sealed record InstalledApplicationRegistration(
	string DisplayVersion,
	string InstallLocation,
	string DisplayIcon,
	string UninstallCommand,
	string QuietUninstallCommand,
	string InstallDate);

internal interface IInstalledApplicationRegistrationStore
{
	void Upsert(InstalledApplicationRegistration registration);

	bool HasMatchingInstallLocation(string installRoot);

	bool RemoveIfMatches(string installRoot);
}

internal sealed class CurrentUserUninstallRegistryStore :
	IInstalledApplicationRegistrationStore
{
	internal const string DefaultSubKeyPath =
		@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AiUsageDashboard";
	private readonly string _subKeyPath;

	internal CurrentUserUninstallRegistryStore(
		string subKeyPath = DefaultSubKeyPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
		_subKeyPath = subKeyPath;
	}

	public void Upsert(InstalledApplicationRegistration registration)
	{
		ArgumentNullException.ThrowIfNull(registration);
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		using RegistryKey key = currentUser.CreateSubKey(
			_subKeyPath,
			writable: true) ?? throw new InvalidOperationException(
				"Windows did not create the Installed apps registration key.");
		key.SetValue("DisplayName", "AI Usage", RegistryValueKind.String);
		key.SetValue(
			"DisplayVersion",
			registration.DisplayVersion,
			RegistryValueKind.String);
		key.SetValue(
			"InstallLocation",
			registration.InstallLocation,
			RegistryValueKind.String);
		key.SetValue(
			"DisplayIcon",
			registration.DisplayIcon,
			RegistryValueKind.String);
		key.SetValue(
			"UninstallString",
			registration.UninstallCommand,
			RegistryValueKind.String);
		key.SetValue(
			"QuietUninstallString",
			registration.QuietUninstallCommand,
			RegistryValueKind.String);
		key.SetValue(
			"InstallDate",
			registration.InstallDate,
			RegistryValueKind.String);
		key.SetValue("NoModify", 1, RegistryValueKind.DWord);
		key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
		key.DeleteValue("NoRemove", throwOnMissingValue: false);
		key.DeleteValue("ModifyPath", throwOnMissingValue: false);
		key.DeleteValue("WindowsInstaller", throwOnMissingValue: false);
	}

	public bool HasMatchingInstallLocation(string installRoot)
	{
		string normalizedInstallRoot =
			ManagedInstallationPaths.NormalizeDirectory(installRoot);
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		using RegistryKey? key = currentUser.OpenSubKey(
			_subKeyPath,
			writable: false);
		return key?.GetValue("InstallLocation") is string registeredRoot &&
			string.Equals(
				ManagedInstallationPaths.NormalizeDirectory(registeredRoot),
				normalizedInstallRoot,
				StringComparison.OrdinalIgnoreCase);
	}

	public bool RemoveIfMatches(string installRoot)
	{
		string normalizedInstallRoot =
			ManagedInstallationPaths.NormalizeDirectory(installRoot);
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		using (RegistryKey? key = currentUser.OpenSubKey(
			_subKeyPath,
			writable: false))
		{
			if (key is null)
			{
				return false;
			}

			if (key.GetValue("InstallLocation") is not string registeredRoot ||
				!string.Equals(
					ManagedInstallationPaths.NormalizeDirectory(registeredRoot),
					normalizedInstallRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"The Installed apps entry belongs to another install root.");
			}
		}

		currentUser.DeleteSubKeyTree(
			_subKeyPath,
			throwOnMissingSubKey: false);
		return true;
	}
}
