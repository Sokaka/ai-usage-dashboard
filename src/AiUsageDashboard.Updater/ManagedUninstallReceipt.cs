using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record ManagedUninstallReceipt(
	[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
	[property: JsonPropertyName("installRoot")] string InstallRoot,
	[property: JsonPropertyName("manifest")] UpdateManifest Manifest)
{
	internal const int CurrentSchemaVersion = 1;
	private const int MaximumReceiptSizeBytes = 128 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};

	internal static async Task<ManagedUninstallReceipt> ReadAsync(
		string receiptPath,
		string expectedInstallRoot,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
		string fullPath = Path.GetFullPath(receiptPath);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
		FileInfo receiptFile = new(fullPath);

		if (receiptFile.Length > MaximumReceiptSizeBytes)
		{
			throw new InvalidDataException(
				"The uninstall receipt exceeds the size limit.");
		}

		try
		{
			string json = await File.ReadAllTextAsync(
				fullPath,
				cancellationToken);
			ManagedUninstallReceipt receipt =
				JsonSerializer.Deserialize<ManagedUninstallReceipt>(
					json,
					JsonOptions) ?? throw new InvalidDataException(
						"The uninstall receipt is empty.");
			receipt.Validate(expectedInstallRoot);
			return receipt;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"The uninstall receipt is not valid JSON.",
				exception);
		}
	}

	internal static async Task<ManagedUninstallReceipt> WriteNewAsync(
		string receiptPath,
		string installRoot,
		UpdateManifest manifest,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
		ArgumentNullException.ThrowIfNull(manifest);
		ManagedUninstallReceipt receipt = new(
			CurrentSchemaVersion,
			ManagedInstallationPaths.NormalizeDirectory(installRoot),
			manifest);
		receipt.Validate(installRoot);
		string fullPath = Path.GetFullPath(receiptPath);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: false);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);

		await using FileStream stream = new(
			fullPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		await stream.WriteAsync(json, cancellationToken);
		await stream.FlushAsync(cancellationToken);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
		return receipt;
	}

	internal void Validate(string expectedInstallRoot)
	{
		if (SchemaVersion != CurrentSchemaVersion)
		{
			throw new InvalidDataException(
				"The uninstall receipt schema version is unsupported.");
		}

		if (string.IsNullOrWhiteSpace(InstallRoot))
		{
			throw new InvalidDataException(
				"The uninstall receipt install root is missing.");
		}

		if (Manifest is null)
		{
			throw new InvalidDataException(
				"The uninstall receipt manifest is missing.");
		}

		Manifest.Validate();
		string normalizedExpectedRoot =
			ManagedInstallationPaths.NormalizeDirectory(expectedInstallRoot);
		string normalizedReceiptRoot =
			ManagedInstallationPaths.NormalizeDirectory(InstallRoot);

		if (!string.Equals(
				normalizedReceiptRoot,
				normalizedExpectedRoot,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The uninstall receipt belongs to another install root.");
		}
	}
}
