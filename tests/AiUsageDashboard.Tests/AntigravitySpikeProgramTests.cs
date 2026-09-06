using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

[CollectionDefinition(nameof(AntigravitySpikeProgramConsoleCollection),
	DisableParallelization = true)]
public sealed class AntigravitySpikeProgramConsoleCollection
{
}

[Collection(nameof(AntigravitySpikeProgramConsoleCollection))]
public sealed class AntigravitySpikeProgramTests
{
	private readonly record struct TestFileState(
		long Length,
		DateTime CreationTimeUtc,
		DateTime LastWriteTimeUtc,
		FileAttributes Attributes);

	private sealed class ScriptedPromptPinFileReplacer :
		IAntigravityPromptPinFileReplacer
	{
		internal Action<int, string, string> MoveAction { get; init; } =
			static (_, source, destination) => File.Move(source, destination);

		internal Action<int, string, string, string> ReplaceAction
		{
			get;
			init;
		} = static (_, source, destination, backup) => File.Replace(
			source,
			destination,
			backup,
			ignoreMetadataErrors: false);

		internal int MoveCallCount { get; private set; }

		internal int ReplaceCallCount { get; private set; }

		public void Move(string sourceFileName, string destinationFileName)
		{
			MoveCallCount++;
			MoveAction(
				MoveCallCount,
				sourceFileName,
				destinationFileName);
		}

		public void Replace(
			string sourceFileName,
			string destinationFileName,
			string destinationBackupFileName)
		{
			ReplaceCallCount++;
			ReplaceAction(
				ReplaceCallCount,
				sourceFileName,
				destinationFileName,
				destinationBackupFileName);
		}
	}

	private static readonly string ExactFingerprint = new('B', 64);
	private static readonly string ReplacementExactFingerprint = new('D', 64);
	private static readonly string ReplacementStructuralFingerprint = new('C', 64);
	private static readonly string StructuralFingerprint = new('A', 64);
	private static readonly string UsageExactFingerprint = new('F', 64);
	private static readonly string UsageStructuralFingerprint = new('E', 64);

	[Fact]
	public async Task Main_WithUnknownLiveSubcommand_FailsBeforeReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		TextWriter originalError = Console.Error;
		using StringWriter error = new();

		try
		{
			Console.SetError(error);

			int exitCode = await Program.Main(new[]
			{
				"unknown-live-command",
				"--profile",
				profilePath,
				"--i-understand-live-r0"
			});

			Assert.Equal(2, exitCode);
			Assert.Contains(
				"Unknown command.",
				error.ToString(),
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"profile validation failed",
				error.ToString(),
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			Console.SetError(originalError);
		}
	}

	[Fact]
	public async Task Main_CalibratePrompt_WithMissingProfile_UsesLiveValidationPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string missingProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"missing-profile.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"calibrate-prompt",
				"--profile",
				missingProfilePath,
				"--i-understand-live-r0"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"profile validation failed closed",
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Main_CalibratePrompt_WithWrongArguments_FailsBeforeReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		string[][] invalidArguments =
		{
			new[]
			{
				"calibrate-prompt", "--profile", unreadProfilePath
			},
			new[]
			{
				"calibrate-prompt", unreadProfilePath, "--profile",
				"--i-understand-live-r0"
			},
			new[]
			{
				"calibrate-prompt", "--profile", unreadProfilePath,
				"--i-understand-live-r0", "extra"
			}
		};

		foreach (string[] arguments in invalidArguments)
		{
			(int exitCode, string output, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Empty(output);
			Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"profile validation failed",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(unreadProfilePath));
		}
	}

	[Fact]
	public async Task Main_Help_ListsR0AndR1Commands()
	{
		(int exitCode, string output, string error) =
			await InvokeMainAsync(new[] { "--help" });

		Assert.Equal(0, exitCode);
		Assert.Empty(error);
		Assert.Contains(
			"calibrate-prompt --profile <absolute-path>",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"repin-prompt --profile <absolute-json> " +
			"--expected-structural <64hex> --expected-exact <64hex> " +
			"--structural <64hex> --exact <64hex> " +
			"--i-understand-live-r0",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"pin-usage --profile <absolute-json> --structural <64hex> " +
			"--exact <64hex> --i-understand-live-r0",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"calibrate-usage-layout-offline --input <absolute-private-bundle> " +
			"--reviewed-spec <absolute-reviewed-spec> " +
			"--output <absolute-reviewed-layout>",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"derive-private-r1-section-spec-draft " +
			"--profile <absolute-private-r0> " +
			"--input <absolute-private-bundle> " +
			"--output <absolute-private-draft>",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"promote-private-r1-section-spec " +
			"--profile <absolute-private-r0> " +
			"--input <absolute-private-bundle> " +
			"--draft <absolute-private-draft> " +
			"--output <absolute-reviewed-spec> " +
			"--approved-fingerprint <64hex>",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"compose-private-r1-section-profile " +
			"--profile <absolute-private-r0> " +
			"--layout <absolute-reviewed-layout> " +
			"--output <absolute-private-r1-profile>",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"capture-usage-r1 --profile <absolute-path> " +
			"--i-understand-live-r1",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"capture-usage-r1-section " +
			"--profile <absolute-private-r1-profile> " +
			"--i-understand-live-r1",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"capture-usage-r1-private --profile <absolute-r0-profile> " +
			"--output <absolute-private-bundle> " +
			"--i-understand-live-private-r1",
			output,
			StringComparison.Ordinal);
		Assert.Contains(
			"derive-private-r0-viewport-profile " +
			"--profile <absolute-private-r0> " +
			"--output <absolute-private-r0> " +
			"--columns <1-1000> --rows <1-256>",
			output,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Main_PromotePrivateR1SectionSpec_WithMissingProfile_UsesPromotionPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string missing = Path.Combine(temporaryDirectory.Path, "missing.json");

		(int exitCode, string output, string error) = await InvokeMainAsync(new[]
		{
			"promote-private-r1-section-spec",
			"--profile", missing,
			"--input", missing,
			"--draft", missing,
			"--output", Path.Combine(temporaryDirectory.Path, "reviewed.json"),
			"--approved-fingerprint", StructuralFingerprint
		});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"promotion failed closed",
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Main_PromotePrivateR1SectionSpec_WithLegacyArguments_IsRejected()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string missing = Path.Combine(temporaryDirectory.Path, "missing.json");

		(int exitCode, string output, string error) = await InvokeMainAsync(new[]
		{
			"promote-private-r1-section-spec",
			"--draft", missing,
			"--output", Path.Combine(temporaryDirectory.Path, "reviewed.json"),
			"--approved-fingerprint", StructuralFingerprint
		});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_WithWrongArguments_FailsBeforeReadingProfile()
	{
		const string RawMarker = "PRIVATE-VIEWPORT-WRONG-ARGS-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-written.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"derive-private-r0-viewport-profile",
				"--profile",
				sourcePath,
				"--output",
				outputPath,
				"--columns",
				"160"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.False(File.Exists(sourcePath));
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_CreatesPrivateUnpinnedProfileAndCaptureFailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string sourcePath, AntigravityLiveR0ProfileFile sourceProfile) =
			await CreatePrivateProfileAsync(
				temporaryDirectory,
				StructuralFingerprint,
				ExactFingerprint);
		string outputPath = Path.Combine(
			Path.GetDirectoryName(sourcePath)!,
			"profile-160x60.json");

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateDeriveViewportProfileArguments(
				sourcePath,
				outputPath,
				"160",
				"60"));

		Assert.Equal(0, exitCode);
		Assert.Equal(
			$"Private R0 viewport profile derived.{Environment.NewLine}",
			output);
		Assert.Empty(error);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(outputPath));
		byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
		AntigravityLiveR0ProfileFile actual =
			AntigravityLiveR0ProfileJson.Deserialize(outputBytes);
		AntigravityLiveR0ProfileFile expected = sourceProfile with
		{
			Columns = 160,
			Rows = 60,
			ExpectedPromptStructuralFingerprint = string.Empty,
			ExpectedExactPromptFingerprint = string.Empty,
			ExpectedUsageStructuralFingerprint = null,
			ExpectedExactUsageFingerprint = null
		};
		Assert.True(JsonNode.DeepEquals(
			JsonSerializer.SerializeToNode(expected),
			JsonSerializer.SerializeToNode(actual)));
		Assert.Empty(Directory.GetFiles(
			Path.GetDirectoryName(outputPath)!,
			$".{Path.GetFileName(outputPath)}.derive-viewport.*.tmp"));

		(AntigravityLiveR0Profile _, byte[] hmacKey) =
			await actual.ToProfileAsync(outputPath);
		CryptographicOperations.ZeroMemory(hmacKey);

		(int captureExitCode, string captureOutput, string captureError) =
			await InvokeMainAsync(new[]
			{
				"capture-usage",
				"--profile",
				outputPath,
				"--i-understand-live-r0"
			});

		Assert.Equal(3, captureExitCode);
		Assert.Empty(captureError);
		Assert.Contains(
			"InvalidProfile",
			captureOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"InputWriteCount\": 0",
			captureOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"InputWriteAttemptCount\": 0",
			captureOutput,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_RepeatedExactRequestIsIdempotent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string sourcePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		string outputPath = Path.Combine(
			Path.GetDirectoryName(sourcePath)!,
			"profile-200x80.json");
		string[] arguments = CreateDeriveViewportProfileArguments(
			sourcePath,
			outputPath,
			"200",
			"80");

		(int firstExitCode, _, _) = await InvokeMainAsync(arguments);
		Assert.Equal(0, firstExitCode);
		byte[] firstBytes = await File.ReadAllBytesAsync(outputPath);
		TestFileState firstState = CaptureTestFileState(outputPath);

		(int secondExitCode, string output, string error) =
			await InvokeMainAsync(arguments);

		Assert.Equal(0, secondExitCode);
		Assert.Equal(
			$"Private R0 viewport profile derived.{Environment.NewLine}",
			output);
		Assert.Empty(error);
		Assert.Equal(firstBytes, await File.ReadAllBytesAsync(outputPath));
		Assert.Equal(firstState, CaptureTestFileState(outputPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(outputPath));
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_DifferentExistingOutputFailsWithoutOverwriteOrLeak()
	{
		const string RawMarker = "PRIVATE-VIEWPORT-COLLISION-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		(string sourcePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string outputPath = Path.Combine(
			Path.GetDirectoryName(sourcePath)!,
			$"{RawMarker}.json");
		byte[] competingBytes = Encoding.UTF8.GetBytes(
			"{\"PrivateCollision\":true}");
		await File.WriteAllBytesAsync(outputPath, competingBytes);
		AssertSafeInheritedProfileAcl(outputPath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateDeriveViewportProfileArguments(
				sourcePath,
				outputPath,
				"160",
				"60"));

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"failed closed at stage: OutputPersistence.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.Equal(competingBytes, await File.ReadAllBytesAsync(outputPath));
		AssertSafeInheritedProfileAcl(outputPath);
	}

	[Theory]
	[InlineData("0", "30")]
	[InlineData("1001", "30")]
	[InlineData("120", "0")]
	[InlineData("120", "257")]
	[InlineData("not-a-number", "30")]
	public async Task Main_DerivePrivateR0ViewportProfile_InvalidDimensionsFailBeforeReading(
		string columns,
		string rows)
	{
		const string RawMarker = "PRIVATE-VIEWPORT-DIMENSION-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.source.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.output.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateDeriveViewportProfileArguments(
				sourcePath,
				outputPath,
				columns,
				rows));

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"failed closed at stage: Arguments.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.False(File.Exists(sourcePath));
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_NonPrivateSourceFailsAtFixedStageWithoutLeak()
	{
		const string RawMarker = "PRIVATE-VIEWPORT-SOURCE-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.output.json"));
		await File.WriteAllTextAsync(
			sourcePath,
			$"{{\"PrivateMarker\":\"{RawMarker}\"}}");

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateDeriveViewportProfileArguments(
				sourcePath,
				outputPath,
				"160",
				"60"));

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"failed closed at stage: ProfileRead.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_DerivePrivateR0ViewportProfile_UnsafeOutputPathFailsWithoutChangingSource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string sourcePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(sourcePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateDeriveViewportProfileArguments(
				sourcePath,
				sourcePath,
				"160",
				"60"));

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"failed closed at stage: ProfileOrOutputValidation.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(sourcePath, error, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
	}

	[Fact]
	public async Task Main_OfflineUsageCalibration_WithWrongArguments_FailsBeforeReadingFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateBundlePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.private.json"));
		string reviewedSpecPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.reviewed.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-created.layout.json"));
		string[][] invalidArguments =
		{
			new[]
			{
				"calibrate-usage-layout-offline",
				"--input",
				privateBundlePath,
				"--reviewed-spec",
				reviewedSpecPath
			},
			new[]
			{
				"calibrate-usage-layout-offline",
				"--private-input",
				privateBundlePath,
				"--reviewed-spec",
				reviewedSpecPath,
				"--output",
				outputPath
			},
			new[]
			{
				"calibrate-usage-layout-offline",
				"--input",
				privateBundlePath,
				"--reviewed-spec",
				reviewedSpecPath,
				"--output",
				outputPath,
				"--i-understand-live-r1"
			}
		};

		foreach (string[] arguments in invalidArguments)
		{
			(int exitCode, string output, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Empty(output);
			Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"usage calibration failed",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				privateBundlePath,
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				reviewedSpecPath,
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				outputPath,
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(outputPath));
		}
	}

	[Fact]
	public async Task Main_OfflineUsageCalibration_WithMissingPrivateBundle_ReportsFixedStageWithoutLeak()
	{
		const string RawMarker = "PRIVATE-R1-CALIBRATION-RAW-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string privateBundlePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.private.json"));
		string reviewedSpecPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.reviewed.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.layout.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"calibrate-usage-layout-offline",
				"--input",
				privateBundlePath,
				"--reviewed-spec",
				reviewedSpecPath,
				"--output",
				outputPath
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"usage calibration failed closed at stage: PrivateBundleRead.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.DoesNotContain(
			privateBundlePath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			reviewedSpecPath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			outputPath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_PrivateR1SectionDraft_WithMissingProfile_ReportsFixedStageWithoutLeak()
	{
		const string RawMarker = "PRIVATE-R1-DRAFT-RAW-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.profile.json"));
		string privateBundlePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.bundle.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.draft.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"derive-private-r1-section-spec-draft",
				"--profile",
				profilePath,
				"--input",
				privateBundlePath,
				"--output",
				outputPath
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"section draft failed closed at stage: ProfileRead.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.DoesNotContain(profilePath, error, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			privateBundlePath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(outputPath, error, StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_CaptureUsageR1_WithMissingProfile_UsesR1ValidationPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string missingProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"missing-r1-profile.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"capture-usage-r1",
				"--profile",
				missingProfilePath,
				"--i-understand-live-r1"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"Live R1 profile validation failed closed.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Live R0", error, StringComparison.Ordinal);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(
			missingProfilePath,
			error,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PrivateR1CalibrationCapture_WithMissingProfile_ReportsFixedStageWithoutLeak()
	{
		const string RawMarker = "PRIVATE-R1-CAPTURE-PATH-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string missingProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.profile.json"));
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			$"{RawMarker}.bundle.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"capture-usage-r1-private",
				"--profile",
				missingProfilePath,
				"--output",
				outputPath,
				"--i-understand-live-private-r1"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"Private AGY R1 calibration capture failed closed at stage: ProfileRead.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.DoesNotContain(
			missingProfilePath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			outputPath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_PrivateR1CalibrationCapture_WithNonAdjacentOutput_FailsBeforeLive()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string outputPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"outside-private-directory.bundle.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"capture-usage-r1-private",
				"--profile",
				profilePath,
				"--output",
				outputPath,
				"--i-understand-live-private-r1"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.True(
			error.Contains(
				"failed closed at stage: ProfileOrOutputValidation.",
				StringComparison.Ordinal),
			error);
		Assert.DoesNotContain(
			profilePath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			outputPath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Main_PrivateR1CalibrationCapture_WithMismatchedExistingContract_FailsBeforeLive()
	{
		const string RawMarker = "PRIVATE-R1-PREFLIGHT-RAW-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string outputPath = Path.Combine(
			Path.GetDirectoryName(profilePath)!,
			"screen-bundle.json");
		string[] lines = Enumerable.Range(0, 30)
			.Select(index => index == 1 ? RawMarker : string.Empty)
			.ToArray();
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			outputPath,
			new TerminalScreenSnapshot(120, 30, false, lines),
			new string('E', 64));
		byte[] originalBytes = await File.ReadAllBytesAsync(outputPath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"capture-usage-r1-private",
				"--profile",
				profilePath,
				"--output",
				outputPath,
				"--i-understand-live-private-r1"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"failed closed at stage: CaptureContractPreflight.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.DoesNotContain(
			profilePath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			outputPath,
			error,
			StringComparison.OrdinalIgnoreCase);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(outputPath));
	}

	[Fact]
	public async Task Main_R0R1AndPrivateConsentFlags_AreNotInterchangeable()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		string unwrittenBundlePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-written.json"));
		string[][] argumentsWithWrongConsent =
		{
			new[]
			{
				"capture-usage-r1",
				"--profile",
				unreadProfilePath,
				"--i-understand-live-r0"
			},
			new[]
			{
				"capture-usage",
				"--profile",
				unreadProfilePath,
				"--i-understand-live-r1"
			},
			new[]
			{
				"capture-usage-r1-private",
				"--profile",
				unreadProfilePath,
				"--output",
				unwrittenBundlePath,
				"--i-understand-live-r1"
			},
			new[]
			{
				"capture-usage-r1-private",
				"--profile",
				unreadProfilePath,
				"--output",
				unwrittenBundlePath,
				"--i-understand-live-r0"
			}
		};

		foreach (string[] arguments in argumentsWithWrongConsent)
		{
			(int exitCode, string output, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Empty(output);
			Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"profile validation failed",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				unreadProfilePath,
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(unreadProfilePath));
			Assert.False(File.Exists(unwrittenBundlePath));
		}
	}

	[Theory]
	[InlineData("{\"SchemaVersion\":\"agy-live-r1-profile-v1\",\"SchemaVersion\":\"PRIVATE-R1-JSON-MARKER\"}")]
	[InlineData("{\"UnknownLegacyUsagePin\":\"PRIVATE-R1-JSON-MARKER\"}")]
	public async Task Main_CaptureUsageR1_WithDuplicateOrUnknownJson_FailsClosedWithoutLeak(
		string profileJson)
	{
		const string RawMarker = "PRIVATE-R1-JSON-MARKER";
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"malformed-r1-profile.json"));
		await File.WriteAllTextAsync(profilePath, profileJson);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			new[]
			{
				"capture-usage-r1",
				"--profile",
				profilePath,
				"--i-understand-live-r1"
			});

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"Live R1 profile validation failed closed.",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Unknown command.", error, StringComparison.Ordinal);
		Assert.DoesNotContain(RawMarker, error, StringComparison.Ordinal);
		Assert.DoesNotContain(profileJson, error, StringComparison.Ordinal);
		Assert.DoesNotContain(
			profilePath,
			error,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_CaptureUsageR1_WithOversizedProfile_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"oversized-r1-profile.json"));
		byte[] oversizedProfile = new byte[(1024 * 1024) + 1];

		try
		{
			await File.WriteAllBytesAsync(profilePath, oversizedProfile);

			(int exitCode, string output, string error) = await InvokeMainAsync(
				new[]
				{
					"capture-usage-r1",
					"--profile",
					profilePath,
					"--i-understand-live-r1"
				});

			Assert.Equal(2, exitCode);
			Assert.Empty(output);
			Assert.Contains(
				"Live R1 profile validation failed closed.",
				error,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				profilePath,
				error,
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(oversizedProfile);
		}
	}

	[Fact]
	public async Task Main_InitHmacKey_CreatesExactKeyWithoutPrintingMaterial()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-key"));
		string keyPath = Path.GetFullPath(Path.Combine(
			keyDirectory,
			"live-r0.key"));
		TextWriter originalOutput = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter output = new();
		using StringWriter error = new();
		byte[]? keyBytes = null;

		try
		{
			Console.SetOut(output);
			Console.SetError(error);

			int exitCode = await Program.Main(new[]
			{
				"init-hmac-key",
				"--output",
				keyPath,
				"--i-understand-live-r0"
			});

			Assert.Equal(0, exitCode);
			keyBytes = await File.ReadAllBytesAsync(keyPath);
			Assert.Equal(32, keyBytes.Length);
			Assert.True(
				AntigravityPrivateKeyAcl.IsPrivateDirectory(keyDirectory));
			Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(keyPath));
			DirectorySecurity directorySecurity = new DirectoryInfo(keyDirectory)
				.GetAccessControl(AccessControlSections.Access);
			FileSecurity fileSecurity = new FileInfo(keyPath)
				.GetAccessControl(AccessControlSections.Access);
			Assert.True(directorySecurity.AreAccessRulesProtected);
			Assert.True(fileSecurity.AreAccessRulesProtected);
			AssertNoBroadAllowRules(directorySecurity);
			AssertNoBroadAllowRules(fileSecurity);
			string keyHex = Convert.ToHexString(keyBytes);
			string consoleText = output.ToString() + error.ToString();
			Assert.DoesNotContain(
				keyHex,
				consoleText,
				StringComparison.OrdinalIgnoreCase);
			Assert.Contains(
				"HMAC key initialized.",
				output.ToString(),
				StringComparison.Ordinal);
		}
		finally
		{
			Console.SetOut(originalOutput);
			Console.SetError(originalError);

			if (keyBytes is not null)
			{
				CryptographicOperations.ZeroMemory(keyBytes);
			}
		}
	}

	[Fact]
	public async Task Main_InitHmacKey_WhenFileExists_FailsWithoutChangingIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-key"));
		string keyPath = Path.GetFullPath(Path.Combine(
			keyDirectory,
			"live-r0.key"));
		TextWriter originalOutput = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter output = new();
		using StringWriter error = new();
		byte[]? originalKey = null;
		byte[]? unchangedKey = null;

		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			string[] args =
			{
				"init-hmac-key",
				"--output",
				keyPath,
				"--i-understand-live-r0"
			};

			int firstExitCode = await Program.Main(args);
			originalKey = await File.ReadAllBytesAsync(keyPath);
			int secondExitCode = await Program.Main(args);
			unchangedKey = await File.ReadAllBytesAsync(keyPath);

			Assert.Equal(0, firstExitCode);
			Assert.Equal(2, secondExitCode);
			Assert.Equal(originalKey, unchangedKey);
			Assert.Contains(
				"failed closed at stage: CreateKeyFile.",
				error.ToString(),
				StringComparison.Ordinal);
		}
		finally
		{
			Console.SetOut(originalOutput);
			Console.SetError(originalError);

			if (originalKey is not null)
			{
				CryptographicOperations.ZeroMemory(originalKey);
			}

			if (unchangedKey is not null)
			{
				CryptographicOperations.ZeroMemory(unchangedKey);
			}
		}
	}

	[Fact]
	public async Task Main_InitHmacKey_WithExistingNonPrivateDirectory_ReportsSafePrepareReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string existingDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"existing-broad-directory"));
		Directory.CreateDirectory(existingDirectory);
		string keyPath = Path.GetFullPath(Path.Combine(
			existingDirectory,
			"live-r0.key"));
		TextWriter originalOutput = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter output = new();
		using StringWriter error = new();

		try
		{
			Console.SetOut(output);
			Console.SetError(error);

			int exitCode = await Program.Main(new[]
			{
				"init-hmac-key",
				"--output",
				keyPath,
				"--i-understand-live-r0"
			});

			Assert.Equal(2, exitCode);
			Assert.False(File.Exists(keyPath));
			Assert.True(Directory.Exists(existingDirectory));
			Assert.Contains(
				"failed closed at stage: " +
					"PreparePrivateDirectory.ExistingDirectoryNotPrivate.",
				error.ToString(),
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				keyPath,
				error.ToString(),
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			Console.SetOut(originalOutput);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public async Task Main_PinPrompt_WithEmptyPins_UpdatesOnlyPinsAndKeepsProfileSafe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, AntigravityLiveR0ProfileFile originalProfile) =
			await CreatePrivateProfileAsync(temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreatePinPromptArguments(
				profilePath,
				StructuralFingerprint,
				ExactFingerprint));
		AntigravityLiveR0ProfileFile? updatedProfile =
			JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
				await File.ReadAllTextAsync(profilePath));
		byte[] updatedBytes = await File.ReadAllBytesAsync(profilePath);

		Assert.Equal(0, exitCode);
		Assert.NotNull(updatedProfile);
		Assert.Equal(
			StructuralFingerprint,
			updatedProfile.ExpectedPromptStructuralFingerprint);
		Assert.Equal(
			ExactFingerprint,
			updatedProfile.ExpectedExactPromptFingerprint);
		AssertSameNonPinFields(originalProfile, updatedProfile);
		Assert.Equal(
			CreateExpectedPinnedBytes(
				originalBytes,
				StructuralFingerprint,
				ExactFingerprint),
			updatedBytes);
		AssertSafeInheritedProfileAcl(profilePath);
		string consoleText = output + error;
		Assert.DoesNotContain(
			StructuralFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			ExactFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			profilePath,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PinPrompt_WithSamePins_IsIdempotentWithoutRewritingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string[] arguments = CreatePinPromptArguments(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint);

		(int firstExitCode, _, _) = await InvokeMainAsync(arguments);
		DateTime expectedLastWriteTimeUtc = new(2024, 1, 2, 3, 4, 5,
			DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(profilePath, expectedLastWriteTimeUtc);
		byte[] bytesBeforeSecondCall = await File.ReadAllBytesAsync(profilePath);
		DateTime lastWriteTimeBeforeSecondCall =
			File.GetLastWriteTimeUtc(profilePath);

		(int secondExitCode, string output, string error) =
			await InvokeMainAsync(arguments);
		byte[] bytesAfterSecondCall = await File.ReadAllBytesAsync(profilePath);
		DateTime lastWriteTimeAfterSecondCall =
			File.GetLastWriteTimeUtc(profilePath);

		Assert.Equal(0, firstExitCode);
		Assert.Equal(0, secondExitCode);
		Assert.Equal(bytesBeforeSecondCall, bytesAfterSecondCall);
		Assert.Equal(
			lastWriteTimeBeforeSecondCall,
			lastWriteTimeAfterSecondCall);
		AssertSafeInheritedProfileAcl(profilePath);
		string consoleText = output + error;
		Assert.DoesNotContain(
			StructuralFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			ExactFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			profilePath,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PinPrompt_WithDifferentOrPartialPins_FailsWithoutChangingProfile()
	{
		(string Structural, string Exact)[] existingPins =
		{
			(new string('C', 64), new string('D', 64)),
			(new string('C', 64), string.Empty),
			(string.Empty, new string('D', 64))
		};

		foreach ((string existingStructural, string existingExact) in
			existingPins)
		{
			using TemporaryDirectory temporaryDirectory = new();
			(string profilePath, _) = await CreatePrivateProfileAsync(
				temporaryDirectory,
				existingStructural,
				existingExact);
			byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

			(int exitCode, string output, string error) =
				await InvokeMainAsync(CreatePinPromptArguments(
					profilePath,
					StructuralFingerprint,
					ExactFingerprint));

			Assert.Equal(2, exitCode);
			Assert.Equal(
				originalBytes,
				await File.ReadAllBytesAsync(profilePath));
			Assert.Contains(
				"pinning failed closed",
				error,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				profilePath,
				output + error,
				StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public async Task Main_PinPrompt_WithInvalidFingerprint_FailsWithoutChangingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		(string Structural, string Exact)[] invalidFingerprints =
		{
			(string.Empty, ExactFingerprint),
			(new string('A', 63), ExactFingerprint),
			(new string('A', 65), ExactFingerprint),
			(new string('A', 63) + "G", ExactFingerprint),
			(StructuralFingerprint, string.Empty),
			(StructuralFingerprint, new string('B', 63)),
			(StructuralFingerprint, new string('B', 65)),
			(StructuralFingerprint, new string('B', 63) + "g")
		};

		foreach ((string structural, string exact) in invalidFingerprints)
		{
			(int exitCode, _, string error) = await InvokeMainAsync(
				CreatePinPromptArguments(profilePath, structural, exact));

			Assert.Equal(2, exitCode);
			Assert.Contains(
				"pinning failed closed",
				error,
				StringComparison.Ordinal);
			Assert.Equal(
				originalBytes,
				await File.ReadAllBytesAsync(profilePath));
		}
	}

	[Fact]
	public async Task Main_PinPrompt_WithInvalidJson_FailsWithoutChangingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string validProfilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string validJson = await File.ReadAllTextAsync(validProfilePath);
		JsonObject unknownProperty = JsonNode.Parse(validJson)!.AsObject();
		unknownProperty["UnexpectedProperty"] = true;
		string duplicateProperty = validJson.Insert(
			validJson.IndexOf('{') + 1,
			"\"Id\":\"duplicate-id\",");
		string[] invalidJsonCases =
		{
			"{",
			unknownProperty.ToJsonString(),
			duplicateProperty,
			RemoveJsonProperty(validJson, "Id"),
			RemoveJsonProperty(validJson, "Rows")
		};
		string privateDirectory = Path.GetDirectoryName(validProfilePath)!;

		for (int index = 0; index < invalidJsonCases.Length; index++)
		{
			string profilePath = Path.Combine(
				privateDirectory,
				$"invalid-{index}.json");
			await File.WriteAllTextAsync(profilePath, invalidJsonCases[index]);
			AssertSafeInheritedProfileAcl(profilePath);
			byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

			(int exitCode, _, string error) = await InvokeMainAsync(
				CreatePinPromptArguments(
					profilePath,
					StructuralFingerprint,
					ExactFingerprint));

			Assert.Equal(2, exitCode);
			Assert.Contains(
				"pinning failed closed",
				error,
				StringComparison.Ordinal);
			Assert.Equal(
				originalBytes,
				await File.ReadAllBytesAsync(profilePath));
		}
	}

	[Fact]
	public async Task Main_PinPrompt_WithProfileInBroadDirectory_FailsWithoutChangingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string privateProfilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string broadDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"broad-profile"));
		Directory.CreateDirectory(broadDirectory);
		Assert.False(
			AntigravityPrivateKeyAcl.IsPrivateDirectory(broadDirectory));
		string broadProfilePath = Path.Combine(
			broadDirectory,
			"profile.json");
		await File.WriteAllBytesAsync(
			broadProfilePath,
			await File.ReadAllBytesAsync(privateProfilePath));
		byte[] originalBytes = await File.ReadAllBytesAsync(broadProfilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreatePinPromptArguments(
				broadProfilePath,
				StructuralFingerprint,
				ExactFingerprint));

		Assert.Equal(2, exitCode);
		Assert.Equal(
			originalBytes,
			await File.ReadAllBytesAsync(broadProfilePath));
		Assert.Contains(
			"pinning failed closed",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			broadProfilePath,
			output + error,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PinPrompt_WithWrongArguments_ReturnsUnknownBeforeReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		string[][] invalidArguments =
		{
			new[]
			{
				"pin-prompt", "--structural", StructuralFingerprint,
				"--profile", unreadProfilePath, "--exact", ExactFingerprint,
				"--i-understand-live-r0"
			},
			new[]
			{
				"pin-prompt", "--profile", unreadProfilePath,
				"--structural", StructuralFingerprint,
				"--exact", ExactFingerprint
			},
			new[]
			{
				"pin-prompt", "--profile", unreadProfilePath,
				"--structural", StructuralFingerprint,
				"--exact", ExactFingerprint,
				"--i-understand-live-r0", "extra"
			},
			new[]
			{
				"pin-prompt", "--profiles", unreadProfilePath,
				"--structural", StructuralFingerprint,
				"--exact", ExactFingerprint,
				"--i-understand-live-r0"
			}
		};

		foreach (string[] arguments in invalidArguments)
		{
			(int exitCode, _, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Contains(
				"Unknown command.",
				error,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"pinning failed",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(unreadProfilePath));
		}
	}

	[Fact]
	public async Task Main_RepinPrompt_WithExpectedPins_UpdatesOnlyPinsAndKeepsProfileSafe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, AntigravityLiveR0ProfileFile originalProfile) =
			await CreatePrivateProfileAsync(
				temporaryDirectory,
				StructuralFingerprint,
				ExactFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateRepinPromptArguments(
				profilePath,
				StructuralFingerprint,
				ExactFingerprint,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));
		byte[] updatedBytes = await File.ReadAllBytesAsync(profilePath);
		AntigravityLiveR0ProfileFile? updatedProfile =
			JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
				updatedBytes);
		TestFileState updatedState = CaptureTestFileState(profilePath);

		Assert.Equal(0, exitCode);
		Assert.NotNull(updatedProfile);
		Assert.Equal(
			ReplacementStructuralFingerprint,
			updatedProfile.ExpectedPromptStructuralFingerprint);
		Assert.Equal(
			ReplacementExactFingerprint,
			updatedProfile.ExpectedExactPromptFingerprint);
		AssertSameNonPinFields(originalProfile, updatedProfile);
		Assert.Equal(
			CreateExpectedRepinnedBytes(
				originalBytes,
				StructuralFingerprint,
				ExactFingerprint,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint),
			updatedBytes);
		Assert.Equal(originalState.Length, updatedState.Length);
		Assert.Equal(
			originalState.CreationTimeUtc,
			updatedState.CreationTimeUtc);
		Assert.Equal(originalState.Attributes, updatedState.Attributes);
		AssertSafeInheritedProfileAcl(profilePath);
		AssertNoPromptPinArtifacts(profilePath);
		string consoleText = output + error;
		Assert.DoesNotContain(
			profilePath,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			StructuralFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			ReplacementStructuralFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_RepinPrompt_WithWrongExpectedPins_FailsWithoutChangingProfileOrDebris()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateRepinPromptArguments(
				profilePath,
				new string('E', 64),
				new string('F', 64),
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));

		Assert.Equal(2, exitCode);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(originalState, CaptureTestFileState(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		Assert.Contains(
			"repinning failed closed",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			profilePath,
			output + error,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_RepinPrompt_WithTargetAlreadyPresent_IsIdempotentWithoutRewritingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			ReplacementStructuralFingerprint,
			ReplacementExactFingerprint);
		DateTime expectedLastWriteTimeUtc = new(
			2024,
			3,
			4,
			5,
			6,
			7,
			DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(profilePath, expectedLastWriteTimeUtc);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

		(int exitCode, _, _) = await InvokeMainAsync(
			CreateRepinPromptArguments(
				profilePath,
				StructuralFingerprint,
				ExactFingerprint,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));

		Assert.Equal(0, exitCode);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(
			expectedLastWriteTimeUtc,
			File.GetLastWriteTimeUtc(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task Main_RepinPrompt_WithSameExpectedAndTarget_FailsWithoutReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreateRepinPromptArguments(
				unreadProfilePath,
				StructuralFingerprint,
				ExactFingerprint,
				StructuralFingerprint,
				ExactFingerprint));

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		Assert.Contains(
			"repinning failed closed",
			error,
			StringComparison.Ordinal);
		Assert.False(File.Exists(unreadProfilePath));
		Assert.False(File.Exists(GetPromptPinLockPath(unreadProfilePath)));
	}

	[Fact]
	public async Task Main_RepinPrompt_WithWrongArguments_ReturnsUnknownBeforeReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		string[][] invalidArguments =
		{
			new[]
			{
				"repin-prompt", "--profile", unreadProfilePath,
				"--expected-exact", ExactFingerprint,
				"--expected-structural", StructuralFingerprint,
				"--structural", ReplacementStructuralFingerprint,
				"--exact", ReplacementExactFingerprint,
				"--i-understand-live-r0"
			},
			new[]
			{
				"repin-prompt", "--profile", unreadProfilePath,
				"--expected-structural", StructuralFingerprint,
				"--expected-exact", ExactFingerprint,
				"--structural", ReplacementStructuralFingerprint,
				"--exact", ReplacementExactFingerprint
			},
			new[]
			{
				"repin-prompt", "--profile", unreadProfilePath,
				"--expected-structural", StructuralFingerprint,
				"--expected-exact", ExactFingerprint,
				"--structural", ReplacementStructuralFingerprint,
				"--exact", ReplacementExactFingerprint,
				"--i-understand-live-r0", "extra"
			},
			new[]
			{
				"repin-prompt", "--profiles", unreadProfilePath,
				"--expected-structural", StructuralFingerprint,
				"--expected-exact", ExactFingerprint,
				"--structural", ReplacementStructuralFingerprint,
				"--exact", ReplacementExactFingerprint,
				"--i-understand-live-r0"
			}
		};

		foreach (string[] arguments in invalidArguments)
		{
			(int exitCode, _, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Contains(
				"Unknown command.",
				error,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"repinning failed",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(unreadProfilePath));
		}
	}

	[Fact]
	public async Task Main_PinPrompt_AfterPinsExist_StillRefusesOverwrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

		(int exitCode, _, string error) = await InvokeMainAsync(
			CreatePinPromptArguments(
				profilePath,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));

		Assert.Equal(2, exitCode);
		Assert.Contains(
			"pinning failed closed",
			error,
			StringComparison.Ordinal);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
	}

	[Fact]
	public async Task Main_PinUsage_WithNullPins_UpdatesOnlyUsageTokensAndKeepsProfileSafe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, AntigravityLiveR0ProfileFile originalProfile) =
			await CreatePrivateProfileAsync(
				temporaryDirectory,
				StructuralFingerprint,
				ExactFingerprint,
				useDefaultUsagePins: false);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreatePinUsageArguments(
				profilePath,
				UsageStructuralFingerprint,
				UsageExactFingerprint));
		byte[] updatedBytes = await File.ReadAllBytesAsync(profilePath);
		AntigravityLiveR0ProfileFile? updatedProfile =
			JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
				updatedBytes);
		TestFileState updatedState = CaptureTestFileState(profilePath);

		Assert.Equal(0, exitCode);
		Assert.NotNull(updatedProfile);
		Assert.Equal(
			UsageStructuralFingerprint,
			updatedProfile.ExpectedUsageStructuralFingerprint);
		Assert.Equal(
			UsageExactFingerprint,
			updatedProfile.ExpectedExactUsageFingerprint);
		Assert.Equal(
			originalProfile.ExpectedPromptStructuralFingerprint,
			updatedProfile.ExpectedPromptStructuralFingerprint);
		Assert.Equal(
			originalProfile.ExpectedExactPromptFingerprint,
			updatedProfile.ExpectedExactPromptFingerprint);
		AssertSameNonPinFields(
			originalProfile with
			{
				ExpectedUsageStructuralFingerprint =
					UsageStructuralFingerprint,
				ExpectedExactUsageFingerprint = UsageExactFingerprint
			},
			updatedProfile);
		Assert.Equal(
			CreateExpectedUsagePinnedBytes(
				originalBytes,
				UsageStructuralFingerprint,
				UsageExactFingerprint),
			updatedBytes);
		Assert.Equal(
			originalState.CreationTimeUtc,
			updatedState.CreationTimeUtc);
		Assert.Equal(originalState.Attributes, updatedState.Attributes);
		AssertSafeInheritedProfileAcl(profilePath);
		AssertNoPromptPinArtifacts(profilePath);
		string consoleText = output + error;
		Assert.Contains(
			"usage fingerprints pinned",
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			profilePath,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			UsageStructuralFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			UsageExactFingerprint,
			consoleText,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PinUsage_WithReversedWhitespaceAndUtf8Bom_PreservesAllOtherBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, AntigravityLiveR0ProfileFile originalProfile) =
			await CreatePrivateProfileAsync(
				temporaryDirectory,
				StructuralFingerprint,
				ExactFingerprint,
				useDefaultUsagePins: false);
		string structuralPropertyName = nameof(
			AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint);
		string exactPropertyName = nameof(
			AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint);
		JsonObject source = JsonSerializer.SerializeToNode(originalProfile)!
			.AsObject();
		JsonObject reordered = new()
		{
			[exactPropertyName] = null
		};

		foreach (KeyValuePair<string, JsonNode?> property in source)
		{
			if (string.Equals(
					property.Key,
					structuralPropertyName,
					StringComparison.Ordinal) ||
				string.Equals(
					property.Key,
					exactPropertyName,
					StringComparison.Ordinal))
			{
				continue;
			}

			reordered[property.Key] = property.Value?.DeepClone();
		}

		reordered[structuralPropertyName] = null;
		string alternativeJson = reordered.ToJsonString(
			new JsonSerializerOptions { WriteIndented = true });
		byte[] preamble = Encoding.UTF8.Preamble.ToArray();
		byte[] content = Encoding.UTF8.GetBytes(alternativeJson);
		byte[] originalBytes = new byte[preamble.Length + content.Length];
		preamble.CopyTo(originalBytes, 0);
		content.CopyTo(originalBytes, preamble.Length);
		await File.WriteAllBytesAsync(profilePath, originalBytes);
		AssertSafeInheritedProfileAcl(profilePath);
		Assert.True(originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
		Assert.True(
			alternativeJson.IndexOf(
				exactPropertyName,
				StringComparison.Ordinal) <
			alternativeJson.IndexOf(
				structuralPropertyName,
				StringComparison.Ordinal));

		string expectedJson = Encoding.UTF8.GetString(originalBytes)
			.Replace(
				$"\"{exactPropertyName}\": null",
				$"\"{exactPropertyName}\": \"{UsageExactFingerprint}\"",
				StringComparison.Ordinal)
			.Replace(
				$"\"{structuralPropertyName}\": null",
				$"\"{structuralPropertyName}\": \"{UsageStructuralFingerprint}\"",
				StringComparison.Ordinal);

		(int exitCode, _, _) = await InvokeMainAsync(
			CreatePinUsageArguments(
				profilePath,
				UsageStructuralFingerprint,
				UsageExactFingerprint));

		Assert.Equal(0, exitCode);
		Assert.Equal(
			Encoding.UTF8.GetBytes(expectedJson),
			await File.ReadAllBytesAsync(profilePath));
		AssertSafeInheritedProfileAcl(profilePath);
		AssertNoPromptPinArtifacts(profilePath);
	}

	[Fact]
	public async Task Main_PinUsage_WithDifferentExistingPair_FailsWithoutChangingProfileOrDebris()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);

		(int exitCode, string output, string error) = await InvokeMainAsync(
			CreatePinUsageArguments(
				profilePath,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));

		Assert.Equal(2, exitCode);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(originalState, CaptureTestFileState(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		Assert.Contains(
			"usage fingerprint pinning failed closed",
			error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			profilePath,
			output + error,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Main_PinUsage_WithTargetAlreadyPresent_IsIdempotentWithoutRewritingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint,
			useDefaultUsagePins: false,
			usageStructuralFingerprint: ReplacementStructuralFingerprint,
			usageExactFingerprint: ReplacementExactFingerprint);
		DateTime expectedLastWriteTimeUtc = new(
			2024,
			5,
			6,
			7,
			8,
			9,
			DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(profilePath, expectedLastWriteTimeUtc);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

		(int exitCode, _, _) = await InvokeMainAsync(
			CreatePinUsageArguments(
				profilePath,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint));

		Assert.Equal(0, exitCode);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(
			expectedLastWriteTimeUtc,
			File.GetLastWriteTimeUtc(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task Main_PinUsage_WithWrongArguments_FailsBeforeReadingProfile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string unreadProfilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"must-not-be-read.json"));
		string[][] invalidArguments =
		{
			new[]
			{
				"pin-usage", "--structural", UsageStructuralFingerprint,
				"--profile", unreadProfilePath, "--exact",
				UsageExactFingerprint, "--i-understand-live-r0"
			},
			new[]
			{
				"pin-usage", "--profile", unreadProfilePath,
				"--structural", UsageStructuralFingerprint,
				"--exact", UsageExactFingerprint
			},
			new[]
			{
				"pin-usage", "--profile", unreadProfilePath,
				"--structural", UsageStructuralFingerprint,
				"--exact", UsageExactFingerprint,
				"--i-understand-live-r0", "extra"
			},
			new[]
			{
				"pin-usage", "--profiles", unreadProfilePath,
				"--structural", UsageStructuralFingerprint,
				"--exact", UsageExactFingerprint,
				"--i-understand-live-r0"
			}
		};

		foreach (string[] arguments in invalidArguments)
		{
			(int exitCode, string output, string error) =
				await InvokeMainAsync(arguments);

			Assert.Equal(2, exitCode);
			Assert.Empty(output);
			Assert.Contains("Unknown command.", error, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"usage fingerprint pinning",
				error,
				StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(unreadProfilePath));
			Assert.False(File.Exists(GetPromptPinLockPath(unreadProfilePath)));
		}

		(int invalidExitCode, _, string invalidError) = await InvokeMainAsync(
			CreatePinUsageArguments(
				unreadProfilePath,
				new string('A', 63),
				UsageExactFingerprint));
		Assert.Equal(2, invalidExitCode);
		Assert.Contains(
			"usage fingerprint pinning failed closed",
			invalidError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(unreadProfilePath));
		Assert.False(File.Exists(GetPromptPinLockPath(unreadProfilePath)));
	}

	[Fact]
	public async Task Main_PinUsage_RequiresLiteralNullPairAndFullyPinnedPrompt()
	{
		(string? Structural, string? Exact, string PromptStructural,
			string PromptExact)[] invalidStates =
		{
			(string.Empty, string.Empty, StructuralFingerprint,
				ExactFingerprint),
			("null", "null", StructuralFingerprint, ExactFingerprint),
			(null, UsageExactFingerprint, StructuralFingerprint,
				ExactFingerprint),
			(null, null, string.Empty, string.Empty)
		};

		foreach ((string? usageStructural, string? usageExact,
			string promptStructural, string promptExact) in invalidStates)
		{
			using TemporaryDirectory temporaryDirectory = new();
			(string profilePath, _) = await CreatePrivateProfileAsync(
				temporaryDirectory,
				promptStructural,
				promptExact,
				useDefaultUsagePins: false,
				usageStructuralFingerprint: usageStructural,
				usageExactFingerprint: usageExact);
			byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
			TestFileState originalState = CaptureTestFileState(profilePath);

			(int exitCode, _, _) = await InvokeMainAsync(
				CreatePinUsageArguments(
					profilePath,
					UsageStructuralFingerprint,
					UsageExactFingerprint));

			Assert.Equal(2, exitCode);
			Assert.Equal(
				originalBytes,
				await File.ReadAllBytesAsync(profilePath));
			Assert.Equal(originalState, CaptureTestFileState(profilePath));
			AssertNoPromptPinArtifacts(profilePath);
		}
	}

	[Fact]
	public async Task PinUsage_AndPromptUpdates_UseTheSameLegacyLock()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint,
			useDefaultUsagePins: false);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		string lockPath = GetPromptPinLockPath(profilePath);

		await using (FileStream lockStream = new(
			lockPath,
			FileMode.CreateNew,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			bool usageResult = await AntigravityPromptPinWriter.TryPinUsageAsync(
				profilePath,
				UsageStructuralFingerprint,
				UsageExactFingerprint);
			bool promptResult = await AntigravityPromptPinWriter.TryRepinAsync(
				profilePath,
				StructuralFingerprint,
				ExactFingerprint,
				ReplacementStructuralFingerprint,
				ReplacementExactFingerprint);

			Assert.False(usageResult);
			Assert.False(promptResult);
			Assert.Equal(
				originalBytes,
				await File.ReadAllBytesAsync(profilePath));
			Assert.True(File.Exists(lockPath));
		}

		File.Delete(lockPath);
		AssertNoPromptPinArtifacts(profilePath);
	}

	[Fact]
	public async Task Main_PinPrompt_WithNullUsagePins_LeavesUsageTokensUntouched()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			useDefaultUsagePins: false);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);

		(int exitCode, _, _) = await InvokeMainAsync(
			CreatePinPromptArguments(
				profilePath,
				StructuralFingerprint,
				ExactFingerprint));
		byte[] updatedBytes = await File.ReadAllBytesAsync(profilePath);
		AntigravityLiveR0ProfileFile? updatedProfile =
			JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
				updatedBytes);

		Assert.Equal(0, exitCode);
		Assert.NotNull(updatedProfile);
		Assert.Null(updatedProfile.ExpectedUsageStructuralFingerprint);
		Assert.Null(updatedProfile.ExpectedExactUsageFingerprint);
		Assert.Equal(
			CreateExpectedPinnedBytes(
				originalBytes,
				StructuralFingerprint,
				ExactFingerprint),
			updatedBytes);
		AssertNoPromptPinArtifacts(profilePath);
	}

	[Fact]
	public async Task PinUsage_AfterUncertainCommit_RestoresOriginalThenRetries()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint,
			useDefaultUsagePins: false);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, source, destination, backup) =>
			{
				File.Replace(
					source,
					destination,
					backup,
					ignoreMetadataErrors: false);

				if (call == 1)
				{
					throw new IOException(
						"Synthetic success-then-throw commit failure.");
				}
			}
		};

		bool uncertainResult =
			await AntigravityPromptPinWriter.TryPinUsageAsync(
				profilePath,
				UsageStructuralFingerprint,
				UsageExactFingerprint,
				replacer);

		Assert.False(uncertainResult);
		Assert.Equal(2, replacer.ReplaceCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);

		bool retryResult = await AntigravityPromptPinWriter.TryPinUsageAsync(
			profilePath,
			UsageStructuralFingerprint,
			UsageExactFingerprint);

		Assert.True(retryResult);
		Assert.Equal(
			CreateExpectedUsagePinnedBytes(
				originalBytes,
				UsageStructuralFingerprint,
				UsageExactFingerprint),
			await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinTargets_RejectExactFingerprintCollisionWithoutChangingProfile()
	{
		using TemporaryDirectory usageDirectory = new();
		(string usageProfilePath, _) = await CreatePrivateProfileAsync(
			usageDirectory,
			StructuralFingerprint,
			ExactFingerprint,
			useDefaultUsagePins: false);
		byte[] usageOriginalBytes =
			await File.ReadAllBytesAsync(usageProfilePath);

		bool usageResult = await AntigravityPromptPinWriter.TryPinUsageAsync(
			usageProfilePath,
			UsageStructuralFingerprint,
			ExactFingerprint);

		Assert.False(usageResult);
		Assert.Equal(
			usageOriginalBytes,
			await File.ReadAllBytesAsync(usageProfilePath));
		AssertNoPromptPinArtifacts(usageProfilePath);

		using TemporaryDirectory promptDirectory = new();
		(string promptProfilePath, _) = await CreatePrivateProfileAsync(
			promptDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] promptOriginalBytes =
			await File.ReadAllBytesAsync(promptProfilePath);

		bool promptResult = await AntigravityPromptPinWriter.TryRepinAsync(
			promptProfilePath,
			StructuralFingerprint,
			ExactFingerprint,
			ReplacementStructuralFingerprint,
			UsageExactFingerprint);

		Assert.False(promptResult);
		Assert.Equal(
			promptOriginalBytes,
			await File.ReadAllBytesAsync(promptProfilePath));
		AssertNoPromptPinArtifacts(promptProfilePath);
	}

	[Fact]
	public async Task RepinPrompt_AfterUncertainCommit_RestoresExpectedThenRetriesIdempotently()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, source, destination, backup) =>
			{
				File.Replace(
					source,
					destination,
					backup,
					ignoreMetadataErrors: false);

				if (call == 1)
				{
					throw new IOException(
						"Synthetic success-then-throw commit failure.");
				}
			}
		};

		bool uncertainResult = await AntigravityPromptPinWriter.TryRepinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			ReplacementStructuralFingerprint,
			ReplacementExactFingerprint,
			replacer);

		Assert.False(uncertainResult);
		Assert.Equal(2, replacer.ReplaceCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);

		bool retryResult = await AntigravityPromptPinWriter.TryRepinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			ReplacementStructuralFingerprint,
			ReplacementExactFingerprint);
		byte[] bytesAfterRetry = await File.ReadAllBytesAsync(profilePath);
		DateTime expectedLastWriteTimeUtc = new(
			2024,
			4,
			5,
			6,
			7,
			8,
			DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(profilePath, expectedLastWriteTimeUtc);

		bool idempotentResult = await AntigravityPromptPinWriter.TryRepinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			ReplacementStructuralFingerprint,
			ReplacementExactFingerprint);

		Assert.True(retryResult);
		Assert.True(idempotentResult);
		Assert.Equal(bytesAfterRetry, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(
			expectedLastWriteTimeUtc,
			File.GetLastWriteTimeUtc(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WithPreCanceledToken_FailsWithoutChangingProfileOrLeavingFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		string privateDirectory = Path.GetDirectoryName(profilePath)!;
		DateTime expectedLastWriteTimeUtc = new(2024, 2, 3, 4, 5, 6,
			DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(profilePath, expectedLastWriteTimeUtc);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		string[] originalFiles = Directory.GetFiles(privateDirectory)
			.Select(Path.GetFileName)
			.Order(StringComparer.Ordinal)
			.ToArray()!;
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			cancellation.Token);

		Assert.False(isPinned);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(
			expectedLastWriteTimeUtc,
			File.GetLastWriteTimeUtc(profilePath));
		Assert.Equal(
			originalFiles,
			Directory.GetFiles(privateDirectory)
				.Select(Path.GetFileName)
				.Order(StringComparer.Ordinal)
				.ToArray()!);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenCommitReplacePartiallyMovesOriginalThenThrows_RestoresOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, _, destination, backup) =>
			{
				Assert.Equal(1, call);
				File.Move(destination, backup);
				throw CreatePartialReplaceException();
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer);

		Assert.False(isPinned);
		Assert.Equal(1, replacer.ReplaceCallCount);
		Assert.Equal(1, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(originalState, CaptureTestFileState(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenCommitSucceedsThenThrows_RollsBackToOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, source, destination, backup) =>
			{
				File.Replace(
					source,
					destination,
					backup,
					ignoreMetadataErrors: false);

				if (call == 1)
				{
					throw new IOException(
						"Synthetic success-then-throw commit failure.");
				}
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer);

		Assert.False(isPinned);
		Assert.Equal(2, replacer.ReplaceCallCount);
		Assert.Equal(0, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenRollbackReplacePartiallyFails_UsesMissingProfileMoveFallback()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, source, destination, backup) =>
			{
				if (call == 1)
				{
					File.Replace(
						source,
						destination,
						backup,
						ignoreMetadataErrors: false);
					throw new IOException(
						"Synthetic success-then-throw commit failure.");
				}

				Assert.Equal(2, call);
				File.Move(destination, backup);
				throw CreatePartialReplaceException();
			},
			MoveAction = (call, source, destination) =>
			{
				Assert.Equal(1, call);
				Assert.Contains(
					".backup.",
					Path.GetFileName(source),
					StringComparison.Ordinal);
				File.Move(source, destination);
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer);

		Assert.False(isPinned);
		Assert.Equal(2, replacer.ReplaceCallCount);
		Assert.Equal(1, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(originalState, CaptureTestFileState(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenCancellationOccursInsideCommitWindow_RestoresOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		using CancellationTokenSource cancellation = new();
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, _, _, _) =>
			{
				Assert.Equal(1, call);
				cancellation.Cancel();
				throw new OperationCanceledException(cancellation.Token);
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer,
			cancellation.Token);

		Assert.False(isPinned);
		Assert.True(cancellation.IsCancellationRequested);
		Assert.Equal(1, replacer.ReplaceCallCount);
		Assert.Equal(0, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		AssertNoPromptPinArtifacts(profilePath);
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenBackupContainsCompetingVersion_RestoresOriginalAndPreservesEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		byte[] competingBytes = Encoding.UTF8.GetBytes(
			Encoding.UTF8.GetString(originalBytes).Replace(
				"synthetic-live-r0",
				"competing-live-r0",
				StringComparison.Ordinal));
		Assert.False(originalBytes.AsSpan().SequenceEqual(competingBytes));
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, _, destination, backup) =>
			{
				Assert.Equal(1, call);
				File.Move(destination, backup);
				File.WriteAllBytes(backup, competingBytes);
				throw CreatePartialReplaceException();
			},
			MoveAction = (call, source, destination) =>
			{
				Assert.Equal(1, call);
				Assert.Contains(
					".recovery.",
					Path.GetFileName(source),
					StringComparison.Ordinal);
				File.Move(source, destination);
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer);

		Assert.False(isPinned);
		Assert.Equal(1, replacer.ReplaceCallCount);
		Assert.Equal(1, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		string[] backupPaths = GetPromptPinArtifactPaths(
			profilePath,
			"backup");
		string backupPath = Assert.Single(backupPaths);
		Assert.Equal(competingBytes, await File.ReadAllBytesAsync(backupPath));
		AssertSafeInheritedProfileAcl(backupPath);
		string newPath = Assert.Single(GetPromptPinArtifactPaths(
			profilePath,
			"new"));
		Assert.Equal(
			CreateExpectedPinnedBytes(
				originalBytes,
				StructuralFingerprint,
				ExactFingerprint),
			await File.ReadAllBytesAsync(newPath));
		Assert.False(File.Exists(GetPromptPinLockPath(profilePath)));
		AssertSafeInheritedProfileAcl(profilePath);
	}

	[Fact]
	public async Task PinPrompt_WhenRollbackWindowLosesRace_RestoresOriginalAndPreservesCompetitor()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string profilePath, _) = await CreatePrivateProfileAsync(
			temporaryDirectory);
		byte[] originalBytes = await File.ReadAllBytesAsync(profilePath);
		TestFileState originalState = CaptureTestFileState(profilePath);
		byte[] updatedBytes = CreateExpectedPinnedBytes(
			originalBytes,
			StructuralFingerprint,
			ExactFingerprint);
		byte[] competingBytes = Encoding.UTF8.GetBytes(
			Encoding.UTF8.GetString(updatedBytes).Replace(
				"synthetic-live-r0",
				"competing-live-r0",
				StringComparison.Ordinal));
		Assert.False(updatedBytes.AsSpan().SequenceEqual(competingBytes));
		ScriptedPromptPinFileReplacer replacer = new()
		{
			ReplaceAction = (call, source, destination, backup) =>
			{
				if (call == 1)
				{
					File.Replace(
						source,
						destination,
						backup,
						ignoreMetadataErrors: false);
					throw new IOException(
						"Synthetic success-then-throw commit failure.");
				}

				Assert.Equal(2, call);
				File.WriteAllBytes(destination, competingBytes);
				File.Replace(
					source,
					destination,
					backup,
					ignoreMetadataErrors: false);
			}
		};

		bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
			profilePath,
			StructuralFingerprint,
			ExactFingerprint,
			replacer);

		Assert.False(isPinned);
		Assert.Equal(2, replacer.ReplaceCallCount);
		Assert.Equal(0, replacer.MoveCallCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
		Assert.Equal(originalState, CaptureTestFileState(profilePath));
		string failedNewPath = Assert.Single(GetPromptPinArtifactPaths(
			profilePath,
			"failed-new"));
		Assert.Equal(
			competingBytes,
			await File.ReadAllBytesAsync(failedNewPath));
		AssertSafeInheritedProfileAcl(failedNewPath);
		Assert.False(File.Exists(GetPromptPinLockPath(profilePath)));
		AssertSafeInheritedProfileAcl(profilePath);
	}

	private static async Task<(string ProfilePath,
		AntigravityLiveR0ProfileFile Profile)> CreatePrivateProfileAsync(
		TemporaryDirectory temporaryDirectory,
		string structuralFingerprint = "",
		string exactFingerprint = "",
		bool useDefaultUsagePins = true,
		string? usageStructuralFingerprint = null,
		string? usageExactFingerprint = null)
	{
		string privateDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-prompt-profile"));
		string keyPath = Path.GetFullPath(Path.Combine(
			privateDirectory,
			"screen.key"));
		(int keyExitCode, _, _) = await InvokeMainAsync(new[]
		{
			"init-hmac-key",
			"--output",
			keyPath,
			"--i-understand-live-r0"
		});
		Assert.Equal(0, keyExitCode);

		string profilePath = Path.GetFullPath(Path.Combine(
			privateDirectory,
			"profile.json"));
		AntigravityCliFingerprint executableFingerprint = new(
			Path.GetFullPath(Path.Combine(
				temporaryDirectory.Path,
				"agy.exe")),
			"1.1.3",
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			new string('C', 64),
			0,
			"CN=Synthetic",
			new string('D', 40));
		AntigravityLiveR0ProfileFile profile = new(
			"synthetic-live-r0",
			executableFingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["SAFE_SYNTHETIC"] = "1"
			},
			120,
			30,
			structuralFingerprint,
			keyPath,
			exactFingerprint,
			useDefaultUsagePins
				? UsageStructuralFingerprint
				: usageStructuralFingerprint,
			new[]
			{
				Path.GetFullPath(Path.Combine(
					temporaryDirectory.Path,
					"settings.json"))
			},
			64 * 1024,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(3),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(25),
			TimeSpan.FromSeconds(1),
			useDefaultUsagePins
				? UsageExactFingerprint
				: usageExactFingerprint);
		await File.WriteAllTextAsync(
			profilePath,
			JsonSerializer.Serialize(profile));
		AssertSafeInheritedProfileAcl(profilePath);
		return (profilePath, profile);
	}

	private static string[] CreatePinPromptArguments(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint)
	{
		return new[]
		{
			"pin-prompt",
			"--profile",
			profilePath,
			"--structural",
			structuralFingerprint,
			"--exact",
			exactFingerprint,
			"--i-understand-live-r0"
		};
	}

	private static string[] CreateDeriveViewportProfileArguments(
		string sourceProfilePath,
		string outputPath,
		string columns,
		string rows)
	{
		return new[]
		{
			"derive-private-r0-viewport-profile",
			"--profile",
			sourceProfilePath,
			"--output",
			outputPath,
			"--columns",
			columns,
			"--rows",
			rows
		};
	}

	private static string[] CreateRepinPromptArguments(
		string profilePath,
		string expectedStructuralFingerprint,
		string expectedExactFingerprint,
		string structuralFingerprint,
		string exactFingerprint)
	{
		return new[]
		{
			"repin-prompt",
			"--profile",
			profilePath,
			"--expected-structural",
			expectedStructuralFingerprint,
			"--expected-exact",
			expectedExactFingerprint,
			"--structural",
			structuralFingerprint,
			"--exact",
			exactFingerprint,
			"--i-understand-live-r0"
		};
	}

	private static string[] CreatePinUsageArguments(
		string profilePath,
		string structuralFingerprint,
		string exactFingerprint)
	{
		return new[]
		{
			"pin-usage",
			"--profile",
			profilePath,
			"--structural",
			structuralFingerprint,
			"--exact",
			exactFingerprint,
			"--i-understand-live-r0"
		};
	}

	private static byte[] CreateExpectedPinnedBytes(
		byte[] originalBytes,
		string structuralFingerprint,
		string exactFingerprint)
	{
		return CreateExpectedRepinnedBytes(
			originalBytes,
			string.Empty,
			string.Empty,
			structuralFingerprint,
			exactFingerprint);
	}

	private static byte[] CreateExpectedRepinnedBytes(
		byte[] originalBytes,
		string expectedStructuralFingerprint,
		string expectedExactFingerprint,
		string structuralFingerprint,
		string exactFingerprint)
	{
		string originalJson = Encoding.UTF8.GetString(originalBytes);
		string structuralToken =
			$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedPromptStructuralFingerprint)}\":\"{expectedStructuralFingerprint}\"";
		string exactToken =
			$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedExactPromptFingerprint)}\":\"{expectedExactFingerprint}\"";
		Assert.Contains(structuralToken, originalJson, StringComparison.Ordinal);
		Assert.Contains(exactToken, originalJson, StringComparison.Ordinal);
		string expectedJson = originalJson
			.Replace(
				structuralToken,
				$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedPromptStructuralFingerprint)}\":\"{structuralFingerprint}\"",
				StringComparison.Ordinal)
			.Replace(
				exactToken,
				$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedExactPromptFingerprint)}\":\"{exactFingerprint}\"",
				StringComparison.Ordinal);
		return Encoding.UTF8.GetBytes(expectedJson);
	}

	private static byte[] CreateExpectedUsagePinnedBytes(
		byte[] originalBytes,
		string structuralFingerprint,
		string exactFingerprint)
	{
		string originalJson = Encoding.UTF8.GetString(originalBytes);
		string structuralToken =
			$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint)}\":null";
		string exactToken =
			$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint)}\":null";
		Assert.Contains(structuralToken, originalJson, StringComparison.Ordinal);
		Assert.Contains(exactToken, originalJson, StringComparison.Ordinal);
		string expectedJson = originalJson
			.Replace(
				structuralToken,
				$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint)}\":\"{structuralFingerprint}\"",
				StringComparison.Ordinal)
			.Replace(
				exactToken,
				$"\"{nameof(AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint)}\":\"{exactFingerprint}\"",
				StringComparison.Ordinal);
		return Encoding.UTF8.GetBytes(expectedJson);
	}

	private static IOException CreatePartialReplaceException()
	{
		const int ErrorUnableToMoveReplacement2 = 1177;
		return new IOException(
			"Synthetic partial ReplaceFile failure.",
			unchecked((int)(0x80070000u | ErrorUnableToMoveReplacement2)));
	}

	private static TestFileState CaptureTestFileState(string path)
	{
		FileInfo file = new(path);
		file.Refresh();
		Assert.True(file.Exists);
		return new TestFileState(
			file.Length,
			file.CreationTimeUtc,
			file.LastWriteTimeUtc,
			file.Attributes);
	}

	private static string[] GetPromptPinArtifactPaths(
		string profilePath,
		string purpose)
	{
		string directoryPath = Path.GetDirectoryName(profilePath)!;
		string profileFileName = Path.GetFileName(profilePath);
		return Directory.GetFiles(
				directoryPath,
				$".{profileFileName}.{purpose}.*.tmp")
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
	}

	private static string GetPromptPinLockPath(string profilePath)
	{
		return Path.Combine(
			Path.GetDirectoryName(profilePath)!,
			$".{Path.GetFileName(profilePath)}.pin-prompt.lock");
	}

	private static void AssertNoPromptPinArtifacts(string profilePath)
	{
		string directoryPath = Path.GetDirectoryName(profilePath)!;
		string profileFileName = Path.GetFileName(profilePath);
		Assert.Empty(Directory.GetFiles(
			directoryPath,
			$".{profileFileName}.*.tmp"));
		Assert.False(File.Exists(GetPromptPinLockPath(profilePath)));
	}

	private static string RemoveJsonProperty(
		string json,
		string propertyName)
	{
		JsonObject profile = JsonNode.Parse(json)!.AsObject();
		Assert.True(profile.Remove(propertyName));
		return profile.ToJsonString();
	}

	private static async Task<(int ExitCode, string Output, string Error)>
		InvokeMainAsync(string[] args)
	{
		TextWriter originalOutput = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter output = new();
		using StringWriter error = new();

		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			int exitCode = await Program.Main(args);
			return (exitCode, output.ToString(), error.ToString());
		}
		finally
		{
			Console.SetOut(originalOutput);
			Console.SetError(originalError);
		}
	}

	private static void AssertSafeInheritedProfileAcl(string profilePath)
	{
		string directoryPath = Path.GetDirectoryName(profilePath)!;
		Assert.True(
			AntigravityPrivateKeyAcl.IsPrivateDirectory(directoryPath));
		FileSecurity security = new FileInfo(profilePath).GetAccessControl(
			AccessControlSections.Access);
		Assert.False(security.AreAccessRulesProtected);
		AssertNoBroadAllowRules(security);
		Assert.All(
			security.GetAccessRules(
				true,
				true,
				typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
			rule => Assert.True(rule.IsInherited));
	}

	private static void AssertSameNonPinFields(
		AntigravityLiveR0ProfileFile expected,
		AntigravityLiveR0ProfileFile actual)
	{
		Assert.Equal(expected.Id, actual.Id);
		Assert.Equal(expected.ExecutableFingerprint, actual.ExecutableFingerprint);
		Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
		Assert.Equal(expected.Environment, actual.Environment);
		Assert.Equal(expected.Columns, actual.Columns);
		Assert.Equal(expected.Rows, actual.Rows);
		Assert.Equal(
			expected.ExactPromptFingerprintHmacKeyPath,
			actual.ExactPromptFingerprintHmacKeyPath);
		Assert.Equal(
			expected.ExpectedUsageStructuralFingerprint,
			actual.ExpectedUsageStructuralFingerprint);
		Assert.Equal(
			expected.NonCredentialSettingsFiles,
			actual.NonCredentialSettingsFiles);
		Assert.Equal(
			expected.MaximumSavedOutputBytes,
			actual.MaximumSavedOutputBytes);
		Assert.Equal(expected.PromptTimeout, actual.PromptTimeout);
		Assert.Equal(expected.UsageTimeout, actual.UsageTimeout);
		Assert.Equal(
			expected.StableScreenDuration,
			actual.StableScreenDuration);
		Assert.Equal(expected.PollInterval, actual.PollInterval);
		Assert.Equal(expected.CleanupTimeout, actual.CleanupTimeout);
		Assert.Equal(
			expected.ExpectedExactUsageFingerprint,
			actual.ExpectedExactUsageFingerprint);
	}

	private static void AssertNoBroadAllowRules(FileSystemSecurity security)
	{
		HashSet<SecurityIdentifier> broadIdentities = new()
		{
			new SecurityIdentifier(
				WellKnownSidType.AuthenticatedUserSid,
				null),
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
		};
		IEnumerable<FileSystemAccessRule> rules = security.GetAccessRules(
			true,
			true,
			typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>();

		Assert.DoesNotContain(
			rules,
			rule =>
				(rule.AccessControlType == AccessControlType.Allow) &&
				(rule.IdentityReference is SecurityIdentifier identity) &&
				broadIdentities.Contains(identity));
	}
}
