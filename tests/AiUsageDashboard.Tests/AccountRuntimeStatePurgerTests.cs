using System.IO;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.Tests;

public sealed class AccountRuntimeStatePurgerTests
{
	[Fact]
	public async Task PurgeAsync_ForClaude_WaitsForBindingGateBeforeAccountGate()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeAccountOperationGate operationGate = new();
		ClaudeBindingCommitGate bindingGate = new();
		TrackingClaudeBindingStore bindingStore = new();
		AccountRuntimeStatePurger purger = CreateClaudePurger(
			operationGate,
			bindingGate,
			bindingStore);
		Task purgeTask;

		IDisposable bindingLease = await bindingGate.EnterAsync();
		try
		{
			purgeTask = purger.PurgeAsync(accountId, ProviderKind.Claude);
			await Task.Yield();
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			using IDisposable operationLease = await operationGate.EnterAsync(
				accountId,
				timeout.Token);
			Assert.False(purgeTask.IsCompleted);
		}
		finally
		{
			bindingLease.Dispose();
		}

		await purgeTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForClaude_WhenDeleteReadBackRetainsBinding_Rethrows()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountOperationGate operationGate = new();
		TrackingClaudeBindingStore bindingStore = new()
		{
			BindingAfterDelete = ClaudeAccountBinding.Create(accountId, context)
		};
		AccountRuntimeStatePurger purger = CreateClaudePurger(
			operationGate,
			new ClaudeBindingCommitGate(),
			bindingStore);

		IOException exception = await Assert.ThrowsAsync<IOException>(
			() => purger.PurgeAsync(accountId, ProviderKind.Claude));

		Assert.Contains("read-back", exception.Message, StringComparison.Ordinal);
		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForClaude_WhenBindingGateWaitIsCanceled_DoesNotLeakLease()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeAccountOperationGate operationGate = new();
		ClaudeBindingCommitGate bindingGate = new();
		TrackingClaudeBindingStore bindingStore = new();
		AccountRuntimeStatePurger purger = CreateClaudePurger(
			operationGate,
			bindingGate,
			bindingStore);
		using CancellationTokenSource cancellationSource = new();

		IDisposable bindingLease = await bindingGate.EnterAsync();
		try
		{
			Task purgeTask = purger.PurgeAsync(
				accountId,
				ProviderKind.Claude,
				cancellationSource.Token);
			cancellationSource.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => purgeTask);
		}
		finally
		{
			bindingLease.Dispose();
		}

		await purger.PurgeAsync(accountId, ProviderKind.Claude)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public void Constructor_WithClaudeBindingStoreButNoSharedGate_Throws()
	{
		ClaudeAccountOperationGate operationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			operationGate,
			new SuccessfulClaudeUsageSafetyStateStore());
		ClaudeUsageProvider provider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		Assert.Throws<ArgumentNullException>(() => new AccountRuntimeStatePurger(
			operationGate,
			poller,
			provider,
			new CodexAccountOperationGate(),
			claudeAccountBindingStore: new TrackingClaudeBindingStore()));
	}

	[Fact]
	public async Task PurgeAsync_ForCodex_WaitsForBindingGateThenDeletesOnlyPrivateBinding()
	{
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		CodexBindingCommitGate bindingGate = new();
		TrackingCodexBindingStore bindingStore = new();
		AccountRuntimeStatePurger purger = CreateCodexPurger(
			operationGate,
			bindingGate,
			bindingStore);
		Task purgeTask;

		IDisposable bindingLease = await bindingGate.EnterAsync();
		try
		{
			purgeTask = purger.PurgeAsync(accountId, ProviderKind.Codex);
			await Task.Yield();
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			using IDisposable operationLease = await operationGate.EnterAsync(
				accountId,
				timeout.Token);
			Assert.False(purgeTask.IsCompleted);
		}
		finally
		{
			bindingLease.Dispose();
		}

		await purgeTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(new[] { accountId }, bindingStore.LoadedAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCodex_WhenDeleteReadBackRetainsBinding_Rethrows()
	{
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		TrackingCodexBindingStore bindingStore = new()
		{
			BindingAfterDelete = CodexWorkspaceBinding.Create(
				accountId,
				"person@example.com",
				Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
		};
		AccountRuntimeStatePurger purger = CreateCodexPurger(
			operationGate,
			new CodexBindingCommitGate(),
			bindingStore);

		IOException exception = await Assert.ThrowsAsync<IOException>(
			() => purger.PurgeAsync(accountId, ProviderKind.Codex));

		Assert.Contains("read-back", exception.Message, StringComparison.Ordinal);
		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public void Constructor_WithCodexBindingStoreButNoSharedGate_Throws()
	{
		ClaudeAccountOperationGate claudeOperationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			claudeOperationGate,
			new SuccessfulClaudeUsageSafetyStateStore());
		ClaudeUsageProvider provider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		Assert.Throws<ArgumentNullException>(() => new AccountRuntimeStatePurger(
			claudeOperationGate,
			poller,
			provider,
			new CodexAccountOperationGate(),
			codexWorkspaceBindingStore: new TrackingCodexBindingStore()));
	}

	[Fact]
	public void CodexPurge_DoesNotDeleteCodexHomeConfigOrAuthFiles()
	{
		string source = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Providers",
			"AccountRuntimeStatePurger.cs"));
		int codexCaseStart = source.IndexOf(
			"case ProviderKind.Codex:",
			StringComparison.Ordinal);
		int copilotCaseStart = source.IndexOf(
			"case ProviderKind.Copilot:",
			codexCaseStart,
			StringComparison.Ordinal);

		Assert.True(codexCaseStart >= 0);
		Assert.True(copilotCaseStart > codexCaseStart);
		string codexCase = source[codexCaseStart..copilotCaseStart];
		Assert.Contains("_codexWorkspaceBindingStore.DeleteAsync", codexCase);
		Assert.DoesNotContain("GetCodexHomeDirectory", codexCase);
		Assert.DoesNotContain("Directory.Delete", codexCase);
		Assert.DoesNotContain("File.Delete", codexCase);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_DeletesOnlyTargetCredentialAndPrivateHome()
	{
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		HashSet<Guid> credentialAccounts = [firstAccountId, secondAccountId];
		HashSet<Guid> privateHomeAccounts = [firstAccountId, secondAccountId];
		CopilotAccountOperationGate operationGate = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			accountId => credentialAccounts.Remove(accountId),
			accountId => privateHomeAccounts.Remove(accountId));

		await purger.PurgeAsync(firstAccountId, ProviderKind.Copilot);

		Assert.DoesNotContain(firstAccountId, credentialAccounts);
		Assert.DoesNotContain(firstAccountId, privateHomeAccounts);
		Assert.Contains(secondAccountId, credentialAccounts);
		Assert.Contains(secondAccountId, privateHomeAccounts);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_WhenGateWaitIsCancelled_DoesNotDeleteAfterGateReleases()
	{
		Guid accountId = Guid.NewGuid();
		CopilotAccountOperationGate operationGate = new();
		List<Guid> deletedCredentialAccountIds = new();
		List<Guid> deletedPrivateHomeAccountIds = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			deletedCredentialAccountIds.Add,
			deletedPrivateHomeAccountIds.Add);
		IDisposable heldLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);

		try
		{
			using CancellationTokenSource cancellationSource = new(
				TimeSpan.FromMilliseconds(50));
			Task purgeTask = purger.PurgeAsync(
				accountId,
				ProviderKind.Copilot,
				cancellationSource.Token);

			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => purgeTask);
			Assert.Empty(deletedCredentialAccountIds);
			Assert.Empty(deletedPrivateHomeAccountIds);
		}
		finally
		{
			heldLease.Dispose();
		}

		await Task.Delay(100);

		Assert.Empty(deletedCredentialAccountIds);
		Assert.Empty(deletedPrivateHomeAccountIds);
		operationGate.RequestPurge(accountId);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_WhenCancelled_ThrowsWithoutDeletingState()
	{
		CopilotAccountOperationGate operationGate = new();
		List<Guid> deletedCredentialAccountIds = new();
		List<Guid> deletedPrivateHomeAccountIds = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			deletedCredentialAccountIds.Add,
			deletedPrivateHomeAccountIds.Add);
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => purger.PurgeAsync(
				Guid.NewGuid(),
				ProviderKind.Copilot,
				cancellationSource.Token));
		Assert.Empty(deletedCredentialAccountIds);
		Assert.Empty(deletedPrivateHomeAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_WithoutCredentialDeleter_FailsClosed()
	{
		Guid accountId = Guid.NewGuid();
		CopilotAccountOperationGate operationGate = new();
		List<Guid> deletedPrivateHomeAccountIds = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			deleteCredential: null,
			deletePrivateAccountDirectory:
				deletedPrivateHomeAccountIds.Add);

		InvalidOperationException failure =
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				purger.PurgeAsync(accountId, ProviderKind.Copilot));

		Assert.Equal("Copilot 執行狀態清理服務尚未設定。", failure.Message);
		Assert.Empty(deletedPrivateHomeAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_WhenCredentialDeletionFails_DoesNotDeleteHomeAndPurgesGate()
	{
		Guid accountId = Guid.NewGuid();
		IOException failure = new("synthetic Copilot credential deletion failure");
		CopilotAccountOperationGate operationGate = new();
		List<Guid> deletedPrivateHomeAccountIds = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			_ => throw failure,
			deletedPrivateHomeAccountIds.Add);

		IOException actual = await Assert.ThrowsAsync<IOException>(() =>
			purger.PurgeAsync(accountId, ProviderKind.Copilot));

		Assert.Same(failure, actual);
		Assert.Empty(deletedPrivateHomeAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForCopilot_WhenHomeDeletionFails_RethrowsAndPurgesGate()
	{
		Guid accountId = Guid.NewGuid();
		IOException failure = new("synthetic Copilot home deletion failure");
		CopilotAccountOperationGate operationGate = new();
		List<Guid> deletedCredentialAccountIds = new();
		AccountRuntimeStatePurger purger = CreateCopilotPurger(
			operationGate,
			deletedCredentialAccountIds.Add,
			_ => throw failure);

		IOException actual = await Assert.ThrowsAsync<IOException>(() =>
			purger.PurgeAsync(accountId, ProviderKind.Copilot));

		Assert.Same(failure, actual);
		Assert.Equal(new[] { accountId }, deletedCredentialAccountIds);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_WhenDurableClaudeCleanupFails_ClearsProcessStateAndRethrows()
	{
		Guid accountId = Guid.NewGuid();
		InvalidDataException durableFailure = new("durable cleanup failed");
		ClaudeAccountOperationGate operationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			operationGate,
			new FailingClearSafetyStateStore(durableFailure));
		ClaudeUsageProvider provider = new(
			poller,
			new NoOpClaudeFallbackProvider());
		await provider.CompleteAccountLoginAsync(
			accountId,
			"old-account@example.com");
		AccountRuntimeStatePurger purger = new(
			operationGate,
			poller,
			provider,
			new CodexAccountOperationGate());

		Assert.Equal(1, provider.CachedAccountIdentitySeedCount);

		InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
			() => purger.PurgeAsync(accountId, ProviderKind.Claude));

		Assert.Same(durableFailure, actual);
		Assert.Equal(0, provider.CachedAccountIdentitySeedCount);
		Assert.Equal(0, operationGate.CachedAccountCount);
	}

	[Fact]
	public async Task PurgeAsync_ForAntigravity_DeletesBindingAndClearsProcessState()
	{
		Guid accountId = Guid.NewGuid();
		TrackingAntigravityOfficialClient officialClient = new();
		AntigravityUsageProvider provider = CreateAntigravityProvider(
			officialClient);
		TrackingAntigravityBindingStore bindingStore = new();
		AccountRuntimeStatePurger purger = CreatePurger(
			provider,
			bindingStore);

		provider.ArmOfficialSafetyRevalidation(accountId);
		await purger.PurgeAsync(accountId, ProviderKind.Antigravity);
		await provider.GetUsageAsync(
			CreateAntigravityAccount(accountId),
			CancellationToken.None);

		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(1, officialClient.CaptureCallCount);
		Assert.Equal(0, officialClient.RevalidateCallCount);
	}

	[Fact]
	public async Task PurgeAsync_WhenAntigravityBindingDeletionFails_ClearsProcessStateAndRethrows()
	{
		Guid accountId = Guid.NewGuid();
		InvalidDataException durableFailure = new("binding delete failed");
		TrackingAntigravityOfficialClient officialClient = new();
		AntigravityUsageProvider provider = CreateAntigravityProvider(
			officialClient);
		TrackingAntigravityBindingStore bindingStore = new(durableFailure);
		AccountRuntimeStatePurger purger = CreatePurger(
			provider,
			bindingStore);

		provider.ArmOfficialSafetyRevalidation(accountId);
		InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
			() => purger.PurgeAsync(accountId, ProviderKind.Antigravity));
		await provider.GetUsageAsync(
			CreateAntigravityAccount(accountId),
			CancellationToken.None);

		Assert.Same(durableFailure, actual);
		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
		Assert.Equal(1, officialClient.CaptureCallCount);
		Assert.Equal(0, officialClient.RevalidateCallCount);
	}

	[Fact]
	public void PurgeAsync_ForGrok_RunsPrivateDirectoryDeletionOffCallingThread()
	{
		Guid accountId = Guid.NewGuid();
		TrackingGrokBindingStore bindingStore = new();
		ManualResetEventSlim deletionStarted = new(initialState: false);
		ManualResetEventSlim releaseDeletion = new(initialState: false);
		ManualResetEventSlim purgeTaskReturned = new(initialState: false);
		int callerThreadId = 0;
		int deletionThreadId = 0;
		Exception? callerFailure = null;
		AccountRuntimeStatePurger purger = CreateGrokPurger(
			bindingStore,
			_ =>
			{
				deletionThreadId = Environment.CurrentManagedThreadId;
				deletionStarted.Set();
				releaseDeletion.Wait();
			});
		Thread callerThread = new(() =>
		{
			try
			{
				callerThreadId = Environment.CurrentManagedThreadId;
				Task purgeTask = purger.PurgeAsync(
					accountId,
					ProviderKind.Grok);
				purgeTaskReturned.Set();
				purgeTask.GetAwaiter().GetResult();
			}
			catch (Exception exception)
			{
				callerFailure = exception;
			}
		})
		{
			IsBackground = true
		};

		callerThread.Start();
		try
		{
			Assert.True(deletionStarted.Wait(TimeSpan.FromSeconds(5)));
			Assert.True(purgeTaskReturned.Wait(TimeSpan.FromSeconds(1)));
			Assert.NotEqual(callerThreadId, deletionThreadId);
		}
		finally
		{
			releaseDeletion.Set();
			Assert.True(callerThread.Join(TimeSpan.FromSeconds(5)));
		}

		Assert.Null(callerFailure);
		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
	}

	private static AccountRuntimeStatePurger CreatePurger(
		AntigravityUsageProvider antigravityUsageProvider,
		IAntigravityVerifiedAccountBindingStore bindingStore)
	{
		ClaudeAccountOperationGate operationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			operationGate,
			new FailingClearSafetyStateStore(
				new InvalidOperationException("Claude cleanup must not run.")));
		ClaudeUsageProvider claudeUsageProvider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		return new AccountRuntimeStatePurger(
			operationGate,
			poller,
			claudeUsageProvider,
			new CodexAccountOperationGate(),
			antigravityUsageProvider,
			bindingStore);
	}

	private static AccountRuntimeStatePurger CreateClaudePurger(
		ClaudeAccountOperationGate operationGate,
		ClaudeBindingCommitGate bindingGate,
		IClaudeAccountBindingStore bindingStore)
	{
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			operationGate,
			new SuccessfulClaudeUsageSafetyStateStore());
		ClaudeUsageProvider provider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		return new AccountRuntimeStatePurger(
			operationGate,
			poller,
			provider,
			new CodexAccountOperationGate(),
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: bindingGate);
	}

	private static AccountRuntimeStatePurger CreateCodexPurger(
		CodexAccountOperationGate operationGate,
		CodexBindingCommitGate bindingGate,
		ICodexWorkspaceBindingStore bindingStore)
	{
		ClaudeAccountOperationGate claudeOperationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			claudeOperationGate,
			new SuccessfulClaudeUsageSafetyStateStore());
		ClaudeUsageProvider provider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		return new AccountRuntimeStatePurger(
			claudeOperationGate,
			poller,
			provider,
			operationGate,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
	}

	private static AccountRuntimeStatePurger CreateGrokPurger(
		IGrokAccountBindingStore bindingStore,
		Action<Guid> deleteGrokPrivateAccountDirectory)
	{
		ClaudeAccountOperationGate operationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			operationGate,
			new FailingClearSafetyStateStore(
				new InvalidOperationException("Claude cleanup must not run.")));
		ClaudeUsageProvider claudeUsageProvider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		return new AccountRuntimeStatePurger(
			operationGate,
			poller,
			claudeUsageProvider,
			new CodexAccountOperationGate(),
			grokAccountOperationGate: new GrokAccountOperationGate(),
			grokAccountBindingStore: bindingStore,
			deleteGrokPrivateAccountDirectory:
				deleteGrokPrivateAccountDirectory);
	}

	private static AccountRuntimeStatePurger CreateCopilotPurger(
		CopilotAccountOperationGate copilotOperationGate,
		Action<Guid>? deleteCredential,
		Action<Guid> deletePrivateAccountDirectory)
	{
		ClaudeAccountOperationGate claudeOperationGate = new();
		ClaudeCliUsagePoller poller = new(
			_ => "unused",
			claudeOperationGate,
			new FailingClearSafetyStateStore(
				new InvalidOperationException("Claude cleanup must not run.")));
		ClaudeUsageProvider claudeUsageProvider = new(
			poller,
			new NoOpClaudeFallbackProvider());

		return new AccountRuntimeStatePurger(
			claudeOperationGate,
			poller,
			claudeUsageProvider,
			new CodexAccountOperationGate(),
			copilotAccountOperationGate: copilotOperationGate,
			deleteCopilotCredential: deleteCredential,
			deleteCopilotPrivateAccountDirectory:
				deletePrivateAccountDirectory);
	}

	private static AntigravityUsageProvider CreateAntigravityProvider(
		TrackingAntigravityOfficialClient officialClient)
	{
		return new AntigravityUsageProvider(
			officialClient,
			new FixedAntigravityOfficialExecutablePathResolver());
	}

	private static AccountProfile CreateAntigravityAccount(Guid accountId)
	{
		return new AccountProfile(
			accountId,
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: "agy@example.invalid");
	}

	private sealed class TrackingAntigravityBindingStore :
		IAntigravityVerifiedAccountBindingStore
	{
		private readonly Exception? _deleteFailure;

		internal TrackingAntigravityBindingStore(Exception? deleteFailure = null)
		{
			_deleteFailure = deleteFailure;
		}

		internal List<Guid> DeletedAccountIds { get; } = new();

		public Task<AntigravityVerifiedAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding load must not run.");
		}

		public Task SaveAsync(
			AntigravityVerifiedAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding save must not run.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			return _deleteFailure is null
				? Task.CompletedTask
				: Task.FromException(_deleteFailure);
		}
	}

	private sealed class TrackingGrokBindingStore : IGrokAccountBindingStore
	{
		internal List<Guid> DeletedAccountIds { get; } = new();

		public Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding inventory must not run.");
		}

		public Task<GrokAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding load must not run.");
		}

		public Task SaveAsync(
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding save must not run.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			return Task.CompletedTask;
		}
	}

	private sealed class TrackingClaudeBindingStore : IClaudeAccountBindingStore
	{
		internal ClaudeAccountBinding? BindingAfterDelete { get; set; }

		internal List<Guid> DeletedAccountIds { get; } = new();

		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding inventory must not run.");
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(
				BindingAfterDelete?.AccountId == accountId
					? BindingAfterDelete
					: null);
		}

		public Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding save must not run.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			return Task.CompletedTask;
		}
	}

	private sealed class TrackingCodexBindingStore :
		ICodexWorkspaceBindingStore
	{
		internal CodexWorkspaceBinding? BindingAfterDelete { get; set; }

		internal List<Guid> DeletedAccountIds { get; } = new();

		internal List<Guid> LoadedAccountIds { get; } = new();

		public Task<IReadOnlyList<CodexWorkspaceBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding inventory must not run.");
		}

		public Task<CodexWorkspaceBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadedAccountIds.Add(accountId);
			return Task.FromResult(
				BindingAfterDelete?.AccountId == accountId
					? BindingAfterDelete
					: null);
		}

		public Task SaveAsync(
			CodexWorkspaceBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Binding save must not run.");
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			return Task.CompletedTask;
		}
	}

	private sealed class TrackingAntigravityOfficialClient :
		IAntigravityOfficialPrintUsageClient
	{
		internal int CaptureCallCount { get; private set; }

		internal int RevalidateCallCount { get; private set; }

		public Task<AntigravityProductionUsageResult> CaptureAsync(
			string executablePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CaptureCallCount++;
			return Task.FromResult(CreateCaptureFailure());
		}

		public Task<AntigravityProductionUsageResult> RevalidateAsync(
			string executablePath,
			Action onSafetyTrackedAttemptStarted,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RevalidateCallCount++;
			onSafetyTrackedAttemptStarted();
			return Task.FromResult(CreateCaptureFailure());
		}

		private static AntigravityProductionUsageResult CreateCaptureFailure()
		{
			return new AntigravityProductionUsageResult(
				isSuccessful: false,
				AntigravityProductionUsageFailureKind.CaptureRejected,
				accountIdentity: null,
				Array.Empty<AntigravityProductionUsageWindow>());
		}
	}

	private sealed class FixedAntigravityOfficialExecutablePathResolver :
		IAntigravityOfficialExecutablePathResolver
	{
		public string? ResolveExecutablePath()
		{
			return @"C:\synthetic\agy.exe";
		}
	}

	private sealed class FailingClearSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		private readonly Exception _failure;

		internal FailingClearSafetyStateStore(Exception failure)
		{
			_failure = failure;
		}

		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<ClaudeUsageSafetyState?>(null);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromException(_failure);
		}
	}

	private sealed class SuccessfulClaudeUsageSafetyStateStore :
		IClaudeUsageSafetyStateStore
	{
		public Task<ClaudeUsageSafetyState?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<ClaudeUsageSafetyState?>(null);
		}

		public Task SaveAsync(
			Guid accountId,
			ClaudeUsageSafetyState state,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task ClearAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}
	}

	private sealed class NoOpClaudeFallbackProvider :
		IUsageProvider,
		IClaudeStatusLineFallbackControl
	{
		public ProviderKind Provider => ProviderKind.Claude;

		public TimeSpan MinimumRefreshInterval => TimeSpan.Zero;

		public Task DisableAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return Task.CompletedTask;
		}

		public Task<UsageSnapshot> GetUsageAsync(
			AccountProfile account,
			CancellationToken cancellationToken)
		{
			throw new InvalidOperationException(
				"Fallback usage must not be read by this test.");
		}
	}
}
