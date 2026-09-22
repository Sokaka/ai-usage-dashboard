using System.Diagnostics;
using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal static class MaintenanceUpdaterFile
{
	internal static void EnsureCompatible(string executablePath)
	{
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			executablePath,
			mustExist: true);
		FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);

		if (!string.Equals(
				versionInfo.ProductName,
				"AiUsageDashboard.Updater",
				StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
		{
			throw new InvalidDataException(
				"The maintenance updater identity is invalid.");
		}
	}

	internal static async Task EnsureFilesMatchAsync(
		string firstPath,
		string secondPath,
		CancellationToken cancellationToken)
	{
		FileInfo first = new(firstPath);
		FileInfo second = new(secondPath);

		if (first.Length != second.Length)
		{
			throw new InvalidDataException(
				"The maintenance updater copy size is invalid.");
		}

		byte[] firstHash = Convert.FromHexString(
			await GetSha256Async(firstPath, cancellationToken));
		byte[] secondHash = Convert.FromHexString(
			await GetSha256Async(secondPath, cancellationToken));

		if (!CryptographicOperations.FixedTimeEquals(firstHash, secondHash))
		{
			throw new InvalidDataException(
				"The maintenance updater copy SHA-256 is invalid.");
		}
	}

	internal static async Task<string> GetSha256Async(
		string filePath,
		CancellationToken cancellationToken)
	{
		await using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
