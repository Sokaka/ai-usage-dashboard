using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.Infrastructure;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal sealed record AccountCleanupPendingWork(
	Guid AccountId,
	ProviderKind Provider,
	bool CachePending,
	bool RuntimePending,
	bool AllowRetainedPrivateStateDeletion = false);

internal interface IAccountCleanupPendingStore
{
	Task<IReadOnlyList<AccountCleanupPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default);

	Task UpsertAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default);

	Task AuthorizeRetainedPrivateStateDeletionAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default);

	Task AuthorizeRetainedPrivateStateDeletionForCommittedAccountsAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default);

	Task<AccountCleanupPendingWork> UpsertCacheCleanupAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);

	Task MarkCacheCompletedAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);

	Task MarkRuntimeCompletedAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default);
}

internal sealed class JsonAccountCleanupPendingStore :
	IAccountCleanupPendingStore
{
	private sealed record PendingCleanupDocument(
		int SchemaVersion,
		IReadOnlyList<PendingCleanupEntry> PendingCleanups);

	private sealed record PendingCleanupEntry(
		Guid AccountId,
		ProviderKind Provider,
		bool CachePending,
		bool RuntimePending,
		bool AllowRetainedPrivateStateDeletion = false);

	private const int CurrentSchemaVersion = 2;
	private const int MinimumSupportedSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 128 * 1024;
	private const int MaximumEntryCount = 512;
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[] { "pendingCleanups", "schemaVersion" },
		StringComparer.Ordinal);
	private static readonly HashSet<string> Version1EntryPropertyNames = new(
		new[]
		{
			"accountId",
			"cachePending",
			"provider",
			"runtimePending"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> Version2EntryPropertyNames = new(
		Version1EntryPropertyNames.Append(
			"allowRetainedPrivateStateDeletion"),
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions =
		CreateSerializerOptions();
	private static readonly TimeSpan[] TransientRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private readonly string _filePath;

	internal JsonAccountCleanupPendingStore(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
	}

	public async Task<IReadOnlyList<AccountCleanupPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		Dictionary<(Guid AccountId, ProviderKind Provider),
			AccountCleanupPendingWork> pending =
			await RunWithTransientRetryAsync(
				() => LoadCoreAsync(cancellationToken),
				cancellationToken).ConfigureAwait(false);
		return pending.Values
			.OrderBy(item => item.Provider)
			.ThenBy(item => item.AccountId)
			.ToArray();
	}

	public async Task UpsertAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(accountKeys);
		if (accountKeys.Count == 0)
		{
			return;
		}

		foreach ((Guid accountId, ProviderKind provider) in accountKeys)
		{
			ValidateKey(accountId, provider);
		}

		await UpdateAsync(
			pending =>
			{
				foreach ((Guid accountId, ProviderKind provider) in accountKeys)
				{
					bool preserveRetainedPrivateStateDeletionAuthorization =
						pending.TryGetValue(
							(accountId, provider),
							out AccountCleanupPendingWork? existing) &&
						existing.RuntimePending &&
						existing.AllowRetainedPrivateStateDeletion;
					pending[(accountId, provider)] = new AccountCleanupPendingWork(
						accountId,
						provider,
						CachePending: true,
						RuntimePending: true,
						AllowRetainedPrivateStateDeletion:
							preserveRetainedPrivateStateDeletionAuthorization);
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task AuthorizeRetainedPrivateStateDeletionAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(accountKeys);
		if (accountKeys.Count == 0)
		{
			return;
		}

		foreach ((Guid accountId, ProviderKind provider) in accountKeys)
		{
			ValidateRetainedPrivateStateDeletionKey(accountId, provider);
		}

		await UpdateAsync(
			pending =>
			{
				foreach ((Guid accountId, ProviderKind provider) in accountKeys)
				{
					if (!pending.TryGetValue(
							(accountId, provider),
							out AccountCleanupPendingWork? work) ||
						!work.RuntimePending)
					{
						throw new InvalidOperationException(
							"只能授權仍待清理的 Copilot private state。");
					}
				}

				foreach ((Guid accountId, ProviderKind provider) in accountKeys)
				{
					pending[(accountId, provider)] = pending[(accountId, provider)] with
					{
						AllowRetainedPrivateStateDeletion = true
					};
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task AuthorizeRetainedPrivateStateDeletionForCommittedAccountsAsync(
		IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(accountKeys);
		if (accountKeys.Count == 0)
		{
			return;
		}

		foreach ((Guid accountId, ProviderKind provider) in accountKeys)
		{
			ValidateRetainedPrivateStateDeletionKey(accountId, provider);
		}

		HashSet<(Guid AccountId, ProviderKind Provider)> committedAccountKeys =
			accountKeys.ToHashSet();
		await UpdateAsync(
			pending =>
			{
				foreach (var key in pending.Keys
					.Where(key => committedAccountKeys.Contains(key))
					.ToArray())
				{
					AccountCleanupPendingWork work = pending[key];
					if (work.RuntimePending)
					{
						pending[key] = work with
						{
							AllowRetainedPrivateStateDeletion = true
						};
					}
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<AccountCleanupPendingWork> UpsertCacheCleanupAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		ValidateKey(accountId, provider);
		AccountCleanupPendingWork? updatedWork = null;
		await UpdateAsync(
			pending =>
			{
				(Guid AccountId, ProviderKind Provider) key =
					(accountId, provider);
				updatedWork = pending.TryGetValue(
						key,
						out AccountCleanupPendingWork? work)
					? work with { CachePending = true }
					: new AccountCleanupPendingWork(
						accountId,
						provider,
						CachePending: true,
						RuntimePending: false);
				pending[key] = updatedWork;
			},
			cancellationToken).ConfigureAwait(false);
		return updatedWork!;
	}

	public Task MarkCacheCompletedAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		return MarkComponentCompletedAsync(
			accountId,
			provider,
			isCacheComponent: true,
			cancellationToken);
	}

	public Task MarkRuntimeCompletedAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		return MarkComponentCompletedAsync(
			accountId,
			provider,
			isCacheComponent: false,
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
					"帳號清理工作路徑不可穿越 reparse point。");
			}

			current = current.Parent;
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

		HashSet<string> observedPropertyNames = new(StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expectedPropertyNames.Contains(property.Name) ||
				!observedPropertyNames.Add(property.Name))
			{
				return false;
			}
		}

		return observedPropertyNames.SetEquals(expectedPropertyNames);
	}

	private static bool IsTransientFileFailure(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException;
	}

	private async Task<Dictionary<
		(Guid AccountId, ProviderKind Provider),
		AccountCleanupPendingWork>> LoadCoreAsync(
			CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("帳號清理工作檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		FileStream stream;

		try
		{
			FileAttributes attributes = File.GetAttributes(_filePath);
			if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("帳號清理工作檔類型無效。");
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
				throw new InvalidDataException("帳號清理工作檔大小無效。");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			return ParseDocument(contents);
		}
	}

	private Task MarkComponentCompletedAsync(
		Guid accountId,
		ProviderKind provider,
		bool isCacheComponent,
		CancellationToken cancellationToken)
	{
		ValidateKey(accountId, provider);
		return UpdateAsync(
			pending =>
			{
				if (!pending.TryGetValue(
						(accountId, provider),
						out AccountCleanupPendingWork? work))
				{
					return;
				}

				AccountCleanupPendingWork updated = isCacheComponent
					? work with { CachePending = false }
					: work with
					{
						RuntimePending = false,
						AllowRetainedPrivateStateDeletion = false
					};
				if (!updated.CachePending && !updated.RuntimePending)
				{
					pending.Remove((accountId, provider));
				}
				else
				{
					pending[(accountId, provider)] = updated;
				}
			},
			cancellationToken);
	}

	private static Dictionary<
		(Guid AccountId, ProviderKind Provider),
		AccountCleanupPendingWork> ParseDocument(byte[] contents)
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
				!root.TryGetProperty("pendingCleanups", out JsonElement entries) ||
				(entries.ValueKind != JsonValueKind.Array) ||
				(entries.GetArrayLength() > MaximumEntryCount))
			{
				throw new InvalidDataException("帳號清理工作檔結構無效。");
			}

			if (!root.TryGetProperty(
					"schemaVersion",
					out JsonElement schemaVersionElement) ||
				(schemaVersionElement.ValueKind != JsonValueKind.Number) ||
				!schemaVersionElement.TryGetInt32(out int schemaVersion) ||
				(schemaVersion < MinimumSupportedSchemaVersion) ||
				(schemaVersion > CurrentSchemaVersion))
			{
				throw new InvalidDataException("帳號清理工作檔版本或內容無效。");
			}

			IReadOnlySet<string> entryPropertyNames = schemaVersion == 1
				? Version1EntryPropertyNames
				: Version2EntryPropertyNames;

			foreach (JsonElement entry in entries.EnumerateArray())
			{
				if (!HasExactProperties(entry, entryPropertyNames))
				{
					throw new InvalidDataException("帳號清理工作項目結構無效。");
				}
			}

			PendingCleanupDocument? document =
				JsonSerializer.Deserialize<PendingCleanupDocument>(
					contents,
					SerializerOptions);
			if (document is null ||
				(document.SchemaVersion != schemaVersion) ||
				(document.PendingCleanups is null) ||
				(document.PendingCleanups.Count > MaximumEntryCount))
			{
				throw new InvalidDataException("帳號清理工作檔版本或內容無效。");
			}

			Dictionary<(Guid, ProviderKind), AccountCleanupPendingWork> result = new();
			foreach (PendingCleanupEntry entry in document.PendingCleanups)
			{
				ValidateKey(entry.AccountId, entry.Provider);
				if (!entry.CachePending && !entry.RuntimePending)
				{
					throw new InvalidDataException("帳號清理工作不可為空。");
				}
				if (entry.AllowRetainedPrivateStateDeletion &&
					((entry.Provider != ProviderKind.Copilot) ||
						!entry.RuntimePending))
				{
					throw new InvalidDataException(
						"帳號清理工作的 retained private state 授權無效。");
				}

				var key = (entry.AccountId, entry.Provider);
				if (result.TryGetValue(key, out AccountCleanupPendingWork? existing))
				{
					result[key] = existing with
					{
						CachePending = existing.CachePending || entry.CachePending,
						RuntimePending = existing.RuntimePending || entry.RuntimePending,
						AllowRetainedPrivateStateDeletion =
							existing.AllowRetainedPrivateStateDeletion ||
							entry.AllowRetainedPrivateStateDeletion
					};
				}
				else
				{
					result.Add(key, new AccountCleanupPendingWork(
						entry.AccountId,
						entry.Provider,
						entry.CachePending,
						entry.RuntimePending,
						entry.AllowRetainedPrivateStateDeletion));
				}
			}

			return result;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("帳號清理工作 JSON 格式無效。", exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException("帳號清理工作內容無效。", exception);
		}
	}

	private static async Task<T> RunWithTransientRetryAsync<T>(
		Func<Task<T>> operation,
		CancellationToken cancellationToken)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				return await operation().ConfigureAwait(false);
			}
			catch (Exception exception) when (
				(attempt < TransientRetryDelays.Length) &&
				IsTransientFileFailure(exception))
			{
				await Task.Delay(
					TransientRetryDelays[attempt],
					cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private async Task UpdateAsync(
		Action<Dictionary<
			(Guid AccountId, ProviderKind Provider),
			AccountCleanupPendingWork>> update,
		CancellationToken cancellationToken)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		await RunWithTransientRetryAsync(
			async () =>
			{
				Dictionary<(Guid AccountId, ProviderKind Provider),
					AccountCleanupPendingWork> pending =
					await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
				update(pending);
				await WriteCoreAsync(pending.Values, cancellationToken)
					.ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	private static void ValidateKey(Guid accountId, ProviderKind provider)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號清理工作的帳號識別碼不可為空。", nameof(accountId));
		}

		if (!Enum.IsDefined(provider))
		{
			throw new ArgumentOutOfRangeException(nameof(provider));
		}
	}

	private static void ValidateRetainedPrivateStateDeletionKey(
		Guid accountId,
		ProviderKind provider)
	{
		ValidateKey(accountId, provider);
		if (provider != ProviderKind.Copilot)
		{
			throw new ArgumentException(
				"只有 Copilot cleanup intent 可授權刪除 retained private state。",
				nameof(provider));
		}
	}

	private async Task WriteCoreAsync(
		IEnumerable<AccountCleanupPendingWork> pending,
		CancellationToken cancellationToken)
	{
		PendingCleanupEntry[] entries = pending
			.OrderBy(item => item.Provider)
			.ThenBy(item => item.AccountId)
			.Select(item => new PendingCleanupEntry(
				item.AccountId,
				item.Provider,
				item.CachePending,
				item.RuntimePending,
				item.AllowRetainedPrivateStateDeletion))
			.ToArray();
		if (entries.Length > MaximumEntryCount)
		{
			throw new InvalidOperationException("帳號清理工作數量超過安全上限。");
		}

		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new PendingCleanupDocument(CurrentSchemaVersion, entries),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidOperationException("帳號清理工作檔超過大小上限。");
		}

		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("帳號清理工作檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		Directory.CreateDirectory(directoryPath);
		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		if (File.Exists(_filePath))
		{
			FileAttributes attributes = File.GetAttributes(_filePath);
			if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("帳號清理工作檔類型無效。");
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
			catch
			{
				// Unique temporary files are never loaded as cleanup work.
			}
		}
	}
}
