using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.Infrastructure;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal sealed class JsonClaudeUsageSafetyStateStore :
	IClaudeUsageSafetyStateStore
{
	private sealed record SafetyStateDocument(
		int SchemaVersion,
		int AutomaticRevalidationAttemptCount,
		string? AccountIdentity,
		string FailureReason,
		ClaudeUsageSafetyRecoveryMode RecoveryMode,
		DateTimeOffset? RetryNotBefore,
		Guid? AttemptId,
		ClaudeUsageSafetyAttemptPhase? AttemptPhase,
		int? LegacySourceSchemaVersion,
		string? DiagnosticCliVersion);

	private sealed record ClearIntentDocument(
		int SchemaVersion,
		Guid AccountId);

	private sealed record TransitionIntentDocument(
		int SchemaVersion,
		Guid AccountId,
		int AutomaticRevalidationAttemptCount,
		string? AccountIdentity,
		string FailureReason,
		ClaudeUsageSafetyRecoveryMode RecoveryMode,
		DateTimeOffset? RetryNotBefore,
		Guid? AttemptId,
		ClaudeUsageSafetyAttemptPhase? AttemptPhase,
		int? LegacySourceSchemaVersion,
		string? DiagnosticCliVersion);

	private const int LegacySafetyStateSchemaVersion = 1;
	private const int AttemptProvenanceSafetyStateSchemaVersion = 2;
	private const int CurrentSafetyStateSchemaVersion = 3;
	private const int ClearIntentSchemaVersion = 1;
	private const string SafetyStateFileName = "usage-safety-v1.json";
	private const string ClearIntentFileSuffix = ".clear-pending-v1.json";
	private const string TransitionIntentFileSuffix =
		".transition-pending-v1.json";
	private const int MaximumAutomaticRevalidationBackoffStep = 3;
	private const int MaximumDocumentSizeBytes = 4 * 1024;
	private const int MaximumFailureReasonLength = 512;
	private const int WindowsLockViolation = 33;
	private const int WindowsSharingViolation = 32;
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> LegacyStatePropertyNames = new(
		new[]
		{
			"accountIdentity",
			"automaticRevalidationAttemptCount",
			"failureReason",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string>
		AttemptProvenanceStatePropertyNames = new(
		new[]
		{
			"accountIdentity",
			"attemptId",
			"attemptPhase",
			"automaticRevalidationAttemptCount",
			"failureReason",
			"legacySourceSchemaVersion",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> CurrentStatePropertyNames = new(
		new[]
		{
			"accountIdentity",
			"attemptId",
			"attemptPhase",
			"automaticRevalidationAttemptCount",
			"diagnosticCliVersion",
			"failureReason",
			"legacySourceSchemaVersion",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> ClearIntentPropertyNames = new(
		new[]
		{
			"accountId",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> LegacyTransitionIntentPropertyNames = new(
		new[]
		{
			"accountId",
			"accountIdentity",
			"automaticRevalidationAttemptCount",
			"failureReason",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string>
		AttemptProvenanceTransitionIntentPropertyNames = new(
		new[]
		{
			"accountId",
			"accountIdentity",
			"attemptId",
			"attemptPhase",
			"automaticRevalidationAttemptCount",
			"failureReason",
			"legacySourceSchemaVersion",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string>
		CurrentTransitionIntentPropertyNames = new(
		new[]
		{
			"accountId",
			"accountIdentity",
			"attemptId",
			"attemptPhase",
			"automaticRevalidationAttemptCount",
			"diagnosticCliVersion",
			"failureReason",
			"legacySourceSchemaVersion",
			"recoveryMode",
			"retryNotBefore",
			"schemaVersion"
		},
		StringComparer.Ordinal);
	private static readonly TimeSpan[] TransientRetryDelays =
	[
		TimeSpan.FromMilliseconds(100),
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromMilliseconds(500)
	];
	private static readonly JsonSerializerOptions SerializerOptions =
		CreateSerializerOptions();
	private readonly Func<Guid, string> _filePathFactory;

	internal JsonClaudeUsageSafetyStateStore(
		Func<Guid, string> filePathFactory)
	{
		_filePathFactory = filePathFactory ??
			throw new ArgumentNullException(nameof(filePathFactory));
	}

	public async Task<ClaudeUsageSafetyState?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			await CleanupOrphanedTemporaryFilesAsync(
				filePath,
				cancellationToken).ConfigureAwait(false);

			if (await LoadClearIntentCoreAsync(
					GetClearIntentFilePath(filePath),
					accountId,
					cancellationToken).ConfigureAwait(false))
			{
				await TryCompleteClearIntentAsync(
					filePath,
					cancellationToken).ConfigureAwait(false);
				return null;
			}

			string transitionIntentFilePath =
				GetTransitionIntentFilePath(filePath);
			ClaudeUsageSafetyState? transitionState =
				await LoadTransitionIntentCoreAsync(
					transitionIntentFilePath,
					accountId,
					cancellationToken).ConfigureAwait(false);
			if (transitionState is not null)
			{
				await TryCompleteTransitionIntentAsync(
					filePath,
					transitionIntentFilePath,
					transitionState,
					cancellationToken).ConfigureAwait(false);
				return transitionState;
			}

			return await RunWithTransientRetryAsync(
				() => LoadCoreAsync(filePath, cancellationToken),
				cancellationToken).ConfigureAwait(false);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態 JSON 格式無效。",
				exception);
		}
	}

	public async Task SaveAsync(
		Guid accountId,
		ClaudeUsageSafetyState state,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(state);
		ValidateState(state);
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		await CleanupOrphanedTemporaryFilesAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		bool hasClearIntent = await LoadClearIntentCoreAsync(
			GetClearIntentFilePath(filePath),
			accountId,
			cancellationToken).ConfigureAwait(false);

		if (hasClearIntent)
		{
			if (!await TryCompleteClearIntentAsync(
					filePath,
					cancellationToken).ConfigureAwait(false))
			{
				throw new ClaudeUsageSafetyStateCleanupPendingException(
					"Claude 用量安全狀態清除標記仍待背景完成。");
			}
		}

		string transitionIntentFilePath =
			GetTransitionIntentFilePath(filePath);
		if (state.RecoveryMode == ClaudeUsageSafetyRecoveryMode.AttemptInProgress)
		{
			ClaudeUsageSafetyState? pendingTransition =
				await LoadTransitionIntentCoreAsync(
					transitionIntentFilePath,
					accountId,
					cancellationToken).ConfigureAwait(false);
			if (pendingTransition is not null &&
				!await TryCompleteTransitionIntentAsync(
					filePath,
					transitionIntentFilePath,
					pendingTransition,
					cancellationToken).ConfigureAwait(false))
			{
				throw new ClaudeUsageSafetyStateCleanupPendingException(
					"Claude 用量安全狀態轉換標記仍待背景完成。");
			}

			await RunWithTransientRetryAsync(
				async () =>
				{
					await SaveCoreAsync(filePath, state, cancellationToken)
						.ConfigureAwait(false);
					return true;
				},
				cancellationToken).ConfigureAwait(false);
			return;
		}

		await RunWithTransientRetryAsync(
			async () =>
			{
				await SaveTransitionIntentCoreAsync(
					transitionIntentFilePath,
					accountId,
					state,
					cancellationToken).ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);

		// Once the write-ahead intent is durable, this state is logically
		// committed. LoadAsync honors it even if primary-file cleanup is delayed.
		await TryCompleteTransitionIntentAsync(
			filePath,
			transitionIntentFilePath,
			state,
			CancellationToken.None).ConfigureAwait(false);
	}

	public async Task ClearAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		bool wereExistingTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		string clearIntentFilePath = GetClearIntentFilePath(filePath);
		await RunWithTransientRetryAsync(
			async () =>
			{
				await SaveClearIntentCoreAsync(
					clearIntentFilePath,
					accountId,
					cancellationToken).ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);

		// Once the marker is durable, the clear is logically committed. Physical
		// cleanup remains best effort and is retried by every later store access.
		bool wasClearIntentCompleted = await TryCompleteClearIntentAsync(
			filePath,
			CancellationToken.None).ConfigureAwait(false);
		bool wereRemainingTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesAsync(
			filePath,
			CancellationToken.None).ConfigureAwait(false);

		if (!wasClearIntentCompleted ||
			!wereExistingTemporaryFilesRemoved ||
			!wereRemainingTemporaryFilesRemoved)
		{
			throw new ClaudeUsageSafetyStateCleanupPendingException(
				"Claude 用量安全狀態已邏輯清除，但本機檔案仍待背景重試。");
		}
	}

	internal async Task RecoverPendingCleanupAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		bool wereTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesAsync(
				filePath,
				cancellationToken).ConfigureAwait(false);
		bool wasClearIntentCompleted = true;

		if (await LoadClearIntentCoreAsync(
				GetClearIntentFilePath(filePath),
				accountId,
				cancellationToken).ConfigureAwait(false))
		{
			wasClearIntentCompleted = await TryCompleteClearIntentAsync(
				filePath,
				cancellationToken).ConfigureAwait(false);
		}

		string transitionIntentFilePath =
			GetTransitionIntentFilePath(filePath);
		ClaudeUsageSafetyState? transitionState =
			await LoadTransitionIntentCoreAsync(
				transitionIntentFilePath,
				accountId,
				cancellationToken).ConfigureAwait(false);
		bool wasTransitionIntentCompleted = transitionState is null ||
			await TryCompleteTransitionIntentAsync(
				filePath,
				transitionIntentFilePath,
				transitionState,
				cancellationToken).ConfigureAwait(false);

		if (!wereTemporaryFilesRemoved ||
			!wasClearIntentCompleted ||
			!wasTransitionIntentCompleted)
		{
			throw new ClaudeUsageSafetyStateCleanupPendingException(
				"Claude 用量安全狀態檔案仍待背景清理。");
		}
	}

	internal static IReadOnlyList<Guid> DiscoverAccountIdsForRecovery(
		string accountRootDirectoryPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountRootDirectoryPath);
		string normalizedRootPath = Path.GetFullPath(accountRootDirectoryPath);
		EnsureExistingPathAncestorsAreNotReparsePoints(normalizedRootPath);

		if (!Directory.Exists(normalizedRootPath))
		{
			return [];
		}

		List<Guid> accountIds = [];

		foreach (string candidatePath in Directory.EnumerateFileSystemEntries(
			normalizedRootPath,
			searchPattern: "*",
			SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string candidateName = Path.GetFileName(candidatePath);

			if (!Guid.TryParseExact(candidateName, "N", out Guid accountId))
			{
				continue;
			}

			FileAttributes attributes = File.GetAttributes(candidatePath);

			if ((attributes & FileAttributes.Directory) == 0 ||
				(attributes & FileAttributes.ReparsePoint) != 0)
			{
				continue;
			}

			if (!ContainsRecoveryArtifact(candidatePath, cancellationToken))
			{
				continue;
			}

			accountIds.Add(accountId);
		}

		return accountIds;
	}

	private static bool ContainsRecoveryArtifact(
		string accountDirectoryPath,
		CancellationToken cancellationToken)
	{
		string clearIntentFileName =
			SafetyStateFileName + ClearIntentFileSuffix;
		string transitionIntentFileName =
			SafetyStateFileName + TransitionIntentFileSuffix;

		foreach (string candidatePath in Directory.EnumerateFileSystemEntries(
			accountDirectoryPath,
			searchPattern: "*",
			SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			FileAttributes attributes = File.GetAttributes(candidatePath);
			if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				continue;
			}

			string candidateFileName = Path.GetFileName(candidatePath);
			if (string.Equals(
					candidateFileName,
					SafetyStateFileName,
					StringComparison.OrdinalIgnoreCase) ||
				string.Equals(
					candidateFileName,
					clearIntentFileName,
					StringComparison.OrdinalIgnoreCase) ||
				string.Equals(
					candidateFileName,
					transitionIntentFileName,
					StringComparison.OrdinalIgnoreCase) ||
				IsOrphanedTemporaryFileName(
					candidateFileName,
					SafetyStateFileName) ||
				IsOrphanedTemporaryFileName(
					candidateFileName,
					clearIntentFileName) ||
				IsOrphanedTemporaryFileName(
					candidateFileName,
					transitionIntentFileName))
			{
				return true;
			}
		}

		return false;
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

	private string GetFilePath(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Claude 帳號識別碼不可為空。",
				nameof(accountId));
		}

		return Path.GetFullPath(_filePathFactory(accountId));
	}

	private static string GetClearIntentFilePath(string filePath)
	{
		return filePath + ClearIntentFileSuffix;
	}

	private static string GetTransitionIntentFilePath(string filePath)
	{
		return filePath + TransitionIntentFileSuffix;
	}

	private static bool HasExactProperties(
		JsonElement root,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> observedNames = new(StringComparer.Ordinal);

		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!expectedPropertyNames.Contains(property.Name) ||
				!observedNames.Add(property.Name))
			{
				return false;
			}
		}

		return observedNames.SetEquals(expectedPropertyNames);
	}

	private static int ReadSafetyStateSchemaVersion(
		JsonElement root,
		string invalidVersionMessage)
	{
		if ((root.ValueKind != JsonValueKind.Object) ||
			!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
			(schemaVersion.ValueKind != JsonValueKind.Number) ||
			!schemaVersion.TryGetInt32(out int value))
		{
			throw new InvalidDataException(invalidVersionMessage);
		}

		return value;
	}

	private static bool IsTransientFileContention(IOException exception)
	{
		int nativeErrorCode = exception.HResult & 0xFFFF;
		return nativeErrorCode is
			WindowsSharingViolation or WindowsLockViolation;
	}

	private static async Task<ClaudeUsageSafetyState?> LoadCoreAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		byte[]? fileContents = await ReadDocumentAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		return fileContents is null
			? null
			: ParseState(fileContents);
	}

	private static async Task<bool> LoadClearIntentCoreAsync(
		string clearIntentFilePath,
		Guid accountId,
		CancellationToken cancellationToken)
	{
		byte[]? fileContents = await RunWithTransientRetryAsync(
			() => ReadDocumentAsync(clearIntentFilePath, cancellationToken),
			cancellationToken).ConfigureAwait(false);

		if (fileContents is null)
		{
			return false;
		}

		try
		{
			using JsonDocument parsedDocument = JsonDocument.Parse(
				fileContents,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8
				});

			if (!HasExactProperties(
				parsedDocument.RootElement,
				ClearIntentPropertyNames))
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態清除標記欄位無效。");
			}

			ClearIntentDocument? document =
				JsonSerializer.Deserialize<ClearIntentDocument>(
					fileContents,
					SerializerOptions);

			if ((document is null) ||
				(document.SchemaVersion != ClearIntentSchemaVersion) ||
				(document.AccountId == Guid.Empty) ||
				(document.AccountId != accountId))
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態清除標記內容無效。");
			}

			return true;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態清除標記 JSON 格式無效。",
				exception);
		}
	}

	private static async Task<ClaudeUsageSafetyState?>
		LoadTransitionIntentCoreAsync(
			string transitionIntentFilePath,
			Guid accountId,
			CancellationToken cancellationToken)
	{
		byte[]? fileContents = await RunWithTransientRetryAsync(
			() => ReadDocumentAsync(
				transitionIntentFilePath,
				cancellationToken),
			cancellationToken).ConfigureAwait(false);

		if (fileContents is null)
		{
			return null;
		}

		try
		{
			using JsonDocument parsedDocument = JsonDocument.Parse(
				fileContents,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8
				});

			int schemaVersion = ReadSafetyStateSchemaVersion(
				parsedDocument.RootElement,
				"Claude 用量安全狀態轉換標記版本無效。");
			IReadOnlySet<string> expectedPropertyNames = schemaVersion switch
			{
				LegacySafetyStateSchemaVersion =>
					LegacyTransitionIntentPropertyNames,
				AttemptProvenanceSafetyStateSchemaVersion =>
					AttemptProvenanceTransitionIntentPropertyNames,
				CurrentSafetyStateSchemaVersion =>
					CurrentTransitionIntentPropertyNames,
				_ => throw new InvalidDataException(
					"Claude 用量安全狀態轉換標記版本無效。")
			};

			if (!HasExactProperties(
					parsedDocument.RootElement,
					expectedPropertyNames))
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態轉換標記欄位無效。");
			}

			TransitionIntentDocument? document =
				JsonSerializer.Deserialize<TransitionIntentDocument>(
					fileContents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != schemaVersion) ||
				(document.AccountId == Guid.Empty) ||
				(document.AccountId != accountId))
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態轉換標記內容無效。");
			}

			ClaudeUsageSafetyState state = new(
				document.AutomaticRevalidationAttemptCount,
				document.AccountIdentity,
				document.FailureReason,
				document.RecoveryMode,
				document.RetryNotBefore,
				document.AttemptId,
				document.AttemptPhase,
				LegacySourceSchemaVersion:
					schemaVersion == LegacySafetyStateSchemaVersion
						? LegacySafetyStateSchemaVersion
						: document.LegacySourceSchemaVersion,
				DiagnosticCliVersion:
					schemaVersion == CurrentSafetyStateSchemaVersion
						? document.DiagnosticCliVersion
						: null);
			try
			{
				ValidateState(state);
			}
			catch (ArgumentException exception)
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態轉換標記內容無效。",
					exception);
			}

			if (state.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.AttemptInProgress)
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態轉換標記不可表示執行中作業。");
			}

			return state;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態轉換標記 JSON 格式無效。",
				exception);
		}
	}

	private static async Task<byte[]?> ReadDocumentAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		FileStream stream;

		try
		{
			stream = new FileStream(
				filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}

		await using (stream)
		{
			if (stream.Length is <= 0 or > MaximumDocumentSizeBytes)
			{
				throw new InvalidDataException(
					"Claude 用量安全狀態檔大小無效。");
			}

			byte[] fileContents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(fileContents, cancellationToken)
				.ConfigureAwait(false);
			return fileContents;
		}
	}

	private static ClaudeUsageSafetyState ParseState(byte[] fileContents)
	{
		using JsonDocument parsedDocument = JsonDocument.Parse(
			fileContents,
			new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 8
			});

		int schemaVersion = ReadSafetyStateSchemaVersion(
			parsedDocument.RootElement,
			"Claude 用量安全狀態版本無效。");
		IReadOnlySet<string> expectedPropertyNames = schemaVersion switch
		{
			LegacySafetyStateSchemaVersion => LegacyStatePropertyNames,
			AttemptProvenanceSafetyStateSchemaVersion =>
				AttemptProvenanceStatePropertyNames,
			CurrentSafetyStateSchemaVersion => CurrentStatePropertyNames,
			_ => throw new InvalidDataException(
				"Claude 用量安全狀態版本無效。")
		};

		if (!HasExactProperties(
				parsedDocument.RootElement,
				expectedPropertyNames))
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態欄位無效。");
		}

		SafetyStateDocument? document =
			JsonSerializer.Deserialize<SafetyStateDocument>(
				fileContents,
				SerializerOptions);

		if ((document is null) ||
			(document.SchemaVersion != schemaVersion))
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態版本無效。");
		}

		ClaudeUsageSafetyState state = new(
			document.AutomaticRevalidationAttemptCount,
			document.AccountIdentity,
			document.FailureReason,
			document.RecoveryMode,
			document.RetryNotBefore,
			document.AttemptId,
			document.AttemptPhase,
			LegacySourceSchemaVersion:
				schemaVersion == LegacySafetyStateSchemaVersion
					? LegacySafetyStateSchemaVersion
					: document.LegacySourceSchemaVersion,
			DiagnosticCliVersion:
				schemaVersion == CurrentSafetyStateSchemaVersion
					? document.DiagnosticCliVersion
					: null);

		try
		{
			ValidateState(state);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態內容無效。",
				exception);
		}

		return state;
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
			catch (IOException exception) when (
				(attempt < TransientRetryDelays.Length) &&
				IsTransientFileContention(exception))
			{
				await Task.Delay(
					TransientRetryDelays[attempt],
					cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static async Task<bool> CleanupOrphanedTemporaryFilesAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		bool wereStateTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesForTargetAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		bool wereClearIntentTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesForTargetAsync(
			GetClearIntentFilePath(filePath),
			cancellationToken).ConfigureAwait(false);
		bool wereTransitionIntentTemporaryFilesRemoved =
			await CleanupOrphanedTemporaryFilesForTargetAsync(
			GetTransitionIntentFilePath(filePath),
			cancellationToken).ConfigureAwait(false);
		return wereStateTemporaryFilesRemoved &&
			wereClearIntentTemporaryFilesRemoved &&
			wereTransitionIntentTemporaryFilesRemoved;
	}

	private static async Task<bool> CleanupOrphanedTemporaryFilesForTargetAsync(
		string targetFilePath,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(targetFilePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"Claude 用量安全狀態目錄無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);

		if (!Directory.Exists(directoryPath))
		{
			return true;
		}

		IEnumerable<string> candidates;
		bool wereAllCandidatesRemoved = true;

		try
		{
			candidates = Directory.EnumerateFiles(
				directoryPath,
				searchPattern: "*",
				SearchOption.TopDirectoryOnly);
		}
		catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
		{
			return false;
		}

		try
		{
			foreach (string candidate in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (!IsOrphanedTemporaryFileName(
						Path.GetFileName(candidate),
						Path.GetFileName(targetFilePath)))
				{
					continue;
				}

				try
				{
					FileAttributes attributes = File.GetAttributes(candidate);

					if (!CanDeleteOrphanedTemporaryFile(attributes))
					{
						wereAllCandidatesRemoved = false;
						continue;
					}

					await RunWithTransientRetryAsync(
						() =>
						{
							File.Delete(candidate);
							return Task.FromResult(true);
						},
						cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (
					IsRecoverableCleanupFailure(exception))
				{
					wereAllCandidatesRemoved = false;
					// A later load/save/clear operation will retry this exact file.
				}
			}
		}
		catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
		{
			return false;
		}

		return wereAllCandidatesRemoved;
	}

	internal static bool CanDeleteOrphanedTemporaryFile(
		FileAttributes attributes)
	{
		return (attributes & (
			FileAttributes.Directory |
			FileAttributes.ReparsePoint)) == 0;
	}

	private static void EnsureExistingPathAncestorsAreNotReparsePoints(
		string directoryPath)
	{
		DirectoryInfo? current = new(Path.GetFullPath(directoryPath));

		while (current is not null)
		{
			FileAttributes attributes;

			try
			{
				attributes = File.GetAttributes(current.FullName);
			}
			catch (Exception exception) when (
				exception is FileNotFoundException or
					DirectoryNotFoundException)
			{
				current = current.Parent;
				continue;
			}

			if ((attributes & FileAttributes.Directory) == 0)
			{
				throw new IOException(
					"Claude 用量安全狀態路徑包含非目錄祖先。");
			}

			if ((attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException(
					"Claude 用量安全狀態路徑不可穿越 reparse point。");
			}

			current = current.Parent;
		}
	}

	private static bool IsOrphanedTemporaryFileName(
		string candidateFileName,
		string targetFileName)
	{
		string prefix = $".{targetFileName}.";
		const string suffix = ".tmp";
		int attemptIdLength = candidateFileName.Length -
			prefix.Length -
			suffix.Length;
		return attemptIdLength == 32 &&
			candidateFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
			candidateFileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
			Guid.TryParseExact(
				candidateFileName.AsSpan(prefix.Length, attemptIdLength),
				"N",
				out _);
	}

	private static bool IsRecoverableCleanupFailure(Exception exception)
	{
		return (exception is IOException) ||
			(exception is UnauthorizedAccessException);
	}

	private static async Task<bool> TryDeleteFileAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		try
		{
			await RunWithTransientRetryAsync(
				() =>
				{
					File.Delete(filePath);
					return Task.FromResult(true);
				},
				cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
		{
			return false;
		}
	}

	private static async Task<bool> TryCompleteClearIntentAsync(
		string filePath,
		CancellationToken cancellationToken)
	{
		if (!await TryDeleteFileAsync(
				GetTransitionIntentFilePath(filePath),
				cancellationToken).ConfigureAwait(false))
		{
			return false;
		}

		if (!await TryDeleteFileAsync(
				filePath,
				cancellationToken).ConfigureAwait(false))
		{
			return false;
		}

		return await TryDeleteFileAsync(
			GetClearIntentFilePath(filePath),
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task<bool> TryCompleteTransitionIntentAsync(
		string filePath,
		string transitionIntentFilePath,
		ClaudeUsageSafetyState state,
		CancellationToken cancellationToken)
	{
		try
		{
			await RunWithTransientRetryAsync(
				async () =>
				{
					await SaveCoreAsync(filePath, state, cancellationToken)
						.ConfigureAwait(false);
					return true;
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
		{
			return false;
		}

		return await TryDeleteFileAsync(
			transitionIntentFilePath,
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task SaveClearIntentCoreAsync(
		string clearIntentFilePath,
		Guid accountId,
		CancellationToken cancellationToken)
	{
		ClearIntentDocument document = new(
			ClearIntentSchemaVersion,
			accountId);
		byte[] fileContents = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);
		await WriteFileAtomicallyAsync(
			clearIntentFilePath,
			fileContents,
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task SaveTransitionIntentCoreAsync(
		string transitionIntentFilePath,
		Guid accountId,
		ClaudeUsageSafetyState state,
		CancellationToken cancellationToken)
	{
		TransitionIntentDocument document = new(
			CurrentSafetyStateSchemaVersion,
			accountId,
			state.AutomaticRevalidationAttemptCount,
			state.AccountIdentity,
			state.FailureReason,
			state.RecoveryMode,
			state.RetryNotBefore,
			state.AttemptId,
			state.AttemptPhase,
			state.LegacySourceSchemaVersion,
			state.DiagnosticCliVersion);
		byte[] fileContents = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);
		await WriteFileAtomicallyAsync(
			transitionIntentFilePath,
			fileContents,
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task SaveCoreAsync(
		string filePath,
		ClaudeUsageSafetyState state,
		CancellationToken cancellationToken)
	{
		SafetyStateDocument document = new(
			CurrentSafetyStateSchemaVersion,
			state.AutomaticRevalidationAttemptCount,
			state.AccountIdentity,
			state.FailureReason,
			state.RecoveryMode,
			state.RetryNotBefore,
			state.AttemptId,
			state.AttemptPhase,
			state.LegacySourceSchemaVersion,
			state.DiagnosticCliVersion);
		byte[] fileContents = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);

		if (fileContents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態檔超過大小上限。");
		}

		await WriteFileAtomicallyAsync(
			filePath,
			fileContents,
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteFileAtomicallyAsync(
		string filePath,
		byte[] fileContents,
		CancellationToken cancellationToken)
	{
		if (fileContents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"Claude 用量安全狀態檔超過大小上限。");
		}

		string? directoryPath = Path.GetDirectoryName(filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"Claude 用量安全狀態目錄無效。");
		}

		Directory.CreateDirectory(directoryPath);
		string temporaryFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

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
				await stream.WriteAsync(fileContents, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
			}

			File.Move(
				temporaryFilePath,
				filePath,
				overwrite: true);
		}
		finally
		{
			try
			{
				File.Delete(temporaryFilePath);
			}
			catch
			{
				// A uniquely named temporary file is never loaded as safety state.
			}
		}
	}

	private static void ValidateState(ClaudeUsageSafetyState state)
	{
		if ((state.AutomaticRevalidationAttemptCount < 0) ||
			(state.AutomaticRevalidationAttemptCount >
				MaximumAutomaticRevalidationBackoffStep))
		{
			throw new ArgumentOutOfRangeException(nameof(state));
		}

		if (string.IsNullOrWhiteSpace(state.FailureReason) ||
			(state.FailureReason.Length > MaximumFailureReasonLength) ||
			!string.Equals(
				state.FailureReason,
				state.FailureReason.Trim(),
				StringComparison.Ordinal) ||
			state.FailureReason.Any(char.IsControl))
		{
			throw new ArgumentException(
				"Claude 用量安全失敗原因無效。",
				nameof(state));
		}

		if (!ProviderAccountIdentityRules.TryNormalize(
				state.AccountIdentity,
				out string? normalizedIdentity) ||
			!string.Equals(
				state.AccountIdentity,
				normalizedIdentity,
				StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"Claude 用量安全狀態帳號識別無效。",
				nameof(state));
		}

		if (!Enum.IsDefined(state.RecoveryMode) ||
			(state.RecoveryMode == ClaudeUsageSafetyRecoveryMode.Healthy))
		{
			throw new ArgumentOutOfRangeException(nameof(state));
		}

		if (state.DiagnosticCliVersion is not null &&
			(state.RecoveryMode ==
				ClaudeUsageSafetyRecoveryMode.AttemptInProgress ||
			 state.LegacySourceSchemaVersion is not null ||
			 !CliVersionPolicies.TryParseCanonicalThreePartVersion(
				state.DiagnosticCliVersion,
				out _)))
		{
			throw new ArgumentException(
				"Claude 用量安全狀態的 CLI 版本診斷無效。",
				nameof(state));
		}

		Guid? attemptId = state.AttemptId;
		bool hasAttemptId = attemptId is not null;
		bool hasAttemptPhase = state.AttemptPhase is not null;
		bool isAttemptInProgress = state.RecoveryMode ==
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress;
		bool hasContainedAttemptMetadata =
			hasAttemptId &&
			state.AttemptPhase is
				ClaudeUsageSafetyAttemptPhase.Prepared or
				ClaudeUsageSafetyAttemptPhase.StartedContained;
		bool hasExplicitUncontainedAttemptMetadata =
			!hasAttemptId &&
			state.AttemptPhase == ClaudeUsageSafetyAttemptPhase.Uncontained;
		bool hasLegacyAttemptWithoutMetadata =
			!hasAttemptId &&
			!hasAttemptPhase &&
			state.LegacySourceSchemaVersion ==
				LegacySafetyStateSchemaVersion;

		if ((hasAttemptId && attemptId!.Value == Guid.Empty) ||
			(hasAttemptPhase && !Enum.IsDefined(state.AttemptPhase!.Value)) ||
			(state.LegacySourceSchemaVersion is not null and not
				LegacySafetyStateSchemaVersion) ||
			(state.LegacySourceSchemaVersion ==
				LegacySafetyStateSchemaVersion &&
				(hasAttemptId || hasAttemptPhase)) ||
			(isAttemptInProgress &&
				!hasContainedAttemptMetadata &&
				!hasExplicitUncontainedAttemptMetadata &&
				!hasLegacyAttemptWithoutMetadata) ||
			(!isAttemptInProgress && (hasAttemptId || hasAttemptPhase)))
		{
			throw new ArgumentException(
				"Claude 用量安全狀態的執行嘗試資訊無效。",
				nameof(state));
		}

		bool requiresRetryDeadline = state.RecoveryMode ==
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending;

		if (requiresRetryDeadline != (state.RetryNotBefore is not null) ||
			(state.RetryNotBefore is DateTimeOffset retryNotBefore &&
			 (retryNotBefore == default ||
			  retryNotBefore.Offset != TimeSpan.Zero)))
		{
			throw new ArgumentException(
				"Claude 用量安全狀態的重試時間無效。",
				nameof(state));
		}
	}
}
