using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Persistence;

internal enum GrokConnectionPendingStage
{
	LoginStarted,
	LoginCompleted,
	PrincipalObserved,
	BindingPersisted
}

internal sealed record GrokConnectionPendingWork(
	Guid AccountId,
	Guid AttemptId,
	DateTimeOffset StartedAtUtc,
	DateTimeOffset UpdatedAtUtc,
	GrokConnectionPendingStage Stage,
	Guid? PublicBindingId);

internal enum GrokConnectionBeginResult
{
	Started,
	ReplacedIncompleteAttempt,
	StartedAfterCorruptStateQuarantined,
	BlockedByExistingRecoverableWork
}

internal interface IGrokConnectionPendingStore
{
	Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default);

	Task<GrokConnectionBeginResult> BeginAsync(
		Guid accountId,
		Guid attemptId,
		DateTimeOffset startedAtUtc,
		CancellationToken cancellationToken = default);

	Task<bool> TryAdvanceAsync(
		Guid accountId,
		Guid attemptId,
		GrokConnectionPendingStage stage,
		Guid? publicBindingId,
		DateTimeOffset updatedAtUtc,
		CancellationToken cancellationToken = default);

	Task<bool> TryRemoveAsync(
		Guid accountId,
		Guid attemptId,
		CancellationToken cancellationToken = default);
}

internal sealed class JsonGrokConnectionPendingStore :
	IGrokConnectionPendingStore
{
	private sealed record PendingDocument(
		int SchemaVersion,
		IReadOnlyList<PendingEntry> PendingConnections);

	private sealed record PendingEntry(
		Guid AccountId,
		Guid AttemptId,
		DateTimeOffset StartedAtUtc,
		DateTimeOffset UpdatedAtUtc,
		GrokConnectionPendingStage Stage,
		Guid? PublicBindingId);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 16 * 1024;
	private const int MaximumEntryCount = 16;
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[] { "pendingConnections", "schemaVersion" },
		StringComparer.Ordinal);
	private static readonly HashSet<string> EntryPropertyNames = new(
		new[]
		{
			"accountId",
			"attemptId",
			"publicBindingId",
			"stage",
			"startedAtUtc",
			"updatedAtUtc"
		},
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions =
		CreateSerializerOptions();
	private readonly string _filePath;

	internal JsonGrokConnectionPendingStore(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
	}

	public async Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		Dictionary<Guid, GrokConnectionPendingWork> pending =
			await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
		return pending.Values
			.OrderBy(work => work.AccountId)
			.ToArray();
	}

	public async Task<GrokConnectionBeginResult> BeginAsync(
		Guid accountId,
		Guid attemptId,
		DateTimeOffset startedAtUtc,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		ValidateAttemptId(attemptId);
		ValidateUtcTimestamp(startedAtUtc, nameof(startedAtUtc));
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		Dictionary<Guid, GrokConnectionPendingWork> pending;
		bool didQuarantineCorruptState = false;

		try
		{
			pending = await LoadCoreAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidDataException)
		{
			QuarantineCorruptState(attemptId);
			pending = new();
			didQuarantineCorruptState = true;
		}

		GrokConnectionBeginResult result;
		if (pending.TryGetValue(
				accountId,
				out GrokConnectionPendingWork? existing))
		{
			if (existing.Stage != GrokConnectionPendingStage.LoginStarted)
			{
				return GrokConnectionBeginResult
					.BlockedByExistingRecoverableWork;
			}

			result = GrokConnectionBeginResult.ReplacedIncompleteAttempt;
		}
		else
		{
			result = didQuarantineCorruptState
				? GrokConnectionBeginResult
					.StartedAfterCorruptStateQuarantined
				: GrokConnectionBeginResult.Started;
		}

		pending[accountId] = new GrokConnectionPendingWork(
			accountId,
			attemptId,
			startedAtUtc,
			startedAtUtc,
			GrokConnectionPendingStage.LoginStarted,
			PublicBindingId: null);
		await WriteCoreAsync(pending.Values, cancellationToken)
			.ConfigureAwait(false);
		return result;
	}

	public Task<bool> TryAdvanceAsync(
		Guid accountId,
		Guid attemptId,
		GrokConnectionPendingStage stage,
		Guid? publicBindingId,
		DateTimeOffset updatedAtUtc,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		ValidateAttemptId(attemptId);
		ValidateStage(stage, publicBindingId);
		ValidateUtcTimestamp(updatedAtUtc, nameof(updatedAtUtc));
		return UpdateAsync(
			pending =>
			{
				if (!pending.TryGetValue(
						accountId,
						out GrokConnectionPendingWork? existing) ||
					(existing.AttemptId != attemptId) ||
					(stage <= existing.Stage))
				{
					return false;
				}

				DateTimeOffset effectiveUpdatedAtUtc =
					updatedAtUtc < existing.UpdatedAtUtc
						? existing.UpdatedAtUtc
						: updatedAtUtc;

				pending[accountId] = existing with
				{
					UpdatedAtUtc = effectiveUpdatedAtUtc,
					Stage = stage,
					PublicBindingId = publicBindingId
				};
				return true;
			},
			cancellationToken);
	}

	public Task<bool> TryRemoveAsync(
		Guid accountId,
		Guid attemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		ValidateAttemptId(attemptId);
		return UpdateAsync(
			pending => pending.TryGetValue(
				accountId,
				out GrokConnectionPendingWork? existing) &&
				(existing.AttemptId == attemptId) &&
				pending.Remove(accountId),
			cancellationToken);
	}

	private static JsonSerializerOptions CreateSerializerOptions()
	{
		JsonSerializerOptions options = new()
		{
			MaxDepth = 8,
			PropertyNameCaseInsensitive = false,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			WriteIndented = true
		};
		options.Converters.Add(new JsonStringEnumConverter(
			namingPolicy: null,
			allowIntegerValues: false));
		return options;
	}

	private async Task<bool> UpdateAsync(
		Func<Dictionary<Guid, GrokConnectionPendingWork>, bool> update,
		CancellationToken cancellationToken)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		Dictionary<Guid, GrokConnectionPendingWork> pending =
			await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
		bool didUpdate = update(pending);
		if (didUpdate)
		{
			await WriteCoreAsync(pending.Values, cancellationToken)
				.ConfigureAwait(false);
		}

		return didUpdate;
	}

	private void QuarantineCorruptState(Guid attemptId)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("Grok 連接續做檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		FileAttributes attributes = File.GetAttributes(_filePath);
		if ((attributes &
			(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
		{
			throw new IOException("Grok 連接續做檔類型無效。");
		}

		string quarantineFilePath = string.Concat(
			_filePath,
			".corrupt-",
			attemptId.ToString("N"));
		File.Move(_filePath, quarantineFilePath, overwrite: false);
	}

	private async Task<Dictionary<Guid, GrokConnectionPendingWork>>
		LoadCoreAsync(CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("Grok 連接續做檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		FileStream stream;
		try
		{
			FileAttributes attributes = File.GetAttributes(_filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("Grok 連接續做檔類型無效。");
			}

			stream = new FileStream(
				_filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
		}
		catch (FileNotFoundException)
		{
			return new();
		}
		catch (DirectoryNotFoundException)
		{
			return new();
		}

		await using (stream)
		{
			if (stream.Length is <= 0 or > MaximumDocumentSizeBytes)
			{
				throw new InvalidDataException("Grok 連接續做檔大小無效。");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			return ParseDocument(contents);
		}
	}

	private static Dictionary<Guid, GrokConnectionPendingWork> ParseDocument(
		byte[] contents)
	{
		try
		{
			using JsonDocument parsed = JsonDocument.Parse(
				contents,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8
				});
			JsonElement root = parsed.RootElement;
			if (!HasExactProperties(root, DocumentPropertyNames) ||
				!root.TryGetProperty(
					"schemaVersion",
					out JsonElement schemaVersionElement) ||
				(schemaVersionElement.ValueKind != JsonValueKind.Number) ||
				!schemaVersionElement.TryGetInt32(out int schemaVersion) ||
				(schemaVersion != CurrentSchemaVersion) ||
				!root.TryGetProperty(
					"pendingConnections",
					out JsonElement entries) ||
				(entries.ValueKind != JsonValueKind.Array) ||
				(entries.GetArrayLength() > MaximumEntryCount))
			{
				throw new InvalidDataException("Grok 連接續做檔結構無效。");
			}

			foreach (JsonElement entry in entries.EnumerateArray())
			{
				if (!HasExactProperties(entry, EntryPropertyNames))
				{
					throw new InvalidDataException(
						"Grok 連接續做項目結構無效。");
				}
			}

			PendingDocument? document =
				JsonSerializer.Deserialize<PendingDocument>(
					contents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != CurrentSchemaVersion) ||
				(document.PendingConnections is null) ||
				(document.PendingConnections.Count > MaximumEntryCount))
			{
				throw new InvalidDataException("Grok 連接續做檔內容無效。");
			}

			Dictionary<Guid, GrokConnectionPendingWork> result = new();
			foreach (PendingEntry entry in document.PendingConnections)
			{
				ValidateAccountId(entry.AccountId);
				ValidateAttemptId(entry.AttemptId);
				ValidateUtcTimestamp(
					entry.StartedAtUtc,
					nameof(entry.StartedAtUtc));
				ValidateUtcTimestamp(
					entry.UpdatedAtUtc,
					nameof(entry.UpdatedAtUtc));
				ValidateStage(entry.Stage, entry.PublicBindingId);
				if (entry.UpdatedAtUtc < entry.StartedAtUtc)
				{
					throw new InvalidDataException(
						"Grok 連接續做項目時間順序無效。");
				}

				if (!result.TryAdd(
						entry.AccountId,
						new GrokConnectionPendingWork(
							entry.AccountId,
							entry.AttemptId,
							entry.StartedAtUtc,
							entry.UpdatedAtUtc,
							entry.Stage,
							entry.PublicBindingId)))
				{
					throw new InvalidDataException(
						"Grok 連接續做帳號不可重複。");
				}
			}

			return result;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Grok 連接續做 JSON 格式無效。",
				exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"Grok 連接續做內容無效。",
				exception);
		}
	}

	private async Task WriteCoreAsync(
		IEnumerable<GrokConnectionPendingWork> pending,
		CancellationToken cancellationToken)
	{
		PendingEntry[] entries = pending
			.OrderBy(work => work.AccountId)
			.Select(work => new PendingEntry(
				work.AccountId,
				work.AttemptId,
				work.StartedAtUtc,
				work.UpdatedAtUtc,
				work.Stage,
				work.PublicBindingId))
			.ToArray();
		if (entries.Length > MaximumEntryCount)
		{
			throw new InvalidOperationException(
				"Grok 連接續做項目超過安全上限。");
		}

		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new PendingDocument(CurrentSchemaVersion, entries),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidOperationException(
				"Grok 連接續做檔超過大小上限。");
		}

		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("Grok 連接續做檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		Directory.CreateDirectory(directoryPath);
		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		if (File.Exists(_filePath))
		{
			FileAttributes attributes = File.GetAttributes(_filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("Grok 連接續做檔類型無效。");
			}
		}

		string temporaryFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await using (FileStream stream = new(
				temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(contents, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
			}

			File.Move(temporaryFilePath, _filePath, overwrite: true);
		}
		finally
		{
			try
			{
				File.Delete(temporaryFilePath);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				// A uniquely named sidecar is never loaded as pending work.
			}
		}
	}

	private static bool HasExactProperties(
		JsonElement element,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> observed = new(StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expectedPropertyNames.Contains(property.Name) ||
				!observed.Add(property.Name))
			{
				return false;
			}
		}

		return observed.SetEquals(expectedPropertyNames);
	}

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Grok 連接續做帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateAttemptId(Guid attemptId)
	{
		if (attemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Grok 連接續做 attempt ID 不可為空。",
				nameof(attemptId));
		}
	}

	private static void ValidateStage(
		GrokConnectionPendingStage stage,
		Guid? publicBindingId)
	{
		if (!Enum.IsDefined(stage) ||
			(publicBindingId == Guid.Empty) ||
			((stage == GrokConnectionPendingStage.BindingPersisted) !=
				(publicBindingId is not null)))
		{
			throw new ArgumentException(
				"Grok 連接續做階段格式無效。",
				nameof(stage));
		}
	}

	private static void ValidateUtcTimestamp(
		DateTimeOffset timestamp,
		string parameterName)
	{
		if ((timestamp == default) || (timestamp.Offset != TimeSpan.Zero))
		{
			throw new ArgumentException(
				"Grok 連接續做時間必須是非預設 UTC 值。",
				parameterName);
		}
	}

	private static void EnsureExistingPathAncestorsAreNotReparsePoints(
		string path)
	{
		DirectoryInfo? current = new(Path.GetFullPath(path));
		while (current is not null)
		{
			if (current.Exists &&
				(current.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException(
					"Grok 連接續做路徑不可穿越 reparse point。");
			}

			current = current.Parent;
		}
	}
}
