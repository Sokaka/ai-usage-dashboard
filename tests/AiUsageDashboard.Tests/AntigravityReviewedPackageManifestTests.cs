using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityReviewedPackageManifestTests
{
	private const int Columns = 80;
	private const int Rows = 5;
	private const string PrivateIdentity =
		"PRIVATE-PORTABLE-MANIFEST@example.invalid";
	private static readonly string ExecutableSha256 = new('A', 64);
	private static readonly string PromptStructuralFingerprint = new('B', 64);
	private static readonly string SignerThumbprint = new('C', 40);

	[Fact]
	public void Serialize_WithPortableReviewedContract_RoundTripsStrictly()
	{
		AntigravityReviewedPackageManifestFile expected = CreateManifest();

		byte[] bytes = AntigravityReviewedPackageManifestJson.Serialize(expected);
		string json = Encoding.UTF8.GetString(bytes);
		AntigravityReviewedPackageManifestFile actual =
			AntigravityReviewedPackageManifestJson.Deserialize(bytes);
		AntigravityReviewedPackageContract contract =
			Assert.Single(actual.Contracts);
		AntigravityUsageR1SectionParseResult parsed =
			AntigravityUsageR1SectionSchemaParser.ParseCalibrationPage(
				CreateSnapshot(PrivateIdentity, 5),
				contract.UsageLayout.Spec);

		Assert.Equal(
			AntigravityReviewedPackageManifestFile.CurrentFormatVersion,
			actual.FormatVersion);
		Assert.Equal(AntigravityUsageR1ReviewState.Reviewed, actual.ReviewState);
		Assert.Equal("agy.synthetic.v1", contract.ContractId);
		Assert.Equal(
			contract.UsageLayout.ExpectedPageFingerprints[0],
			parsed.Capture.PageFingerprint);
		Assert.Equal(
			contract.ContractFingerprint,
			AntigravityReviewedPackageManifestJson
				.ComputeContractFingerprint(contract));
		Assert.DoesNotContain(
			PrivateIdentity,
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("50%", json, StringComparison.Ordinal);

		string[] forbiddenProperties =
		{
			"AbsolutePath",
			"WorkingDirectory",
			"Environment",
			"LocalFingerprintHmacKeyPath",
			"ExpectedExactPromptFingerprint",
			"NonCredentialSettingsFiles",
			"SettingsBaselineFingerprint",
			"CaptureContractFingerprint",
			"ApprovedDraftFingerprint",
			"AccountIdentity",
			"Snapshot",
			"Sequences"
		};

		foreach (string property in forbiddenProperties)
		{
			Assert.DoesNotContain(
				$"\"{property}\"",
				json,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void Deserialize_WithNonStrictShape_RejectsEveryVariant()
	{
		string validJson = Encoding.UTF8.GetString(
			AntigravityReviewedPackageManifestJson.Serialize(CreateManifest()));
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				"{",
				"{\n  \"Unexpected\": true,"),
			ReplaceFirst(
				validJson,
				"\"FormatVersion\": \"agy-reviewed-package-manifest-v1\",",
				"\"FormatVersion\": \"agy-reviewed-package-manifest-v1\",\n  \"FormatVersion\": \"agy-reviewed-package-manifest-v1\","),
			ReplaceFirst(validJson, "\"ReviewState\"", "\"reviewState\""),
			ReplaceFirst(
				validJson,
				"\"ReviewState\": \"Reviewed\"",
				"\"ReviewState\": 0"),
			ReplaceFirst(
				validJson,
				"\"ReviewState\": \"Reviewed\"",
				"\"ReviewState\": \"reviewed\""),
			ReplaceFirst(
				validJson,
				"\"ReviewState\": \"Reviewed\"",
				"\"ReviewState\": \"REVIEWED\""),
			ReplaceFirst(
				validJson,
				"\"ReviewState\": \"Reviewed\"",
				"\"ReviewState\": \" Reviewed \""),
			ReplaceFirst(
				validJson,
				"\"ReviewState\": \"Reviewed\"",
				"\"ReviewState\": \"Reviewed, Reviewed\""),
			ReplaceFirst(
				validJson,
				"\"Executable\": {",
				"\"Executable\": {\n        \"AbsolutePath\": \"C:\\\\Users\\\\test\\\\agy.exe\","),
			ReplaceFirst(
				validJson,
				"\"UsageLayout\": {",
				"\"UsageLayout\": {\n        \"CaptureContractFingerprint\": \"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\","),
			ReplaceFirst(validJson, "\"WindowKind\": \"Weekly\"", "\"WindowKind\": 0"),
			ReplaceFirst(validJson, "\"Kind\": \"Identity\"", "\"Kind\": \"identity\""),
			ReplaceFirst(validJson, "\"Order\": 0,", string.Empty)
		};

		foreach (string invalidJson in invalidJsonCases)
		{
			Assert.Throws<JsonException>(() =>
				AntigravityReviewedPackageManifestJson.Deserialize(invalidJson));
		}
	}

	[Fact]
	public void Deserialize_WithTamperedReviewedContract_RejectsEveryVariant()
	{
		AntigravityReviewedPackageManifestFile manifest = CreateManifest();
		AntigravityReviewedPackageContract contract =
			Assert.Single(manifest.Contracts);
		string validJson = Encoding.UTF8.GetString(
			AntigravityReviewedPackageManifestJson.Serialize(manifest));
		string[] invalidJsonCases =
		{
			ReplaceFirst(
				validJson,
				"\"CliVersion\": \"1.2.3\"",
				"\"CliVersion\": \"1.2.4\""),
			ReplaceFirst(
				validJson,
				$"\"ExpectedPromptStructuralFingerprint\": \"{PromptStructuralFingerprint}\"",
				$"\"ExpectedPromptStructuralFingerprint\": \"{new string('D', 64)}\""),
			ReplaceFirst(
				validJson,
				$"\"SchemaFingerprint\": \"{contract.UsageLayout.SchemaFingerprint}\"",
				$"\"SchemaFingerprint\": \"{new string('D', 64)}\""),
			ReplaceFirst(
				validJson,
				$"\"{contract.UsageLayout.ExpectedPageFingerprints[0]}\"",
				$"\"{new string('D', 64)}\""),
			ReplaceFirst(
				validJson,
				$"\"ContractFingerprint\": \"{contract.ContractFingerprint}\"",
				$"\"ContractFingerprint\": \"{new string('D', 64)}\""),
			ReplaceFirst(
				validJson,
				"\"RenderedLiteral\": \"Synthetic Section\"",
				"\"RenderedLiteral\": \"Changed Section\"")
		};

		foreach (string invalidJson in invalidJsonCases)
		{
			Assert.Throws<JsonException>(() =>
				AntigravityReviewedPackageManifestJson.Deserialize(invalidJson));
		}
	}

	[Fact]
	public void Serialize_WithNeedsReviewOrNonCanonicalFingerprint_Rejects()
	{
		AntigravityReviewedPackageManifestFile manifest = CreateManifest();
		AntigravityReviewedPackageContract contract =
			Assert.Single(manifest.Contracts);
		AntigravityReviewedPackageManifestFile needsReview = manifest with
		{
			ReviewState = AntigravityUsageR1ReviewState.NeedsReview
		};
		AntigravityReviewedPackageManifestFile lowercaseFingerprint =
			manifest with
			{
				Contracts = new[]
				{
					contract with
					{
						ExpectedPromptStructuralFingerprint =
							PromptStructuralFingerprint.ToLowerInvariant()
					}
				}
			};

		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Serialize(needsReview));
		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Serialize(
				lowercaseFingerprint));
	}

	[Fact]
	public void Serialize_WithDuplicateContractId_Rejects()
	{
		AntigravityReviewedPackageManifestFile manifest = CreateManifest();
		AntigravityReviewedPackageContract contract =
			Assert.Single(manifest.Contracts);
		AntigravityReviewedPackageManifestFile duplicate = manifest with
		{
			Contracts = new[] { contract, contract }
		};

		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Serialize(duplicate));
	}

	[Fact]
	public void Serialize_WithCompatibleBuild_RoundTripsAndRejectsTampering()
	{
		AntigravityReviewedPackageManifestFile manifest = CreateManifest();
		AntigravityReviewedPackageContract contract =
			Assert.Single(manifest.Contracts);
		AntigravityReviewedPackageExecutable compatibleExecutable =
			contract.Executable with
			{
				CliVersion = "1.2.4",
				Sha256 = new string('D', 64)
			};
		AntigravityReviewedPackageContract compatibleDraft = new(
			"agy.synthetic.v1.2.4",
			compatibleExecutable,
			contract.ExpectedPromptStructuralFingerprint,
			contract.UsageLayout,
			ContractFingerprint: string.Empty);
		AntigravityReviewedPackageCompatibleBuild compatibleBuild = new(
			compatibleDraft.ContractId,
			compatibleExecutable,
			AntigravityReviewedPackageManifestJson
				.ComputeContractFingerprint(compatibleDraft));
		AntigravityReviewedPackageManifestFile compatibleManifest =
			manifest with
			{
				Contracts = new[]
				{
					contract with
					{
						CompatibleBuilds = new[] { compatibleBuild }
					}
				}
			};

		byte[] bytes = AntigravityReviewedPackageManifestJson.Serialize(
			compatibleManifest);
		AntigravityReviewedPackageManifestFile actual =
			AntigravityReviewedPackageManifestJson.Deserialize(bytes);
		AntigravityReviewedPackageCompatibleBuild actualCompatibleBuild =
			Assert.Single(Assert.Single(actual.Contracts).CompatibleBuilds!);
		string json = Encoding.UTF8.GetString(bytes);

		Assert.Equal(compatibleBuild, actualCompatibleBuild);
		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Deserialize(
				ReplaceFirst(
					json,
					"\"CliVersion\": \"1.2.4\"",
					"\"CliVersion\": \"1.2.5\"")));
		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Serialize(
				compatibleManifest with
				{
					Contracts = new[]
					{
						contract with
						{
							CompatibleBuilds = new[]
							{
								compatibleBuild with
								{
									ContractId = contract.ContractId
								}
							}
						}
					}
				}));
	}

	[Fact]
	public void Serialize_WithPrivateValueOrInvalidContractId_Rejects()
	{
		AntigravityReviewedPackageManifestFile manifest = CreateManifest();
		AntigravityReviewedPackageContract contract =
			Assert.Single(manifest.Contracts);
		AntigravityReviewedPackageContract privateDraft = contract with
		{
			Executable = contract.Executable with
			{
				SignerSubject = "CN=private-user@example.invalid"
			},
			ContractFingerprint = string.Empty
		};
		AntigravityReviewedPackageContract privateContract =
			privateDraft with
			{
				ContractFingerprint =
					AntigravityReviewedPackageManifestJson
						.ComputeContractFingerprint(privateDraft)
			};
		AntigravityReviewedPackageManifestFile privateManifest =
			manifest with
			{
				Contracts = new[] { privateContract }
			};

		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.Serialize(privateManifest));
		Assert.Throws<JsonException>(() =>
			AntigravityReviewedPackageManifestJson.ComputeContractFingerprint(
				contract with
				{
					ContractId = ".hidden",
					ContractFingerprint = string.Empty
				}));
	}

	[Fact]
	public void EmbeddedCatalog_LoadsAndMatchesOnlyExactReviewedContract()
	{
		IReadOnlyList<AntigravityReviewedPackageContract> contracts =
			AntigravityReviewedPackageManifestCatalog.Contracts;
		Assert.Equal(2, contracts.Count);
		AntigravityReviewedPackageContract currentContract = Assert.Single(
			contracts,
			contract => string.Equals(
				contract.Executable.CliVersion,
				"1.1.9",
				StringComparison.Ordinal));
		Assert.Equal(
			"83FB6E9D80E751D174B3738C3EEFB054E75E85E47B17D1E159FE4831ADCEADC8",
			currentContract.Executable.Sha256);
		Assert.Equal(
			"607A3EDAA64933E94422FC8F0C80388E0590986C",
			currentContract.Executable.SignerThumbprint);
		Assert.Equal(
			contracts[0].ExpectedPromptStructuralFingerprint,
			currentContract.ExpectedPromptStructuralFingerprint);
		Assert.Equal(
			contracts[0].UsageLayout.SchemaFingerprint,
			currentContract.UsageLayout.SchemaFingerprint);
		Assert.Equal(
			contracts[0].UsageLayout.ExpectedPageFingerprints,
			currentContract.UsageLayout.ExpectedPageFingerprints);
		byte[] bytes = AntigravityReviewedPackageManifestJson.Serialize(
			new AntigravityReviewedPackageManifestFile(
				AntigravityReviewedPackageManifestFile.CurrentFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				contracts));

		try
		{
			string json = Encoding.UTF8.GetString(bytes);
			Assert.DoesNotContain(
				"AbsolutePath",
				json,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"AccountIdentity",
				json,
				StringComparison.Ordinal);
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(
				bytes);
		}

		AntigravityReviewedPackageContract contract = contracts[0];
		AntigravityLiveR1SectionProfileFile profile =
			CreateProfileFile(contract);
		Assert.Equal(
			contract,
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				profile));
		Assert.Equal(
			currentContract,
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				CreateProfileFile(currentContract)));
		Assert.Null(
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				profile with
				{
					ExecutableFingerprint =
						profile.ExecutableFingerprint with
						{
							Sha256 = new string('D', 64)
						}
				}));
		Assert.Null(
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				profile with
				{
					PromptGuard = profile.PromptGuard with
					{
						ExpectedPromptStructuralFingerprint =
							new string('D', 64)
					}
				}));
		Assert.Null(
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				profile with
				{
					ReviewedUsageLayout =
						profile.ReviewedUsageLayout with
						{
							SchemaFingerprint = new string('D', 64)
						}
				}));
		Assert.Null(
			AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
				profile with
				{
					ReviewedUsageLayout =
						profile.ReviewedUsageLayout with
						{
							ExpectedPageFingerprints =
								new[] { new string('D', 64) }
						}
				}));
	}

	[Fact]
	public void LocalBinding_RequiresExactContractCaptureAndKey()
	{
		byte[] key = Enumerable.Range(0, 32)
			.Select(value => (byte)value)
			.ToArray();
		string contractFingerprint = new('A', 64);
		string captureFingerprint = new('B', 64);
		string binding = AntigravityReviewedPackageLocalBinding.Compute(
			contractFingerprint,
			captureFingerprint,
			key);

		Assert.True(AntigravityReviewedPackageLocalBinding.Matches(
			contractFingerprint,
			captureFingerprint,
			key,
			binding));
		Assert.False(AntigravityReviewedPackageLocalBinding.Matches(
			new string('C', 64),
			captureFingerprint,
			key,
			binding));
		Assert.False(AntigravityReviewedPackageLocalBinding.Matches(
			contractFingerprint,
			new string('C', 64),
			key,
			binding));

		key[0] ^= 0xFF;
		Assert.False(AntigravityReviewedPackageLocalBinding.Matches(
			contractFingerprint,
			captureFingerprint,
			key,
			binding));
	}

	private static AntigravityLiveR1SectionProfileFile CreateProfileFile(
		AntigravityReviewedPackageContract contract)
	{
		AntigravityReviewedPackageExecutable executable =
			contract.Executable;
		AntigravityUsageR1SectionSpec spec = contract.UsageLayout.Spec;
		return new AntigravityLiveR1SectionProfileFile(
			AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
			"synthetic-profile",
			new AntigravityCliFingerprint(
				"synthetic-absolute-path",
				executable.CliVersion,
				executable.FileVersion,
				executable.ProductVersion,
				executable.Sha256,
				executable.WinVerifyTrustStatus,
				executable.SignerSubject,
				executable.SignerThumbprint),
			"synthetic-working-directory",
			new Dictionary<string, string>(),
			checked((short)spec.Columns),
			checked((short)spec.Rows),
			new AntigravityLiveR1PromptGuard(
				contract.ExpectedPromptStructuralFingerprint,
				new string('D', 64),
				"synthetic-key-path"),
			new AntigravityUsageR1SectionLayoutFile(
				AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				spec,
				contract.UsageLayout.SchemaFingerprint,
				contract.UsageLayout.ExpectedPageFingerprints,
				new string('E', 64),
				new string('F', 64)),
			new[] { "synthetic-settings-path" },
			1024,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromSeconds(5));
	}

	private static AntigravityReviewedPackageManifestFile CreateManifest()
	{
		AntigravityUsageR1SectionSpec spec = CreateSpec();
		string schemaFingerprint =
			AntigravityUsageR1SectionSchemaParser.ComputeSchemaFingerprint(
				AntigravityUsageR1SectionSchemaParser.FreezeAndValidateSpec(
					spec));
		string pageFingerprint =
			AntigravityUsageR1SectionSchemaParser.ComputePageFingerprint(
				spec);
		AntigravityReviewedPackageUsageLayout layout = new(
			spec,
			schemaFingerprint,
			new[] { pageFingerprint });
		AntigravityReviewedPackageExecutable executable = new(
			"1.2.3",
			"1.2.3.0",
			"1.2.3",
			ExecutableSha256,
			0,
			"CN=Synthetic Publisher, O=Example Organization",
			SignerThumbprint);
		AntigravityReviewedPackageContract draft = new(
			"agy.synthetic.v1",
			executable,
			PromptStructuralFingerprint,
			layout,
			ContractFingerprint: string.Empty);
		AntigravityReviewedPackageContract contract = draft with
		{
			ContractFingerprint =
				AntigravityReviewedPackageManifestJson
					.ComputeContractFingerprint(draft)
		};
		return new AntigravityReviewedPackageManifestFile(
			AntigravityReviewedPackageManifestFile.CurrentFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			new[] { contract });
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
			"portable-manifest-test",
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

	private static TerminalScreenSnapshot CreateSnapshot(
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
		return new TerminalScreenSnapshot(
			Columns,
			Rows,
			false,
			lines);
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

	private static string ReplaceFirst(
		string value,
		string oldValue,
		string newValue)
	{
		int index = value.IndexOf(oldValue, StringComparison.Ordinal);
		Assert.True(index >= 0, $"Could not find replacement source: {oldValue}");
		return string.Concat(
			value.AsSpan(0, index),
			newValue,
			value.AsSpan(index + oldValue.Length));
	}
}
