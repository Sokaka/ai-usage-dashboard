using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.Updater.Core;

public static class UpdateInstanceNames
{
	public const string SingleInstanceMutexNamePrefix =
		@"Local\AiUsageDashboard.SingleInstance";

	public static string CreateCurrentUserSingleInstanceMutexName()
	{
		return CreateUserScopedSingleInstanceMutexName(
			$@"{Environment.UserDomainName}\{Environment.UserName}");
	}

	public static string CreateUserScopedSingleInstanceMutexName(
		string userIdentity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);
		byte[] identityHash = SHA256.HashData(
			Encoding.UTF8.GetBytes(userIdentity));
		string suffix = Convert.ToHexString(identityHash.AsSpan(0, 12));
		return $"{SingleInstanceMutexNamePrefix}.{suffix}";
	}
}
