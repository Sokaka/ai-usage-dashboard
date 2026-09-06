using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1CalibrationTests
{
	private static readonly string CaptureContractFingerprint =
		new('A', 64);
	private const string PrivateIdentityMarker =
		"PRIVATE-CALIBRATION-ACCOUNT@example.invalid";
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
	private static readonly DateTimeOffset ResetTimeOne = new(
		2026,
		7,
		17,
		1,
		2,
		3,
		TimeSpan.Zero);
	private static readonly DateTimeOffset ResetTimeTwo = new(
		2026,
		7,
		18,
		4,
		5,
		6,
		TimeSpan.Zero);

	[Fact]
	public async Task CompileFromFilesAsync_WithDynamicPrivateSequences_WritesOnlyReviewedTemplate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			spec,
			PrivateIdentityMarker,
			10,
			20,
			ResetTimeOne);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			spec,
			PrivateIdentityMarker,
			60,
			80,
			ResetTimeTwo);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(first, second));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1ReviewedLayoutFile reviewedLayout =
			await AntigravityUsageR1CalibrationCompiler
				.CompileFromFilesAsync(bundlePath, specPath);
		string outputPath = Path.Combine(
			temporaryDirectory.Path,
			"reviewed-layout.json");

		Assert.True(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
			outputPath,
			reviewedLayout));
		string output = await File.ReadAllTextAsync(outputPath);

		Assert.Equal(AntigravityUsageR1ReviewState.Reviewed,
			reviewedLayout.ReviewState);
		Assert.Single(reviewedLayout.ExpectedPageFingerprints);
		Assert.Matches("^[0-9A-F]{64}$", reviewedLayout.SchemaFingerprint);
		Assert.DoesNotContain(
			PrivateIdentityMarker,
			output,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			ResetTimeOne.ToString("O", CultureInfo.InvariantCulture),
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			ResetTimeTwo.ToString("O", CultureInfo.InvariantCulture),
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain("AccountIdentity", output, StringComparison.Ordinal);
		Assert.DoesNotContain("UsedPercent", output, StringComparison.Ordinal);
		Assert.DoesNotContain("RemainingPercent", output, StringComparison.Ordinal);
		Assert.DoesNotContain("ResetsAt", output, StringComparison.Ordinal);
		_ = reviewedLayout.ToLayout();
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithReorderedSequences_IsDeterministic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			spec,
			PrivateIdentityMarker,
			10,
			20,
			ResetTimeOne);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			spec,
			PrivateIdentityMarker,
			60,
			80,
			ResetTimeTwo);
		string firstBundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"first-bundle.json",
			CreateBundle(first, second));
		string secondBundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"second-bundle.json",
			CreateBundle(second, first));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1ReviewedLayoutFile firstLayout =
			await AntigravityUsageR1CalibrationCompiler
				.CompileFromFilesAsync(firstBundlePath, specPath);
		AntigravityUsageR1ReviewedLayoutFile secondLayout =
			await AntigravityUsageR1CalibrationCompiler
				.CompileFromFilesAsync(secondBundlePath, specPath);
		string firstOutputPath = Path.Combine(
			temporaryDirectory.Path,
			"first-output.json");
		string secondOutputPath = Path.Combine(
			temporaryDirectory.Path,
			"second-output.json");

		Assert.True(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
			firstOutputPath,
			firstLayout));
		Assert.True(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
			secondOutputPath,
			secondLayout));
		Assert.Equal(
			await File.ReadAllBytesAsync(firstOutputPath),
			await File.ReadAllBytesAsync(secondOutputPath));
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithOneSequence_RejectsInsufficientBundle()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(CreateScreen(
				spec,
				PrivateIdentityMarker,
				10,
				20,
				ResetTimeOne)));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1CalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithoutDynamicChange_RejectsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreen screen = CreateScreen(
			spec,
			PrivateIdentityMarker,
			10,
			20,
			ResetTimeOne);
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(screen, screen));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1CalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.DynamicEvidence);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithRawLayoutMismatch_UsesRedactedStageError()
	{
		const string RawMismatchMarker = "PRIVATE-RAW-MISMATCH-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			spec,
			PrivateIdentityMarker,
			10,
			20,
			ResetTimeOne);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			spec,
			PrivateIdentityMarker,
			60,
			80,
			ResetTimeTwo);
		string[] mismatchedLines = second.Lines.ToArray();
		mismatchedLines[spec.PrefixLines.Count + 3] = RawMismatchMarker;
		second = second with { Lines = Array.AsReadOnly(mismatchedLines) };
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(first, second));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1CalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
		Assert.DoesNotContain(
			RawMismatchMarker,
			exception.Message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			PrivateIdentityMarker,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WhenPageFingerprintChanges_RefusesToExpandAllowlist()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec baselineSpec = CreateSpec();
		AntigravityUsageR1ModelRule optionalModel = new(
			"agy.synthetic.optional",
			"AGY Synthetic Optional",
			false,
			2);
		AntigravityUsageR1ReviewedSpec spec = baselineSpec with
		{
			Models = Array.AsReadOnly(baselineSpec.Models
				.Concat(new[] { optionalModel })
				.ToArray())
		};
		AntigravityUsageR1PrivateScreen first = CreateScreen(
			spec,
			PrivateIdentityMarker,
			10,
			20,
			ResetTimeOne);
		AntigravityUsageR1PrivateScreen second = CreateScreen(
			spec,
			PrivateIdentityMarker,
			60,
			80,
			ResetTimeTwo);
		List<string> secondLines = second.Lines.ToList();
		int footerIndex = spec.PrefixLines.Count + 4 + 2;
		secondLines.Insert(
			footerIndex,
			CreateRow(
				spec,
				optionalModel,
				40,
				ResetTimeTwo.ToString("O", CultureInfo.InvariantCulture)));
		secondLines.RemoveAt(secondLines.Count - 1);
		second = second with
		{
			Lines = Array.AsReadOnly(secondLines.ToArray())
		};
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(first, second));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1CalibrationCompiler
					.CompileFromFilesAsync(bundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.ScreenValidation);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithNonPrivateBundle_RejectsBeforeParsing()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreenBundleFile bundle = CreateBundle(
			CreateScreen(spec, PrivateIdentityMarker, 10, 20, ResetTimeOne),
			CreateScreen(spec, PrivateIdentityMarker, 60, 80, ResetTimeTwo));
		string broadBundlePath = Path.Combine(
			temporaryDirectory.Path,
			"broad-bundle.json");
		await File.WriteAllTextAsync(
			broadBundlePath,
			JsonSerializer.Serialize(bundle, JsonOptions));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		AntigravityUsageR1CalibrationException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
				async () => await AntigravityUsageR1CalibrationCompiler
					.CompileFromFilesAsync(broadBundlePath, specPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1CalibrationFailureStage.PrivateBundleRead);
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithNonStrictBundleJson_RejectsEveryVariant()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		AntigravityUsageR1PrivateScreenBundleFile bundle = CreateBundle(
			CreateScreen(spec, PrivateIdentityMarker, 10, 20, ResetTimeOne),
			CreateScreen(spec, PrivateIdentityMarker, 60, 80, ResetTimeTwo));
		string validJson = JsonSerializer.Serialize(bundle, JsonOptions);
		string formatProperty =
			$"\"FormatVersion\": \"{AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion}\",";
		string captureContractProperty =
			$"\"CaptureContractFingerprint\": \"{CaptureContractFingerprint}\",";
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				"\"Sequences\":",
				"\"Unexpected\": true,\n  \"Sequences\":"),
			ReplaceFirst(validJson, "\"Sequences\":", "\"sequences\":"),
			ReplaceFirst(
				validJson,
				formatProperty,
				$"{formatProperty}\n  {formatProperty}"),
			ReplaceFirst(
				validJson,
				captureContractProperty,
				string.Empty),
			ReplaceFirst(
				validJson,
				CaptureContractFingerprint,
				CaptureContractFingerprint.ToLowerInvariant())
		};
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));

		for (int index = 0; index < invalidJsonCases.Length; index++)
		{
			string bundlePath = await WritePrivateTextAsync(
				privateDirectory,
				$"invalid-bundle-{index}.json",
				invalidJsonCases[index]);
			AntigravityUsageR1CalibrationException exception =
				await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
					async () => await AntigravityUsageR1CalibrationCompiler
						.CompileFromFilesAsync(bundlePath, specPath));

			AssertFixedFailure(
				exception,
				AntigravityUsageR1CalibrationFailureStage.PrivateBundleFormat);
		}
	}

	[Fact]
	public async Task CompileFromFilesAsync_WithNonStrictReviewedSpec_RejectsEveryVariant()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(spec, PrivateIdentityMarker, 10, 20, ResetTimeOne),
				CreateScreen(spec, PrivateIdentityMarker, 60, 80, ResetTimeTwo)));
		string validJson = JsonSerializer.Serialize(
			CreateReviewedSpecFile(spec),
			JsonOptions);
		string reviewProperty = "\"ReviewState\": \"Reviewed\",";
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				AntigravityUsageR1SchemaParser.ReviewedSpecFormatVersion,
				"agy-usage-r1-reviewed-spec-unknown"),
			ReplaceFirst(validJson, "\"Reviewed\"", "\"reviewed\""),
			ReplaceFirst(
				validJson,
				reviewProperty,
				$"{reviewProperty}\n  {reviewProperty}"),
			ReplaceFirst(
				validJson,
				"\"Order\": 0",
				"\"Unexpected\": true,\n        \"Order\": 0")
		};

		for (int index = 0; index < invalidJsonCases.Length; index++)
		{
			string specPath = Path.Combine(
				temporaryDirectory.Path,
				$"invalid-spec-{index}.json");
			await File.WriteAllTextAsync(specPath, invalidJsonCases[index]);
			AntigravityUsageR1CalibrationException exception =
				await Assert.ThrowsAsync<AntigravityUsageR1CalibrationException>(
					async () => await AntigravityUsageR1CalibrationCompiler
						.CompileFromFilesAsync(bundlePath, specPath));

			AssertFixedFailure(
				exception,
				AntigravityUsageR1CalibrationFailureStage.ReviewedSpecFormat);
		}
	}

	[Fact]
	public async Task TryWriteAsync_IsAtomicIdempotentAndRefusesDifferentOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		AntigravityUsageR1ReviewedSpec spec = CreateSpec();
		string bundlePath = await WritePrivateBundleAsync(
			privateDirectory,
			"bundle.json",
			CreateBundle(
				CreateScreen(spec, PrivateIdentityMarker, 10, 20, ResetTimeOne),
				CreateScreen(spec, PrivateIdentityMarker, 60, 80, ResetTimeTwo)));
		string specPath = await WriteReviewedSpecAsync(
			temporaryDirectory,
			"reviewed-spec.json",
			CreateReviewedSpecFile(spec));
		AntigravityUsageR1ReviewedLayoutFile reviewedLayout =
			await AntigravityUsageR1CalibrationCompiler
				.CompileFromFilesAsync(bundlePath, specPath);
		string outputPath = Path.Combine(
			temporaryDirectory.Path,
			"reviewed-layout.json");

		Assert.True(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
			outputPath,
			reviewedLayout));
		byte[] originalBytes = await File.ReadAllBytesAsync(outputPath);
		DateTime expectedLastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
		File.SetLastWriteTimeUtc(outputPath, expectedLastWriteTimeUtc);
		expectedLastWriteTimeUtc = File.GetLastWriteTimeUtc(outputPath);

		Assert.True(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
			outputPath,
			reviewedLayout));
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(outputPath));
		Assert.Equal(expectedLastWriteTimeUtc, File.GetLastWriteTimeUtc(outputPath));

		string differentFingerprint = reviewedLayout.ExpectedPageFingerprints[0][0]
			== 'A'
				? new string('B', 64)
				: new string('A', 64);
		AntigravityUsageR1ReviewedLayoutFile differentLayout =
			reviewedLayout with
			{
				ExpectedPageFingerprints = Array.AsReadOnly(new[]
				{
					differentFingerprint
				})
			};

		Assert.False(await AntigravityUsageR1ReviewedLayoutWriter.TryWriteAsync(
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
		ReadOnlyCollection<AntigravityUsageR1PrivateScreenSequence> sequences =
			Array.AsReadOnly(screens
				.Select(screen => new AntigravityUsageR1PrivateScreenSequence(
					Array.AsReadOnly(new[] { screen })))
				.ToArray());
		return new AntigravityUsageR1PrivateScreenBundleFile(
			AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
			CaptureContractFingerprint,
			sequences);
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

	private static AntigravityUsageR1ReviewedSpec CreateSpec()
	{
		return new AntigravityUsageR1ReviewedSpec(
			1,
			"synthetic-r1-calibration-v1",
			120,
			12,
			false,
			Array.AsReadOnly(new[] { "SYNTHETIC FIXED PREFIX" }),
			"SYNTHETIC AGY R1 QUOTAS",
			"Account: ",
			"Page: ",
			"Model | Used | Remaining | Reset",
			"END SYNTHETIC AGY R1 QUOTAS",
			" | ",
			Array.AsReadOnly(new[] { "SYNTHETIC FIXED SUFFIX" }),
			Array.AsReadOnly(new[]
			{
				new AntigravityUsageR1ModelRule(
					"agy.synthetic.flash",
					"AGY Synthetic Flash",
					true,
					0),
				new AntigravityUsageR1ModelRule(
					"agy.synthetic.pro",
					"AGY Synthetic Pro",
					true,
					1)
			}),
			AntigravityUsageR1PaginationPolicy.RequireSinglePage);
	}

	private static AntigravityUsageR1ReviewedSpecFile CreateReviewedSpecFile(
		AntigravityUsageR1ReviewedSpec spec)
	{
		return new AntigravityUsageR1ReviewedSpecFile(
			AntigravityUsageR1SchemaParser.ReviewedSpecFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			spec);
	}

	private static AntigravityUsageR1PrivateScreen CreateScreen(
		AntigravityUsageR1ReviewedSpec spec,
		string identity,
		int firstUsedPercent,
		int secondUsedPercent,
		DateTimeOffset resetTime)
	{
		string reset = resetTime.ToString("O", CultureInfo.InvariantCulture);
		string[] contentLines = spec.PrefixLines
			.Concat(new[]
			{
				spec.PanelTitle,
				$"{spec.AccountPrefix}{identity}",
				$"{spec.PagePrefix}single",
				spec.TableHeader,
				CreateRow(spec, spec.Models[0], firstUsedPercent, reset),
				CreateRow(spec, spec.Models[1], secondUsedPercent, reset),
				spec.PanelFooter
			})
			.Concat(spec.SuffixLines)
			.ToArray();
		string[] lines = contentLines
			.Concat(Enumerable.Repeat(string.Empty, spec.Rows - contentLines.Length))
			.ToArray();
		return new AntigravityUsageR1PrivateScreen(
			spec.Columns,
			spec.Rows,
			spec.IsAlternateScreen,
			Array.AsReadOnly(lines));
	}

	private static string CreateRow(
		AntigravityUsageR1ReviewedSpec spec,
		AntigravityUsageR1ModelRule model,
		int usedPercent,
		string reset)
	{
		return string.Join(
			spec.FieldSeparator,
			model.RenderedLiteral,
			$"{usedPercent}%",
			$"{100 - usedPercent}%",
			reset);
	}

	private static string ReplaceFirst(
		string value,
		string oldValue,
		string newValue)
	{
		int index = value.IndexOf(oldValue, StringComparison.Ordinal);

		if (index < 0)
		{
			throw new InvalidOperationException("Test JSON mutation target is missing.");
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
		return await WritePrivateTextAsync(
			privateDirectory,
			fileName,
			JsonSerializer.Serialize(bundle, JsonOptions));
	}

	private static async Task<string> WritePrivateTextAsync(
		string privateDirectory,
		string fileName,
		string value)
	{
		string path = Path.Combine(privateDirectory, fileName);
		await File.WriteAllTextAsync(
			path,
			value,
			new UTF8Encoding(
				encoderShouldEmitUTF8Identifier: false,
				throwOnInvalidBytes: true));
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
		return path;
	}

	private static async Task<string> WriteReviewedSpecAsync(
		TemporaryDirectory temporaryDirectory,
		string fileName,
		AntigravityUsageR1ReviewedSpecFile reviewedSpec)
	{
		string path = Path.Combine(temporaryDirectory.Path, fileName);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(reviewedSpec, JsonOptions),
			new UTF8Encoding(
				encoderShouldEmitUTF8Identifier: false,
				throwOnInvalidBytes: true));
		return path;
	}
}
