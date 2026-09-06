using System.IO;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class JsonClaudeUsageSafetyStateStoreTests
{
	[Fact]
	public async Task SaveAsync_ThenNewStoreLoadsManualState_AndClearRemovesIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 3,
			AccountIdentity: "claude@example.com",
			FailureReason: "Claude 用量檢查未通過安全驗證。",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null);
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);

		await store.SaveAsync(accountId, expected);

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId);
		Assert.Equal(expected, actual);

		await store.ClearAsync(accountId);

		Assert.Null(await store.LoadAsync(accountId));
		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task SaveAsync_ThenNewStoreLoadsAutomaticRetryStateAtMaximumBackoffStep()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 3,
			AccountIdentity: "claude@example.com",
			FailureReason: "Claude 回傳的零用量無法驗證。",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			RetryNotBefore: new DateTimeOffset(
				2026,
				8,
				15,
				0,
				4,
				0,
				TimeSpan.Zero),
			DiagnosticCliVersion: "2.1.220");

		await new JsonClaudeUsageSafetyStateStore(_ => filePath)
			.SaveAsync(accountId, expected);

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId);

		Assert.Equal(expected, actual);
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));
		Assert.Equal(
			3,
			document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			"2.1.220",
			document.RootElement
				.GetProperty("diagnosticCliVersion")
				.GetString());
	}

	[Fact]
	public async Task SaveAsync_WithCanonicalHistoricalVersion_PreservesMetadata()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "historical format failure",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null,
			DiagnosticCliVersion: "2.1.169");

		await new JsonClaudeUsageSafetyStateStore(_ => filePath)
			.SaveAsync(accountId, expected);

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData((int)ClaudeUsageSafetyAttemptPhase.Prepared)]
	[InlineData((int)ClaudeUsageSafetyAttemptPhase.StartedContained)]
	public async Task SaveAsync_ThenNewStoreLoadsSchemaThreeAttemptState(
		int attemptPhaseValue)
	{
		ClaudeUsageSafetyAttemptPhase attemptPhase =
			(ClaudeUsageSafetyAttemptPhase)attemptPhaseValue;
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 1,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: attemptId,
			AttemptPhase: attemptPhase);
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);

		await store.SaveAsync(accountId, expected);

		Assert.Equal(
			expected,
			await new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(accountId));
		using JsonDocument document = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));
		Assert.Equal(
			3,
			document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			attemptId,
			document.RootElement.GetProperty("attemptId").GetGuid());
		Assert.Equal(
			attemptPhase.ToString(),
			document.RootElement.GetProperty("attemptPhase").GetString());
		Assert.Equal(
			JsonValueKind.Null,
			document.RootElement
				.GetProperty("legacySourceSchemaVersion")
				.ValueKind);
		Assert.Equal(
			JsonValueKind.Null,
			document.RootElement
				.GetProperty("diagnosticCliVersion")
				.ValueKind);
	}

	[Fact]
	public async Task SaveAsync_ThenNewStoreLoadsExplicitUncontainedAttemptState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: null,
			AttemptPhase: ClaudeUsageSafetyAttemptPhase.Uncontained);
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);

		await store.SaveAsync(accountId, expected);

		Assert.Equal(
			expected,
			await new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(accountId));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaOneAttemptState_LoadsWithoutAttemptMetadata()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 1,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "interrupted attempt",
			  "recoveryMode": "AttemptInProgress",
			  "retryNotBefore": null
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(Guid.NewGuid());

		Assert.NotNull(actual);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			actual.RecoveryMode);
		Assert.Null(actual.AttemptId);
		Assert.Null(actual.AttemptPhase);
		Assert.Equal(1, actual.LegacySourceSchemaVersion);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaOneManualInterruptedState_LoadsForMigration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 1,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "Claude Code `/usage` 在完成零用量安全驗證前已中斷。",
			  "recoveryMode": "ManualRevalidationRequired",
			  "retryNotBefore": null
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(Guid.NewGuid());

		Assert.NotNull(actual);
		Assert.Equal(0, actual.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			actual.RecoveryMode);
		Assert.Null(actual.RetryNotBefore);
		Assert.Null(actual.AttemptId);
		Assert.Null(actual.AttemptPhase);
		Assert.Equal(1, actual.LegacySourceSchemaVersion);
	}

	[Fact]
	public async Task SaveAsync_WhenPrimaryStateIsLocked_CommitsTransitionIntentAndNewStoreUsesIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		ClaudeUsageSafetyState attemptState = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: Guid.NewGuid(),
			AttemptPhase: ClaudeUsageSafetyAttemptPhase.Prepared);
		ClaudeUsageSafetyState pendingState = new(
			AutomaticRevalidationAttemptCount: 1,
			AccountIdentity: "claude@example.com",
			FailureReason: "recoverable result",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			RetryNotBefore: new DateTimeOffset(
				2026,
				8,
				15,
				0,
				2,
				0,
				TimeSpan.Zero),
			DiagnosticCliVersion: "2.1.220");
		await store.SaveAsync(accountId, attemptState);

		using (FileStream lockedState = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await store.SaveAsync(accountId, pendingState);

			Assert.True(File.Exists(transitionIntentFilePath));
			Assert.Equal(
				pendingState,
				await new JsonClaudeUsageSafetyStateStore(_ => filePath)
					.LoadAsync(accountId));
		}

		Assert.Equal(
			pendingState,
			await new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(accountId));
		Assert.False(File.Exists(transitionIntentFilePath));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaOneTransitionIntent_AppliesLegacyTransition()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		DateTimeOffset retryNotBefore = new(
			2026,
			8,
			15,
			0,
			2,
			0,
			TimeSpan.Zero);
		await File.WriteAllTextAsync(
			transitionIntentFilePath,
			$$"""
			{
			  "schemaVersion": 1,
			  "accountId": "{{accountId}}",
			  "automaticRevalidationAttemptCount": 1,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "recoverable result",
			  "recoveryMode": "AutomaticRevalidationPending",
			  "retryNotBefore": "{{retryNotBefore:O}}"
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId);

		Assert.NotNull(actual);
		Assert.Equal(1, actual.AutomaticRevalidationAttemptCount);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			actual.RecoveryMode);
		Assert.Equal(retryNotBefore, actual.RetryNotBefore);
		Assert.Null(actual.AttemptId);
		Assert.Null(actual.AttemptPhase);
		Assert.Equal(1, actual.LegacySourceSchemaVersion);
		Assert.False(File.Exists(transitionIntentFilePath));
		using JsonDocument persistedDocument = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));
		Assert.Equal(
			3,
			persistedDocument.RootElement
				.GetProperty("schemaVersion")
				.GetInt32());
		Assert.Equal(
			1,
			persistedDocument.RootElement
				.GetProperty("legacySourceSchemaVersion")
				.GetInt32());
	}

	[Fact]
	public async Task LoadAsync_WithSchemaTwoTransitionIntent_AppliesWithoutDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		DateTimeOffset retryNotBefore = new(
			2026,
			8,
			15,
			0,
			3,
			0,
			TimeSpan.Zero);
		await File.WriteAllTextAsync(
			transitionIntentFilePath,
			$$"""
			{
			  "schemaVersion": 2,
			  "accountId": "{{accountId}}",
			  "automaticRevalidationAttemptCount": 1,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "recoverable result",
			  "recoveryMode": "AutomaticRevalidationPending",
			  "retryNotBefore": "{{retryNotBefore:O}}",
			  "attemptId": null,
			  "attemptPhase": null,
			  "legacySourceSchemaVersion": null
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId);

		Assert.NotNull(actual);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			actual.RecoveryMode);
		Assert.Equal(retryNotBefore, actual.RetryNotBefore);
		Assert.Null(actual.DiagnosticCliVersion);
		Assert.False(File.Exists(transitionIntentFilePath));
		using JsonDocument persistedDocument = JsonDocument.Parse(
			await File.ReadAllTextAsync(filePath));
		Assert.Equal(
			3,
			persistedDocument.RootElement
				.GetProperty("schemaVersion")
				.GetInt32());
	}

	[Fact]
	public async Task LoadAsync_WithSchemaTwoState_LoadsWithoutVersionDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 2,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "manual safety latch",
			  "recoveryMode": "ManualRevalidationRequired",
			  "retryNotBefore": null,
			  "attemptId": null,
			  "attemptPhase": null,
			  "legacySourceSchemaVersion": null
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(Guid.NewGuid());

		Assert.NotNull(actual);
		Assert.Null(actual.DiagnosticCliVersion);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			actual.RecoveryMode);
	}

	[Fact]
	public async Task LoadAsync_WithSchemaTwoAttemptState_PreservesAttemptMetadata()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid attemptId = Guid.NewGuid();
		await File.WriteAllTextAsync(
			filePath,
			$$"""
			{
			  "schemaVersion": 2,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "interrupted attempt",
			  "recoveryMode": "AttemptInProgress",
			  "retryNotBefore": null,
			  "attemptId": "{{attemptId}}",
			  "attemptPhase": "StartedContained",
			  "legacySourceSchemaVersion": null
			}
			""");

		ClaudeUsageSafetyState? actual = await new
			JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(Guid.NewGuid());

		Assert.NotNull(actual);
		Assert.Equal(
			ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			actual.RecoveryMode);
		Assert.Equal(attemptId, actual.AttemptId);
		Assert.Equal(
			ClaudeUsageSafetyAttemptPhase.StartedContained,
			actual.AttemptPhase);
		Assert.Null(actual.DiagnosticCliVersion);
	}

	[Fact]
	public async Task LoadAsync_WithNoncanonicalDiagnosticVersion_ThrowsInvalidDataException()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 3,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "format failure",
			  "recoveryMode": "ManualRevalidationRequired",
			  "retryNotBefore": null,
			  "attemptId": null,
			  "attemptPhase": null,
			  "legacySourceSchemaVersion": null,
			  "diagnosticCliVersion": "02.1.220"
			}
			""");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(Guid.NewGuid()));
	}

	[Fact]
	public async Task LoadAsync_WithFutureSchema_ThrowsInvalidDataException()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			"""
			{
			  "schemaVersion": 4,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": null,
			  "failureReason": "interrupted",
			  "recoveryMode": "AttemptInProgress",
			  "retryNotBefore": null
			}
			""");
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(Guid.NewGuid()));
	}

	[Fact]
	public async Task SaveAsync_WithInvalidState_DoesNotReplaceExistingState()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		ClaudeUsageSafetyState expected = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: null,
			FailureReason: "manual safety latch",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null);
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		await store.SaveAsync(accountId, expected);
		ClaudeUsageSafetyState invalid = expected with
		{
			RecoveryMode = ClaudeUsageSafetyRecoveryMode.Healthy
		};

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => store.SaveAsync(accountId, invalid));

		Assert.Equal(expected, await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task SaveAsync_WithInvalidAttemptMetadata_RejectsEveryInvalidCombination()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		Guid accountId = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		ClaudeUsageSafetyState attemptState = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: attemptId,
			AttemptPhase: ClaudeUsageSafetyAttemptPhase.Prepared);
		ClaudeUsageSafetyState automaticState = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "recoverable result",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			RetryNotBefore: new DateTimeOffset(
				2026,
				8,
				15,
				0,
				1,
				0,
				TimeSpan.Zero));
		ClaudeUsageSafetyState[] invalidStates =
		[
			attemptState with { AttemptId = null },
			attemptState with { AttemptPhase = null },
			attemptState with { AttemptId = Guid.Empty },
			attemptState with
			{
				AttemptPhase = (ClaudeUsageSafetyAttemptPhase)int.MaxValue
			},
			attemptState with { AttemptId = null, AttemptPhase = null },
			attemptState with
			{
				AttemptPhase = ClaudeUsageSafetyAttemptPhase.Uncontained
			},
			attemptState with { LegacySourceSchemaVersion = 2 },
			attemptState with { LegacySourceSchemaVersion = 1 },
			attemptState with
			{
				AttemptPhase = ClaudeUsageSafetyAttemptPhase.StartedContained,
				LegacySourceSchemaVersion = 1
			},
			automaticState with
			{
				AttemptId = attemptId,
				AttemptPhase = ClaudeUsageSafetyAttemptPhase.StartedContained
			}
		];

		foreach (ClaudeUsageSafetyState invalidState in invalidStates)
		{
			await Assert.ThrowsAsync<ArgumentException>(
				() => store.SaveAsync(accountId, invalidState));
		}

		Assert.False(File.Exists(filePath));
	}

	[Fact]
	public async Task LoadAsync_WithSchemaTwoMissingAttemptPhase_ThrowsInvalidDataException()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		await File.WriteAllTextAsync(
			filePath,
			$$"""
			{
			  "schemaVersion": 2,
			  "automaticRevalidationAttemptCount": 0,
			  "accountIdentity": "claude@example.com",
			  "failureReason": "interrupted attempt",
			  "recoveryMode": "AttemptInProgress",
			  "retryNotBefore": null,
			  "attemptId": "{{Guid.NewGuid()}}",
			  "legacySourceSchemaVersion": null
			}
			""");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(Guid.NewGuid()));
	}

	[Fact]
	public async Task ClearAsync_WhenStateFileIsLocked_CommitsClearIntentAndRecoversLater()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentFilePath =
			filePath + ".clear-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		await store.SaveAsync(
			accountId,
			CreateManualState("claude@example.com"));

		using (FileStream lockedState = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await Assert.ThrowsAsync<
				ClaudeUsageSafetyStateCleanupPendingException>(
				() => store.ClearAsync(accountId));

			Assert.True(File.Exists(filePath));
			Assert.True(File.Exists(clearIntentFilePath));
			using (JsonDocument clearIntentDocument = JsonDocument.Parse(
				await File.ReadAllTextAsync(clearIntentFilePath)))
			{
				Assert.Equal(
					1,
					clearIntentDocument.RootElement
						.GetProperty("schemaVersion")
						.GetInt32());
			}
			Assert.DoesNotContain(
				"claude@example.com",
				await File.ReadAllTextAsync(clearIntentFilePath),
				StringComparison.OrdinalIgnoreCase);
			Assert.Null(await store.LoadAsync(accountId));
		}

		Assert.Null(await store.LoadAsync(accountId));
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists(clearIntentFilePath));
	}

	[Fact]
	public async Task LoadAsync_WhenClearAndTransitionIntentsExist_ClearWinsAndRemovesTransition()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentFilePath =
			filePath + ".clear-pending-v1.json";
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		await store.SaveAsync(
			accountId,
			new ClaudeUsageSafetyState(
				AutomaticRevalidationAttemptCount: 0,
				AccountIdentity: "claude@example.com",
				FailureReason: "interrupted attempt",
				RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
				RetryNotBefore: null,
				AttemptId: Guid.NewGuid(),
				AttemptPhase: ClaudeUsageSafetyAttemptPhase.Prepared));
		ClaudeUsageSafetyState pendingState = new(
			AutomaticRevalidationAttemptCount: 1,
			AccountIdentity: "claude@example.com",
			FailureReason: "recoverable result",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.AutomaticRevalidationPending,
			RetryNotBefore: new DateTimeOffset(
				2026,
				8,
				15,
				0,
				2,
				0,
				TimeSpan.Zero));

		using (FileStream lockedState = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await store.SaveAsync(accountId, pendingState);
			await Assert.ThrowsAsync<
				ClaudeUsageSafetyStateCleanupPendingException>(
					() => store.ClearAsync(accountId));

			Assert.False(File.Exists(transitionIntentFilePath));
			Assert.True(File.Exists(clearIntentFilePath));
			Assert.Null(await new JsonClaudeUsageSafetyStateStore(_ => filePath)
				.LoadAsync(accountId));
		}

		Assert.Null(await new JsonClaudeUsageSafetyStateStore(_ => filePath)
			.LoadAsync(accountId));
		Assert.False(File.Exists(filePath));
		Assert.False(File.Exists(transitionIntentFilePath));
		Assert.False(File.Exists(clearIntentFilePath));
	}

	[Fact]
	public async Task SaveAsync_AfterCommittedClearIntent_PublishesNewStateAndRemovesIntent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentFilePath =
			filePath + ".clear-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		await store.SaveAsync(
			accountId,
			CreateManualState("old@example.com"));

		using (FileStream lockedState = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			await Assert.ThrowsAsync<
				ClaudeUsageSafetyStateCleanupPendingException>(
				() => store.ClearAsync(accountId));
			Assert.True(File.Exists(clearIntentFilePath));
		}

		ClaudeUsageSafetyState replacement = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "new@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: Guid.NewGuid(),
			AttemptPhase: ClaudeUsageSafetyAttemptPhase.StartedContained);
		await store.SaveAsync(accountId, replacement);

		Assert.Equal(replacement, await store.LoadAsync(accountId));
		Assert.False(File.Exists(clearIntentFilePath));
	}

	[Fact]
	public async Task LoadAsync_RemovesOnlyExactOrphanedTemporaryFileNames()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentFilePath =
			filePath + ".clear-pending-v1.json";
		string stateOrphan = Path.Combine(
			temporaryDirectory.Path,
			$".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
		string intentOrphan = Path.Combine(
			temporaryDirectory.Path,
			$".{Path.GetFileName(clearIntentFilePath)}.{Guid.NewGuid():N}.tmp");
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		string transitionIntentOrphan = Path.Combine(
			temporaryDirectory.Path,
			$".{Path.GetFileName(transitionIntentFilePath)}.{Guid.NewGuid():N}.tmp");
		string nearMatch = Path.Combine(
			temporaryDirectory.Path,
			$".{Path.GetFileName(filePath)}.not-a-guid.tmp");
		await File.WriteAllTextAsync(stateOrphan, "claude@example.com");
		await File.WriteAllTextAsync(intentOrphan, "clear intent");
		await File.WriteAllTextAsync(
			transitionIntentOrphan,
			"transition intent");
		await File.WriteAllTextAsync(nearMatch, "keep");
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);

		Assert.Null(await store.LoadAsync(Guid.NewGuid()));

		Assert.False(File.Exists(stateOrphan));
		Assert.False(File.Exists(intentOrphan));
		Assert.False(File.Exists(transitionIntentOrphan));
		Assert.True(File.Exists(nearMatch));
	}

	[Fact]
	public async Task DiscoverAccountIdsForRecovery_ReturnsOnlyExactNormalTopLevelGuidDirectories()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid normalAccountId = Guid.NewGuid();
		Guid junctionAccountId = Guid.NewGuid();
		Guid fileAccountId = Guid.NewGuid();
		Guid nestedAccountId = Guid.NewGuid();
		string junctionTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"junction-target");
		string junctionPath = Path.Combine(
			accountRootPath,
			junctionAccountId.ToString("N"));
		string normalAccountPath = Path.Combine(
			accountRootPath,
			normalAccountId.ToString("N"));
		Directory.CreateDirectory(normalAccountPath);
		await File.WriteAllTextAsync(
			Path.Combine(normalAccountPath, "usage-safety-v1.json"),
			"safety state");
		Directory.CreateDirectory(junctionTargetPath);
		Directory.CreateDirectory(Path.Combine(
			accountRootPath,
			"not-an-account",
			nestedAccountId.ToString("N")));
		await File.WriteAllTextAsync(
			Path.Combine(accountRootPath, fileAccountId.ToString("N")),
			"not a directory");
		await CreateJunctionAsync(junctionPath, junctionTargetPath);

		try
		{
			IReadOnlyList<Guid> accountIds =
				JsonClaudeUsageSafetyStateStore.DiscoverAccountIdsForRecovery(
					accountRootPath);

			Assert.Equal([normalAccountId], accountIds);
		}
		finally
		{
			Directory.Delete(junctionPath, recursive: false);
		}
	}

	[Fact]
	public async Task DiscoverAccountIdsForRecovery_SkipsConfigOnlyAccountDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountRootPath = Path.Combine(
			temporaryDirectory.Path,
			"claude");
		Guid configOnlyAccountId = Guid.NewGuid();
		Guid pendingAccountId = Guid.NewGuid();
		string configOnlyPath = Path.Combine(
			accountRootPath,
			configOnlyAccountId.ToString("N"));
		string pendingPath = Path.Combine(
			accountRootPath,
			pendingAccountId.ToString("N"));
		Directory.CreateDirectory(Path.Combine(configOnlyPath, "config"));
		Directory.CreateDirectory(pendingPath);
		await File.WriteAllTextAsync(
			Path.Combine(
				pendingPath,
				"usage-safety-v1.json.clear-pending-v1.json"),
			"clear intent");

		IReadOnlyList<Guid> accountIds =
			JsonClaudeUsageSafetyStateStore.DiscoverAccountIdsForRecovery(
				accountRootPath);

		Assert.Equal([pendingAccountId], accountIds);
	}

	[Fact]
	public async Task DiscoverAccountIdsForRecovery_WithJunctionRoot_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(
			temporaryDirectory.Path,
			"target");
		string junctionPath = Path.Combine(
			temporaryDirectory.Path,
			"claude-junction");
		Directory.CreateDirectory(targetPath);
		await CreateJunctionAsync(junctionPath, targetPath);

		try
		{
			Assert.Throws<IOException>(
				() => JsonClaudeUsageSafetyStateStore
					.DiscoverAccountIdsForRecovery(junctionPath));
		}
		finally
		{
			Directory.Delete(junctionPath, recursive: false);
		}
	}

	[Fact]
	public void CanDeleteOrphanedTemporaryFile_RejectsReparsePoints()
	{
		Assert.False(JsonClaudeUsageSafetyStateStore
			.CanDeleteOrphanedTemporaryFile(FileAttributes.ReparsePoint));
		Assert.False(JsonClaudeUsageSafetyStateStore
			.CanDeleteOrphanedTemporaryFile(
				FileAttributes.Directory | FileAttributes.ReparsePoint));
		Assert.True(JsonClaudeUsageSafetyStateStore
			.CanDeleteOrphanedTemporaryFile(FileAttributes.Normal));
	}

	[Fact]
	public async Task LoadAsync_WithMalformedClearIntent_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string clearIntentFilePath =
			filePath + ".clear-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		ClaudeUsageSafetyState expected =
			CreateManualState("claude@example.com");
		await store.SaveAsync(accountId, expected);
		await File.WriteAllTextAsync(clearIntentFilePath, "{}");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(accountId));

		File.Delete(clearIntentFilePath);
		Assert.Equal(expected, await store.LoadAsync(accountId));
	}

	[Fact]
	public async Task LoadAsync_WithMalformedTransitionIntent_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"usage-safety-v1.json");
		string transitionIntentFilePath =
			filePath + ".transition-pending-v1.json";
		Guid accountId = Guid.NewGuid();
		JsonClaudeUsageSafetyStateStore store = new(_ => filePath);
		ClaudeUsageSafetyState attemptState = new(
			AutomaticRevalidationAttemptCount: 0,
			AccountIdentity: "claude@example.com",
			FailureReason: "interrupted attempt",
			RecoveryMode: ClaudeUsageSafetyRecoveryMode.AttemptInProgress,
			RetryNotBefore: null,
			AttemptId: Guid.NewGuid(),
			AttemptPhase: ClaudeUsageSafetyAttemptPhase.Prepared);
		await store.SaveAsync(accountId, attemptState);
		await File.WriteAllTextAsync(transitionIntentFilePath, "{}");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.LoadAsync(accountId));

		File.Delete(transitionIntentFilePath);
		Assert.Equal(attemptState, await store.LoadAsync(accountId));
	}

	private static ClaudeUsageSafetyState CreateManualState(
		string? accountIdentity)
	{
		return new ClaudeUsageSafetyState(
			AutomaticRevalidationAttemptCount: 3,
			AccountIdentity: accountIdentity,
			FailureReason: "Claude 用量檢查未通過安全驗證。",
			RecoveryMode:
				ClaudeUsageSafetyRecoveryMode.ManualRevalidationRequired,
			RetryNotBefore: null);
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
		startInfo.ArgumentList.Add("/d");
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
