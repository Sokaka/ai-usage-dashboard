using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Persistence;

internal sealed record AntigravityConnectionPendingWork(
	Guid AccountId,
	string ExpectedProfileIdentityFingerprint,
	bool CachePending,
	bool ProfilePending,
	Guid? SetupAttemptId = null)
{
	internal bool IsSetupPending => !CachePending && !ProfilePending;
}

internal interface IAntigravityConnectionPendingStore
{
	Task<IReadOnlyList<AntigravityConnectionPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default);

	Task BeginSetupAsync(
		Guid accountId,
		string expectedProfileIdentityFingerprint,
		Guid setupAttemptId,
		CancellationToken cancellationToken = default);

	Task UpsertAsync(
		Guid accountId,
		string expectedProfileIdentityFingerprint,
		CancellationToken cancellationToken = default);

	Task MarkCacheCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task MarkProfileCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task RemoveAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task<AntigravitySetupApprovalReceipt?> LoadSetupApprovalAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default);

	Task<AntigravitySetupAttemptState?> LoadSetupAttemptStateAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default);

	Task<bool> TryRemoveLaunchingSetupAttemptAsync(
		Guid setupAttemptId,
		AntigravitySetupAttemptState observedLaunchingState,
		CancellationToken cancellationToken = default);

	Task RemoveSetupApprovalAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default);

	Task RemoveSetupAttemptStateAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default);

	Task CleanupOrphanedSetupArtifactsAsync(
		IReadOnlySet<Guid> activeAttemptIds,
		CancellationToken cancellationToken = default);
}

internal sealed class JsonAntigravityConnectionPendingStore :
	IAntigravityConnectionPendingStore
{
	private sealed record PendingConnectionDocument(
		int SchemaVersion,
		IReadOnlyList<PendingConnectionEntry> PendingConnections);

	private sealed record PendingConnectionEntry(
		Guid AccountId,
		string ExpectedProfileIdentityFingerprint,
		bool CachePending,
		bool ProfilePending,
		Guid? SetupAttemptId = null);

	private const int CurrentSchemaVersion = 2;
	private const int LegacySchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 32 * 1024;
	private const int MaximumEntryCount = 256;
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[] { "pendingConnections", "schemaVersion" },
		StringComparer.Ordinal);
	private static readonly HashSet<string> EntryPropertyNames = new(
		new[]
		{
			"accountId",
			"cachePending",
			"expectedProfileIdentityFingerprint",
			"profilePending",
			"setupAttemptId"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> LegacyEntryPropertyNames = new(
		new[]
		{
			"accountId",
			"cachePending",
			"expectedProfileIdentityFingerprint",
			"profilePending"
		},
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 8,
		PropertyNameCaseInsensitive = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};
	private static readonly TimeSpan[] TransientRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private readonly string _filePath;
	private readonly AntigravitySetupApprovalReceiptStore _setupApprovalStore;
	private readonly AntigravitySetupAttemptStateStore _setupAttemptStateStore;

	internal JsonAntigravityConnectionPendingStore(
		string filePath,
		AntigravitySetupApprovalReceiptStore? setupApprovalStore = null,
		AntigravitySetupAttemptStateStore? setupAttemptStateStore = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
		_setupApprovalStore = setupApprovalStore ??
			AntigravitySetupApprovalReceiptStore.CreateDefault();
		_setupAttemptStateStore = setupAttemptStateStore ??
			AntigravitySetupAttemptStateStore.CreateDefault();
	}

	public async Task<IReadOnlyList<AntigravityConnectionPendingWork>> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		Dictionary<Guid, AntigravityConnectionPendingWork> pending =
			await RunWithTransientRetryAsync(
				() => LoadCoreAsync(cancellationToken),
				cancellationToken).ConfigureAwait(false);
		return pending.Values
			.OrderBy(work => work.AccountId)
			.ToArray();
	}

	public Task UpsertAsync(
		Guid accountId,
		string expectedProfileIdentityFingerprint,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		ValidateExpectedProfileIdentityFingerprint(
			expectedProfileIdentityFingerprint);
		return UpdateAsync(
			pending =>
			{
				pending.TryGetValue(
					accountId,
					out AntigravityConnectionPendingWork? existing);
				pending[accountId] =
					new AntigravityConnectionPendingWork(
						accountId,
						expectedProfileIdentityFingerprint,
						CachePending: true,
						ProfilePending: true,
						existing?.SetupAttemptId);
			},
			cancellationToken);
	}

	public Task BeginSetupAsync(
		Guid accountId,
		string expectedProfileIdentityFingerprint,
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		ValidateSetupAttemptId(setupAttemptId);
		ValidateExpectedProfileIdentityFingerprint(
			expectedProfileIdentityFingerprint);
		return UpdateAsync(
			pending => pending[accountId] =
				new AntigravityConnectionPendingWork(
					accountId,
					expectedProfileIdentityFingerprint,
					CachePending: false,
					ProfilePending: false,
					setupAttemptId),
			cancellationToken);
	}

	public Task<AntigravitySetupApprovalReceipt?> LoadSetupApprovalAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateSetupAttemptId(setupAttemptId);
		return _setupApprovalStore.ReadAsync(
			setupAttemptId,
			cancellationToken);
	}

	public Task RemoveSetupApprovalAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateSetupAttemptId(setupAttemptId);
		return _setupApprovalStore.RemoveAsync(
			setupAttemptId,
			cancellationToken);
	}

	public Task<AntigravitySetupAttemptState?> LoadSetupAttemptStateAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateSetupAttemptId(setupAttemptId);
		return _setupAttemptStateStore.ReadAsync(
			setupAttemptId,
			cancellationToken);
	}

	public Task<bool> TryRemoveLaunchingSetupAttemptAsync(
		Guid setupAttemptId,
		AntigravitySetupAttemptState observedLaunchingState,
		CancellationToken cancellationToken = default)
	{
		ValidateSetupAttemptId(setupAttemptId);
		return _setupAttemptStateStore.TryRemoveLaunchingAsync(
			setupAttemptId,
			observedLaunchingState,
			cancellationToken);
	}

	public Task RemoveSetupAttemptStateAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateSetupAttemptId(setupAttemptId);
		return _setupAttemptStateStore.RemoveAsync(
			setupAttemptId,
			cancellationToken);
	}

	public async Task CleanupOrphanedSetupArtifactsAsync(
		IReadOnlySet<Guid> activeAttemptIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(activeAttemptIds);
		HashSet<Guid> activeAttemptIdSnapshot = new(activeAttemptIds);
		if (activeAttemptIdSnapshot.Contains(Guid.Empty))
		{
			throw new ArgumentException(
				"Antigravity setup active attempt ID 不可為空。",
				nameof(activeAttemptIds));
		}

		await Task.WhenAll(
			_setupApprovalStore.RemoveUnreferencedAsync(
				activeAttemptIdSnapshot,
				cancellationToken),
			_setupAttemptStateStore.RemoveUnreferencedAsync(
				activeAttemptIdSnapshot,
				cancellationToken)).ConfigureAwait(false);
	}

	public Task MarkCacheCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return MarkComponentCompletedAsync(
			accountId,
			isCacheComponent: true,
			cancellationToken);
	}

	public Task MarkProfileCompletedAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		return MarkComponentCompletedAsync(
			accountId,
			isCacheComponent: false,
			cancellationToken);
	}

	public Task RemoveAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		return UpdateAsync(
			pending => pending.Remove(accountId),
			cancellationToken);
	}

	private Task MarkComponentCompletedAsync(
		Guid accountId,
		bool isCacheComponent,
		CancellationToken cancellationToken)
	{
		ValidateAccountId(accountId);
		return UpdateAsync(
			pending =>
			{
				if (!pending.TryGetValue(
						accountId,
						out AntigravityConnectionPendingWork? work))
				{
					return;
				}

				if (work.IsSetupPending)
				{
					throw new InvalidOperationException(
						"Antigravity setup 尚未確認完成，不能先完成後續階段。");
				}

				AntigravityConnectionPendingWork updated = isCacheComponent
					? work with { CachePending = false }
					: work with { ProfilePending = false };
				if (!updated.CachePending && !updated.ProfilePending)
				{
					pending.Remove(accountId);
				}
				else
				{
					pending[accountId] = updated;
				}
			},
			cancellationToken);
	}

	private async Task UpdateAsync(
		Action<Dictionary<Guid, AntigravityConnectionPendingWork>> update,
		CancellationToken cancellationToken)
	{
		using IDisposable lease = await FileGates.EnterAsync(
			_filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		await RunWithTransientRetryAsync(
			async () =>
			{
				Dictionary<Guid, AntigravityConnectionPendingWork> pending =
					await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
				update(pending);
				await WriteCoreAsync(pending.Values, cancellationToken)
					.ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<Dictionary<Guid, AntigravityConnectionPendingWork>>
		LoadCoreAsync(CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"Antigravity 連接續做檔路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		FileStream stream;
		try
		{
			FileAttributes attributes = File.GetAttributes(_filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("Antigravity 連接續做檔類型無效。");
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
				throw new InvalidDataException(
					"Antigravity 連接續做檔大小無效。");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			return ParseDocument(contents);
		}
	}

	private static Dictionary<Guid, AntigravityConnectionPendingWork>
		ParseDocument(byte[] contents)
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
					"pendingConnections",
					out JsonElement entries) ||
				(entries.ValueKind != JsonValueKind.Array) ||
				(entries.GetArrayLength() > MaximumEntryCount))
			{
				throw new InvalidDataException(
					"Antigravity 連接續做檔結構無效。");
			}

			if (!root.TryGetProperty(
					"schemaVersion",
					out JsonElement schemaVersionElement) ||
				(schemaVersionElement.ValueKind != JsonValueKind.Number) ||
				!schemaVersionElement.TryGetInt32(out int schemaVersion) ||
				(schemaVersion is not CurrentSchemaVersion and
					not LegacySchemaVersion))
			{
				throw new InvalidDataException(
					"Antigravity 連接續做檔版本無效。");
			}

			IReadOnlySet<string> expectedEntryPropertyNames =
				schemaVersion == LegacySchemaVersion
					? LegacyEntryPropertyNames
					: EntryPropertyNames;
			foreach (JsonElement entry in entries.EnumerateArray())
			{
				if (!HasExactProperties(
						entry,
						expectedEntryPropertyNames))
				{
					throw new InvalidDataException(
						"Antigravity 連接續做項目結構無效。");
				}
			}

			PendingConnectionDocument? document =
				JsonSerializer.Deserialize<PendingConnectionDocument>(
					contents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != schemaVersion) ||
				(document.PendingConnections is null) ||
				(document.PendingConnections.Count > MaximumEntryCount))
			{
				throw new InvalidDataException(
					"Antigravity 連接續做檔版本或內容無效。");
			}

			Dictionary<Guid, AntigravityConnectionPendingWork> result = new();
			foreach (PendingConnectionEntry entry in document.PendingConnections)
			{
				ValidateAccountId(entry.AccountId);
				ValidateExpectedProfileIdentityFingerprint(
					entry.ExpectedProfileIdentityFingerprint);
				if (entry.SetupAttemptId == Guid.Empty)
				{
					throw new InvalidDataException(
						"Antigravity setup attempt ID 無效。");
				}

				bool isSetupPending =
					!entry.CachePending && !entry.ProfilePending;
				if ((schemaVersion == CurrentSchemaVersion) &&
					isSetupPending &&
					(entry.SetupAttemptId is null))
				{
					throw new InvalidDataException(
						"Antigravity setup intent 缺少 attempt ID。");
				}

				if (!result.TryAdd(
						entry.AccountId,
						new AntigravityConnectionPendingWork(
							entry.AccountId,
							entry.ExpectedProfileIdentityFingerprint,
							entry.CachePending,
							entry.ProfilePending,
							entry.SetupAttemptId)))
				{
					throw new InvalidDataException(
						"Antigravity 連接續做項目內容無效。");
				}
			}

			return result;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Antigravity 連接續做 JSON 格式無效。",
				exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"Antigravity 連接續做內容無效。",
				exception);
		}
	}

	private async Task WriteCoreAsync(
		IEnumerable<AntigravityConnectionPendingWork> pending,
		CancellationToken cancellationToken)
	{
		PendingConnectionEntry[] entries = pending
			.OrderBy(work => work.AccountId)
			.Select(work => new PendingConnectionEntry(
				work.AccountId,
				work.ExpectedProfileIdentityFingerprint,
				work.CachePending,
				work.ProfilePending,
				work.SetupAttemptId))
			.ToArray();
		if (entries.Length > MaximumEntryCount)
		{
			throw new InvalidOperationException(
				"Antigravity 連接續做項目超過安全上限。");
		}

		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new PendingConnectionDocument(CurrentSchemaVersion, entries),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidOperationException(
				"Antigravity 連接續做檔超過大小上限。");
		}

		string? directoryPath = Path.GetDirectoryName(_filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"Antigravity 連接續做檔路徑無效。");
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
				throw new IOException("Antigravity 連接續做檔類型無效。");
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
				// Unique temporary files are never loaded as pending work.
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
				"Antigravity 連接續做帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateSetupAttemptId(Guid setupAttemptId)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}
	}

	private static void ValidateExpectedProfileIdentityFingerprint(
		string fingerprint)
	{
		if ((fingerprint is null) ||
			(fingerprint.Length != 64) ||
			fingerprint.Any(character =>
				!char.IsAsciiHexDigit(character) || char.IsLower(character)))
		{
			throw new ArgumentException(
				"Antigravity 連接續做身分指紋格式無效。",
				nameof(fingerprint));
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
					"Antigravity 連接續做路徑不可穿越 reparse point。");
			}

			current = current.Parent;
		}
	}

	private static bool IsTransientFileFailure(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException;
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
}
