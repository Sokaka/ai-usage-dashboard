using System.Text;
using System.Text.Json;
using System.ComponentModel;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class PortableSettingsJsonServiceTests
{
	private static readonly Guid FirstAccountId =
		Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid SecondAccountId =
		Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly PortableWidgetPreferences DefaultWidgetPreferences =
		new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			Corner: FloatingWidgetCorner.BottomRight,
			Theme: AppTheme.ClassicBlue,
			IsHeightFollowingCardCount: false);

	[Fact]
	public void RemoteIdentityFallback_WhenNormalizedAccessIsDenied_RetriesWithOpenedName()
	{
		List<uint> queriedFlags = [];

		string resolvedPath = PortableSettingsPathIdentity
			.ResolveFinalNtPathForTesting(
				isConfirmedRemotePath: true,
				flags =>
				{
					queriedFlags.Add(flags);
					return queriedFlags.Count == 1
						? (false, null, 5)
						: (true, @"\Device\Mup\server\share\portable.json", 0);
				});

		Assert.Equal(@"\Device\Mup\server\share\portable.json", resolvedPath);
		Assert.Equal(new uint[] { 0x00000002, 0x0000000A }, queriedFlags);
	}

	[Theory]
	[InlineData(false, 5)]
	[InlineData(true, 3)]
	public void RemoteIdentityFallback_WhenNotEligible_DoesNotRetry(
		bool isConfirmedRemotePath,
		int normalizedErrorCode)
	{
		List<uint> queriedFlags = [];

		IOException exception = Assert.Throws<IOException>(() =>
			PortableSettingsPathIdentity.ResolveFinalNtPathForTesting(
				isConfirmedRemotePath,
				flags =>
				{
					queriedFlags.Add(flags);
					return (false, null, normalizedErrorCode);
				}));

		Assert.Equal(new uint[] { 0x00000002 }, queriedFlags);
		Assert.Equal(
			normalizedErrorCode,
			Assert.IsType<Win32Exception>(exception.InnerException)
				.NativeErrorCode);
	}

	[Fact]
	public void RemoteIdentityFallback_WhenOpenedNameAlsoFails_ReportsSecondError()
	{
		int queryCount = 0;

		IOException exception = Assert.Throws<IOException>(() =>
			PortableSettingsPathIdentity.ResolveFinalNtPathForTesting(
				isConfirmedRemotePath: true,
				_ =>
				{
					queryCount++;
					return (false, null, queryCount == 1 ? 5 : 87);
				}));

		Assert.Equal(2, queryCount);
		Assert.Equal(
			87,
			Assert.IsType<Win32Exception>(exception.InnerException)
				.NativeErrorCode);
	}

	[Fact]
	public void RemoteIdentityDetection_RecognizesUncAndMappedDrives()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.True(PortableSettingsPathIdentity.IsConfirmedRemotePathForTesting(
			@"\\server\share\folder\portable.json",
			_ => throw new InvalidOperationException(
				"UNC paths must not need drive-type lookup.")));
		Assert.True(PortableSettingsPathIdentity.IsConfirmedRemotePathForTesting(
			@"Z:\folder\portable.json",
			rootPath =>
			{
				Assert.Equal(@"Z:\", rootPath);
				return 4;
			}));
		Assert.False(PortableSettingsPathIdentity.IsConfirmedRemotePathForTesting(
			@"C:\folder\portable.json",
			_ => 3));
	}

	[Fact]
	public async Task ExportAndImportAsync_RoundTripsAccountOrderAndPortablePreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Copilot,
					"  工作帳號  ",
					IsEnabled: false,
					ProviderAccountIdentity: "  User@Example.com  ",
					HasAcceptedClaudeQuotaRisk: true),
				new AccountProfile(
					SecondAccountId,
					ProviderKind.Claude,
					"個人帳號",
					IsEnabled: true,
					ProviderAccountIdentity: null,
					HasAcceptedClaudeQuotaRisk: true,
					ShowSubscriptionContext: true)
			],
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			new PortableWidgetPreferences(
				IsWidgetVisible: false,
				IsCollapsed: true,
				IsTopmost: false,
				Corner: FloatingWidgetCorner.TopLeft,
				Theme: AppTheme.Midnight,
				IsHeightFollowingCardCount: true));

		await service.ExportAsync(filePath, snapshot);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		Assert.Equal(UsageSortMode.Automatic, imported.UsageSortMode);
		Assert.Equal(UsageDisplayMode.Remaining, imported.UsageDisplayMode);
		Assert.Equal(snapshot.WidgetPreferences, imported.WidgetPreferences);
		Assert.Equal(
			[FirstAccountId, SecondAccountId],
			imported.Accounts.Select(account => account.Id));
		Assert.Equal("工作帳號", imported.Accounts[0].DisplayName);
		Assert.Null(imported.Accounts[0].ProviderAccountIdentity);
		Assert.False(imported.Accounts[0].IsEnabled);
		Assert.False(imported.Accounts[0].HasAcceptedClaudeQuotaRisk);
		Assert.False(imported.Accounts[0].ShowSubscriptionContext);
		Assert.Equal("個人帳號", imported.Accounts[1].DisplayName);
		Assert.True(imported.Accounts[1].IsEnabled);
		Assert.False(imported.Accounts[1].HasAcceptedClaudeQuotaRisk);
		Assert.True(imported.Accounts[1].ShowSubscriptionContext);
	}

	[Fact]
	public async Task ImportAsync_SchemaFourDefaultsSubscriptionContextToHidden()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string document = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 4,
			  "accounts": [
			    {
			      "id": "{{FirstAccountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true,
			      "providerAccountIdentity": null
			    }
			  ],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used",
			    "isWidgetVisible": true,
			    "isCollapsed": false,
			    "isTopmost": true,
			    "corner": "BottomRight",
			    "theme": "Light"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		Assert.False(Assert.Single(imported.Accounts).ShowSubscriptionContext);
		Assert.Equal(AppTheme.Light, imported.WidgetPreferences?.Theme);
		Assert.Null(imported.WidgetPreferences?.IsHeightFollowingCardCount);
	}

	[Fact]
	public async Task ImportAsync_SchemaFiveRequiresSubscriptionContextSetting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string document = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 5,
			  "accounts": [
			    {
			      "id": "{{FirstAccountId}}",
			      "provider": "Claude",
			      "displayName": "Claude",
			      "isEnabled": true,
			      "providerAccountIdentity": null
			    }
			  ],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used",
			    "isWidgetVisible": true,
			    "isCollapsed": false,
			    "isTopmost": true,
			    "corner": "BottomRight",
			    "theme": "Light"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, document);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => new PortableSettingsJsonService().ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_SchemaFivePreservesWidgetPreferencesWithoutHeightSetting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string Document = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 5,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Automatic",
			    "usageDisplayMode": "Remaining",
			    "isWidgetVisible": false,
			    "isCollapsed": true,
			    "isTopmost": false,
			    "corner": "TopLeft",
			    "theme": "Light"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, Document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		PortableWidgetPreferences preferences =
			Assert.IsType<PortableWidgetPreferences>(imported.WidgetPreferences);
		Assert.Equal(AppTheme.Light, preferences.Theme);
		Assert.Null(preferences.IsHeightFollowingCardCount);
	}

	[Theory]
	[InlineData((int)AppTheme.ClassicBlue)]
	[InlineData((int)AppTheme.Midnight)]
	[InlineData((int)AppTheme.Light)]
	[InlineData((int)AppTheme.Sakura)]
	public async Task ExportAndImportAsync_RoundTripsEveryTheme(int themeValue)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		AppTheme theme = (AppTheme)themeValue;
		PortableSettingsSnapshot snapshot = CreateSnapshot() with
		{
			WidgetPreferences = DefaultWidgetPreferences with { Theme = theme }
		};

		PortableSettingsJsonService service = new();
		await service.ExportAsync(filePath, snapshot);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		Assert.Equal(theme, imported.WidgetPreferences?.Theme);
	}

	[Fact]
	public async Task ExportAsync_WritesExactAllowlistWithoutSecretsOrMachineLocalPreferences()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Antigravity,
					"工作",
					ProviderAccountIdentity: "agy.local-session.v1",
					HasAcceptedClaudeQuotaRisk: true)
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			new PortableWidgetPreferences(
				IsWidgetVisible: false,
				IsCollapsed: true,
				IsTopmost: false,
				Corner: FloatingWidgetCorner.TopLeft,
				Theme: AppTheme.Light,
				IsHeightFollowingCardCount: true));

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;

		Assert.Equal(
			["format", "schemaVersion", "accounts", "preferences"],
			root.EnumerateObject().Select(property => property.Name));
		Assert.Equal(
			"ai-usage-dashboard-settings",
			root.GetProperty("format").GetString());
		Assert.Equal(6, root.GetProperty("schemaVersion").GetInt32());
		JsonElement account = root.GetProperty("accounts")[0];
		Assert.Equal(
			[
				"id",
				"provider",
				"displayName",
				"isEnabled",
				"providerAccountIdentity",
				"showSubscriptionContext"
			],
			account.EnumerateObject().Select(property => property.Name));
		Assert.Equal(JsonValueKind.Null, account.GetProperty(
			"providerAccountIdentity").ValueKind);
		Assert.False(account.GetProperty("showSubscriptionContext").GetBoolean());
		Assert.Equal(
			[
				"usageSortMode",
				"usageDisplayMode",
				"isWidgetVisible",
				"isCollapsed",
				"isTopmost",
				"corner",
				"theme",
				"isHeightFollowingCardCount"
			],
			root.GetProperty("preferences")
				.EnumerateObject()
				.Select(property => property.Name));
		JsonElement preferences = root.GetProperty("preferences");
		Assert.False(preferences.GetProperty("isWidgetVisible").GetBoolean());
		Assert.True(preferences.GetProperty("isCollapsed").GetBoolean());
		Assert.False(preferences.GetProperty("isTopmost").GetBoolean());
		Assert.Equal("TopLeft", preferences.GetProperty("corner").GetString());
		Assert.Equal("Light", preferences.GetProperty("theme").GetString());
		Assert.True(
			preferences.GetProperty("isHeightFollowingCardCount").GetBoolean());
		Assert.DoesNotContain(
			"hasAcceptedClaudeQuotaRisk",
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("cache", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"monitorDeviceName",
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"startupSurface",
			json,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExportAndImportAsync_WithGrokAccount_DropsMachineLocalIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Grok,
					"Grok",
					ProviderAccountIdentity: "grok.machine-local-binding")
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement account = document.RootElement.GetProperty("accounts")[0];
		Assert.Equal("Grok", account.GetProperty("provider").GetString());
		Assert.Equal(
			JsonValueKind.Null,
			account.GetProperty("providerAccountIdentity").ValueKind);
		Assert.Null(Assert.Single(imported.Accounts).ProviderAccountIdentity);
		Assert.DoesNotContain(
			"grok.machine-local-binding",
			json,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportAndImportAsync_WithMultipleCopilotAccounts_PreservesCardsAndDropsIdentities()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string FirstIdentity = "github:copilot:github.com/first-user";
		const string SecondIdentity = "github:copilot:github.com/second-user";
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Copilot,
					"Copilot 工作帳號",
					ProviderAccountIdentity: FirstIdentity),
				new AccountProfile(
					SecondAccountId,
					ProviderKind.Copilot,
					"Copilot 個人帳號",
					ProviderAccountIdentity: SecondIdentity)
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement[] accountDocuments = document.RootElement
			.GetProperty("accounts")
			.EnumerateArray()
			.ToArray();
		Assert.Equal(2, accountDocuments.Length);
		Assert.All(accountDocuments, account => Assert.Equal(
			JsonValueKind.Null,
			account.GetProperty("providerAccountIdentity").ValueKind));
		Assert.Equal(
			[FirstAccountId, SecondAccountId],
			imported.Accounts.Select(account => account.Id));
		Assert.All(imported.Accounts, account =>
			Assert.Null(account.ProviderAccountIdentity));
		Assert.DoesNotContain(FirstIdentity, json, StringComparison.Ordinal);
		Assert.DoesNotContain(SecondIdentity, json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportAndImportAsync_WithClaudeOpaqueBinding_DropsMachineLocalIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string opaqueBindingIdentity =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab").ToString("N");
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Claude,
					"Claude Team",
					ProviderAccountIdentity: opaqueBindingIdentity)
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement account = document.RootElement.GetProperty("accounts")[0];
		Assert.Equal("Claude", account.GetProperty("provider").GetString());
		Assert.Equal(
			JsonValueKind.Null,
			account.GetProperty("providerAccountIdentity").ValueKind);
		Assert.Null(Assert.Single(imported.Accounts).ProviderAccountIdentity);
		Assert.DoesNotContain(
			opaqueBindingIdentity,
			json,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ImportAsync_WithSerializedClaudeOpaqueBinding_ResetsIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string opaqueBindingIdentity =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab").ToString("N");
		string document = CreateDocument(CreateAccountJson(
			FirstAccountId,
			nameof(ProviderKind.Claude),
			"Claude Team",
			opaqueBindingIdentity));
		await File.WriteAllTextAsync(filePath, document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		AccountProfile account = Assert.Single(imported.Accounts);
		Assert.Equal(ProviderKind.Claude, account.Provider);
		Assert.Null(account.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ExportAndImportAsync_WithCodexOpaqueBinding_DropsMachineLocalIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string opaqueBindingIdentity =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab").ToString("N");
		PortableSettingsJsonService service = new();
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Codex,
					"Codex Team",
					ProviderAccountIdentity: opaqueBindingIdentity)
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement account = document.RootElement.GetProperty("accounts")[0];
		Assert.Equal("Codex", account.GetProperty("provider").GetString());
		Assert.Equal(
			JsonValueKind.Null,
			account.GetProperty("providerAccountIdentity").ValueKind);
		Assert.Null(Assert.Single(imported.Accounts).ProviderAccountIdentity);
		Assert.DoesNotContain(
			opaqueBindingIdentity,
			json,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ImportAsync_WithSerializedCodexOpaqueBinding_ResetsIdentity()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string opaqueBindingIdentity =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab").ToString("N");
		string document = CreateDocument(CreateAccountJson(
			FirstAccountId,
			nameof(ProviderKind.Codex),
			"Codex Team",
			opaqueBindingIdentity));
		await File.WriteAllTextAsync(filePath, document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		AccountProfile account = Assert.Single(imported.Accounts);
		Assert.Equal(ProviderKind.Codex, account.Provider);
		Assert.Null(account.ProviderAccountIdentity);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public async Task ImportAsync_LegacySchemasResetCodexIdentityWithoutWidgetPreferences(
		int schemaVersion)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string document = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": {{schemaVersion}},
			  "accounts": [
			    {
			      "id": "{{FirstAccountId}}",
			      "provider": "Codex",
			      "displayName": "Legacy",
			      "isEnabled": true,
			      "providerAccountIdentity": "person@example.com"
			    }
			  ],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Remaining"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		AccountProfile account = Assert.Single(imported.Accounts);
		Assert.Equal(ProviderKind.Codex, account.Provider);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Null(imported.WidgetPreferences);
	}

	[Fact]
	public async Task ImportAsync_SchemaThreePreservesWidgetPreferencesWithoutTheme()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string Document = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 3,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Automatic",
			    "usageDisplayMode": "Remaining",
			    "isWidgetVisible": false,
			    "isCollapsed": true,
			    "isTopmost": false,
			    "corner": "TopLeft"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, Document);

		PortableSettingsSnapshot imported =
			await new PortableSettingsJsonService().ImportAsync(filePath);

		Assert.Equal(UsageSortMode.Automatic, imported.UsageSortMode);
		Assert.Equal(UsageDisplayMode.Remaining, imported.UsageDisplayMode);
		PortableWidgetPreferences preferences = Assert.IsType<PortableWidgetPreferences>(
			imported.WidgetPreferences);
		Assert.False(preferences.IsWidgetVisible);
		Assert.True(preferences.IsCollapsed);
		Assert.False(preferences.IsTopmost);
		Assert.Equal(FloatingWidgetCorner.TopLeft, preferences.Corner);
		Assert.Null(preferences.Theme);
		Assert.Null(preferences.IsHeightFollowingCardCount);
	}

	[Fact]
	public async Task ExportAndImportAsync_WithTwoGrokAccounts_RoundTripsBothCards()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		PortableSettingsSnapshot snapshot = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Grok,
					"First",
					ProviderAccountIdentity:
						"11111111111111111111111111111111"),
				new AccountProfile(
					SecondAccountId,
					ProviderKind.Grok,
					"Second",
					ProviderAccountIdentity:
						"22222222222222222222222222222222")
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);
		PortableSettingsJsonService service = new();

		await service.ExportAsync(filePath, snapshot);
		string json = await File.ReadAllTextAsync(filePath);
		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		Assert.Collection(
			imported.Accounts,
			firstAccount =>
			{
				Assert.Equal(FirstAccountId, firstAccount.Id);
				Assert.Equal(ProviderKind.Grok, firstAccount.Provider);
				Assert.Equal("First", firstAccount.DisplayName);
				Assert.Null(firstAccount.ProviderAccountIdentity);
			},
			secondAccount =>
			{
				Assert.Equal(SecondAccountId, secondAccount.Id);
				Assert.Equal(ProviderKind.Grok, secondAccount.Provider);
				Assert.Equal("Second", secondAccount.DisplayName);
				Assert.Null(secondAccount.ProviderAccountIdentity);
			});
		Assert.DoesNotContain(
			"11111111111111111111111111111111",
			json,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"22222222222222222222222222222222",
			json,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportAsync_RejectsCanonicalAppManagedDataDestinations()
	{
		string uniqueParentDirectoryName =
			$".portable-settings-managed-{Guid.NewGuid():N}";
		string relativeManagedDataDirectoryPath = Path.Combine(
			uniqueParentDirectoryName,
			"AiUsageDashboard");
		string absoluteManagedDataDirectoryPath = Path.GetFullPath(
			relativeManagedDataDirectoryPath);
		PortableSettingsJsonService service = new(
			new PortableSettingsFileOperations(),
			relativeManagedDataDirectoryPath);
		string[] protectedDestinations =
		[
			Path.Combine(relativeManagedDataDirectoryPath, "accounts.json"),
			Path.Combine(
				absoluteManagedDataDirectoryPath.ToUpperInvariant(),
				"ACCOUNTS.JSON.BAK"),
			Path.Combine(
				absoluteManagedDataDirectoryPath,
				"preferences.json"),
			Path.Combine(
				absoluteManagedDataDirectoryPath,
				"portable-settings-import-transaction-v1",
				"target-0.bak"),
			absoluteManagedDataDirectoryPath
		];

		foreach (string destinationPath in protectedDestinations)
		{
			PortableSettingsException exception =
				await Assert.ThrowsAsync<PortableSettingsException>(
					() => service.ExportAsync(destinationPath, CreateSnapshot()));

			Assert.Contains("AI Usage 管理", exception.Message, StringComparison.Ordinal);
		}

		Assert.False(Directory.Exists(Path.GetFullPath(uniqueParentDirectoryName)));
	}

	[Fact]
	public async Task ExportAsync_RejectsExtendedDeviceAliasToManagedDataDirectory()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string managedDataDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard");
		string ordinaryDestinationPath = Path.Combine(
			managedDataDirectoryPath,
			"accounts.json");
		string extendedDestinationPath = @"\\?\" + ordinaryDestinationPath;
		PortableSettingsJsonService service = new(
			new PortableSettingsFileOperations(),
			managedDataDirectoryPath);

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ExportAsync(
					extendedDestinationPath,
					CreateSnapshot()));

		Assert.Contains("裝置命名空間", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(ordinaryDestinationPath));
		Assert.False(Directory.Exists(managedDataDirectoryPath));
	}

	[Fact]
	public async Task ExportAsync_AllowsSiblingWithManagedDirectoryNamePrefix()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string managedDataDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard");
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard-export",
			"portable.json");
		PortableSettingsJsonService service = new(
			new PortableSettingsFileOperations(),
			managedDataDirectoryPath);

		await service.ExportAsync(destinationPath, CreateSnapshot());

		Assert.True(File.Exists(destinationPath));
	}

	[Fact]
	public async Task ExportAsync_RejectsJunctionAliasToManagedDataDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string managedDataDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"managed");
		string junctionPath = Path.Combine(
			temporaryDirectory.Path,
			"export-alias");
		string protectedSettingsPath = Path.Combine(
			managedDataDirectoryPath,
			"accounts.json");
		Directory.CreateDirectory(managedDataDirectoryPath);
		await File.WriteAllTextAsync(protectedSettingsPath, "live-settings");
		await CreateJunctionAsync(junctionPath, managedDataDirectoryPath);

		try
		{
			PortableSettingsJsonService service = new(
				new PortableSettingsFileOperations(),
				managedDataDirectoryPath);

			PortableSettingsException exception =
				await Assert.ThrowsAsync<PortableSettingsException>(
					() => service.ExportAsync(
						Path.Combine(junctionPath, "accounts.json"),
						CreateSnapshot()));

			Assert.Contains("連結的資料夾", exception.Message, StringComparison.Ordinal);
			Assert.Equal(
				"live-settings",
				await File.ReadAllTextAsync(protectedSettingsPath));
			Assert.Empty(
				Directory.GetFiles(managedDataDirectoryPath, "*.tmp"));
		}
		finally
		{
			Directory.Delete(junctionPath, recursive: false);
		}
	}

	[Fact]
	public async Task ExportAsync_RejectsPhysicalBackingPathWhenManagedDirectoryIsJunction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string backingDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"managed-backing");
		string managedDataDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"managed-junction");
		string protectedSettingsPath = Path.Combine(
			backingDirectoryPath,
			"accounts.json");
		Directory.CreateDirectory(backingDirectoryPath);
		await File.WriteAllTextAsync(protectedSettingsPath, "live-settings");
		await CreateJunctionAsync(
			managedDataDirectoryPath,
			backingDirectoryPath);

		try
		{
			PortableSettingsJsonService service = new(
				new PortableSettingsFileOperations(),
				managedDataDirectoryPath);

			PortableSettingsException exception =
				await Assert.ThrowsAsync<PortableSettingsException>(
					() => service.ExportAsync(
						protectedSettingsPath,
						CreateSnapshot()));

			Assert.Contains("AI Usage 管理", exception.Message, StringComparison.Ordinal);
			Assert.Equal(
				"live-settings",
				await File.ReadAllTextAsync(protectedSettingsPath));
			Assert.Empty(Directory.GetFiles(backingDirectoryPath, "*.tmp"));
		}
		finally
		{
			Directory.Delete(managedDataDirectoryPath, recursive: false);
		}
	}

	[Fact]
	public async Task ExportAsync_RejectsPhysicalBackingPathWhenManagedAncestorIsJunction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string backingParentDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"managed-parent-backing");
		string managedParentJunctionPath = Path.Combine(
			temporaryDirectory.Path,
			"managed-parent-junction");
		string physicalManagedDataDirectoryPath = Path.Combine(
			backingParentDirectoryPath,
			"AiUsageDashboard");
		string managedDataDirectoryPath = Path.Combine(
			managedParentJunctionPath,
			"AiUsageDashboard");
		string protectedSettingsPath = Path.Combine(
			physicalManagedDataDirectoryPath,
			"preferences.json");
		Directory.CreateDirectory(physicalManagedDataDirectoryPath);
		await File.WriteAllTextAsync(protectedSettingsPath, "live-preferences");
		await CreateJunctionAsync(
			managedParentJunctionPath,
			backingParentDirectoryPath);

		try
		{
			PortableSettingsJsonService service = new(
				new PortableSettingsFileOperations(),
				managedDataDirectoryPath);

			PortableSettingsException exception =
				await Assert.ThrowsAsync<PortableSettingsException>(
					() => service.ExportAsync(
						protectedSettingsPath,
						CreateSnapshot()));

			Assert.Contains("AI Usage 管理", exception.Message, StringComparison.Ordinal);
			Assert.Equal(
				"live-preferences",
				await File.ReadAllTextAsync(protectedSettingsPath));
			Assert.Empty(
				Directory.GetFiles(physicalManagedDataDirectoryPath, "*.tmp"));
		}
		finally
		{
			Directory.Delete(managedParentJunctionPath, recursive: false);
		}
	}

	[Fact]
	public async Task ExportAsync_RejectsHardLinkAliasToManagedFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string managedDataDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"managed");
		string protectedSettingsPath = Path.Combine(
			managedDataDirectoryPath,
			"accounts.json");
		string hardLinkPath = Path.Combine(
			temporaryDirectory.Path,
			"portable.json");
		Directory.CreateDirectory(managedDataDirectoryPath);
		await File.WriteAllTextAsync(protectedSettingsPath, "live-settings");
		await CreateHardLinkAsync(hardLinkPath, protectedSettingsPath);
		PortableSettingsJsonService service = new(
			new PortableSettingsFileOperations(),
			managedDataDirectoryPath);

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ExportAsync(hardLinkPath, CreateSnapshot()));

		Assert.Contains("硬連結", exception.Message, StringComparison.Ordinal);
		Assert.Equal(
			"live-settings",
			await File.ReadAllTextAsync(protectedSettingsPath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Theory]
	[InlineData("not-json")]
	[InlineData("null")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":3,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":3,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":1,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":3,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":0}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":3,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"Center\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":4,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":4,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":4,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\",\"theme\":0}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":4,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\",\"theme\":\"midnight\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":5,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":6,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\",\"theme\":\"ClassicBlue\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":6,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\",\"isWidgetVisible\":true,\"isCollapsed\":false,\"isTopmost\":true,\"corner\":\"BottomRight\",\"theme\":\"ClassicBlue\",\"isHeightFollowingCardCount\":1}}")]
	[InlineData("{\"format\":\"different-format\",\"schemaVersion\":1,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":\"Used\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":1,\"accounts\":[],\"preferences\":{\"usageSortMode\":0,\"usageDisplayMode\":\"Used\"}}")]
	[InlineData("{\"format\":\"ai-usage-dashboard-settings\",\"schemaVersion\":1,\"accounts\":[],\"preferences\":{\"usageSortMode\":\"Manual\",\"usageDisplayMode\":1}}")]
	public async Task ImportAsync_RejectsMalformedFutureAndIntegerEnumDocuments(
		string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		await File.WriteAllTextAsync(filePath, json);
		PortableSettingsJsonService service = new();

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ImportAsync(filePath));

		Assert.NotEmpty(exception.Message);
		Assert.Contains(exception.Message, character => character > 127);
	}

	[Fact]
	public async Task ImportAsync_FutureSchemaWithNewFields_ReportsNewerVersionFirst()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string FutureDocument = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 7,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used"
			  },
			  "futurePreference": true
			}
			""";
		await File.WriteAllTextAsync(filePath, FutureDocument);
		PortableSettingsJsonService service = new();

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ImportAsync(filePath));

		Assert.Contains("較新版本", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("未知", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("monitorDeviceName")]
	[InlineData("startupSurface")]
	[InlineData("theme")]
	[InlineData("apiToken")]
	public async Task ImportAsync_SchemaThreeRejectsNonPortablePreferenceFields(
		string propertyName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string document = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 3,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used",
			    "isWidgetVisible": true,
			    "isCollapsed": false,
			    "isTopmost": true,
			    "corner": "BottomRight",
			    "{{propertyName}}": "must-not-be-imported"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, document);

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => new PortableSettingsJsonService().ImportAsync(filePath));

		Assert.DoesNotContain(
			"must-not-be-imported",
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ImportAsync_SchemaThreeRejectsDuplicateWidgetPreferenceFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string Document = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 3,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used",
			    "isWidgetVisible": true,
			    "isCollapsed": false,
			    "isTopmost": true,
			    "corner": "BottomRight",
			    "corner": "TopLeft"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, Document);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => new PortableSettingsJsonService().ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_SchemaFourRejectsDuplicateThemeFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string Document = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 4,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used",
			    "isWidgetVisible": true,
			    "isCollapsed": false,
			    "isTopmost": true,
			    "corner": "BottomRight",
			    "theme": "Midnight",
			    "theme": "Light"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, Document);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => new PortableSettingsJsonService().ImportAsync(filePath));
	}

	[Fact]
	public void ImportPreview_SummarizesChangesWithoutDisclosingAccountIdentity()
	{
		PortableSettingsSnapshot current = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Claude,
					"舊名稱",
					ProviderAccountIdentity: "private@example.com")
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			new PortableWidgetPreferences(
				IsWidgetVisible: true,
				IsCollapsed: false,
				IsTopmost: true,
				Corner: FloatingWidgetCorner.BottomRight,
				Theme: AppTheme.ClassicBlue));
		PortableSettingsSnapshot imported = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Claude,
					"新名稱",
					IsEnabled: false,
					ProviderAccountIdentity: "other@example.com"),
				new AccountProfile(
					SecondAccountId,
					ProviderKind.Codex,
					"新增")
			],
			UsageSortMode.Automatic,
			UsageDisplayMode.Remaining,
			new PortableWidgetPreferences(
				IsWidgetVisible: false,
				IsCollapsed: true,
				IsTopmost: false,
				Corner: FloatingWidgetCorner.TopLeft,
				Theme: AppTheme.Midnight,
				IsHeightFollowingCardCount: true));

		string preview = FloatingWidgetWindow.CreatePortableSettingsImportPreview(
			current,
			imported);

		Assert.Contains("1 → 2", preview, StringComparison.Ordinal);
		Assert.Contains("新增 1", preview, StringComparison.Ordinal);
		Assert.Contains("更新 1", preview, StringComparison.Ordinal);
		Assert.Contains("手動 → 自動", preview, StringComparison.Ordinal);
		Assert.Contains("已使用 → 剩餘", preview, StringComparison.Ordinal);
		Assert.Contains(
			"顯示、展開、置頂、右下角、固定可用高度 → " +
				"隱藏、收合、不置頂、左上角、高度隨卡片數量",
			preview,
			StringComparison.Ordinal);
		Assert.Contains("主題：經典藍 → 曜石黑", preview, StringComparison.Ordinal);
		Assert.Contains(
			"需重新確認 Claude 用量讀取：1 個帳號",
			preview,
			StringComparison.Ordinal);
		Assert.DoesNotContain("private@example.com", preview, StringComparison.Ordinal);
		Assert.DoesNotContain("other@example.com", preview, StringComparison.Ordinal);
	}

	[Fact]
	public void ImportPreview_UsesProviderDisplayName()
	{
		PortableSettingsSnapshot current = new(
			[],
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		PortableSettingsSnapshot imported = new(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Copilot,
					"Copilot")
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used);

		string preview = FloatingWidgetWindow.CreatePortableSettingsImportPreview(
			current,
			imported);

		Assert.Contains("帳號所屬服務：GitHub Copilot 1", preview, StringComparison.Ordinal);
		Assert.Contains("主題：保留目前主題", preview, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ImportAsync_RejectsUnknownAndDuplicateFieldsWithoutEchoingThem()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		const string Sentinel = "DO-NOT-ECHO-THIS-VALUE";
		string unknownFieldDocument = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 1,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used"
			  },
			  "apiToken": "{{Sentinel}}"
			}
			""";
		await File.WriteAllTextAsync(filePath, unknownFieldDocument);
		PortableSettingsJsonService service = new();

		PortableSettingsException unknownException =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ImportAsync(filePath));

		Assert.DoesNotContain(Sentinel, unknownException.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("apiToken", unknownException.Message, StringComparison.Ordinal);

		const string DuplicateFieldDocument = """
			{
			  "format": "ai-usage-dashboard-settings",
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 1,
			  "accounts": [],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, DuplicateFieldDocument);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_RejectsUnknownNestedFieldsAndIntegerProvider()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string unknownAccountField = CreateDocument(
			$$"""
			{
			  "id": "{{FirstAccountId}}",
			  "provider": "Codex",
			  "displayName": "工作",
			  "isEnabled": true,
			  "providerAccountIdentity": null,
			  "refreshToken": "hidden"
			}
			""");
		await File.WriteAllTextAsync(filePath, unknownAccountField);
		PortableSettingsJsonService service = new();

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));

		string integerProvider = CreateDocument(
			$$"""
			{
			  "id": "{{FirstAccountId}}",
			  "provider": 1,
			  "displayName": "工作",
			  "isEnabled": true,
			  "providerAccountIdentity": null
			}
			""");
		await File.WriteAllTextAsync(filePath, integerProvider);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_RejectsOversizedAndOverlyDeepDocuments()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		await File.WriteAllBytesAsync(
			filePath,
			Enumerable.Repeat((byte)' ', (1024 * 1024) + 1).ToArray());
		PortableSettingsJsonService service = new();

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));

		string nestedValue = "[]";

		for (int index = 0; index < 20; index++)
		{
			nestedValue = $"[{nestedValue}]";
		}

		string deepDocument = $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 1,
			  "accounts": {{nestedValue}},
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used"
			  }
			}
			""";
		await File.WriteAllTextAsync(filePath, deepDocument);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_RejectsDuplicateIds()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string duplicateIds = CreateDocument(
			CreateAccountJson(
				FirstAccountId,
				"Codex",
				"第一個",
				"first@example.com"),
			CreateAccountJson(
				FirstAccountId,
				"Claude",
				"第二個",
				"second@example.com"));
		await File.WriteAllTextAsync(filePath, duplicateIds);
		PortableSettingsJsonService service = new();

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));
	}

	[Fact]
	public async Task ImportAsync_NormalizesNamesScrubsIdentityAndRejectsInvalidAccountValues()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		string validDocument = CreateDocument(
			CreateAccountJson(
				FirstAccountId,
				"Copilot",
				"  顯示名稱  ",
				"  identity@example.com  "));
		await File.WriteAllTextAsync(filePath, validDocument);
		PortableSettingsJsonService service = new();

		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);

		Assert.Equal("顯示名稱", imported.Accounts[0].DisplayName);
		Assert.Null(imported.Accounts[0].ProviderAccountIdentity);

		string emptyIdDocument = CreateDocument(
			CreateAccountJson(
				Guid.Empty,
				"Copilot",
				"顯示名稱",
				null));
		await File.WriteAllTextAsync(filePath, emptyIdDocument);
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));

		string longNameDocument = CreateDocument(
			CreateAccountJson(
				FirstAccountId,
				"Copilot",
				new string('字', 81),
				null));
		await File.WriteAllTextAsync(filePath, longNameDocument);
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));

		string controlNameDocument = CreateDocument(
			CreateAccountJson(
				FirstAccountId,
				"Copilot",
				"控制\u0001字元",
				null));
		await File.WriteAllTextAsync(filePath, controlNameDocument);
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ImportAsync(filePath));
	}

	[Fact]
	public async Task ExportAsync_ReplacesAtomicallyAndCleansTemporaryFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		await File.WriteAllTextAsync(filePath, "舊內容");
		RecordingFileOperations fileOperations = new();
		PortableSettingsJsonService service = new(fileOperations);
		PortableSettingsSnapshot snapshot = CreateSnapshot();

		await service.ExportAsync(filePath, snapshot);

		PortableSettingsSnapshot imported = await service.ImportAsync(filePath);
		Assert.Equal(FirstAccountId, Assert.Single(imported.Accounts).Id);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.bak"));
		Assert.False(fileOperations.IgnoreMetadataErrors);
		Assert.Equal(
			Path.GetFullPath(temporaryDirectory.Path),
			Path.GetDirectoryName(fileOperations.BackupFilePath));
		Assert.True(
			fileOperations.Operations.IndexOf("FlushToDisk") <
			fileOperations.Operations.IndexOf("ReplaceFile"));
	}

	[Fact]
	public async Task ExportAsync_WhenInitialMoveFails_RetainsCompletedTemporaryFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			"portable.json");
		Directory.CreateDirectory(destinationPath);
		PortableSettingsJsonService service = new();

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(destinationPath, CreateSnapshot()));

		Assert.True(Directory.Exists(destinationPath));
		string temporaryFilePath = Assert.Single(
			Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
		PortableSettingsSnapshot retained = await service.ImportAsync(
			temporaryFilePath);
		Assert.Equal(FirstAccountId, Assert.Single(retained.Accounts).Id);
	}

	[Fact]
	public async Task ExportAsync_WhenReplaceMovesOriginalThenFails_RestoresOriginalAndRetainsNewCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			"portable.json");
		await File.WriteAllTextAsync(destinationPath, "舊內容");
		RecordingFileOperations fileOperations = new()
		{
			ReplacementBehavior = ReplacementBehavior.MoveOriginalThenThrow
		};
		PortableSettingsJsonService service = new(fileOperations);

		PortableSettingsException exception =
			await Assert.ThrowsAsync<PortableSettingsException>(
				() => service.ExportAsync(destinationPath, CreateSnapshot()));

		Assert.Equal("舊內容", await File.ReadAllTextAsync(destinationPath));
		string temporaryFilePath = Assert.Single(
			Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
		PortableSettingsSnapshot retained = await service.ImportAsync(
			temporaryFilePath);
		Assert.Equal(FirstAccountId, Assert.Single(retained.Accounts).Id);
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.bak"));
		Assert.DoesNotContain("未變更", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportAsync_WhenReplacePromotesNewFileThenFails_RetainsNewDestinationAndOldBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			"portable.json");
		await File.WriteAllTextAsync(destinationPath, "舊內容");
		RecordingFileOperations fileOperations = new()
		{
			ReplacementBehavior = ReplacementBehavior.PromoteNewThenThrow
		};
		PortableSettingsJsonService service = new(fileOperations);

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(destinationPath, CreateSnapshot()));

		PortableSettingsSnapshot retained = await service.ImportAsync(
			destinationPath);
		Assert.Equal(FirstAccountId, Assert.Single(retained.Accounts).Id);
		string backupFilePath = Assert.Single(
			Directory.GetFiles(temporaryDirectory.Path, "*.bak"));
		Assert.Equal("舊內容", await File.ReadAllTextAsync(backupFilePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task ExportAsync_WhenBackupCleanupFails_StillCommitsAndRetainsBackup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string destinationPath = Path.Combine(
			temporaryDirectory.Path,
			"portable.json");
		await File.WriteAllTextAsync(destinationPath, "舊內容");
		RecordingFileOperations fileOperations = new()
		{
			FailBackupDeletion = true
		};
		PortableSettingsJsonService service = new(fileOperations);

		await service.ExportAsync(destinationPath, CreateSnapshot());

		PortableSettingsSnapshot retained = await service.ImportAsync(
			destinationPath);
		Assert.Equal(FirstAccountId, Assert.Single(retained.Accounts).Id);
		string backupFilePath = Assert.Single(
			Directory.GetFiles(temporaryDirectory.Path, "*.bak"));
		Assert.Equal("舊內容", await File.ReadAllTextAsync(backupFilePath));
	}

	[Fact]
	public async Task ExportAsync_WhenCanceled_DoesNotLeaveTemporaryFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		PortableSettingsJsonService service = new();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => service.ExportAsync(
				filePath,
				CreateSnapshot(),
				cancellation.Token));

		Assert.False(File.Exists(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	[Fact]
	public async Task ExportAsync_RejectsInvalidSnapshotBeforeWriting()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "portable.json");
		PortableSettingsJsonService service = new();
		IReadOnlyList<AccountProfile> tooManyAccounts = Enumerable
			.Range(0, 257)
			.Select(index => new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Codex,
				$"帳號 {index}"))
			.ToArray();

		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				new PortableSettingsSnapshot(
					tooManyAccounts,
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DefaultWidgetPreferences)));
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				new PortableSettingsSnapshot(
					[],
					(UsageSortMode)int.MaxValue,
					UsageDisplayMode.Used,
					DefaultWidgetPreferences)));
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				new PortableSettingsSnapshot(
					[],
					UsageSortMode.Manual,
					UsageDisplayMode.Used)));
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				new PortableSettingsSnapshot(
					[],
					UsageSortMode.Manual,
					UsageDisplayMode.Used,
					DefaultWidgetPreferences with
					{
						Corner = (FloatingWidgetCorner)int.MaxValue
					})));
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				CreateSnapshot() with
				{
					WidgetPreferences = DefaultWidgetPreferences with
					{
						Theme = null
					}
				}));
		await Assert.ThrowsAsync<PortableSettingsException>(
			() => service.ExportAsync(
				filePath,
				CreateSnapshot() with
				{
					WidgetPreferences = DefaultWidgetPreferences with
					{
						Theme = (AppTheme)int.MaxValue
					}
				}));

		Assert.False(File.Exists(filePath));
		Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.tmp"));
	}

	private static string CreateAccountJson(
		Guid id,
		string provider,
		string displayName,
		string? providerIdentity)
	{
		return JsonSerializer.Serialize(new
		{
			id,
			provider,
			displayName,
			isEnabled = true,
			providerAccountIdentity = providerIdentity
		});
	}

	private static string CreateDocument(params string[] accountDocuments)
	{
		return $$"""
			{
			  "format": "ai-usage-dashboard-settings",
			  "schemaVersion": 1,
			  "accounts": [{{string.Join(",", accountDocuments)}}],
			  "preferences": {
			    "usageSortMode": "Manual",
			    "usageDisplayMode": "Used"
			  }
			}
			""";
	}

	private static PortableSettingsSnapshot CreateSnapshot()
	{
		return new PortableSettingsSnapshot(
			[
				new AccountProfile(
					FirstAccountId,
					ProviderKind.Claude,
					"工作")
			],
			UsageSortMode.Manual,
			UsageDisplayMode.Used,
			DefaultWidgetPreferences);
	}

	private static async Task CreateJunctionAsync(
		string junctionPath,
		string targetPath)
	{
		await RunMkLinkAsync("/J", junctionPath, targetPath);
	}

	private static async Task CreateHardLinkAsync(
		string linkPath,
		string targetPath)
	{
		await RunMkLinkAsync("/H", linkPath, targetPath);
	}

	private static async Task RunMkLinkAsync(
		string linkType,
		string linkPath,
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
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add(linkType);
		startInfo.ArgumentList.Add(linkPath);
		startInfo.ArgumentList.Add(targetPath);
		using System.Diagnostics.Process process =
			System.Diagnostics.Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"The link creation process did not start.");
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);
	}

	private enum ReplacementBehavior
	{
		Succeed,
		MoveOriginalThenThrow,
		PromoteNewThenThrow
	}

	private sealed class RecordingFileOperations :
		IPortableSettingsFileOperations
	{
		internal List<string> Operations { get; } = [];

		internal string? BackupFilePath { get; private set; }

		internal bool FailBackupDeletion { get; init; }

		internal bool? IgnoreMetadataErrors { get; private set; }

		internal ReplacementBehavior ReplacementBehavior { get; init; }

		public bool FileExists(string filePath)
		{
			return File.Exists(filePath);
		}

		public void DeleteFile(string filePath)
		{
			Operations.Add("DeleteFile");

			if (FailBackupDeletion &&
				string.Equals(
					Path.GetExtension(filePath),
					".bak",
					StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("Simulated backup cleanup failure.");
			}

			File.Delete(filePath);
		}

		public void FlushToDisk(FileStream stream)
		{
			Operations.Add("FlushToDisk");
			stream.Flush(flushToDisk: true);
		}

		public void MoveFile(
			string sourceFilePath,
			string destinationFilePath)
		{
			Operations.Add("MoveFile");
			File.Move(sourceFilePath, destinationFilePath);
		}

		public void ReplaceFile(
			string sourceFilePath,
			string destinationFilePath,
			string destinationBackupFilePath,
			bool ignoreMetadataErrors)
		{
			Operations.Add("ReplaceFile");
			BackupFilePath = destinationBackupFilePath;
			IgnoreMetadataErrors = ignoreMetadataErrors;

			switch (ReplacementBehavior)
			{
				case ReplacementBehavior.Succeed:
					// The restricted test sandbox cannot merge destination ACLs.
					// Model File.Replace's successful observable state while the
					// assertions above verify the production call's strict flag.
					File.Move(
						destinationFilePath,
						destinationBackupFilePath);
					File.Move(sourceFilePath, destinationFilePath);
					break;

				case ReplacementBehavior.MoveOriginalThenThrow:
					File.Move(
						destinationFilePath,
						destinationBackupFilePath);
					throw new IOException(
						"Simulated partial replacement failure.");

				case ReplacementBehavior.PromoteNewThenThrow:
					File.Move(
						destinationFilePath,
						destinationBackupFilePath);
					File.Move(sourceFilePath, destinationFilePath);
					throw new IOException(
						"Simulated post-promotion failure.");

				default:
					throw new ArgumentOutOfRangeException();
			}
		}
	}
}
