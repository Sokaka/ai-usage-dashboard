using System.Diagnostics;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class LocalUserGuideLauncherTests
{
	[Fact]
	public void Open_WhenGuideExists_UsesDefaultApplication()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string userGuidePath = Path.Combine(
			temporaryDirectory.Path,
			"README.md");
		File.WriteAllText(userGuidePath, "# 使用說明");
		ProcessStartInfo? observed = null;
		bool wasNotepadStarted = false;

		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open(
			temporaryDirectory.Path,
			startInfo => observed = startInfo,
			_ => wasNotepadStarted = true);

		Assert.Equal(LocalUserGuideOpenResult.Opened, result);
		Assert.NotNull(observed);
		Assert.Equal(userGuidePath, observed.FileName);
		Assert.True(observed.UseShellExecute);
		Assert.False(wasNotepadStarted);
	}

	[Fact]
	public void Open_WhenGuideIsMissing_DoesNotStartProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		bool wasStarted = false;

		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open(
			temporaryDirectory.Path,
			_ => wasStarted = true,
			_ => wasStarted = true);

		Assert.Equal(LocalUserGuideOpenResult.Missing, result);
		Assert.False(wasStarted);
	}

	[Fact]
	public void Open_WhenDefaultApplicationFails_UsesNotepad()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string userGuidePath = Path.Combine(
			temporaryDirectory.Path,
			"README.md");
		File.WriteAllText(userGuidePath, "# 使用說明");
		ProcessStartInfo? observed = null;

		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open(
			temporaryDirectory.Path,
			_ => throw new InvalidOperationException("Test failure."),
			startInfo => observed = startInfo);

		Assert.Equal(LocalUserGuideOpenResult.Opened, result);
		Assert.NotNull(observed);
		Assert.Equal("notepad.exe", observed.FileName);
		Assert.True(observed.UseShellExecute);
		Assert.Equal(new[] { userGuidePath }, observed.ArgumentList);
	}

	[Fact]
	public void Open_WhenAllApplicationsFail_ReturnsFailed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		File.WriteAllText(
			Path.Combine(temporaryDirectory.Path, "README.md"),
			"# 使用說明");

		LocalUserGuideOpenResult result = LocalUserGuideLauncher.Open(
			temporaryDirectory.Path,
			_ => throw new InvalidOperationException("Test failure."),
			_ => throw new InvalidOperationException("Test failure."));

		Assert.Equal(LocalUserGuideOpenResult.Failed, result);
	}
}
