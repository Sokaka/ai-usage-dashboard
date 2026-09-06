using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityLiveR1PromptGuard(
	string ExpectedPromptStructuralFingerprint,
	string ExpectedExactPromptFingerprint,
	string LocalFingerprintHmacKeyPath);

internal sealed class AntigravityLiveR1Profile
{
	internal AntigravityLiveR0Profile CaptureProfile { get; }

	internal AntigravityUsageR1Layout UsageLayout { get; }

	internal AntigravityLiveR1Profile(
		AntigravityLiveR0Profile captureProfile,
		AntigravityUsageR1Layout usageLayout)
	{
		ArgumentNullException.ThrowIfNull(captureProfile);
		ArgumentNullException.ThrowIfNull(usageLayout);

		if ((captureProfile.ExpectedUsageStructuralFingerprint is not null) ||
			(captureProfile.ExpectedExactUsageFingerprint is not null))
		{
			throw new InvalidDataException(
				"The AGY live R1 capture profile contains legacy usage fingerprints.");
		}

		if ((captureProfile.Columns != usageLayout.Spec.Columns) ||
			(captureProfile.Rows != usageLayout.Spec.Rows))
		{
			throw new InvalidDataException(
				"The AGY live R1 capture viewport does not match the reviewed usage layout.");
		}

		CaptureProfile = captureProfile;
		UsageLayout = usageLayout;
	}
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityLiveR1ProfileFile(
	string SchemaVersion,
	string Id,
	AntigravityCliFingerprint ExecutableFingerprint,
	string WorkingDirectory,
	IReadOnlyDictionary<string, string> Environment,
	short Columns,
	short Rows,
	AntigravityLiveR1PromptGuard PromptGuard,
	AntigravityUsageR1ReviewedLayoutFile ReviewedUsageLayout,
	IReadOnlyList<string> NonCredentialSettingsFiles,
	int MaximumSavedOutputBytes,
	TimeSpan PromptTimeout,
	TimeSpan UsageTimeout,
	TimeSpan StableScreenDuration,
	TimeSpan PollInterval,
	TimeSpan CleanupTimeout)
{
	internal const string CurrentSchemaVersion = "agy-live-r1-profile-v1";

	internal async Task<(AntigravityLiveR1Profile Profile, byte[] HmacKey)>
		ToProfileAsync(
			string absoluteProfilePath,
			CancellationToken cancellationToken = default)
	{
		if (!string.Equals(
				SchemaVersion,
				CurrentSchemaVersion,
				StringComparison.Ordinal) ||
			(PromptGuard is null) ||
			(ReviewedUsageLayout is null) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				PromptGuard.ExpectedPromptStructuralFingerprint) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				PromptGuard.ExpectedExactPromptFingerprint))
		{
			throw new InvalidDataException(
				"The AGY live R1 profile schema is invalid.");
		}

		AntigravityUsageR1Layout usageLayout =
			ReviewedUsageLayout.ToLayout();

		if ((Columns != usageLayout.Spec.Columns) ||
			(Rows != usageLayout.Spec.Rows))
		{
			throw new InvalidDataException(
				"The AGY live R1 profile viewport does not match the reviewed usage layout.");
		}

		AntigravityLiveR0ProfileFile captureProfileFile = new(
			Id,
			ExecutableFingerprint,
			WorkingDirectory,
			Environment,
			Columns,
			Rows,
			PromptGuard.ExpectedPromptStructuralFingerprint,
			PromptGuard.LocalFingerprintHmacKeyPath,
			PromptGuard.ExpectedExactPromptFingerprint,
			ExpectedUsageStructuralFingerprint: null,
			NonCredentialSettingsFiles,
			MaximumSavedOutputBytes,
			PromptTimeout,
			UsageTimeout,
			StableScreenDuration,
			PollInterval,
			CleanupTimeout,
			ExpectedExactUsageFingerprint: null);
		(AntigravityLiveR0Profile captureProfile, byte[] hmacKey) =
			await captureProfileFile.ToProfileAsync(
				absoluteProfilePath,
				cancellationToken);

		try
		{
			AntigravityLiveR1Profile profile = new(
				captureProfile,
				usageLayout);
			return (profile, hmacKey);
		}
		catch
		{
			CryptographicOperations.ZeroMemory(hmacKey);
			throw;
		}
	}
}

internal static class AntigravityLiveR1ProfileJson
{
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 32
	};
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		AllowTrailingCommas = false,
		MaxDepth = 32,
		PropertyNameCaseInsensitive = false,
		ReadCommentHandling = JsonCommentHandling.Disallow,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static AntigravityLiveR1ProfileFile Deserialize(string json)
	{
		ArgumentNullException.ThrowIfNull(json);

		using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
		RequireNoDuplicateProperties(document.RootElement);
		return JsonSerializer.Deserialize<AntigravityLiveR1ProfileFile>(
			json,
			ReadOptions) ?? throw new JsonException();
	}

	internal static AntigravityLiveR1ProfileFile Deserialize(
		ReadOnlyMemory<byte> json)
	{
		using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
		RequireNoDuplicateProperties(document.RootElement);
		return JsonSerializer.Deserialize<AntigravityLiveR1ProfileFile>(
			json.Span,
			ReadOptions) ?? throw new JsonException();
	}

	private static void RequireNoDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> propertyNames = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!propertyNames.Add(property.Name))
				{
					throw new JsonException();
				}

				RequireNoDuplicateProperties(property.Value);
			}

			return;
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement item in element.EnumerateArray())
		{
			RequireNoDuplicateProperties(item);
		}
	}
}
