using System.Text.Json;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class JsonGrokAccountBindingStoreTests
{
	private static readonly Guid AccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");

	[Fact]
	public void Create_WithSamePrincipal_UsesIndependentRandomBindingsAndSalts()
	{
		GrokAccountBinding first = GrokAccountBinding.Create(
			AccountId,
			"email",
			"person@example.com");
		GrokAccountBinding second = GrokAccountBinding.Create(
			AccountId,
			"email",
			"person@example.com");

		Assert.NotEqual(Guid.Empty, first.PublicBindingId);
		Assert.NotEqual(first.PublicBindingId, second.PublicBindingId);
		Assert.NotEqual(first.SaltBase64, second.SaltBase64);
		Assert.NotEqual(
			first.PrincipalFingerprintSha256,
			second.PrincipalFingerprintSha256);
		Assert.True(first.MatchesPrincipal("email", "person@example.com"));
		Assert.False(first.MatchesPrincipal("email", "other@example.com"));
		Assert.False(first.MatchesPrincipal("user-id", "person@example.com"));
	}

	[Fact]
	public void Create_WithExplicitPublicBindingId_PreservesAttemptCorrelation()
	{
		Guid publicBindingId =
			Guid.Parse("33333333-3333-3333-3333-333333333333");

		GrokAccountBinding binding = GrokAccountBinding.Create(
			AccountId,
			publicBindingId,
			"email",
			"person@example.com");

		Assert.Equal(publicBindingId, binding.PublicBindingId);
		Assert.True(binding.MatchesPrincipal("email", "person@example.com"));
	}

	[Fact]
	public void Create_WithEmptyExplicitPublicBindingId_RejectsBinding()
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(() =>
			GrokAccountBinding.Create(
				AccountId,
				Guid.Empty,
				"email",
				"person@example.com"));

		Assert.Equal("publicBindingId", exception.ParamName);
	}

	[Fact]
	public async Task SaveAndLoadAsync_PersistsOnlySaltedBindingMaterial()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-binding-v1.json");
		JsonGrokAccountBindingStore store = new(_ => filePath);
		GrokAccountBinding binding = GrokAccountBinding.Create(
			AccountId,
			"email",
			"person@example.com");

		await store.SaveAsync(binding);
		GrokAccountBinding? loaded = await store.LoadAsync(AccountId);
		string json = await File.ReadAllTextAsync(filePath);

		Assert.Equal(binding, loaded);
		Assert.True(loaded!.MatchesPrincipal("email", "person@example.com"));
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.Equal(
			[
				"schemaVersion",
				"publicBindingId",
				"saltBase64",
				"principalFingerprintSha256"
			],
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name));
		Assert.DoesNotContain("person@example.com", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"email\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain(AccountId.ToString(), json, StringComparison.Ordinal);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task LoadAllAsync_ReturnsBindingsFromCanonicalAccountDirectories()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid secondAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		JsonGrokAccountBindingStore store = new(
			accountId => Path.Combine(
				temporaryDirectory.Path,
				accountId.ToString("N"),
				"account-binding-v1.json"),
			() => temporaryDirectory.Path);
		GrokAccountBinding first = GrokAccountBinding.Create(
			AccountId,
			"email",
			"first@example.com");
		GrokAccountBinding second = GrokAccountBinding.Create(
			secondAccountId,
			"email",
			"second@example.com");

		await store.SaveAsync(first);
		await store.SaveAsync(second);
		Directory.CreateDirectory(Path.Combine(
			temporaryDirectory.Path,
			"version-probe"));

		IReadOnlyList<GrokAccountBinding> bindings = await store.LoadAllAsync();

		Assert.Equal(
			new[] { first, second }.OrderBy(binding => binding.AccountId),
			bindings.OrderBy(binding => binding.AccountId));
	}

	[Fact]
	public async Task LoadAsync_WithUnknownProperty_RejectsDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"account-binding-v1.json");
		GrokAccountBinding binding = GrokAccountBinding.Create(
			AccountId,
			"email",
			"person@example.com");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			publicBindingId = binding.PublicBindingId,
			saltBase64 = binding.SaltBase64,
			principalFingerprintSha256 = binding.PrincipalFingerprintSha256,
			rawIdentity = "person@example.com"
		});
		await File.WriteAllTextAsync(filePath, document);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new JsonGrokAccountBindingStore(_ => filePath).LoadAsync(AccountId));
	}

	[Fact]
	public async Task DeleteAsync_RemovesOnlyRequestedBinding()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "first.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "second.json");
		Guid secondAccountId =
			Guid.Parse("22222222-2222-2222-2222-222222222222");
		JsonGrokAccountBindingStore store = new(accountId =>
			accountId == AccountId ? firstPath : secondPath);

		await store.SaveAsync(GrokAccountBinding.Create(
			AccountId,
			"email",
			"first@example.com"));
		await store.SaveAsync(GrokAccountBinding.Create(
			secondAccountId,
			"email",
			"second@example.com"));

		await store.DeleteAsync(AccountId);

		Assert.False(File.Exists(firstPath));
		Assert.True(File.Exists(secondPath));
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
		JsonGrokAccountBindingStore targetStore = new(_ => targetFilePath);
		await targetStore.SaveAsync(GrokAccountBinding.Create(
			AccountId,
			"email",
			"person@example.com"));
		await CreateJunctionAsync(junctionDirectoryPath, targetDirectoryPath);

		try
		{
			JsonGrokAccountBindingStore aliasedStore = new(_ => Path.Combine(
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
