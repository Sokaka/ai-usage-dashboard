using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.ViewModels;

namespace AiUsageDashboard.Tests;

public sealed class JsonDashboardPreferencesStoreTests
{
	public enum UsageLoadOperation
	{
		DisplayMode,
		SortMode
	}

	public enum UsageSaveOperation
	{
		DisplayMode,
		SortMode,
		BothModes
	}

	[Fact]
	public async Task LoadUsageDisplayModeAsync_WhenFileIsMissing_ReturnsUsed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		UsageDisplayMode displayMode = await store.LoadUsageDisplayModeAsync();

		Assert.Equal(UsageDisplayMode.Used, displayMode);
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveUsageDisplayModeAsync_RoundTripsRemainingMode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await store.SaveUsageDisplayModeAsync(UsageDisplayMode.Remaining);
		UsageDisplayMode displayMode =
			await new JsonDashboardPreferencesStore(filePath)
				.LoadUsageDisplayModeAsync();
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(UsageDisplayMode.Remaining, displayMode);
		Assert.Contains(
			"\"usageDisplayMode\": \"Remaining\"",
			json,
			StringComparison.Ordinal);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task LoadUsageSortModeAsync_WhenFileIsMissing_ReturnsManual()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		UsageSortMode sortMode = await store.LoadUsageSortModeAsync();

		Assert.Equal(UsageSortMode.Manual, sortMode);
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_RoundTripsAutomaticMode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await store.SaveUsageSortModeAsync(UsageSortMode.Automatic);
		UsageSortMode sortMode = await new JsonDashboardPreferencesStore(filePath)
			.LoadUsageSortModeAsync();
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(UsageSortMode.Automatic, sortMode);
		Assert.Contains("\"usageSortMode\": \"Automatic\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveUsagePreferencesAsync_SavesBothModesAndPreservesShellPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expectedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		await store.SaveDashboardShellPreferencesAsync(expectedShellPreferences);

		await store.SaveUsagePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining);

		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await store.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveUsagePreferencesAsync_WithNewerSchema_ThrowsWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string FutureDocument =
			"{\"schemaVersion\":8,\"usageSortMode\":\"Manual\"," +
			"\"usageDisplayMode\":\"Used\",\"futureField\":true}";
		await File.WriteAllTextAsync(filePath, FutureDocument);
		JsonDashboardPreferencesStore store = new(filePath);

		await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
			() => store.SaveUsagePreferencesAsync(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		Assert.Equal(FutureDocument, await File.ReadAllTextAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task UsageLoads_WhenExternalFileChangesAgainBeforeRecovery_PreserveLatestExternalUsage()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences initialShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences firstExternalShellPreferences =
			initialShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY2",
				Corner = FloatingWidgetCorner.TopLeft
			};
		DashboardShellPreferences latestExternalShellPreferences =
			initialShellPreferences with
			{
				IsWidgetVisible = false,
				MonitorDeviceName = @"\\.\DISPLAY3",
				Corner = FloatingWidgetCorner.BottomLeft,
				StartupSurface = DashboardStartupSurface.Tray
			};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			initialShellPreferences);
		JsonDashboardPreferencesStore externalWriter = new(filePath);
		await externalWriter.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			firstExternalShellPreferences);
		int recoveryRequestCount = 0;
		store.RecoveryRequested += () => recoveryRequestCount++;

		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());

		await externalWriter.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			latestExternalShellPreferences);
		byte[] latestExternalDocument = await File.ReadAllBytesAsync(filePath);
		DashboardPreferencesSnapshot currentPreferences = new(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			initialShellPreferences);
		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(currentPreferences);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				currentPreferences);
		DashboardPreferencesSnapshot expectedPreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			latestExternalShellPreferences);

		Assert.Equal(2, recoveryRequestCount);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, prepareResult.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(expectedPreferences, commitResult.Preferences);
		Assert.Equal(latestExternalDocument, await File.ReadAllBytesAsync(filePath));
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
		JsonDashboardPreferencesStore reloadedStore = new(filePath);
		Assert.Equal(
			UsageSortMode.Manual,
			await reloadedStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Used,
			await reloadedStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			latestExternalShellPreferences,
			await reloadedStore.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Theory]
	[InlineData("{\"schemaVersion\":8,\"usageSortMode\":\"Automatic\"}")]
	[InlineData("{\"schemaVersion\":1,\"usageSortMode\":\"Unknown\"}")]
	[InlineData("not-json")]
	public async Task LoadUsageSortModeAsync_WithUnsupportedOrInvalidDocument_ReturnsManual(
		string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(filePath, json);
		JsonDashboardPreferencesStore store = new(filePath);

		UsageSortMode sortMode = await store.LoadUsageSortModeAsync();

		Assert.Equal(UsageSortMode.Manual, sortMode);
		Assert.Equal(json, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task LoadPreferencesAsync_WithOversizedDocument_ReturnsDefaultsWithoutChangingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string document =
			"{\"schemaVersion\":3,\"usageDisplayMode\":\"Remaining\"," +
			"\"usageSortMode\":\"Automatic\",\"isWidgetVisible\":true," +
			"\"isCollapsed\":true,\"isTopmost\":false," +
			"\"monitorDeviceName\":\"DISPLAY2\",\"corner\":\"TopLeft\"," +
			"\"startupSurface\":\"Widget\"}" +
			new string(' ', (64 * 1024) + 1);
		await File.WriteAllTextAsync(filePath, document);
		JsonDashboardPreferencesStore store = new(filePath);

		UsageDisplayMode displayMode = await store.LoadUsageDisplayModeAsync();
		UsageSortMode sortMode = await store.LoadUsageSortModeAsync();
		DashboardShellPreferences shellPreferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(UsageDisplayMode.Used, displayMode);
		Assert.Equal(UsageSortMode.Manual, sortMode);
		Assert.Equal(DashboardShellPreferences.Default, shellPreferences);
		Assert.Equal(document, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_WithNewerSchema_ThrowsWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string FutureDocument =
			"{\"schemaVersion\":8,\"usageSortMode\":\"Automatic\",\"futureField\":true}";
		await File.WriteAllTextAsync(filePath, FutureDocument);
		JsonDashboardPreferencesStore store = new(filePath);

		await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
			() => store.SaveUsageSortModeAsync(UsageSortMode.Manual));

		Assert.Equal(FutureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_WithOversizedDocument_ThrowsWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string document =
			"{\"schemaVersion\":3,\"usageSortMode\":\"Automatic\"}" +
			new string(' ', (64 * 1024) + 1);
		await File.WriteAllTextAsync(filePath, document);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardPreferencesSaveBlockedException exception =
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
			() => store.SaveUsageSortModeAsync(UsageSortMode.Manual));

		Assert.Equal(
			DashboardPreferencesSaveBlockReason.DocumentTooLarge,
			exception.Reason);
		Assert.Equal(document, await File.ReadAllTextAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_WithUnknownMode_ThrowsWithoutWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => store.SaveUsageSortModeAsync((UsageSortMode)int.MaxValue));

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_RoundTripsAndPreservesSortMode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expected = new(
			IsWidgetVisible: true,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.DashboardAndWidget);

		await store.SaveUsageSortModeAsync(UsageSortMode.Automatic);
		await store.SaveDashboardShellPreferencesAsync(expected);
		DashboardShellPreferences actual =
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync();
		UsageSortMode sortMode =
			await new JsonDashboardPreferencesStore(filePath)
				.LoadUsageSortModeAsync();

		Assert.Equal(expected, actual);
		Assert.Equal(UsageSortMode.Automatic, sortMode);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_RoundTripsShellPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expected = DashboardShellPreferences.Default with
		{
			Theme = AppTheme.Sakura,
			IsHeightFollowingCardCount = true,
			CollapsedPositionXRatio = 0.25,
			CollapsedPositionYRatio = 0.75
		};

		await store.SaveDashboardShellPreferencesAsync(expected);

		Assert.Equal(
			expected,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		string json = await File.ReadAllTextAsync(filePath);
		Assert.Contains("\"schemaVersion\": 7", json, StringComparison.Ordinal);
		Assert.Contains("\"theme\": \"Sakura\"", json, StringComparison.Ordinal);
		Assert.Contains(
			"\"isHeightFollowingCardCount\": true",
			json,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"collapsedPositionXRatio\": 0.25",
			json,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"collapsedPositionYRatio\": 0.75",
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain("showClaudeOrganization", json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_PreservesDashboardShellPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expected = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomLeft,
			StartupSurface: DashboardStartupSurface.Widget);

		await store.SaveDashboardShellPreferencesAsync(expected);
		await store.SaveUsageSortModeAsync(UsageSortMode.Automatic);

		Assert.Equal(
			expected,
			await store.LoadDashboardShellPreferencesAsync());
	}

	[Fact]
	public async Task SaveUsageSortModeAsync_PreservesUsageDisplayMode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await store.SaveUsageDisplayModeAsync(UsageDisplayMode.Remaining);
		await store.SaveUsageSortModeAsync(UsageSortMode.Automatic);

		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
	}

	[Fact]
	public async Task SaveUsageDisplayModeAsync_PreservesSortAndShellPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expectedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.TopRight,
			StartupSurface: DashboardStartupSurface.DashboardAndWidget);

		await store.SaveUsageSortModeAsync(UsageSortMode.Automatic);
		await store.SaveDashboardShellPreferencesAsync(expectedShellPreferences);
		await store.SaveUsageDisplayModeAsync(UsageDisplayMode.Remaining);

		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await store.LoadDashboardShellPreferencesAsync());
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_PreservesUsageDisplayMode()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await store.SaveUsageDisplayModeAsync(UsageDisplayMode.Remaining);
		await store.SaveDashboardShellPreferencesAsync(
			DashboardShellPreferences.Default);

		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
	}

	[Fact]
	public async Task LegacySortOnlyDocument_LoadsSortAndDefaultShellPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"{\"schemaVersion\":1,\"usageSortMode\":\"Automatic\"}");
		JsonDashboardPreferencesStore store = new(filePath);

		UsageSortMode sortMode = await store.LoadUsageSortModeAsync();
		UsageDisplayMode displayMode = await store.LoadUsageDisplayModeAsync();
		DashboardShellPreferences shellPreferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(UsageSortMode.Automatic, sortMode);
		Assert.Equal(UsageDisplayMode.Used, displayMode);
		Assert.Equal(DashboardShellPreferences.Default, shellPreferences);
	}

	[Fact]
	public async Task VersionTwoDocument_LoadsShellAndDefaultsDisplayModeToUsed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 2,
			  "usageSortMode": "Automatic",
			  "isWidgetVisible": true,
			  "isCollapsed": true,
			  "isTopmost": false,
			  "monitorDeviceName": "\\\\.\\DISPLAY2",
			  "corner": "TopLeft",
			  "startupSurface": "DashboardAndWidget"
			}
			""");
		JsonDashboardPreferencesStore store = new(filePath);

		UsageDisplayMode displayMode = await store.LoadUsageDisplayModeAsync();
		DashboardShellPreferences shellPreferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(UsageDisplayMode.Used, displayMode);
		Assert.True(shellPreferences.IsWidgetVisible);
		Assert.True(shellPreferences.IsCollapsed);
		Assert.False(shellPreferences.IsTopmost);
		Assert.Equal(FloatingWidgetCorner.TopLeft, shellPreferences.Corner);
		Assert.Equal(
			DashboardStartupSurface.DashboardAndWidget,
			shellPreferences.StartupSurface);

		await store.SaveUsageDisplayModeAsync(UsageDisplayMode.Remaining);

		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		Assert.Equal(
			shellPreferences,
			await store.LoadDashboardShellPreferencesAsync());
		Assert.Contains(
			"\"schemaVersion\": 7",
			await File.ReadAllTextAsync(filePath),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task VersionThreeDocument_DefaultsThemeToClassicBlue()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 3,
			  "usageDisplayMode": "Remaining",
			  "usageSortMode": "Automatic",
			  "isWidgetVisible": true,
			  "isCollapsed": true,
			  "isTopmost": false,
			  "monitorDeviceName": "\\\\.\\DISPLAY2",
			  "corner": "TopLeft",
			  "startupSurface": "DashboardAndWidget"
			}
			""");
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences preferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(AppTheme.ClassicBlue, DashboardShellPreferences.Default.Theme);
		Assert.Equal(AppTheme.ClassicBlue, preferences.Theme);
		Assert.Equal(UsageDisplayMode.Remaining, await store.LoadUsageDisplayModeAsync());
	}

	[Fact]
	public async Task VersionFourDocument_PreservesThemeAndIgnoresRemovedGlobalSetting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 4,
			  "usageDisplayMode": "Remaining",
			  "theme": "Light",
			  "showClaudeOrganization": true
			}
			""");
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences preferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(AppTheme.Light, preferences.Theme);
		Assert.False(preferences.IsHeightFollowingCardCount);
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
	}

	[Fact]
	public async Task VersionFiveDocument_DefaultsHeightAndRemovesLegacyClaudeOrganizationSetting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 5,
			  "usageDisplayMode": "Remaining",
			  "theme": "Light",
			  "showClaudeOrganization": true
			}
			""");
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences preferences =
			await store.LoadDashboardShellPreferencesAsync();
		await store.SaveDashboardShellPreferencesAsync(preferences);

		Assert.Equal(AppTheme.Light, preferences.Theme);
		Assert.False(preferences.IsHeightFollowingCardCount);
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		string savedJson = await File.ReadAllTextAsync(filePath);
		Assert.Contains("\"schemaVersion\": 7", savedJson, StringComparison.Ordinal);
		Assert.Contains(
			"\"isHeightFollowingCardCount\": false",
			savedJson,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"showClaudeOrganization",
			savedJson,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task VersionSixDocument_PreservesCornerAndDefaultsCollapsedPosition()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 6,
			  "usageDisplayMode": "Remaining",
			  "monitorDeviceName": "\\\\.\\DISPLAY2",
			  "corner": "TopLeft",
			  "theme": "Light",
			  "isHeightFollowingCardCount": true
			}
			""");
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences loaded =
			await store.LoadDashboardShellPreferencesAsync();
		await store.SaveDashboardShellPreferencesAsync(loaded);

		Assert.Equal(FloatingWidgetCorner.TopLeft, loaded.Corner);
		Assert.Equal(@"\\.\DISPLAY2", loaded.MonitorDeviceName);
		Assert.Equal(AppTheme.Light, loaded.Theme);
		Assert.True(loaded.IsHeightFollowingCardCount);
		Assert.Null(loaded.CollapsedPositionXRatio);
		Assert.Null(loaded.CollapsedPositionYRatio);
		Assert.Equal(
			loaded,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		string savedJson = await File.ReadAllTextAsync(filePath);
		Assert.Contains("\"schemaVersion\": 7", savedJson, StringComparison.Ordinal);
		Assert.Contains(
			"\"collapsedPositionXRatio\": null",
			savedJson,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"collapsedPositionYRatio\": null",
			savedJson,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task LoadUsageDisplayModeAsync_WithUnknownMode_ReturnsUsed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string Document =
			"{\"schemaVersion\":3,\"usageDisplayMode\":\"Unknown\"}";
		await File.WriteAllTextAsync(filePath, Document);
		JsonDashboardPreferencesStore store = new(filePath);

		UsageDisplayMode displayMode = await store.LoadUsageDisplayModeAsync();

		Assert.Equal(UsageDisplayMode.Used, displayMode);
		Assert.Equal(Document, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveUsageDisplayModeAsync_WithUnknownMode_ThrowsWithoutWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => store.SaveUsageDisplayModeAsync(
				(UsageDisplayMode)int.MaxValue));

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveUsageDisplayModeAsync_WithNewerSchema_ThrowsWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string FutureDocument =
			"{\"schemaVersion\":8,\"usageDisplayMode\":\"Remaining\",\"futureField\":true}";
		await File.WriteAllTextAsync(filePath, FutureDocument);
		JsonDashboardPreferencesStore store = new(filePath);

		await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
			() => store.SaveUsageDisplayModeAsync(UsageDisplayMode.Used));

		Assert.Equal(FutureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Theory]
	[InlineData("not-json")]
	[InlineData("{\"schemaVersion\":2,\"corner\":\"Unknown\"}")]
	[InlineData("{\"schemaVersion\":2,\"monitorDeviceName\":null}")]
	[InlineData("{\"schemaVersion\":7,\"isCollapsed\":true,\"collapsedPositionXRatio\":0.5}")]
	[InlineData("{\"schemaVersion\":7,\"isCollapsed\":true,\"collapsedPositionXRatio\":null,\"collapsedPositionYRatio\":0.5}")]
	[InlineData("{\"schemaVersion\":7,\"isCollapsed\":true,\"collapsedPositionXRatio\":-0.01,\"collapsedPositionYRatio\":0.5}")]
	[InlineData("{\"schemaVersion\":7,\"isCollapsed\":true,\"collapsedPositionXRatio\":0.5,\"collapsedPositionYRatio\":1.01}")]
	[InlineData("{\"schemaVersion\":7,\"isCollapsed\":true,\"collapsedPositionXRatio\":\"0.5\",\"collapsedPositionYRatio\":0.5}")]
	[InlineData("{\"schemaVersion\":99,\"isWidgetVisible\":true}")]
	public async Task LoadDashboardShellPreferencesAsync_WithInvalidDocument_ReturnsDefaults(
		string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(filePath, json);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences preferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(DashboardShellPreferences.Default, preferences);
		Assert.Equal(json, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_WithUnknownTheme_ReturnsDefaults()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string Document = "{\"schemaVersion\":4,\"theme\":2147483647}";
		await File.WriteAllTextAsync(filePath, Document);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardShellPreferences preferences =
			await store.LoadDashboardShellPreferencesAsync();

		Assert.Equal(DashboardShellPreferences.Default, preferences);
		Assert.Equal(Document, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WithUnknownTheme_ThrowsWithoutWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences invalidPreferences =
			DashboardShellPreferences.Default with
			{
				Theme = (AppTheme)int.MaxValue
			};

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => store.SaveDashboardShellPreferencesAsync(invalidPreferences));

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WithInvalidCollapsedPosition_ThrowsWithoutWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		(double? X, double? Y)[] invalidPositions =
		[
			(null, 0.5),
			(0.5, null),
			(-0.01, 0.5),
			(0.5, 1.01),
			(double.NaN, 0.5),
			(0.5, double.PositiveInfinity)
		];

		foreach ((double? x, double? y) in invalidPositions)
		{
			DashboardShellPreferences invalid = DashboardShellPreferences.Default with
			{
				CollapsedPositionXRatio = x,
				CollapsedPositionYRatio = y
			};
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
				() => store.SaveDashboardShellPreferencesAsync(invalid));
			Assert.False(File.Exists(filePath));
		}
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_WhenFileLockIsReleased_StartupSnapshotPreservesStoredPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY9",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		JsonDashboardPreferencesStore initialStore = new(filePath);
		await initialStore.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			storedShellPreferences);
		byte[] expectedDocument = await File.ReadAllBytesAsync(filePath);
		JsonDashboardPreferencesStore store = new(filePath);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
		}

		DashboardShellPreferences startupSnapshot = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		await store.SaveDashboardShellPreferencesAsync(startupSnapshot);

		Assert.Equal(expectedDocument, await File.ReadAllBytesAsync(filePath));
		JsonDashboardPreferencesStore verificationStore = new(filePath);
		Assert.Equal(
			UsageSortMode.Automatic,
			await verificationStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await verificationStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			storedShellPreferences,
			await verificationStore.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_RecoverySession_MergesOnlyFieldsChangedAfterStartupSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY9",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(storedShellPreferences);
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences startupSnapshot = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences firstChangedSnapshot = startupSnapshot with
		{
			IsTopmost = false
		};
		DashboardShellPreferences secondChangedSnapshot =
			firstChangedSnapshot with
			{
				IsTopmost = true,
				Corner = FloatingWidgetCorner.TopRight
			};

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(startupSnapshot));
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(firstChangedSnapshot));
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(secondChangedSnapshot));
		}

		await store.SaveDashboardShellPreferencesAsync(secondChangedSnapshot);

		DashboardShellPreferences expectedAfterRecovery =
			storedShellPreferences with
			{
				IsTopmost = true,
				Corner = FloatingWidgetCorner.TopRight
			};
		Assert.Equal(
			expectedAfterRecovery,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());

		DashboardShellPreferences laterSnapshot = secondChangedSnapshot with
		{
			MonitorDeviceName = @"\\.\DISPLAY7"
		};
		await store.SaveDashboardShellPreferencesAsync(laterSnapshot);

		Assert.Equal(
			expectedAfterRecovery with
			{
				MonitorDeviceName = @"\\.\DISPLAY7"
			},
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_RecoverySession_TracksEveryShellField()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY9",
			Corner: FloatingWidgetCorner.BottomLeft,
			StartupSurface: DashboardStartupSurface.Dashboard,
			Theme: AppTheme.Light,
			IsHeightFollowingCardCount: false);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(storedShellPreferences);
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences startupSnapshot = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget,
			Theme: AppTheme.ClassicBlue,
			IsHeightFollowingCardCount: false);
		DashboardShellPreferences changedSnapshot = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.TopRight,
			StartupSurface: DashboardStartupSurface.Tray,
			Theme: AppTheme.Midnight,
			IsHeightFollowingCardCount: true,
			CollapsedPositionXRatio: 0.4,
			CollapsedPositionYRatio: 0.6);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(startupSnapshot));
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(changedSnapshot));
		}

		await store.SaveDashboardShellPreferencesAsync(changedSnapshot);

		Assert.Equal(
			changedSnapshot,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_RecoverySession_TracksThemeChange()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences storedPreferences =
			DashboardShellPreferences.Default with
			{
				IsTopmost = false,
				Theme = AppTheme.Light
			};
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(storedPreferences);
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences startupPreferences =
			DashboardShellPreferences.Default;
		DashboardShellPreferences changedPreferences = startupPreferences with
		{
			Theme = AppTheme.Midnight
		};

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(startupPreferences));
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(() =>
				store.SaveDashboardShellPreferencesAsync(changedPreferences));
		}

		await store.SaveDashboardShellPreferencesAsync(changedPreferences);

		Assert.Equal(
			storedPreferences with { Theme = AppTheme.Midnight },
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
	}

	[Fact]
	public async Task LoadDashboardShellPreferencesAsync_WhenFileLockIsReleased_AllowsPortableSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(DashboardShellPreferences.Default);
		JsonDashboardPreferencesStore store = new(filePath);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
		}

		DashboardShellPreferences expectedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY3",
			Corner: FloatingWidgetCorner.TopRight,
			StartupSurface: DashboardStartupSurface.Tray);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			expectedShellPreferences);

		JsonDashboardPreferencesStore verificationStore = new(filePath);
		Assert.Equal(
			UsageSortMode.Automatic,
			await verificationStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await verificationStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await verificationStore.LoadDashboardShellPreferencesAsync());

		DashboardShellPreferences postPortableShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY4",
			Corner: FloatingWidgetCorner.BottomLeft,
			StartupSurface: DashboardStartupSurface.Widget);
		await store.SaveDashboardShellPreferencesAsync(
			postPortableShellPreferences);
		Assert.Equal(
			postPortableShellPreferences,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LoadDashboardShellPreferencesAsync_WhenFileRemainsLocked_BlocksSaveWithoutOverwriting(
		bool savePortablePreferences)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences expected = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(expected);
		byte[] expectedDocument = await File.ReadAllBytesAsync(filePath);
		JsonDashboardPreferencesStore store = new(filePath);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
			Func<Task> save = savePortablePreferences
				? () => store.SavePortablePreferencesAsync(
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					DashboardShellPreferences.Default)
				: () => store.SaveDashboardShellPreferencesAsync(
					DashboardShellPreferences.Default);

			DashboardPreferencesSaveBlockedException exception =
				await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
					save);
			Assert.Equal(
				DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable,
				exception.Reason);
			Assert.IsType<IOException>(exception.InnerException);
		}

		Assert.Equal(expectedDocument, await File.ReadAllBytesAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UsagePreferenceLoadTransientFailure_RecoveryRestoresCompleteStoredSnapshot(
		bool failSortModeLoad)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY8",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			storedShellPreferences);
		Assert.Equal(
			storedShellPreferences,
			await store.LoadDashboardShellPreferencesAsync());

		UsageSortMode currentSortMode = UsageSortMode.Automatic;
		UsageDisplayMode currentDisplayMode = UsageDisplayMode.Remaining;

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			if (failSortModeLoad)
			{
				currentSortMode = await store.LoadUsageSortModeAsync();
				Assert.Equal(UsageSortMode.Manual, currentSortMode);
			}
			else
			{
				currentDisplayMode = await store.LoadUsageDisplayModeAsync();
				Assert.Equal(UsageDisplayMode.Used, currentDisplayMode);
			}
		}

		DashboardPreferencesSnapshot currentPreferences = new(
			currentSortMode,
			currentDisplayMode,
			storedShellPreferences);
		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(currentPreferences);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				currentPreferences);

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, prepareResult.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(
			new DashboardPreferencesSnapshot(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				storedShellPreferences),
			commitResult.Preferences);
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
	}

	[Fact]
	public async Task DashboardPreferencesRecovery_WhenPublishFailsOnce_RetriesChangedFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await new JsonDashboardPreferencesStore(filePath)
			.SavePortablePreferencesAsync(
				UsageSortMode.Manual,
				UsageDisplayMode.Used,
				DashboardShellPreferences.Default);
		int publishAttemptCount = 0;
		JsonDashboardPreferencesStore store = new(
			filePath,
			publishTemporaryFileAtomically: (source, destination) =>
			{
				publishAttemptCount++;

				if (publishAttemptCount == 1)
				{
					throw new IOException("Synthetic first recovery publish failure.");
				}

				File.Move(source, destination, overwrite: true);
			});
		DashboardPreferencesSnapshot fallbackPreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DashboardShellPreferences.Default);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				UsageSortMode.Manual,
				await store.LoadUsageSortModeAsync());
			DashboardPreferencesRecoveryPrepareResult pendingPrepare =
				await store.PrepareRecoveryAsync(fallbackPreferences);
			Assert.Equal(
				DashboardPreferencesRecoveryStatus.Pending,
				pendingPrepare.Status);
		}

		DashboardPreferencesSnapshot changedPreferences =
			fallbackPreferences with
			{
				UsageSortMode = UsageSortMode.Automatic
			};
		DashboardPreferencesRecoveryPrepareResult firstPrepare =
			await store.PrepareRecoveryAsync(changedPreferences);
		DashboardPreferencesRecoveryCommitResult firstCommit =
			await store.CommitRecoveryAsync(
				firstPrepare.Generation,
				changedPreferences);

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, firstPrepare.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Pending, firstCommit.Status);
		Assert.Equal(1, publishAttemptCount);

		DashboardPreferencesRecoveryPrepareResult retryPrepare =
			await store.PrepareRecoveryAsync(changedPreferences);
		DashboardPreferencesRecoveryCommitResult retryCommit =
			await store.CommitRecoveryAsync(
				retryPrepare.Generation,
				changedPreferences);

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, retryPrepare.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, retryCommit.Status);
		Assert.Equal(changedPreferences, retryCommit.Preferences);
		Assert.Equal(2, publishAttemptCount);
		Assert.True(await store.CompleteRecoveryAsync(retryCommit.Generation));
		Assert.Equal(
			UsageSortMode.Automatic,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadUsageSortModeAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WhenKnownFileChangesExternally_MergesOnlyRuntimeDelta()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences persistedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			persistedShellPreferences with { IsCollapsed = true };
		DashboardShellPreferences externalShellPreferences =
			persistedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY6",
				Corner = FloatingWidgetCorner.TopLeft,
				CollapsedPositionXRatio = 0.4,
				CollapsedPositionYRatio = 0.6
			};
		DashboardPreferencesSnapshot runtimePreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			runtimeShellPreferences);
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences);
		await new JsonDashboardPreferencesStore(filePath)
			.SavePortablePreferencesAsync(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				externalShellPreferences);

		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with { IsCollapsed = true };
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		JsonDashboardPreferencesStore writtenStore = new(filePath);
		Assert.Equal(
			UsageSortMode.Automatic,
			await writtenStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await writtenStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await writtenStore.LoadDashboardShellPreferencesAsync());

		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(runtimePreferences);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				runtimePreferences);

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, prepareResult.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(
			new DashboardPreferencesSnapshot(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				expectedShellPreferences),
			commitResult.Preferences);
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_MergesMonitorAndCollapsedPositionAsUnit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences original = DashboardShellPreferences.Default with
		{
			MonitorDeviceName = @"\\.\DISPLAY1",
			CollapsedPositionXRatio = 0.1,
			CollapsedPositionYRatio = 0.2
		};
		DashboardShellPreferences runtime = original with
		{
			CollapsedPositionYRatio = 0.8
		};
		DashboardShellPreferences external = original with
		{
			IsTopmost = false,
			MonitorDeviceName = @"\\.\DISPLAY9",
			CollapsedPositionXRatio = 0.9
		};
		DashboardShellPreferences expected = external with
		{
			MonitorDeviceName = runtime.MonitorDeviceName,
			CollapsedPositionXRatio = runtime.CollapsedPositionXRatio,
			CollapsedPositionYRatio = runtime.CollapsedPositionYRatio
		};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SaveDashboardShellPreferencesAsync(original);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(external);

		await store.SaveDashboardShellPreferencesAsync(runtime);

		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		Assert.Equal(
			expected,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());

		DashboardPreferencesSnapshot runtimeSnapshot = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			runtime);
		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(runtimeSnapshot);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				runtimeSnapshot);

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(expected, commitResult.Preferences?.ShellPreferences);
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WhenMonitorChanges_PreservesRuntimePositionPair()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences original = DashboardShellPreferences.Default with
		{
			MonitorDeviceName = @"\\.\DISPLAY1",
			CollapsedPositionXRatio = 0.1,
			CollapsedPositionYRatio = 0.2
		};
		DashboardShellPreferences runtime = original with
		{
			MonitorDeviceName = @"\\.\DISPLAY2"
		};
		DashboardShellPreferences external = original with
		{
			IsTopmost = false,
			CollapsedPositionXRatio = 0.8,
			CollapsedPositionYRatio = 0.9
		};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SaveDashboardShellPreferencesAsync(original);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(external);

		await store.SaveDashboardShellPreferencesAsync(runtime);

		Assert.Equal(
			runtime with { IsTopmost = false },
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_MergesRuntimeFieldsWithExternalShellChanges()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences persistedPreferences =
			DashboardShellPreferences.Default;
		DashboardShellPreferences runtimePreferences = persistedPreferences with
		{
			Theme = AppTheme.Midnight,
			IsHeightFollowingCardCount = true
		};
		DashboardShellPreferences externalPreferences = persistedPreferences with
		{
			IsTopmost = false,
			Theme = AppTheme.Light
		};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SaveDashboardShellPreferencesAsync(persistedPreferences);
		await new JsonDashboardPreferencesStore(filePath)
			.SaveDashboardShellPreferencesAsync(externalPreferences);

		await store.SaveDashboardShellPreferencesAsync(runtimePreferences);

		Assert.Equal(
			externalPreferences with
			{
				Theme = AppTheme.Midnight,
				IsHeightFollowingCardCount = true
			},
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
	}

	[Theory]
	[InlineData(UsageLoadOperation.DisplayMode)]
	[InlineData(UsageLoadOperation.SortMode)]
	public async Task UsageLoad_WhenKnownFileChangesExternally_PreservesExternalShell(
		UsageLoadOperation operation)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences persistedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			persistedShellPreferences with { IsCollapsed = true };
		DashboardShellPreferences externalShellPreferences =
			persistedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY4",
				Corner = FloatingWidgetCorner.TopLeft,
				StartupSurface = DashboardStartupSurface.Tray
			};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences);
		int recoveryRequestCount = 0;
		store.RecoveryRequested += () => recoveryRequestCount++;
		await new JsonDashboardPreferencesStore(filePath)
			.SavePortablePreferencesAsync(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				externalShellPreferences);

		switch (operation)
		{
			case UsageLoadOperation.DisplayMode:
				Assert.Equal(
					UsageDisplayMode.Remaining,
					await store.LoadUsageDisplayModeAsync());
				break;
			case UsageLoadOperation.SortMode:
				Assert.Equal(
					UsageSortMode.Automatic,
					await store.LoadUsageSortModeAsync());
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation));
		}

		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with { IsCollapsed = true };
		JsonDashboardPreferencesStore reloadedStore = new(filePath);
		Assert.Equal(1, recoveryRequestCount);
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		Assert.Equal(
			UsageSortMode.Automatic,
			await reloadedStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await reloadedStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await reloadedStore.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Theory]
	[InlineData(UsageSaveOperation.DisplayMode)]
	[InlineData(UsageSaveOperation.SortMode)]
	[InlineData(UsageSaveOperation.BothModes)]
	public async Task UsageSave_WhenKnownFileChangesExternally_PreservesExternalShell(
		UsageSaveOperation operation)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		DashboardShellPreferences persistedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			persistedShellPreferences with { IsCollapsed = true };
		DashboardShellPreferences externalShellPreferences =
			persistedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY5",
				Corner = FloatingWidgetCorner.TopRight,
				StartupSurface = DashboardStartupSurface.Tray
			};
		JsonDashboardPreferencesStore store = new(filePath);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences);
		int recoveryRequestCount = 0;
		store.RecoveryRequested += () => recoveryRequestCount++;
		await new JsonDashboardPreferencesStore(filePath)
			.SavePortablePreferencesAsync(
				UsageSortMode.Manual,
				UsageDisplayMode.Used,
				externalShellPreferences);

		UsageSortMode expectedSortMode = UsageSortMode.Manual;
		UsageDisplayMode expectedDisplayMode = UsageDisplayMode.Used;

		switch (operation)
		{
			case UsageSaveOperation.DisplayMode:
				expectedDisplayMode = UsageDisplayMode.Remaining;
				await store.SaveUsageDisplayModeAsync(expectedDisplayMode);
				break;
			case UsageSaveOperation.SortMode:
				expectedSortMode = UsageSortMode.Automatic;
				await store.SaveUsageSortModeAsync(expectedSortMode);
				break;
			case UsageSaveOperation.BothModes:
				expectedSortMode = UsageSortMode.Automatic;
				expectedDisplayMode = UsageDisplayMode.Remaining;
				await store.SaveUsagePreferencesAsync(
					expectedSortMode,
					expectedDisplayMode);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation));
		}

		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with { IsCollapsed = true };
		JsonDashboardPreferencesStore reloadedStore = new(filePath);
		Assert.Equal(1, recoveryRequestCount);
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		Assert.Equal(
			expectedSortMode,
			await reloadedStore.LoadUsageSortModeAsync());
		Assert.Equal(
			expectedDisplayMode,
			await reloadedStore.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await reloadedStore.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task PortableImportRollback_WithUnverifiableBaseline_ForcesRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences persistedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardPreferencesSnapshot runtimePreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences with { IsCollapsed = true });
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences);
		await store.BeginPortableImportTransactionAsync(
			runtimePreferences,
			CancellationToken.None);
		PortableSettingsImportRestoreResult restoreResult = new(
			[
				KeyValuePair.Create(
					filePath,
					new PortableSettingsImportTargetRestoreResult(
						RollbackBaselineState: null,
						DoesFinalTargetMatchRollbackBaseline: false))
			]);

		bool isRecoveryActive =
			await store.SynchronizeAfterPortableImportRestoreAsync(
				restoreResult,
				CancellationToken.None);
		store.CompletePortableImportTransaction();
		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(runtimePreferences);

		Assert.True(isRecoveryActive);
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		Assert.Equal(
			DashboardPreferencesRecoveryStatus.Ready,
			prepareResult.Status);
	}

	[Fact]
	public async Task PortableImportRollback_RestoresRecoveryStateBeforeLaterShellSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonDashboardPreferencesStore store = new(
			filePath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY9",
			Corner: FloatingWidgetCorner.TopLeft,
			StartupSurface: DashboardStartupSurface.Tray);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			storedShellPreferences);
		byte[] originalDocument = await File.ReadAllBytesAsync(filePath);
		DashboardPreferencesSnapshot fallbackPreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DashboardShellPreferences.Default);

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			Assert.Equal(
				DashboardShellPreferences.Default,
				await store.LoadDashboardShellPreferencesAsync());
			DashboardPreferencesRecoveryPrepareResult pending =
				await store.PrepareRecoveryAsync(fallbackPreferences);
			Assert.Equal(DashboardPreferencesRecoveryStatus.Pending, pending.Status);
		}

		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[filePath],
			persistenceGate: persistenceGate,
			onTargetsRestoredWithResult:
				store.SynchronizeAfterPortableImportRestoreAsync,
			onOperationStarting: cancellationToken =>
				store.BeginPortableImportTransactionAsync(
					fallbackPreferences,
					cancellationToken),
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<IOException>(() =>
			transaction.ExecuteAsync(async cancellationToken =>
			{
				await store.SavePortablePreferencesAsync(
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DashboardShellPreferences.Default,
					cancellationToken);
				throw new IOException("Synthetic post-preference failure.");
			}));
		await store.SaveDashboardShellPreferencesAsync(
			DashboardShellPreferences.Default);

		Assert.Equal(originalDocument, await File.ReadAllBytesAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task PortableImportRollback_ExternalWriteAfterRestore_MergesRuntimeDelta()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonDashboardPreferencesStore store = new(
			filePath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		DashboardShellPreferences storedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			storedShellPreferences with
			{
				IsCollapsed = true,
				Corner = FloatingWidgetCorner.TopLeft
			};
		DashboardShellPreferences externalShellPreferences =
			storedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY5",
				Corner = FloatingWidgetCorner.BottomLeft,
				StartupSurface = DashboardStartupSurface.Tray
			};
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			storedShellPreferences);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[filePath],
			persistenceGate: persistenceGate,
			onTargetsRestoredWithResult:
				store.SynchronizeAfterPortableImportRestoreAsync,
			onOperationStarting: cancellationToken =>
				store.BeginPortableImportTransactionAsync(
					new DashboardPreferencesSnapshot(
						UsageSortMode.Automatic,
						UsageDisplayMode.Remaining,
						runtimeShellPreferences),
					cancellationToken),
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<IOException>(() =>
			transaction.ExecuteAsync(async cancellationToken =>
			{
				await store.SavePortablePreferencesAsync(
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DashboardShellPreferences.Default,
					cancellationToken);
				throw new IOException("Synthetic post-preference failure.");
			}));
		await new JsonDashboardPreferencesStore(filePath)
			.SavePortablePreferencesAsync(
				UsageSortMode.Manual,
				UsageDisplayMode.Used,
				externalShellPreferences);
		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with
			{
				IsCollapsed = true,
				Corner = FloatingWidgetCorner.TopLeft
			};
		JsonDashboardPreferencesStore reloadedStore = new(filePath);
		Assert.Equal(
			expectedShellPreferences,
			await reloadedStore.LoadDashboardShellPreferencesAsync());
		Assert.Equal(
			UsageSortMode.Manual,
			await reloadedStore.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Used,
			await reloadedStore.LoadUsageDisplayModeAsync());
		Assert.True(((IDashboardPreferencesRecoveryStore)store).IsRecoveryActive);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task PortableImportRollback_ExternalPreferencesPreserved_MergesRuntimeDelta()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonDashboardPreferencesStore store = new(
			filePath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		DashboardShellPreferences persistedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			persistedShellPreferences with { IsCollapsed = true };
		DashboardShellPreferences externalShellPreferences =
			persistedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY8",
				Corner = FloatingWidgetCorner.TopLeft
			};
		DashboardPreferencesSnapshot runtimePreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			runtimeShellPreferences);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			persistedShellPreferences);
		JsonDashboardPreferencesStore externalWriter = new(filePath);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[filePath],
			persistenceGate: persistenceGate,
			onTargetsRestoredWithResult:
				store.SynchronizeAfterPortableImportRestoreAsync,
			onOperationStarting: cancellationToken =>
				store.BeginPortableImportTransactionAsync(
					runtimePreferences,
					cancellationToken),
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<IOException>(() =>
			transaction.ExecuteAsync(async cancellationToken =>
			{
				await store.SavePortablePreferencesAsync(
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DashboardShellPreferences.Default,
					cancellationToken);
				await externalWriter.SavePortablePreferencesAsync(
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					externalShellPreferences,
					cancellationToken);
				throw new IOException("Synthetic post-external-write failure.");
			}));
		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(runtimePreferences);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				runtimePreferences);
		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with { IsCollapsed = true };

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, prepareResult.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(
			new DashboardPreferencesSnapshot(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				expectedShellPreferences),
			commitResult.Preferences);
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
		Assert.Equal(
			expectedShellPreferences,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task PortableImportRollback_OperationBaselineChangedAfterCheckpoint_ForcesRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonDashboardPreferencesStore store = new(
			filePath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		DashboardShellPreferences loadedShellPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY1",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);
		DashboardShellPreferences runtimeShellPreferences =
			loadedShellPreferences with { IsCollapsed = true };
		DashboardShellPreferences externalShellPreferences =
			loadedShellPreferences with
			{
				IsTopmost = false,
				MonitorDeviceName = @"\\.\DISPLAY7",
				Corner = FloatingWidgetCorner.TopRight
			};
		DashboardPreferencesSnapshot runtimePreferences = new(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			runtimeShellPreferences);
		await store.SavePortablePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			loadedShellPreferences);
		JsonDashboardPreferencesStore externalWriter = new(filePath);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[filePath],
			persistenceGate: persistenceGate,
			onTargetsRestoredWithResult:
				store.SynchronizeAfterPortableImportRestoreAsync,
			onOperationStarting: async cancellationToken =>
			{
				await store.BeginPortableImportTransactionAsync(
					runtimePreferences,
					cancellationToken);
				await externalWriter.SavePortablePreferencesAsync(
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					externalShellPreferences,
					cancellationToken);
			},
			onTransactionFinished: store.CompletePortableImportTransaction,
			writeTracker: writeTracker);

		await Assert.ThrowsAsync<IOException>(() =>
			transaction.ExecuteAsync(async cancellationToken =>
			{
				await store.SavePortablePreferencesAsync(
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DashboardShellPreferences.Default,
					cancellationToken);
				throw new IOException("Synthetic import failure.");
			}));
		await store.SaveDashboardShellPreferencesAsync(
			runtimeShellPreferences);

		DashboardPreferencesRecoveryPrepareResult prepareResult =
			await store.PrepareRecoveryAsync(runtimePreferences);
		DashboardPreferencesRecoveryCommitResult commitResult =
			await store.CommitRecoveryAsync(
				prepareResult.Generation,
				runtimePreferences);
		DashboardShellPreferences expectedShellPreferences =
			externalShellPreferences with { IsCollapsed = true };

		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, prepareResult.Status);
		Assert.Equal(DashboardPreferencesRecoveryStatus.Ready, commitResult.Status);
		Assert.Equal(
			new DashboardPreferencesSnapshot(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				expectedShellPreferences),
			commitResult.Preferences);
		Assert.True(await store.CompleteRecoveryAsync(commitResult.Generation));
		Assert.Equal(
			expectedShellPreferences,
			await new JsonDashboardPreferencesStore(filePath)
				.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SavePortablePreferencesAsync_SavesUsageAndShellPreferencesTogether()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		JsonDashboardPreferencesStore store = new(filePath);
		DashboardShellPreferences expectedShellPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			MonitorDeviceName: @"\\.\DISPLAY3",
			Corner: FloatingWidgetCorner.TopRight,
			StartupSurface: DashboardStartupSurface.Tray);

		await store.SavePortablePreferencesAsync(
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			expectedShellPreferences);

		Assert.Equal(
			UsageSortMode.Automatic,
			await store.LoadUsageSortModeAsync());
		Assert.Equal(
			UsageDisplayMode.Remaining,
			await store.LoadUsageDisplayModeAsync());
		Assert.Equal(
			expectedShellPreferences,
			await store.LoadDashboardShellPreferencesAsync());
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WithOversizedDocument_ThrowsTypedReasonWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		string document =
			"{\"schemaVersion\":4,\"isWidgetVisible\":true}" +
			new string(' ', (64 * 1024) + 1);
		await File.WriteAllTextAsync(filePath, document);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardPreferencesSaveBlockedException exception =
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
				() => store.SaveDashboardShellPreferencesAsync(
					DashboardShellPreferences.Default));

		Assert.Equal(
			DashboardPreferencesSaveBlockReason.DocumentTooLarge,
			exception.Reason);
		Assert.Equal(document, await File.ReadAllTextAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WithNewerSchema_ThrowsWithoutOverwriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string FutureDocument =
			"{\"schemaVersion\":8,\"usageSortMode\":\"Automatic\",\"futureField\":true}";
		await File.WriteAllTextAsync(filePath, FutureDocument);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardPreferencesSaveBlockedException exception =
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
				() => store.SaveDashboardShellPreferencesAsync(
					DashboardShellPreferences.Default));

		Assert.Equal(
			DashboardPreferencesSaveBlockReason.NewerSchema,
			exception.Reason);
		Assert.Equal(FutureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveUsagePreferencesAsync_WhenDurableFlushFails_PreservesOriginalDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string OriginalDocument =
			"{\"schemaVersion\":3,\"usageSortMode\":\"Manual\"}";
		await File.WriteAllTextAsync(filePath, OriginalDocument);
		bool flushWasAttempted = false;
		JsonDashboardPreferencesStore store = new(
			filePath,
			flushTemporaryFileToDisk: stream =>
			{
				flushWasAttempted = true;
				Assert.True(stream.CanWrite);
				throw new IOException("Synthetic durable flush failure.");
			});

		await Assert.ThrowsAsync<IOException>(
			() => store.SaveUsagePreferencesAsync(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		Assert.True(flushWasAttempted);
		Assert.Equal(OriginalDocument, await File.ReadAllTextAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveUsagePreferencesAsync_WhenAtomicPublishFails_PreservesOriginalDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "preferences.json");
		const string OriginalDocument =
			"{\"schemaVersion\":3,\"usageSortMode\":\"Manual\"}";
		await File.WriteAllTextAsync(filePath, OriginalDocument);
		bool publishWasAttempted = false;
		JsonDashboardPreferencesStore store = new(
			filePath,
			publishTemporaryFileAtomically: (source, destination) =>
			{
				publishWasAttempted = true;
				Assert.True(File.Exists(source));
				Assert.Equal(filePath, destination);
				throw new IOException("Synthetic atomic publish failure.");
			});

		await Assert.ThrowsAsync<IOException>(
			() => store.SaveUsagePreferencesAsync(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		Assert.True(publishWasAttempted);
		Assert.Equal(OriginalDocument, await File.ReadAllTextAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task Operations_WithJunctionAncestor_FailClosedWithoutTouchingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"target");
		string junctionDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences-junction");
		string targetFilePath = Path.Combine(
			targetDirectoryPath,
			"preferences.json");
		Directory.CreateDirectory(targetDirectoryPath);
		JsonDashboardPreferencesStore targetStore = new(targetFilePath);
		await targetStore.SaveUsagePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		byte[] expectedDocument = await File.ReadAllBytesAsync(targetFilePath);
		await JunctionTestHelper.CreateAsync(
			junctionDirectoryPath,
			targetDirectoryPath);

		try
		{
			JsonDashboardPreferencesStore aliasedStore = new(Path.Combine(
				junctionDirectoryPath,
				"preferences.json"));

			Assert.Equal(
				UsageDisplayMode.Used,
				await aliasedStore.LoadUsageDisplayModeAsync());
			DashboardPreferencesSaveBlockedException exception =
				await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
					() => aliasedStore.SaveUsageDisplayModeAsync(
						UsageDisplayMode.Remaining));
			Assert.Equal(
				DashboardPreferencesSaveBlockReason.UnsafePath,
				exception.Reason);
			Assert.Equal(
				expectedDocument,
				await File.ReadAllBytesAsync(targetFilePath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectoryPath);
		}
	}

	[Fact]
	public async Task SaveDashboardShellPreferencesAsync_WithDirectoryAtFilePath_ThrowsUnsafePathReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		Directory.CreateDirectory(filePath);
		JsonDashboardPreferencesStore store = new(filePath);

		DashboardPreferencesSaveBlockedException exception =
			await Assert.ThrowsAsync<DashboardPreferencesSaveBlockedException>(
				() => store.SaveDashboardShellPreferencesAsync(
					DashboardShellPreferences.Default));

		Assert.Equal(
			DashboardPreferencesSaveBlockReason.UnsafePath,
			exception.Reason);
		Assert.True(Directory.Exists(filePath));
	}
}
