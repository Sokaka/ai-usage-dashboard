using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityLiveR1SectionProfileTests
{
	private const short ScreenColumns = 80;
	private const short ScreenRows = 5;
	private static readonly string ApprovedDraftFingerprint = new('8', 64);
	private static readonly string PlaceholderCaptureContractFingerprint =
		new('7', 64);
	private static readonly string PromptExactFingerprint = new('B', 64);
	private static readonly string PromptStructuralFingerprint = new('A', 64);
	private static readonly string SettingsBaselineFingerprint = new('9', 64);
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
	public async Task ToProfileAsync_WithReviewedSectionLayout_CreatesIsolatedProfileAndCanZeroKey()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		string profilePath = CreateProfilePath(keyPath);
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		await File.WriteAllTextAsync(
			profilePath,
			JsonSerializer.Serialize(profileFile, JsonOptions));

		(AntigravityLiveR1SectionProfile profile, byte[] loadedKey) =
			await profileFile.ToProfileAsync(profilePath);

		try
		{
			Assert.Equal(sourceKey, loadedKey);
			Assert.True(
				profile.CaptureProfile.ExactPromptFingerprintHmacKey.Span
					.SequenceEqual(sourceKey));
			Assert.Equal(
				PromptStructuralFingerprint,
				profile.CaptureProfile.ExpectedPromptStructuralFingerprint);
			Assert.Equal(
				PromptExactFingerprint,
				profile.CaptureProfile.ExpectedExactPromptFingerprint);
			Assert.Null(
				profile.CaptureProfile.ExpectedUsageStructuralFingerprint);
			Assert.Null(
				profile.CaptureProfile.ExpectedExactUsageFingerprint);
			Assert.Equal(
				"synthetic-r1-section-layout",
				profile.UsageLayout.Spec.LayoutId);
			Assert.Equal(ScreenColumns, profile.UsageLayout.Spec.Columns);
			Assert.Equal(ScreenRows, profile.UsageLayout.Spec.Rows);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
			CryptographicOperations.ZeroMemory(loadedKey);
		}

		Assert.All(loadedKey, value => Assert.Equal((byte)0, value));
		Assert.True(
			profile.CaptureProfile.ExactPromptFingerprintHmacKey.Span
				.ToArray()
				.All(value => value == 0));
	}

	[Fact]
	public void Deserialize_WithExactV3Shape_ValidatesNestedLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR1SectionProfileFile expected = CreateProfileFile(
			temporaryDirectory,
			Path.Combine(temporaryDirectory.Path, "profile.key"));
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
			expected,
			JsonOptions);

		AntigravityLiveR1SectionProfileFile actual =
			AntigravityLiveR1SectionProfileJson.Deserialize(bytes);

		Assert.Equal(
			AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
			actual.SchemaVersion);
		Assert.Equal(
			expected.ReviewedUsageLayout.SchemaFingerprint,
			actual.ReviewedUsageLayout.SchemaFingerprint);
		Assert.Equal(
			expected.ReviewedUsageLayout.CaptureContractFingerprint,
			actual.ReviewedUsageLayout.CaptureContractFingerprint);
		Assert.Equal(
			expected.ReviewedUsageLayout.ApprovedDraftFingerprint,
			actual.ReviewedUsageLayout.ApprovedDraftFingerprint);
		AntigravityUsageR1SectionLayout runtimeLayout =
			actual.ReviewedUsageLayout.ToLayout();
		Assert.Equal(
			expected.ReviewedUsageLayout.CaptureContractFingerprint,
			runtimeLayout.CaptureContractFingerprint);
		Assert.Equal(
			expected.ReviewedUsageLayout.ApprovedDraftFingerprint,
			runtimeLayout.ApprovedDraftFingerprint);
	}

	[Fact]
	public async Task ToProfileAsync_WithWrongSchemaVersion_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath) with
		{
			SchemaVersion = "agy-live-r1-profile-v1"
		};

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WhenViewportDiffersFromReviewedLayout_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath) with
		{
			Rows = ScreenRows - 1
		};

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithNeedsReviewLayout_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityUsageR1SectionLayoutFile layout =
			CreateReviewedLayoutFile() with
			{
				ReviewState = AntigravityUsageR1ReviewState.NeedsReview
			};
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath,
			layout);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithMismatchedSectionSchemaFingerprint_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityUsageR1SectionLayoutFile layout =
			CreateReviewedLayoutFile() with
			{
				SchemaFingerprint = new string('0', 64)
			};
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath,
			layout);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithInvalidPageFingerprint_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityUsageR1SectionLayoutFile layout =
			CreateReviewedLayoutFile() with
			{
				ExpectedPageFingerprints = new[] { "not-a-fingerprint" }
			};
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath,
			layout);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public void RuntimeProfile_WithLegacyUsagePins_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] runtimeKey = CreateKey();
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			Path.Combine(temporaryDirectory.Path, "profile.key"));

		try
		{
			AntigravityLiveR0Profile captureProfile = new(
				profileFile.Id,
				profileFile.ExecutableFingerprint,
				profileFile.WorkingDirectory,
				profileFile.Environment,
				profileFile.Columns,
				profileFile.Rows,
				profileFile.PromptGuard.ExpectedPromptStructuralFingerprint,
				runtimeKey,
				profileFile.PromptGuard.ExpectedExactPromptFingerprint,
				new string('C', 64),
				profileFile.NonCredentialSettingsFiles,
				profileFile.MaximumSavedOutputBytes,
				profileFile.PromptTimeout,
				profileFile.UsageTimeout,
				profileFile.StableScreenDuration,
				profileFile.PollInterval,
				profileFile.CleanupTimeout,
				new string('D', 64));
			AntigravityUsageR1SectionLayout usageLayout =
				profileFile.ReviewedUsageLayout.ToLayout();

			Assert.Throws<InvalidDataException>(() =>
				new AntigravityLiveR1SectionProfile(
					captureProfile,
					usageLayout));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(runtimeKey);
		}
	}

	[Fact]
	public void Deserialize_WithLegacyUsagePins_RejectsUnknownOuterFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonObject root = CreateProfileJson(temporaryDirectory);
		root[nameof(
			AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint)] =
			new string('C', 64);
		root[nameof(
			AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint)] =
			new string('D', 64);

		Assert.Throws<JsonException>(() =>
			AntigravityLiveR1SectionProfileJson.Deserialize(
				root.ToJsonString(JsonOptions)));
	}

	[Fact]
	public void Deserialize_WithOuterUnknownDuplicateOrCaseDrift_RejectsEveryVariant()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string validJson = CreateProfileJson(temporaryDirectory)
			.ToJsonString(JsonOptions);
		JsonObject unknownRoot = JsonNode.Parse(validJson)!.AsObject();
		unknownRoot["Unexpected"] = true;
		string[] invalidJsonCases =
		{
			unknownRoot.ToJsonString(JsonOptions),
			ReplaceFirst(
				validJson,
				"\"SchemaVersion\"",
				"\"schemaVersion\""),
			ReplaceFirst(
				validJson,
				$"\"SchemaVersion\": \"{AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion}\"",
				$"\"SchemaVersion\": \"{AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion}\",\n  \"SchemaVersion\": \"{AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion}\"")
		};

		foreach (string invalidJson in invalidJsonCases)
		{
			Assert.Throws<JsonException>(() =>
				AntigravityLiveR1SectionProfileJson.Deserialize(invalidJson));
		}
	}

	[Fact]
	public void Deserialize_WithNestedLayoutDrift_RejectsEveryVariant()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonObject missingProperty = CreateProfileJson(temporaryDirectory);
		missingProperty["ReviewedUsageLayout"]!
			.AsObject()
			.Remove("ExpectedPageFingerprints");
		JsonObject unknownProperty = CreateProfileJson(temporaryDirectory);
		unknownProperty["ReviewedUsageLayout"]!["Spec"]!["Unexpected"] = true;
		JsonObject integerEnum = CreateProfileJson(temporaryDirectory);
		integerEnum["ReviewedUsageLayout"]!["ReviewState"] = 0;
		JsonObject caseDrift = CreateProfileJson(temporaryDirectory);
		caseDrift["ReviewedUsageLayout"]!["Spec"]!["PaginationPolicy"] =
			"requireFullyVisible";
		string duplicateJson = CreateProfileJson(temporaryDirectory)
			.ToJsonString(JsonOptions);
		duplicateJson = ReplaceFirst(
			duplicateJson,
			"\"SchemaVersion\": 2",
			"\"SchemaVersion\": 2,\n      \"SchemaVersion\": 2");
		string[] invalidJsonCases =
		{
			missingProperty.ToJsonString(JsonOptions),
			unknownProperty.ToJsonString(JsonOptions),
			integerEnum.ToJsonString(JsonOptions),
			caseDrift.ToJsonString(JsonOptions),
			duplicateJson
		};

		foreach (string invalidJson in invalidJsonCases)
		{
			Assert.ThrowsAny<JsonException>(() =>
				AntigravityLiveR1SectionProfileJson.Deserialize(invalidJson));
		}
	}

	[Fact]
	public async Task ComposeAsync_WithValidInputs_WritesPrivateProfileIdempotently()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();

		try
		{
			string privateDirectory = CreateComposerPrivateDirectory(
				temporaryDirectory);
			string keyPath = WritePrivateFile(
				privateDirectory,
				"profile.key",
				sourceKey);
			AntigravityLiveR1SectionProfileFile template = CreateProfileFile(
				temporaryDirectory,
				keyPath);
			AntigravityLiveR0ProfileFile r0 = CreateR0ProfileFile(template);
			string r0Path = WritePrivateFile(
				privateDirectory,
				"r0.json",
				JsonSerializer.SerializeToUtf8Bytes(
					r0,
					JsonOptions));
			AntigravityUsageR1SectionLayoutFile reviewedLayout =
				await CreateComposerLayoutAsync(
					r0,
					r0Path,
					template.ReviewedUsageLayout);
			string layoutPath = WritePrivateFile(
				privateDirectory,
				"layout.json",
				AntigravityUsageR1SectionCalibrationJson.SerializeLayout(
					reviewedLayout));
			string outputPath = Path.Combine(privateDirectory, "r1.json");

			await AntigravityLiveR1SectionProfileComposer.ComposeAsync(
				r0Path,
				layoutPath,
				outputPath,
				settingsBaselineFingerprintProvider:
					ReturnSettingsBaselineFingerprintAsync);
			byte[] firstBytes = await File.ReadAllBytesAsync(outputPath);
			DateTime fixedTimestamp = new(
				2025,
				3,
				4,
				5,
				6,
				7,
				DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(outputPath, fixedTimestamp);
			await AntigravityLiveR1SectionProfileComposer.ComposeAsync(
				r0Path,
				layoutPath,
				outputPath,
				settingsBaselineFingerprintProvider:
					ReturnSettingsBaselineFingerprintAsync);
			byte[] secondBytes = await File.ReadAllBytesAsync(outputPath);

			Assert.Equal(firstBytes, secondBytes);
			Assert.Equal(fixedTimestamp, File.GetLastWriteTimeUtc(outputPath));
			Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(outputPath));
			_ = AntigravityLiveR1SectionProfileJson.Deserialize(secondBytes);
			Assert.Empty(Directory.EnumerateFiles(
				privateDirectory,
				"*.tmp",
				SearchOption.TopDirectoryOnly));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ComposeAsync_WhenViewportDoesNotMatch_FailsBeforeCommit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();

		try
		{
			string privateDirectory = CreateComposerPrivateDirectory(
				temporaryDirectory);
			string keyPath = WritePrivateFile(
				privateDirectory,
				"profile.key",
				sourceKey);
			AntigravityLiveR1SectionProfileFile template = CreateProfileFile(
				temporaryDirectory,
				keyPath);
			AntigravityLiveR0ProfileFile mismatched =
				CreateR0ProfileFile(template) with
				{
					Rows = checked((short)(template.Rows - 1))
				};
			string r0Path = WritePrivateFile(
				privateDirectory,
				"r0.json",
				JsonSerializer.SerializeToUtf8Bytes(mismatched, JsonOptions));
			AntigravityUsageR1SectionLayoutFile reviewedLayout =
				await CreateComposerLayoutAsync(
					mismatched,
					r0Path,
					template.ReviewedUsageLayout);
			string layoutPath = WritePrivateFile(
				privateDirectory,
				"layout.json",
				AntigravityUsageR1SectionCalibrationJson.SerializeLayout(
					reviewedLayout));
			string outputPath = Path.Combine(privateDirectory, "r1.json");

			InvalidDataException exception = await Assert.ThrowsAsync<
				InvalidDataException>(async () =>
					await AntigravityLiveR1SectionProfileComposer.ComposeAsync(
						r0Path,
						layoutPath,
						outputPath,
						settingsBaselineFingerprintProvider:
							ReturnSettingsBaselineFingerprintAsync));

			Assert.Equal("PreCommitValidation", exception.Message);
			Assert.False(File.Exists(outputPath));
			Assert.Empty(Directory.EnumerateFiles(
				privateDirectory,
				"*.tmp",
				SearchOption.TopDirectoryOnly));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ComposeAsync_WhenCurrentCaptureContractDiffers_FailsBeforeCommit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();

		try
		{
			string privateDirectory = CreateComposerPrivateDirectory(
				temporaryDirectory);
			string keyPath = WritePrivateFile(
				privateDirectory,
				"profile.key",
				sourceKey);
			AntigravityLiveR1SectionProfileFile template = CreateProfileFile(
				temporaryDirectory,
				keyPath);
			AntigravityLiveR0ProfileFile r0 = CreateR0ProfileFile(template);
			string r0Path = WritePrivateFile(
				privateDirectory,
				"r0.json",
				JsonSerializer.SerializeToUtf8Bytes(r0, JsonOptions));
			AntigravityUsageR1SectionLayoutFile matchingLayout =
				await CreateComposerLayoutAsync(
					r0,
					r0Path,
					template.ReviewedUsageLayout);
			string mismatchedCaptureContractFingerprint = string.Equals(
				matchingLayout.CaptureContractFingerprint,
				new string('0', 64),
				StringComparison.Ordinal)
				? new string('1', 64)
				: new string('0', 64);
			AntigravityUsageR1SectionLayoutFile mismatchedLayout =
				matchingLayout with
				{
					CaptureContractFingerprint =
						mismatchedCaptureContractFingerprint
				};
			string layoutPath = WritePrivateFile(
				privateDirectory,
				"layout.json",
				AntigravityUsageR1SectionCalibrationJson.SerializeLayout(
					mismatchedLayout));
			string outputPath = Path.Combine(privateDirectory, "r1.json");

			InvalidDataException exception = await Assert.ThrowsAsync<
				InvalidDataException>(async () =>
					await AntigravityLiveR1SectionProfileComposer.ComposeAsync(
						r0Path,
						layoutPath,
						outputPath,
						settingsBaselineFingerprintProvider:
							ReturnSettingsBaselineFingerprintAsync));

			Assert.Equal("ProvenanceValidation", exception.Message);
			Assert.False(File.Exists(outputPath));
			Assert.Empty(Directory.EnumerateFiles(
				privateDirectory,
				"*.tmp",
				SearchOption.TopDirectoryOnly));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	private static AntigravityLiveR1SectionProfileFile CreateProfileFile(
		TemporaryDirectory temporaryDirectory,
		string keyPath,
		AntigravityUsageR1SectionLayoutFile? reviewedLayout = null)
	{
		string settingsPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"settings.json"));
		File.WriteAllText(settingsPath, "{}");
		AntigravityCliFingerprint fingerprint = new(
			Path.GetFullPath(Path.Combine(
				temporaryDirectory.Path,
				"agy.exe")),
			"1.1.3",
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			new string('E', 64),
			0,
			"CN=Synthetic",
			new string('F', 40));
		return new AntigravityLiveR1SectionProfileFile(
			AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
			"synthetic-live-r1-section",
			fingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			ScreenColumns,
			ScreenRows,
			new AntigravityLiveR1PromptGuard(
				PromptStructuralFingerprint,
				PromptExactFingerprint,
				Path.GetFullPath(keyPath)),
			reviewedLayout ?? CreateReviewedLayoutFile(),
			new[] { settingsPath },
			64 * 1024,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(1),
			TimeSpan.FromMilliseconds(10),
			TimeSpan.FromSeconds(1));
	}

	private static AntigravityLiveR0ProfileFile CreateR0ProfileFile(
		AntigravityLiveR1SectionProfileFile profile)
	{
		return new AntigravityLiveR0ProfileFile(
			profile.Id,
			profile.ExecutableFingerprint,
			profile.WorkingDirectory,
			profile.Environment,
			profile.Columns,
			profile.Rows,
			profile.PromptGuard.ExpectedPromptStructuralFingerprint,
			profile.PromptGuard.LocalFingerprintHmacKeyPath,
			profile.PromptGuard.ExpectedExactPromptFingerprint,
			ExpectedUsageStructuralFingerprint: null,
			profile.NonCredentialSettingsFiles,
			profile.MaximumSavedOutputBytes,
			profile.PromptTimeout,
			profile.UsageTimeout,
			profile.StableScreenDuration,
			profile.PollInterval,
			profile.CleanupTimeout,
			ExpectedExactUsageFingerprint: null);
	}

	private static async Task<AntigravityUsageR1SectionLayoutFile>
		CreateComposerLayoutAsync(
			AntigravityLiveR0ProfileFile r0,
			string r0Path,
			AntigravityUsageR1SectionLayoutFile layout)
	{
		(AntigravityLiveR0Profile profile, byte[] loadedHmacKey) =
			await r0.ToProfileAsync(r0Path);

		try
		{
			string captureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					profile,
					layout.Spec.IsAlternateScreen,
					SettingsBaselineFingerprint);
			return layout with
			{
				CaptureContractFingerprint = captureContractFingerprint
			};
		}
		finally
		{
			CryptographicOperations.ZeroMemory(loadedHmacKey);
		}
	}

	private static Task<string> ReturnSettingsBaselineFingerprintAsync(
		AntigravityLiveR0Profile profile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(SettingsBaselineFingerprint);
	}

	private static string CreateComposerPrivateDirectory(
		TemporaryDirectory temporaryDirectory)
	{
		string path = Path.Combine(temporaryDirectory.Path, "composer-private");
		Assert.True(AntigravityPrivateKeyAcl.TryPrepareDirectory(path, out _));
		return path;
	}

	private static string WritePrivateFile(
		string directory,
		string fileName,
		ReadOnlySpan<byte> bytes)
	{
		string path = Path.Combine(directory, fileName);
		File.WriteAllBytes(path, bytes.ToArray());
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
		return path;
	}

	private static AntigravityUsageR1SectionLayoutFile
		CreateReviewedLayoutFile()
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
							new AntigravityUsageR1SectionProgressMeterRule(
								10,
								"\u2588",
								"\u2591",
								AntigravityUsageR1SectionPercentMeaning.Remaining,
								AntigravityUsageR1SectionMeterRounding.Nearest)),
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
		AntigravityUsageR1SectionSpec spec = new(
			2,
			"synthetic-r1-section-layout",
			ScreenColumns,
			ScreenRows,
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
		AntigravityUsageR1SectionSpec frozen =
			AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(spec);
		return new AntigravityUsageR1SectionLayoutFile(
			AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			frozen,
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(frozen),
			new[] { new string('C', 64) },
			PlaceholderCaptureContractFingerprint,
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

	private static JsonObject CreateProfileJson(
		TemporaryDirectory temporaryDirectory)
	{
		AntigravityLiveR1SectionProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			Path.Combine(temporaryDirectory.Path, "profile.key"));
		return JsonNode.Parse(
			JsonSerializer.Serialize(profileFile, JsonOptions))!
			.AsObject();
	}

	private static string CreateProfilePath(string keyPath)
	{
		return Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(keyPath)!,
			"profile.json"));
	}

	private static string CreatePrivateKeyFile(
		TemporaryDirectory temporaryDirectory,
		string fileName,
		byte[] contents)
	{
		string keyDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-key"));
		Assert.True(AntigravityPrivateKeyAcl.TryPrepareDirectory(
			keyDirectory,
			out _));
		string keyPath = Path.GetFullPath(Path.Combine(
			keyDirectory,
			fileName));
		File.WriteAllBytes(keyPath, contents);
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(keyPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(keyPath));
		return keyPath;
	}

	private static byte[] CreateKey()
	{
		return Enumerable.Range(1, 32)
			.Select(value => (byte)value)
			.ToArray();
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
}
