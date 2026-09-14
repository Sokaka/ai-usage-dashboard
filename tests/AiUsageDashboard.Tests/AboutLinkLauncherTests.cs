using System.ComponentModel;
using System.Diagnostics;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class AboutLinkLauncherTests
{
	private sealed class DisposalObservedProcess : Process
	{
		internal bool HasBeenDisposed { get; private set; }

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);

			if (disposing)
			{
				HasBeenDisposed = true;
			}
		}
	}

	[Theory]
	[InlineData("Releases", "https://github.com/Sokaka/ai-usage-dashboard/releases")]
	[InlineData("ReportIssue", "https://github.com/Sokaka/ai-usage-dashboard/issues/new/choose")]
	public void Open_WithKnownLink_UsesFixedShellUrlAndDisposesProcess(
		string linkName,
		string expectedUrl)
	{
		using DisposalObservedProcess process = new();
		ProcessStartInfo? observed = null;

		AboutLinkLauncher.Open(
			Enum.Parse<AboutLink>(linkName),
			startInfo =>
			{
				observed = startInfo;
				return process;
			});

		Assert.NotNull(observed);
		Assert.Equal(expectedUrl, observed.FileName);
		Assert.True(observed.UseShellExecute);
		Assert.Empty(observed.ArgumentList);
		Assert.Empty(observed.Arguments);
		Assert.True(process.HasBeenDisposed);
	}

	[Fact]
	public void Open_WhenShellReusesBrowserWithoutProcess_Completes()
	{
		bool wasStarted = false;

		AboutLinkLauncher.Open(AboutLink.Releases, _ =>
		{
			wasStarted = true;
			return null;
		});

		Assert.True(wasStarted);
	}

	[Fact]
	public void Open_WhenWindowsRejectsLaunch_PropagatesOriginalError()
	{
		Win32Exception original = new(5, "Synthetic browser launch failure.");

		Win32Exception actual = Assert.Throws<Win32Exception>(() =>
			AboutLinkLauncher.Open(AboutLink.ReportIssue, _ => throw original));

		Assert.Same(original, actual);
	}

	[Fact]
	public void Open_WithUndefinedLink_RejectsBeforeLaunching()
	{
		bool wasStarted = false;

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			AboutLinkLauncher.Open((AboutLink)int.MaxValue, _ =>
			{
				wasStarted = true;
				return null;
			}));

		Assert.False(wasStarted);
	}
}
