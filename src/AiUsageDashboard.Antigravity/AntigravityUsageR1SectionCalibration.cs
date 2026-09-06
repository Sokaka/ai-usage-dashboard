using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

#if !ANTIGRAVITY_PRODUCTION
internal static class AntigravityUsageR1SectionCalibrationCompiler
{
	private const int MaximumPrivateBundleBytes = 4 * 1024 * 1024;
	private const int MaximumReviewedSpecBytes = 1024 * 1024;
	private const int MaximumScreenLineLength = 4096;
	private const int MaximumSequences = 16;

	internal static async Task<AntigravityUsageR1SectionLayoutFile>
		CompileFromFilesAsync(
			string privateBundlePath,
			string reviewedSpecPath,
			CancellationToken cancellationToken = default)
	{
		AntigravityUsageR1PrivateScreenBundleFile privateBundle =
			await ReadPrivateBundleAsync(
				privateBundlePath,
				cancellationToken);
		AntigravityUsageR1SectionSpecFile reviewedSpecFile =
			await ReadReviewedSpecAsync(
				reviewedSpecPath,
				cancellationToken);

		return Compile(privateBundle, reviewedSpecFile);
	}

	private static AntigravityUsageR1SectionLayoutFile Compile(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		AntigravityUsageR1SectionSpecFile reviewedSpecFile)
	{
		AntigravityUsageR1SectionSpec reviewedSpec;

		try
		{
			if (!string.Equals(
					reviewedSpecFile.FormatVersion,
					AntigravityUsageR1SectionSchemaParser
						.ReviewedSpecFormatVersion,
					StringComparison.Ordinal) ||
				(reviewedSpecFile.ReviewState !=
					AntigravityUsageR1ReviewState.Reviewed))
			{
				throw new InvalidDataException();
			}

			reviewedSpec =
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
					reviewedSpecFile.Spec);
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}

		ValidatePrivateBundle(privateBundle, reviewedSpec);

		if (!string.Equals(
				reviewedSpecFile.CaptureContractFingerprint,
				privateBundle.CaptureContractFingerprint,
				StringComparison.Ordinal))
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}

		List<AntigravityUsageR1SectionParseResult> parseResults = new();
		HashSet<string> pageFingerprints = new(StringComparer.Ordinal);
		HashSet<string> stabilityFingerprints = new(StringComparer.Ordinal);
		byte[] stabilityKey = RandomNumberGenerator.GetBytes(32);

		try
		{
			foreach (AntigravityUsageR1PrivateScreenSequence sequence in
				privateBundle.Sequences)
			{
				AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];
				TerminalScreenSnapshot snapshot = new(
					screen.Columns,
					screen.Rows,
					screen.IsAlternateScreen,
					screen.Lines.ToArray());
				AntigravityUsageR1SectionParseResult result =
					AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
						snapshot,
						reviewedSpec);

				parseResults.Add(result);
				pageFingerprints.Add(result.Capture.PageFingerprint);
				stabilityFingerprints.Add(
					AntigravityUsageR1SectionSchemaParser
						.ComputeStabilityFingerprint(result, stabilityKey));
			}
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(stabilityKey);
		}

		if (pageFingerprints.Count != 1)
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
		}

		if ((stabilityFingerprints.Count < 2) ||
			!HasQuotaSemanticDifference(parseResults))
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.DynamicEvidence);
		}

		try
		{
			string[] expectedPageFingerprints = pageFingerprints
				.OrderBy(fingerprint => fingerprint, StringComparer.Ordinal)
				.ToArray();
			AntigravityUsageR1SectionLayoutFile layoutFile = new(
				AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				reviewedSpec,
				AntigravityUsageR1SectionSchemaParser
					.ComputeSchemaFingerprint(reviewedSpec),
				Array.AsReadOnly(expectedPageFingerprints),
				reviewedSpecFile.CaptureContractFingerprint,
				reviewedSpecFile.ApprovedDraftFingerprint);
			_ = layoutFile.ToLayout();
			return layoutFile;
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.OutputValidation);
		}
	}

	private static bool HasQuotaSemanticDifference(
		IReadOnlyList<AntigravityUsageR1SectionParseResult> results)
	{
		AntigravityUsageR1SectionParseResult baseline = results[0];

		for (int index = 1; index < results.Count; index++)
		{
			if (AntigravityUsageR1SectionSchemaParser
				.HasQuotaSemanticDifference(baseline, results[index]))
			{
				return true;
			}
		}

		return false;
	}

	private static async Task<AntigravityUsageR1PrivateScreenBundleFile>
		ReadPrivateBundleAsync(
			string path,
			CancellationToken cancellationToken)
	{
		byte[]? bytes = null;

		try
		{
			bytes = await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
				path,
				MaximumPrivateBundleBytes,
				requirePrivateFile: true,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleRead);
		}

		try
		{
			return AntigravityUsageR1CalibrationJson
				.DeserializePrivateBundle(bytes);
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static async Task<AntigravityUsageR1SectionSpecFile>
		ReadReviewedSpecAsync(
			string path,
			CancellationToken cancellationToken)
	{
		byte[]? bytes = null;

		try
		{
			bytes = await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
				path,
				MaximumReviewedSpecBytes,
				requirePrivateFile: false,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecRead);
		}

		try
		{
			return AntigravityUsageR1SectionCalibrationJson
				.DeserializeReviewedSpec(bytes);
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static void ValidatePrivateBundle(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		AntigravityUsageR1SectionSpec reviewedSpec)
	{
		try
		{
			if (!string.Equals(
					privateBundle.FormatVersion,
					AntigravityUsageR1CalibrationCompiler
						.PrivateBundleFormatVersion,
					StringComparison.Ordinal) ||
				!AntigravityUsageR1SchemaParser.IsFingerprint(
					privateBundle.CaptureContractFingerprint) ||
				(privateBundle.Sequences is null) ||
				(privateBundle.Sequences.Count < 2) ||
				(privateBundle.Sequences.Count > MaximumSequences))
			{
				throw new InvalidDataException();
			}

			int? columns = null;
			int? rows = null;
			bool? isAlternateScreen = null;

			foreach (AntigravityUsageR1PrivateScreenSequence sequence in
				privateBundle.Sequences)
			{
				if ((sequence is null) ||
					(sequence.Pages is null) ||
					(sequence.Pages.Count != 1))
				{
					throw new InvalidDataException();
				}

				AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];

				if ((screen is null) ||
					(screen.Columns != reviewedSpec.Columns) ||
					(screen.Rows != reviewedSpec.Rows) ||
					(screen.IsAlternateScreen !=
						reviewedSpec.IsAlternateScreen) ||
					(screen.Lines is null) ||
					(screen.Lines.Count != screen.Rows) ||
					screen.Lines.Any(line =>
						(line is null) ||
						(line.Length > MaximumScreenLineLength)))
				{
					throw new InvalidDataException();
				}

				columns ??= screen.Columns;
				rows ??= screen.Rows;
				isAlternateScreen ??= screen.IsAlternateScreen;

				if ((screen.Columns != columns.Value) ||
					(screen.Rows != rows.Value) ||
					(screen.IsAlternateScreen != isAlternateScreen.Value))
				{
					throw new InvalidDataException();
				}
			}
		}
		catch
		{
			throw CreateFailure(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
		}
	}

	private static AntigravityUsageR1CalibrationException CreateFailure(
		AntigravityUsageR1CalibrationFailureStage stage)
	{
		return new AntigravityUsageR1CalibrationException(stage);
	}
}

internal static class AntigravityUsageR1SectionLayoutWriter
{
	private const int MaximumReviewedLayoutBytes = 1024 * 1024;

	internal static async Task<bool> TryWriteAsync(
		string outputPath,
		AntigravityUsageR1SectionLayoutFile layoutFile,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(layoutFile);
		byte[]? serializedBytes = null;
		string? temporaryPath = null;
		bool ownsTemporaryFile = false;

		try
		{
			_ = layoutFile.ToLayout();
			serializedBytes = AntigravityUsageR1SectionCalibrationJson
				.SerializeLayout(layoutFile);

			if ((serializedBytes.Length == 0) ||
				(serializedBytes.Length > MaximumReviewedLayoutBytes) ||
				!AntigravityUsageR1CalibrationFile.TryResolveOutputPath(
					outputPath,
					out string absoluteOutputPath,
					out string parentPath))
			{
				return false;
			}

			if (File.Exists(absoluteOutputPath))
			{
				return await HasExactExistingBytesAsync(
					absoluteOutputPath,
					serializedBytes,
					cancellationToken);
			}

			temporaryPath = Path.Combine(
				parentPath,
				$".{Path.GetFileName(absoluteOutputPath)}.r1-section-layout.{Guid.NewGuid():N}.tmp");

			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				ownsTemporaryFile = true;
				await stream.WriteAsync(serializedBytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
				stream.Flush(flushToDisk: true);
			}

			if (!await HasExactExistingBytesAsync(
					temporaryPath,
					serializedBytes,
					cancellationToken))
			{
				return false;
			}

			if (File.Exists(absoluteOutputPath))
			{
				return await HasExactExistingBytesAsync(
					absoluteOutputPath,
					serializedBytes,
					cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				File.Move(temporaryPath, absoluteOutputPath);
				ownsTemporaryFile = false;
				temporaryPath = null;
			}
			catch (IOException)
			{
				return File.Exists(absoluteOutputPath) &&
					await HasExactExistingBytesAsync(
						absoluteOutputPath,
						serializedBytes,
						cancellationToken);
			}

			return await HasExactExistingBytesAsync(
				absoluteOutputPath,
				serializedBytes,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (ownsTemporaryFile && (temporaryPath is not null))
			{
				AntigravityUsageR1CalibrationFile.TryDelete(temporaryPath);
			}

			if (serializedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(serializedBytes);
			}
		}
	}

	private static async Task<bool> HasExactExistingBytesAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		byte[]? existingBytes = null;

		try
		{
			existingBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					path,
					MaximumReviewedLayoutBytes,
					requirePrivateFile: false,
					cancellationToken);

			return existingBytes.AsSpan().SequenceEqual(expectedBytes.Span) &&
				AntigravityUsageR1SectionCalibrationJson.IsStrictLayout(
					existingBytes);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (existingBytes is not null)
			{
				CryptographicOperations.ZeroMemory(existingBytes);
			}
		}
	}
}
#endif

#if ANTIGRAVITY_PRODUCTION
internal static class AntigravityUsageR1SectionCalibrationJson
{
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 64
	};
	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		AllowTrailingCommas = false,
		MaxDepth = 64,
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
		MaxDepth = 64,
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static AntigravityUsageR1SectionSpecFile DeserializeReviewedSpec(
		ReadOnlyMemory<byte> bytes)
	{
		using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
		JsonElement root = document.RootElement;
		RequireExactObject(
			root,
			"FormatVersion",
			"ReviewState",
			"Spec",
			"CaptureContractFingerprint",
			"ApprovedDraftFingerprint");
		RequireExactString(
			root.GetProperty("FormatVersion"),
			AntigravityUsageR1SectionSchemaParser.ReviewedSpecFormatVersion);
		RequireExactEnum<AntigravityUsageR1ReviewState>(
			root.GetProperty("ReviewState"),
			AntigravityUsageR1ReviewState.Reviewed);
		RequireString(root.GetProperty("CaptureContractFingerprint"));
		RequireString(root.GetProperty("ApprovedDraftFingerprint"));
		ValidateSpec(root.GetProperty("Spec"));

		AntigravityUsageR1SectionSpecFile specFile =
			JsonSerializer.Deserialize<AntigravityUsageR1SectionSpecFile>(
				bytes.Span,
				ReadOptions) ?? throw new JsonException();

		if (!AntigravityUsageR1SchemaParser.IsFingerprint(
				specFile.CaptureContractFingerprint) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				specFile.ApprovedDraftFingerprint))
		{
			throw new JsonException();
		}

		return specFile;
	}

	internal static byte[] SerializeReviewedSpec(
		AntigravityUsageR1SectionSpecFile specFile)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			specFile,
			WriteOptions);

		try
		{
			AntigravityUsageR1SectionSpecFile roundTrip =
				DeserializeReviewedSpec(bytes);
			_ = AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				roundTrip.Spec);
			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
	}

	internal static bool IsStrictLayout(ReadOnlyMemory<byte> bytes)
	{
		try
		{
			_ = DeserializeLayout(bytes);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static AntigravityUsageR1SectionLayoutFile DeserializeLayout(
		ReadOnlyMemory<byte> bytes)
	{
		using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
		JsonElement root = document.RootElement;
		RequireExactObject(
			root,
			"FormatVersion",
			"ReviewState",
			"Spec",
			"SchemaFingerprint",
			"ExpectedPageFingerprints",
			"CaptureContractFingerprint",
			"ApprovedDraftFingerprint");
		RequireExactString(
			root.GetProperty("FormatVersion"),
			AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion);
		RequireExactEnum<AntigravityUsageR1ReviewState>(
			root.GetProperty("ReviewState"),
			AntigravityUsageR1ReviewState.Reviewed);
		ValidateSpec(root.GetProperty("Spec"));
		RequireString(root.GetProperty("SchemaFingerprint"));
		RequireStringArray(root.GetProperty("ExpectedPageFingerprints"));
		RequireString(root.GetProperty("CaptureContractFingerprint"));
		RequireString(root.GetProperty("ApprovedDraftFingerprint"));
		AntigravityUsageR1SectionLayoutFile layoutFile =
			JsonSerializer.Deserialize<AntigravityUsageR1SectionLayoutFile>(
				bytes.Span,
				ReadOptions) ?? throw new JsonException();
		_ = layoutFile.ToLayout();
		return layoutFile;
	}

	internal static byte[] SerializeLayout(
		AntigravityUsageR1SectionLayoutFile layoutFile)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			layoutFile,
			WriteOptions);

		if (!IsStrictLayout(bytes))
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw new JsonException();
		}

		return bytes;
	}

	private static void ValidateSpec(JsonElement spec)
	{
		RequireExactObject(
			spec,
			"SchemaVersion",
			"LayoutId",
			"Columns",
			"Rows",
			"IsAlternateScreen",
			"Sections",
			"Lines",
			"PaginationPolicy",
			"PaginationMode");
		RequireNumber(spec.GetProperty("SchemaVersion"));
		RequireString(spec.GetProperty("LayoutId"));
		RequireNumber(spec.GetProperty("Columns"));
		RequireNumber(spec.GetProperty("Rows"));
		RequireBoolean(spec.GetProperty("IsAlternateScreen"));
		RequireExactEnum<AntigravityUsageR1SectionPaginationPolicy>(
			spec.GetProperty("PaginationPolicy"));
		RequireExactEnum<AntigravityUsageR1SectionPaginationMode>(
			spec.GetProperty("PaginationMode"));
		ValidateSections(spec.GetProperty("Sections"));
		ValidateLines(spec.GetProperty("Lines"));
	}

	private static void ValidateSections(JsonElement sections)
	{
		RequireArray(sections);

		foreach (JsonElement section in sections.EnumerateArray())
		{
			RequireExactObject(
				section,
				"StableSectionId",
				"RenderedLiteral",
				"Order",
				"Windows");
			RequireString(section.GetProperty("StableSectionId"));
			RequireString(section.GetProperty("RenderedLiteral"));
			RequireNumber(section.GetProperty("Order"));
			ValidateWindows(section.GetProperty("Windows"));
		}
	}

	private static void ValidateWindows(JsonElement windows)
	{
		RequireArray(windows);

		foreach (JsonElement window in windows.EnumerateArray())
		{
			RequireExactObject(
				window,
				"StableWindowId",
				"RenderedLiteral",
				"WindowKind",
				"WindowHours",
				"Order",
				"CapacityPolicy",
				"ResetPolicy");
			RequireString(window.GetProperty("StableWindowId"));
			RequireString(window.GetProperty("RenderedLiteral"));
			RequireExactEnum<AntigravityUsageR1SectionWindowKind>(
				window.GetProperty("WindowKind"));
			RequireNumberOrNull(window.GetProperty("WindowHours"));
			RequireNumber(window.GetProperty("Order"));
			RequireExactEnum<AntigravityUsageR1SectionCapacityPolicy>(
				window.GetProperty("CapacityPolicy"));
			RequireExactEnum<AntigravityUsageR1SectionResetPolicy>(
				window.GetProperty("ResetPolicy"));
		}
	}

	private static void ValidateLines(JsonElement lines)
	{
		RequireArray(lines);

		foreach (JsonElement line in lines.EnumerateArray())
		{
			RequireExactObject(
				line,
				"RowIndex",
				"StableLineId",
				"Role",
				"StableSectionId",
				"StableWindowId",
				"Alternatives");
			RequireNumber(line.GetProperty("RowIndex"));
			RequireString(line.GetProperty("StableLineId"));
			RequireExactEnum<AntigravityUsageR1SectionLineRole>(
				line.GetProperty("Role"));
			RequireStringOrNull(line.GetProperty("StableSectionId"));
			RequireStringOrNull(line.GetProperty("StableWindowId"));
			ValidatePatterns(line.GetProperty("Alternatives"));
		}
	}

	private static void ValidatePatterns(JsonElement patterns)
	{
		RequireArray(patterns);

		foreach (JsonElement pattern in patterns.EnumerateArray())
		{
			RequireExactObject(pattern, "PatternId", "Tokens");
			RequireString(pattern.GetProperty("PatternId"));
			ValidateTokens(pattern.GetProperty("Tokens"));
		}
	}

	private static void ValidateTokens(JsonElement tokens)
	{
		RequireArray(tokens);

		foreach (JsonElement token in tokens.EnumerateArray())
		{
			if (token.ValueKind != JsonValueKind.Object)
			{
				throw new JsonException();
			}

			JsonElement kindElement = token.GetProperty("Kind");
			RequireString(kindElement);

			switch (kindElement.GetString())
			{
				case "Literal":
					RequireExactObject(token, "Kind", "Value");
					RequireString(token.GetProperty("Value"));
					break;
				case "Identity":
					RequireExactObject(token, "Kind");
					break;
				case "Percent":
					RequireExactObject(
						token,
						"Kind",
						"Meaning",
						"DecimalPlaces");
					RequireExactEnum<AntigravityUsageR1SectionPercentMeaning>(
						token.GetProperty("Meaning"));
					RequireNumber(token.GetProperty("DecimalPlaces"));
					break;
				case "Countdown":
					RequireExactObject(token, "Kind", "Rule");
					ValidateCountdownRule(token.GetProperty("Rule"));
					break;
				case "Availability":
					RequireExactObject(token, "Kind", "Values");
					ValidateAvailabilityRules(token.GetProperty("Values"));
					break;
				case "ProgressMeter":
					RequireExactObject(token, "Kind", "Rule");
					ValidateMeterRule(token.GetProperty("Rule"));
					break;
				case "PaginationNumber":
					RequireExactObject(token, "Kind", "Field");
					RequireExactEnum<AntigravityUsageR1SectionPaginationField>(
						token.GetProperty("Field"));
					break;
				case "Opaque":
					RequireExactObject(token, "Kind", "MaximumLength");
					RequireNumber(token.GetProperty("MaximumLength"));
					break;
				case "UnsignedAmount":
					RequireExactObject(
						token,
						"Kind",
						"MinimumDigits",
						"MaximumDigits");
					RequireNumber(token.GetProperty("MinimumDigits"));
					RequireNumber(token.GetProperty("MaximumDigits"));
					break;
				case "OuterLineShape":
					RequireExactObject(
						token,
						"Kind",
						"PublicPrefix",
						"PublicSuffix",
						"MinimumDynamicLength",
						"MaximumDynamicLength",
						"AllowedCharacters");
					RequireStringOrNull(token.GetProperty("PublicPrefix"));
					RequireStringOrNull(token.GetProperty("PublicSuffix"));
					RequireNumber(token.GetProperty("MinimumDynamicLength"));
					RequireNumber(token.GetProperty("MaximumDynamicLength"));
					RequireExactEnum<
						AntigravityUsageR1SectionOuterLineCharacterClass>(
							token.GetProperty("AllowedCharacters"));
					break;
				default:
					throw new JsonException();
			}
		}
	}

	private static void ValidateCountdownRule(JsonElement rule)
	{
		RequireExactObject(
			rule,
			"Separator",
			"Units",
			"MaximumTotalMinutes");
		RequireString(rule.GetProperty("Separator"));
		RequireNumber(rule.GetProperty("MaximumTotalMinutes"));
		JsonElement units = rule.GetProperty("Units");
		RequireArray(units);

		foreach (JsonElement unit in units.EnumerateArray())
		{
			RequireExactObject(
				unit,
				"Unit",
				"SingularLiteral",
				"PluralLiteral",
				"Order");
			RequireExactEnum<AntigravityUsageR1SectionCountdownUnit>(
				unit.GetProperty("Unit"));
			RequireString(unit.GetProperty("SingularLiteral"));
			RequireString(unit.GetProperty("PluralLiteral"));
			RequireNumber(unit.GetProperty("Order"));
		}
	}

	private static void ValidateAvailabilityRules(JsonElement values)
	{
		RequireArray(values);

		foreach (JsonElement value in values.EnumerateArray())
		{
			RequireExactObject(
				value,
				"StableStatusId",
				"RenderedLiteral");
			RequireString(value.GetProperty("StableStatusId"));
			RequireString(value.GetProperty("RenderedLiteral"));
		}
	}

	private static void ValidateMeterRule(JsonElement rule)
	{
		RequireExactObject(
			rule,
			"Width",
			"FilledGlyph",
			"EmptyGlyph",
			"Meaning",
			"Rounding");
		RequireNumber(rule.GetProperty("Width"));
		RequireString(rule.GetProperty("FilledGlyph"));
		RequireString(rule.GetProperty("EmptyGlyph"));
		RequireExactEnum<AntigravityUsageR1SectionPercentMeaning>(
			rule.GetProperty("Meaning"));
		RequireExactEnum<AntigravityUsageR1SectionMeterRounding>(
			rule.GetProperty("Rounding"));
	}

	private static void RequireArray(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException();
		}
	}

	private static void RequireBoolean(JsonElement element)
	{
		if ((element.ValueKind != JsonValueKind.True) &&
			(element.ValueKind != JsonValueKind.False))
		{
			throw new JsonException();
		}
	}

	private static void RequireExactEnum<TEnum>(JsonElement element)
		where TEnum : struct, Enum
	{
		RequireString(element);
		string value = element.GetString() ?? throw new JsonException();

		if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
			!string.Equals(
				Enum.GetName(parsed),
				value,
				StringComparison.Ordinal))
		{
			throw new JsonException();
		}
	}

	private static void RequireExactEnum<TEnum>(
		JsonElement element,
		TEnum expected)
		where TEnum : struct, Enum
	{
		RequireExactEnum<TEnum>(element);

		if (!EqualityComparer<TEnum>.Default.Equals(
				Enum.Parse<TEnum>(element.GetString()!, ignoreCase: false),
				expected))
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

	private static void RequireExactString(
		JsonElement element,
		string expected)
	{
		if ((element.ValueKind != JsonValueKind.String) ||
			!string.Equals(
				element.GetString(),
				expected,
				StringComparison.Ordinal))
		{
			throw new JsonException();
		}
	}

	private static void RequireNumber(JsonElement element)
	{
		if ((element.ValueKind != JsonValueKind.Number) ||
			!element.TryGetInt32(out _))
		{
			throw new JsonException();
		}
	}

	private static void RequireNumberOrNull(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Null)
		{
			return;
		}

		RequireNumber(element);
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
		RequireArray(element);

		foreach (JsonElement item in element.EnumerateArray())
		{
			RequireString(item);
		}
	}

	private static void RequireStringOrNull(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Null)
		{
			return;
		}

		RequireString(element);
	}
}
#endif
