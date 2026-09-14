using System.Diagnostics;

namespace AiUsageDashboard.App;

internal enum AboutLink
{
	Releases,
	ReportIssue
}

internal static class AboutLinkLauncher
{
	internal static string GetUrl(AboutLink link)
	{
		return link switch
		{
			AboutLink.Releases =>
				"https://github.com/Sokaka/ai-usage-dashboard/releases",
			AboutLink.ReportIssue =>
				"https://github.com/Sokaka/ai-usage-dashboard/issues/new/choose",
			_ => throw new ArgumentOutOfRangeException(
				nameof(link), link, "無法開啟未定義的 AI Usage 支援連結。")
		};
	}

	internal static void Open(AboutLink link)
	{
		Open(link, Process.Start);
	}

	internal static void Open(
		AboutLink link,
		Func<ProcessStartInfo, Process?> startProcess)
	{
		ArgumentNullException.ThrowIfNull(startProcess);
		using Process? process = startProcess(new ProcessStartInfo
		{
			FileName = GetUrl(link),
			UseShellExecute = true
		});
	}
}
