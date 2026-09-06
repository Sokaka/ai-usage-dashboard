using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.App.Persistence;

internal sealed record GrokAccountBinding(
	Guid AccountId,
	Guid PublicBindingId,
	string SaltBase64,
	string PrincipalFingerprintSha256)
{
	private const int SaltSizeBytes = 32;
	private const int MaximumPrincipalIdLength = 1024;
	private const int MaximumPrincipalTypeLength = 64;

	internal static GrokAccountBinding Create(
		Guid accountId,
		string principalType,
		string principalId)
	{
		return Create(
			accountId,
			Guid.NewGuid(),
			principalType,
			principalId);
	}

	internal static GrokAccountBinding Create(
		Guid accountId,
		Guid publicBindingId,
		string principalType,
		string principalId)
	{
		ValidateAccountId(accountId);
		if (publicBindingId == Guid.Empty)
		{
			throw new ArgumentException(
				"Grok public binding ID 不可為空。",
				nameof(publicBindingId));
		}

		ValidatePrincipal(principalType, principalId);
		byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

		try
		{
			return new GrokAccountBinding(
				accountId,
				publicBindingId,
				Convert.ToBase64String(salt),
				ComputeFingerprint(salt, principalType, principalId));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
		}
	}

	internal static bool IsValidPrincipal(
		string? principalType,
		string? principalId)
	{
		return IsValidPrincipalPart(
			principalType,
			MaximumPrincipalTypeLength) &&
			IsValidPrincipalPart(
				principalId,
				MaximumPrincipalIdLength);
	}

	internal bool MatchesPrincipal(string principalType, string principalId)
	{
		ValidatePrincipal(principalType, principalId);
		byte[] salt = Convert.FromBase64String(SaltBase64);
		byte[] expectedFingerprint = Convert.FromHexString(
			PrincipalFingerprintSha256);

		try
		{
			byte[] actualFingerprint = Convert.FromHexString(
				ComputeFingerprint(salt, principalType, principalId));

			try
			{
				return CryptographicOperations.FixedTimeEquals(
					expectedFingerprint,
					actualFingerprint);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(actualFingerprint);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(expectedFingerprint);
		}
	}

	internal static string CreatePublicBindingIdentity(Guid publicBindingId)
	{
		if (publicBindingId == Guid.Empty)
		{
			throw new ArgumentException(
				"Grok public binding ID 不可為空。",
				nameof(publicBindingId));
		}

		return publicBindingId.ToString("N");
	}

	internal static bool TryNormalizePublicBindingIdentity(
		string? value,
		out string? normalizedIdentity)
	{
		normalizedIdentity = null;

		if (string.IsNullOrWhiteSpace(value) ||
			!Guid.TryParseExact(value, "N", out Guid publicBindingId) ||
			(publicBindingId == Guid.Empty))
		{
			return false;
		}

		string canonicalIdentity = publicBindingId.ToString("N");

		if (!string.Equals(
			value,
			canonicalIdentity,
			StringComparison.Ordinal))
		{
			return false;
		}

		normalizedIdentity = canonicalIdentity;
		return true;
	}

	internal static bool IsCanonicalSalt(string? saltBase64)
	{
		if (string.IsNullOrWhiteSpace(saltBase64))
		{
			return false;
		}

		try
		{
			byte[] salt = Convert.FromBase64String(saltBase64);

			try
			{
				return (salt.Length == SaltSizeBytes) &&
					string.Equals(
						saltBase64,
						Convert.ToBase64String(salt),
						StringComparison.Ordinal);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(salt);
			}
		}
		catch (FormatException)
		{
			return false;
		}
	}

	internal static bool IsCanonicalFingerprint(string? fingerprint)
	{
		return (fingerprint?.Length == 64) &&
			fingerprint.All(character =>
				char.IsAsciiHexDigit(character) && !char.IsLower(character));
	}

	private static string ComputeFingerprint(
		byte[] salt,
		string principalType,
		string principalId)
	{
		byte[] principal = Encoding.UTF8.GetBytes(
			string.Concat(principalType, "\0", principalId));

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHash(
				HashAlgorithmName.SHA256);
			hash.AppendData(salt);
			hash.AppendData(principal);
			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(principal);
		}
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

	private static void ValidatePrincipal(
		string principalType,
		string principalId)
	{
		if (!IsValidPrincipalPart(
				principalType,
				MaximumPrincipalTypeLength))
		{
			throw new ArgumentException(
				"Grok principal type 格式無效。",
				nameof(principalType));
		}

		if (!IsValidPrincipalPart(
				principalId,
				MaximumPrincipalIdLength))
		{
			throw new ArgumentException(
				"Grok principal ID 格式無效。",
				nameof(principalId));
		}
	}

	private static bool IsValidPrincipalPart(
		string? value,
		int maximumLength)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= maximumLength) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
			!value.Any(char.IsControl);
	}
}

internal interface IGrokAccountBindingStore
{
	Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
		CancellationToken cancellationToken = default);

	Task<GrokAccountBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		GrokAccountBinding binding,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
