using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterRefreshPolicyTests
{
	private const string FirstHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string SecondHash =
		"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

	[Theory]
	[InlineData("1.0.0", FirstHash, 100)]
	[InlineData("2.0.0", FirstHash, 100)]
	[InlineData("1.2.3", SecondHash, 100)]
	[InlineData("1.2.3", FirstHash, 101)]
	public void SignedInstallationRejectsAnUpdaterWithDifferentTermsOrBytes(string version, string hash, long sizeBytes)
	{
		CurrentUpdaterIdentity current = CreateCurrent(version, hash) with { SizeBytes = sizeBytes };
		Assert.Throws<InvalidDataException>(() => UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(
			current, CreateAvailable("1.2.3", FirstHash)));
	}

	[Fact]
	public void SignedInstallationAcceptsTheExactPublishedUpdater()
	{
		UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(
			CreateCurrent("1.2.3", FirstHash), CreateAvailable("1.2.3", FirstHash));
	}

	[Fact]
	public void Evaluate_WithNewerUpdater_Delegates()
	{
		Assert.Equal(
			UpdaterRefreshAction.DelegateToDownloadedUpdater,
			UpdaterRefreshPolicy.Evaluate(
				CreateCurrent("1.0.0", FirstHash),
				CreateAvailable("1.1.0", SecondHash)));
	}

	[Fact]
	public void Evaluate_WithOlderUpdater_RunsCurrentWithoutDowngrade()
	{
		Assert.Equal(
			UpdaterRefreshAction.RunCurrent,
			UpdaterRefreshPolicy.Evaluate(
				CreateCurrent("2.0.0", FirstHash),
				CreateAvailable("1.9.9", SecondHash)));
	}

	[Fact]
	public void Evaluate_WithSameUpdaterIdentity_RunsCurrent()
	{
		Assert.Equal(
			UpdaterRefreshAction.RunCurrent,
			UpdaterRefreshPolicy.Evaluate(
				CreateCurrent("1.2.3", FirstHash),
				CreateAvailable("1.2.3", FirstHash)));
	}

	[Fact]
	public void Evaluate_WithSameVersionAndDifferentBytes_Rejects()
	{
		Assert.Throws<InvalidDataException>(() =>
			UpdaterRefreshPolicy.Evaluate(
				CreateCurrent("1.2.3", FirstHash),
				CreateAvailable("1.2.3", SecondHash)));
	}

	private static UpdateReleaseArtifact CreateAvailable(
		string version,
		string hash)
	{
		return new UpdateReleaseArtifact(
			"AiUsageDashboard.Updater",
			version,
			"win-x64",
			$"AiUsageDashboard-Updater-{version}-win-x64.exe",
			$"https://downloads.example.test/updater-{version}.exe",
			SizeBytes: 100,
			hash,
			"0123456789abcdef0123456789abcdef01234567");
	}

	private static CurrentUpdaterIdentity CreateCurrent(
		string version,
		string hash)
	{
		return new CurrentUpdaterIdentity(
			@"C:\Tools\AiUsageDashboard.Updater.exe",
			version,
			SizeBytes: 100,
			hash);
	}
}
