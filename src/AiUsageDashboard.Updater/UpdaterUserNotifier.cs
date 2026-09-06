using System.Runtime.InteropServices;

namespace AiUsageDashboard.Updater;

internal static class UpdaterUserNotifier
{
	private const uint ErrorIcon = 0x00000010;
	private const uint InformationIcon = 0x00000040;
	private const uint OkButton = 0x00000000;
	private const string Title = "AI Usage Updater";

	internal static void ShowError(string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		_ = MessageBox(
			IntPtr.Zero,
			message,
			Title,
			OkButton | ErrorIcon);
	}

	internal static void ShowSuccess(string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		_ = MessageBox(
			IntPtr.Zero,
			message,
			Title,
			OkButton | InformationIcon);
	}

	[DllImport(
		"user32.dll",
		EntryPoint = "MessageBoxW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	private static extern int MessageBox(
		IntPtr windowHandle,
		string text,
		string caption,
		uint type);
}
