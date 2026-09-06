using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class JsonAntigravityVerifiedAccountBindingStoreTests
{
	private static readonly Guid AccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly DateTimeOffset SourceCapturedAtUtc =
		new(2026, 8, 13, 9, 40, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset QuotaObservedAtUtc =
		new(2026, 8, 13, 9, 40, 3, TimeSpan.Zero);
	private static readonly DateTimeOffset VerifiedAtUtc =
		new(2026, 8, 13, 9, 40, 5, TimeSpan.Zero);

	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsStrictDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		AntigravityVerifiedAccountBinding expected = CreateBinding();

		await store.SaveAsync(expected, CancellationToken.None);
		AntigravityVerifiedAccountBinding? actual = await CreateStore(
			temporaryDirectory).LoadAsync(AccountId, CancellationToken.None);

		Assert.Equal(expected, actual);
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllBytesAsync(filePath));
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(AccountId, document.RootElement.GetProperty("accountId").GetGuid());
		Assert.Equal(
			expected.NormalizedEmailSha256,
			document.RootElement.GetProperty("normalizedEmailSha256").GetString());
		Assert.Equal(6, document.RootElement.EnumerateObject().Count());
		Assert.Empty(Directory.GetFiles(
			Path.GetDirectoryName(filePath)!,
			"*.tmp"));
	}

	[Fact]
	public async Task SaveAsync_WhenCaptureShortlyFollowsQuota_RejectsAmbiguousEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		AntigravityVerifiedAccountBinding expected = CreateBinding() with
		{
			SourceCapturedAtUtc = QuotaObservedAtUtc.AddSeconds(50),
			VerifiedAtUtc = QuotaObservedAtUtc.AddSeconds(51)
		};

		await Assert.ThrowsAnyAsync<ArgumentException>(() =>
			store.SaveAsync(expected, CancellationToken.None));
		Assert.False(File.Exists(GetBindingFilePath(
			temporaryDirectory,
			AccountId)));
	}

	[Fact]
	public void ComputeNormalizedEmailSha256_IsCaseInsensitiveAndCanonical()
	{
		string first =
			AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
				"Player.Name+AGY@example.com");
		string second =
			AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
				"PLAYER.NAME+AGY@EXAMPLE.COM");

		Assert.Equal(first, second);
		Assert.Equal(64, first.Length);
		Assert.All(
			first,
			character => Assert.True(
				char.IsAsciiDigit(character) ||
				(character is >= 'A' and <= 'F')));
		Assert.Throws<ArgumentException>(() =>
			AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
				"not an email"));
	}

	[Fact]
	public async Task SaveAsync_WhenTargetExists_AtomicallyReplacesCompleteDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		AntigravityVerifiedAccountBinding original = CreateBinding();
		AntigravityVerifiedAccountBinding replacement = original with
		{
			NormalizedEmailSha256 =
				AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
					"replacement@example.com"),
			SourceCapturedAtUtc = SourceCapturedAtUtc.AddMinutes(1),
			QuotaObservedAtUtc = QuotaObservedAtUtc.AddMinutes(1),
			VerifiedAtUtc = VerifiedAtUtc.AddMinutes(1)
		};

		await store.SaveAsync(original, CancellationToken.None);
		await store.SaveAsync(replacement, CancellationToken.None);

		Assert.Equal(
			replacement,
			await store.LoadAsync(AccountId, CancellationToken.None));
		string directoryPath = Path.GetDirectoryName(
			GetBindingFilePath(temporaryDirectory, AccountId))!;
		Assert.Empty(Directory.GetFiles(directoryPath, "*.tmp"));
		Assert.Empty(Directory.GetFiles(directoryPath, "*.bak"));
	}

	[Fact]
	public async Task LoadAsync_WhenFileIsMissing_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();

		AntigravityVerifiedAccountBinding? binding = await CreateStore(
			temporaryDirectory).LoadAsync(AccountId, CancellationToken.None);

		Assert.Null(binding);
	}

	[Fact]
	public async Task LoadAsync_WhenDocumentIsMalformedOrViolatesSchema_ReturnsNullAndPreservesFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string validJson = CreateValidJson(AccountId);
		string otherAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222").ToString("D");
		string lowercaseHash = CreateBinding().NormalizedEmailSha256.ToLowerInvariant();
		string[] invalidDocuments =
		{
			"{\"schemaVersion\":1,",
			validJson.Replace(
				"\"verifiedAtUtc\":",
				"\"unexpected\":true,\"verifiedAtUtc\":",
				StringComparison.Ordinal),
			validJson.Replace(
				"\"schemaVersion\":1,",
				"\"schemaVersion\":1,\"schemaVersion\":1,",
				StringComparison.Ordinal),
			validJson.Replace(
				$"\"accountId\":\"{AccountId:D}\",",
				string.Empty,
				StringComparison.Ordinal),
			validJson.Replace(
				"\"schemaVersion\":1",
				"\"schemaVersion\":2",
				StringComparison.Ordinal),
			validJson.Replace(
				AccountId.ToString("D"),
				otherAccountId,
				StringComparison.Ordinal),
			validJson.Replace(
				CreateBinding().NormalizedEmailSha256,
				lowercaseHash,
				StringComparison.Ordinal),
			validJson.Replace(
				CreateBinding().NormalizedEmailSha256,
				"ABCDEF",
				StringComparison.Ordinal),
			validJson.Replace(
				"2026-08-13T09:40:00+00:00",
				"2026-08-13T17:40:00+08:00",
				StringComparison.Ordinal),
			validJson.Replace(
				"2026-08-13T09:40:00+00:00",
				"2026-08-13T09:40:04+00:00",
				StringComparison.Ordinal),
			validJson.Replace(
				"2026-08-13T09:40:03+00:00",
				"0001-01-01T00:00:00+00:00",
				StringComparison.Ordinal),
			validJson.Replace(
				"2026-08-13T09:40:03+00:00",
				"2026-08-13T09:38:59+00:00",
				StringComparison.Ordinal),
			validJson.Replace(
				"2026-08-13T09:40:05+00:00",
				"2026-08-13T09:40:02+00:00",
				StringComparison.Ordinal),
			validJson.Replace(
				$"\"normalizedEmailSha256\":\"{CreateBinding().NormalizedEmailSha256}\"",
				"\"normalizedEmailSha256\":42",
				StringComparison.Ordinal),
			"[]"
		};
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);

		foreach (string invalidDocument in invalidDocuments)
		{
			await WriteDocumentAsync(filePath, invalidDocument);

			AntigravityVerifiedAccountBinding? loaded = await CreateStore(
				temporaryDirectory).LoadAsync(AccountId, CancellationToken.None);

			Assert.Null(loaded);
			Assert.Equal(invalidDocument, await File.ReadAllTextAsync(filePath));
		}
	}

	[Fact]
	public async Task LoadAsync_WhenDocumentExceedsSizeLimit_ReturnsNullWithoutModifyingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);
		byte[] oversizedDocument = Encoding.UTF8.GetBytes(
			new string(' ', (4 * 1024) + 1));
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		await File.WriteAllBytesAsync(filePath, oversizedDocument);

		AntigravityVerifiedAccountBinding? loaded = await CreateStore(
			temporaryDirectory).LoadAsync(AccountId, CancellationToken.None);

		Assert.Null(loaded);
		Assert.Equal(oversizedDocument, await File.ReadAllBytesAsync(filePath));
	}

	[Fact]
	public async Task SaveAsync_WhenBindingIsInvalid_RejectsItAndPreservesTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		AntigravityVerifiedAccountBinding original = CreateBinding();
		await store.SaveAsync(original, CancellationToken.None);
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);
		byte[] originalDocument = await File.ReadAllBytesAsync(filePath);
		AntigravityVerifiedAccountBinding[] invalidBindings =
		{
			original with { AccountId = Guid.Empty },
			original with { NormalizedEmailSha256 = original.NormalizedEmailSha256.ToLowerInvariant() },
			original with { NormalizedEmailSha256 = "ABCDEF" },
			original with { SourceCapturedAtUtc = default },
			original with { QuotaObservedAtUtc = new DateTimeOffset(2026, 8, 13, 17, 40, 3, TimeSpan.FromHours(8)) },
			original with { VerifiedAtUtc = default },
			original with { QuotaObservedAtUtc = SourceCapturedAtUtc.AddSeconds(-61) },
			original with { SourceCapturedAtUtc = QuotaObservedAtUtc.AddSeconds(61) },
			original with
			{
				SourceCapturedAtUtc = QuotaObservedAtUtc.AddSeconds(1),
				VerifiedAtUtc = QuotaObservedAtUtc.AddSeconds(2)
			},
			original with { VerifiedAtUtc = QuotaObservedAtUtc.AddSeconds(-1) }
		};

		foreach (AntigravityVerifiedAccountBinding invalidBinding in invalidBindings)
		{
			await Assert.ThrowsAnyAsync<ArgumentException>(() =>
				store.SaveAsync(invalidBinding, CancellationToken.None));

			Assert.Equal(originalDocument, await File.ReadAllBytesAsync(filePath));
		}
	}

	[Fact]
	public async Task SaveAsync_WhenStorageIsUnavailable_IsBestEffort()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string blockingFilePath = Path.Combine(temporaryDirectory.Path, "blocked");
		await File.WriteAllTextAsync(blockingFilePath, "not a directory");
		JsonAntigravityVerifiedAccountBindingStore store = new(
			accountId => Path.Combine(
				blockingFilePath,
				accountId.ToString("N"),
				"account-display-binding-v1.json"));

		await store.SaveAsync(CreateBinding(), CancellationToken.None);

		Assert.Equal("not a directory", await File.ReadAllTextAsync(blockingFilePath));
	}

	[Fact]
	public async Task DeleteAsync_AfterSaveRemovesFileAndMissingDeleteIsSafe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);
		await store.SaveAsync(CreateBinding(), CancellationToken.None);
		string directoryPath = Path.GetDirectoryName(filePath)!;
		string fileName = Path.GetFileName(filePath);
		string ownedBackupPath = Path.Combine(
			directoryPath,
			$".{fileName}.recovery.bak");
		string ownedTemporaryPath = Path.Combine(
			directoryPath,
			$".{fileName}.interrupted.tmp");
		string unrelatedPath = Path.Combine(directoryPath, "unrelated.bak");
		await File.WriteAllTextAsync(ownedBackupPath, "old binding");
		await File.WriteAllTextAsync(ownedTemporaryPath, "partial binding");
		await File.WriteAllTextAsync(unrelatedPath, "keep me");

		await store.DeleteAsync(AccountId, CancellationToken.None);
		await store.DeleteAsync(AccountId, CancellationToken.None);

		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists(ownedBackupPath));
		Assert.False(File.Exists(ownedTemporaryPath));
		Assert.True(File.Exists(unrelatedPath));
		Assert.Null(await store.LoadAsync(AccountId, CancellationToken.None));
	}

	[Fact]
	public async Task DeleteAsync_WhenTargetIsLocked_PropagatesStorageFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		string filePath = GetBindingFilePath(temporaryDirectory, AccountId);
		await store.SaveAsync(CreateBinding(), CancellationToken.None);

		await using (FileStream lockedTarget = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			await Assert.ThrowsAnyAsync<IOException>(() =>
				store.DeleteAsync(AccountId, CancellationToken.None));
			Assert.True(File.Exists(filePath));
		}

		await store.DeleteAsync(AccountId, CancellationToken.None);
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task Operations_WhenAccountIdIsEmpty_RejectIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.LoadAsync(Guid.Empty, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() =>
			store.DeleteAsync(Guid.Empty, CancellationToken.None));
	}

	[Fact]
	public async Task Operations_WhenAlreadyCanceled_PropagateCancellation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore store = CreateStore(
			temporaryDirectory);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			store.LoadAsync(AccountId, cancellation.Token));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			store.SaveAsync(CreateBinding(), cancellation.Token));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			store.DeleteAsync(AccountId, cancellation.Token));
	}

	[Fact]
	public async Task Operations_WithJunctionAncestor_FailClosedWithoutTouchingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"target");
		string junctionDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"binding-junction");
		string targetFilePath = Path.Combine(
			targetDirectoryPath,
			"account-display-binding-v1.json");
		Directory.CreateDirectory(targetDirectoryPath);
		JsonAntigravityVerifiedAccountBindingStore targetStore = new(
			_ => targetFilePath);
		await targetStore.SaveAsync(CreateBinding(), CancellationToken.None);
		byte[] expectedDocument = await File.ReadAllBytesAsync(targetFilePath);
		await JunctionTestHelper.CreateAsync(
			junctionDirectoryPath,
			targetDirectoryPath);

		try
		{
			JsonAntigravityVerifiedAccountBindingStore aliasedStore = new(
				_ => Path.Combine(
					junctionDirectoryPath,
					"account-display-binding-v1.json"));

			Assert.Null(await aliasedStore.LoadAsync(
				AccountId,
				CancellationToken.None));
			await aliasedStore.SaveAsync(
				CreateBinding() with
				{
					NormalizedEmailSha256 =
						AntigravityVerifiedAccountBinding
							.ComputeNormalizedEmailSha256(
								"replacement@example.com")
				},
				CancellationToken.None);
			await Assert.ThrowsAsync<IOException>(() => aliasedStore.DeleteAsync(
				AccountId,
				CancellationToken.None));
			Assert.Equal(
				expectedDocument,
				await File.ReadAllBytesAsync(targetFilePath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectoryPath);
		}
	}

	[Fact]
	public async Task ConcurrentSaves_AreSerializedIntoOneCompleteDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityVerifiedAccountBindingStore[] stores = Enumerable
			.Range(0, 8)
			.Select(_ => CreateStore(temporaryDirectory))
			.ToArray();
		AntigravityVerifiedAccountBinding[] candidates = Enumerable
			.Range(0, stores.Length)
			.Select(index => CreateBinding() with
			{
				NormalizedEmailSha256 =
					AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
						$"account{index}@example.com"),
				VerifiedAtUtc = VerifiedAtUtc.AddSeconds(index)
			})
			.ToArray();

		await Task.WhenAll(stores.Select((store, index) =>
			store.SaveAsync(candidates[index], CancellationToken.None)));

		AntigravityVerifiedAccountBinding? loaded = await stores[0].LoadAsync(
			AccountId,
			CancellationToken.None);
		Assert.NotNull(loaded);
		Assert.Contains(loaded, candidates);
		string directoryPath = Path.GetDirectoryName(
			GetBindingFilePath(temporaryDirectory, AccountId))!;
		Assert.Empty(Directory.GetFiles(directoryPath, "*.tmp"));
	}

	private static AntigravityVerifiedAccountBinding CreateBinding()
	{
		return new AntigravityVerifiedAccountBinding(
			AccountId,
			AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
				"player@example.com"),
			SourceCapturedAtUtc,
			QuotaObservedAtUtc,
			VerifiedAtUtc);
	}

	private static JsonAntigravityVerifiedAccountBindingStore CreateStore(
		TemporaryDirectory temporaryDirectory)
	{
		return new JsonAntigravityVerifiedAccountBindingStore(
			accountId => GetBindingFilePath(temporaryDirectory, accountId));
	}

	private static string GetBindingFilePath(
		TemporaryDirectory temporaryDirectory,
		Guid accountId)
	{
		return Path.Combine(
			temporaryDirectory.Path,
			"antigravity",
			accountId.ToString("N"),
			"account-display-binding-v1.json");
	}

	private static string CreateValidJson(Guid accountId)
	{
		return "{" +
			"\"schemaVersion\":1," +
			$"\"accountId\":\"{accountId:D}\"," +
			$"\"normalizedEmailSha256\":\"{CreateBinding().NormalizedEmailSha256}\"," +
			"\"sourceCapturedAtUtc\":\"2026-08-13T09:40:00+00:00\"," +
			"\"quotaObservedAtUtc\":\"2026-08-13T09:40:03+00:00\"," +
			"\"verifiedAtUtc\":\"2026-08-13T09:40:05+00:00\"" +
			"}";
	}

	private static async Task WriteDocumentAsync(
		string filePath,
		string json)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		await File.WriteAllTextAsync(
			filePath,
			json,
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}
}
