using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.Infrastructure;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal sealed class JsonUpdateCheckStateStore : IUpdateCheckStateStore
{
	private sealed record UpdateCheckStateDocument(
		int SchemaVersion,
		bool IsAutoCheckEnabled,
		bool HasShownAutomaticCheckNotice,
		DateTimeOffset? LastAttemptUtc,
		DateTimeOffset? LastSuccessfulCheckUtc,
		int ConsecutiveFailureCount,
		long? HighestObservedReleaseSequence,
		UpdateKnownResult? LastKnownResult,
		string? LastBalloonAttemptKey,
		UpdateSnoozeState? Snooze);

	private const int CurrentSchemaVersion = 1;
	private const string DiagnosticOperation = "update-check-state-cache";
	private const long MaximumDocumentSizeBytes = 64 * 1024;
	private const int MaximumFailureCount = 1_000_000;
	private const string ManagedPathDescription =
		"The update check state cache path";
	private static readonly HashSet<string> KnownResultPropertyNames = new(
		new[]
		{
			"availableVersion",
			"checkedAgainstVersion",
			"isUpdateAvailable",
			"releaseSequence"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> RootPropertyNames = new(
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
		StringComparer.Ordinal);
	private static readonly HashSet<string> SnoozePropertyNames = new(
		new[]
		{
			"releaseSequence",
			"snoozedUntilUtc",
			"version"
		},
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates = new(
		StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 8,
		PropertyNameCaseInsensitive = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};
	private readonly string _filePath;
	private readonly Action<FileStream> _flushTemporaryFileToDisk;
	private readonly Action<string, string> _publishTemporaryFileAtomically;
	private readonly Action<string, Exception?> _reportDiagnostic;

	internal JsonUpdateCheckStateStore(string filePath)
		: this(
			filePath,
			FlushFileToDisk,
			PublishFileAtomically,
			ReportDiagnostic)
	{
	}

	internal JsonUpdateCheckStateStore(
		string filePath,
		Action<FileStream> flushTemporaryFileToDisk,
		Action<string, string> publishTemporaryFileAtomically,
		Action<string, Exception?>? reportDiagnostic = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
		_flushTemporaryFileToDisk = flushTemporaryFileToDisk ??
			throw new ArgumentNullException(nameof(flushTemporaryFileToDisk));
		_publishTemporaryFileAtomically =
			publishTemporaryFileAtomically ??
			throw new ArgumentNullException(
				nameof(publishTemporaryFileAtomically));
		_reportDiagnostic = reportDiagnostic ?? ReportDiagnostic;
	}

	public async Task<UpdateCheckPersistentState?> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable fileLease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			if (!File.Exists(_filePath))
			{
				return null;
			}

			await using FileStream stream = new(
				_filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			long fileLength = stream.Length;
			if ((fileLength <= 0) ||
				(fileLength > MaximumDocumentSizeBytes))
			{
				throw new InvalidDataException(
					"The update check state cache has an invalid size.");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				(int)fileLength);
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			byte[] trailingByte = new byte[1];
			if (await stream.ReadAsync(trailingByte, cancellationToken)
					.ConfigureAwait(false) != 0)
			{
				throw new InvalidDataException(
					"The update check state cache changed while it was read.");
			}

			using JsonDocument parsedDocument = JsonDocument.Parse(
				contents,
				new JsonDocumentOptions { MaxDepth = 8 });
			ValidateDocumentShape(parsedDocument.RootElement);
			UpdateCheckStateDocument document =
				JsonSerializer.Deserialize<UpdateCheckStateDocument>(
					contents,
					SerializerOptions) ?? throw new InvalidDataException(
						"The update check state cache is empty.");
			UpdateCheckPersistentState state = CreateState(document);
			ValidateState(state);
			return NormalizeTimestamps(state);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (IsCacheAccessFailure(exception))
		{
			TryReportDiagnostic(
				"The update check state cache could not be loaded and was ignored.",
				exception);
			return null;
		}
	}

	public async Task<bool> TrySaveAsync(
		UpdateCheckPersistentState state,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(state);
		ValidateState(state);
		UpdateCheckPersistentState normalizedState = NormalizeTimestamps(state);
		UpdateCheckStateDocument document = CreateDocument(normalizedState);
		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);
		if (contents.Length > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"The update check state cache exceeds its size limit.");
		}

		using IDisposable fileLease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken).ConfigureAwait(false);
		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"The update check state cache path has no parent directory.");
		}

		string temporaryPath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			ManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
				directoryPath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryPath,
				ManagedPathDescription);

			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(contents, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken)
					.ConfigureAwait(false);
				_flushTemporaryFileToDisk(stream);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryPath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			_publishTemporaryFileAtomically(temporaryPath, _filePath);
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (IsCacheAccessFailure(exception))
		{
			TryReportDiagnostic(
				"The update check state cache could not be saved; in-memory state remains active.",
				exception);
			return false;
		}
		finally
		{
			try
			{
				ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					temporaryPath,
					ManagedPathDescription);
				File.Delete(temporaryPath);
			}
			catch (Exception exception) when (
				IsCacheAccessFailure(exception))
			{
				TryReportDiagnostic(
					"A temporary update check state file could not be cleaned up.",
					exception);
			}
		}
	}

	private static UpdateCheckStateDocument CreateDocument(
		UpdateCheckPersistentState state)
	{
		return new UpdateCheckStateDocument(
			CurrentSchemaVersion,
			state.IsAutoCheckEnabled,
			state.HasShownAutomaticCheckNotice,
			state.LastAttemptUtc,
			state.LastSuccessfulCheckUtc,
			state.ConsecutiveFailureCount,
			state.HighestObservedReleaseSequence,
			state.LastKnownResult,
			state.LastBalloonAttemptKey,
			state.Snooze);
	}

	private static UpdateCheckPersistentState CreateState(
		UpdateCheckStateDocument document)
	{
		if (document.SchemaVersion != CurrentSchemaVersion)
		{
			throw new InvalidDataException(
				$"Unsupported update check state schema version: " +
				$"{document.SchemaVersion}.");
		}

		return new UpdateCheckPersistentState(
			document.IsAutoCheckEnabled,
			document.HasShownAutomaticCheckNotice,
			document.LastAttemptUtc,
			document.LastSuccessfulCheckUtc,
			document.ConsecutiveFailureCount,
			document.HighestObservedReleaseSequence,
			document.LastKnownResult,
			document.LastBalloonAttemptKey,
			document.Snooze);
	}

	private static void FlushFileToDisk(FileStream stream)
	{
		stream.Flush(flushToDisk: true);
	}

	private static bool HasExactProperties(
		JsonElement element,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> actualPropertyNames = new(StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expectedPropertyNames.Contains(property.Name) ||
				!actualPropertyNames.Add(property.Name))
			{
				return false;
			}
		}

		return actualPropertyNames.SetEquals(expectedPropertyNames);
	}

	private static bool IsCacheAccessFailure(Exception exception)
	{
		return exception is IOException or
			UnauthorizedAccessException or
			SecurityException or
			NotSupportedException or
			JsonException or
			InvalidDataException;
	}

	private static UpdateCheckPersistentState NormalizeTimestamps(
		UpdateCheckPersistentState state)
	{
		return state with
		{
			LastAttemptUtc = state.LastAttemptUtc?.ToUniversalTime(),
			LastSuccessfulCheckUtc =
				state.LastSuccessfulCheckUtc?.ToUniversalTime(),
			Snooze = state.Snooze is null
				? null
				: state.Snooze with
				{
					SnoozedUntilUtc =
						state.Snooze.SnoozedUntilUtc.ToUniversalTime()
				}
		};
	}

	private static void PublishFileAtomically(
		string temporaryPath,
		string destinationPath)
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
	}

	private static void ReportDiagnostic(
		string summary,
		Exception? exception)
	{
		_ = AppDiagnostics.TryWrite(
			DiagnosticOperation,
			summary,
			exception);
	}

	private static void ValidateDocumentShape(JsonElement rootElement)
	{
		if (!HasExactProperties(rootElement, RootPropertyNames))
		{
			throw new InvalidDataException(
				"The update check state cache has an invalid root shape.");
		}

		JsonElement knownResultElement =
			rootElement.GetProperty("lastKnownResult");
		if ((knownResultElement.ValueKind != JsonValueKind.Null) &&
			!HasExactProperties(
				knownResultElement,
				KnownResultPropertyNames))
		{
			throw new InvalidDataException(
				"The cached update result has an invalid shape.");
		}

		JsonElement snoozeElement = rootElement.GetProperty("snooze");
		if ((snoozeElement.ValueKind != JsonValueKind.Null) &&
			!HasExactProperties(snoozeElement, SnoozePropertyNames))
		{
			throw new InvalidDataException(
				"The cached update snooze has an invalid shape.");
		}
	}

	private static void ValidateState(UpdateCheckPersistentState state)
	{
		if ((state.ConsecutiveFailureCount < 0) ||
			(state.ConsecutiveFailureCount > MaximumFailureCount) ||
			((state.ConsecutiveFailureCount > 0) &&
				(state.LastAttemptUtc is null)))
		{
			throw new InvalidDataException(
				"The cached update failure state is invalid.");
		}

		if (state.HighestObservedReleaseSequence is long highestSequence &&
			(highestSequence <= 0))
		{
			throw new InvalidDataException(
				"The cached highest release sequence is invalid.");
		}

		if (state.LastKnownResult is UpdateKnownResult knownResult)
		{
			ValidateKnownResult(knownResult);
			if (state.LastSuccessfulCheckUtc is null)
			{
				throw new InvalidDataException(
					"The cached update result has no successful check time.");
			}

			if (state.HighestObservedReleaseSequence is long observedSequence &&
				(observedSequence < knownResult.ReleaseSequence))
			{
				throw new InvalidDataException(
					"The cached update result exceeds the observed release sequence.");
			}
		}
		else if (state.LastSuccessfulCheckUtc is not null)
		{
			throw new InvalidDataException(
				"The cached successful update check has no result.");
		}

		if ((state.LastBalloonAttemptKey is not null) &&
			!UpdateNotificationKey.TryParse(
				state.LastBalloonAttemptKey,
				out _))
		{
			throw new InvalidDataException(
				"The cached update balloon attempt key is invalid.");
		}

		if (state.Snooze is UpdateSnoozeState snooze)
		{
			if (!ReleaseVersion.TryParse(snooze.Version, out _) ||
				(snooze.ReleaseSequence <= 0) ||
				(state.LastKnownResult is not UpdateKnownResult snoozedResult) ||
				!snoozedResult.IsUpdateAvailable ||
				(snoozedResult.NotificationKey != snooze.NotificationKey))
			{
				throw new InvalidDataException(
					"The cached update snooze is invalid.");
			}
		}
	}

	private static void ValidateKnownResult(UpdateKnownResult result)
	{
		if (!ReleaseVersion.TryParse(
				result.AvailableVersion,
				out ReleaseVersion availableVersion) ||
			(result.ReleaseSequence <= 0))
		{
			throw new InvalidDataException(
				"The cached update result is invalid.");
		}

		if (result.CheckedAgainstVersion is null)
		{
			if (result.IsUpdateAvailable)
			{
				throw new InvalidDataException(
					"The cached unknown-version result cannot claim an available update.");
			}

			return;
		}

		if (!ReleaseVersion.TryParse(
				result.CheckedAgainstVersion,
				out ReleaseVersion checkedAgainstVersion))
		{
			throw new InvalidDataException(
				"The cached checked-against version is invalid.");
		}

		bool isNewerVersion =
			availableVersion.CompareTo(checkedAgainstVersion) > 0;
		if (result.IsUpdateAvailable != isNewerVersion)
		{
			throw new InvalidDataException(
				"The cached update availability does not match its versions.");
		}
	}

	private void TryReportDiagnostic(
		string summary,
		Exception exception)
	{
		try
		{
			_reportDiagnostic(summary, exception);
		}
		catch (Exception diagnosticException)
		{
			_ = AppDiagnostics.TryWrite(
				DiagnosticOperation,
				"The update check state diagnostic callback failed.",
				diagnosticException);
		}
	}
}
