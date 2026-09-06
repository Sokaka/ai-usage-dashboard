using System.Security.Cryptography;
using System.Text.Json;

namespace AiUsageDashboard.Updater.Core;

public sealed class UpdateFeedTrustStore
{
	private const int MaximumTrustedKeys = 8;
	private readonly IReadOnlyDictionary<string, byte[]> _publicKeys;

	private UpdateFeedTrustStore(IReadOnlyDictionary<string, byte[]> publicKeys)
	{
		_publicKeys = publicKeys;
	}

	public static UpdateFeedTrustStore Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		try
		{
			using JsonDocument document = SignedUpdateFeed.ParseDocument(json);
			JsonElement root = document.RootElement;
			SignedUpdateFeed.RequireProperties(root, "schemaVersion", "keys");
			if ((root.GetProperty("schemaVersion").GetInt32() != 1) ||
				(root.GetProperty("keys").ValueKind != JsonValueKind.Array))
			{
				throw new InvalidDataException("Update feed trust store has an unsupported format.");
			}

			Dictionary<string, byte[]> publicKeys = new(StringComparer.Ordinal);
			foreach (JsonElement entry in root.GetProperty("keys").EnumerateArray())
			{
				SignedUpdateFeed.RequireProperties(entry, "keyId", "publicKeyPem");
				string keyId = SignedUpdateFeed.ReadRequiredString(entry, "keyId");
				SignedUpdateFeed.ValidateKeyId(keyId);
				string pem = SignedUpdateFeed.ReadRequiredString(entry, "publicKeyPem");
				if (!pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
					!pem.TrimEnd().EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal))
				{
					throw new InvalidDataException($"Trusted update key '{keyId}' must contain only a public SPKI PEM.");
				}

				using RSA rsa = RSA.Create();
				rsa.ImportFromPem(pem);
				SignedUpdateFeed.ValidateKeySize(rsa);
				if (!publicKeys.TryAdd(keyId, rsa.ExportSubjectPublicKeyInfo()) ||
					(publicKeys.Count > MaximumTrustedKeys))
				{
					throw new InvalidDataException($"Duplicate or excessive trusted update key '{keyId}'.");
				}
			}

			if (publicKeys.Count == 0)
			{
				throw new InvalidDataException("Update feed trust store must contain at least one public key.");
			}

			return new UpdateFeedTrustStore(publicKeys);
		}
		catch (Exception exception) when (exception is JsonException or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
		{
			throw new InvalidDataException("Unable to parse the update feed public trust store.", exception);
		}
	}

	internal RSA OpenKey(string keyId)
	{
		if (!_publicKeys.TryGetValue(keyId, out byte[]? publicKey))
		{
			throw new InvalidDataException($"Update feed signer '{keyId}' is not trusted by this updater.");
		}

		RSA rsa = RSA.Create();
		try
		{
			rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
			return rsa;
		}
		catch
		{
			rsa.Dispose();
			throw;
		}
	}
}
