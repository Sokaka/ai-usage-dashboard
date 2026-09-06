using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterCommandLineTests
{
	private static readonly Uri DefaultFeedUri = new(
		"https://downloads.example.test/AiUsageDashboard-update-stable.json");

	[Fact]
	public void ExplicitLicensePromptSupportsInteractiveDelegationWithoutDuplicateSuccessNotifications()
	{
		UpdaterCommandLineOptions options = Parse(["update-online", "--prompt-for-licenses", "--skip-updater-refresh"]);
		Assert.True(options.ShouldPromptForLicenses);
		Assert.False(options.ShouldNotifyUser);
		Assert.False(options.ShouldRefreshUpdater);
		Assert.False(Parse(["update-online"]).ShouldPromptForLicenses);
		Assert.True(Parse([]).ShouldPromptForLicenses);
	}

	[Theory]
	[InlineData("update-online", "--prompt-for-licenses", "--prompt-for-licenses")]
	[InlineData("uninstall", "--confirm", "--prompt-for-licenses")]
	public void LicensePromptFlagRejectsDuplicatesAndNonInstallCommands(string command, string first, string second)
	{
		Assert.Throws<UpdaterCommandLineException>(() => Parse([command, first, second]));
	}

	[Fact]
	public void Parse_WithoutArguments_UsesInteractiveOnlineDefaults()
	{
		UpdaterCommandLineOptions options = Parse([]);

		Assert.Equal(UpdaterCommand.UpdateOnline, options.Command);
		Assert.Equal(DefaultFeedUri, options.FeedUri);
		Assert.Equal("stable", options.Channel);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboard",
			options.InstallRoot);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboardUpdater",
			options.MaintenanceRoot);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\AiUsageDashboard",
			options.UserDataRoot);
		Assert.True(options.ShouldRestart);
		Assert.True(options.ShouldRefreshUpdater);
		Assert.True(options.ShouldRegisterInstalledApp);
		Assert.False(options.IsUninstallConfirmed);
		Assert.True(options.ShouldNotifyUser);
	}

	[Fact]
	public void Parse_OnlineWithOverrides_UsesExplicitValues()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"update-online",
			"--feed-url",
			"https://mirror.example.test/preview.json",
			"--install-root",
			@"D:\Apps\AI Usage",
			"--no-restart",
			"--skip-updater-refresh"
		]);

		Assert.Equal(UpdaterCommand.UpdateOnline, options.Command);
		Assert.Equal(
			new Uri("https://mirror.example.test/preview.json"),
			options.FeedUri);
		Assert.Equal(@"D:\Apps\AI Usage", options.InstallRoot);
		Assert.False(options.ShouldRestart);
		Assert.False(options.ShouldRefreshUpdater);
		Assert.False(options.ShouldRegisterInstalledApp);
		Assert.False(options.ShouldNotifyUser);
	}

	[Fact]
	public void Parse_WithRequiredLocalArguments_UsesDefaultInstallRootAndRestart()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"apply-local",
			"--package",
			@"packages\release.zip",
			"--sha256",
			@"packages\release.zip.sha256"
		]);

		Assert.Equal(UpdaterCommand.ApplyLocal, options.Command);
		Assert.Equal(@"D:\work\packages\release.zip", options.PackagePath);
		Assert.Equal(
			@"D:\work\packages\release.zip.sha256",
			options.Sha256Path);
		Assert.Null(options.FeedUri);
		Assert.Equal(
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboard",
			options.InstallRoot);
		Assert.True(options.ShouldRestart);
		Assert.False(options.ShouldRefreshUpdater);
		Assert.True(options.ShouldRegisterInstalledApp);
	}

	[Fact]
	public void Parse_UninstallWithConfirmation_UsesManagedDefaults()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"uninstall",
			"--confirm"
		]);

		Assert.Equal(UpdaterCommand.Uninstall, options.Command);
		Assert.True(options.IsUninstallConfirmed);
		Assert.False(options.ShouldRestart);
		Assert.False(options.ShouldRefreshUpdater);
		Assert.True(options.ShouldRegisterInstalledApp);
		Assert.Null(options.FeedUri);
		Assert.Null(options.PackagePath);
		Assert.Null(options.Sha256Path);
		Assert.False(options.ShouldNotifyUser);
	}

	[Fact]
	public void Parse_UninstallWithNotify_RequestsUserNotification()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"uninstall",
			"--confirm",
			"--notify"
		]);

		Assert.Equal(UpdaterCommand.Uninstall, options.Command);
		Assert.True(options.IsUninstallConfirmed);
		Assert.True(options.ShouldNotifyUser);
	}

	[Fact]
	public void Parse_UninstallWithCanonicalRootAndTrailingSeparator_ManagesRegistration()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"uninstall",
			"--confirm",
			"--install-root",
			@"C:\Users\Test\AppData\Local\Programs\AiUsageDashboard\"
		]);

		Assert.True(options.ShouldRegisterInstalledApp);
	}

	[Fact]
	public void Parse_UninstallWithCustomRoot_DoesNotManageCanonicalRegistration()
	{
		UpdaterCommandLineOptions options = Parse(
		[
			"uninstall",
			"--confirm",
			"--install-root",
			@"D:\Apps\AI Usage"
		]);

		Assert.Equal(@"D:\Apps\AI Usage", options.InstallRoot);
		Assert.False(options.ShouldRegisterInstalledApp);
	}

	[Theory]
	[InlineData("install")]
	[InlineData("apply-local", "--package", "release.zip")]
	[InlineData("apply-local", "--sha256", "release.zip.sha256")]
	[InlineData(
		"apply-local",
		"--package",
		"release.zip",
		"--sha256",
		"release.zip.sha256",
		"--unknown")]
	[InlineData("update-online", "--package", "release.zip")]
	[InlineData("uninstall")]
	[InlineData("uninstall", "--confirm", "--no-restart")]
	[InlineData("uninstall", "--confirm", "--feed-url", "https://example.test/feed.json")]
	public void Parse_WithInvalidContract_Throws(params string[] arguments)
	{
		Assert.Throws<UpdaterCommandLineException>(() => Parse(arguments));
	}

	[Theory]
	[InlineData("--package", "first.zip", "--package", "second.zip")]
	[InlineData(
		"--sha256",
		"first.sha256",
		"--sha256",
		"second.sha256")]
	[InlineData("--install-root", @"D:\First", "--install-root", @"D:\Second")]
	[InlineData("--no-restart", "--no-restart")]
	public void Parse_LocalWithDuplicateOption_Throws(
		params string[] optionArguments)
	{
		List<string> arguments =
		[
			"apply-local",
			"--package",
			"release.zip",
			"--sha256",
			"release.zip.sha256",
			.. optionArguments
		];

		Assert.Throws<UpdaterCommandLineException>(() => Parse(arguments));
	}

	[Fact]
	public void Parse_UninstallWithDuplicateConfirmation_Throws()
	{
		Assert.Throws<UpdaterCommandLineException>(() => Parse(
		[
			"uninstall",
			"--confirm",
			"--confirm"
		]));
	}

	[Fact]
	public void Parse_UninstallWithDuplicateNotification_Throws()
	{
		Assert.Throws<UpdaterCommandLineException>(() => Parse(
		[
			"uninstall",
			"--confirm",
			"--notify",
			"--notify"
		]));
	}

	[Theory]
	[InlineData("relative.json")]
	[InlineData("https://user@example.test/feed.json")]
	[InlineData("https://example.test/feed.json#fragment")]
	public void Parse_OnlineWithUnsafeFeedUrl_Throws(string feedUrl)
	{
		Assert.Throws<UpdaterCommandLineException>(() => Parse(
		[
			"update-online",
			"--feed-url",
			feedUrl
		]));
	}

	[Fact]
	public void Parse_WhenOptionValueIsMissing_Throws()
	{
		UpdaterCommandLineException exception = Assert.Throws<
			UpdaterCommandLineException>(() => Parse(
			[
				"apply-local",
				"--package",
				"--sha256",
				"release.zip.sha256"
			]));

		Assert.Contains("--package", exception.Message, StringComparison.Ordinal);
	}

	private static UpdaterCommandLineOptions Parse(
		IReadOnlyList<string> arguments)
	{
		return UpdaterCommandLine.Parse(
			arguments,
			@"C:\Users\Test\AppData\Local",
			@"D:\work",
			DefaultFeedUri,
			"stable");
	}
}
