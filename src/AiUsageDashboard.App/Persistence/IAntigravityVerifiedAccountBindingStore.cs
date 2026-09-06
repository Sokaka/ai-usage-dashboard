using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Persistence;

internal sealed record AntigravityVerifiedAccountBinding(
	Guid AccountId,
	string NormalizedEmailSha256,
	DateTimeOffset SourceCapturedAtUtc,
	DateTimeOffset QuotaObservedAtUtc,
	DateTimeOffset VerifiedAtUtc)
{
	internal static readonly TimeSpan MaximumSourceQuotaAssociationLag =
		TimeSpan.FromMinutes(1);

	internal static string ComputeNormalizedEmailSha256(string email)
	{
		if (!AntigravityStatusLineCapture.IsValidAsciiEmail(email))
		{
			throw new ArgumentException(
				"Antigravity 帳號電子郵件格式無效。",
				nameof(email));
		}

		byte[] normalizedEmail = Encoding.UTF8.GetBytes(
			email.ToUpperInvariant());

		try
		{
			return Convert.ToHexString(SHA256.HashData(normalizedEmail));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(normalizedEmail);
		}
	}
}

internal interface IAntigravityVerifiedAccountBindingStore
{
	Task<AntigravityVerifiedAccountBinding?> LoadAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		AntigravityVerifiedAccountBinding binding,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);
}
