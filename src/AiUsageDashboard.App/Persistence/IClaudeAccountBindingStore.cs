using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal sealed record ClaudeAccountBinding(
	Guid AccountId,
	Guid PublicBindingId,
	string SaltBase64,
	string AccountFingerprintSha256,
	string SubscriptionScopeFingerprintSha256,
	string EntitlementFingerprintSha256)
{
	private const int SaltSizeBytes = 32;

	internal static ClaudeAccountBinding Create(
		Guid accountId,
		ClaudeSubscriptionContext context)
	{
		return Create(accountId, Guid.NewGuid(), context);
	}

	internal static ClaudeAccountBinding Create(
		Guid accountId,
		Guid publicBindingId,
		ClaudeSubscriptionContext context)
	{
		ValidateAccountId(accountId);
		ValidatePublicBindingId(publicBindingId);
		ValidateContext(context);
		byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

		try
		{
			return new ClaudeAccountBinding(
				accountId,
				publicBindingId,
				Convert.ToBase64String(salt),
				ComputeFingerprint(
					salt,
					"claude.account.v1",
					context.AccountIdentity),
				ComputeFingerprint(
					salt,
					"claude.scope.v1",
					context.SubscriptionScopeIdentity),
				ComputeFingerprint(
					salt,
					context.ProviderDefinedEntitlementKey.Version,
					context.ProviderDefinedEntitlementKey.AccountIdentity,
					context.ProviderDefinedEntitlementKey
						.SubscriptionScopeIdentity));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(salt);
		}
	}

	internal bool MatchesAccount(string accountIdentity)
	{
		return MatchesFingerprint(
			AccountFingerprintSha256,
			"claude.account.v1",
			NormalizeAccountIdentity(accountIdentity));
	}

	internal bool MatchesSubscriptionScope(string subscriptionScopeIdentity)
	{
		return MatchesFingerprint(
			SubscriptionScopeFingerprintSha256,
			"claude.scope.v1",
			NormalizeScopeIdentity(subscriptionScopeIdentity));
	}

	internal bool MatchesEntitlement(ClaudeSubscriptionContext context)
	{
		ValidateContext(context);
		return MatchesFingerprint(
			EntitlementFingerprintSha256,
			context.ProviderDefinedEntitlementKey.Version,
			context.ProviderDefinedEntitlementKey.AccountIdentity,
			context.ProviderDefinedEntitlementKey.SubscriptionScopeIdentity);
	}

	internal bool MatchesPublicProfile(AccountProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);

		return (profile.Provider == ProviderKind.Claude) &&
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

	private static string NormalizeAccountIdentity(string value)
	{
		return ClaudeSubscriptionContext.CreateVerified(
			value,
			"scope-placeholder",
			"plan-placeholder",
			subscriptionScopeDisplayName: null).AccountIdentity;
	}

	private static string NormalizeScopeIdentity(string value)
	{
		return ClaudeSubscriptionContext.CreateVerified(
			"account@example.invalid",
			value,
			"plan-placeholder",
			subscriptionScopeDisplayName: null).SubscriptionScopeIdentity;
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

	private static void ValidatePublicBindingId(Guid publicBindingId)
	{
		if (publicBindingId == Guid.Empty)
		{
			throw new ArgumentException(
				"Claude public binding ID 不可為空。",
				nameof(publicBindingId));
		}
	}

	private static void ValidateContext(ClaudeSubscriptionContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		ClaudeSubscriptionContext normalized =
			ClaudeSubscriptionContext.CreateVerified(
				context.AccountIdentity,
				context.SubscriptionScopeIdentity,
				context.PlanTier,
				context.SubscriptionScopeDisplayName);

		if ((context.VerificationState != SubscriptionVerificationState.Verified) ||
			!Equals(context, normalized))
		{
			throw new ArgumentException(
				"Claude 訂閱範圍必須是 canonical 且已驗證的 observation。",
				nameof(context));
		}
	}
}

internal interface IClaudeAccountBindingStore
{
	Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
		CancellationToken cancellationToken = default);

	Task<ClaudeAccountBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		ClaudeAccountBinding binding,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
