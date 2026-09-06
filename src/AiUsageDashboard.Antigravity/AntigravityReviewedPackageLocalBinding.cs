using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityReviewedPackageLocalBinding
{
	internal static string Compute(
		string contractFingerprint,
		string captureContractFingerprint,
		ReadOnlySpan<byte> hmacKey)
	{
		ValidateInputs(
			contractFingerprint,
			captureContractFingerprint,
			hmacKey);
		byte[] value = Encoding.UTF8.GetBytes(
			"agy-reviewed-package-local-binding-v1\n" +
			$"contract:{contractFingerprint}\n" +
			$"capture:{captureContractFingerprint}\n");
		byte[] hash = HMACSHA256.HashData(hmacKey, value);

		try
		{
			return Convert.ToHexString(hash);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(hash);
			CryptographicOperations.ZeroMemory(value);
		}
	}

	internal static bool Matches(
		string contractFingerprint,
		string captureContractFingerprint,
		ReadOnlySpan<byte> hmacKey,
		string expectedBinding)
	{
		byte[]? computedBytes = null;
		byte[]? expectedBytes = null;

		try
		{
			if (!AntigravityUsageR1SchemaParser.IsFingerprint(
					expectedBinding))
			{
				return false;
			}

			string computed = Compute(
				contractFingerprint,
				captureContractFingerprint,
				hmacKey);
			computedBytes = Convert.FromHexString(computed);
			expectedBytes = Convert.FromHexString(expectedBinding);
			return CryptographicOperations.FixedTimeEquals(
				computedBytes,
				expectedBytes);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (computedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(computedBytes);
			}

			if (expectedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedBytes);
			}
		}
	}

	private static void ValidateInputs(
		string contractFingerprint,
		string captureContractFingerprint,
		ReadOnlySpan<byte> hmacKey)
	{
		if (!AntigravityUsageR1SchemaParser.IsFingerprint(
				contractFingerprint) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				captureContractFingerprint) ||
			(hmacKey.Length < 32))
		{
			throw new InvalidDataException(
				"The reviewed AGY setup binding inputs are invalid.");
		}
	}
}
