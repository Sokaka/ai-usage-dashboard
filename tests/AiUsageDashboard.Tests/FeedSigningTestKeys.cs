using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class FeedSigningTestKeys : IDisposable
{
	private readonly RSA _firstKey = RSA.Create(3072);
	private readonly RSA _secondKey = RSA.Create(3072);

	public string Sign(string payload, string keyId = "test-first")
	{
		return SignedUpdateFeed.Sign(payload, keyId, keyId == "test-second" ? _secondKey : _firstKey);
	}

	public UpdateFeedTrustStore Trust(params string[] keyIds)
	{
		return UpdateFeedTrustStore.Parse(TrustJson(keyIds));
	}

	public string TrustJson(params string[] keyIds)
	{
		return JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			keys = keyIds.Select(keyId => new
			{
				keyId,
				publicKeyPem = (keyId == "test-second" ? _secondKey : _firstKey).ExportSubjectPublicKeyInfoPem()
			})
		});
	}

	public string SignRawPayload(string payload)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(payload);
		byte[] signingBytes = [.. Encoding.ASCII.GetBytes("AiUsageDashboard.UpdateFeed\n1\nRSA-PSS-SHA256\ntest-first\n"), .. bytes];
		return JsonSerializer.Serialize(new
		{
			formatVersion = 1,
			algorithm = "RSA-PSS-SHA256",
			signerKeyId = "test-first",
			payload = Convert.ToBase64String(bytes),
			signature = Convert.ToBase64String(_firstKey.SignData(signingBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
		});
	}

	public void Dispose()
	{
		_firstKey.Dispose();
		_secondKey.Dispose();
	}
}
