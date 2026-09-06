using System.Security.Cryptography;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityExecutableFingerprintPromotionTests
{
	private sealed class FakeExecutableInspector : IAntigravityExecutableInspector
	{
		private readonly AntigravityExecutableInspection _inspection;
		private readonly Queue<AntigravityExecutableFileState> _states;

		internal int CaptureCount { get; private set; }

		internal int InspectionCount { get; private set; }

		internal FakeExecutableInspector(
			IEnumerable<AntigravityExecutableFileState> states,
			AntigravityExecutableInspection inspection)
		{
			_states = new Queue<AntigravityExecutableFileState>(states);
			_inspection = inspection;
		}

		public AntigravityExecutableFileState CaptureFileState(
			string absolutePath)
		{
			CaptureCount++;

			if (_states.Count == 0)
			{
				throw new InvalidOperationException(
					"The fake has no file state remaining.");
			}

			return _states.Dequeue();
		}

		public Task<AntigravityExecutableInspection> InspectAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			InspectionCount++;
			return Task.FromResult(_inspection);
		}
	}

	private sealed class FakeVersionProbe : IAntigravityCliVersionProbe
	{
		private readonly string _version;

		internal int CallCount { get; private set; }

		internal FakeVersionProbe(string version)
		{
			_version = version;
		}

		public Task<string> ProbeAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			return Task.FromResult(_version);
		}
	}

	private sealed record PromotionFixture(
		string ExecutablePath,
		string OutputPath,
		string SourcePath);

	private const string CliVersion = "1.1.3";
	private const string FileVersion = "1.1.3.0";
	private const string ProductVersion = "1.1.3";
	private const string SignerSubject = "CN=Synthetic AGY Signer";
	private static readonly string ApprovedSha256 = new('C', 64);
	private static readonly string ExistingSha256 = new('A', 64);
	private static readonly string SignerThumbprint = new('B', 40);
	private static readonly AntigravityExecutableFileState UnchangedFileState = new(
		2,
		new DateTime(2026, 7, 18, 1, 2, 3, DateTimeKind.Utc),
		new DateTime(2026, 7, 18, 1, 2, 4, DateTimeKind.Utc),
		FileAttributes.Archive,
		false);
	private static readonly AntigravityExecutableInspection TrustedInspection = new(
		FileVersion,
		ProductVersion,
		ApprovedSha256,
		0,
		SignerSubject,
		SignerThumbprint);

	[Fact]
	public async Task PromoteAsync_WithShaMismatch_ReportsSafeStageAndDoesNotProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		PromotionFixture fixture = await CreateFixtureAsync(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		string unapprovedSha256 = new('D', 64);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => AntigravityExecutableFingerprintPromotion.PromoteAsync(
				fixture.SourcePath,
				fixture.OutputPath,
				unapprovedSha256,
				inspector,
				versionProbe));

		Assert.Equal("ApprovedShaValidation", exception.Message);
		Assert.Equal(2, inspector.CaptureCount);
		Assert.Equal(1, inspector.InspectionCount);
		Assert.Equal(0, versionProbe.CallCount);
		Assert.False(File.Exists(fixture.OutputPath));
		Assert.DoesNotContain(
			fixture.SourcePath,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			fixture.OutputPath,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			fixture.ExecutablePath,
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			SignerSubject,
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task PromoteAsync_WithPreInspectionReparse_FailsBeforeInspectionOrProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		PromotionFixture fixture = await CreateFixtureAsync(temporaryDirectory);
		AntigravityExecutableFileState reparseState = UnchangedFileState with
		{
			Attributes = FileAttributes.Archive | FileAttributes.ReparsePoint,
			HasReparsePoint = true
		};
		FakeExecutableInspector inspector = new(
			new[] { reparseState },
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => AntigravityExecutableFingerprintPromotion.PromoteAsync(
				fixture.SourcePath,
				fixture.OutputPath,
				ApprovedSha256,
				inspector,
				versionProbe));

		Assert.Equal("PreInspectionFileStateValidation", exception.Message);
		Assert.Equal(1, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(0, versionProbe.CallCount);
		Assert.False(File.Exists(fixture.OutputPath));
	}

	[Fact]
	public async Task PromoteAsync_WithPostInspectionReparse_FailsBeforeProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		PromotionFixture fixture = await CreateFixtureAsync(temporaryDirectory);
		AntigravityExecutableFileState reparseState = UnchangedFileState with
		{
			Attributes = FileAttributes.Archive | FileAttributes.ReparsePoint,
			HasReparsePoint = true
		};
		FakeExecutableInspector inspector = new(
			new[] { UnchangedFileState, reparseState },
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => AntigravityExecutableFingerprintPromotion.PromoteAsync(
				fixture.SourcePath,
				fixture.OutputPath,
				ApprovedSha256,
				inspector,
				versionProbe));

		Assert.Equal("PostInspectionFileStateValidation", exception.Message);
		Assert.Equal(2, inspector.CaptureCount);
		Assert.Equal(1, inspector.InspectionCount);
		Assert.Equal(0, versionProbe.CallCount);
		Assert.False(File.Exists(fixture.OutputPath));
	}

	[Fact]
	public async Task PromoteAsync_WhenFileChangesAfterProbe_FailsClosedWithoutOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		PromotionFixture fixture = await CreateFixtureAsync(temporaryDirectory);
		AntigravityExecutableFileState changedState = UnchangedFileState with
		{
			Length = UnchangedFileState.Length + 1
		};
		FakeExecutableInspector inspector = new(
			new[]
			{
				UnchangedFileState,
				UnchangedFileState,
				changedState
			},
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
			() => AntigravityExecutableFingerprintPromotion.PromoteAsync(
				fixture.SourcePath,
				fixture.OutputPath,
				ApprovedSha256,
				inspector,
				versionProbe));

		Assert.Equal("PostProbeFileStateValidation", exception.Message);
		Assert.Equal(3, inspector.CaptureCount);
		Assert.Equal(1, inspector.InspectionCount);
		Assert.Equal(1, versionProbe.CallCount);
		Assert.False(File.Exists(fixture.OutputPath));
	}

	[Fact]
	public async Task PromoteAsync_WithExactExistingOutput_IsIdempotentAndPreservesSource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		PromotionFixture fixture = await CreateFixtureAsync(temporaryDirectory);
		byte[] sourceBefore = await File.ReadAllBytesAsync(fixture.SourcePath);
		byte[]? firstOutput = null;
		byte[]? secondOutput = null;

		try
		{
			FakeExecutableInspector firstInspector = new(
				RepeatState(UnchangedFileState, 3),
				TrustedInspection);
			FakeVersionProbe firstProbe = new(CliVersion);
			AntigravityExecutableFingerprintPromotionMetadata first =
				await AntigravityExecutableFingerprintPromotion.PromoteAsync(
					fixture.SourcePath,
					fixture.OutputPath,
					ApprovedSha256,
					firstInspector,
					firstProbe);

			Assert.True(first.WasWritten);
			Assert.Equal(ApprovedSha256, first.ApprovedSha256);
			Assert.Equal(1, firstProbe.CallCount);
			Assert.True(File.Exists(fixture.SourcePath));
			Assert.True(File.Exists(fixture.OutputPath));
			Assert.True(
				AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					fixture.OutputPath));
			Assert.Equal(
				sourceBefore,
				await File.ReadAllBytesAsync(fixture.SourcePath));
			firstOutput = await File.ReadAllBytesAsync(fixture.OutputPath);

			AntigravityLiveR0ProfileFile sourceProfile =
				AntigravityLiveR0ProfileJson.Deserialize(sourceBefore);
			AntigravityLiveR0ProfileFile promotedProfile =
				AntigravityLiveR0ProfileJson.Deserialize(firstOutput);
			Assert.Equal(
				ExistingSha256,
				sourceProfile.ExecutableFingerprint.Sha256);
			Assert.Equal(
				ApprovedSha256,
				promotedProfile.ExecutableFingerprint.Sha256);

			FakeExecutableInspector secondInspector = new(
				RepeatState(UnchangedFileState, 3),
				TrustedInspection);
			FakeVersionProbe secondProbe = new(CliVersion);
			AntigravityExecutableFingerprintPromotionMetadata second =
				await AntigravityExecutableFingerprintPromotion.PromoteAsync(
					fixture.SourcePath,
					fixture.OutputPath,
					ApprovedSha256,
					secondInspector,
					secondProbe);

			Assert.False(second.WasWritten);
			Assert.Equal(ApprovedSha256, second.ApprovedSha256);
			Assert.Equal(1, secondProbe.CallCount);
			secondOutput = await File.ReadAllBytesAsync(fixture.OutputPath);
			Assert.Equal(firstOutput, secondOutput);
			Assert.Equal(
				sourceBefore,
				await File.ReadAllBytesAsync(fixture.SourcePath));

			string safeMetadata = JsonSerializer.Serialize(second);
			using JsonDocument metadataDocument = JsonDocument.Parse(safeMetadata);
			Assert.Equal(
				new[] { "WasWritten", "ApprovedSha256" },
				metadataDocument.RootElement
					.EnumerateObject()
					.Select(property => property.Name));
			Assert.DoesNotContain(
				fixture.SourcePath,
				safeMetadata,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				fixture.OutputPath,
				safeMetadata,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				fixture.ExecutablePath,
				safeMetadata,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				SignerSubject,
				safeMetadata,
				StringComparison.Ordinal);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceBefore);

			if (firstOutput is not null)
			{
				CryptographicOperations.ZeroMemory(firstOutput);
			}

			if (secondOutput is not null)
			{
				CryptographicOperations.ZeroMemory(secondOutput);
			}
		}
	}

	private static async Task<PromotionFixture> CreateFixtureAsync(
		TemporaryDirectory temporaryDirectory)
	{
		string privateDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-profile"));
		Assert.True(AntigravityPrivateKeyAcl.TryPrepareDirectory(
			privateDirectory,
			out _));
		string keyPath = Path.Combine(privateDirectory, "screen.key");
		byte[] key = Enumerable.Range(1, 32)
			.Select(value => (byte)value)
			.ToArray();

		try
		{
			await File.WriteAllBytesAsync(keyPath, key);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
		}

		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(keyPath));
		string executablePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"agy.exe"));
		await File.WriteAllBytesAsync(
			executablePath,
			new byte[] { 0x4D, 0x5A });
		AntigravityCliFingerprint fingerprint = new(
			executablePath,
			CliVersion,
			FileVersion,
			ProductVersion,
			ExistingSha256,
			0,
			SignerSubject,
			SignerThumbprint);
		AntigravityLiveR0ProfileFile profile = new(
			"synthetic-executable-promotion",
			fingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			120,
			50,
			new string('D', 64),
			keyPath,
			new string('E', 64),
			null,
			Array.Empty<string>(),
			64 * 1024,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(10),
			TimeSpan.FromMilliseconds(10),
			TimeSpan.FromSeconds(1));
		string sourcePath = Path.Combine(privateDirectory, "source.json");
		string outputPath = Path.Combine(privateDirectory, "promoted.json");
		byte[] profileBytes = AntigravityLiveR0ProfileJson.Serialize(profile);

		try
		{
			await File.WriteAllBytesAsync(sourcePath, profileBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(profileBytes);
		}

		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(sourcePath));
		Assert.True(
			AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(sourcePath));
		return new PromotionFixture(executablePath, outputPath, sourcePath);
	}

	private static IEnumerable<AntigravityExecutableFileState> RepeatState(
		AntigravityExecutableFileState state,
		int count)
	{
		return Enumerable.Repeat(state, count);
	}
}
