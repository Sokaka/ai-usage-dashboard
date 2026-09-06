using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class JsonCodexWorkspaceBindingStoreTests
{
	private static readonly Guid AccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid WorkspaceId =
		Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

	[Fact]
	public void Create_UsesOpaqueIdentityAndMatchesCanonicalEntitlement()
	{
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"Person@Example.com",
			WorkspaceId);

		Assert.False(binding.IsRestartQuarantine);
		Assert.NotEqual(Guid.Empty, binding.PublicBindingId);
		Assert.True(binding.MatchesAccount(" person@example.com "));
		Assert.True(binding.MatchesWorkspace(WorkspaceId));
		Assert.True(binding.MatchesEntitlement(
			"PERSON@EXAMPLE.COM",
			WorkspaceId));
		Assert.False(binding.MatchesEntitlement(
			"other@example.com",
			WorkspaceId));
		Assert.False(binding.MatchesEntitlement(
			"person@example.com",
			Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
	}

	[Fact]
	public void CreateRestartQuarantine_UsesOpaqueMaterialAndNeverMatchesBinding()
	{
		CodexWorkspaceBinding binding =
			CodexWorkspaceBinding.CreateRestartQuarantine(AccountId);
		string publicIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);

		Assert.True(binding.IsRestartQuarantine);
		Assert.NotEqual(Guid.Empty, binding.PublicBindingId);
		Assert.True(CodexWorkspaceBinding.IsCanonicalSalt(binding.SaltBase64));
		Assert.True(CodexWorkspaceBinding.IsCanonicalFingerprint(
			binding.AccountFingerprintSha256));
		Assert.True(CodexWorkspaceBinding.IsCanonicalFingerprint(
			binding.WorkspaceFingerprintSha256));
		Assert.True(CodexWorkspaceBinding.IsCanonicalFingerprint(
			binding.EntitlementFingerprintSha256));
		Assert.False(binding.MatchesAccount("person@example.com"));
		Assert.False(binding.MatchesWorkspace(WorkspaceId));
		Assert.False(binding.MatchesEntitlement(
			"person@example.com",
			WorkspaceId));
		Assert.False(binding.MatchesPublicProfile(new AccountProfile(
			AccountId,
			ProviderKind.Codex,
			"Work",
			ProviderAccountIdentity: publicIdentity)));
	}

	[Fact]
	public void MatchesPublicProfile_RequiresSameAccountProviderAndOpaqueBinding()
	{
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"person@example.com",
			WorkspaceId);
		string publicIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);

		Assert.True(binding.MatchesPublicProfile(new AccountProfile(
			AccountId,
			ProviderKind.Codex,
			"Work",
			ProviderAccountIdentity: publicIdentity)));
		Assert.False(binding.MatchesPublicProfile(new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Work",
			ProviderAccountIdentity: publicIdentity)));
		Assert.False(binding.MatchesPublicProfile(new AccountProfile(
			AccountId,
			ProviderKind.Claude,
			"Work",
			ProviderAccountIdentity: publicIdentity)));
	}

	[Fact]
	public async Task SaveAndLoadAsync_PersistsNormalBindingAsVersion2()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		JsonCodexWorkspaceBindingStore store = new(_ => filePath);
		const string AccountIdentity = "private-person@example.com";
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			AccountIdentity,
			WorkspaceId);

		await store.SaveAsync(binding);
		CodexWorkspaceBinding? loaded = await store.LoadAsync(AccountId);
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(binding, loaded);
		Assert.False(loaded!.IsRestartQuarantine);
		Assert.True(loaded!.MatchesEntitlement(AccountIdentity, WorkspaceId));
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.False(document.RootElement
			.GetProperty("isRestartQuarantine")
			.GetBoolean());
		Assert.Equal(
			[
				"schemaVersion",
				"isRestartQuarantine",
				"publicBindingId",
				"saltBase64",
				"accountFingerprintSha256",
				"workspaceFingerprintSha256",
				"entitlementFingerprintSha256"
			],
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name));
		Assert.DoesNotContain(
			AccountIdentity,
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			WorkspaceId.ToString("D"),
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			AccountId.ToString("D"),
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task SaveAndLoadAsync_PersistsRestartQuarantineAsVersion2()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		JsonCodexWorkspaceBindingStore store = new(_ => filePath);
		CodexWorkspaceBinding binding =
			CodexWorkspaceBinding.CreateRestartQuarantine(AccountId);

		await store.SaveAsync(binding);
		CodexWorkspaceBinding? loaded = await store.LoadAsync(AccountId);
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(binding, loaded);
		Assert.True(loaded!.IsRestartQuarantine);
		Assert.False(loaded.MatchesAccount("private-person@example.com"));
		Assert.False(loaded.MatchesWorkspace(WorkspaceId));
		Assert.False(loaded.MatchesEntitlement(
			"private-person@example.com",
			WorkspaceId));
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.True(document.RootElement
			.GetProperty("isRestartQuarantine")
			.GetBoolean());
		Assert.DoesNotContain(
			AccountId.ToString("D"),
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			WorkspaceId.ToString("D"),
			json,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task LoadAsync_WithVersion1NormalBinding_LoadsAsNonQuarantine()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"person@example.com",
			WorkspaceId);
		await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			workspaceFingerprintSha256 = binding.WorkspaceFingerprintSha256,
			entitlementFingerprintSha256 = binding.EntitlementFingerprintSha256
		}));
		JsonCodexWorkspaceBindingStore store = new(_ => filePath);

		CodexWorkspaceBinding? loaded = await store.LoadAsync(AccountId);

		Assert.Equal(binding, loaded);
		Assert.False(loaded!.IsRestartQuarantine);
		Assert.True(loaded.MatchesEntitlement(
			"person@example.com",
			WorkspaceId));
	}

	[Fact]
	public async Task LoadAsync_WithUnknownOrMissingProperty_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"person@example.com",
			WorkspaceId);
		await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			workspaceFingerprintSha256 = binding.WorkspaceFingerprintSha256,
			entitlementFingerprintSha256 = binding.EntitlementFingerprintSha256,
			rawWorkspaceId = WorkspaceId
		}));

		JsonCodexWorkspaceBindingStore store = new(_ => filePath);
		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(AccountId));

		await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			entitlementFingerprintSha256 = binding.EntitlementFingerprintSha256
		}));
		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(AccountId));
	}

	[Fact]
	public async Task LoadAsync_WithVersion2UnknownOrMissingProperty_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"person@example.com",
			WorkspaceId);
		await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new
		{
			schemaVersion = 2,
			isRestartQuarantine = false,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			workspaceFingerprintSha256 = binding.WorkspaceFingerprintSha256,
			entitlementFingerprintSha256 = binding.EntitlementFingerprintSha256,
			rawWorkspaceId = WorkspaceId
		}));

		JsonCodexWorkspaceBindingStore store = new(_ => filePath);
		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(AccountId));

		await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new
		{
			schemaVersion = 2,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			accountFingerprintSha256 = binding.AccountFingerprintSha256,
			workspaceFingerprintSha256 = binding.WorkspaceFingerprintSha256,
			entitlementFingerprintSha256 = binding.EntitlementFingerprintSha256
		}));
		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(AccountId));
	}

	[Fact]
	public async Task LoadAsync_WithNonObjectRoot_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"workspace-binding-v1.json");
		await File.WriteAllTextAsync(filePath, "[]");
		JsonCodexWorkspaceBindingStore store = new(_ => filePath);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(AccountId));
	}

	[Fact]
	public async Task LoadAllAndDeleteAsync_EnumeratesCanonicalAccountsOnly()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"codex");
		Guid secondAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		JsonCodexWorkspaceBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"workspace-binding-v1.json"),
			() => rootDirectoryPath);
		CodexWorkspaceBinding first = CodexWorkspaceBinding.Create(
			AccountId,
			"first@example.com",
			WorkspaceId);
		CodexWorkspaceBinding second = CodexWorkspaceBinding.Create(
			secondAccountId,
			"second@example.com",
			Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
		await store.SaveAsync(first);
		await store.SaveAsync(second);
		Directory.CreateDirectory(Path.Combine(rootDirectoryPath, "not-an-id"));

		IReadOnlyList<CodexWorkspaceBinding> bindings =
			await store.LoadAllAsync();
		await store.DeleteAsync(AccountId);

		Assert.Equal(2, bindings.Count);
		Assert.Contains(first, bindings);
		Assert.Contains(second, bindings);
		Assert.Null(await store.LoadAsync(AccountId));
		Assert.Equal(second, await store.LoadAsync(secondAccountId));
	}

	[Fact]
	public async Task LoadAllAsync_WithMoreThanMaximumHomeOnlyDirectories_LoadsBinding()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"codex");
		JsonCodexWorkspaceBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"workspace-binding-v1.json"),
			() => rootDirectoryPath);
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			AccountId,
			"person@example.com",
			WorkspaceId);
		await store.SaveAsync(binding);

		for (int index = 1; index <= 257; index++)
		{
			Guid homeOnlyAccountId = Guid.Parse(
				$"00000000-0000-0000-0000-{index:D12}");
			Directory.CreateDirectory(Path.Combine(
				rootDirectoryPath,
				homeOnlyAccountId.ToString("N")));
		}

		IReadOnlyList<CodexWorkspaceBinding> bindings =
			await store.LoadAllAsync();

		Assert.Equal(binding, Assert.Single(bindings));
	}

	[Fact]
	public async Task LoadAllAsync_WithMoreThanMaximumBindings_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string rootDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"codex");
		JsonCodexWorkspaceBindingStore store = new(
			accountId => Path.Combine(
				rootDirectoryPath,
				accountId.ToString("N"),
				"workspace-binding-v1.json"),
			() => rootDirectoryPath);

		for (int index = 1; index <= 257; index++)
		{
			Guid accountId = Guid.Parse(
				$"10000000-0000-0000-0000-{index:D12}");
			await store.SaveAsync(CodexWorkspaceBinding.Create(
				accountId,
				$"person-{index}@example.com",
				WorkspaceId));
		}

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(
				() => store.LoadAllAsync());

		Assert.Contains("超過安全上限", exception.Message, StringComparison.Ordinal);
	}
}
