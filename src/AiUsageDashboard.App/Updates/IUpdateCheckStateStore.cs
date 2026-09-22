namespace AiUsageDashboard.App.Updates;

internal interface IUpdateCheckStateStore
{
	Task<UpdateCheckPersistentState?> LoadAsync(
		CancellationToken cancellationToken = default);

	Task<bool> TrySaveAsync(
		UpdateCheckPersistentState state,
		CancellationToken cancellationToken = default);
}
