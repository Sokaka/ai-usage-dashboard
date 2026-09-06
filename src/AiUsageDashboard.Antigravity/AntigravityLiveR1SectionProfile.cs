using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed class AntigravityLiveR1SectionProfile
{
	internal AntigravityLiveR0Profile CaptureProfile { get; }

	internal AntigravityUsageR1SectionLayout UsageLayout { get; }

	internal AntigravityLiveR1SectionProfile(
		AntigravityLiveR0Profile captureProfile,
		AntigravityUsageR1SectionLayout usageLayout)
	{
		ArgumentNullException.ThrowIfNull(captureProfile);
		ArgumentNullException.ThrowIfNull(usageLayout);

		if ((captureProfile.ExpectedUsageStructuralFingerprint is not null) ||
			(captureProfile.ExpectedExactUsageFingerprint is not null))
		{
			throw new InvalidDataException(
				"The AGY live R1 section capture profile contains legacy usage fingerprints.");
		}

		if ((captureProfile.Columns != usageLayout.Spec.Columns) ||
			(captureProfile.Rows != usageLayout.Spec.Rows))
		{
			throw new InvalidDataException(
				"The AGY live R1 section capture viewport does not match the reviewed usage layout.");
		}

		CaptureProfile = captureProfile;
		UsageLayout = usageLayout;
	}
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityLiveR1SectionProfileFile(
	string SchemaVersion,
	string Id,
	AntigravityCliFingerprint ExecutableFingerprint,
	string WorkingDirectory,
	IReadOnlyDictionary<string, string> Environment,
	short Columns,
	short Rows,
	AntigravityLiveR1PromptGuard PromptGuard,
	AntigravityUsageR1SectionLayoutFile ReviewedUsageLayout,
	IReadOnlyList<string> NonCredentialSettingsFiles,
	int MaximumSavedOutputBytes,
	TimeSpan PromptTimeout,
	TimeSpan UsageTimeout,
	TimeSpan StableScreenDuration,
	TimeSpan PollInterval,
	TimeSpan CleanupTimeout)
{
	internal const string CurrentSchemaVersion = "agy-live-r1-profile-v3";

	internal async Task<(AntigravityLiveR1SectionProfile Profile, byte[] HmacKey)>
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
				"The AGY live R1 section profile schema is invalid.");
		}

		AntigravityUsageR1SectionLayout usageLayout =
			ReviewedUsageLayout.ToLayout();

		if ((Columns != usageLayout.Spec.Columns) ||
			(Rows != usageLayout.Spec.Rows))
		{
			throw new InvalidDataException(
				"The AGY live R1 section profile viewport does not match the reviewed usage layout.");
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
			AntigravityLiveR1SectionProfile profile = new(
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

internal static class AntigravityLiveR1SectionProfileJson
{
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 96
	};
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		AllowTrailingCommas = false,
		MaxDepth = 96,
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
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		MaxDepth = 96,
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static byte[] Serialize(
		AntigravityLiveR1SectionProfileFile profileFile)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			profileFile,
			WriteOptions);

		try
		{
			_ = Deserialize(bytes);
			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
	}

	internal static AntigravityLiveR1SectionProfileFile Deserialize(
		string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		byte[] bytes = Encoding.UTF8.GetBytes(json);

		try
		{
			return Deserialize(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	internal static AntigravityLiveR1SectionProfileFile Deserialize(
		ReadOnlyMemory<byte> json)
	{
		ReadOnlyMemory<byte> payload = json.Span.StartsWith(
			Encoding.UTF8.Preamble)
			? json[Encoding.UTF8.Preamble.Length..]
			: json;
		using JsonDocument document = JsonDocument.Parse(
			payload,
			DocumentOptions);
		JsonElement root = document.RootElement;
		RequireExactObject(
			root,
			"SchemaVersion",
			"Id",
			"ExecutableFingerprint",
			"WorkingDirectory",
			"Environment",
			"Columns",
			"Rows",
			"PromptGuard",
			"ReviewedUsageLayout",
			"NonCredentialSettingsFiles",
			"MaximumSavedOutputBytes",
			"PromptTimeout",
			"UsageTimeout",
			"StableScreenDuration",
			"PollInterval",
			"CleanupTimeout");
		RequireString(root.GetProperty("SchemaVersion"));
		RequireString(root.GetProperty("Id"));
		ValidateExecutableFingerprint(
			root.GetProperty("ExecutableFingerprint"));
		RequireString(root.GetProperty("WorkingDirectory"));
		ValidateStringDictionary(root.GetProperty("Environment"));
		RequireInt16(root.GetProperty("Columns"));
		RequireInt16(root.GetProperty("Rows"));
		ValidatePromptGuard(root.GetProperty("PromptGuard"));
		RequireStringArray(root.GetProperty("NonCredentialSettingsFiles"));
		RequireInt32(root.GetProperty("MaximumSavedOutputBytes"));
		RequireString(root.GetProperty("PromptTimeout"));
		RequireString(root.GetProperty("UsageTimeout"));
		RequireString(root.GetProperty("StableScreenDuration"));
		RequireString(root.GetProperty("PollInterval"));
		RequireString(root.GetProperty("CleanupTimeout"));

		byte[] layoutBytes = Encoding.UTF8.GetBytes(
			root.GetProperty("ReviewedUsageLayout").GetRawText());
		AntigravityUsageR1SectionLayoutFile reviewedLayout;

		try
		{
			reviewedLayout = AntigravityUsageR1SectionCalibrationJson
				.DeserializeLayout(layoutBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(layoutBytes);
		}

		AntigravityLiveR1SectionProfileFile profileFile =
			JsonSerializer.Deserialize<AntigravityLiveR1SectionProfileFile>(
				payload.Span,
				ReadOptions) ?? throw new JsonException();
		return profileFile with { ReviewedUsageLayout = reviewedLayout };
	}

	private static void ValidateExecutableFingerprint(JsonElement element)
	{
		RequireExactObject(
			element,
			"AbsolutePath",
			"CliVersion",
			"FileVersion",
			"ProductVersion",
			"Sha256",
			"WinVerifyTrustStatus",
			"SignerSubject",
			"SignerThumbprint");
		RequireString(element.GetProperty("AbsolutePath"));
		RequireString(element.GetProperty("CliVersion"));
		RequireString(element.GetProperty("FileVersion"));
		RequireString(element.GetProperty("ProductVersion"));
		RequireString(element.GetProperty("Sha256"));
		RequireInt32(element.GetProperty("WinVerifyTrustStatus"));
		RequireString(element.GetProperty("SignerSubject"));
		RequireString(element.GetProperty("SignerThumbprint"));
	}

	private static void ValidatePromptGuard(JsonElement element)
	{
		RequireExactObject(
			element,
			"ExpectedPromptStructuralFingerprint",
			"ExpectedExactPromptFingerprint",
			"LocalFingerprintHmacKeyPath");
		RequireString(element.GetProperty(
			"ExpectedPromptStructuralFingerprint"));
		RequireString(element.GetProperty(
			"ExpectedExactPromptFingerprint"));
		RequireString(element.GetProperty("LocalFingerprintHmacKeyPath"));
	}

	private static void ValidateStringDictionary(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException();
		}

		HashSet<string> names = new(StringComparer.Ordinal);

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!names.Add(property.Name))
			{
				throw new JsonException();
			}

			RequireString(property.Value);
		}
	}

	private static void RequireExactObject(
		JsonElement element,
		params string[] expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException();
		}

		HashSet<string> expected = new(
			expectedPropertyNames,
			StringComparer.Ordinal);
		HashSet<string> observed = new(StringComparer.Ordinal);

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expected.Contains(property.Name) ||
				!observed.Add(property.Name))
			{
				throw new JsonException();
			}
		}

		if (!observed.SetEquals(expected))
		{
			throw new JsonException();
		}
	}

	private static void RequireInt16(JsonElement element)
	{
		if ((element.ValueKind != JsonValueKind.Number) ||
			!element.TryGetInt16(out _))
		{
			throw new JsonException();
		}
	}

	private static void RequireInt32(JsonElement element)
	{
		if ((element.ValueKind != JsonValueKind.Number) ||
			!element.TryGetInt32(out _))
		{
			throw new JsonException();
		}
	}

	private static void RequireString(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.String)
		{
			throw new JsonException();
		}
	}

	private static void RequireStringArray(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException();
		}

		foreach (JsonElement item in element.EnumerateArray())
		{
			RequireString(item);
		}
	}
}
