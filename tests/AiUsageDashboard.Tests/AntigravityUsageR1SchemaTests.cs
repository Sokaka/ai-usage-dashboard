using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1SchemaTests
{
	private const int ScreenColumns = 120;
	private const int ScreenRows = 12;
	private const string FirstIdentity =
		"first.private.sentinel@example.invalid";
	private const string SecondIdentity =
		"second.private.sentinel@example.invalid";
	private const string FirstReset =
		"2026-07-17T00:00:00.0000000+00:00";
	private const string SecondReset =
		"2026-07-18T12:30:00.0000000+00:00";
	private static readonly byte[] StabilityKey = Enumerable
		.Range(1, 32)
		.Select(value => (byte)value)
		.ToArray();

	[Fact]
	public void ParseReviewedPage_WithDifferentDynamicValues_PreservesSchemaAndPageFingerprints()
	{
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1ParseResult first =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateFirstScreen()),
				spec);
		AntigravityUsageR1Layout layout = CreateReviewedLayout(
			spec,
			first.Capture.PageFingerprint);

		AntigravityUsageR1ParseResult second =
			AntigravityUsageR1SchemaParser.ParseReviewedPage(
				CreateSnapshot(CreateSecondScreen()),
				layout);

		Assert.Equal(
			first.Capture.SchemaFingerprint,
			second.Capture.SchemaFingerprint);
		Assert.Equal(
			first.Capture.PageFingerprint,
			second.Capture.PageFingerprint);
		Assert.NotEqual(first.Page.AccountIdentity, second.Page.AccountIdentity);
		Assert.NotEqual(first.Page.Rows[0], second.Page.Rows[0]);
		Assert.Equal(
			new[]
			{
				"gemini.synthetic.flash",
				"gemini.synthetic.pro"
			},
			second.Capture.StableModelIds);
	}

	[Fact]
	public void ComputeSchemaFingerprint_WhenSchemaFieldChanges_ChangesFingerprint()
	{
		AntigravityUsageR1ReviewedSpec baseline = CreateSpec();
		string baselineFingerprint = ComputeValidatedSchemaFingerprint(baseline);
		AntigravityUsageR1ReviewedSpec[] changedSpecs =
		{
			baseline with { LayoutId = "synthetic-r1-v2" },
			baseline with
			{
				PanelTitle = "SYNTHETIC R1 AGY REVIEWED MODEL QUOTAS"
			},
			baseline with
			{
				PrefixLines = new[] { "DIFFERENT SYNTHETIC AGY CHROME" }
			},
			baseline with { Rows = ScreenRows + 1 },
			baseline with { IsAlternateScreen = true }
		};

		foreach (AntigravityUsageR1ReviewedSpec changedSpec in changedSpecs)
		{
			Assert.NotEqual(
				baselineFingerprint,
				ComputeValidatedSchemaFingerprint(changedSpec));
		}
	}

	[Fact]
	public void ComputeSchemaFingerprint_WhenModelRuleChanges_ChangesFingerprint()
	{
		AntigravityUsageR1ReviewedSpec baseline = CreateSpec();
		string baselineFingerprint = ComputeValidatedSchemaFingerprint(baseline);
		AntigravityUsageR1ModelRule firstModel = baseline.Models[0];
		AntigravityUsageR1ModelRule secondModel = baseline.Models[1];
		AntigravityUsageR1ReviewedSpec[] changedSpecs =
		{
			baseline with
			{
				Models = new[]
				{
					firstModel with
					{
						StableModelId = "gemini.synthetic.flash.changed"
					},
					secondModel
				}
			},
			baseline with
			{
				Models = new[]
				{
					firstModel with
					{
						RenderedLiteral = "Gemini Synthetic Flash Changed"
					},
					secondModel
				}
			},
			baseline with
			{
				Models = new[]
				{
					firstModel with { IsRequired = false },
					secondModel
				}
			}
		};

		foreach (AntigravityUsageR1ReviewedSpec changedSpec in changedSpecs)
		{
			Assert.NotEqual(
				baselineFingerprint,
				ComputeValidatedSchemaFingerprint(changedSpec));
		}
	}

	[Fact]
	public void ParseCalibrationPage_WithWrongLiteralButSameCoarseShape_FailsClosed()
	{
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		string[] validLines = CreateFirstScreen();
		string[] wrongLines = (string[])validLines.Clone();
		wrongLines[4] = "Model IX | Used | Remaining | Reset";
		TerminalScreenSnapshot validSnapshot = CreateSnapshot(validLines);
		TerminalScreenSnapshot wrongSnapshot = CreateSnapshot(wrongLines);

		Assert.Equal(validLines[4].Length, wrongLines[4].Length);
		Assert.Equal(
			AntigravityRedactedStructuralCapture.Create(validSnapshot)
				.StructuralFingerprint,
			AntigravityRedactedStructuralCapture.Create(wrongSnapshot)
				.StructuralFingerprint);
		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				wrongSnapshot,
				spec));
	}

	[Fact]
	public void ParseCalibrationPage_WithUnreviewedChrome_FailsClosed()
	{
		string[] lines = CreateFirstScreen();
		lines[^1] = "UNREVIEWED SYNTHETIC CHROME";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Theory]
	[InlineData("top")]
	[InlineData("middle")]
	[InlineData("bottom")]
	public void ParseCalibrationPage_WithNonSinglePage_FailsClosed(
		string pageKind)
	{
		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateScreen(
					FirstIdentity,
					pageKind,
					$"Gemini Synthetic Flash | 10% | 90% | {FirstReset}",
					"Gemini Synthetic Pro | 20% | 80% | -")),
				CreateSpec()));
	}

	[Fact]
	public void ParseCalibrationPage_WithModelsOutOfOrder_FailsClosed()
	{
		string[] lines = CreateScreen(
			FirstIdentity,
			"single",
			"Gemini Synthetic Pro | 20% | 80% | -",
			$"Gemini Synthetic Flash | 10% | 90% | {FirstReset}");

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Fact]
	public void ParseCalibrationPage_WithControlCharacter_FailsClosed()
	{
		string[] lines = CreateFirstScreen();
		lines[0] = "SYNTHETIC\tAGY CHROME";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Fact]
	public void ComputeStabilityFingerprint_TracksDynamicValuesButIgnoresCursor()
	{
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		string[] stableLines = CreateFirstScreen();
		AntigravityUsageR1ParseResult first =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(stableLines, cursorColumn: 0, cursorRow: 0),
				spec);
		AntigravityUsageR1ParseResult movedCursor =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(stableLines, cursorColumn: 19, cursorRow: 9),
				spec);
		AntigravityUsageR1ParseResult changedValues =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateSecondScreen(), cursorColumn: 19, cursorRow: 9),
				spec);

		string firstFingerprint =
			AntigravityUsageR1SchemaParser.ComputeStabilityFingerprint(
				first,
				StabilityKey);
		string movedCursorFingerprint =
			AntigravityUsageR1SchemaParser.ComputeStabilityFingerprint(
				movedCursor,
				StabilityKey);
		string changedValuesFingerprint =
			AntigravityUsageR1SchemaParser.ComputeStabilityFingerprint(
				changedValues,
				StabilityKey);

		Assert.Equal(firstFingerprint, movedCursorFingerprint);
		Assert.NotEqual(firstFingerprint, changedValuesFingerprint);
	}

	[Fact]
	public void SemanticCapture_DoesNotExposeIdentityQuotaOrResetValues()
	{
		AntigravityUsageR1ParseResult result =
			AntigravityUsageR1SchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateFirstScreen()),
				CreateSpec());

		string json = JsonSerializer.Serialize(result.Capture);
		string[] propertyNames = typeof(AntigravityUsageR1SemanticCapture)
			.GetProperties()
			.Select(property => property.Name)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			new[]
			{
				"LayoutId",
				"PageFingerprint",
				"PageKind",
				"RowCount",
				"SchemaFingerprint",
				"StableModelIds"
			},
			propertyNames);
		Assert.DoesNotContain(
			FirstIdentity,
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(FirstReset, json, StringComparison.Ordinal);
		Assert.DoesNotContain("AccountIdentity", json, StringComparison.Ordinal);
		Assert.DoesNotContain("UsedPercent", json, StringComparison.Ordinal);
		Assert.DoesNotContain("RemainingPercent", json, StringComparison.Ordinal);
		Assert.DoesNotContain("ResetsAt", json, StringComparison.Ordinal);
	}

	private static string ComputeValidatedSchemaFingerprint(
		AntigravityUsageR1ReviewedSpec spec)
	{
		AntigravityUsageR1ReviewedSpec immutableSpec =
			AntigravityUsageR1SchemaParser.FreezeAndValidateSpec(spec);
		return AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(
			immutableSpec);
	}

	private static AntigravityUsageR1Layout CreateReviewedLayout(
		AntigravityUsageR1ReviewedSpec spec,
		string pageFingerprint)
	{
		AntigravityUsageR1ReviewedSpec immutableSpec =
			AntigravityUsageR1SchemaParser.FreezeAndValidateSpec(spec);
		AntigravityUsageR1ReviewedLayoutFile file = new(
			AntigravityUsageR1SchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			immutableSpec,
			AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(immutableSpec),
			new[] { pageFingerprint });
		return file.ToLayout();
	}

	private static AntigravityUsageR1ReviewedSpec CreateSpec()
	{
		return new AntigravityUsageR1ReviewedSpec(
			1,
			"synthetic-r1-v1",
			ScreenColumns,
			ScreenRows,
			false,
			new[] { "SYNTHETIC AGY CHROME" },
			"SYNTHETIC R1 AGY MODEL QUOTAS",
			"Account: ",
			"Page: ",
			"Model ID | Used | Remaining | Reset",
			"END SYNTHETIC MODEL QUOTAS",
			" | ",
			new[] { "SYNTHETIC FOOTER CHROME" },
			new[]
			{
				new AntigravityUsageR1ModelRule(
					"gemini.synthetic.flash",
					"Gemini Synthetic Flash",
					true,
					0),
				new AntigravityUsageR1ModelRule(
					"gemini.synthetic.pro",
					"Gemini Synthetic Pro",
					true,
					1)
			},
			AntigravityUsageR1PaginationPolicy.RequireSinglePage);
	}

	private static string[] CreateFirstScreen()
	{
		return CreateScreen(
			FirstIdentity,
			"single",
			$"Gemini Synthetic Flash | 10% | 90% | {FirstReset}",
			"Gemini Synthetic Pro | 20% | 80% | -");
	}

	private static string[] CreateSecondScreen()
	{
		return CreateScreen(
			SecondIdentity,
			"single",
			"Gemini Synthetic Flash | 75% | 25% | -",
			$"Gemini Synthetic Pro | 0% | 100% | {SecondReset}");
	}

	private static string[] CreateScreen(
		string identity,
		string pageKind,
		params string[] modelRows)
	{
		List<string> lines = new()
		{
			"SYNTHETIC AGY CHROME",
			"SYNTHETIC R1 AGY MODEL QUOTAS",
			$"Account: {identity}",
			$"Page: {pageKind}",
			"Model ID | Used | Remaining | Reset"
		};
		lines.AddRange(modelRows);
		lines.Add("END SYNTHETIC MODEL QUOTAS");
		lines.Add("SYNTHETIC FOOTER CHROME");

		if (lines.Count > ScreenRows)
		{
			throw new InvalidOperationException(
				"The synthetic AGY R1 screen exceeds its viewport.");
		}

		lines.AddRange(Enumerable.Repeat(string.Empty, ScreenRows - lines.Count));
		return lines.ToArray();
	}

	private static TerminalScreenSnapshot CreateSnapshot(
		IReadOnlyList<string> lines,
		int cursorColumn = 0,
		int cursorRow = 0)
	{
		return new TerminalScreenSnapshot(
			ScreenColumns,
			ScreenRows,
			isAlternateScreen: false,
			lines.ToArray(),
			cursorColumn,
			cursorRow,
			isCursorVisible: true,
			isBracketedPasteEnabled: false);
	}
}
