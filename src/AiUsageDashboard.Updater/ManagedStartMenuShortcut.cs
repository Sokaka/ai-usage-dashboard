using System.Security;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class ManagedStartMenuShortcut : IManagedStartMenuShortcut
{
	internal const string ShortcutFileName = "AI Usage.lnk";
	internal const string ShortcutDescription = "AI Usage Dashboard managed installation";
	private readonly Func<string> _getProgramsRoot;

	internal ManagedStartMenuShortcut(string programsRoot)
		: this(() => programsRoot)
	{
	}

	internal ManagedStartMenuShortcut(Func<string>? getProgramsRoot = null)
	{
		_getProgramsRoot = getProgramsRoot ?? (() => Environment.GetFolderPath(
			Environment.SpecialFolder.Programs,
			Environment.SpecialFolderOption.DoNotVerify));
	}

	public string? EnsurePresent(string installRoot)
	{
		string? shortcutPath = null;

		try
		{
			string programsRoot = ResolveProgramsRoot();
			shortcutPath = Path.Combine(programsRoot, ShortcutFileName);
			ShellShortcutDefinition expected = CreateExpectedDefinition(installRoot);
			UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
				expected.WorkingDirectory);
			UpdateTransaction.ThrowIfNotOrdinaryFile(expected.TargetPath, mustExist: true);
			EnsureSafeShortcutPath(programsRoot, shortcutPath);
			Directory.CreateDirectory(programsRoot);
			EnsureSafeShortcutPath(programsRoot, shortcutPath);

			if (File.Exists(shortcutPath))
			{
				return GetConflictWarning(shortcutPath, expected);
			}

			string temporaryPath = Path.Combine(
				programsRoot,
				$".AiUsageDashboard-{Guid.NewGuid():N}.lnk");

			try
			{
				WindowsShellShortcut.WriteNew(temporaryPath, expected);
				EnsureSafeShortcutPath(programsRoot, shortcutPath);
				UpdateTransaction.ThrowIfNotOrdinaryFile(temporaryPath, mustExist: true);
				File.Move(temporaryPath, shortcutPath, overwrite: false);
			}
			finally
			{
				EnsureSafeShortcutPath(programsRoot, temporaryPath);

				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}

			return GetConflictWarning(shortcutPath, expected);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or ArgumentException)
		{
			return $"無法建立開始選單捷徑「{shortcutPath ?? ShortcutFileName}」；請檢查該路徑與權限，再重新執行更新程式。{exception.Message}";
		}
	}

	public string? RemoveIfMatches(string installRoot)
	{
		string? shortcutPath = null;

		try
		{
			string programsRoot = ResolveProgramsRoot();
			shortcutPath = Path.Combine(programsRoot, ShortcutFileName);
			EnsureSafeShortcutPath(programsRoot, shortcutPath);

			if (!File.Exists(shortcutPath))
			{
				return null;
			}

			string? warning = GetConflictWarning(
				shortcutPath,
				CreateExpectedDefinition(installRoot));

			if (warning is not null)
			{
				return warning;
			}

			EnsureSafeShortcutPath(programsRoot, shortcutPath);
			File.Delete(shortcutPath);
			return null;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or ArgumentException)
		{
			return $"無法清理開始選單捷徑「{shortcutPath ?? ShortcutFileName}」，已保留；請檢查該路徑與權限。{exception.Message}";
		}
	}

	private static ShellShortcutDefinition CreateExpectedDefinition(string installRoot)
	{
		string targetPath = ManagedInstallationPaths.GetInstalledAppExecutable(installRoot);
		string workingDirectory = Path.GetDirectoryName(targetPath) ??
			throw new InvalidOperationException($"開始選單目標缺少父目錄：{targetPath}");
		return new ShellShortcutDefinition(
			targetPath,
			workingDirectory,
			string.Empty,
			ShortcutDescription,
			targetPath,
			0);
	}

	private static string? GetConflictWarning(
		string shortcutPath,
		ShellShortcutDefinition expected)
	{
		ShellShortcutDefinition actual = WindowsShellShortcut.Read(shortcutPath);
		bool matches = (string.Equals(actual.TargetPath, expected.TargetPath, StringComparison.OrdinalIgnoreCase)) &&
			(string.Equals(actual.WorkingDirectory, expected.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) &&
			(string.Equals(actual.IconPath, expected.IconPath, StringComparison.OrdinalIgnoreCase)) &&
			(actual.Arguments == expected.Arguments) &&
			(actual.Description == expected.Description) &&
			(actual.IconIndex == expected.IconIndex) &&
			(actual.ShowCommand == expected.ShowCommand) &&
			(actual.Hotkey == expected.Hotkey);
		return matches
			? null
			: $"開始選單捷徑「{shortcutPath}」的內容與此安裝不符，已保留；請檢查該捷徑，移開同名衝突後再重新執行更新程式。";
	}

	private static void EnsureSafeShortcutPath(string programsRoot, string shortcutPath)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(programsRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(shortcutPath, mustExist: false);
	}

	private string ResolveProgramsRoot()
	{
		string programsRoot = _getProgramsRoot();

		if (string.IsNullOrWhiteSpace(programsRoot))
		{
			throw new InvalidDataException("Windows 未提供目前使用者的開始選單 Programs 目錄。");
		}

		return ManagedInstallationPaths.NormalizeDirectory(programsRoot);
	}
}
