using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.Updater.Core;

internal sealed record UpdateReleaseArtifact(
	[property: JsonPropertyName("artifactId"), JsonRequired] string ArtifactId,
	[property: JsonPropertyName("version"), JsonRequired] string Version,
	[property: JsonPropertyName("runtimeIdentifier"), JsonRequired] string RuntimeIdentifier,
	[property: JsonPropertyName("fileName"), JsonRequired] string FileName,
	[property: JsonPropertyName("downloadUrl"), JsonRequired] string DownloadUrl,
	[property: JsonPropertyName("sizeBytes"), JsonRequired] long SizeBytes,
	[property: JsonPropertyName("sha256"), JsonRequired] string Sha256,
	[property: JsonPropertyName("sourceRevision"), JsonRequired] string SourceRevision)
{
	[JsonIgnore]
	public Uri DownloadUri => new(DownloadUrl, UriKind.Absolute);
}

internal sealed record UpdateReleaseFeed(
	[property: JsonPropertyName("schemaVersion"), JsonRequired] int SchemaVersion,
	[property: JsonPropertyName("channel"), JsonRequired] string Channel,
	[property: JsonPropertyName("releaseSequence"), JsonRequired] long ReleaseSequence,
	[property: JsonPropertyName("minimumUpdaterVersion"), JsonRequired] string MinimumUpdaterVersion,
	[property: JsonPropertyName("package"), JsonRequired] UpdateReleaseArtifact Package,
	[property: JsonPropertyName("updater"), JsonRequired] UpdateReleaseArtifact Updater)
{
	public const int CurrentSchemaVersion = 1;
	public const int MaximumFeedSizeBytes = 64 * 1024;
	public const long MaximumUpdaterSizeBytes = 128L * 1024 * 1024;

	private const string ExpectedPackageArtifactId = "AiUsageDashboard";
	private const string ExpectedRuntimeIdentifier = "win-x64";
	private const string ExpectedUpdaterArtifactId = "AiUsageDashboard.Updater";
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 16
	};
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 16,
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};

	public static UpdateReleaseFeed Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		int byteCount = Encoding.UTF8.GetByteCount(json);

		if (byteCount > MaximumFeedSizeBytes)
		{
			throw new InvalidDataException("Update release feed exceeds the size limit.");
		}

		return Parse(Encoding.UTF8.GetBytes(json));
	}

	public static UpdateReleaseFeed Parse(ReadOnlySpan<byte> json)
	{
		if (json.Length > MaximumFeedSizeBytes)
		{
			throw new InvalidDataException("Update release feed exceeds the size limit.");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				json.ToArray(),
				DocumentOptions);
			ThrowIfDuplicateProperties(document.RootElement);

			UpdateReleaseFeed feed =
				JsonSerializer.Deserialize<UpdateReleaseFeed>(
					json,
					SerializerOptions) ??
				throw new InvalidDataException("Update release feed is empty.");
			feed.Validate();
			return feed;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"Update release feed is not valid JSON.",
				exception);
		}
	}

	public UpdateManifest CreatePackageManifest()
	{
		Validate();

		return new UpdateManifest(
			UpdateManifest.CurrentSchemaVersion,
			Package.ArtifactId,
			Package.Version,
			Package.RuntimeIdentifier,
			Package.SizeBytes,
			Package.Sha256,
			Package.SourceRevision,
			ReleaseSequence);
	}

	public void ValidateExpectedChannel(string expectedChannel)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);
		Validate();

		if (!string.Equals(Channel, expectedChannel, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update release feed channel does not match the requested channel.");
		}
	}

	private static void ThrowIfDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> propertyNames = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!propertyNames.Add(property.Name))
				{
					throw new InvalidDataException(
						$"Update release feed contains duplicate property '{property.Name}'.");
				}

				ThrowIfDuplicateProperties(property.Value);
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				ThrowIfDuplicateProperties(item);
			}
		}
	}

	private static void ValidateArtifact(
		UpdateReleaseArtifact artifact,
		string expectedArtifactId,
		string expectedFileName,
		long maximumSizeBytes)
	{
		if (!string.Equals(
				artifact.ArtifactId,
				expectedArtifactId,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update release artifact ID is invalid.");
		}

		if (!ReleaseVersion.TryParse(artifact.Version, out _))
		{
			throw new InvalidDataException(
				"Update release artifact version is invalid.");
		}

		if (!string.Equals(
				artifact.RuntimeIdentifier,
				ExpectedRuntimeIdentifier,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update release artifact runtime identifier is invalid.");
		}

		if (!string.Equals(
				artifact.FileName,
				expectedFileName,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update release artifact filename is not canonical.");
		}

		if (!Uri.TryCreate(
				artifact.DownloadUrl,
				UriKind.Absolute,
				out Uri? downloadUri) ||
			!string.Equals(
				downloadUri.Scheme,
				Uri.UriSchemeHttps,
				StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrEmpty(downloadUri.Host) ||
			!string.IsNullOrEmpty(downloadUri.UserInfo) ||
			!string.IsNullOrEmpty(downloadUri.Fragment))
		{
			throw new InvalidDataException(
				"Update release artifact download URL must be absolute HTTPS.");
		}

		if ((artifact.SizeBytes <= 0) ||
			(artifact.SizeBytes > maximumSizeBytes))
		{
			throw new InvalidDataException(
				"Update release artifact size is outside the allowed range.");
		}

		if ((artifact.Sha256 is null) ||
			(artifact.Sha256.Length != 64) ||
			!artifact.Sha256.All(character =>
				(character >= '0' && character <= '9') ||
				(character >= 'a' && character <= 'f')))
		{
			throw new InvalidDataException(
				"Update release artifact SHA-256 must be lowercase hexadecimal.");
		}

		if (string.IsNullOrWhiteSpace(artifact.SourceRevision) ||
			(artifact.SourceRevision.Length > 128) ||
			artifact.SourceRevision.Any(char.IsControl))
		{
			throw new InvalidDataException(
				"Update release artifact source revision is invalid.");
		}
	}

	private static bool IsValidChannel(string? channel)
	{
		if (string.IsNullOrEmpty(channel) ||
			(channel.Length > 32) ||
			(channel[0] < 'a') ||
			(channel[0] > 'z'))
		{
			return false;
		}

		return channel.Skip(1).All(character =>
			(character >= 'a' && character <= 'z') ||
			(character >= '0' && character <= '9') ||
			(character == '-'));
	}

	private void Validate()
	{
		if (SchemaVersion != CurrentSchemaVersion)
		{
			throw new InvalidDataException(
				$"Unsupported update release feed schema version: {SchemaVersion}.");
		}

		if (!IsValidChannel(Channel))
		{
			throw new InvalidDataException(
				"Update release feed channel is invalid.");
		}

		if (ReleaseSequence <= 0)
		{
			throw new InvalidDataException(
				"Update release feed sequence must be positive.");
		}

		if (!ReleaseVersion.TryParse(
				MinimumUpdaterVersion,
				out ReleaseVersion minimumUpdaterVersion))
		{
			throw new InvalidDataException(
				"Minimum updater version is invalid.");
		}

		if (Package is null || Updater is null)
		{
			throw new InvalidDataException(
				"Update release feed artifacts are required.");
		}

		ValidateArtifact(
			Package,
			ExpectedPackageArtifactId,
			$"AiUsageDashboard-{Package.Version}-win-x64.zip",
			UpdatePackageStager.DefaultMaximumArchiveSizeBytes);
		ValidateArtifact(
			Updater,
			ExpectedUpdaterArtifactId,
			$"AiUsageDashboard-Updater-{Updater.Version}-win-x64.exe",
			MaximumUpdaterSizeBytes);

		ReleaseVersion updaterVersion = ReleaseVersion.Parse(Updater.Version);

		if (minimumUpdaterVersion.CompareTo(updaterVersion) > 0)
		{
			throw new InvalidDataException(
				"Minimum updater version exceeds the published updater version.");
		}
	}
}
