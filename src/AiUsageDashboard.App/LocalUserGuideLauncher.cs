using System.Diagnostics;
using System.IO;

namespace AiUsageDashboard.App;

internal enum LocalUserGuideOpenResult
{
	Opened,
	Missing,
	Failed
}

internal static class LocalUserGuideLauncher
{
	private const string UserGuideFileName = "README.md";

	internal static LocalUserGuideOpenResult Open()
	{
		return Open(
			AppContext.BaseDirectory,
			StartProcess,
			StartProcess);
	}

	internal static LocalUserGuideOpenResult Open(
		string baseDirectory,
		Action<ProcessStartInfo> startDefaultApplication,
		Action<ProcessStartInfo> startNotepad)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
		ArgumentNullException.ThrowIfNull(startDefaultApplication);
		ArgumentNullException.ThrowIfNull(startNotepad);

		string userGuidePath = Path.Combine(
			baseDirectory,
			UserGuideFileName);

		if (!File.Exists(userGuidePath))
		{
			return LocalUserGuideOpenResult.Missing;
		}

		try
		{
			startDefaultApplication(new ProcessStartInfo
			{
				FileName = userGuidePath,
				UseShellExecute = true
			});
			return LocalUserGuideOpenResult.Opened;
		}
		catch (Exception)
		{
			// Fall back to Notepad when Windows has no Markdown file association.
		}

		try
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = "notepad.exe",
				UseShellExecute = true
			};
			startInfo.ArgumentList.Add(userGuidePath);
			startNotepad(startInfo);
			return LocalUserGuideOpenResult.Opened;
		}
		catch (Exception)
		{
			return LocalUserGuideOpenResult.Failed;
		}
	}

	internal static string GetFailureMessage(LocalUserGuideOpenResult result)
	{
		return result switch
		{
			LocalUserGuideOpenResult.Missing =>
				"找不到使用說明。請重新解壓完整安裝包。",
			LocalUserGuideOpenResult.Failed =>
				"Windows 無法開啟使用說明。請到 app 資料夾手動開啟 README.md。",
			_ => throw new ArgumentOutOfRangeException(nameof(result))
		};
	}

	private static void StartProcess(ProcessStartInfo startInfo)
	{
		using Process? process = Process.Start(startInfo);
	}
}
