using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

public sealed record AntigravitySetupApprovalReceipt(
	Guid AttemptId,
	AntigravityMachineSetupSourceKind SourceKind,
	string TargetIdentityFingerprint);

public sealed class AntigravitySetupApprovalReceiptStore
{
	private sealed record ReceiptDocument(
		int SchemaVersion,
		Guid AttemptId,
		AntigravityMachineSetupSourceKind SourceKind,
		string TargetIdentityFingerprint);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 4 * 1024;
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string AntigravityDirectoryName = "antigravity";
	private const string ReceiptDirectoryName = "setup-approval-receipts-v1";
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[]
		{
			"attemptId",
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
	private static readonly TimeSpan[] TransientRetryDelays =
	[
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(150)
	];
	private readonly string _directoryPath;

	public AntigravitySetupApprovalReceiptStore(string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
		_directoryPath = Path.GetFullPath(directoryPath);
	}

	public static AntigravitySetupApprovalReceiptStore CreateDefault()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException(
				"無法取得 Antigravity setup approval receipt 資料夾。");
		}

		return new AntigravitySetupApprovalReceiptStore(Path.Combine(
			localApplicationData,
			ApplicationDirectoryName,
			AntigravityDirectoryName,
			ReceiptDirectoryName));
	}

	public async Task WriteAsync(
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
			ComputeTargetIdentityFingerprint(targetIdentity);
		AntigravitySetupApprovalReceipt expectedReceipt = new(
			attemptId,
			sourceKind,
			targetIdentityFingerprint);
		await RunWithTransientRetryAsync(
			async () =>
			{
				EnsureExistingPathAncestorsAreNotReparsePoints(
					_directoryPath);
				Directory.CreateDirectory(_directoryPath);
				EnsureExistingPathAncestorsAreNotReparsePoints(
					_directoryPath);
				AntigravitySetupApprovalReceipt? existingReceipt =
					await ReadCoreAsync(attemptId, cancellationToken)
						.ConfigureAwait(false);
				if (existingReceipt is not null)
				{
					if (existingReceipt == expectedReceipt)
					{
						return;
					}

					throw new InvalidDataException(
						"Antigravity setup approval receipt 與既有內容衝突。");
				}

				string filePath = GetFilePath(attemptId);
				EnsureExistingFileIsSafe(filePath);
				byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
					new ReceiptDocument(
						CurrentSchemaVersion,
						attemptId,
						sourceKind,
						targetIdentityFingerprint),
					SerializerOptions);
				if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
				{
					throw new InvalidOperationException(
						"Antigravity setup approval receipt 超過大小上限。");
				}

				string temporaryFilePath = Path.Combine(
					_directoryPath,
					$".{attemptId:N}.{Guid.NewGuid():N}.tmp");
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
						await stream.FlushAsync(cancellationToken)
							.ConfigureAwait(false);
						stream.Flush(flushToDisk: true);
					}

					File.Move(temporaryFilePath, filePath, overwrite: false);
				}
				finally
				{
					try
					{
						File.Delete(temporaryFilePath);
					}
					catch
					{
						// Unique temporary files are never accepted as receipts.
					}
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	public Task<AntigravitySetupApprovalReceipt?> ReadAsync(
		Guid attemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		return RunWithTransientRetryAsync(
			() => ReadCoreAsync(attemptId, cancellationToken),
			cancellationToken);
	}

	public Task RemoveAsync(
		Guid attemptId,
		CancellationToken cancellationToken = default)
	{
		ValidateAttemptId(attemptId);
		return RunWithTransientRetryAsync(
			() =>
			{
				EnsureExistingPathAncestorsAreNotReparsePoints(
					_directoryPath);
				string filePath = GetFilePath(attemptId);
				EnsureExistingFileIsSafe(filePath);
				File.Delete(filePath);
				return Task.CompletedTask;
			},
			cancellationToken);
	}

	public Task RemoveUnreferencedAsync(
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

		return RunWithTransientRetryAsync(
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
			cancellationToken);
	}

	public static string ComputeTargetIdentityFingerprint(
		string targetIdentity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
		string normalizedIdentity = targetIdentity.Trim();
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
			$"AiUsageDashboard.AntigravitySetupApproval.v1\n{normalizedIdentity.ToUpperInvariant()}")));
	}

	private async Task<AntigravitySetupApprovalReceipt?> ReadCoreAsync(
		Guid attemptId,
		CancellationToken cancellationToken)
	{
		EnsureExistingPathAncestorsAreNotReparsePoints(_directoryPath);
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
					"Antigravity setup approval receipt 大小無效。");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			await stream.ReadExactlyAsync(contents, cancellationToken)
				.ConfigureAwait(false);
			return ParseDocument(contents, attemptId);
		}
	}

	private static AntigravitySetupApprovalReceipt ParseDocument(
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
					"Antigravity setup approval receipt 結構無效。");
			}

			ReceiptDocument? document =
				JsonSerializer.Deserialize<ReceiptDocument>(
					contents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != CurrentSchemaVersion) ||
				(document.AttemptId != expectedAttemptId) ||
				(document.SourceKind !=
					AntigravityMachineSetupSourceKind.OfficialPrint) ||
				!IsValidFingerprint(document.TargetIdentityFingerprint))
			{
				throw new InvalidDataException(
					"Antigravity setup approval receipt 內容無效。");
			}

			return new AntigravitySetupApprovalReceipt(
				document.AttemptId,
				document.SourceKind,
				document.TargetIdentityFingerprint);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Antigravity setup approval receipt JSON 格式無效。",
				exception);
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
					"Antigravity setup approval receipt 資料夾類型無效。");
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

	private static void EnsureExistingFileIsSafe(string filePath)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException(
					"Antigravity setup approval receipt 類型無效。");
			}
		}
		catch (FileNotFoundException)
		{
		}
		catch (DirectoryNotFoundException)
		{
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

	private static bool IsValidFingerprint(string fingerprint)
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
					"Antigravity setup approval receipt 路徑不可穿越 reparse point。");
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
