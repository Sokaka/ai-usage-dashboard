using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityLiveR1ProfileTests
{
	private const short ScreenColumns = 80;
	private const short ScreenRows = 12;
	private static readonly string PromptExactFingerprint = new('B', 64);
	private static readonly string PromptStructuralFingerprint = new('A', 64);

	[Fact]
	public async Task ToProfileAsync_WithReviewedLayout_CreatesIsolatedCaptureProfileAndCanZeroKey()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		string profilePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(keyPath)!,
			"profile.json"));
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		await File.WriteAllTextAsync(
			profilePath,
			JsonSerializer.Serialize(profileFile));
		(AntigravityLiveR1Profile profile, byte[] loadedKey) =
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
			Assert.Null(profile.CaptureProfile.ExpectedExactUsageFingerprint);
			Assert.Equal(
				"synthetic-r1-layout-v1",
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
	public void JsonShape_IsIndependentAndRejectsLegacyUsagePins()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			Path.GetFullPath(Path.Combine(
				temporaryDirectory.Path,
				"profile.key")));
		string json = JsonSerializer.Serialize(profileFile);

		Assert.DoesNotContain(
			nameof(AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint),
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			nameof(AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint),
			json,
			StringComparison.Ordinal);
		Assert.Contains(
			nameof(AntigravityLiveR1ProfileFile.PromptGuard),
			json,
			StringComparison.Ordinal);

		JsonObject root = JsonNode.Parse(json)!.AsObject();
		root[nameof(
			AntigravityLiveR0ProfileFile.ExpectedUsageStructuralFingerprint)] =
			new string('C', 64);
		root[nameof(AntigravityLiveR0ProfileFile.ExpectedExactUsageFingerprint)] =
			new string('D', 64);

		Assert.Throws<JsonException>(() =>
			JsonSerializer.Deserialize<AntigravityLiveR1ProfileFile>(
				root.ToJsonString()));
	}

	[Fact]
	public void JsonDeserialization_WithR0Shape_DoesNotTreatItAsR1()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.key"));
		AntigravityLiveR1ProfileFile r1Profile = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		AntigravityLiveR0ProfileFile r0Profile = new(
			r1Profile.Id,
			r1Profile.ExecutableFingerprint,
			r1Profile.WorkingDirectory,
			r1Profile.Environment,
			r1Profile.Columns,
			r1Profile.Rows,
			r1Profile.PromptGuard.ExpectedPromptStructuralFingerprint,
			r1Profile.PromptGuard.LocalFingerprintHmacKeyPath,
			r1Profile.PromptGuard.ExpectedExactPromptFingerprint,
			ExpectedUsageStructuralFingerprint: null,
			r1Profile.NonCredentialSettingsFiles,
			r1Profile.MaximumSavedOutputBytes,
			r1Profile.PromptTimeout,
			r1Profile.UsageTimeout,
			r1Profile.StableScreenDuration,
			r1Profile.PollInterval,
			r1Profile.CleanupTimeout,
			ExpectedExactUsageFingerprint: null);
		string r0Json = JsonSerializer.Serialize(r0Profile);

		Assert.Throws<JsonException>(() =>
			JsonSerializer.Deserialize<AntigravityLiveR1ProfileFile>(r0Json));
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
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath) with
		{
			SchemaVersion = "agy-live-r0"
		};

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(
					CreateProfilePath(keyPath));
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
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath) with
		{
			Columns = ScreenColumns - 1
		};

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(
					CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WhenReviewedSchemaFingerprintMismatches_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityUsageR1ReviewedLayoutFile reviewedLayout =
			CreateReviewedLayoutFile() with
			{
				SchemaFingerprint = new string('F', 64)
			};
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath,
			reviewedLayout);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(
					CreateProfilePath(keyPath));
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithBroadKeyAcl_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"broad.key"));
		await File.WriteAllBytesAsync(keyPath, sourceKey);
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(
					CreateProfilePath(keyPath));
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
		AntigravityLiveR1ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			Path.GetFullPath(Path.Combine(
				temporaryDirectory.Path,
				"profile.key")));

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
			AntigravityUsageR1Layout usageLayout =
				profileFile.ReviewedUsageLayout.ToLayout();

			Assert.Throws<InvalidDataException>(() =>
				new AntigravityLiveR1Profile(captureProfile, usageLayout));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(runtimeKey);
		}
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

	private static AntigravityLiveR1ProfileFile CreateProfileFile(
		TemporaryDirectory temporaryDirectory,
		string keyPath,
		AntigravityUsageR1ReviewedLayoutFile? reviewedLayout = null)
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
		return new AntigravityLiveR1ProfileFile(
			AntigravityLiveR1ProfileFile.CurrentSchemaVersion,
			"synthetic-live-r1",
			fingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			ScreenColumns,
			ScreenRows,
			new AntigravityLiveR1PromptGuard(
				PromptStructuralFingerprint,
				PromptExactFingerprint,
				keyPath),
			reviewedLayout ?? CreateReviewedLayoutFile(),
			new[] { settingsPath },
			64 * 1024,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(1),
			TimeSpan.FromMilliseconds(10),
			TimeSpan.FromSeconds(1));
	}

	private static AntigravityUsageR1ReviewedLayoutFile
		CreateReviewedLayoutFile()
	{
		AntigravityUsageR1ReviewedSpec spec = new(
			1,
			"synthetic-r1-layout-v1",
			ScreenColumns,
			ScreenRows,
			false,
			new[] { "SYNTHETIC AGY CHROME" },
			"SYNTHETIC R1 AGY MODEL QUOTAS",
			"Account: ",
			"Page: ",
			"Model ID | Used | Remaining | Reset",
			"END SYNTHETIC MODEL QUOTAS",
			" | ",
			new[] { "SYNTHETIC FOOTER CHROME" },
			new[]
			{
				new AntigravityUsageR1ModelRule(
					"gemini.synthetic.flash",
					"Gemini Synthetic Flash",
					true,
					0)
			},
			AntigravityUsageR1PaginationPolicy.RequireSinglePage);
		AntigravityUsageR1ReviewedSpec immutableSpec =
			AntigravityUsageR1SchemaParser.FreezeAndValidateSpec(spec);
		return new AntigravityUsageR1ReviewedLayoutFile(
			AntigravityUsageR1SchemaParser.LayoutFormatVersion,
			AntigravityUsageR1ReviewState.Reviewed,
			immutableSpec,
			AntigravityUsageR1SchemaParser.ComputeSchemaFingerprint(
				immutableSpec),
			new[] { new string('C', 64) });
	}
}
