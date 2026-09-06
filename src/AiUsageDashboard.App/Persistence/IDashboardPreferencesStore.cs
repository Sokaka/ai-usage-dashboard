using AiUsageDashboard.App.ViewModels;

namespace AiUsageDashboard.App.Persistence;

public interface IDashboardPreferencesStore
{
	Task<UsageDisplayMode> LoadUsageDisplayModeAsync(
		CancellationToken cancellationToken = default);

	Task<UsageSortMode> LoadUsageSortModeAsync(
		CancellationToken cancellationToken = default);

	Task SaveUsageDisplayModeAsync(
		UsageDisplayMode displayMode,
		CancellationToken cancellationToken = default);

	Task SaveUsageSortModeAsync(
		UsageSortMode sortMode,
		CancellationToken cancellationToken = default);

	Task SaveUsagePreferencesAsync(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		CancellationToken cancellationToken = default);
}

internal enum DashboardStartupSurface
{
	Dashboard,
	Widget,
	DashboardAndWidget,
	Tray
}

internal sealed record DashboardShellPreferences(
	bool IsWidgetVisible,
	bool IsCollapsed,
	bool IsTopmost,
	string MonitorDeviceName,
	FloatingWidgetCorner Corner,
	DashboardStartupSurface StartupSurface,
	AppTheme Theme = AppTheme.ClassicBlue,
	bool IsHeightFollowingCardCount = false)
{
	internal static DashboardShellPreferences Default { get; } = new(
		IsWidgetVisible: true,
		IsCollapsed: false,
		IsTopmost: true,
		MonitorDeviceName: string.Empty,
		Corner: FloatingWidgetCorner.BottomRight,
		StartupSurface: DashboardStartupSurface.Widget,
		Theme: AppTheme.ClassicBlue,
		IsHeightFollowingCardCount: false);
}

internal sealed record DashboardPreferencesSnapshot(
	UsageSortMode UsageSortMode,
	UsageDisplayMode UsageDisplayMode,
	DashboardShellPreferences ShellPreferences);

internal enum DashboardPreferencesRecoveryStatus
{
	NotRequired,
	Pending,
	Ready
}

internal sealed record DashboardPreferencesRecoveryPrepareResult(
	DashboardPreferencesRecoveryStatus Status,
	long Generation);

internal sealed record DashboardPreferencesRecoveryCommitResult(
	DashboardPreferencesRecoveryStatus Status,
	long Generation,
	DashboardPreferencesSnapshot? Preferences);

internal interface IDashboardPreferencesRecoveryStore
{
	bool IsRecoveryActive { get; }

	Task<DashboardPreferencesRecoveryPrepareResult> PrepareRecoveryAsync(
		DashboardPreferencesSnapshot currentPreferences,
		CancellationToken cancellationToken = default);

	Task<DashboardPreferencesRecoveryCommitResult> CommitRecoveryAsync(
		long expectedGeneration,
		DashboardPreferencesSnapshot currentPreferences,
		CancellationToken cancellationToken = default);

	Task<bool> CompleteRecoveryAsync(
		long expectedGeneration,
		CancellationToken cancellationToken = default);
}

internal interface IWidgetPreferencesStore
{
	Task<DashboardShellPreferences> LoadDashboardShellPreferencesAsync(
		CancellationToken cancellationToken = default);

	Task SavePortablePreferencesAsync(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		DashboardShellPreferences shellPreferences,
		CancellationToken cancellationToken = default);

	Task SaveDashboardShellPreferencesAsync(
		DashboardShellPreferences preferences,
		CancellationToken cancellationToken = default);
}
