using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.Updater.Core;

internal sealed record UpdatePayloadIdentity(
	[property: JsonPropertyName("manifestSha256")] string ManifestSha256,
	[property: JsonPropertyName("applicationSha256")] string ApplicationSha256)
{
	private const int MaximumManifestSizeBytes = 64 * 1024;
	private const int Sha256HexLength = 64;

	internal static UpdatePayloadIdentity Read(string directoryPath)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(directoryPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(directoryPath, mustExist: true);
		string manifestPath = Path.Combine(directoryPath, UpdateManifest.InstalledFileName);
		string executablePath = Path.Combine(directoryPath, "app", "AiUsageDashboard.App.exe");
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(Path.GetDirectoryName(executablePath)!);
		UpdateTransaction.ThrowIfNotOrdinaryFile(manifestPath, mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(executablePath, mustExist: true);
		using FileStream manifestStream = File.OpenRead(manifestPath);
		if (manifestStream.Length > MaximumManifestSizeBytes)
		{
			throw new InvalidDataException($"Recovery manifest exceeds the size limit: {manifestPath}");
		}

		using (StreamReader reader = new(manifestStream, leaveOpen: true))
		{
			_ = UpdateManifest.Parse(reader.ReadToEnd());
		}

		manifestStream.Position = 0;
		using FileStream executableStream = File.OpenRead(executablePath);
		return new UpdatePayloadIdentity(
			Convert.ToHexString(SHA256.HashData(manifestStream)),
			Convert.ToHexString(SHA256.HashData(executableStream)));
	}

	internal void Validate(string receiptPath)
	{
		if ((!IsSha256(ManifestSha256)) || (!IsSha256(ApplicationSha256)))
		{
			throw new InvalidDataException($"Invalid payload identity in transaction receipt: {receiptPath}");
		}
	}

	private static bool IsSha256(string? value)
	{
		return (value?.Length == Sha256HexLength) && value.All(char.IsAsciiHexDigit);
	}
}
