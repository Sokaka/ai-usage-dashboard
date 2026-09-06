using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class JsonAntigravityOfficialPrintSafetyStateStoreTests
{
	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsExactReasonAndUtcTimestamp()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState expected = new(
			AntigravityUsageSafetyFailureReason.TimedOut,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "4f6266fdd8fb4e66ae0f8973b03b9c42");

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? actual = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.NotNull(actual);
		Assert.Equal(expected.Reason, actual.Reason);
		Assert.Equal(expected.LatchedAtUtc, actual.LatchedAtUtc);
		Assert.Equal(
			expected.AutomaticRevalidationAllowed,
			actual.AutomaticRevalidationAllowed);
		Assert.Equal(
			expected.HasAttemptedAutomaticRevalidation,
			actual.HasAttemptedAutomaticRevalidation);
		Assert.Equal(expected.AttemptId, actual.AttemptId);
		Assert.Equal(TimeSpan.Zero, actual.LatchedAtUtc.Offset);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithSafelyQuiescedCancellation_PreservesRecoveryProvenance()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState expected = new(
			AntigravityUsageSafetyFailureReason.CanceledAfterStart,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "5f6266fdd8fb4e66ae0f8973b03b9c43");

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? actual =
			await store.LoadAsync(CancellationToken.None);

		Assert.NotNull(actual);
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithQuiescedNonZeroExit_PreservesVersionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState expected = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "6f6266fdd8fb4e66ae0f8973b03b9c44",
			DetectedCliVersion: "1.1.12");

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? actual =
			await store.LoadAsync(CancellationToken.None);
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));

		Assert.NotNull(actual);
		Assert.Equal(expected, actual);
		Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			"1.1.12",
			document.RootElement.GetProperty("detectedCliVersion").GetString());
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithCanonicalReferenceMetadata_PreservesForReassessment()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState expected = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "6f6266fdd8fb4e66ae0f8973b03b9c44",
			DetectedCliVersion: "1.1.11");

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? actual =
			await store.LoadAsync(CancellationToken.None);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithPreparedInterruptedAttempt_PreservesAutomaticRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState expected = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46");

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? actual = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));

		Assert.Equal(expected, actual);
		Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
	}

	[Fact]
	public async Task LoadAsync_WithLegacyTimedOutMarker_RemainsManualOnly()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		const string legacyJson =
			"{\"schemaVersion\":1,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\"}";
		await WriteDocumentAsync(filePath, legacyJson);

		AntigravityOfficialPrintSafetyState? loaded = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.NotNull(loaded);
		Assert.Equal(AntigravityUsageSafetyFailureReason.TimedOut, loaded.Reason);
		Assert.False(loaded.AutomaticRevalidationAllowed);
		Assert.False(loaded.HasAttemptedAutomaticRevalidation);
	}

	[Fact]
	public async Task LoadAsync_WithLegacyNonTimeoutMarker_DoesNotArmAutomaticRevalidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		const string legacyJson =
			"{\"schemaVersion\":1,\"reason\":\"AttemptInterrupted\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\"}";
		await WriteDocumentAsync(filePath, legacyJson);

		AntigravityOfficialPrintSafetyState? loaded = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.NotNull(loaded);
		Assert.False(loaded.AutomaticRevalidationAllowed);
		Assert.False(loaded.HasAttemptedAutomaticRevalidation);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaV2RecoverableTimeout_PreservesAutomaticEligibility()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		const string schemaV2Json =
			"{\"schemaVersion\":2,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false}";
		await WriteDocumentAsync(filePath, schemaV2Json);

		AntigravityOfficialPrintSafetyState? loaded = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.NotNull(loaded);
		Assert.Equal(
			AntigravityUsageSafetyFailureReason.TimedOut,
			loaded.Reason);
		Assert.True(loaded.AutomaticRevalidationAllowed);
		Assert.False(loaded.HasAttemptedAutomaticRevalidation);
		Assert.Null(loaded.AttemptId);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaV3RecoverableFailure_PreservesStateWithoutVersionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		const string schemaV3Json =
			"{\"schemaVersion\":3,\"reason\":\"NonZeroExit\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\"}";
		await WriteDocumentAsync(filePath, schemaV3Json);

		AntigravityOfficialPrintSafetyState? loaded = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.NotNull(loaded);
		Assert.Equal(AntigravityUsageSafetyFailureReason.NonZeroExit, loaded.Reason);
		Assert.True(loaded.AutomaticRevalidationAllowed);
		Assert.Equal(
			"6f6266fdd8fb4e66ae0f8973b03b9c44",
			loaded.AttemptId);
		Assert.Null(loaded.DetectedCliVersion);
	}

	[Fact]
	public async Task ClearAsync_AfterSavedState_RemovesFileAndLoadReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState state = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			DateTimeOffset.UtcNow);

		await store.SaveAsync(state, CancellationToken.None);
		await store.ClearAsync(CancellationToken.None);
		AntigravityOfficialPrintSafetyState? loaded =
			await store.LoadAsync(CancellationToken.None);

		Assert.False(File.Exists(filePath));
		Assert.Null(loaded);
	}

	[Fact]
	public async Task SaveAsync_WhenPrimaryPublishIsBlocked_NewStoreUsesDurableJournalTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		string journalFilePath = filePath + ".journal";
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState startedState = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: false,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46");
		AntigravityOfficialPrintSafetyState recoverableState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			new DateTimeOffset(2026, 8, 13, 9, 43, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false,
			AttemptId: "9f6266fdd8fb4e66ae0f8973b03b9c47");
		await store.SaveAsync(startedState, CancellationToken.None);

		await using (FileStream primaryLock = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await store.SaveAsync(recoverableState, CancellationToken.None);

			Assert.True(File.Exists(journalFilePath));
			AntigravityOfficialPrintSafetyState? restartedState = await new
				JsonAntigravityOfficialPrintSafetyStateStore(filePath)
				.LoadAsync(CancellationToken.None);
			Assert.Equal(recoverableState, restartedState);
			Assert.True(File.Exists(journalFilePath));
		}

		AntigravityOfficialPrintSafetyState? reconciledState = await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None);

		Assert.Equal(recoverableState, reconciledState);
	}

	[Fact]
	public async Task ClearAsync_WhenPrimaryDeleteIsBlocked_NewStoreUsesDurableClearJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		string journalFilePath = filePath + ".journal";
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		await store.SaveAsync(
			new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
				AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46"),
			CancellationToken.None);

		await using (FileStream primaryLock = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await store.ClearAsync(CancellationToken.None);

			Assert.True(File.Exists(filePath));
			Assert.True(File.Exists(journalFilePath));
			Assert.Null(await new
				JsonAntigravityOfficialPrintSafetyStateStore(filePath)
				.LoadAsync(CancellationToken.None));
			Assert.True(File.Exists(journalFilePath));
		}

		Assert.Null(await new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None));
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists(journalFilePath));
	}

	[Fact]
	public async Task LoadAsync_WithInvalidJournal_FailsClosedBeforeReadingPrimaryState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		await store.SaveAsync(
			new AntigravityOfficialPrintSafetyState(
				AntigravityUsageSafetyFailureReason.AttemptInterrupted,
				new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
				AttemptId: "8f6266fdd8fb4e66ae0f8973b03b9c46"),
			CancellationToken.None);
		const string invalidJournal =
			"{\"schemaVersion\":1,\"operation\":\"Clear\",\"unexpected\":true}";
		await WriteDocumentAsync(filePath + ".journal", invalidJournal);

		await Assert.ThrowsAsync<InvalidDataException>(() => new
			JsonAntigravityOfficialPrintSafetyStateStore(filePath)
			.LoadAsync(CancellationToken.None));

		Assert.True(File.Exists(filePath));
		Assert.Equal(
			invalidJournal,
			await File.ReadAllTextAsync(filePath + ".journal"));
	}

	[Fact]
	public async Task LoadAsync_WhenJsonIsMalformed_ThrowsAndPreservesMarker()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		const string malformedJson = "{\"schemaVersion\":1,";
		await WriteDocumentAsync(filePath, malformedJson);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.LoadAsync(CancellationToken.None));

		Assert.Equal(malformedJson, await File.ReadAllTextAsync(filePath));
	}

	[Theory]
	[InlineData(
		"{\"schemaVersion\":1,\"reason\":\"AttemptInterrupted\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"unexpected\":true}")]
	[InlineData(
		"{\"schemaVersion\":1,\"reason\":\"AttemptInterrupted\",\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\"}")]
	[InlineData(
		"{\"schemaVersion\":1,\"reason\":\"FutureSafetyReason\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\"}")]
	[InlineData(
		"{\"schemaVersion\":2,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true}")]
	[InlineData(
		"{\"schemaVersion\":2,\"reason\":\"AttemptInterrupted\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false}")]
	[InlineData(
		"{\"schemaVersion\":3,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false}")]
	[InlineData(
		"{\"schemaVersion\":3,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"not-an-attempt\"}")]
	[InlineData(
		"{\"schemaVersion\":2,\"reason\":\"CanceledAfterStart\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false}")]
	[InlineData(
		"{\"schemaVersion\":4,\"reason\":\"NonZeroExit\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\"}")]
	[InlineData(
		"{\"schemaVersion\":4,\"reason\":\"NonZeroExit\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\",\"detectedCliVersion\":2}")]
	[InlineData(
		"{\"schemaVersion\":4,\"reason\":\"TimedOut\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\",\"detectedCliVersion\":\"1.1.12\"}")]
	[InlineData(
		"{\"schemaVersion\":4,\"reason\":\"NonZeroExit\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\",\"detectedCliVersion\":\"2.0.0\"}")]
	[InlineData(
		"{\"schemaVersion\":3,\"reason\":\"NonZeroExit\",\"latchedAtUtc\":\"2026-08-13T09:42:17+00:00\",\"automaticRevalidationAllowed\":true,\"hasAttemptedAutomaticRevalidation\":false,\"attemptId\":\"6f6266fdd8fb4e66ae0f8973b03b9c44\",\"detectedCliVersion\":\"1.1.12\"}")]
	public async Task LoadAsync_WhenSchemaIsUntrusted_ThrowsInvalidDataException(
		string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		await WriteDocumentAsync(filePath, json);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			store.LoadAsync(CancellationToken.None));

		Assert.Equal(json, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveAsync_WhenPreparedInterruptedAttemptHasNoAttemptId_RejectsState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState invalidState = new(
			AntigravityUsageSafetyFailureReason.AttemptInterrupted,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.SaveAsync(invalidState, CancellationToken.None));

		Assert.False(File.Exists(filePath));
	}

	[Theory]
	[InlineData(AntigravityUsageSafetyFailureReason.CanceledAfterStart)]
	[InlineData(AntigravityUsageSafetyFailureReason.ProcessFailedAfterStart)]
	[InlineData(AntigravityUsageSafetyFailureReason.NonZeroExit)]
	[InlineData(AntigravityUsageSafetyFailureReason.EmptyOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.StdoutTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.StderrTooLarge)]
	[InlineData(AntigravityUsageSafetyFailureReason.UnverifiableOutput)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputEventSequenceInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputJsonInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputHasDuplicateProperties)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputResultEnvelopeInvalid)]
	[InlineData(AntigravityUsageSafetyFailureReason.OutputCommandCopiesDiffer)]
	public async Task SaveAsync_WhenRecoverableStateHasNoAttemptId_RejectsState(
		AntigravityUsageSafetyFailureReason reason)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState invalidState = new(
			reason,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.SaveAsync(invalidState, CancellationToken.None));

		Assert.False(File.Exists(filePath));
	}

	[Theory]
	[InlineData(
		AntigravityUsageSafetyFailureReason.TimedOut,
		"1.1.12",
		"6f6266fdd8fb4e66ae0f8973b03b9c44")]
	[InlineData(
		AntigravityUsageSafetyFailureReason.NonZeroExit,
		"2.0.0",
		"6f6266fdd8fb4e66ae0f8973b03b9c44")]
	[InlineData(
		AntigravityUsageSafetyFailureReason.NonZeroExit,
		"01.1.12",
		"6f6266fdd8fb4e66ae0f8973b03b9c44")]
	[InlineData(
		AntigravityUsageSafetyFailureReason.NonZeroExit,
		"1.1.12",
		null)]
	public async Task SaveAsync_WhenVersionDiagnosticStateIsInvalid_RejectsState(
		AntigravityUsageSafetyFailureReason reason,
		string detectedCliVersion,
		string? attemptId)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState invalidState = new(
			reason,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AttemptId: attemptId,
			DetectedCliVersion: detectedCliVersion);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.SaveAsync(invalidState, CancellationToken.None));

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithLegacyAttemptFlag_PreservesAutomaticRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState state = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: true,
			AttemptId: "7f6266fdd8fb4e66ae0f8973b03b9c45");

		await store.SaveAsync(state, CancellationToken.None);
		AntigravityOfficialPrintSafetyState? loaded =
			await store.LoadAsync(CancellationToken.None);

		Assert.Equal(state, loaded);
	}

	[Fact]
	public async Task AcquireExecutionLeaseAsync_SerializesDifferentStoreInstances()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore firstStore = new(filePath);
		JsonAntigravityOfficialPrintSafetyStateStore secondStore = new(filePath);
		using IDisposable firstLease =
			await firstStore.AcquireExecutionLeaseAsync(CancellationToken.None);
		using CancellationTokenSource timeoutSource =
			new(TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			secondStore.AcquireExecutionLeaseAsync(timeoutSource.Token));

		firstLease.Dispose();
		using IDisposable secondLease =
			await secondStore.AcquireExecutionLeaseAsync(CancellationToken.None);
	}

	[Fact]
	public async Task LoadAsync_WhenDocumentExceedsSizeLimit_ThrowsWithoutParsing()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		byte[] oversizedDocument = Encoding.UTF8.GetBytes(
			new string(' ', (4 * 1024) + 1));
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		await File.WriteAllBytesAsync(filePath, oversizedDocument);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(CancellationToken.None));

		Assert.Contains("invalid size", exception.Message, StringComparison.Ordinal);
		Assert.Equal(oversizedDocument.Length, new FileInfo(filePath).Length);
	}

	[Theory]
	[InlineData(AntigravityUsageSafetyFailureReason.None)]
	[InlineData((AntigravityUsageSafetyFailureReason)int.MaxValue)]
	public async Task SaveAsync_WhenReasonIsInvalid_RejectsStateAndLeavesTargetUnchanged(
		AntigravityUsageSafetyFailureReason invalidReason)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.NonZeroExit,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero));
		await store.SaveAsync(originalState, CancellationToken.None);
		byte[] originalDocument = await File.ReadAllBytesAsync(filePath);
		AntigravityOfficialPrintSafetyState invalidState = new(
			invalidReason,
			DateTimeOffset.UtcNow);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			store.SaveAsync(invalidState, CancellationToken.None));

		Assert.Equal(originalDocument, await File.ReadAllBytesAsync(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task SaveAsync_WhenTimestampIsNotUtc_RejectsStateAndLeavesTargetUnchanged()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetSafetyStateFilePath(temporaryDirectory);
		JsonAntigravityOfficialPrintSafetyStateStore store = new(filePath);
		AntigravityOfficialPrintSafetyState originalState = new(
			AntigravityUsageSafetyFailureReason.EmptyOutput,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero));
		await store.SaveAsync(originalState, CancellationToken.None);
		byte[] originalDocument = await File.ReadAllBytesAsync(filePath);
		AntigravityOfficialPrintSafetyState invalidState = new(
			AntigravityUsageSafetyFailureReason.EmptyOutput,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.FromHours(8)));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.SaveAsync(invalidState, CancellationToken.None));

		Assert.Equal(originalDocument, await File.ReadAllBytesAsync(filePath));
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
			"safety-junction");
		string targetFilePath = Path.Combine(
			targetDirectoryPath,
			"official-print-safety-v1.json");
		Directory.CreateDirectory(targetDirectoryPath);
		JsonAntigravityOfficialPrintSafetyStateStore targetStore = new(
			targetFilePath);
		AntigravityOfficialPrintSafetyState state = new(
			AntigravityUsageSafetyFailureReason.TimedOut,
			new DateTimeOffset(2026, 8, 13, 9, 42, 17, TimeSpan.Zero),
			AutomaticRevalidationAllowed: true,
			HasAttemptedAutomaticRevalidation: false);
		await targetStore.SaveAsync(state, CancellationToken.None);
		byte[] expectedDocument = await File.ReadAllBytesAsync(targetFilePath);
		await JunctionTestHelper.CreateAsync(
			junctionDirectoryPath,
			targetDirectoryPath);

		try
		{
			JsonAntigravityOfficialPrintSafetyStateStore aliasedStore = new(
				Path.Combine(
					junctionDirectoryPath,
					"official-print-safety-v1.json"));

			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.LoadAsync(CancellationToken.None));
			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.SaveAsync(state, CancellationToken.None));
			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.ClearAsync(CancellationToken.None));
			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.AcquireExecutionLeaseAsync(
					CancellationToken.None));
			Assert.Equal(
				expectedDocument,
				await File.ReadAllBytesAsync(targetFilePath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectoryPath);
		}
	}

	private static string GetSafetyStateFilePath(
		TemporaryDirectory temporaryDirectory)
	{
		return Path.Combine(
			temporaryDirectory.Path,
			"antigravity",
			"official-print-safety-v1.json");
	}

	private static async Task WriteDocumentAsync(
		string filePath,
		string json)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
	}
}
