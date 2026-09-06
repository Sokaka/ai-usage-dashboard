using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityRedactedWidthBucket
{
	OneToEight,
	NineToSixteen,
	SeventeenToThirtyTwo,
	ThirtyThreeToSixtyFour,
	SixtyFiveToOneHundredTwentyEight,
	OneHundredTwentyNineOrMore
}

internal sealed record AntigravityRedactedLineShape(
	int LineIndex,
	AntigravityRedactedWidthBucket OccupiedWidthBucket);

internal sealed record AntigravityRedactedStructuralCapture(
	int Columns,
	int Rows,
	bool IsAlternateScreen,
	string StructuralFingerprint,
	IReadOnlyList<AntigravityRedactedLineShape> NonEmptyLines,
	bool IsBracketedPasteEnabled,
	bool IsFocusReportingEnabled,
	bool IsWin32InputModeEnabled,
	int ModifyOtherKeysLevel,
	int KittyKeyboardFlags = 0,
	bool HasObservedKittyKeyboardFlagsQuery = false,
	int? CursorStyle = null,
	bool HasObservedDecscusr = false)
{
	internal static AntigravityRedactedStructuralCapture Create(
		TerminalScreenSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if ((snapshot.Columns <= 0) ||
			(snapshot.Rows <= 0) ||
			(snapshot.Lines.Count != snapshot.Rows))
		{
			throw new InvalidDataException(
				"The terminal snapshot dimensions are invalid.");
		}

		List<AntigravityRedactedLineShape> nonEmptyLines = new();

		for (int lineIndex = 0; lineIndex < snapshot.Lines.Count; lineIndex++)
		{
			string line = snapshot.Lines[lineIndex] ??
				throw new InvalidDataException(
					"The terminal snapshot contains an invalid line.");
			int occupiedWidth = line.TrimEnd(' ').Length;

			if (occupiedWidth == 0)
			{
				continue;
			}

			nonEmptyLines.Add(new AntigravityRedactedLineShape(
				lineIndex,
				GetWidthBucket(occupiedWidth)));
		}

		string canonicalShape = BuildCanonicalShape(snapshot, nonEmptyLines);
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalShape));
		ReadOnlyCollection<AntigravityRedactedLineShape> readonlyLines =
			nonEmptyLines.AsReadOnly();
		return new AntigravityRedactedStructuralCapture(
			snapshot.Columns,
			snapshot.Rows,
			snapshot.IsAlternateScreen,
			Convert.ToHexString(hash),
			readonlyLines,
			snapshot.IsBracketedPasteEnabled,
			snapshot.IsFocusReportingEnabled,
			snapshot.IsWin32InputModeEnabled,
			snapshot.ModifyOtherKeysLevel,
			snapshot.KittyKeyboardFlags,
			snapshot.HasObservedKittyKeyboardFlagsQuery,
			snapshot.HasObservedDecscusr ? snapshot.CursorStyle : null,
			snapshot.HasObservedDecscusr);
	}

	private static string BuildCanonicalShape(
		TerminalScreenSnapshot snapshot,
		IReadOnlyList<AntigravityRedactedLineShape> nonEmptyLines)
	{
		StringBuilder canonical = new();
		canonical.Append("viewport:");
		canonical.Append(snapshot.Columns);
		canonical.Append('x');
		canonical.Append(snapshot.Rows);
		canonical.Append('\n');
		canonical.Append("alternate:");
		canonical.Append(snapshot.IsAlternateScreen ? '1' : '0');
		canonical.Append('\n');
		canonical.Append("bracketed-paste:");
		canonical.Append(snapshot.IsBracketedPasteEnabled ? '1' : '0');
		canonical.Append('\n');
		canonical.Append("focus-reporting:");
		canonical.Append(snapshot.IsFocusReportingEnabled ? '1' : '0');
		canonical.Append('\n');
		canonical.Append("win32-input:");
		canonical.Append(snapshot.IsWin32InputModeEnabled ? '1' : '0');
		canonical.Append('\n');
		canonical.Append("kitty-keyboard-flags:");
		canonical.Append(snapshot.KittyKeyboardFlags);
		canonical.Append('\n');
		canonical.Append("observed-kitty-keyboard-flags-query:");
		canonical.Append(
			snapshot.HasObservedKittyKeyboardFlagsQuery ? '1' : '0');
		canonical.Append('\n');
		canonical.Append("modify-other-keys:");
		canonical.Append(snapshot.ModifyOtherKeysLevel);

		if (snapshot.HasObservedDecscusr)
		{
			canonical.Append('\n');
			canonical.Append("observed-decscusr:1\n");
			canonical.Append("cursor-style:");
			canonical.Append(snapshot.CursorStyle);
		}

		foreach (AntigravityRedactedLineShape line in nonEmptyLines)
		{
			canonical.Append('\n');
			canonical.Append("line:");
			canonical.Append(line.LineIndex);
			canonical.Append(':');
			canonical.Append((int)line.OccupiedWidthBucket);
		}

		return canonical.ToString();
	}

	private static AntigravityRedactedWidthBucket GetWidthBucket(int width)
	{
		return width switch
		{
			<= 8 => AntigravityRedactedWidthBucket.OneToEight,
			<= 16 => AntigravityRedactedWidthBucket.NineToSixteen,
			<= 32 => AntigravityRedactedWidthBucket.SeventeenToThirtyTwo,
			<= 64 => AntigravityRedactedWidthBucket.ThirtyThreeToSixtyFour,
			<= 128 => AntigravityRedactedWidthBucket.SixtyFiveToOneHundredTwentyEight,
			_ => AntigravityRedactedWidthBucket.OneHundredTwentyNineOrMore
		};
	}
}

internal static class AntigravityLocalExactScreenFingerprint
{
	internal static string Compute(
		TerminalScreenSnapshot snapshot,
		ReadOnlySpan<byte> hmacKey)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (hmacKey.Length < 32)
		{
			throw new ArgumentException(
				"The local exact-screen fingerprint key must contain at least 32 bytes.",
				nameof(hmacKey));
		}

		byte[] keyCopy = hmacKey.ToArray();

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHMAC(
				HashAlgorithmName.SHA256,
				keyCopy);
			Append(hash, $"viewport:{snapshot.Columns}x{snapshot.Rows}\n");
			Append(hash, $"alternate:{(snapshot.IsAlternateScreen ? 1 : 0)}\n");
			Append(hash, $"cursor-row:{snapshot.CursorRow}\n");
			Append(hash, $"cursor-column:{snapshot.CursorColumn}\n");
			Append(hash, $"cursor-visible:{(snapshot.IsCursorVisible ? 1 : 0)}\n");
			Append(
				hash,
				$"bracketed-paste:{(snapshot.IsBracketedPasteEnabled ? 1 : 0)}\n");
			Append(
				hash,
				$"focus-reporting:{(snapshot.IsFocusReportingEnabled ? 1 : 0)}\n");
			Append(
				hash,
				$"win32-input:{(snapshot.IsWin32InputModeEnabled ? 1 : 0)}\n");
			Append(
				hash,
				$"kitty-keyboard-flags:{snapshot.KittyKeyboardFlags}\n");
			Append(
				hash,
				$"observed-kitty-keyboard-flags-query:{(snapshot.HasObservedKittyKeyboardFlagsQuery ? 1 : 0)}\n");
			Append(
				hash,
				$"modify-other-keys:{snapshot.ModifyOtherKeysLevel}\n");

			if (snapshot.HasObservedDecscusr)
			{
				Append(hash, "observed-decscusr:1\n");
				Append(hash, $"cursor-style:{snapshot.CursorStyle}\n");
			}

			for (int lineIndex = 0; lineIndex < snapshot.Lines.Count; lineIndex++)
			{
				Append(hash, $"line:{lineIndex}:");
				Append(hash, snapshot.Lines[lineIndex]);
				Append(hash, "\n");
			}

			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(keyCopy);
		}
	}

	private static void Append(IncrementalHash hash, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);

		try
		{
			hash.AppendData(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}
