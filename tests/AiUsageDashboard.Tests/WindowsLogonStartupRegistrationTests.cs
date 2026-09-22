using Microsoft.Win32;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class WindowsLogonStartupRegistrationTests
{
	[Fact]
	public void Contract_BuildsCanonicalManagedInstallCommand()
	{
		const string localApplicationData =
			@"C:\SyntheticProfiles\測 試 & More\AppData\Local";
		string installRoot = Path.Combine(
			localApplicationData,
			"Programs",
			"AiUsageDashboard");
		string executablePath = Path.Combine(
			installRoot,
			"current",
			"app",
			"AiUsageDashboard.App.exe");

		Assert.Equal(
			installRoot,
			WindowsLogonStartupRegistrationContract.GetCanonicalInstallRoot(
				localApplicationData));
		Assert.Equal(
			executablePath,
			WindowsLogonStartupRegistrationContract.GetCanonicalExecutablePath(
				localApplicationData));
		Assert.Equal(
			$"\"{executablePath}\" --startup",
			WindowsLogonStartupRegistrationContract.CreateExpectedCommand(
				localApplicationData));
	}

	[Fact]
	public void MaintenanceUpdaterContract_BuildsCanonicalExecutablePath()
	{
		const string localApplicationData =
			@"C:\SyntheticProfiles\Test\AppData\Local";
		string maintenanceRoot = Path.Combine(
			localApplicationData,
			"Programs",
			"AiUsageDashboardUpdater");

		Assert.Equal(
			maintenanceRoot,
			MaintenanceUpdaterPathContract.GetCanonicalRoot(
				localApplicationData));
		Assert.Equal(
			Path.Combine(
				maintenanceRoot,
				"AiUsageDashboard.Updater.exe"),
			MaintenanceUpdaterPathContract.GetCanonicalExecutablePath(
				localApplicationData));
	}

	[Fact]
	public void Contract_ExecutablePathMatchesUpdaterManagedInstallationPath()
	{
		const string localApplicationData =
			@"C:\SyntheticProfiles\Test\AppData\Local";
		string installRoot = WindowsLogonStartupRegistrationContract
			.GetCanonicalInstallRoot(localApplicationData);

		Assert.Equal(
			ManagedInstallationPaths.GetInstalledAppExecutable(installRoot),
			WindowsLogonStartupRegistrationContract.GetCanonicalExecutablePath(
				localApplicationData));
	}

	[Fact]
	public void Contract_EligibilityRequiresCanonicalRootAndRunningExecutable()
	{
		const string localApplicationData =
			@"C:\SyntheticProfiles\Test\AppData\Local";
		string canonicalRoot = WindowsLogonStartupRegistrationContract
			.GetCanonicalInstallRoot(localApplicationData);
		string canonicalExecutable = WindowsLogonStartupRegistrationContract
			.GetCanonicalExecutablePath(localApplicationData);

		Assert.True(
			WindowsLogonStartupRegistrationContract.IsCanonicalInstallRoot(
				canonicalRoot.ToUpperInvariant() +
					Path.DirectorySeparatorChar,
				localApplicationData));
		Assert.True(
			WindowsLogonStartupRegistrationContract.IsCanonicalExecutablePath(
				canonicalExecutable.ToUpperInvariant(),
				localApplicationData));
		Assert.False(
			WindowsLogonStartupRegistrationContract.IsCanonicalInstallRoot(
				@"C:\Custom\AiUsageDashboard",
				localApplicationData));
		Assert.False(
			WindowsLogonStartupRegistrationContract.IsCanonicalExecutablePath(
				@"D:\Portable\AiUsageDashboard.App.exe",
				localApplicationData));
		Assert.False(
			WindowsLogonStartupRegistrationContract.IsCanonicalExecutablePath(
				"AiUsageDashboard.App.exe",
				localApplicationData));
	}

	[Fact]
	public void Contract_CommandLengthBoundary_Allows260AndRejects261Characters()
	{
		const string canonicalSuffix =
			@"\Programs\AiUsageDashboard\current\app\AiUsageDashboard.App.exe";
		int commandDecorationLength = 3 +
			WindowsLogonStartupRegistrationContract.StartupArgument.Length;
		int localApplicationDataLength =
			WindowsLogonStartupRegistrationContract.MaximumCommandLength -
			canonicalSuffix.Length -
			commandDecorationLength;
		string localApplicationData = @"C:\" + new string(
			'a',
			localApplicationDataLength - 3);

		string command = WindowsLogonStartupRegistrationContract
			.CreateExpectedCommand(localApplicationData);

		Assert.Equal(
			WindowsLogonStartupRegistrationContract.MaximumCommandLength,
			command.Length);
		Assert.Throws<ArgumentException>(() =>
			WindowsLogonStartupRegistrationContract.CreateExpectedCommand(
				localApplicationData + "a"));
	}

	[Fact]
	public void Contract_InvalidOrRelativePaths_AreRejectedOrIneligible()
	{
		Assert.Throws<ArgumentException>(() =>
			WindowsLogonStartupRegistrationContract.CreateExpectedCommand(
				"relative"));
		Assert.Throws<ArgumentException>(() =>
			WindowsLogonStartupRegistrationContract.CreateExpectedCommand(
				"C:\\SyntheticProfiles\\Invalid\"Path\\AppData\\Local"));
		Assert.False(
			WindowsLogonStartupRegistrationContract.IsCanonicalInstallRoot(
				"relative",
				@"C:\SyntheticProfiles\Test\AppData\Local"));
		Assert.False(
			WindowsLogonStartupRegistrationContract.IsCanonicalExecutablePath(
				null,
				@"C:\SyntheticProfiles\Test\AppData\Local"));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public void RegistryStore_EnablesQueriesOverwritesAndRemovesByExactMatch()
	{
		string testRoot = CreateTestRoot();
		string subKeyPath = $@"{testRoot}\Run";
		CurrentUserLogonStartupRegistryStore store = new(subKeyPath);
		string expectedCommand = WindowsLogonStartupRegistrationContract
			.CreateExpectedCommand(
				@"C:\SyntheticProfiles\Test\AppData\Local");
		const string conflictingCommand =
			@"""C:\Portable\AiUsageDashboard.App.exe"" --startup";

		try
		{
			Assert.Equal(
				LogonStartupRegistrationState.Absent,
				store.Query(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.ExactMatch,
				store.Enable(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.ExactMatch,
				store.Enable(expectedCommand));
			AssertRegistryValue(
				subKeyPath,
				expectedCommand,
				RegistryValueKind.String);

			SetRegistryValue(
				subKeyPath,
				conflictingCommand,
				RegistryValueKind.String);

			Assert.Equal(
				LogonStartupRegistrationState.Conflict,
				store.Query(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.Conflict,
				store.Enable(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.Conflict,
				store.RemoveIfMatches(expectedCommand));
			AssertRegistryValue(
				subKeyPath,
				conflictingCommand,
				RegistryValueKind.String);

			Assert.Equal(
				LogonStartupRegistrationState.ExactMatch,
				store.Overwrite(expectedCommand));
			AssertRegistryValue(
				subKeyPath,
				expectedCommand,
				RegistryValueKind.String);
			Assert.Equal(
				LogonStartupRegistrationState.Absent,
				store.RemoveIfMatches(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.Absent,
				store.RemoveIfMatches(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.Absent,
				store.Query(expectedCommand));
		}
		finally
		{
			DeleteTestRoot(testRoot);
		}
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public void RegistryStore_ExpandStringWithEquivalentExpansion_IsConflict()
	{
		string testRoot = CreateTestRoot();
		string subKeyPath = $@"{testRoot}\Run";
		CurrentUserLogonStartupRegistryStore store = new(subKeyPath);
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		string expectedCommand = WindowsLogonStartupRegistrationContract
			.CreateExpectedCommand(localApplicationData);
		string expandableCommand = expectedCommand.Replace(
			localApplicationData,
			"%LOCALAPPDATA%",
			StringComparison.OrdinalIgnoreCase);

		try
		{
			SetRegistryValue(
				subKeyPath,
				expandableCommand,
				RegistryValueKind.ExpandString);

			Assert.Equal(
				LogonStartupRegistrationState.Conflict,
				store.Query(expectedCommand));
			Assert.Equal(
				LogonStartupRegistrationState.Conflict,
				store.Enable(expectedCommand));
			AssertRegistryValue(
				subKeyPath,
				expandableCommand,
				RegistryValueKind.ExpandString);
			Assert.Equal(
				LogonStartupRegistrationState.ExactMatch,
				store.Overwrite(expectedCommand));
			AssertRegistryValue(
				subKeyPath,
				expectedCommand,
				RegistryValueKind.String);
		}
		finally
		{
			DeleteTestRoot(testRoot);
		}
	}

	[Fact]
	public void RegistryStore_RegistryFailuresPropagateInsteadOfReturningState()
	{
		CurrentUserLogonStartupRegistryStore store = new(
			@"Software\AiUsageDashboard.Tests\SyntheticFailure",
			() => throw new IOException("Synthetic registry failure."));
		const string expectedCommand =
			@"""C:\App\AiUsageDashboard.App.exe"" --startup";

		Assert.Throws<IOException>(() => store.Query(expectedCommand));
		Assert.Throws<IOException>(() => store.Enable(expectedCommand));
		Assert.Throws<IOException>(() => store.Overwrite(expectedCommand));
		Assert.Throws<IOException>(() =>
			store.RemoveIfMatches(expectedCommand));
	}

	private static string CreateTestRoot()
	{
		return $@"Software\AiUsageDashboard.Tests\{Guid.NewGuid():N}";
	}

	private static void SetRegistryValue(
		string subKeyPath,
		string value,
		RegistryValueKind kind)
	{
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		using RegistryKey key = currentUser.CreateSubKey(
			subKeyPath,
			writable: true) ?? throw new InvalidOperationException(
				"Test registry key was not created.");
		key.SetValue(
			WindowsLogonStartupRegistrationContract.ValueName,
			value,
			kind);
	}

	private static void AssertRegistryValue(
		string subKeyPath,
		string expectedValue,
		RegistryValueKind expectedKind)
	{
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		using RegistryKey key = currentUser.OpenSubKey(subKeyPath) ??
			throw new InvalidOperationException("Test registry key was not found.");
		Assert.Equal(
			expectedValue,
			key.GetValue(
				WindowsLogonStartupRegistrationContract.ValueName,
				defaultValue: null,
				RegistryValueOptions.DoNotExpandEnvironmentNames));
		Assert.Equal(
			expectedKind,
			key.GetValueKind(
				WindowsLogonStartupRegistrationContract.ValueName));
	}

	private static void DeleteTestRoot(string testRoot)
	{
		using RegistryKey currentUser = RegistryKey.OpenBaseKey(
			RegistryHive.CurrentUser,
			RegistryView.Registry64);
		currentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
	}
}
