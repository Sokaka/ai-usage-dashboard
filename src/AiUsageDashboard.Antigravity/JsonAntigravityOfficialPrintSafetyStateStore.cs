using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

public static class AntigravityOfficialPrintSafetyStatePaths
{
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string AntigravityDirectoryName = "antigravity";
	private const string SafetyStateFileName =
		"official-print-safety-v1.json";

	public static string GetDefaultFilePath()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException(
				"The local application-data directory is unavailable.");
		}

		return Path.Combine(
			localApplicationData,
			ApplicationDirectoryName,
			AntigravityDirectoryName,
			SafetyStateFileName);
	}
}

public sealed class JsonAntigravityOfficialPrintSafetyStateStore :
	IAntigravityOfficialPrintSafetyStateStore,
	IAntigravityOfficialPrintExecutionGate
{
	private sealed record SafetyStateDocument(
		int SchemaVersion,
		string Reason,
		DateTimeOffset LatchedAtUtc,
		bool AutomaticRevalidationAllowed,
		bool HasAttemptedAutomaticRevalidation,
		string? AttemptId,
		string? DetectedCliVersion);

	private sealed record SafetyStateJournalDocument(
		int SchemaVersion,
		string Operation,
		SafetyStateDocument? State);

	private sealed record ParsedSafetyStateJournal(
		bool IsClear,
		AntigravityOfficialPrintSafetyState? State);

	private const int LegacySchemaVersion = 1;
	private const int AutomaticRevalidationSchemaVersion = 2;
	private const int AttemptProvenanceSchemaVersion = 3;
	private const int VersionDiagnosticSchemaVersion = 4;
	private const int JournalSchemaVersion = 1;
	private const string JournalSaveOperation = "Save";
	private const string JournalClearOperation = "Clear";
	private const string ManagedPathDescription =
		"The AGY official-print safety-state path";
	private const int WindowsSharingViolation = 32;
	private const int WindowsLockViolation = 33;
	private const int PersistenceRetryCount = 3;
	private const long MaximumDocumentSizeBytes = 4 * 1024;
	private static readonly TimeSpan ExecutionGatePollInterval =
		TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan PersistenceRetryDelay =
		TimeSpan.FromMilliseconds(25);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};
	private readonly string _filePath;
	private readonly string _journalFilePath;
	private readonly string _executionLockFilePath;
	private readonly SemaphoreSlim _persistenceGate = new(1, 1);

	public JsonAntigravityOfficialPrintSafetyStateStore(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
		string? directoryPath = Path.GetDirectoryName(_filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new ArgumentException(
				"The AGY official-print safety-state path has no directory.",
				nameof(filePath));
		}

		_executionLockFilePath = Path.Combine(
			directoryPath,
			"official-print-execution-v1.lock");
		_journalFilePath = _filePath + ".journal";
	}

	public async Task<IDisposable> AcquireExecutionLeaseAsync(
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_executionLockFilePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"The AGY official-print execution-lock directory is unavailable.");
		}

		AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			ManagedPathDescription);

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					_executionLockFilePath,
					ManagedPathDescription);
				return new FileStream(
					_executionLockFilePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.WriteThrough);
			}
			catch (IOException exception) when (
				IsExecutionGateContention(exception))
			{
				await Task.Delay(
					ExecutionGatePollInterval,
					cancellationToken);
			}
		}
	}

	private static bool IsExecutionGateContention(IOException exception)
	{
		int nativeErrorCode = exception.HResult & 0xFFFF;
		return nativeErrorCode is
			WindowsSharingViolation or WindowsLockViolation;
	}

	public async Task<AntigravityOfficialPrintSafetyState?> LoadAsync(
		CancellationToken cancellationToken)
	{
		await _persistenceGate.WaitAsync(cancellationToken);

		try
		{
			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_journalFilePath,
				ManagedPathDescription);
			if (File.Exists(_journalFilePath))
			{
				ParsedSafetyStateJournal journal =
					await ReadJournalAsync(
						_journalFilePath,
						cancellationToken);
				await TryReconcileJournalAsync(journal);
				return journal.State;
			}

			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			if (!File.Exists(_filePath))
			{
				return null;
			}

			return await ReadStateAsync(_filePath, cancellationToken);
		}
		finally
		{
			_persistenceGate.Release();
		}
	}

	public async Task SaveAsync(
		AntigravityOfficialPrintSafetyState state,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state);
		ValidateState(state);

		await _persistenceGate.WaitAsync(cancellationToken);

		try
		{
			string? directoryPath = Path.GetDirectoryName(_filePath);

			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				throw new InvalidOperationException(
					"The AGY official-print safety-state directory is unavailable.");
			}

			AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
				directoryPath,
				ManagedPathDescription);
			ParsedSafetyStateJournal journal = new(
				IsClear: false,
				state);
			await WriteJournalAtomicallyAsync(journal, cancellationToken);
			await TryReconcileJournalAsync(journal);
		}
		finally
		{
			_persistenceGate.Release();
		}
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await _persistenceGate.WaitAsync(cancellationToken);

		try
		{
			string? directoryPath = Path.GetDirectoryName(_filePath);

			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				throw new InvalidOperationException(
					"The AGY official-print safety-state directory is unavailable.");
			}

			AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
				directoryPath,
				ManagedPathDescription);
			ParsedSafetyStateJournal journal = new(
				IsClear: true,
				State: null);
			await WriteJournalAtomicallyAsync(journal, cancellationToken);
			await TryReconcileJournalAsync(journal);
		}
		finally
		{
			_persistenceGate.Release();
		}
	}

	private static async Task<AntigravityOfficialPrintSafetyState> ReadStateAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		using JsonDocument jsonDocument = await ReadJsonDocumentAsync(
			filePath,
			maxDepth: 4,
			cancellationToken);
		return ParseDocument(jsonDocument.RootElement);
	}

	private static async Task<ParsedSafetyStateJournal> ReadJournalAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		using JsonDocument jsonDocument = await ReadJsonDocumentAsync(
			filePath,
			maxDepth: 6,
			cancellationToken);
		return ParseJournal(jsonDocument.RootElement);
	}

	private static async Task<JsonDocument> ReadJsonDocumentAsync(
		string filePath,
		int maxDepth,
		CancellationToken cancellationToken)
	{
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		if (stream.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"The AGY official-print safety document has an invalid size.");
		}

		try
		{
			return await JsonDocument.ParseAsync(
				stream,
				new JsonDocumentOptions { MaxDepth = maxDepth },
				cancellationToken);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"The AGY official-print safety document is malformed.",
				exception);
		}
	}

	private static ParsedSafetyStateJournal ParseJournal(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException(
				"The AGY official-print safety journal must be a JSON object.");
		}

		int propertyCount = 0;
		int schemaVersionCount = 0;
		int operationCount = 0;
		int stateCount = 0;
		int schemaVersion = default;
		string? operation = null;
		JsonElement stateElement = default;

		foreach (JsonProperty property in root.EnumerateObject())
		{
			propertyCount++;

			switch (property.Name)
			{
				case "schemaVersion":
					schemaVersionCount++;

					if (!property.Value.TryGetInt32(out schemaVersion))
					{
						throw new InvalidDataException(
							"The AGY safety-journal schema version is invalid.");
					}

					break;
				case "operation":
					operationCount++;
					operation = property.Value.ValueKind == JsonValueKind.String
						? property.Value.GetString()
						: null;
					break;
				case "state":
					stateCount++;
					stateElement = property.Value;
					break;
				default:
					throw new InvalidDataException(
						"The AGY official-print safety journal contains an unknown property.");
			}
		}

		bool isSave = string.Equals(
			operation,
			JournalSaveOperation,
			StringComparison.Ordinal);
		bool isClear = string.Equals(
			operation,
			JournalClearOperation,
			StringComparison.Ordinal);

		if (schemaVersion != JournalSchemaVersion ||
			schemaVersionCount != 1 ||
			operationCount != 1 ||
			(!isSave && !isClear) ||
			propertyCount != (isSave ? 3 : 2) ||
			stateCount != (isSave ? 1 : 0) ||
			(isSave && stateElement.ValueKind != JsonValueKind.Object))
		{
			throw new InvalidDataException(
				"The AGY official-print safety journal does not match its schema.");
		}

		return isClear
			? new ParsedSafetyStateJournal(IsClear: true, State: null)
			: new ParsedSafetyStateJournal(
				IsClear: false,
				ParseDocument(stateElement));
	}

	private static AntigravityOfficialPrintSafetyState ParseDocument(
		JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException(
				"The AGY official-print safety state must be a JSON object.");
		}

		int propertyCount = 0;
		int schemaVersionCount = 0;
		int reasonCount = 0;
		int latchedAtUtcCount = 0;
		int automaticRevalidationAllowedCount = 0;
		int automaticRevalidationCount = 0;
		int attemptIdCount = 0;
		int detectedCliVersionCount = 0;
		int schemaVersion = default;
		string? reasonText = null;
		string? latchedAtUtcText = null;
		DateTimeOffset latchedAtUtc = default;
		bool automaticRevalidationAllowed = false;
		bool hasAttemptedAutomaticRevalidation = false;
		string? attemptId = null;
		string? detectedCliVersion = null;

		foreach (JsonProperty property in root.EnumerateObject())
		{
			propertyCount++;

			switch (property.Name)
			{
				case "schemaVersion":
					schemaVersionCount++;

					if (!property.Value.TryGetInt32(out schemaVersion))
					{
						throw new InvalidDataException(
							"The AGY safety-state schema version is invalid.");
					}

					break;
				case "reason":
					reasonCount++;
					reasonText = property.Value.ValueKind == JsonValueKind.String
						? property.Value.GetString()
						: null;
					break;
				case "latchedAtUtc":
					latchedAtUtcCount++;
					latchedAtUtcText =
						property.Value.ValueKind == JsonValueKind.String
							? property.Value.GetString()
							: null;

					if (!HasExplicitUtcOffset(latchedAtUtcText) ||
						!property.Value.TryGetDateTimeOffset(out latchedAtUtc))
					{
						throw new InvalidDataException(
							"The AGY safety-state timestamp is invalid.");
					}

					break;
				case "automaticRevalidationAllowed":
					automaticRevalidationAllowedCount++;

					if (property.Value.ValueKind is not
						(JsonValueKind.True or JsonValueKind.False))
					{
						throw new InvalidDataException(
							"The AGY automatic-revalidation eligibility is invalid.");
					}

					automaticRevalidationAllowed =
						property.Value.GetBoolean();
					break;
				case "hasAttemptedAutomaticRevalidation":
					automaticRevalidationCount++;

					if (property.Value.ValueKind is not
						(JsonValueKind.True or JsonValueKind.False))
					{
						throw new InvalidDataException(
							"The AGY automatic-revalidation state is invalid.");
					}

					hasAttemptedAutomaticRevalidation =
						property.Value.GetBoolean();
					break;
				case "attemptId":
					attemptIdCount++;
					attemptId = property.Value.ValueKind == JsonValueKind.String
						? property.Value.GetString()
						: null;
					break;
				case "detectedCliVersion":
					detectedCliVersionCount++;
					detectedCliVersion =
						property.Value.ValueKind == JsonValueKind.String
							? property.Value.GetString()
							: null;
					break;
				default:
					throw new InvalidDataException(
						"The AGY official-print safety state contains an unknown property.");
			}
		}

		bool isLegacyDocument = schemaVersion == LegacySchemaVersion;
		bool isAutomaticRevalidationDocument =
			schemaVersion == AutomaticRevalidationSchemaVersion;
		bool isAttemptProvenanceDocument =
			schemaVersion == AttemptProvenanceSchemaVersion;
		bool isVersionDiagnosticDocument =
			schemaVersion == VersionDiagnosticSchemaVersion;
		bool hasAttemptProvenance =
			isAttemptProvenanceDocument || isVersionDiagnosticDocument;

		if ((!isLegacyDocument &&
			 !isAutomaticRevalidationDocument &&
			 !isAttemptProvenanceDocument &&
			 !isVersionDiagnosticDocument) ||
			propertyCount != (isLegacyDocument
				? 3
				: isAutomaticRevalidationDocument
					? 5
					: isAttemptProvenanceDocument ? 6 : 7) ||
			schemaVersionCount != 1 ||
			reasonCount != 1 ||
			latchedAtUtcCount != 1 ||
			automaticRevalidationAllowedCount !=
				(isLegacyDocument ? 0 : 1) ||
			automaticRevalidationCount != (isLegacyDocument ? 0 : 1) ||
			attemptIdCount != (hasAttemptProvenance ? 1 : 0) ||
			detectedCliVersionCount !=
				(isVersionDiagnosticDocument ? 1 : 0) ||
			(isVersionDiagnosticDocument && detectedCliVersion is null) ||
			!TryParseReason(reasonText, out AntigravityUsageSafetyFailureReason reason))
		{
			throw new InvalidDataException(
				"The AGY official-print safety state does not match a supported schema.");
		}

		if ((hasAttemptProvenance && !IsValidAttemptId(attemptId)) ||
			(!hasAttemptProvenance &&
			 automaticRevalidationAllowed &&
			 reason == AntigravityUsageSafetyFailureReason.CanceledAfterStart))
		{
			throw new InvalidDataException(
				"The AGY official-print safety state has invalid recovery provenance.");
		}

		AntigravityOfficialPrintSafetyState state = new(
			reason,
			latchedAtUtc,
			!isLegacyDocument && automaticRevalidationAllowed,
			!isLegacyDocument && hasAttemptedAutomaticRevalidation,
			hasAttemptProvenance ? attemptId : null,
			isVersionDiagnosticDocument ? detectedCliVersion : null);

		try
		{
			ValidateState(state);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"The AGY official-print safety state is semantically invalid.",
				exception);
		}

		return state;
	}

	private static async Task WriteTemporaryDocumentAsync(
		string temporaryFilePath,
		AntigravityOfficialPrintSafetyState state,
		CancellationToken cancellationToken)
	{
		SafetyStateDocument document = CreateStateDocument(state);

		await using MemoryStream serializedDocument = new();
		await JsonSerializer.SerializeAsync(
			serializedDocument,
			document,
			SerializerOptions,
			cancellationToken);

		if (serializedDocument.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"The AGY official-print safety state exceeds its size limit.");
		}

		serializedDocument.Position = 0;
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			temporaryFilePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			temporaryFilePath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await serializedDocument.CopyToAsync(stream, cancellationToken);
		await stream.FlushAsync(cancellationToken);
		stream.Flush(flushToDisk: true);
	}

	private async Task WriteJournalAtomicallyAsync(
		ParsedSafetyStateJournal journal,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_journalFilePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"The AGY official-print safety-journal directory is unavailable.");
		}

		AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			ManagedPathDescription);
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_journalFilePath,
			ManagedPathDescription);

		SafetyStateJournalDocument document = new(
			JournalSchemaVersion,
			journal.IsClear ? JournalClearOperation : JournalSaveOperation,
			journal.State is null ? null : CreateStateDocument(journal.State));
		for (int attempt = 1; ; attempt++)
		{
			string temporaryFilePath = Path.Combine(
				directoryPath,
				$".{Path.GetFileName(_journalFilePath)}.{Guid.NewGuid():N}.tmp");

			try
			{
				AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					temporaryFilePath,
					ManagedPathDescription);
				await WriteTemporaryJournalAsync(
					temporaryFilePath,
					document,
					cancellationToken);
				PublishJournalAtomically(temporaryFilePath, _journalFilePath);
				return;
			}
			catch (Exception exception) when (
				attempt < PersistenceRetryCount &&
				IsRetryablePersistenceException(exception))
			{
				await Task.Delay(PersistenceRetryDelay, cancellationToken);
			}
			finally
			{
				TryDeleteTemporaryFile(temporaryFilePath);
			}
		}
	}

	private async Task TryReconcileJournalAsync(
		ParsedSafetyStateJournal journal)
	{
		for (int attempt = 1; ; attempt++)
		{
			bool primaryStateReconciled;

			try
			{
				if (journal.IsClear)
				{
					AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
						_filePath,
						ManagedPathDescription);
					if (File.Exists(_filePath))
					{
						File.Delete(_filePath);
					}

					primaryStateReconciled = true;
				}
				else
				{
					await WriteStateAtomicallyAsync(
						journal.State!,
						CancellationToken.None);
					primaryStateReconciled = true;
				}
			}
			catch (Exception exception) when (
				attempt < PersistenceRetryCount &&
				IsRetryablePersistenceException(exception))
			{
				await Task.Delay(PersistenceRetryDelay);
				continue;
			}
			catch
			{
				// Once the journal is durable it is the logical state. Leave it in
				// place so a future process can finish publishing or deleting the
				// primary file without losing the committed transition.
				primaryStateReconciled = false;
			}

			if (primaryStateReconciled)
			{
				await TryDeleteJournalAsync();
			}

			return;
		}
	}

	private async Task TryDeleteJournalAsync()
	{
		for (int attempt = 1; attempt <= PersistenceRetryCount; attempt++)
		{
			try
			{
				AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					_journalFilePath,
					ManagedPathDescription);
				if (File.Exists(_journalFilePath))
				{
					File.Delete(_journalFilePath);
				}

				return;
			}
			catch (Exception exception) when (
				attempt < PersistenceRetryCount &&
				IsRetryablePersistenceException(exception))
			{
				await Task.Delay(PersistenceRetryDelay);
			}
			catch
			{
				return;
			}
		}
	}

	private async Task WriteStateAtomicallyAsync(
		AntigravityOfficialPrintSafetyState state,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"The AGY official-print safety-state directory is unavailable.");
		}

		AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			ManagedPathDescription);
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_filePath,
			ManagedPathDescription);

		string temporaryFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			await WriteTemporaryDocumentAsync(
				temporaryFilePath,
				state,
				cancellationToken);
			PublishFileAtomically(temporaryFilePath, _filePath);
		}
		finally
		{
			TryDeleteTemporaryFile(temporaryFilePath);
		}
	}

	private static async Task WriteTemporaryJournalAsync(
		string temporaryFilePath,
		SafetyStateJournalDocument document,
		CancellationToken cancellationToken)
	{
		await using MemoryStream serializedDocument = new();
		await JsonSerializer.SerializeAsync(
			serializedDocument,
			document,
			SerializerOptions,
			cancellationToken);

		if (serializedDocument.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"The AGY official-print safety journal exceeds its size limit.");
		}

		serializedDocument.Position = 0;
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			temporaryFilePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			temporaryFilePath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await serializedDocument.CopyToAsync(stream, cancellationToken);
		await stream.FlushAsync(cancellationToken);
		stream.Flush(flushToDisk: true);
	}

	private static SafetyStateDocument CreateStateDocument(
		AntigravityOfficialPrintSafetyState state)
	{
		return new SafetyStateDocument(
			state.DetectedCliVersion is not null
				? VersionDiagnosticSchemaVersion
				: state.AttemptId is null
					? AutomaticRevalidationSchemaVersion
					: AttemptProvenanceSchemaVersion,
			state.Reason.ToString(),
			state.LatchedAtUtc,
			state.AutomaticRevalidationAllowed,
			state.HasAttemptedAutomaticRevalidation,
			state.AttemptId,
			state.DetectedCliVersion);
	}

	private static void PublishFileAtomically(
		string temporaryFilePath,
		string destinationFilePath)
	{
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			temporaryFilePath,
			ManagedPathDescription);
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			destinationFilePath,
			ManagedPathDescription);
		if (File.Exists(destinationFilePath))
		{
			File.Replace(
				temporaryFilePath,
				destinationFilePath,
				destinationBackupFileName: null);
			return;
		}

		File.Move(temporaryFilePath, destinationFilePath);
	}

	private static void PublishJournalAtomically(
		string temporaryFilePath,
		string destinationFilePath)
	{
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			temporaryFilePath,
			ManagedPathDescription);
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			destinationFilePath,
			ManagedPathDescription);
		File.Move(
			temporaryFilePath,
			destinationFilePath,
			overwrite: true);
	}

	private static bool IsRetryablePersistenceException(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException;
	}

	private static bool TryParseReason(
		string? reasonText,
		out AntigravityUsageSafetyFailureReason reason)
	{
		return Enum.TryParse(reasonText, ignoreCase: false, out reason) &&
			Enum.IsDefined(reason) &&
			reason != AntigravityUsageSafetyFailureReason.None &&
			string.Equals(
				reasonText,
				reason.ToString(),
				StringComparison.Ordinal);
	}

	private static bool HasExplicitUtcOffset(string? timestampText)
	{
		return !string.IsNullOrWhiteSpace(timestampText) &&
			(timestampText.EndsWith('Z') ||
				timestampText.EndsWith(
					"+00:00",
					StringComparison.Ordinal));
	}

	private static bool IsValidAttemptId(string? attemptId)
	{
		return attemptId is not null &&
			Guid.TryParseExact(attemptId, "N", out _);
	}

	private static void ValidateState(
		AntigravityOfficialPrintSafetyState state)
	{
		if (!Enum.IsDefined(state.Reason) ||
			state.Reason == AntigravityUsageSafetyFailureReason.None)
		{
			throw new ArgumentOutOfRangeException(
				nameof(state),
				state.Reason,
				"The AGY safety-state reason is invalid.");
		}

		if (state.LatchedAtUtc == default ||
			state.LatchedAtUtc.Offset != TimeSpan.Zero)
		{
			throw new ArgumentException(
				"The AGY safety-state timestamp must be a non-default UTC value.",
				nameof(state));
		}

		if (state.AutomaticRevalidationAllowed &&
			state.Reason !=
				AntigravityUsageSafetyFailureReason.AttemptInterrupted &&
			state.Reason !=
				AntigravityUsageSafetyFailureReason.SafetyStateUnavailable &&
			!AntigravityAutomaticRecoveryPolicy.IsRecoverableReason(state.Reason))
		{
			throw new ArgumentException(
				"Only an explicitly recoverable AGY safety state may allow automatic revalidation.",
				nameof(state));
		}

		if (state.AutomaticRevalidationAllowed &&
			state.Reason != AntigravityUsageSafetyFailureReason.TimedOut &&
			!IsValidAttemptId(state.AttemptId))
		{
			throw new ArgumentException(
				"An automatically recoverable AGY failure requires current process-tree provenance.",
				nameof(state));
		}

		if (state.AttemptId is not null &&
			!IsValidAttemptId(state.AttemptId))
		{
			throw new ArgumentException(
				"The AGY safety-state attempt identifier is invalid.",
				nameof(state));
		}

		if ((state.DetectedCliVersion is not null) &&
			(!IsValidAttemptId(state.AttemptId) ||
			 !AntigravityOfficialPrintVersionDiagnosticPolicy
				 .IsValidPersistedState(
					 state.Reason,
					 state.DetectedCliVersion)))
		{
			throw new ArgumentException(
				"The AGY safety-state CLI version diagnostic is invalid.",
				nameof(state));
		}
	}

	private static void TryDeleteTemporaryFile(string temporaryFilePath)
	{
		try
		{
			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryFilePath,
				ManagedPathDescription);
			if (File.Exists(temporaryFilePath))
			{
				File.Delete(temporaryFilePath);
			}
		}
		catch
		{
			// A failed best-effort cleanup must not replace the persistence error.
		}
	}
}
