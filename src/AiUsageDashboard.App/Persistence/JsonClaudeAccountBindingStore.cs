using System.IO;
using System.Text.Json;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Persistence;

internal sealed class JsonClaudeAccountBindingStore : IClaudeAccountBindingStore
{
	private sealed record BindingDocument(
		int SchemaVersion,
		Guid PublicBindingId,
		string SaltBase64,
		string AccountFingerprintSha256,
		string SubscriptionScopeFingerprintSha256,
		string EntitlementFingerprintSha256);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumBindingCount = 256;
	private const int MaximumDocumentSizeBytes = 8 * 1024;
	private const string PathDescription = "The Claude account binding path";
	private static readonly HashSet<string> ExpectedPropertyNames = new(
		new[]
		{
			"accountFingerprintSha256",
			"entitlementFingerprintSha256",
			"publicBindingId",
			"saltBase64",
			"schemaVersion",
			"subscriptionScopeFingerprintSha256"
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
	private readonly Func<string>? _rootDirectoryPathFactory;

	internal JsonClaudeAccountBindingStore(
		Func<Guid, string> filePathFactory,
		Func<string>? rootDirectoryPathFactory = null)
	{
		_filePathFactory = filePathFactory ??
			throw new ArgumentNullException(nameof(filePathFactory));
		_rootDirectoryPathFactory = rootDirectoryPathFactory;
	}

	public async Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
		CancellationToken cancellationToken = default)
	{
		string rootDirectoryPath = GetRootDirectoryPath();
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			Path.Combine(rootDirectoryPath, ".inventory-probe"),
			PathDescription);

		if (!Directory.Exists(rootDirectoryPath))
		{
			return Array.Empty<ClaudeAccountBinding>();
		}

		string[] accountDirectories = Directory.GetDirectories(
			rootDirectoryPath,
			"*",
			SearchOption.TopDirectoryOnly);

		List<ClaudeAccountBinding> bindings = new(
			Math.Min(accountDirectories.Length, MaximumBindingCount + 1));

		foreach (string accountDirectory in accountDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string directoryName = Path.GetFileName(accountDirectory);

			if (!Guid.TryParseExact(directoryName, "N", out Guid accountId) ||
				(accountId == Guid.Empty) ||
				!string.Equals(
					directoryName,
					accountId.ToString("N"),
					StringComparison.Ordinal))
			{
				continue;
			}

			ClaudeAccountBinding? binding = await LoadAsync(
				accountId,
				cancellationToken).ConfigureAwait(false);

			if (binding is not null)
			{
				bindings.Add(binding);

				if (bindings.Count > MaximumBindingCount)
				{
					throw new InvalidDataException(
						"Claude 帳號 binding 數量超過安全上限。");
				}
			}
		}

		return bindings;
	}

	public async Task<ClaudeAccountBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		string filePath = GetFilePath(accountId);
		using IDisposable lease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				PathDescription);
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
					"Claude 帳號 binding 檔大小無效。");
			}

			using JsonDocument document = await JsonDocument.ParseAsync(
				stream,
				new JsonDocumentOptions { MaxDepth = 4 },
				cancellationToken).ConfigureAwait(false);
			return ParseDocument(document.RootElement, accountId);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Claude 帳號 binding JSON 格式無效。",
				exception);
		}
	}

	public async Task SaveAsync(
		ClaudeAccountBinding binding,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(binding);
		ValidateBinding(binding);
		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new BindingDocument(
				CurrentSchemaVersion,
				binding.PublicBindingId,
				binding.SaltBase64,
				binding.AccountFingerprintSha256,
				binding.SubscriptionScopeFingerprintSha256,
				binding.EntitlementFingerprintSha256),
			SerializerOptions);

		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"Claude 帳號 binding 檔超過大小上限。");
		}

		string filePath = GetFilePath(binding.AccountId);
		using IDisposable lease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);
		await WriteDocumentAsync(filePath, contents, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		string filePath = GetFilePath(accountId);
		using IDisposable lease = await FileGates.EnterAsync(
			filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			PathDescription);
		File.Delete(filePath);
	}

	private static ClaudeAccountBinding ParseDocument(
		JsonElement root,
		Guid accountId)
	{
		if (!HasExactProperties(root) ||
			!TryGetInt32(root, "schemaVersion", out int schemaVersion) ||
			(schemaVersion != CurrentSchemaVersion) ||
			!TryGetGuid(root, "publicBindingId", out Guid publicBindingId) ||
			(publicBindingId == Guid.Empty) ||
			!TryGetString(root, "saltBase64", out string? saltBase64) ||
			!ClaudeAccountBinding.IsCanonicalSalt(saltBase64) ||
			!TryGetCanonicalFingerprint(
				root,
				"accountFingerprintSha256",
				out string? accountFingerprint) ||
			!TryGetCanonicalFingerprint(
				root,
				"subscriptionScopeFingerprintSha256",
				out string? scopeFingerprint) ||
			!TryGetCanonicalFingerprint(
				root,
				"entitlementFingerprintSha256",
				out string? entitlementFingerprint))
		{
			throw new InvalidDataException("Claude 帳號 binding 結構無效。");
		}

		return new ClaudeAccountBinding(
			accountId,
			publicBindingId,
			saltBase64!,
			accountFingerprint!,
			scopeFingerprint!,
			entitlementFingerprint!);
	}

	private static bool HasExactProperties(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> observed = new(StringComparer.Ordinal);
		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!ExpectedPropertyNames.Contains(property.Name) ||
				!observed.Add(property.Name))
			{
				return false;
			}
		}

		return observed.SetEquals(ExpectedPropertyNames);
	}

	private static bool TryGetCanonicalFingerprint(
		JsonElement root,
		string propertyName,
		out string? fingerprint)
	{
		return TryGetString(root, propertyName, out fingerprint) &&
			ClaudeAccountBinding.IsCanonicalFingerprint(fingerprint);
	}

	private static bool TryGetGuid(
		JsonElement root,
		string propertyName,
		out Guid value)
	{
		JsonElement property = root.GetProperty(propertyName);
		value = default;
		return (property.ValueKind == JsonValueKind.String) &&
			property.TryGetGuid(out value);
	}

	private static bool TryGetInt32(
		JsonElement root,
		string propertyName,
		out int value)
	{
		JsonElement property = root.GetProperty(propertyName);
		value = default;
		return (property.ValueKind == JsonValueKind.Number) &&
			property.TryGetInt32(out value);
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

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Claude 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateBinding(ClaudeAccountBinding binding)
	{
		ValidateAccountId(binding.AccountId);

		if ((binding.PublicBindingId == Guid.Empty) ||
			!ClaudeAccountBinding.IsCanonicalSalt(binding.SaltBase64) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.AccountFingerprintSha256) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.SubscriptionScopeFingerprintSha256) ||
			!ClaudeAccountBinding.IsCanonicalFingerprint(
				binding.EntitlementFingerprintSha256))
		{
			throw new ArgumentException(
				"Claude 帳號 binding 格式無效。",
				nameof(binding));
		}
	}

	private static async Task WriteDocumentAsync(
		string filePath,
		byte[] contents,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new IOException("Claude 帳號 binding 路徑無效。");
		}

		ManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			PathDescription);
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			PathDescription);
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
				await stream.WriteAsync(contents, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				PathDescription);
			File.Move(temporaryFilePath, filePath, overwrite: true);
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
				// 唯一命名的 sidecar 永遠不會被當作目前 binding 載入。
			}
		}
	}

	private string GetFilePath(Guid accountId)
	{
		string filePath = _filePathFactory(accountId);
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		return Path.GetFullPath(filePath);
	}

	private string GetRootDirectoryPath()
	{
		string rootDirectoryPath;

		if (_rootDirectoryPathFactory is not null)
		{
			rootDirectoryPath = _rootDirectoryPathFactory();
		}
		else
		{
			Guid probeAccountId =
				Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
			string probeFilePath = GetFilePath(probeAccountId);
			string? accountDirectoryPath = Path.GetDirectoryName(probeFilePath);
			rootDirectoryPath = accountDirectoryPath is null
				? string.Empty
				: Path.GetDirectoryName(accountDirectoryPath) ?? string.Empty;
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectoryPath);
		return Path.GetFullPath(rootDirectoryPath);
	}
}
