using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiUsageDashboard.Licensing;

public static class LegalNativeTermsDialog
{
	private const uint YesNoButtons = 0x00000004;
	private const uint InformationIcon = 0x00000040;
	private const uint DefaultSecondButton = 0x00000100;
	private const uint SetForeground = 0x00010000;
	private const int YesResult = 6;
	private const int NoResult = 7;

	public static bool EnsureAccepted(LegalCatalog catalog, LegalAcceptanceStore store)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(store);
		if (store.IsAccepted(catalog))
		{
			return true;
		}
		if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
		{
			throw new InvalidOperationException("License acceptance requires an interactive Windows desktop. Use --licenses and then --accept-licenses <displayed-digest> before unattended installation.");
		}

		string viewerPath = OpenReadableTerms(catalog);
		int result = MessageBox(nint.Zero,
			"已在記事本開啟完整授權與第三方條款。請先閱讀，再選擇是否接受。\n\n" +
			$"文件：{viewerPath}\n\n條款版本：{catalog.TermsVersion}\n" +
			$"接受範圍 SHA256：{catalog.Digest}\n\n" +
			"選擇「是」表示接受這個版本及範圍，並在目前 Windows 使用者的本機保存接受紀錄。選擇「否」即離開，不會進行安裝或更新。",
			"AI Usage Dashboard：是否接受第三方條款？",
			YesNoButtons | InformationIcon | DefaultSecondButton | SetForeground);
		if (result == 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot display the AI Usage Dashboard license acceptance dialog.");
		}
		if (result == NoResult)
		{
			return false;
		}
		if (result != YesResult)
		{
			throw new InvalidOperationException($"The license acceptance dialog returned unexpected result {result}.");
		}
		store.Accept(catalog, catalog.Digest);
		return true;
	}

	private static string OpenReadableTerms(LegalCatalog catalog)
	{
		string viewerDirectory = Path.Combine(Path.GetTempPath(), "AiUsageDashboard.LicenseText");
		Directory.CreateDirectory(viewerDirectory);
		string viewerPath = Path.Combine(viewerDirectory, catalog.Profile + "." + catalog.Digest + ".txt");
		byte[] text = LegalCatalog.StrictUtf8.GetBytes(catalog.GetReadableText());
		if (File.Exists(viewerPath))
		{
			if (!text.AsSpan().SequenceEqual(File.ReadAllBytes(viewerPath)))
			{
				throw new InvalidDataException($"The cached license text '{viewerPath}' does not match the embedded terms. Export to a new directory with --export-licenses.");
			}
		}
		else
		{
			using FileStream output = new(viewerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
			output.Write(text);
		}

		string notepadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
		ProcessStartInfo startInfo = new(notepadPath) { UseShellExecute = false };
		startInfo.ArgumentList.Add(viewerPath);
		// 記事本由使用者操作；只釋放 process handle，文字快取保留供視窗讀取。
		using Process viewer = Process.Start(startInfo) ??
			throw new InvalidOperationException($"Cannot open the full license text '{viewerPath}' in Windows Notepad.");
		return viewerPath;
	}

	[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int MessageBox(nint window, string text, string caption, uint type);
}
