using System.Buffers;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsageR1SectionPaginationPolicy
{
	RequireFullyVisible
}

internal enum AntigravityUsageR1SectionPaginationMode
{
	NoneWhenFullyVisible,
	Counter
}

internal enum AntigravityUsageR1SectionWindowKind
{
	Weekly,
	RollingHours
}

internal enum AntigravityUsageR1SectionCapacityPolicy
{
	Percentage,
	Availability,
	PercentageOrAvailability
}

internal enum AntigravityUsageR1SectionResetPolicy
{
	None,
	Countdown,
	Availability,
	CountdownOrAvailability
}

internal enum AntigravityUsageR1SectionLineRole
{
	Fixed,
	OuterChrome,
	NonQuotaDynamic,
	Identity,
	SectionHeading,
	WindowLabel,
	Capacity,
	Reset,
	Pagination
}

internal enum AntigravityUsageR1SectionPercentMeaning
{
	Used,
	Remaining
}

internal enum AntigravityUsageR1SectionPaginationField
{
	Start,
	End,
	Total
}

internal enum AntigravityUsageR1SectionCountdownUnit
{
	Days,
	Hours,
	Minutes
}

internal enum AntigravityUsageR1SectionMeterRounding
{
	Floor,
	Ceiling,
	Nearest
}

internal enum AntigravityUsageR1SectionOuterLineCharacterClass
{
	AsciiPrintable,
	UnicodeVisibleText
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionLiteralToken), "Literal")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionIdentityToken), "Identity")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionPercentToken), "Percent")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionCountdownToken), "Countdown")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionAvailabilityToken), "Availability")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionProgressMeterToken), "ProgressMeter")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionPaginationNumberToken), "PaginationNumber")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionOpaqueToken), "Opaque")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionUnsignedAmountToken), "UnsignedAmount")]
[JsonDerivedType(typeof(AntigravityUsageR1SectionOuterLineShapeToken), "OuterLineShape")]
internal abstract record AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionLiteralToken(string Value) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionIdentityToken :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionPercentToken(
	AntigravityUsageR1SectionPercentMeaning Meaning,
	int DecimalPlaces) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionCountdownToken(
	AntigravityUsageR1SectionCountdownRule Rule) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionAvailabilityToken(
	IReadOnlyList<AntigravityUsageR1SectionAvailabilityRule> Values) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionProgressMeterToken(
	AntigravityUsageR1SectionProgressMeterRule Rule) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionPaginationNumberToken(
	AntigravityUsageR1SectionPaginationField Field) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionOpaqueToken(int MaximumLength) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionUnsignedAmountToken(
	int MinimumDigits,
	int MaximumDigits) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionOuterLineShapeToken(
	string? PublicPrefix,
	string? PublicSuffix,
	int MinimumDynamicLength,
	int MaximumDynamicLength,
	AntigravityUsageR1SectionOuterLineCharacterClass AllowedCharacters) :
	AntigravityUsageR1SectionToken;

internal sealed record AntigravityUsageR1SectionAvailabilityRule(
	string StableStatusId,
	string RenderedLiteral);

internal sealed record AntigravityUsageR1SectionCountdownUnitRule(
	AntigravityUsageR1SectionCountdownUnit Unit,
	string SingularLiteral,
	string PluralLiteral,
	int Order);

internal sealed record AntigravityUsageR1SectionCountdownRule(
	string Separator,
	IReadOnlyList<AntigravityUsageR1SectionCountdownUnitRule> Units,
	int MaximumTotalMinutes);

internal sealed record AntigravityUsageR1SectionProgressMeterRule(
	int Width,
	string FilledGlyph,
	string EmptyGlyph,
	AntigravityUsageR1SectionPercentMeaning Meaning,
	AntigravityUsageR1SectionMeterRounding Rounding);

internal sealed record AntigravityUsageR1SectionLinePattern(
	string PatternId,
	IReadOnlyList<AntigravityUsageR1SectionToken> Tokens);

internal sealed record AntigravityUsageR1SectionLineRule(
	int RowIndex,
	string StableLineId,
	AntigravityUsageR1SectionLineRole Role,
	string? StableSectionId,
	string? StableWindowId,
	IReadOnlyList<AntigravityUsageR1SectionLinePattern> Alternatives);

internal sealed record AntigravityUsageR1SectionWindowRule(
	string StableWindowId,
	string RenderedLiteral,
	AntigravityUsageR1SectionWindowKind WindowKind,
	int? WindowHours,
	int Order,
	AntigravityUsageR1SectionCapacityPolicy CapacityPolicy,
	AntigravityUsageR1SectionResetPolicy ResetPolicy);

internal sealed record AntigravityUsageR1SectionRule(
	string StableSectionId,
	string RenderedLiteral,
	int Order,
	IReadOnlyList<AntigravityUsageR1SectionWindowRule> Windows);

internal sealed record AntigravityUsageR1SectionSpec(
	int SchemaVersion,
	string LayoutId,
	int Columns,
	int Rows,
	bool IsAlternateScreen,
	IReadOnlyList<AntigravityUsageR1SectionRule> Sections,
	IReadOnlyList<AntigravityUsageR1SectionLineRule> Lines,
	AntigravityUsageR1SectionPaginationPolicy PaginationPolicy,
	AntigravityUsageR1SectionPaginationMode PaginationMode);

internal sealed record AntigravityUsageR1SectionSpecFile(
	string FormatVersion,
	AntigravityUsageR1ReviewState ReviewState,
	AntigravityUsageR1SectionSpec Spec,
	string? CaptureContractFingerprint = null,
	string? ApprovedDraftFingerprint = null);

internal sealed record AntigravityUsageR1SectionLayoutFile(
	string FormatVersion,
	AntigravityUsageR1ReviewState ReviewState,
	AntigravityUsageR1SectionSpec Spec,
	string SchemaFingerprint,
	IReadOnlyList<string> ExpectedPageFingerprints,
	string? CaptureContractFingerprint = null,
	string? ApprovedDraftFingerprint = null)
{
	internal AntigravityUsageR1SectionLayout ToLayout()
	{
		if (!string.Equals(
				FormatVersion,
				AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
				StringComparison.Ordinal) ||
			(ReviewState != AntigravityUsageR1ReviewState.Reviewed) ||
			(ExpectedPageFingerprints is null) ||
			(ExpectedPageFingerprints.Count != 1) ||
			!ExpectedPageFingerprints.All(
				AntigravityUsageR1SchemaParser.IsFingerprint) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				CaptureContractFingerprint) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				ApprovedDraftFingerprint))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 section layout is incomplete.");
		}

		AntigravityUsageR1SectionSpec frozen =
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(Spec);
		string schemaFingerprint =
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				frozen);

		if (!string.Equals(
				SchemaFingerprint,
				schemaFingerprint,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 section schema fingerprint is invalid.");
		}

		FrozenSet<string> pageFingerprints = ExpectedPageFingerprints
			.ToFrozenSet(StringComparer.Ordinal);

		if (pageFingerprints.Count != ExpectedPageFingerprints.Count)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 section page fingerprints are invalid.");
		}

		return new AntigravityUsageR1SectionLayout(
			frozen,
			schemaFingerprint,
			pageFingerprints,
			CaptureContractFingerprint!,
			ApprovedDraftFingerprint!);
	}
}

internal sealed record AntigravityUsageR1SectionLayout(
	AntigravityUsageR1SectionSpec Spec,
	string SchemaFingerprint,
	FrozenSet<string> ExpectedPageFingerprints,
	string CaptureContractFingerprint,
	string ApprovedDraftFingerprint);

internal sealed record AntigravityUsageR1SectionCapacity(
	decimal? UsedPercent,
	decimal? RemainingPercent,
	string? AvailabilityStatusId);

internal sealed record AntigravityUsageR1SectionReset(
	TimeSpan? ResetsIn,
	string? AvailabilityStatusId);

internal sealed record AntigravityUsageR1SectionQuotaWindow(
	string StableWindowId,
	AntigravityUsageR1SectionWindowKind WindowKind,
	int? WindowHours,
	AntigravityUsageR1SectionCapacity Capacity,
	AntigravityUsageR1SectionReset Reset);

internal sealed record AntigravityUsageR1SectionQuotaSection(
	string StableSectionId,
	IReadOnlyList<AntigravityUsageR1SectionQuotaWindow> Windows);

internal sealed record AntigravityUsageR1SectionPagination(
	int StartLine,
	int EndLine,
	int TotalLines)
{
	internal bool IsFullyVisible =>
		(StartLine == 1) && (EndLine == TotalLines);
}

internal sealed record AntigravityUsageR1SectionPage(
	string LayoutId,
	string PageFingerprint,
	string AccountIdentity,
	AntigravityUsageR1SectionPagination? Pagination,
	IReadOnlyList<AntigravityUsageR1SectionQuotaSection> Sections);

internal sealed record AntigravityUsageR1SectionSemanticCapture(
	string LayoutId,
	string SchemaFingerprint,
	string PageFingerprint,
	AntigravityUsageR1SectionPaginationPolicy PaginationPolicy,
	IReadOnlyList<string> StableSectionIds,
	IReadOnlyList<string> StableWindowIds,
	int SectionCount,
	int WindowCount);

internal sealed record AntigravityUsageR1SectionParseResult(
	AntigravityUsageR1SectionPage Page,
	AntigravityUsageR1SectionSemanticCapture Capture);

internal static class AntigravityUsageR1SectionSchemaParser
{
	private sealed record ParsedPercent(
		decimal UsedPercent,
		decimal RemainingPercent,
		AntigravityUsageR1SectionPercentMeaning Meaning,
		int DecimalPlaces);

	private sealed record ParsedMeter(
		AntigravityUsageR1SectionProgressMeterRule Rule,
		string Rendered);

	private sealed class LineMatch
	{
		internal string? AvailabilityStatusId { get; set; }
		internal TimeSpan? Countdown { get; set; }
		internal string? Identity { get; set; }
		internal ParsedMeter? Meter { get; set; }
		internal int? PaginationEnd { get; set; }
		internal int? PaginationStart { get; set; }
		internal int? PaginationTotal { get; set; }
		internal ParsedPercent? Percent { get; set; }
	}

	private sealed class WindowBuilder
	{
		internal AntigravityUsageR1SectionCapacity? Capacity { get; set; }
		internal decimal? DetailRemainingPercent { get; set; }
		internal AntigravityUsageR1SectionReset? Reset { get; set; }
		internal AntigravityUsageR1SectionWindowRule Rule { get; }

		internal WindowBuilder(AntigravityUsageR1SectionWindowRule rule)
		{
			Rule = rule;
		}
	}

	internal const string LayoutFormatVersion = "agy-usage-r1-layout-v3";
	internal const string ReviewedSpecFormatVersion =
		"agy-usage-r1-reviewed-spec-v3";
	private const int MaximumCountdownMinutes = 8 * 24 * 60;
	private const int MaximumIdentityLength = 320;
	private const int MaximumLiteralLength = 4096;
	private const int MaximumOpaqueLength = 1000;
	private const int MaximumOuterLineDynamicLength = 1000;
	private const int MaximumRows = 256;
	private const int MaximumUnsignedAmountDigits = 128;
	private const int SchemaVersion = 2;

	internal static AntigravityUsageR1SectionParseResult ParseCalibrationPage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1SectionSpec spec)
	{
		return ParseCore(snapshot, FreezeAndValidateSpec(spec));
	}

	internal static AntigravityUsageR1SectionParseResult ParseReviewedPage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1SectionLayout layout)
	{
		ArgumentNullException.ThrowIfNull(layout);
		AntigravityUsageR1SectionSpec frozen = FreezeAndValidateSpec(
			layout.Spec);
		string schemaFingerprint = ComputeSchemaFingerprint(frozen);

		if (!string.Equals(
				layout.SchemaFingerprint,
				schemaFingerprint,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The AGY R1 section schema changed after review.");
		}

		AntigravityUsageR1SectionParseResult result = ParseCore(
			snapshot,
			frozen);

		if (!layout.ExpectedPageFingerprints.Contains(
				result.Capture.PageFingerprint))
		{
			throw new InvalidDataException(
				"The AGY R1 section page shape is not reviewed.");
		}

		return result;
	}

	internal static AntigravityUsageR1SectionSpec FreezeAndValidateSpec(
		AntigravityUsageR1SectionSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		ArgumentNullException.ThrowIfNull(spec.Sections);
		ArgumentNullException.ThrowIfNull(spec.Lines);

		if ((spec.SchemaVersion != SchemaVersion) ||
			!IsStableToken(spec.LayoutId, 128) ||
			(spec.Columns < 1) ||
			(spec.Columns > 1000) ||
			(spec.Rows < 1) ||
			(spec.Rows > MaximumRows) ||
			(spec.Sections.Count < 1) ||
			(spec.Sections.Count > 8) ||
			(spec.Lines.Count != spec.Rows) ||
			(spec.PaginationPolicy !=
				AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible) ||
			!Enum.IsDefined(spec.PaginationMode))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 section specification is incomplete.");
		}

		AntigravityUsageR1SectionRule[] sections = spec.Sections
			.Select(FreezeSection)
			.OrderBy(section => section.Order)
			.ToArray();
		HashSet<string> sectionIds = new(StringComparer.Ordinal);

		for (int index = 0; index < sections.Length; index++)
		{
			if ((sections[index].Order != index) ||
				!sectionIds.Add(sections[index].StableSectionId))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 section order is invalid.");
			}
		}

		AntigravityUsageR1SectionLineRule[] lines = spec.Lines
			.Select(FreezeLine)
			.OrderBy(line => line.RowIndex)
			.ToArray();
		HashSet<string> lineIds = new(StringComparer.Ordinal);

		for (int index = 0; index < lines.Length; index++)
		{
			if ((lines[index].RowIndex != index) ||
				!lineIds.Add(lines[index].StableLineId))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 section line ownership is invalid.");
			}
		}

		ValidateTopology(sections, lines, spec.PaginationMode);
		return spec with
		{
			Sections = Array.AsReadOnly(sections),
			Lines = Array.AsReadOnly(lines)
		};
	}

	internal static string ComputeSchemaFingerprint(
		AntigravityUsageR1SectionSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		StringBuilder canonical = new();
		Append(canonical, "grammar",
			"section-v2;ordinal;full-screen;anchored-token;one-line-choice");
		Append(canonical, "schema", spec.SchemaVersion.ToString(
			CultureInfo.InvariantCulture));
		Append(canonical, "layout", spec.LayoutId);
		Append(canonical, "viewport",
			$"{spec.Columns}x{spec.Rows}|{spec.IsAlternateScreen}");
		Append(canonical, "pagination", spec.PaginationPolicy.ToString());
		Append(canonical, "pagination-mode", spec.PaginationMode.ToString());

		foreach (AntigravityUsageR1SectionRule section in spec.Sections)
		{
			Append(canonical, "section",
				$"{section.Order}|{section.StableSectionId}|{section.RenderedLiteral}");

			foreach (AntigravityUsageR1SectionWindowRule window in section.Windows)
			{
				Append(canonical, "window", string.Join(
					'|',
					section.StableSectionId,
					window.Order,
					window.StableWindowId,
					window.RenderedLiteral,
					window.WindowKind,
					window.WindowHours?.ToString(CultureInfo.InvariantCulture) ?? "-",
					window.CapacityPolicy,
					window.ResetPolicy));
			}
		}

		foreach (AntigravityUsageR1SectionLineRule line in spec.Lines)
		{
			Append(canonical, "line", string.Join(
				'|',
				line.RowIndex,
				line.StableLineId,
				line.Role,
				line.StableSectionId ?? "-",
				line.StableWindowId ?? "-"));

			foreach (AntigravityUsageR1SectionLinePattern pattern in
				line.Alternatives)
			{
				Append(canonical, "pattern", pattern.PatternId);

				foreach (AntigravityUsageR1SectionToken token in pattern.Tokens)
				{
					AppendToken(canonical, token);
				}
			}
		}

		return Hash(canonical.ToString());
	}

	internal static string ComputePageFingerprint(
		AntigravityUsageR1SectionSpec spec)
	{
		AntigravityUsageR1SectionSpec frozen = FreezeAndValidateSpec(spec);
		string schemaFingerprint = ComputeSchemaFingerprint(frozen);
		return ComputePageFingerprint(frozen, schemaFingerprint);
	}

	internal static string ComputeStabilityFingerprint(
		AntigravityUsageR1SectionParseResult result,
		ReadOnlySpan<byte> hmacKey)
	{
		ArgumentNullException.ThrowIfNull(result);

		if (hmacKey.Length < 32)
		{
			throw new ArgumentException(
				"The AGY R1 section stability key must contain 32 bytes.",
				nameof(hmacKey));
		}

		StringBuilder canonical = new();
		Append(canonical, "schema", result.Capture.SchemaFingerprint);
		Append(canonical, "page", result.Capture.PageFingerprint);
		Append(canonical, "identity", result.Page.AccountIdentity);

		foreach (AntigravityUsageR1SectionQuotaSection section in
			result.Page.Sections)
		{
			Append(canonical, "section", section.StableSectionId);

			foreach (AntigravityUsageR1SectionQuotaWindow window in
				section.Windows)
			{
				Append(canonical, "window", window.StableWindowId);
				Append(canonical, "used",
					FormatDecimal(window.Capacity.UsedPercent));
				Append(canonical, "remaining",
					FormatDecimal(window.Capacity.RemainingPercent));
				Append(canonical, "capacity-status",
					window.Capacity.AvailabilityStatusId ?? "-");
				Append(canonical, "reset-ticks",
					window.Reset.ResetsIn?.Ticks.ToString(
						CultureInfo.InvariantCulture) ?? "-");
				Append(canonical, "reset-status",
					window.Reset.AvailabilityStatusId ?? "-");
			}
		}

		byte[] key = hmacKey.ToArray();
		byte[] value = Encoding.UTF8.GetBytes(canonical.ToString());

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHMAC(
				HashAlgorithmName.SHA256,
				key);
			hash.AppendData(value);
			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(value);
		}
	}

	internal static bool HasQuotaSemanticDifference(
		AntigravityUsageR1SectionParseResult left,
		AntigravityUsageR1SectionParseResult right)
	{
		ArgumentNullException.ThrowIfNull(left);
		ArgumentNullException.ThrowIfNull(right);
		return HasQuotaSemanticDifference(left.Page, right.Page);
	}

	internal static bool HasQuotaSemanticDifference(
		AntigravityUsageR1SectionPage left,
		AntigravityUsageR1SectionPage right)
	{
		ArgumentNullException.ThrowIfNull(left);
		ArgumentNullException.ThrowIfNull(right);

		if (left.Sections.Count != right.Sections.Count)
		{
			return true;
		}

		for (int sectionIndex = 0;
			sectionIndex < left.Sections.Count;
			sectionIndex++)
		{
			AntigravityUsageR1SectionQuotaSection leftSection =
				left.Sections[sectionIndex];
			AntigravityUsageR1SectionQuotaSection rightSection =
				right.Sections[sectionIndex];

			if (!string.Equals(
					leftSection.StableSectionId,
					rightSection.StableSectionId,
					StringComparison.Ordinal) ||
				(leftSection.Windows.Count != rightSection.Windows.Count))
			{
				return true;
			}

			for (int windowIndex = 0;
				windowIndex < leftSection.Windows.Count;
				windowIndex++)
			{
				AntigravityUsageR1SectionQuotaWindow leftWindow =
					leftSection.Windows[windowIndex];
				AntigravityUsageR1SectionQuotaWindow rightWindow =
					rightSection.Windows[windowIndex];

				if (leftWindow != rightWindow)
				{
					return true;
				}
			}
		}

		return false;
	}

	private static AntigravityUsageR1SectionParseResult ParseCore(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageR1SectionSpec spec)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if ((snapshot.Columns != spec.Columns) ||
			(snapshot.Rows != spec.Rows) ||
			(snapshot.IsAlternateScreen != spec.IsAlternateScreen) ||
			(snapshot.Lines.Count != spec.Rows))
		{
			throw new InvalidDataException(
				"The AGY R1 section viewport is not reviewed.");
		}

		Dictionary<string, AntigravityUsageR1SectionRule> sectionsById =
			spec.Sections.ToDictionary(
				section => section.StableSectionId,
				StringComparer.Ordinal);
		Dictionary<string, WindowBuilder> windowsById = new(
			StringComparer.Ordinal);

		foreach (AntigravityUsageR1SectionRule section in spec.Sections)
		{
			foreach (AntigravityUsageR1SectionWindowRule window in
				section.Windows)
			{
				windowsById.Add(window.StableWindowId, new WindowBuilder(window));
			}
		}

		string? identity = null;
		int? paginationStart = null;
		int? paginationEnd = null;
		int? paginationTotal = null;

		foreach (AntigravityUsageR1SectionLineRule lineRule in spec.Lines)
		{
			string line = NormalizeLine(snapshot.Lines[lineRule.RowIndex]);
			LineMatch match = MatchLine(line, lineRule);

			switch (lineRule.Role)
			{
				case AntigravityUsageR1SectionLineRole.Fixed:
				case AntigravityUsageR1SectionLineRole.OuterChrome:
				case AntigravityUsageR1SectionLineRole.NonQuotaDynamic:
				case AntigravityUsageR1SectionLineRole.SectionHeading:
				case AntigravityUsageR1SectionLineRole.WindowLabel:
					break;
				case AntigravityUsageR1SectionLineRole.Identity:
					if ((identity is not null) || (match.Identity is null))
					{
						throw new InvalidDataException(
							"The AGY R1 account identity is ambiguous.");
					}

					identity = match.Identity;
					break;
				case AntigravityUsageR1SectionLineRole.Capacity:
					ApplyCapacityMatch(
						GetWindowBuilder(windowsById, lineRule),
						match);
					break;
				case AntigravityUsageR1SectionLineRole.Reset:
					ApplyResetMatch(
						GetWindowBuilder(windowsById, lineRule),
						match);
					break;
				case AntigravityUsageR1SectionLineRole.Pagination:
					paginationStart = match.PaginationStart;
					paginationEnd = match.PaginationEnd;
					paginationTotal = match.PaginationTotal;
					break;
				default:
					throw new InvalidDataException(
						"The AGY R1 section line role is unsupported.");
			}
		}

		if (identity is null)
		{
			throw new InvalidDataException(
				"The AGY R1 account identity was not observed.");
		}

		AntigravityUsageR1SectionPagination? pagination =
			BuildPagination(
				spec.PaginationMode,
				paginationStart,
				paginationEnd,
				paginationTotal);
		List<AntigravityUsageR1SectionQuotaSection> parsedSections = new();

		foreach (AntigravityUsageR1SectionRule section in spec.Sections)
		{
			List<AntigravityUsageR1SectionQuotaWindow> parsedWindows = new();

			foreach (AntigravityUsageR1SectionWindowRule window in
				section.Windows)
			{
				parsedWindows.Add(FinalizeWindow(windowsById[window.StableWindowId]));
			}

			parsedSections.Add(new AntigravityUsageR1SectionQuotaSection(
				section.StableSectionId,
				parsedWindows.AsReadOnly()));
		}

		string schemaFingerprint = ComputeSchemaFingerprint(spec);
		string pageFingerprint = ComputePageFingerprint(
			spec,
			schemaFingerprint);
		string[] stableSectionIds = spec.Sections
			.Select(section => section.StableSectionId)
			.ToArray();
		string[] stableWindowIds = spec.Sections
			.SelectMany(section => section.Windows)
			.Select(window => window.StableWindowId)
			.ToArray();
		AntigravityUsageR1SectionSemanticCapture capture = new(
			spec.LayoutId,
			schemaFingerprint,
			pageFingerprint,
			spec.PaginationPolicy,
			Array.AsReadOnly(stableSectionIds),
			Array.AsReadOnly(stableWindowIds),
			stableSectionIds.Length,
			stableWindowIds.Length);
		AntigravityUsageR1SectionPage page = new(
			spec.LayoutId,
			pageFingerprint,
			identity,
			pagination,
			parsedSections.AsReadOnly());
		_ = sectionsById;
		return new AntigravityUsageR1SectionParseResult(page, capture);
	}

	private static WindowBuilder GetWindowBuilder(
		IReadOnlyDictionary<string, WindowBuilder> windows,
		AntigravityUsageR1SectionLineRule lineRule)
	{
		if ((lineRule.StableWindowId is null) ||
			!windows.TryGetValue(lineRule.StableWindowId, out WindowBuilder? builder))
		{
			throw new InvalidDataException(
				"The AGY R1 quota window line is not reviewed.");
		}

		return builder;
	}

	private static void ApplyCapacityMatch(
		WindowBuilder builder,
		LineMatch match)
	{
		if (builder.Capacity is not null)
		{
			throw new InvalidDataException(
				"The AGY R1 quota capacity is duplicated.");
		}

		if (match.Percent is not null)
		{
			ValidateMeter(match);
			builder.Capacity = new AntigravityUsageR1SectionCapacity(
				match.Percent.UsedPercent,
				match.Percent.RemainingPercent,
				null);
			return;
		}

		if (match.AvailabilityStatusId is not null)
		{
			builder.Capacity = new AntigravityUsageR1SectionCapacity(
				null,
				null,
				match.AvailabilityStatusId);
			return;
		}

		throw new InvalidDataException(
			"The AGY R1 quota capacity is incomplete.");
	}

	private static void ApplyResetMatch(
		WindowBuilder builder,
		LineMatch match)
	{
		if (builder.Reset is not null)
		{
			throw new InvalidDataException(
				"The AGY R1 quota reset is duplicated.");
		}

		if (match.Countdown is not null)
		{
			builder.DetailRemainingPercent =
				match.Percent?.RemainingPercent;
			builder.Reset = new AntigravityUsageR1SectionReset(
				match.Countdown,
				null);
			return;
		}

		if (match.AvailabilityStatusId is not null)
		{
			builder.Reset = new AntigravityUsageR1SectionReset(
				null,
				match.AvailabilityStatusId);
			return;
		}

		if (builder.Rule.ResetPolicy == AntigravityUsageR1SectionResetPolicy.None)
		{
			builder.Reset = new AntigravityUsageR1SectionReset(null, null);
			return;
		}

		throw new InvalidDataException(
			"The AGY R1 quota reset is incomplete.");
	}

	private static AntigravityUsageR1SectionQuotaWindow FinalizeWindow(
		WindowBuilder builder)
	{
		AntigravityUsageR1SectionCapacity capacity = builder.Capacity ??
			throw new InvalidDataException(
				"The AGY R1 quota capacity was not observed.");
		AntigravityUsageR1SectionReset reset = builder.Reset ??
			throw new InvalidDataException(
				"The AGY R1 quota reset was not observed.");

		ValidateCapacityPolicy(builder.Rule.CapacityPolicy, capacity);
		ValidateResetPolicy(builder.Rule.ResetPolicy, reset);

		if ((builder.DetailRemainingPercent is not null) &&
			(capacity.RemainingPercent is not null) &&
			(decimal.Round(
				capacity.RemainingPercent.Value,
				0,
				MidpointRounding.AwayFromZero) !=
				builder.DetailRemainingPercent.Value))
		{
			throw new InvalidDataException(
				"The AGY R1 quota detail percentage is inconsistent.");
		}

		return new AntigravityUsageR1SectionQuotaWindow(
			builder.Rule.StableWindowId,
			builder.Rule.WindowKind,
			builder.Rule.WindowHours,
			capacity,
			reset);
	}

	private static void ValidateCapacityPolicy(
		AntigravityUsageR1SectionCapacityPolicy policy,
		AntigravityUsageR1SectionCapacity capacity)
	{
		bool hasPercentage =
			(capacity.UsedPercent is not null) &&
			(capacity.RemainingPercent is not null) &&
			(capacity.AvailabilityStatusId is null);
		bool hasAvailability =
			(capacity.UsedPercent is null) &&
			(capacity.RemainingPercent is null) &&
			(capacity.AvailabilityStatusId is not null);
		bool isAllowed = policy switch
		{
			AntigravityUsageR1SectionCapacityPolicy.Percentage => hasPercentage,
			AntigravityUsageR1SectionCapacityPolicy.Availability => hasAvailability,
			AntigravityUsageR1SectionCapacityPolicy.PercentageOrAvailability =>
				hasPercentage || hasAvailability,
			_ => false
		};

		if (!isAllowed)
		{
			throw new InvalidDataException(
				"The AGY R1 quota capacity policy was violated.");
		}
	}

	private static void ValidateResetPolicy(
		AntigravityUsageR1SectionResetPolicy policy,
		AntigravityUsageR1SectionReset reset)
	{
		bool hasCountdown =
			(reset.ResetsIn is not null) &&
			(reset.AvailabilityStatusId is null);
		bool hasAvailability =
			(reset.ResetsIn is null) &&
			(reset.AvailabilityStatusId is not null);
		bool hasNoValue =
			(reset.ResetsIn is null) &&
			(reset.AvailabilityStatusId is null);
		bool isAllowed = policy switch
		{
			AntigravityUsageR1SectionResetPolicy.None => hasNoValue,
			AntigravityUsageR1SectionResetPolicy.Countdown => hasCountdown,
			AntigravityUsageR1SectionResetPolicy.Availability => hasAvailability,
			AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability =>
				hasCountdown || hasAvailability,
			_ => false
		};

		if (!isAllowed)
		{
			throw new InvalidDataException(
				"The AGY R1 quota reset policy was violated.");
		}
	}

	private static AntigravityUsageR1SectionPagination? BuildPagination(
		AntigravityUsageR1SectionPaginationMode mode,
		int? start,
		int? end,
		int? total)
	{
		if (mode ==
			AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible)
		{
			if ((start is not null) || (end is not null) || (total is not null))
			{
				throw new InvalidDataException(
					"The AGY R1 full page unexpectedly contains a counter.");
			}

			return null;
		}

		if ((mode != AntigravityUsageR1SectionPaginationMode.Counter) ||
			(start is null) ||
			(end is null) ||
			(total is null))
		{
			throw new InvalidDataException(
				"The AGY R1 pagination counter is incomplete.");
		}

		AntigravityUsageR1SectionPagination pagination = new(
			start.Value,
			end.Value,
			total.Value);

		if ((pagination.StartLine < 1) ||
			(pagination.EndLine < pagination.StartLine) ||
			(pagination.TotalLines < pagination.EndLine) ||
			!pagination.IsFullyVisible)
		{
			throw new InvalidDataException(
				"The AGY R1 quota page is not fully visible.");
		}

		return pagination;
	}

	private static AntigravityUsageR1SectionRule FreezeSection(
		AntigravityUsageR1SectionRule section)
	{
		ArgumentNullException.ThrowIfNull(section);
		ArgumentNullException.ThrowIfNull(section.Windows);

		if (!IsStableToken(section.StableSectionId, 128) ||
			!IsFixedLiteral(section.RenderedLiteral, allowEmpty: false) ||
			(section.Order < 0) ||
			(section.Windows.Count < 1) ||
			(section.Windows.Count > 16))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 section is invalid.");
		}

		AntigravityUsageR1SectionWindowRule[] windows = section.Windows
			.OrderBy(window => window.Order)
			.ToArray();
		HashSet<string> windowIds = new(StringComparer.Ordinal);
		HashSet<string> renderedLiterals = new(StringComparer.Ordinal);

		for (int index = 0; index < windows.Length; index++)
		{
			AntigravityUsageR1SectionWindowRule window = windows[index] ??
				throw new InvalidDataException(
					"The reviewed AGY R1 quota window is invalid.");

			if ((window.Order != index) ||
				!IsStableToken(window.StableWindowId, 128) ||
				!IsFixedLiteral(window.RenderedLiteral, allowEmpty: false) ||
				!windowIds.Add(window.StableWindowId) ||
				!renderedLiterals.Add(window.RenderedLiteral) ||
				!Enum.IsDefined(window.WindowKind) ||
				!Enum.IsDefined(window.CapacityPolicy) ||
				!Enum.IsDefined(window.ResetPolicy) ||
				!IsWindowDurationValid(window))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 quota window is invalid.");
			}
		}

		return section with { Windows = Array.AsReadOnly(windows) };
	}

	private static bool IsWindowDurationValid(
		AntigravityUsageR1SectionWindowRule window)
	{
		return window.WindowKind switch
		{
			AntigravityUsageR1SectionWindowKind.Weekly =>
				window.WindowHours is null,
			AntigravityUsageR1SectionWindowKind.RollingHours =>
				(window.WindowHours >= 1) && (window.WindowHours <= 168),
			_ => false
		};
	}

	private static AntigravityUsageR1SectionLineRule FreezeLine(
		AntigravityUsageR1SectionLineRule line)
	{
		ArgumentNullException.ThrowIfNull(line);
		ArgumentNullException.ThrowIfNull(line.Alternatives);

		if ((line.RowIndex < 0) ||
			!IsStableToken(line.StableLineId, 128) ||
			!Enum.IsDefined(line.Role) ||
			(line.Alternatives.Count < 1) ||
			(line.Alternatives.Count > 8))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 physical line is invalid.");
		}

		AntigravityUsageR1SectionLinePattern[] alternatives =
			line.Alternatives
				.Select(FreezePattern)
				.ToArray();
		HashSet<string> patternIds = new(StringComparer.Ordinal);

		if (alternatives.Any(pattern => !patternIds.Add(pattern.PatternId)))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 line alternatives are ambiguous.");
		}

		foreach (AntigravityUsageR1SectionLinePattern pattern in alternatives)
		{
			ValidatePatternForRole(line.Role, pattern);
		}

		return line with { Alternatives = Array.AsReadOnly(alternatives) };
	}

	private static AntigravityUsageR1SectionLinePattern FreezePattern(
		AntigravityUsageR1SectionLinePattern pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(pattern.Tokens);

		if (!IsStableToken(pattern.PatternId, 128) ||
			(pattern.Tokens.Count < 1) ||
			(pattern.Tokens.Count > 32))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 line pattern is invalid.");
		}

		AntigravityUsageR1SectionToken[] tokens = pattern.Tokens
			.Select(FreezeToken)
			.ToArray();

		if (tokens.All(token =>
			token is AntigravityUsageR1SectionLiteralToken))
		{
			string renderedLine = string.Concat(tokens
				.Cast<AntigravityUsageR1SectionLiteralToken>()
				.Select(token => token.Value));

			if (!IsFixedLiteral(renderedLine, allowEmpty: true))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 exact line is invalid.");
			}
		}
		else
		{
			if (tokens
				.OfType<AntigravityUsageR1SectionLiteralToken>()
				.Any(token => token.Value.Length == 0))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 dynamic line has an empty anchor.");
			}

			if ((tokens[^1] is
					AntigravityUsageR1SectionLiteralToken finalLiteral) &&
				!IsFixedLiteral(finalLiteral.Value, allowEmpty: false))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 dynamic line has an invalid suffix.");
			}
		}

		for (int index = 1; index < tokens.Length; index++)
		{
			if ((tokens[index - 1] is not AntigravityUsageR1SectionLiteralToken) &&
				(tokens[index] is not AntigravityUsageR1SectionLiteralToken))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 dynamic tokens require literal anchors.");
			}
		}

		for (int index = 0; index < tokens.Length; index++)
		{
			if (tokens[index] is AntigravityUsageR1SectionIdentityToken)
			{
				bool hasPrefixAnchor = (index > 0) &&
					(tokens[index - 1] is
						AntigravityUsageR1SectionLiteralToken prefix) &&
					(prefix.Value.Length > 0);
				bool hasSuffixAnchor = (index < (tokens.Length - 1)) &&
					(tokens[index + 1] is
						AntigravityUsageR1SectionLiteralToken suffix) &&
					(suffix.Value.Length > 0);

				if (!hasPrefixAnchor && !hasSuffixAnchor)
				{
					throw new InvalidDataException(
						"The reviewed AGY R1 identity is not anchored.");
				}
			}

			if (tokens[index] is AntigravityUsageR1SectionUnsignedAmountToken)
			{
				bool hasPrefixAnchor = (index > 0) &&
					(tokens[index - 1] is
						AntigravityUsageR1SectionLiteralToken prefix) &&
					(prefix.Value.Length > 0);
				bool hasSuffixAnchor = (index < (tokens.Length - 1)) &&
					(tokens[index + 1] is
						AntigravityUsageR1SectionLiteralToken suffix) &&
					(suffix.Value.Length > 0);

				if (!hasPrefixAnchor && !hasSuffixAnchor)
				{
					throw new InvalidDataException(
						"The reviewed AGY R1 unsigned amount is not anchored.");
				}
			}

			if ((tokens[index] is AntigravityUsageR1SectionOuterLineShapeToken) &&
				(tokens.Length != 1))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 outer line shape must own the whole line.");
			}

			if (tokens[index] is not AntigravityUsageR1SectionOpaqueToken)
			{
				continue;
			}

			if ((index == 0) ||
				(index == (tokens.Length - 1)) ||
				(tokens[index - 1] is not AntigravityUsageR1SectionLiteralToken before) ||
				(tokens[index + 1] is not AntigravityUsageR1SectionLiteralToken after) ||
				(before.Value.Length == 0) ||
				(after.Value.Length == 0))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 opaque chrome is not narrowly anchored.");
			}
		}

		return pattern with { Tokens = Array.AsReadOnly(tokens) };
	}

	private static AntigravityUsageR1SectionToken FreezeToken(
		AntigravityUsageR1SectionToken token)
	{
		ArgumentNullException.ThrowIfNull(token);

		return token switch
		{
			AntigravityUsageR1SectionLiteralToken literal
				when IsLiteralTokenValue(literal.Value, allowEmpty: true) =>
					literal,
			AntigravityUsageR1SectionIdentityToken identity => identity,
			AntigravityUsageR1SectionPercentToken percent
				when Enum.IsDefined(percent.Meaning) &&
					(percent.DecimalPlaces >= 0) &&
					(percent.DecimalPlaces <= 4) => percent,
			AntigravityUsageR1SectionCountdownToken countdown =>
				countdown with { Rule = FreezeCountdownRule(countdown.Rule) },
			AntigravityUsageR1SectionAvailabilityToken availability =>
				availability with
				{
					Values = FreezeAvailabilityRules(availability.Values)
				},
			AntigravityUsageR1SectionProgressMeterToken meter
				when IsMeterRuleValid(meter.Rule) => meter,
			AntigravityUsageR1SectionPaginationNumberToken pagination
				when Enum.IsDefined(pagination.Field) => pagination,
			AntigravityUsageR1SectionOpaqueToken opaque
				when (opaque.MaximumLength >= 1) &&
					(opaque.MaximumLength <= MaximumOpaqueLength) => opaque,
			AntigravityUsageR1SectionUnsignedAmountToken amount
				when (amount.MinimumDigits >= 1) &&
					(amount.MinimumDigits <= amount.MaximumDigits) &&
					(amount.MaximumDigits <= MaximumUnsignedAmountDigits) => amount,
			AntigravityUsageR1SectionOuterLineShapeToken outerLineShape =>
				FreezeOuterLineShapeToken(outerLineShape),
			_ => throw new InvalidDataException(
				"The reviewed AGY R1 line token is invalid.")
		};
	}

	private static AntigravityUsageR1SectionOuterLineShapeToken
		FreezeOuterLineShapeToken(
			AntigravityUsageR1SectionOuterLineShapeToken token)
	{
		string prefix = token.PublicPrefix ?? string.Empty;
		string suffix = token.PublicSuffix ?? string.Empty;

		if (!IsLiteralTokenValue(prefix, allowEmpty: true) ||
			!IsLiteralTokenValue(suffix, allowEmpty: true) ||
			((prefix.Length == 0) && (suffix.Length == 0)) ||
			((suffix.Length > 0) &&
				!IsFixedLiteral(suffix, allowEmpty: false)) ||
			(token.MinimumDynamicLength < 1) ||
			(token.MinimumDynamicLength > token.MaximumDynamicLength) ||
			(token.MaximumDynamicLength > MaximumOuterLineDynamicLength) ||
			!Enum.IsDefined(token.AllowedCharacters))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 outer line shape is invalid.");
		}

		return token with
		{
			PublicPrefix = prefix,
			PublicSuffix = suffix
		};
	}

	private static AntigravityUsageR1SectionCountdownRule FreezeCountdownRule(
		AntigravityUsageR1SectionCountdownRule rule)
	{
		ArgumentNullException.ThrowIfNull(rule);
		ArgumentNullException.ThrowIfNull(rule.Units);

		if (!IsLiteralTokenValue(rule.Separator, allowEmpty: false) ||
			(rule.Units.Count < 1) ||
			(rule.Units.Count > 3) ||
			(rule.MaximumTotalMinutes < 1) ||
			(rule.MaximumTotalMinutes > MaximumCountdownMinutes))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 countdown rule is invalid.");
		}

		AntigravityUsageR1SectionCountdownUnitRule[] units = rule.Units
			.OrderBy(unit => unit.Order)
			.ToArray();
		HashSet<AntigravityUsageR1SectionCountdownUnit> unitKinds = new();

		for (int index = 0; index < units.Length; index++)
		{
			AntigravityUsageR1SectionCountdownUnitRule unit = units[index] ??
				throw new InvalidDataException(
					"The reviewed AGY R1 countdown unit is invalid.");

			if ((unit.Order != index) ||
				!Enum.IsDefined(unit.Unit) ||
				!unitKinds.Add(unit.Unit) ||
				!IsFixedLiteral(unit.SingularLiteral, allowEmpty: false) ||
				!IsFixedLiteral(unit.PluralLiteral, allowEmpty: false))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 countdown unit is invalid.");
			}
		}

		if (!units.Select(unit => unit.Unit).SequenceEqual(
			units.Select(unit => unit.Unit).OrderBy(UnitMagnitude).Reverse()))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 countdown units are not largest first.");
		}

		return rule with { Units = Array.AsReadOnly(units) };
	}

	private static IReadOnlyList<AntigravityUsageR1SectionAvailabilityRule>
		FreezeAvailabilityRules(
			IReadOnlyList<AntigravityUsageR1SectionAvailabilityRule> values)
	{
		ArgumentNullException.ThrowIfNull(values);

		if ((values.Count < 1) || (values.Count > 16))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 availability values are invalid.");
		}

		AntigravityUsageR1SectionAvailabilityRule[] frozen = values.ToArray();
		HashSet<string> ids = new(StringComparer.Ordinal);
		HashSet<string> literals = new(StringComparer.Ordinal);

		foreach (AntigravityUsageR1SectionAvailabilityRule value in frozen)
		{
			if ((value is null) ||
				!IsStableToken(value.StableStatusId, 128) ||
				!IsFixedLiteral(value.RenderedLiteral, allowEmpty: false) ||
				!ids.Add(value.StableStatusId) ||
				!literals.Add(value.RenderedLiteral))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 availability value is invalid.");
			}
		}

		if (frozen.Any(first => frozen.Any(second =>
			!ReferenceEquals(first, second) &&
			second.RenderedLiteral.StartsWith(
				first.RenderedLiteral,
				StringComparison.Ordinal))))
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 availability values have ambiguous prefixes.");
		}

		return Array.AsReadOnly(frozen);
	}

	private static bool IsMeterRuleValid(
		AntigravityUsageR1SectionProgressMeterRule rule)
	{
		return (rule is not null) &&
			(rule.Width >= 1) &&
			(rule.Width <= 200) &&
			IsSinglePrintableCharacter(rule.FilledGlyph) &&
			IsSinglePrintableCharacter(rule.EmptyGlyph) &&
			!string.Equals(
				rule.FilledGlyph,
				rule.EmptyGlyph,
				StringComparison.Ordinal) &&
			Enum.IsDefined(rule.Meaning) &&
			Enum.IsDefined(rule.Rounding);
	}

	private static void ValidatePatternForRole(
		AntigravityUsageR1SectionLineRole role,
		AntigravityUsageR1SectionLinePattern pattern)
	{
		int identities = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionIdentityToken);
		int opaques = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionOpaqueToken);
		int unsignedAmounts = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionUnsignedAmountToken);
		int outerLineShapes = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionOuterLineShapeToken);
		int percents = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionPercentToken);
		int meters = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionProgressMeterToken);
		int countdowns = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionCountdownToken);
		int availabilities = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionAvailabilityToken);
		int paginationNumbers = pattern.Tokens.Count(
			token => token is AntigravityUsageR1SectionPaginationNumberToken);
		AntigravityUsageR1SectionPaginationField[] paginationFields =
			pattern.Tokens
				.OfType<AntigravityUsageR1SectionPaginationNumberToken>()
				.Select(token => token.Field)
				.ToArray();
		bool hasExactPaginationFields = paginationFields.Length == 3 &&
			paginationFields.Distinct().Count() == 3 &&
			paginationFields.Contains(
				AntigravityUsageR1SectionPaginationField.Start) &&
			paginationFields.Contains(
				AntigravityUsageR1SectionPaginationField.End) &&
			paginationFields.Contains(
				AntigravityUsageR1SectionPaginationField.Total);
		bool valid = role switch
		{
			AntigravityUsageR1SectionLineRole.Fixed or
			AntigravityUsageR1SectionLineRole.SectionHeading or
			AntigravityUsageR1SectionLineRole.WindowLabel =>
				(identities + opaques + unsignedAmounts + outerLineShapes +
				 percents + meters + countdowns + availabilities +
				 paginationNumbers) == 0,
			AntigravityUsageR1SectionLineRole.OuterChrome =>
				((opaques + outerLineShapes) == 1) &&
				((identities + unsignedAmounts + percents + meters +
				  countdowns + availabilities + paginationNumbers) == 0),
			AntigravityUsageR1SectionLineRole.NonQuotaDynamic =>
				(unsignedAmounts == 1) &&
				((identities + opaques + outerLineShapes + percents + meters +
				  countdowns + availabilities + paginationNumbers) == 0),
			AntigravityUsageR1SectionLineRole.Identity =>
				(identities == 1) &&
				((opaques + unsignedAmounts + outerLineShapes + percents +
				  meters + countdowns + availabilities + paginationNumbers) == 0),
			AntigravityUsageR1SectionLineRole.Capacity =>
				(identities == 0) && (opaques == 0) &&
				(unsignedAmounts == 0) && (outerLineShapes == 0) &&
				(countdowns == 0) && (paginationNumbers == 0) &&
				(((percents == 1) && (meters == 1) &&
				  (availabilities == 0)) ||
				 ((percents == 0) && (meters == 0) &&
				  (availabilities == 1))),
			AntigravityUsageR1SectionLineRole.Reset =>
				(identities == 0) && (opaques == 0) &&
				(unsignedAmounts == 0) && (outerLineShapes == 0) &&
				(meters == 0) &&
				(paginationNumbers == 0) &&
				(((countdowns == 1) && (percents <= 1) &&
				  (availabilities == 0)) ||
				 ((countdowns == 0) && (percents == 0) &&
				  (availabilities == 1)) ||
				 ((countdowns == 0) && (percents == 0) &&
				  (availabilities == 0))),
			AntigravityUsageR1SectionLineRole.Pagination =>
				(paginationNumbers == 3) &&
				hasExactPaginationFields &&
				((identities + opaques + unsignedAmounts + outerLineShapes +
				  percents + meters + countdowns + availabilities) == 0),
			_ => false
		};

		if (!valid)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 line tokens do not match their role.");
		}
	}

	private static void ValidateTopology(
		IReadOnlyList<AntigravityUsageR1SectionRule> sections,
		IReadOnlyList<AntigravityUsageR1SectionLineRule> lines,
		AntigravityUsageR1SectionPaginationMode paginationMode)
	{
		Dictionary<string, AntigravityUsageR1SectionRule> sectionById =
			sections.ToDictionary(
				section => section.StableSectionId,
				StringComparer.Ordinal);
		Dictionary<string, (string SectionId,
			AntigravityUsageR1SectionWindowRule Window)> windowById = new(
			StringComparer.Ordinal);

		foreach (AntigravityUsageR1SectionRule section in sections)
		{
			foreach (AntigravityUsageR1SectionWindowRule window in
				section.Windows)
			{
				if (!windowById.TryAdd(
					window.StableWindowId,
					(section.StableSectionId, window)))
				{
					throw new InvalidDataException(
						"The reviewed AGY R1 window IDs are not globally unique.");
				}
			}
		}

		if (lines.Count(line =>
			line.Role == AntigravityUsageR1SectionLineRole.Identity) != 1)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 layout must own one identity line.");
		}

		int paginationLineCount = lines.Count(line =>
			line.Role == AntigravityUsageR1SectionLineRole.Pagination);
		int expectedPaginationLineCount = paginationMode ==
			AntigravityUsageR1SectionPaginationMode.Counter
			? 1
			: 0;

		if (paginationLineCount != expectedPaginationLineCount)
		{
			throw new InvalidDataException(
				"The reviewed AGY R1 pagination ownership is invalid.");
		}

		foreach (AntigravityUsageR1SectionRule section in sections)
		{
			AntigravityUsageR1SectionLineRule[] headings = lines
				.Where(line =>
					(line.Role ==
						AntigravityUsageR1SectionLineRole.SectionHeading) &&
					string.Equals(
						line.StableSectionId,
						section.StableSectionId,
						StringComparison.Ordinal))
				.ToArray();

			if ((headings.Length != 1) ||
				!headings[0].Alternatives.All(pattern =>
					ContainsLiteral(pattern, section.RenderedLiteral)))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 section heading is not exact.");
			}
		}

		foreach ((string windowId,
			(string sectionId, AntigravityUsageR1SectionWindowRule window)) in
			windowById)
		{
			AntigravityUsageR1SectionLineRule[] labels = FindWindowLines(
				lines,
				windowId,
				AntigravityUsageR1SectionLineRole.WindowLabel);
			AntigravityUsageR1SectionLineRule[] capacities = FindWindowLines(
				lines,
				windowId,
				AntigravityUsageR1SectionLineRole.Capacity);
			AntigravityUsageR1SectionLineRule[] resets = FindWindowLines(
				lines,
				windowId,
				AntigravityUsageR1SectionLineRole.Reset);

			if ((labels.Length != 1) ||
				(capacities.Length != 1) ||
				(resets.Length != 1) ||
				!labels[0].Alternatives.All(pattern =>
					ContainsLiteral(pattern, window.RenderedLiteral)) ||
				!string.Equals(
					labels[0].StableSectionId,
					sectionId,
					StringComparison.Ordinal) ||
				!string.Equals(
					capacities[0].StableSectionId,
					sectionId,
					StringComparison.Ordinal) ||
				!string.Equals(
					resets[0].StableSectionId,
					sectionId,
					StringComparison.Ordinal) ||
				!AreWindowAlternativesValid(
					window,
					capacities[0],
					resets[0]))
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 quota window topology is invalid.");
			}
		}

		foreach (AntigravityUsageR1SectionLineRule line in lines)
		{
			bool hasSection = line.StableSectionId is not null;
			bool hasWindow = line.StableWindowId is not null;
			bool isKnownSection = !hasSection ||
				sectionById.ContainsKey(line.StableSectionId!);
			bool isKnownWindow = !hasWindow ||
				windowById.ContainsKey(line.StableWindowId!);
			bool hasValidOwnership = line.Role switch
			{
				AntigravityUsageR1SectionLineRole.SectionHeading =>
					hasSection && !hasWindow,
				AntigravityUsageR1SectionLineRole.WindowLabel or
					AntigravityUsageR1SectionLineRole.Capacity or
					AntigravityUsageR1SectionLineRole.Reset =>
					hasSection && hasWindow,
				AntigravityUsageR1SectionLineRole.Fixed or
					AntigravityUsageR1SectionLineRole.OuterChrome or
					AntigravityUsageR1SectionLineRole.NonQuotaDynamic or
					AntigravityUsageR1SectionLineRole.Identity or
					AntigravityUsageR1SectionLineRole.Pagination =>
					!hasSection && !hasWindow,
				_ => false
			};

			if (!isKnownSection || !isKnownWindow || !hasValidOwnership)
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 line ownership is invalid.");
			}
		}

		ValidateSemanticRowOrder(sections, lines);
	}

	private static bool AreWindowAlternativesValid(
		AntigravityUsageR1SectionWindowRule window,
		AntigravityUsageR1SectionLineRule capacityLine,
		AntigravityUsageR1SectionLineRule resetLine)
	{
		bool[] percentageCapacityAlternatives = capacityLine.Alternatives
			.Select(IsPercentageCapacityPatternValid)
			.ToArray();
		bool[] availabilityCapacityAlternatives = capacityLine.Alternatives
			.Select(pattern => pattern.Tokens.Any(token =>
				token is AntigravityUsageR1SectionAvailabilityToken))
			.ToArray();
		bool capacityAlternativesValid = window.CapacityPolicy switch
		{
			AntigravityUsageR1SectionCapacityPolicy.Percentage =>
				percentageCapacityAlternatives.All(value => value),
			AntigravityUsageR1SectionCapacityPolicy.Availability =>
				availabilityCapacityAlternatives.All(value => value),
			AntigravityUsageR1SectionCapacityPolicy.PercentageOrAvailability =>
				percentageCapacityAlternatives.Any(value => value) &&
				availabilityCapacityAlternatives.Any(value => value) &&
				percentageCapacityAlternatives
					.Zip(
						availabilityCapacityAlternatives,
						(hasPercentage, hasAvailability) =>
							hasPercentage != hasAvailability)
					.All(value => value),
			_ => false
		};

		bool[] countdownResetAlternatives = resetLine.Alternatives
			.Select(pattern => pattern.Tokens.Any(token =>
				token is AntigravityUsageR1SectionCountdownToken))
			.ToArray();
		bool[] availabilityResetAlternatives = resetLine.Alternatives
			.Select(pattern => pattern.Tokens.Any(token =>
				token is AntigravityUsageR1SectionAvailabilityToken))
			.ToArray();
		bool[] emptyResetAlternatives = resetLine.Alternatives
			.Select(pattern => !pattern.Tokens.Any(token =>
				token is AntigravityUsageR1SectionCountdownToken or
					AntigravityUsageR1SectionAvailabilityToken))
			.ToArray();
		bool resetAlternativesValid = window.ResetPolicy switch
		{
			AntigravityUsageR1SectionResetPolicy.None =>
				emptyResetAlternatives.All(value => value),
			AntigravityUsageR1SectionResetPolicy.Countdown =>
				countdownResetAlternatives.All(value => value),
			AntigravityUsageR1SectionResetPolicy.Availability =>
				availabilityResetAlternatives.All(value => value),
			AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability =>
				countdownResetAlternatives.Any(value => value) &&
				availabilityResetAlternatives.Any(value => value) &&
				countdownResetAlternatives
					.Zip(
						availabilityResetAlternatives,
						(hasCountdown, hasAvailability) =>
							hasCountdown != hasAvailability)
					.All(value => value),
			_ => false
		};
		int maximumWindowMinutes = window.WindowKind ==
			AntigravityUsageR1SectionWindowKind.Weekly
			? 7 * 24 * 60
			: checked(window.WindowHours!.Value * 60);
		bool countdownBoundsValid = resetLine.Alternatives
			.SelectMany(pattern => pattern.Tokens)
			.OfType<AntigravityUsageR1SectionCountdownToken>()
			.All(token =>
				token.Rule.MaximumTotalMinutes <= maximumWindowMinutes);

		return capacityAlternativesValid &&
			resetAlternativesValid &&
			countdownBoundsValid;
	}

	private static bool IsPercentageCapacityPatternValid(
		AntigravityUsageR1SectionLinePattern pattern)
	{
		AntigravityUsageR1SectionPercentToken? percent = pattern.Tokens
			.OfType<AntigravityUsageR1SectionPercentToken>()
			.SingleOrDefault();
		AntigravityUsageR1SectionProgressMeterToken? meter = pattern.Tokens
			.OfType<AntigravityUsageR1SectionProgressMeterToken>()
			.SingleOrDefault();

		return (percent is not null) &&
			(meter is not null) &&
			(percent.Meaning == meter.Rule.Meaning);
	}

	private static void ValidateSemanticRowOrder(
		IReadOnlyList<AntigravityUsageR1SectionRule> sections,
		IReadOnlyList<AntigravityUsageR1SectionLineRule> lines)
	{
		int previousSectionEndRow = -1;

		foreach (AntigravityUsageR1SectionRule section in sections)
		{
			AntigravityUsageR1SectionLineRule heading = lines.Single(line =>
				(line.Role ==
					AntigravityUsageR1SectionLineRole.SectionHeading) &&
				string.Equals(
					line.StableSectionId,
					section.StableSectionId,
					StringComparison.Ordinal));

			if (heading.RowIndex <= previousSectionEndRow)
			{
				throw new InvalidDataException(
					"The reviewed AGY R1 section row order is invalid.");
			}

			int previousWindowEndRow = heading.RowIndex;

			foreach (AntigravityUsageR1SectionWindowRule window in
				section.Windows)
			{
				AntigravityUsageR1SectionLineRule label = FindWindowLines(
					lines,
					window.StableWindowId,
					AntigravityUsageR1SectionLineRole.WindowLabel).Single();
				AntigravityUsageR1SectionLineRule capacity = FindWindowLines(
					lines,
					window.StableWindowId,
					AntigravityUsageR1SectionLineRole.Capacity).Single();
				AntigravityUsageR1SectionLineRule reset = FindWindowLines(
					lines,
					window.StableWindowId,
					AntigravityUsageR1SectionLineRole.Reset).Single();

				if ((label.RowIndex <= previousWindowEndRow) ||
					(capacity.RowIndex <= label.RowIndex) ||
					(reset.RowIndex <= capacity.RowIndex))
				{
					throw new InvalidDataException(
						"The reviewed AGY R1 quota window row order is invalid.");
				}

				previousWindowEndRow = reset.RowIndex;
			}

			previousSectionEndRow = previousWindowEndRow;
		}
	}

	private static AntigravityUsageR1SectionLineRule[] FindWindowLines(
		IReadOnlyList<AntigravityUsageR1SectionLineRule> lines,
		string windowId,
		AntigravityUsageR1SectionLineRole role)
	{
		return lines
			.Where(line =>
				(line.Role == role) &&
				string.Equals(
					line.StableWindowId,
					windowId,
					StringComparison.Ordinal))
			.ToArray();
	}

	private static bool ContainsLiteral(
		AntigravityUsageR1SectionLinePattern pattern,
		string value)
	{
		return pattern.Tokens.Any(token =>
			token is AntigravityUsageR1SectionLiteralToken literal &&
			string.Equals(
				literal.Value,
				value,
				StringComparison.Ordinal));
	}

	private static LineMatch MatchLine(
		string line,
		AntigravityUsageR1SectionLineRule rule)
	{
		List<LineMatch> matches = new();

		foreach (AntigravityUsageR1SectionLinePattern pattern in
			rule.Alternatives)
		{
			if (TryMatchPattern(line, pattern, out LineMatch? match))
			{
				matches.Add(match!);
			}
		}

		if (matches.Count != 1)
		{
			throw new InvalidDataException(
				"The AGY R1 rendered line is missing or ambiguous.");
		}

		return matches[0];
	}

	private static bool TryMatchPattern(
		string line,
		AntigravityUsageR1SectionLinePattern pattern,
		out LineMatch? match)
	{
		LineMatch candidate = new();
		int position = 0;

		for (int index = 0; index < pattern.Tokens.Count; index++)
		{
			AntigravityUsageR1SectionToken token = pattern.Tokens[index];

			if (token is AntigravityUsageR1SectionLiteralToken literal)
			{
				if (!line.AsSpan(position).StartsWith(
						literal.Value,
						StringComparison.Ordinal))
				{
					match = null;
					return false;
				}

				position += literal.Value.Length;
				continue;
			}

			if (token is AntigravityUsageR1SectionIdentityToken)
			{
				if (!TryCaptureVariable(
						line,
						position,
						pattern.Tokens,
						index,
						out string value,
						out position) ||
					!TryNormalizeIdentity(value, out string? identity) ||
					(candidate.Identity is not null))
				{
					match = null;
					return false;
				}

				candidate.Identity = identity;
				continue;
			}

			if (token is AntigravityUsageR1SectionOpaqueToken opaque)
			{
				if (!TryCaptureVariable(
						line,
						position,
						pattern.Tokens,
						index,
						out string value,
						out position) ||
					(value.Length < 1) ||
					(value.Length > opaque.MaximumLength) ||
					value.Any(char.IsControl))
				{
					match = null;
					return false;
				}

				continue;
			}

			if (token is AntigravityUsageR1SectionUnsignedAmountToken amount)
			{
				if (!TryMatchUnsignedAmount(
						line,
						position,
						amount,
						out position))
				{
					match = null;
					return false;
				}

				continue;
			}

			if (token is AntigravityUsageR1SectionOuterLineShapeToken outerLineShape)
			{
				if (!TryMatchOuterLineShape(
						line,
						position,
						outerLineShape,
						out position))
				{
					match = null;
					return false;
				}

				continue;
			}

			if (token is AntigravityUsageR1SectionPercentToken percent)
			{
				if (!TryParsePercent(
						line,
						position,
						percent,
						out ParsedPercent? parsed,
						out position) ||
					(candidate.Percent is not null))
				{
					match = null;
					return false;
				}

				candidate.Percent = parsed;
				continue;
			}

			if (token is AntigravityUsageR1SectionProgressMeterToken meter)
			{
				if ((position + meter.Rule.Width) > line.Length)
				{
					match = null;
					return false;
				}

				string rendered = line.Substring(position, meter.Rule.Width);
				position += meter.Rule.Width;

				if (!IsMeterShapeValid(rendered, meter.Rule) ||
					(candidate.Meter is not null))
				{
					match = null;
					return false;
				}

				candidate.Meter = new ParsedMeter(meter.Rule, rendered);
				continue;
			}

			if (token is AntigravityUsageR1SectionCountdownToken countdown)
			{
				if (!TryCaptureVariable(
						line,
						position,
						pattern.Tokens,
						index,
						out string value,
						out position) ||
					!TryParseCountdown(value, countdown.Rule, out TimeSpan parsed) ||
					(candidate.Countdown is not null))
				{
					match = null;
					return false;
				}

				candidate.Countdown = parsed;
				continue;
			}

			if (token is AntigravityUsageR1SectionAvailabilityToken availability)
			{
				AntigravityUsageR1SectionAvailabilityRule[] values =
					availability.Values
						.Where(value => line.AsSpan(position).StartsWith(
							value.RenderedLiteral,
							StringComparison.Ordinal))
						.ToArray();

				if ((values.Length != 1) ||
					(candidate.AvailabilityStatusId is not null))
				{
					match = null;
					return false;
				}

				candidate.AvailabilityStatusId = values[0].StableStatusId;
				position += values[0].RenderedLiteral.Length;
				continue;
			}

			if (token is AntigravityUsageR1SectionPaginationNumberToken number)
			{
				if (!TryParseUnsignedInteger(
						line,
						position,
						out int parsed,
						out position) ||
					!TrySetPaginationNumber(candidate, number.Field, parsed))
				{
					match = null;
					return false;
				}

				continue;
			}

			match = null;
			return false;
		}

		if (position != line.Length)
		{
			match = null;
			return false;
		}

		match = candidate;
		return true;
	}

	private static bool TryCaptureVariable(
		string line,
		int position,
		IReadOnlyList<AntigravityUsageR1SectionToken> tokens,
		int tokenIndex,
		out string value,
		out int nextPosition)
	{
		if ((tokenIndex + 1) < tokens.Count)
		{
			if (tokens[tokenIndex + 1] is not
				AntigravityUsageR1SectionLiteralToken anchor ||
				(anchor.Value.Length == 0))
			{
				value = string.Empty;
				nextPosition = position;
				return false;
			}

			int anchorIndex = line.IndexOf(
				anchor.Value,
				position,
				StringComparison.Ordinal);

			if (anchorIndex < position)
			{
				value = string.Empty;
				nextPosition = position;
				return false;
			}

			value = line[position..anchorIndex];
			nextPosition = anchorIndex;
			return true;
		}

		value = line[position..];
		nextPosition = line.Length;
		return true;
	}

	private static bool TryMatchUnsignedAmount(
		string line,
		int position,
		AntigravityUsageR1SectionUnsignedAmountToken rule,
		out int nextPosition)
	{
		int start = position;

		while ((position < line.Length) &&
			(line[position] >= '0') &&
			(line[position] <= '9'))
		{
			position++;
		}

		int digitCount = position - start;
		bool isMatch = (digitCount >= rule.MinimumDigits) &&
			(digitCount <= rule.MaximumDigits);
		nextPosition = isMatch ? position : start;
		return isMatch;
	}

	private static bool TryMatchOuterLineShape(
		string line,
		int position,
		AntigravityUsageR1SectionOuterLineShapeToken rule,
		out int nextPosition)
	{
		string prefix = rule.PublicPrefix ?? string.Empty;
		string suffix = rule.PublicSuffix ?? string.Empty;
		ReadOnlySpan<char> remaining = line.AsSpan(position);

		if (!remaining.StartsWith(prefix, StringComparison.Ordinal))
		{
			nextPosition = position;
			return false;
		}

		int dynamicStart = position + prefix.Length;
		int dynamicLength = line.Length - dynamicStart - suffix.Length;

		if ((dynamicLength < rule.MinimumDynamicLength) ||
			(dynamicLength > rule.MaximumDynamicLength) ||
			!line.AsSpan(dynamicStart).EndsWith(
				suffix,
				StringComparison.Ordinal) ||
			!IsOuterLineDynamicTextAllowed(
				line.AsSpan(dynamicStart, dynamicLength),
				rule.AllowedCharacters))
		{
			nextPosition = position;
			return false;
		}

		nextPosition = line.Length;
		return true;
	}

	private static bool IsOuterLineDynamicTextAllowed(
		ReadOnlySpan<char> value,
		AntigravityUsageR1SectionOuterLineCharacterClass allowedCharacters)
	{
		if (allowedCharacters ==
			AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable)
		{
			foreach (char character in value)
			{
				if ((character < ' ') || (character > '~'))
				{
					return false;
				}
			}

			return true;
		}

		if (allowedCharacters !=
			AntigravityUsageR1SectionOuterLineCharacterClass.UnicodeVisibleText)
		{
			return false;
		}

		while (!value.IsEmpty)
		{
			OperationStatus status = Rune.DecodeFromUtf16(
				value,
				out Rune rune,
				out int consumed);

			if ((status != OperationStatus.Done) ||
				!IsUnicodeVisibleTextRune(rune))
			{
				return false;
			}

			value = value[consumed..];
		}

		return true;
	}

	private static bool IsUnicodeVisibleTextRune(Rune rune)
	{
		return Rune.GetUnicodeCategory(rune) switch
		{
			UnicodeCategory.Control or
			UnicodeCategory.Format or
			UnicodeCategory.Surrogate or
			UnicodeCategory.PrivateUse or
			UnicodeCategory.OtherNotAssigned or
			UnicodeCategory.LineSeparator or
			UnicodeCategory.ParagraphSeparator => false,
			_ => true
		};
	}

	private static bool TryNormalizeIdentity(
		string rendered,
		out string? identity)
	{
		if ((rendered.Length < 1) ||
			(rendered.Length > MaximumIdentityLength) ||
			!string.Equals(rendered, rendered.Trim(), StringComparison.Ordinal) ||
			rendered.Any(char.IsControl))
		{
			identity = null;
			return false;
		}

		string normalized = rendered
			.Normalize(NormalizationForm.FormKC)
			.ToLowerInvariant();

		if ((normalized.Length < 1) ||
			(normalized.Length > MaximumIdentityLength) ||
			normalized.Any(char.IsControl))
		{
			identity = null;
			return false;
		}

		identity = normalized;
		return true;
	}

	private static bool TryParsePercent(
		string line,
		int position,
		AntigravityUsageR1SectionPercentToken rule,
		out ParsedPercent? parsed,
		out int nextPosition)
	{
		int start = position;

		while ((position < line.Length) &&
			(line[position] >= '0') &&
			(line[position] <= '9'))
		{
			position++;
		}

		if (position == start)
		{
			parsed = null;
			nextPosition = start;
			return false;
		}

		if (rule.DecimalPlaces > 0)
		{
			if ((position >= line.Length) || (line[position] != '.'))
			{
				parsed = null;
				nextPosition = start;
				return false;
			}

			position++;
			int fractionalStart = position;

			while ((position < line.Length) &&
				(line[position] >= '0') &&
				(line[position] <= '9'))
			{
				position++;
			}

			if ((position - fractionalStart) != rule.DecimalPlaces)
			{
				parsed = null;
				nextPosition = start;
				return false;
			}
		}

		if ((position >= line.Length) || (line[position] != '%') ||
			!decimal.TryParse(
				line.AsSpan(start, position - start),
				NumberStyles.AllowDecimalPoint,
				CultureInfo.InvariantCulture,
				out decimal value) ||
			(value < 0m) ||
			(value > 100m))
		{
			parsed = null;
			nextPosition = start;
			return false;
		}

		position++;
		decimal used = rule.Meaning ==
			AntigravityUsageR1SectionPercentMeaning.Used
			? value
			: 100m - value;
		decimal remaining = rule.Meaning ==
			AntigravityUsageR1SectionPercentMeaning.Remaining
			? value
			: 100m - value;
		parsed = new ParsedPercent(
			used,
			remaining,
			rule.Meaning,
			rule.DecimalPlaces);
		nextPosition = position;
		return true;
	}

	private static bool IsMeterShapeValid(
		string rendered,
		AntigravityUsageR1SectionProgressMeterRule rule)
	{
		bool hasObservedEmpty = false;

		foreach (char character in rendered)
		{
			string glyph = character.ToString();

			if (string.Equals(glyph, rule.EmptyGlyph, StringComparison.Ordinal))
			{
				hasObservedEmpty = true;
				continue;
			}

			if (!string.Equals(glyph, rule.FilledGlyph, StringComparison.Ordinal) ||
				hasObservedEmpty)
			{
				return false;
			}
		}

		return true;
	}

	private static void ValidateMeter(LineMatch match)
	{
		if ((match.Meter is null) || (match.Percent is null) ||
			(match.Meter.Rule.Meaning != match.Percent.Meaning))
		{
			throw new InvalidDataException(
				"The AGY R1 quota meter is incomplete.");
		}

		decimal percentage = match.Meter.Rule.Meaning ==
			AntigravityUsageR1SectionPercentMeaning.Remaining
			? match.Percent.RemainingPercent
			: match.Percent.UsedPercent;
		decimal exactCells =
			percentage * match.Meter.Rule.Width / 100m;
		int expectedCells = match.Meter.Rule.Rounding switch
		{
			AntigravityUsageR1SectionMeterRounding.Floor =>
				decimal.ToInt32(decimal.Floor(exactCells)),
			AntigravityUsageR1SectionMeterRounding.Ceiling =>
				decimal.ToInt32(decimal.Ceiling(exactCells)),
			AntigravityUsageR1SectionMeterRounding.Nearest =>
				decimal.ToInt32(decimal.Round(
					exactCells,
					0,
					MidpointRounding.AwayFromZero)),
			_ => -1
		};
		int actualCells = match.Meter.Rendered.Count(character =>
			string.Equals(
				character.ToString(),
				match.Meter.Rule.FilledGlyph,
				StringComparison.Ordinal));

		if (actualCells != expectedCells)
		{
			throw new InvalidDataException(
				"The AGY R1 quota meter disagrees with its percentage.");
		}
	}

	private static bool TryParseCountdown(
		string rendered,
		AntigravityUsageR1SectionCountdownRule rule,
		out TimeSpan value)
	{
		string[] components = rendered.Split(
			rule.Separator,
			StringSplitOptions.None);

		if (components.Length != rule.Units.Count)
		{
			value = default;
			return false;
		}

		long totalMinutes = 0;

		for (int index = 0; index < components.Length; index++)
		{
			AntigravityUsageR1SectionCountdownUnitRule unit =
				rule.Units[index];

			if (!TryParseCountdownComponent(
					components[index],
					unit,
					out int amount))
			{
				value = default;
				return false;
			}

			if ((unit.Unit == AntigravityUsageR1SectionCountdownUnit.Minutes) &&
				(amount > 59))
			{
				value = default;
				return false;
			}

			try
			{
				totalMinutes = checked(
					totalMinutes +
					((long)amount * UnitMagnitude(unit.Unit)));
			}
			catch (OverflowException)
			{
				value = default;
				return false;
			}
		}

		if ((totalMinutes < 0) ||
			(totalMinutes > rule.MaximumTotalMinutes))
		{
			value = default;
			return false;
		}

		value = TimeSpan.FromMinutes(totalMinutes);
		return true;
	}

	private static bool TryParseCountdownComponent(
		string component,
		AntigravityUsageR1SectionCountdownUnitRule unit,
		out int amount)
	{
		amount = 0;
		int digitCount = 0;

		while ((digitCount < component.Length) &&
			(component[digitCount] >= '0') &&
			(component[digitCount] <= '9'))
		{
			digitCount++;
		}

		if ((digitCount == 0) ||
			!int.TryParse(
				component.AsSpan(0, digitCount),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out amount))
		{
			return false;
		}

		string suffix = component[digitCount..];
		string expected = amount == 1
			? unit.SingularLiteral
			: unit.PluralLiteral;
		return string.Equals(suffix, expected, StringComparison.Ordinal) ||
			(string.Equals(
				unit.SingularLiteral,
				unit.PluralLiteral,
				StringComparison.Ordinal) &&
			 string.Equals(suffix, unit.SingularLiteral, StringComparison.Ordinal));
	}

	private static int UnitMagnitude(
		AntigravityUsageR1SectionCountdownUnit unit)
	{
		return unit switch
		{
			AntigravityUsageR1SectionCountdownUnit.Days => 24 * 60,
			AntigravityUsageR1SectionCountdownUnit.Hours => 60,
			AntigravityUsageR1SectionCountdownUnit.Minutes => 1,
			_ => 0
		};
	}

	private static bool TryParseUnsignedInteger(
		string line,
		int position,
		out int value,
		out int nextPosition)
	{
		value = 0;
		int start = position;

		while ((position < line.Length) &&
			(line[position] >= '0') &&
			(line[position] <= '9'))
		{
			position++;
		}

		bool isParsed = (position > start) &&
			int.TryParse(
				line.AsSpan(start, position - start),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out value);
		nextPosition = isParsed ? position : start;
		return isParsed;
	}

	private static bool TrySetPaginationNumber(
		LineMatch match,
		AntigravityUsageR1SectionPaginationField field,
		int value)
	{
		switch (field)
		{
			case AntigravityUsageR1SectionPaginationField.Start
				when match.PaginationStart is null:
				match.PaginationStart = value;
				return true;
			case AntigravityUsageR1SectionPaginationField.End
				when match.PaginationEnd is null:
				match.PaginationEnd = value;
				return true;
			case AntigravityUsageR1SectionPaginationField.Total
				when match.PaginationTotal is null:
				match.PaginationTotal = value;
				return true;
			default:
				return false;
		}
	}

	private static string NormalizeLine(string line)
	{
		ArgumentNullException.ThrowIfNull(line);

		if (line.Any(char.IsControl))
		{
			throw new InvalidDataException(
				"The AGY R1 rendered line contains a control character.");
		}

		return line.TrimEnd(' ');
	}

	private static string ComputePageFingerprint(
		AntigravityUsageR1SectionSpec spec,
		string schemaFingerprint)
	{
		StringBuilder canonical = new();
		Append(canonical, "schema", schemaFingerprint);
		Append(canonical, "pagination-mode", spec.PaginationMode.ToString());

		foreach (AntigravityUsageR1SectionLineRule line in spec.Lines)
		{
			Append(canonical, "line", string.Join(
				'|',
				line.RowIndex,
				line.Role,
				line.StableSectionId ?? "-",
				line.StableWindowId ?? "-"));
		}

		foreach (AntigravityUsageR1SectionRule section in spec.Sections)
		{
			Append(canonical, "section", section.StableSectionId);

			foreach (AntigravityUsageR1SectionWindowRule window in
				section.Windows)
			{
				Append(canonical, "window", window.StableWindowId);
			}
		}

		return Hash(canonical.ToString());
	}

	private static void AppendToken(
		StringBuilder canonical,
		AntigravityUsageR1SectionToken token)
	{
		switch (token)
		{
			case AntigravityUsageR1SectionLiteralToken literal:
				Append(canonical, "token-literal", literal.Value);
				break;
			case AntigravityUsageR1SectionIdentityToken:
				Append(canonical, "token-identity", "formkc;lower;trim-exact;max320");
				break;
			case AntigravityUsageR1SectionPercentToken percent:
				Append(canonical, "token-percent", string.Join(
					'|',
					percent.Meaning,
					percent.DecimalPlaces));
				break;
			case AntigravityUsageR1SectionCountdownToken countdown:
				Append(canonical, "token-countdown", string.Join(
					'|',
					countdown.Rule.Separator,
					countdown.Rule.MaximumTotalMinutes));

				foreach (AntigravityUsageR1SectionCountdownUnitRule unit in
					countdown.Rule.Units)
				{
					Append(canonical, "countdown-unit", string.Join(
						'|',
						unit.Order,
						unit.Unit,
						unit.SingularLiteral,
						unit.PluralLiteral));
				}
				break;
			case AntigravityUsageR1SectionAvailabilityToken availability:
				foreach (AntigravityUsageR1SectionAvailabilityRule value in
					availability.Values.OrderBy(
						value => value.StableStatusId,
						StringComparer.Ordinal))
				{
					Append(canonical, "availability", string.Join(
						'|',
						value.StableStatusId,
						value.RenderedLiteral));
				}
				break;
			case AntigravityUsageR1SectionProgressMeterToken meter:
				Append(canonical, "token-meter", string.Join(
					'|',
					meter.Rule.Width,
					meter.Rule.FilledGlyph,
					meter.Rule.EmptyGlyph,
					meter.Rule.Meaning,
					meter.Rule.Rounding));
				break;
			case AntigravityUsageR1SectionPaginationNumberToken pagination:
				Append(canonical, "token-pagination", pagination.Field.ToString());
				break;
			case AntigravityUsageR1SectionOpaqueToken opaque:
				Append(canonical, "token-opaque", opaque.MaximumLength.ToString(
					CultureInfo.InvariantCulture));
				break;
			case AntigravityUsageR1SectionUnsignedAmountToken amount:
				Append(canonical, "token-unsigned-amount", string.Join(
					'|',
					amount.MinimumDigits,
					amount.MaximumDigits));
				break;
			case AntigravityUsageR1SectionOuterLineShapeToken outerLineShape:
				Append(canonical, "token-outer-line-shape", string.Join(
					'|',
					outerLineShape.PublicPrefix ?? string.Empty,
					outerLineShape.PublicSuffix ?? string.Empty,
					outerLineShape.MinimumDynamicLength,
					outerLineShape.MaximumDynamicLength,
					outerLineShape.AllowedCharacters));
				break;
			default:
				throw new InvalidDataException(
					"The reviewed AGY R1 token cannot be fingerprinted.");
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

	private static bool IsStableToken(string? value, int maximumLength)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= maximumLength) &&
			string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
			value.All(character =>
				((character >= 'a') && (character <= 'z')) ||
				((character >= '0') && (character <= '9')) ||
				(character == '.') ||
				(character == '-') ||
				(character == '_'));
	}

	private static bool IsFixedLiteral(string? value, bool allowEmpty)
	{
		return IsLiteralTokenValue(value, allowEmpty) &&
			string.Equals(value, value!.TrimEnd(' '), StringComparison.Ordinal);
	}

	private static bool IsLiteralTokenValue(string? value, bool allowEmpty)
	{
		return (value is not null) &&
			(value.Length <= MaximumLiteralLength) &&
			(allowEmpty || (value.Length > 0)) &&
			!value.Any(char.IsControl);
	}

	private static bool IsSinglePrintableCharacter(string? value)
	{
		return (value?.Length == 1) && !char.IsControl(value[0]);
	}

	private static string FormatDecimal(decimal? value)
	{
		return value?.ToString("G29", CultureInfo.InvariantCulture) ?? "-";
	}

}
