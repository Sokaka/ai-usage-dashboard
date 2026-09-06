using System.IO;
using System.Text.Json;

using AiUsageDashboard.App.Infrastructure;

namespace AiUsageDashboard.App.Persistence;

internal sealed class JsonGrokAccountBindingStore : IGrokAccountBindingStore
{
	private sealed record BindingDocument(
		int SchemaVersion,
		Guid PublicBindingId,
		string SaltBase64,
		string PrincipalFingerprintSha256);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumBindingCount = 256;
	private const int MaximumDocumentSizeBytes = 4 * 1024;
	private static readonly HashSet<string> ExpectedPropertyNames = new(
		new[]
		{
			"principalFingerprintSha256",
			"publicBindingId",
			"saltBase64",
			"schemaVersion"
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

	internal JsonGrokAccountBindingStore(
		Func<Guid, string> filePathFactory,
		Func<string>? rootDirectoryPathFactory = null)
	{
		_filePathFactory = filePathFactory ??
			throw new ArgumentNullException(nameof(filePathFactory));
		_rootDirectoryPathFactory = rootDirectoryPathFactory;
	}

	public async Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
		CancellationToken cancellationToken = default)
	{
		string rootDirectoryPath = GetRootDirectoryPath();
		EnsureExistingBindingPathIsSafe(
			Path.Combine(rootDirectoryPath, ".inventory-probe"));

		if (!Directory.Exists(rootDirectoryPath))
		{
			return Array.Empty<GrokAccountBinding>();
		}

		string[] accountDirectories = Directory.GetDirectories(
			rootDirectoryPath,
			"*",
			SearchOption.TopDirectoryOnly);
		List<GrokAccountBinding> bindings = new(
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

			GrokAccountBinding? binding = await LoadAsync(
				accountId,
				cancellationToken).ConfigureAwait(false);

			if (binding is null)
			{
				continue;
			}

			bindings.Add(binding);
			if (bindings.Count > MaximumBindingCount)
			{
				throw new InvalidDataException(
					"Grok 帳號 binding 數量超過安全上限。");
			}
		}

		return bindings;
	}

	public async Task<GrokAccountBinding?> LoadAsync(
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
			EnsureExistingBindingPathIsSafe(filePath);
			FileStream stream = OpenExistingRegularFile(filePath);
			await using (stream)
			{
				if (stream.Length is <= 0 or > MaximumDocumentSizeBytes)
				{
					throw new InvalidDataException(
						"Grok 帳號 binding 檔大小無效。");
				}

				using JsonDocument document = await JsonDocument.ParseAsync(
					stream,
					new JsonDocumentOptions { MaxDepth = 4 },
					cancellationToken).ConfigureAwait(false);
				return ParseDocument(document.RootElement, accountId);
			}
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
				"Grok 帳號 binding JSON 格式無效。",
				exception);
		}
	}

	public async Task SaveAsync(
		GrokAccountBinding binding,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(binding);
		ValidateBinding(binding);
		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new BindingDocument(
				CurrentSchemaVersion,
				binding.PublicBindingId,
				binding.SaltBase64,
				binding.PrincipalFingerprintSha256),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidDataException(
				"Grok 帳號 binding 檔超過大小上限。");
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
		EnsureExistingBindingPathIsSafe(filePath);
		File.Delete(filePath);
	}

	private static void EnsureExistingBindingPathIsSafe(string filePath)
	{
		string? directoryPath = Path.GetDirectoryName(filePath);
		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new IOException("Grok 帳號 binding 路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		try
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("Grok 帳號 binding 檔類型無效。");
			}
		}
		catch (FileNotFoundException)
		{
			// A missing binding is a valid disconnected state.
		}
		catch (DirectoryNotFoundException)
		{
			// A missing binding directory is a valid disconnected state.
		}
	}

	private static FileStream OpenExistingRegularFile(string filePath)
	{
		FileAttributes attributes = File.GetAttributes(filePath);
		if ((attributes &
			(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
		{
			throw new IOException("Grok 帳號 binding 檔類型無效。");
		}

		return new FileStream(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
	}

	private static GrokAccountBinding ParseDocument(
		JsonElement root,
		Guid accountId)
	{
		if (!HasExactProperties(root) ||
			!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
			(schema.ValueKind != JsonValueKind.Number) ||
			!schema.TryGetInt32(out int schemaVersion) ||
			(schemaVersion != CurrentSchemaVersion) ||
			!TryGetGuid(root, "publicBindingId", out Guid publicBindingId) ||
			(publicBindingId == Guid.Empty) ||
			!TryGetString(root, "saltBase64", out string? saltBase64) ||
			!GrokAccountBinding.IsCanonicalSalt(saltBase64) ||
			!TryGetString(
				root,
				"principalFingerprintSha256",
				out string? fingerprint) ||
			!GrokAccountBinding.IsCanonicalFingerprint(fingerprint))
		{
			throw new InvalidDataException("Grok 帳號 binding 結構無效。");
		}

		return new GrokAccountBinding(
			accountId,
			publicBindingId,
			saltBase64!,
			fingerprint!);
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
				"Grok 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidateBinding(GrokAccountBinding binding)
	{
		ValidateAccountId(binding.AccountId);
		if ((binding.PublicBindingId == Guid.Empty) ||
			!GrokAccountBinding.IsCanonicalSalt(binding.SaltBase64) ||
			!GrokAccountBinding.IsCanonicalFingerprint(
				binding.PrincipalFingerprintSha256))
		{
			throw new ArgumentException(
				"Grok 帳號 binding 格式無效。",
				nameof(binding));
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
					"Grok 帳號 binding 路徑不可穿越 reparse point。");
			}

			current = current.Parent;
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
			throw new IOException("Grok 帳號 binding 路徑無效。");
		}

		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		Directory.CreateDirectory(directoryPath);
		EnsureExistingPathAncestorsAreNotReparsePoints(directoryPath);
		if (File.Exists(filePath))
		{
			FileAttributes attributes = File.GetAttributes(filePath);
			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				throw new IOException("Grok 帳號 binding 檔類型無效。");
			}
		}

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
				// A uniquely named sidecar is never loaded as the current binding.
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
		if (_rootDirectoryPathFactory is null)
		{
			throw new InvalidOperationException(
				"Grok 帳號 binding inventory 根目錄未設定。");
		}

		string rootDirectoryPath = _rootDirectoryPathFactory();
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectoryPath);
		return Path.GetFullPath(rootDirectoryPath);
	}
}
