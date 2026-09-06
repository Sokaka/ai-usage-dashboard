using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsageR1SectionPrivateDraftKind
{
	MachineGenerated
}

internal enum AntigravityUsageR1SectionPrivateDraftReviewCode
{
	ConfirmExactLiteral,
	ConfirmOuterChromeShape,
	ConfirmIdentityBoundary,
	ConfirmSectionHeading,
	ConfirmWindowLabel,
	ConfirmCapacityGrammar,
	ConfirmResetAlternatives,
	ConfirmNonQuotaDynamicAmount
}

internal enum AntigravityUsageR1SectionPrivateDraftFailureStage
{
	PathValidation,
	FingerprintKeyValidation,
	PrivateBundleRead,
	PrivateBundleFormat,
	DraftExtraction,
	OutputSerialization,
	ExistingOutputValidation,
	TemporaryWrite,
	Commit,
	OutputVerification
}

internal enum AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
{
	Blueprint,
	IdentityGrammar,
	CapacityGrammar,
	ResetGrammar,
	ResetShape,
	ResetEvidence,
	AmountGrammar,
	OuterChromeGrammar,
	SchemaValidation,
	ObservationLineShape,
	ObservationIdentity,
	ObservationCapacity,
	ObservationReset,
	ObservationMeterConsistency,
	ObservationDetailPercentageConsistency,
	ObservationPagination,
	ObservationOther,
	PageFingerprint
}

internal sealed record AntigravityUsageR1SectionPrivateDraftReviewItem(
	AntigravityUsageR1SectionPrivateDraftReviewCode Code,
	int RowIndex);

internal sealed record AntigravityUsageR1SectionPrivateDraftFile(
	string FormatVersion,
	AntigravityUsageR1SectionPrivateDraftKind DraftKind,
	AntigravityUsageR1ReviewState ReviewState,
	AntigravityUsageR1SectionSpec Spec,
	IReadOnlyList<AntigravityUsageR1SectionPrivateDraftReviewItem> ReviewItems,
	string DraftFingerprint);

internal sealed record AntigravityUsageR1SectionPrivateDraftMetadata(
	bool WasWritten,
	int ObservationCount,
	int ReviewItemCount,
	string DraftFingerprint);

internal sealed class AntigravityUsageR1SectionPrivateDraftException :
	IOException
{
	internal AntigravityUsageR1SectionPrivateDraftExtractionFailureCode?
		ExtractionFailureCode { get; }

	internal int? ObservationIndex { get; }

	internal int? RowIndex { get; }

	internal AntigravityUsageR1SectionPrivateDraftFailureStage Stage { get; }

	internal AntigravityUsageR1SectionPrivateDraftException(
		AntigravityUsageR1SectionPrivateDraftFailureStage stage,
		AntigravityUsageR1SectionPrivateDraftExtractionFailureCode?
			extractionFailureCode = null,
		int? rowIndex = null,
		int? observationIndex = null)
		: base($"The private AGY R1 section draft operation failed at stage: {stage}.")
	{
		Stage = stage;
		ExtractionFailureCode = extractionFailureCode;
		RowIndex = rowIndex;
		ObservationIndex = observationIndex;
	}
}

internal static class AntigravityUsageR1SectionPrivateDraftGenerator
{
	private sealed class DraftExtractionDiagnosticException : Exception
	{
		internal AntigravityUsageR1SectionPrivateDraftExtractionFailureCode Code
		{
			get;
		}

		internal int? ObservationIndex { get; }

		internal int? RowIndex { get; }

		internal DraftExtractionDiagnosticException(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode code,
			int? rowIndex = null,
			int? observationIndex = null)
		{
			Code = code;
			RowIndex = rowIndex;
			ObservationIndex = observationIndex;
		}
	}

	private enum CountdownFormat
	{
		HoursAndMinutes,
		MinutesOnly
	}

	private sealed record IdentityShape(string Prefix, string Suffix);

	private sealed record CapacityShape(string Prefix, string Separator);

	private sealed record CountdownShape(string Prefix, string Middle);

	private sealed record CountdownObservation(
		CountdownShape Shape,
		CountdownFormat Format);

	private sealed record AvailabilityShape(string Prefix, string Status);

	private sealed record UnsignedAmountShape(string Prefix, string Suffix);

	private const int Columns = 120;
	private const int Rows = 50;
	private const int MaximumPrivateBundleBytes = 4 * 1024 * 1024;
	private const int MaximumDraftBytes = 1024 * 1024;
	private const int MaximumSequences = 16;
	private const int MaximumLineLength = 4096;
	private const int WeeklyMaximumMinutes = 7 * 24 * 60;
	private const int RollingMaximumMinutes = 5 * 60;
	private const string FilledMeterGlyph = "█";
	private const string EmptyMeterGlyph = "░";
	private const string AvailabilityStatusId = "quota_available";
	internal const string FormatVersion =
		"agy-usage-r1-section-private-machine-draft-v1";
	private const string FingerprintFormatVersion =
		"agy-usage-r1-section-private-machine-draft-fingerprint-v1";

	private static readonly int[] CapacityRows = { 14, 18, 26, 30 };
	private static readonly int[] ResetRows = { 15, 19, 27, 31 };
	private static readonly HashSet<string> IdentityAnchorWords = new(
		new[] { "account", "email", "user", "signed", "in", "as" },
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> CountdownAnchorWords = new(
		new[]
		{
			"left", "remaining", "refresh", "refreshes", "renew", "renews",
			"reset", "resets", "in"
		},
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> AvailabilityWords = new(
		new[] { "available", "is", "now", "quota", "usage" },
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> AmountAnchorWords = new(
		new[] { "ai", "available", "credit", "credits" },
		StringComparer.OrdinalIgnoreCase);

	internal static async Task<AntigravityUsageR1SectionPrivateDraftMetadata>
		GenerateFromPrivateBundleAsync(
			string privateBundlePath,
			string outputPath,
			string expectedCaptureContractFingerprint,
			ReadOnlyMemory<byte> draftFingerprintHmacKey,
			CancellationToken cancellationToken = default)
	{
		AntigravityUsageR1SectionPrivateDraftFailureStage stage =
			AntigravityUsageR1SectionPrivateDraftFailureStage.PathValidation;
		byte[]? privateBundleBytes = null;
		byte[]? serializedDraft = null;
		string? temporaryPath = null;

		try
		{
			ResolvePaths(
				privateBundlePath,
				outputPath,
				out string absoluteBundlePath,
				out string absoluteOutputPath,
				out string privateDirectory);

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.FingerprintKeyValidation;

			if (!AntigravityUsageR1SchemaParser.IsFingerprint(
					expectedCaptureContractFingerprint) ||
				draftFingerprintHmacKey.Length < 32)
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.PrivateBundleRead;
			privateBundleBytes =
				await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
					absoluteBundlePath,
					MaximumPrivateBundleBytes,
					requirePrivateFile: true,
					cancellationToken);

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.PrivateBundleFormat;
			AntigravityUsageR1PrivateScreenBundleFile privateBundle =
				AntigravityUsageR1CalibrationJson.DeserializePrivateBundle(
					privateBundleBytes);
			ValidatePrivateBundle(privateBundle);

			if (!string.Equals(
					privateBundle.CaptureContractFingerprint,
					expectedCaptureContractFingerprint,
					StringComparison.Ordinal))
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.DraftExtraction;
			AntigravityUsageR1SectionPrivateDraftFile draft = BuildDraft(
				privateBundle,
				draftFingerprintHmacKey.Span);

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.OutputSerialization;
			serializedDraft = AntigravityUsageR1SectionPrivateDraftJson
				.Serialize(draft);

			if ((serializedDraft.Length <= 0) ||
				(serializedDraft.Length > MaximumDraftBytes))
			{
				throw CreateFailure(stage);
			}

			if (File.Exists(absoluteOutputPath))
			{
				stage = AntigravityUsageR1SectionPrivateDraftFailureStage
					.ExistingOutputValidation;

				if (!await HasExactPrivateDraftAsync(
						absoluteOutputPath,
						serializedDraft,
						cancellationToken))
				{
					throw CreateFailure(stage);
				}

				return CreateMetadata(
					draft,
					privateBundle.Sequences.Count,
					wasWritten: false);
			}

			if (Directory.Exists(absoluteOutputPath))
			{
				throw CreateFailure(
					AntigravityUsageR1SectionPrivateDraftFailureStage
						.PathValidation);
			}

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.TemporaryWrite;
			temporaryPath = Path.Combine(
				privateDirectory,
				$".{Path.GetFileName(absoluteOutputPath)}.r1-draft.{Guid.NewGuid():N}.tmp");
			await WritePrivateFileAsync(
				temporaryPath,
				serializedDraft,
				cancellationToken);

			if (!await HasExactPrivateDraftAsync(
					temporaryPath,
					serializedDraft,
					cancellationToken))
			{
				throw CreateFailure(stage);
			}

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage.Commit;
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				File.Move(temporaryPath, absoluteOutputPath);
				temporaryPath = null;
			}
			catch (IOException)
			{
				if (!await HasExactPrivateDraftAsync(
						absoluteOutputPath,
						serializedDraft,
						cancellationToken))
				{
					throw CreateFailure(stage);
				}
			}

			stage = AntigravityUsageR1SectionPrivateDraftFailureStage
				.OutputVerification;

			if (!await HasExactPrivateDraftAsync(
					absoluteOutputPath,
					serializedDraft,
					CancellationToken.None))
			{
				throw CreateFailure(stage);
			}

			return CreateMetadata(
				draft,
				privateBundle.Sequences.Count,
				wasWritten: true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (AntigravityUsageR1SectionPrivateDraftException)
		{
			throw;
		}
		catch (DraftExtractionDiagnosticException exception)
		{
			throw CreateFailure(
				stage,
				exception.Code,
				exception.RowIndex,
				exception.ObservationIndex);
		}
		catch
		{
			throw CreateFailure(stage);
		}
		finally
		{
			TryDeletePrivateSibling(temporaryPath);
			ZeroBytes(privateBundleBytes);
			ZeroBytes(serializedDraft);
		}
	}

	internal static bool IsExactApprovedDraft(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		ReadOnlyMemory<byte> observedDraftBytes,
		string approvedDraftFingerprint,
		ReadOnlyMemory<byte> draftFingerprintHmacKey)
	{
		if (!AntigravityUsageR1SchemaParser.IsFingerprint(
				approvedDraftFingerprint) ||
			draftFingerprintHmacKey.Length < 32)
		{
			return false;
		}

		byte[]? expectedBytes = null;

		try
		{
			ValidatePrivateBundle(privateBundle);
			AntigravityUsageR1SectionPrivateDraftFile expected = BuildDraft(
				privateBundle,
				draftFingerprintHmacKey.Span);

			if (!string.Equals(
					expected.DraftFingerprint,
					approvedDraftFingerprint,
					StringComparison.Ordinal))
			{
				return false;
			}

			expectedBytes = AntigravityUsageR1SectionPrivateDraftJson.Serialize(
				expected);
			return CryptographicOperations.FixedTimeEquals(
				expectedBytes,
				observedDraftBytes.Span);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (expectedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedBytes);
			}
		}
	}

	private static AntigravityUsageR1SectionPrivateDraftFile BuildDraft(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		ReadOnlySpan<byte> hmacKey)
	{
		AntigravityUsageR1PrivateScreen baseline =
			privateBundle.Sequences[0].Pages[0];
		string[] baselineLines = baseline.Lines
			.Select(NormalizeLine)
			.ToArray();
		RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode.Blueprint,
			rowIndex: null,
			() => ValidateExactBlueprintRows(privateBundle, baselineLines));

		IdentityShape identity = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.IdentityGrammar,
			8,
			() => ExtractIdentityShape(baselineLines[8]));
		CapacityShape capacity = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.CapacityGrammar,
			14,
			() => ExtractCapacityShape(baselineLines[14]));
		CountdownShape? countdown = null;
		HashSet<CountdownFormat> countdownFormats = new();
		AvailabilityShape? availability = null;

		foreach (AntigravityUsageR1PrivateScreenSequence sequence in
			privateBundle.Sequences)
		{
			foreach (int row in ResetRows)
			{
				try
				{
					string rendered = NormalizeLine(sequence.Pages[0].Lines[row]);

					if (TryExtractCountdownObservation(
							rendered,
							out CountdownObservation? observed))
					{
						if (observed is null)
						{
							throw new InvalidDataException();
						}

						if ((countdown is not null) &&
							(countdown != observed.Shape))
						{
							throw new DraftExtractionDiagnosticException(
								AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
									.ResetShape,
								row);
						}

						countdown ??= observed.Shape;
						countdownFormats.Add(observed.Format);
						continue;
					}

					AvailabilityShape observedAvailability =
						ExtractAvailabilityShape(rendered);

					if ((availability is not null) &&
						(availability != observedAvailability))
					{
						throw new DraftExtractionDiagnosticException(
							AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
								.ResetShape,
							row);
					}

					availability ??= observedAvailability;
				}
				catch (DraftExtractionDiagnosticException)
				{
					throw;
				}
				catch
				{
					throw new DraftExtractionDiagnosticException(
						AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
							.ResetGrammar,
						row);
				}
			}
		}

		if ((countdown is null) ||
			(countdownFormats.Count == 0) ||
			(availability is null))
		{
			throw new DraftExtractionDiagnosticException(
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ResetEvidence);
		}

		UnsignedAmountShape amount = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.AmountGrammar,
			45,
			() => ExtractUnsignedAmountShape(baselineLines[45]));
		string outerTopPrefix = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.OuterChromeGrammar,
			0,
			() => ExtractOuterPublicPrefix(baselineLines[0]));
		string outerBottomPrefix = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.OuterChromeGrammar,
			49,
			() => ExtractOuterPublicPrefix(baselineLines[49]));
		AntigravityUsageR1SectionSpec frozen = RunDraftExtraction(
			AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.SchemaValidation,
			rowIndex: null,
			() => AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
				CreateSpec(
					baselineLines,
					identity,
					capacity,
					countdown,
					countdownFormats,
					availability,
					amount,
					outerTopPrefix,
					outerBottomPrefix)));
		string? pageFingerprint = null;

		for (int observationIndex = 0;
			observationIndex < privateBundle.Sequences.Count;
			observationIndex++)
		{
			AntigravityUsageR1PrivateScreenSequence sequence =
				privateBundle.Sequences[observationIndex];
			AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];
			AntigravityUsageR1SectionParseResult result;

			try
			{
				result = AntigravityUsageR1SectionSchemaParser
					.ParseCalibrationPage(
					new TerminalScreenSnapshot(
						screen.Columns,
						screen.Rows,
						screen.IsAlternateScreen,
						screen.Lines.ToArray()),
					frozen);
			}
			catch (InvalidDataException exception)
			{
				throw new DraftExtractionDiagnosticException(
					ClassifyObservationFailure(exception),
					observationIndex: observationIndex);
			}

			pageFingerprint ??= result.Capture.PageFingerprint;

			if (!string.Equals(
					pageFingerprint,
					result.Capture.PageFingerprint,
					StringComparison.Ordinal))
			{
				throw new DraftExtractionDiagnosticException(
					AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
						.PageFingerprint,
					observationIndex: observationIndex);
			}
		}

		ReadOnlyCollection<AntigravityUsageR1SectionPrivateDraftReviewItem>
			reviewItems = CreateReviewItems(frozen.Lines);
		string fingerprint = ComputeDraftFingerprint(
			privateBundle,
			frozen,
			reviewItems,
			hmacKey);

		return new AntigravityUsageR1SectionPrivateDraftFile(
			FormatVersion,
			AntigravityUsageR1SectionPrivateDraftKind.MachineGenerated,
			AntigravityUsageR1ReviewState.NeedsReview,
			frozen,
			reviewItems,
			fingerprint);
	}

	private static T RunDraftExtraction<T>(
		AntigravityUsageR1SectionPrivateDraftExtractionFailureCode code,
		int? rowIndex,
		Func<T> action)
	{
		return RunDraftExtraction(
			code,
			rowIndex,
			observationIndex: null,
			action);
	}

	private static T RunDraftExtraction<T>(
		AntigravityUsageR1SectionPrivateDraftExtractionFailureCode code,
		int? rowIndex,
		int? observationIndex,
		Func<T> action)
	{
		try
		{
			return action();
		}
		catch (DraftExtractionDiagnosticException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is InvalidDataException or
			ArgumentException or
			InvalidOperationException)
		{
			throw new DraftExtractionDiagnosticException(
				code,
				rowIndex,
				observationIndex);
		}
	}

	private static void RunDraftExtraction(
		AntigravityUsageR1SectionPrivateDraftExtractionFailureCode code,
		int? rowIndex,
		Action action)
	{
		try
		{
			action();
		}
		catch (DraftExtractionDiagnosticException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is InvalidDataException or
			ArgumentException or
			InvalidOperationException)
		{
			throw new DraftExtractionDiagnosticException(code, rowIndex);
		}
	}

	private static AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
		ClassifyObservationFailure(InvalidDataException exception)
	{
		return exception.Message switch
		{
			"The AGY R1 rendered line is missing or ambiguous." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationLineShape,
			"The AGY R1 account identity is ambiguous." or
			"The AGY R1 account identity was not observed." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationIdentity,
			"The AGY R1 quota meter disagrees with its percentage." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationMeterConsistency,
			"The AGY R1 quota detail percentage is inconsistent." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationDetailPercentageConsistency,
			"The AGY R1 quota capacity is duplicated." or
			"The AGY R1 quota capacity is incomplete." or
			"The AGY R1 quota capacity was not observed." or
			"The AGY R1 quota capacity policy was violated." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationCapacity,
			"The AGY R1 quota reset is duplicated." or
			"The AGY R1 quota reset is incomplete." or
			"The AGY R1 quota reset was not observed." or
			"The AGY R1 quota reset policy was violated." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationReset,
			"The AGY R1 full page unexpectedly contains a counter." or
			"The AGY R1 pagination counter is incomplete." or
			"The AGY R1 quota page is not fully visible." =>
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
					.ObservationPagination,
			_ => AntigravityUsageR1SectionPrivateDraftExtractionFailureCode
				.ObservationOther
		};
	}

	private static AntigravityUsageR1SectionSpec CreateSpec(
		IReadOnlyList<string> lines,
		IdentityShape identity,
		CapacityShape capacity,
		CountdownShape countdown,
		IReadOnlySet<CountdownFormat> countdownFormats,
		AvailabilityShape availability,
		UnsignedAmountShape amount,
		string outerTopPrefix,
		string outerBottomPrefix)
	{
		const string firstSectionId = "agy.gemini";
		const string secondSectionId = "agy.claude";
		const string firstWeeklyId = "agy.gemini.weekly";
		const string firstRollingId = "agy.gemini.rolling-5h";
		const string secondWeeklyId = "agy.claude.weekly";
		const string secondRollingId = "agy.claude.rolling-5h";
		AntigravityUsageR1SectionRule[] sections =
		{
			new(
				firstSectionId,
				lines[10],
				0,
				Array.AsReadOnly(new[]
				{
					CreateWindow(firstWeeklyId, lines[13], weekly: true, 0),
					CreateWindow(firstRollingId, lines[17], weekly: false, 1)
				})),
			new(
				secondSectionId,
				lines[22],
				1,
				Array.AsReadOnly(new[]
				{
					CreateWindow(secondWeeklyId, lines[25], weekly: true, 0),
					CreateWindow(secondRollingId, lines[29], weekly: false, 1)
				}))
		};
		Dictionary<int, (string SectionId, string WindowId, bool Weekly)> windows =
			new()
			{
				[13] = (firstSectionId, firstWeeklyId, true),
				[14] = (firstSectionId, firstWeeklyId, true),
				[15] = (firstSectionId, firstWeeklyId, true),
				[17] = (firstSectionId, firstRollingId, false),
				[18] = (firstSectionId, firstRollingId, false),
				[19] = (firstSectionId, firstRollingId, false),
				[25] = (secondSectionId, secondWeeklyId, true),
				[26] = (secondSectionId, secondWeeklyId, true),
				[27] = (secondSectionId, secondWeeklyId, true),
				[29] = (secondSectionId, secondRollingId, false),
				[30] = (secondSectionId, secondRollingId, false),
				[31] = (secondSectionId, secondRollingId, false)
			};
		AntigravityUsageR1SectionLineRule[] lineRules = new
			AntigravityUsageR1SectionLineRule[Rows];

		for (int row = 0; row < Rows; row++)
		{
			if (row == 0)
			{
				lineRules[row] = CreateOuterLine(row, outerTopPrefix);
			}
			else if (row == 49)
			{
				lineRules[row] = CreateOuterLine(row, outerBottomPrefix);
			}
			else if (row == 8)
			{
				lineRules[row] = CreateIdentityLine(row, identity);
			}
			else if (row == 45)
			{
				lineRules[row] = CreateUnsignedAmountLine(row, amount);
			}
			else if ((row == 10) || (row == 22))
			{
				string sectionId = row == 10 ? firstSectionId : secondSectionId;
				lineRules[row] = CreateExactLine(
					row,
					AntigravityUsageR1SectionLineRole.SectionHeading,
					lines[row],
					sectionId,
					stableWindowId: null);
			}
			else if (windows.TryGetValue(row, out var window) &&
				((row == 13) || (row == 17) || (row == 25) || (row == 29)))
			{
				lineRules[row] = CreateExactLine(
					row,
					AntigravityUsageR1SectionLineRole.WindowLabel,
					lines[row],
					window.SectionId,
					window.WindowId);
			}
			else if (windows.TryGetValue(row, out window) &&
				CapacityRows.Contains(row))
			{
				lineRules[row] = CreateCapacityLine(
					row,
					window.SectionId,
					window.WindowId,
					capacity);
			}
			else if (windows.TryGetValue(row, out window) &&
				ResetRows.Contains(row))
			{
				lineRules[row] = CreateResetLine(
					row,
					window.SectionId,
					window.WindowId,
					window.Weekly,
					countdown,
					countdownFormats,
					availability);
			}
			else
			{
				lineRules[row] = CreateExactLine(
					row,
					AntigravityUsageR1SectionLineRole.Fixed,
					lines[row],
					stableSectionId: null,
					stableWindowId: null);
			}
		}

		return new AntigravityUsageR1SectionSpec(
			2,
			"agy-usage-r1-120x50-primary-section-v1",
			Columns,
			Rows,
			IsAlternateScreen: false,
			Array.AsReadOnly(sections),
			Array.AsReadOnly(lineRules),
			AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible,
			AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible);
	}

	private static AntigravityUsageR1SectionWindowRule CreateWindow(
		string stableWindowId,
		string renderedLiteral,
		bool weekly,
		int order)
	{
		return new AntigravityUsageR1SectionWindowRule(
			stableWindowId,
			renderedLiteral,
			weekly
				? AntigravityUsageR1SectionWindowKind.Weekly
				: AntigravityUsageR1SectionWindowKind.RollingHours,
			weekly ? null : 5,
			order,
			AntigravityUsageR1SectionCapacityPolicy.Percentage,
			AntigravityUsageR1SectionResetPolicy.CountdownOrAvailability);
	}

	private static AntigravityUsageR1SectionLineRule CreateExactLine(
		int row,
		AntigravityUsageR1SectionLineRole role,
		string rendered,
		string? stableSectionId,
		string? stableWindowId)
	{
		return CreateLine(
			row,
			role,
			stableSectionId,
			stableWindowId,
			CreatePattern(
				$"row-{row:D2}-exact",
				new AntigravityUsageR1SectionLiteralToken(rendered)));
	}

	private static AntigravityUsageR1SectionLineRule CreateOuterLine(
		int row,
		string publicPrefix)
	{
		return CreateLine(
			row,
			AntigravityUsageR1SectionLineRole.OuterChrome,
			stableSectionId: null,
			stableWindowId: null,
			CreatePattern(
				$"row-{row:D2}-outer-shape",
				new AntigravityUsageR1SectionOuterLineShapeToken(
					publicPrefix,
					PublicSuffix: null,
					MinimumDynamicLength: 1,
					MaximumDynamicLength: 1000,
					AntigravityUsageR1SectionOuterLineCharacterClass
						.UnicodeVisibleText)));
	}

	private static AntigravityUsageR1SectionLineRule CreateIdentityLine(
		int row,
		IdentityShape shape)
	{
		List<AntigravityUsageR1SectionToken> tokens = new();
		AddLiteral(tokens, shape.Prefix);
		tokens.Add(new AntigravityUsageR1SectionIdentityToken());
		AddLiteral(tokens, shape.Suffix);
		return CreateLine(
			row,
			AntigravityUsageR1SectionLineRole.Identity,
			null,
			null,
			CreatePattern($"row-{row:D2}-identity", tokens.ToArray()));
	}

	private static AntigravityUsageR1SectionLineRule CreateCapacityLine(
		int row,
		string sectionId,
		string windowId,
		CapacityShape shape)
	{
		List<AntigravityUsageR1SectionToken> tokens = new();
		AddLiteral(tokens, shape.Prefix);
		tokens.Add(new AntigravityUsageR1SectionProgressMeterToken(
			new AntigravityUsageR1SectionProgressMeterRule(
				50,
				FilledMeterGlyph,
				EmptyMeterGlyph,
				AntigravityUsageR1SectionPercentMeaning.Remaining,
				AntigravityUsageR1SectionMeterRounding.Nearest)));
		AddLiteral(tokens, shape.Separator);
		tokens.Add(new AntigravityUsageR1SectionPercentToken(
			AntigravityUsageR1SectionPercentMeaning.Remaining,
			2));
		return CreateLine(
			row,
			AntigravityUsageR1SectionLineRole.Capacity,
			sectionId,
			windowId,
			CreatePattern($"row-{row:D2}-capacity", tokens.ToArray()));
	}

	private static AntigravityUsageR1SectionLineRule CreateResetLine(
		int row,
		string sectionId,
		string windowId,
		bool weekly,
		CountdownShape countdown,
		IReadOnlySet<CountdownFormat> countdownFormats,
		AvailabilityShape availability)
	{
		List<AntigravityUsageR1SectionLinePattern> alternatives = new();

		if (countdownFormats.Contains(CountdownFormat.HoursAndMinutes))
		{
			alternatives.Add(CreateCountdownPattern(
				row,
				weekly,
				countdown,
				CountdownFormat.HoursAndMinutes));
		}

		if (countdownFormats.Contains(CountdownFormat.MinutesOnly))
		{
			alternatives.Add(CreateCountdownPattern(
				row,
				weekly,
				countdown,
				CountdownFormat.MinutesOnly));
		}

		List<AntigravityUsageR1SectionToken> availabilityTokens = new();
		AddLiteral(availabilityTokens, availability.Prefix);
		availabilityTokens.Add(new AntigravityUsageR1SectionAvailabilityToken(
			Array.AsReadOnly(new[]
			{
				new AntigravityUsageR1SectionAvailabilityRule(
					AvailabilityStatusId,
					availability.Status)
			})));
		alternatives.Add(CreatePattern(
			$"row-{row:D2}-available",
			availabilityTokens.ToArray()));
		return CreateLine(
			row,
			AntigravityUsageR1SectionLineRole.Reset,
			sectionId,
			windowId,
			alternatives.ToArray());
	}

	private static AntigravityUsageR1SectionLinePattern CreateCountdownPattern(
		int row,
		bool weekly,
		CountdownShape countdown,
		CountdownFormat format)
	{
		List<AntigravityUsageR1SectionToken> tokens = new();
		AddLiteral(tokens, countdown.Prefix);
		tokens.Add(new AntigravityUsageR1SectionPercentToken(
			AntigravityUsageR1SectionPercentMeaning.Remaining,
			0));
		AddLiteral(tokens, countdown.Middle);
		AntigravityUsageR1SectionCountdownUnitRule[] units = format switch
		{
			CountdownFormat.HoursAndMinutes =>
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
			CountdownFormat.MinutesOnly =>
				new[]
				{
					new AntigravityUsageR1SectionCountdownUnitRule(
						AntigravityUsageR1SectionCountdownUnit.Minutes,
						"m",
						"m",
						0)
				},
			_ => throw new InvalidDataException()
		};
		tokens.Add(new AntigravityUsageR1SectionCountdownToken(
			new AntigravityUsageR1SectionCountdownRule(
				" ",
				Array.AsReadOnly(units),
				weekly ? WeeklyMaximumMinutes : RollingMaximumMinutes)));
		string patternSuffix = format == CountdownFormat.HoursAndMinutes
			? "countdown"
			: "countdown-minutes-only";
		return CreatePattern(
			$"row-{row:D2}-{patternSuffix}",
			tokens.ToArray());
	}

	private static AntigravityUsageR1SectionLineRule CreateUnsignedAmountLine(
		int row,
		UnsignedAmountShape shape)
	{
		List<AntigravityUsageR1SectionToken> tokens = new();
		AddLiteral(tokens, shape.Prefix);
		tokens.Add(new AntigravityUsageR1SectionUnsignedAmountToken(1, 128));
		AddLiteral(tokens, shape.Suffix);
		return CreateLine(
			row,
			AntigravityUsageR1SectionLineRole.NonQuotaDynamic,
			null,
			null,
			CreatePattern($"row-{row:D2}-unsigned-amount", tokens.ToArray()));
	}

	private static AntigravityUsageR1SectionLineRule CreateLine(
		int row,
		AntigravityUsageR1SectionLineRole role,
		string? stableSectionId,
		string? stableWindowId,
		params AntigravityUsageR1SectionLinePattern[] alternatives)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"row-{row:D2}",
			role,
			stableSectionId,
			stableWindowId,
			Array.AsReadOnly(alternatives));
	}

	private static AntigravityUsageR1SectionLinePattern CreatePattern(
		string patternId,
		params AntigravityUsageR1SectionToken[] tokens)
	{
		return new AntigravityUsageR1SectionLinePattern(
			patternId,
			Array.AsReadOnly(tokens));
	}

	private static void AddLiteral(
		ICollection<AntigravityUsageR1SectionToken> tokens,
		string value)
	{
		if (value.Length > 0)
		{
			tokens.Add(new AntigravityUsageR1SectionLiteralToken(value));
		}
	}

	private static IdentityShape ExtractIdentityShape(string line)
	{
		int at = line.IndexOf('@');

		if ((at <= 0) ||
			(at != line.LastIndexOf('@')))
		{
			throw new InvalidDataException();
		}

		int start = at;

		while ((start > 0) && IsIdentityCharacter(line[start - 1]))
		{
			start--;
		}

		int end = at + 1;

		while ((end < line.Length) && IsIdentityCharacter(line[end]))
		{
			end++;
		}

		string identity = line[start..end];
		string prefix = line[..start];
		string suffix = line[end..];

		if ((identity.Length < 3) ||
			(prefix.Length == 0 && suffix.Length == 0) ||
			!IsSafePublicText(prefix, IdentityAnchorWords, requireWord: false) ||
			!IsSafePunctuation(suffix))
		{
			throw new InvalidDataException();
		}

		return new IdentityShape(prefix, suffix);
	}

	private static CapacityShape ExtractCapacityShape(string line)
	{
		int meterStart = line.IndexOfAny(new[] { '█', '░' });

		if ((meterStart < 0) || ((meterStart + 50) > line.Length))
		{
			throw new InvalidDataException();
		}

		string meter = line.Substring(meterStart, 50);
		bool sawEmpty = false;

		foreach (char character in meter)
		{
			if (character == '░')
			{
				sawEmpty = true;
			}
			else if ((character != '█') || sawEmpty)
			{
				throw new InvalidDataException();
			}
		}

		int percentageStart = meterStart + 50;

		while ((percentageStart < line.Length) &&
			!IsAsciiDigit(line[percentageStart]))
		{
			percentageStart++;
		}

		if ((percentageStart >= line.Length) ||
			!TryParseFixedDecimalPercent(line[percentageStart..], 2, out _))
		{
			throw new InvalidDataException();
		}

		string prefix = line[..meterStart];
		string separator = line[(meterStart + 50)..percentageStart];

		if (!IsSafePunctuation(prefix) ||
			!IsSafePunctuation(separator) ||
			(separator.Length == 0))
		{
			throw new InvalidDataException();
		}

		return new CapacityShape(prefix, separator);
	}

	private static bool TryExtractCountdownObservation(
		string line,
		out CountdownObservation? observation)
	{
		observation = null;
		int minuteSuffix = line.Length - 1;

		if ((minuteSuffix < 0) || (line[minuteSuffix] != 'm'))
		{
			return false;
		}

		int minuteStart = minuteSuffix;

		while ((minuteStart > 0) && IsAsciiDigit(line[minuteStart - 1]))
		{
			minuteStart--;
		}

		if ((minuteStart == minuteSuffix) ||
			(minuteStart < 2) ||
			(line[minuteStart - 1] != ' '))
		{
			return false;
		}

		if (!int.TryParse(
				line.AsSpan(minuteStart, minuteSuffix - minuteStart),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int minutes) ||
			(minutes < 0) ||
			(minutes > 59))
		{
			return false;
		}

		int countdownStart = minuteStart;
		CountdownFormat format = CountdownFormat.MinutesOnly;
		int hourSuffix = minuteStart - 2;

		if ((hourSuffix >= 0) && (line[hourSuffix] == 'h'))
		{
			int hourStart = hourSuffix;

			while ((hourStart > 0) && IsAsciiDigit(line[hourStart - 1]))
			{
				hourStart--;
			}

			if ((hourStart == hourSuffix) ||
				!int.TryParse(
					line.AsSpan(hourStart, hourSuffix - hourStart),
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out int hours) ||
				(hours < 0))
			{
				return false;
			}

			countdownStart = hourStart;
			format = CountdownFormat.HoursAndMinutes;
		}

		int percentSuffix = line.LastIndexOf('%', countdownStart - 1);

		if (percentSuffix < 1)
		{
			return false;
		}

		int percentStart = percentSuffix;

		while ((percentStart > 0) && IsAsciiDigit(line[percentStart - 1]))
		{
			percentStart--;
		}

		if ((percentStart == percentSuffix) ||
			!int.TryParse(
				line.AsSpan(percentStart, percentSuffix - percentStart),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int percent) ||
			(percent < 0) ||
			(percent > 100))
		{
			return false;
		}

		string prefix = line[..percentStart];
		string middle = line[(percentSuffix + 1)..countdownStart];

		if (!IsSafePunctuation(prefix) ||
			!IsSafePublicText(
				middle,
				CountdownAnchorWords,
				requireWord: true) ||
			(middle.Length == 0))
		{
			return false;
		}

		observation = new CountdownObservation(
			new CountdownShape(prefix, middle),
			format);
		return true;
	}

	private static AvailabilityShape ExtractAvailabilityShape(string line)
	{
		int statusStart = 0;

		while ((statusStart < line.Length) &&
			!char.IsLetter(line[statusStart]))
		{
			statusStart++;
		}

		if ((statusStart >= line.Length) ||
			!string.Equals(line, line.TrimEnd(), StringComparison.Ordinal))
		{
			throw new InvalidDataException();
		}

		string prefix = line[..statusStart];
		string status = line[statusStart..];

		if (!IsSafePunctuation(prefix) ||
			!IsSafePublicText(
				status,
				AvailabilityWords,
				requireWord: true) ||
			!ContainsWord(status, "available"))
		{
			throw new InvalidDataException();
		}

		return new AvailabilityShape(prefix, status);
	}

	private static UnsignedAmountShape ExtractUnsignedAmountShape(string line)
	{
		int start = 0;

		while ((start < line.Length) && !IsAsciiDigit(line[start]))
		{
			start++;
		}

		if (start >= line.Length)
		{
			throw new InvalidDataException();
		}

		int end = start;

		while ((end < line.Length) && IsAsciiDigit(line[end]))
		{
			end++;
		}

		if ((end - start) > 128 ||
			line.AsSpan(end).ContainsAnyInRange('0', '9'))
		{
			throw new InvalidDataException();
		}

		string prefix = line[..start];
		string suffix = line[end..];

		if ((prefix.Length == 0 && suffix.Length == 0) ||
			!IsSafePublicText(
				prefix,
				AmountAnchorWords,
				requireWord: false) ||
			!IsSafePunctuation(suffix))
		{
			throw new InvalidDataException();
		}

		return new UnsignedAmountShape(prefix, suffix);
	}

	private static string ExtractOuterPublicPrefix(string line)
	{
		int length = 0;

		while ((length < line.Length) && !char.IsLetterOrDigit(line[length]))
		{
			length++;
		}

		if ((length <= 0) ||
			(length >= line.Length) ||
			!IsSafeOuterPublicPrefix(line[..length]))
		{
			throw new InvalidDataException();
		}

		return line[..length];
	}

	private static void ValidateExactBlueprintRows(
		AntigravityUsageR1PrivateScreenBundleFile bundle,
		IReadOnlyList<string> baselineLines)
	{
		HashSet<int> dynamicRows = new(
			new[] { 0, 8, 14, 15, 18, 19, 26, 27, 30, 31, 45, 49 });

		foreach (AntigravityUsageR1PrivateScreenSequence sequence in
			bundle.Sequences)
		{
			IReadOnlyList<string> observed = sequence.Pages[0].Lines;

			for (int row = 0; row < Rows; row++)
			{
				if (!dynamicRows.Contains(row) &&
					!string.Equals(
						baselineLines[row],
						NormalizeLine(observed[row]),
						StringComparison.Ordinal))
				{
					throw new InvalidDataException();
				}
			}
		}
	}

	private static void ValidatePrivateBundle(
		AntigravityUsageR1PrivateScreenBundleFile bundle)
	{
		if (!string.Equals(
				bundle.FormatVersion,
				AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
				StringComparison.Ordinal) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				bundle.CaptureContractFingerprint) ||
			(bundle.Sequences is null) ||
			(bundle.Sequences.Count < 2) ||
			(bundle.Sequences.Count > MaximumSequences))
		{
			throw new InvalidDataException();
		}

		foreach (AntigravityUsageR1PrivateScreenSequence sequence in
			bundle.Sequences)
		{
			if ((sequence is null) ||
				(sequence.Pages is null) ||
				(sequence.Pages.Count != 1))
			{
				throw new InvalidDataException();
			}

			AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];

			if ((screen is null) ||
				(screen.Columns != Columns) ||
				(screen.Rows != Rows) ||
				screen.IsAlternateScreen ||
				(screen.Lines is null) ||
				(screen.Lines.Count != Rows) ||
				screen.Lines.Any(line =>
					(line is null) ||
					(line.Length > MaximumLineLength) ||
					line.Contains('\r', StringComparison.Ordinal) ||
					line.Contains('\n', StringComparison.Ordinal)))
			{
				throw new InvalidDataException();
			}
		}
	}

	private static ReadOnlyCollection<
		AntigravityUsageR1SectionPrivateDraftReviewItem> CreateReviewItems(
		IReadOnlyList<AntigravityUsageR1SectionLineRule> lines)
	{
		AntigravityUsageR1SectionPrivateDraftReviewItem[] items = lines
			.Select(line => new AntigravityUsageR1SectionPrivateDraftReviewItem(
				line.Role switch
				{
					AntigravityUsageR1SectionLineRole.OuterChrome =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmOuterChromeShape,
					AntigravityUsageR1SectionLineRole.Identity =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmIdentityBoundary,
					AntigravityUsageR1SectionLineRole.SectionHeading =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmSectionHeading,
					AntigravityUsageR1SectionLineRole.WindowLabel =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmWindowLabel,
					AntigravityUsageR1SectionLineRole.Capacity =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmCapacityGrammar,
					AntigravityUsageR1SectionLineRole.Reset =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmResetAlternatives,
					AntigravityUsageR1SectionLineRole.NonQuotaDynamic =>
						AntigravityUsageR1SectionPrivateDraftReviewCode
							.ConfirmNonQuotaDynamicAmount,
					_ => AntigravityUsageR1SectionPrivateDraftReviewCode
						.ConfirmExactLiteral
				},
				line.RowIndex))
			.OrderBy(item => item.RowIndex)
			.ThenBy(item => item.Code)
			.ToArray();
		return Array.AsReadOnly(items);
	}

	private static string ComputeDraftFingerprint(
		AntigravityUsageR1PrivateScreenBundleFile privateBundle,
		AntigravityUsageR1SectionSpec spec,
		IReadOnlyList<AntigravityUsageR1SectionPrivateDraftReviewItem> items,
		ReadOnlySpan<byte> hmacKey)
	{
		byte[] key = hmacKey.ToArray();

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHMAC(
				HashAlgorithmName.SHA256,
				key);
			Append(hash, "format", FingerprintFormatVersion);
			Append(
				hash,
				"captureContractFingerprint",
				privateBundle.CaptureContractFingerprint);
			Append(
				hash,
				"schemaFingerprint",
				AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(spec));
			AppendCount(hash, "sequenceCount", privateBundle.Sequences.Count);

			foreach (AntigravityUsageR1PrivateScreenSequence sequence in
				privateBundle.Sequences)
			{
				AntigravityUsageR1PrivateScreen screen = sequence.Pages[0];
				AppendCount(hash, "columns", screen.Columns);
				AppendCount(hash, "rows", screen.Rows);
				Append(hash, "alternate", screen.IsAlternateScreen ? "true" : "false");

				foreach (string line in screen.Lines)
				{
					Append(hash, "line", line);
				}
			}

			AppendCount(hash, "reviewItemCount", items.Count);

			foreach (AntigravityUsageR1SectionPrivateDraftReviewItem item in items)
			{
				Append(hash, "reviewCode", item.Code.ToString());
				AppendCount(hash, "reviewRow", item.RowIndex);
			}

			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
		}
	}

	private static void Append(
		IncrementalHash hash,
		string name,
		string value)
	{
		AppendUtf8(hash, name);
		AppendUtf8(hash, value);
	}

	private static void AppendCount(
		IncrementalHash hash,
		string name,
		long value)
	{
		Append(hash, name, value.ToString(CultureInfo.InvariantCulture));
	}

	private static void AppendUtf8(IncrementalHash hash, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		Span<byte> length = stackalloc byte[sizeof(int)];
		BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
		hash.AppendData(length);

		try
		{
			hash.AppendData(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static bool TryParseFixedDecimalPercent(
		string value,
		int decimalPlaces,
		out decimal percent)
	{
		percent = 0m;

		if ((value.Length < (decimalPlaces + 3)) ||
			(value[^1] != '%'))
		{
			return false;
		}

		string numeric = value[..^1];
		int dot = numeric.IndexOf('.');

		return (dot >= 1) &&
			(dot == numeric.LastIndexOf('.')) &&
			((numeric.Length - dot - 1) == decimalPlaces) &&
			numeric.AsSpan(0, dot).IndexOfAnyExceptInRange('0', '9') < 0 &&
			numeric.AsSpan(dot + 1).IndexOfAnyExceptInRange('0', '9') < 0 &&
			decimal.TryParse(
				numeric,
				NumberStyles.AllowDecimalPoint,
				CultureInfo.InvariantCulture,
				out percent) &&
			(percent >= 0m) &&
			(percent <= 100m);
	}

	private static bool IsIdentityCharacter(char value)
	{
		return char.IsAsciiLetterOrDigit(value) ||
			".!#$%&'*+/=?^_`{|}~-@".Contains(value, StringComparison.Ordinal);
	}

	private static bool IsAsciiDigit(char value)
	{
		return (value >= '0') && (value <= '9');
	}

	private static bool IsSafePunctuation(string value)
	{
		return value.All(character =>
			!char.IsControl(character) &&
			!char.IsLetterOrDigit(character));
	}

	private static bool IsSafeOuterPublicPrefix(string value)
	{
		return (value.Length <= 128) &&
			IsSafePunctuation(value) &&
			!value.Contains('/', StringComparison.Ordinal) &&
			!value.Contains('\\', StringComparison.Ordinal) &&
			!value.Contains(':', StringComparison.Ordinal) &&
			!value.Contains('@', StringComparison.Ordinal);
	}

	private static string NormalizeLine(string value)
	{
		return value.TrimEnd(' ');
	}

	private static bool IsSafePublicText(
		string value,
		IReadOnlySet<string> allowedWords,
		bool requireWord)
	{
		if (value.Any(char.IsControl))
		{
			return false;
		}

		List<string> words = new();
		StringBuilder current = new();

		foreach (char character in value)
		{
			if (char.IsAsciiLetter(character))
			{
				current.Append(char.ToLowerInvariant(character));
			}
			else
			{
				if (char.IsLetterOrDigit(character))
				{
					return false;
				}

				if (current.Length > 0)
				{
					words.Add(current.ToString());
					current.Clear();
				}
			}
		}

		if (current.Length > 0)
		{
			words.Add(current.ToString());
		}

		return (!requireWord || words.Count > 0) &&
			words.All(allowedWords.Contains);
	}

	private static bool ContainsWord(string value, string expected)
	{
		return value
			.Split(
				new[] { ' ', '\t', '.', ',', ':', ';', '-', '_', '(', ')' },
				StringSplitOptions.RemoveEmptyEntries)
			.Any(word => string.Equals(
				word,
				expected,
				StringComparison.OrdinalIgnoreCase));
	}

	private static void ResolvePaths(
		string privateBundlePath,
		string outputPath,
		out string absoluteBundlePath,
		out string absoluteOutputPath,
		out string privateDirectory)
	{
		if (string.IsNullOrWhiteSpace(privateBundlePath) ||
			string.IsNullOrWhiteSpace(outputPath) ||
			!Path.IsPathFullyQualified(privateBundlePath) ||
			!Path.IsPathFullyQualified(outputPath) ||
			!string.Equals(
				Path.GetExtension(privateBundlePath),
				".json",
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				Path.GetExtension(outputPath),
				".json",
				StringComparison.OrdinalIgnoreCase))
		{
			throw CreateFailure(
				AntigravityUsageR1SectionPrivateDraftFailureStage.PathValidation);
		}

		absoluteBundlePath = Path.GetFullPath(privateBundlePath);
		absoluteOutputPath = Path.GetFullPath(outputPath);
		privateDirectory = Path.GetDirectoryName(absoluteBundlePath) ??
			string.Empty;
		string outputDirectory = Path.GetDirectoryName(absoluteOutputPath) ??
			string.Empty;

		if (string.Equals(
				absoluteBundlePath,
				absoluteOutputPath,
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				privateDirectory,
				outputDirectory,
				StringComparison.OrdinalIgnoreCase) ||
			!AntigravityPrivateKeyAcl.IsPrivateDirectory(privateDirectory) ||
			!File.Exists(absoluteBundlePath) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteBundlePath))
		{
			throw CreateFailure(
				AntigravityUsageR1SectionPrivateDraftFailureStage.PathValidation);
		}

		if (File.Exists(absoluteOutputPath) &&
			!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath))
		{
			throw CreateFailure(
				AntigravityUsageR1SectionPrivateDraftFailureStage.PathValidation);
		}
	}

	private static async Task WritePrivateFileAsync(
		string path,
		ReadOnlyMemory<byte> bytes,
		CancellationToken cancellationToken)
	{
		await using (FileStream stream = new(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough))
		{
			await stream.FlushAsync(cancellationToken);
			stream.Flush(flushToDisk: true);
		}

		if (!AntigravityPrivateKeyAcl.TryProtectNewFile(path) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(path))
		{
			throw new IOException();
		}

		await using FileStream writeStream = new(
			path,
			FileMode.Open,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.Asynchronous | FileOptions.WriteThrough);
		await writeStream.WriteAsync(bytes, cancellationToken);
		await writeStream.FlushAsync(cancellationToken);
		writeStream.Flush(flushToDisk: true);
	}

	private static async Task<bool> HasExactPrivateDraftAsync(
		string path,
		ReadOnlyMemory<byte> expected,
		CancellationToken cancellationToken)
	{
		byte[]? observed = null;

		try
		{
			observed = await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
				path,
				MaximumDraftBytes,
				requirePrivateFile: true,
				cancellationToken);
			_ = AntigravityUsageR1SectionPrivateDraftJson.Deserialize(observed);
			return observed.AsSpan().SequenceEqual(expected.Span);
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
			ZeroBytes(observed);
		}
	}

	private static AntigravityUsageR1SectionPrivateDraftMetadata CreateMetadata(
		AntigravityUsageR1SectionPrivateDraftFile draft,
		int observationCount,
		bool wasWritten)
	{
		return new AntigravityUsageR1SectionPrivateDraftMetadata(
			wasWritten,
			observationCount,
			draft.ReviewItems.Count,
			draft.DraftFingerprint);
	}

	private static AntigravityUsageR1SectionPrivateDraftException CreateFailure(
		AntigravityUsageR1SectionPrivateDraftFailureStage stage,
		AntigravityUsageR1SectionPrivateDraftExtractionFailureCode?
			extractionFailureCode = null,
		int? rowIndex = null,
		int? observationIndex = null)
	{
		return new AntigravityUsageR1SectionPrivateDraftException(
			stage,
			extractionFailureCode,
			rowIndex,
			observationIndex);
	}

	private static void TryDeletePrivateSibling(string? path)
	{
		try
		{
			if ((path is not null) &&
				File.Exists(path) &&
				((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0))
			{
				File.Delete(path);
			}
		}
		catch
		{
			// Cleanup must never mask a fixed-stage failure.
		}
	}

	private static void ZeroBytes(byte[]? bytes)
	{
		if (bytes is not null)
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}

internal static class AntigravityUsageR1SectionPrivateDraftJson
{
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = false,
		CommentHandling = JsonCommentHandling.Disallow,
		MaxDepth = 64
	};
	private static readonly JsonSerializerOptions Options = new()
	{
		AllowTrailingCommas = false,
		MaxDepth = 64,
		PropertyNameCaseInsensitive = false,
		ReadCommentHandling = JsonCommentHandling.Disallow,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static byte[] Serialize(
		AntigravityUsageR1SectionPrivateDraftFile draft)
	{
		Validate(draft);
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(draft, Options);

		try
		{
			AntigravityUsageR1SectionPrivateDraftFile roundTrip =
				Deserialize(bytes);

			if (!string.Equals(
					draft.DraftFingerprint,
					roundTrip.DraftFingerprint,
					StringComparison.Ordinal))
			{
				throw new JsonException();
			}

			return bytes;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(bytes);
			throw;
		}
	}

	internal static AntigravityUsageR1SectionPrivateDraftFile Deserialize(
		ReadOnlyMemory<byte> bytes)
	{
		using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
		JsonElement root = document.RootElement;
		RequireNoDuplicateProperties(root);
		RequireExactObject(
			root,
			"FormatVersion",
			"DraftKind",
			"ReviewState",
			"Spec",
			"ReviewItems",
			"DraftFingerprint");
		AntigravityUsageR1SectionPrivateDraftFile draft =
			JsonSerializer.Deserialize<AntigravityUsageR1SectionPrivateDraftFile>(
				bytes.Span,
				Options) ?? throw new JsonException();
		Validate(draft);
		return draft;
	}

	private static void RequireNoDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> propertyNames = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!propertyNames.Add(property.Name))
				{
					throw new JsonException();
				}

				RequireNoDuplicateProperties(property.Value);
			}

			return;
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement item in element.EnumerateArray())
		{
			RequireNoDuplicateProperties(item);
		}
	}

	private static void Validate(
		AntigravityUsageR1SectionPrivateDraftFile draft)
	{
		ArgumentNullException.ThrowIfNull(draft);

		if (!string.Equals(
				draft.FormatVersion,
				AntigravityUsageR1SectionPrivateDraftGenerator.FormatVersion,
				StringComparison.Ordinal) ||
			(draft.DraftKind !=
				AntigravityUsageR1SectionPrivateDraftKind.MachineGenerated) ||
			(draft.ReviewState != AntigravityUsageR1ReviewState.NeedsReview) ||
			(draft.ReviewItems is null) ||
			(draft.ReviewItems.Count != 50) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				draft.DraftFingerprint))
		{
			throw new JsonException();
		}

		_ = AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
			draft.Spec);
		HashSet<int> rows = new();

		foreach (AntigravityUsageR1SectionPrivateDraftReviewItem item in
			draft.ReviewItems)
		{
			if ((item is null) ||
				!Enum.IsDefined(item.Code) ||
				(item.RowIndex < 0) ||
				(item.RowIndex >= 50) ||
				!rows.Add(item.RowIndex))
			{
				throw new JsonException();
			}
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
}
