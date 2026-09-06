using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityMachineSetupPrivateTransactionTests
{
	private static readonly byte[] ProfileBytes =
		Encoding.UTF8.GetBytes("{\"SchemaVersion\":\"synthetic-profile\"}");

	[Fact]
	public async Task CreateAsync_WithNewPrivateRoot_CreatesPrivateKey()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-001");
		string keyPath = transaction.KeyPath;

		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(privateRoot));
		Assert.Equal(privateRoot, Path.GetDirectoryName(keyPath));
		Assert.Equal(privateRoot, Path.GetDirectoryName(transaction.ProfilePath));
		Assert.NotEqual(keyPath, transaction.ProfilePath);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(keyPath));
		Assert.Equal(32, new FileInfo(keyPath).Length);
		Assert.False(File.Exists(transaction.ProfilePath));

		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.False(File.Exists(keyPath));
		Assert.Empty(Directory.EnumerateFiles(privateRoot));
	}

	[Fact]
	public async Task CreateAsync_WithMissingProviderParent_CreatesPrivateHierarchy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string applicationRoot = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard");
		Directory.CreateDirectory(applicationRoot);
		string providerRoot = Path.Combine(
			applicationRoot,
			"antigravity");
		string privateRoot = Path.Combine(providerRoot, "private");

		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-clean-machine");

		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			providerRoot));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			privateRoot));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(
			transaction.KeyPath));

		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.False(File.Exists(transaction.KeyPath));
	}

	[Fact]
	public async Task WriteProfileAsync_WithApprovedTransaction_PreservesExactPrivateFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-002");

		await transaction.WriteProfileAsync(ProfileBytes);
		transaction.MarkApproved();
		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(
			transaction.KeyPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(
			transaction.ProfilePath));
		Assert.Equal(
			ProfileBytes,
			await File.ReadAllBytesAsync(transaction.ProfilePath));
		Assert.Equal(
			new[]
			{
				Path.GetFullPath(transaction.KeyPath),
				Path.GetFullPath(transaction.ProfilePath)
			}.ToHashSet(StringComparer.OrdinalIgnoreCase),
			Directory.EnumerateFiles(privateRoot)
				.Select(Path.GetFullPath)
				.ToHashSet(StringComparer.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task DisposeAsync_WithoutApproval_RemovesOnlyOwnedFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-003");
		string unrelatedPath = Path.Combine(privateRoot, "unrelated.txt");
		await WritePrivateFileAsync(unrelatedPath, "unrelated");

		await transaction.WriteProfileAsync(ProfileBytes);
		string keyPath = transaction.KeyPath;
		string profilePath = transaction.ProfilePath;
		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.False(File.Exists(keyPath));
		Assert.False(File.Exists(profilePath));
		Assert.Equal(
			"unrelated",
			await File.ReadAllTextAsync(unrelatedPath));
		Assert.Equal(
			new[] { Path.GetFullPath(unrelatedPath) },
			Directory.EnumerateFiles(privateRoot)
				.Select(Path.GetFullPath));
	}

	[Fact]
	public async Task WriteProfileAsync_WhenDestinationAlreadyExists_RejectsWithoutOverwriteOrCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-005");
		byte[] existingBytes = Encoding.UTF8.GetBytes("existing");
		await WritePrivateFileAsync(
			transaction.ProfilePath,
			"existing");

		await Assert.ThrowsAsync<IOException>(() =>
			transaction.WriteProfileAsync(ProfileBytes));
		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.False(File.Exists(transaction.KeyPath));
		Assert.Equal(
			existingBytes,
			await File.ReadAllBytesAsync(transaction.ProfilePath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(
			transaction.ProfilePath));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData(".hidden")]
	[InlineData("../escape")]
	[InlineData("nested/path")]
	[InlineData("nested\\path")]
	[InlineData("Contract-With-Uppercase")]
	public async Task CreateAsync_WithUnsafeContractId_Rejects(
		string contractId)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");

		await Assert.ThrowsAnyAsync<ArgumentException>(() =>
			AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				contractId));

		Assert.False(Directory.Exists(privateRoot));
	}

	[Fact]
	public async Task CreateAsync_WithManifestCompatibleDottedContractId_Succeeds()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"agy.reviewed.v1");

		Assert.Contains(
			"agy.reviewed.v1",
			Path.GetFileName(transaction.ProfilePath),
			StringComparison.Ordinal);

		await transaction.DisposeAsync();
		Assert.True(transaction.CleanupSucceeded);
	}

	[Fact]
	public async Task CreateAsync_WithRelativePrivateRoot_Rejects()
	{
		await Assert.ThrowsAsync<ArgumentException>(() =>
			AntigravityMachineSetupPrivateTransaction.CreateAsync(
				"relative-private",
				"contract-006"));
	}

	[Fact]
	public async Task CreateAsync_WithExistingNonPrivateRoot_Rejects()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		Directory.CreateDirectory(privateRoot);
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateDirectory(privateRoot));

		await Assert.ThrowsAsync<IOException>(() =>
			AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-007"));

		Assert.Empty(Directory.EnumerateFiles(privateRoot));
	}

	[Fact]
	public async Task WriteProfileAsync_WhenCancelledBeforeWrite_LeavesNoProfileDebris()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-008");
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			transaction.WriteProfileAsync(
				ProfileBytes,
				cancellation.Token));
		Assert.Equal(
			new[] { Path.GetFullPath(transaction.KeyPath) },
			Directory.EnumerateFiles(privateRoot)
				.Select(Path.GetFullPath));

		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.Empty(Directory.EnumerateFiles(privateRoot));
	}

	[Fact]
	public async Task DisposeAsync_WhenOwnedKeyIsLocked_CanRetryCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-009");
		string keyPath = transaction.KeyPath;

		await using (FileStream lockedKey = new(
			keyPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await transaction.DisposeAsync();
			Assert.False(transaction.CleanupSucceeded);
			Assert.True(File.Exists(keyPath));
		}

		await transaction.DisposeAsync();

		Assert.True(transaction.CleanupSucceeded);
		Assert.False(File.Exists(keyPath));
	}

	[Fact]
	public async Task MarkApproved_BeforeProfileWrite_Rejects()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private");
		AntigravityMachineSetupPrivateTransaction transaction =
			await AntigravityMachineSetupPrivateTransaction.CreateAsync(
				privateRoot,
				"contract-010");

		Assert.Throws<InvalidOperationException>(() =>
			transaction.MarkApproved());

		await transaction.DisposeAsync();
		Assert.True(transaction.CleanupSucceeded);
	}

	private static async Task WritePrivateFileAsync(
		string path,
		string contents)
	{
		await File.WriteAllTextAsync(path, contents);
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
	}
}
