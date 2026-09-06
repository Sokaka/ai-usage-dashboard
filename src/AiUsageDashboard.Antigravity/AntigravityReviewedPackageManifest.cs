using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.AntigravitySpike;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityReviewedPackageExecutable(
	string CliVersion,
	string FileVersion,
	string ProductVersion,
	string Sha256,
	int WinVerifyTrustStatus,
	string SignerSubject,
	string SignerThumbprint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityReviewedPackageUsageLayout(
	AntigravityUsageR1SectionSpec Spec,
	string SchemaFingerprint,
	IReadOnlyList<string> ExpectedPageFingerprints);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityReviewedPackageCompatibleBuild(
	string ContractId,
	AntigravityReviewedPackageExecutable Executable,
	string ContractFingerprint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityReviewedPackageContract(
	string ContractId,
	AntigravityReviewedPackageExecutable Executable,
	string ExpectedPromptStructuralFingerprint,
	AntigravityReviewedPackageUsageLayout UsageLayout,
	string ContractFingerprint,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	IReadOnlyList<AntigravityReviewedPackageCompatibleBuild>?
		CompatibleBuilds = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AntigravityReviewedPackageManifestFile(
	string FormatVersion,
	AntigravityUsageR1ReviewState ReviewState,
	IReadOnlyList<AntigravityReviewedPackageContract> Contracts)
{
	internal const string CurrentFormatVersion =
		"agy-reviewed-package-manifest-v1";
}

internal static class AntigravityReviewedPackageManifestJson
{
	private sealed class StrictStringEnumConverterFactory :
		JsonConverterFactory
	{
		public override bool CanConvert(Type typeToConvert)
		{
			return typeToConvert.IsEnum;
		}

		public override JsonConverter CreateConverter(
			Type typeToConvert,
			JsonSerializerOptions options)
		{
			Type converterType =
				typeof(StrictStringEnumConverter<>).MakeGenericType(
					typeToConvert);
			return (JsonConverter)(Activator.CreateInstance(converterType) ??
				throw new InvalidOperationException(
					"The strict enum converter could not be created."));
		}
	}

	private sealed class StrictStringEnumConverter<TEnum> :
		JsonConverter<TEnum>
		where TEnum : struct, Enum
	{
		public override TEnum Read(
			ref Utf8JsonReader reader,
			Type typeToConvert,
			JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.String)
			{
				throw new JsonException();
			}

			string? text = reader.GetString();

			if ((text is null) ||
				!Enum.TryParse(text, ignoreCase: false, out TEnum value) ||
				!Enum.IsDefined(value) ||
				!string.Equals(
					Enum.GetName(typeof(TEnum), value),
					text,
					StringComparison.Ordinal))
			{
				throw new JsonException();
			}

			return value;
		}

		public override void Write(
			Utf8JsonWriter writer,
			TEnum value,
			JsonSerializerOptions options)
		{
			string name = Enum.GetName(typeof(TEnum), value) ??
				throw new JsonException();
			writer.WriteStringValue(name);
		}
	}

	private const string ContractFingerprintFormatVersion =
		"agy-reviewed-package-contract-fingerprint-v1";
	private const int MaximumContracts = 64;
	private const int MaximumJsonBytes = 1024 * 1024;
	private const int MaximumSignerSubjectLength = 2048;
	private const int MaximumVersionLength = 256;
	private const int Sha1ThumbprintLength = 40;
	private const int Sha256Length = 64;
	private const int WinTrustSuccess = 0;
	private static readonly Regex AbsoluteDrivePathPattern = new(
		@"(?<![A-Z0-9])[A-Z]:[\\/]",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);
	private static readonly Regex EmailAddressPattern = new(
		@"(?<![A-Z0-9._%+\-])[A-Z0-9._%+\-]{1,64}@[A-Z0-9.\-]+\.[A-Z]{2,63}(?![A-Z0-9.\-])",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);
	private static readonly Regex QuotaInstancePattern = new(
		@"(?<![A-Z0-9])(?:100|[0-9]{1,2})(?:\.[0-9]+)?\s*%",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);

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
			new StrictStringEnumConverterFactory()
		}
	};
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		MaxDepth = 96,
		WriteIndented = true,
		Converters =
		{
			new StrictStringEnumConverterFactory()
		}
	};

	internal static string ComputeContractFingerprint(
		AntigravityReviewedPackageContract contract)
	{
		AntigravityReviewedPackageContract normalized =
			NormalizeContractCore(contract);
		return ComputeContractFingerprintCore(normalized);
	}

	internal static AntigravityReviewedPackageManifestFile Deserialize(
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

	internal static AntigravityReviewedPackageManifestFile Deserialize(
		ReadOnlyMemory<byte> json)
	{
		ReadOnlyMemory<byte> payload = json.Span.StartsWith(
			Encoding.UTF8.Preamble)
			? json[Encoding.UTF8.Preamble.Length..]
			: json;

		if ((payload.Length == 0) || (payload.Length > MaximumJsonBytes))
		{
			throw new JsonException(
				"The reviewed AGY package manifest size is invalid.");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				payload,
				DocumentOptions);
			RequireNoDuplicateProperties(document.RootElement);
			ValidateJsonShape(document.RootElement);
			AntigravityReviewedPackageManifestFile manifest =
				JsonSerializer.Deserialize<
					AntigravityReviewedPackageManifestFile>(
						payload.Span,
						ReadOptions) ?? throw new JsonException();
			return NormalizeManifest(manifest);
		}
		catch (JsonException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new JsonException(
				"The reviewed AGY package manifest is invalid.",
				exception);
		}
	}

	internal static byte[] Serialize(
		AntigravityReviewedPackageManifestFile manifest)
	{
		AntigravityReviewedPackageManifestFile normalized =
			NormalizeManifest(manifest);
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			normalized,
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

	private static string ComputeContractFingerprintCore(
		AntigravityReviewedPackageContract contract)
	{
		StringBuilder canonical = new();
		Append(
			canonical,
			"format",
			ContractFingerprintFormatVersion);
		Append(canonical, "contract-id", contract.ContractId);
		Append(
			canonical,
			"executable.cli-version",
			contract.Executable.CliVersion);
		Append(
			canonical,
			"executable.file-version",
			contract.Executable.FileVersion);
		Append(
			canonical,
			"executable.product-version",
			contract.Executable.ProductVersion);
		Append(
			canonical,
			"executable.sha256",
			contract.Executable.Sha256);
		Append(
			canonical,
			"executable.win-verify-trust-status",
			contract.Executable.WinVerifyTrustStatus.ToString(
				CultureInfo.InvariantCulture));
		Append(
			canonical,
			"executable.signer-subject",
			contract.Executable.SignerSubject);
		Append(
			canonical,
			"executable.signer-thumbprint",
			contract.Executable.SignerThumbprint);
		Append(
			canonical,
			"prompt.structural-fingerprint",
			contract.ExpectedPromptStructuralFingerprint);
		Append(
			canonical,
			"usage.schema-fingerprint",
			contract.UsageLayout.SchemaFingerprint);
		Append(
			canonical,
			"usage.page-count",
			contract.UsageLayout.ExpectedPageFingerprints.Count.ToString(
				CultureInfo.InvariantCulture));

		foreach (string pageFingerprint in
			contract.UsageLayout.ExpectedPageFingerprints)
		{
			Append(
				canonical,
				"usage.page-fingerprint",
				pageFingerprint);
		}

		return Hash(canonical.ToString());
	}

	private static AntigravityReviewedPackageContract NormalizeContract(
		AntigravityReviewedPackageContract contract)
	{
		AntigravityReviewedPackageContract normalized =
			NormalizeContractCore(contract);
		string expectedFingerprint =
			ComputeContractFingerprintCore(normalized);

		if (!string.Equals(
				normalized.ContractFingerprint,
				expectedFingerprint,
				StringComparison.Ordinal))
		{
			throw new JsonException(
				"The reviewed AGY package contract fingerprint is invalid.");
		}

		if (normalized.CompatibleBuilds is not null)
		{
			foreach (AntigravityReviewedPackageCompatibleBuild compatibleBuild in
				normalized.CompatibleBuilds)
			{
				AntigravityReviewedPackageContract expanded = new(
					compatibleBuild.ContractId,
					compatibleBuild.Executable,
					normalized.ExpectedPromptStructuralFingerprint,
					normalized.UsageLayout,
					compatibleBuild.ContractFingerprint);
				string expectedCompatibleFingerprint =
					ComputeContractFingerprintCore(expanded);

				if (!string.Equals(
					compatibleBuild.ContractFingerprint,
					expectedCompatibleFingerprint,
					StringComparison.Ordinal))
				{
					throw new JsonException(
						"The reviewed AGY compatible build fingerprint is invalid.");
				}
			}
		}

		return normalized;
	}

	private static AntigravityReviewedPackageContract NormalizeContractCore(
		AntigravityReviewedPackageContract contract)
	{
		ArgumentNullException.ThrowIfNull(contract);

		if (!IsStableId(contract.ContractId) ||
			!IsCanonicalFingerprint(
				contract.ExpectedPromptStructuralFingerprint,
				Sha256Length))
		{
			throw new JsonException(
				"The reviewed AGY package contract is invalid.");
		}

		AntigravityReviewedPackageExecutable executable =
			NormalizeExecutable(contract.Executable);
		AntigravityReviewedPackageUsageLayout usageLayout =
			NormalizeUsageLayout(contract.UsageLayout);
		IReadOnlyList<AntigravityReviewedPackageCompatibleBuild>?
			compatibleBuilds = NormalizeCompatibleBuilds(
				contract.CompatibleBuilds);
		return contract with
		{
			Executable = executable,
			UsageLayout = usageLayout,
			CompatibleBuilds = compatibleBuilds
		};
	}

	private static IReadOnlyList<AntigravityReviewedPackageCompatibleBuild>?
		NormalizeCompatibleBuilds(
			IReadOnlyList<AntigravityReviewedPackageCompatibleBuild>?
				compatibleBuilds)
	{
		if (compatibleBuilds is null)
		{
			return null;
		}

		if ((compatibleBuilds.Count == 0) ||
			(compatibleBuilds.Count >= MaximumContracts))
		{
			throw new JsonException(
				"The reviewed AGY compatible build list is invalid.");
		}

		AntigravityReviewedPackageCompatibleBuild[] normalized =
			new AntigravityReviewedPackageCompatibleBuild[
				compatibleBuilds.Count];

		for (int index = 0; index < compatibleBuilds.Count; index++)
		{
			AntigravityReviewedPackageCompatibleBuild compatibleBuild =
				compatibleBuilds[index] ?? throw new JsonException(
					"The reviewed AGY compatible build is invalid.");

			if (!IsStableId(compatibleBuild.ContractId) ||
				!IsCanonicalFingerprint(
					compatibleBuild.ContractFingerprint,
					Sha256Length))
			{
				throw new JsonException(
					"The reviewed AGY compatible build is invalid.");
			}

			normalized[index] = compatibleBuild with
			{
				Executable = NormalizeExecutable(compatibleBuild.Executable)
			};
		}

		return Array.AsReadOnly(normalized);
	}

	private static AntigravityReviewedPackageExecutable NormalizeExecutable(
		AntigravityReviewedPackageExecutable executable)
	{
		ArgumentNullException.ThrowIfNull(executable);

		if (!IsPortableText(executable.CliVersion, MaximumVersionLength) ||
			!IsPortableText(executable.FileVersion, MaximumVersionLength) ||
			!IsPortableText(executable.ProductVersion, MaximumVersionLength) ||
			!IsCanonicalFingerprint(executable.Sha256, Sha256Length) ||
			(executable.WinVerifyTrustStatus != WinTrustSuccess) ||
			!IsPortableText(
				executable.SignerSubject,
				MaximumSignerSubjectLength) ||
			!IsCanonicalFingerprint(
				executable.SignerThumbprint,
				Sha1ThumbprintLength))
		{
			throw new JsonException(
				"The reviewed AGY executable metadata is invalid.");
		}

		return executable;
	}

	private static AntigravityReviewedPackageManifestFile NormalizeManifest(
		AntigravityReviewedPackageManifestFile manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);

		if (!string.Equals(
				manifest.FormatVersion,
				AntigravityReviewedPackageManifestFile.CurrentFormatVersion,
				StringComparison.Ordinal) ||
			(manifest.ReviewState != AntigravityUsageR1ReviewState.Reviewed) ||
			(manifest.Contracts is null) ||
			(manifest.Contracts.Count == 0) ||
			(manifest.Contracts.Count > MaximumContracts))
		{
			throw new JsonException(
				"The reviewed AGY package manifest header is invalid.");
		}

		HashSet<string> contractIds = new(StringComparer.Ordinal);
		int contractCount = 0;
		AntigravityReviewedPackageContract[] contracts =
			new AntigravityReviewedPackageContract[manifest.Contracts.Count];

		for (int index = 0; index < manifest.Contracts.Count; index++)
		{
			AntigravityReviewedPackageContract contract =
				NormalizeContract(manifest.Contracts[index]);

			if ((++contractCount > MaximumContracts) ||
				!contractIds.Add(contract.ContractId))
			{
				throw new JsonException(
					"The reviewed AGY package manifest contains a duplicate contract.");
			}

			if (contract.CompatibleBuilds is not null)
			{
				foreach (AntigravityReviewedPackageCompatibleBuild compatibleBuild in
					contract.CompatibleBuilds)
				{
					if ((++contractCount > MaximumContracts) ||
						!contractIds.Add(compatibleBuild.ContractId))
					{
						throw new JsonException(
							"The reviewed AGY package manifest contains a duplicate contract.");
					}
				}
			}

			contracts[index] = contract;
		}

		return manifest with
		{
			Contracts = Array.AsReadOnly(contracts)
		};
	}

	private static AntigravityReviewedPackageUsageLayout NormalizeUsageLayout(
		AntigravityReviewedPackageUsageLayout usageLayout)
	{
		ArgumentNullException.ThrowIfNull(usageLayout);
		AntigravityUsageR1SectionSpec frozen =
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				usageLayout.Spec);
		string schemaFingerprint =
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				frozen);

		if (!IsCanonicalFingerprint(
				usageLayout.SchemaFingerprint,
				Sha256Length) ||
			!string.Equals(
				usageLayout.SchemaFingerprint,
				schemaFingerprint,
				StringComparison.Ordinal) ||
			(usageLayout.ExpectedPageFingerprints is null) ||
			(usageLayout.ExpectedPageFingerprints.Count != 1))
		{
			throw new JsonException(
				"The reviewed AGY package usage layout is invalid.");
		}

		string expectedPageFingerprint =
			AntigravityUsageR1SectionSchemaParser.ComputePageFingerprint(
				frozen);
		string pageFingerprint =
			usageLayout.ExpectedPageFingerprints[0];

		if (!IsCanonicalFingerprint(pageFingerprint, Sha256Length) ||
			!string.Equals(
				pageFingerprint,
				expectedPageFingerprint,
				StringComparison.Ordinal))
		{
			throw new JsonException(
				"The reviewed AGY package page fingerprint is invalid.");
		}

		return usageLayout with
		{
			Spec = frozen,
			ExpectedPageFingerprints = Array.AsReadOnly(
				new[] { pageFingerprint })
		};
	}

	private static void ValidateJsonShape(JsonElement root)
	{
		RequireExactObject(
			root,
			"FormatVersion",
			"ReviewState",
			"Contracts");
		JsonElement contracts = root.GetProperty("Contracts");
		RequireArray(contracts);

		foreach (JsonElement contract in contracts.EnumerateArray())
		{
			bool hasCompatibleBuilds =
				contract.TryGetProperty("CompatibleBuilds", out JsonElement compatibleBuilds);
			RequireExactObject(
				contract,
				hasCompatibleBuilds
					? new[]
					{
						"ContractId",
						"Executable",
						"ExpectedPromptStructuralFingerprint",
						"UsageLayout",
						"ContractFingerprint",
						"CompatibleBuilds"
					}
					: new[]
					{
						"ContractId",
						"Executable",
						"ExpectedPromptStructuralFingerprint",
						"UsageLayout",
						"ContractFingerprint"
					});
			JsonElement executable = contract.GetProperty("Executable");
			ValidateExecutableJsonShape(executable);
			JsonElement usageLayout = contract.GetProperty("UsageLayout");
			RequireExactObject(
				usageLayout,
				"Spec",
				"SchemaFingerprint",
				"ExpectedPageFingerprints");
			ValidateSpecJsonShape(usageLayout.GetProperty("Spec"));

			if (hasCompatibleBuilds)
			{
				RequireArray(compatibleBuilds);

				foreach (JsonElement compatibleBuild in
					compatibleBuilds.EnumerateArray())
				{
					RequireExactObject(
						compatibleBuild,
						"ContractId",
						"Executable",
						"ContractFingerprint");
					ValidateExecutableJsonShape(
						compatibleBuild.GetProperty("Executable"));
				}
			}
		}

		RequireNoObviousPrivateStringValues(root);
	}

	private static void ValidateExecutableJsonShape(JsonElement executable)
	{
		RequireExactObject(
			executable,
			"CliVersion",
			"FileVersion",
			"ProductVersion",
			"Sha256",
			"WinVerifyTrustStatus",
			"SignerSubject",
			"SignerThumbprint");
	}

	private static void RequireNoObviousPrivateStringValues(
		JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				RequireNoObviousPrivateStringValues(property.Value);
			}

			return;
		}

		if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				RequireNoObviousPrivateStringValues(item);
			}

			return;
		}

		if (element.ValueKind != JsonValueKind.String)
		{
			return;
		}

		string value = element.GetString() ?? string.Empty;

		if (EmailAddressPattern.IsMatch(value) ||
			AbsoluteDrivePathPattern.IsMatch(value) ||
			QuotaInstancePattern.IsMatch(value) ||
			value.StartsWith(@"\\", StringComparison.Ordinal) ||
			value.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
			value.Contains("/Users/", StringComparison.OrdinalIgnoreCase) ||
			value.Contains("/home/", StringComparison.OrdinalIgnoreCase) ||
			value.Contains(
				"%USERPROFILE%",
				StringComparison.OrdinalIgnoreCase) ||
			value.Contains(
				"%LOCALAPPDATA%",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new JsonException(
				"The reviewed AGY package manifest contains a non-portable value.");
		}
	}

	private static void ValidateSpecJsonShape(JsonElement spec)
	{
		byte[]? bytes = null;

		try
		{
			using MemoryStream stream = new();

			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteString(
					"FormatVersion",
					AntigravityUsageR1SectionSchemaParser
						.ReviewedSpecFormatVersion);
				writer.WriteString(
					"ReviewState",
					nameof(AntigravityUsageR1ReviewState.Reviewed));
				writer.WritePropertyName("Spec");
				spec.WriteTo(writer);
				writer.WriteString(
					"CaptureContractFingerprint",
					new string('A', Sha256Length));
				writer.WriteString(
					"ApprovedDraftFingerprint",
					new string('B', Sha256Length));
				writer.WriteEndObject();
			}

			bytes = stream.ToArray();
			_ = AntigravityUsageR1SectionCalibrationJson
				.DeserializeReviewedSpec(bytes);
		}
		finally
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
	}

	private static void RequireArray(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException();
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

	private static void Append(
		StringBuilder builder,
		string name,
		string value)
	{
		builder.Append(name.Length.ToString(CultureInfo.InvariantCulture));
		builder.Append(':');
		builder.Append(name);
		builder.Append('=');
		builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
		builder.Append(':');
		builder.Append(value);
		builder.Append('\n');
	}

	private static string Hash(string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);

		try
		{
			return Convert.ToHexString(SHA256.HashData(bytes));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static bool IsCanonicalFingerprint(
		string? value,
		int expectedLength)
	{
		return (value?.Length == expectedLength) &&
			value.All(character =>
				((character >= '0') && (character <= '9')) ||
				((character >= 'A') && (character <= 'F')));
	}

	private static bool IsPortableText(string? value, int maximumLength)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= maximumLength) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
			!value.Any(char.IsControl);
	}

	internal static bool IsStableId(string? value)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= 128) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
			(((value[0] >= 'a') && (value[0] <= 'z')) ||
				((value[0] >= '0') && (value[0] <= '9'))) &&
			value.All(character =>
				((character >= 'a') && (character <= 'z')) ||
				((character >= '0') && (character <= '9')) ||
				(character == '.') ||
				(character == '-') ||
				(character == '_'));
	}
}
