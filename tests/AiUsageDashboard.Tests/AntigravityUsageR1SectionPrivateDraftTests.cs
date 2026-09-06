using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1SectionPrivateDraftTests
{
	private const string FirstIdentity =
		"private-user-a@example.invalid";
	private const string SecondIdentity =
		"private-user-b@example.invalid";
	private const string FirstOuterTop =
		@"C:\private\session-a";
	private const string SecondOuterTop =
		@"C:\private\session-b";
	private const string FirstOuterBottom = "private-status-a";
	private const string SecondOuterBottom = "private-status-b";
	private static readonly string CaptureContractFingerprint = new('A', 64);
	private static readonly byte[] FingerprintKey = Enumerable
		.Range(1, 32)
		.Select(value => checked((byte)value))
		.ToArray();
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithReviewedBlueprint_WritesDeterministicSafeNeedsReviewDraft()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			weeklyPercent: 80,
			countdownMinutes: 150,
			credit: 123);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			weeklyPercent: 78,
			countdownMinutes: 149,
			credit: 124);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));
		string outputPath = Path.Combine(privateDirectory, "draft.json");

		AntigravityUsageR1SectionPrivateDraftMetadata firstResult =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					outputPath,
					CaptureContractFingerprint,
					FingerprintKey);
		byte[] firstBytes = await File.ReadAllBytesAsync(outputPath);
		DateTime fixedTimestamp = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(outputPath, fixedTimestamp);
		AntigravityUsageR1SectionPrivateDraftMetadata secondResult =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					outputPath,
					CaptureContractFingerprint,
					FingerprintKey);
		byte[] secondBytes = await File.ReadAllBytesAsync(outputPath);
		AntigravityUsageR1SectionPrivateDraftFile draft =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(firstBytes);
		string rendered = Encoding.UTF8.GetString(firstBytes);

		Assert.True(firstResult.WasWritten);
		Assert.False(secondResult.WasWritten);
		Assert.Equal(2, firstResult.ObservationCount);
		Assert.Equal(50, firstResult.ReviewItemCount);
		Assert.Equal(firstResult.DraftFingerprint, secondResult.DraftFingerprint);
		Assert.Equal(firstBytes, secondBytes);
		Assert.Equal(fixedTimestamp, File.GetLastWriteTimeUtc(outputPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(outputPath));
		Assert.Equal(
			AntigravityUsageR1SectionPrivateDraftKind.MachineGenerated,
			draft.DraftKind);
		Assert.Equal(AntigravityUsageR1ReviewState.NeedsReview, draft.ReviewState);
		Assert.Matches("^[0-9A-F]{64}$", draft.DraftFingerprint);
		Assert.Equal(Enumerable.Range(0, 50),
			draft.ReviewItems.Select(item => item.RowIndex));
		Assert.Equal(
			AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible,
			draft.Spec.PaginationMode);
		Assert.Equal(120, draft.Spec.Columns);
		Assert.Equal(50, draft.Spec.Rows);
		Assert.False(draft.Spec.IsAlternateScreen);
		_ = AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
			draft.Spec);

		AssertDynamicLineUsesToken<AntigravityUsageR1SectionOuterLineShapeToken>(
			draft,
			0);
		AssertDynamicLineUsesToken<AntigravityUsageR1SectionIdentityToken>(
			draft,
			8);
		AssertDynamicLineUsesToken<AntigravityUsageR1SectionUnsignedAmountToken>(
			draft,
			45);
		Assert.DoesNotContain(FirstIdentity, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondIdentity, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain(FirstOuterTop, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondOuterTop, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain(FirstOuterBottom, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondOuterBottom, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("80.00%", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("78.00%", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("80% remaining", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("78% remaining", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("2h 30m", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("2h 29m", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("  123", rendered, StringComparison.Ordinal);
		Assert.DoesNotContain("  124", rendered, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithObservedCountdownFormatsAndAvailability_WritesExactAlternativesIdempotently()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			weeklyPercent: 80,
			countdownMinutes: 150,
			credit: 123);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			weeklyPercent: 78,
			countdownMinutes: 59,
			credit: 124);
		second = ReplaceLine(
			second,
			15,
			CreateMinuteOnlyCountdown(78, 59));
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));
		string outputPath = Path.Combine(privateDirectory, "draft.json");

		AntigravityUsageR1SectionPrivateDraftMetadata firstResult =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					outputPath,
					CaptureContractFingerprint,
					FingerprintKey);
		byte[] firstBytes = await File.ReadAllBytesAsync(outputPath);
		AntigravityUsageR1SectionPrivateDraftMetadata secondResult =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					outputPath,
					CaptureContractFingerprint,
					FingerprintKey);
		byte[] secondBytes = await File.ReadAllBytesAsync(outputPath);
		AntigravityUsageR1SectionPrivateDraftFile draft =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(firstBytes);
		AntigravityUsageR1SectionLineRule resetLine = draft.Spec.Lines
			.Single(line => line.RowIndex == 15);
		AntigravityUsageR1SectionLinePattern[] countdownAlternatives =
			resetLine.Alternatives
				.Where(pattern => pattern.Tokens.Any(token =>
					token is AntigravityUsageR1SectionCountdownToken))
				.ToArray();
		AntigravityUsageR1SectionParseResult firstParsed =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(first),
				draft.Spec);
		AntigravityUsageR1SectionParseResult secondParsed =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(second),
				draft.Spec);

		Assert.True(firstResult.WasWritten);
		Assert.False(secondResult.WasWritten);
		Assert.Equal(firstResult.DraftFingerprint, secondResult.DraftFingerprint);
		Assert.Equal(firstBytes, secondBytes);
		Assert.Equal(3, resetLine.Alternatives.Count);
		Assert.Collection(
			countdownAlternatives,
			alternative => Assert.Equal(
				new[]
				{
					AntigravityUsageR1SectionCountdownUnit.Hours,
					AntigravityUsageR1SectionCountdownUnit.Minutes
				},
				alternative.Tokens
					.OfType<AntigravityUsageR1SectionCountdownToken>()
					.Single()
					.Rule.Units
					.Select(unit => unit.Unit)),
			alternative => Assert.Equal(
				new[] { AntigravityUsageR1SectionCountdownUnit.Minutes },
				alternative.Tokens
					.OfType<AntigravityUsageR1SectionCountdownToken>()
					.Single()
					.Rule.Units
					.Select(unit => unit.Unit)));
		Assert.Contains(
			resetLine.Alternatives,
			alternative => alternative.Tokens.Any(token =>
				token is AntigravityUsageR1SectionAvailabilityToken));
		Assert.Equal(
			TimeSpan.FromMinutes(150),
			firstParsed.Page.Sections[0].Windows[0].Reset.ResetsIn);
		Assert.Equal(
			TimeSpan.FromMinutes(59),
			secondParsed.Page.Sections[0].Windows[0].Reset.ResetsIn);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithOnlyHoursAndMinutes_DoesNotAllowUnobservedMinutesOnly()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			80,
			150,
			123);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			78,
			149,
			124);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));
		string outputPath = Path.Combine(privateDirectory, "draft.json");
		await AntigravityUsageR1SectionPrivateDraftGenerator
			.GenerateFromPrivateBundleAsync(
				bundlePath,
				outputPath,
				CaptureContractFingerprint,
				FingerprintKey);
		AntigravityUsageR1SectionPrivateDraftFile draft =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				await File.ReadAllBytesAsync(outputPath));
		AntigravityUsageR1SectionLineRule resetLine = draft.Spec.Lines
			.Single(line => line.RowIndex == 15);
		AntigravityUsageR1PrivateScreen unobserved = ReplaceLine(
			first,
			15,
			CreateMinuteOnlyCountdown(80, 59));

		Assert.Equal(2, resetLine.Alternatives.Count);
		Assert.Single(
			resetLine.Alternatives,
			alternative =>
				alternative.Tokens.Any(token =>
					token is AntigravityUsageR1SectionCountdownToken));
		Assert.Throws<InvalidDataException>(() =>
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(unobserved),
				draft.Spec));
	}

	[Theory]
	[InlineData("60m")]
	[InlineData("1d 2h 30m")]
	public async Task GenerateFromPrivateBundleAsync_WithMalformedOrUnknownCountdown_FailsClosed(
		string renderedDuration)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			80,
			150,
			123);
		first = ReplaceLine(
			first,
			15,
			ReplaceCountdownDuration(
				CreateCountdown(80, 150),
				renderedDuration));
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			78,
			149,
			124);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(async () =>
					await AntigravityUsageR1SectionPrivateDraftGenerator
						.GenerateFromPrivateBundleAsync(
							bundlePath,
							Path.Combine(privateDirectory, "draft.json"),
							CaptureContractFingerprint,
							FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage.DraftExtraction);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithoutAvailabilityEvidence_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = RemoveAvailabilityEvidence(
			CreateScreen(
				FirstIdentity,
				FirstOuterTop,
				FirstOuterBottom,
				80,
				150,
				123),
			80,
			minutesOnly: false);
		AntigravityUsageR1PrivateScreen second = RemoveAvailabilityEvidence(
			CreateScreen(
				SecondIdentity,
				SecondOuterTop,
				SecondOuterBottom,
				78,
				59,
				124),
			78,
			minutesOnly: true);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(async () =>
					await AntigravityUsageR1SectionPrivateDraftGenerator
						.GenerateFromPrivateBundleAsync(
							bundlePath,
							Path.Combine(privateDirectory, "draft.json"),
							CaptureContractFingerprint,
							FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage.DraftExtraction);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithDifferentHmacKey_ChangesOnlyPrivateCommitment()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string firstPath = Path.Combine(privateDirectory, "first-draft.json");
		string secondPath = Path.Combine(privateDirectory, "second-draft.json");
		byte[] secondKey = FingerprintKey.Select(value => (byte)(value ^ 0x5A))
			.ToArray();

		await AntigravityUsageR1SectionPrivateDraftGenerator
			.GenerateFromPrivateBundleAsync(
				bundlePath,
				firstPath,
				CaptureContractFingerprint,
				FingerprintKey);
		await AntigravityUsageR1SectionPrivateDraftGenerator
			.GenerateFromPrivateBundleAsync(
				bundlePath,
				secondPath,
				CaptureContractFingerprint,
				secondKey);
		AntigravityUsageR1SectionPrivateDraftFile first =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				await File.ReadAllBytesAsync(firstPath));
		AntigravityUsageR1SectionPrivateDraftFile second =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				await File.ReadAllBytesAsync(secondPath));

		Assert.NotEqual(first.DraftFingerprint, second.DraftFingerprint);
		Assert.Equal(
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(first.Spec),
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(second.Spec));
		Assert.Equal(first.ReviewItems, second.ReviewItems);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WhenCaptureContractDiffers_FailsClosedBeforeDraftExtraction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string outputPath = Path.Combine(privateDirectory, "draft.json");

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(
					async () => await AntigravityUsageR1SectionPrivateDraftGenerator
						.GenerateFromPrivateBundleAsync(
							bundlePath,
							outputPath,
							new string('B', 64),
							FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage
				.PrivateBundleFormat);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WhenBlueprintExactRowDrifts_FailsClosedWithoutPrivateValue()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			80,
			150,
			123);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			78,
			149,
			124);
		string privateDrift = "PRIVATE-EXACT-DRIFT";
		second = ReplaceLine(second, 34, privateDrift);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(
				async () => await AntigravityUsageR1SectionPrivateDraftGenerator
					.GenerateFromPrivateBundleAsync(
						bundlePath,
						Path.Combine(privateDirectory, "draft.json"),
						CaptureContractFingerprint,
						FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage.DraftExtraction);
		Assert.DoesNotContain(privateDrift, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WhenDynamicGrammarIsInvalid_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			FirstIdentity,
			FirstOuterTop,
			FirstOuterBottom,
			80,
			150,
			123);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			SecondIdentity,
			SecondOuterTop,
			SecondOuterBottom,
			78,
			149,
			124);
		second = ReplaceLine(
			second,
			14,
			$"  {new string('█', 49)} {new string('░', 1)} 78.00%");
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(first, second));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(
				async () => await AntigravityUsageR1SectionPrivateDraftGenerator
					.GenerateFromPrivateBundleAsync(
						bundlePath,
						Path.Combine(privateDirectory, "draft.json"),
						CaptureContractFingerprint,
						FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage.DraftExtraction);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WithConflictingExistingPrivateFile_PreservesItAndFailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string outputPath = Path.Combine(privateDirectory, "draft.json");
		byte[] conflict = Encoding.UTF8.GetBytes("{}");
		await File.WriteAllBytesAsync(outputPath, conflict);
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(outputPath));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(
				async () => await AntigravityUsageR1SectionPrivateDraftGenerator
					.GenerateFromPrivateBundleAsync(
						bundlePath,
						outputPath,
						CaptureContractFingerprint,
						FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage
				.ExistingOutputValidation);
		Assert.Equal(conflict, await File.ReadAllBytesAsync(outputPath));
		Assert.Empty(Directory.EnumerateFiles(
			privateDirectory,
			"*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task GeneratedSpec_WhenStillNeedsReview_IsRejectedByCompilerGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string draftPath = Path.Combine(privateDirectory, "draft.json");
		await AntigravityUsageR1SectionPrivateDraftGenerator
			.GenerateFromPrivateBundleAsync(
				bundlePath,
				draftPath,
				CaptureContractFingerprint,
				FingerprintKey);
		AntigravityUsageR1SectionPrivateDraftFile draft =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				await File.ReadAllBytesAsync(draftPath));
		_ = AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
			draft.Spec);
		AntigravityUsageR1SectionSpecFile needsReviewSpec = new(
			AntigravityUsageR1SectionSchemaParser.ReviewedSpecFormatVersion,
			AntigravityUsageR1ReviewState.NeedsReview,
			draft.Spec);
		string needsReviewPath = Path.Combine(
			temporaryDirectory.Path,
			"needs-review-spec.json");
		await File.WriteAllTextAsync(
			needsReviewPath,
			JsonSerializer.Serialize(needsReviewSpec, JsonOptions),
			CreateUtf8Encoding());

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1SectionCalibrationCompiler
					.CompileFromFilesAsync(bundlePath, needsReviewPath));

		Assert.Equal(
			AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat,
			exception.Stage);
	}

	[Fact]
	public async Task GenerateFromPrivateBundleAsync_WhenOutputLeavesPrivateDirectory_RejectsPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));

		AntigravityUsageR1SectionPrivateDraftException exception =
			await Assert.ThrowsAsync<
				AntigravityUsageR1SectionPrivateDraftException>(
				async () => await AntigravityUsageR1SectionPrivateDraftGenerator
					.GenerateFromPrivateBundleAsync(
						bundlePath,
						Path.Combine(temporaryDirectory.Path, "draft.json"),
						CaptureContractFingerprint,
						FingerprintKey));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1SectionPrivateDraftFailureStage.PathValidation);
	}

	[Fact]
	public async Task PromoteAsync_WithExactApprovedDraft_WritesReviewedSpecIdempotently()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string draftPath = Path.Combine(privateDirectory, "draft.json");
		AntigravityUsageR1SectionPrivateDraftMetadata draft =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					draftPath,
					CaptureContractFingerprint,
					FingerprintKey);
		string outputPath = Path.Combine(privateDirectory, "reviewed.json");

		AntigravityUsageR1SectionPromotionMetadata first =
			await AntigravityUsageR1SectionPromotion.PromoteAsync(
				bundlePath,
				draftPath,
				outputPath,
				draft.DraftFingerprint,
				FingerprintKey);
		DateTime fixedTimestamp = new(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(outputPath, fixedTimestamp);
		AntigravityUsageR1SectionPromotionMetadata second =
			await AntigravityUsageR1SectionPromotion.PromoteAsync(
				bundlePath,
				draftPath,
				outputPath,
				draft.DraftFingerprint,
				FingerprintKey);
		AntigravityUsageR1SectionSpecFile reviewed =
			AntigravityUsageR1SectionCalibrationJson.DeserializeReviewedSpec(
				await File.ReadAllBytesAsync(outputPath));

		Assert.True(first.WasWritten);
		Assert.False(second.WasWritten);
		Assert.Equal(first.SchemaFingerprint, second.SchemaFingerprint);
		Assert.Equal(AntigravityUsageR1ReviewState.Reviewed, reviewed.ReviewState);
		Assert.Equal(fixedTimestamp, File.GetLastWriteTimeUtc(outputPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(outputPath));
	}

	[Fact]
	public async Task PromoteAsync_WhenSpecChangesButFingerprintIsRetained_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string draftPath = Path.Combine(privateDirectory, "draft.json");
		AntigravityUsageR1SectionPrivateDraftMetadata metadata =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					draftPath,
					CaptureContractFingerprint,
					FingerprintKey);
		AntigravityUsageR1SectionPrivateDraftFile draft =
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				await File.ReadAllBytesAsync(draftPath));
		draft = draft with
		{
			Spec = draft.Spec with { LayoutId = "tampered-layout" }
		};
		await File.WriteAllBytesAsync(
			draftPath,
			AntigravityUsageR1SectionPrivateDraftJson.Serialize(draft));
		string outputPath = Path.Combine(privateDirectory, "reviewed.json");

		await Assert.ThrowsAsync<InvalidDataException>(async () =>
			await AntigravityUsageR1SectionPromotion.PromoteAsync(
				bundlePath,
				draftPath,
				outputPath,
				metadata.DraftFingerprint,
				FingerprintKey));

		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task PromoteAsync_WithWrongHmacKey_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string draftPath = Path.Combine(privateDirectory, "draft.json");
		AntigravityUsageR1SectionPrivateDraftMetadata metadata =
			await AntigravityUsageR1SectionPrivateDraftGenerator
				.GenerateFromPrivateBundleAsync(
					bundlePath,
					draftPath,
					CaptureContractFingerprint,
					FingerprintKey);
		byte[] wrongKey = FingerprintKey.Select(value => (byte)(value ^ 0xA5))
			.ToArray();
		string outputPath = Path.Combine(privateDirectory, "reviewed.json");

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
				await AntigravityUsageR1SectionPromotion.PromoteAsync(
					bundlePath,
					draftPath,
					outputPath,
					metadata.DraftFingerprint,
					wrongKey));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(wrongKey);
		}

		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Deserialize_WhenNestedKnownPropertyIsDuplicated_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			CreateBundle(
				CreateScreen(
					FirstIdentity,
					FirstOuterTop,
					FirstOuterBottom,
					80,
					150,
					123),
				CreateScreen(
					SecondIdentity,
					SecondOuterTop,
					SecondOuterBottom,
					78,
					149,
					124)));
		string draftPath = Path.Combine(privateDirectory, "draft.json");
		await AntigravityUsageR1SectionPrivateDraftGenerator
			.GenerateFromPrivateBundleAsync(
				bundlePath,
				draftPath,
				CaptureContractFingerprint,
				FingerprintKey);
		string json = await File.ReadAllTextAsync(draftPath);
		const string Property =
			"\"LayoutId\": \"agy-usage-r1-120x50-primary-section-v1\"";
		json = json.Replace(
			Property,
			$"{Property},{Environment.NewLine}    {Property}",
			StringComparison.Ordinal);

		Assert.Throws<JsonException>(() =>
			AntigravityUsageR1SectionPrivateDraftJson.Deserialize(
				Encoding.UTF8.GetBytes(json)));
	}

	private static void AssertDynamicLineUsesToken<TToken>(
		AntigravityUsageR1SectionPrivateDraftFile draft,
		int row)
		where TToken : AntigravityUsageR1SectionToken
	{
		Assert.Contains(
			draft.Spec.Lines.Single(line => line.RowIndex == row)
				.Alternatives.SelectMany(pattern => pattern.Tokens),
			token => token is TToken);
	}

	private static void AssertFixedFailure(
		AntigravityUsageR1SectionPrivateDraftException exception,
		AntigravityUsageR1SectionPrivateDraftFailureStage expectedStage)
	{
		Assert.Equal(expectedStage, exception.Stage);
		Assert.Equal(
			$"The private AGY R1 section draft operation failed at stage: {expectedStage}.",
			exception.Message);
	}

	private static AntigravityUsageR1PrivateScreenBundleFile CreateBundle(
		params AntigravityUsageR1PrivateScreen[] screens)
	{
		return new AntigravityUsageR1PrivateScreenBundleFile(
			AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
			CaptureContractFingerprint,
			Array.AsReadOnly(screens
				.Select(screen => new AntigravityUsageR1PrivateScreenSequence(
					Array.AsReadOnly(new[] { screen })))
				.ToArray()));
	}

	private static AntigravityUsageR1PrivateScreen CreateScreen(
		string identity,
		string outerTop,
		string outerBottom,
		int weeklyPercent,
		int countdownMinutes,
		int credit)
	{
		string[] lines = Enumerable.Repeat(string.Empty, 50).ToArray();
		lines[0] = $"╭─ {outerTop}";
		lines[1] = "Synthetic session chrome";
		lines[3] = new string('─', 60);
		lines[4] = ">";
		lines[5] = new string('─', 40);
		lines[6] = "Synthetic quotas";
		lines[8] = $"  Account: {identity}";
		lines[10] = "  Synthetic Gemini";
		lines[11] = "  Synthetic Gemini family 3";
		lines[13] = "    Weekly quota";
		lines[14] = CreateCapacity(weeklyPercent);
		lines[15] = CreateCountdown(weeklyPercent, countdownMinutes);
		lines[17] = "    Rolling 5-hour quota";
		lines[18] = CreateCapacity(70);
		lines[19] = "  Available";
		lines[22] = "  Synthetic Claude";
		lines[23] = "  Synthetic Claude family";
		lines[25] = "    Weekly quota";
		lines[26] = CreateCapacity(60);
		lines[27] = "  Available";
		lines[29] = "    Rolling 5-hour quota";
		lines[30] = CreateCapacity(50);
		lines[31] = "  Available";
		lines[34] = "  Synthetic informational callout";
		lines[35] = "  This is fixed public help text.";
		lines[36] = "  It contains no captured account data.";
		lines[37] = "  Review the quota grammar before use.";
		lines[38] = "  Machine drafts are not reviewed specs.";
		lines[39] = "  End of fixed callout.";
		lines[41] = "  Model Credits";
		lines[42] = "  Available model credit balance";
		lines[44] = "  Credits";
		lines[45] = $"  {credit}";
		lines[48] = "  Synthetic navigation help";
		lines[49] = $"╰─ {outerBottom}";
		return new AntigravityUsageR1PrivateScreen(
			120,
			50,
			false,
			Array.AsReadOnly(lines));
	}

	private static string CreateCapacity(int remainingPercent)
	{
		int filled = remainingPercent / 2;
		return $"  {new string('█', filled)}{new string('░', 50 - filled)} {remainingPercent}.00%";
	}

	private static string CreateCountdown(
		int remainingPercent,
		int countdownMinutes)
	{
		int hours = countdownMinutes / 60;
		int minutes = countdownMinutes % 60;
		return $"  {remainingPercent}% remaining · refreshes in {hours}h {minutes}m";
	}

	private static string CreateMinuteOnlyCountdown(
		int remainingPercent,
		int countdownMinutes)
	{
		if ((countdownMinutes < 0) || (countdownMinutes > 59))
		{
			throw new ArgumentOutOfRangeException(nameof(countdownMinutes));
		}

		string rendered = CreateCountdown(
			remainingPercent,
			countdownMinutes);
		int zeroHours = rendered.LastIndexOf("0h ", StringComparison.Ordinal);

		if (zeroHours < 0)
		{
			throw new InvalidOperationException();
		}

		return rendered.Remove(zeroHours, 3);
	}

	private static string ReplaceCountdownDuration(
		string rendered,
		string replacement)
	{
		const string ReferenceDuration = "2h 30m";

		if (!rendered.EndsWith(ReferenceDuration, StringComparison.Ordinal))
		{
			throw new InvalidOperationException();
		}

		return rendered[..(rendered.Length - ReferenceDuration.Length)] +
			replacement;
	}

	private static AntigravityUsageR1PrivateScreen RemoveAvailabilityEvidence(
		AntigravityUsageR1PrivateScreen screen,
		int weeklyPercent,
		bool minutesOnly)
	{
		(int Row, int Percent, int Minutes)[] resets =
		{
			(15, weeklyPercent, minutesOnly ? 59 : 150),
			(19, 70, minutesOnly ? 58 : 120),
			(27, 60, minutesOnly ? 57 : 100),
			(31, 50, minutesOnly ? 56 : 60)
		};

		foreach ((int row, int percent, int minutes) in resets)
		{
			screen = ReplaceLine(
				screen,
				row,
				minutesOnly
					? CreateMinuteOnlyCountdown(percent, minutes)
					: CreateCountdown(percent, minutes));
		}

		return screen;
	}

	private static TerminalScreenSnapshot CreateSnapshot(
		AntigravityUsageR1PrivateScreen screen)
	{
		return new TerminalScreenSnapshot(
			screen.Columns,
			screen.Rows,
			screen.IsAlternateScreen,
			screen.Lines.ToArray());
	}

	private static AntigravityUsageR1PrivateScreen ReplaceLine(
		AntigravityUsageR1PrivateScreen screen,
		int row,
		string value)
	{
		string[] lines = screen.Lines.ToArray();
		lines[row] = value;
		return screen with { Lines = Array.AsReadOnly(lines) };
	}

	private static string CreatePrivateDirectory(
		TemporaryDirectory temporaryDirectory)
	{
		string privateDirectory = Path.Combine(
			temporaryDirectory.Path,
			"private");
		Assert.True(
			AntigravityPrivateKeyAcl.TryPrepareDirectory(
				privateDirectory,
				out _,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason),
			failureReason.ToString());
		return privateDirectory;
	}

	private static async Task<string> WritePrivateBundleAsync(
		string privateDirectory,
		AntigravityUsageR1PrivateScreenBundleFile bundle)
	{
		string path = Path.Combine(privateDirectory, "bundle.json");
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(bundle, JsonOptions),
			CreateUtf8Encoding());
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
		return path;
	}

	private static UTF8Encoding CreateUtf8Encoding()
	{
		return new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
	}
}
