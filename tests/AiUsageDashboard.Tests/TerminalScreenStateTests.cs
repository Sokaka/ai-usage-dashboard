using System.IO;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class TerminalScreenStateTests
{
	private static readonly byte[] ExactFingerprintKey =
		Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

	[Fact]
	public void Feed_WithSplitUtf8CsiSgrAndOsc_MatchesSingleChunk()
	{
		const string stream =
			"\u001b]0;Quota │ Window\u0007" +
			"\u001b[2J\u001b[H" +
			"\u001b[31mModel │ 50%\u001b[0m" +
			"\u001b[2;1HReset │ 12:00";
		byte[] bytes = Encoding.UTF8.GetBytes(stream);
		TerminalScreenState whole = new(40, 4, 4096);
		TerminalScreenState split = new(40, 4, 4096);

		whole.Feed(bytes);
		whole.Complete();

		for (int index = 0; index < bytes.Length; index++)
		{
			split.Feed(bytes.AsSpan(index, 1));
		}

		split.Complete();

		TerminalScreenSnapshot wholeSnapshot = whole.Capture();
		TerminalScreenSnapshot splitSnapshot = split.Capture();
		Assert.Equal(
			wholeSnapshot.Lines.ToArray(),
			splitSnapshot.Lines.ToArray());
		Assert.Equal("Model │ 50%", splitSnapshot.Lines[0].TrimEnd());
		Assert.Equal("Reset │ 12:00", splitSnapshot.Lines[1].TrimEnd());
		Assert.Equal(bytes.Length, split.TotalInputBytes);
	}

	[Theory]
	[InlineData("\u001b[ q", 0)]
	[InlineData("\u001b[0 q", 0)]
	[InlineData("\u001b[1 q", 1)]
	[InlineData("\u001b[2 q", 2)]
	[InlineData("\u001b[3 q", 3)]
	[InlineData("\u001b[4 q", 4)]
	[InlineData("\u001b[5 q", 5)]
	[InlineData("\u001b[6 q", 6)]
	public void Feed_WithDocumentedDecscusr_TracksStateAndFingerprints(
		string sequence,
		int expectedCursorStyle)
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState changed = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		Feed(changed, "before" + sequence + "after");
		baseline.Complete();
		changed.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot changedSnapshot = changed.Capture();
		AntigravityRedactedStructuralCapture baselineCapture =
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot);
		AntigravityRedactedStructuralCapture changedCapture =
			AntigravityRedactedStructuralCapture.Create(changedSnapshot);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			changedSnapshot.Lines.ToArray());
		Assert.False(baselineSnapshot.HasObservedDecscusr);
		Assert.True(changedSnapshot.HasObservedDecscusr);
		Assert.Equal(expectedCursorStyle, changedSnapshot.CursorStyle);
		Assert.False(baselineCapture.HasObservedDecscusr);
		Assert.Null(baselineCapture.CursorStyle);
		Assert.True(changedCapture.HasObservedDecscusr);
		Assert.Equal(expectedCursorStyle, changedCapture.CursorStyle);
		Assert.NotEqual(
			baselineCapture.StructuralFingerprint,
			changedCapture.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				changedSnapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithSplitDecscusr_MatchesExactObservedSequence()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[");
		Feed(screen, "2");
		Feed(screen, " ");
		Feed(screen, "q");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.True(snapshot.HasObservedDecscusr);
		Assert.Equal(2, snapshot.CursorStyle);
	}

	[Fact]
	public void Fingerprints_WithDifferentDecscusrStyles_Differ()
	{
		TerminalScreenState blinkingBlock = new(20, 3, 128);
		TerminalScreenState steadyBar = new(20, 3, 128);

		Feed(blinkingBlock, "same\u001b[0 q");
		Feed(steadyBar, "same\u001b[6 q");
		blinkingBlock.Complete();
		steadyBar.Complete();

		TerminalScreenSnapshot blinkingSnapshot = blinkingBlock.Capture();
		TerminalScreenSnapshot steadySnapshot = steadyBar.Capture();
		Assert.NotEqual(
			AntigravityRedactedStructuralCapture.Create(blinkingSnapshot)
				.StructuralFingerprint,
			AntigravityRedactedStructuralCapture.Create(steadySnapshot)
				.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				blinkingSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				steadySnapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithDecscusrThenRis_ResetsStateAndPreservesProvenance()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[6 q");
		Assert.Equal(6, screen.Capture().CursorStyle);

		Feed(screen, "\u001bc");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		AntigravityRedactedStructuralCapture capture =
			AntigravityRedactedStructuralCapture.Create(snapshot);
		Assert.Equal(0, snapshot.CursorStyle);
		Assert.True(snapshot.HasObservedDecscusr);
		Assert.Equal(0, capture.CursorStyle);
		Assert.True(capture.HasObservedDecscusr);
		Assert.True(snapshot.HadTerminalReset);
	}

	[Fact]
	public void Fingerprints_WithoutDecscusr_PreserveExistingCanonicalValues()
	{
		TerminalScreenSnapshot snapshot = new(
			4,
			2,
			isAlternateScreen: false,
			new[] { "AB  ", "    " });
		AntigravityRedactedStructuralCapture capture =
			AntigravityRedactedStructuralCapture.Create(snapshot);

		Assert.False(snapshot.HasObservedDecscusr);
		Assert.Null(capture.CursorStyle);
		Assert.False(capture.HasObservedDecscusr);
		Assert.Equal(
			"C4F00F12DB270C31EDDB149D0BC3DC0AA10F27727C7A9959B047BC84505C96B5",
			capture.StructuralFingerprint);
		Assert.Equal(
			"C2516E09A0F7348F2463127A1064C3F061EEB5F9E1E623FDE061CF5494735998",
			AntigravityLocalExactScreenFingerprint.Compute(
				snapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithSplitModifyOtherKeysReset_MatchesBaseline()
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState withReset = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		baseline.Complete();

		Feed(withReset, "before\u001b[");
		Feed(withReset, ">");
		Feed(withReset, "4");
		Feed(withReset, "mafter");
		withReset.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot resetSnapshot = withReset.Capture();
		Assert.Equal(0, baselineSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(0, resetSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			resetSnapshot.Lines.ToArray());
		Assert.Equal(baselineSnapshot.CursorRow, resetSnapshot.CursorRow);
		Assert.Equal(baselineSnapshot.CursorColumn, resetSnapshot.CursorColumn);
		Assert.Equal(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				resetSnapshot,
				ExactFingerprintKey));
		Assert.Equal(
			baselineSnapshot.HadVisibleContentDiscarded,
			resetSnapshot.HadVisibleContentDiscarded);
		Assert.Equal(
			baselineSnapshot.HadTerminalReset,
			resetSnapshot.HadTerminalReset);
		Assert.Equal(
			baselineSnapshot.HasEverEnteredAlternateScreen,
			resetSnapshot.HasEverEnteredAlternateScreen);
		Assert.Equal(
			baselineSnapshot.HasEverEnabledBracketedPaste,
			resetSnapshot.HasEverEnabledBracketedPaste);
	}

	[Fact]
	public void Feed_WithSplitModifyOtherKeysLevelTwo_TracksFingerprintState()
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState levelTwo = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		baseline.Complete();

		Feed(levelTwo, "before\u001b[");
		Feed(levelTwo, ">4");
		Feed(levelTwo, ";");
		Feed(levelTwo, "2");
		Feed(levelTwo, "mafter");
		levelTwo.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot levelTwoSnapshot = levelTwo.Capture();
		AntigravityRedactedStructuralCapture baselineCapture =
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot);
		AntigravityRedactedStructuralCapture levelTwoCapture =
			AntigravityRedactedStructuralCapture.Create(levelTwoSnapshot);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			levelTwoSnapshot.Lines.ToArray());
		Assert.Equal(baselineSnapshot.CursorRow, levelTwoSnapshot.CursorRow);
		Assert.Equal(baselineSnapshot.CursorColumn, levelTwoSnapshot.CursorColumn);
		Assert.Equal(0, baselineSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(2, levelTwoSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(0, baselineCapture.ModifyOtherKeysLevel);
		Assert.Equal(2, levelTwoCapture.ModifyOtherKeysLevel);
		Assert.NotEqual(
			baselineCapture.StructuralFingerprint,
			levelTwoCapture.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				levelTwoSnapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithModifyOtherKeysLevelTwoThenReset_ReturnsToInitialLevel()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[>4;2m");
		Assert.Equal(2, screen.Capture().ModifyOtherKeysLevel);

		Feed(screen, "\u001b[>4m");
		screen.Complete();

		Assert.Equal(0, screen.Capture().ModifyOtherKeysLevel);
	}

	[Fact]
	public void Feed_WithStandardSgrFour_RemainsAccepted()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[4mvisible");
		screen.Complete();

		Assert.Equal("visible", screen.Capture().Lines[0].TrimEnd());
	}

	[Fact]
	public void Feed_WithSplitKittyKeyboardReset_MatchesBaseline()
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState withReset = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		baseline.Complete();

		Feed(withReset, "before\u001b[");
		Feed(withReset, "=");
		Feed(withReset, "0;");
		Feed(withReset, "1");
		Feed(withReset, "uafter");
		withReset.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot resetSnapshot = withReset.Capture();
		Assert.Equal(0, baselineSnapshot.KittyKeyboardFlags);
		Assert.Equal(0, resetSnapshot.KittyKeyboardFlags);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			resetSnapshot.Lines.ToArray());
		Assert.Equal(baselineSnapshot.CursorRow, resetSnapshot.CursorRow);
		Assert.Equal(baselineSnapshot.CursorColumn, resetSnapshot.CursorColumn);
		Assert.Equal(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				resetSnapshot,
				ExactFingerprintKey));
		Assert.Equal(
			baselineSnapshot.HadVisibleContentDiscarded,
			resetSnapshot.HadVisibleContentDiscarded);
		Assert.Equal(
			baselineSnapshot.HadTerminalReset,
			resetSnapshot.HadTerminalReset);
		Assert.Equal(
			baselineSnapshot.HasEverEnteredAlternateScreen,
			resetSnapshot.HasEverEnteredAlternateScreen);
		Assert.Equal(
			baselineSnapshot.HasEverEnabledBracketedPaste,
			resetSnapshot.HasEverEnabledBracketedPaste);
	}

	[Fact]
	public void Feed_WithSplitKittyKeyboardFlagsOne_TracksFingerprintState()
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState flagsOne = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		baseline.Complete();

		Feed(flagsOne, "before\u001b[");
		Feed(flagsOne, "=");
		Feed(flagsOne, "1;");
		Feed(flagsOne, "1");
		Feed(flagsOne, "uafter");
		flagsOne.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot flagsOneSnapshot = flagsOne.Capture();
		AntigravityRedactedStructuralCapture baselineCapture =
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot);
		AntigravityRedactedStructuralCapture flagsOneCapture =
			AntigravityRedactedStructuralCapture.Create(flagsOneSnapshot);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			flagsOneSnapshot.Lines.ToArray());
		Assert.Equal(baselineSnapshot.CursorRow, flagsOneSnapshot.CursorRow);
		Assert.Equal(baselineSnapshot.CursorColumn, flagsOneSnapshot.CursorColumn);
		Assert.Equal(0, baselineSnapshot.KittyKeyboardFlags);
		Assert.Equal(1, flagsOneSnapshot.KittyKeyboardFlags);
		Assert.Equal(0, baselineCapture.KittyKeyboardFlags);
		Assert.Equal(1, flagsOneCapture.KittyKeyboardFlags);
		Assert.NotEqual(
			baselineCapture.StructuralFingerprint,
			flagsOneCapture.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				flagsOneSnapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithKittyKeyboardFlagsOneThenReset_ReturnsToInitialFlags()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[=1;1u");
		Assert.Equal(1, screen.Capture().KittyKeyboardFlags);

		Feed(screen, "\u001b[=0;1u");
		screen.Complete();

		Assert.Equal(0, screen.Capture().KittyKeyboardFlags);
	}

	[Fact]
	public void Feed_WithSplitKittyKeyboardFlagsQuery_TracksProvenance()
	{
		TerminalScreenState baseline = new(20, 3, 256);
		TerminalScreenState queried = new(20, 3, 256);

		Feed(baseline, "beforeafter");
		baseline.Complete();

		Feed(queried, "before\u001b[");
		Feed(queried, "?");
		Feed(queried, "uafter");
		queried.Complete();

		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot queriedSnapshot = queried.Capture();
		AntigravityRedactedStructuralCapture baselineCapture =
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot);
		AntigravityRedactedStructuralCapture queriedCapture =
			AntigravityRedactedStructuralCapture.Create(queriedSnapshot);
		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			queriedSnapshot.Lines.ToArray());
		Assert.Equal(baselineSnapshot.CursorRow, queriedSnapshot.CursorRow);
		Assert.Equal(baselineSnapshot.CursorColumn, queriedSnapshot.CursorColumn);
		Assert.False(baselineSnapshot.HasObservedKittyKeyboardFlagsQuery);
		Assert.True(queriedSnapshot.HasObservedKittyKeyboardFlagsQuery);
		Assert.False(baselineCapture.HasObservedKittyKeyboardFlagsQuery);
		Assert.True(queriedCapture.HasObservedKittyKeyboardFlagsQuery);
		Assert.NotEqual(
			baselineCapture.StructuralFingerprint,
			queriedCapture.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				queriedSnapshot,
				ExactFingerprintKey));
	}

	[Fact]
	public void Feed_WithBareCsiU_RestoresCursor()
	{
		TerminalScreenState screen = new(20, 4, 128);

		Feed(screen, "\u001b[2;3H\u001b[s\u001b[4;8H\u001b[u");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal(1, snapshot.CursorRow);
		Assert.Equal(2, snapshot.CursorColumn);
	}

	[Fact]
	public void Feed_WithAlternateScreen_RestoresMainBufferAndCursor()
	{
		TerminalScreenState screen = new(20, 4, 4096);

		Feed(screen, "main\u001b[?1049h\u001b[Hquota");

		TerminalScreenSnapshot alternateSnapshot = screen.Capture();
		Assert.True(alternateSnapshot.IsAlternateScreen);
		Assert.Equal("quota", alternateSnapshot.Lines[0].TrimEnd());

		Feed(screen, "\u001b[?1049l!");
		screen.Complete();

		TerminalScreenSnapshot mainSnapshot = screen.Capture();
		Assert.False(mainSnapshot.IsAlternateScreen);
		Assert.Equal("main!", mainSnapshot.Lines[0].TrimEnd());
	}

	[Fact]
	public void Feed_WithCursorRewriteAndSaveRestore_UsesFinalCells()
	{
		TerminalScreenState screen = new(24, 4, 4096);

		Feed(
			screen,
			"Quota 10%\rQuota 42%" +
			"\u001b7\u001b[3;5H100%\u001b8!");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("Quota 42%!", snapshot.Lines[0].TrimEnd());
		Assert.Equal("    100%", snapshot.Lines[2].TrimEnd());
	}

	[Fact]
	public void Feed_WithEraseLineAndDisplay_ClearsRequestedCells()
	{
		TerminalScreenState screen = new(20, 3, 4096);

		Feed(screen, "Old quota\r\u001b[2KNew");

		TerminalScreenSnapshot lineSnapshot = screen.Capture();
		Assert.Equal("New", lineSnapshot.Lines[0].TrimEnd());
		Assert.DoesNotContain("Old", lineSnapshot.Lines[0], StringComparison.Ordinal);

		Feed(screen, "\u001b[2J");
		screen.Complete();

		Assert.All(
			screen.Capture().Lines,
			line => Assert.True(string.IsNullOrWhiteSpace(line)));
	}

	[Theory]
	[InlineData("\u001b[X")]
	[InlineData("\u001b[0X")]
	[InlineData("\u001b[01X")]
	public void Feed_WithDefaultEraseCharacters_ErasesOneAndPreservesCursor(
		string sequence)
	{
		TerminalScreenState screen = new(8, 2, 128);

		Feed(screen, "ABCDEFGH\u001b[1;3H" + sequence);
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("AB DEFGH", snapshot.Lines[0]);
		Assert.Equal(0, snapshot.CursorRow);
		Assert.Equal(2, snapshot.CursorColumn);
		Assert.True(snapshot.HadVisibleContentDiscarded);
	}

	[Fact]
	public void Feed_WithEraseCharactersAfterFullLine_CancelsPendingWrap()
	{
		TerminalScreenState screen = new(4, 2, 128);

		Feed(screen, "ABCD\u001b[XZ");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("ABCZ", snapshot.Lines[0]);
		Assert.True(string.IsNullOrWhiteSpace(snapshot.Lines[1]));
	}

	[Fact]
	public void Feed_WithEraseCharactersPastLineEnd_ClampsWithoutTouchingNextLine()
	{
		TerminalScreenState screen = new(8, 2, 128);

		Feed(screen, "ABCDEFGHIJKLMNOP\u001b[1;4H\u001b[39X");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("ABC     ", snapshot.Lines[0]);
		Assert.Equal("IJKLMNOP", snapshot.Lines[1]);
		Assert.Equal(0, snapshot.CursorRow);
		Assert.Equal(3, snapshot.CursorColumn);
	}

	[Theory]
	[InlineData("\u001b[1;2H\u001b[X")]
	[InlineData("\u001b[1;3H\u001b[X")]
	public void Feed_WithEraseCharactersIntersectingWideGlyph_ClearsWholeGlyph(
		string sequence)
	{
		TerminalScreenState screen = new(8, 2, 128);

		Feed(screen, "A\u6e2cBC" + sequence);
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("A  BC   ", snapshot.Lines[0]);
		Assert.True(snapshot.HadVisibleContentDiscarded);
	}

	[Fact]
	public void Feed_WithEraseCharactersEndingAtWideGlyph_ClearsContinuationBeyondRange()
	{
		TerminalScreenState screen = new(8, 2, 128);

		Feed(screen, "AB\u6e2cC\u001b[1;2H\u001b[2X");
		screen.Complete();

		Assert.Equal("A   C   ", screen.Capture().Lines[0]);
	}

	[Fact]
	public void Feed_WithSplitEraseCharacters_MatchesAtomicSequence()
	{
		TerminalScreenState screen = new(8, 2, 128);

		Feed(screen, "abcdef\u001b[1;2H\u001b[");
		Feed(screen, "3");
		Feed(screen, "X");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("a   ef  ", snapshot.Lines[0]);
		Assert.Equal(0, snapshot.CursorRow);
		Assert.Equal(1, snapshot.CursorColumn);
	}

	[Theory]
	[InlineData("\u001b[;X")]
	[InlineData("\u001b[1;X")]
	[InlineData("\u001b[1;2X")]
	[InlineData("\u001b[1:2X")]
	[InlineData("\u001b[?1X")]
	[InlineData("\u001b[>1X")]
	[InlineData("\u001b[1 X")]
	[InlineData("\u001b[1$X")]
	[InlineData("\u001b[-1X")]
	[InlineData("\u001b[1000001X")]
	public void Feed_WithMalformedEraseCharacters_FailsClosed(string sequence)
	{
		TerminalScreenState screen = new(8, 2, 128);

		Assert.Throws<InvalidDataException>(() => Feed(screen, sequence));
	}

	[Theory]
	[InlineData(80)]
	[InlineData(120)]
	[InlineData(160)]
	public void Feed_WithFixedViewport_WrapsAtConfiguredColumn(int columns)
	{
		TerminalScreenState screen = new(columns, 3, columns + 32);
		string firstLine = new('x', columns);

		Feed(screen, firstLine + "Y");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal(columns, snapshot.Columns);
		Assert.Equal(firstLine, snapshot.Lines[0]);
		Assert.Equal('Y', snapshot.Lines[1][0]);
		Assert.True(string.IsNullOrWhiteSpace(snapshot.Lines[1][1..]));
	}

	[Fact]
	public void Feed_WithDoubleWidthUtf8Rune_PreservesViewportColumns()
	{
		TerminalScreenState screen = new(4, 2, 128);

		Feed(screen, "A模B!");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal("A模B", snapshot.Lines[0]);
		Assert.Equal('!', snapshot.Lines[1][0]);
	}

	[Fact]
	public void Feed_WithInvalidUtf8_FailsClosed()
	{
		TerminalScreenState screen = new(20, 3, 128);

		screen.Feed(new byte[] { 0xE2 });

		Assert.Throws<DecoderFallbackException>(() =>
			screen.Feed(new byte[] { 0x28 }));
		Assert.Throws<InvalidOperationException>(() => screen.Capture());
	}

	[Fact]
	public void Complete_WithIncompleteUtf8_FailsClosed()
	{
		TerminalScreenState screen = new(20, 3, 128);

		screen.Feed(new byte[] { 0xE2, 0x94 });

		Assert.Throws<DecoderFallbackException>(() => screen.Complete());
	}

	[Fact]
	public void Capture_WithSplitControlSequence_WaitsForStableBoundary()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "old\u001b[");

		Assert.Throws<InvalidOperationException>(() => screen.Capture());

		Feed(screen, "2K\rnew");
		screen.Complete();

		Assert.Equal("new", screen.Capture().Lines[0].TrimEnd());
	}

	[Fact]
	public void Capture_WhileSynchronizedOutputIsOpen_WaitsForClosingMode()
	{
		TerminalScreenState screen = new(20, 3, 256);

		Feed(screen, "\u001b[?2026hpartial");

		Assert.Throws<InvalidOperationException>(() => screen.Capture());

		Feed(screen, " frame\u001b[?2026l");
		screen.Complete();

		Assert.Equal("partial frame", screen.Capture().Lines[0].TrimEnd());
	}

	[Fact]
	public void Capture_WithCursorAndPrivateModes_ExposesExactScreenState()
	{
		TerminalScreenState screen = new(20, 4, 256);

		Feed(
			screen,
			"same cells\u001b[3;5H\u001b[?25l\u001b[?1004h" +
			"\u001b[?2004h\u001b[?9001h");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.Equal(2, snapshot.CursorRow);
		Assert.Equal(4, snapshot.CursorColumn);
		Assert.False(snapshot.IsCursorVisible);
		Assert.True(snapshot.IsBracketedPasteEnabled);
		Assert.True(snapshot.IsFocusReportingEnabled);
		Assert.True(snapshot.IsWin32InputModeEnabled);
	}

	[Fact]
	public void Feed_WithFocusAndWin32Modes_ResetClearsFinalStates()
	{
		TerminalScreenState screen = new(20, 4, 256);

		Feed(
			screen,
			"content\u001b[?1004h\u001b[?9001h\u001b[>4;2m" +
			"\u001b[=1;1u\u001b[?u");
		TerminalScreenSnapshot enabledSnapshot = screen.Capture();
		Assert.True(enabledSnapshot.IsFocusReportingEnabled);
		Assert.True(enabledSnapshot.IsWin32InputModeEnabled);
		Assert.Equal(2, enabledSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(1, enabledSnapshot.KittyKeyboardFlags);
		Assert.True(enabledSnapshot.HasObservedKittyKeyboardFlagsQuery);

		Feed(screen, "\u001bc");
		screen.Complete();

		TerminalScreenSnapshot resetSnapshot = screen.Capture();
		Assert.False(resetSnapshot.IsFocusReportingEnabled);
		Assert.False(resetSnapshot.IsWin32InputModeEnabled);
		Assert.Equal(0, resetSnapshot.ModifyOtherKeysLevel);
		Assert.Equal(0, resetSnapshot.KittyKeyboardFlags);
		Assert.True(resetSnapshot.HasObservedKittyKeyboardFlagsQuery);
		Assert.True(resetSnapshot.HadTerminalReset);
		Assert.True(resetSnapshot.HadVisibleContentDiscarded);
	}

	[Fact]
	public void Feed_WithParameterlessPrimaryDeviceAttributesQuery_IsAccepted()
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, "\u001b[cfixture-1.2.3");
		screen.Complete();

		Assert.Equal("fixture-1.2.3", screen.Capture().Lines[0].TrimEnd());
	}

	[Theory]
	[InlineData("\u001b[0c")]
	[InlineData("\u001b[1c")]
	[InlineData("\u001b[?1c")]
	public void Feed_WithParameterizedPrimaryDeviceAttributesQuery_FailsClosed(
		string stream)
	{
		TerminalScreenState screen = new(20, 3, 128);

		Assert.Throws<InvalidDataException>(() => Feed(screen, stream));
	}

	[Fact]
	public void Feed_WithBlankEraseAndIdenticalWideGlyphRewrite_DoesNotReportDiscard()
	{
		TerminalScreenState screen = new(20, 3, 256);

		Feed(screen, "\u001b[2J\u6e2c\r\u6e2c");
		screen.Complete();

		TerminalScreenSnapshot snapshot = screen.Capture();
		Assert.False(snapshot.HadVisibleContentDiscarded);
		Assert.Equal("\u6e2c", snapshot.Lines[0].TrimEnd());
	}

	[Fact]
	public void ExactFingerprint_WithDifferentCursorPosition_DiffersWhileCoarseShapeMatches()
	{
		TerminalScreenState first = new(20, 4, 256);
		TerminalScreenState second = new(20, 4, 256);
		Feed(first, "same cells\u001b[1;1H");
		Feed(second, "same cells\u001b[3;5H");
		first.Complete();
		second.Complete();
		TerminalScreenSnapshot firstSnapshot = first.Capture();
		TerminalScreenSnapshot secondSnapshot = second.Capture();

		Assert.Equal(
			firstSnapshot.Lines.ToArray(),
			secondSnapshot.Lines.ToArray());
		Assert.Equal(
			AntigravityRedactedStructuralCapture.Create(firstSnapshot)
				.StructuralFingerprint,
			AntigravityRedactedStructuralCapture.Create(secondSnapshot)
				.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				firstSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				secondSnapshot,
				ExactFingerprintKey));
	}

	[Theory]
	[InlineData("\u001b[?25l")]
	public void ExactFingerprint_WithDifferentPrivateMode_DiffersWhileCoarseShapeMatches(
		string privateModeSequence)
	{
		TerminalScreenState baseline = new(20, 4, 256);
		TerminalScreenState changed = new(20, 4, 256);
		Feed(baseline, "same cells\u001b[1;1H");
		Feed(changed, "same cells\u001b[1;1H" + privateModeSequence);
		baseline.Complete();
		changed.Complete();
		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot changedSnapshot = changed.Capture();

		Assert.Equal(
			baselineSnapshot.Lines.ToArray(),
			changedSnapshot.Lines.ToArray());
		Assert.Equal(
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot)
				.StructuralFingerprint,
			AntigravityRedactedStructuralCapture.Create(changedSnapshot)
				.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				changedSnapshot,
				ExactFingerprintKey));
	}

	[Theory]
	[InlineData("\u001b[?1004h")]
	[InlineData("\u001b[?2004h")]
	[InlineData("\u001b[?9001h")]
	public void Fingerprints_WithDifferentInputMode_Differ(
		string privateModeSequence)
	{
		TerminalScreenState baseline = new(20, 4, 256);
		TerminalScreenState changed = new(20, 4, 256);
		Feed(baseline, "same cells\u001b[1;1H");
		Feed(changed, "same cells\u001b[1;1H" + privateModeSequence);
		baseline.Complete();
		changed.Complete();
		TerminalScreenSnapshot baselineSnapshot = baseline.Capture();
		TerminalScreenSnapshot changedSnapshot = changed.Capture();

		Assert.NotEqual(
			AntigravityRedactedStructuralCapture.Create(baselineSnapshot)
				.StructuralFingerprint,
			AntigravityRedactedStructuralCapture.Create(changedSnapshot)
				.StructuralFingerprint);
		Assert.NotEqual(
			AntigravityLocalExactScreenFingerprint.Compute(
				baselineSnapshot,
				ExactFingerprintKey),
			AntigravityLocalExactScreenFingerprint.Compute(
				changedSnapshot,
				ExactFingerprintKey));
	}

	[Theory]
	[InlineData("\u001b[")]
	[InlineData("\u001b]0;unfinished")]
	public void Complete_WithIncompleteControlSequence_FailsClosed(string stream)
	{
		TerminalScreenState screen = new(20, 3, 128);

		Feed(screen, stream);

		Assert.Throws<InvalidDataException>(() => screen.Complete());
	}

	[Theory]
	[InlineData("\u001bPpayload")]
	[InlineData("\u001b]52;clipboard\u0007")]
	[InlineData("\u001b[?9999h")]
	[InlineData("\u001b[999999999999A")]
	[InlineData("\u001b[>4;0m")]
	[InlineData("\u001b[>4;1m")]
	[InlineData("\u001b[>4;3m")]
	[InlineData("\u001b[>04;2m")]
	[InlineData("\u001b[>4;02m")]
	[InlineData("\u001b[>4;2;m")]
	[InlineData("\u001b[>4:2m")]
	[InlineData("\u001b[?4;2m")]
	[InlineData("\u001b[>0m")]
	[InlineData("\u001b[>04m")]
	[InlineData("\u001b[>4;m")]
	[InlineData("\u001b[>4:0m")]
	[InlineData("\u001b[?4m")]
	[InlineData("\u001b[=0u")]
	[InlineData("\u001b[=0;01u")]
	[InlineData("\u001b[=00;1u")]
	[InlineData("\u001b[=0;2u")]
	[InlineData("\u001b[=0;1;u")]
	[InlineData("\u001b[=0:1u")]
	[InlineData("\u001b[>0;1u")]
	[InlineData("\u001b[?0;1u")]
	[InlineData("\u001b[=1u")]
	[InlineData("\u001b[=01;1u")]
	[InlineData("\u001b[=1;01u")]
	[InlineData("\u001b[=1;2u")]
	[InlineData("\u001b[=1;3u")]
	[InlineData("\u001b[=2;1u")]
	[InlineData("\u001b[=1;1;u")]
	[InlineData("\u001b[=1:1u")]
	[InlineData("\u001b[?1;1u")]
	[InlineData("\u001b[>1;1u")]
	[InlineData("\u001b[?0u")]
	[InlineData("\u001b[?1u")]
	[InlineData("\u001b[??u")]
	[InlineData("\u001b[?;u")]
	[InlineData("\u001b[? u")]
	[InlineData("\u001b[?>u")]
	[InlineData("\u001b[q")]
	[InlineData("\u001b[1q")]
	[InlineData("\u001b[\"q")]
	[InlineData("\u001b[0\"q")]
	[InlineData("\u001b[7 q")]
	[InlineData("\u001b[01 q")]
	[InlineData("\u001b[1;2 q")]
	[InlineData("\u001b[1  q")]
	[InlineData("\u001b[?1 q")]
	[InlineData("\u001b[>1 q")]
	[InlineData("\u001b[-1 q")]
	public void Feed_WithUnsupportedControlSequence_FailsClosed(string stream)
	{
		TerminalScreenState screen = new(20, 3, 4096);

		Assert.Throws<InvalidDataException>(() => Feed(screen, stream));
	}

	[Fact]
	public void Feed_WhenErasedOutputExceedsCumulativeLimit_FailsClosed()
	{
		const string erasedOutput = "12345\r\u001b[2K";
		int maximumBytes = Encoding.UTF8.GetByteCount(erasedOutput);
		TerminalScreenState screen = new(20, 3, maximumBytes);

		Feed(screen, erasedOutput);

		Assert.True(string.IsNullOrWhiteSpace(screen.Capture().Lines[0]));
		Assert.Equal(maximumBytes, screen.TotalInputBytes);
		Assert.Throws<InvalidDataException>(() => Feed(screen, "x"));
	}

	private static void Feed(TerminalScreenState screen, string value)
	{
		screen.Feed(Encoding.UTF8.GetBytes(value));
	}
}
