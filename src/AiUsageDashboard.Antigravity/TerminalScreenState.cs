using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed class TerminalScreenSnapshot
{
	internal int Columns { get; }

	internal int CursorColumn { get; }

	internal int CursorRow { get; }

	internal int CursorStyle { get; }

	internal bool HadTerminalReset { get; }

	internal bool HadVisibleContentDiscarded { get; }

	internal bool HasEverEnabledBracketedPaste { get; }

	internal bool HasEverEnteredAlternateScreen { get; }

	internal bool HasObservedDecscusr { get; }

	internal bool HasObservedKittyKeyboardFlagsQuery { get; }

	internal bool IsAlternateScreen { get; }

	internal bool IsBracketedPasteEnabled { get; }

	internal bool IsCursorVisible { get; }

	internal bool IsFocusReportingEnabled { get; }

	internal bool IsWin32InputModeEnabled { get; }

	internal int KittyKeyboardFlags { get; }

	internal IReadOnlyList<string> Lines { get; }

	internal int ModifyOtherKeysLevel { get; }

	internal int Rows { get; }

	internal TerminalScreenSnapshot(
		int columns,
		int rows,
		bool isAlternateScreen,
		string[] lines)
		: this(
			columns,
			rows,
			isAlternateScreen,
			lines,
			0,
			0,
			true,
			false)
	{
	}

	internal TerminalScreenSnapshot(
		int columns,
		int rows,
		bool isAlternateScreen,
		string[] lines,
		int cursorColumn,
		int cursorRow,
		bool isCursorVisible,
		bool isBracketedPasteEnabled,
		bool isFocusReportingEnabled = false,
		bool isWin32InputModeEnabled = false,
		bool hadVisibleContentDiscarded = false,
		bool hadTerminalReset = false,
		bool hasEverEnteredAlternateScreen = false,
		bool hasEverEnabledBracketedPaste = false,
		int modifyOtherKeysLevel = 0,
		int kittyKeyboardFlags = 0,
		bool hasObservedKittyKeyboardFlagsQuery = false,
		int cursorStyle = 0,
		bool hasObservedDecscusr = false)
	{
		if ((modifyOtherKeysLevel != 0) &&
			(modifyOtherKeysLevel != 2))
		{
			throw new ArgumentOutOfRangeException(nameof(modifyOtherKeysLevel));
		}

		if ((kittyKeyboardFlags != 0) &&
			(kittyKeyboardFlags != 1))
		{
			throw new ArgumentOutOfRangeException(nameof(kittyKeyboardFlags));
		}

		if ((cursorStyle < 0) || (cursorStyle > 6))
		{
			throw new ArgumentOutOfRangeException(nameof(cursorStyle));
		}

		if (!hasObservedDecscusr && (cursorStyle != 0))
		{
			throw new ArgumentException(
				"A non-default cursor style requires observed DECSCUSR provenance.",
				nameof(cursorStyle));
		}

		Columns = columns;
		Rows = rows;
		IsAlternateScreen = isAlternateScreen;
		Lines = new ReadOnlyCollection<string>(lines);
		CursorColumn = cursorColumn;
		CursorRow = cursorRow;
		CursorStyle = cursorStyle;
		IsCursorVisible = isCursorVisible;
		IsBracketedPasteEnabled = isBracketedPasteEnabled;
		IsFocusReportingEnabled = isFocusReportingEnabled;
		IsWin32InputModeEnabled = isWin32InputModeEnabled;
		HadVisibleContentDiscarded = hadVisibleContentDiscarded;
		HadTerminalReset = hadTerminalReset;
		HasEverEnteredAlternateScreen = hasEverEnteredAlternateScreen;
		HasEverEnabledBracketedPaste = hasEverEnabledBracketedPaste;
		HasObservedDecscusr = hasObservedDecscusr;
		ModifyOtherKeysLevel = modifyOtherKeysLevel;
		KittyKeyboardFlags = kittyKeyboardFlags;
		HasObservedKittyKeyboardFlagsQuery =
			hasObservedKittyKeyboardFlagsQuery;
	}
}

internal sealed class TerminalScreenState
{
	private enum ParserState
	{
		Ground,
		Escape,
		Csi,
		Osc,
		OscEscape
	}

	private sealed class ScreenBuffer
	{
		private readonly string?[,] _cells;
		private readonly int _columns;
		private readonly bool[,] _continuations;
		private readonly Action _onVisibleContentDiscarded;
		private readonly int _rows;
		private int _cursorColumn;
		private int _cursorRow;
		private int _savedCursorColumn;
		private int _savedCursorRow;
		private bool _wrapPending;

		internal int CursorColumn => _cursorColumn;

		internal int CursorRow => _cursorRow;

		internal ScreenBuffer(
			int columns,
			int rows,
			Action onVisibleContentDiscarded)
		{
			_columns = columns;
			_rows = rows;
			_onVisibleContentDiscarded = onVisibleContentDiscarded;
			_cells = new string?[rows, columns];
			_continuations = new bool[rows, columns];
		}

		internal bool HasVisibleContent
		{
			get
			{
				for (int row = 0; row < _rows; row++)
				{
					if (RowHasVisibleContent(row))
					{
						return true;
					}
				}

				return false;
			}
		}

		internal void Backspace()
		{
			_cursorColumn = Math.Max(0, _cursorColumn - 1);
			_wrapPending = false;
		}

		internal void CarriageReturn()
		{
			_cursorColumn = 0;
			_wrapPending = false;
		}

		internal string[] CaptureLines()
		{
			string[] lines = new string[_rows];

			for (int row = 0; row < _rows; row++)
			{
				StringBuilder line = new(_columns);

				for (int column = 0; column < _columns; column++)
				{
					if (_continuations[row, column])
					{
						continue;
					}

					line.Append(_cells[row, column] ?? " ");
				}

				lines[row] = line.ToString();
			}

			return lines;
		}

		internal void Clear(bool reportVisibleContentDiscarded)
		{
			if (reportVisibleContentDiscarded && HasVisibleContent)
			{
				_onVisibleContentDiscarded();
			}

			Array.Clear(_cells, 0, _cells.Length);
			Array.Clear(_continuations, 0, _continuations.Length);
			_cursorColumn = 0;
			_cursorRow = 0;
			_savedCursorColumn = 0;
			_savedCursorRow = 0;
			_wrapPending = false;
		}

		internal void CursorDown(int count)
		{
			_cursorRow = Math.Min(_rows - 1, _cursorRow + count);
			_wrapPending = false;
		}

		internal void CursorLeft(int count)
		{
			_cursorColumn = Math.Max(0, _cursorColumn - count);
			_wrapPending = false;
		}

		internal void CursorNextLine(int count)
		{
			CursorDown(count);
			_cursorColumn = 0;
		}

		internal void CursorPreviousLine(int count)
		{
			CursorUp(count);
			_cursorColumn = 0;
		}

		internal void CursorRight(int count)
		{
			_cursorColumn = Math.Min(_columns - 1, _cursorColumn + count);
			_wrapPending = false;
		}

		internal void CursorUp(int count)
		{
			_cursorRow = Math.Max(0, _cursorRow - count);
			_wrapPending = false;
		}

		internal void EraseDisplay(int mode)
		{
			switch (mode)
			{
				case 0:
					EraseRange(
						(_cursorRow * _columns) + _cursorColumn,
						(_rows * _columns) - 1);
					break;
				case 1:
					EraseRange(
						0,
						(_cursorRow * _columns) + _cursorColumn);
					break;
				case 2:
				case 3:
					EraseRange(0, (_rows * _columns) - 1);
					break;
				default:
					throw new InvalidDataException(
						$"Unsupported erase-display mode: {mode}.");
			}

			_wrapPending = false;
		}

		internal void EraseCharacters(int count)
		{
			if (count <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(count));
			}

			int startIndex = (_cursorRow * _columns) + _cursorColumn;
			int lineEndIndex = ((_cursorRow + 1) * _columns) - 1;
			int endIndex = Math.Min(lineEndIndex, startIndex + count - 1);
			EraseRange(startIndex, endIndex);
			_wrapPending = false;
		}

		internal void EraseLine(int mode)
		{
			switch (mode)
			{
				case 0:
					EraseRange(
						(_cursorRow * _columns) + _cursorColumn,
						((_cursorRow + 1) * _columns) - 1);
					break;
				case 1:
					EraseRange(
						_cursorRow * _columns,
						(_cursorRow * _columns) + _cursorColumn);
					break;
				case 2:
					EraseRange(
						_cursorRow * _columns,
						((_cursorRow + 1) * _columns) - 1);
					break;
				default:
					throw new InvalidDataException(
						$"Unsupported erase-line mode: {mode}.");
			}

			_wrapPending = false;
		}

		internal void HorizontalTab()
		{
			int nextTabStop = ((_cursorColumn / 8) + 1) * 8;
			_cursorColumn = Math.Min(_columns - 1, nextTabStop);
			_wrapPending = false;
		}

		internal void LineFeed()
		{
			_wrapPending = false;

			if (_cursorRow == (_rows - 1))
			{
				ScrollUp();
				return;
			}

			_cursorRow++;
		}

		internal void RestoreCursor()
		{
			SetCursor(_savedCursorRow, _savedCursorColumn);
		}

		internal void ReverseIndex()
		{
			_wrapPending = false;

			if (_cursorRow == 0)
			{
				ScrollDown();
				return;
			}

			_cursorRow--;
		}

		internal void SaveCursor()
		{
			_savedCursorColumn = _cursorColumn;
			_savedCursorRow = _cursorRow;
		}

		internal void SetColumn(int column)
		{
			_cursorColumn = Math.Clamp(column, 0, _columns - 1);
			_wrapPending = false;
		}

		internal void SetCursor(int row, int column)
		{
			_cursorRow = Math.Clamp(row, 0, _rows - 1);
			_cursorColumn = Math.Clamp(column, 0, _columns - 1);
			_wrapPending = false;
		}

		internal void SetRow(int row)
		{
			_cursorRow = Math.Clamp(row, 0, _rows - 1);
			_wrapPending = false;
		}

		internal void WriteRune(Rune rune, int width)
		{
			if ((width < 1) || (width > 2))
			{
				throw new ArgumentOutOfRangeException(nameof(width));
			}

			if ((width == 2) && (_columns < 2))
			{
				throw new InvalidDataException(
					"The terminal viewport cannot display a double-width rune.");
			}

			if (_wrapPending ||
				((width == 2) && (_cursorColumn == (_columns - 1))))
			{
				WrapToNextLine();
			}

			if (IsDifferentGlyphOverwrite(rune, width))
			{
				_onVisibleContentDiscarded();
			}

			ClearGlyphAt(_cursorRow, _cursorColumn);

			if (width == 2)
			{
				ClearGlyphAt(_cursorRow, _cursorColumn + 1);
			}

			_cells[_cursorRow, _cursorColumn] = rune.ToString();

			if (width == 2)
			{
				_continuations[_cursorRow, _cursorColumn + 1] = true;
			}

			int endingColumn = _cursorColumn + width - 1;

			if (endingColumn == (_columns - 1))
			{
				_cursorColumn = endingColumn;
				_wrapPending = true;
				return;
			}

			_cursorColumn += width;
		}

		private void ClearGlyphAt(int row, int column)
		{
			if (_continuations[row, column])
			{
				_continuations[row, column] = false;
				_cells[row, column] = null;

				if (column > 0)
				{
					_cells[row, column - 1] = null;
				}

				return;
			}

			_cells[row, column] = null;

			if (((column + 1) < _columns) &&
				_continuations[row, column + 1])
			{
				_continuations[row, column + 1] = false;
				_cells[row, column + 1] = null;
			}
		}

		private void EraseRange(int startIndex, int endIndex)
		{
			bool hasVisibleGlyph = false;

			for (int index = startIndex; index <= endIndex; index++)
			{
				int row = index / _columns;
				int column = index % _columns;

				if (((_cells[row, column] != null) &&
					!string.Equals(
						_cells[row, column],
						" ",
						StringComparison.Ordinal)) ||
					_continuations[row, column])
				{
					hasVisibleGlyph = true;
					break;
				}
			}

			if (hasVisibleGlyph)
			{
				_onVisibleContentDiscarded();
			}

			for (int index = startIndex; index <= endIndex; index++)
			{
				ClearGlyphAt(index / _columns, index % _columns);
			}
		}

		private void ScrollDown()
		{
			if (RowHasVisibleContent(_rows - 1))
			{
				_onVisibleContentDiscarded();
			}

			for (int row = _rows - 1; row > 0; row--)
			{
				CopyRow(row - 1, row);
			}

			ClearRow(0);
		}

		private void ScrollUp()
		{
			if (RowHasVisibleContent(0))
			{
				_onVisibleContentDiscarded();
			}

			for (int row = 0; row < (_rows - 1); row++)
			{
				CopyRow(row + 1, row);
			}

			ClearRow(_rows - 1);
		}

		private void ClearRow(int row)
		{
			for (int column = 0; column < _columns; column++)
			{
				_cells[row, column] = null;
				_continuations[row, column] = false;
			}
		}

		private void CopyRow(int sourceRow, int destinationRow)
		{
			for (int column = 0; column < _columns; column++)
			{
				_cells[destinationRow, column] = _cells[sourceRow, column];
				_continuations[destinationRow, column] =
					_continuations[sourceRow, column];
			}
		}

		private bool IsDifferentGlyphOverwrite(Rune rune, int width)
		{
			int existingStartColumn = -1;
			int existingWidth = 0;
			string? existingText = null;
			bool hasMultipleGlyphs = false;

			for (int column = _cursorColumn;
				column < (_cursorColumn + width);
				column++)
			{
				if (!TryGetGlyph(
					_cursorRow,
					column,
					out int startColumn,
					out string? text,
					out int glyphWidth))
				{
					continue;
				}

				if (string.Equals(text, " ", StringComparison.Ordinal))
				{
					continue;
				}

				if (existingStartColumn < 0)
				{
					existingStartColumn = startColumn;
					existingText = text;
					existingWidth = glyphWidth;
					continue;
				}

				if (startColumn != existingStartColumn)
				{
					hasMultipleGlyphs = true;
				}
			}

			if (existingStartColumn < 0)
			{
				return false;
			}

			return hasMultipleGlyphs ||
				(existingStartColumn != _cursorColumn) ||
				(existingWidth != width) ||
				!string.Equals(
					existingText,
					rune.ToString(),
					StringComparison.Ordinal);
		}

		private bool RowHasVisibleContent(int row)
		{
			for (int column = 0; column < _columns; column++)
			{
				if ((_cells[row, column] != null) &&
					!string.Equals(
						_cells[row, column],
						" ",
						StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		private bool TryGetGlyph(
			int row,
			int column,
			out int startColumn,
			out string? text,
			out int width)
		{
			if (_continuations[row, column])
			{
				startColumn = column - 1;
				text = _cells[row, startColumn];
				width = 2;
				return text != null;
			}

			text = _cells[row, column];

			if (text == null)
			{
				startColumn = -1;
				width = 0;
				return false;
			}

			startColumn = column;
			width = (((column + 1) < _columns) &&
				_continuations[row, column + 1])
				? 2
				: 1;
			return true;
		}

		private void WrapToNextLine()
		{
			_cursorColumn = 0;
			LineFeed();
		}
	}

	private const int MaximumCellCount = 1024 * 1024;
	private const int MaximumControlSequenceLength = 128;
	private const int MaximumNumericParameter = 1_000_000;
	private const int MaximumOscLength = 4096;
	private const string UnsupportedSequenceExceptionPrefix =
		"Unsupported or malformed terminal control sequence:";
	private readonly ScreenBuffer _alternateBuffer;
	private readonly StringBuilder _controlSequence = new();
	private readonly ScreenBuffer _mainBuffer;
	private readonly int _maximumInputBytes;
	private readonly StringBuilder _osc = new();
	private readonly byte[] _utf8Bytes = new byte[4];
	private ScreenBuffer _activeBuffer;
	private int _alternateEntryColumn;
	private int _alternateEntryRow;
	private int _cursorStyle;
	private int _kittyKeyboardFlags;
	private int _modifyOtherKeysLevel;
	private bool _hadTerminalReset;
	private bool _hadVisibleContentDiscarded;
	private bool _hasAlternateEntryCursor;
	private bool _hasEverEnabledBracketedPaste;
	private bool _hasEverEnteredAlternateScreen;
	private bool _hasObservedDecscusr;
	private bool _hasObservedKittyKeyboardFlagsQuery;
	private bool _isAlternateScreenActive;
	private bool _isBracketedPasteEnabled;
	private bool _isCompleted;
	private bool _isCursorVisible = true;
	private bool _isFaulted;
	private bool _isFocusReportingEnabled;
	private bool _isSynchronizedOutputActive;
	private bool _isWin32InputModeEnabled;
	private ParserState _parserState;
	private long _totalInputBytes;
	private int _utf8Count;
	private int _utf8ExpectedLength;

	internal int Columns { get; }

	internal int Rows { get; }

	internal long TotalInputBytes => _totalInputBytes;

	internal TerminalScreenState(
		int columns,
		int rows,
		int maximumInputBytes)
	{
		if (columns <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(columns));
		}

		if (rows <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(rows));
		}

		if (((long)columns * rows) > MaximumCellCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(rows),
				"The terminal viewport is too large.");
		}

		if (maximumInputBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
		}

		Columns = columns;
		Rows = rows;
		_maximumInputBytes = maximumInputBytes;
		_mainBuffer = new ScreenBuffer(
			columns,
			rows,
			MarkVisibleContentDiscarded);
		_alternateBuffer = new ScreenBuffer(
			columns,
			rows,
			MarkVisibleContentDiscarded);
		_activeBuffer = _mainBuffer;
	}

	private static InvalidDataException CreateUnsupportedSequenceException(
		string sequence)
	{
		return new InvalidDataException(
			$"{UnsupportedSequenceExceptionPrefix} {sequence}.");
	}

	private static bool IsUnsupportedSequenceException(
		InvalidDataException exception)
	{
		return exception.Message.StartsWith(
			UnsupportedSequenceExceptionPrefix,
			StringComparison.Ordinal);
	}

	private static int GetParameter(
		IReadOnlyList<int?> parameters,
		int index,
		int defaultValue,
		bool zeroMeansDefault)
	{
		if ((index >= parameters.Count) || !parameters[index].HasValue)
		{
			return defaultValue;
		}

		int value = parameters[index]!.Value;
		return zeroMeansDefault && (value == 0) ? defaultValue : value;
	}

	private static int GetRuneWidth(Rune rune)
	{
		UnicodeCategory category = Rune.GetUnicodeCategory(rune);

		if ((category == UnicodeCategory.Control) ||
			(category == UnicodeCategory.Format) ||
			(category == UnicodeCategory.NonSpacingMark) ||
			(category == UnicodeCategory.SpacingCombiningMark) ||
			(category == UnicodeCategory.EnclosingMark))
		{
			throw new InvalidDataException(
				"The terminal stream contains an unsupported Unicode rune.");
		}

		int value = rune.Value;
		bool isWide =
			((value >= 0x1100) && (value <= 0x115F)) ||
			((value >= 0x2329) && (value <= 0x232A)) ||
			(((value >= 0x2E80) && (value <= 0xA4CF)) && (value != 0x303F)) ||
			((value >= 0xAC00) && (value <= 0xD7A3)) ||
			((value >= 0xF900) && (value <= 0xFAFF)) ||
			((value >= 0xFE10) && (value <= 0xFE19)) ||
			((value >= 0xFE30) && (value <= 0xFE6F)) ||
			((value >= 0xFF00) && (value <= 0xFF60)) ||
			((value >= 0xFFE0) && (value <= 0xFFE6)) ||
			((value >= 0x1F300) && (value <= 0x1FAFF)) ||
			((value >= 0x20000) && (value <= 0x3FFFD));
		return isWide ? 2 : 1;
	}

	private static bool IsSimpleSgrCode(int value)
	{
		return ((value >= 0) && (value <= 9)) ||
			((value >= 21) && (value <= 29)) ||
			((value >= 30) && (value <= 37)) ||
			(value == 39) ||
			((value >= 40) && (value <= 47)) ||
			(value == 49) ||
			((value >= 51) && (value <= 55)) ||
			(value == 59) ||
			((value >= 90) && (value <= 97)) ||
			((value >= 100) && (value <= 107));
	}

	private static bool IsSupportedPrivateMode(int mode)
	{
		return (mode == 25) ||
			(mode == 1004) ||
			(mode == 1049) ||
			(mode == 2004) ||
			(mode == 2026) ||
			(mode == 9001);
	}

	private static int?[] ParseParameters(string body, int maximumCount)
	{
		if (body.Length == 0)
		{
			return Array.Empty<int?>();
		}

		string[] parts = body.Split(';', StringSplitOptions.None);

		if (parts.Length > maximumCount)
		{
			throw CreateUnsupportedSequenceException($"CSI {body}");
		}

		int?[] parameters = new int?[parts.Length];

		for (int index = 0; index < parts.Length; index++)
		{
			string part = parts[index];

			if (part.Length == 0)
			{
				continue;
			}

			if (!int.TryParse(
				part,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int value) ||
				(value > MaximumNumericParameter))
			{
				throw CreateUnsupportedSequenceException($"CSI {body}");
			}

			parameters[index] = value;
		}

		return parameters;
	}

	private static void ValidateParameterCount(
		IReadOnlyCollection<int?> parameters,
		int maximumCount,
		string sequence)
	{
		if (parameters.Count > maximumCount)
		{
			throw CreateUnsupportedSequenceException(sequence);
		}
	}

	private static void ValidateSgr(string body)
	{
		int?[] parsedParameters = ParseParameters(body, 32);
		int[] parameters = parsedParameters.Length == 0
			? new[] { 0 }
			: parsedParameters.Select(parameter => parameter ?? 0).ToArray();

		for (int index = 0; index < parameters.Length; index++)
		{
			int code = parameters[index];

			if (IsSimpleSgrCode(code))
			{
				continue;
			}

			if ((code != 38) && (code != 48) && (code != 58))
			{
				throw CreateUnsupportedSequenceException($"CSI {body}m");
			}

			if ((index + 1) >= parameters.Length)
			{
				throw CreateUnsupportedSequenceException($"CSI {body}m");
			}

			int colorMode = parameters[++index];

			if (colorMode == 5)
			{
				if (((index + 1) >= parameters.Length) ||
					(parameters[++index] > 255))
				{
					throw CreateUnsupportedSequenceException($"CSI {body}m");
				}

				continue;
			}

			if (colorMode != 2)
			{
				throw CreateUnsupportedSequenceException($"CSI {body}m");
			}

			if ((index + 3) >= parameters.Length)
			{
				throw CreateUnsupportedSequenceException($"CSI {body}m");
			}

			for (int component = 0; component < 3; component++)
			{
				if (parameters[++index] > 255)
				{
					throw CreateUnsupportedSequenceException($"CSI {body}m");
				}
			}
		}
	}

	internal TerminalScreenSnapshot Capture()
	{
		EnsureNotFaulted();

		if ((_utf8Count != 0) ||
			(_parserState != ParserState.Ground) ||
			_isSynchronizedOutputActive)
		{
			throw new InvalidOperationException(
				"The terminal screen is between atomic updates.");
		}

		return new TerminalScreenSnapshot(
			Columns,
			Rows,
			_isAlternateScreenActive,
			_activeBuffer.CaptureLines(),
			_activeBuffer.CursorColumn,
			_activeBuffer.CursorRow,
			_isCursorVisible,
			_isBracketedPasteEnabled,
			_isFocusReportingEnabled,
			_isWin32InputModeEnabled,
			_hadVisibleContentDiscarded,
			_hadTerminalReset,
			_hasEverEnteredAlternateScreen,
			_hasEverEnabledBracketedPaste,
			_modifyOtherKeysLevel,
			_kittyKeyboardFlags,
			_hasObservedKittyKeyboardFlagsQuery,
			_cursorStyle,
			_hasObservedDecscusr);
	}

	internal void Complete()
	{
		EnsureCanFeed();

		try
		{
			if (_utf8Count != 0)
			{
				throw new DecoderFallbackException(
					"The terminal stream ended inside a UTF-8 sequence.");
			}

			if ((_parserState != ParserState.Ground) ||
				_isSynchronizedOutputActive)
			{
				throw new InvalidDataException(
					"The terminal stream ended inside a control sequence.");
			}

			_isCompleted = true;
		}
		catch
		{
			_isFaulted = true;
			throw;
		}
	}

	internal void Feed(ReadOnlySpan<byte> bytes)
	{
		EnsureCanFeed();

		try
		{
			if ((_totalInputBytes + bytes.Length) > _maximumInputBytes)
			{
				throw new InvalidDataException(
					"Terminal output exceeded the cumulative byte limit.");
			}

			_totalInputBytes += bytes.Length;

			foreach (byte value in bytes)
			{
				ProcessByte(value);
			}
		}
		catch
		{
			_isFaulted = true;
			throw;
		}
	}

	private void AppendControlCharacter(char value)
	{
		if (_controlSequence.Length >= MaximumControlSequenceLength)
		{
			throw new InvalidDataException(
				"Terminal control sequence exceeded the allowed length.");
		}

		_controlSequence.Append(value);
	}

	private void AppendOsc(Rune rune)
	{
		if ((_osc.Length + rune.Utf16SequenceLength) > MaximumOscLength)
		{
			throw new InvalidDataException(
				"Terminal OSC payload exceeded the allowed length.");
		}

		_osc.Append(rune.ToString());
	}

	private void CompleteOsc()
	{
		string value = _osc.ToString();
		_osc.Clear();
		int separatorIndex = value.IndexOf(';', StringComparison.Ordinal);

		if (separatorIndex <= 0)
		{
			throw CreateUnsupportedSequenceException("OSC");
		}

		string command = value[..separatorIndex];

		if ((command != "0") && (command != "1") && (command != "2"))
		{
			throw CreateUnsupportedSequenceException($"OSC {command}");
		}

		_parserState = ParserState.Ground;
	}

	private void EnsureCanFeed()
	{
		EnsureNotFaulted();

		if (_isCompleted)
		{
			throw new InvalidOperationException(
				"The terminal stream has already been completed.");
		}
	}

	private void EnsureNotFaulted()
	{
		if (_isFaulted)
		{
			throw new InvalidOperationException(
				"The terminal screen state is faulted.");
		}
	}

	private void EnterAlternateScreen()
	{
		_hasEverEnteredAlternateScreen = true;

		if (_isAlternateScreenActive)
		{
			return;
		}

		_alternateEntryColumn = _mainBuffer.CursorColumn;
		_alternateEntryRow = _mainBuffer.CursorRow;
		_hasAlternateEntryCursor = true;
		_alternateBuffer.Clear(reportVisibleContentDiscarded: true);
		_activeBuffer = _alternateBuffer;
		_isAlternateScreenActive = true;
	}

	private void ExecuteCsi(string body, char command)
	{
		int?[] parameters;

		switch (command)
		{
			case 'A':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorUp(GetParameter(parameters, 0, 1, true));
				break;
			case 'B':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorDown(GetParameter(parameters, 0, 1, true));
				break;
			case 'c':
				parameters = ParseParameters(body, 0);
				ValidateParameterCount(parameters, 0, $"CSI {body}c");
				break;
			case 'C':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorRight(GetParameter(parameters, 0, 1, true));
				break;
			case 'D':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorLeft(GetParameter(parameters, 0, 1, true));
				break;
			case 'E':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorNextLine(GetParameter(parameters, 0, 1, true));
				break;
			case 'F':
				parameters = ParseParameters(body, 1);
				_activeBuffer.CursorPreviousLine(GetParameter(parameters, 0, 1, true));
				break;
			case 'G':
				parameters = ParseParameters(body, 1);
				_activeBuffer.SetColumn(
					GetParameter(parameters, 0, 1, true) - 1);
				break;
			case 'H':
			case 'f':
				parameters = ParseParameters(body, 2);
				_activeBuffer.SetCursor(
					GetParameter(parameters, 0, 1, true) - 1,
					GetParameter(parameters, 1, 1, true) - 1);
				break;
			case 'J':
				parameters = ParseParameters(body, 1);
				_activeBuffer.EraseDisplay(
					GetParameter(parameters, 0, 0, false));
				break;
			case 'K':
				parameters = ParseParameters(body, 1);
				_activeBuffer.EraseLine(
					GetParameter(parameters, 0, 0, false));
				break;
			case 'X':
				parameters = ParseParameters(body, 1);
				_activeBuffer.EraseCharacters(
					GetParameter(parameters, 0, 1, true));
				break;
			case 'd':
				parameters = ParseParameters(body, 1);
				_activeBuffer.SetRow(
					GetParameter(parameters, 0, 1, true) - 1);
				break;
			case 'h':
				ExecutePrivateMode(body, enabled: true);
				break;
			case 'l':
				ExecutePrivateMode(body, enabled: false);
				break;
			case 'm':
				if (string.Equals(body, ">4", StringComparison.Ordinal))
				{
					// CSI >4m restores the terminal's initial value; this
					// renderer's initial modifyOtherKeys level is zero.
					_modifyOtherKeysLevel = 0;
					break;
				}

				if (string.Equals(body, ">4;2", StringComparison.Ordinal))
				{
					// Track only the exact observed XTMODKEYS state changes.
					// Live input uses raw-byte injection rather than a
					// keyboard-event encoder. Re-evaluate if that changes.
					_modifyOtherKeysLevel = 2;
					break;
				}

				ValidateSgr(body);
				break;
			case 'q':
				// DECSCUSR is exactly CSI Ps SP q. Ps is omitted or 0..6.
				if (string.Equals(body, " ", StringComparison.Ordinal))
				{
					_cursorStyle = 0;
				}
				else if ((body.Length == 2) &&
					(body[0] >= '0') &&
					(body[0] <= '6') &&
					(body[1] == ' '))
				{
					_cursorStyle = body[0] - '0';
				}
				else
				{
					throw CreateUnsupportedSequenceException($"CSI {body}q");
				}

				_hasObservedDecscusr = true;
				break;
			case 's':
				parameters = ParseParameters(body, 0);
				ValidateParameterCount(parameters, 0, $"CSI {body}s");
				_activeBuffer.SaveCursor();
				break;
			case 'u':
				if (string.Equals(body, "?", StringComparison.Ordinal))
				{
					// R0 intentionally behaves as an unsupported terminal and
					// sends no reply. Continue only if a stable prompt still
					// appears. Re-review any future terminal reply support.
					_hasObservedKittyKeyboardFlagsQuery = true;
					break;
				}

				if (string.Equals(body, "=0;1", StringComparison.Ordinal))
				{
					// Clear all progressive-enhancement flags.
					_kittyKeyboardFlags = 0;
					break;
				}

				if (string.Equals(body, "=1;1", StringComparison.Ordinal))
				{
					// Flag 1 disambiguates escape codes. The live path injects
					// raw bytes directly and does not use a keyboard-event
					// encoder. Re-evaluate if that input path changes.
					_kittyKeyboardFlags = 1;
					break;
				}

				parameters = ParseParameters(body, 0);
				ValidateParameterCount(parameters, 0, $"CSI {body}u");
				_activeBuffer.RestoreCursor();
				break;
			default:
				throw CreateUnsupportedSequenceException($"CSI {body}{command}");
		}
	}

	private void ExecutePrivateMode(string body, bool enabled)
	{
		if (!body.StartsWith("?", StringComparison.Ordinal) ||
			(body.Length == 1))
		{
			throw CreateUnsupportedSequenceException($"CSI {body}{(enabled ? 'h' : 'l')}");
		}

		int?[] parameters = ParseParameters(body[1..], 8);

		if (parameters.Any(parameter =>
			!parameter.HasValue ||
			!IsSupportedPrivateMode(parameter.Value)))
		{
			throw CreateUnsupportedSequenceException($"CSI {body}{(enabled ? 'h' : 'l')}");
		}

		foreach (int mode in parameters.Select(parameter => parameter!.Value))
		{
			if (mode == 25)
			{
				_isCursorVisible = enabled;
				continue;
			}

			if (mode == 2004)
			{
				_isBracketedPasteEnabled = enabled;

				if (enabled)
				{
					_hasEverEnabledBracketedPaste = true;
				}

				continue;
			}

			if (mode == 1004)
			{
				_isFocusReportingEnabled = enabled;
				continue;
			}

			if (mode == 9001)
			{
				_isWin32InputModeEnabled = enabled;
				continue;
			}

			if (mode == 2026)
			{
				_isSynchronizedOutputActive = enabled;
				continue;
			}

			if (mode != 1049)
			{
				continue;
			}

			if (enabled)
			{
				EnterAlternateScreen();
			}
			else
			{
				ExitAlternateScreen();
			}
		}
	}

	private void ExitAlternateScreen()
	{
		if (!_isAlternateScreenActive)
		{
			return;
		}

		if (_alternateBuffer.HasVisibleContent)
		{
			MarkVisibleContentDiscarded();
		}

		_activeBuffer = _mainBuffer;
		_isAlternateScreenActive = false;

		if (_hasAlternateEntryCursor)
		{
			_mainBuffer.SetCursor(_alternateEntryRow, _alternateEntryColumn);
			_hasAlternateEntryCursor = false;
		}
	}

	private void ProcessByte(byte value)
	{
		if (_utf8Count == 0)
		{
			if (value < 0x80)
			{
				ProcessRune(new Rune(value));
				return;
			}

			_utf8ExpectedLength = value switch
			{
				>= 0xC2 and <= 0xDF => 2,
				>= 0xE0 and <= 0xEF => 3,
				>= 0xF0 and <= 0xF4 => 4,
				_ => throw new DecoderFallbackException(
					"The terminal stream contains invalid UTF-8.")
			};
			_utf8Bytes[0] = value;
			_utf8Count = 1;
			return;
		}

		if ((value < 0x80) || (value > 0xBF))
		{
			throw new DecoderFallbackException(
				"The terminal stream contains invalid UTF-8.");
		}

		_utf8Bytes[_utf8Count++] = value;

		if (_utf8Count != _utf8ExpectedLength)
		{
			return;
		}

		OperationStatus status = Rune.DecodeFromUtf8(
			_utf8Bytes.AsSpan(0, _utf8Count),
			out Rune rune,
			out int consumed);

		if ((status != OperationStatus.Done) || (consumed != _utf8Count))
		{
			throw new DecoderFallbackException(
				"The terminal stream contains invalid UTF-8.");
		}

		_utf8Count = 0;
		_utf8ExpectedLength = 0;
		ProcessRune(rune);
	}

	private void MarkVisibleContentDiscarded()
	{
		_hadVisibleContentDiscarded = true;
	}

	private void ProcessCsi(Rune rune)
	{
		if (rune.Value > 0x7F)
		{
			throw CreateUnsupportedSequenceException("CSI");
		}

		char value = (char)rune.Value;

		if ((value >= 0x40) && (value <= 0x7E))
		{
			string body = _controlSequence.ToString();
			_controlSequence.Clear();
			_parserState = ParserState.Ground;

			try
			{
				ExecuteCsi(body, value);
			}
			catch (InvalidDataException exception) when (
				IsUnsupportedSequenceException(exception))
			{
				throw CreateUnsupportedSequenceException(
					$"CSI {body}{value}");
			}

			return;
		}

		if ((value < 0x20) || (value > 0x3F))
		{
			throw CreateUnsupportedSequenceException("CSI");
		}

		AppendControlCharacter(value);
	}

	private void ProcessEscape(Rune rune)
	{
		if (rune.Value > 0x7F)
		{
			throw CreateUnsupportedSequenceException("ESC");
		}

		_parserState = ParserState.Ground;

		switch ((char)rune.Value)
		{
			case '[':
				_controlSequence.Clear();
				_parserState = ParserState.Csi;
				break;
			case ']':
				_osc.Clear();
				_parserState = ParserState.Osc;
				break;
			case '7':
				_activeBuffer.SaveCursor();
				break;
			case '8':
				_activeBuffer.RestoreCursor();
				break;
			case 'D':
				_activeBuffer.LineFeed();
				break;
			case 'E':
				_activeBuffer.CarriageReturn();
				_activeBuffer.LineFeed();
				break;
			case 'M':
				_activeBuffer.ReverseIndex();
				break;
			case 'c':
				ResetTerminal();
				break;
			default:
				throw CreateUnsupportedSequenceException(
					$"ESC {(char)rune.Value}");
		}
	}

	private void ProcessGround(Rune rune)
	{
		switch (rune.Value)
		{
			case 0x07:
				return;
			case 0x08:
				_activeBuffer.Backspace();
				return;
			case 0x09:
				_activeBuffer.HorizontalTab();
				return;
			case 0x0A:
			case 0x0B:
			case 0x0C:
				_activeBuffer.LineFeed();
				return;
			case 0x0D:
				_activeBuffer.CarriageReturn();
				return;
			case 0x1B:
				_parserState = ParserState.Escape;
				return;
		}

		if ((rune.Value < 0x20) || (rune.Value == 0x7F))
		{
			throw new InvalidDataException(
				"The terminal stream contains an unsupported control character.");
		}

		_activeBuffer.WriteRune(rune, GetRuneWidth(rune));
	}

	private void ProcessOsc(Rune rune)
	{
		if (rune.Value == 0x07)
		{
			CompleteOsc();
			return;
		}

		if (rune.Value == 0x1B)
		{
			_parserState = ParserState.OscEscape;
			return;
		}

		if ((rune.Value < 0x20) || (rune.Value == 0x7F))
		{
			throw CreateUnsupportedSequenceException("OSC");
		}

		AppendOsc(rune);
	}

	private void ProcessOscEscape(Rune rune)
	{
		if (rune.Value != '\\')
		{
			throw CreateUnsupportedSequenceException("OSC ST");
		}

		CompleteOsc();
	}

	private void ProcessRune(Rune rune)
	{
		switch (_parserState)
		{
			case ParserState.Ground:
				ProcessGround(rune);
				break;
			case ParserState.Escape:
				ProcessEscape(rune);
				break;
			case ParserState.Csi:
				ProcessCsi(rune);
				break;
			case ParserState.Osc:
				ProcessOsc(rune);
				break;
			case ParserState.OscEscape:
				ProcessOscEscape(rune);
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private void ResetTerminal()
	{
		_hadTerminalReset = true;
		_mainBuffer.Clear(reportVisibleContentDiscarded: true);
		_alternateBuffer.Clear(reportVisibleContentDiscarded: true);
		_activeBuffer = _mainBuffer;
		_hasAlternateEntryCursor = false;
		_isAlternateScreenActive = false;
		_isBracketedPasteEnabled = false;
		_isCursorVisible = true;
		_isFocusReportingEnabled = false;
		_isSynchronizedOutputActive = false;
		_isWin32InputModeEnabled = false;
		_cursorStyle = 0;
		_kittyKeyboardFlags = 0;
		_modifyOtherKeysLevel = 0;
	}
}
