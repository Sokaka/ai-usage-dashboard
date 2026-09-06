using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokCliExecutableStagerTests
{
	private const string ExactSignerSubject =
		"CN=X.AI LLC, O=X.AI LLC, L=Palo Alto, S=California, C=US";

	[Fact]
	public void Stage_WithTrustedSource_CreatesContentAddressedCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		List<string> inspectedPaths = new();
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			path =>
			{
				inspectedPaths.Add(path);
				return CreateTrustedSignature();
			});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;

		Assert.True(lease.IsProtected);
		string expectedHash = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
		Assert.Equal(
			Path.Combine(trustedRoot, $"grok-{expectedHash}.exe"),
			stagedPath,
			ignoreCase: true);
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(stagedPath));
		Assert.Collection(
			inspectedPaths,
			path => Assert.Equal(Path.GetFullPath(sourcePath), path),
			path =>
			{
				Assert.Equal(
					Path.GetFullPath(trustedRoot),
					Path.GetDirectoryName(path));
				Assert.StartsWith(
					".grok-stage-",
					Path.GetFileName(path),
					StringComparison.Ordinal);
			},
			path => Assert.Equal(stagedPath, path));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(trustedRoot),
			path => Path.GetFileName(path).StartsWith(
				".grok-stage-",
				StringComparison.Ordinal));
	}

	[Fact]
	public void Stage_WhenOfficialSourceChanges_RetainsCurrentAndPreviousCopies()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		string firstStagedPath;

		using (WindowsOfficialCliExecutableLease firstLease =
			stager.Stage(sourcePath))
		{
			firstStagedPath = firstLease.ExecutablePath;
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x02, 0x03]);
		using WindowsOfficialCliExecutableLease secondLease =
			stager.Stage(sourcePath);
		string secondStagedPath = secondLease.ExecutablePath;

		Assert.NotEqual(firstStagedPath, secondStagedPath);
		Assert.True(File.Exists(firstStagedPath));
		Assert.Equal(
			File.ReadAllBytes(sourcePath),
			File.ReadAllBytes(secondStagedPath));
	}

	[Fact]
	public void Stage_WhenSourceIsUnchanged_ReusesGenerationWithoutSignatureInspection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		int signatureInspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ =>
			{
				Interlocked.Increment(ref signatureInspectionCount);
				return CreateTrustedSignature();
			});
		WindowsOfficialCliExecutableLease firstLease = stager.Stage(sourcePath);

		try
		{
			int firstStageInspectionCount =
				Volatile.Read(ref signatureInspectionCount);
			Assert.True(firstStageInspectionCount > 0);
			using WindowsOfficialCliExecutableLease secondLease =
				stager.Stage(sourcePath);

			Assert.Equal(
				firstStageInspectionCount,
				Volatile.Read(ref signatureInspectionCount));
			Assert.Equal(
				firstLease.ExecutablePath,
				secondLease.ExecutablePath,
				ignoreCase: true);
			Assert.NotSame(firstLease, secondLease);

			firstLease.Dispose();
			Assert.False(firstLease.IsProtected);
			Assert.True(secondLease.IsProtected);
			using WindowsOfficialCliExecutableLease duplicateLease =
				secondLease.Duplicate();
			Assert.True(duplicateLease.IsProtected);
		}
		finally
		{
			firstLease.Dispose();
		}
	}

	[Fact]
	public void Stage_WhenSourceContentChangesWithSameLengthAndLastWriteTime_CreatesNewGeneration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] originalContent = [0x4D, 0x5A, 0x01, 0x02];
		byte[] changedContent = [0x4D, 0x5A, 0x03, 0x04];
		string sourcePath = CreateSource(temporaryDirectory, originalContent);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());
		string firstStagedPath;

		using (WindowsOfficialCliExecutableLease firstLease =
			stager.Stage(sourcePath))
		{
			firstStagedPath = firstLease.ExecutablePath;
		}

		DateTime originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);

		File.WriteAllBytes(sourcePath, changedContent);
		File.SetLastWriteTimeUtc(sourcePath, originalLastWriteTimeUtc);

		Assert.Equal(originalContent.Length, new FileInfo(sourcePath).Length);
		Assert.Equal(originalLastWriteTimeUtc, File.GetLastWriteTimeUtc(sourcePath));
		using WindowsOfficialCliExecutableLease secondLease =
			stager.Stage(sourcePath);
		string secondStagedPath = secondLease.ExecutablePath;

		Assert.NotEqual(firstStagedPath, secondStagedPath);
		Assert.True(File.Exists(firstStagedPath));
		Assert.Equal(changedContent, File.ReadAllBytes(secondStagedPath));
	}

	[Fact]
	public void Stage_AfterThirdGeneration_RemovesFirstAndRetainsLatestTwo()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		string firstStagedPath;

		using (WindowsOfficialCliExecutableLease firstLease =
			stager.Stage(sourcePath))
		{
			firstStagedPath = firstLease.ExecutablePath;
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x02, 0x03]);
		string secondStagedPath;

		using (WindowsOfficialCliExecutableLease secondLease =
			stager.Stage(sourcePath))
		{
			secondStagedPath = secondLease.ExecutablePath;
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x03, 0x04, 0x05]);
		using WindowsOfficialCliExecutableLease thirdLease =
			stager.Stage(sourcePath);
		string thirdStagedPath = thirdLease.ExecutablePath;

		Assert.False(File.Exists(firstStagedPath));
		Assert.True(File.Exists(secondStagedPath));
		Assert.True(File.Exists(thirdStagedPath));
		Assert.Equal(
			2,
			Directory.EnumerateFiles(trustedRoot, "grok-*.exe").Count());
	}

	[Fact]
	public void Stage_WhenPruningGenerations_PreservesNonExactProviderFileNames()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());
		string firstStagedPath;

		using (WindowsOfficialCliExecutableLease firstLease =
			stager.Stage(sourcePath))
		{
			firstStagedPath = firstLease.ExecutablePath;
		}

		string[] unrelatedPaths =
		[
			Path.Combine(trustedRoot, $"grok-{new string('a', 63)}.exe"),
			Path.Combine(trustedRoot, $"grok-{new string('b', 64)}.dll"),
			Path.Combine(trustedRoot, $"other-{new string('c', 64)}.exe"),
			Path.Combine(trustedRoot, $"grok-{new string('d', 64)}.exe.bak"),
			Path.Combine(trustedRoot, $"grok-{new string('z', 64)}.exe")
		];

		foreach (string unrelatedPath in unrelatedPaths)
		{
			File.WriteAllBytes(unrelatedPath, [0x01]);
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x02, 0x03]);
		string secondStagedPath;

		using (WindowsOfficialCliExecutableLease secondLease =
			stager.Stage(sourcePath))
		{
			secondStagedPath = secondLease.ExecutablePath;
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x03, 0x04, 0x05]);
		using WindowsOfficialCliExecutableLease thirdLease =
			stager.Stage(sourcePath);
		string thirdStagedPath = thirdLease.ExecutablePath;

		Assert.False(File.Exists(firstStagedPath));
		Assert.True(File.Exists(secondStagedPath));
		Assert.True(File.Exists(thirdStagedPath));
		Assert.All(unrelatedPaths, path => Assert.True(File.Exists(path)));
	}

	[Fact]
	public void Stage_WhenOldGenerationIsLocked_RetriesPruningOnNextStage()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		WindowsOfficialCliExecutableLease firstLease =
			stager.Stage(sourcePath);
		string firstStagedPath = firstLease.ExecutablePath;

		try
		{
			File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x02, 0x03]);
			string secondStagedPath;

			using (WindowsOfficialCliExecutableLease secondLease =
				stager.Stage(sourcePath))
			{
				secondStagedPath = secondLease.ExecutablePath;
			}

			File.WriteAllBytes(
				sourcePath,
				[0x4D, 0x5A, 0x03, 0x04, 0x05]);
			string thirdStagedPath;

			using (WindowsOfficialCliExecutableLease thirdLease =
				stager.Stage(sourcePath))
			{
				thirdStagedPath = thirdLease.ExecutablePath;
			}

			Assert.True(firstLease.IsProtected);
			Assert.True(File.Exists(firstStagedPath));
			Assert.True(File.Exists(secondStagedPath));
			Assert.True(File.Exists(thirdStagedPath));

			firstLease.Dispose();
			using WindowsOfficialCliExecutableLease retryLease =
				stager.Stage(sourcePath);
			Assert.Equal(thirdStagedPath, retryLease.ExecutablePath);
			Assert.False(File.Exists(firstStagedPath));
			Assert.True(File.Exists(secondStagedPath));
			Assert.True(File.Exists(thirdStagedPath));
		}
		finally
		{
			firstLease.Dispose();
		}
	}

	[Fact]
	public void Stage_WhileConsumerLeaseIsAlive_RejectsStagedCopyWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());
		WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;

		try
		{
			Assert.True(lease.IsProtected);
			stager.Dispose();
			Assert.Throws<IOException>(() =>
				File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF]));
		}
		finally
		{
			lease.Dispose();
			stager.Dispose();
		}

		Assert.False(lease.IsProtected);
		File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF]);
	}

	[Fact]
	public void Stage_AfterConsumerLeaseIsDisposed_BaseStillRejectsStagedCopyWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceContent = [0x4D, 0x5A, 0x01, 0x02];
		string sourcePath = CreateSource(temporaryDirectory, sourceContent);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());
		WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;
		lease.Dispose();

		try
		{
			Assert.Throws<IOException>(() =>
				File.WriteAllBytes(
					stagedPath,
					[0x4D, 0x5A, 0xFF, 0xFE]));
		}
		finally
		{
			stager.Dispose();
		}

		File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF, 0xFE]);
	}

	[Fact]
	public void Duplicate_AfterOriginalAndBaseAreDisposed_StillRejectsWrite()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());
		WindowsOfficialCliExecutableLease originalLease =
			stager.Stage(sourcePath);
		WindowsOfficialCliExecutableLease duplicateLease =
			originalLease.Duplicate();
		string stagedPath = duplicateLease.ExecutablePath;

		try
		{
			originalLease.Dispose();
			stager.Dispose();

			Assert.False(originalLease.IsProtected);
			Assert.True(duplicateLease.IsProtected);
			Assert.Throws<IOException>(() =>
				File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF]));
		}
		finally
		{
			duplicateLease.Dispose();
			originalLease.Dispose();
			stager.Dispose();
		}

		File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF]);
	}

	[Fact]
	public void Duplicate_FromUnprotectedLease_RemainsUnprotected()
	{
		string executablePath = @"C:\test\grok.exe";
		using WindowsOfficialCliExecutableLease originalLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		using WindowsOfficialCliExecutableLease duplicateLease =
			originalLease.Duplicate();

		Assert.Equal(executablePath, duplicateLease.ExecutablePath);
		Assert.False(originalLease.IsProtected);
		Assert.False(duplicateLease.IsProtected);
	}

	[Fact]
	public void Stage_WithStaleExactTemporaryFile_CleansCrashLeftFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		Directory.CreateDirectory(trustedRoot);
		string staleTemporaryPath = Path.Combine(
			trustedRoot,
			$".grok-stage-{Guid.NewGuid():N}.exe");
		File.WriteAllBytes(staleTemporaryPath, [0x01]);
		File.SetLastWriteTimeUtc(
			staleTemporaryPath,
			DateTime.UtcNow.AddHours(-2));
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.False(File.Exists(staleTemporaryPath));
		Assert.True(File.Exists(lease.ExecutablePath));
	}

	[Fact]
	public void Stage_WithRecentExactAndStaleLookalikeTemporaryFiles_PreservesThem()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		Directory.CreateDirectory(trustedRoot);
		string recentTemporaryPath = Path.Combine(
			trustedRoot,
			$".grok-stage-{Guid.NewGuid():N}.exe");
		string lookalikePath = Path.Combine(
			trustedRoot,
			$".grok-stage-{new string('z', 32)}.exe");
		string nestedDirectory = Path.Combine(trustedRoot, "nested");
		string nestedTemporaryPath = Path.Combine(
			nestedDirectory,
			$".grok-stage-{Guid.NewGuid():N}.exe");
		Directory.CreateDirectory(nestedDirectory);
		File.WriteAllBytes(recentTemporaryPath, [0x01]);
		File.WriteAllBytes(lookalikePath, [0x02]);
		File.WriteAllBytes(nestedTemporaryPath, [0x03]);
		File.SetLastWriteTimeUtc(
			lookalikePath,
			DateTime.UtcNow.AddHours(-2));
		File.SetLastWriteTimeUtc(
			nestedTemporaryPath,
			DateTime.UtcNow.AddHours(-2));
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.True(File.Exists(lease.ExecutablePath));
		Assert.True(File.Exists(recentTemporaryPath));
		Assert.True(File.Exists(lookalikePath));
		Assert.True(File.Exists(nestedTemporaryPath));
	}

	[Fact]
	public void Stage_DuringSourceSignatureInspection_LocksSourceAgainstWriteAndDelete()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		bool sourceWasLocked = false;
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			path =>
			{
				if (string.Equals(
					path,
					Path.GetFullPath(sourcePath),
					StringComparison.OrdinalIgnoreCase))
				{
					Assert.Throws<IOException>(() =>
					{
						using FileStream _ = new(
							sourcePath,
							FileMode.Open,
							FileAccess.Write,
							FileShare.ReadWrite | FileShare.Delete);
					});
					Assert.Throws<IOException>(() => File.Delete(sourcePath));
					sourceWasLocked = true;
				}

				return CreateTrustedSignature();
			});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;

		Assert.True(sourceWasLocked);
		Assert.True(File.Exists(sourcePath));
		Assert.True(File.Exists(stagedPath));
	}

	[Fact]
	public async Task Stage_WhenConcurrentGateWaitIsCanceled_ThrowsPromptly()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using ManualResetEventSlim firstInspectionEntered = new(false);
		using ManualResetEventSlim releaseFirstInspection = new(false);
		using ManualResetEventSlim secondStageStarted = new(false);
		using CancellationTokenSource cancellation = new();
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ =>
			{
				if (Interlocked.Increment(ref inspectionCount) == 1)
				{
					firstInspectionEntered.Set();
					releaseFirstInspection.Wait();
				}

				return CreateTrustedSignature();
			});
		Task<WindowsOfficialCliExecutableLease> firstStage =
			Task.Run(() => stager.Stage(sourcePath));
		Task? secondStage = null;

		try
		{
			Assert.True(firstInspectionEntered.Wait(TimeSpan.FromSeconds(5)));
			secondStage = Task.Run(() =>
			{
				secondStageStarted.Set();
				using WindowsOfficialCliExecutableLease lease =
					stager.Stage(sourcePath, cancellation.Token);
				return lease.ExecutablePath;
			});
			Assert.True(secondStageStarted.Wait(TimeSpan.FromSeconds(5)));
			await Task.Delay(TimeSpan.FromMilliseconds(100));
			Assert.False(secondStage.IsCompleted);

			Stopwatch stopwatch = Stopwatch.StartNew();
			cancellation.Cancel();
			Task completedStage = await Task.WhenAny(
				secondStage,
				Task.Delay(TimeSpan.FromSeconds(2)));
			stopwatch.Stop();

			Assert.Same(secondStage, completedStage);
			OperationCanceledException exception = await Assert.ThrowsAnyAsync<
				OperationCanceledException>(() => secondStage);
			Assert.Equal(cancellation.Token, exception.CancellationToken);
			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
			Assert.False(firstStage.IsCompleted);
		}
		finally
		{
			cancellation.Cancel();
			releaseFirstInspection.Set();
		}

		using WindowsOfficialCliExecutableLease firstLease =
			await firstStage.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void Stage_WithUntrustedSourceSigner_RejectsWithoutCreatingTemporaryFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => new WindowsAuthenticodeInspection(
				WinVerifyTrustStatus: 0,
				SignerSubject: "CN=Unknown",
				SignerThumbprint: new string('B', 40)));

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Empty(Directory.EnumerateFiles(trustedRoot));
	}

	[Fact]
	public void Stage_WithUntrustedTemporarySigner_RejectsAndCleansTemporaryFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		List<string> inspectedPaths = new();
		string? temporaryPath = null;
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			path =>
			{
				inspectedPaths.Add(path);

				if (inspectedPaths.Count == 2)
				{
					temporaryPath = path;
					Assert.True(File.Exists(temporaryPath));
					return CreateUntrustedSignature();
				}

				return CreateTrustedSignature();
			});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(2, inspectedPaths.Count);
		Assert.NotNull(temporaryPath);
		Assert.StartsWith(
			".grok-stage-",
			Path.GetFileName(temporaryPath),
			StringComparison.Ordinal);
		Assert.False(File.Exists(temporaryPath));
		Assert.Empty(Directory.EnumerateFiles(trustedRoot));
	}

	[Fact]
	public void Stage_WithUntrustedFinalSigner_RejectsFinalCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		List<string> inspectedPaths = new();
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			path =>
			{
				inspectedPaths.Add(path);
				return inspectedPaths.Count == 3
					? CreateUntrustedSignature()
					: CreateTrustedSignature();
			});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(3, inspectedPaths.Count);
		string finalPath = inspectedPaths[2];
		Assert.StartsWith(
			"grok-",
			Path.GetFileName(finalPath),
			StringComparison.Ordinal);
		Assert.True(File.Exists(finalPath));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(trustedRoot),
			path => Path.GetFileName(path).StartsWith(
				".grok-stage-",
				StringComparison.Ordinal));
	}

	[Fact]
	public void Stage_WithUntrustedPreexistingContentAddressedSigner_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		Directory.CreateDirectory(trustedRoot);
		string expectedHash = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
		string stagedPath = Path.Combine(
			trustedRoot,
			$"grok-{expectedHash}.exe");
		File.Copy(sourcePath, stagedPath);
		List<string> inspectedPaths = new();
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			path =>
			{
				inspectedPaths.Add(path);
				return string.Equals(
					path,
					stagedPath,
					StringComparison.OrdinalIgnoreCase)
					? CreateUntrustedSignature()
					: CreateTrustedSignature();
			});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(stagedPath, inspectedPaths[^1], ignoreCase: true);
		Assert.Equal(3, inspectedPaths.Count);
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(stagedPath));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(trustedRoot),
			path => Path.GetFileName(path).StartsWith(
				".grok-stage-",
				StringComparison.Ordinal));
	}

	[Fact]
	public void Stage_WithPreexistingCorruptContentAddressedFile_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		Directory.CreateDirectory(trustedRoot);
		string expectedHash = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
		string stagedPath = Path.Combine(
			trustedRoot,
			$"grok-{expectedHash}.exe");
		File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0xFF]);
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature());

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(
			new byte[] { 0x4D, 0x5A, 0xFF },
			File.ReadAllBytes(stagedPath));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(trustedRoot),
			path => Path.GetFileName(path).StartsWith(
				".grok-stage-",
				StringComparison.Ordinal));
	}

	[Fact]
	public void Stage_WhenProtectedCopyAclValidationFails_RejectsCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateTrustedSignature(),
			isPathAclSafe: false);

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Empty(Directory.EnumerateFiles(trustedRoot));
	}

	[Fact]
	public void Stage_WhenSourcePathBecomesNonCanonical_RejectsBeforeSignature()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01]);
		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		int signatureInspectionCount = 0;
		using GrokCliExecutableStager stager = new(
			() => trustedRoot,
			_ =>
			{
				signatureInspectionCount++;
				return CreateTrustedSignature();
			},
			_ => false,
			_ => true,
			(_, _) => true,
			(_, _) => true,
			path =>
			{
				Directory.CreateDirectory(path);
				return true;
			},
			File.Exists);

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(0, signatureInspectionCount);
		Assert.False(Directory.Exists(trustedRoot));
	}

	[Fact]
	public void Stage_WhenSourceExceedsSizeLimit_RejectsBeforeSignature()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A]);
		using (FileStream stream = new(
			sourcePath,
			FileMode.Open,
			FileAccess.Write,
			FileShare.None))
		{
			stream.SetLength(
				GrokCliExecutableStager.MaximumExecutableSizeBytes + 1);
		}

		string trustedRoot = Path.Combine(
			temporaryDirectory.Path,
			"trusted");
		int signatureInspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ =>
			{
				signatureInspectionCount++;
				return CreateTrustedSignature();
			});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});

		Assert.Equal(0, signatureInspectionCount);
		Assert.False(Directory.Exists(trustedRoot));
	}

	[Fact]
	public void ResolveDefaultTrustedRoot_IsFixedDriveAndCurrentUserScoped()
	{
		string trustedRoot = GrokCliExecutableStager.ResolveDefaultTrustedRoot();
		string fixedDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ??
			throw new InvalidOperationException(
				"The Windows system drive is unavailable.");
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		string currentUserSid = identity.User?.Value ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");

		Assert.StartsWith(
			Path.GetFullPath(fixedDriveRoot),
			Path.GetFullPath(trustedRoot),
			StringComparison.OrdinalIgnoreCase);
		Assert.Contains(currentUserSid, trustedRoot, StringComparison.Ordinal);
	}

	[Fact]
	public void Stage_WithVolumeRootProtectedHierarchy_PassesRealAclPolicy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(
			temporaryDirectory,
			[0x4D, 0x5A, 0x01, 0x02]);
		string volumeRoot = Path.GetPathRoot(Environment.SystemDirectory) ??
			throw new InvalidOperationException(
				"The Windows system drive is unavailable.");
		string parentPath = Path.Combine(
			volumeRoot,
			$"AiUsageDashboard.GrokCliAclTests.{Guid.NewGuid():N}");
		string trustedRoot = Path.Combine(parentPath, "executables");

		try
		{
			bool wasPrepared = AntigravityPrivateKeyAcl
				.TryPreparePrivateStorageDirectory(
					trustedRoot,
					out _,
					out AntigravityPrivateKeyDirectoryFailureReason failureReason);
			Assert.True(wasPrepared, failureReason.ToString());
			Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(parentPath));
			Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(trustedRoot));
			Assert.True(
				WindowsExecutablePathSecurity.IsFixedDrivePath(trustedRoot));
			Assert.True(WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(
					trustedRoot,
					trustedRoot));
			using GrokCliExecutableStager stager = new(
				() => trustedRoot,
				_ => CreateTrustedSignature(),
				WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
				WindowsExecutablePathSecurity.IsFixedDrivePath,
				WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot,
				WindowsExecutablePathSecurity
					.IsDirectoryPathAclSafeWithinTrustedRoot,
				path => AntigravityPrivateKeyAcl
					.TryPreparePrivateStorageDirectory(path, out _, out _),
				AntigravityPrivateKeyAcl.TryProtectNewFile);
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
			string stagedPath = lease.ExecutablePath;

			Assert.True(File.Exists(stagedPath));
			Assert.True(WindowsExecutablePathSecurity
				.IsPathAclSafeWithinTrustedRoot(stagedPath, trustedRoot));
		}
		finally
		{
			if (Directory.Exists(parentPath) &&
				string.Equals(
					Path.GetDirectoryName(Path.GetFullPath(parentPath)),
					Path.TrimEndingDirectorySeparator(volumeRoot),
					StringComparison.OrdinalIgnoreCase) &&
				Path.GetFileName(parentPath).StartsWith(
					"AiUsageDashboard.GrokCliAclTests.",
					StringComparison.Ordinal))
			{
				Directory.Delete(parentPath, recursive: true);
			}
		}
	}

	private static GrokCliExecutableStager CreateStager(
		string trustedRoot,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		bool isPathAclSafe = true)
	{
		return new GrokCliExecutableStager(
			() => trustedRoot,
			inspectSignature,
			GrokCliExecutableValidator.IsCanonicalNonReparseFile,
			_ => true,
			(_, _) => isPathAclSafe,
			(_, _) => true,
			path =>
			{
				Directory.CreateDirectory(path);
				return true;
			},
			File.Exists);
	}

	private static string CreateSource(
		TemporaryDirectory temporaryDirectory,
		byte[] content)
	{
		string sourceDirectory = Path.Combine(
			temporaryDirectory.Path,
			"source");
		Directory.CreateDirectory(sourceDirectory);
		string sourcePath = Path.Combine(sourceDirectory, "grok.exe");
		File.WriteAllBytes(sourcePath, content);
		return sourcePath;
	}

	private static WindowsAuthenticodeInspection CreateTrustedSignature()
	{
		return new WindowsAuthenticodeInspection(
			WinVerifyTrustStatus: 0,
			SignerSubject: ExactSignerSubject,
			SignerThumbprint: new string('A', 40));
	}

	private static WindowsAuthenticodeInspection CreateUntrustedSignature()
	{
		return new WindowsAuthenticodeInspection(
			WinVerifyTrustStatus: 0,
			SignerSubject: "CN=Unknown",
			SignerThumbprint: new string('B', 40));
	}
}
