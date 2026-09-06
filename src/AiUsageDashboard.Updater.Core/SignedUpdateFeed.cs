using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiUsageDashboard.Updater.Core;

public static class SignedUpdateFeed
{
	public const int MaximumEnvelopeSizeBytes = 96 * 1024;
	private const int FormatVersion = 1;
	private const string Algorithm = "RSA-PSS-SHA256";
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 16
	};

	public static string Sign(string payloadJson, string signerKeyId, RSA signingKey)
	{
		ArgumentNullException.ThrowIfNull(signingKey);
		ValidateKeyId(signerKeyId);
		ValidateKeySize(signingKey);
		UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(payloadJson);
		ValidateInstallerVersion(feed);
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(feed);
		byte[] signature = signingKey.SignData(
			CreateSigningBytes(payload, signerKeyId),
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pss);
		return JsonSerializer.Serialize(new
		{
			formatVersion = FormatVersion,
			algorithm = Algorithm,
			signerKeyId,
			payload = Convert.ToBase64String(payload),
			signature = Convert.ToBase64String(signature)
		});
	}

	public static string Verify(string envelopeJson, UpdateFeedTrustStore trustStore, string expectedChannel)
	{
		ArgumentNullException.ThrowIfNull(envelopeJson);
		UpdateReleaseFeed feed = VerifyAndParse(Encoding.UTF8.GetBytes(envelopeJson), trustStore, expectedChannel);
		return JsonSerializer.Serialize(feed);
	}

	internal static UpdateReleaseFeed VerifyAndParse(ReadOnlySpan<byte> envelopeJson, UpdateFeedTrustStore trustStore, string expectedChannel)
	{
		ArgumentNullException.ThrowIfNull(trustStore);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);
		if (envelopeJson.Length > MaximumEnvelopeSizeBytes)
		{
			throw new InvalidDataException("Signed update feed exceeds the envelope size limit.");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(envelopeJson.ToArray(), DocumentOptions);
			JsonElement root = document.RootElement;
			RequireProperties(root, "formatVersion", "algorithm", "signerKeyId", "payload", "signature");
			if ((root.GetProperty("formatVersion").GetInt32() != FormatVersion) ||
				(ReadRequiredString(root, "algorithm") != Algorithm))
			{
				throw new InvalidDataException("Signed update feed format or signature algorithm is unsupported.");
			}

			string keyId = ReadRequiredString(root, "signerKeyId");
			ValidateKeyId(keyId);
			using RSA verificationKey = trustStore.OpenKey(keyId);
			byte[] payload = DecodeCanonicalBase64(root, "payload");
			byte[] signature = DecodeCanonicalBase64(root, "signature");
			if ((payload.Length > UpdateReleaseFeed.MaximumFeedSizeBytes) ||
				(signature.Length != (verificationKey.KeySize / 8)) ||
				!verificationKey.VerifyData(CreateSigningBytes(payload, keyId), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
			{
				throw new InvalidDataException($"Update feed signature verification failed for signer '{keyId}'.");
			}

			// 驗簽完成後才解析安全欄位；格式規格見 docs/UPDATE_FEED_FORMAT.md。
			UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(payload);
			if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(feed)))
			{
				throw new InvalidDataException("Signed update feed payload is not canonical UTF-8 JSON.");
			}

			feed.ValidateExpectedChannel(expectedChannel);
			ValidateInstallerVersion(feed);
			return feed;
		}
		catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException or InvalidOperationException)
		{
			throw new InvalidDataException("Unable to verify the signed update feed envelope.", exception);
		}
	}

	internal static JsonDocument ParseDocument(string json)
	{
		if (Encoding.UTF8.GetByteCount(json) > MaximumEnvelopeSizeBytes)
		{
			throw new InvalidDataException("Update signing document exceeds the size limit.");
		}
		return JsonDocument.Parse(json, DocumentOptions);
	}

	internal static void RequireProperties(JsonElement element, params string[] names)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("Update signing document requires a JSON object.");
		}
		HashSet<string> remaining = new(names, StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!remaining.Remove(property.Name))
			{
				throw new InvalidDataException($"Unknown or duplicate update signing property '{property.Name}'.");
			}
		}
		if (remaining.Count != 0)
		{
			throw new InvalidDataException($"Update signing document is missing required properties: {string.Join(", ", remaining)}.");
		}
	}

	internal static string ReadRequiredString(JsonElement element, string name)
	{
		return element.GetProperty(name).GetString() ??
			throw new InvalidDataException($"Update signing property '{name}' must be a string.");
	}

	internal static void ValidateKeyId(string keyId)
	{
		if (string.IsNullOrEmpty(keyId) || (keyId.Length > 64) ||
			!keyId.All(character => char.IsAsciiLetterOrDigit(character) || (character == '-') || (character == '_')))
		{
			throw new InvalidDataException("Update signer key ID must contain 1–64 ASCII letters, digits, hyphens or underscores.");
		}
	}

	internal static void ValidateKeySize(RSA key)
	{
		if ((key.KeySize != 3072) && (key.KeySize != 4096))
		{
			throw new InvalidDataException("Update signing keys must be RSA 3072 or 4096 bits.");
		}
	}

	private static byte[] DecodeCanonicalBase64(JsonElement element, string name)
	{
		string encoded = ReadRequiredString(element, name);
		byte[] decoded = Convert.FromBase64String(encoded);
		if (Convert.ToBase64String(decoded) != encoded)
		{
			throw new InvalidDataException($"Signed update feed '{name}' must use canonical padded Base64.");
		}
		return decoded;
	}

	private static void ValidateInstallerVersion(UpdateReleaseFeed feed)
	{
		// 新 App 的條款由同一候選 Updater 交付，避免舊 installer 沿用較早的同意範圍。
		if (feed.MinimumUpdaterVersion != feed.Updater.Version)
		{
			throw new InvalidDataException("Signed update feed minimumUpdaterVersion must equal its published updater version.");
		}
	}

	private static byte[] CreateSigningBytes(byte[] payload, string signerKeyId)
	{
		byte[] domain = Encoding.ASCII.GetBytes($"AiUsageDashboard.UpdateFeed\n{FormatVersion}\n{Algorithm}\n{signerKeyId}\n");
		return [.. domain, .. payload];
	}
}
