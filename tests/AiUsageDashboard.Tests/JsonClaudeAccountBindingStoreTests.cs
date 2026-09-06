using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class JsonClaudeAccountBindingStoreTests
{
	private static readonly Guid AccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");

	[Fact]
	public void Create_WithSameEntitlement_UsesIndependentRandomBindingsAndSalts()
	{
		ClaudeSubscriptionContext context = CreateContext(
			"person@example.com",
			"org-team");
		ClaudeAccountBinding first = ClaudeAccountBinding.Create(
			AccountId,
			context);
		ClaudeAccountBinding second = ClaudeAccountBinding.Create(
			AccountId,
			context);

		Assert.NotEqual(Guid.Empty, first.PublicBindingId);
		Assert.NotEqual(first.PublicBindingId, second.PublicBindingId);
		Assert.NotEqual(first.SaltBase64, second.SaltBase64);
		Assert.NotEqual(
			first.AccountFingerprintSha256,
			second.AccountFingerprintSha256);
		Assert.NotEqual(
			first.SubscriptionScopeFingerprintSha256,
			second.SubscriptionScopeFingerprintSha256);
		Assert.NotEqual(
			first.EntitlementFingerprintSha256,
			second.EntitlementFingerprintSha256);
		Assert.True(first.MatchesEntitlement(context));
		Assert.True(second.MatchesEntitlement(context));
	}

	[Fact]
	public void MatchesEntitlement_WithSameEmailAndDifferentOrganization_ReturnsFalse()
	{
		ClaudeSubscriptionContext personal = CreateContext(
			"person@example.com",
			"org-personal");
		ClaudeSubscriptionContext company = CreateContext(
			"person@example.com",
			"org-company");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			personal);

		Assert.True(binding.MatchesAccount(company.AccountIdentity));
		Assert.False(binding.MatchesSubscriptionScope(
			company.SubscriptionScopeIdentity));
		Assert.False(binding.MatchesEntitlement(company));
	}

	[Fact]
	public void MatchesEntitlement_WithDifferentEmailAndSameOrganization_ReturnsFalse()
	{
		ClaudeSubscriptionContext firstMember = CreateContext(
			"first@example.com",
			"org-company");
		ClaudeSubscriptionContext secondMember = CreateContext(
			"second@example.com",
			"org-company");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			firstMember);

		Assert.False(binding.MatchesAccount(secondMember.AccountIdentity));
		Assert.True(binding.MatchesSubscriptionScope(
			secondMember.SubscriptionScopeIdentity));
		Assert.False(binding.MatchesEntitlement(secondMember));
	}

	[Fact]
	public void MatchesEntitlement_WithSameTupleAndDifferentDisplayFields_ReturnsTrue()
	{
		ClaudeSubscriptionContext first = ClaudeSubscriptionContext.CreateVerified(
			"Person@Example.com",
			"org-company",
			"max",
			"Personal");
		ClaudeSubscriptionContext second = ClaudeSubscriptionContext.CreateVerified(
			" person@example.com ",
			" org-company ",
			"team",
			"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(AccountId, first);

		Assert.Equal(first.AccountIdentity, second.AccountIdentity);
		Assert.Equal(
			first.SubscriptionScopeIdentity,
			second.SubscriptionScopeIdentity);
		Assert.NotEqual(first.PlanTier, second.PlanTier);
		Assert.NotEqual(
			first.SubscriptionScopeDisplayName,
			second.SubscriptionScopeDisplayName);
		Assert.True(binding.MatchesEntitlement(second));
	}

	[Fact]
	public void PublicBindingIdentity_RequiresCanonicalLowercaseNFormat()
	{
		Guid publicBindingId =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
		string identity = ClaudeAccountBinding.CreatePublicBindingIdentity(
			publicBindingId);

		Assert.Equal("abcdefabcdefabcdefabcdefabcdefab", identity);
		Assert.True(ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
			identity,
			out string? normalizedIdentity));
		Assert.Equal(identity, normalizedIdentity);

		foreach (string invalidIdentity in new[]
		{
			identity.ToUpperInvariant(),
			publicBindingId.ToString("D"),
			Guid.Empty.ToString("N"),
			"not-a-binding-id"
		})
		{
			Assert.False(ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
				invalidIdentity,
				out normalizedIdentity));
			Assert.Null(normalizedIdentity);
		}
	}

	[Fact]
	public async Task SaveAndLoadAsync_PersistsOnlySaltedBindingMaterial()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-binding-v1.json");
		JsonClaudeAccountBindingStore store = new(_ => filePath);
		ClaudeSubscriptionContext context = ClaudeSubscriptionContext.CreateVerified(
			"private-person@example.com",
			"org-private-company",
			"enterprise-private-plan",
			"Private Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			context);

		await store.SaveAsync(binding);
		ClaudeAccountBinding? loaded = await store.LoadAsync(AccountId);
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(binding, loaded);
		Assert.True(loaded!.MatchesAccount(context.AccountIdentity));
		Assert.True(loaded.MatchesSubscriptionScope(
			context.SubscriptionScopeIdentity));
		Assert.True(loaded.MatchesEntitlement(context));
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.Equal(
			[
				"schemaVersion",
				"publicBindingId",
				"saltBase64",
				"accountFingerprintSha256",
				"subscriptionScopeFingerprintSha256",
				"entitlementFingerprintSha256"
			],
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name));
		Assert.DoesNotContain(
			context.AccountIdentity,
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			context.SubscriptionScopeIdentity,
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			context.PlanTier,
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			context.SubscriptionScopeDisplayName!,
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			AccountId.ToString(),
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task LoadAsync_WithUnknownProperty_RejectsDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-binding-v1.json");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("person@example.com", "org-company"));
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			subscriptionScopeFingerprintSha256 =
				binding.SubscriptionScopeFingerprintSha256,
			entitlementFingerprintSha256 =
				binding.EntitlementFingerprintSha256,
			rawAccountIdentity = "person@example.com"
		});
		await File.WriteAllTextAsync(filePath, document);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new JsonClaudeAccountBindingStore(_ => filePath).LoadAsync(AccountId));
	}

	[Fact]
	public async Task LoadAsync_WithMissingRequiredFingerprint_RejectsDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-binding-v1.json");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("person@example.com", "org-company"));
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			entitlementFingerprintSha256 =
				binding.EntitlementFingerprintSha256
		});
		await File.WriteAllTextAsync(filePath, document);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new JsonClaudeAccountBindingStore(_ => filePath).LoadAsync(AccountId));
	}

	[Fact]
	public async Task DeleteAsync_RemovesOnlyRequestedBinding()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "first.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "second.json");
		Guid secondAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		JsonClaudeAccountBindingStore store = new(accountId =>
			accountId == AccountId ? firstPath : secondPath);

		await store.SaveAsync(ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("first@example.com", "org-first")));
		await store.SaveAsync(ClaudeAccountBinding.Create(
			secondAccountId,
			CreateContext("second@example.com", "org-second")));

		await store.DeleteAsync(AccountId);

		Assert.False(File.Exists(firstPath));
		Assert.True(File.Exists(secondPath));
	}

	[Fact]
	public async Task LoadAllAsync_IncludesEveryCanonicalBindingAndIgnoresUnrelatedDirectories()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid orphanAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		JsonClaudeAccountBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => rootDirectoryPath);
		ClaudeAccountBinding configuredBinding = ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("configured@example.com", "org-configured"));
		ClaudeAccountBinding orphanBinding = ClaudeAccountBinding.Create(
			orphanAccountId,
			CreateContext("orphan@example.com", "org-orphan"));

		await store.SaveAsync(configuredBinding);
		await store.SaveAsync(orphanBinding);
		string unrelatedDirectoryPath = Path.Combine(
			rootDirectoryPath,
			"not-an-account-id");
		Directory.CreateDirectory(unrelatedDirectoryPath);
		await File.WriteAllTextAsync(
			Path.Combine(unrelatedDirectoryPath, "account-binding-v1.json"),
			"not json");

		IReadOnlyList<ClaudeAccountBinding> loaded = await store.LoadAllAsync();

		Assert.Equal(2, loaded.Count);
		Assert.Contains(configuredBinding, loaded);
		Assert.Contains(orphanBinding, loaded);
	}

	[Fact]
	public async Task LoadAllAsync_WhenAnyCanonicalBindingIsInvalid_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		JsonClaudeAccountBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => rootDirectoryPath);
		await store.SaveAsync(ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("configured@example.com", "org-configured")));
		Guid invalidAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		string invalidDirectoryPath = Path.Combine(
			rootDirectoryPath,
			invalidAccountId.ToString("N"));
		Directory.CreateDirectory(invalidDirectoryPath);
		await File.WriteAllTextAsync(
			Path.Combine(invalidDirectoryPath, "account-binding-v1.json"),
			"{}");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAllAsync());
	}

	[Fact]
	public async Task LoadAllAsync_WithMoreThanMaximumHomeOnlyDirectories_LoadsBinding()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		JsonClaudeAccountBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => rootDirectoryPath);
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("person@example.com", "org-company"));
		await store.SaveAsync(binding);

		for (int index = 1; index <= 257; index++)
		{
			Guid homeOnlyAccountId = Guid.Parse(
				$"00000000-0000-0000-0000-{index:D12}");
			Directory.CreateDirectory(Path.Combine(
				rootDirectoryPath,
				homeOnlyAccountId.ToString("N")));
		}

		IReadOnlyList<ClaudeAccountBinding> bindings = await store.LoadAllAsync();

		Assert.Equal(binding, Assert.Single(bindings));
	}

	[Fact]
	public async Task LoadAllAsync_WithMoreThanMaximumBindings_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		JsonClaudeAccountBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => rootDirectoryPath);

		for (int index = 1; index <= 257; index++)
		{
			Guid accountId = Guid.Parse(
				$"10000000-0000-0000-0000-{index:D12}");
			await store.SaveAsync(ClaudeAccountBinding.Create(
				accountId,
				CreateContext(
					$"person-{index}@example.com",
					$"org-{index}")));
		}

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(
				() => store.LoadAllAsync());

		Assert.Contains("超過安全上限", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LoadAllAsync_WithJunctionRoot_FailsClosedWithoutReadingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetRootPath = Path.Combine(temporaryDirectory.Path, "target");
		string junctionRootPath = Path.Combine(
			temporaryDirectory.Path,
			"binding-junction");
		JsonClaudeAccountBindingStore targetStore = new(
			accountId => Path.Combine(
				targetRootPath,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => targetRootPath);
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("person@example.com", "org-company"));
		await targetStore.SaveAsync(binding);
		await CreateJunctionAsync(junctionRootPath, targetRootPath);

		try
		{
			JsonClaudeAccountBindingStore aliasedStore = new(
				accountId => Path.Combine(
					junctionRootPath,
					accountId.ToString("N"),
					"account-binding-v1.json"),
				() => junctionRootPath);

			await Assert.ThrowsAsync<IOException>(
				() => aliasedStore.LoadAllAsync());
			Assert.Equal(binding, await targetStore.LoadAsync(AccountId));
		}
		finally
		{
			Directory.Delete(junctionRootPath, recursive: false);
		}
	}

	[Fact]
	public async Task LoadAndDeleteAsync_WithJunctionAncestor_FailClosedWithoutTouchingTarget()
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
			"account-binding-v1.json");
		Directory.CreateDirectory(targetDirectoryPath);
		JsonClaudeAccountBindingStore targetStore = new(_ => targetFilePath);
		await targetStore.SaveAsync(ClaudeAccountBinding.Create(
			AccountId,
			CreateContext("person@example.com", "org-company")));
		await CreateJunctionAsync(junctionDirectoryPath, targetDirectoryPath);

		try
		{
			JsonClaudeAccountBindingStore aliasedStore = new(_ => Path.Combine(
				junctionDirectoryPath,
				"account-binding-v1.json"));

			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.LoadAsync(AccountId));
			await Assert.ThrowsAsync<IOException>(() =>
				aliasedStore.DeleteAsync(AccountId));
			Assert.True(File.Exists(targetFilePath));
			Assert.NotNull(await targetStore.LoadAsync(AccountId));
		}
		finally
		{
			Directory.Delete(junctionDirectoryPath, recursive: false);
		}
	}

	private static ClaudeSubscriptionContext CreateContext(
		string accountIdentity,
		string subscriptionScopeIdentity)
	{
		return ClaudeSubscriptionContext.CreateVerified(
			accountIdentity,
			subscriptionScopeIdentity,
			"max",
			"Team");
	}

	private static async Task CreateJunctionAsync(
		string junctionPath,
		string targetPath)
	{
		System.Diagnostics.ProcessStartInfo startInfo = new(
			Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add("/J");
		startInfo.ArgumentList.Add(junctionPath);
		startInfo.ArgumentList.Add(targetPath);
		using System.Diagnostics.Process process =
			System.Diagnostics.Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"The junction creation process did not start.");
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);
	}
}
