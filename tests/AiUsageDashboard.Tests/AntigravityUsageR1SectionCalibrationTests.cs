using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1SectionCalibrationTests
{
	private const int Columns = 80;
	private const int Rows = 5;
	private const string FirstIdentity =
		"PRIVATE-SECTION-FIRST@example.invalid";
	private const string SecondIdentity =
		"PRIVATE-SECTION-SECOND@example.invalid";
	private static readonly string ApprovedDraftFingerprint = new('B', 64);
	private static readonly string CaptureContractFingerprint = new('A', 64);
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
	public async Task CompileFromFilesAsync_WithQuotaChange_ReturnsSafeLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(FirstIdentity, 6)));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1SectionLayoutFile layoutFile =
			await AntigravityUsageR1SectionCalibrationCompiler
				.CompileFromFilesAsync(bundlePath, specPath);
		string outputPath = Path.Combine(
			temporaryDirectory.Path,
			"layout.json");

		Assert.True(await AntigravityUsageR1SectionLayoutWriter.TryWriteAsync(
			outputPath,
			layoutFile));
		byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
		string output = Encoding.UTF8.GetString(outputBytes);
		AntigravityUsageR1SectionLayoutFile deserializedLayout =
			AntigravityUsageR1SectionCalibrationJson.DeserializeLayout(outputBytes);

		Assert.Equal(
			AntigravityUsageR1ReviewState.Reviewed,
			layoutFile.ReviewState);
		Assert.Single(layoutFile.ExpectedPageFingerprints);
		Assert.Matches("^[0-9A-F]{64}$", layoutFile.SchemaFingerprint);
		Assert.Equal(
			CaptureContractFingerprint,
			layoutFile.CaptureContractFingerprint);
		Assert.Equal(
			ApprovedDraftFingerprint,
			layoutFile.ApprovedDraftFingerprint);
		Assert.DoesNotContain(
			FirstIdentity,
			output,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("50%", output, StringComparison.Ordinal);
		Assert.DoesNotContain("60%", output, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AccountIdentity",
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"RemainingPercent",
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ResetsIn",
			output,
			StringComparison.Ordinal);
		_ = layoutFile.ToLayout();
		_ = deserializedLayout.ToLayout();
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithCaptureContractMismatch_RejectsSpec()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(FirstIdentity, 6)));
		AntigravityUsageR1SectionSpecFile mismatchedSpec =
			CreateReviewedSpecFile(CreateSpec()) with
			{
				CaptureContractFingerprint = new string('C', 64)
			};
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			mismatchedSpec);

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1SectionCalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithIdentityOnlyChange_RejectsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(SecondIdentity, 5)));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1SectionCalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.DynamicEvidence);
		Assert.DoesNotContain(
			FirstIdentity,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			SecondIdentity,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithNeedsReviewSpec_RejectsPromotion()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(FirstIdentity, 6)));
		AntigravityUsageR1SectionSpecFile draft =
			CreateReviewedSpecFile(CreateSpec()) with
			{
				ReviewState = AntigravityUsageR1ReviewState.NeedsReview
			};
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"needs-review-spec.json",
			draft);

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1SectionCalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithNonStrictSpec_RejectsEveryVariant()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(FirstIdentity, 6)));
		string validJson = JsonSerializer.Serialize(
			CreateReviewedSpecFile(spec),
			JsonOptions);
		string schemaProperty = "\"SchemaVersion\": 2,";
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				"\"LayoutId\": \"section-calibration-test\"," ,
				"\"LayoutId\": \"section-calibration-test\",\n    \"Unexpected\": true,"),
			ReplaceFirst(
				validJson,
				"\"RequireFullyVisible\"",
				"\"requireFullyVisible\""),
			ReplaceFirst(validJson, "\"Identity\"", "\"identity\""),
			ReplaceFirst(
				validJson,
				schemaProperty,
				$"{schemaProperty}\n    {schemaProperty}"),
			ReplaceFirst(validJson, "\"Columns\": 80", "\"Columns\": \"80\""),
			ReplaceFirst(
				validJson,
				"\"Width\": 10,",
				"\"Width\": 10,\n                \"Unexpected\": true,")
		};

		for (int index = 0; index < invalidJsonCases.Length; index++)
		{
			string specPath = Path.Combine(
				temporaryDirectory.Path,
				$"invalid-spec-{index}.json");
			await File.WriteAllTextAsync(
				specPath,
				invalidJsonCases[index],
				CreateUtf8Encoding());
			AntigravityUsageR1CalibrationException exception =
				await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
					async () => await AntigravityUsageR1SectionCalibrationCompiler
						.CompileFromFilesAsync(bundlePath, specPath));

			AssertFixedFailure(
				exception,
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}
	}

	[Fact]
	public void DeserializeReviewedSpec_WithAllTokenKinds_AcceptsExactShapes()
	{
		AntigravityUsageR1SectionSpecFile expected = CreateAllTokenSpecFile();
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions);

		AntigravityUsageR1SectionSpecFile actual =
			AntigravityUsageR1SectionCalibrationJson
				.DeserializeReviewedSpec(bytes);

		Assert.Equal(10, actual.Spec.Lines[0].Alternatives[0].Tokens.Count);
		Assert.IsType<AntigravityUsageR1SectionLiteralToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[0]);
		Assert.IsType<AntigravityUsageR1SectionIdentityToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[1]);
		Assert.IsType<AntigravityUsageR1SectionPercentToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[2]);
		Assert.IsType<AntigravityUsageR1SectionCountdownToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[3]);
		Assert.IsType<AntigravityUsageR1SectionAvailabilityToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[4]);
		Assert.IsType<AntigravityUsageR1SectionProgressMeterToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[5]);
		Assert.IsType<AntigravityUsageR1SectionPaginationNumberToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[6]);
		Assert.IsType<AntigravityUsageR1SectionOpaqueToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[7]);
		Assert.IsType<AntigravityUsageR1SectionUnsignedAmountToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[8]);
		Assert.IsType<AntigravityUsageR1SectionOuterLineShapeToken>(
			actual.Spec.Lines[0].Alternatives[0].Tokens[9]);
	}

	[Fact]
	public void DeserializeReviewedSpec_WithNestedShapeOrTokenDrift_RejectsEveryVariant()
	{
		string validJson = JsonSerializer.Serialize(
			CreateAllTokenSpecFile(),
			JsonOptions);
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				"\"MaximumTotalMinutes\": 60",
				"\"MaximumTotalMinutes\": 60,\n                  \"Unexpected\": true"),
			ReplaceFirst(
				validJson,
				"\"PluralLiteral\": \"h\",",
				string.Empty),
			ReplaceFirst(
				validJson,
				"\"MaximumLength\": 20",
				"\"MaximumLength\": 20,\n              \"MaximumLength\": 20"),
			ReplaceFirst(validJson, "\"Hours\"", "\"hours\""),
			ReplaceFirst(validJson, "\"Kind\": \"Opaque\"", "\"Kind\": \"Unknown\"")
		};

		foreach (string invalidJson in invalidJsonCases)
		{
			Assert.ThrowsAny<JsonException>(() =>
				AntigravityUsageR1SectionCalibrationJson
					.DeserializeReviewedSpec(Encoding.UTF8.GetBytes(invalidJson)));
		}
	}

	[Fact]
	public async Task TryWriteAsync_IsAtomicIdempotentAndRefusesDifferentOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(FirstIdentity, 5),
				CreateScreen(FirstIdentity, 6)));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));
		AntigravityUsageR1SectionLayoutFile layoutFile =
			await AntigravityUsageR1SectionCalibrationCompiler
				.CompileFromFilesAsync(bundlePath, specPath);
		string outputPath = Path.Combine(
			temporaryDirectory.Path,
			"layout.json");

		Assert.True(await AntigravityUsageR1SectionLayoutWriter.TryWriteAsync(
			outputPath,
			layoutFile));
		byte[] originalBytes = await File.ReadAllBytesAsync(outputPath);
		DateTime expectedLastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
		File.SetLastWriteTimeUtc(outputPath, expectedLastWriteTimeUtc);
		expectedLastWriteTimeUtc = File.GetLastWriteTimeUtc(outputPath);

		Assert.True(await AntigravityUsageR1SectionLayoutWriter.TryWriteAsync(
			outputPath,
			layoutFile));
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(outputPath));
		Assert.Equal(expectedLastWriteTimeUtc, File.GetLastWriteTimeUtc(outputPath));

		string differentFingerprint = layoutFile.ExpectedPageFingerprints[0][0]
			== 'A'
				? new string('B', 64)
				: new string('A', 64);
		AntigravityUsageR1SectionLayoutFile differentLayout = layoutFile with
		{
			ExpectedPageFingerprints = new[] { differentFingerprint }
		};

		Assert.False(await AntigravityUsageR1SectionLayoutWriter.TryWriteAsync(
			outputPath,
			differentLayout));
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(outputPath));
		Assert.Equal(expectedLastWriteTimeUtc, File.GetLastWriteTimeUtc(outputPath));
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	private static void AssertFixedFailure(
		AntigravityUsageR1CalibrationException exception,
		AntigravityUsageR1CalibrationFailureStage expectedStage)
	{
		Assert.Equal(expectedStage, exception.Stage);
		Assert.Equal(
			$"The offline AGY R1 usage calibration failed at stage: {expectedStage}.",
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
		int filledCells)
	{
		string[] lines =
		{
			$"Account={identity}",
			"Synthetic Section",
			"Weekly limit",
			$"M:{CreateMeter(filledCells)}|{filledCells * 10}%",
			"reset:none"
		};
		return new AntigravityUsageR1PrivateScreen(
			Columns,
			Rows,
			false,
			Array.AsReadOnly(lines));
	}

	private static AntigravityUsageR1SectionSpecFile CreateAllTokenSpecFile()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		AntigravityUsageR1SectionLineRule[] lines = spec.Lines.ToArray();
		lines[0] = lines[0] with
		{
			Alternatives = new[]
			{
				new AntigravityUsageR1SectionLinePattern(
					"all-token-shapes",
					new AntigravityUsageR1SectionToken[]
					{
						new AntigravityUsageR1SectionLiteralToken("literal"),
						new AntigravityUsageR1SectionIdentityToken(),
						new AntigravityUsageR1SectionPercentToken(
							AntigravityUsageR1SectionPercentMeaning.Remaining,
							0),
						new AntigravityUsageR1SectionCountdownToken(
							new AntigravityUsageR1SectionCountdownRule(
								"-",
								new[]
								{
									new AntigravityUsageR1SectionCountdownUnitRule(
										AntigravityUsageR1SectionCountdownUnit.Hours,
										"h",
										"h",
										0)
								},
								60)),
						new AntigravityUsageR1SectionAvailabilityToken(
							new[]
							{
								new AntigravityUsageR1SectionAvailabilityRule(
									"available",
									"available")
							}),
						new AntigravityUsageR1SectionProgressMeterToken(
							CreateMeterRule()),
						new AntigravityUsageR1SectionPaginationNumberToken(
							AntigravityUsageR1SectionPaginationField.Start),
						new AntigravityUsageR1SectionOpaqueToken(20),
						new AntigravityUsageR1SectionUnsignedAmountToken(1, 12),
						new AntigravityUsageR1SectionOuterLineShapeToken(
							"prefix",
							null,
							1,
							20,
							AntigravityUsageR1SectionOuterLineCharacterClass
								.AsciiPrintable)
					})
			}
		};
		return CreateReviewedSpecFile(spec with { Lines = lines });
	}

	private static AntigravityUsageR1SectionSpec CreateSpec()
	{
		const string SectionId = "synthetic";
		const string WindowId = "synthetic.weekly";
		AntigravityUsageR1SectionLineRule[] lines =
		{
			new(
				0,
				"line-0",
				AntigravityUsageR1SectionLineRole.Identity,
				null,
				null,
				new[]
				{
					Pattern(
						"identity",
						new AntigravityUsageR1SectionLiteralToken("Account="),
						new AntigravityUsageR1SectionIdentityToken())
				}),
			OwnedExactLine(
				1,
				AntigravityUsageR1SectionLineRole.SectionHeading,
				SectionId,
				null,
				"Synthetic Section"),
			OwnedExactLine(
				2,
				AntigravityUsageR1SectionLineRole.WindowLabel,
				SectionId,
				WindowId,
				"Weekly limit"),
			new(
				3,
				"line-3",
				AntigravityUsageR1SectionLineRole.Capacity,
				SectionId,
				WindowId,
				new[]
				{
					Pattern(
						"capacity",
						new AntigravityUsageR1SectionLiteralToken("M:"),
						new AntigravityUsageR1SectionProgressMeterToken(
							CreateMeterRule()),
						new AntigravityUsageR1SectionLiteralToken("|"),
						new AntigravityUsageR1SectionPercentToken(
							AntigravityUsageR1SectionPercentMeaning.Remaining,
							0))
				}),
			OwnedExactLine(
				4,
				AntigravityUsageR1SectionLineRole.Reset,
				SectionId,
				WindowId,
				"reset:none")
		};
		return new AntigravityUsageR1SectionSpec(
			2,
			"section-calibration-test",
			Columns,
			Rows,
			false,
			new[]
			{
				new AntigravityUsageR1SectionRule(
					SectionId,
					"Synthetic Section",
					0,
					new[]
					{
						new AntigravityUsageR1SectionWindowRule(
							WindowId,
							"Weekly limit",
							AntigravityUsageR1SectionWindowKind.Weekly,
							null,
							0,
							AntigravityUsageR1SectionCapacityPolicy.Percentage,
							AntigravityUsageR1SectionResetPolicy.None)
					})
			},
			lines,
			AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible,
			AntigravityUsageR1SectionPaginationMode.NoneWhenFullyVisible);
	}

	private static AntigravityUsageR1SectionSpecFile CreateReviewedSpecFile(
		AntigravityUsageR1SectionSpec spec)
	{
		return new AntigravityUsageR1SectionSpecFile(
			AntigravityUsageR1SectionSchemaParser.ReviewedSpecFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			spec,
			CaptureContractFingerprint,
			ApprovedDraftFingerprint);
	}

	private static AntigravityUsageR1SectionLineRule OwnedExactLine(
		int row,
		AntigravityUsageR1SectionLineRole role,
		string? sectionId,
		string? windowId,
		string literal)
	{
		return new AntigravityUsageR1SectionLineRule(
			row,
			$"line-{row}",
			role,
			sectionId,
			windowId,
			new[]
			{
				Pattern(
					$"line-{row}-exact",
					new AntigravityUsageR1SectionLiteralToken(literal))
			});
	}

	private static AntigravityUsageR1SectionLinePattern Pattern(
		string id,
		params AntigravityUsageR1SectionToken[] tokens)
	{
		return new AntigravityUsageR1SectionLinePattern(id, tokens);
	}

	private static AntigravityUsageR1SectionProgressMeterRule CreateMeterRule()
	{
		return new AntigravityUsageR1SectionProgressMeterRule(
			10,
			"\u2588",
			"\u2591",
			AntigravityUsageR1SectionPercentMeaning.Remaining,
			AntigravityUsageR1SectionMeterRounding.Nearest);
	}

	private static string CreateMeter(int filledCells)
	{
		return string.Concat(
			new string('\u2588', filledCells),
			new string('\u2591', 10 - filledCells));
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

	private static string ReplaceFirst(
		string value,
		string oldValue,
		string newValue)
	{
		int index = value.IndexOf(oldValue, StringComparison.Ordinal);

		if (index < 0)
		{
			throw new InvalidOperationException(
				"Test JSON mutation target is missing.");
		}

		return string.Concat(
			value.AsSpan(0, index),
			newValue,
			value.AsSpan(index + oldValue.Length));
	}

	private static async Task<string> WritePrivateBundleAsync(
		string privateDirectory,
		string fileName,
		AntigravityUsageR1PrivateScreenBundleFile bundle)
	{
		string path = Path.Combine(privateDirectory, fileName);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(bundle, JsonOptions),
			CreateUtf8Encoding());
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
		return path;
	}

	private static async Task<string> WriteReviewedSpecAsync(
		TemporaryDirectory temporaryDirectory,
		string fileName,
		AntigravityUsageR1SectionSpecFile specFile)
	{
		string path = Path.Combine(temporaryDirectory.Path, fileName);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(specFile, JsonOptions),
			CreateUtf8Encoding());
		return path;
	}

	private static UTF8Encoding CreateUtf8Encoding()
	{
		return new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
	}
}
