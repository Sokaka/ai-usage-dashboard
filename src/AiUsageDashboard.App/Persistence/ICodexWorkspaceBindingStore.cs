using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal sealed record CodexWorkspaceBinding(
	Guid AccountId,
	Guid PublicBindingId,
	string SaltBase64,
	string AccountFingerprintSha256,
	string WorkspaceFingerprintSha256,
	string EntitlementFingerprintSha256,
	bool IsRestartQuarantine)
{
	private const int FingerprintSizeBytes = 32;
	private const int SaltSizeBytes = 32;
	private const string AccountFingerprintDomain = "codex.account.v1";
	private const string WorkspaceFingerprintDomain = "codex.workspace.v1";
	private const string EntitlementFingerprintDomain =
		"codex.chatgpt-workspace-member.v1";

	internal static CodexWorkspaceBinding Create(
		Guid accountId,
		string accountIdentity,
		Guid workspaceId)
	{
		return Create(
			accountId,
			Guid.NewGuid(),
			accountIdentity,
			workspaceId);
	}

	internal static CodexWorkspaceBinding Create(
		Guid accountId,
		Guid publicBindingId,
		string accountIdentity,
		Guid workspaceId)
	{
		ValidateAccountId(accountId);
		ValidatePublicBindingId(publicBindingId);
		string normalizedAccountIdentity = NormalizeAccountIdentity(
			accountIdentity);
		string normalizedWorkspaceIdentity = NormalizeWorkspaceIdentity(
			workspaceId);
		byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

		try
		{
			return new CodexWorkspaceBinding(
				accountId,
				publicBindingId,
				Convert.ToBase64String(salt),
				ComputeFingerprint(
					salt,
					AccountFingerprintDomain,
					normalizedAccountIdentity),
				ComputeFingerprint(
					salt,
					WorkspaceFingerprintDomain,
					normalizedWorkspaceIdentity),
				ComputeFingerprint(
					salt,
					EntitlementFingerprintDomain,
					normalizedAccountIdentity,
					normalizedWorkspaceIdentity),
				IsRestartQuarantine: false);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
		}
	}

	internal static CodexWorkspaceBinding CreateRestartQuarantine(Guid accountId)
	{
		ValidateAccountId(accountId);
		byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

		try
		{
			return new CodexWorkspaceBinding(
				accountId,
				Guid.NewGuid(),
				Convert.ToBase64String(salt),
				CreateRandomFingerprint(),
				CreateRandomFingerprint(),
				CreateRandomFingerprint(),
				IsRestartQuarantine: true);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
		}
	}

	internal bool MatchesAccount(string accountIdentity)
	{
		return !IsRestartQuarantine && MatchesFingerprint(
			AccountFingerprintSha256,
			AccountFingerprintDomain,
			NormalizeAccountIdentity(accountIdentity));
	}

	internal bool MatchesWorkspace(Guid workspaceId)
	{
		return !IsRestartQuarantine && MatchesFingerprint(
			WorkspaceFingerprintSha256,
			WorkspaceFingerprintDomain,
			NormalizeWorkspaceIdentity(workspaceId));
	}

	internal bool MatchesEntitlement(
		string accountIdentity,
		Guid workspaceId)
	{
		return !IsRestartQuarantine && MatchesFingerprint(
			EntitlementFingerprintSha256,
			EntitlementFingerprintDomain,
			NormalizeAccountIdentity(accountIdentity),
			NormalizeWorkspaceIdentity(workspaceId));
	}

	internal bool MatchesPublicProfile(AccountProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);

		return !IsRestartQuarantine &&
			(profile.Provider == ProviderKind.Codex) &&
			(profile.Id == AccountId) &&
			TryNormalizePublicBindingIdentity(
				profile.ProviderAccountIdentity,
				out string? normalizedIdentity) &&
			string.Equals(
				normalizedIdentity,
				CreatePublicBindingIdentity(PublicBindingId),
				StringComparison.Ordinal);
	}

	internal static string CreatePublicBindingIdentity(Guid publicBindingId)
	{
		ValidatePublicBindingId(publicBindingId);
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
		if (!string.Equals(value, canonicalIdentity, StringComparison.Ordinal))
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

	private bool MatchesFingerprint(
		string expectedFingerprint,
		params string[] components)
	{
		byte[] salt = Convert.FromBase64String(SaltBase64);
		byte[] expected = Convert.FromHexString(expectedFingerprint);

		try
		{
			byte[] actual = Convert.FromHexString(
				ComputeFingerprint(salt, components));

			try
			{
				return CryptographicOperations.FixedTimeEquals(expected, actual);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(actual);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(expected);
		}
	}

	private static string ComputeFingerprint(
		byte[] salt,
		params string[] components)
	{
		byte[] value = Encoding.UTF8.GetBytes(string.Join('\0', components));

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHash(
				HashAlgorithmName.SHA256);
			hash.AppendData(salt);
			hash.AppendData(value);
			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(value);
		}
	}

	private static string CreateRandomFingerprint()
	{
		byte[] fingerprint = RandomNumberGenerator.GetBytes(FingerprintSizeBytes);

		try
		{
			return Convert.ToHexString(fingerprint);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(fingerprint);
		}
	}

	private static string NormalizeAccountIdentity(string value)
	{
		if (!ProviderAccountIdentityRules.TryNormalize(
				value,
				out string? normalizedIdentity) ||
			(normalizedIdentity is null))
		{
			throw new ArgumentException(
				"Codex account identity 格式無效。",
				nameof(value));
		}

		return normalizedIdentity.ToLowerInvariant();
	}

	private static string NormalizeWorkspaceIdentity(Guid workspaceId)
	{
		if (workspaceId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex workspace UUID 不可為空。",
				nameof(workspaceId));
		}

		return workspaceId.ToString("D");
	}

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static void ValidatePublicBindingId(Guid publicBindingId)
	{
		if (publicBindingId == Guid.Empty)
		{
			throw new ArgumentException(
				"Codex public binding ID 不可為空。",
				nameof(publicBindingId));
		}
	}
}

internal interface ICodexWorkspaceBindingStore
{
	Task<IReadOnlyList<CodexWorkspaceBinding>> LoadAllAsync(
		CancellationToken cancellationToken = default);

	Task<CodexWorkspaceBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		CodexWorkspaceBinding binding,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
