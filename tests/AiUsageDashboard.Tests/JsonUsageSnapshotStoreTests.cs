using System.Text.Json;
using System.Text.Json.Nodes;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class JsonUsageSnapshotStoreTests
{
	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		internal void Advance(TimeSpan value)
		{
			_utcNow += value;
		}
	}

	private static readonly DateTimeOffset TestNow = new(
		2026,
		7,
		16,
		2,
		2,
		3,
		TimeSpan.Zero);

	[Fact]
	public async Task SaveAndLoadAsync_RoundTripsAllowlistedFieldsAsStaleSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		AccountProfile savedAccount = new(
			Guid.Parse("11111111-1111-1111-1111-111111111111"),
			ProviderKind.Claude,
			"舊名稱");
		DateTimeOffset fetchedAt = new(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);
		DateTimeOffset observedAt = fetchedAt.AddSeconds(-2);
		DateTimeOffset staleAfter = fetchedAt.AddMinutes(2);
		DateTimeOffset resetsAt = fetchedAt.AddHours(5);
		UsageSnapshot savedSnapshot = new(
			savedAccount,
			new[]
			{
				new UsageMetric(
					"claude.five_hour",
					"5 小時用量",
					25,
					"已使用 25%",
					resetsAt,
					"2 小時 15 分鐘後重置"),
				new UsageMetric(
					"claude.reset_count",
					"重置次數",
					null,
					"2 次")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			fetchedAt,
			observedAt,
			staleAfter,
			Error: "不應寫入磁碟",
			ProviderAccountIdentity: "person@example.com",
			ProviderAccountDisplayIdentity: "private-display@example.com",
			SubscriptionScopeDisplayName: "Company Team",
			PlanTier: "max",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		AccountProfile currentAccount = savedAccount with
		{
			DisplayName = "目前名稱",
			IsEnabled = false
		};

		await store.SaveAsync(savedSnapshot);
		UsageSnapshot? loadedSnapshot = await store.LoadAsync(currentAccount);

		Assert.NotNull(loadedSnapshot);
		Assert.Same(currentAccount, loadedSnapshot.Account);
		Assert.Equal(savedSnapshot.Metrics, loadedSnapshot.Metrics);
		Assert.Equal(savedSnapshot.SourceTrust, loadedSnapshot.SourceTrust);
		Assert.Equal(SnapshotStatus.Stale, loadedSnapshot.Status);
		Assert.Equal(fetchedAt, loadedSnapshot.FetchedAt);
		Assert.Equal(observedAt, loadedSnapshot.ObservedAt);
		Assert.Equal(staleAfter, loadedSnapshot.StaleAfter);
		Assert.Equal("person@example.com", loadedSnapshot.ProviderAccountIdentity);
		Assert.Equal(
			"private-display@example.com",
			loadedSnapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			"Company Team",
			loadedSnapshot.SubscriptionScopeDisplayName);
		Assert.Equal("max", loadedSnapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			loadedSnapshot.SubscriptionVerificationState);
		Assert.Null(loadedSnapshot.Error);
	}

	[Fact]
	public async Task SaveAsync_WritesOnlyAllowlistedNonSecretFields()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"私人顯示名稱")) with
		{
			ProviderAccountDisplayIdentity = "private-display@example.com",
			SubscriptionScopeDisplayName = "Company Team",
			PlanTier = "max",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};

		await store.SaveAsync(snapshot);

		string json = await File.ReadAllTextAsync(filePath);
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			new[]
			{
				"accountId",
				"fetchedAt",
				"metrics",
				"observedAt",
				"planTier",
				"provider",
				"providerAccountDisplayIdentity",
				"providerAccountIdentity",
				"schemaVersion",
				"sourceTrust",
				"staleAfter",
				"subscriptionScopeDisplayName",
				"subscriptionVerificationState"
			},
			document.RootElement
				.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());
		Assert.Equal(
			new[]
			{
				"displayValue",
				"key",
				"label",
				"resetDisplayValue",
				"resetsAt",
				"usedPercent"
			},
			document.RootElement
				.GetProperty("metrics")[0]
				.EnumerateObject()
				.Select(property => property.Name)
				.OrderBy(name => name)
				.ToArray());

		string normalizedJson = json.ToLowerInvariant();
		Assert.DoesNotContain("私人顯示名稱", json, StringComparison.Ordinal);
		Assert.DoesNotContain("isenabled", normalizedJson);
		Assert.DoesNotContain("error", normalizedJson);
		Assert.DoesNotContain("secret", normalizedJson);
		Assert.DoesNotContain("token", normalizedJson);
		Assert.DoesNotContain("credential", normalizedJson);
		Assert.DoesNotContain("cookie", normalizedJson);
		Assert.Equal(
			"private-display@example.com",
			document.RootElement
				.GetProperty("providerAccountDisplayIdentity")
				.GetString());
		Assert.Equal(
			"Company Team",
			document.RootElement
				.GetProperty("subscriptionScopeDisplayName")
				.GetString());
		Assert.Equal(
			"max",
			document.RootElement.GetProperty("planTier").GetString());
		Assert.Equal(
			nameof(SubscriptionVerificationState.Verified),
			document.RootElement
				.GetProperty("subscriptionVerificationState")
				.GetString());
	}

	[Fact]
	public async Task SaveAndLoadAsync_WithExpiredMetrics_PersistsOnlyCurrentMetrics()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");
		UsageSnapshot snapshot = new(
			account,
			new[]
			{
				new UsageMetric("past", "Past", 10, "past", TestNow - TimeSpan.FromTicks(1)),
				new UsageMetric("boundary", "Boundary", 20, "boundary", TestNow),
				new UsageMetric("future", "Future", 30, "future", TestNow + TimeSpan.FromMinutes(1)),
				new UsageMetric("no-reset", "No reset", 40, "no-reset")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			TestNow);

		await store.SaveAsync(snapshot);
		UsageSnapshot? loaded = await store.LoadAsync(account);

		Assert.NotNull(loaded);
		Assert.Equal(
			new[] { "future", "no-reset" },
			loaded.Metrics.Select(metric => metric.Key));
	}

	[Fact]
	public async Task SaveAsync_WhenAllMetricsAreExpired_DeletesExistingCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");
		UsageSnapshot existing = new(
			account,
			new[] { new UsageMetric("current", "Current", 10, "current") },
			SourceTrust.Official,
			SnapshotStatus.Ready,
			TestNow);
		await store.SaveAsync(existing);
		UsageSnapshot expired = existing with
		{
			FetchedAt = TestNow + TimeSpan.FromMinutes(1),
			Metrics = new[]
			{
				new UsageMetric("past", "Past", 20, "past", TestNow - TimeSpan.FromTicks(1)),
				new UsageMetric("boundary", "Boundary", 30, "boundary", TestNow)
			}
		};

		await store.SaveAsync(expired);

		Assert.False(File.Exists(filePath));
		Assert.Null(await store.LoadAsync(account));
	}

	[Fact]
	public async Task LoadAsync_WhenLastMetricReachesResetBoundary_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		FakeTimeProvider timeProvider = new(TestNow);
		JsonUsageSnapshotStore store = CreateStore(filePath, timeProvider);
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");
		UsageSnapshot snapshot = new(
			account,
			new[]
			{
				new UsageMetric(
					"weekly",
					"Weekly",
					25,
					"current",
					TestNow + TimeSpan.FromMinutes(1))
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			TestNow);
		await store.SaveAsync(snapshot);
		timeProvider.Advance(TimeSpan.FromMinutes(1));

		UsageSnapshot? loaded = await store.LoadAsync(account);

		Assert.Null(loaded);
		Assert.True(File.Exists(filePath));
	}

	[Fact]
	public async Task LoadAsync_WithStrictLegacyV1Shape_MapsResetDisplayValueToNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		JsonNode legacyDocument = JsonNode.Parse(
			await File.ReadAllTextAsync(filePath))!;
		legacyDocument["schemaVersion"] = 1;
		Assert.True(legacyDocument.AsObject().Remove(
			"providerAccountDisplayIdentity"));
		Assert.True(legacyDocument.AsObject().Remove(
			"subscriptionScopeDisplayName"));
		Assert.True(legacyDocument.AsObject().Remove("planTier"));
		Assert.True(legacyDocument.AsObject().Remove(
			"subscriptionVerificationState"));
		Assert.True(legacyDocument["metrics"]![0]!
			.AsObject()
			.Remove("resetDisplayValue"));
		await File.WriteAllTextAsync(filePath, legacyDocument.ToJsonString());

		UsageSnapshot? loadedSnapshot = await store.LoadAsync(snapshot.Account);

		Assert.NotNull(loadedSnapshot);
		Assert.Single(loadedSnapshot.Metrics);
		Assert.Null(loadedSnapshot.Metrics[0].ResetDisplayValue);
		Assert.Null(loadedSnapshot.ProviderAccountDisplayIdentity);
		Assert.Null(loadedSnapshot.SubscriptionScopeDisplayName);
		Assert.Null(loadedSnapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			loadedSnapshot.SubscriptionVerificationState);
	}

	[Fact]
	public async Task LoadAsync_WithStrictV2Shape_DefaultsSubscriptionContextToUnverified()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude")) with
		{
			ProviderAccountDisplayIdentity = "person@example.com",
			SubscriptionScopeDisplayName = "Company Team",
			PlanTier = "max",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		await store.SaveAsync(snapshot);
		JsonObject v2Document = JsonNode.Parse(
			await File.ReadAllTextAsync(filePath))!.AsObject();
		v2Document["schemaVersion"] = 2;
		Assert.True(v2Document.Remove("providerAccountDisplayIdentity"));
		Assert.True(v2Document.Remove("subscriptionScopeDisplayName"));
		Assert.True(v2Document.Remove("planTier"));
		Assert.True(v2Document.Remove("subscriptionVerificationState"));
		await File.WriteAllTextAsync(filePath, v2Document.ToJsonString());

		UsageSnapshot? loadedSnapshot = await store.LoadAsync(snapshot.Account);

		Assert.NotNull(loadedSnapshot);
		Assert.Null(loadedSnapshot.ProviderAccountDisplayIdentity);
		Assert.Null(loadedSnapshot.SubscriptionScopeDisplayName);
		Assert.Null(loadedSnapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			loadedSnapshot.SubscriptionVerificationState);

		v2Document["planTier"] = "max";
		await File.WriteAllTextAsync(filePath, v2Document.ToJsonString());
		Assert.Null(await store.LoadAsync(snapshot.Account));
	}

	[Fact]
	public async Task LoadAsync_WithInvalidV2ResetDisplayValue_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		JsonObject validV2Document = JsonNode.Parse(
			await File.ReadAllTextAsync(filePath))!.AsObject();
		validV2Document["schemaVersion"] = 2;
		Assert.True(validV2Document.Remove("providerAccountDisplayIdentity"));
		Assert.True(validV2Document.Remove("subscriptionScopeDisplayName"));
		Assert.True(validV2Document.Remove("planTier"));
		Assert.True(validV2Document.Remove("subscriptionVerificationState"));
		string[] invalidValues =
		[
			new string('r', 513),
			"invalid\u0001value"
		];

		foreach (string invalidValue in invalidValues)
		{
			JsonObject invalidV2Document = validV2Document.DeepClone().AsObject();
			invalidV2Document["metrics"]![0]!["resetDisplayValue"] = invalidValue;
			await File.WriteAllTextAsync(filePath, invalidV2Document.ToJsonString());

			Assert.Null(await store.LoadAsync(snapshot.Account));
		}
	}

	[Fact]
	public async Task LoadAsync_WithInvalidV3SubscriptionContext_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude")) with
		{
			ProviderAccountDisplayIdentity = "person@example.com",
			SubscriptionScopeDisplayName = "Company Team",
			PlanTier = "max",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		await store.SaveAsync(snapshot);
		string validJson = await File.ReadAllTextAsync(filePath);
		Action<JsonObject>[] corruptions =
		[
			document => document.Remove("providerAccountDisplayIdentity"),
			document => document["unexpectedContext"] = true,
			document => document["providerAccountDisplayIdentity"] =
				" person@example.com ",
			document => document["subscriptionScopeDisplayName"] =
				new string('s', 257),
			document => document["planTier"] = new string('p', 65),
			document => document["subscriptionVerificationState"] =
				"FutureState"
		];

		foreach (Action<JsonObject> corrupt in corruptions)
		{
			JsonObject document = JsonNode.Parse(validJson)!.AsObject();
			corrupt(document);
			await File.WriteAllTextAsync(filePath, document.ToJsonString());

			Assert.Null(await store.LoadAsync(snapshot.Account));
		}
	}

	[Fact]
	public async Task SaveAsync_WithInvalidSubscriptionContext_RejectsSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		UsageSnapshot[] invalidSnapshots =
		[
			snapshot with
			{
				ProviderAccountDisplayIdentity = " person@example.com "
			},
			snapshot with
			{
				SubscriptionScopeDisplayName = new string('s', 257)
			},
			snapshot with { PlanTier = new string('p', 65) },
			snapshot with
			{
				SubscriptionVerificationState =
					(SubscriptionVerificationState)int.MaxValue
			}
		];

		foreach (UsageSnapshot invalidSnapshot in invalidSnapshots)
		{
			await Assert.ThrowsAsync<ArgumentException>(
				() => store.SaveAsync(invalidSnapshot));
		}
	}

	[Fact]
	public async Task LoadAsync_WhenMetricShapeDoesNotMatchSchema_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		string validV3Json = await File.ReadAllTextAsync(filePath);

		JsonNode missingV2Property = JsonNode.Parse(validV3Json)!;
		Assert.True(missingV2Property["metrics"]![0]!
			.AsObject()
			.Remove("resetDisplayValue"));
		await File.WriteAllTextAsync(filePath, missingV2Property.ToJsonString());
		Assert.Null(await store.LoadAsync(snapshot.Account));

		JsonNode unknownV2Property = JsonNode.Parse(validV3Json)!;
		unknownV2Property["metrics"]![0]!["unexpected"] = true;
		await File.WriteAllTextAsync(filePath, unknownV2Property.ToJsonString());
		Assert.Null(await store.LoadAsync(snapshot.Account));

		JsonNode v1WithV2Property = JsonNode.Parse(validV3Json)!;
		v1WithV2Property["schemaVersion"] = 1;
		Assert.True(v1WithV2Property.AsObject().Remove(
			"providerAccountDisplayIdentity"));
		Assert.True(v1WithV2Property.AsObject().Remove(
			"subscriptionScopeDisplayName"));
		Assert.True(v1WithV2Property.AsObject().Remove("planTier"));
		Assert.True(v1WithV2Property.AsObject().Remove(
			"subscriptionVerificationState"));
		await File.WriteAllTextAsync(filePath, v1WithV2Property.ToJsonString());
		Assert.Null(await store.LoadAsync(snapshot.Account));
	}

	[Fact]
	public async Task LoadAsync_WhenUnknownVersionHasOtherwiseValidShape_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		JsonNode futureDocument = JsonNode.Parse(
			await File.ReadAllTextAsync(filePath))!;
		futureDocument["schemaVersion"] = 4;
		await File.WriteAllTextAsync(filePath, futureDocument.ToJsonString());

		UsageSnapshot? loadedSnapshot = await store.LoadAsync(snapshot.Account);

		Assert.Null(loadedSnapshot);
		Assert.True(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveAsync_WhenSnapshotIsNotEligible_DoesNotCreateCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");
		UsageSnapshot readySnapshot = CreateReadySnapshot(account);
		UsageSnapshot[] ineligibleSnapshots =
		{
			readySnapshot with { Status = SnapshotStatus.Error },
			readySnapshot with { Status = SnapshotStatus.NotConfigured },
			readySnapshot with { Metrics = Array.Empty<UsageMetric>() },
			readySnapshot with { SourceTrust = SourceTrust.Unavailable }
		};

		for (int index = 0; index < ineligibleSnapshots.Length; index++)
		{
			string filePath = Path.Combine(
				temporaryDirectory.Path,
				$"usage-{index}.json");
			await CreateStore(filePath).SaveAsync(ineligibleSnapshots[index]);
			Assert.False(File.Exists(filePath));
		}
	}

	[Fact]
	public async Task LoadAsync_WhenCacheIsCorrupt_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");
		string corruptPath = Path.Combine(temporaryDirectory.Path, "corrupt.json");
		await File.WriteAllTextAsync(corruptPath, "{ this is not json");

		UsageSnapshot? corruptResult = await CreateStore(corruptPath).LoadAsync(account);

		Assert.Null(corruptResult);
		Assert.True(File.Exists(corruptPath));
	}

	[Fact]
	public async Task LoadAsync_WhenCacheExceedsSizeLimit_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "oversized.json");
		await using (FileStream stream = new(
			filePath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			stream.SetLength((256 * 1024) + 1);
		}
		AccountProfile account = new(Guid.NewGuid(), ProviderKind.Claude, "Claude");

		UsageSnapshot? snapshot = await CreateStore(filePath).LoadAsync(account);

		Assert.Null(snapshot);
	}

	[Fact]
	public async Task LoadAsync_WhenSchemaOrMetricValidationFails_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Codex, "Codex"));
		await store.SaveAsync(snapshot);
		string validJson = await File.ReadAllTextAsync(filePath);
		string unknownPropertyJson = validJson.Replace(
			"\"schemaVersion\": 3,",
			"\"schemaVersion\": 3,\n  \"unexpected\": true,",
			StringComparison.Ordinal);
		await File.WriteAllTextAsync(filePath, unknownPropertyJson);

		UsageSnapshot? unknownPropertyResult = await store.LoadAsync(snapshot.Account);

		Assert.Null(unknownPropertyResult);

		string invalidPercentageJson = validJson.Replace(
			"\"usedPercent\": 25",
			"\"usedPercent\": 101",
			StringComparison.Ordinal);
		await File.WriteAllTextAsync(filePath, invalidPercentageJson);

		UsageSnapshot? invalidPercentageResult = await store.LoadAsync(snapshot.Account);

		Assert.Null(invalidPercentageResult);

		string invalidResetDisplayValueJson = validJson.Replace(
			"\"resetDisplayValue\": null",
			"\"resetDisplayValue\": \" \"",
			StringComparison.Ordinal);
		await File.WriteAllTextAsync(filePath, invalidResetDisplayValueJson);

		UsageSnapshot? invalidResetDisplayValueResult =
			await store.LoadAsync(snapshot.Account);

		Assert.Null(invalidResetDisplayValueResult);
	}

	[Fact]
	public async Task LoadAsync_WhenAccountDoesNotMatchCache_ReturnsNull()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude 1"));
		await store.SaveAsync(snapshot);
		AccountProfile differentAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 2");

		UsageSnapshot? loadedSnapshot = await store.LoadAsync(differentAccount);

		Assert.Null(loadedSnapshot);
	}

	[Fact]
	public async Task LoadAsync_WhenFileAccessRepeatedlyFails_ReportsFirstFailureAndRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"Sensitive local account"));
		await CreateStore(filePath).SaveAsync(snapshot);
		List<(string Operation, string Summary, Exception? Exception)> diagnostics =
			new();
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (operation, summary, exception) =>
			{
				diagnostics.Add((operation, summary, exception));
				return true;
			});

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			Assert.Null(await store.LoadAsync(snapshot.Account));
			Assert.Null(await store.LoadAsync(snapshot.Account));
		}

		Assert.NotNull(await store.LoadAsync(snapshot.Account));

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			Assert.Null(await store.LoadAsync(snapshot.Account));
		}

		Assert.Equal(3, diagnostics.Count);
		Assert.All(
			diagnostics,
			diagnostic => Assert.Equal("usage-snapshot-cache", diagnostic.Operation));
		Assert.Equal(
			"action=load;provider=Claude;stage=read;result=unavailable",
			diagnostics[0].Summary);
		Assert.IsAssignableFrom<IOException>(diagnostics[0].Exception);
		Assert.Equal(
			"action=load;provider=Claude;result=recovered",
			diagnostics[1].Summary);
		Assert.Null(diagnostics[1].Exception);
		Assert.Equal(diagnostics[0].Summary, diagnostics[2].Summary);
		Assert.IsAssignableFrom<IOException>(diagnostics[2].Exception);
		Assert.All(
			diagnostics,
			diagnostic =>
			{
				Assert.DoesNotContain(
					filePath,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.Account.Id.ToString(),
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.Account.DisplayName,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.ProviderAccountIdentity!,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
			});
	}

	[Fact]
	public async Task LoadAsync_AfterFailure_DoesNotReportRecoveryUntilUsableSnapshotLoads()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await CreateStore(filePath).SaveAsync(snapshot);
		List<string> summaries = new();
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (_, summary, _) =>
			{
				summaries.Add(summary);
				return true;
			});

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			Assert.Null(await store.LoadAsync(snapshot.Account));
		}

		File.Delete(filePath);

		Assert.Null(await store.LoadAsync(snapshot.Account));
		Assert.Single(summaries);
		Assert.Equal(
			"action=load;provider=Claude;stage=read;result=unavailable",
			summaries[0]);

		await File.WriteAllTextAsync(
			filePath,
			"{\"schemaVersion\":99}");

		Assert.Null(await store.LoadAsync(snapshot.Account));
		Assert.Single(summaries);

		await File.WriteAllTextAsync(filePath, "not-json");

		Assert.Null(await store.LoadAsync(snapshot.Account));
		Assert.Single(summaries);

		await CreateStore(filePath).SaveAsync(snapshot);

		Assert.NotNull(await store.LoadAsync(snapshot.Account));
		Assert.Equal(2, summaries.Count);
		Assert.Equal(
			"action=load;provider=Claude;result=recovered",
			summaries[1]);
	}

	[Fact]
	public async Task SaveAsync_WhenReplacingCache_IsAtomicAndLeavesNoTempFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot firstSnapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		UsageSnapshot secondSnapshot = firstSnapshot with
		{
			FetchedAt = firstSnapshot.FetchedAt.AddMinutes(1),
			Metrics = new[]
			{
				firstSnapshot.Metrics[0] with
				{
					UsedPercent = 75,
					DisplayValue = "已使用 75%"
				}
			}
		};

		await store.SaveAsync(firstSnapshot);
		await store.SaveAsync(secondSnapshot);
		UsageSnapshot? loadedSnapshot = await store.LoadAsync(firstSnapshot.Account);

		Assert.NotNull(loadedSnapshot);
		Assert.Equal(75, loadedSnapshot.Metrics[0].UsedPercent);
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp",
			SearchOption.AllDirectories));
	}

	[Fact]
	public async Task SaveAsync_WithEquivalentContentAcrossStoreInstances_DoesNotRewriteFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore firstStore = CreateStore(filePath);
		JsonUsageSnapshotStore secondStore = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await firstStore.SaveAsync(snapshot);
		DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(filePath, marker);
		DateTime persistedMarker = File.GetLastWriteTimeUtc(filePath);

		await secondStore.SaveAsync(snapshot);

		Assert.Equal(persistedMarker, File.GetLastWriteTimeUtc(filePath));
	}

	[Fact]
	public async Task SaveAsync_AfterExternalReplacement_RepairsEquivalentSnapshot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		byte[] expectedDocument = await File.ReadAllBytesAsync(filePath);
		await File.WriteAllTextAsync(filePath, "externally replaced");

		await store.SaveAsync(snapshot);

		Assert.Equal(expectedDocument, await File.ReadAllBytesAsync(filePath));
		Assert.NotNull(await store.LoadAsync(snapshot.Account));
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
			"snapshot-junction");
		string targetFilePath = Path.Combine(targetDirectoryPath, "usage.json");
		Directory.CreateDirectory(targetDirectoryPath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await CreateStore(targetFilePath).SaveAsync(snapshot);
		byte[] expectedDocument = await File.ReadAllBytesAsync(targetFilePath);
		await JunctionTestHelper.CreateAsync(
			junctionDirectoryPath,
			targetDirectoryPath);

		try
		{
			JsonUsageSnapshotStore aliasedStore = CreateStore(Path.Combine(
				junctionDirectoryPath,
				"usage.json"));

			Assert.Null(await aliasedStore.LoadAsync(snapshot.Account));
			await aliasedStore.SaveAsync(snapshot with
			{
				Metrics = new[]
				{
					snapshot.Metrics[0] with { UsedPercent = 75 }
				}
			});
			await Assert.ThrowsAsync<IOException>(() => aliasedStore.DeleteAsync(
				snapshot.Account.Id,
				snapshot.Account.Provider));
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
	public async Task SaveAsync_WhenContentChanges_RewritesFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot firstSnapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(firstSnapshot);
		DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(filePath, marker);
		DateTime persistedMarker = File.GetLastWriteTimeUtc(filePath);
		UsageSnapshot changedSnapshot = firstSnapshot with
		{
			Metrics = new[]
			{
				firstSnapshot.Metrics[0] with
				{
					UsedPercent = 75,
					DisplayValue = "已使用 75%"
				}
			}
		};

		await store.SaveAsync(changedSnapshot);

		Assert.NotEqual(persistedMarker, File.GetLastWriteTimeUtc(filePath));
		UsageSnapshot? loadedSnapshot = await store.LoadAsync(firstSnapshot.Account);
		Assert.NotNull(loadedSnapshot);
		Assert.Equal(75, loadedSnapshot.Metrics[0].UsedPercent);
	}

	[Fact]
	public async Task LoadAsync_WhenCacheIsValid_SeedsEquivalentSaveDeduplication()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.Combine(temporaryDirectory.Path, "source.json");
		string loadedPath = Path.Combine(temporaryDirectory.Path, "loaded.json");
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await CreateStore(sourcePath).SaveAsync(snapshot);
		File.Copy(sourcePath, loadedPath);
		JsonUsageSnapshotStore loadedStore = CreateStore(loadedPath);
		Assert.NotNull(await loadedStore.LoadAsync(snapshot.Account));
		DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(loadedPath, marker);
		DateTime persistedMarker = File.GetLastWriteTimeUtc(loadedPath);

		await CreateStore(loadedPath).SaveAsync(snapshot);

		Assert.Equal(persistedMarker, File.GetLastWriteTimeUtc(loadedPath));
	}

	[Fact]
	public async Task SaveAsync_AfterPriorWriteFailure_ReportsFirstFailureAndRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string blockedDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"blocked");
		await File.WriteAllTextAsync(blockedDirectoryPath, "not a directory");
		string filePath = Path.Combine(blockedDirectoryPath, "usage.json");
		List<(string Operation, string Summary, Exception? Exception)> diagnostics =
			new();
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (operation, summary, exception) =>
			{
				diagnostics.Add((operation, summary, exception));
				return true;
			});
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				"Sensitive local account"));

		await store.SaveAsync(snapshot);
		await store.SaveAsync(snapshot);

		Assert.False(File.Exists(filePath));
		File.Delete(blockedDirectoryPath);
		Directory.CreateDirectory(blockedDirectoryPath);

		await store.SaveAsync(snapshot);
		await store.SaveAsync(snapshot);

		Assert.True(File.Exists(filePath));
		Assert.Equal(2, diagnostics.Count);
		Assert.All(
			diagnostics,
			diagnostic => Assert.Equal("usage-snapshot-cache", diagnostic.Operation));
		Assert.Equal(
			"action=save;provider=Claude;stage=persist;result=unavailable",
			diagnostics[0].Summary);
		Assert.IsAssignableFrom<IOException>(diagnostics[0].Exception);
		Assert.Equal(
			"action=save;provider=Claude;result=recovered",
			diagnostics[1].Summary);
		Assert.Null(diagnostics[1].Exception);
		Assert.All(
			diagnostics,
			diagnostic =>
			{
				Assert.DoesNotContain(
					filePath,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.Account.Id.ToString(),
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.Account.DisplayName,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					snapshot.ProviderAccountIdentity!,
					diagnostic.Summary,
					StringComparison.OrdinalIgnoreCase);
			});
	}

	[Fact]
	public async Task DiagnosticCallback_WhenWriteInitiallyFails_RetriesPersistentFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string blockedDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"blocked");
		await File.WriteAllTextAsync(blockedDirectoryPath, "not a directory");
		string filePath = Path.Combine(blockedDirectoryPath, "usage.json");
		List<string> attemptedSummaries = new();
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (_, summary, _) =>
			{
				attemptedSummaries.Add(summary);
				return attemptedSummaries.Count > 1;
			});
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));

		await store.SaveAsync(snapshot);
		await store.SaveAsync(snapshot);
		await store.SaveAsync(snapshot);

		Assert.Equal(2, attemptedSummaries.Count);
		Assert.All(
			attemptedSummaries,
			summary => Assert.Equal(
				"action=save;provider=Claude;stage=persist;result=unavailable",
				summary));

		File.Delete(blockedDirectoryPath);
		Directory.CreateDirectory(blockedDirectoryPath);

		await store.SaveAsync(snapshot);

		Assert.True(File.Exists(filePath));
		Assert.Equal(3, attemptedSummaries.Count);
		Assert.Equal(
			"action=save;provider=Claude;result=recovered",
			attemptedSummaries[2]);
	}

	[Fact]
	public async Task DiagnosticCallback_WhenRecoveryWriteInitiallyFails_RetriesRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await CreateStore(filePath).SaveAsync(snapshot);
		List<string> attemptedSummaries = new();
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (_, summary, _) =>
			{
				attemptedSummaries.Add(summary);
				return attemptedSummaries.Count != 2;
			});

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			Assert.Null(await store.LoadAsync(snapshot.Account));
		}

		Assert.NotNull(await store.LoadAsync(snapshot.Account));
		Assert.NotNull(await store.LoadAsync(snapshot.Account));
		Assert.NotNull(await store.LoadAsync(snapshot.Account));

		Assert.Equal(3, attemptedSummaries.Count);
		Assert.Equal(
			"action=load;provider=Claude;stage=read;result=unavailable",
			attemptedSummaries[0]);
		Assert.All(
			attemptedSummaries.Skip(1),
			summary => Assert.Equal(
				"action=load;provider=Claude;result=recovered",
				summary));
	}

	[Fact]
	public async Task DiagnosticCallback_WhenItThrows_DoesNotChangeBestEffortOutcome()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string blockedDirectoryPath = Path.Combine(
			temporaryDirectory.Path,
			"blocked");
		await File.WriteAllTextAsync(blockedDirectoryPath, "not a directory");
		string filePath = Path.Combine(blockedDirectoryPath, "usage.json");
		int callbackAttemptCount = 0;
		JsonUsageSnapshotStore store = CreateStore(
			filePath,
			reportDiagnostic: (_, _, _) =>
			{
				callbackAttemptCount++;
				throw new InvalidOperationException("Synthetic diagnostic failure.");
			});
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));

		await store.SaveAsync(snapshot);
		await store.SaveAsync(snapshot);
		File.Delete(blockedDirectoryPath);
		Directory.CreateDirectory(blockedDirectoryPath);
		await store.SaveAsync(snapshot);

		Assert.True(File.Exists(filePath));

		await using (FileStream exclusiveLease = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None))
		{
			Assert.Null(await store.LoadAsync(snapshot.Account));
			Assert.Null(await store.LoadAsync(snapshot.Account));
		}

		Assert.NotNull(await store.LoadAsync(snapshot.Account));
		Assert.Equal(4, callbackAttemptCount);
	}

	[Fact]
	public async Task SaveAsync_WithEquivalentContentAtDifferentPaths_WritesEachFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstPath = Path.Combine(temporaryDirectory.Path, "first.json");
		string secondPath = Path.Combine(temporaryDirectory.Path, "second.json");
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));

		await CreateStore(firstPath).SaveAsync(snapshot);
		await CreateStore(secondPath).SaveAsync(snapshot);

		Assert.True(File.Exists(firstPath));
		Assert.True(File.Exists(secondPath));
		Assert.Equal(
			await File.ReadAllBytesAsync(firstPath),
			await File.ReadAllBytesAsync(secondPath));
	}

	[Fact]
	public async Task SaveAsync_WhenSerializedCacheExceedsSizeLimit_PreservesExistingCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot existingSnapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(existingSnapshot);
		byte[] existingContents = await File.ReadAllBytesAsync(filePath);
		UsageMetric[] maximumSizeMetrics = Enumerable.Range(0, 64)
			.Select(index => new UsageMetric(
				$"{index:D2}{new string('甲', 198)}",
				new string('乙', 200),
				50,
				new string('丙', 512)))
			.ToArray();
		UsageSnapshot oversizedSnapshot = existingSnapshot with
		{
			FetchedAt = existingSnapshot.FetchedAt.AddMinutes(1),
			Metrics = maximumSizeMetrics
		};

		await store.SaveAsync(oversizedSnapshot);

		Assert.Equal(existingContents, await File.ReadAllBytesAsync(filePath));
		UsageSnapshot? loadedSnapshot = await store.LoadAsync(existingSnapshot.Account);
		Assert.NotNull(loadedSnapshot);
		Assert.Equal(existingSnapshot.FetchedAt, loadedSnapshot.FetchedAt);
		Assert.Equal(existingSnapshot.Metrics, loadedSnapshot.Metrics);
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp",
			SearchOption.AllDirectories));
	}

	[Fact]
	public async Task DeleteAsync_RemovesOnlyRequestedAccountCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Dictionary<(ProviderKind Provider, Guid AccountId), string> paths = new();
		JsonUsageSnapshotStore store = new((provider, accountId) =>
		{
			if (!paths.TryGetValue((provider, accountId), out string? path))
			{
				path = Path.Combine(
					temporaryDirectory.Path,
					provider.ToString(),
					accountId.ToString("N"),
					"usage.json");
				paths.Add((provider, accountId), path);
			}

			return path;
		}, CreateTimeProvider());
		UsageSnapshot firstSnapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		UsageSnapshot secondSnapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Codex, "Codex"));
		await store.SaveAsync(firstSnapshot);
		await store.SaveAsync(secondSnapshot);

		await store.DeleteAsync(
			firstSnapshot.Account.Id,
			firstSnapshot.Account.Provider);

		Assert.False(File.Exists(paths[(ProviderKind.Claude, firstSnapshot.Account.Id)]));
		Assert.True(File.Exists(paths[(ProviderKind.Codex, secondSnapshot.Account.Id)]));
	}

	[Fact]
	public async Task DeleteAsync_ThenEquivalentSave_RecreatesCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);

		await store.DeleteAsync(snapshot.Account.Id, snapshot.Account.Provider);
		await store.SaveAsync(snapshot);

		Assert.True(File.Exists(filePath));
		Assert.NotNull(await store.LoadAsync(snapshot.Account));
	}

	[Fact]
	public async Task DeleteAsync_AfterSave_EvictsFileGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		Assert.True(JsonUsageSnapshotStore.IsFileGateCached(filePath));

		await store.DeleteAsync(snapshot.Account.Id, snapshot.Account.Provider);

		Assert.False(JsonUsageSnapshotStore.IsFileGateCached(filePath));
	}

	[Fact]
	public async Task DeleteAsync_WhenFileIsLocked_PropagatesFailureAndEvictsFileGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, "usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		UsageSnapshot snapshot = CreateReadySnapshot(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, "Claude"));
		await store.SaveAsync(snapshot);
		await using FileStream fileLock = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);

		await Assert.ThrowsAsync<IOException>(
			() => store.DeleteAsync(snapshot.Account.Id, snapshot.Account.Provider));

		Assert.True(File.Exists(filePath));
		Assert.False(JsonUsageSnapshotStore.IsFileGateCached(filePath));
	}

	[Fact]
	public async Task DeleteAsync_WhenParentDirectoryIsMissing_CompletesAndEvictsFileGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"missing",
			"usage.json");
		JsonUsageSnapshotStore store = CreateStore(filePath);
		Guid accountId = Guid.NewGuid();

		await store.DeleteAsync(accountId, ProviderKind.Claude);

		Assert.False(File.Exists(filePath));
		Assert.False(JsonUsageSnapshotStore.IsFileGateCached(filePath));
	}

	private static UsageSnapshot CreateReadySnapshot(AccountProfile account)
	{
		DateTimeOffset fetchedAt = new(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);
		return new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric(
					"usage.primary",
					"5 小時用量",
					25,
					"已使用 25%",
					fetchedAt.AddHours(5))
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			fetchedAt,
			fetchedAt.AddSeconds(-1),
			fetchedAt.AddMinutes(2),
			ProviderAccountIdentity: "person@example.com");
	}

	private static JsonUsageSnapshotStore CreateStore(
		string filePath,
		TimeProvider? timeProvider = null,
		Func<string, string, Exception?, bool>? reportDiagnostic = null)
	{
		return new JsonUsageSnapshotStore(
			(_, _) => filePath,
			reportDiagnostic,
			timeProvider ?? CreateTimeProvider());
	}

	private static FakeTimeProvider CreateTimeProvider()
	{
		return new FakeTimeProvider(TestNow);
	}
}
