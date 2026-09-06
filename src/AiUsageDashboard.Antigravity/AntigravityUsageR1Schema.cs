using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsageR1ReviewState
{
	Reviewed,
	NeedsReview
}

internal enum AntigravityUsageR1PaginationPolicy
{
	RequireSinglePage
}

internal sealed record AntigravityUsageR1ModelRule(
	string StableModelId,
	string RenderedLiteral,
	bool IsRequired,
	int Order);

internal sealed record AntigravityUsageR1ReviewedSpec(
	int SchemaVersion,
	string LayoutId,
	int Columns,
	int Rows,
	bool IsAlternateScreen,
	IReadOnlyList<string> PrefixLines,
	string PanelTitle,
	string AccountPrefix,
	string PagePrefix,
	string TableHeader,
	string PanelFooter,
	string FieldSeparator,
	IReadOnlyList<string> SuffixLines,
	IReadOnlyList<AntigravityUsageR1ModelRule> Models,
	AntigravityUsageR1PaginationPolicy PaginationPolicy);

internal sealed record AntigravityUsageR1ReviewedSpecFile(
	string FormatVersion,
	AntigravityUsageR1ReviewState ReviewState,
	AntigravityUsageR1ReviewedSpec Spec);

internal sealed record AntigravityUsageR1ReviewedLayoutFile(
	string FormatVersion,
	AntigravityUsageR1ReviewState ReviewState,
	AntigravityUsageR1ReviewedSpec Spec,
	string SchemaFingerprint,
	IReadOnlyList<string> ExpectedPageFingerprints)
{
	internal AntigravityUsageR1Layout ToLayout()
	{
		if (!string.Equals(
				FormatVersion,
				AntigravityUsageR1SchemaParser.LayoutFormatVersion,
				StringComparison.Ordinal) ||
			(ReviewState != AntigravityUsageR1ReviewState.Reviewed) ||
			(ExpectedPageFingerprints is null) ||
			(ExpectedPageFingerprints.Count != 1) ||
			ExpectedPageFingerprints.Any(fingerprint =>
				!AntigravityUsageR1SchemaParser.IsFingerprint(fingerprint)))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage layout is incomplete.");
		}

		AntigravityUsageR1ReviewedSpec immutableSpec =
			AntigravityUsageR1SchemaParser.FreezeAndValidateSpec(Spec);
		string computedSchemaFingerprint =
			AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(
				immutableSpec);

		if (!string.Equals(
				SchemaFingerprint,
				computedSchemaFingerprint,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage schema fingerprint does not match its contract.");
		}

		FrozenSet<string> expectedPageFingerprints = ExpectedPageFingerprints
			.ToFrozenSet(StringComparer.Ordinal);

		if (expectedPageFingerprints.Count != ExpectedPageFingerprints.Count)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage layout contains duplicate page fingerprints.");
		}

		return new AntigravityUsageR1Layout(
			immutableSpec,
			computedSchemaFingerprint,
			expectedPageFingerprints);
	}
}

internal sealed class AntigravityUsageR1Layout
{
	internal FrozenSet<string> ExpectedPageFingerprints { get; }

	internal string SchemaFingerprint { get; }

	internal AntigravityUsageR1ReviewedSpec Spec { get; }

	internal AntigravityUsageR1Layout(
		AntigravityUsageR1ReviewedSpec spec,
		string schemaFingerprint,
		FrozenSet<string> expectedPageFingerprints)
	{
		Spec = spec;
		SchemaFingerprint = schemaFingerprint;
		ExpectedPageFingerprints = expectedPageFingerprints;
	}
}

internal sealed record AntigravityUsageR1SemanticCapture(
	string LayoutId,
	string SchemaFingerprint,
	string PageFingerprint,
	AntigravityUsagePageKind PageKind,
	IReadOnlyList<string> StableModelIds,
	int RowCount);

internal sealed class AntigravityUsageR1ParseResult
{
	internal AntigravityUsageR1SemanticCapture Capture { get; }

	internal AntigravityUsagePage Page { get; }

	internal AntigravityUsageR1ParseResult(
		AntigravityUsagePage page,
		AntigravityUsageR1SemanticCapture capture)
	{
		Page = page;
		Capture = capture;
	}
}

internal static class AntigravityUsageR1SchemaParser
{
	internal const string LayoutFormatVersion = "agy-usage-r1-layout-v1";
	internal const string ReviewedSpecFormatVersion =
		"agy-usage-r1-reviewed-spec-v1";
	private const int MaximumFixedLineLength = 1000;
	private const int MaximumLayoutIdLength = 128;
	private const int MaximumModelIdLength = 256;
	private const int MaximumModels = 64;
	private const int MaximumScreenColumns = 1000;
	private const int MaximumScreenRows = 256;
	private const int SchemaVersion = 1;

	internal static AntigravityUsageR1ParseResult ParseCalibrationPage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1ReviewedSpec spec)
	{
		AntigravityUsageR1ReviewedSpec immutableSpec =
			FreezeAndValidateSpec(spec);
		return ParseCore(snapshot, immutableSpec);
	}

	internal static AntigravityUsageR1ParseResult ParseReviewedPage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1Layout layout)
	{
		ArgumentNullException.ThrowIfNull(layout);
		string computedSchemaFingerprint = ComputeSchemaFingerprint(layout.Spec);

		if (!string.Equals(
				computedSchemaFingerprint,
				layout.SchemaFingerprint,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The AGY R1 usage schema changed after review.");
		}

		AntigravityUsageR1ParseResult result = ParseCore(snapshot, layout.Spec);

		if (!layout.ExpectedPageFingerprints.Contains(
				result.Capture.PageFingerprint))
		{
			throw new InvalidDataException(
				"The AGY R1 usage page shape is not reviewed.");
		}

		return result;
	}

	internal static string ComputeSchemaFingerprint(
		AntigravityUsageR1ReviewedSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		StringBuilder canonical = new();
		AppendCanonical(canonical, "schema-version", spec.SchemaVersion.ToString(
			CultureInfo.InvariantCulture));
		AppendCanonical(canonical, "layout-id", spec.LayoutId);
		AppendCanonical(canonical, "columns", spec.Columns.ToString(
			CultureInfo.InvariantCulture));
		AppendCanonical(canonical, "rows", spec.Rows.ToString(
			CultureInfo.InvariantCulture));
		AppendCanonical(
			canonical,
			"alternate-screen",
			spec.IsAlternateScreen ? "1" : "0");
		AppendCanonicalLines(canonical, "prefix", spec.PrefixLines);
		AppendCanonical(canonical, "panel-title", spec.PanelTitle);
		AppendCanonical(canonical, "account-prefix", spec.AccountPrefix);
		AppendCanonical(canonical, "page-prefix", spec.PagePrefix);
		AppendCanonical(canonical, "table-header", spec.TableHeader);
		AppendCanonical(canonical, "panel-footer", spec.PanelFooter);
		AppendCanonical(canonical, "field-separator", spec.FieldSeparator);
		AppendCanonicalLines(canonical, "suffix", spec.SuffixLines);
		AppendCanonical(
			canonical,
			"pagination-policy",
			spec.PaginationPolicy.ToString());
		AppendCanonical(
			canonical,
			"line-grammar",
			"ordinal-fixed-lines;trim-end-ascii-space;reject-all-controls;full-screen-owned");
		AppendCanonical(
			canonical,
			"row-grammar",
			"four-fields;ordinal-separator;no-empty-or-edge-space-fields;ordered-models");
		AppendCanonical(
			canonical,
			"percent-grammar",
			"invariant-unsigned-integer-percent;range-0-100;used-plus-remaining-100");
		AppendCanonical(
			canonical,
			"reset-grammar",
			"roundtrip-O-or-dash");

		foreach (AntigravityUsageR1ModelRule model in spec.Models
			.OrderBy(model => model.Order))
		{
			string prefix = $"model-{model.Order}";
			AppendCanonical(canonical, $"{prefix}-stable-id", model.StableModelId);
			AppendCanonical(canonical, $"{prefix}-rendered", model.RenderedLiteral);
			AppendCanonical(
				canonical,
				$"{prefix}-required",
				model.IsRequired ? "1" : "0");
		}

		return ComputeSha256(canonical.ToString());
	}

	internal static string ComputeStabilityFingerprint(
		AntigravityUsageR1ParseResult result,
		ReadOnlySpan<byte> hmacKey)
	{
		ArgumentNullException.ThrowIfNull(result);

		if (hmacKey.Length < 32)
		{
			throw new ArgumentException(
				"The AGY R1 semantic stability key must contain at least 32 bytes.",
				nameof(hmacKey));
		}

		StringBuilder canonical = new();
		AppendCanonical(
			canonical,
			"schema-fingerprint",
			result.Capture.SchemaFingerprint);
		AppendCanonical(
			canonical,
			"page-fingerprint",
			result.Capture.PageFingerprint);
		AppendCanonical(canonical, "identity", result.Page.AccountIdentity);

		for (int index = 0; index < result.Page.Rows.Count; index++)
		{
			AntigravityQuotaRow row = result.Page.Rows[index];
			string prefix = $"row-{index}";
			AppendCanonical(canonical, $"{prefix}-model", row.StableModelId);
			AppendCanonical(
				canonical,
				$"{prefix}-used",
				row.UsedPercent.ToString(CultureInfo.InvariantCulture));
			AppendCanonical(
				canonical,
				$"{prefix}-remaining",
				row.RemainingPercent.ToString(CultureInfo.InvariantCulture));
			AppendCanonical(
				canonical,
				$"{prefix}-reset",
				row.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-");
		}

		byte[] keyCopy = hmacKey.ToArray();
		byte[] valueBytes = Encoding.UTF8.GetBytes(canonical.ToString());

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHMAC(
				HashAlgorithmName.SHA256,
				keyCopy);
			hash.AppendData(valueBytes);
			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(keyCopy);
			CryptographicOperations.ZeroMemory(valueBytes);
		}
	}

	internal static AntigravityUsageR1ReviewedSpec FreezeAndValidateSpec(
		AntigravityUsageR1ReviewedSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);

		if ((spec.SchemaVersion != SchemaVersion) ||
			string.IsNullOrWhiteSpace(spec.LayoutId) ||
			(spec.LayoutId.Length > MaximumLayoutIdLength) ||
			(spec.Columns < 1) ||
			(spec.Columns > MaximumScreenColumns) ||
			(spec.Rows < 1) ||
			(spec.Rows > MaximumScreenRows) ||
			(spec.PrefixLines is null) ||
			(spec.SuffixLines is null) ||
			(spec.Models is null) ||
			(spec.Models.Count == 0) ||
			(spec.Models.Count > MaximumModels) ||
			(spec.PaginationPolicy !=
				AntigravityUsageR1PaginationPolicy.RequireSinglePage))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage specification is incomplete.");
		}

		ValidateRequiredLiteral(spec.PanelTitle, nameof(spec.PanelTitle));
		ValidateSegmentLiteral(spec.AccountPrefix, nameof(spec.AccountPrefix));
		ValidateSegmentLiteral(spec.PagePrefix, nameof(spec.PagePrefix));
		ValidateRequiredLiteral(spec.TableHeader, nameof(spec.TableHeader));
		ValidateRequiredLiteral(spec.PanelFooter, nameof(spec.PanelFooter));
		ValidateSegmentLiteral(spec.FieldSeparator, nameof(spec.FieldSeparator));
		string[] prefixLines = spec.PrefixLines
			.Select((line, index) => ValidateFixedLine(
				line,
				$"{nameof(spec.PrefixLines)}[{index}]"))
			.ToArray();
		string[] suffixLines = spec.SuffixLines
			.Select((line, index) => ValidateFixedLine(
				line,
				$"{nameof(spec.SuffixLines)}[{index}]"))
			.ToArray();
		AntigravityUsageR1ModelRule[] models = spec.Models
			.OrderBy(model => model.Order)
			.ToArray();
		HashSet<string> stableIds = new(StringComparer.Ordinal);
		HashSet<string> renderedLiterals = new(StringComparer.Ordinal);

		for (int index = 0; index < models.Length; index++)
		{
			AntigravityUsageR1ModelRule model = models[index] ??
				throw new InvalidDataException(
					"The reviewed AGY R1 model specification is invalid.");

			if ((model.Order != index) ||
				!IsValidModelToken(model.StableModelId) ||
				!IsValidModelToken(model.RenderedLiteral) ||
				!stableIds.Add(model.StableModelId) ||
				!renderedLiterals.Add(model.RenderedLiteral))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 model specification is invalid.");
			}
		}

		if (!models.Any(model => model.IsRequired) ||
			((prefixLines.Length + suffixLines.Length + 6) > spec.Rows))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage specification cannot fit its viewport.");
		}

		return spec with
		{
			PrefixLines = Array.AsReadOnly(prefixLines),
			SuffixLines = Array.AsReadOnly(suffixLines),
			Models = Array.AsReadOnly(models)
		};
	}

	internal static bool IsFingerprint(string? value)
	{
		return (value?.Length == 64) &&
			value.All(character =>
				((character >= '0') && (character <= '9')) ||
				((character >= 'A') && (character <= 'F')));
	}

	private static AntigravityUsageR1ParseResult ParseCore(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1ReviewedSpec spec)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if ((snapshot.Columns != spec.Columns) ||
			(snapshot.Rows != spec.Rows) ||
			(snapshot.IsAlternateScreen != spec.IsAlternateScreen))
		{
			throw new InvalidDataException(
				"The AGY R1 usage screen does not match the reviewed viewport.");
		}

		Dictionary<string, AntigravityUsageR1ModelRule> renderedModels =
			spec.Models.ToDictionary(
				model => model.RenderedLiteral,
				StringComparer.Ordinal);
		HashSet<string> allowedRenderedModels = renderedModels.Keys
			.ToHashSet(StringComparer.Ordinal);
		HashSet<string> requiredRenderedModels = spec.Models
			.Where(model => model.IsRequired)
			.Select(model => model.RenderedLiteral)
			.ToHashSet(StringComparer.Ordinal);
		AntigravityUsageLayout candidateLayout = new(
			spec.LayoutId,
			spec.PanelTitle,
			spec.AccountPrefix,
			spec.PagePrefix,
			spec.TableHeader,
			spec.PanelFooter,
			spec.FieldSeparator,
			spec.IsAlternateScreen,
			allowedRenderedModels,
			requiredRenderedModels,
			new Dictionary<int, string>());
		AntigravityUsagePage candidate =
			AntigravityUsageScreenParser.ParseCandidatePage(
				snapshot,
				candidateLayout);

		if (candidate.PageKind != AntigravityUsagePageKind.Single)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 usage layout requires unsupported pagination.");
		}

		ValidateFullScreenCoverage(snapshot, spec, candidate.Rows.Count);
		List<AntigravityQuotaRow> stableRows = new(candidate.Rows.Count);
		List<string> stableModelIds = new(candidate.Rows.Count);
		int previousOrder = -1;

		foreach (AntigravityQuotaRow row in candidate.Rows)
		{
			AntigravityUsageR1ModelRule model = renderedModels[row.StableModelId];

			if (model.Order <= previousOrder)
			{
				throw new InvalidDataException(
					"The AGY R1 usage model order does not match the reviewed schema.");
			}

			previousOrder = model.Order;
			stableModelIds.Add(model.StableModelId);
			stableRows.Add(row with { StableModelId = model.StableModelId });
		}

		string schemaFingerprint = ComputeSchemaFingerprint(spec);
		string pageFingerprint = ComputePageFingerprint(
			spec,
			schemaFingerprint,
			candidate.PageKind,
			stableModelIds);
		ReadOnlyCollection<string> requiredStableModelIds = Array.AsReadOnly(
			spec.Models
				.Where(model => model.IsRequired)
				.OrderBy(model => model.Order)
				.Select(model => model.StableModelId)
				.ToArray());
		AntigravityUsagePage page = new(
			spec.LayoutId,
			pageFingerprint,
			candidate.AccountIdentity,
			candidate.PageKind,
			requiredStableModelIds,
			stableRows.AsReadOnly());
		AntigravityUsageR1SemanticCapture capture = new(
			spec.LayoutId,
			schemaFingerprint,
			pageFingerprint,
			candidate.PageKind,
			stableModelIds.AsReadOnly(),
			stableRows.Count);
		return new AntigravityUsageR1ParseResult(page, capture);
	}

	private static string ComputePageFingerprint(
		AntigravityUsageR1ReviewedSpec spec,
		string schemaFingerprint,
		AntigravityUsagePageKind pageKind,
		IReadOnlyList<string> stableModelIds)
	{
		int titleIndex = spec.PrefixLines.Count;
		int headerIndex = titleIndex + 3;
		int footerIndex = headerIndex + 1 + stableModelIds.Count;
		StringBuilder canonical = new();
		AppendCanonical(canonical, "schema-fingerprint", schemaFingerprint);
		AppendCanonical(canonical, "page-kind", pageKind.ToString());
		AppendCanonical(
			canonical,
			"title-index",
			titleIndex.ToString(CultureInfo.InvariantCulture));
		AppendCanonical(
			canonical,
			"header-index",
			headerIndex.ToString(CultureInfo.InvariantCulture));
		AppendCanonical(
			canonical,
			"footer-index",
			footerIndex.ToString(CultureInfo.InvariantCulture));

		for (int index = 0; index < stableModelIds.Count; index++)
		{
			AppendCanonical(
				canonical,
				$"row-{index}",
				$"{stableModelIds[index]}|<used-percent>|<remaining-percent>|<reset>");
		}

		return ComputeSha256(canonical.ToString());
	}

	private static void ValidateFullScreenCoverage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1ReviewedSpec spec,
		int modelRowCount)
	{
		string[] lines = snapshot.Lines
			.Select(NormalizeRenderedLine)
			.ToArray();
		int titleIndex = spec.PrefixLines.Count;
		int footerIndex = titleIndex + 4 + modelRowCount;
		int suffixStartIndex = footerIndex + 1;

		if ((suffixStartIndex + spec.SuffixLines.Count) > lines.Length)
		{
			throw new InvalidDataException(
				"The AGY R1 usage screen is truncated after its panel.");
		}

		for (int index = 0; index < spec.PrefixLines.Count; index++)
		{
			if (!string.Equals(
					lines[index],
					spec.PrefixLines[index],
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The AGY R1 usage screen prefix is not reviewed.");
			}
		}

		if (!string.Equals(
				lines[titleIndex],
				spec.PanelTitle,
				StringComparison.Ordinal) ||
			!string.Equals(
				lines[footerIndex],
				spec.PanelFooter,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The AGY R1 usage panel position is not reviewed.");
		}

		for (int index = 0; index < spec.SuffixLines.Count; index++)
		{
			if (!string.Equals(
					lines[suffixStartIndex + index],
					spec.SuffixLines[index],
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The AGY R1 usage screen suffix is not reviewed.");
			}
		}

		for (int index = suffixStartIndex + spec.SuffixLines.Count;
			index < lines.Length;
			index++)
		{
			if (lines[index].Length != 0)
			{
				throw new InvalidDataException(
					"The AGY R1 usage screen contains unreviewed content.");
			}
		}
	}

	private static string NormalizeRenderedLine(string line)
	{
		ArgumentNullException.ThrowIfNull(line);

		if (line.Any(char.IsControl))
		{
			throw new InvalidDataException(
				"The AGY R1 usage screen contains control characters.");
		}

		return line.TrimEnd(' ');
	}

	private static bool IsValidModelToken(string? value)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= MaximumModelIdLength) &&
			!value.Any(char.IsControl) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal);
	}

	private static string ValidateFixedLine(string? value, string fieldName)
	{
		if ((value is null) ||
			(value.Length > MaximumFixedLineLength) ||
			value.Any(char.IsControl) ||
			!string.Equals(value, value.TrimEnd(' '), StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"The reviewed AGY R1 fixed line is invalid: {fieldName}.");
		}

		return value;
	}

	private static void ValidateRequiredLiteral(string? value, string fieldName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidDataException(
				$"The reviewed AGY R1 literal is missing: {fieldName}.");
		}

		_ = ValidateFixedLine(value, fieldName);
	}

	private static void ValidateSegmentLiteral(string? value, string fieldName)
	{
		if (string.IsNullOrWhiteSpace(value) ||
			(value.Length > MaximumFixedLineLength) ||
			value.Any(char.IsControl))
		{
			throw new InvalidDataException(
				$"The reviewed AGY R1 segment literal is invalid: {fieldName}.");
		}
	}

	private static void AppendCanonicalLines(
		StringBuilder canonical,
		string fieldName,
		IReadOnlyList<string> lines)
	{
		AppendCanonical(
			canonical,
			$"{fieldName}-count",
			lines.Count.ToString(CultureInfo.InvariantCulture));

		for (int index = 0; index < lines.Count; index++)
		{
			AppendCanonical(
				canonical,
				$"{fieldName}-{index}",
				lines[index]);
		}
	}

	private static void AppendCanonical(
		StringBuilder canonical,
		string fieldName,
		string value)
	{
		canonical.Append(fieldName);
		canonical.Append('=');
		canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
		canonical.Append(':');
		canonical.Append(value);
		canonical.Append('\n');
	}

	private static string ComputeSha256(string value)
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
}
