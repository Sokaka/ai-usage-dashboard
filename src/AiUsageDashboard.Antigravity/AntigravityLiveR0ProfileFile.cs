using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityLiveR0ProfileFile(
	string Id,
	AntigravityCliFingerprint ExecutableFingerprint,
	string WorkingDirectory,
	IReadOnlyDictionary<string, string> Environment,
	short Columns,
	short Rows,
	string ExpectedPromptStructuralFingerprint,
	string ExactPromptFingerprintHmacKeyPath,
	string ExpectedExactPromptFingerprint,
	string? ExpectedUsageStructuralFingerprint,
	IReadOnlyList<string> NonCredentialSettingsFiles,
	int MaximumSavedOutputBytes,
	TimeSpan PromptTimeout,
	TimeSpan UsageTimeout,
	TimeSpan StableScreenDuration,
	TimeSpan PollInterval,
	TimeSpan CleanupTimeout,
	string? ExpectedExactUsageFingerprint = null)
{
	internal async Task<(AntigravityLiveR0Profile Profile, byte[] HmacKey)>
		ToProfileAsync(
			string absoluteProfilePath,
			CancellationToken cancellationToken = default)
	{
		if (!Path.IsPathFullyQualified(absoluteProfilePath) ||
			!Path.IsPathFullyQualified(ExactPromptFingerprintHmacKeyPath) ||
			!string.Equals(
				Path.GetExtension(ExactPromptFingerprintHmacKeyPath),
				".key",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The local exact-screen fingerprint key path is invalid.");
		}

		string profilePath = Path.GetFullPath(absoluteProfilePath);
		string keyPath = Path.GetFullPath(
			ExactPromptFingerprintHmacKeyPath);
		string profileDirectory = Path.GetDirectoryName(profilePath) ??
			throw new InvalidDataException(
				"The live R0 profile directory is invalid.");
		string keyDirectory = Path.GetDirectoryName(keyPath) ??
			throw new InvalidDataException(
				"The local exact-screen fingerprint key directory is invalid.");

		if (!string.Equals(
			profileDirectory,
			keyDirectory,
			StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The local exact-screen fingerprint key must be next to the profile.");
		}

		if (!IsExistingRegularNonReparseFile(keyPath) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(keyPath))
		{
			throw new InvalidDataException(
				"The local exact-screen fingerprint key path is not safe.");
		}

		FileInfo before = new(keyPath);
		before.Refresh();

		if (!before.Exists ||
			(before.Length != 32) ||
			((before.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			throw new InvalidDataException(
				"The local exact-screen fingerprint key is not a regular 32-byte file.");
		}

		long beforeLength = before.Length;
		DateTime beforeCreationTimeUtc = before.CreationTimeUtc;
		DateTime beforeLastWriteTimeUtc = before.LastWriteTimeUtc;
		FileAttributes beforeAttributes = before.Attributes;
		byte[] hmacKey = new byte[32];
		byte[] trailingByte = new byte[1];

		try
		{
			await using (FileStream stream = new(
				keyPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await stream.ReadExactlyAsync(
					hmacKey,
					cancellationToken);
				int trailingByteCount = await stream.ReadAsync(
					trailingByte,
					cancellationToken);

				if (trailingByteCount != 0)
				{
					throw new InvalidDataException(
						"The local exact-screen fingerprint key has an invalid length.");
				}
			}

			FileInfo after = new(keyPath);
			after.Refresh();

			if (!after.Exists ||
				(after.Length != beforeLength) ||
				(after.CreationTimeUtc != beforeCreationTimeUtc) ||
				(after.LastWriteTimeUtc != beforeLastWriteTimeUtc) ||
				(after.Attributes != beforeAttributes) ||
				((after.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) ||
				!IsExistingRegularNonReparseFile(keyPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(keyPath))
			{
				throw new IOException(
					"The local exact-screen fingerprint key changed while it was read.");
			}

			AntigravityLiveR0Profile profile = new(
				Id,
				ExecutableFingerprint,
				WorkingDirectory,
				Environment,
				Columns,
				Rows,
				ExpectedPromptStructuralFingerprint,
				hmacKey,
				ExpectedExactPromptFingerprint,
				ExpectedUsageStructuralFingerprint,
				NonCredentialSettingsFiles,
				MaximumSavedOutputBytes,
				PromptTimeout,
				UsageTimeout,
				StableScreenDuration,
				PollInterval,
				CleanupTimeout,
				ExpectedExactUsageFingerprint);
			return (profile, hmacKey);
		}
		catch
		{
			CryptographicOperations.ZeroMemory(hmacKey);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(trailingByte);
		}
	}

	private static bool IsExistingRegularNonReparseFile(string absolutePath)
	{
		try
		{
			if (!Path.IsPathFullyQualified(absolutePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absolutePath);
			string? root = Path.GetPathRoot(fullPath);

			if (string.IsNullOrWhiteSpace(root))
			{
				return false;
			}

			FileAttributes rootAttributes = File.GetAttributes(root);

			if (((rootAttributes & FileAttributes.Directory) == 0) ||
				((rootAttributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			string relativePath = Path.GetRelativePath(root, fullPath);
			string[] components = relativePath.Split(
				new[]
				{
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar
				},
				StringSplitOptions.RemoveEmptyEntries);

			if ((components.Length == 0) ||
				components.Any(component =>
					(component == ".") || (component == "..")))
			{
				return false;
			}

			string currentPath = root;

			for (int index = 0; index < components.Length; index++)
			{
				currentPath = Path.Combine(currentPath, components[index]);
				FileAttributes attributes = File.GetAttributes(currentPath);
				bool isDirectory =
					(attributes & FileAttributes.Directory) != 0;

				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}

				bool isFileComponent = index == (components.Length - 1);

				if ((isFileComponent && isDirectory) ||
					(!isFileComponent && !isDirectory))
				{
					return false;
				}
			}

			return File.Exists(fullPath);
		}
		catch
		{
			return false;
		}
	}
}

internal static class AntigravityLiveR0ProfileJson
{
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 32
	};
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		AllowTrailingCommas = false,
		MaxDepth = 32,
		PropertyNameCaseInsensitive = false,
		ReadCommentHandling = JsonCommentHandling.Disallow,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		MaxDepth = 32,
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static byte[] Serialize(AntigravityLiveR0ProfileFile profile)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, WriteOptions);

		try
		{
			_ = Deserialize(bytes);
			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
	}

	internal static AntigravityLiveR0ProfileFile Deserialize(
		ReadOnlyMemory<byte> json)
	{
		ReadOnlyMemory<byte> payload = json.Span.StartsWith(
			Encoding.UTF8.Preamble)
			? json[Encoding.UTF8.Preamble.Length..]
			: json;
		using JsonDocument document = JsonDocument.Parse(
			payload,
			DocumentOptions);
		RequireNoDuplicateProperties(document.RootElement);
		return JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
			payload.Span,
			ReadOptions) ?? throw new JsonException();
	}

	private static void RequireNoDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> propertyNames = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!propertyNames.Add(property.Name))
				{
					throw new JsonException();
				}

				RequireNoDuplicateProperties(property.Value);
			}

			return;
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement item in element.EnumerateArray())
		{
			RequireNoDuplicateProperties(item);
		}
	}
}

internal static class AntigravityLiveR0PrivateProfileFile
{
	private readonly record struct ProfileFileState(
		long Length,
		DateTime CreationTimeUtc,
		DateTime LastWriteTimeUtc,
		FileAttributes Attributes);

	internal static async Task<byte[]> ReadBoundedAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path) ||
			!Path.IsPathFullyQualified(path) ||
			(maximumBytes <= 0))
		{
			throw new InvalidDataException();
		}

		string fullPath = Path.GetFullPath(path);

		if (!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(fullPath))
		{
			throw new InvalidDataException();
		}

		ProfileFileState before = CaptureState(fullPath, maximumBytes);
		byte[] bytes = new byte[checked((int)before.Length)];
		byte[] trailingByte = new byte[1];

		try
		{
			await using FileStream stream = new(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);

			if (stream.Length != before.Length)
			{
				throw new IOException();
			}

			await stream.ReadExactlyAsync(bytes, cancellationToken);
			int trailingByteCount = await stream.ReadAsync(
				trailingByte,
				cancellationToken);

			if ((trailingByteCount != 0) ||
				(CaptureState(fullPath, maximumBytes) != before) ||
				!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(fullPath))
			{
				throw new IOException();
			}

			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(trailingByte);
		}
	}

	private static ProfileFileState CaptureState(
		string path,
		int maximumBytes)
	{
		FileInfo file = new(path);
		file.Refresh();

		if (!file.Exists ||
			(file.Length <= 0) ||
			(file.Length > maximumBytes) ||
			((file.Attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
		{
			throw new InvalidDataException();
		}

		return new ProfileFileState(
			file.Length,
			file.CreationTimeUtc,
			file.LastWriteTimeUtc,
			file.Attributes);
	}
}
