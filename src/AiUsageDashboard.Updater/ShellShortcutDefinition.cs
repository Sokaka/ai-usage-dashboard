namespace AiUsageDashboard.Updater;

internal sealed record ShellShortcutDefinition(
	string TargetPath,
	string WorkingDirectory,
	string Arguments,
	string Description,
	string IconPath,
	int IconIndex,
	int ShowCommand = 1,
	ushort Hotkey = 0);
