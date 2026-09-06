using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class JsonAccountProfileStoreTests
{
	[Fact]
	public async Task LoadAsync_WhenFileIsMissing_ReturnsEmptyWithoutCreatingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Missing, result.Status);
		Assert.True(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveAsync_WhenAccountCountExceedsLimit_RejectsWithoutCreatingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile[] accounts = Enumerable
			.Range(0, 257)
			.Select(index => new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				$"帳號 {index}"))
			.ToArray();
		await store.LoadAsync();

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => store.SaveAsync(accounts));

		Assert.Contains("256", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists($"{filePath}.bak"));
	}

	[Fact]
	public async Task SaveAsync_WhenDisplayNameContainsControlCharacter_RejectsWithoutCreatingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude\n管理員");

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => store.SaveAsync(new[] { account }));

		Assert.Contains("控制字元", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists($"{filePath}.bak"));
	}

	[Fact]
	public async Task SaveAndLoadAsync_WhenDisplayNameIsEmpty_RoundTripsEmptyDisplayName()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			string.Empty,
			HasAcceptedClaudeQuotaRisk: true);
		JsonAccountProfileStore store = new(filePath);

		await store.LoadAsync();
		await store.SaveAsync(new[] { account });
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));

		Assert.Equal(account, Assert.Single(result.Accounts));
		Assert.Equal(
			string.Empty,
			document.RootElement
				.GetProperty("accounts")[0]
				.GetProperty("displayName")
				.GetString());
	}

	[Fact]
	public async Task SaveAsync_WhenDisplayNameIsNull_RejectsWithoutCreatingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			null!);
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => store.SaveAsync(new[] { account }));

		Assert.Contains("null", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists($"{filePath}.bak"));
	}

	[Fact]
	public async Task LoadAsync_WhenDisplayNameContainsControlCharacter_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string invalidDocument = JsonSerializer.Serialize(new
		{
			schemaVersion = 4,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Claude",
					displayName = "Claude\t管理員",
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, invalidDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.Empty(result.Accounts);
		Assert.False(File.Exists(filePath));
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(invalidDocument, await File.ReadAllTextAsync(result.RecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WhenAccountCountExceedsLimit_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		object[] accounts = Enumerable
			.Range(0, 257)
			.Select(index => (object)new
			{
				id = Guid.NewGuid(),
				provider = "Claude",
				displayName = $"帳號 {index}",
				isEnabled = true
			})
			.ToArray();
		string json = JsonSerializer.Serialize(new
		{
			schemaVersion = 2,
			accounts
		});
		await File.WriteAllTextAsync(filePath, json);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.False(File.Exists(filePath));
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(json, await File.ReadAllTextAsync(result.RecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryExceedsSizeLimit_PreservesFileAndBlocksSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		byte[] oversizedContents = new byte[(1024 * 1024) + 1];
		await File.WriteAllBytesAsync(filePath, oversizedContents);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Unavailable, result.Status);
		Assert.False(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.Contains("1 MB", result.Message, StringComparison.Ordinal);
		Assert.True(File.Exists(filePath));
		Assert.Equal(oversizedContents.Length, new FileInfo(filePath).Length);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(Array.Empty<AccountProfile>()));
		Assert.Equal(oversizedContents.Length, new FileInfo(filePath).Length);
	}

	[Fact]
	public async Task LoadAsync_WhenValidPrimaryExistsWithoutBackup_CreatesValidatedBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		Guid accountId = Guid.NewGuid();
		string primaryDocument = $$"""
			{
			  "schemaVersion": 2,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "既有帳號",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, primaryDocument);
		byte[] primaryBytes = await File.ReadAllBytesAsync(filePath);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.True(result.CanSave);
		Assert.Equal(accountId, Assert.Single(result.Accounts).Id);
		Assert.True(File.Exists(backupPath));
		Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(backupPath));
		AccountProfileLoadResult backupResult =
			await new JsonAccountProfileStore(backupPath).LoadAsync();
		Assert.Equal(AccountProfileLoadStatus.Loaded, backupResult.Status);
		Assert.Equal(result.Accounts, backupResult.Accounts);
	}

	[Fact]
	public async Task LoadAsync_WhenValidPrimaryHasMalformedBackup_QuarantinesAndRepairsBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		Guid accountId = Guid.NewGuid();
		string primaryDocument = $$"""
			{
			  "schemaVersion": 2,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Codex",
			      "displayName": "既有帳號",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		byte[] malformedBackupBytes = "{ malformed backup"u8.ToArray();
		await File.WriteAllTextAsync(filePath, primaryDocument);
		byte[] primaryBytes = await File.ReadAllBytesAsync(filePath);
		await File.WriteAllBytesAsync(backupPath, malformedBackupBytes);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.True(result.CanSave);
		Assert.Null(result.Message);
		Assert.Equal(accountId, Assert.Single(result.Accounts).Id);
		Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(backupPath));
		string backupRecoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"accounts.json.corrupt-*.bak",
			SearchOption.TopDirectoryOnly));
		Assert.Equal(
			malformedBackupBytes,
			await File.ReadAllBytesAsync(backupRecoveryPath));
		AccountProfileLoadResult backupResult =
			await new JsonAccountProfileStore(backupPath).LoadAsync();
		Assert.Equal(AccountProfileLoadStatus.Loaded, backupResult.Status);
		Assert.Equal(result.Accounts, backupResult.Accounts);
	}

	[Fact]
	public async Task LoadAsync_WhenValidPrimaryHasFutureBackup_PreservesBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		Guid accountId = Guid.NewGuid();
		string primaryDocument = $$"""
			{
			  "schemaVersion": 2,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "既有帳號",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		const string futureBackup = """
			{
			  "schemaVersion": 8,
			  "accounts": [],
			  "futureField": true
			}
			""";
		await File.WriteAllTextAsync(filePath, primaryDocument);
		await File.WriteAllTextAsync(backupPath, futureBackup);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.False(result.CanSave);
		Assert.NotNull(result.Message);
		Assert.Contains("較新版本", result.Message);
		Assert.Contains("目前無法編輯帳號", result.Message);
		Assert.Equal(accountId, Assert.Single(result.Accounts).Id);
		Assert.Equal(futureBackup, await File.ReadAllTextAsync(backupPath));
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"accounts.json.corrupt-*.bak",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task LoadAsync_WhenBackupExceedsSizeLimit_PreservesBackupAndBlocksSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		Guid accountId = Guid.NewGuid();
		string primaryDocument = $$"""
			{
			  "schemaVersion": 2,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "既有帳號",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		byte[] oversizedBackup = new byte[(1024 * 1024) + 1];
		await File.WriteAllTextAsync(filePath, primaryDocument);
		await File.WriteAllBytesAsync(backupPath, oversizedBackup);
		byte[] primaryBytes = await File.ReadAllBytesAsync(filePath);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.False(result.CanSave);
		Assert.Equal(accountId, Assert.Single(result.Accounts).Id);
		Assert.Contains(
			"備份超過 1 MB",
			result.Message,
			StringComparison.Ordinal);
		Assert.Contains("目前無法編輯帳號", result.Message, StringComparison.Ordinal);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(result.Accounts));
		Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(filePath));
		Assert.Equal(oversizedBackup.Length, new FileInfo(backupPath).Length);
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"accounts.json.corrupt-*.bak",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsAllowlistedAccountFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile[] accounts =
		{
			new(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"私人帳號",
				IsEnabled: true,
				HasAcceptedClaudeQuotaRisk: true,
				ShowSubscriptionContext: true),
			new(Guid.NewGuid(), ProviderKind.Claude, "工作帳號", IsEnabled: false)
		};

		await store.LoadAsync();
		await store.SaveAsync(accounts);
		AccountProfileLoadResult result = await new JsonAccountProfileStore(filePath).LoadAsync();
		string backupPath = $"{filePath}.bak";

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.Equal(accounts, result.Accounts);
		Assert.True(File.Exists(backupPath));
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync(backupPath));

		string json = await File.ReadAllTextAsync(filePath);
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		Assert.Equal(
			new[] { "accounts", "schemaVersion" },
			root.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());

		JsonElement firstAccount = root.GetProperty("accounts")[0];
		Assert.True(firstAccount.GetProperty("showSubscriptionContext").GetBoolean());
		Assert.Equal(
			new[]
			{
				"displayName",
				"hasAcceptedClaudeQuotaRisk",
				"id",
				"isEnabled",
				"provider",
				"showSubscriptionContext"
			},
			firstAccount.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());
		Assert.Equal("Claude", firstAccount.GetProperty("provider").GetString());
		Assert.True(firstAccount.GetProperty("hasAcceptedClaudeQuotaRisk").GetBoolean());
		Assert.False(
			root.GetProperty("accounts")[1]
				.GetProperty("hasAcceptedClaudeQuotaRisk")
				.GetBoolean());
		Assert.Equal(7, root.GetProperty("schemaVersion").GetInt32());

		string normalizedJson = json.ToLowerInvariant();
		Assert.DoesNotContain("secret", normalizedJson);
		Assert.DoesNotContain("token", normalizedJson);
		Assert.DoesNotContain("credential", normalizedJson);
		Assert.DoesNotContain("cookie", normalizedJson);
	}

	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsProviderAccountIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		JsonAccountProfileStore store = new(filePath);

		await store.LoadAsync();
		await store.SaveAsync(new[] { account });
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));

		Assert.Equal(account, Assert.Single(result.Accounts));
		Assert.Equal(
			"person@example.com",
			document.RootElement
				.GetProperty("accounts")[0]
				.GetProperty("providerAccountIdentity")
				.GetString());
	}

	[Theory]
	[InlineData(ProviderKind.Claude, "Claude")]
	[InlineData(ProviderKind.Claude, "Claude 2")]
	[InlineData(ProviderKind.Codex, "Codex")]
	[InlineData(ProviderKind.Codex, "Codex 23")]
	[InlineData(ProviderKind.Codex, "Codex 999999999999999999999999")]
	[InlineData(ProviderKind.Copilot, "GitHub Copilot")]
	[InlineData(ProviderKind.Copilot, "GitHub Copilot 3")]
	[InlineData(ProviderKind.Antigravity, "Antigravity")]
	[InlineData(ProviderKind.Antigravity, "Antigravity 4")]
	public async Task LoadAsync_WithSchemaFourAutoGeneratedDisplayName_MigratesToEmpty(
		ProviderKind provider,
		string displayName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 4,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = provider.ToString(),
					displayName,
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.Equal(string.Empty, Assert.Single(result.Accounts).DisplayName);
		Assert.Equal(document, await File.ReadAllTextAsync(filePath));
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public async Task LoadAsync_WithAutoNamingSchema_MigratesProviderNameToEmpty(
		int schemaVersion)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Claude",
					displayName = "Claude",
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(string.Empty, Assert.Single(result.Accounts).DisplayName);
	}

	[Theory]
	[InlineData("claude")]
	[InlineData("Claude 1")]
	[InlineData("Claude 02")]
	[InlineData("Claude +2")]
	[InlineData("Claude 工作")]
	public async Task LoadAsync_WithSchemaFourCustomDisplayName_PreservesName(
		string displayName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 4,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Claude",
					displayName,
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(displayName, Assert.Single(result.Accounts).DisplayName);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaFourClaudeConsent_PreservesConsent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 4,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Claude",
					displayName = "私人帳號",
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = true
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.True(Assert.Single(result.Accounts).HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaFiveExactProviderName_PreservesExplicitNickname()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 5,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Claude",
					displayName = "Claude",
					isEnabled = true,
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal("Claude", Assert.Single(result.Accounts).DisplayName);
	}

	[Theory]
	[InlineData("")]
	[InlineData("\"displayName\": null,")]
	public async Task LoadAsync_WithSchemaFiveMissingOrNullDisplayName_QuarantinesOriginal(
		string displayNameProperty)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		Guid accountId = Guid.NewGuid();
		string invalidDocument = $$"""
			{
			  "schemaVersion": 5,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      {{displayNameProperty}}
			      "isEnabled": true,
			      "hasAcceptedClaudeQuotaRisk": false
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, invalidDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(
			invalidDocument,
			await File.ReadAllTextAsync(result.RecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WithPreConsentSchema_DefaultsClaudeConsentToFalse()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid accountId = Guid.NewGuid();
		string legacyDocument = $$"""
			{
			  "schemaVersion": 3,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true,
			      "hasAcceptedClaudeQuotaRisk": true
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, legacyDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.False(Assert.Single(result.Accounts).HasAcceptedClaudeQuotaRisk);
		Assert.Equal(legacyDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaFourMissingConsent_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid accountId = Guid.NewGuid();
		string invalidDocument = $$"""
			{
			  "schemaVersion": 4,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, invalidDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(
			invalidDocument,
			await File.ReadAllTextAsync(result.RecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaSevenMissingSubscriptionContext_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid accountId = Guid.NewGuid();
		string invalidDocument = $$"""
			{
			  "schemaVersion": 7,
			  "accounts": [
			    {
			      "id": "{{accountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true,
			      "hasAcceptedClaudeQuotaRisk": false
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, invalidDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.RecoveryPath);
	}

	[Fact]
	public async Task SaveAsync_WithDuplicateProviderIdentity_RejectsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();
		AccountProfile[] accounts =
		{
			new(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"First",
				ProviderAccountIdentity: "person@example.com"),
			new(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"Second",
				ProviderAccountIdentity: "PERSON@example.com")
		};

		await Assert.ThrowsAsync<ArgumentException>(
			() => store.SaveAsync(accounts));

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithSingleGrokAccount_RoundTripsSchemaSeven()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: "11111111111111111111111111111111");
		JsonAccountProfileStore store = new(filePath);

		await store.LoadAsync();
		await store.SaveAsync([account]);
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));

		Assert.Equal(account, Assert.Single(result.Accounts));
		Assert.Equal(
			7,
			document.RootElement.GetProperty("schemaVersion").GetInt32());
	}

	[Fact]
	public async Task SaveAsync_WithRawGrokIdentity_RejectsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => store.SaveAsync(
			[
				new AccountProfile(
					Guid.NewGuid(),
					ProviderKind.Grok,
					"Grok",
					ProviderAccountIdentity: "person@example.com")
			]));

		Assert.Contains("Grok", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task LoadAsync_WithRawGrokIdentity_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 6,
			accounts = new[]
			{
				new
				{
					id = Guid.NewGuid(),
					provider = "Grok",
					displayName = "Grok",
					isEnabled = true,
					providerAccountIdentity = "person@example.com",
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.RecoveryPath);
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithTwoGrokAccounts_RoundTripsBothCards()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		AccountProfile[] accounts =
		[
			new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Grok,
				"First",
				ProviderAccountIdentity: "11111111111111111111111111111111"),
			new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Grok,
				"Second",
				ProviderAccountIdentity: "22222222222222222222222222222222")
		];
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();

		await store.SaveAsync(accounts);
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.Equal(accounts, result.Accounts);
	}

	[Fact]
	public async Task LoadAsync_WithTwoGrokAccounts_LoadsBothCards()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string document = JsonSerializer.Serialize(new
		{
			schemaVersion = 6,
			accounts = new[]
			{
				new
				{
					id = firstAccountId,
					provider = "Grok",
					displayName = "First",
					isEnabled = true,
					providerAccountIdentity =
						"11111111111111111111111111111111",
					hasAcceptedClaudeQuotaRisk = false
				},
				new
				{
					id = secondAccountId,
					provider = "Grok",
					displayName = "Second",
					isEnabled = true,
					providerAccountIdentity =
						"22222222222222222222222222222222",
					hasAcceptedClaudeQuotaRisk = false
				}
			}
		});
		await File.WriteAllTextAsync(filePath, document);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.Collection(
			result.Accounts,
			firstAccount =>
			{
				Assert.Equal(firstAccountId, firstAccount.Id);
				Assert.Equal(ProviderKind.Grok, firstAccount.Provider);
				Assert.Equal("First", firstAccount.DisplayName);
				Assert.Equal(
					"11111111111111111111111111111111",
					firstAccount.ProviderAccountIdentity);
				Assert.False(firstAccount.ShowSubscriptionContext);
			},
			secondAccount =>
			{
				Assert.Equal(secondAccountId, secondAccount.Id);
				Assert.Equal(ProviderKind.Grok, secondAccount.Provider);
				Assert.Equal("Second", secondAccount.DisplayName);
				Assert.Equal(
					"22222222222222222222222222222222",
					secondAccount.ProviderAccountIdentity);
				Assert.False(secondAccount.ShowSubscriptionContext);
			});
		Assert.Null(result.RecoveryPath);
	}

	[Fact]
	public async Task LoadAsync_WithLegacySchema_UsesLegacyDisplayOrderUntilSaved()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		Guid codexAccountId = Guid.NewGuid();
		Guid secondClaudeAccountId = Guid.NewGuid();
		Guid firstClaudeAccountId = Guid.NewGuid();
		string legacyDocument = $$"""
			{
			  "schemaVersion": 1,
			  "accounts": [
			    {
			      "id": "{{codexAccountId}}",
			      "provider": "Codex",
			      "displayName": "Zeta",
			      "isEnabled": true
			    },
			    {
			      "id": "{{secondClaudeAccountId}}",
			      "provider": "Claude",
			      "displayName": "Beta",
			      "isEnabled": true
			    },
			    {
			      "id": "{{firstClaudeAccountId}}",
			      "provider": "Claude",
			      "displayName": "Alpha",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, legacyDocument);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(
			new[] { firstClaudeAccountId, secondClaudeAccountId, codexAccountId },
			result.Accounts.Select(account => account.Id));
		Assert.Equal(legacyDocument, await File.ReadAllTextAsync(filePath));

		await store.SaveAsync(result.Accounts);

		using JsonDocument migratedDocument = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));
		Assert.Equal(
			7,
			migratedDocument.RootElement.GetProperty("schemaVersion").GetInt32());
		AccountProfileLoadResult migratedResult =
			await new JsonAccountProfileStore(filePath).LoadAsync();
		Assert.Equal(
			new[] { firstClaudeAccountId, secondClaudeAccountId, codexAccountId },
			migratedResult.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaOneProviderNames_PreservesNamesAndLegacyOrder()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		Guid secondAccountId = Guid.NewGuid();
		Guid firstAccountId = Guid.NewGuid();
		string legacyDocument = $$"""
			{
			  "schemaVersion": 1,
			  "accounts": [
			    {
			      "id": "{{secondAccountId}}",
			      "provider": "Claude",
			      "displayName": "Claude 2",
			      "isEnabled": true
			    },
			    {
			      "id": "{{firstAccountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true
			    }
			  ]
			}
			""";
		await File.WriteAllTextAsync(filePath, legacyDocument);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(
			new[] { firstAccountId, secondAccountId },
			result.Accounts.Select(account => account.Id));
		Assert.Equal(
			new[] { "Claude", "Claude 2" },
			result.Accounts.Select(account => account.DisplayName));
	}

	[Fact]
	public async Task SaveAsync_WhenFileExists_AtomicallyReplacesCompleteSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"私人帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"工作帳號");
		await store.LoadAsync();
		await store.SaveAsync(new[] { firstAccount, secondAccount });
		AccountProfile updatedAccount = firstAccount with
		{
			DisplayName = "私人帳號（已更新）",
			IsEnabled = false
		};

		await store.SaveAsync(new[] { updatedAccount });
		AccountProfileLoadResult result = await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(new[] { updatedAccount }, result.Accounts);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync($"{filePath}.bak"));
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task SaveAsync_WhenMainCommitsButBackupUpdateFails_TracksCommittedFingerprintForRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		JsonAccountProfileStore store = new(filePath);
		AccountProfile originalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"原始帳號");
		AccountProfile updatedAccount = originalAccount with
		{
			DisplayName = "更新帳號"
		};
		await store.SaveAsync(new[] { originalAccount });
		await using (FileStream backupLock = new(
			backupPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			AccountProfileStoreException exception =
				await Assert.ThrowsAsync<AccountProfileStoreException>(
					() => store.SaveAsync(new[] { updatedAccount }));

			Assert.True(exception.HasCommittedChanges);
			Assert.Contains("帳號設定已套用", exception.Message);
			Assert.Contains("備份更新失敗", exception.Message);
			Assert.Contains("再次儲存帳號設定", exception.Message);
			using JsonDocument committedDocument = JsonDocument.Parse(
				await File.ReadAllTextAsync(filePath));
			Assert.Equal(
				"更新帳號",
				committedDocument.RootElement
					.GetProperty("accounts")[0]
					.GetProperty("displayName")
					.GetString());
		}

		await store.SaveAsync(new[] { updatedAccount });
		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(new[] { updatedAccount }, result.Accounts);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync(backupPath));
	}

	[Fact]
	public async Task LoadAsync_WhenBackupStayedStaleAfterPartialCommit_RepairsBeforeFutureRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		JsonAccountProfileStore store = new(filePath);
		AccountProfile originalAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"原始帳號");
		AccountProfile updatedAccount = originalAccount with
		{
			DisplayName = "更新帳號"
		};
		await store.SaveAsync(new[] { originalAccount });
		byte[] originalBackupBytes = await File.ReadAllBytesAsync(backupPath);

		await using (FileStream backupLock = new(
			backupPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			AccountProfileStoreException exception =
				await Assert.ThrowsAsync<AccountProfileStoreException>(
					() => store.SaveAsync(new[] { updatedAccount }));

			Assert.True(exception.HasCommittedChanges);
			Assert.Equal(originalBackupBytes, await File.ReadAllBytesAsync(backupPath));
			AccountProfileLoadResult unavailableBackupResult =
				await new JsonAccountProfileStore(filePath).LoadAsync();
			Assert.Equal(AccountProfileLoadStatus.Loaded, unavailableBackupResult.Status);
			Assert.True(unavailableBackupResult.CanSave);
			Assert.NotNull(unavailableBackupResult.Message);
			Assert.Contains("無法建立或修復備份", unavailableBackupResult.Message);
			Assert.Equal(new[] { updatedAccount }, unavailableBackupResult.Accounts);
		}

		AccountProfileLoadResult repairedResult =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, repairedResult.Status);
		Assert.True(repairedResult.CanSave);
		Assert.Null(repairedResult.Message);
		Assert.Equal(new[] { updatedAccount }, repairedResult.Accounts);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync(backupPath));

		await File.WriteAllTextAsync(filePath, "{ corrupt latest primary");
		AccountProfileLoadResult recoveredResult =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, recoveredResult.Status);
		Assert.True(recoveredResult.CanSave);
		Assert.Equal(new[] { updatedAccount }, recoveredResult.Accounts);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync(backupPath));
	}

	[Fact]
	public async Task SaveAsync_WhenParentDirectoryIsMissing_CreatesDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"nested",
			"settings",
			"accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號");

		await store.SaveAsync(new[] { account });
		AccountProfileLoadResult result = await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(new[] { account }, result.Accounts);
	}

	[Fact]
	public async Task LoadAsync_WhenJsonIsCorrupt_QuarantinesOriginalAndAllowsFreshSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		byte[] originalBytes = "{ this is not json"u8.ToArray();
		await File.WriteAllBytesAsync(filePath, originalBytes);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.False(File.Exists(filePath));
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(result.RecoveryPath));

		await store.SaveAsync(Array.Empty<AccountProfile>());
		Assert.True(File.Exists(filePath));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryIsCorrupt_RestoresValidatedBackupWithStableAccountIds()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		JsonAccountProfileStore store = new(filePath);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"私人帳號");
		await store.SaveAsync(new[] { account });
		byte[] backupBytes = await File.ReadAllBytesAsync(backupPath);
		byte[] corruptBytes = "{ this is not json"u8.ToArray();
		await File.WriteAllBytesAsync(filePath, corruptBytes);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Equal(new[] { account }, result.Accounts);
		Assert.Equal(account.Id, Assert.Single(result.Accounts).Id);
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(result.RecoveryPath));
		Assert.Equal(backupBytes, await File.ReadAllBytesAsync(filePath));
		Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryIsCorruptAfterAddingAccount_RestoresLatestBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號");
		AccountProfile addedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"新增帳號");
		await store.SaveAsync(new[] { firstAccount });
		await store.SaveAsync(new[] { firstAccount, addedAccount });
		await File.WriteAllTextAsync(filePath, "{ corrupt latest primary");

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Equal(new[] { firstAccount, addedAccount }, result.Accounts);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync($"{filePath}.bak"));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryIsCorruptAfterDeletingAccount_RestoresLatestBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		AccountProfile retainedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"保留帳號");
		AccountProfile deletedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"刪除帳號");
		await store.SaveAsync(new[] { retainedAccount, deletedAccount });
		await store.SaveAsync(new[] { retainedAccount });
		await File.WriteAllTextAsync(filePath, "{ corrupt latest primary");

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Equal(new[] { retainedAccount }, result.Accounts);
		Assert.DoesNotContain(
			result.Accounts,
			account => account.Id == deletedAccount.Id);
		Assert.Equal(
			await File.ReadAllBytesAsync(filePath),
			await File.ReadAllBytesAsync($"{filePath}.bak"));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryIsMissing_RestoresValidatedBackupWithStableAccountIds()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		JsonAccountProfileStore store = new(filePath);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號");
		await store.SaveAsync(new[] { account });
		byte[] backupBytes = await File.ReadAllBytesAsync(backupPath);
		File.Delete(filePath);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Equal(new[] { account }, result.Accounts);
		Assert.Equal(account.Id, Assert.Single(result.Accounts).Id);
		Assert.Null(result.RecoveryPath);
		Assert.Equal(backupBytes, await File.ReadAllBytesAsync(filePath));
		Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryAndBackupAreCorrupt_QuarantinesBothAndUsesEmptySettings()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		byte[] corruptPrimaryBytes = "{ corrupt primary"u8.ToArray();
		byte[] corruptBackupBytes = "{ corrupt backup"u8.ToArray();
		await File.WriteAllBytesAsync(filePath, corruptPrimaryBytes);
		await File.WriteAllBytesAsync(backupPath, corruptBackupBytes);

		AccountProfileLoadResult result =
			await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.Message);
		Assert.Contains("備份", result.Message);
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists(backupPath));
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(corruptPrimaryBytes, await File.ReadAllBytesAsync(result.RecoveryPath));
		string backupRecoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"accounts.json.corrupt-*.bak",
			SearchOption.TopDirectoryOnly));
		Assert.Equal(corruptBackupBytes, await File.ReadAllBytesAsync(backupRecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryIsCorruptAndBackupIsNewer_RestoresBackupAndBlocksSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		byte[] corruptPrimaryBytes = "{ corrupt primary"u8.ToArray();
		const string futureDocument = """
			{
			  "schemaVersion": 8,
			  "accounts": [],
			  "futureField": true
			}
			""";
		await File.WriteAllBytesAsync(filePath, corruptPrimaryBytes);
		await File.WriteAllTextAsync(backupPath, futureDocument);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.UnsupportedVersion, result.Status);
		Assert.False(result.CanSave);
		Assert.Empty(result.Accounts);
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal(corruptPrimaryBytes, await File.ReadAllBytesAsync(result.RecoveryPath));
		Assert.Equal(futureDocument, await File.ReadAllTextAsync(filePath));
		Assert.Equal(futureDocument, await File.ReadAllTextAsync(backupPath));
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(Array.Empty<AccountProfile>()));
		Assert.Equal(futureDocument, await File.ReadAllTextAsync(filePath));
		Assert.Equal(futureDocument, await File.ReadAllTextAsync(backupPath));
	}

	[Fact]
	public async Task LoadAsync_WhenRootIsNotAnObject_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(filePath, "[]");
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.NotNull(result.RecoveryPath);
		Assert.Equal("[]", await File.ReadAllTextAsync(result.RecoveryPath));
	}

	[Fact]
	public async Task LoadAsync_WhenAccountItemIsNull_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(
			filePath,
			"{\"schemaVersion\":1,\"accounts\":[null]}");
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.NotNull(result.RecoveryPath);
	}

	[Theory]
	[InlineData("{\"schemaVersion\":1,\"accounts\":[{\"id\":\"b54c119b-d404-4a13-bb6b-63f1bbd7788a\",\"displayName\":\"帳號\",\"isEnabled\":true}]}")]
	[InlineData("{\"schemaVersion\":1,\"accounts\":[{\"id\":\"b54c119b-d404-4a13-bb6b-63f1bbd7788a\",\"provider\":\"Claude\",\"displayName\":\"帳號\"}]}")]
	public async Task LoadAsync_WhenRequiredAccountFieldIsMissing_QuarantinesOriginal(
		string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(filePath, json);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
		Assert.NotNull(result.RecoveryPath);
	}

	[Fact]
	public async Task LoadAsync_WhenSchemaVersionIsAString_QuarantinesOriginal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(
			filePath,
			"{\"schemaVersion\":\"1\",\"accounts\":[]}");
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.RecoveredCorruptFile, result.Status);
		Assert.True(result.CanSave);
	}

	[Fact]
	public async Task LoadAsync_WhenSchemaIsNewer_BlocksSaveWithoutChangingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		const string futureDocument = """
			{
			  "schemaVersion": 8,
			  "accounts": [],
			  "futureField": true
			}
			""";
		await File.WriteAllTextAsync(filePath, futureDocument);
		JsonAccountProfileStore store = new(filePath);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.UnsupportedVersion, result.Status);
		Assert.False(result.CanSave);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(Array.Empty<AccountProfile>()));
		Assert.Equal(futureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveAsync_WithoutExplicitLoad_DoesNotOverwriteNewerSchema()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		const string futureDocument = """
			{
			  "schemaVersion": 8,
			  "accounts": []
			}
			""";
		await File.WriteAllTextAsync(filePath, futureDocument);
		JsonAccountProfileStore store = new(filePath);

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(Array.Empty<AccountProfile>()));

		Assert.Equal(futureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveAsync_WhenBackupHasNewerSchema_DoesNotOverwriteEitherFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		JsonAccountProfileStore seedStore = new(filePath);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號");
		await seedStore.SaveAsync(new[] { account });
		byte[] primaryBytes = await File.ReadAllBytesAsync(filePath);
		const string futureBackup = """
			{
			  "schemaVersion": 8,
			  "accounts": [],
			  "futureField": true
			}
			""";
		await File.WriteAllTextAsync(backupPath, futureBackup);
		JsonAccountProfileStore store = new(filePath);
		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.False(result.CanSave);
		Assert.NotNull(result.Message);
		Assert.Contains("較新版本", result.Message);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(result.Accounts));

		Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(filePath));
		Assert.Equal(futureBackup, await File.ReadAllTextAsync(backupPath));
	}

	[Fact]
	public async Task SaveAsync_WhenFileChangesAfterLoad_RejectsStaleSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore store = new(filePath);
		await store.LoadAsync();
		const string futureDocument = """
			{
			  "schemaVersion": 2,
			  "accounts": []
			}
			""";
		await File.WriteAllTextAsync(filePath, futureDocument);

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(Array.Empty<AccountProfile>()));

		Assert.Equal(futureDocument, await File.ReadAllTextAsync(filePath));
	}

	[Fact]
	public async Task SaveAsync_WithTwoStoreInstances_RejectsSecondStaleWriter()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = System.IO.Path.Combine(temporaryDirectory.Path, "accounts.json");
		JsonAccountProfileStore firstStore = new(filePath);
		JsonAccountProfileStore secondStore = new(filePath);
		await firstStore.LoadAsync();
		await secondStore.LoadAsync();
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個 writer");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第二個 writer");

		await firstStore.SaveAsync(new[] { firstAccount });
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => secondStore.SaveAsync(new[] { secondAccount }));
		AccountProfileLoadResult result = await new JsonAccountProfileStore(filePath).LoadAsync();

		Assert.Equal(new[] { firstAccount }, result.Accounts);
	}

	[Fact]
	public async Task LoadAsync_WhenBackupPathIsDirectory_PreservesAccountsAndBlocksSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		await new JsonAccountProfileStore(filePath).SaveAsync(new[] { account });
		byte[] expectedPrimary = await File.ReadAllBytesAsync(filePath);
		File.Delete(backupPath);
		Directory.CreateDirectory(backupPath);
		string? reportedOperation = null;
		string? reportedSummary = null;
		Exception? reportedException = null;
		JsonAccountProfileStore store = new(
			filePath,
			portableSettingsImportWriteTracker: null,
			reportDiagnostic: (operation, summary, exception) =>
			{
				reportedOperation = operation;
				reportedSummary = summary;
				reportedException = exception;
			});

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
		Assert.False(result.CanSave);
		Assert.Equal(account, Assert.Single(result.Accounts));
		Assert.NotNull(result.Message);
		Assert.Contains("無法讀取備份", result.Message);
		Assert.Equal("account-profile-load", reportedOperation);
		Assert.Equal(
			"stage=backup-validation;result=unavailable",
			reportedSummary);
		Assert.IsType<IOException>(reportedException);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => store.SaveAsync(result.Accounts));
		Assert.Equal(expectedPrimary, await File.ReadAllBytesAsync(filePath));
		Assert.True(Directory.Exists(backupPath));
	}

	[Fact]
	public async Task LoadAsync_WhenBackupPathIsJunction_PreservesAccountsAndBlocksSave()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string backupPath = $"{filePath}.bak";
		string targetDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"backup-target");
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		await new JsonAccountProfileStore(filePath).SaveAsync(new[] { account });
		byte[] expectedPrimary = await File.ReadAllBytesAsync(filePath);
		File.Delete(backupPath);
		Directory.CreateDirectory(targetDirectoryPath);
		await JunctionTestHelper.CreateAsync(backupPath, targetDirectoryPath);

		try
		{
			string? reportedSummary = null;
			JsonAccountProfileStore store = new(
				filePath,
				portableSettingsImportWriteTracker: null,
				reportDiagnostic: (_, summary, _) =>
				{
					reportedSummary = summary;
				});

			AccountProfileLoadResult result = await store.LoadAsync();

			Assert.Equal(AccountProfileLoadStatus.Loaded, result.Status);
			Assert.False(result.CanSave);
			Assert.Equal(account, Assert.Single(result.Accounts));
			Assert.Equal(
				"stage=backup-validation;result=unavailable",
				reportedSummary);
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => store.SaveAsync(result.Accounts));
			Assert.Equal(expectedPrimary, await File.ReadAllBytesAsync(filePath));
			Assert.Empty(Directory.EnumerateFileSystemEntries(targetDirectoryPath));
		}
		finally
		{
			JunctionTestHelper.Delete(backupPath);
		}
	}

	[Fact]
	public async Task LoadAsync_WhenPrimaryReadFails_ReportsExceptionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(
			filePath,
			"{\"schemaVersion\":6,\"accounts\":[]}");
		string? reportedOperation = null;
		string? reportedSummary = null;
		Exception? reportedException = null;
		JsonAccountProfileStore store = new(
			filePath,
			portableSettingsImportWriteTracker: null,
			reportDiagnostic: (operation, summary, exception) =>
			{
				reportedOperation = operation;
				reportedSummary = summary;
				reportedException = exception;
			});
		await using FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);

		AccountProfileLoadResult result = await store.LoadAsync();

		Assert.Equal(AccountProfileLoadStatus.Unavailable, result.Status);
		Assert.False(result.CanSave);
		Assert.Equal("account-profile-load", reportedOperation);
		Assert.Equal(
			"stage=primary-read;result=unavailable",
			reportedSummary);
		Assert.IsType<IOException>(reportedException);
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
			"profiles-junction");
		string targetFilePath = Path.Combine(
			targetDirectoryPath,
			"accounts.json");
		Directory.CreateDirectory(targetDirectoryPath);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		await new JsonAccountProfileStore(targetFilePath).SaveAsync(
			new[] { account });
		byte[] expectedPrimary = await File.ReadAllBytesAsync(targetFilePath);
		byte[] expectedBackup = await File.ReadAllBytesAsync(
			$"{targetFilePath}.bak");
		await JunctionTestHelper.CreateAsync(
			junctionDirectoryPath,
			targetDirectoryPath);

		try
		{
			JsonAccountProfileStore aliasedStore = new(Path.Combine(
				junctionDirectoryPath,
				"accounts.json"));

			AccountProfileLoadResult result = await aliasedStore.LoadAsync();
			Assert.Equal(AccountProfileLoadStatus.Unavailable, result.Status);
			Assert.False(result.CanSave);
			await Assert.ThrowsAsync<AccountProfileStoreException>(() =>
				aliasedStore.SaveAsync(Array.Empty<AccountProfile>()));
			Assert.Equal(
				expectedPrimary,
				await File.ReadAllBytesAsync(targetFilePath));
			Assert.Equal(
				expectedBackup,
				await File.ReadAllBytesAsync($"{targetFilePath}.bak"));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectoryPath);
		}
	}
}
