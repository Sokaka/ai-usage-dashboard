using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal static class Program
{
	private enum KeyInitializationStage
	{
		ValidateOutput,
		PreparePrivateDirectory,
		CreateKeyFile,
		ProtectKeyAcl,
		WriteKey,
		ValidateKeyFile,
		Unexpected
	}

	private sealed record PrivateR1CalibrationCommandReport(
		bool IsCaptureSuccessful,
		bool WasBundleUpdated,
		int ObservationCount,
		AntigravityLiveR1PrivateCalibrationCaptureReport CaptureReport);

	private sealed record ReviewedPackagePromptCalibrationReport(
		string FormatVersion,
		string StructuralFingerprint);

	private const string ConsentFlag = "--i-understand-live-r0";
	private const string ManifestExportConsentFlag =
		"--i-understand-public-manifest-export";
	private const string PromptReanchorConsentFlag =
		"--i-understand-reviewed-prompt-reanchor";
	private const string PrivateR1ConsentFlag =
		"--i-understand-live-private-r1";
	private const string R1ConsentFlag = "--i-understand-live-r1";
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	public static async Task<int> Main(string[] args)
	{
		if ((args.Length == 4) &&
			string.Equals(
				args[0],
				"__test-job-crash-host",
				StringComparison.Ordinal))
		{
			return await RunJobCrashHostAsync(args[1], args[2], args[3]);
		}

		if ((args.Length == 0) ||
			((args.Length == 1) &&
				(string.Equals(args[0], "--help", StringComparison.Ordinal) ||
				 string.Equals(args[0], "-h", StringComparison.Ordinal))))
		{
			PrintHelp();
			return 0;
		}

		if ((args.Length == 4) &&
			string.Equals(
				args[0],
				"calibrate-reviewed-package-prompt",
				StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], ConsentFlag, StringComparison.Ordinal))
		{
			return await RunReviewedPackagePromptCalibrationAsync(args[2]);
		}

		if ((args.Length == 10) &&
			string.Equals(
				args[0],
				"reanchor-reviewed-package-prompt",
				StringComparison.Ordinal) &&
			string.Equals(args[1], "--input", StringComparison.Ordinal) &&
			string.Equals(args[3], "--output", StringComparison.Ordinal) &&
			string.Equals(args[5], "--contract-id", StringComparison.Ordinal) &&
			string.Equals(args[7], "--structural", StringComparison.Ordinal) &&
			string.Equals(
				args[9],
				PromptReanchorConsentFlag,
				StringComparison.Ordinal))
		{
			return await RunReviewedPackagePromptReanchorAsync(
				args[2],
				args[4],
				args[6],
				args[8]);
		}

		if (TryParseLiveCommand(
			args,
			out AntigravityLiveR0Mode mode,
			out string profilePath))
		{
			return await RunLiveAsync(mode, profilePath);
		}

		if (TryParseLiveR1Command(args, out string r1ProfilePath))
		{
			return await RunLiveR1Async(r1ProfilePath);
		}

		if (TryParsePrivateR1CalibrationCaptureCommand(
			args,
			out string privateCaptureProfilePath,
			out string privateCaptureOutputPath))
		{
			return await RunPrivateR1CalibrationCaptureAsync(
				privateCaptureProfilePath,
				privateCaptureOutputPath);
		}

		if (TryParseOfflineUsageCalibrationCommand(
			args,
			out string privateBundlePath,
			out string reviewedSpecPath,
			out string reviewedLayoutOutputPath))
		{
			return await RunOfflineUsageCalibrationAsync(
				privateBundlePath,
				reviewedSpecPath,
				reviewedLayoutOutputPath);
		}

		if (TryParsePrivateR1SectionDraftCommand(
			args,
			out string draftProfilePath,
			out string draftBundlePath,
			out string draftOutputPath))
		{
			return await RunPrivateR1SectionDraftAsync(
				draftProfilePath,
				draftBundlePath,
				draftOutputPath);
		}

		if (TryParsePrivateR0ViewportProfileDerivationCommand(
			args,
			out string sourceViewportProfilePath,
			out string derivedViewportProfilePath,
			out string columnsValue,
			out string rowsValue))
		{
			return await RunPrivateR0ViewportProfileDerivationAsync(
				sourceViewportProfilePath,
				derivedViewportProfilePath,
				columnsValue,
				rowsValue);
		}

		if (TryParseInitHmacKeyCommand(
			args,
			out string outputPath))
		{
			return await InitializeHmacKeyAsync(outputPath);
		}

		if (TryParsePinPromptCommand(
			args,
			out string promptProfilePath,
			out string structuralFingerprint,
			out string exactFingerprint))
		{
			bool isPinned = await AntigravityPromptPinWriter.TryPinAsync(
				promptProfilePath,
				structuralFingerprint,
				exactFingerprint);

			if (isPinned)
			{
				Console.WriteLine("Live R0 prompt fingerprints pinned.");
				return 0;
			}

			Console.Error.WriteLine(
				"Live R0 prompt fingerprint pinning failed closed.");
			return 2;
		}

		if (TryParseRepinPromptCommand(
			args,
			out string repinProfilePath,
			out string expectedStructuralFingerprint,
			out string expectedExactFingerprint,
			out string replacementStructuralFingerprint,
			out string replacementExactFingerprint))
		{
			bool isRepinned = await AntigravityPromptPinWriter.TryRepinAsync(
				repinProfilePath,
				expectedStructuralFingerprint,
				expectedExactFingerprint,
				replacementStructuralFingerprint,
				replacementExactFingerprint);

			if (isRepinned)
			{
				Console.WriteLine("Live R0 prompt fingerprints repinned.");
				return 0;
			}

			Console.Error.WriteLine(
				"Live R0 prompt fingerprint repinning failed closed.");
			return 2;
		}

		if (TryParsePinUsageCommand(
			args,
			out string usageProfilePath,
			out string usageStructuralFingerprint,
			out string usageExactFingerprint))
		{
			bool isPinned = await AntigravityPromptPinWriter.TryPinUsageAsync(
				usageProfilePath,
				usageStructuralFingerprint,
				usageExactFingerprint);

			if (isPinned)
			{
				Console.WriteLine("Live R0 usage fingerprints pinned.");
				return 0;
			}

			Console.Error.WriteLine(
				"Live R0 usage fingerprint pinning failed closed.");
			return 2;
		}

		if ((args.Length == 11) &&
			string.Equals(args[0], "promote-private-r1-section-spec", StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], "--input", StringComparison.Ordinal) &&
			string.Equals(args[5], "--draft", StringComparison.Ordinal) &&
			string.Equals(args[7], "--output", StringComparison.Ordinal) &&
			string.Equals(args[9], "--approved-fingerprint", StringComparison.Ordinal))
		{
			return await RunR1SectionPromotionAsync(
				args[2],
				args[4],
				args[6],
				args[8],
				args[10]);
		}

		if ((args.Length == 7) &&
			string.Equals(args[0], "compose-private-r1-section-profile", StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], "--layout", StringComparison.Ordinal) &&
			string.Equals(args[5], "--output", StringComparison.Ordinal))
		{
			try
			{
				await AntigravityLiveR1SectionProfileComposer.ComposeAsync(
					args[2], args[4], args[6]);
				Console.WriteLine("Private AGY R1 section profile composed.");
				return 0;
			}
			catch (InvalidDataException exception)
			{
				Console.Error.WriteLine($"Private AGY R1 section profile composition failed closed at stage: {exception.Message}.");
				return 2;
			}
		}

		if ((args.Length == 4) &&
			string.Equals(args[0], "capture-usage-r1-section", StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], R1ConsentFlag, StringComparison.Ordinal))
		{
			return await RunLiveR1SectionAsync(args[2]);
		}

		if ((args.Length == 7) &&
			string.Equals(
				args[0],
				"promote-executable-fingerprint",
				StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], "--output", StringComparison.Ordinal) &&
			string.Equals(
				args[5],
				"--approved-sha256",
				StringComparison.Ordinal))
		{
			return await RunExecutableFingerprintPromotionAsync(
				args[2],
				args[4],
				args[6]);
		}

		if ((args.Length == 8) &&
			string.Equals(
				args[0],
				"export-reviewed-package-manifest",
				StringComparison.Ordinal) &&
			string.Equals(args[1], "--profile", StringComparison.Ordinal) &&
			string.Equals(args[3], "--output", StringComparison.Ordinal) &&
			string.Equals(args[5], "--contract-id", StringComparison.Ordinal) &&
			string.Equals(
				args[7],
				ManifestExportConsentFlag,
				StringComparison.Ordinal))
		{
			return await RunReviewedPackageManifestExportAsync(
				args[2],
				args[4],
				args[6]);
		}

		Console.Error.WriteLine("Unknown command. This AGY feasibility harness does not launch agy.exe without a separately consented live-test path.");
		return 2;
	}

	private static async Task<int> RunJobCrashHostAsync(
		string executablePath,
		string attemptId,
		string rootProcessIdPath)
	{
		try
		{
			WindowsAntigravityOfficialPrintProcessRunner runner = new(
				commandTimeout: TimeSpan.FromMinutes(5),
				cleanupTimeout: TimeSpan.FromSeconds(5),
				afterProcessCreated: processId => File.WriteAllText(
					Path.GetFullPath(rootProcessIdPath),
					processId.ToString(CultureInfo.InvariantCulture)));
			using AntigravityOfficialPrintProcessResult _ =
				await runner.RunAsync(
					Path.GetFullPath(executablePath),
					attemptId,
					_ => Task.CompletedTask,
					CancellationToken.None);
			Console.Error.WriteLine(
				"The crash-containment fixture completed unexpectedly.");
			return 3;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);
			return 2;
		}
	}

	private static async Task<int>
		RunReviewedPackagePromptCalibrationAsync(string profilePath)
	{
		const int maximumProfileBytes = 1024 * 1024;
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			if (!Path.IsPathFullyQualified(profilePath))
			{
				throw new InvalidDataException();
			}

			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					CancellationToken.None);
			AntigravityLiveR1SectionProfileFile profileFile =
				AntigravityLiveR1SectionProfileJson.Deserialize(profileBytes);
			(AntigravityLiveR1SectionProfile materializedProfile,
				byte[] loadedHmacKey) = await profileFile.ToProfileAsync(
					profilePath,
					CancellationToken.None);
			hmacKey = loadedHmacKey;
			string userProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(userProfile) ||
				!Path.IsPathFullyQualified(userProfile) ||
				!Directory.Exists(userProfile))
			{
				throw new InvalidDataException();
			}

			AntigravityLiveR0Profile calibrationProfile =
				materializedProfile.CaptureProfile with
				{
					WorkingDirectory = userProfile,
					Environment =
						AntigravityResearchMachineSetup
							.BuildCaptureEnvironment(userProfile),
					ExpectedPromptStructuralFingerprint = string.Empty,
					ExpectedExactPromptFingerprint = string.Empty
				};
			AntigravityLiveR0Report report =
				await new AntigravityLiveR0Runner().RunAsync(
					AntigravityLiveR0Mode.CalibratePrompt,
					calibrationProfile,
					new AntigravityLiveR0Consent(true),
					CancellationToken.None);

			if (!AntigravityResearchMachineSetup.IsSafePromptCalibration(
					report))
			{
				throw new InvalidDataException();
			}

			Console.WriteLine(JsonSerializer.Serialize(
				new ReviewedPackagePromptCalibrationReport(
					"agy-reviewed-package-prompt-calibration-v1",
					report.PromptCapture!.StructuralFingerprint),
				JsonOptions));
			return 0;
		}
		catch
		{
			Console.Error.WriteLine(
				"Reviewed AGY package prompt calibration failed closed.");
			return 2;
		}
		finally
		{
			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}

			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}
		}
	}

	private static async Task<int> RunReviewedPackagePromptReanchorAsync(
		string inputPath,
		string outputPath,
		string contractId,
		string structuralFingerprint)
	{
		const int maximumManifestBytes = 1024 * 1024;
		byte[]? inputBytes = null;
		byte[]? outputBytes = null;

		try
		{
			if (!Path.IsPathFullyQualified(inputPath) ||
				!Path.IsPathFullyQualified(outputPath) ||
				!string.Equals(
					Path.GetExtension(outputPath),
					".json",
					StringComparison.OrdinalIgnoreCase) ||
				File.Exists(outputPath) ||
				Directory.Exists(outputPath) ||
				!AntigravityUsageR1SchemaParser.IsFingerprint(
					structuralFingerprint))
			{
				throw new InvalidDataException();
			}

			inputBytes = await File.ReadAllBytesAsync(inputPath);

			if ((inputBytes.Length == 0) ||
				(inputBytes.Length > maximumManifestBytes))
			{
				throw new InvalidDataException();
			}

			AntigravityReviewedPackageManifestFile manifest =
				AntigravityReviewedPackageManifestJson.Deserialize(inputBytes);
			int matchingContractCount = manifest.Contracts.Count(
				contract => string.Equals(
					contract.ContractId,
					contractId,
					StringComparison.Ordinal));

			if (matchingContractCount != 1)
			{
				throw new InvalidDataException();
			}

			AntigravityReviewedPackageContract[] contracts =
				manifest.Contracts
					.Select(
						contract => string.Equals(
							contract.ContractId,
							contractId,
							StringComparison.Ordinal)
							? ReanchorContract(
								contract,
								structuralFingerprint)
							: contract)
					.ToArray();
			outputBytes = AntigravityReviewedPackageManifestJson.Serialize(
				manifest with
				{
					Contracts = Array.AsReadOnly(contracts)
				});
			RequirePortableManifestPrivacy(outputBytes);
			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string? outputDirectory = Path.GetDirectoryName(
				absoluteOutputPath);

			if (string.IsNullOrWhiteSpace(outputDirectory) ||
				!Directory.Exists(outputDirectory))
			{
				throw new InvalidDataException();
			}

			await using FileStream stream = new(
				absoluteOutputPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough);
			await stream.WriteAsync(outputBytes);
			await stream.FlushAsync();
			stream.Flush(flushToDisk: true);
			Console.WriteLine(
				"Reviewed AGY package prompt anchor updated in a new portable manifest.");
			return 0;
		}
		catch
		{
			Console.Error.WriteLine(
				"Reviewed AGY package prompt reanchor failed closed.");
			return 2;
		}
		finally
		{
			if (inputBytes is not null)
			{
				CryptographicOperations.ZeroMemory(inputBytes);
			}

			if (outputBytes is not null)
			{
				CryptographicOperations.ZeroMemory(outputBytes);
			}
		}
	}

	private static AntigravityReviewedPackageContract ReanchorContract(
		AntigravityReviewedPackageContract contract,
		string structuralFingerprint)
	{
		AntigravityReviewedPackageContract draft = contract with
		{
			ExpectedPromptStructuralFingerprint = structuralFingerprint,
			ContractFingerprint = string.Empty
		};
		return draft with
		{
			ContractFingerprint =
				AntigravityReviewedPackageManifestJson
					.ComputeContractFingerprint(draft)
		};
	}

	private static async Task<int> RunReviewedPackageManifestExportAsync(
		string profilePath,
		string outputPath,
		string contractId)
	{
		const int maximumProfileBytes = 1024 * 1024;
		byte[]? hmacKey = null;
		byte[]? manifestBytes = null;
		byte[]? profileBytes = null;

		try
		{
			if (!Path.IsPathFullyQualified(profilePath) ||
				!Path.IsPathFullyQualified(outputPath) ||
				!string.Equals(
					Path.GetExtension(outputPath),
					".json",
					StringComparison.OrdinalIgnoreCase) ||
				File.Exists(outputPath) ||
				Directory.Exists(outputPath))
			{
				throw new InvalidDataException();
			}

			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					CancellationToken.None);
			AntigravityLiveR1SectionProfileFile profile =
				AntigravityLiveR1SectionProfileJson.Deserialize(profileBytes);
			(AntigravityLiveR1SectionProfile materializedProfile,
				byte[] loadedHmacKey) = await profile.ToProfileAsync(
					profilePath,
					CancellationToken.None);
			hmacKey = loadedHmacKey;
			AntigravityCliCapabilityValidator validator = new(
				new[] { profile.ExecutableFingerprint });
			using AntigravityCliCapabilityValidationResult validation =
				await validator.ValidateAsync(
					profile.ExecutableFingerprint.AbsolutePath,
					CancellationToken.None);

			if (!validation.IsSupported ||
				(validation.ObservedFingerprint is null))
			{
				throw new InvalidDataException();
			}

			AntigravityLiveR0Runner runner = new();
			string settingsBaselineFingerprint = await runner
				.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
					materializedProfile.CaptureProfile,
					CancellationToken.None);
			string captureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					materializedProfile.CaptureProfile,
					materializedProfile.UsageLayout.Spec.IsAlternateScreen,
					settingsBaselineFingerprint);

			if (!string.Equals(
					materializedProfile.UsageLayout
						.CaptureContractFingerprint,
					captureContractFingerprint,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException();
			}

			AntigravityCliFingerprint executable =
				profile.ExecutableFingerprint;
			AntigravityReviewedPackageExecutable reviewedExecutable = new(
				executable.CliVersion,
				executable.FileVersion,
				executable.ProductVersion,
				executable.Sha256,
				executable.WinVerifyTrustStatus,
				executable.SignerSubject,
				executable.SignerThumbprint);
			AntigravityReviewedPackageUsageLayout reviewedLayout = new(
				profile.ReviewedUsageLayout.Spec,
				profile.ReviewedUsageLayout.SchemaFingerprint,
				profile.ReviewedUsageLayout.ExpectedPageFingerprints);
			AntigravityReviewedPackageContract contract = new(
				contractId,
				reviewedExecutable,
				profile.PromptGuard.ExpectedPromptStructuralFingerprint,
				reviewedLayout,
				new string('0', 64));
			contract = contract with
			{
				ContractFingerprint =
					AntigravityReviewedPackageManifestJson
						.ComputeContractFingerprint(contract)
			};
			AntigravityReviewedPackageManifestFile manifest = new(
				AntigravityReviewedPackageManifestFile.CurrentFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				Array.AsReadOnly(new[] { contract }));
			manifestBytes =
				AntigravityReviewedPackageManifestJson.Serialize(manifest);
			RequirePortableManifestPrivacy(manifestBytes);
			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string? outputDirectory =
				Path.GetDirectoryName(absoluteOutputPath);

			if (string.IsNullOrWhiteSpace(outputDirectory) ||
				!Directory.Exists(outputDirectory))
			{
				throw new InvalidDataException();
			}

			await using FileStream stream = new(
				absoluteOutputPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough);
			await stream.WriteAsync(manifestBytes);
			await stream.FlushAsync();
			stream.Flush(flushToDisk: true);
			Console.WriteLine(
				"Reviewed AGY package manifest exported without private machine fields.");
			return 0;
		}
		catch
		{
			Console.Error.WriteLine(
				"Reviewed AGY package manifest export failed closed.");
			return 2;
		}
		finally
		{
			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}

			if (manifestBytes is not null)
			{
				CryptographicOperations.ZeroMemory(manifestBytes);
			}

			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}
		}
	}

	private static void RequirePortableManifestPrivacy(
		ReadOnlySpan<byte> manifestBytes)
	{
		string json = Encoding.UTF8.GetString(manifestBytes);
		string[] forbiddenPropertyNames =
		{
			"AbsolutePath",
			"WorkingDirectory",
			"Environment",
			"LocalFingerprintHmacKeyPath",
			"ExpectedExactPromptFingerprint",
			"NonCredentialSettingsFiles",
			"CaptureContractFingerprint",
			"ApprovedDraftFingerprint",
			"AccountIdentity",
			"RemainingPercent",
			"ResetsIn"
		};

		if (forbiddenPropertyNames.Any(name => json.Contains(
				$"\"{name}\"",
				StringComparison.Ordinal)))
		{
			throw new InvalidDataException();
		}
	}

	private static async Task<int> RunExecutableFingerprintPromotionAsync(
		string profilePath,
		string outputPath,
		string approvedSha256)
	{
		try
		{
			AntigravityExecutableFingerprintPromotionMetadata metadata =
				await AntigravityExecutableFingerprintPromotion.PromoteAsync(
					profilePath,
					outputPath,
					approvedSha256);
			Console.WriteLine(JsonSerializer.Serialize(metadata, JsonOptions));
			return 0;
		}
		catch (InvalidDataException exception)
		{
			Console.Error.WriteLine(
				$"Private AGY executable fingerprint promotion failed closed at stage: {exception.Message}.");
			return 2;
		}
	}

	private static async Task<int> RunR1SectionPromotionAsync(
		string profilePath,
		string privateBundlePath,
		string draftPath,
		string outputPath,
		string approvedFingerprint)
	{
		const int maximumProfileBytes = 1024 * 1024;
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					CancellationToken.None);
			AntigravityLiveR0ProfileFile profileFile =
				AntigravityLiveR0ProfileJson.Deserialize(profileBytes);

			if ((profileFile.Columns != 120) ||
				(profileFile.Rows != 50) ||
				(profileFile.ExpectedUsageStructuralFingerprint is not null) ||
				(profileFile.ExpectedExactUsageFingerprint is not null))
			{
				throw new InvalidDataException();
			}

			(AntigravityLiveR0Profile _, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			AntigravityUsageR1SectionPromotionMetadata metadata =
				await AntigravityUsageR1SectionPromotion.PromoteAsync(
					privateBundlePath,
					draftPath,
					outputPath,
					approvedFingerprint,
					hmacKey);
			Console.WriteLine(JsonSerializer.Serialize(metadata, JsonOptions));
			return 0;
		}
		catch
		{
			Console.Error.WriteLine(
				"Private AGY R1 section promotion failed closed.");
			return 2;
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int> RunLiveR1SectionAsync(string profilePath)
	{
		const int maximumProfileBytes = 1024 * 1024;
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			profileBytes = await AntigravityUsageR1CalibrationFile.ReadBoundedAsync(
				profilePath, maximumProfileBytes, requirePrivateFile: true,
				CancellationToken.None);
			AntigravityLiveR1SectionProfileFile profileFile =
				AntigravityLiveR1SectionProfileJson.Deserialize(profileBytes);
			(AntigravityLiveR1SectionProfile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			AntigravityLiveR1SectionRunResult result =
				await new AntigravityLiveR0Runner().RunR1SectionAsync(
					profile,
					new AntigravityLiveR1SectionConsent(true));
			Console.WriteLine(JsonSerializer.Serialize(result.Report, JsonOptions));
			return result.Report.IsCaptureSuccessful ? 0 : 3;
		}
		catch
		{
			return WriteR1ProfileFailure();
		}
		finally
		{
			if (profileBytes is not null) CryptographicOperations.ZeroMemory(profileBytes);
			if (hmacKey is not null) CryptographicOperations.ZeroMemory(hmacKey);
		}
	}

	private static async Task<int> RunLiveAsync(
		AntigravityLiveR0Mode mode,
		string profilePath)
	{
		byte[]? hmacKey = null;

		try
		{
			if (!Path.IsPathFullyQualified(profilePath) ||
				!File.Exists(profilePath))
			{
				return WriteProfileFailure();
			}

			string json = await File.ReadAllTextAsync(profilePath);
			AntigravityLiveR0ProfileFile? profileFile =
				JsonSerializer.Deserialize<AntigravityLiveR0ProfileFile>(
					json,
					JsonOptions);

			if (profileFile is null)
			{
				return WriteProfileFailure();
			}

			(AntigravityLiveR0Profile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			AntigravityLiveR0Runner runner = new();
			AntigravityLiveR0Report report = await runner.RunAsync(
				mode,
				profile,
				new AntigravityLiveR0Consent(true));
			Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
			return report.IsCaptureSuccessful ? 0 : 3;
		}
		catch
		{
			return WriteProfileFailure();
		}
		finally
		{
			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int> RunLiveR1Async(string profilePath)
	{
		const int maximumProfileBytes = 1024 * 1024;
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			profileBytes = await AntigravityUsageR1CalibrationFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					requirePrivateFile: false,
					CancellationToken.None);
			AntigravityLiveR1ProfileFile profileFile =
				AntigravityLiveR1ProfileJson.Deserialize(profileBytes);
			(AntigravityLiveR1Profile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			AntigravityLiveR0Runner runner = new();
			AntigravityLiveR1RunResult result = await runner.RunR1Async(
				profile,
				new AntigravityLiveR1Consent(true));
			Console.WriteLine(JsonSerializer.Serialize(result.Report, JsonOptions));
			return result.Report.IsCaptureSuccessful ? 0 : 3;
		}
		catch
		{
			return WriteR1ProfileFailure();
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int> RunPrivateR1CalibrationCaptureAsync(
		string profilePath,
		string outputPath)
	{
		const int maximumProfileBytes = 1024 * 1024;
		string failureStage = "ProfileRead";
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;
		string? requiredSettingsBaselineFingerprint = null;

		try
		{
			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					CancellationToken.None);
			failureStage = "ProfileFormat";
			AntigravityLiveR0ProfileFile profileFile =
				AntigravityLiveR0ProfileJson.Deserialize(profileBytes) with
				{
					ExpectedUsageStructuralFingerprint = null,
					ExpectedExactUsageFingerprint = null
				};
			failureStage = "ProfileOrOutputValidation";

			if (!IsPrivateR1CalibrationOutputPathSafe(
					profilePath,
					profileFile.ExactPromptFingerprintHmacKeyPath,
					outputPath))
			{
				return WritePrivateR1CalibrationCaptureFailure(
					"ProfileOrOutputValidation");
			}

			failureStage = "ProfileValidation";
			(AntigravityLiveR0Profile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			AntigravityLiveR0Runner runner = new();
			failureStage = "OutputPreflight";
			AntigravityUsageR1PrivateBundleMetadata preflight =
				await AntigravityUsageR1PrivateBundleWriter.PreflightAsync(
					outputPath);

			if (preflight.Exists)
			{
				failureStage = "CaptureContractPreflight";

				if ((preflight.Columns != profile.Columns) ||
					(preflight.Rows != profile.Rows) ||
					(preflight.IsAlternateScreen is null))
				{
					return WritePrivateR1CalibrationCaptureFailure(
						"CaptureContractPreflight");
				}

				requiredSettingsBaselineFingerprint =
					await runner
						.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
							profile);
				string expectedCaptureContractFingerprint =
					AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
						profile,
						preflight.IsAlternateScreen.Value,
						requiredSettingsBaselineFingerprint);

				if (!string.Equals(
						preflight.CaptureContractFingerprint,
						expectedCaptureContractFingerprint,
						StringComparison.Ordinal))
				{
					return WritePrivateR1CalibrationCaptureFailure(
						"CaptureContractPreflight");
				}
			}

			failureStage = "LiveCapture";
			AntigravityLiveR1PrivateCalibrationCaptureResult capture =
				await runner.RunR1PrivateCalibrationCaptureAsync(
					profile,
					new AntigravityLiveR1PrivateCalibrationCaptureConsent(true),
					CancellationToken.None,
					requiredSettingsBaselineFingerprint);
			TerminalScreenSnapshot? snapshot = capture.Snapshot;
			string? captureContractFingerprint =
				capture.Report.CaptureContractFingerprint;

			if (!capture.Report.IsCaptureSuccessful ||
				(snapshot is null) ||
				(captureContractFingerprint is null))
			{
				PrivateR1CalibrationCommandReport failedReport = new(
					IsCaptureSuccessful: false,
					WasBundleUpdated: false,
					ObservationCount: preflight.SequenceCount,
					CaptureReport: capture.Report);
				Console.WriteLine(JsonSerializer.Serialize(
					failedReport,
					JsonOptions));
				return 3;
			}

			failureStage = "OutputPersistence";
			AntigravityUsageR1PrivateBundleMetadata persisted =
				await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
					outputPath,
					snapshot,
					captureContractFingerprint);
			PrivateR1CalibrationCommandReport successReport = new(
				IsCaptureSuccessful: true,
				WasBundleUpdated: persisted.WasUpdated,
				ObservationCount: persisted.SequenceCount,
				CaptureReport: capture.Report);
			Console.WriteLine(JsonSerializer.Serialize(
				successReport,
				JsonOptions));
			return 0;
		}
		catch (AntigravityUsageR1PrivateBundleException exception)
		{
			return WritePrivateR1CalibrationCaptureFailure(
				exception.Stage.ToString());
		}
		catch
		{
			return WritePrivateR1CalibrationCaptureFailure(failureStage);
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int> RunOfflineUsageCalibrationAsync(
		string privateBundlePath,
		string reviewedSpecPath,
		string outputPath)
	{
		try
		{
			AntigravityUsageR1SectionLayoutFile reviewedLayout =
				await AntigravityUsageR1SectionCalibrationCompiler
					.CompileFromFilesAsync(
						privateBundlePath,
						reviewedSpecPath);
			bool isWritten =
				await AntigravityUsageR1SectionLayoutWriter.TryWriteAsync(
					outputPath,
					reviewedLayout);

			if (!isWritten)
			{
				Console.Error.WriteLine(
					"Offline AGY R1 reviewed layout write failed closed.");
				return 2;
			}

			Console.WriteLine(
				"Offline AGY R1 reviewed usage layout created.");
			return 0;
		}
		catch (AntigravityUsageR1CalibrationException exception)
		{
			Console.Error.WriteLine(
				$"Offline AGY R1 usage calibration failed closed at stage: {exception.Stage}.");
			return 2;
		}
		catch
		{
			Console.Error.WriteLine(
				"Offline AGY R1 usage calibration failed closed.");
			return 2;
		}
	}

	private static async Task<int> RunPrivateR1SectionDraftAsync(
		string profilePath,
		string privateBundlePath,
		string outputPath)
	{
		const int maximumProfileBytes = 1024 * 1024;
		string failureStage = "ProfileRead";
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					maximumProfileBytes,
					CancellationToken.None);
			failureStage = "ProfileFormat";
			AntigravityLiveR0ProfileFile profileFile =
				AntigravityLiveR0ProfileJson.Deserialize(profileBytes);
			failureStage = "PathValidation";

			if ((profileFile.ExpectedUsageStructuralFingerprint is not null) ||
				(profileFile.ExpectedExactUsageFingerprint is not null) ||
				(profileFile.Columns != 120) ||
				(profileFile.Rows != 50) ||
				!IsPrivateR1CalibrationOutputPathSafe(
					profilePath,
					profileFile.ExactPromptFingerprintHmacKeyPath,
					privateBundlePath) ||
				!IsPrivateR1CalibrationOutputPathSafe(
					profilePath,
					profileFile.ExactPromptFingerprintHmacKeyPath,
					outputPath))
			{
				return WritePrivateR1SectionDraftFailure(failureStage);
			}

			failureStage = "ProfileValidation";
			(AntigravityLiveR0Profile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(profilePath);
			hmacKey = loadedHmacKey;
			failureStage = "BundlePreflight";
			AntigravityUsageR1PrivateBundleMetadata preflight =
				await AntigravityUsageR1PrivateBundleWriter.PreflightAsync(
					privateBundlePath);

			if (!preflight.Exists ||
				(preflight.SequenceCount < 2) ||
				(preflight.Columns != profile.Columns) ||
				(preflight.Rows != profile.Rows) ||
				(preflight.IsAlternateScreen is null) ||
				(preflight.CaptureContractFingerprint is null))
			{
				return WritePrivateR1SectionDraftFailure(failureStage);
			}

			failureStage = "CaptureContract";
			AntigravityLiveR0Runner runner = new();
			string settingsBaselineFingerprint =
				await runner
					.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
						profile);
			string expectedCaptureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					profile,
					preflight.IsAlternateScreen.Value,
					settingsBaselineFingerprint);

			if (!string.Equals(
					preflight.CaptureContractFingerprint,
					expectedCaptureContractFingerprint,
					StringComparison.Ordinal))
			{
				return WritePrivateR1SectionDraftFailure(failureStage);
			}

			failureStage = "DraftGeneration";
			AntigravityUsageR1SectionPrivateDraftMetadata metadata =
				await AntigravityUsageR1SectionPrivateDraftGenerator
					.GenerateFromPrivateBundleAsync(
						privateBundlePath,
						outputPath,
						expectedCaptureContractFingerprint,
						hmacKey);
			Console.WriteLine(JsonSerializer.Serialize(metadata, JsonOptions));
			return 0;
		}
		catch (AntigravityUsageR1SectionPrivateDraftException exception)
		{
			string diagnostic = exception.Stage.ToString();

			if (exception.ExtractionFailureCode is
				AntigravityUsageR1SectionPrivateDraftExtractionFailureCode code)
			{
				diagnostic = $"{diagnostic}.{code}";

				if (exception.RowIndex is int rowIndex)
				{
					diagnostic = $"{diagnostic}.Row{rowIndex}";
				}

				if (exception.ObservationIndex is int observationIndex)
				{
					diagnostic =
						$"{diagnostic}.Observation{observationIndex}";
				}
			}

			return WritePrivateR1SectionDraftFailure(diagnostic);
		}
		catch
		{
			return WritePrivateR1SectionDraftFailure(failureStage);
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int>
		RunPrivateR0ViewportProfileDerivationAsync(
			string sourceProfilePath,
			string outputPath,
			string columnsValue,
			string rowsValue)
	{
		const int maximumProfileBytes = 1024 * 1024;
		string failureStage = "Arguments";
		byte[]? hmacKey = null;
		byte[]? sourceBytes = null;

		try
		{
			if (!int.TryParse(
					columnsValue,
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out int columns) ||
				!int.TryParse(
					rowsValue,
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out int rows) ||
				!AntigravityPrivateR0ViewportProfileWriter.AreValidDimensions(
					columns,
					rows))
			{
				return WritePrivateR0ViewportProfileDerivationFailure(
					failureStage);
			}

			failureStage = "ProfileRead";
			sourceBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					sourceProfilePath,
					maximumProfileBytes,
					CancellationToken.None);
			failureStage = "ProfileFormat";
			AntigravityLiveR0ProfileFile derivedProfile =
				AntigravityLiveR0ProfileJson.Deserialize(sourceBytes) with
				{
					Columns = checked((short)columns),
					Rows = checked((short)rows),
					ExpectedPromptStructuralFingerprint = string.Empty,
					ExpectedExactPromptFingerprint = string.Empty,
					ExpectedUsageStructuralFingerprint = null,
					ExpectedExactUsageFingerprint = null
				};
			failureStage = "ProfileOrOutputValidation";

			if (!AntigravityPrivateR0ViewportProfileWriter.IsSafeOutputPath(
					sourceProfilePath,
					derivedProfile.ExactPromptFingerprintHmacKeyPath,
					outputPath))
			{
				return WritePrivateR0ViewportProfileDerivationFailure(
					failureStage);
			}

			failureStage = "ProfileValidation";
			(AntigravityLiveR0Profile _, byte[] loadedHmacKey) =
				await derivedProfile.ToProfileAsync(outputPath);
			hmacKey = loadedHmacKey;
			failureStage = "OutputPersistence";
			bool isWritten =
				await AntigravityPrivateR0ViewportProfileWriter.TryWriteAsync(
					outputPath,
					derivedProfile);

			if (!isWritten)
			{
				return WritePrivateR0ViewportProfileDerivationFailure(
					failureStage);
			}

			Console.WriteLine("Private R0 viewport profile derived.");
			return 0;
		}
		catch
		{
			return WritePrivateR0ViewportProfileDerivationFailure(
				failureStage);
		}
		finally
		{
			if (sourceBytes is not null)
			{
				CryptographicOperations.ZeroMemory(sourceBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static async Task<int> InitializeHmacKeyAsync(string outputPath)
	{
		byte[]? hmacKey = null;
		string? createdDirectoryPath = null;
		string? createdKeyPath = null;
		bool shouldDeleteCreatedDirectory = false;
		bool shouldDeletePartialKey = false;
		KeyInitializationStage stage = KeyInitializationStage.Unexpected;
		AntigravityPrivateKeyDirectoryFailureReason directoryFailureReason =
			AntigravityPrivateKeyDirectoryFailureReason.None;

		try
		{
			stage = KeyInitializationStage.ValidateOutput;

			if (!Path.IsPathFullyQualified(outputPath) ||
				!string.Equals(
					Path.GetExtension(outputPath),
					".key",
					StringComparison.OrdinalIgnoreCase))
			{
				return WriteKeyInitializationFailure(stage);
			}

			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string? parentPath = Path.GetDirectoryName(absoluteOutputPath);

			if (parentPath is null)
			{
				return WriteKeyInitializationFailure(stage);
			}

			stage = KeyInitializationStage.PreparePrivateDirectory;
			bool isDirectoryPrepared =
				AntigravityPrivateKeyAcl.TryPrepareDirectory(
					parentPath,
					out bool wasDirectoryCreated,
					out directoryFailureReason);

			if (wasDirectoryCreated)
			{
				createdDirectoryPath = parentPath;
				shouldDeleteCreatedDirectory = true;
			}

			if (!isDirectoryPrepared)
			{
				return WriteKeyInitializationFailure(
					stage,
					directoryFailureReason);
			}

			stage = KeyInitializationStage.CreateKeyFile;
			hmacKey = new byte[32];
			RandomNumberGenerator.Fill(hmacKey);

			await using (FileStream createStream = new(
				absoluteOutputPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				createdKeyPath = absoluteOutputPath;
				shouldDeletePartialKey = true;
				await createStream.FlushAsync();
				createStream.Flush(flushToDisk: true);
			}

			stage = KeyInitializationStage.ProtectKeyAcl;

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(
					absoluteOutputPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath))
			{
				return WriteKeyInitializationFailure(stage);
			}

			stage = KeyInitializationStage.WriteKey;
			await using (FileStream writeStream = new(
				absoluteOutputPath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await writeStream.WriteAsync(hmacKey);
				await writeStream.FlushAsync();
				writeStream.Flush(flushToDisk: true);
			}

			stage = KeyInitializationStage.ValidateKeyFile;
			FileInfo createdFile = new(absoluteOutputPath);
			createdFile.Refresh();

			if (!createdFile.Exists ||
				(createdFile.Length != 32) ||
				((createdFile.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(parentPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath))
			{
				return WriteKeyInitializationFailure(stage);
			}

			shouldDeletePartialKey = false;
			shouldDeleteCreatedDirectory = false;
			Console.WriteLine("Live R0 HMAC key initialized.");
			return 0;
		}
		catch
		{
			return WriteKeyInitializationFailure(
				stage,
				directoryFailureReason);
		}
		finally
		{
			if (shouldDeletePartialKey && (createdKeyPath is not null))
			{
				TryDeletePartialKey(createdKeyPath);
			}

			if (shouldDeleteCreatedDirectory &&
				(createdDirectoryPath is not null))
			{
				AntigravityPrivateKeyAcl.TryDeleteEmptyCreatedDirectory(
					createdDirectoryPath);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static void TryDeletePartialKey(string absoluteKeyPath)
	{
		try
		{
			string? parentPath = Path.GetDirectoryName(absoluteKeyPath);
			FileInfo partialKey = new(absoluteKeyPath);
			partialKey.Refresh();

			if ((parentPath is null) ||
				!IsNonReparseDirectoryChain(parentPath) ||
				!partialKey.Exists ||
				((partialKey.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
			{
				return;
			}

			File.Delete(absoluteKeyPath);
		}
		catch
		{
			// Best-effort cleanup must never mask the fail-closed result.
		}
	}

	private static bool IsNonReparseDirectoryChain(
		string absoluteDirectoryPath)
	{
		DirectoryInfo? directory = new(absoluteDirectoryPath);

		while (directory is not null)
		{
			directory.Refresh();

			if (!directory.Exists ||
				((directory.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			directory = directory.Parent;
		}

		return true;
	}

	private static bool TryParseInitHmacKeyCommand(
		IReadOnlyList<string> args,
		out string outputPath)
	{
		outputPath = string.Empty;

		if ((args.Count != 4) ||
			!string.Equals(
				args[0],
				"init-hmac-key",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--output", StringComparison.Ordinal) ||
			!string.Equals(args[3], ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		outputPath = args[2];
		return true;
	}

	private static bool TryParseLiveCommand(
		IReadOnlyList<string> args,
		out AntigravityLiveR0Mode mode,
		out string profilePath)
	{
		mode = default;
		profilePath = string.Empty;

		if ((args.Count != 4) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		if (string.Equals(
			args[0],
			"observe-prompt",
			StringComparison.Ordinal))
		{
			mode = AntigravityLiveR0Mode.ObservePrompt;
		}
		else if (string.Equals(
			args[0],
			"calibrate-prompt",
			StringComparison.Ordinal))
		{
			mode = AntigravityLiveR0Mode.CalibratePrompt;
		}
		else if (string.Equals(
			args[0],
			"capture-usage",
			StringComparison.Ordinal))
		{
			mode = AntigravityLiveR0Mode.CaptureUsage;
		}
		else
		{
			return false;
		}

		profilePath = args[2];
		return true;
	}

	private static bool TryParseLiveR1Command(
		IReadOnlyList<string> args,
		out string profilePath)
	{
		profilePath = string.Empty;

		if ((args.Count != 4) ||
			!string.Equals(
				args[0],
				"capture-usage-r1",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], R1ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		return true;
	}

	private static bool TryParsePrivateR1CalibrationCaptureCommand(
		IReadOnlyList<string> args,
		out string profilePath,
		out string outputPath)
	{
		profilePath = string.Empty;
		outputPath = string.Empty;

		if ((args.Count != 6) ||
			!string.Equals(
				args[0],
				"capture-usage-r1-private",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], "--output", StringComparison.Ordinal) ||
			!string.Equals(
				args[5],
				PrivateR1ConsentFlag,
				StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		outputPath = args[4];
		return true;
	}

	private static bool TryParseOfflineUsageCalibrationCommand(
		IReadOnlyList<string> args,
		out string privateBundlePath,
		out string reviewedSpecPath,
		out string outputPath)
	{
		privateBundlePath = string.Empty;
		reviewedSpecPath = string.Empty;
		outputPath = string.Empty;

		if ((args.Count != 7) ||
			!string.Equals(
				args[0],
				"calibrate-usage-layout-offline",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--input", StringComparison.Ordinal) ||
			!string.Equals(
				args[3],
				"--reviewed-spec",
				StringComparison.Ordinal) ||
			!string.Equals(args[5], "--output", StringComparison.Ordinal))
		{
			return false;
		}

		privateBundlePath = args[2];
		reviewedSpecPath = args[4];
		outputPath = args[6];
		return true;
	}

	private static bool TryParsePrivateR1SectionDraftCommand(
		IReadOnlyList<string> args,
		out string profilePath,
		out string privateBundlePath,
		out string outputPath)
	{
		profilePath = string.Empty;
		privateBundlePath = string.Empty;
		outputPath = string.Empty;

		if ((args.Count != 7) ||
			!string.Equals(
				args[0],
				"derive-private-r1-section-spec-draft",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], "--input", StringComparison.Ordinal) ||
			!string.Equals(args[5], "--output", StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		privateBundlePath = args[4];
		outputPath = args[6];
		return true;
	}

	private static bool TryParsePrivateR0ViewportProfileDerivationCommand(
		IReadOnlyList<string> args,
		out string sourceProfilePath,
		out string outputPath,
		out string columnsValue,
		out string rowsValue)
	{
		sourceProfilePath = string.Empty;
		outputPath = string.Empty;
		columnsValue = string.Empty;
		rowsValue = string.Empty;

		if ((args.Count != 9) ||
			!string.Equals(
				args[0],
				"derive-private-r0-viewport-profile",
				StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], "--output", StringComparison.Ordinal) ||
			!string.Equals(args[5], "--columns", StringComparison.Ordinal) ||
			!string.Equals(args[7], "--rows", StringComparison.Ordinal))
		{
			return false;
		}

		sourceProfilePath = args[2];
		outputPath = args[4];
		columnsValue = args[6];
		rowsValue = args[8];
		return true;
	}

	private static bool TryParsePinPromptCommand(
		IReadOnlyList<string> args,
		out string profilePath,
		out string structuralFingerprint,
		out string exactFingerprint)
	{
		profilePath = string.Empty;
		structuralFingerprint = string.Empty;
		exactFingerprint = string.Empty;

		if ((args.Count != 8) ||
			!string.Equals(args[0], "pin-prompt", StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], "--structural", StringComparison.Ordinal) ||
			!string.Equals(args[5], "--exact", StringComparison.Ordinal) ||
			!string.Equals(args[7], ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		structuralFingerprint = args[4];
		exactFingerprint = args[6];
		return true;
	}

	private static bool TryParseRepinPromptCommand(
		IReadOnlyList<string> args,
		out string profilePath,
		out string expectedStructuralFingerprint,
		out string expectedExactFingerprint,
		out string structuralFingerprint,
		out string exactFingerprint)
	{
		profilePath = string.Empty;
		expectedStructuralFingerprint = string.Empty;
		expectedExactFingerprint = string.Empty;
		structuralFingerprint = string.Empty;
		exactFingerprint = string.Empty;

		if ((args.Count != 12) ||
			!string.Equals(args[0], "repin-prompt", StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(
				args[3],
				"--expected-structural",
				StringComparison.Ordinal) ||
			!string.Equals(
				args[5],
				"--expected-exact",
				StringComparison.Ordinal) ||
			!string.Equals(args[7], "--structural", StringComparison.Ordinal) ||
			!string.Equals(args[9], "--exact", StringComparison.Ordinal) ||
			!string.Equals(args[11], ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		expectedStructuralFingerprint = args[4];
		expectedExactFingerprint = args[6];
		structuralFingerprint = args[8];
		exactFingerprint = args[10];
		return true;
	}

	private static bool TryParsePinUsageCommand(
		IReadOnlyList<string> args,
		out string profilePath,
		out string structuralFingerprint,
		out string exactFingerprint)
	{
		profilePath = string.Empty;
		structuralFingerprint = string.Empty;
		exactFingerprint = string.Empty;

		if ((args.Count != 8) ||
			!string.Equals(args[0], "pin-usage", StringComparison.Ordinal) ||
			!string.Equals(args[1], "--profile", StringComparison.Ordinal) ||
			!string.Equals(args[3], "--structural", StringComparison.Ordinal) ||
			!string.Equals(args[5], "--exact", StringComparison.Ordinal) ||
			!string.Equals(args[7], ConsentFlag, StringComparison.Ordinal))
		{
			return false;
		}

		profilePath = args[2];
		structuralFingerprint = args[4];
		exactFingerprint = args[6];
		return true;
	}

	private static int WriteProfileFailure()
	{
		Console.Error.WriteLine("Live R0 profile validation failed closed.");
		return 2;
	}

	private static int WriteR1ProfileFailure()
	{
		Console.Error.WriteLine("Live R1 profile validation failed closed.");
		return 2;
	}

	private static int WritePrivateR1CalibrationCaptureFailure(string stage)
	{
		Console.Error.WriteLine(
			$"Private AGY R1 calibration capture failed closed at stage: {stage}.");
		return 2;
	}

	private static int WritePrivateR0ViewportProfileDerivationFailure(
		string stage)
	{
		Console.Error.WriteLine(
			$"Private R0 viewport profile derivation failed closed at stage: {stage}.");
		return 2;
	}

	private static bool IsPrivateR1CalibrationOutputPathSafe(
		string profilePath,
		string keyPath,
		string outputPath)
	{
		try
		{
			if (!Path.IsPathFullyQualified(profilePath) ||
				!Path.IsPathFullyQualified(keyPath) ||
				!Path.IsPathFullyQualified(outputPath) ||
				!string.Equals(
					Path.GetExtension(outputPath),
					".json",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string absoluteProfilePath = Path.GetFullPath(profilePath);
			string absoluteKeyPath = Path.GetFullPath(keyPath);
			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string profileDirectory =
				Path.GetDirectoryName(absoluteProfilePath) ?? string.Empty;
			string outputDirectory =
				Path.GetDirectoryName(absoluteOutputPath) ?? string.Empty;

			return
				string.Equals(
					profileDirectory,
					outputDirectory,
					StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(
					absoluteOutputPath,
					absoluteProfilePath,
					StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(
					absoluteOutputPath,
					absoluteKeyPath,
					StringComparison.OrdinalIgnoreCase) &&
				AntigravityPrivateKeyAcl.IsPrivateDirectory(
					outputDirectory);
		}
		catch
		{
			return false;
		}
	}

	private static int WriteKeyInitializationFailure(
		KeyInitializationStage stage,
		AntigravityPrivateKeyDirectoryFailureReason directoryFailureReason =
			AntigravityPrivateKeyDirectoryFailureReason.None)
	{
		string stageCode =
			(stage == KeyInitializationStage.PreparePrivateDirectory) &&
			(directoryFailureReason !=
				AntigravityPrivateKeyDirectoryFailureReason.None)
				? $"{stage}.{directoryFailureReason}"
				: stage.ToString();
		Console.Error.WriteLine(
			$"Live R0 HMAC key initialization failed closed at stage: {stageCode}.");
		return 2;
	}

	private static int WritePrivateR1SectionDraftFailure(string stage)
	{
		Console.Error.WriteLine(
			$"Private AGY R1 section draft failed closed at stage: {stage}.");
		return 2;
	}

	private static void PrintHelp()
	{
		Console.WriteLine("AGY feasibility harness (dev-only)");
		Console.WriteLine();
		Console.WriteLine("The current build contains offline capability, ConPTY, terminal-rendering, and safety-gate primitives.");
		Console.WriteLine("No production AGY layout or executable fingerprint is compiled into this tool.");
		Console.WriteLine();
		Console.WriteLine("Offline R1 commands (do not launch AGY or write terminal input):");
		Console.WriteLine("  calibrate-usage-layout-offline --input <absolute-private-bundle> --reviewed-spec <absolute-reviewed-spec> --output <absolute-reviewed-layout>");
		Console.WriteLine("  derive-private-r1-section-spec-draft --profile <absolute-private-r0> --input <absolute-private-bundle> --output <absolute-private-draft>");
		Console.WriteLine("  promote-private-r1-section-spec --profile <absolute-private-r0> --input <absolute-private-bundle> --draft <absolute-private-draft> --output <absolute-reviewed-spec> --approved-fingerprint <64hex>");
		Console.WriteLine("  compose-private-r1-section-profile --profile <absolute-private-r0> --layout <absolute-reviewed-layout> --output <absolute-private-r1-profile>");
		Console.WriteLine("  derive-private-r0-viewport-profile --profile <absolute-private-r0> --output <absolute-private-r0> --columns <1-1000> --rows <1-256>");
		Console.WriteLine("  reanchor-reviewed-package-prompt --input <reviewed-package-manifest> --output <new-temporary-json> --contract-id <stable-reviewed-contract-id> --structural <64hex> --i-understand-reviewed-prompt-reanchor");
		Console.WriteLine();
		Console.WriteLine("Executable verification commands (run only the fixed --version probe; do not write terminal input):");
		Console.WriteLine("  promote-executable-fingerprint --profile <absolute-private-r0> --output <absolute-private-r0> --approved-sha256 <64hex>");
		Console.WriteLine("  export-reviewed-package-manifest --profile <absolute-reviewed-private-profile> --output <new-temporary-json> --contract-id <stable-reviewed-contract-id> --i-understand-public-manifest-export");
		Console.WriteLine();
		Console.WriteLine("Consented local-only commands:");
		Console.WriteLine("  calibrate-reviewed-package-prompt --profile <absolute-private-r1-profile> --i-understand-live-r0");
		Console.WriteLine("  init-hmac-key --output <absolute .key path> --i-understand-live-r0");
		Console.WriteLine("  pin-prompt --profile <absolute-json> --structural <64hex> --exact <64hex> --i-understand-live-r0");
		Console.WriteLine("  repin-prompt --profile <absolute-json> --expected-structural <64hex> --expected-exact <64hex> --structural <64hex> --exact <64hex> --i-understand-live-r0");
		Console.WriteLine("  pin-usage --profile <absolute-json> --structural <64hex> --exact <64hex> --i-understand-live-r0");
		Console.WriteLine("  calibrate-prompt --profile <absolute-path> --i-understand-live-r0");
		Console.WriteLine("  observe-prompt --profile <absolute-path> --i-understand-live-r0");
		Console.WriteLine("  capture-usage --profile <absolute-path> --i-understand-live-r0");
		Console.WriteLine("  capture-usage-r1 --profile <absolute-path> --i-understand-live-r1");
		Console.WriteLine("  capture-usage-r1-section --profile <absolute-private-r1-profile> --i-understand-live-r1");
		Console.WriteLine("  capture-usage-r1-private --profile <absolute-r0-profile> --output <absolute-private-bundle> --i-understand-live-private-r1");
	}
}
