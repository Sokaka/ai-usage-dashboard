using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

public enum AntigravitySetupAttemptPhase
{
	Launching = 1,
	Active = 2,
	ApprovalRequested = 3
}

public sealed record AntigravitySetupProcessIdentity(
	int ProcessId,
	long ProcessStartTimeUtcTicks)
{
	public static AntigravitySetupProcessIdentity CaptureCurrent()
	{
		using Process process = Process.GetCurrentProcess();
		return new AntigravitySetupProcessIdentity(
			process.Id,
			process.StartTime.ToUniversalTime().Ticks);
	}
}

public sealed record AntigravitySetupAttemptState(
	Guid AttemptId,
	AntigravitySetupAttemptPhase Phase,
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	long CreatedAtUtcTicks,
	AntigravityMachineSetupSourceKind? SourceKind = null,
	string? TargetIdentityFingerprint = null);

public sealed class AntigravitySetupAttemptStateStore
{
	private sealed record AttemptStateDocument(
		int SchemaVersion,
		Guid AttemptId,
		AntigravitySetupAttemptPhase Phase,
		int ProcessId,
		long ProcessStartTimeUtcTicks,
		long CreatedAtUtcTicks,
		AntigravityMachineSetupSourceKind? SourceKind,
		string? TargetIdentityFingerprint);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 4 * 1024;
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string AntigravityDirectoryName = "antigravity";
	private const string StateDirectoryName = "setup-attempt-states-v1";
	private const string StoreLockFileName = ".store.lock";
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[]
		{
			"attemptId",
			"createdAtUtcTicks",
			"phase",
			"processId",
			"processStartTimeUtcTicks",
			"schemaVersion",
			"sourceKind",
			"targetIdentityFingerprint"
		},
		StringComparer.Ordinal);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 4,
		PropertyNameCaseInsensitive = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};
	private static readonly TimeSpan[] LockRetryDelays =
	[
		TimeSpan.FromMilliseconds(25),
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(100),
		TimeSpan.FromMilliseconds(200),
		TimeSpan.FromMilliseconds(400),
		TimeSpan.FromMilliseconds(800)
	];
	private static readonly TimeSpan[] TransientRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private readonly string _directoryPath;

	public AntigravitySetupAttemptStateStore(string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
		_directoryPath = Path.GetFullPath(directoryPath);
	}

	public static AntigravitySetupAttemptStateStore CreateDefault()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException(
				"無法取得 Antigravity setup attempt state 資料夾。");
		}

		return new AntigravitySetupAttemptStateStore(Path.Combine(
			localApplicationData,
			ApplicationDirectoryName,
			AntigravityDirectoryName,
			StateDirectoryName));
	}

	public Task BeginLaunchAsync(
		Guid attemptId,
		AntigravitySetupProcessIdentity parentProcess,
		DateTimeOffset createdAtUtc,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		ValidateProcessIdentity(parentProcess);
		long createdAtUtcTicks = createdAtUtc.ToUniversalTime().Ticks;
		ValidateUtcTicks(createdAtUtcTicks, nameof(createdAtUtc));
		AntigravitySetupAttemptState desired = new(
			attemptId,
			AntigravitySetupAttemptPhase.Launching,
			parentProcess.ProcessId,
			parentProcess.ProcessStartTimeUtcTicks,
			createdAtUtcTicks);
		return UpdateAsync(
			attemptId,
			existing => existing is null
				? desired
				: existing == desired
					? existing
					: throw new InvalidDataException(
						"Antigravity setup launch state 與既有內容衝突。"),
			cancellationToken);
	}

	public Task MarkActiveAsync(
		Guid attemptId,
		AntigravitySetupProcessIdentity helperProcess,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		ValidateProcessIdentity(helperProcess);
		return UpdateAsync(
			attemptId,
			existing => CreateActiveState(
				attemptId,
				helperProcess,
				existing),
			cancellationToken);
	}

	public Task MarkApprovalRequestedAsync(
		Guid attemptId,
		AntigravityMachineSetupSourceKind sourceKind,
		string targetIdentity,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		if (sourceKind != AntigravityMachineSetupSourceKind.OfficialPrint)
		{
			throw new ArgumentOutOfRangeException(nameof(sourceKind));
		}

		string targetIdentityFingerprint =
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(targetIdentity);
		return UpdateAsync(
			attemptId,
			existing => CreateApprovalRequestedState(
				attemptId,
				sourceKind,
				targetIdentityFingerprint,
				existing),
			cancellationToken);
	}

	public async Task<bool> TryRemoveLaunchingAsync(
		Guid attemptId,
		AntigravitySetupAttemptState observedLaunchingState,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		ArgumentNullException.ThrowIfNull(observedLaunchingState);
		ValidateState(observedLaunchingState, attemptId);
		if (observedLaunchingState.Phase !=
			AntigravitySetupAttemptPhase.Launching)
		{
			throw new ArgumentException(
				"只能放棄仍在啟動中的 Antigravity setup attempt。",
				nameof(observedLaunchingState));
		}

		await using FileStream storeLock =
			await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
		return await RunWithTransientRetryAsync(
			async () =>
			{
				AntigravitySetupAttemptState? existing =
					await ReadCoreAsync(attemptId, cancellationToken)
						.ConfigureAwait(false);
				if (existing is null)
				{
					return true;
				}

				if (existing != observedLaunchingState)
				{
					return false;
				}

				string filePath = GetFilePath(attemptId);
				EnsureExistingFileIsSafe(filePath);
				File.Delete(filePath);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<AntigravitySetupAttemptState?> ReadAsync(
		Guid attemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		await using FileStream storeLock =
			await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
		return await RunWithTransientRetryAsync(
			() => ReadCoreAsync(attemptId, cancellationToken),
			cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveAsync(
		Guid attemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		await using FileStream storeLock =
			await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
		await RunWithTransientRetryAsync(
			() =>
			{
				string filePath = GetFilePath(attemptId);
				EnsureExistingFileIsSafe(filePath);
				File.Delete(filePath);
				return Task.CompletedTask;
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveUnreferencedAsync(
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

		EnsureExistingPathAncestorsAreNotReparsePoints(_directoryPath);
		if (!TryEnsureExistingDirectoryIsSafe(_directoryPath))
		{
			return;
		}

		await using FileStream storeLock =
			await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
		await RunWithTransientRetryAsync(
			() =>
			{
				EnsureExistingPathAncestorsAreNotReparsePoints(
					_directoryPath);
				if (!TryEnsureExistingDirectoryIsSafe(_directoryPath))
				{
					return Task.CompletedTask;
				}

				foreach (string filePath in Directory.EnumerateFiles(
					_directoryPath,
					"*.json",
					SearchOption.TopDirectoryOnly))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!TryParseCanonicalAttemptFileName(
							filePath,
							out Guid attemptId))
					{
						continue;
					}

					EnsureExistingFileIsSafe(filePath);
					if (!activeAttemptIdSnapshot.Contains(attemptId))
					{
						File.Delete(filePath);
					}
				}

				return Task.CompletedTask;
			},
			cancellationToken).ConfigureAwait(false);
	}

	private static AntigravitySetupAttemptState CreateActiveState(
		Guid attemptId,
		AntigravitySetupProcessIdentity helperProcess,
		AntigravitySetupAttemptState? existing)
	{
		if (existing is null)
		{
			throw new InvalidDataException(
				"Antigravity setup launch state 不存在，不能啟動 helper。");
		}

		if (existing.Phase != AntigravitySetupAttemptPhase.Launching)
		{
			if ((existing.ProcessId == helperProcess.ProcessId) &&
				(existing.ProcessStartTimeUtcTicks ==
					helperProcess.ProcessStartTimeUtcTicks))
			{
				return existing;
			}

			throw new InvalidDataException(
				"Antigravity setup active process 與既有內容衝突。");
		}

		return new AntigravitySetupAttemptState(
			attemptId,
			AntigravitySetupAttemptPhase.Active,
			helperProcess.ProcessId,
			helperProcess.ProcessStartTimeUtcTicks,
			existing.CreatedAtUtcTicks);
	}

	private static AntigravitySetupAttemptState CreateApprovalRequestedState(
		Guid attemptId,
		AntigravityMachineSetupSourceKind sourceKind,
		string targetIdentityFingerprint,
		AntigravitySetupAttemptState? existing)
	{
		if (existing?.Phase ==
			AntigravitySetupAttemptPhase.ApprovalRequested)
		{
			if ((existing.SourceKind == sourceKind) &&
				string.Equals(
					existing.TargetIdentityFingerprint,
					targetIdentityFingerprint,
					StringComparison.Ordinal))
			{
				return existing;
			}

			throw new InvalidDataException(
				"Antigravity setup approval request 與既有內容衝突。");
		}

		if ((existing is null) ||
			(existing.Phase != AntigravitySetupAttemptPhase.Active))
		{
			throw new InvalidDataException(
				"Antigravity setup attempt 尚未啟動，不能要求 approval。");
		}

		return existing with
		{
			Phase = AntigravitySetupAttemptPhase.ApprovalRequested,
			SourceKind = sourceKind,
			TargetIdentityFingerprint = targetIdentityFingerprint
		};
	}

	private async Task UpdateAsync(
		Guid attemptId,
		Func<
			AntigravitySetupAttemptState?,
			AntigravitySetupAttemptState> update,
		CancellationToken cancellationToken)
	{
		await using FileStream storeLock =
			await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
		await RunWithTransientRetryAsync(
			async () =>
			{
				AntigravitySetupAttemptState? existing =
					await ReadCoreAsync(attemptId, cancellationToken)
						.ConfigureAwait(false);
				AntigravitySetupAttemptState updated = update(existing);
				ValidateState(updated, attemptId);
				if (updated != existing)
				{
					await WriteCoreAsync(updated, cancellationToken)
						.ConfigureAwait(false);
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<FileStream> AcquireStoreLockAsync(
		CancellationToken cancellationToken)
	{
		EnsureExistingPathAncestorsAreNotReparsePoints(_directoryPath);
		Directory.CreateDirectory(_directoryPath);
		EnsureExistingPathAncestorsAreNotReparsePoints(_directoryPath);
		string lockFilePath = Path.Combine(
			_directoryPath,
			StoreLockFileName);
		EnsureExistingFileIsSafe(lockFilePath);

		for (int attempt = 0; ; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				return new FileStream(
					lockFilePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.WriteThrough);
			}
			catch (IOException) when (attempt < LockRetryDelays.Length)
			{
				await Task.Delay(
					LockRetryDelays[attempt],
					cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private async Task<AntigravitySetupAttemptState?> ReadCoreAsync(
		Guid attemptId,
		CancellationToken cancellationToken)
	{
		string filePath = GetFilePath(attemptId);
		FileStream stream;
		try
		{
			EnsureExistingFileIsSafe(filePath);
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
					"Antigravity setup attempt state 大小無效。");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			return ParseDocument(contents, attemptId);
		}
	}

	private async Task WriteCoreAsync(
		AntigravitySetupAttemptState state,
		CancellationToken cancellationToken)
	{
		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new AttemptStateDocument(
				CurrentSchemaVersion,
				state.AttemptId,
				state.Phase,
				state.ProcessId,
				state.ProcessStartTimeUtcTicks,
				state.CreatedAtUtcTicks,
				state.SourceKind,
				state.TargetIdentityFingerprint),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidOperationException(
				"Antigravity setup attempt state 超過大小上限。");
		}

		string filePath = GetFilePath(state.AttemptId);
		EnsureExistingFileIsSafe(filePath);
		string temporaryFilePath = Path.Combine(
			_directoryPath,
			$".{state.AttemptId:N}.{Guid.NewGuid():N}.tmp");
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

			File.Move(temporaryFilePath, filePath, overwrite: true);
		}
		finally
		{
			try
			{
				File.Delete(temporaryFilePath);
			}
			catch
			{
				// Unique temporary files are never loaded as attempt state.
			}
		}
	}

	private static AntigravitySetupAttemptState ParseDocument(
		byte[] contents,
		Guid expectedAttemptId)
	{
		try
		{
			using JsonDocument parsed = JsonDocument.Parse(
				contents,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 4
				});
			if (!HasExactProperties(
					parsed.RootElement,
					DocumentPropertyNames))
			{
				throw new InvalidDataException(
					"Antigravity setup attempt state 結構無效。");
			}

			AttemptStateDocument? document =
				JsonSerializer.Deserialize<AttemptStateDocument>(
					contents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != CurrentSchemaVersion))
			{
				throw new InvalidDataException(
					"Antigravity setup attempt state 版本無效。");
			}

			AntigravitySetupAttemptState state = new(
				document.AttemptId,
				document.Phase,
				document.ProcessId,
				document.ProcessStartTimeUtcTicks,
				document.CreatedAtUtcTicks,
				document.SourceKind,
				document.TargetIdentityFingerprint);
			ValidateState(state, expectedAttemptId);
			return state;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Antigravity setup attempt state JSON 格式無效。",
				exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"Antigravity setup attempt state 內容無效。",
				exception);
		}
	}

	private static void ValidateState(
		AntigravitySetupAttemptState state,
		Guid expectedAttemptId)
	{
		if ((state.AttemptId != expectedAttemptId) ||
			!Enum.IsDefined(state.Phase) ||
			(state.ProcessId <= 0))
		{
			throw new InvalidDataException(
				"Antigravity setup attempt state 內容無效。");
		}

		ValidateUtcTicks(
			state.ProcessStartTimeUtcTicks,
			nameof(state.ProcessStartTimeUtcTicks));
		ValidateUtcTicks(
			state.CreatedAtUtcTicks,
			nameof(state.CreatedAtUtcTicks));
		bool isApprovalRequested = state.Phase ==
			AntigravitySetupAttemptPhase.ApprovalRequested;
		if (isApprovalRequested != state.SourceKind.HasValue ||
			isApprovalRequested !=
				IsValidFingerprint(state.TargetIdentityFingerprint) ||
			(state.SourceKind.HasValue &&
				(state.SourceKind.Value !=
					AntigravityMachineSetupSourceKind.OfficialPrint)))
		{
			throw new InvalidDataException(
				"Antigravity setup attempt state 階段內容無效。");
		}
	}

	private static void ValidateProcessIdentity(
		AntigravitySetupProcessIdentity process)
	{
		ArgumentNullException.ThrowIfNull(process);
		if (process.ProcessId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(process));
		}

		ValidateUtcTicks(
			process.ProcessStartTimeUtcTicks,
			nameof(process.ProcessStartTimeUtcTicks));
	}

	private static void ValidateUtcTicks(long ticks, string parameterName)
	{
		if ((ticks <= DateTime.MinValue.Ticks) ||
			(ticks > DateTime.MaxValue.Ticks))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}

	private string GetFilePath(Guid attemptId)
	{
		return Path.Combine(_directoryPath, $"{attemptId:N}.json");
	}

	private static bool TryParseCanonicalAttemptFileName(
		string filePath,
		out Guid attemptId)
	{
		string fileName = Path.GetFileName(filePath);
		if ((fileName.Length != 37) ||
			!fileName.EndsWith(".json", StringComparison.Ordinal) ||
			!Guid.TryParseExact(
				fileName.AsSpan(0, 32),
				"N",
				out attemptId) ||
			!string.Equals(
				fileName,
				$"{attemptId:N}.json",
				StringComparison.Ordinal))
		{
			attemptId = Guid.Empty;
			return false;
		}

		return true;
	}

	private static bool TryEnsureExistingDirectoryIsSafe(string directoryPath)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(directoryPath);
			if ((attributes & FileAttributes.Directory) == 0 ||
				(attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException(
					"Antigravity setup attempt state 資料夾類型無效。");
			}

			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
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

	private static bool IsValidFingerprint(string? fingerprint)
	{
		return (fingerprint is not null) &&
			(fingerprint.Length == 64) &&
			fingerprint.All(character =>
				char.IsAsciiHexDigit(character) && !char.IsLower(character));
	}

	private static void ValidateAttemptId(Guid attemptId)
	{
		if (attemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(attemptId));
		}
	}

	private static void EnsureExistingFileIsSafe(string filePath)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException(
					"Antigravity setup attempt state 檔案類型無效。");
			}
		}
		catch (FileNotFoundException)
		{
		}
		catch (DirectoryNotFoundException)
		{
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
					"Antigravity setup attempt state 路徑不可穿越 reparse point。");
			}

			current = current.Parent;
		}
	}

	private static bool IsTransientFileFailure(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException;
	}

	private static async Task RunWithTransientRetryAsync(
		Func<Task> operation,
		CancellationToken cancellationToken)
	{
		await RunWithTransientRetryAsync(
			async () =>
			{
				await operation().ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
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
