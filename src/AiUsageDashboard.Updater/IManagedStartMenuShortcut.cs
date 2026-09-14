namespace AiUsageDashboard.Updater;

internal interface IManagedStartMenuShortcut
{
	string? EnsurePresent(string installRoot);

	string? RemoveIfMatches(string installRoot);
}
