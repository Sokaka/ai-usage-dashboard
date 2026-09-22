using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Updates;

namespace AiUsageDashboard.Tests;

public sealed class JsonUpdateCheckStateStoreTests
{
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		4,
		0,
		0,
		TimeSpan.Zero);

	[Fact]
	public async Task TrySaveAndLoadAsync_RoundTripsSchedulingAndPresentationState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		JsonUpdateCheckStateStore store = new(filePath);
		UpdateCheckPersistentState expected = CreateState();

		bool wasSaved = await store.TrySaveAsync(expected);
		UpdateCheckPersistentState? actual = await store.LoadAsync();

		Assert.True(wasSaved);
		Assert.Equal(expected, actual);
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllBytesAsync(filePath));
		Assert.Equal(
			new[]
			{
				"consecutiveFailureCount",
				"hasShownAutomaticCheckNotice",
				"highestObservedReleaseSequence",
				"isAutoCheckEnabled",
				"lastAttemptUtc",
				"lastBalloonAttemptKey",
				"lastKnownResult",
				"lastSuccessfulCheckUtc",
				"schemaVersion",
				"snooze"
			},
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());
		Assert.DoesNotContain(
			"downloadUrl",
			await File.ReadAllTextAsync(filePath),
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task TrySaveAndLoadAsync_RoundTripsUnknownCurrentVersionResult()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		JsonUpdateCheckStateStore store = new(filePath);
		UpdateCheckPersistentState expected =
			UpdateCheckPersistentState.Default with
			{
				LastAttemptUtc = TestNow,
				LastSuccessfulCheckUtc = TestNow,
				HighestObservedReleaseSequence = 1018,
				LastKnownResult = new UpdateKnownResult(
					CheckedAgainstVersion: null,
					AvailableVersion: "1.0.4",
					ReleaseSequence: 1018,
					IsUpdateAvailable: false)
			};

		Assert.True(await store.TrySaveAsync(expected));

		Assert.Equal(expected, await store.LoadAsync());
	}

	[Fact]
	public async Task LoadAsync_WhenDocumentIsMalformedOrOversized_ReturnsCacheMiss()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		List<Exception?> diagnostics = new();
		JsonUpdateCheckStateStore store = CreateStore(
			filePath,
			diagnostics);
		await File.WriteAllTextAsync(filePath, "{");

		Assert.Null(await store.LoadAsync());
		Assert.Single(diagnostics);

		diagnostics.Clear();
		await File.WriteAllBytesAsync(filePath, new byte[(64 * 1024) + 1]);

		Assert.Null(await store.LoadAsync());
		Assert.Single(diagnostics);
	}

	[Fact]
	public async Task LoadAsync_WhenSchemaOrShapeIsUnknown_ReturnsCacheMiss()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		List<Exception?> diagnostics = new();
		JsonUpdateCheckStateStore store = CreateStore(
			filePath,
			diagnostics);
		Assert.True(await store.TrySaveAsync(CreateState()));
		JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(
			await File.ReadAllTextAsync(filePath)));
		root["schemaVersion"] = 2;
		await File.WriteAllTextAsync(filePath, root.ToJsonString());

		Assert.Null(await store.LoadAsync());
		Assert.Single(diagnostics);

		diagnostics.Clear();
		root["schemaVersion"] = 1;
		root["unexpected"] = true;
		await File.WriteAllTextAsync(filePath, root.ToJsonString());

		Assert.Null(await store.LoadAsync());
		Assert.Single(diagnostics);
	}

	[Fact]
	public async Task TrySaveAsync_WhenAtomicPublishFails_PreservesExistingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		JsonUpdateCheckStateStore initialStore = new(filePath);
		UpdateCheckPersistentState initialState = CreateState();
		Assert.True(await initialStore.TrySaveAsync(initialState));
		List<Exception?> diagnostics = new();
		JsonUpdateCheckStateStore failingStore = new(
			filePath,
			stream => stream.Flush(flushToDisk: true),
			(_, _) => throw new IOException("synthetic replace failure"),
			(_, exception) => diagnostics.Add(exception));
		UpdateCheckPersistentState replacementState = initialState with
		{
			IsAutoCheckEnabled = true,
			HasShownAutomaticCheckNotice = false
		};

		bool wasSaved = await failingStore.TrySaveAsync(replacementState);
		UpdateCheckPersistentState? reloadedState =
			await initialStore.LoadAsync();

		Assert.False(wasSaved);
		Assert.Equal(initialState, reloadedState);
		Assert.Single(diagnostics);
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp"));
	}

	[Fact]
	public async Task TrySaveAsync_WhenWriteIsDenied_ReturnsFalseAndCleansTempFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "state.json");
		List<Exception?> diagnostics = new();
		JsonUpdateCheckStateStore store = new(
			filePath,
			_ => throw new UnauthorizedAccessException("synthetic denial"),
			(_, _) => throw new InvalidOperationException(
				"Publish must not run after a flush failure."),
			(_, exception) => diagnostics.Add(exception));

		bool wasSaved = await store.TrySaveAsync(CreateState());

		Assert.False(wasSaved);
		Assert.False(File.Exists(filePath));
		Assert.Single(diagnostics);
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp"));
	}

	[Fact]
	public void GetUpdateCheckStateFilePath_ReturnsApplicationScopedPath()
	{
		string filePath = AppDataPaths.GetUpdateCheckStateFilePath();

		Assert.True(Path.IsPathFullyQualified(filePath));
		Assert.Equal("update-check-state-v1.json", Path.GetFileName(filePath));
		Assert.Equal("AiUsageDashboard", Directory.GetParent(filePath)?.Name);
	}

	private static JsonUpdateCheckStateStore CreateStore(
		string filePath,
		List<Exception?> diagnostics)
	{
		return new JsonUpdateCheckStateStore(
			filePath,
			stream => stream.Flush(flushToDisk: true),
			(temporaryPath, destinationPath) =>
			{
				if (File.Exists(destinationPath))
				{
					File.Replace(
						temporaryPath,
						destinationPath,
						destinationBackupFileName: null);
				}
				else
				{
					File.Move(temporaryPath, destinationPath);
				}
			},
			(_, exception) => diagnostics.Add(exception));
	}

	private static UpdateCheckPersistentState CreateState()
	{
		UpdateKnownResult result = new(
			"1.0.3",
			"1.0.4",
			ReleaseSequence: 1018,
			IsUpdateAvailable: true);
		return new UpdateCheckPersistentState(
			IsAutoCheckEnabled: false,
			HasShownAutomaticCheckNotice: true,
			LastAttemptUtc: TestNow,
			LastSuccessfulCheckUtc: TestNow,
			ConsecutiveFailureCount: 0,
			HighestObservedReleaseSequence: 1018,
			LastKnownResult: result,
			LastBalloonAttemptKey: result.NotificationKey.ToString(),
			Snooze: new UpdateSnoozeState(
				"1.0.4",
				1018,
				TestNow + TimeSpan.FromHours(2)));
	}
}
