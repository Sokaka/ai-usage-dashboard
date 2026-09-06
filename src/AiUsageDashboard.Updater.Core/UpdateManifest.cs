using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.Updater.Core;

public sealed record UpdateManifest(
	[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
	[property: JsonPropertyName("packageId")] string PackageId,
	[property: JsonPropertyName("version")] string Version,
	[property: JsonPropertyName("runtimeIdentifier")] string RuntimeIdentifier,
	[property: JsonPropertyName("archiveSizeBytes")] long ArchiveSizeBytes,
	[property: JsonPropertyName("archiveSha256")] string ArchiveSha256,
	[property: JsonPropertyName("sourceRevision")] string? SourceRevision = null,
	[property: JsonPropertyName("releaseSequence")] long? ReleaseSequence = null)
{
	public const int CurrentSchemaVersion = 1;
	public const string ExpectedPackageId = "AiUsageDashboard";
	public const string ExpectedRuntimeIdentifier = "win-x64";
	public const string InstalledFileName = "update-manifest.json";

	private const int MaximumManifestSizeBytes = 64 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};
	private static readonly Regex VersionPattern = new(
		@"^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$",
		RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	[JsonIgnore]
	public string PayloadGenerationId => ArchiveSha256.ToLowerInvariant();

	public static UpdateManifest Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);

		if (json.Length > MaximumManifestSizeBytes)
		{
			throw new InvalidDataException("Update manifest exceeds the size limit.");
		}

		try
		{
			UpdateManifest manifest = JsonSerializer.Deserialize<UpdateManifest>(
				json,
				JsonOptions) ??
				throw new InvalidDataException("Update manifest is empty.");
			manifest.Validate();
			return manifest;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Update manifest is not valid JSON.", exception);
		}
	}

	public static async Task<UpdateManifest> ReadAsync(
		string manifestPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
		FileInfo manifestFile = new(Path.GetFullPath(manifestPath));

		if (!manifestFile.Exists)
		{
			throw new FileNotFoundException(
				"Update manifest was not found.",
				manifestFile.FullName);
		}

		if (manifestFile.Length > MaximumManifestSizeBytes)
		{
			throw new InvalidDataException("Update manifest exceeds the size limit.");
		}

		string json = await File.ReadAllTextAsync(
			manifestFile.FullName,
			cancellationToken);
		return Parse(json);
	}

	public async Task WriteAsync(
		string manifestPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
		Validate();
		string fullPath = Path.GetFullPath(manifestPath);
		string? parentPath = Path.GetDirectoryName(fullPath);

		if (string.IsNullOrEmpty(parentPath))
		{
			throw new ArgumentException(
				"Manifest path must have a parent directory.",
				nameof(manifestPath));
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		Directory.CreateDirectory(parentPath);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			parentPath,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: false);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

		await using (FileStream manifestStream = new(
			fullPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan))
		{
			UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
			UpdateTransaction.ThrowIfNotOrdinaryDirectory(
				parentPath,
				mustExist: true);
			UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
			await manifestStream.WriteAsync(json, cancellationToken);
			await manifestStream.FlushAsync(cancellationToken);
			UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
			UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
	}

	public void Validate()
	{
		if (SchemaVersion != CurrentSchemaVersion)
		{
			throw new InvalidDataException(
				$"Unsupported update manifest schema version: {SchemaVersion}.");
		}

		if (!string.Equals(PackageId, ExpectedPackageId, StringComparison.Ordinal))
		{
			throw new InvalidDataException("Update manifest package ID is invalid.");
		}

		if (string.IsNullOrWhiteSpace(Version) || !VersionPattern.IsMatch(Version))
		{
			throw new InvalidDataException("Update manifest version is invalid.");
		}

		if (!string.Equals(
			RuntimeIdentifier,
			ExpectedRuntimeIdentifier,
			StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update manifest runtime identifier is invalid.");
		}

		if (ArchiveSizeBytes <= 0)
		{
			throw new InvalidDataException(
				"Update manifest archive size must be positive.");
		}

		if (string.IsNullOrEmpty(ArchiveSha256) ||
			(ArchiveSha256.Length != 64) ||
			!ArchiveSha256.All(Uri.IsHexDigit))
		{
			throw new InvalidDataException("Update manifest SHA-256 is invalid.");
		}

		if ((SourceRevision is not null) &&
			(string.IsNullOrWhiteSpace(SourceRevision) ||
			(SourceRevision.Length > 128) ||
			SourceRevision.Any(char.IsControl)))
		{
			throw new InvalidDataException(
				"Update manifest source revision is invalid.");
		}

		if (ReleaseSequence.HasValue && (ReleaseSequence.Value <= 0))
		{
			throw new InvalidDataException(
				"Update manifest release sequence must be positive when present.");
		}
	}
}
