using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageScreenParserTests
{
	private const int ScreenRows = 30;
	private static readonly DateTimeOffset ResetTime =
		new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(80)]
	[InlineData(120)]
	[InlineData(160)]
	public void ParsePage_WithAllowlistedSyntheticLayout_ReturnsValidatedRows(int columns)
	{
		AntigravityUsageLayout layout = CreateLayout();
		TerminalScreenSnapshot snapshot = CreateSnapshot(
			CreateScreen(
				"single",
				"Synthetic.User@Example.Invalid",
				$"gemini.synthetic.flash | 0% | 100% | {ResetTime:O}",
				"gemini.synthetic.pro | 100% | 0% | -"),
			columns);

		AntigravityUsagePage page = AntigravityUsageScreenParser.ParsePage(
			snapshot,
			layout);

		Assert.True(page.IsAtTop);
		Assert.True(page.IsAtBottom);
		Assert.Equal("synthetic.user@example.invalid", page.AccountIdentity);
		Assert.Equal(2, page.RequiredModelIds.Count);
		Assert.Collection(
			page.Rows,
			row =>
			{
				Assert.Equal("gemini.synthetic.flash", row.StableModelId);
				Assert.Equal(0, row.UsedPercent);
				Assert.Equal(100, row.RemainingPercent);
				Assert.Equal(ResetTime, row.ResetsAt);
			},
			row =>
			{
				Assert.Equal("gemini.synthetic.pro", row.StableModelId);
				Assert.Equal(100, row.UsedPercent);
				Assert.Equal(0, row.RemainingPercent);
				Assert.Null(row.ResetsAt);
			});
	}

	[Fact]
	public void ParsePage_WithMissingRequiredModel_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		TerminalScreenSnapshot snapshot = CreateSnapshot(
			CreateScreen(
				"single",
				"synthetic@example.invalid",
				"gemini.synthetic.flash | 10% | 90% | -"),
			120);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(snapshot, layout));
	}

	[Fact]
	public void ParsePage_WithDuplicateModelId_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		TerminalScreenSnapshot snapshot = CreateSnapshot(
			CreateScreen(
				"single",
				"synthetic@example.invalid",
				"gemini.synthetic.flash | 10% | 90% | -",
				"gemini.synthetic.flash | 10% | 90% | -"),
			120);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(snapshot, layout));
	}

	[Theory]
	[InlineData("gemini.synthetic.flash | NaN% | 100% | -")]
	[InlineData("gemini.synthetic.flash | 10.5% | 89.5% | -")]
	[InlineData("gemini.synthetic.flash | 101% | 0% | -")]
	[InlineData("gemini.synthetic.flash | 25% | 80% | -")]
	[InlineData("gemini.synthetic.flash | 25% | 75% | tomorrow")]
	[InlineData("gemini.synthetic.flash | disabled | 100% | -")]
	public void ParsePage_WithInvalidQuotaField_FailsClosed(string invalidRow)
	{
		AntigravityUsageLayout layout = CreateLayout();
		TerminalScreenSnapshot snapshot = CreateSnapshot(
			CreateScreen(
				"single",
				"synthetic@example.invalid",
				invalidRow,
				"gemini.synthetic.pro | 20% | 80% | -"),
			120);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(snapshot, layout));
	}

	[Fact]
	public void ParsePage_WithUnknownHeaderOrModel_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		string[] unknownHeader = CreateScreen(
			"single",
			"synthetic@example.invalid",
			"gemini.synthetic.flash | 10% | 90% | -",
			"gemini.synthetic.pro | 20% | 80% | -");
		unknownHeader[4] = "Model | Used | Remaining | Reset";
		TerminalScreenSnapshot unknownModel = CreateSnapshot(
			CreateScreen(
				"single",
				"synthetic@example.invalid",
				"unknown.synthetic.model | 10% | 90% | -",
				"gemini.synthetic.pro | 20% | 80% | -"),
			120);

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(
				CreateSnapshot(unknownHeader, 120),
				layout));
		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(unknownModel, layout));
	}

	[Fact]
	public void ParsePage_WithUnknownViewportOrShiftedPanel_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		string[] screen = CreateScreen(
			"single",
			"synthetic@example.invalid",
			"gemini.synthetic.flash | 10% | 90% | -",
			"gemini.synthetic.pro | 20% | 80% | -");
		string[] shiftedScreen = new[] { "extra chrome" }.Concat(screen).ToArray();

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(
				CreateSnapshot(screen, 100),
				layout));
		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(
				CreateSnapshot(shiftedScreen, 120),
				layout));
	}

	[Theory]
	[InlineData("Account:  synthetic@example.invalid")]
	[InlineData("Page:  single")]
	[InlineData("gemini.synthetic.flash  | 10% | 90% | -")]
	[InlineData("gemini.synthetic.flash|10%|90%|-")]
	public void ParsePage_WithRenderedWhitespaceDrift_FailsClosed(string driftedLine)
	{
		AntigravityUsageLayout layout = CreateLayout();
		string[] screen = CreateScreen(
			"single",
			"synthetic@example.invalid",
			"gemini.synthetic.flash | 10% | 90% | -",
			"gemini.synthetic.pro | 20% | 80% | -");
		int lineIndex = driftedLine.StartsWith("Account", StringComparison.Ordinal)
			? 2
			: driftedLine.StartsWith("Page", StringComparison.Ordinal)
				? 3
				: 5;
		screen[lineIndex] = driftedLine;

		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageScreenParser.ParsePage(
				CreateSnapshot(screen, 120),
				layout));
	}

	[Fact]
	public void ParsePage_WithDynamicValues_PreservesStructuralFingerprint()
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage first = AntigravityUsageScreenParser.ParsePage(
			CreateSnapshot(
				CreateScreen(
					"single",
					"first@example.invalid",
					"gemini.synthetic.flash | 10% | 90% | -",
					"gemini.synthetic.pro | 20% | 80% | -"),
				120),
			layout);
		AntigravityUsagePage second = AntigravityUsageScreenParser.ParsePage(
			CreateSnapshot(
				CreateScreen(
					"single",
					"second@example.invalid",
					$"gemini.synthetic.flash | 90% | 10% | {ResetTime:O}",
					"gemini.synthetic.pro | 0% | 100% | -"),
				120),
			layout);

		Assert.Equal(first.SemanticFingerprint, second.SemanticFingerprint);
	}

	[Fact]
	public void Accumulator_WithTopAndBottomPages_ReturnsCompleteResult()
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage bottom = ParsePage(
			layout,
			"bottom",
			"gemini.synthetic.flash | 10% | 90% | -",
			"gemini.synthetic.pro | 20% | 80% | -");
		AntigravityUsagePageAccumulator accumulator = new();

		accumulator.AddPage(top);
		accumulator.AddPage(bottom);
		AntigravityUsageResult result = accumulator.Build();

		Assert.True(accumulator.IsComplete);
		Assert.Equal(2, result.Rows.Count);
	}

	[Fact]
	public void Accumulator_WithoutBottomOrProgress_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage repeatedBottom = top with
		{
			PageKind = AntigravityUsagePageKind.Bottom
		};
		AntigravityUsagePageAccumulator accumulator = new();
		accumulator.AddPage(top);

		Assert.Throws<InvalidOperationException>(() => accumulator.Build());
		Assert.Throws<InvalidDataException>(() => accumulator.AddPage(repeatedBottom));
	}

	[Fact]
	public void Accumulator_WhenFingerprintChanges_FailsClosed()
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage bottom = ParsePage(
			layout,
			"bottom",
			"gemini.synthetic.pro | 20% | 80% | -") with
		{
			SemanticFingerprint = new string('A', 64)
		};
		AntigravityUsagePageAccumulator accumulator = new();
		accumulator.AddPage(top);

		Assert.Throws<InvalidDataException>(() => accumulator.AddPage(bottom));
	}

	[Fact]
	public void Accumulator_WhenRejectedPageContainsEarlierNewRow_DoesNotPolluteState()
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage validBottom = ParsePage(
			layout,
			"bottom",
			"gemini.synthetic.pro | 20% | 80% | -");
		AntigravityUsagePage rejectedBottom = validBottom with
		{
			Rows = Array.AsReadOnly(new[]
			{
				validBottom.Rows[0],
				top.Rows[0] with
				{
					UsedPercent = 11,
					RemainingPercent = 89
				}
			})
		};
		AntigravityUsagePageAccumulator accumulator = new();
		accumulator.AddPage(top);

		Assert.Throws<InvalidDataException>(() =>
			accumulator.AddPage(rejectedBottom));
		accumulator.AddPage(validBottom);

		AntigravityUsageResult result = accumulator.Build();
		Assert.Equal(2, result.Rows.Count);
	}

	[Fact]
	public void Accumulator_WhenMultiPageResultMissesRequiredModel_FailsClosed()
	{
		AntigravityUsageLayout baseline = CreateLayout();
		HashSet<string> allowedModels = new(
			baseline.AllowedModelIds,
			StringComparer.Ordinal)
		{
			"gemini.synthetic.optional"
		};
		AntigravityUsageLayout layout = baseline with
		{
			AllowedModelIds = allowedModels
		};
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage bottom = ParsePage(
			layout,
			"bottom",
			"gemini.synthetic.optional | 20% | 80% | -");
		AntigravityUsagePageAccumulator accumulator = new();
		accumulator.AddPage(top);
		accumulator.AddPage(bottom);

		Assert.Throws<InvalidDataException>(() => accumulator.Build());
	}

	[Theory]
	[InlineData("layout")]
	[InlineData("account")]
	[InlineData("required-models")]
	public void Accumulator_WhenPageContractChanges_FailsClosed(string changedField)
	{
		AntigravityUsageLayout layout = CreateLayout();
		AntigravityUsagePage top = ParsePage(
			layout,
			"top",
			"gemini.synthetic.flash | 10% | 90% | -");
		AntigravityUsagePage bottom = ParsePage(
			layout,
			"bottom",
			"gemini.synthetic.pro | 20% | 80% | -");
		bottom = changedField switch
		{
			"layout" => bottom with { LayoutId = "different-layout" },
			"account" => bottom with { AccountIdentity = "other@example.invalid" },
			"required-models" => bottom with
			{
				RequiredModelIds = Array.AsReadOnly(new[]
				{
					"gemini.synthetic.flash"
				})
			},
			_ => throw new ArgumentOutOfRangeException(nameof(changedField))
		};
		AntigravityUsagePageAccumulator accumulator = new();
		accumulator.AddPage(top);

		Assert.Throws<InvalidDataException>(() => accumulator.AddPage(bottom));
	}

	[Fact]
	public void TerminalScreen_ToSemanticParser_ParsesSyntheticAlternateScreen()
	{
		const int columns = 120;
		string[] screen = CreateScreen(
			"single",
			"synthetic@example.invalid",
			"gemini.synthetic.flash | 10% | 90% | -",
			"gemini.synthetic.pro | 20% | 80% | -");
		string stream = "\u001b[?1049h\u001b[2J\u001b[H" + string.Join("\r\n", screen);
		TerminalScreenState terminal = new(columns, ScreenRows, 64 * 1024);
		terminal.Feed(Encoding.UTF8.GetBytes(stream));
		terminal.Complete();
		TerminalScreenSnapshot snapshot = terminal.Capture();
		AntigravityUsageLayout layout = CreateLayout(isAlternateScreen: true);

		AntigravityUsagePage page = AntigravityUsageScreenParser.ParsePage(
			snapshot,
			layout);

		Assert.True(snapshot.IsAlternateScreen);
		Assert.Equal(2, page.Rows.Count);
	}

	private static AntigravityUsagePage ParsePage(
		AntigravityUsageLayout layout,
		string page,
		params string[] rows)
	{
		return AntigravityUsageScreenParser.ParsePage(
			CreateSnapshot(
				CreateScreen(page, "synthetic@example.invalid", rows),
				120),
			layout);
	}

	private static AntigravityUsageLayout CreateLayout(
		bool isAlternateScreen = false)
	{
		HashSet<string> modelIds = new(StringComparer.Ordinal)
		{
			"gemini.synthetic.flash",
			"gemini.synthetic.pro"
		};
		AntigravityUsageLayout layout = new(
			"synthetic-r0-v1",
			"SYNTHETIC R0 AGY MODEL QUOTAS",
			"Account: ",
			"Page: ",
			"Model ID | Used | Remaining | Reset",
			"END SYNTHETIC MODEL QUOTAS",
			" | ",
			isAlternateScreen,
			modelIds,
			modelIds,
			new Dictionary<int, string>());
		Dictionary<int, string> fingerprints = isAlternateScreen
			? new Dictionary<int, string>
			{
				[80] = "FD5118F9864E99FB8B5E578DF51CB832DEEFB25F798B4A12C68E7E5553D13D53",
				[120] = "F0A90B896086E187E241011E617591202B61CE3BACC40140AA35BA4E08A96D19",
				[160] = "89E51CF5F657ACB17EF1DA01432B0FB56C6EF048168762B3DECCAE8817B013BE"
			}
			: new Dictionary<int, string>
			{
				[80] = "BDB52D9BDFA093EAF82FBEE14C1E4474B2B7D17F633F1B46337874A2B9D30734",
				[120] = "F72BA2AB717CEAEF33C97D2D334FAA9DCF897EAB0B58570A5301EE565FEDF710",
				[160] = "99FA84453C296BE8B3218E441A5D2915FB7E87ACEBAA12AC5C5BD61DD20F2E61"
			};

		return layout with
		{
			ExpectedSemanticFingerprints = fingerprints
		};
	}

	private static TerminalScreenSnapshot CreateSnapshot(
		IReadOnlyList<string> lines,
		int columns,
		bool isAlternateScreen = false)
	{
		if (lines.Count > ScreenRows)
		{
			throw new ArgumentOutOfRangeException(nameof(lines));
		}

		string[] paddedLines = lines
			.Concat(Enumerable.Repeat(string.Empty, ScreenRows - lines.Count))
			.ToArray();
		return new TerminalScreenSnapshot(
			columns,
			ScreenRows,
			isAlternateScreen,
			paddedLines);
	}

	private static string[] CreateScreen(
		string page,
		string identity,
		params string[] rows)
	{
		return new[]
		{
			"unrelated prompt chrome",
			"SYNTHETIC R0 AGY MODEL QUOTAS",
			$"Account: {identity}",
			$"Page: {page}",
			"Model ID | Used | Remaining | Reset"
		}
		.Concat(rows)
		.Concat(new[]
		{
			"END SYNTHETIC MODEL QUOTAS",
			"unrelated footer chrome"
		})
		.ToArray();
	}
}
