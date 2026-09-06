using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsageR1CalibrationFailureStage
{
	PrivateBundleRead,
	PrivateBundleFormat,
	ReviewedSpecRead,
	ReviewedSpecFormat,
	ScreenValidation,
	DynamicEvidence,
	OutputValidation
}

internal sealed record AntigravityUsageR1PrivateScreen(
	int Columns,
	int Rows,
	bool IsAlternateScreen,
	IReadOnlyList<string> Lines);

internal sealed record AntigravityUsageR1PrivateScreenSequence(
	IReadOnlyList<AntigravityUsageR1PrivateScreen> Pages);

internal sealed record AntigravityUsageR1PrivateScreenBundleFile(
	string FormatVersion,
	string CaptureContractFingerprint,
	IReadOnlyList<AntigravityUsageR1PrivateScreenSequence> Sequences);

internal sealed class AntigravityUsageR1CalibrationException :
	IOException
{
	internal AntigravityUsageR1CalibrationFailureStage Stage { get; }

	internal AntigravityUsageR1CalibrationException(
		AntigravityUsageR1CalibrationFailureStage stage)
		: base($"The offline AGY R1 usage calibration failed at stage: {stage}.")
	{
		Stage = stage;
	}
}

internal static class AntigravityUsageR1CalibrationCompiler
{
	internal const string PrivateBundleFormatVersion =
		"agy-usage-r1-private-screen-bundle-v2";
	private const int MaximumPrivateBundleBytes = 4 * 1024 * 1024;
	private const int MaximumReviewedSpecBytes = 256 * 1024;
	private const int MaximumScreenLineLength = 4096;
	private const int MaximumSequences = 16;

	internal static async Task<AntigravityUsageR1ReviewedLayoutFile>
		CompileFromFilesAsync(
			string privateBundlePath,
			string reviewedSpecPath,
			CancellationToken cancellationToken = default)
	{
		AntigravityUsageR1PrivateScreenBundleFile privateBundle =
			await ReadPrivateBundleAsync(
				privateBundlePath,
				cancellationToken);
		AntigravityUsageR1ReviewedSpecFile reviewedSpecFile =
			await ReadReviewedSpecAsync(
				reviewedSpecPath,
				cancellationToken);

		return Compile(privateBundle, reviewedSpecFile);
	}

	private static AntigravityUsageR1ReviewedLayoutFile Compile(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		AntigravityUsageR1ReviewedSpecFile reviewedSpecFile)
	{
		AntigravityUsageR1ReviewedSpec reviewedSpec;

		try
		{
			if (!string.Equals(
					reviewedSpecFile.FormatVersion,
					AntigravityUsageR1SchemaParser.ReviewedSpecFormatVersion,
					StringComparison.Ordinal) ||
				(reviewedSpecFile.ReviewState !=
					AntigravityUsageR1ReviewState.Reviewed))
			{
				throw new InvalidDataException();
			}

			reviewedSpec =
				AntigravityUsageR1SchemaParser.FreezeAndValidateSpec(
					reviewedSpecFile.Spec);
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}

		ValidatePrivateBundle(privateBundle, reviewedSpec);
		List<AntigravityUsageR1ParseResult> parseResults = new();
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
				AntigravityUsageR1ParseResult result =
					AntigravityUsageR1SchemaParser.ParseCalibrationPage(
						snapshot,
						reviewedSpec);

				if (result.Capture.PageKind != AntigravityUsagePageKind.Single)
				{
					throw new InvalidDataException();
				}

				parseResults.Add(result);
				pageFingerprints.Add(result.Capture.PageFingerprint);
				stabilityFingerprints.Add(
					AntigravityUsageR1SchemaParser.ComputeStabilityFingerprint(
						result,
						stabilityKey));
			}
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(stabilityKey);
		}

		if (pageFingerprints.Count != 1)
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
		}

		if ((stabilityFingerprints.Count < 2) ||
			!HasDifferentDynamicEvidence(parseResults))
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.DynamicEvidence);
		}

		try
		{
			string[] expectedPageFingerprints = pageFingerprints
				.OrderBy(fingerprint => fingerprint, StringComparer.Ordinal)
				.ToArray();
			AntigravityUsageR1ReviewedLayoutFile reviewedLayout = new(
				AntigravityUsageR1SchemaParser.LayoutFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				reviewedSpec,
				AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(
					reviewedSpec),
				Array.AsReadOnly(expectedPageFingerprints));
			_ = reviewedLayout.ToLayout();
			return reviewedLayout;
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.OutputValidation);
		}
	}

	private static bool HasDifferentDynamicEvidence(
		IReadOnlyList<AntigravityUsageR1ParseResult> results)
	{
		AntigravityUsageR1ParseResult baseline = results[0];
		Dictionary<string, AntigravityQuotaRow> baselineRows =
			baseline.Page.Rows.ToDictionary(
				row => row.StableModelId,
				StringComparer.Ordinal);

		for (int index = 1; index < results.Count; index++)
		{
			AntigravityUsageR1ParseResult candidate = results[index];

			if (!string.Equals(
					baseline.Page.AccountIdentity,
					candidate.Page.AccountIdentity,
					StringComparison.Ordinal))
			{
				return true;
			}

			foreach (AntigravityQuotaRow candidateRow in candidate.Page.Rows)
			{
				if (baselineRows.TryGetValue(
						candidateRow.StableModelId,
						out AntigravityQuotaRow? baselineRow) &&
					((baselineRow.UsedPercent != candidateRow.UsedPercent) ||
					 (baselineRow.RemainingPercent !=
						candidateRow.RemainingPercent) ||
					 (baselineRow.ResetsAt != candidateRow.ResetsAt)))
				{
					return true;
				}
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
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleRead);
		}

		try
		{
			return AntigravityUsageR1CalibrationJson
				.DeserializePrivateBundle(bytes);
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static async Task<AntigravityUsageR1ReviewedSpecFile>
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
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecRead);
		}

		try
		{
			return AntigravityUsageR1CalibrationJson
				.DeserializeReviewedSpec(bytes);
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static void ValidatePrivateBundle(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		AntigravityUsageR1ReviewedSpec reviewedSpec)
	{
		try
		{
			if (!string.Equals(
					privateBundle.FormatVersion,
					PrivateBundleFormatVersion,
					StringComparison.Ordinal) ||
				!AntigravityUsageR1SchemaParser.IsFingerprint(
					privateBundle.CaptureContractFingerprint) ||
				(privateBundle.Sequences is null) ||
				(privateBundle.Sequences.Count < 2) ||
				(privateBundle.Sequences.Count > MaximumSequences))
			{
				throw new InvalidDataException();
			}

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
			}
		}
		catch
		{
			throw new AntigravityUsageR1CalibrationException(
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
		}
	}
}

internal static class AntigravityUsageR1ReviewedLayoutWriter
{
	private const int MaximumReviewedLayoutBytes = 256 * 1024;

	internal static async Task<bool> TryWriteAsync(
		string outputPath,
		AntigravityUsageR1ReviewedLayoutFile reviewedLayout,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(reviewedLayout);
		byte[] serializedBytes;
		string absoluteOutputPath;
		string parentPath;
		string? temporaryPath = null;
		bool ownsTemporaryFile = false;

		try
		{
			_ = reviewedLayout.ToLayout();
			serializedBytes = AntigravityUsageR1CalibrationJson
				.SerializeReviewedLayout(reviewedLayout);

			if ((serializedBytes.Length == 0) ||
				(serializedBytes.Length > MaximumReviewedLayoutBytes) ||
				!AntigravityUsageR1CalibrationFile.TryResolveOutputPath(
					outputPath,
					out absoluteOutputPath,
					out parentPath))
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
				$".{Path.GetFileName(absoluteOutputPath)}.r1-layout.{Guid.NewGuid():N}.tmp");

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
		}
	}

	private static async Task<bool> HasExactExistingBytesAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			byte[] existingBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					path,
					MaximumReviewedLayoutBytes,
					requirePrivateFile: false,
					cancellationToken);

			return existingBytes.AsSpan().SequenceEqual(expectedBytes.Span) &&
				AntigravityUsageR1CalibrationJson.IsStrictReviewedLayout(
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
	}
}

internal static class AntigravityUsageR1CalibrationFile
{
	internal static async Task<byte[]> ReadBoundedAsync(
		string path,
		int maximumBytes,
		bool requirePrivateFile,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(path) ||
			!Path.IsPathFullyQualified(path))
		{
			throw new InvalidDataException();
		}

		string fullPath = Path.GetFullPath(path);
		FileAttributes attributes = File.GetAttributes(fullPath);

		if (((attributes &
			(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) ||
			(requirePrivateFile &&
			 !AntigravityPrivateKeyAcl.IsPrivateFile(fullPath)))
		{
			throw new InvalidDataException();
		}

		await using FileStream stream = new(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		if ((stream.Length <= 0) || (stream.Length > maximumBytes))
		{
			throw new InvalidDataException();
		}

		byte[] bytes = new byte[checked((int)stream.Length)];

		try
		{
			await stream.ReadExactlyAsync(bytes, cancellationToken);
			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
	}

	internal static bool TryResolveOutputPath(
		string path,
		out string absolutePath,
		out string parentPath)
	{
		absolutePath = string.Empty;
		parentPath = string.Empty;

		try
		{
			if (string.IsNullOrWhiteSpace(path) ||
				!Path.IsPathFullyQualified(path) ||
				!string.Equals(
					Path.GetExtension(path),
					".json",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			absolutePath = Path.GetFullPath(path);
			parentPath = Path.GetDirectoryName(absolutePath) ?? string.Empty;

			if (!IsNonReparseDirectoryChain(parentPath))
			{
				return false;
			}

			if (File.Exists(absolutePath))
			{
				FileAttributes attributes = File.GetAttributes(absolutePath);
				return (attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
			}

			return !Directory.Exists(absolutePath);
		}
		catch
		{
			absolutePath = string.Empty;
			parentPath = string.Empty;
			return false;
		}
	}

	internal static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path) &&
				((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0))
			{
				File.Delete(path);
			}
		}
		catch
		{
			// Cleanup must never mask the fail-closed result.
		}
	}

	private static bool IsNonReparseDirectoryChain(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return false;
		}

		DirectoryInfo? directory = new(Path.GetFullPath(path));

		while (directory is not null)
		{
			FileAttributes attributes = directory.Attributes;

			if (((attributes & FileAttributes.Directory) == 0) ||
				((attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			directory = directory.Parent;
		}

		return true;
	}
}

internal static class AntigravityUsageR1CalibrationJson
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
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		MaxDepth = 32,
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static AntigravityUsageR1PrivateScreenBundleFile
		DeserializePrivateBundle(ReadOnlyMemory<byte> bytes)
	{
		using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
		JsonElement root = document.RootElement;
		RequireExactObject(
			root,
			"FormatVersion",
			"CaptureContractFingerprint",
			"Sequences");

		if ((root.GetProperty("FormatVersion").ValueKind !=
				JsonValueKind.String) ||
			!string.Equals(
				root.GetProperty("FormatVersion").GetString(),
				AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
				StringComparison.Ordinal))
		{
			throw new JsonException();
		}

		JsonElement captureContractFingerprint =
			root.GetProperty("CaptureContractFingerprint");

		if ((captureContractFingerprint.ValueKind != JsonValueKind.String) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				captureContractFingerprint.GetString()))
		{
			throw new JsonException();
		}

		JsonElement sequences = root.GetProperty("Sequences");

		if (sequences.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException();
		}

		foreach (JsonElement sequence in sequences.EnumerateArray())
		{
			RequireExactObject(sequence, "Pages");
			JsonElement pages = sequence.GetProperty("Pages");

			if (pages.ValueKind != JsonValueKind.Array)
			{
				throw new JsonException();
			}

			foreach (JsonElement page in pages.EnumerateArray())
			{
				RequireExactObject(
					page,
					"Columns",
					"Rows",
					"IsAlternateScreen",
					"Lines");
				RequireNumber(page.GetProperty("Columns"));
				RequireNumber(page.GetProperty("Rows"));
				RequireBoolean(page.GetProperty("IsAlternateScreen"));
				RequireStringArray(page.GetProperty("Lines"));
			}
		}

		return JsonSerializer.Deserialize<
			AntigravityUsageR1PrivateScreenBundleFile>(
				bytes.Span,
				ReadOptions) ?? throw new JsonException();
	}

	internal static AntigravityUsageR1ReviewedSpecFile
		DeserializeReviewedSpec(ReadOnlyMemory<byte> bytes)
	{
		using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
		JsonElement root = document.RootElement;
		RequireExactObject(root, "FormatVersion", "ReviewState", "Spec");
		RequireExactString(
			root.GetProperty("FormatVersion"),
			AntigravityUsageR1SchemaParser.ReviewedSpecFormatVersion);
		RequireExactString(
			root.GetProperty("ReviewState"),
			nameof(AntigravityUsageR1ReviewState.Reviewed));
		ValidateSpecElement(root.GetProperty("Spec"));

		return JsonSerializer.Deserialize<AntigravityUsageR1ReviewedSpecFile>(
			bytes.Span,
			ReadOptions) ?? throw new JsonException();
	}

	internal static bool IsStrictReviewedLayout(ReadOnlyMemory<byte> bytes)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(
				bytes,
				DocumentOptions);
			JsonElement root = document.RootElement;
			RequireExactObject(
				root,
				"FormatVersion",
				"ReviewState",
				"Spec",
				"SchemaFingerprint",
				"ExpectedPageFingerprints");
			RequireExactString(
				root.GetProperty("FormatVersion"),
				AntigravityUsageR1SchemaParser.LayoutFormatVersion);
			RequireExactString(
				root.GetProperty("ReviewState"),
				nameof(AntigravityUsageR1ReviewState.Reviewed));
			ValidateSpecElement(root.GetProperty("Spec"));
			RequireString(root.GetProperty("SchemaFingerprint"));
			RequireStringArray(root.GetProperty("ExpectedPageFingerprints"));
			AntigravityUsageR1ReviewedLayoutFile reviewedLayout =
				JsonSerializer.Deserialize<
					AntigravityUsageR1ReviewedLayoutFile>(
						bytes.Span,
						ReadOptions) ?? throw new JsonException();
			_ = reviewedLayout.ToLayout();
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static byte[] SerializeReviewedLayout(
		AntigravityUsageR1ReviewedLayoutFile reviewedLayout)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			reviewedLayout,
			WriteOptions);

		if (!IsStrictReviewedLayout(bytes))
		{
			throw new JsonException();
		}

		return bytes;
	}

	private static void RequireBoolean(JsonElement element)
	{
		if ((element.ValueKind != JsonValueKind.True) &&
			(element.ValueKind != JsonValueKind.False))
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

	private static void ValidateSpecElement(JsonElement spec)
	{
		RequireExactObject(
			spec,
			"SchemaVersion",
			"LayoutId",
			"Columns",
			"Rows",
			"IsAlternateScreen",
			"PrefixLines",
			"PanelTitle",
			"AccountPrefix",
			"PagePrefix",
			"TableHeader",
			"PanelFooter",
			"FieldSeparator",
			"SuffixLines",
			"Models",
			"PaginationPolicy");
		RequireNumber(spec.GetProperty("SchemaVersion"));
		RequireString(spec.GetProperty("LayoutId"));
		RequireNumber(spec.GetProperty("Columns"));
		RequireNumber(spec.GetProperty("Rows"));
		RequireBoolean(spec.GetProperty("IsAlternateScreen"));
		RequireStringArray(spec.GetProperty("PrefixLines"));
		RequireString(spec.GetProperty("PanelTitle"));
		RequireString(spec.GetProperty("AccountPrefix"));
		RequireString(spec.GetProperty("PagePrefix"));
		RequireString(spec.GetProperty("TableHeader"));
		RequireString(spec.GetProperty("PanelFooter"));
		RequireString(spec.GetProperty("FieldSeparator"));
		RequireStringArray(spec.GetProperty("SuffixLines"));
		RequireExactString(
			spec.GetProperty("PaginationPolicy"),
			nameof(AntigravityUsageR1PaginationPolicy.RequireSinglePage));
		JsonElement models = spec.GetProperty("Models");

		if (models.ValueKind != JsonValueKind.Array)
		{
			throw new JsonException();
		}

		foreach (JsonElement model in models.EnumerateArray())
		{
			RequireExactObject(
				model,
				"StableModelId",
				"RenderedLiteral",
				"IsRequired",
				"Order");
			RequireString(model.GetProperty("StableModelId"));
			RequireString(model.GetProperty("RenderedLiteral"));
			RequireBoolean(model.GetProperty("IsRequired"));
			RequireNumber(model.GetProperty("Order"));
		}
	}
}
