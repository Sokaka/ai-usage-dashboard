using System.IO;
using System.Text.Json;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Persistence;

internal sealed class JsonAntigravityVerifiedAccountBindingStore :
	IAntigravityVerifiedAccountBindingStore
{
	private sealed record BindingDocument(
		int SchemaVersion,
		Guid AccountId,
		string NormalizedEmailSha256,
		DateTimeOffset SourceCapturedAtUtc,
		DateTimeOffset QuotaObservedAtUtc,
		DateTimeOffset VerifiedAtUtc);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 4 * 1024;
	private const string ManagedPathDescription =
		"The Antigravity verified-account binding path";
	private static readonly HashSet<string> ExpectedPropertyNames = new(
		new[]
		{
			"accountId",
			"normalizedEmailSha256",
			"quotaObservedAtUtc",
			"schemaVersion",
			"sourceCapturedAtUtc",
			"verifiedAtUtc"
		},
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 4,
		PropertyNameCaseInsensitive = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private readonly Func<Guid, string> _filePathFactory;

	internal JsonAntigravityVerifiedAccountBindingStore(
		Func<Guid, string> filePathFactory)
	{
		_filePathFactory = filePathFactory ??
			throw new ArgumentNullException(nameof(filePathFactory));
	}

	public async Task<AntigravityVerifiedAccountBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			if (!File.Exists(filePath))
			{
				return null;
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
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
				return null;
			}

			using JsonDocument jsonDocument = await JsonDocument.ParseAsync(
				stream,
				new JsonDocumentOptions { MaxDepth = 4 },
				cancellationToken).ConfigureAwait(false);

			return TryParseDocument(
				jsonDocument.RootElement,
				accountId,
				out AntigravityVerifiedAccountBinding? binding)
				? binding
				: null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is JsonException) ||
			(exception is InvalidDataException) ||
			IsStorageAccessFailure(exception))
		{
			return null;
		}
	}

	public async Task SaveAsync(
		AntigravityVerifiedAccountBinding binding,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(binding);
		ValidateBinding(binding);

		BindingDocument document = new(
			CurrentSchemaVersion,
			binding.AccountId,
			binding.NormalizedEmailSha256,
			binding.SourceCapturedAtUtc,
			binding.QuotaObservedAtUtc,
			binding.VerifiedAtUtc);
		byte[] serializedDocument = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);

		if (serializedDocument.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"The Antigravity verified-account binding exceeds its size limit.");
		}

		string filePath = GetFilePath(binding.AccountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			await SaveDocumentAsync(
				filePath,
				serializedDocument,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (IsStorageAccessFailure(exception))
		{
			// Display continuity is best effort and must not fail a usage refresh.
		}
	}

	public async Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		string filePath = GetFilePath(accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);

		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			ManagedPathDescription);
		File.Delete(filePath);
		TryDeleteOwnedSidecarFiles(filePath);
	}

	private static bool TryParseDocument(
		JsonElement root,
		Guid expectedAccountId,
		out AntigravityVerifiedAccountBinding? binding)
	{
		binding = null;

		if (!HasExactProperties(root) ||
			!TryGetInt32(root, "schemaVersion", out int schemaVersion) ||
			(schemaVersion != CurrentSchemaVersion) ||
			!TryGetGuid(root, "accountId", out Guid accountId) ||
			(accountId == Guid.Empty) ||
			(accountId != expectedAccountId) ||
			!TryGetString(
				root,
				"normalizedEmailSha256",
				out string? normalizedEmailSha256) ||
			!IsCanonicalSha256(normalizedEmailSha256) ||
			!TryGetUtcTimestamp(
				root,
				"sourceCapturedAtUtc",
				out DateTimeOffset sourceCapturedAtUtc) ||
			!TryGetUtcTimestamp(
				root,
				"quotaObservedAtUtc",
				out DateTimeOffset quotaObservedAtUtc) ||
			!TryGetUtcTimestamp(
				root,
				"verifiedAtUtc",
				out DateTimeOffset verifiedAtUtc) ||
			!HasValidEvidenceTiming(
				sourceCapturedAtUtc,
				quotaObservedAtUtc,
				verifiedAtUtc))
		{
			return false;
		}

		binding = new AntigravityVerifiedAccountBinding(
			accountId,
			normalizedEmailSha256!,
			sourceCapturedAtUtc,
			quotaObservedAtUtc,
			verifiedAtUtc);
		return true;
	}

	private static bool HasExactProperties(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> actualPropertyNames = new(StringComparer.Ordinal);

		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!ExpectedPropertyNames.Contains(property.Name) ||
				!actualPropertyNames.Add(property.Name))
			{
				return false;
			}
		}

		return actualPropertyNames.SetEquals(ExpectedPropertyNames);
	}

	private static bool TryGetInt32(
		JsonElement root,
		string propertyName,
		out int value)
	{
		value = default;
		JsonElement property = root.GetProperty(propertyName);
		return property.ValueKind == JsonValueKind.Number &&
			property.TryGetInt32(out value);
	}

	private static bool TryGetGuid(
		JsonElement root,
		string propertyName,
		out Guid value)
	{
		value = default;
		JsonElement property = root.GetProperty(propertyName);
		return property.ValueKind == JsonValueKind.String &&
			property.TryGetGuid(out value);
	}

	private static bool TryGetString(
		JsonElement root,
		string propertyName,
		out string? value)
	{
		JsonElement property = root.GetProperty(propertyName);
		value = property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;
		return value is not null;
	}

	private static bool TryGetUtcTimestamp(
		JsonElement root,
		string propertyName,
		out DateTimeOffset value)
	{
		JsonElement property = root.GetProperty(propertyName);
		string? timestampText = property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;
		value = default;

		return HasExplicitUtcOffset(timestampText) &&
			property.TryGetDateTimeOffset(out value) &&
			(value != default) &&
			(value.Offset == TimeSpan.Zero);
	}

	private static bool HasExplicitUtcOffset(string? timestampText)
	{
		return !string.IsNullOrWhiteSpace(timestampText) &&
			(timestampText.EndsWith('Z') ||
				timestampText.EndsWith(
					"+00:00",
					StringComparison.Ordinal));
	}

	private static bool IsCanonicalSha256(string? value)
	{
		if ((value is null) || (value.Length != 64))
		{
			return false;
		}

		foreach (char character in value)
		{
			if (!((character >= '0' && character <= '9') ||
				(character >= 'A' && character <= 'F')))
			{
				return false;
			}
		}

		return true;
	}

	private static void ValidateBinding(
		AntigravityVerifiedAccountBinding binding)
	{
		ValidateAccountId(binding.AccountId);

		if (!IsCanonicalSha256(binding.NormalizedEmailSha256))
		{
			throw new ArgumentException(
				"Antigravity 已驗證帳號的電子郵件 SHA-256 格式無效。",
				nameof(binding));
		}

		ValidateUtcTimestamp(binding.SourceCapturedAtUtc, nameof(binding));
		ValidateUtcTimestamp(binding.QuotaObservedAtUtc, nameof(binding));
		ValidateUtcTimestamp(binding.VerifiedAtUtc, nameof(binding));

		if (!HasValidEvidenceTiming(
			binding.SourceCapturedAtUtc,
			binding.QuotaObservedAtUtc,
			binding.VerifiedAtUtc))
		{
			throw new ArgumentException(
				"Antigravity 已驗證帳號的時間順序無效。",
				nameof(binding));
		}
	}

	private static bool HasValidEvidenceTiming(
		DateTimeOffset sourceCapturedAtUtc,
		DateTimeOffset quotaObservedAtUtc,
		DateTimeOffset verifiedAtUtc)
	{
		if (sourceCapturedAtUtc > quotaObservedAtUtc)
		{
			return false;
		}

		return quotaObservedAtUtc - sourceCapturedAtUtc <=
				AntigravityVerifiedAccountBinding
					.MaximumSourceQuotaAssociationLag &&
			verifiedAtUtc >= quotaObservedAtUtc;
	}

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateUtcTimestamp(
		DateTimeOffset timestamp,
		string parameterName)
	{
		if ((timestamp == default) || (timestamp.Offset != TimeSpan.Zero))
		{
			throw new ArgumentException(
				"Antigravity 已驗證帳號的時間必須是非預設 UTC 值。",
				parameterName);
		}
	}

	private static bool IsStorageAccessFailure(Exception exception)
	{
		return (exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is NotSupportedException);
	}

	private static async Task SaveDocumentAsync(
		string filePath,
		byte[] serializedDocument,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new IOException(
				"The Antigravity verified-account binding directory is unavailable.");
		}

		ManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			ManagedPathDescription);
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			ManagedPathDescription);
		string temporaryFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
		string backupFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.bak");
		bool didFinalizeReplacement = false;

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryFilePath,
				ManagedPathDescription);
			await using (FileStream stream = new(
				temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(serializedDocument, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryFilePath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			if (File.Exists(filePath))
			{
				File.Replace(
					temporaryFilePath,
					filePath,
					backupFilePath);
				didFinalizeReplacement = true;
			}
			else
			{
				File.Move(temporaryFilePath, filePath);
				didFinalizeReplacement = true;
			}
		}
		finally
		{
			TryDeleteSidecarFile(temporaryFilePath);

			if (didFinalizeReplacement)
			{
				TryDeleteSidecarFile(backupFilePath);
			}
			else
			{
				TryRestoreBackupIfDestinationIsMissing(
					filePath,
					backupFilePath);
			}
			// File.Replace can fail after moving the old destination into the
			// backup path. Restore that regular same-directory file when possible;
			// otherwise preserve it as recovery material. The loader never treats
			// a sidecar as current by itself.
		}
	}

	private static void TryRestoreBackupIfDestinationIsMissing(
		string destinationFilePath,
		string backupFilePath)
	{
		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				destinationFilePath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				backupFilePath,
				ManagedPathDescription);
			if (File.Exists(destinationFilePath) ||
				!File.Exists(backupFilePath))
			{
				return;
			}

			FileAttributes attributes = File.GetAttributes(backupFilePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return;
			}

			File.Move(backupFilePath, destinationFilePath);
		}
		catch (Exception exception) when (IsStorageAccessFailure(exception))
		{
			// Keep the uniquely named backup for manual recovery if restoration
			// cannot be completed safely.
		}
	}

	private static void TryDeleteOwnedSidecarFiles(string destinationFilePath)
	{
		try
		{
			string? directoryPath = Path.GetDirectoryName(destinationFilePath);
			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				return;
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				destinationFilePath,
				ManagedPathDescription);
			if (!Directory.Exists(directoryPath))
			{
				return;
			}

			string destinationFileName = Path.GetFileName(destinationFilePath);
			foreach (string extension in new[] { ".tmp", ".bak" })
			{
				string pattern = $".{destinationFileName}.*{extension}";
				foreach (string candidatePath in Directory.EnumerateFiles(
					directoryPath,
					pattern,
					SearchOption.TopDirectoryOnly))
				{
					TryDeleteOwnedSidecarFile(
						directoryPath,
						destinationFileName,
						extension,
						candidatePath);
				}
			}
		}
		catch (Exception exception) when (IsStorageAccessFailure(exception))
		{
			// Optional display metadata cleanup is best effort.
		}
	}

	private static void TryDeleteOwnedSidecarFile(
		string expectedDirectoryPath,
		string destinationFileName,
		string expectedExtension,
		string candidatePath)
	{
		try
		{
			string fullDirectoryPath = Path.GetFullPath(expectedDirectoryPath);
			string fullCandidatePath = Path.GetFullPath(candidatePath);
			string? candidateDirectoryPath =
				Path.GetDirectoryName(fullCandidatePath);
			string candidateFileName = Path.GetFileName(fullCandidatePath);
			string requiredPrefix = $".{destinationFileName}.";

			if (!string.Equals(
					candidateDirectoryPath,
					fullDirectoryPath,
					StringComparison.OrdinalIgnoreCase) ||
				!candidateFileName.StartsWith(
					requiredPrefix,
					StringComparison.Ordinal) ||
				!candidateFileName.EndsWith(
					expectedExtension,
					StringComparison.Ordinal) ||
				candidateFileName.Length <=
					requiredPrefix.Length + expectedExtension.Length)
			{
				return;
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				fullCandidatePath,
				ManagedPathDescription);
			FileAttributes attributes = File.GetAttributes(fullCandidatePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return;
			}

			File.Delete(fullCandidatePath);
		}
		catch (Exception exception) when (IsStorageAccessFailure(exception))
		{
			// Optional display metadata cleanup is best effort.
		}
	}

	private static void TryDeleteSidecarFile(string filePath)
	{
		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			File.Delete(filePath);
		}
		catch (Exception exception) when (IsStorageAccessFailure(exception))
		{
			// Uniquely named sidecar files are never loaded.
		}
	}

	private string GetFilePath(Guid accountId)
	{
		string filePath = _filePathFactory(accountId);
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		return Path.GetFullPath(filePath);
	}
}
