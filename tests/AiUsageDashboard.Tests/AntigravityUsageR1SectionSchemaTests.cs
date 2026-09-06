using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1SectionSchemaTests
{
	private const int Columns = 120;
	private const int Rows = 50;
	private const string FirstIdentity = "first.private@example.invalid";
	private const string SecondIdentity = "second.private@example.invalid";
	private const string Filled = "█";
	private const string Empty = "░";
	private static readonly byte[] StabilityKey = Enumerable
		.Range(1, 32)
		.Select(value => (byte)value)
		.ToArray();

	[Fact]
	public void ParseCalibrationPage_WithReviewedFullSectionPage_ReturnsQuotaSemantics()
	{
		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateLines()),
				CreateSpec());

		Assert.Equal(FirstIdentity, result.Page.AccountIdentity);
		Assert.Null(result.Page.Pagination);
		Assert.Equal(2, result.Page.Sections.Count);
		Assert.Equal(4, result.Capture.WindowCount);
		Assert.Equal(
			new[] { "gemini.weekly", "gemini.rolling-5h", "claude.weekly", "claude.rolling-5h" },
			result.Capture.StableWindowIds);

		AntigravityUsageR1SectionQuotaWindow weekly =
			result.Page.Sections[0].Windows[0];
		Assert.Equal(20m, weekly.Capacity.UsedPercent);
		Assert.Equal(80m, weekly.Capacity.RemainingPercent);
		Assert.Equal(TimeSpan.FromMinutes(150), weekly.Reset.ResetsIn);
		Assert.Null(weekly.Reset.AvailabilityStatusId);

		AntigravityUsageR1SectionQuotaWindow rolling =
			result.Page.Sections[0].Windows[1];
		Assert.Equal(100m, rolling.Capacity.RemainingPercent);
		Assert.Equal("quota_available", rolling.Reset.AvailabilityStatusId);
	}

	[Fact]
	public void ParseReviewedPage_WhenDynamicValuesChange_PreservesPageFingerprint()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionParseResult first =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateLines()),
				spec);
		AntigravityUsageR1SectionLayout layout = CreateLayout(
			spec,
			first.Capture.PageFingerprint);
		string[] changedLines = CreateLines();
		changedLines[15] = "  80% remaining · refreshes in 2h 29m";
		AntigravityUsageR1SectionParseResult changed =
			AntigravityUsageR1SectionSchemaParser.ParseReviewedPage(
				CreateSnapshot(changedLines),
				layout);

		Assert.Equal(
			first.Capture.SchemaFingerprint,
			changed.Capture.SchemaFingerprint);
		Assert.Equal(
			first.Capture.PageFingerprint,
			changed.Capture.PageFingerprint);
		Assert.True(
			AntigravityUsageR1SectionSchemaParser.HasQuotaSemanticDifference(
				first,
				changed));
		Assert.NotEqual(
			AntigravityUsageR1SectionSchemaParser.ComputeStabilityFingerprint(
				first,
				StabilityKey),
			AntigravityUsageR1SectionSchemaParser.ComputeStabilityFingerprint(
				changed,
				StabilityKey));
	}

	[Fact]
	public void ParseReviewedPage_WithProgrammaticallyConstructedPartialSpec_FailsSchemaValidation()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionSpec partial = spec with
		{
			Lines = spec.Lines.Take(spec.Lines.Count - 1).ToArray()
		};
		AntigravityUsageR1SectionLayout layout = new(
			partial,
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				partial),
			new[] { new string('A', 64) }.ToFrozenSet(StringComparer.Ordinal),
			new string('B', 64),
			new string('C', 64));

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseReviewedPage(
				CreateSnapshot(CreateLines()),
				layout));

		Assert.Equal(
			"The reviewed AGY R1 section specification is incomplete.",
			exception.Message);
	}

	[Fact]
	public void HasQuotaSemanticDifference_WhenOnlyIdentityChanges_ReturnsFalse()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionParseResult first =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateLines()),
				spec);
		string[] identityChanged = CreateLines();
		identityChanged[8] = $"Account: {SecondIdentity} │";
		AntigravityUsageR1SectionParseResult second =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(identityChanged),
				spec);

		Assert.NotEqual(first.Page.AccountIdentity, second.Page.AccountIdentity);
		Assert.False(
			AntigravityUsageR1SectionSchemaParser.HasQuotaSemanticDifference(
				first,
				second));
	}

	[Fact]
	public void ParseCalibrationPage_WhenMeterDisagreesWithDecimalPercent_FailsClosed()
	{
		string[] lines = CreateLines();
		lines[14] = $"  {Meter(39)} 80.00%";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Theory]
	[InlineData("80.49", "80")]
	[InlineData("80.50", "81")]
	[InlineData("80.51", "81")]
	public void ParseCalibrationPage_WhenDetailPercentMatchesRoundedCapacity_Succeeds(
		string capacityPercent,
		string detailPercent)
	{
		string[] lines = CreateLines();
		lines[14] = $"  {Meter(40)} {capacityPercent}%";
		lines[15] =
			$"  {detailPercent}% remaining · refreshes in 2h 30m";

		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec());

		Assert.Equal(
			decimal.Parse(
				capacityPercent,
				System.Globalization.CultureInfo.InvariantCulture),
			result.Page.Sections[0].Windows[0].Capacity.RemainingPercent);
	}

	[Fact]
	public void ParseCalibrationPage_WhenDetailPercentDiffersFromRoundedCapacity_FailsClosed()
	{
		string[] lines = CreateLines();
		lines[14] = $"  {Meter(40)} 80.51%";
		lines[15] = "  80% remaining · refreshes in 2h 30m";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Fact]
	public void ParseCalibrationPage_WithPartialPaginationCounter_FailsClosed()
	{
		string[] lines = CreateLines();
		lines[40] = "[1-30 of 37 lines]";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec(withPagination: true)));
	}

	[Fact]
	public void ParseCalibrationPage_WithFullyVisiblePaginationCounter_Succeeds()
	{
		string[] lines = CreateLines();
		lines[40] = "[1-37 of 37 lines]";

		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec(withPagination: true));

		Assert.NotNull(result.Page.Pagination);
		Assert.True(result.Page.Pagination.IsFullyVisible);
	}

	[Fact]
	public void FreezeAndValidateSpec_WhenCounterModeHasNoCounterLine_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with
				{
					PaginationMode =
						AntigravityUsageR1SectionPaginationMode.Counter
				}));
	}

	[Fact]
	public void FreezeAndValidateSpec_WhenFullPageModeHasCounterLine_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline =
			CreateSpec(withPagination: true);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with
				{
					PaginationMode =
						AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible
				}));
	}

	[Theory]
	[InlineData("2h")]
	[InlineData("30m 2h")]
	[InlineData("2h 60m")]
	[InlineData("169h 0m")]
	public void ParseCalibrationPage_WithInvalidCountdown_FailsClosed(
		string renderedCountdown)
	{
		string[] lines = CreateLines();
		lines[15] = lines[15].Replace(
			"2h 30m",
			renderedCountdown,
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Fact]
	public void ParseCalibrationPage_WithUnknownAvailability_FailsClosed()
	{
		string[] lines = CreateLines();
		lines[19] = lines[19].Replace(
			"quota available",
			"quota unavailable",
			StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				CreateSpec()));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithTrailingSpaceOnExactLine_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		lines[1] = FixedLine(1, "fixed-01 ");

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithEmptyDynamicAnchor_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		AntigravityUsageR1SectionLinePattern capacity =
			lines[14].Alternatives[0];
		AntigravityUsageR1SectionToken[] tokens = capacity.Tokens.ToArray();
		tokens[2] = new AntigravityUsageR1SectionLiteralToken(string.Empty);
		lines[14] = lines[14] with
		{
			Alternatives = new[]
			{
				capacity with { Tokens = tokens }
			}
		};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithPolicyAlternativeMismatch_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionRule[] sections = baseline.Sections.ToArray();
		AntigravityUsageR1SectionWindowRule[] windows =
			sections[0].Windows.ToArray();
		windows[1] = windows[1] with
		{
			ResetPolicy = AntigravityUsageR1SectionResetPolicy.Countdown
		};
		sections[0] = sections[0] with { Windows = windows };

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Sections = sections }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithCountdownBeyondWindow_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		AntigravityUsageR1SectionLinePattern reset =
			lines[19].Alternatives[0];
		AntigravityUsageR1SectionToken[] tokens = reset.Tokens.ToArray();
		AntigravityUsageR1SectionCountdownToken countdown =
			Assert.IsType<AntigravityUsageR1SectionCountdownToken>(tokens[^1]);
		tokens[^1] = countdown with
		{
			Rule = countdown.Rule with { MaximumTotalMinutes = (5 * 60) + 1 }
		};
		lines[19] = lines[19] with
		{
			Alternatives = new[]
			{
				reset with { Tokens = tokens },
				lines[19].Alternatives[1]
			}
		};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithWindowRowsOutOfOrder_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();

		for (int offset = 0; offset < 3; offset++)
		{
			lines[13 + offset] = lines[13 + offset] with
			{
				RowIndex = 17 + offset
			};
			lines[17 + offset] = lines[17 + offset] with
			{
				RowIndex = 13 + offset
			};
		}

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithMeterMeaningMismatch_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		AntigravityUsageR1SectionLinePattern capacity =
			lines[14].Alternatives[0];
		AntigravityUsageR1SectionToken[] tokens = capacity.Tokens.ToArray();
		AntigravityUsageR1SectionProgressMeterToken meter =
			Assert.IsType<AntigravityUsageR1SectionProgressMeterToken>(tokens[1]);
		tokens[1] = meter with
		{
			Rule = meter.Rule with
			{
				Meaning = AntigravityUsageR1SectionPercentMeaning.Used
			}
		};
		lines[14] = lines[14] with
		{
			Alternatives = new[]
			{
				capacity with { Tokens = tokens }
			}
		};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithDuplicatePaginationField_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline =
			CreateSpec(withPagination: true);
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		AntigravityUsageR1SectionLinePattern pagination =
			lines[40].Alternatives[0];
		AntigravityUsageR1SectionToken[] tokens = pagination.Tokens.ToArray();
		tokens[3] = new AntigravityUsageR1SectionPaginationNumberToken(
			AntigravityUsageR1SectionPaginationField.Start);
		lines[40] = lines[40] with
		{
			Alternatives = new[]
			{
				pagination with { Tokens = tokens }
			}
		};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithHeadingSubstringOnly_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		lines[10] = OwnedFixedLine(
			10,
			AntigravityUsageR1SectionLineRole.SectionHeading,
			"gemini",
			null,
			"prefix-Gemini-suffix");

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithUnanchoredIdentity_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		lines[8] = new AntigravityUsageR1SectionLineRule(
			8,
			"line-08",
			AntigravityUsageR1SectionLineRole.Identity,
			null,
			null,
			new[]
			{
				Pattern(
					"account-unanchored",
					new AntigravityUsageR1SectionIdentityToken())
			});

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithUnanchoredOpaqueChrome_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		lines[0] = new AntigravityUsageR1SectionLineRule(
			0,
			"line-00",
			AntigravityUsageR1SectionLineRole.OuterChrome,
			null,
			null,
			new[]
			{
				new AntigravityUsageR1SectionLinePattern(
					"outer-unanchored",
					new AntigravityUsageR1SectionToken[]
					{
						new AntigravityUsageR1SectionOpaqueToken(100)
					})
			});

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				baseline with { Lines = lines }));
	}

	[Fact]
	public void ParseCalibrationPage_WithAmbiguousExactAlternative_FailsClosed()
	{
		AntigravityUsageR1SectionSpec baseline = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = baseline.Lines.ToArray();
		AntigravityUsageR1SectionLinePattern first = lines[1].Alternatives[0];
		lines[1] = lines[1] with
		{
			Alternatives = new[]
			{
				first,
				first with { PatternId = "line-01-duplicate" }
			}
		};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateLines()),
				baseline with { Lines = lines }));
	}

	[Fact]
	public void ParseCalibrationPage_WithTrailingUnsignedAmount_DiscardsInstance()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			UnsignedAmountLine(45, "credits=", 1, 9));
		string[] firstLines = CreateLines();
		firstLines[45] = "credits=123456789";
		string[] secondLines = CreateLines();
		secondLines[45] = "credits=987654321";
		AntigravityUsageR1SectionParseResult first =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(firstLines),
				spec);
		AntigravityUsageR1SectionParseResult second =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(secondLines),
				spec);

		Assert.Equal(
			first.Capture.PageFingerprint,
			second.Capture.PageFingerprint);
		Assert.Equal(
			AntigravityUsageR1SectionSchemaParser.ComputeStabilityFingerprint(
				first,
				StabilityKey),
			AntigravityUsageR1SectionSchemaParser.ComputeStabilityFingerprint(
				second,
				StabilityKey));
		Assert.DoesNotContain(
			"123456789",
			JsonSerializer.Serialize(first.Page),
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("")]
	[InlineData("１２３")]
	[InlineData("١٢٣")]
	[InlineData("1234")]
	public void ParseCalibrationPage_WithInvalidUnsignedAmount_FailsClosed(
		string renderedAmount)
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			UnsignedAmountLine(45, "credits=", 1, 3));
		string[] lines = CreateLines();
		lines[45] = $"credits={renderedAmount}";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithUnsignedAmountInOuterChrome_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionLineRule invalid =
			UnsignedAmountLine(45, "credits=", 1, 9) with
			{
				Role = AntigravityUsageR1SectionLineRole.OuterChrome
			};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				ReplaceLine(spec, invalid)));
	}

	[Fact]
	public void ParseCalibrationPage_WithOneSidedOuterLineShape_Succeeds()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"cwd=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.UnicodeVisibleText));

		string[] lines = CreateLines();
		lines[0] = "cwd=私有路徑";
		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec);

		Assert.Equal(4, result.Capture.WindowCount);
	}

	[Fact]
	public void FreezeAndValidateSpec_WithUnanchoredOuterLineShape_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				null,
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable));

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(spec));
	}

	[Fact]
	public void FreezeAndValidateSpec_WithOuterLineShapeInNonQuotaRole_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionLineRule invalid = OuterLineShapeLine(
			0,
			"cwd=",
			null,
			1,
			100,
			AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable) with
			{
				Role = AntigravityUsageR1SectionLineRole.NonQuotaDynamic
			};

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				ReplaceLine(spec, invalid)));
	}

	[Fact]
	public void ParseCalibrationPage_WithControlInOuterLineShape_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"cwd=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.UnicodeVisibleText));
		string[] lines = CreateLines();
		lines[0] = "cwd=private\u0001value";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec));
	}

	[Fact]
	public void ParseCalibrationPage_WithOuterLineShapeOverMaximum_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"cwd=",
				null,
				1,
				8,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable));
		string[] lines = CreateLines();
		lines[0] = "cwd=123456789";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec));
	}

	[Fact]
	public void ParseCalibrationPage_WithWrongOuterLineShapePrefix_FailsClosed()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"cwd=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable));
		string[] lines = CreateLines();
		lines[0] = "path=private-value";

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec));
	}

	[Fact]
	public void ComputeSchemaFingerprint_WhenDiscardedTokenRuleChanges_Changes()
	{
		AntigravityUsageR1SectionSpec first = ReplaceLine(
			CreateSpec(),
			UnsignedAmountLine(45, "credits=", 1, 9));
		AntigravityUsageR1SectionSpec second = ReplaceLine(
			CreateSpec(),
			UnsignedAmountLine(45, "credits=", 1, 10));

		Assert.NotEqual(
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(first)),
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(second)));

		AntigravityUsageR1SectionSpec firstOuter = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"cwd=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable));
		AntigravityUsageR1SectionSpec secondOuter = ReplaceLine(
			CreateSpec(),
			OuterLineShapeLine(
				0,
				"path=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable));

		Assert.NotEqual(
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(firstOuter)),
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(secondOuter)));
	}

	[Fact]
	public void TokenJson_WithDiscardedTokenTypes_UsesStableDiscriminators()
	{
		JsonSerializerOptions options = new()
		{
			Converters = { new JsonStringEnumConverter() }
		};
		AntigravityUsageR1SectionToken[] tokens =
		{
			new AntigravityUsageR1SectionUnsignedAmountToken(1, 9),
			new AntigravityUsageR1SectionOuterLineShapeToken(
				"cwd=",
				null,
				1,
				100,
				AntigravityUsageR1SectionOuterLineCharacterClass.AsciiPrintable)
		};

		string json = JsonSerializer.Serialize(tokens, options);
		AntigravityUsageR1SectionToken[]? roundTrip =
			JsonSerializer.Deserialize<AntigravityUsageR1SectionToken[]>(
				json,
				options);

		Assert.Contains("\"Kind\":\"UnsignedAmount\"", json, StringComparison.Ordinal);
		Assert.Contains("\"Kind\":\"OuterLineShape\"", json, StringComparison.Ordinal);
		Assert.IsType<AntigravityUsageR1SectionUnsignedAmountToken>(roundTrip![0]);
		Assert.IsType<AntigravityUsageR1SectionOuterLineShapeToken>(roundTrip[1]);
	}

	[Fact]
	public void SemanticCapture_WithDiscardedTokens_DoesNotExposeInstances()
	{
		AntigravityUsageR1SectionSpec spec = ReplaceLine(
			ReplaceLine(
				CreateSpec(),
				OuterLineShapeLine(
					0,
					"cwd=",
					null,
					1,
					100,
					AntigravityUsageR1SectionOuterLineCharacterClass.UnicodeVisibleText)),
			UnsignedAmountLine(45, "credits=", 1, 9));
		string[] lines = CreateLines();
		lines[0] = "cwd=private-shape-sentinel";
		lines[45] = "credits=987654321";
		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(lines),
				spec);
		string json = JsonSerializer.Serialize(result.Capture);

		Assert.DoesNotContain(
			"private-shape-sentinel",
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain("987654321", json, StringComparison.Ordinal);
	}

	[Fact]
	public void SemanticCapture_DoesNotExposePrivateOrDynamicInstances()
	{
		AntigravityUsageR1SectionParseResult result =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(CreateLines()),
				CreateSpec());
		string json = JsonSerializer.Serialize(result.Capture);

		Assert.DoesNotContain(FirstIdentity, json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("80.00", json, StringComparison.Ordinal);
		Assert.DoesNotContain("2h 30m", json, StringComparison.Ordinal);
		Assert.DoesNotContain("AccountIdentity", json, StringComparison.Ordinal);
		Assert.DoesNotContain("ResetsIn", json, StringComparison.Ordinal);
	}

	private static AntigravityUsageR1SectionSpec ReplaceLine(
		AntigravityUsageR1SectionSpec spec,
		AntigravityUsageR1SectionLineRule replacement)
	{
		AntigravityUsageR1SectionLineRule[] lines = spec.Lines.ToArray();
		lines[replacement.RowIndex] = replacement;
		return spec with { Lines = lines };
	}

	private static AntigravityUsageR1SectionLineRule UnsignedAmountLine(
		int row,
		string prefix,
		int minimumDigits,
		int maximumDigits)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.NonQuotaDynamic,
			null,
			null,
			new[]
			{
				Pattern(
					$"line-{row:00}-unsigned-amount",
					new AntigravityUsageR1SectionLiteralToken(prefix),
					new AntigravityUsageR1SectionUnsignedAmountToken(
						minimumDigits,
						maximumDigits))
			});
	}

	private static AntigravityUsageR1SectionLineRule OuterLineShapeLine(
		int row,
		string? publicPrefix,
		string? publicSuffix,
		int minimumDynamicLength,
		int maximumDynamicLength,
		AntigravityUsageR1SectionOuterLineCharacterClass allowedCharacters)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.OuterChrome,
			null,
			null,
			new[]
			{
				Pattern(
					$"line-{row:00}-outer-line-shape",
					new AntigravityUsageR1SectionOuterLineShapeToken(
						publicPrefix,
						publicSuffix,
						minimumDynamicLength,
						maximumDynamicLength,
						allowedCharacters))
			});
	}

	private static AntigravityUsageR1SectionLayout CreateLayout(
		AntigravityUsageR1SectionSpec spec,
		string pageFingerprint)
	{
		AntigravityUsageR1SectionSpec frozen =
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(spec);
		return new AntigravityUsageR1SectionLayoutFile(
			AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			frozen,
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(frozen),
			new[] { pageFingerprint },
			new string('A', 64),
			new string('B', 64))
			.ToLayout();
	}

	private static AntigravityUsageR1SectionSpec CreateSpec(
		bool withPagination = false)
	{
		AntigravityUsageR1SectionLineRule[] lines = Enumerable
			.Range(0, Rows)
			.Select(index => FixedLine(index, CreateLines()[index]))
			.ToArray();
		lines[0] = OpaqueLine(0, "cwd=", " | ready");
		lines[8] = IdentityLine(8);
		lines[10] = OwnedFixedLine(
			10,
			AntigravityUsageR1SectionLineRole.SectionHeading,
			"gemini",
			null,
			"Gemini");
		lines[13] = OwnedFixedLine(
			13,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"gemini",
			"gemini.weekly",
			"Weekly limit");
		lines[14] = CapacityLine(14, "gemini", "gemini.weekly");
		lines[15] = ResetLine(15, "gemini", "gemini.weekly");
		lines[17] = OwnedFixedLine(
			17,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"gemini",
			"gemini.rolling-5h",
			"5-hour limit");
		lines[18] = CapacityLine(18, "gemini", "gemini.rolling-5h");
		lines[19] = ResetLine(19, "gemini", "gemini.rolling-5h");
		lines[22] = OwnedFixedLine(
			22,
			AntigravityUsageR1SectionLineRole.SectionHeading,
			"claude",
			null,
			"Claude");
		lines[25] = OwnedFixedLine(
			25,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"claude",
			"claude.weekly",
			"Weekly limit");
		lines[26] = CapacityLine(26, "claude", "claude.weekly");
		lines[27] = ResetLine(27, "claude", "claude.weekly");
		lines[29] = OwnedFixedLine(
			29,
			AntigravityUsageR1SectionLineRole.WindowLabel,
			"claude",
			"claude.rolling-5h",
			"5-hour limit");
		lines[30] = CapacityLine(30, "claude", "claude.rolling-5h");
		lines[31] = ResetLine(31, "claude", "claude.rolling-5h");
		lines[45] = OpaqueLine(45, "credits=", " available");
		lines[49] = OpaqueLine(49, "model=", " | credits=ok");

		if (withPagination)
		{
			lines[40] = PaginationLine(40);
		}

		return new AntigravityUsageR1SectionSpec(
			2,
			"agy-section-synthetic-v2",
			Columns,
			Rows,
			false,
			new[]
			{
				Section("gemini", "Gemini"),
				Section("claude", "Claude") with { Order = 1 }
			},
			lines,
			AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible,
			withPagination
				? AntigravityUsageR1SectionPaginationMode.Counter
				: AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible);
	}

	private static AntigravityUsageR1SectionRule Section(
		string id,
		string rendered)
	{
		return new AntigravityUsageR1SectionRule(
			id,
			rendered,
			0,
			new[]
			{
				new AntigravityUsageR1SectionWindowRule(
					$"{id}.weekly",
					"Weekly limit",
					AntigravityUsageR1SectionWindowKind.Weekly,
					null,
					0,
					AntigravityUsageR1SectionCapacityPolicy.Percentage,
					AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability),
				new AntigravityUsageR1SectionWindowRule(
					$"{id}.rolling-5h",
					"5-hour limit",
					AntigravityUsageR1SectionWindowKind.RollingHours,
					5,
					1,
					AntigravityUsageR1SectionCapacityPolicy.Percentage,
					AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability)
			});
	}

	private static AntigravityUsageR1SectionLineRule FixedLine(
		int row,
		string literal)
	{
		return OwnedFixedLine(
			row,
			AntigravityUsageR1SectionLineRole.Fixed,
			null,
			null,
			literal);
	}

	private static AntigravityUsageR1SectionLineRule OwnedFixedLine(
		int row,
		AntigravityUsageR1SectionLineRole role,
		string? sectionId,
		string? windowId,
		string literal)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			role,
			sectionId,
			windowId,
			new[]
			{
				Pattern($"line-{row:00}-exact",
					new AntigravityUsageR1SectionLiteralToken(literal))
			});
	}

	private static AntigravityUsageR1SectionLineRule OpaqueLine(
		int row,
		string prefix,
		string suffix)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.OuterChrome,
			null,
			null,
			new[]
			{
				Pattern(
					$"line-{row:00}-opaque",
					new AntigravityUsageR1SectionLiteralToken(prefix),
					new AntigravityUsageR1SectionOpaqueToken(200),
					new AntigravityUsageR1SectionLiteralToken(suffix))
			});
	}

	private static AntigravityUsageR1SectionLineRule IdentityLine(int row)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.Identity,
			null,
			null,
			new[]
			{
				Pattern(
					"account-identity",
					new AntigravityUsageR1SectionLiteralToken("Account: "),
					new AntigravityUsageR1SectionIdentityToken(),
					new AntigravityUsageR1SectionLiteralToken(" │"))
			});
	}

	private static AntigravityUsageR1SectionLineRule CapacityLine(
		int row,
		string sectionId,
		string windowId)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.Capacity,
			sectionId,
			windowId,
			new[]
			{
				Pattern(
					$"{windowId}-meter",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionProgressMeterToken(MeterRule()),
					new AntigravityUsageR1SectionLiteralToken(" "),
					new AntigravityUsageR1SectionPercentToken(
						AntigravityUsageR1SectionPercentMeaning.Remaining,
						2))
			});
	}

	private static AntigravityUsageR1SectionLineRule ResetLine(
		int row,
		string sectionId,
		string windowId)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.Reset,
			sectionId,
			windowId,
			new[]
			{
				Pattern(
					$"{windowId}-countdown",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionPercentToken(
						AntigravityUsageR1SectionPercentMeaning.Remaining,
						0),
					new AntigravityUsageR1SectionLiteralToken(
						" remaining · refreshes in "),
					new AntigravityUsageR1SectionCountdownToken(
						CountdownRule(windowId.EndsWith(
							".weekly",
							StringComparison.Ordinal)
							? 168 * 60
							: 5 * 60))),
				Pattern(
					$"{windowId}-available",
					new AntigravityUsageR1SectionLiteralToken("  "),
					new AntigravityUsageR1SectionAvailabilityToken(
						new[]
						{
							new AntigravityUsageR1SectionAvailabilityRule(
								"quota_available",
								"quota available")
						}))
			});
	}

	private static AntigravityUsageR1SectionLineRule PaginationLine(int row)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row:00}",
			AntigravityUsageR1SectionLineRole.Pagination,
			null,
			null,
			new[]
			{
				Pattern(
					"pagination",
					new AntigravityUsageR1SectionLiteralToken("["),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.Start),
					new AntigravityUsageR1SectionLiteralToken("-"),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.End),
					new AntigravityUsageR1SectionLiteralToken(" of "),
					new AntigravityUsageR1SectionPaginationNumberToken(
						AntigravityUsageR1SectionPaginationField.Total),
					new AntigravityUsageR1SectionLiteralToken(" lines]"))
			});
	}

	private static AntigravityUsageR1SectionLinePattern Pattern(
		string id,
		params AntigravityUsageR1SectionToken[] tokens)
	{
		return new AntigravityUsageR1SectionLinePattern(id, tokens);
	}

	private static AntigravityUsageR1SectionProgressMeterRule MeterRule()
	{
		return new AntigravityUsageR1SectionProgressMeterRule(
			50,
			Filled,
			Empty,
			AntigravityUsageR1SectionPercentMeaning.Remaining,
			AntigravityUsageR1SectionMeterRounding.Nearest);
	}

	private static AntigravityUsageR1SectionCountdownRule CountdownRule(
		int maximumTotalMinutes)
	{
		return new AntigravityUsageR1SectionCountdownRule(
			" ",
			new[]
			{
				new AntigravityUsageR1SectionCountdownUnitRule(
					AntigravityUsageR1SectionCountdownUnit.Hours,
					"h",
					"h",
					0),
				new AntigravityUsageR1SectionCountdownUnitRule(
					AntigravityUsageR1SectionCountdownUnit.Minutes,
					"m",
					"m",
					1)
			},
			maximumTotalMinutes);
	}

	private static string[] CreateLines()
	{
		string[] lines = Enumerable
			.Range(0, Rows)
			.Select(index => $"fixed-{index:00}")
			.ToArray();
		int[] blankRows = { 2, 7, 9, 12, 16, 20, 21, 24, 28, 32, 33, 40, 43, 46, 47 };

		foreach (int row in blankRows)
		{
			lines[row] = string.Empty;
		}

		lines[0] = "cwd=C:\\private\\sentinel | ready";
		lines[8] = $"Account: {FirstIdentity} │";
		lines[10] = "Gemini";
		lines[13] = "Weekly limit";
		lines[14] = $"  {Meter(40)} 80.00%";
		lines[15] = "  80% remaining · refreshes in 2h 30m";
		lines[17] = "5-hour limit";
		lines[18] = $"  {Meter(50)} 100.00%";
		lines[19] = "  quota available";
		lines[22] = "Claude";
		lines[25] = "Weekly limit";
		lines[26] = $"  {Meter(50)} 100.00%";
		lines[27] = "  quota available";
		lines[29] = "5-hour limit";
		lines[30] = $"  {Meter(50)} 100.00%";
		lines[31] = "  quota available";
		lines[45] = "credits=123 private-units available";
		lines[49] = "model=private-model | credits=ok";
		return lines;
	}

	private static string Meter(int filledCells)
	{
		return string.Concat(
			string.Concat(Enumerable.Repeat(Filled, filledCells)),
			string.Concat(Enumerable.Repeat(Empty, 50 - filledCells)));
	}

	private static TerminalScreenSnapshot CreateSnapshot(
		IReadOnlyList<string> lines)
	{
		return new TerminalScreenSnapshot(
			Columns,
			Rows,
			isAlternateScreen: false,
			lines.ToArray(),
			cursorColumn: 0,
			cursorRow: 0,
			isCursorVisible: true,
			isBracketedPasteEnabled: true);
	}
}
