using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUsageR1PrivateBundleWriterTests
{
	private sealed class RacingFileCommitter :
		IAntigravityUsageR1PrivateBundleFileCommitter
	{
		private readonly byte[] _racedBytes;

		internal RacingFileCommitter(byte[] racedBytes)
		{
			_racedBytes = racedBytes;
		}

		public void Move(string sourceFileName, string destinationFileName)
		{
			File.Move(sourceFileName, destinationFileName);
		}

		public void Replace(
			string sourceFileName,
			string destinationFileName,
			string destinationBackupFileName)
		{
			File.WriteAllBytes(destinationFileName, _racedBytes);
			File.Replace(
				sourceFileName,
				destinationFileName,
				destinationBackupFileName,
				ignoreMetadataErrors: false);
		}
	}

	private static readonly string CaptureContractFingerprint = new('A', 64);
	private static readonly string DifferentCaptureContractFingerprint =
		new('B', 64);
	private const string PrivateIdentityMarker =
		"PRIVATE-R1-BUNDLE-ACCOUNT@example.invalid";

	[Fact]
	public async Task PreflightAsync_WhenBundleDoesNotExist_ReturnsSafeEmptyMetadata()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");

		AntigravityUsageR1PrivateBundleMetadata result =
			await AntigravityUsageR1PrivateBundleWriter.PreflightAsync(bundlePath);

		Assert.False(result.Exists);
		Assert.False(result.WasUpdated);
		Assert.Equal(0, result.SequenceCount);
		Assert.Null(result.CaptureContractFingerprint);
		Assert.Null(result.Columns);
		Assert.Null(result.Rows);
		Assert.Null(result.IsAlternateScreen);
		Assert.False(File.Exists(bundlePath));
	}

	[Fact]
	public async Task AppendAsync_WhenBundleDoesNotExist_CreatesStrictPrivateV2Bundle()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		TerminalScreenSnapshot snapshot = CreateSnapshot(PrivateIdentityMarker);

		AntigravityUsageR1PrivateBundleMetadata appendResult =
			await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
				bundlePath,
				snapshot,
				CaptureContractFingerprint);
		AntigravityUsageR1PrivateBundleMetadata preflightResult =
			await AntigravityUsageR1PrivateBundleWriter.PreflightAsync(bundlePath);
		byte[] bytes = await File.ReadAllBytesAsync(bundlePath);
		AntigravityUsageR1PrivateScreenBundleFile bundle =
			AntigravityUsageR1CalibrationJson.DeserializePrivateBundle(bytes);

		Assert.True(appendResult.Exists);
		Assert.True(appendResult.WasUpdated);
		Assert.Equal(1, appendResult.SequenceCount);
		Assert.Equal(CaptureContractFingerprint,
			appendResult.CaptureContractFingerprint);
		Assert.Equal(80, appendResult.Columns);
		Assert.Equal(4, appendResult.Rows);
		Assert.False(appendResult.IsAlternateScreen);
		Assert.True(preflightResult.Exists);
		Assert.False(preflightResult.WasUpdated);
		Assert.Equal(1, preflightResult.SequenceCount);
		Assert.Equal(
			AntigravityUsageR1CalibrationCompiler.PrivateBundleFormatVersion,
			bundle.FormatVersion);
		Assert.Equal(CaptureContractFingerprint,
			bundle.CaptureContractFingerprint);
		Assert.Single(bundle.Sequences);
		Assert.Single(bundle.Sequences[0].Pages);
		Assert.Equal(snapshot.Lines, bundle.Sequences[0].Pages[0].Lines);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(bundlePath));
		Assert.Contains(
			PrivateIdentityMarker,
			Encoding.UTF8.GetString(bytes),
			StringComparison.Ordinal);
		AssertNoWriterDebris(privateDirectory, bundlePath);
	}

	[Fact]
	public async Task AppendAsync_WithExactStoredSnapshot_IsIdempotent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		TerminalScreenSnapshot snapshot = CreateSnapshot(PrivateIdentityMarker);
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			snapshot,
			CaptureContractFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(bundlePath);
		DateTime expectedLastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
		File.SetLastWriteTimeUtc(bundlePath, expectedLastWriteTimeUtc);
		expectedLastWriteTimeUtc = File.GetLastWriteTimeUtc(bundlePath);

		AntigravityUsageR1PrivateBundleMetadata result =
			await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
				bundlePath,
				snapshot,
				CaptureContractFingerprint);

		Assert.True(result.Exists);
		Assert.False(result.WasUpdated);
		Assert.Equal(1, result.SequenceCount);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(bundlePath));
		Assert.Equal(expectedLastWriteTimeUtc,
			File.GetLastWriteTimeUtc(bundlePath));
		AssertNoWriterDebris(privateDirectory, bundlePath);
	}

	[Fact]
	public async Task AppendAsync_WithDistinctSnapshot_AppendsOneSequence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			CreateSnapshot("first-private-account"),
			CaptureContractFingerprint);

		AntigravityUsageR1PrivateBundleMetadata result =
			await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
				bundlePath,
				CreateSnapshot("second-private-account"),
				CaptureContractFingerprint);
		AntigravityUsageR1PrivateScreenBundleFile bundle =
			AntigravityUsageR1CalibrationJson.DeserializePrivateBundle(
				await File.ReadAllBytesAsync(bundlePath));

		Assert.True(result.WasUpdated);
		Assert.Equal(2, result.SequenceCount);
		Assert.Equal("Account: first-private-account",
			bundle.Sequences[0].Pages[0].Lines[1]);
		Assert.Equal("Account: second-private-account",
			bundle.Sequences[1].Pages[0].Lines[1]);
		AssertNoWriterDebris(privateDirectory, bundlePath);
	}

	[Fact]
	public async Task AppendAsync_WithDifferentContract_RejectsWithoutChangingBundle()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			CreateSnapshot(PrivateIdentityMarker),
			CaptureContractFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(bundlePath);

		AntigravityUsageR1PrivateBundleException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
					bundlePath,
					CreateSnapshot("different-private-account"),
					DifferentCaptureContractFingerprint));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1PrivateBundleFailureStage
				.ExistingBundleValidation);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(bundlePath));
		AssertNoWriterDebris(privateDirectory, bundlePath);
	}

	[Fact]
	public async Task AppendAsync_WithDifferentViewport_RejectsWithoutChangingBundle()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			CreateSnapshot(PrivateIdentityMarker),
			CaptureContractFingerprint);
		byte[] originalBytes = await File.ReadAllBytesAsync(bundlePath);
		TerminalScreenSnapshot differentViewport = new(
			81,
			4,
			false,
			new[] { "Title", "Account: other", "Quota: 10%", string.Empty });

		AntigravityUsageR1PrivateBundleException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
					bundlePath,
					differentViewport,
					CaptureContractFingerprint));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1PrivateBundleFailureStage
				.ExistingBundleValidation);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(bundlePath));
	}

	[Theory]
	[InlineData("relative.json")]
	[InlineData("C:\\invalid-extension.txt")]
	public async Task PreflightAsync_WithInvalidOutputPath_RejectsAtFixedStage(
		string outputPath)
	{
		AntigravityUsageR1PrivateBundleException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter
					.PreflightAsync(outputPath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1PrivateBundleFailureStage.PathValidation);
	}

	[Fact]
	public async Task PreflightAsync_WithStaleLockOrRecoveryDebris_RejectsBeforeLive()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		string lockPath = Path.Combine(
			privateDirectory,
			".bundle.json.r1-bundle.lock");
		await File.WriteAllTextAsync(lockPath, string.Empty);

		AntigravityUsageR1PrivateBundleException lockException =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter
					.PreflightAsync(bundlePath));
		AssertFixedFailure(
			lockException,
			AntigravityUsageR1PrivateBundleFailureStage
				.RecoveryStateValidation);

		File.Delete(lockPath);
		string recoveryPath = Path.Combine(
			privateDirectory,
			".bundle.json.r1-bundle.recovery.synthetic.tmp");
		await File.WriteAllTextAsync(recoveryPath, string.Empty);

		AntigravityUsageR1PrivateBundleException recoveryException =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter
					.PreflightAsync(bundlePath));
		AssertFixedFailure(
			recoveryException,
			AntigravityUsageR1PrivateBundleFailureStage
				.RecoveryStateValidation);
	}

	[Fact]
	public async Task PreflightAsync_WithNonStrictExistingBundle_RejectsWithoutRewriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			CreateSnapshot(PrivateIdentityMarker),
			CaptureContractFingerprint);
		string validJson = await File.ReadAllTextAsync(bundlePath);
		string invalidJson = validJson.Replace(
			"\"Sequences\":",
			"\"Unexpected\": true,\r\n  \"Sequences\":",
			StringComparison.Ordinal);
		await File.WriteAllTextAsync(bundlePath, invalidJson);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(bundlePath));
		byte[] invalidBytes = await File.ReadAllBytesAsync(bundlePath);

		AntigravityUsageR1PrivateBundleException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter
					.PreflightAsync(bundlePath));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1PrivateBundleFailureStage
				.ExistingBundleValidation);
		Assert.Equal(invalidBytes, await File.ReadAllBytesAsync(bundlePath));
	}

	[Fact]
	public async Task AppendAsync_AtCapacity_AllowsDuplicateButRejectsDistinct()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");

		for (int index = 0; index < 16; index++)
		{
			_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
				bundlePath,
				CreateSnapshot($"private-account-{index}"),
				CaptureContractFingerprint);
		}

		AntigravityUsageR1PrivateBundleMetadata duplicateResult =
			await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
				bundlePath,
				CreateSnapshot("private-account-0"),
				CaptureContractFingerprint);
		AntigravityUsageR1PrivateBundleException appendException =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
					bundlePath,
					CreateSnapshot("private-account-16"),
					CaptureContractFingerprint));
		AntigravityUsageR1PrivateBundleException preflightException =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter
					.PreflightAsync(bundlePath));

		Assert.False(duplicateResult.WasUpdated);
		Assert.Equal(16, duplicateResult.SequenceCount);
		AssertFixedFailure(
			appendException,
			AntigravityUsageR1PrivateBundleFailureStage.CapacityValidation);
		AssertFixedFailure(
			preflightException,
			AntigravityUsageR1PrivateBundleFailureStage.CapacityValidation);
		AssertNoWriterDebris(privateDirectory, bundlePath);
	}

	[Fact]
	public async Task AppendAsync_WhenTargetRacesBeforeReplace_RestoresRacedStateAndFailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = CreatePrivateDirectory(temporaryDirectory);
		string bundlePath = Path.Combine(privateDirectory, "bundle.json");
		string racedBundlePath = Path.Combine(
			privateDirectory,
			"raced-bundle.json");
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			bundlePath,
			CreateSnapshot("original-private-account"),
			CaptureContractFingerprint);
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			racedBundlePath,
			CreateSnapshot("original-private-account"),
			CaptureContractFingerprint);
		_ = await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
			racedBundlePath,
			CreateSnapshot("raced-private-account"),
			CaptureContractFingerprint);
		byte[] racedBytes = await File.ReadAllBytesAsync(racedBundlePath);
		RacingFileCommitter committer = new(racedBytes);

		AntigravityUsageR1PrivateBundleException exception =
			await Assert.ThrowsAsync<AntigravityUsageR1PrivateBundleException>(
				async () => await AntigravityUsageR1PrivateBundleWriter.AppendAsync(
					bundlePath,
					CreateSnapshot("our-private-account"),
					CaptureContractFingerprint,
					committer));

		AssertFixedFailure(
			exception,
			AntigravityUsageR1PrivateBundleFailureStage.Commit);
		Assert.Equal(racedBytes, await File.ReadAllBytesAsync(bundlePath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(bundlePath));
		AssertNoWriterDebris(privateDirectory, bundlePath, racedBundlePath);
	}

	private static void AssertFixedFailure(
		AntigravityUsageR1PrivateBundleException exception,
		AntigravityUsageR1PrivateBundleFailureStage expectedStage)
	{
		Assert.Equal(expectedStage, exception.Stage);
		Assert.Equal(
			$"The private AGY R1 screen bundle operation failed at stage: {expectedStage}.",
			exception.Message);
		Assert.DoesNotContain(
			PrivateIdentityMarker,
			exception.ToString(),
			StringComparison.Ordinal);
	}

	private static void AssertNoWriterDebris(
		string privateDirectory,
		params string[] expectedPaths)
	{
		HashSet<string> expected = expectedPaths
			.Select(Path.GetFullPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Equal(
			expected,
			Directory.EnumerateFiles(privateDirectory)
				.Select(Path.GetFullPath)
				.ToHashSet(StringComparer.OrdinalIgnoreCase));
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

	private static TerminalScreenSnapshot CreateSnapshot(string identity)
	{
		return new TerminalScreenSnapshot(
			80,
			4,
			false,
			new[]
			{
				"Synthetic AGY quota",
				$"Account: {identity}",
				"Quota: 10% | Remaining: 90%",
				string.Empty
			});
	}
}
