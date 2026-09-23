using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Core.Providers;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.Tests;

public sealed class DashboardViewModelTests
{
	private sealed class ManualRefreshTimeProvider : TimeProvider
	{
		private long _timestamp;
		private DateTimeOffset _utcNow;

		internal ManualRefreshTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		public override long GetTimestamp()
		{
			return _timestamp;
		}

		internal void Advance(TimeSpan value)
		{
			_utcNow += value;
			_timestamp += value.Ticks;
		}
	}

	private sealed class FakeDashboardPreferencesStore :
		IDashboardPreferencesStore,
		IWidgetPreferencesStore
	{
		internal UsageDisplayMode LoadedDisplayMode { get; set; } =
			UsageDisplayMode.Used;

		internal UsageSortMode LoadedSortMode { get; set; } = UsageSortMode.Manual;

		internal DashboardShellPreferences LoadedShellPreferences { get; set; } =
			DashboardShellPreferences.Default;

		internal bool ShouldFailSave { get; set; }

		internal List<UsageDisplayMode> SavedDisplayModes { get; } = new();

		internal List<UsageSortMode> SavedSortModes { get; } = new();

		internal List<(UsageSortMode SortMode, UsageDisplayMode DisplayMode)>
			SavedUsagePreferences { get; } = new();

		internal List<DashboardShellPreferences> SavedShellPreferences { get; } = new();

		internal List<(
			UsageSortMode SortMode,
			UsageDisplayMode DisplayMode,
			DashboardShellPreferences ShellPreferences)>
			SavedPortablePreferences { get; } = new();

		public Task<UsageDisplayMode> LoadUsageDisplayModeAsync(
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(LoadedDisplayMode);
		}

		public Task<UsageSortMode> LoadUsageSortModeAsync(
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(LoadedSortMode);
		}

		public Task<DashboardShellPreferences> LoadDashboardShellPreferencesAsync(
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(LoadedShellPreferences);
		}

		public Task SaveUsageDisplayModeAsync(
			UsageDisplayMode displayMode,
			CancellationToken cancellationToken = default)
		{
			if (ShouldFailSave)
			{
				throw new IOException("測試用顯示偏好儲存失敗。");
			}

			SavedDisplayModes.Add(displayMode);
			return Task.CompletedTask;
		}

		public Task SaveUsageSortModeAsync(
			UsageSortMode sortMode,
			CancellationToken cancellationToken = default)
		{
			if (ShouldFailSave)
			{
				throw new IOException("測試用排序偏好儲存失敗。");
			}

			SavedSortModes.Add(sortMode);
			return Task.CompletedTask;
		}

		public Task SaveUsagePreferencesAsync(
			UsageSortMode sortMode,
			UsageDisplayMode displayMode,
			CancellationToken cancellationToken = default)
		{
			if (ShouldFailSave)
			{
				throw new IOException("測試用顯示偏好儲存失敗。");
			}

			SavedSortModes.Add(sortMode);
			SavedDisplayModes.Add(displayMode);
			SavedUsagePreferences.Add((sortMode, displayMode));
			return Task.CompletedTask;
		}

		public Task SavePortablePreferencesAsync(
			UsageSortMode sortMode,
			UsageDisplayMode displayMode,
			DashboardShellPreferences shellPreferences,
			CancellationToken cancellationToken = default)
		{
			if (ShouldFailSave)
			{
				throw new IOException("測試用可攜偏好儲存失敗。");
			}

			SavedSortModes.Add(sortMode);
			SavedDisplayModes.Add(displayMode);
			SavedUsagePreferences.Add((sortMode, displayMode));
			SavedShellPreferences.Add(shellPreferences);
			SavedPortablePreferences.Add((
				sortMode,
				displayMode,
				shellPreferences));
			return Task.CompletedTask;
		}

		public Task SaveDashboardShellPreferencesAsync(
			DashboardShellPreferences preferences,
			CancellationToken cancellationToken = default)
		{
			if (ShouldFailSave)
			{
				throw new IOException("測試用浮窗偏好儲存失敗。");
			}

			SavedShellPreferences.Add(preferences);
			return Task.CompletedTask;
		}
	}

	private sealed class DelayedDashboardPreferencesRecoveryStore :
		IDashboardPreferencesRecoveryStore
	{
		private int _commitCount;

		public bool IsRecoveryActive => true;

		internal TaskCompletionSource AllowFirstCommit { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal int CommitCount => _commitCount;

		internal TaskCompletionSource FirstCommitStarted { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal DashboardPreferencesSnapshot StoredPreferences { get; init; } =
			new(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				DashboardShellPreferences.Default);

		public Task<DashboardPreferencesRecoveryPrepareResult> PrepareRecoveryAsync(
			DashboardPreferencesSnapshot currentPreferences,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new DashboardPreferencesRecoveryPrepareResult(
				DashboardPreferencesRecoveryStatus.Ready,
				Generation: 1));
		}

		public async Task<DashboardPreferencesRecoveryCommitResult>
			CommitRecoveryAsync(
				long expectedGeneration,
				DashboardPreferencesSnapshot currentPreferences,
				CancellationToken cancellationToken = default)
		{
			int commitCount = Interlocked.Increment(ref _commitCount);

			if (commitCount == 1)
			{
				FirstCommitStarted.TrySetResult();
				await AllowFirstCommit.Task.WaitAsync(cancellationToken);
			}

			DashboardPreferencesSnapshot recoveredPreferences =
				StoredPreferences with
				{
					ShellPreferences = StoredPreferences.ShellPreferences with
					{
						Corner = currentPreferences.ShellPreferences.Corner
					}
				};
			return new DashboardPreferencesRecoveryCommitResult(
				DashboardPreferencesRecoveryStatus.Ready,
				Generation: 1,
				recoveredPreferences);
		}

		public Task<bool> CompleteRecoveryAsync(
			long expectedGeneration,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(expectedGeneration == 1);
		}
	}

	private sealed class FakeAccountProfileStore : IAccountProfileStore
	{
		private IReadOnlyList<AccountProfile> _accounts;

		internal bool CanSaveOnLoad { get; set; } = true;

		internal AccountProfileLoadStatus? LoadStatusOnLoad { get; set; }

		internal string? LoadMessage { get; set; }

		internal string? RecoveryPathOnLoad { get; set; }

		internal bool ShouldFailSave { get; set; }

		internal bool ShouldFailAfterCommit { get; set; }

		internal Exception? FailureAfterPrimaryCommit { get; set; }

		internal Func<
			IReadOnlyList<AccountProfile>,
			CancellationToken,
			Task>? SaveHandler { get; set; }

		internal List<IReadOnlyList<AccountProfile>> SavedSnapshots { get; } = new();

		internal FakeAccountProfileStore(params AccountProfile[] accounts)
		{
			_accounts = accounts;
		}

		public Task<AccountProfileLoadResult> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new AccountProfileLoadResult(
				_accounts,
				LoadStatusOnLoad ??
					(CanSaveOnLoad
						? AccountProfileLoadStatus.Loaded
						: AccountProfileLoadStatus.UnsupportedVersion),
				CanSaveOnLoad,
				LoadMessage ?? (CanSaveOnLoad ? null : "測試用停寫狀態。"),
				RecoveryPathOnLoad));
		}

		public async Task SaveAsync(
			IReadOnlyList<AccountProfile> accounts,
			CancellationToken cancellationToken = default)
		{
			AccountProfile[] snapshot = accounts.ToArray();
			SavedSnapshots.Add(snapshot);

			if (ShouldFailSave)
			{
				throw new AccountProfileStoreException("測試用儲存失敗。");
			}

			if (SaveHandler is not null)
			{
				await SaveHandler(snapshot, cancellationToken);
			}

			_accounts = snapshot;

			if (FailureAfterPrimaryCommit is not null)
			{
				Exception failure = FailureAfterPrimaryCommit;
				FailureAfterPrimaryCommit = null;
				throw failure;
			}

			if (ShouldFailAfterCommit)
			{
				throw new AccountProfileStoreException(
					"帳號設定已套用，但安全備份更新失敗。請再次儲存帳號設定。",
					hasCommittedChanges: true);
			}

		}
	}

	private sealed class FakeUsageSnapshotStore : IUsageSnapshotStore
	{
		private readonly Dictionary<(Guid AccountId, ProviderKind Provider), UsageSnapshot>
			_snapshots = new();

		internal List<(Guid AccountId, ProviderKind Provider)> DeletedSnapshots { get; } = new();

		internal List<UsageSnapshot> SavedSnapshots { get; } = new();

		internal Func<AccountProfile, CancellationToken, Task<UsageSnapshot?>>?
			LoadHandler { get; set; }

		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			DeleteHandler { get; set; }

		internal Func<UsageSnapshot, CancellationToken, Task>? SaveHandler { get; set; }

		internal FakeUsageSnapshotStore(params UsageSnapshot[] snapshots)
		{
			foreach (UsageSnapshot snapshot in snapshots)
			{
				_snapshots[(snapshot.Account.Id, snapshot.Account.Provider)] = snapshot;
			}
		}

		public Task<UsageSnapshot?> LoadAsync(
			AccountProfile account,
			CancellationToken cancellationToken = default)
		{
			if (LoadHandler is not null)
			{
				return LoadHandler(account, cancellationToken);
			}

			_snapshots.TryGetValue((account.Id, account.Provider), out UsageSnapshot? snapshot);
			return Task.FromResult(snapshot);
		}

		public async Task SaveAsync(
			UsageSnapshot snapshot,
			CancellationToken cancellationToken = default)
		{
			SavedSnapshots.Add(snapshot);

			if (SaveHandler is not null)
			{
				await SaveHandler(snapshot, cancellationToken);
			}

			_snapshots[(snapshot.Account.Id, snapshot.Account.Provider)] = snapshot;
		}

		public async Task DeleteAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			DeletedSnapshots.Add((accountId, provider));

			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, provider, cancellationToken);
			}

			_snapshots.Remove((accountId, provider));
		}
	}

	private sealed class FakeClaudeAccountBindingStore : IClaudeAccountBindingStore
	{
		private readonly Dictionary<Guid, ClaudeAccountBinding> _bindings;

		internal Func<Guid, CancellationToken, Task>? DeleteHandler { get; set; }

		internal List<Guid> DeletedAccountIds { get; } = new();

		internal bool ShouldRetainDeletedBindings { get; set; }

		internal FakeClaudeAccountBindingStore(
			params ClaudeAccountBinding[] bindings)
		{
			_bindings = bindings.ToDictionary(binding => binding.AccountId);
		}

		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<IReadOnlyList<ClaudeAccountBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings.TryGetValue(accountId, out ClaudeAccountBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		internal ClaudeAccountBinding? GetBinding(Guid accountId)
		{
			_bindings.TryGetValue(accountId, out ClaudeAccountBinding? binding);
			return binding;
		}

		public async Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);

			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, cancellationToken);
			}

			if (!ShouldRetainDeletedBindings)
			{
				_bindings.Remove(accountId);
			}
		}
	}

	private sealed class FakeCodexWorkspaceBindingStore :
		ICodexWorkspaceBindingStore
	{
		private readonly Dictionary<Guid, CodexWorkspaceBinding> _bindings;

		internal Func<Guid, CancellationToken, Task>? DeleteHandler { get; set; }

		internal List<Guid> DeletedAccountIds { get; } = new();

		internal bool ShouldRetainDeletedBindings { get; set; }

		internal FakeCodexWorkspaceBindingStore(
			params CodexWorkspaceBinding[] bindings)
		{
			_bindings = bindings.ToDictionary(binding => binding.AccountId);
		}

		public Task<IReadOnlyList<CodexWorkspaceBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<IReadOnlyList<CodexWorkspaceBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<CodexWorkspaceBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings.TryGetValue(accountId, out CodexWorkspaceBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			CodexWorkspaceBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		internal CodexWorkspaceBinding? GetBinding(Guid accountId)
		{
			_bindings.TryGetValue(accountId, out CodexWorkspaceBinding? binding);
			return binding;
		}

		public async Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);

			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, cancellationToken);
			}

			if (!ShouldRetainDeletedBindings)
			{
				_bindings.Remove(accountId);
			}
		}
	}

	private sealed class FakeGrokAccountBindingStore : IGrokAccountBindingStore
	{
		private readonly Dictionary<Guid, GrokAccountBinding> _bindings;

		internal List<Guid> DeletedAccountIds { get; } = new();

		internal FakeGrokAccountBindingStore(
			params GrokAccountBinding[] bindings)
		{
			_bindings = bindings.ToDictionary(binding => binding.AccountId);
		}

		public Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<IReadOnlyList<GrokAccountBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<GrokAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			_bindings.Remove(accountId);
			return Task.CompletedTask;
		}

		internal GrokAccountBinding? GetBinding(Guid accountId)
		{
			_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
			return binding;
		}
	}

	private sealed class FakeAccountRuntimeStatePurger :
		AiUsageDashboard.App.Providers.IAccountRuntimeStatePurger
	{
		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			PurgeHandler { get; set; }

		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			ResetUsageSafetyStateHandler { get; set; }

		internal List<(Guid AccountId, ProviderKind Provider)> PurgedAccounts { get; } =
			new();

		internal List<(Guid AccountId, ProviderKind Provider)> ResetAccounts { get; } =
			new();

		public async Task PurgeAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			PurgedAccounts.Add((accountId, provider));

			if (PurgeHandler is not null)
			{
				await PurgeHandler(accountId, provider, cancellationToken);
			}
		}

		public async Task ResetUsageSafetyStateAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			ResetAccounts.Add((accountId, provider));

			if (ResetUsageSafetyStateHandler is not null)
			{
				await ResetUsageSafetyStateHandler(
					accountId,
					provider,
					cancellationToken);
			}
		}
	}

	private sealed class FakeAccountCleanupPendingStore :
		IAccountCleanupPendingStore
	{
		private readonly Dictionary<
			(Guid AccountId, ProviderKind Provider),
			AccountCleanupPendingWork> _pending = new();

		internal FakeAccountCleanupPendingStore(
			params AccountCleanupPendingWork[] pendingWork)
		{
			foreach (AccountCleanupPendingWork work in pendingWork)
			{
				_pending[(work.AccountId, work.Provider)] = work;
			}
		}

		internal Func<CancellationToken, Task<IReadOnlyList<AccountCleanupPendingWork>>>?
			LoadHandler { get; set; }

		internal Func<
			IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)>,
			CancellationToken,
			Task>? UpsertHandler { get; set; }

		internal Func<
			IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)>,
			CancellationToken,
			Task>? AuthorizeRetainedPrivateStateDeletionHandler { get; set; }

		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			UpsertCacheCleanupHandler { get; set; }

		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			MarkCacheCompletedHandler { get; set; }

		internal Func<Guid, ProviderKind, CancellationToken, Task>?
			MarkRuntimeCompletedHandler { get; set; }

		internal List<(Guid AccountId, ProviderKind Provider)> UpsertedAccounts { get; } =
			new();

		internal List<(Guid AccountId, ProviderKind Provider)>
			RetainedPrivateStateDeletionAuthorizedAccounts { get; } = new();

		internal List<(Guid AccountId, ProviderKind Provider)>
			CacheCleanupUpsertedAccounts { get; } = new();

		internal List<(Guid AccountId, ProviderKind Provider)> CacheCompletedAccounts { get; } =
			new();

		internal List<(Guid AccountId, ProviderKind Provider)> RuntimeCompletedAccounts { get; } =
			new();

		internal IReadOnlyList<AccountCleanupPendingWork> PendingWork =>
			_pending.Values.ToArray();

		public async Task<IReadOnlyList<AccountCleanupPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			if (LoadHandler is not null)
			{
				return await LoadHandler(cancellationToken);
			}

			return _pending.Values.ToArray();
		}

		public async Task UpsertAsync(
			IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
			CancellationToken cancellationToken = default)
		{
			UpsertedAccounts.AddRange(accountKeys);
			if (UpsertHandler is not null)
			{
				await UpsertHandler(accountKeys, cancellationToken);
			}

			foreach (var accountKey in accountKeys)
			{
				bool preserveRetainedPrivateStateDeletionAuthorization =
					_pending.TryGetValue(
						accountKey,
						out AccountCleanupPendingWork? existing) &&
					existing.RuntimePending &&
					existing.AllowRetainedPrivateStateDeletion;
				_pending[accountKey] = new AccountCleanupPendingWork(
					accountKey.AccountId,
					accountKey.Provider,
					CachePending: true,
					RuntimePending: true,
					AllowRetainedPrivateStateDeletion:
						preserveRetainedPrivateStateDeletionAuthorization);
			}
		}

		public async Task AuthorizeRetainedPrivateStateDeletionAsync(
			IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RetainedPrivateStateDeletionAuthorizedAccounts.AddRange(accountKeys);
			if (AuthorizeRetainedPrivateStateDeletionHandler is not null)
			{
				await AuthorizeRetainedPrivateStateDeletionHandler(
					accountKeys,
					cancellationToken);
			}

			foreach (var accountKey in accountKeys)
			{
				if (!_pending.TryGetValue(
						accountKey,
						out AccountCleanupPendingWork? work) ||
					!work.RuntimePending)
				{
					throw new InvalidOperationException(
						"只能授權仍待清理的 private state。");
				}

				_pending[accountKey] = work with
				{
					AllowRetainedPrivateStateDeletion = true
				};
			}
		}

		public Task AuthorizeRetainedPrivateStateDeletionForCommittedAccountsAsync(
			IReadOnlyCollection<(Guid AccountId, ProviderKind Provider)> accountKeys,
			CancellationToken cancellationToken = default)
		{
			(Guid AccountId, ProviderKind Provider)[] pendingKeys = accountKeys
				.Where(key => _pending.TryGetValue(
					key,
					out AccountCleanupPendingWork? work) &&
					work.RuntimePending)
				.ToArray();
			return AuthorizeRetainedPrivateStateDeletionAsync(
				pendingKeys,
				cancellationToken);
		}

		public async Task<AccountCleanupPendingWork> UpsertCacheCleanupAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			CacheCleanupUpsertedAccounts.Add((accountId, provider));
			if (UpsertCacheCleanupHandler is not null)
			{
				await UpsertCacheCleanupHandler(
					accountId,
					provider,
					cancellationToken);
			}

			(Guid AccountId, ProviderKind Provider) key = (accountId, provider);
			AccountCleanupPendingWork updatedWork = _pending.TryGetValue(
					key,
					out AccountCleanupPendingWork? work)
				? work with { CachePending = true }
				: new AccountCleanupPendingWork(
					accountId,
					provider,
					CachePending: true,
					RuntimePending: false);
			_pending[key] = updatedWork;
			return updatedWork;
		}

		public async Task MarkCacheCompletedAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			CacheCompletedAccounts.Add((accountId, provider));
			if (MarkCacheCompletedHandler is not null)
			{
				await MarkCacheCompletedHandler(accountId, provider, cancellationToken);
			}

			MarkComponentCompleted(accountId, provider, isCacheComponent: true);
		}

		public async Task MarkRuntimeCompletedAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			RuntimeCompletedAccounts.Add((accountId, provider));
			if (MarkRuntimeCompletedHandler is not null)
			{
				await MarkRuntimeCompletedHandler(accountId, provider, cancellationToken);
			}

			MarkComponentCompleted(accountId, provider, isCacheComponent: false);
		}

		private void MarkComponentCompleted(
			Guid accountId,
			ProviderKind provider,
			bool isCacheComponent)
		{
			if (!_pending.TryGetValue((accountId, provider), out AccountCleanupPendingWork? work))
			{
				return;
			}

			AccountCleanupPendingWork updated = isCacheComponent
				? work with { CachePending = false }
				: work with
				{
					RuntimePending = false,
					AllowRetainedPrivateStateDeletion = false
				};
			if (!updated.CachePending && !updated.RuntimePending)
			{
				_pending.Remove((accountId, provider));
			}
			else
			{
				_pending[(accountId, provider)] = updated;
			}
		}
	}

	private sealed class FakeGrokConnectionPendingStore :
		IGrokConnectionPendingStore
	{
		private readonly List<GrokConnectionPendingWork> _pending;

		internal FakeGrokConnectionPendingStore(
			params GrokConnectionPendingWork[] pendingWork)
		{
			_pending = pendingWork.ToList();
		}

		internal Func<CancellationToken, Task<IReadOnlyList<GrokConnectionPendingWork>>>?
			LoadHandler { get; set; }

		internal int LoadCallCount { get; private set; }

		public async Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			LoadCallCount++;
			if (LoadHandler is not null)
			{
				return await LoadHandler(cancellationToken);
			}

			return _pending.ToArray();
		}

		public Task<GrokConnectionBeginResult> BeginAsync(
			Guid accountId,
			Guid attemptId,
			DateTimeOffset startedAtUtc,
			CancellationToken cancellationToken = default)
		{
			_pending.RemoveAll(work => work.AccountId == accountId);
			_pending.Add(new GrokConnectionPendingWork(
				accountId,
				attemptId,
				startedAtUtc,
				startedAtUtc,
				GrokConnectionPendingStage.LoginStarted,
				PublicBindingId: null));
			return Task.FromResult(GrokConnectionBeginResult.Started);
		}

		public Task<bool> TryAdvanceAsync(
			Guid accountId,
			Guid attemptId,
			GrokConnectionPendingStage stage,
			Guid? publicBindingId,
			DateTimeOffset updatedAtUtc,
			CancellationToken cancellationToken = default)
		{
			int index = _pending.FindIndex(work =>
				(work.AccountId == accountId) &&
				(work.AttemptId == attemptId));
			if (index < 0)
			{
				return Task.FromResult(false);
			}

			_pending[index] = _pending[index] with
			{
				UpdatedAtUtc = updatedAtUtc,
				Stage = stage,
				PublicBindingId = publicBindingId
			};
			return Task.FromResult(true);
		}

		public Task<bool> TryRemoveAsync(
			Guid accountId,
			Guid attemptId,
			CancellationToken cancellationToken = default)
		{
			int removedCount = _pending.RemoveAll(work =>
				(work.AccountId == accountId) &&
				(work.AttemptId == attemptId));
			return Task.FromResult(removedCount > 0);
		}
	}

	private sealed class FakeAntigravityConnectionPendingStore :
		IAntigravityConnectionPendingStore
	{
		private readonly Dictionary<Guid, AntigravityConnectionPendingWork>
			_pending = new();
		private readonly Dictionary<Guid, AntigravitySetupApprovalReceipt>
			_approvals = new();
		private readonly Dictionary<Guid, AntigravitySetupAttemptState>
			_attemptStates = new();

		internal FakeAntigravityConnectionPendingStore(
			params AntigravityConnectionPendingWork[] pendingWork)
		{
			foreach (AntigravityConnectionPendingWork work in pendingWork)
			{
				_pending[work.AccountId] = work;
			}
		}

		internal Func<Guid, string, CancellationToken, Task>? UpsertHandler
			{ get; set; }

		internal Func<Guid, string, CancellationToken, Task>? BeginSetupHandler
			{ get; set; }

		internal Func<
			CancellationToken,
			Task<IReadOnlyList<AntigravityConnectionPendingWork>>>?
			LoadHandler { get; set; }

		internal Func<IReadOnlySet<Guid>, CancellationToken, Task>?
			CleanupOrphanedSetupArtifactsHandler { get; set; }

		internal Func<Guid, CancellationToken, Task>?
			MarkCacheCompletedHandler { get; set; }

		internal Func<Guid, CancellationToken, Task>?
			MarkProfileCompletedHandler { get; set; }

		internal Func<Guid, CancellationToken, Task>?
			RemoveHandler { get; set; }

		internal Func<Guid, CancellationToken, Task>?
			RemoveSetupApprovalHandler { get; set; }

		internal Func<Guid, CancellationToken, Task>?
			RemoveSetupAttemptStateHandler { get; set; }

		internal Func<
			Guid,
			AntigravitySetupAttemptState,
			CancellationToken,
			Task<bool>>? TryRemoveLaunchingSetupAttemptHandler { get; set; }

		internal List<Guid> UpsertedAccountIds { get; } = new();

		internal List<Guid> BegunSetupAccountIds { get; } = new();

		internal List<Guid> BegunSetupAttemptIds { get; } = new();

		internal List<Guid> CacheCompletedAccountIds { get; } = new();

		internal List<Guid> ProfileCompletedAccountIds { get; } = new();

		internal List<Guid> RemovedAccountIds { get; } = new();

		internal List<IReadOnlySet<Guid>> CleanupActiveAttemptIdSets
			{ get; } = new();

		internal IReadOnlyList<AntigravityConnectionPendingWork> PendingWork =>
			_pending.Values.ToArray();

		internal void AddSetupApproval(
			Guid setupAttemptId,
			AntigravityMachineSetupSourceKind sourceKind,
			string targetProviderAccountIdentity)
		{
			_approvals[setupAttemptId] = new AntigravitySetupApprovalReceipt(
				setupAttemptId,
				sourceKind,
				AntigravitySetupApprovalReceiptStore
					.ComputeTargetIdentityFingerprint(
						targetProviderAccountIdentity));
		}

		internal void AddSetupAttemptState(
			AntigravitySetupAttemptState state)
		{
			_attemptStates[state.AttemptId] = state;
		}

		public Task<IReadOnlyList<AntigravityConnectionPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			if (LoadHandler is not null)
			{
				return LoadHandler(cancellationToken);
			}

			return Task.FromResult<IReadOnlyList<
				AntigravityConnectionPendingWork>>(
				_pending.Values.ToArray());
		}

		public async Task CleanupOrphanedSetupArtifactsAsync(
			IReadOnlySet<Guid> activeAttemptIds,
			CancellationToken cancellationToken = default)
		{
			HashSet<Guid> activeAttemptIdSnapshot = new(activeAttemptIds);
			CleanupActiveAttemptIdSets.Add(activeAttemptIdSnapshot);
			if (CleanupOrphanedSetupArtifactsHandler is not null)
			{
				await CleanupOrphanedSetupArtifactsHandler(
					activeAttemptIdSnapshot,
					cancellationToken);
			}
		}

		public async Task UpsertAsync(
			Guid accountId,
			string expectedProfileIdentityFingerprint,
			CancellationToken cancellationToken = default)
		{
			UpsertedAccountIds.Add(accountId);
			if (UpsertHandler is not null)
			{
				await UpsertHandler(
					accountId,
					expectedProfileIdentityFingerprint,
					cancellationToken);
			}

			_pending.TryGetValue(
				accountId,
				out AntigravityConnectionPendingWork? existing);
			_pending[accountId] = new AntigravityConnectionPendingWork(
				accountId,
				expectedProfileIdentityFingerprint,
				CachePending: true,
				ProfilePending: true,
				existing?.SetupAttemptId);
		}

		public async Task BeginSetupAsync(
			Guid accountId,
			string expectedProfileIdentityFingerprint,
			Guid setupAttemptId,
			CancellationToken cancellationToken = default)
		{
			BegunSetupAccountIds.Add(accountId);
			BegunSetupAttemptIds.Add(setupAttemptId);
			if (BeginSetupHandler is not null)
			{
				await BeginSetupHandler(
					accountId,
					expectedProfileIdentityFingerprint,
					cancellationToken);
			}

			_pending[accountId] = new AntigravityConnectionPendingWork(
				accountId,
				expectedProfileIdentityFingerprint,
				CachePending: false,
				ProfilePending: false,
				setupAttemptId);
		}

		public Task<AntigravitySetupApprovalReceipt?> LoadSetupApprovalAsync(
			Guid setupAttemptId,
			CancellationToken cancellationToken = default)
		{
			_approvals.TryGetValue(
				setupAttemptId,
				out AntigravitySetupApprovalReceipt? approval);
			return Task.FromResult(approval);
		}

		public async Task RemoveSetupApprovalAsync(
			Guid setupAttemptId,
			CancellationToken cancellationToken = default)
		{
			if (RemoveSetupApprovalHandler is not null)
			{
				await RemoveSetupApprovalHandler(
					setupAttemptId,
					cancellationToken);
			}

			_approvals.Remove(setupAttemptId);
		}

		public Task<AntigravitySetupAttemptState?> LoadSetupAttemptStateAsync(
			Guid setupAttemptId,
			CancellationToken cancellationToken = default)
		{
			_attemptStates.TryGetValue(
				setupAttemptId,
				out AntigravitySetupAttemptState? state);
			return Task.FromResult(state);
		}

		public async Task<bool> TryRemoveLaunchingSetupAttemptAsync(
			Guid setupAttemptId,
			AntigravitySetupAttemptState observedLaunchingState,
			CancellationToken cancellationToken = default)
		{
			if (TryRemoveLaunchingSetupAttemptHandler is not null)
			{
				return await TryRemoveLaunchingSetupAttemptHandler(
					setupAttemptId,
					observedLaunchingState,
					cancellationToken);
			}

			if (!_attemptStates.TryGetValue(
					setupAttemptId,
					out AntigravitySetupAttemptState? existing))
			{
				return true;
			}

			if (existing != observedLaunchingState)
			{
				return false;
			}

			_attemptStates.Remove(setupAttemptId);
			return true;
		}

		public async Task RemoveSetupAttemptStateAsync(
			Guid setupAttemptId,
			CancellationToken cancellationToken = default)
		{
			if (RemoveSetupAttemptStateHandler is not null)
			{
				await RemoveSetupAttemptStateHandler(
					setupAttemptId,
					cancellationToken);
			}

			_attemptStates.Remove(setupAttemptId);
		}

		public async Task MarkCacheCompletedAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			CacheCompletedAccountIds.Add(accountId);
			if (MarkCacheCompletedHandler is not null)
			{
				await MarkCacheCompletedHandler(accountId, cancellationToken);
			}

			MarkComponentCompleted(accountId, isCacheComponent: true);
		}

		public async Task MarkProfileCompletedAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			ProfileCompletedAccountIds.Add(accountId);
			if (MarkProfileCompletedHandler is not null)
			{
				await MarkProfileCompletedHandler(accountId, cancellationToken);
			}

			MarkComponentCompleted(accountId, isCacheComponent: false);
		}

		public async Task RemoveAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			RemovedAccountIds.Add(accountId);
			if (RemoveHandler is not null)
			{
				await RemoveHandler(accountId, cancellationToken);
			}

			_pending.Remove(accountId);
		}

		private void MarkComponentCompleted(
			Guid accountId,
			bool isCacheComponent)
		{
			if (!_pending.TryGetValue(
					accountId,
					out AntigravityConnectionPendingWork? work))
			{
				return;
			}

			AntigravityConnectionPendingWork updated = isCacheComponent
				? work with { CachePending = false }
				: work with { ProfilePending = false };
			if (!updated.CachePending && !updated.ProfilePending)
			{
				_pending.Remove(accountId);
			}
			else
			{
				_pending[accountId] = updated;
			}
		}
	}

	private sealed class FakeUsageRefreshCoordinator : IUsageRefreshCoordinator
	{
		internal Func<AccountProfile, TimeSpan?>? RemainingCooldownHandler { get; set; }

		internal Func<AccountProfile, Task<UsageSnapshot>>? RefreshHandler { get; set; }

		internal List<Guid> InvalidatedAccountIds { get; } = new();

		internal List<AccountProfile> RefreshRequests { get; } = new();

		internal List<UsageSnapshot> SeededSnapshots { get; } = new();

		internal bool ShouldAcceptSeed { get; set; } = true;

		public TimeSpan? GetRemainingCooldown(AccountProfile account)
		{
			return RemainingCooldownHandler?.Invoke(account);
		}

		public void Invalidate(Guid accountId)
		{
			InvalidatedAccountIds.Add(accountId);
		}

		public Task<UsageSnapshot> RefreshAsync(
			AccountProfile account,
			CancellationToken cancellationToken = default)
		{
			RefreshRequests.Add(account);

			if (RefreshHandler is not null)
			{
				return RefreshHandler(account);
			}

			return Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				DateTimeOffset.UtcNow,
				Error: "測試用未設定狀態。"));
		}

		public bool SeedLastKnownGood(UsageSnapshot snapshot)
		{
			if (!ShouldAcceptSeed)
			{
				return false;
			}

			SeededSnapshots.Add(snapshot);
			return true;
		}
	}

	private sealed class StatefulUsageProvider : IUsageProvider, IUsageProviderInvalidator
	{
		private readonly Func<AccountProfile, int, Task<UsageSnapshot>> _handler;
		private int _callCount;

		public ProviderKind Provider { get; }

		public TimeSpan MinimumRefreshInterval { get; }

		internal int CallCount => Volatile.Read(ref _callCount);

		internal List<Guid> InvalidatedAccountIds { get; } = new();

		internal StatefulUsageProvider(
			ProviderKind provider,
			Func<AccountProfile, int, Task<UsageSnapshot>> handler,
			TimeSpan? minimumRefreshInterval = null)
		{
			Provider = provider;
			_handler = handler;
			MinimumRefreshInterval =
				minimumRefreshInterval ?? TimeSpan.FromMinutes(5);
		}

		public Task<UsageSnapshot> GetUsageAsync(
			AccountProfile account,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return _handler(account, Interlocked.Increment(ref _callCount));
		}

		public void Invalidate(Guid accountId)
		{
			InvalidatedAccountIds.Add(accountId);
		}
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile account,
		int usedPercent = 10)
	{
		return new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric(
					"test",
					"測試用量",
					usedPercent,
					$"已使用 {usedPercent}%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity:
				account.ProviderAccountIdentity ??
				$"{account.Id:N}@example.invalid");
	}

	[Fact]
	public async Task InitializeAsync_LoadsConfiguredAccounts()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號");
		FakeAccountProfileStore store = new(profile);
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(profile, account.Profile);
		Assert.True(viewModel.HasAccounts);
		Assert.False(viewModel.HasNoAccounts);
	}

	[Fact]
	public async Task InitializeAsync_PreservesStoredAccountOrder()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Zeta");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Alpha");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			new FakeUsageRefreshCoordinator());

		await viewModel.InitializeAsync();

		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.False(viewModel.Accounts[0].CanMoveUp);
		Assert.True(viewModel.Accounts[0].CanMoveDown);
		Assert.True(viewModel.Accounts[1].CanMoveUp);
		Assert.False(viewModel.Accounts[1].CanMoveDown);
	}

	[Fact]
	public async Task OfficialAntigravityConnection_WhenCacheCleanupFails_RestartContinuesWithoutSetup()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity",
			ProviderAccountIdentity: "legacy@example.com");
		FakeAccountProfileStore profileStore = new(profile);
		FakeAntigravityConnectionPendingStore pendingStore = new();
		FakeUsageSnapshotStore failingSnapshotStore = new()
		{
			DeleteHandler = (_, _, _) =>
				Task.FromException(new IOException("cache locked"))
		};
		DashboardViewModel firstViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: failingSnapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await firstViewModel.InitializeAsync();
		AccountUsageViewModel firstAccount =
			Assert.Single(firstViewModel.Accounts);
		firstAccount.BeginProviderAccountChange();
		Assert.True(firstAccount.TryBeginProviderAccountChangeCommit());

		bool wasPersisted =
			await firstViewModel.TryPersistOfficialAntigravityConnectionAsync(
				firstAccount);
		firstAccount.AbortProviderAccountChange();

		Assert.False(wasPersisted);
		Assert.Equal(3, failingSnapshotStore.DeletedSnapshots.Count);
		AntigravityConnectionPendingWork pending =
			Assert.Single(pendingStore.PendingWork);
		Assert.True(pending.CachePending);
		Assert.True(pending.ProfilePending);
		Assert.False(firstAccount.ShowAntigravityDefaultConnectionAction);
		Assert.Empty(profileStore.SavedSnapshots);

		FakeUsageSnapshotStore restartedSnapshotStore = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: restartedSnapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(restartedSnapshotStore.DeletedSnapshots));
		Assert.False(restartedAccount.ShowAntigravityDefaultConnectionAction);
	}

	[Fact]
	public async Task OfficialAntigravityConnection_WhenProfileSaveFails_RestartResumesAfterDurableCachePhase()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeAntigravityConnectionPendingStore pendingStore = new();
		FakeUsageSnapshotStore firstSnapshotStore = new();
		DashboardViewModel firstViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: firstSnapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await firstViewModel.InitializeAsync();
		AccountUsageViewModel firstAccount =
			Assert.Single(firstViewModel.Accounts);
		firstAccount.BeginProviderAccountChange();
		Assert.True(firstAccount.TryBeginProviderAccountChangeCommit());

		bool wasPersisted =
			await firstViewModel.TryPersistOfficialAntigravityConnectionAsync(
				firstAccount);
		firstAccount.AbortProviderAccountChange();

		Assert.False(wasPersisted);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(firstSnapshotStore.DeletedSnapshots));
		AntigravityConnectionPendingWork pending =
			Assert.Single(pendingStore.PendingWork);
		Assert.False(pending.CachePending);
		Assert.True(pending.ProfilePending);

		profileStore.ShouldFailSave = false;
		FakeUsageSnapshotStore restartedSnapshotStore = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: restartedSnapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await restartedViewModel.InitializeAsync();

		Assert.Empty(restartedSnapshotStore.DeletedSnapshots);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			Assert.Single(restartedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			Assert.Single(profileStore.SavedSnapshots[^1])
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravityConnection_WhenProfileWasReplaced_DiscardsStaleWork()
	{
		AccountProfile replacementProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"replacement",
			ProviderAccountIdentity: "replacement@example.com");
		FakeAccountProfileStore profileStore = new(replacementProfile);
		FakeAntigravityConnectionPendingStore pendingStore = new();
		await pendingStore.UpsertAsync(
			replacementProfile.Id,
			new string('A', 64));
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(pendingStore.PendingWork);
		Assert.Contains(replacementProfile.Id, pendingStore.RemovedAccountIds);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Equal(
			"replacement@example.com",
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravityConnection_WhenSameIdProfileWasReplacedByUnboundCard_DiscardsStaleWorkAndRestoresConnectionAction()
	{
		AccountProfile replacementProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"replacement");
		FakeAccountProfileStore profileStore = new(replacementProfile);
		FakeAntigravityConnectionPendingStore pendingStore = new();
		await pendingStore.UpsertAsync(
			replacementProfile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(
					"previous@example.com"));
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(pendingStore.PendingWork);
		Assert.Contains(replacementProfile.Id, pendingStore.RemovedAccountIds);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.CurrentSnapshot);
		Assert.Equal(UsageRecoveryAction.None, account.RecoveryAction);
		Assert.True(account.CanConnectProviderAccount);
		Assert.True(account.ShowAntigravityDefaultConnectionAction);
	}

	[Fact]
	public void AntigravityPendingProfileFingerprint_MatchesIdentityComparerCasing()
	{
		Assert.Equal(
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(
					"Legacy@Example.com"),
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(
					"legacy@example.com"));
	}

	[Fact]
	public async Task PendingAntigravityConnection_WhenDisabledAtStartup_StaysAutomaticAfterEnable()
	{
		AccountProfile disabledProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"disabled",
			IsEnabled: false);
		FakeAccountProfileStore profileStore = new(disabledProfile);
		FakeAntigravityConnectionPendingStore pendingStore = new();
		await pendingStore.UpsertAsync(
			disabledProfile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null));
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();

		await viewModel.UpdateAccountAsync(
			disabledProfile with { IsEnabled = true });

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.Single(pendingStore.PendingWork);
	}

	[Fact]
	public async Task AntigravityArtifactCleanup_AfterHydration_PreservesAllActiveAttemptsAndRunsOnce()
	{
		Guid setupAttemptId = Guid.NewGuid();
		Guid commitAttemptId = Guid.NewGuid();
		AccountProfile setupProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"setup",
			IsEnabled: false);
		AccountProfile commitProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"commit",
			IsEnabled: false);
		FakeAntigravityConnectionPendingStore pendingStore = new(
			new AntigravityConnectionPendingWork(
				setupProfile.Id,
				DashboardViewModel
					.CreateAntigravityPendingProfileIdentityFingerprint(null),
				CachePending: false,
				ProfilePending: false,
				setupAttemptId),
			new AntigravityConnectionPendingWork(
				commitProfile.Id,
				DashboardViewModel
					.CreateAntigravityPendingProfileIdentityFingerprint(null),
				CachePending: true,
				ProfilePending: true,
				commitAttemptId));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(setupProfile, commitProfile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		IReadOnlySet<Guid> activeAttemptIds = Assert.Single(
			pendingStore.CleanupActiveAttemptIdSets);
		Assert.True(activeAttemptIds.SetEquals(
			new[] { setupAttemptId, commitAttemptId }));

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Single(pendingStore.CleanupActiveAttemptIdSets);
	}

	[Fact]
	public async Task AntigravityArtifactCleanup_WhenTransientlyBlocked_RetriesWithBackoff()
	{
		DateTimeOffset now = DateTimeOffset.Parse(
			"2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		int cleanupAttempts = 0;
		FakeAntigravityConnectionPendingStore pendingStore = new()
		{
			CleanupOrphanedSetupArtifactsHandler = (_, _) =>
			{
				cleanupAttempts++;
				return cleanupAttempts == 1
					? Task.FromException(
						new IOException("artifact directory locked"))
					: Task.CompletedTask;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Antigravity,
				"Antigravity")),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(1, cleanupAttempts);
		Assert.False(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Equal(1, cleanupAttempts);

		timeProvider.Advance(TimeSpan.FromMinutes(1));

		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Equal(2, cleanupAttempts);
		Assert.True(
			Assert.Single(viewModel.Accounts)
				.ShowAntigravityDefaultConnectionAction);
	}

	[Fact]
	public async Task AntigravityArtifactCleanup_WhenExactDeleteFails_RearmsSweep()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Guid setupAttemptId = Guid.NewGuid();
		Assert.True(await viewModel.TryBeginOfficialAntigravitySetupIntentAsync(
			account,
			setupAttemptId));
		pendingStore.RemoveSetupApprovalHandler = (_, _) =>
			Task.FromException(new IOException("receipt locked"));

		Assert.True(await viewModel.CancelOfficialAntigravitySetupIntentAsync(
			account.Id,
			setupAttemptId));
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(2, pendingStore.CleanupActiveAttemptIdSets.Count);
		Assert.Empty(pendingStore.CleanupActiveAttemptIdSets[1]);
	}

	[Fact]
	public async Task AntigravityArtifactCleanup_WithLegacyUnknownAttempt_DelaysSweep()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"legacy",
			IsEnabled: false);
		FakeAntigravityConnectionPendingStore pendingStore = new(
			new AntigravityConnectionPendingWork(
				profile.Id,
				DashboardViewModel
					.CreateAntigravityPendingProfileIdentityFingerprint(null),
				CachePending: false,
				ProfilePending: false));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(pendingStore.CleanupActiveAttemptIdSets);
	}

	[Fact]
	public async Task PendingAntigravityConnection_WhenJournalLoadFails_BlocksChangesAndAutomaticallyRecovers()
	{
		DateTimeOffset now = DateTimeOffset.Parse(
			"2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new()
		{
			LoadHandler = _ =>
				Task.FromException<IReadOnlyList<
					AntigravityConnectionPendingWork>>(
					new IOException("journal locked"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.Contains(
			"確認上次 Antigravity 連接是否完成",
			account.CurrentSnapshot?.Error,
			StringComparison.Ordinal);
		InvalidOperationException addException =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.AddAccountAsync(new AccountProfile(
					Guid.NewGuid(),
					ProviderKind.Antigravity,
					"另一張 Antigravity")));
		Assert.Contains(
			"暫時無法讀寫 Antigravity 連接進度",
			addException.Message,
			StringComparison.Ordinal);
		InvalidOperationException importException =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.ReplacePortableSettingsAsync(
					new PortableSettingsSnapshot(
						new[] { profile },
						UsageSortMode.Manual,
						UsageDisplayMode.Used)));
		Assert.Contains(
			"暫時無法讀寫 Antigravity 連接進度",
			importException.Message,
			StringComparison.Ordinal);
		Assert.Empty(pendingStore.CleanupActiveAttemptIdSets);

		pendingStore.LoadHandler = null;
		timeProvider.Advance(TimeSpan.FromMinutes(1));

		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.True(account.CanConnectProviderAccount);
		Assert.True(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(UsageRecoveryAction.None, account.RecoveryAction);
		Assert.DoesNotContain(
			"暫時無法讀寫 Antigravity 連接進度",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task PendingAntigravityConnection_WhenInitialUpsertFails_CurrentWorkWinsOverOldSameIdJournal()
	{
		DateTimeOffset now = DateTimeOffset.Parse(
			"2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity",
			ProviderAccountIdentity: "current@example.com");
		FakeAccountProfileStore profileStore = new(profile);
		FakeAntigravityConnectionPendingStore pendingStore = new()
		{
			UpsertHandler = (_, _, _) =>
				Task.FromException(new IOException("journal locked"))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		Assert.False(
			await viewModel.TryPersistOfficialAntigravityConnectionAsync(
				account));
		account.AbortProviderAccountChange();
		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.False(account.CanConnectProviderAccount);

		pendingStore.UpsertHandler = null;
		await pendingStore.UpsertAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(
					"old@example.com"));
		await pendingStore.MarkCacheCompletedAsync(profile.Id);
		timeProvider.Advance(TimeSpan.FromMinutes(1));

		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			Assert.Single(viewModel.Accounts)
				.Profile.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
		Assert.DoesNotContain(
			"暫時無法讀寫 Antigravity 連接進度",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task OfficialAntigravitySetup_WhenCompletionWriteFails_RestartVerifiesIntentAndContinues()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		FakeAntigravityConnectionPendingStore pendingStore = new();
		DashboardViewModel firstViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await firstViewModel.InitializeAsync();
		AccountUsageViewModel firstAccount =
			Assert.Single(firstViewModel.Accounts);
		firstAccount.BeginProviderAccountChange();
		Guid setupAttemptId = Guid.NewGuid();
		Assert.True(
			await firstViewModel.TryBeginOfficialAntigravitySetupIntentAsync(
				firstAccount,
				setupAttemptId));
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		Assert.True(
			Assert.Single(pendingStore.PendingWork).IsSetupPending);
		pendingStore.UpsertHandler = (_, _, _) =>
			Task.FromException(new IOException("completion write failed"));
		Assert.True(firstAccount.TryBeginProviderAccountChangeCommit());

		Assert.False(
			await firstViewModel.TryPersistAntigravityConnectionAsync(
				firstAccount,
				setupAttemptId,
				AntigravityMachineSetupSourceKind.OfficialPrint));
		firstAccount.AbortProviderAccountChange();

		AntigravityConnectionPendingWork durableSetupIntent =
			Assert.Single(pendingStore.PendingWork);
		Assert.True(durableSetupIntent.IsSetupPending);
		Assert.Empty(profileStore.SavedSnapshots);

		pendingStore.UpsertHandler = null;
		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.test",
						"AGY test",
						25,
						"used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity))
		};
		FakeUsageSnapshotStore restartedSnapshotStore = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			usageSnapshotStore: restartedSnapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.Empty(restartedRefreshCoordinator.RefreshRequests);
		Assert.True(
			Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(restartedAccount.Profile.ProviderAccountIdentity);
		Assert.False(restartedAccount.CanConnectProviderAccount);
		Assert.False(restartedAccount.CanExecuteRecoveryAction);
		Assert.True(
			await restartedViewModel
				.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			profile.Id,
			Assert.Single(restartedRefreshCoordinator.RefreshRequests).Id);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(restartedSnapshotStore.DeletedSnapshots));
		Assert.False(restartedAccount.ShowAntigravityDefaultConnectionAction);
		Assert.NotEqual(
			UsageRecoveryAction.ReconfigureUsageSource,
			restartedAccount.RecoveryAction);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WithoutReceipt_NeverUsesOldQuotaAsCompletionProof()
	{
		DateTimeOffset now = DateTimeOffset.Parse(
			"2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity",
			ProviderAccountIdentity: "old@example.com");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(
					profile.ProviderAccountIdentity),
			setupAttemptId);
		pendingStore.RemoveHandler = (_, _) =>
			Task.FromException(new IOException("journal locked"));
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("agy.test", "AGY test", 25, "used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Equal(
			"old@example.com",
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);

		pendingStore.RemoveHandler = null;
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			"old@example.com",
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhileHelperIsActive_WaitsForReceipt()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Guid setupAttemptId = Guid.NewGuid();
		Assert.True(
			await viewModel.TryBeginOfficialAntigravitySetupIntentAsync(
				account,
				setupAttemptId));

		Assert.False(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.True(account.IsProviderAccountChangeInProgress);
		Assert.True(await viewModel.CancelOfficialAntigravitySetupIntentAsync(
			account.Id,
			setupAttemptId));
		account.AbortProviderAccountChange();
	}

	[Theory]
	[InlineData(false, false, true)]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(true, true, false)]
	public void SetupProcessWait_OnlySkipsInactiveOrCompletedInProcessAttempt(
		bool isProcessInactive,
		bool isCurrentApplicationProcess,
		bool expected)
	{
		Assert.Equal(
			expected,
			DashboardViewModel.ShouldWaitForSetupProcessCompletion(
				isProcessInactive,
				isCurrentApplicationProcess));
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenInProcessActiveStateOutlivesDialog_DiscardsWithoutQuotaRead()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Active,
			process.ProcessId,
			process.ProcessStartTimeUtcTicks,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.True(account.CanConnectProviderAccount);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenInProcessActiveDiscardInitiallyFails_RetryClearsWithoutRestart()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		int removalAttempts = 0;
		FakeAntigravityConnectionPendingStore pendingStore = new()
		{
			RemoveHandler = (_, _) =>
			{
				removalAttempts++;
				return removalAttempts == 1
					? Task.FromException(new IOException(
						"Synthetic first journal removal failure."))
					: Task.CompletedTask;
			}
		};
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Active,
			process.ProcessId,
			process.ProcessStartTimeUtcTicks,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(1, removalAttempts);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Equal(2, removalAttempts);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Empty(refreshCoordinator.RefreshRequests);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenActiveHelperProcessIsDead_DiscardsWithoutQuotaRead()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Active,
			int.MaxValue,
			1,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Null(Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenProcessIdWasReused_DiscardsWithoutQuotaRead()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Active,
			process.ProcessId,
			process.ProcessStartTimeUtcTicks + 1,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Null(Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenLaunchWasInterrupted_WaitsForRegistrationGraceThenDiscards()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity parentProcess =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Launching,
			parentProcess.ProcessId,
			parentProcess.ProcessStartTimeUtcTicks,
			now.UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);

		timeProvider.Advance(TimeSpan.FromMinutes(3));
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Null(Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenHelperWinsRemovalRace_RetainsIntent()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity parentProcess =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Launching,
			parentProcess.ProcessId,
			parentProcess.ProcessStartTimeUtcTicks,
			now.AddMinutes(-3).UtcDateTime.Ticks));
		int removalAttempts = 0;
		pendingStore.TryRemoveLaunchingSetupAttemptHandler =
			(attemptId, launching, _) =>
			{
				removalAttempts++;
				pendingStore.AddSetupAttemptState(launching with
				{
					Phase = AntigravitySetupAttemptPhase.Active
				});
				return Task.FromResult(false);
			};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: new FixedTimeProvider(now),
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(1, removalAttempts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Empty(pendingStore.RemovedAccountIds);
		Assert.False(Assert.Single(viewModel.Accounts).CanConnectProviderAccount);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenLaunchTimestampIsFarInFuture_DoesNotRemainStuck()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity parentProcess =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.Launching,
			parentProcess.ProcessId,
			parentProcess.ProcessStartTimeUtcTicks,
			now.AddDays(1).UtcDateTime.Ticks));
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: new FixedTimeProvider(now),
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Null(Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingApprovalRequest_WhenLateReceiptArrives_AutomaticallyCompletes()
	{
		const string TargetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset now = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.ApprovalRequested,
			process.ProcessId,
			process.ProcessStartTimeUtcTicks,
			now.UtcDateTime.Ticks,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(TargetIdentity)));
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("agy.test", "AGY test", 25, "used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: TargetIdentity))
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);

		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			TargetIdentity);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(
			TargetIdentity,
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
	}

	[Fact]
	public async Task InitializeAsync_WithPendingAntigravityApproval_DoesNotStartProviderIo()
	{
		const string TargetIdentity = "hydrate-only@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			TargetIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => throw new InvalidOperationException(
				"Startup hydrate must not start provider I/O.")
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.CanExecuteRecoveryAction);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenAntigravityRecoveryIsBlocked_StartsHealthyAccountRefresh()
	{
		const string TargetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		const string HealthyIdentity = "healthy@example.com";
		AccountProfile pendingAntigravityProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		AccountProfile healthyCodexProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			pendingAntigravityProfile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.ApprovalRequested,
			int.MaxValue,
			1,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(TargetIdentity)));
		TaskCompletionSource<UsageSnapshot> antigravityVerificationResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> healthyRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (account.Id == pendingAntigravityProfile.Id)
				{
					return antigravityVerificationResult.Task;
				}

				healthyRefreshStarted.TrySetResult(true);
				return Task.FromResult(CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = HealthyIdentity
				});
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				pendingAntigravityProfile,
				healthyCodexProfile),
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		Task backgroundRefresh = viewModel.RefreshUsageInBackgroundAsync();

		try
		{
			await healthyRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.False(backgroundRefresh.IsCompleted);
			Assert.Contains(
				refreshCoordinator.RefreshRequests,
				request => request.Id == pendingAntigravityProfile.Id);
			Assert.Contains(
				refreshCoordinator.RefreshRequests,
				request => request.Id == healthyCodexProfile.Id);
		}
		finally
		{
			antigravityVerificationResult.TrySetResult(new UsageSnapshot(
				pendingAntigravityProfile,
				new[]
				{
					new UsageMetric(
						"agy.test",
						"AGY test",
						25,
						"used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: TargetIdentity));
		}

		await backgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(
			1,
			refreshCoordinator.RefreshRequests.Count(
				request => request.Id == pendingAntigravityProfile.Id));
		Assert.Equal(
			TargetIdentity,
			viewModel.Accounts.Single(
				account => account.Id == pendingAntigravityProfile.Id)
				.Profile.ProviderAccountIdentity);
		Assert.Equal(
			SnapshotStatus.Ready,
			viewModel.Accounts.Single(
				account => account.Id == healthyCodexProfile.Id)
				.CurrentSnapshot?.Status);
		Assert.Equal(
			HealthyIdentity,
			viewModel.Accounts.Single(
				account => account.Id == healthyCodexProfile.Id)
				.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task PendingApprovalRequest_WhenHelperDiedBeforeReceipt_VerifiesAndCompletes()
	{
		const string TargetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.ApprovalRequested,
			int.MaxValue,
			1,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(TargetIdentity)));
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("agy.test", "AGY test", 25, "used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: TargetIdentity))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.CanExecuteRecoveryAction);
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(
			TargetIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Single(profileStore.SavedSnapshots);
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.RefreshRequests).Id);
	}

	[Fact]
	public async Task PendingApprovalRequest_WhenAttemptStateConflictsWithReceipt_RemainsPendingWithoutQuotaRead()
	{
		const string StateIdentity = "state@example.com";
		const string ReceiptIdentity = "receipt@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		AntigravitySetupProcessIdentity process =
			AntigravitySetupProcessIdentity.CaptureCurrent();
		pendingStore.AddSetupAttemptState(new AntigravitySetupAttemptState(
			setupAttemptId,
			AntigravitySetupAttemptPhase.ApprovalRequested,
			process.ProcessId,
			process.ProcessStartTimeUtcTicks,
			DateTimeOffset.UtcNow.UtcDateTime.Ticks,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(StateIdentity)));
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			ReceiptIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.CanConnectProviderAccount);
		Assert.Contains(
			"損壞",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task PendingOfficialPrintSetup_WithMatchingReceipt_RestartCommitsLocalSessionIdentity()
	{
		const string TargetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			TargetIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("agy.test", "AGY test", 25, "used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: TargetIdentity))
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.CanExecuteRecoveryAction);
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(
			TargetIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			TargetIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.RefreshRequests).Id);
	}

	[Fact]
	public async Task PendingOfficialPrintSetup_WhenReceiptTargetMismatches_DoesNotCommit()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"approved@example.com");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.test",
						"AGY test",
						25,
						"used 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.CanExecuteRecoveryAction);
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, account.CurrentSnapshot?.Status);
		Assert.Empty(account.CurrentSnapshot?.Metrics ?? Array.Empty<UsageMetric>());
		Assert.Equal(UsageRecoveryAction.ConnectAccount, account.RecoveryAction);
		Assert.True(account.CanInvokeRecoveryAction);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.RefreshRequests).Id);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenVerificationIsRetryable_KeepsIntentAndRetriesWithoutAction()
	{
		DateTimeOffset now = DateTimeOffset.Parse(
			"2026-08-16T00:00:00Z");
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		bool isVerificationReady = false;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				isVerificationReady
					? new UsageSnapshot(
						account,
						new[]
						{
							new UsageMetric(
								"agy.test",
								"AGY test",
								25,
								"used 25%")
						},
						SourceTrust.OfficialExperimental,
						SnapshotStatus.Ready,
						DateTimeOffset.UtcNow,
						ProviderAccountIdentity:
							AntigravityOfficialPrintUsageClient
								.LocalSessionIdentity)
					: new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						DateTimeOffset.UtcNow,
						Error: "temporary verification failure",
						RecoveryAction: UsageRecoveryAction.Retry))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Single(refreshCoordinator.RefreshRequests);
		isVerificationReady = true;
		timeProvider.Advance(TimeSpan.FromMinutes(1));

		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Empty(pendingStore.PendingWork);
		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
	}

	[Fact]
	public async Task PendingAntigravitySetupIntent_WhenVerificationRequiresConnection_DiscardsIntentAndShowsAction()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAntigravityConnectionPendingStore pendingStore = new();
		Guid setupAttemptId = Guid.NewGuid();
		await pendingStore.BeginSetupAsync(
			profile.Id,
			DashboardViewModel
				.CreateAntigravityPendingProfileIdentityFingerprint(null),
			setupAttemptId);
		pendingStore.AddSetupApproval(
			setupAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				DateTimeOffset.UtcNow,
				Error: "Antigravity is not signed in.",
				RecoveryAction: UsageRecoveryAction.ConnectAccount))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await viewModel.InitializeAsync();
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.True(Assert.Single(pendingStore.PendingWork).IsSetupPending);
		Assert.True(
			await viewModel.RetryPendingAntigravityConnectionsNowAsync());

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Empty(pendingStore.PendingWork);
		Assert.True(account.CanConnectProviderAccount);
		Assert.True(account.CanInvokeRecoveryAction);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, account.RecoveryAction);
		Assert.Single(refreshCoordinator.RefreshRequests);
	}

	private sealed class FakeAntigravityReportedAccountSource :
		IAntigravityReportedAccountSource
	{
		private long _generation;
		private bool _hasFreshStatusLineInvocation;
		private AntigravityReportedAccount? _reportedAccount;

		internal AntigravityReportedAccount? ReportedAccount
		{
			get => _reportedAccount;
			set
			{
				if (Equals(_reportedAccount, value))
				{
					return;
				}

				_reportedAccount = value;
				_generation++;
			}
		}

		internal Func<int, CancellationToken, ValueTask<bool>>?
			RemoveHandler { get; set; }

		internal AntigravityReportedAccountObservationStatus Status { get; set; } =
			AntigravityReportedAccountObservationStatus.Available;

		internal Exception? ReadException { get; set; }

		internal int ReadCount { get; private set; }

		internal int RemoveCount { get; private set; }

		internal void SignalFreshStatusLineInvocation()
		{
			_generation++;
			_hasFreshStatusLineInvocation = true;
		}

		public ValueTask<AntigravityReportedAccountObservation> ReadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ReadCount++;

			if (ReadException is not null)
			{
				throw ReadException;
			}

			bool hasFreshStatusLineInvocation =
				_hasFreshStatusLineInvocation;
			_hasFreshStatusLineInvocation = false;
			return ValueTask.FromResult(
				new AntigravityReportedAccountObservation(
					_generation,
					ReportedAccount,
					hasFreshStatusLineInvocation,
					Status));
		}

		public ValueTask<bool> RemoveOwnedAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RemoveCount++;
			return RemoveHandler?.Invoke(RemoveCount, cancellationToken) ??
				ValueTask.FromResult(true);
		}
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private long _timestamp;
		private DateTimeOffset _utcNow;

		internal FixedTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		public override long GetTimestamp()
		{
			return _timestamp;
		}

		internal void Advance(TimeSpan elapsed)
		{
			_utcNow += elapsed;
			_timestamp += elapsed.Ticks;
		}
	}

	private sealed class FakeAntigravityVerifiedAccountBindingStore :
		IAntigravityVerifiedAccountBindingStore
	{
		private readonly Dictionary<Guid, AntigravityVerifiedAccountBinding>
			_bindings = new();

		internal FakeAntigravityVerifiedAccountBindingStore(
			params AntigravityVerifiedAccountBinding[] bindings)
		{
			foreach (AntigravityVerifiedAccountBinding binding in bindings)
			{
				_bindings[binding.AccountId] = binding;
			}
		}

		internal List<Guid> LoadedAccountIds { get; } = new();

		internal List<AntigravityVerifiedAccountBinding> SavedBindings { get; } =
			new();

		internal List<Guid> DeletedAccountIds { get; } = new();

		public Task<AntigravityVerifiedAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadedAccountIds.Add(accountId);
			_bindings.TryGetValue(accountId, out AntigravityVerifiedAccountBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			AntigravityVerifiedAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SavedBindings.Add(binding);
			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeletedAccountIds.Add(accountId);
			_bindings.Remove(accountId);
			return Task.CompletedTask;
		}
	}

	[Fact]
	public async Task InitializeAsync_WithUnconnectedAgyCard_RemovesOwnedStatusLineOnce()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			IsEnabled: true,
			ProviderAccountIdentity: null);
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		Assert.Equal(1, reportedAccountSource.RemoveCount);
		Assert.Equal(0, reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyArtifactRemovalKeepsFailing_RetriesWithBackoffAndStopsAfterLimit()
	{
		DateTimeOffset now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			IsEnabled: true,
			ProviderAccountIdentity: null);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			RemoveHandler = (_, _) => ValueTask.FromResult(false)
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);

		await viewModel.InitializeAsync();

		for (int index = 0; index < 5; index++)
		{
			await viewModel.RefreshUsageAsync();
		}
		Assert.Equal(1, reportedAccountSource.RemoveCount);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await viewModel.RefreshUsageAsync();
		await viewModel.RefreshUsageAsync();
		Assert.Equal(2, reportedAccountSource.RemoveCount);

		timeProvider.Advance(TimeSpan.FromMinutes(2));
		await viewModel.RefreshUsageAsync();
		Assert.Equal(3, reportedAccountSource.RemoveCount);

		timeProvider.Advance(TimeSpan.FromDays(1));
		await viewModel.RefreshUsageAsync();
		Assert.Equal(3, reportedAccountSource.RemoveCount);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyArtifactRemovalThrows_RetriesAfterBackoff()
	{
		DateTimeOffset now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			IsEnabled: true,
			ProviderAccountIdentity: null);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			RemoveHandler = (attempt, _) => attempt == 1
				? throw new IOException("synthetic removal failure")
				: ValueTask.FromResult(true)
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		Assert.Equal(1, reportedAccountSource.RemoveCount);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await viewModel.RefreshUsageAsync();
		await viewModel.RefreshUsageAsync();

		Assert.Equal(2, reportedAccountSource.RemoveCount);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhileAgyAccountChangeIsInProgress_DoesNotRemoveStatusLineArtifacts()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);

		account.BeginProviderAccountChange();
		await viewModel.RefreshUsageAsync();

		Assert.True(account.IsProviderAccountChangeInProgress);
		Assert.Equal(0, reportedAccountSource.RemoveCount);
	}

	[Fact]
	public async Task CreatePortableSettingsSnapshotAsync_CapturesManualOrderAndModes()
	{
		AccountProfile codexAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		AccountProfile claudeAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore accountStore = new(
			codexAccount,
			claudeAccount);
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			LoadedSortMode = UsageSortMode.Automatic,
			LoadedDisplayMode = UsageDisplayMode.Remaining
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		PortableSettingsSnapshot snapshot =
			await viewModel.CreatePortableSettingsSnapshotAsync();

		Assert.Equal(
			new[] { claudeAccount.Id, codexAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.Equal(
			new[] { codexAccount.Id, claudeAccount.Id },
			snapshot.Accounts.Select(account => account.Id));
		Assert.Equal(UsageSortMode.Automatic, snapshot.UsageSortMode);
		Assert.Equal(UsageDisplayMode.Remaining, snapshot.UsageDisplayMode);
		Assert.Equal(
			"依服務、主要用量週期與重置時間排序，不依用量百分比排序。按一下切換為手動。",
			viewModel.SortModeToolTip);
		Assert.Empty(accountStore.SavedSnapshots);
	}

	[Fact]
	public async Task CreatePortableSettingsSnapshotAsync_CapturesWidgetPreferences()
	{
		PortableWidgetPreferences widgetPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			Corner: FloatingWidgetCorner.TopLeft,
			Theme: AppTheme.Light,
			IsHeightFollowingCardCount: true);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();

		PortableSettingsSnapshot snapshot =
			await viewModel.CreatePortableSettingsSnapshotAsync(widgetPreferences);

		Assert.Equal(widgetPreferences, snapshot.WidgetPreferences);
	}

	[Fact]
	public async Task RecoverDashboardPreferencesAsync_AwaitDeltaUsesLatestShellPreferences()
	{
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();
		DashboardShellPreferences currentShellPreferences =
			DashboardShellPreferences.Default;
		DashboardShellPreferences? appliedShellPreferences = null;
		DelayedDashboardPreferencesRecoveryStore recoveryStore = new()
		{
			StoredPreferences = new DashboardPreferencesSnapshot(
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				DashboardShellPreferences.Default with
				{
					IsWidgetVisible = false,
					IsCollapsed = true,
					IsTopmost = false
				})
		};

		Task<DashboardPreferencesRecoveryStatus> recoveryTask =
			viewModel.RecoverDashboardPreferencesAsync(
				recoveryStore,
				() => currentShellPreferences,
				preferences => appliedShellPreferences = preferences);
		await recoveryStore.FirstCommitStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5));
		currentShellPreferences = currentShellPreferences with
		{
			Corner = FloatingWidgetCorner.TopLeft
		};
		recoveryStore.AllowFirstCommit.TrySetResult();

		Assert.Equal(
			DashboardPreferencesRecoveryStatus.Ready,
			await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Equal(2, recoveryStore.CommitCount);
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.NotNull(appliedShellPreferences);
		Assert.False(appliedShellPreferences.IsWidgetVisible);
		Assert.True(appliedShellPreferences.IsCollapsed);
		Assert.False(appliedShellPreferences.IsTopmost);
		Assert.Equal(
			FloatingWidgetCorner.TopLeft,
			appliedShellPreferences.Corner);
	}

	[Fact]
	public async Task ReplaceAndUndoPortableSettings_PreservesLocalShellFieldsAndRestoresPortableFields()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		DashboardShellPreferences shellBeforeImport = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget,
			Theme: AppTheme.Midnight,
			IsHeightFollowingCardCount: false);
		PortableWidgetPreferences importedWidgetPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			Corner: FloatingWidgetCorner.TopLeft,
			Theme: AppTheme.Light,
			IsHeightFollowingCardCount: true);

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[account],
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				importedWidgetPreferences),
			shellBeforeImport);

		DashboardShellPreferences importedShell = Assert.Single(
			preferencesStore.SavedPortablePreferences).ShellPreferences;
		Assert.False(importedShell.IsWidgetVisible);
		Assert.True(importedShell.IsCollapsed);
		Assert.False(importedShell.IsTopmost);
		Assert.Equal(@"\\.\DISPLAY2", importedShell.MonitorDeviceName);
		Assert.Equal(FloatingWidgetCorner.TopLeft, importedShell.Corner);
		Assert.Equal(DashboardStartupSurface.Tray, importedShell.StartupSurface);
		Assert.Equal(AppTheme.Light, importedShell.Theme);
		Assert.True(importedShell.IsHeightFollowingCardCount);
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);

		DashboardShellPreferences shellBeforeUndo = importedShell with
		{
			MonitorDeviceName = @"\\.\DISPLAY3"
		};
		await viewModel.UndoLastPortableSettingsImportAsync(shellBeforeUndo);

		DashboardShellPreferences restoredShell =
			preferencesStore.SavedPortablePreferences[^1].ShellPreferences;
		Assert.True(restoredShell.IsWidgetVisible);
		Assert.False(restoredShell.IsCollapsed);
		Assert.True(restoredShell.IsTopmost);
		Assert.Equal(@"\\.\DISPLAY3", restoredShell.MonitorDeviceName);
		Assert.Equal(FloatingWidgetCorner.BottomRight, restoredShell.Corner);
		Assert.Equal(DashboardStartupSurface.Widget, restoredShell.StartupSurface);
		Assert.Equal(AppTheme.Midnight, restoredShell.Theme);
		Assert.False(restoredShell.IsHeightFollowingCardCount);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Contains(
			"浮窗設定與主題已還原",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task NotifyPortableWidgetPreferencesChanged_AfterImport_InvalidatesUndo()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		PortableWidgetPreferences importedWidgetPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			Corner: FloatingWidgetCorner.BottomRight,
			IsHeightFollowingCardCount: true);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();
		DashboardShellPreferences currentShell = DashboardShellPreferences.Default;
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[account],
				UsageSortMode.Manual,
				UsageDisplayMode.Used,
				importedWidgetPreferences),
			currentShell);
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);

		viewModel.NotifyPortableWidgetPreferencesChanged(
			importedWidgetPreferences with { IsWidgetVisible = false });
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);

		viewModel.NotifyPortableWidgetPreferencesChanged(
			importedWidgetPreferences with
			{
				IsHeightFollowingCardCount = false
			});

		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
	}

	[Fact]
	public async Task NotifyPortableWidgetPreferencesChanged_AfterThemeImport_InvalidatesUndo()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		PortableWidgetPreferences importedWidgetPreferences = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			Corner: FloatingWidgetCorner.BottomRight,
			Theme: AppTheme.Midnight);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[account],
				UsageSortMode.Manual,
				UsageDisplayMode.Used,
				importedWidgetPreferences),
			DashboardShellPreferences.Default);

		viewModel.NotifyPortableWidgetPreferencesChanged(
			importedWidgetPreferences with { Theme = AppTheme.Light });

		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
	}

	[Fact]
	public async Task ReplaceAndUndoPortableSettings_WithLegacyFields_PreservesCurrentValues()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		DashboardShellPreferences shellBeforeImport =
			DashboardShellPreferences.Default with
			{
				Theme = AppTheme.Midnight,
				IsHeightFollowingCardCount = true
			};
		PortableWidgetPreferences legacyWidgetPreferences = new(
			IsWidgetVisible: false,
			IsCollapsed: true,
			IsTopmost: false,
			Corner: FloatingWidgetCorner.TopLeft,
			Theme: null,
			IsHeightFollowingCardCount: null);

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[account],
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining,
				legacyWidgetPreferences),
			shellBeforeImport);

		DashboardShellPreferences importedShell = Assert.Single(
			preferencesStore.SavedPortablePreferences).ShellPreferences;
		Assert.Equal(AppTheme.Midnight, importedShell.Theme);
		Assert.True(importedShell.IsHeightFollowingCardCount);
		DashboardShellPreferences shellAfterThemeChange = importedShell with
		{
			Theme = AppTheme.Light,
			IsHeightFollowingCardCount = false
		};
		viewModel.NotifyPortableWidgetPreferencesChanged(
			legacyWidgetPreferences with
			{
				Theme = AppTheme.Light,
				IsHeightFollowingCardCount = false
			});
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);

		await viewModel.UndoLastPortableSettingsImportAsync(
			shellAfterThemeChange);

		DashboardShellPreferences restoredShell =
			preferencesStore.SavedPortablePreferences[^1].ShellPreferences;
		Assert.Equal(AppTheme.Light, restoredShell.Theme);
		Assert.False(restoredShell.IsHeightFollowingCardCount);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAccountSaveFails_RestoresWidgetPreferences()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore accountStore = new(account);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		accountStore.ShouldFailSave = true;
		DashboardShellPreferences currentShell = new(
			IsWidgetVisible: true,
			IsCollapsed: false,
			IsTopmost: true,
			MonitorDeviceName: @"\\.\DISPLAY2",
			Corner: FloatingWidgetCorner.BottomRight,
			StartupSurface: DashboardStartupSurface.Widget);

		await Assert.ThrowsAsync<AccountProfileStoreException>(() =>
			viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					[account],
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining,
					new PortableWidgetPreferences(
						IsWidgetVisible: false,
						IsCollapsed: true,
						IsTopmost: false,
						Corner: FloatingWidgetCorner.TopLeft)),
				currentShell));

		Assert.Equal(2, preferencesStore.SavedPortablePreferences.Count);
		Assert.Equal(
			currentShell,
			preferencesStore.SavedPortablePreferences[^1].ShellPreferences);
		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.Equal(account, Assert.Single(viewModel.Accounts).Profile);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_AppliesModesAndRestoresImportedManualOrder()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Existing");
		AccountProfile importedCodexAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex",
			ProviderAccountIdentity: "codex@example.com");
		AccountProfile importedClaudeAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { importedCodexAccount, importedClaudeAccount },
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		IReadOnlyList<AccountProfile> persistedAccounts = Assert.Single(
			accountStore.SavedSnapshots);
		Assert.Equal(
			new[] { importedCodexAccount.Id, importedClaudeAccount.Id },
			persistedAccounts.Select(account => account.Id));
		Assert.False(
			persistedAccounts.Single(
				account => account.Id == importedClaudeAccount.Id)
				.HasAcceptedClaudeQuotaRisk);
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.Equal(
			new[] { importedClaudeAccount.Id, importedCodexAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.True(
			viewModel.Accounts.Single(
				account => account.Id == importedClaudeAccount.Id)
				.RequiresClaudeQuotaRiskConsent);
		Assert.Equal(
			(UsageSortMode.Automatic, UsageDisplayMode.Remaining),
			Assert.Single(preferencesStore.SavedUsagePreferences));
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);

		await viewModel.ToggleUsageSortModeAsync();

		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Equal(
			new[] { importedCodexAccount.Id, importedClaudeAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task UndoLastPortableSettingsImportAsync_RestoresProfilesAndResetsClaudeMachineLocalIdentity()
	{
		AccountProfile previousClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Original Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile previousAntigravity = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Original AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountProfile importedClaude = previousClaude with
		{
			DisplayName = "Imported Claude"
		};
		AccountProfile importedAntigravity = previousAntigravity with
		{
			DisplayName = "Imported AGY"
		};
		AccountProfile[] previousProfiles =
		{
			previousClaude,
			previousAntigravity
		};
		FakeAccountProfileStore accountStore = new(previousProfiles);
		FakeDashboardPreferencesStore preferencesStore = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			preferencesStore,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { importedAntigravity, importedClaude },
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		Assert.True(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Null(viewModel.Accounts.Single(
			account => account.Id == previousAntigravity.Id)
			.Profile.ProviderAccountIdentity);
		Assert.False(viewModel.Accounts.Single(
			account => account.Id == previousClaude.Id)
			.Profile.HasAcceptedClaudeQuotaRisk);

		cleanupPendingStore.UpsertedAccounts.Clear();
		bool cleanupIntentWasDurableBeforeAccountSave = false;
		accountStore.SaveHandler = (_, _) =>
		{
			cleanupIntentWasDurableBeforeAccountSave =
				cleanupPendingStore.UpsertedAccounts.Contains(
					(importedClaude.Id, importedClaude.Provider)) &&
				cleanupPendingStore.UpsertedAccounts.Contains(
					(importedAntigravity.Id, importedAntigravity.Provider));
			return Task.CompletedTask;
		};
		await viewModel.UndoLastPortableSettingsImportAsync();

		PortableSettingsSnapshot restored =
			await viewModel.CreatePortableSettingsSnapshotAsync();
		AccountProfile[] expectedRestoredProfiles =
		[
			previousClaude with { ProviderAccountIdentity = null },
			previousAntigravity
		];
		Assert.True(cleanupIntentWasDurableBeforeAccountSave);
		Assert.Equal(expectedRestoredProfiles, restored.Accounts);
		Assert.Equal(UsageSortMode.Manual, restored.UsageSortMode);
		Assert.Equal(UsageDisplayMode.Used, restored.UsageDisplayMode);
		Assert.Equal(expectedRestoredProfiles, accountStore.SavedSnapshots[^1]);
		Assert.Equal(
			new[]
			{
				(UsageSortMode.Automatic, UsageDisplayMode.Remaining),
				(UsageSortMode.Manual, UsageDisplayMode.Used)
			},
			preferencesStore.SavedUsagePreferences);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Contains(
			"部分用量可能需要重新檢查",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"浮窗設定已還原",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task UndoLastPortableSettingsImportAsync_WhenPreviousSettingsContainGrok_KeepsMachineLocalIdentityReset()
	{
		AccountProfile previousGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Original Grok",
			ProviderAccountIdentity:
				GrokAccountBinding.CreatePublicBindingIdentity(Guid.NewGuid()));
		AccountProfile importedCodex = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex",
			ProviderAccountIdentity: "imported@example.com");
		FakeAccountProfileStore accountStore = new(previousGrok);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: new FakeAccountCleanupPendingStore());
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedCodex],
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));
		await viewModel.UndoLastPortableSettingsImportAsync();

		AccountProfile restored = Assert.Single(viewModel.Accounts).Profile;
		Assert.Equal(previousGrok.Id, restored.Id);
		Assert.Equal(ProviderKind.Grok, restored.Provider);
		Assert.Null(restored.ProviderAccountIdentity);
		Assert.Null(Assert.Single(accountStore.SavedSnapshots[^1])
			.ProviderAccountIdentity);
		Assert.Contains(
			"逐一重新連接需要使用的 Grok 帳號",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task UndoLastPortableSettingsImportAsync_WhenAccountSaveFails_KeepsImportedStateAndUndoAvailable()
	{
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Previous Claude",
			ProviderAccountIdentity: "previous@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Imported Copilot",
			ProviderAccountIdentity: "imported@example.com");
		FakeAccountProfileStore accountStore = new(previousAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { importedAccount },
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));
		accountStore.ShouldFailSave = true;

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.UndoLastPortableSettingsImportAsync());

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(
			importedAccount with { ProviderAccountIdentity = null },
			account.Profile);
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Equal(
			new[]
			{
				(UsageSortMode.Automatic, UsageDisplayMode.Remaining),
				(UsageSortMode.Manual, UsageDisplayMode.Used),
				(UsageSortMode.Automatic, UsageDisplayMode.Remaining)
			},
			preferencesStore.SavedUsagePreferences);
	}

	[Fact]
	public async Task UndoLastPortableSettingsImportAsync_WhenBackupUpdateFailsInsideTransaction_ReportsRollbackAndKeepsUndoAvailable()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Previous Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex");
		FakeAccountProfileStore accountStore = new(previousAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		string accountTarget = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string preferenceTarget = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(accountTarget, "old-accounts");
		await File.WriteAllTextAsync(preferenceTarget, "old-preferences");
		PortableSettingsImportTransaction transaction = new(
			Path.Combine(temporaryDirectory.Path, "transaction"),
			[accountTarget, preferenceTarget]);
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: preferencesStore,
			accountRuntimeStatePurger: null,
			portableSettingsImportTransaction: transaction);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedAccount],
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));
		accountStore.ShouldFailAfterCommit = true;

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.UndoLastPortableSettingsImportAsync());

		AccountProfileStoreException storeException =
			Assert.IsType<AccountProfileStoreException>(exception.InnerException);
		Assert.True(storeException.HasCommittedChanges);
		Assert.Contains("已保留", exception.Message, StringComparison.Ordinal);
		Assert.Equal(importedAccount.Id, Assert.Single(viewModel.Accounts).Id);
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(accountTarget));
		Assert.Equal("old-preferences", await File.ReadAllTextAsync(preferenceTarget));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_ResetsLegacyAntigravityBindingsAndKeepsFirstEnabledPrimary()
	{
		AccountProfile firstAntigravityAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"First Antigravity",
			ProviderAccountIdentity: "first@example.com");
		AccountProfile secondAntigravityAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Second Antigravity",
			ProviderAccountIdentity: "second@example.com");
		DashboardViewModel sourceViewModel = new(
			new FakeAccountProfileStore(
				firstAntigravityAccount,
				secondAntigravityAccount),
			new FakeUsageRefreshCoordinator());
		await sourceViewModel.InitializeAsync();
		PortableSettingsSnapshot snapshot =
			await sourceViewModel.CreatePortableSettingsSnapshotAsync();
		FakeAccountProfileStore targetAccountStore = new();
		DashboardViewModel targetViewModel = new(
			targetAccountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await targetViewModel.InitializeAsync();

		await targetViewModel.ReplacePortableSettingsAsync(snapshot);

		IReadOnlyList<AccountProfile> persistedAccounts = Assert.Single(
			targetAccountStore.SavedSnapshots);
		Assert.Equal(
			new[] { firstAntigravityAccount.Id, secondAntigravityAccount.Id },
			persistedAccounts.Select(account => account.Id));
		Assert.All(persistedAccounts, account => Assert.True(account.IsEnabled));
		Assert.All(
			persistedAccounts,
			account => Assert.Null(account.ProviderAccountIdentity));
		Assert.All(
			targetViewModel.Accounts,
			account => Assert.False(account.HasProviderAccountIdentity));
		Assert.True(targetViewModel.IsPrimaryEnabledAntigravityAccount(
			firstAntigravityAccount.Id));
		Assert.False(targetViewModel.IsPrimaryEnabledAntigravityAccount(
			secondAntigravityAccount.Id));
		Assert.Contains(
			"重新連接 Antigravity 帳號",
			targetViewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WithOnlyGrokCard_ReportsGrokReconnectWithoutAgyMessage()
	{
		AccountProfile importedGrokAccount = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Imported Grok");
		FakeAccountProfileStore accountStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedGrokAccount],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		Assert.Equal(
			importedGrokAccount.Id,
			Assert.Single(viewModel.Accounts).Id);
		Assert.Contains(
			"逐一重新連接 Grok 帳號",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"AGY",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_ForCodex_ClearsPublicAndPrivateBindingState()
	{
		string publicBindingIdentity =
			Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab").ToString("N");
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = new(
			existingAccount,
			[new UsageMetric("weekly", "週用量", 25, "舊資料 25%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: new FakeAccountCleanupPendingStore());
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[existingAccount],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.CurrentSnapshot);
		Assert.Contains(
			(existingAccount.Id, ProviderKind.Codex),
			snapshotStore.DeletedSnapshots);
		Assert.Contains(
			(existingAccount.Id, ProviderKind.Codex),
			runtimeStatePurger.PurgedAccounts);
		Assert.Contains(
			"使用 workspace 連接時，要重新輸入 workspace ID",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAgyCardAlreadyExists_ClearsMachineLocalBindingAndCache()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LocalSessionIdentity);
		UsageSnapshot cachedSnapshot = new(
			existingAccount,
			new[]
			{
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini 每週",
					25d,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new(
			new AntigravityVerifiedAccountBinding(
				existingAccount.Id,
				AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
					"person@example.com"),
				observedAt - TimeSpan.FromSeconds(2),
				observedAt,
				observedAt + TimeSpan.FromSeconds(1)));
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		Assert.NotNull(Assert.Single(viewModel.Accounts).CurrentSnapshot);

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { existingAccount },
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		AccountProfile savedProfile = Assert.Single(
			Assert.Single(accountStore.SavedSnapshots));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(savedProfile.ProviderAccountIdentity);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Null(account.CurrentSnapshot);
		Assert.Equal(AccountStatusKind.NotConnected, account.StatusKind);
		Assert.Equal("連接 Antigravity 帳號", account.AntigravityAccountActionText);
		Assert.Equal(
			(existingAccount.Id, existingAccount.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Equal(
			(existingAccount.Id, existingAccount.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Contains(existingAccount.Id, bindingStore.DeletedAccountIds);
		Assert.Contains(
			"重新連接 Antigravity 帳號",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_UnboundImportedAgyIsNotQueriedByOrdinaryRefreshes()
	{
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Imported AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			refreshCoordinator,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore());
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedAccount],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.False(account.CanRefreshUsage);

		await viewModel.RefreshUsageAsync();
		await viewModel.RefreshUsageInBackgroundAsync();
		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.HasConfirmedProviderAccountBinding);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_UnboundAgyExplicitConnectionCanQueryAndBind()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY");
		FakeAccountProfileStore accountStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				[new UsageMetric(
					"agy.gemini.weekly",
					"Gemini weekly",
					25d,
					"已使用 25%")],
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		DashboardViewModel viewModel = new(accountStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();

		await viewModel.RefreshUsageAfterInvalidationAsync(account.Id);

		AccountProfile request = Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Null(request.ProviderAccountIdentity);
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(accountStore.SavedSnapshots));
		Assert.Equal(LocalSessionIdentity, savedProfile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.True(account.HasConfirmedProviderAccountBinding);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenOldRefreshIgnoresCancellation_CompletesWithUndoAvailable()
	{
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Previous Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex");
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ =>
			{
				refreshStarted.TrySetResult(true);
				return refreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(previousAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountRefreshQuiesceTimeout: TimeSpan.FromMilliseconds(25));
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { importedAccount },
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining))
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(importedAccount.Id, Assert.Single(viewModel.Accounts).Id);
		Assert.True(viewModel.CanUndoLastPortableSettingsImport);
		Assert.False(viewModel.IsManagingAccounts);
		Assert.Contains(
			"尚未停止",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);

		refreshResult.TrySetResult(CreateReadySnapshot(previousAccount));
		await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(importedAccount.Id, Assert.Single(viewModel.Accounts).Id);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WithManyHungClaudeRefreshes_UsesOneBoundedQuiesceWindow()
	{
		const int AccountCount = 32;
		AccountProfile[] previousAccounts = Enumerable.Range(0, AccountCount)
			.Select(index => new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Claude,
				$"Claude {index}",
				HasAcceptedClaudeQuotaRisk: true))
			.ToArray();
		Dictionary<Guid, TaskCompletionSource<UsageSnapshot>> refreshResults =
			previousAccounts.ToDictionary(
				account => account.Id,
				_ => new TaskCompletionSource<UsageSnapshot>(
					TaskCreationOptions.RunContinuationsAsynchronously));
		TaskCompletionSource<bool> allRefreshesStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int startedCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref startedCount) == AccountCount)
				{
					allRefreshesStarted.TrySetResult(true);
				}

				return refreshResults[account.Id].Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(previousAccounts),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountRefreshQuiesceTimeout: TimeSpan.FromMilliseconds(50));
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await allRefreshesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				previousAccounts,
				UsageSortMode.Manual,
				UsageDisplayMode.Used))
			.WaitAsync(TimeSpan.FromSeconds(5));
		elapsed.Stop();

		Assert.True(
			elapsed.Elapsed < TimeSpan.FromSeconds(1),
			$"Quiescing {AccountCount} accounts took {elapsed.Elapsed}.");
		Assert.All(
			viewModel.Accounts,
			account => Assert.True(account.RequiresClaudeQuotaRiskConsent));

		foreach (AccountProfile account in previousAccounts)
		{
			refreshResults[account.Id].TrySetResult(CreateReadySnapshot(account));
		}

		await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.All(
			viewModel.Accounts,
			account => Assert.Null(account.CurrentSnapshot));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenProviderChangesForSameId_QuiescesAccountOnlyOnce()
	{
		Guid accountId = Guid.NewGuid();
		AccountProfile previousAccount = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			accountId,
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(previousAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedAccount],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(accountId, account.Id);
		Assert.Equal(ProviderKind.Antigravity, account.Provider);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.False(account.CanQueryUsage);
		Assert.Equal([accountId], refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenSameClaudeRefreshFinishesAfterConsentReset_DoesNotApplyResult()
	{
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Previous Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = previousAccount with
		{
			DisplayName = "Imported Claude"
		};
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ =>
			{
				refreshStarted.TrySetResult(true);
				return refreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(previousAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountRefreshQuiesceTimeout: TimeSpan.FromMilliseconds(25));
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { importedAccount },
				UsageSortMode.Manual,
				UsageDisplayMode.Used))
			.WaitAsync(TimeSpan.FromSeconds(5));

		AccountUsageViewModel importedViewModel = Assert.Single(viewModel.Accounts);
		Assert.True(importedViewModel.RequiresClaudeQuotaRiskConsent);
		Assert.Null(importedViewModel.CurrentSnapshot);

		refreshResult.TrySetResult(CreateReadySnapshot(previousAccount));
		await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(importedViewModel.RequiresClaudeQuotaRiskConsent);
		Assert.Null(importedViewModel.CurrentSnapshot);
		Assert.Empty(importedViewModel.UsageMetrics);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenManualClaudeRefreshWasQueuedBeforeConsentReset_DoesNotStartSecondQuery()
	{
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> firstRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> firstRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ =>
			{
				firstRefreshStarted.TrySetResult(true);
				return firstRefreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(previousAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountRefreshQuiesceTimeout: TimeSpan.FromMilliseconds(25));
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task queuedManualRefresh = viewModel.RefreshAccountUsageAsync(
			previousAccount.Id);

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { previousAccount },
				UsageSortMode.Manual,
				UsageDisplayMode.Used))
			.WaitAsync(TimeSpan.FromSeconds(5));
		firstRefreshResult.TrySetResult(CreateReadySnapshot(previousAccount));

		await Task.WhenAll(activeRefresh, queuedManualRefresh)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(refreshCoordinator.RefreshRequests);
		AccountUsageViewModel importedViewModel = Assert.Single(viewModel.Accounts);
		Assert.True(importedViewModel.RequiresClaudeQuotaRiskConsent);
		Assert.Null(importedViewModel.CurrentSnapshot);
	}

	[Fact]
	public async Task InitializeAsync_WithUnboundAgyCard_DoesNotRestoreOfficialMachineLocalCache()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Imported AGY");
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini 每週",
					25d,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.NotConnected, account.StatusKind);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task RestoreDeferredStartupDisplayStateAsync_WithoutBindingStoreWithFreshCorrelatedAgyCache_RestoresEmailEphemerally()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		UsageSnapshot cachedSnapshot = CreateReadyAntigravitySnapshot(
			profile,
			observedAt);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				"person@example.com",
				observedAt - TimeSpan.FromSeconds(5))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: new FixedTimeProvider(
				observedAt + TimeSpan.FromMinutes(1)));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot?.Status);
		Assert.Equal("目前登入的 Antigravity 帳號（上次確認）", account.AccountDisplayText);
		Assert.Equal(0, reportedAccountSource.ReadCount);

		await viewModel.RestoreDeferredStartupDisplayStateAsync();

		Assert.Equal(
			"person@example.com（上次確認）",
			account.AccountDisplayText);
		Assert.Equal(1, reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task InitializeAsync_WithPersistedReverseOrderAgyBinding_DoesNotRestoreEmail()
	{
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		DateTimeOffset capturedAt = observedAt + TimeSpan.FromSeconds(5);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new(
			new AntigravityVerifiedAccountBinding(
				profile.Id,
				AntigravityVerifiedAccountBinding
					.ComputeNormalizedEmailSha256(Email),
				capturedAt,
				observedAt,
				capturedAt + TimeSpan.FromSeconds(1)));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource:
				new FakeAntigravityReportedAccountSource
				{
					ReportedAccount = new AntigravityReportedAccount(
						Email,
						capturedAt)
				},
			timeProvider: new FixedTimeProvider(
				capturedAt + TimeSpan.FromMinutes(1)),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
		Assert.Empty(bindingStore.SavedBindings);
	}

	[Fact]
	public async Task InitializeAsync_WithOldCorrelatedAgyCacheAndNoPersistedBinding_DoesNotBackfill()
	{
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(
			2026,
			8,
			12,
			12,
			0,
			30,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				Email,
				observedAt - TimeSpan.FromSeconds(5))
		};
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: new FixedTimeProvider(
				observedAt + TimeSpan.FromHours(2)),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot?.Status);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
		Assert.Empty(bindingStore.SavedBindings);
	}

	[Fact]
	public async Task InitializeAsync_WithOldAgyCaptureShortlyAfterCachedQuota_DoesNotBackfillAmbiguousBinding()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(
			2026,
			8,
			13,
			5,
			58,
			16,
			TimeSpan.Zero);
		DateTimeOffset capturedAt = observedAt + TimeSpan.FromSeconds(50);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		JsonAntigravityVerifiedAccountBindingStore bindingStore = new(
			accountId => Path.Combine(
				temporaryDirectory.Path,
				"antigravity",
				accountId.ToString("N"),
				"account-display-binding-v1.json"));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource:
				new FakeAntigravityReportedAccountSource
				{
					ReportedAccount = new AntigravityReportedAccount(
						Email,
						capturedAt)
				},
			timeProvider: new FixedTimeProvider(
				capturedAt + TimeSpan.FromHours(2)),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot?.Status);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
		AntigravityVerifiedAccountBinding? savedBinding =
			await bindingStore.LoadAsync(profile.Id, CancellationToken.None);
		Assert.Null(savedBinding);
	}

	[Fact]
	public async Task InitializeAsync_WithOldAgyCaptureTooLongAfterCachedQuota_DoesNotBackfillBinding()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(
			2026,
			8,
			13,
			5,
			58,
			16,
			TimeSpan.Zero);
		DateTimeOffset capturedAt = observedAt + TimeSpan.FromSeconds(61);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource:
				new FakeAntigravityReportedAccountSource
				{
					ReportedAccount = new AntigravityReportedAccount(
						"unrelated@example.com",
						capturedAt)
				},
			timeProvider: new FixedTimeProvider(
				capturedAt + TimeSpan.FromHours(2)),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
		Assert.Empty(bindingStore.SavedBindings);
	}

	[Fact]
	public async Task RestoreDeferredStartupDisplayStateAsync_WhenAgyReportDoesNotMatchCachedQuota_KeepsLocalLoginLabel()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				"unrelated@example.com",
				observedAt + TimeSpan.FromSeconds(1))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: new FixedTimeProvider(
				observedAt + TimeSpan.FromMinutes(1)));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(0, reportedAccountSource.ReadCount);

		await viewModel.RestoreDeferredStartupDisplayStateAsync();

		Assert.Equal("目前登入的 Antigravity 帳號（上次確認）", account.AccountDisplayText);
		Assert.Equal(1, reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task RestoreDeferredStartupDisplayStateAsync_WhenMatchingAgyReportIsOldAndNoBindingStore_KeepsLocalLoginLabel()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				"old-account@example.com",
				observedAt - TimeSpan.FromSeconds(5))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: new FixedTimeProvider(
				observedAt + TimeSpan.FromMinutes(16)));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(0, reportedAccountSource.ReadCount);

		await viewModel.RestoreDeferredStartupDisplayStateAsync();

		Assert.Equal("目前登入的 Antigravity 帳號（上次確認）", account.AccountDisplayText);
		Assert.Equal(1, reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task RestoreDeferredStartupDisplayStateAsync_WhenVerifiedAgyBindingMatchesOldRawCapture_RestoresEmailAcrossRestart()
	{
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset capturedAt = now - TimeSpan.FromHours(2);
		DateTimeOffset quotaAt = capturedAt + TimeSpan.FromSeconds(5);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityReportedAccountSource source = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				Email,
				capturedAt,
				"Pro")
		};
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new(
			new AntigravityVerifiedAccountBinding(
				profile.Id,
				AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(Email),
				capturedAt,
				quotaAt,
				quotaAt + TimeSpan.FromSeconds(1)));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, quotaAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: source,
			timeProvider: new FixedTimeProvider(now),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(0, source.ReadCount);

		await viewModel.RestoreDeferredStartupDisplayStateAsync();

		Assert.Equal($"{Email}（上次確認）", account.AccountDisplayText);
		Assert.Equal("Pro", account.CurrentSnapshot!.PlanTier);
		Assert.Equal(1, source.ReadCount);
		Assert.Equal(profile.Id, Assert.Single(bindingStore.LoadedAccountIds));
		Assert.Empty(bindingStore.SavedBindings);
	}

	[Fact]
	public async Task InitializeAsync_WhenVerifiedAgyBindingHashDoesNotMatch_DoesNotRestoreOldCapture()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset capturedAt = now - TimeSpan.FromHours(2);
		DateTimeOffset quotaAt = capturedAt + TimeSpan.FromSeconds(5);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new(
			new AntigravityVerifiedAccountBinding(
				profile.Id,
				AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
					"other@example.com"),
				capturedAt,
				quotaAt,
				quotaAt + TimeSpan.FromSeconds(1)));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, quotaAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource:
				new FakeAntigravityReportedAccountSource
				{
					ReportedAccount = new AntigravityReportedAccount(
						"person@example.com",
						capturedAt)
				},
			timeProvider: new FixedTimeProvider(now),
			antigravityVerifiedAccountBindingStore: bindingStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
		Assert.Empty(bindingStore.SavedBindings);
	}

	[Fact]
	public async Task InitializeAsync_WhenAgySourceReturnsMalformedEmail_IgnoresDisplayMetadata()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(
			2026,
			8,
			13,
			10,
			0,
			0,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(
				CreateReadyAntigravitySnapshot(profile, observedAt)),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource:
				new FakeAntigravityReportedAccountSource
				{
					ReportedAccount = new AntigravityReportedAccount(
						"not an email",
						observedAt - TimeSpan.FromSeconds(5))
				},
			timeProvider: new FixedTimeProvider(
				observedAt + TimeSpan.FromMinutes(1)),
			antigravityVerifiedAccountBindingStore:
				new FakeAntigravityVerifiedAccountBindingStore());

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Contains("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain("@", account.AccountDisplayText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyMetadataMatchesSuccessfulQuotaCapture_PersistsPlanTier()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		FakeUsageSnapshotStore snapshotStore = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				reportedAccountSource.ReportedAccount =
					new AntigravityReportedAccount(
						"person@example.com",
						observedAt - TimeSpan.FromSeconds(5),
						"Pro");
				timeProvider.Advance(TimeSpan.FromSeconds(11));
				return Task.FromResult(new UsageSnapshot(
					account,
					new[]
					{
						new UsageMetric(
							"agy.gemini.weekly",
							"Gemini 每週",
							25d,
							"已使用 25%")
					},
					SourceTrust.OfficialExperimental,
					SnapshotStatus.Ready,
					observedAt,
					observedAt,
					ProviderAccountIdentity: LocalSessionIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("Antigravity · person@example.com", account.AccountName);
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		Assert.Equal("Pro", account.CurrentSnapshot!.PlanTier);
		Assert.Contains(
			snapshotStore.SavedSnapshots,
			snapshot => (snapshot.Account.Id == profile.Id) &&
				string.Equals(
					snapshot.PlanTier,
					"Pro",
					StringComparison.Ordinal));
		Assert.Equal(2, reportedAccountSource.ReadCount);
		AntigravityVerifiedAccountBinding savedBinding =
			Assert.Single(bindingStore.SavedBindings);
		Assert.Equal(profile.Id, savedBinding.AccountId);
		Assert.Equal(
			AntigravityVerifiedAccountBinding.ComputeNormalizedEmailSha256(
				"person@example.com"),
			savedBinding.NormalizedEmailSha256);
		Assert.Equal(
			observedAt - TimeSpan.FromSeconds(5),
			savedBinding.SourceCapturedAtUtc);
		Assert.Equal(observedAt, savedBinding.QuotaObservedAtUtc);
		Assert.Equal(
			observedAt + TimeSpan.FromSeconds(1),
			savedBinding.VerifiedAtUtc);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenSameAgyEmailIsRecapturedInsideThrottleWindow_PersistsEffectiveTimestamp()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset setupCapturedAt = new(
			2026,
			8,
			12,
			12,
			0,
			0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			setupCapturedAt + TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				"person@example.com",
				setupCapturedAt)
		};
		JsonAntigravityVerifiedAccountBindingStore bindingStore = new(
			accountId => Path.Combine(
				temporaryDirectory.Path,
				"antigravity",
				accountId.ToString("N"),
				"account-display-binding-v1.json"));
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				// The helper observed the same email during this /usage call,
				// while the capture file retained the setup timestamp because it
				// was still inside the 30-second disk-throttle window.
				reportedAccountSource.SignalFreshStatusLineInvocation();
				timeProvider.Advance(TimeSpan.FromSeconds(59));
				DateTimeOffset observedAt = timeProvider.GetUtcNow();
				return Task.FromResult(new UsageSnapshot(
					account,
					new[]
					{
						new UsageMetric(
							"agy.gemini.weekly",
							"Gemini 每週",
							25d,
							"已使用 25%")
					},
					SourceTrust.OfficialExperimental,
					SnapshotStatus.Ready,
					observedAt,
					observedAt,
					ProviderAccountIdentity: LocalSessionIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("Antigravity · person@example.com", account.AccountName);
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		Assert.Equal(setupCapturedAt, reportedAccountSource.ReportedAccount?.CapturedAtUtc);
		Assert.Equal(2, reportedAccountSource.ReadCount);
		AntigravityVerifiedAccountBinding savedBinding = Assert.IsType<
			AntigravityVerifiedAccountBinding>(
			await bindingStore.LoadAsync(profile.Id, CancellationToken.None));
		Assert.Equal(
			setupCapturedAt + TimeSpan.FromSeconds(10),
			savedBinding.SourceCapturedAtUtc);
		Assert.Equal(
			setupCapturedAt + TimeSpan.FromSeconds(69),
			savedBinding.QuotaObservedAtUtc);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyIsCoolingDown_PreservesReportedEmailWithoutReadingSource()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref refreshCount) == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							"person@example.com",
							observedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
				}

				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					observedAt));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		int sourceReadCountAfterLiveRefresh = reportedAccountSource.ReadCount;
		refreshCoordinator.RemainingCooldownHandler = _ =>
			TimeSpan.FromSeconds(30);

		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		Assert.Equal(
			sourceReadCountAfterLiveRefresh,
			reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhileAgyRefreshIsRunning_DoesNotFlickerToLocalLogin()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset firstObservedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		TaskCompletionSource secondRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> secondRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref refreshCount) == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							"person@example.com",
							firstObservedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
					return Task.FromResult(CreateReadyAntigravitySnapshot(
						account,
						firstObservedAt));
				}

				reportedAccountSource.SignalFreshStatusLineInvocation();
				secondRefreshStarted.TrySetResult();
				return secondRefreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		List<string> displayedIdentities = new();
		account.PropertyChanged += (_, args) =>
		{
			if ((args.PropertyName == nameof(AccountUsageViewModel.AccountName)) ||
				(args.PropertyName == nameof(
					AccountUsageViewModel.AccountDisplayText)))
			{
				displayedIdentities.Add(account.AccountDisplayText);
			}
		};

		Task refreshTask = viewModel.RefreshUsageAsync();
		await secondRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);

		timeProvider.Advance(TimeSpan.FromSeconds(5));
		secondRefreshResult.TrySetResult(CreateReadyAntigravitySnapshot(
			profile,
			timeProvider.GetUtcNow()));
		await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		Assert.DoesNotContain(
			displayedIdentities,
			identity => identity.Contains(
				"目前登入的 Antigravity 帳號",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenSuccessfulAgyQuotaHasNoFreshAccountCapture_FallsBackToLocalSessionLabel()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset firstObservedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		TaskCompletionSource secondRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> secondRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref refreshCount) == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							"old-account@example.com",
							firstObservedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
					return Task.FromResult(CreateReadyAntigravitySnapshot(
						account,
						firstObservedAt,
						"舊帳號用量"));
				}

				secondRefreshStarted.TrySetResult();
				return secondRefreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		List<string> displayedIdentities = new();
		account.PropertyChanged += (_, args) =>
		{
			if ((args.PropertyName == nameof(AccountUsageViewModel.AccountName)) ||
				(args.PropertyName == nameof(
					AccountUsageViewModel.AccountDisplayText)))
			{
				displayedIdentities.Add(account.AccountDisplayText);
			}
		};

		Task refreshTask = viewModel.RefreshUsageAsync();
		await secondRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(
			"old-account@example.com",
			account.AccountDisplayText);

		timeProvider.Advance(TimeSpan.FromSeconds(5));
		secondRefreshResult.TrySetResult(CreateReadyAntigravitySnapshot(
			profile,
			timeProvider.GetUtcNow(),
			"新帳號用量"));
		await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain(
			"old-account@example.com",
			account.AccountName,
			StringComparison.Ordinal);
		Assert.Contains(
			displayedIdentities,
			identity => identity.Contains(
				"目前登入的 Antigravity 帳號",
				StringComparison.Ordinal));
		Assert.Equal(
			"新帳號用量",
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Single(bindingStore.SavedBindings);

		FakeAntigravityReportedAccountSource restartedSource = new()
		{
			ReportedAccount = reportedAccountSource.ReportedAccount
		};
		FakeAntigravityVerifiedAccountBindingStore emptyRestartBindingStore = new();
		DashboardViewModel restartedViewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(account.CurrentSnapshot!),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: restartedSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: emptyRestartBindingStore);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.Contains("目前登入的 Antigravity 帳號", restartedAccount.AccountDisplayText);
		Assert.DoesNotContain("@", restartedAccount.AccountDisplayText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyAccountSourceIsTransientlyUnavailable_PreservesStaleEmailUntilSameAccountIsFresh()
	{
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset firstObservedAt = new(
			2026,
			8,
			13,
			10,
			0,
			30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				int currentRefresh = Interlocked.Increment(ref refreshCount);

				if (currentRefresh == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							Email,
							firstObservedAt - TimeSpan.FromSeconds(5));
				}
				else if (currentRefresh == 3)
				{
					reportedAccountSource.SignalFreshStatusLineInvocation();
				}

				timeProvider.Advance(TimeSpan.FromSeconds(
					currentRefresh == 1 ? 11 : 5));
				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					timeProvider.GetUtcNow()));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore:
				new FakeAntigravityVerifiedAccountBindingStore());
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(Email, account.AccountDisplayText);
		Assert.False(account.IsAntigravityReportedAccountEmailStale);

		reportedAccountSource.Status =
			AntigravityReportedAccountObservationStatus.Unavailable;
		await viewModel.RefreshUsageAsync();

		Assert.Equal($"{Email}（上次確認）", account.AccountDisplayText);
		Assert.True(account.IsAntigravityReportedAccountEmailStale);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);

		reportedAccountSource.Status =
			AntigravityReportedAccountObservationStatus.Available;
		await viewModel.RefreshUsageAsync();

		Assert.Equal(Email, account.AccountDisplayText);
		Assert.False(account.IsAntigravityReportedAccountEmailStale);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyAccountSourceThrows_PreservesStaleEmailThenSwitchesOnFreshAccount()
	{
		const string OriginalEmail = "first@example.com";
		const string ReplacementEmail = "second@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset firstObservedAt = new(
			2026,
			8,
			13,
			11,
			0,
			30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				int currentRefresh = Interlocked.Increment(ref refreshCount);

				if (currentRefresh == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							OriginalEmail,
							firstObservedAt - TimeSpan.FromSeconds(5));
				}
				else if (currentRefresh == 3)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							ReplacementEmail,
							timeProvider.GetUtcNow() +
								TimeSpan.FromSeconds(1));
				}

				timeProvider.Advance(TimeSpan.FromSeconds(
					currentRefresh == 1 ? 11 : 5));
				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					timeProvider.GetUtcNow()));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore:
				new FakeAntigravityVerifiedAccountBindingStore());
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(OriginalEmail, account.AccountDisplayText);

		reportedAccountSource.ReadException =
			new IOException("Transient status-line read failure.");
		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			$"{OriginalEmail}（上次確認）",
			account.AccountDisplayText);
		Assert.True(account.IsAntigravityReportedAccountEmailStale);

		reportedAccountSource.ReadException = null;
		await viewModel.RefreshUsageAsync();

		Assert.Equal(ReplacementEmail, account.AccountDisplayText);
		Assert.False(account.IsAntigravityReportedAccountEmailStale);
		Assert.DoesNotContain(
			OriginalEmail,
			account.AccountName,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyRefreshFails_PreservesReportedEmail()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset firstObservedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref refreshCount) == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							"person@example.com",
							firstObservedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
					return Task.FromResult(CreateReadyAntigravitySnapshot(
						account,
						firstObservedAt));
				}

				timeProvider.Advance(TimeSpan.FromSeconds(5));
				return Task.FromResult(new UsageSnapshot(
					account,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					timeProvider.GetUtcNow(),
					Error: "暫時無法讀取 AGY 用量。",
					ProviderAccountIdentity: LocalSessionIdentity,
					RecoveryAction: UsageRecoveryAction.Retry));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int sourceReadCountAfterSuccess = reportedAccountSource.ReadCount;

		await viewModel.RefreshUsageAsync();

		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.Equal(
			"person@example.com（上次確認）",
			account.AccountDisplayText);
		Assert.Equal(
			sourceReadCountAfterSuccess + 1,
			reportedAccountSource.ReadCount);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RefreshUsageAsync_AcrossRepeatedAgyAutomaticRecovery_PreservesVerifiedEmailAndLastUsage(
		bool firstAutomaticRetrySucceeds)
	{
		const string Email = "person@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		const string LastSuccessfulUsage = "自動重試前用量";
		DateTimeOffset firstObservedAt = new(
			2026,
			8,
			13,
			6,
			30,
			0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			firstObservedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		StatefulUsageProvider provider = new(
			ProviderKind.Antigravity,
			(account, callCount) =>
			{
				if (callCount == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							Email,
							firstObservedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
					return Task.FromResult(CreateReadyAntigravitySnapshot(
						account,
						firstObservedAt,
						LastSuccessfulUsage));
				}

				timeProvider.Advance(TimeSpan.FromSeconds(5));

				if (callCount == 2)
				{
					return Task.FromResult(new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "AGY 暫時逾時，稍後會自動重試。",
						RecoveryAction: UsageRecoveryAction.Retry));
				}

				if (callCount == 3)
				{
					if (firstAutomaticRetrySucceeds)
					{
						reportedAccountSource.ReportedAccount =
							new AntigravityReportedAccount(
								Email,
								timeProvider.GetUtcNow());
						return Task.FromResult(CreateReadyAntigravitySnapshot(
							account,
							timeProvider.GetUtcNow(),
							"自動重試後用量"));
					}

					return Task.FromResult(new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "AGY 自動安全重試仍然逾時，稍後會繼續自動重試。",
						RecoveryAction: UsageRecoveryAction.Retry));
				}

				if ((callCount == 4) && !firstAutomaticRetrySucceeds)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							Email,
							timeProvider.GetUtcNow());
					return Task.FromResult(CreateReadyAntigravitySnapshot(
						account,
						timeProvider.GetUtcNow(),
						"第二輪自動重試後用量"));
				}

				throw new InvalidOperationException(
					"AGY 自動恢復流程執行了非預期的用量讀取。");
			},
			TimeSpan.Zero);
		UsageRefreshCoordinator refreshCoordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(Email, account.AccountDisplayText);
		Assert.Equal(
			LastSuccessfulUsage,
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Single(bindingStore.SavedBindings);
		List<string> displayedIdentities = new();
		account.PropertyChanged += (_, args) =>
		{
			if ((args.PropertyName == nameof(AccountUsageViewModel.AccountName)) ||
				(args.PropertyName == nameof(
					AccountUsageViewModel.AccountDisplayText)))
			{
				displayedIdentities.Add(account.AccountDisplayText);
			}
		};

		// A recoverable timeout is projected as stale last-known-good data. It
		// must not expose a manual retry action while the bounded retry is pending.
		await viewModel.RefreshUsageAsync();

		Assert.Equal(2, provider.CallCount);
		Assert.Equal($"{Email}（上次確認）", account.AccountDisplayText);
		Assert.Equal(AccountStatusKind.Stale, account.StatusKind);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.False(account.CanExecuteRecoveryAction);
		Assert.Equal(
			LastSuccessfulUsage,
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Equal(
			TimeSpan.FromMinutes(1),
			refreshCoordinator.GetRemainingCooldown(profile));

		// A normal refresh tick inside the cooldown must not invoke AGY again or
		// disturb either the verified email or the last successful usage.
		await viewModel.RefreshUsageAsync();

		Assert.Equal(2, provider.CallCount);
		Assert.Equal($"{Email}（上次確認）", account.AccountDisplayText);
		Assert.Equal(
			LastSuccessfulUsage,
			Assert.Single(account.UsageMetrics).DisplayValue);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await viewModel.RefreshUsageAsync();

		Assert.Equal(3, provider.CallCount);
		Assert.Equal(
			firstAutomaticRetrySucceeds
				? Email
				: $"{Email}（上次確認）",
			account.AccountDisplayText);

		if (firstAutomaticRetrySucceeds)
		{
			Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
			Assert.Equal(UsageRecoveryAction.None, account.RecoveryAction);
			Assert.Equal(
				"自動重試後用量",
				Assert.Single(account.UsageMetrics).DisplayValue);
		}
		else
		{
			Assert.Equal(AccountStatusKind.Stale, account.StatusKind);
			Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
			Assert.False(account.CanExecuteRecoveryAction);
			Assert.Equal(
				LastSuccessfulUsage,
				Assert.Single(account.UsageMetrics).DisplayValue);
			Assert.Equal(
				TimeSpan.FromMinutes(2),
				refreshCoordinator.GetRemainingCooldown(profile));

			// A second confirmed timeout remains an automatic state. The normal
			// refresh loop applies its increasing backoff without asking the user.
			await viewModel.RefreshUsageAsync();
			Assert.Equal(3, provider.CallCount);
			Assert.Equal($"{Email}（上次確認）", account.AccountDisplayText);
			timeProvider.Advance(TimeSpan.FromMinutes(2));
			await viewModel.RefreshUsageAsync();

			Assert.Equal(4, provider.CallCount);
			Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
			Assert.Equal(UsageRecoveryAction.None, account.RecoveryAction);
			Assert.Equal(Email, account.AccountDisplayText);
			Assert.Equal(
				"第二輪自動重試後用量",
				Assert.Single(account.UsageMetrics).DisplayValue);
		}

		Assert.NotEmpty(displayedIdentities);
		Assert.All(
			displayedIdentities,
			identity => Assert.Contains(Email, identity, StringComparison.Ordinal));
		Assert.Contains($"{Email}（上次確認）", displayedIdentities);
		Assert.DoesNotContain(
			displayedIdentities,
			identity => identity.Contains(
				"目前登入的 Antigravity 帳號",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RefreshUsageAsync_DuringAgyAccountChange_PreservesVerifiedEmailUntilAbort()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(CreateReadyAntigravitySnapshot(profile, observedAt));
		Assert.True(account.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt,
			TimeSpan.FromMinutes(1),
			isStale: false));

		account.BeginProviderAccountChange();
		await viewModel.RefreshUsageAsync();
		account.AbortProviderAccountChange();

		Assert.Equal(
			"person@example.com（上次確認）",
			account.AccountDisplayText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WithInactiveAgyCard_DoesNotClearPrimaryEmailDuringCooldown()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile primaryProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		AccountProfile inactiveProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"舊 AGY 卡",
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				Assert.Equal(primaryProfile.Id, account.Id);
				reportedAccountSource.ReportedAccount =
					new AntigravityReportedAccount(
						"person@example.com",
						observedAt - TimeSpan.FromSeconds(5));
				timeProvider.Advance(TimeSpan.FromSeconds(11));
				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					observedAt));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(primaryProfile, inactiveProfile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel primaryAccount = viewModel.Accounts.Single(
			account => account.Id == primaryProfile.Id);
		Assert.Equal(
			"person@example.com",
			primaryAccount.AccountDisplayText);
		int sourceReadCountAfterSuccess = reportedAccountSource.ReadCount;
		refreshCoordinator.RemainingCooldownHandler = account =>
			account.Id == primaryProfile.Id
				? TimeSpan.FromSeconds(30)
				: null;

		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			"person@example.com",
			primaryAccount.AccountDisplayText);
		Assert.Equal(
			sourceReadCountAfterSuccess,
			reportedAccountSource.ReadCount);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenAgyAliasChanges_PreservesReportedEmail()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				reportedAccountSource.ReportedAccount =
					new AntigravityReportedAccount(
						"person@example.com",
						observedAt - TimeSpan.FromSeconds(5));
				timeProvider.Advance(TimeSpan.FromSeconds(11));
				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					observedAt));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.UpdateAccountAsync(
			profile with { DisplayName = "工作帳號" });

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("工作帳號", account.AccountName);
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenRecentAgyEmailPredatesCurrentQuotaRefresh_DoesNotAttachItToNewQuota()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini 每週",
						25d,
						"新帳號用量 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				observedAt,
				observedAt,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		FakeAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReportedAccount = new AntigravityReportedAccount(
				"old-account@example.com",
				observedAt - TimeSpan.FromSeconds(5),
				"Wrong Plan")
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: new FixedTimeProvider(observedAt));
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("Antigravity · 目前登入的 Antigravity 帳號", account.AccountName);
		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.Equal(
			"新帳號用量 25%",
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.DoesNotContain(
			"old-account@example.com",
			account.AccountName,
			StringComparison.Ordinal);
		Assert.Null(account.CurrentSnapshot!.PlanTier);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAgyEmailIsNewerThanSuccessfulQuota_DoesNotApplyItToOldQuota()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 12, 12, 0, 30,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				reportedAccountSource.ReportedAccount =
					new AntigravityReportedAccount(
						"new-account@example.com",
						observedAt + TimeSpan.FromSeconds(1));
				timeProvider.Advance(TimeSpan.FromSeconds(12));
				return Task.FromResult(new UsageSnapshot(
					account,
					new[]
					{
						new UsageMetric(
							"agy.gemini.weekly",
							"Gemini 每週",
							25d,
							"舊帳號用量 25%")
					},
					SourceTrust.OfficialExperimental,
					SnapshotStatus.Ready,
					observedAt,
					observedAt,
					ProviderAccountIdentity: LocalSessionIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("Antigravity · 目前登入的 Antigravity 帳號", account.AccountName);
		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.Equal(
			"舊帳號用量 25%",
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.DoesNotContain(
			"new-account@example.com",
			account.AccountName,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAccountsAreRemoved_DeletesCachesAndPurgesRuntimeState()
	{
		AccountProfile removedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Removed Claude");
		AccountProfile retainedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Retained Codex");
		FakeAccountProfileStore accountStore = new(
			removedAccount,
			retainedAccount);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		bool cleanupIntentWasDurableBeforeAccountSave = false;
		accountStore.SaveHandler = (_, _) =>
		{
			cleanupIntentWasDurableBeforeAccountSave =
				cleanupPendingStore.UpsertedAccounts.Contains(
					(removedAccount.Id, removedAccount.Provider));
			return Task.CompletedTask;
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { retainedAccount },
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		Assert.Contains(
			(removedAccount.Id, removedAccount.Provider),
			snapshotStore.DeletedSnapshots);
		Assert.Contains(
			(retainedAccount.Id, retainedAccount.Provider),
			snapshotStore.DeletedSnapshots);
		Assert.Equal(2, snapshotStore.DeletedSnapshots.Count);
		Assert.Contains(
			(removedAccount.Id, removedAccount.Provider),
			runtimeStatePurger.PurgedAccounts);
		Assert.Contains(
			(retainedAccount.Id, retainedAccount.Provider),
			runtimeStatePurger.PurgedAccounts);
		Assert.Equal(2, runtimeStatePurger.PurgedAccounts.Count);
		Assert.True(cleanupIntentWasDurableBeforeAccountSave);
		Assert.Equal(
			"已匯入 1 個帳號、排序與顯示設定。 Codex 連接不會隨設定匯入，已重設。請逐一重新連接 Codex 帳號。使用 workspace 連接時，要重新輸入 workspace ID。",
			viewModel.AccountSettingsMessage);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenProcessStopsAfterAccountCommit_NextStartupCompletesPrewrittenCleanup()
	{
		AccountProfile previousAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Previous Codex",
			ProviderAccountIdentity: "previous@example.com");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported Claude",
			ProviderAccountIdentity: "imported@example.com");
		FakeAccountProfileStore accountStore = new(previousAccount)
		{
			FailureAfterPrimaryCommit = new InvalidOperationException(
				"測試用：帳號主檔提交後程序中止。")
		};
		FakeUsageSnapshotStore snapshotStore = new(
			CreateReadySnapshot(importedAccount));
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		DashboardViewModel interruptedViewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			new FakeAccountRuntimeStatePurger(),
			accountCleanupPendingStore: cleanupPendingStore);
		await interruptedViewModel.InitializeAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			interruptedViewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					new[] { importedAccount },
					UsageSortMode.Manual,
					UsageDisplayMode.Used)));
		interruptedViewModel.Dispose();

		Assert.Contains(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == importedAccount.Id) &&
				(work.Provider == importedAccount.Provider));

		FakeAccountRuntimeStatePurger recoveryPurger = new();
		DashboardViewModel restartedViewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			recoveryPurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restoredAccount = Assert.Single(
			restartedViewModel.Accounts);
		Assert.Equal(importedAccount.Id, restoredAccount.Id);
		Assert.Null(restoredAccount.CurrentSnapshot);
		Assert.Contains(
			(importedAccount.Id, importedAccount.Provider),
			snapshotStore.DeletedSnapshots);
		Assert.Contains(
			(importedAccount.Id, importedAccount.Provider),
			recoveryPurger.PurgedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(restartedViewModel.IsAccountCleanupBlocked(
			importedAccount.Id,
			importedAccount.Provider));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenRemovedAccountCleanupFails_ReportsEveryCleanupWarning()
	{
		AccountProfile cacheCleanupFailure = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Cache Cleanup Failure");
		AccountProfile runtimeCleanupFailure = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Runtime Cleanup Failure");
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteHandler = (accountId, _, _) =>
				accountId == cacheCleanupFailure.Id
					? Task.FromException(new IOException("測試用快取清理失敗。"))
					: Task.CompletedTask
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (accountId, _, _) =>
				accountId == runtimeCleanupFailure.Id
					? Task.FromException(new IOException("測試用執行狀態清理失敗。"))
					: Task.CompletedTask
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				cacheCleanupFailure,
				runtimeCleanupFailure),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				Array.Empty<AccountProfile>(),
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		Assert.Equal(
			new[]
			{
				(cacheCleanupFailure.Id, cacheCleanupFailure.Provider),
				(cacheCleanupFailure.Id, cacheCleanupFailure.Provider),
				(cacheCleanupFailure.Id, cacheCleanupFailure.Provider),
				(runtimeCleanupFailure.Id, runtimeCleanupFailure.Provider)
			},
			snapshotStore.DeletedSnapshots);
		Assert.Equal(
			new[]
			{
				(cacheCleanupFailure.Id, cacheCleanupFailure.Provider),
				(runtimeCleanupFailure.Id, runtimeCleanupFailure.Provider)
			},
			runtimeStatePurger.PurgedAccounts);
		Assert.Contains(
			"資料可能仍保留在這台電腦",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"多次嘗試後仍無法清除部分帳號的舊資料",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAccountSaveFails_RollsBackPreferencesAndLeavesUiUnchanged()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Existing Claude",
			ProviderAccountIdentity: "existing@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex",
			ProviderAccountIdentity: "imported@example.com");
		FakeAccountProfileStore accountStore = new(existingAccount)
		{
			ShouldFailSave = true
		};
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			LoadedSortMode = UsageSortMode.Automatic,
			LoadedDisplayMode = UsageDisplayMode.Remaining
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel originalAccountViewModel = Assert.Single(
			viewModel.Accounts);
		string originalMessage = viewModel.AccountSettingsMessage;

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					new[] { importedAccount },
					UsageSortMode.Manual,
					UsageDisplayMode.Used)));

		Assert.Same(originalAccountViewModel, Assert.Single(viewModel.Accounts));
		Assert.Equal(existingAccount, originalAccountViewModel.Profile);
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.Equal(originalMessage, viewModel.AccountSettingsMessage);
		Assert.False(viewModel.IsManagingAccounts);
		Assert.Single(accountStore.SavedSnapshots);
		Assert.Equal(
			new[]
			{
				(UsageSortMode.Manual, UsageDisplayMode.Used),
				(UsageSortMode.Automatic, UsageDisplayMode.Remaining)
			},
			preferencesStore.SavedUsagePreferences);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenBackupUpdateFailsInsideTransaction_RollsBackAndLeavesUiUnchanged()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Existing Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex");
		FakeAccountProfileStore accountStore = new(existingAccount)
		{
			ShouldFailAfterCommit = true
		};
		FakeDashboardPreferencesStore preferencesStore = new();
		string firstTarget = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string secondTarget = Path.Combine(temporaryDirectory.Path, "preferences.json");
		await File.WriteAllTextAsync(firstTarget, "old-accounts");
		await File.WriteAllTextAsync(secondTarget, "old-preferences");
		PortableSettingsImportTransaction transaction = new(
			Path.Combine(temporaryDirectory.Path, "transaction"),
			[firstTarget, secondTarget]);
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: preferencesStore,
			accountRuntimeStatePurger: null,
			portableSettingsImportTransaction: transaction);
		await viewModel.InitializeAsync();
		AccountUsageViewModel originalViewModel = Assert.Single(viewModel.Accounts);

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.ReplacePortableSettingsAsync(
					new PortableSettingsSnapshot(
						[importedAccount],
						UsageSortMode.Automatic,
						UsageDisplayMode.Remaining)));

		AccountProfileStoreException storeException =
			Assert.IsType<AccountProfileStoreException>(exception.InnerException);
		Assert.True(storeException.HasCommittedChanges);
		Assert.Contains("已還原", exception.Message, StringComparison.Ordinal);
		Assert.Same(originalViewModel, Assert.Single(viewModel.Accounts));
		Assert.Equal(existingAccount, originalViewModel.Profile);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.Equal("old-accounts", await File.ReadAllTextAsync(firstTarget));
		Assert.Equal("old-preferences", await File.ReadAllTextAsync(secondTarget));
		Assert.Equal(
			(UsageSortMode.Automatic, UsageDisplayMode.Remaining),
			Assert.Single(preferencesStore.SavedUsagePreferences));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenUnboundGrokTransactionRollsBack_PreservesRecoverableStateUntilSuccessfulRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile connectedGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recoverable Grok");
		AccountProfile importedGrok = connectedGrok with
		{
			DisplayName = "Imported Grok"
		};
		Guid unrelatedAccountId = Guid.NewGuid();
		FakeAccountProfileStore accountStore = new(connectedGrok)
		{
			ShouldFailAfterCommit = true
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (accountId, _, _) =>
				accountId == unrelatedAccountId
					? Task.FromException(new IOException(
						"測試用既有清理工作仍待重試。"))
					: Task.CompletedTask
		};
		string accountTarget = Path.Combine(
			temporaryDirectory.Path,
			"accounts.json");
		string preferenceTarget = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		await File.WriteAllTextAsync(accountTarget, "old-accounts");
		await File.WriteAllTextAsync(preferenceTarget, "old-preferences");
		PortableSettingsImportTransaction transaction = new(
			Path.Combine(temporaryDirectory.Path, "transaction"),
			[accountTarget, preferenceTarget]);
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: runtimeStatePurger,
			portableSettingsImportTransaction: transaction,
			accountCleanupPendingStore: cleanupPendingStore,
			grokConnectionPendingStore:
				new FakeGrokConnectionPendingStore());
		await viewModel.InitializeAsync();
		await cleanupPendingStore.UpsertAsync(
			new[] { (unrelatedAccountId, ProviderKind.Codex) });

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					[importedGrok],
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining)));
		await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.Equal(connectedGrok, Assert.Single(viewModel.Accounts).Profile);
		Assert.DoesNotContain(
			runtimeStatePurger.PurgedAccounts,
			purged =>
				(purged.AccountId == connectedGrok.Id) &&
				(purged.Provider == connectedGrok.Provider));
		Assert.DoesNotContain(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == connectedGrok.Id) &&
				(work.Provider == connectedGrok.Provider));
		Assert.Contains(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == unrelatedAccountId) &&
				(work.Provider == ProviderKind.Codex));
		Assert.False(viewModel.IsAccountCleanupBlocked(
			connectedGrok.Id,
			connectedGrok.Provider));

		accountStore.ShouldFailAfterCommit = false;
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedGrok],
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		Assert.Null(Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Equal(
			1,
			runtimeStatePurger.PurgedAccounts.Count(purged =>
				(purged.AccountId == connectedGrok.Id) &&
				(purged.Provider == connectedGrok.Provider)));
		Assert.Contains(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == unrelatedAccountId) &&
				(work.Provider == ProviderKind.Codex));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAccountIsReplacedBeforeCommit_PreservesExternalFilesAndUi()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string accountPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string preferencesPath = Path.Combine(
			temporaryDirectory.Path,
			"preferences.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Existing Codex");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Imported Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile nextAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Next Copilot");
		byte[] externalPrimary = "later-external-primary"u8.ToArray();
		byte[] externalBackup = "later-external-backup"u8.ToArray();
		SettingsPersistenceGate persistenceGate = new();
		PortableSettingsImportWriteTracker writeTracker = new();
		JsonAccountProfileStore accountStore = new(accountPath, writeTracker);
		await accountStore.LoadAsync();
		await accountStore.SaveAsync([existingAccount]);
		JsonDashboardPreferencesStore preferencesStore = new(
			preferencesPath,
			persistenceGate,
			portableSettingsImportWriteTracker: writeTracker);
		await preferencesStore.SaveUsagePreferencesAsync(
			UsageSortMode.Manual,
			UsageDisplayMode.Used);
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[accountPath, $"{accountPath}.bak", preferencesPath],
			flushCommittedMarkerToDisk: _ =>
			{
				File.WriteAllBytes(accountPath, externalPrimary);
				File.WriteAllBytes($"{accountPath}.bak", externalBackup);
				throw new IOException("Synthetic post-operation failure.");
			},
			persistenceGate: persistenceGate,
			onTargetsRestored:
				accountStore.SynchronizeLoadedFileStateAfterExternalRestoreAsync,
			onOperationStarting:
				accountStore.BeginPortableImportTransactionAsync,
			onTransactionFinished:
				accountStore.CompletePortableImportTransaction,
			writeTracker: writeTracker);
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: preferencesStore,
			accountRuntimeStatePurger: null,
			portableSettingsImportTransaction: transaction);
		await viewModel.InitializeAsync();
		AccountUsageViewModel originalViewModel = Assert.Single(viewModel.Accounts);

		await Assert.ThrowsAsync<IOException>(
			() => viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					[importedAccount],
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining)));

		Assert.Same(originalViewModel, Assert.Single(viewModel.Accounts));
		Assert.Equal(existingAccount, originalViewModel.Profile);
		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.False(viewModel.CanUndoLastPortableSettingsImport);
		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(
			externalBackup,
			await File.ReadAllBytesAsync($"{accountPath}.bak"));
		AccountProfileStoreException staleException =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => accountStore.SaveAsync([nextAccount]));
		Assert.Contains(
			"其他程序變更",
			staleException.Message,
			StringComparison.Ordinal);
		Assert.Equal(externalPrimary, await File.ReadAllBytesAsync(accountPath));
		Assert.Equal(
			externalBackup,
			await File.ReadAllBytesAsync($"{accountPath}.bak"));
		Assert.False(Directory.Exists(transactionPath));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenPreferenceSaveFails_DoesNotWriteAccountsOrChangeUi()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Existing Claude");
		AccountProfile importedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Imported Codex");
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			ShouldFailSave = true
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel originalAccountViewModel = Assert.Single(
			viewModel.Accounts);

		await Assert.ThrowsAsync<IOException>(
			() => viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					new[] { importedAccount },
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining)));

		Assert.Empty(accountStore.SavedSnapshots);
		Assert.Empty(preferencesStore.SavedUsagePreferences);
		Assert.Same(originalAccountViewModel, Assert.Single(viewModel.Accounts));
		Assert.Equal(existingAccount, originalAccountViewModel.Profile);
		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAccountConnectionIsActive_IsRejectedWithoutWrites()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.ReplacePortableSettingsAsync(
				new PortableSettingsSnapshot(
					new[] { existingAccount },
					UsageSortMode.Automatic,
					UsageDisplayMode.Remaining)));

		Assert.True(account.IsProviderAccountChangeInProgress);
		Assert.Empty(accountStore.SavedSnapshots);
		Assert.Empty(preferencesStore.SavedUsagePreferences);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenCopilotIdentityIsPresent_StripsMachineLocalIdentity()
	{
		string existingIdentity = CopilotAccountIdentityRules.Create(
			new CopilotAccountIdentity(
				"github.com",
				"NODE_EXISTING",
				1001,
				"existing"));
		string importedIdentity = CopilotAccountIdentityRules.Create(
			new CopilotAccountIdentity(
				"github.com",
				"NODE_IMPORTED",
				1002,
				"imported"));
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Existing Copilot",
			ProviderAccountIdentity: existingIdentity);
		AccountProfile conflictingAccount = existingAccount with
		{
			DisplayName = "Imported Copilot",
			ProviderAccountIdentity = importedIdentity
		};
		FakeAccountProfileStore accountStore = new(existingAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { conflictingAccount },
				UsageSortMode.Automatic,
				UsageDisplayMode.Remaining));

		AccountProfile expectedAccount = conflictingAccount with
		{
			ProviderAccountIdentity = null
		};
		Assert.Equal(expectedAccount, Assert.Single(viewModel.Accounts).Profile);
		Assert.Equal(
			expectedAccount,
			Assert.Single(Assert.Single(accountStore.SavedSnapshots)));
		Assert.Equal(
			(UsageSortMode.Automatic, UsageDisplayMode.Remaining),
			Assert.Single(preferencesStore.SavedUsagePreferences));
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenCommittedCleanupReceiptFails_PreservesIntentAndJournal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile connectedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Connected Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(
				new CopilotAccountIdentity(
					"github.com",
					"NODE_CONNECTED",
					1001,
					"connected")));
		AccountProfile importedCopilot = connectedCopilot with
		{
			DisplayName = "Imported Copilot",
			ProviderAccountIdentity = null
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(temporaryDirectory.Path, "transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
				throw new IOException("Synthetic committed cleanup receipt failure."));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(connectedCopilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: runtimeStatePurger,
			portableSettingsImportTransaction: transaction,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		PortableSettingsImportCommittedException exception =
			await Assert.ThrowsAsync<PortableSettingsImportCommittedException>(
				() => viewModel.ReplacePortableSettingsAsync(
					new PortableSettingsSnapshot(
						[importedCopilot],
						UsageSortMode.Automatic,
						UsageDisplayMode.Remaining)));

		Assert.Contains("已提交", exception.Message, StringComparison.Ordinal);
		Assert.Equal(importedCopilot, Assert.Single(viewModel.Accounts).Profile);
		AccountCleanupPendingWork pendingWork = Assert.Single(
			cleanupPendingStore.PendingWork);
		Assert.Equal(connectedCopilot.Id, pendingWork.AccountId);
		Assert.False(pendingWork.AllowRetainedPrivateStateDeletion);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		Assert.Contains(
			"重新啟動後會自動再試",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.False(string.IsNullOrWhiteSpace(
			viewModel.AccountSettingsHealthMessage));

		bool retryCompleted = await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.False(retryCompleted);
		Assert.Single(cleanupPendingStore.PendingWork);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));

		int upsertCountBeforeNextImport =
			cleanupPendingStore.UpsertedAccounts.Count;
		InvalidOperationException nextImportException =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.ReplacePortableSettingsAsync(
					new PortableSettingsSnapshot(
						[importedCopilot],
						UsageSortMode.Manual,
						UsageDisplayMode.Used)));

		Assert.Contains(
			"帳號清理尚未完成",
			nextImportException.Message,
			StringComparison.Ordinal);
		Assert.Equal(
			upsertCountBeforeNextImport,
			cleanupPendingStore.UpsertedAccounts.Count);
		Assert.False(Assert.Single(
			cleanupPendingStore.PendingWork)
			.AllowRetainedPrivateStateDeletion);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenOldCommittedCallbackStillFails_BlocksBeforeNewCleanupIntentUpsert()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int callbackAttempts = 0;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackAttempts++;
				throw new IOException(
					"Synthetic committed callback failure.");
			});
		await Assert.ThrowsAsync<PortableSettingsImportCommittedException>(
			() => transaction.ExecuteAsync(
				cancellationToken => File.WriteAllTextAsync(
					targetPath,
					"committed-accounts",
					cancellationToken)));
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));

		AccountProfile connectedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Connected Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(
				new CopilotAccountIdentity(
					"github.com",
					"NODE_CONNECTED",
					1001,
					"connected")));
		AccountProfile importedCopilot = connectedCopilot with
		{
			DisplayName = "Imported Copilot",
			ProviderAccountIdentity = null
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(connectedCopilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: runtimeStatePurger,
			portableSettingsImportTransaction: transaction,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.ReplacePortableSettingsAsync(
					new PortableSettingsSnapshot(
						[importedCopilot],
						UsageSortMode.Automatic,
						UsageDisplayMode.Remaining)));

		Assert.Contains(
			"帳號清理尚未完成",
			exception.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, callbackAttempts);
		Assert.Empty(cleanupPendingStore.UpsertedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Equal(connectedCopilot, Assert.Single(viewModel.Accounts).Profile);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenOldCommittedCallbackStillFails_BlocksBeforeFreshCleanupIntentUpsert()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		string transactionPath = Path.Combine(
			temporaryDirectory.Path,
			"transaction");
		await File.WriteAllTextAsync(targetPath, "old-accounts");
		int callbackAttempts = 0;
		PortableSettingsImportTransaction transaction = new(
			transactionPath,
			[targetPath],
			onTargetsCommitted: _ =>
			{
				callbackAttempts++;
				throw new IOException(
					"Synthetic committed callback failure.");
			});
		await Assert.ThrowsAsync<PortableSettingsImportCommittedException>(
			() => transaction.ExecuteAsync(
				cancellationToken => File.WriteAllTextAsync(
					targetPath,
					"committed-accounts",
					cancellationToken)));

		AccountProfile oldCommittedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Old committed Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(
				new CopilotAccountIdentity(
					"github.com",
					"NODE_OLD",
					1001,
					"old")));
		AccountProfile freshCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Fresh Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(
				new CopilotAccountIdentity(
					"github.com",
					"NODE_FRESH",
					1002,
					"fresh")));
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				oldCommittedCopilot,
				freshCopilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			portableSettingsImportTransaction: transaction,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.RemoveAccountAsync(freshCopilot.Id));

		Assert.Contains(
			"帳號清理尚未完成",
			exception.Message,
			StringComparison.Ordinal);
		Assert.Equal(3, callbackAttempts);
		Assert.Empty(cleanupPendingStore.UpsertedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Contains(
			viewModel.Accounts,
			account => account.Id == oldCommittedCopilot.Id);
		Assert.Contains(
			viewModel.Accounts,
			account => account.Id == freshCopilot.Id);
		Assert.True(File.Exists(Path.Combine(transactionPath, "committed")));
		Assert.False(File.Exists(Path.Combine(
			transactionPath,
			"commit-callback-completed")));
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenOldAuthorizedCleanupOverlaps_DrainsItBeforeFreshIntentUpsert()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile copilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot",
			ProviderAccountIdentity: CopilotAccountIdentityRules.Create(
				new CopilotAccountIdentity(
					"github.com",
					"NODE_CONNECTED",
					1001,
					"connected")));
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				copilot.Id,
				copilot.Provider,
				CachePending: true,
				RuntimePending: true,
				AllowRetainedPrivateStateDeletion: true));
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
				throw new IOException("Synthetic old cleanup failure.")
		};
		string targetPath = Path.Combine(temporaryDirectory.Path, "accounts.json");
		await File.WriteAllTextAsync(targetPath, "accounts");
		PortableSettingsImportTransaction transaction = new(
			Path.Combine(temporaryDirectory.Path, "transaction"),
			[targetPath]);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(copilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			portableSettingsImportTransaction: transaction,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		Assert.True(Assert.Single(
			cleanupPendingStore.PendingWork)
			.AllowRetainedPrivateStateDeletion);

		runtimeStatePurger.PurgeHandler = null;
		bool oldAuthorizedWorkWasDrainedBeforeUpsert = false;
		cleanupPendingStore.UpsertHandler = (_, _) =>
		{
			oldAuthorizedWorkWasDrainedBeforeUpsert =
				cleanupPendingStore.PendingWork.All(work =>
					!work.AllowRetainedPrivateStateDeletion);
			return Task.CompletedTask;
		};

		await viewModel.RemoveAccountAsync(copilot.Id);

		Assert.True(oldAuthorizedWorkWasDrainedBeforeUpsert);
		Assert.Equal(
			(copilot.Id, copilot.Provider),
			Assert.Single(cleanupPendingStore.UpsertedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.Empty(viewModel.Accounts);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WithoutClaudeConsent_SkipsOnlyClaude()
	{
		AccountProfile claudeAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		AccountProfile codexAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(claudeAccount, codexAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageInBackgroundAsync();

		AccountProfile request = Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(codexAccount.Id, request.Id);
		Assert.False(viewModel.Accounts[0].CanRefreshUsage);
		Assert.True(viewModel.Accounts[1].CanRefreshUsage);
	}

	[Fact]
	public async Task RefreshUsageAsync_WithoutClaudeConsent_SkipsClaudeAndRefreshesOtherProviders()
	{
		AccountProfile claudeAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		AccountProfile codexAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(claudeAccount, codexAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountProfile request = Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(codexAccount.Id, request.Id);
		Assert.True(Assert.Single(
			viewModel.Accounts,
			account => account.Id == claudeAccount.Id).RequiresClaudeQuotaRiskConsent);
	}

	[Fact]
	public async Task InitializeAsync_CleansUnpairedClaudeBindingAndKeepsPairedBinding()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding pairedBinding = ClaudeAccountBinding.Create(
			accountId,
			context);
		ClaudeAccountBinding orphanBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			context);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity:
				ClaudeAccountBinding.CreatePublicBindingIdentity(
					pairedBinding.PublicBindingId));
		FakeClaudeAccountBindingStore bindingStore = new(
			pairedBinding,
			orphanBinding);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Equal(pairedBinding, bindingStore.GetBinding(accountId));
		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Equal(
			new[] { orphanBinding.AccountId },
			bindingStore.DeletedAccountIds);
		Assert.DoesNotContain(
			"未配對的 Claude",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task InitializeAsync_WhenClaudeBindingCleanupFails_SkipsClaudeCacheAndWarns()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding pairedBinding = ClaudeAccountBinding.Create(
			accountId,
			context);
		ClaudeAccountBinding orphanBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				pairedBinding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[] { new UsageMetric("weekly", "週用量", 50, "舊資料 50%") },
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: context.AccountIdentity,
			SubscriptionScopeDisplayName: context.SubscriptionScopeDisplayName,
			PlanTier: context.PlanTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeClaudeAccountBindingStore bindingStore = new(
			pairedBinding,
			orphanBinding)
		{
			ShouldRetainDeletedBindings = true
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(orphanBinding, bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Contains(
			"無法確認或清理未配對的 Claude",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(AccountProfileLoadStatus.UnsupportedVersion)]
	[InlineData(AccountProfileLoadStatus.Unavailable)]
	[InlineData(AccountProfileLoadStatus.RecoveredCorruptFile)]
	public async Task InitializeAsync_WhenProfileInventoryIsNotAuthoritative_DoesNotDeleteClaudeBindings(
		AccountProfileLoadStatus loadStatus)
	{
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			context);
		FakeAccountProfileStore profileStore = new()
		{
			LoadStatusOnLoad = loadStatus,
			CanSaveOnLoad = loadStatus ==
				AccountProfileLoadStatus.RecoveredCorruptFile,
			LoadMessage = "Synthetic non-authoritative inventory."
		};
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Equal(binding, bindingStore.GetBinding(binding.AccountId));
		Assert.Empty(bindingStore.DeletedAccountIds);
	}

	[Fact]
	public async Task InitializeAsync_WithNonCanonicalClaudePublicIdentity_DeletesPrivateBinding()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity:
				binding.PublicBindingId.ToString("N").ToUpperInvariant());
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.Equal(new[] { accountId }, bindingStore.DeletedAccountIds);
	}

	[Fact]
	public void Constructor_WithClaudeBindingStoreButNoSharedGate_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new DashboardViewModel(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: new FakeClaudeAccountBindingStore()));
	}

	[Fact]
	public async Task InitializeAsync_CleansUnpairedCodexBindingAndKeepsPairedBinding()
	{
		Guid accountId = Guid.NewGuid();
		Guid workspaceId =
			Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
		CodexWorkspaceBinding pairedBinding = CodexWorkspaceBinding.Create(
			accountId,
			"person@example.com",
			workspaceId);
		CodexWorkspaceBinding orphanBinding = CodexWorkspaceBinding.Create(
			Guid.NewGuid(),
			"person@example.com",
			workspaceId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity:
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					pairedBinding.PublicBindingId));
		FakeCodexWorkspaceBindingStore bindingStore = new(
			pairedBinding,
			orphanBinding);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: new CodexBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Equal(pairedBinding, bindingStore.GetBinding(accountId));
		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Equal(
			new[] { orphanBinding.AccountId },
			bindingStore.DeletedAccountIds);
	}

	[Fact]
	public async Task InitializeAsync_WhenCodexBindingCleanupFails_SkipsCacheAndWarns()
	{
		Guid accountId = Guid.NewGuid();
		Guid workspaceId =
			Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
		CodexWorkspaceBinding pairedBinding = CodexWorkspaceBinding.Create(
			accountId,
			"person@example.com",
			workspaceId);
		CodexWorkspaceBinding orphanBinding = CodexWorkspaceBinding.Create(
			Guid.NewGuid(),
			"orphan@example.com",
			Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
		string publicBindingIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				pairedBinding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = new(
			profile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			PlanTier: "team",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeCodexWorkspaceBindingStore bindingStore = new(
			pairedBinding,
			orphanBinding)
		{
			ShouldRetainDeletedBindings = true
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: new CodexBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Contains(
			"無法確認或清理未配對的 Codex workspace 連接資料",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task InitializeAsync_WithRawCodexIdentity_RestoresUnverifiedCache()
	{
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"person@example.com",
			Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified);
		FakeCodexWorkspaceBindingStore bindingStore = new(binding);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: new CodexBindingCommitGate());

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("person@example.com", account.Profile.ProviderAccountIdentity);
		UsageSnapshot restored = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Stale, restored.Status);
		Assert.Equal("person@example.com", restored.ProviderAccountIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			restored.SubscriptionVerificationState);
		Assert.Null(bindingStore.GetBinding(accountId));
	}

	[Fact]
	public async Task InitializeAsync_WithRawCodexIdentityWithoutBindingStore_RestoresUnverifiedCache()
	{
		Guid accountId = Guid.NewGuid();
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		UsageSnapshot restored = Assert.IsType<UsageSnapshot>(
			Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Stale, restored.Status);
		Assert.Equal("person@example.com", restored.ProviderAccountIdentity);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			restored.SubscriptionVerificationState);
	}

	[Fact]
	public async Task InitializeAsync_WithRawCodexIdentity_RejectsVerifiedCache()
	{
		Guid accountId = Guid.NewGuid();
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
	}

	[Fact]
	public async Task InitializeAsync_WithPairedVerifiedCodexCache_RestoresSnapshot()
	{
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"person@example.com",
			Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
		string publicBindingIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = new(
			profile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			PlanTier: "team",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore:
				new FakeCodexWorkspaceBindingStore(binding),
			codexBindingCommitGate: new CodexBindingCommitGate());

		await viewModel.InitializeAsync();

		UsageSnapshot restored = Assert.IsType<UsageSnapshot>(
			Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Stale, restored.Status);
		Assert.Equal(publicBindingIdentity, restored.ProviderAccountIdentity);
	}

	[Fact]
	public void Constructor_WithCodexBindingStoreButNoSharedGate_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new DashboardViewModel(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore:
				new FakeCodexWorkspaceBindingStore()));
	}

	[Fact]
	public async Task InitializeAsync_WithoutClaudeConsent_ShowsConsentActionWithCachedUsage()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("weekly", "週用量", 90, "舊資料 90%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: context.AccountIdentity,
			SubscriptionScopeDisplayName: context.SubscriptionScopeDisplayName,
			PlanTier: context.PlanTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.True(account.RequiresClaudeQuotaRiskConsent);
		Assert.True(account.ShowClaudeDefaultConnectionAction);
		Assert.Equal("確認 Claude 用量讀取", account.ClaudeAccountActionText);
		Assert.Single(account.UsageMetrics);
	}

	[Fact]
	public async Task AddAccountAsync_ClaudeAlwaysStartsWithoutQuotaRiskConsent()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);

		await viewModel.AddAccountAsync(profile);

		AccountProfile savedProfile = Assert.Single(
			Assert.Single(store.SavedSnapshots));
		Assert.False(savedProfile.HasAcceptedClaudeQuotaRisk);
		Assert.False(
			Assert.Single(viewModel.Accounts)
				.Profile.HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	public async Task AddAccountAsync_InManualMode_InsertsAfterMatchingProvider()
	{
		AccountProfile existingClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude existing");
		AccountProfile existingCodex = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		AccountProfile addedClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude added");
		FakeAccountProfileStore store = new(existingClaude, existingCodex);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(addedClaude);

		Guid[] expectedOrder =
		{
			existingClaude.Id,
			addedClaude.Id,
			existingCodex.Id
		};
		Assert.Equal(expectedOrder, viewModel.Accounts.Select(account => account.Id));
		Assert.Equal(
			expectedOrder,
			Assert.Single(store.SavedSnapshots).Select(account => account.Id));
	}

	[Fact]
	public async Task AddAccountAsync_InManualMode_UsesProviderPriorityForNewGroup()
	{
		AccountProfile existingClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		AccountProfile existingAntigravity = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		AccountProfile existingCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		AccountProfile addedCodex = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore store = new(
			existingClaude,
			existingAntigravity,
			existingCopilot);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(addedCodex);

		Assert.Equal(
			new[]
			{
				existingClaude.Id,
				addedCodex.Id,
				existingAntigravity.Id,
				existingCopilot.Id
			},
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task AddAccountAsync_InManualMode_DoesNotReorderExistingCards()
	{
		AccountProfile existingCodex = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		AccountProfile firstClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude first");
		AccountProfile secondClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude second");
		AccountProfile existingCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		AccountProfile addedClaude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude added");
		FakeAccountProfileStore store = new(
			existingCodex,
			firstClaude,
			secondClaude,
			existingCopilot);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(addedClaude);

		Assert.Equal(
			new[]
			{
				existingCodex.Id,
				firstClaude.Id,
				secondClaude.Id,
				addedClaude.Id,
				existingCopilot.Id
			},
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task UpdateAccountAsync_PreservesClaudeQuotaRiskConsent()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore store = new(profile);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.UpdateAccountAsync(profile with
		{
			DisplayName = "Claude Team",
			HasAcceptedClaudeQuotaRisk = false
		});

		AccountProfile savedProfile = Assert.Single(
			Assert.Single(store.SavedSnapshots));
		Assert.Equal("Claude Team", savedProfile.DisplayName);
		Assert.True(savedProfile.HasAcceptedClaudeQuotaRisk);
		Assert.True(
			Assert.Single(viewModel.Accounts)
				.Profile.HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	public async Task QuiesceAccountRefreshAsync_WaitsForActiveRefreshAndSkipsNewPolling()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		TaskCompletionSource refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = async account =>
			{
				refreshStarted.TrySetResult();
				await allowRefreshToFinish.Task;
				return CreateReadySnapshot(account);
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);

		Task activeRefresh = viewModel.RefreshUsageAsync();
		await refreshStarted.Task;
		account.BeginProviderAccountChange();
		Task quiesce = viewModel.QuiesceAccountRefreshAsync(profile.Id);

		Assert.False(quiesce.IsCompleted);
		Assert.Equal(
			new[] { profile.Id },
			refreshCoordinator.InvalidatedAccountIds);

		allowRefreshToFinish.TrySetResult();
		await activeRefresh;
		await quiesce;
		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Single(refreshCoordinator.RefreshRequests);
	}

	[Fact]
	public async Task QuiesceAccountRefreshAsync_WhenUnrelatedAccountRemainsActive_WaitsOnlyForTargetAccount()
	{
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountProfile unrelatedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: "unrelated@example.com");
		TaskCompletionSource targetRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource unrelatedRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowTargetRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowUnrelatedRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = async account =>
			{
				if (account.Id == targetProfile.Id)
				{
					targetRefreshStarted.TrySetResult();
					await allowTargetRefreshToFinish.Task;
				}
				else
				{
					unrelatedRefreshStarted.TrySetResult();
					await allowUnrelatedRefreshToFinish.Task;
				}

				return CreateReadySnapshot(account);
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetProfile, unrelatedProfile),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);

		Task activeRefresh = viewModel.RefreshUsageAsync();
		await Task.WhenAll(
			targetRefreshStarted.Task,
			unrelatedRefreshStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
		targetAccount.BeginProviderAccountChange();
		Task quiesce = viewModel.QuiesceAccountRefreshAsync(targetProfile.Id);

		try
		{
			Assert.False(quiesce.IsCompleted);
			allowTargetRefreshToFinish.TrySetResult();
			await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(activeRefresh.IsCompleted);
		}
		finally
		{
			allowTargetRefreshToFinish.TrySetResult();
			allowUnrelatedRefreshToFinish.TrySetResult();
			await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task InitializeAsync_WithMultipleAntigravityAccounts_PreservesStoredAccounts()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第一個 Antigravity");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第二個 Antigravity");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());

		await viewModel.InitializeAsync();

		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WithUnboundLegacyAntigravityAccounts_RequiresReconnectBeforeQuery()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第一個 Antigravity");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第二個 Antigravity");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
				Task.FromResult(CreateReadySnapshot(account))
		};
		FakeUsageSnapshotStore snapshotStore = new(
			CreateReadySnapshot(secondAccount, 77));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			refreshCoordinator,
			snapshotStore);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		AccountUsageViewModel firstViewModel = viewModel.Accounts.Single(
			account => account.Id == firstAccount.Id);
		AccountUsageViewModel secondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);
		Assert.Equal(AccountStatusKind.NotConnected, firstViewModel.StatusKind);
		Assert.Equal(AccountStatusKind.NotConfigured, secondViewModel.StatusKind);
		Assert.Empty(secondViewModel.UsageMetrics);
		Assert.Equal(
			UsageRecoveryAction.None,
			secondViewModel.RecoveryAction);
		Assert.False(secondViewModel.HasRecoveryAction);
		Assert.Contains(
			"不必重新登入",
			secondViewModel.SecondaryText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			refreshCoordinator.SeededSnapshots,
			snapshot => snapshot.Account.Id == secondAccount.Id);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WithInactiveLegacyAntigravityAccount_DoesNotCallProvider()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第一個 Antigravity");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第二個 Antigravity");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
				Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(secondAccount.Id);

		Assert.Empty(refreshCoordinator.RefreshRequests);
		AccountUsageViewModel secondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);
		Assert.Equal(AccountStatusKind.NotConfigured, secondViewModel.StatusKind);
		Assert.Equal(
			UsageRecoveryAction.None,
			secondViewModel.RecoveryAction);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenActiveAntigravityIsDisabled_TransfersToNextEnabledAccount()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第一個 Antigravity",
			ProviderAccountIdentity: "first-agy@example.com");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第二個 Antigravity",
			ProviderAccountIdentity: "second-agy@example.com");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
				Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(store, refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.UpdateAccountAsync(firstAccount with
		{
			IsEnabled = false
		});
		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			refreshCoordinator.RefreshRequests.Select(account => account.Id));
		AccountUsageViewModel firstViewModel = viewModel.Accounts.Single(
			account => account.Id == firstAccount.Id);
		AccountUsageViewModel secondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);
		Assert.False(firstViewModel.IsEnabled);
		Assert.Equal(AccountStatusKind.Ready, secondViewModel.StatusKind);
		IReadOnlyList<AccountProfile> savedAccounts = Assert.Single(
			store.SavedSnapshots);
		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			savedAccounts.Select(account => account.Id));
		Assert.False(savedAccounts[0].IsEnabled);
		Assert.True(savedAccounts[1].IsEnabled);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenLocalSessionPrimaryIsDisabled_AllowsNextAgyCardToClaimSession()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第一個 Antigravity",
			ProviderAccountIdentity: LocalSessionIdentity);
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"第二個 Antigravity",
			ProviderAccountIdentity: "legacy-second@example.com");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadyAntigravitySnapshot(
					account,
					DateTimeOffset.UtcNow))
		};
		DashboardViewModel viewModel = new(store, refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.UpdateAccountAsync(firstAccount with
		{
			IsEnabled = false
		});
		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel firstViewModel = viewModel.Accounts.Single(
			account => account.Id == firstAccount.Id);
		AccountUsageViewModel secondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);
		Assert.False(firstViewModel.IsEnabled);
		Assert.Equal(LocalSessionIdentity, firstViewModel.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Ready, secondViewModel.StatusKind);
		Assert.Equal(LocalSessionIdentity, secondViewModel.ProviderAccountIdentity);
		Assert.False(secondViewModel.DidRejectProviderAccountSnapshot);
		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			refreshCoordinator.RefreshRequests.Select(account => account.Id));
		Assert.Equal(2, store.SavedSnapshots.Count);
		IReadOnlyList<AccountProfile> savedAccounts = store.SavedSnapshots[^1];
		Assert.Equal(
			LocalSessionIdentity,
			savedAccounts.Single(account => account.Id == secondAccount.Id)
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ToggleUsageSortModeAsync_AutomaticThenManual_RestoresStoredOrder()
	{
		AccountProfile copilotAccount = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		AccountProfile antigravityAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY");
		AccountProfile codexAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		AccountProfile claudeAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore accountStore = new(
			copilotAccount,
			antigravityAccount,
			codexAccount,
			claudeAccount);
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await viewModel.ToggleUsageSortModeAsync();

		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
		Assert.Equal(
			new[]
			{
				ProviderKind.Claude,
				ProviderKind.Codex,
				ProviderKind.Antigravity,
				ProviderKind.Copilot
			},
			viewModel.Accounts.Select(account => account.Provider));
		Assert.All(
			viewModel.Accounts,
			account =>
			{
				Assert.False(account.CanMoveUp);
				Assert.False(account.CanMoveDown);
			});
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.MoveAccountAsync(copilotAccount.Id, -1));
		Assert.Empty(accountStore.SavedSnapshots);

		await viewModel.ToggleUsageSortModeAsync();

		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(
			new[]
			{
				copilotAccount.Id,
				antigravityAccount.Id,
				codexAccount.Id,
				claudeAccount.Id
			},
			viewModel.Accounts.Select(account => account.Id));
		Assert.Equal(
			new[] { UsageSortMode.Automatic, UsageSortMode.Manual },
			preferencesStore.SavedSortModes);
	}

	[Fact]
	public async Task InitializeAsync_WithRemainingDisplayPreference_ProjectsCachedUsage()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot",
			ProviderAccountIdentity: "person@example.com");
		DateTimeOffset now = DateTimeOffset.UtcNow;
		UsageSnapshot cachedSnapshot = new(
			account,
			new[]
			{
				new UsageMetric(
					"codex.rate_limit.five_hour",
					"5小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			now,
			now,
			now.AddMinutes(2),
			ProviderAccountIdentity: "person@example.com");
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			LoadedDisplayMode = UsageDisplayMode.Remaining
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot),
			preferencesStore);

		await viewModel.InitializeAsync();

		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.True(viewModel.IsShowingRemainingUsage);
		Assert.Equal("顯示：剩餘", viewModel.UsageDisplayModeText);
		UsageMetricViewModel metric = Assert.Single(
			Assert.Single(viewModel.Accounts).UsageMetrics);
		Assert.Equal(75, metric.DisplayPercent);
		Assert.Equal("剩餘 75%", metric.DisplayValue);
		Assert.Equal(
			"已使用 25%",
			Assert.Single(
				Assert.Single(viewModel.Accounts).CurrentSnapshot!.Metrics)
				.DisplayValue);
	}

	[Fact]
	public async Task ToggleUsageDisplayModeAsync_ReprojectsWithoutRefreshingAndIsReversible()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "codex@example.com");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = profile => Task.FromResult(new UsageSnapshot(
				profile,
				new[]
				{
					new UsageMetric(
						"codex:codex:primary",
						"7 天用量",
						41.25,
						"已使用 41.25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: profile.ProviderAccountIdentity))
		};
		FakeDashboardPreferencesStore preferencesStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		UsageSnapshot snapshotBeforeToggle = accountViewModel.CurrentSnapshot!;
		int refreshCountBeforeToggle = refreshCoordinator.RefreshRequests.Count;
		Assert.Equal("改為顯示剩餘用量", viewModel.UsageDisplayModeActionText);
		Assert.Equal(
			"目前顯示已使用量。按一下改為剩餘。",
			viewModel.UsageDisplayModeToolTip);

		await viewModel.ToggleUsageDisplayModeAsync();

		Assert.Equal(UsageDisplayMode.Remaining, viewModel.DisplayMode);
		Assert.Equal("改為顯示已使用量", viewModel.UsageDisplayModeActionText);
		Assert.Equal(
			"目前顯示剩餘用量。按一下改為已使用。",
			viewModel.UsageDisplayModeToolTip);
		Assert.Equal("已改為顯示剩餘用量。", viewModel.AccountSettingsMessage);
		Assert.Equal(
			new[] { UsageDisplayMode.Remaining },
			preferencesStore.SavedDisplayModes);
		Assert.Equal(
			refreshCountBeforeToggle,
			refreshCoordinator.RefreshRequests.Count);
		Assert.Same(snapshotBeforeToggle, accountViewModel.CurrentSnapshot);
		UsageMetricViewModel remainingMetric =
			Assert.Single(accountViewModel.UsageMetrics);
		Assert.Equal(58.75, remainingMetric.DisplayPercent);
		Assert.Equal("剩餘 58.75%", remainingMetric.DisplayValue);

		await viewModel.ToggleUsageDisplayModeAsync();

		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.Equal("改為顯示剩餘用量", viewModel.UsageDisplayModeActionText);
		Assert.Equal(
			"目前顯示已使用量。按一下改為剩餘。",
			viewModel.UsageDisplayModeToolTip);
		Assert.Equal("已改為顯示已使用量。", viewModel.AccountSettingsMessage);
		UsageMetricViewModel usedMetric = Assert.Single(
			accountViewModel.UsageMetrics);
		Assert.Equal(41.25, usedMetric.DisplayPercent);
		Assert.Equal("已使用 41.25%", usedMetric.DisplayValue);
		Assert.Equal(
			new[]
			{
				UsageDisplayMode.Remaining,
				UsageDisplayMode.Used
			},
			preferencesStore.SavedDisplayModes);
		Assert.Equal(
			refreshCountBeforeToggle,
			refreshCoordinator.RefreshRequests.Count);
	}

	[Fact]
	public async Task ToggleUsageDisplayModeAsync_WhenPreferenceSaveFails_DoesNotPublishMode()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = profile => Task.FromResult(new UsageSnapshot(
				profile,
				new[] { new UsageMetric("quota", "用量", 25, "已使用 25%") },
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: profile.ProviderAccountIdentity))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await Assert.ThrowsAsync<IOException>(
			() => viewModel.ToggleUsageDisplayModeAsync());

		Assert.Equal(UsageDisplayMode.Used, viewModel.DisplayMode);
		Assert.Equal(
			"已使用 25%",
			Assert.Single(Assert.Single(viewModel.Accounts).UsageMetrics)
				.DisplayValue);
		Assert.Empty(preferencesStore.SavedDisplayModes);
	}

	[Fact]
	public async Task UpdateAccountAsync_ChangesSubscriptionContextForOnlyOneCard()
	{
		const string FirstBindingIdentity = "00112233445566778899aabbccddeeff";
		const string SecondBindingIdentity = "ffeeddccbbaa99887766554433221100";
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude first",
			ProviderAccountIdentity: FirstBindingIdentity);
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude second",
			ProviderAccountIdentity: SecondBindingIdentity);
		FakeAccountProfileStore store = new(firstProfile, secondProfile);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel firstAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == secondProfile.Id);
		ApplyClaudeOrganizationSnapshot(
			firstAccount,
			FirstBindingIdentity,
			"First Team");

		Assert.Equal(
			"first@example.com",
			firstAccount.AccountCardDisplayText);

		ApplyClaudeOrganizationSnapshot(
			secondAccount,
			SecondBindingIdentity,
			"Second Team",
			"second@example.com");

		Assert.Equal(
			"second@example.com",
			secondAccount.AccountCardDisplayText);

		await viewModel.UpdateAccountAsync(
			firstAccount.Profile with { ShowSubscriptionContext = true });

		Assert.Equal(
			$"first@example.com{Environment.NewLine}First Team",
			firstAccount.AccountCardDisplayText);
		Assert.Equal(
			"second@example.com",
			secondAccount.AccountCardDisplayText);
		Assert.True(firstAccount.Profile.ShowSubscriptionContext);
		Assert.False(secondAccount.Profile.ShowSubscriptionContext);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenSubscriptionContextSaveFails_KeepsCardHidden()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore store = new(profile);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		store.ShouldFailSave = true;

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.UpdateAccountAsync(
				profile with { ShowSubscriptionContext = true }));

		Assert.False(account.ShowSubscriptionContext);
		Assert.False(account.Profile.ShowSubscriptionContext);
	}

	[Fact]
	public async Task RefreshUsageAsync_InAutomaticMode_OrdersCodexBeforeAntigravityAcrossWindowKinds()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountProfile codexLaterAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex later",
			ProviderAccountIdentity: "codex-later@example.com");
		AccountProfile antigravitySoonerAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY sooner",
			ProviderAccountIdentity: "agy-sooner@example.com");
		AccountProfile codexSoonerAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex sooner",
			ProviderAccountIdentity: "codex-sooner@example.com");
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			LoadedSortMode = UsageSortMode.Automatic
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				string metricKey = account.Provider == ProviderKind.Antigravity
					? "agy.claude.rolling-5h"
					: "codex:codex:primary";
				string metricLabel = account.Provider == ProviderKind.Antigravity
					? "Claude rolling 5h"
					: "主要用量";
				DateTimeOffset resetsAt = account.Id == antigravitySoonerAccount.Id
					? now.AddMinutes(5)
					: account.Id == codexSoonerAccount.Id
						? now.AddMinutes(10)
						: now.AddHours(1);
				UsageMetric metric = new(
					metricKey,
					metricLabel,
					10,
					"10%",
					resetsAt);
				return Task.FromResult(new UsageSnapshot(
					account,
					new[] { metric },
					SourceTrust.Official,
					SnapshotStatus.Ready,
					now,
					ProviderAccountIdentity:
						account.ProviderAccountIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				codexLaterAccount,
				antigravitySoonerAccount,
				codexSoonerAccount),
			refreshCoordinator,
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			new[]
			{
				codexSoonerAccount.Id,
				codexLaterAccount.Id,
				antigravitySoonerAccount.Id
			},
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task RefreshUsageAsync_InAutomaticMode_OrdersPastResetAfterFutureReset()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountProfile codexPastAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex past",
			ProviderAccountIdentity: "codex-past@example.com");
		AccountProfile codexFutureAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex future",
			ProviderAccountIdentity: "codex-future@example.com");
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			LoadedSortMode = UsageSortMode.Automatic
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				UsageMetric metric = new(
					"five_hour",
					"5小時用量",
					10,
					"10%",
					account.Id == codexFutureAccount.Id
						? now.AddHours(1)
						: now.AddHours(-1));
				return Task.FromResult(new UsageSnapshot(
					account,
					new[] { metric },
					SourceTrust.Official,
					SnapshotStatus.Ready,
					now,
					ProviderAccountIdentity:
						account.ProviderAccountIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				codexPastAccount,
				codexFutureAccount),
			refreshCoordinator,
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			new[]
			{
				codexFutureAccount.Id,
				codexPastAccount.Id
			},
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task ToggleUsageSortModeAsync_WhenPreferenceSaveFails_DoesNotPublishMode()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第一個帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第二個帳號");
		FakeDashboardPreferencesStore preferencesStore = new()
		{
			ShouldFailSave = true
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			new FakeUsageRefreshCoordinator(),
			dashboardPreferencesStore: preferencesStore);
		await viewModel.InitializeAsync();

		await Assert.ThrowsAsync<IOException>(
			() => viewModel.ToggleUsageSortModeAsync());

		Assert.Equal(UsageSortMode.Manual, viewModel.SortMode);
		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.Empty(preferencesStore.SavedSortModes);
	}

	[Fact]
	public async Task MoveAccountAsync_MovesOneStepAndPersistsOrder()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第二個帳號");
		AccountProfile thirdAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第三個帳號");
		FakeAccountProfileStore store = new(
			firstAccount,
			secondAccount,
			thirdAccount);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel originalThirdViewModel = viewModel.Accounts[2];

		await viewModel.MoveAccountAsync(thirdAccount.Id, -1);

		Assert.Equal(
			new[] { firstAccount.Id, thirdAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.Same(originalThirdViewModel, viewModel.Accounts[1]);
		IReadOnlyList<AccountProfile> persistedAccounts = Assert.Single(
			store.SavedSnapshots);
		Assert.Equal(
			new[] { firstAccount.Id, thirdAccount.Id, secondAccount.Id },
			persistedAccounts.Select(account => account.Id));
		Assert.False(viewModel.Accounts[0].CanMoveUp);
		Assert.True(viewModel.Accounts[1].CanMoveUp);
		Assert.True(viewModel.Accounts[1].CanMoveDown);
		Assert.False(viewModel.Accounts[2].CanMoveDown);
	}

	[Fact]
	public async Task MoveAccountAsync_AtBoundary_DoesNotSave()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第二個帳號");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.MoveAccountAsync(firstAccount.Id, -1);
		await viewModel.MoveAccountAsync(secondAccount.Id, 1);

		Assert.Empty(store.SavedSnapshots);
		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
	}

	[Fact]
	public async Task MoveAccountAsync_WhenSaveFails_DoesNotPublishOrder()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第二個帳號");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		store.ShouldFailSave = true;

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.MoveAccountAsync(secondAccount.Id, -1));

		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id },
			viewModel.Accounts.Select(account => account.Id));
		Assert.False(viewModel.IsManagingAccounts);
		Assert.True(viewModel.Accounts[0].CanMoveDown);
		Assert.True(viewModel.Accounts[1].CanMoveUp);
	}

	[Fact]
	public async Task InitializeAsync_WhenCachedUsageExists_RestoresItAsStale()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot Pro",
			ProviderAccountIdentity: "person@example.com");
		DateTimeOffset fetchedAt = new(2026, 7, 15, 8, 30, 0, TimeSpan.Zero);
		DateTimeOffset resetsAt = fetchedAt + TimeSpan.FromHours(5);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 35, "已使用 35%", resetsAt),
				new UsageMetric("seven_day", "週用量", 60, "已使用 60%", resetsAt)
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			fetchedAt,
			ProviderAccountIdentity: "person@example.com");
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("顯示上次資料", account.StatusText);
		Assert.Equal("person@example.com", account.ProviderAccountIdentity);
		Assert.Equal(2, account.UsageMetrics.Count);
		Assert.Equal(resetsAt, account.CurrentSnapshot!.Metrics[0].ResetsAt);
		Assert.False(account.HasNotice);
		Assert.Equal(string.Empty, account.NoticeText);
		UsageSnapshot seededSnapshot = Assert.Single(refreshCoordinator.SeededSnapshots);
		Assert.Equal(SnapshotStatus.Stale, seededSnapshot.Status);
		Assert.Equal(fetchedAt, seededSnapshot.FetchedAt);
	}

	[Theory]
	[InlineData(SubscriptionVerificationState.Verified)]
	[InlineData(SubscriptionVerificationState.TransientProbeError)]
	public async Task InitializeAsync_WithBoundClaudeCache_RestoresVerifiedOwnershipState(
		SubscriptionVerificationState verificationState)
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude Team",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			SourceTrust = SourceTrust.OfficialExperimental,
			ProviderAccountIdentity = publicBindingIdentity,
			ProviderAccountDisplayIdentity = context.AccountIdentity,
			SubscriptionScopeDisplayName = context.SubscriptionScopeDisplayName,
			PlanTier = context.PlanTier,
			SubscriptionVerificationState = verificationState
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot!.Status);
		Assert.Equal(verificationState, account.CurrentSnapshot
			.SubscriptionVerificationState);
		Assert.Equal(context.AccountIdentity, account.CurrentSnapshot
			.ProviderAccountDisplayIdentity);
		Assert.Equal(context.SubscriptionScopeDisplayName, account.CurrentSnapshot
			.SubscriptionScopeDisplayName);
		Assert.Equal(context.PlanTier, account.CurrentSnapshot.PlanTier);
		Assert.NotEmpty(account.UsageMetrics);
		Assert.Single(refreshCoordinator.SeededSnapshots);
	}

	[Theory]
	[InlineData(SubscriptionVerificationState.Unverified)]
	[InlineData(SubscriptionVerificationState.DefiniteScopeMismatch)]
	[InlineData(SubscriptionVerificationState.UsageUnavailable)]
	public async Task InitializeAsync_WithNonRestorableClaudeOwnershipState_SkipsCache(
		SubscriptionVerificationState verificationState)
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude Team",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState = verificationState
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenDefiniteScopeMismatchCacheDeleteFails_RetriesAfterRestart()
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"user",
			"person@example.com");
		string publicBindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			DeleteHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cache is temporarily locked"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeGrokAccountBindingStore bindingStore = new(binding);
		GrokBindingCommitGate bindingCommitGate = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				new DateTimeOffset(2026, 8, 30, 3, 0, 0, TimeSpan.Zero),
				Error: "Grok principal mismatch.",
				ProviderAccountIdentity: publicBindingIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount,
				SubscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.CacheCleanupUpsertedAccounts));
		Assert.True(Assert.Single(cleanupPendingStore.PendingWork).CachePending);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));

		snapshotStore.DeleteHandler = null;
		DashboardViewModel restartedViewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);
		await restartedViewModel.InitializeAsync();

		Assert.Null(Assert.Single(restartedViewModel.Accounts).CurrentSnapshot);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.Null(await snapshotStore.LoadAsync(profile));
		Assert.False(restartedViewModel.IsAccountCleanupBlocked(
			profile.Id,
			profile.Provider));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenDefiniteScopeMismatchCleanupCannotBeMadeDurable_DisablesProfileAcrossRestart()
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"user",
			"person@example.com");
		string publicBindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			DeleteHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cache remains locked"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertCacheCleanupHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cleanup journal remains locked"))
		};
		FakeGrokAccountBindingStore bindingStore = new(binding);
		GrokBindingCommitGate bindingCommitGate = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				new DateTimeOffset(2026, 8, 30, 3, 30, 0, TimeSpan.Zero),
				Error: "Grok principal mismatch.",
				ProviderAccountIdentity: publicBindingIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount,
				SubscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);
		await viewModel.InitializeAsync();
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			Assert.IsType<UsageSnapshot>(Assert.Single(viewModel.Accounts).CurrentSnapshot)
				.SubscriptionVerificationState);

		await viewModel.RefreshUsageAsync();

		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.CacheCleanupUpsertedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
		AccountUsageViewModel failClosedAccount = Assert.Single(viewModel.Accounts);
		Assert.False(failClosedAccount.IsEnabled);
		Assert.Null(failClosedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(failClosedAccount.CurrentSnapshot);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedProfile = Assert.Single(persisted.Accounts);
		Assert.False(persistedProfile.IsEnabled);
		Assert.Null(persistedProfile.ProviderAccountIdentity);

		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);
		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.False(restartedAccount.IsEnabled);
		Assert.Null(restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(restartedAccount.CurrentSnapshot);
		Assert.Empty(restartedRefreshCoordinator.SeededSnapshots);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAllDurableCacheProtectionFails_DeletesClaudeBindingBeforeRestart()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude Company",
			ProviderAccountIdentity: publicBindingIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			DeleteHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cache remains locked"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertCacheCleanupHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cleanup journal remains locked"))
		};
		FakeClaudeAccountBindingStore bindingStore = new(binding);
		ClaudeBindingCommitGate bindingCommitGate = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				new DateTimeOffset(2026, 8, 30, 4, 0, 0, TimeSpan.Zero),
				Error: "Claude subscription scope mismatch.",
				ProviderAccountIdentity: publicBindingIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount,
				SubscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: bindingCommitGate);
		await viewModel.InitializeAsync();
		Assert.NotNull(Assert.Single(viewModel.Accounts).CurrentSnapshot);

		await viewModel.RefreshUsageAsync();

		Assert.NotEmpty(profileStore.SavedSnapshots);
		Assert.Equal(accountId, Assert.Single(bindingStore.DeletedAccountIds));
		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
		AccountUsageViewModel failClosedAccount = Assert.Single(viewModel.Accounts);
		Assert.False(failClosedAccount.IsEnabled);
		Assert.Null(failClosedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(failClosedAccount.CurrentSnapshot);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedProfile = Assert.Single(persisted.Accounts);
		Assert.True(persistedProfile.IsEnabled);
		Assert.Equal(publicBindingIdentity, persistedProfile.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		snapshotStore.DeleteHandler = null;
		cleanupPendingStore.UpsertCacheCleanupHandler = null;
		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: bindingCommitGate);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.True(restartedAccount.IsEnabled);
		Assert.Equal(
			publicBindingIdentity,
			restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(restartedAccount.CurrentSnapshot);
		Assert.Empty(restartedRefreshCoordinator.SeededSnapshots);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAllDurableCacheProtectionFails_DeletesGrokBindingBeforeRestart()
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"user",
			"person@example.com");
		string publicBindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			DeleteHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cache remains locked"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertCacheCleanupHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cleanup journal remains locked"))
		};
		FakeGrokAccountBindingStore bindingStore = new(binding);
		GrokBindingCommitGate bindingCommitGate = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				new DateTimeOffset(2026, 8, 30, 4, 30, 0, TimeSpan.Zero),
				Error: "Grok principal mismatch.",
				ProviderAccountIdentity: publicBindingIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount,
				SubscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);
		await viewModel.InitializeAsync();
		Assert.NotNull(Assert.Single(viewModel.Accounts).CurrentSnapshot);

		await viewModel.RefreshUsageAsync();

		Assert.Equal(accountId, Assert.Single(bindingStore.DeletedAccountIds));
		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
		AccountUsageViewModel failClosedAccount = Assert.Single(viewModel.Accounts);
		Assert.False(failClosedAccount.IsEnabled);
		Assert.Null(failClosedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(failClosedAccount.CurrentSnapshot);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedProfile = Assert.Single(persisted.Accounts);
		Assert.True(persistedProfile.IsEnabled);
		Assert.Equal(
			publicBindingIdentity,
			persistedProfile.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		snapshotStore.DeleteHandler = null;
		cleanupPendingStore.UpsertCacheCleanupHandler = null;
		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: bindingStore,
			grokBindingCommitGate: bindingCommitGate);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.True(restartedAccount.IsEnabled);
		Assert.Equal(
			publicBindingIdentity,
			restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(restartedAccount.CurrentSnapshot);
		Assert.Empty(restartedRefreshCoordinator.SeededSnapshots);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAllDurableCacheProtectionFails_QuarantinesCodexAccountBeforeRestart()
	{
		Guid accountId = Guid.NewGuid();
		const string AccountIdentity = "person@example.com";
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: AccountIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = AccountIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			DeleteHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cache remains locked"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertCacheCleanupHandler = (_, _, _) => Task.FromException(
				new InvalidOperationException("cleanup journal remains locked"))
		};
		FakeCodexWorkspaceBindingStore bindingStore = new();
		CodexBindingCommitGate bindingCommitGate = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				new DateTimeOffset(2026, 8, 30, 5, 0, 0, TimeSpan.Zero),
				Error: "Codex account scope mismatch.",
				ProviderAccountIdentity: AccountIdentity,
				RecoveryAction: UsageRecoveryAction.SwitchAccount,
				SubscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingCommitGate);
		await viewModel.InitializeAsync();
		Assert.NotNull(Assert.Single(viewModel.Accounts).CurrentSnapshot);

		await viewModel.RefreshUsageAsync();

		CodexWorkspaceBinding quarantine = Assert.IsType<CodexWorkspaceBinding>(
			bindingStore.GetBinding(accountId));
		Assert.True(quarantine.IsRestartQuarantine);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
		AccountUsageViewModel failClosedAccount = Assert.Single(viewModel.Accounts);
		Assert.False(failClosedAccount.IsEnabled);
		Assert.Null(failClosedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(failClosedAccount.CurrentSnapshot);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedProfile = Assert.Single(persisted.Accounts);
		Assert.True(persistedProfile.IsEnabled);
		Assert.Equal(AccountIdentity, persistedProfile.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		snapshotStore.DeleteHandler = null;
		cleanupPendingStore.UpsertCacheCleanupHandler = null;
		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingCommitGate);

		await restartedViewModel.InitializeAsync();

		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.True(restartedAccount.IsEnabled);
		Assert.Equal(
			AccountIdentity,
			restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(restartedAccount.CurrentSnapshot);
		Assert.Empty(restartedRefreshCoordinator.SeededSnapshots);
		Assert.Same(quarantine, bindingStore.GetBinding(accountId));
		Assert.NotNull(await snapshotStore.LoadAsync(profile));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InitializeAsync_WithoutMatchingPrivateClaudeBinding_SkipsVerifiedCache(
		bool hasMismatchedBinding)
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding expectedBinding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				expectedBinding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude Team",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeClaudeAccountBindingStore bindingStore = hasMismatchedBinding
			? new FakeClaudeAccountBindingStore(
				ClaudeAccountBinding.Create(accountId, context))
			: new FakeClaudeAccountBindingStore();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task InitializeAsync_WithLegacyClaudeCache_DoesNotRestoreAutomatically()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Legacy",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = "person@example.com",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task TryPersistAuthenticatedProviderBindingAsync_WithRetainedClaudeLegacyCache_RewritesOwnershipContext()
	{
		AccountProfile legacyProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Legacy",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot legacySnapshot = CreateReadySnapshot(legacyProfile) with
		{
			SourceTrust = SourceTrust.OfficialExperimental,
			ProviderAccountIdentity = "person@example.com",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(legacyProfile);
		FakeUsageSnapshotStore snapshotStore = new(legacySnapshot);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		UsageSnapshot retainedSnapshot = Assert.IsType<UsageSnapshot>(
			await viewModel.LoadClaudeLegacyCachedSnapshotAsync(account));
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			legacyProfile.Id,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity(publicBindingIdentity);
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		AuthenticatedProviderBindingCommitResult result =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				publicBindingIdentity,
				retainedClaudeLegacySnapshot: retainedSnapshot,
				claudeSubscriptionContext: context);

		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			result);
		UsageSnapshot rewrittenSnapshot = Assert.Single(
			snapshotStore.SavedSnapshots);
		Assert.Equal(SnapshotStatus.Stale, rewrittenSnapshot.Status);
		Assert.Equal(publicBindingIdentity, rewrittenSnapshot
			.ProviderAccountIdentity);
		Assert.Equal(context.AccountIdentity, rewrittenSnapshot
			.ProviderAccountDisplayIdentity);
		Assert.Equal(context.SubscriptionScopeDisplayName, rewrittenSnapshot
			.SubscriptionScopeDisplayName);
		Assert.Equal(context.PlanTier, rewrittenSnapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			rewrittenSnapshot.SubscriptionVerificationState);
		Assert.Equal(publicBindingIdentity, rewrittenSnapshot.Account
			.ProviderAccountIdentity);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Contains(legacySnapshot.Metrics[0], rewrittenSnapshot.Metrics);
		Assert.Equal(
			publicBindingIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task TryPersistAuthenticatedProviderBindingAsync_WhenRetainedClaudeCacheSaveSucceedsButReadBackIsMissing_KeepsHealthWarning()
	{
		AccountProfile legacyProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Legacy",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot legacySnapshot = CreateReadySnapshot(legacyProfile) with
		{
			SourceTrust = SourceTrust.OfficialExperimental,
			ProviderAccountIdentity = "person@example.com",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		bool saveAttempted = false;
		FakeUsageSnapshotStore snapshotStore = new(legacySnapshot)
		{
			LoadHandler = (_, _) => Task.FromResult<UsageSnapshot?>(
				saveAttempted ? null : legacySnapshot),
			SaveHandler = (_, _) =>
			{
				saveAttempted = true;
				return Task.CompletedTask;
			}
		};
		FakeAccountProfileStore profileStore = new(legacyProfile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		UsageSnapshot retainedSnapshot = Assert.IsType<UsageSnapshot>(
			await viewModel.LoadClaudeLegacyCachedSnapshotAsync(account));
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			legacyProfile.Id,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity(publicBindingIdentity);
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		AuthenticatedProviderBindingCommitResult result =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				publicBindingIdentity,
				retainedClaudeLegacySnapshot: retainedSnapshot,
				claudeSubscriptionContext: context);

		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			result);
		Assert.True(saveAttempted);
		Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Contains(
			"無法持久保留你確認歸屬於此訂閱範圍的舊版用量",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_AfterRetainingClaudeLegacyCacheAndUsageUnavailable_PreservesRewrittenCache()
	{
		AccountProfile legacyProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Legacy",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		UsageSnapshot legacySnapshot = CreateReadySnapshot(legacyProfile) with
		{
			SourceTrust = SourceTrust.OfficialExperimental,
			ProviderAccountIdentity = "person@example.com",
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(legacyProfile);
		FakeUsageSnapshotStore snapshotStore = new(legacySnapshot);
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"team",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			legacyProfile.Id,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				DateTimeOffset.UtcNow,
				Error:
			"Claude team 訂閱範圍已確認，但 /usage 格式尚未經驗證。",
				ProviderAccountIdentity: publicBindingIdentity,
				RecoveryAction: UsageRecoveryAction.Retry,
				ProviderAccountDisplayIdentity: context.AccountIdentity,
				SubscriptionScopeDisplayName:
					context.SubscriptionScopeDisplayName,
				PlanTier: context.PlanTier,
				SubscriptionVerificationState:
					SubscriptionVerificationState.UsageUnavailable))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		UsageSnapshot retainedSnapshot = Assert.IsType<UsageSnapshot>(
			await viewModel.LoadClaudeLegacyCachedSnapshotAsync(account));
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity(publicBindingIdentity);
		Assert.True(account.TryBeginProviderAccountChangeCommit());
		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				publicBindingIdentity,
				retainedClaudeLegacySnapshot: retainedSnapshot,
				claudeSubscriptionContext: context));

		await viewModel.RefreshUsageAfterInvalidationAsync(account.Id);

		UsageSnapshot durableSnapshot = Assert.IsType<UsageSnapshot>(
			await snapshotStore.LoadAsync(account.Profile));
		Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.True(ClaudeAccountBinding.TryNormalizePublicBindingIdentity(
			durableSnapshot.ProviderAccountIdentity,
			out string? normalizedIdentity));
		Assert.Equal(publicBindingIdentity, normalizedIdentity);
		Assert.Equal(publicBindingIdentity, durableSnapshot.Account
			.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Stale, durableSnapshot.Status);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			durableSnapshot.SubscriptionVerificationState);
		Assert.Equal(context.AccountIdentity, durableSnapshot
			.ProviderAccountDisplayIdentity);
		Assert.Equal(context.SubscriptionScopeDisplayName, durableSnapshot
			.SubscriptionScopeDisplayName);
		Assert.Equal("team", durableSnapshot.PlanTier);
		Assert.Contains(legacySnapshot.Metrics[0], durableSnapshot.Metrics);
	}

	[Fact]
	public async Task InitializeAsync_WhenCleanupJournalIsInvalid_BlocksAllAccountsAndShowsManualRecoveryWarning()
	{
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第一個 Codex 帳號",
			ProviderAccountIdentity: "first@example.com");
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第二個 Codex 帳號",
			ProviderAccountIdentity: "second@example.com");
		int cacheLoadAttempts = 0;
		FakeUsageSnapshotStore snapshotStore = new()
		{
			LoadHandler = (_, _) =>
			{
				cacheLoadAttempts++;
				return Task.FromResult<UsageSnapshot?>(null);
			}
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			LoadHandler = _ => Task.FromException<
				IReadOnlyList<AccountCleanupPendingWork>>(
					new InvalidDataException("測試用清理工作檔格式錯誤。"))
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstProfile, secondProfile),
			refreshCoordinator,
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		Assert.True(viewModel.IsAccountCleanupBlocked(
			firstProfile.Id,
			firstProfile.Provider));
		Assert.True(viewModel.IsAccountCleanupBlocked(
			secondProfile.Id,
			secondProfile.Provider));
		Assert.False(viewModel.CanRefresh);
		Assert.Equal(0, cacheLoadAttempts);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			"所有帳號的用量檢查已暫停",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"無法自動修復",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"保留設定資料",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"更新 AI Usage",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"聯絡維護人員",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"稍後會自動再試",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task InitializeAsync_WhenCleanupJournalLoadFailsTransiently_ShowsAutomaticRetryWarning()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			LoadHandler = _ => Task.FromException<
				IReadOnlyList<AccountCleanupPendingWork>>(
					new IOException("測試用暫時性讀取失敗。"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Contains(
			"稍後會自動再試",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"無法自動修復",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenInvalidCleanupJournalIsRepaired_UnblocksAndClearsWarning()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		int loadAttempts = 0;
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			LoadHandler = _ =>
			{
				loadAttempts++;
				return loadAttempts == 1
					? Task.FromException<IReadOnlyList<AccountCleanupPendingWork>>(
						new InvalidDataException("測試用清理工作檔格式錯誤。"))
					: Task.FromResult<IReadOnlyList<AccountCleanupPendingWork>>(
						Array.Empty<AccountCleanupPendingWork>());
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Contains(
			"無法自動修復",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(2, loadAttempts);
		Assert.False(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Equal(profile.Id, Assert.Single(refreshCoordinator.RefreshRequests).Id);
		Assert.Equal(string.Empty, viewModel.AccountSettingsHealthMessage);
	}

	[Fact]
	public async Task FailClosedAuthenticatedProviderBindingConflictAsync_WhenRuntimeCleanupAlreadyPending_KeepsAccountBlocked()
	{
		string bindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(Guid.NewGuid());
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: bindingIdentity);
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(CreateReadySnapshot(profile)),
			new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		await cleanupPendingStore.UpsertAsync(new[]
		{
			(profile.Id, profile.Provider)
		});
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		bool wasPersisted =
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account);

		Assert.True(wasPersisted);
		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		AccountCleanupPendingWork pending =
			Assert.Single(cleanupPendingStore.PendingWork);
		Assert.False(pending.CachePending);
		Assert.True(pending.RuntimePending);
		Assert.Contains(
			"部分帳號的舊資料仍待清理",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task FailClosedAuthenticatedProviderBindingConflictAsync_WhenCacheDeleteFails_RestartCompletesCacheOnlyCleanupBeforeRestore()
	{
		string bindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(Guid.NewGuid());
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: bindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile);
		bool shouldFailDelete = true;
		bool cacheExists = true;
		List<string> operations = new();
		FakeUsageSnapshotStore snapshotStore = new()
		{
			LoadHandler = (_, _) =>
			{
				operations.Add("load-cache");
				return Task.FromResult<UsageSnapshot?>(
					cacheExists ? cachedSnapshot : null);
			},
			DeleteHandler = (_, _, _) =>
			{
				operations.Add("delete-cache");
				if (shouldFailDelete)
				{
					throw new IOException("cache locked");
				}

				cacheExists = false;
				return Task.CompletedTask;
			}
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Assert.True(account.TryBeginProviderAccountChangeCommit());
		operations.Clear();

		bool wasPersisted =
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account);

		Assert.True(wasPersisted);
		Assert.Equal(3, operations.Count(operation =>
			operation == "delete-cache"));
		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		AccountCleanupPendingWork pending =
			Assert.Single(cleanupPendingStore.PendingWork);
		Assert.True(pending.CachePending);
		Assert.False(pending.RuntimePending);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		AccountUsageViewModel disconnectedAccount = Assert.Single(
			viewModel.Accounts);
		Assert.True(disconnectedAccount.IsEnabled);
		Assert.Null(disconnectedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(disconnectedAccount.CurrentSnapshot);

		shouldFailDelete = false;
		operations.Clear();
		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new();
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await restartedViewModel.InitializeAsync();

		Assert.Equal(new[] { "delete-cache", "load-cache" }, operations);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(restartedViewModel.IsAccountCleanupBlocked(
			profile.Id,
			profile.Provider));
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Empty(restartedRefreshCoordinator.RefreshRequests);
		AccountUsageViewModel restartedAccount =
			Assert.Single(restartedViewModel.Accounts);
		Assert.Null(restartedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(restartedAccount.ProviderAccountIdentity);
		Assert.Null(restartedAccount.CurrentSnapshot);
	}

	[Fact]
	public async Task FailClosedAuthenticatedProviderBindingConflictAsync_WhenCleanupIntentAndDeleteFail_PersistsDisabledProfile()
	{
		string bindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(Guid.NewGuid());
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok",
			ProviderAccountIdentity: bindingIdentity);
		FakeUsageSnapshotStore snapshotStore = new(CreateReadySnapshot(profile))
		{
			DeleteHandler = (_, _, _) =>
				Task.FromException(new IOException("cache locked"))
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertCacheCleanupHandler = (_, _, _) =>
				Task.FromException(new IOException("journal locked"))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		bool wasPersisted =
			await viewModel.FailClosedAuthenticatedProviderBindingConflictAsync(
				account);

		Assert.True(wasPersisted);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		AccountUsageViewModel disconnectedAccount = Assert.Single(
			viewModel.Accounts);
		Assert.False(disconnectedAccount.IsEnabled);
		Assert.Null(disconnectedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(disconnectedAccount.ProviderAccountIdentity);
		Assert.Null(disconnectedAccount.CurrentSnapshot);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedProfile = Assert.Single(persisted.Accounts);
		Assert.False(persistedProfile.IsEnabled);
		Assert.Null(persistedProfile.ProviderAccountIdentity);
		Assert.Contains(
			"這些資料不會顯示在目前帳號",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		AccountProfile disabledProfile = disconnectedAccount.Profile;
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.UpdateAccountAsync(
				disabledProfile with { IsEnabled = true }));
		Assert.False(Assert.Single(viewModel.Accounts).IsEnabled);

		snapshotStore.DeleteHandler = null;
		await viewModel.UpdateAccountAsync(
			disabledProfile with { IsEnabled = true });

		AccountUsageViewModel reEnabledAccount = Assert.Single(viewModel.Accounts);
		Assert.True(reEnabledAccount.IsEnabled);
		Assert.Null(reEnabledAccount.Profile.ProviderAccountIdentity);
		Assert.Null(reEnabledAccount.ProviderAccountIdentity);
		Assert.Null(reEnabledAccount.CurrentSnapshot);
	}

	[Fact]
	public async Task InitializeAsync_WhenOnlyCacheCleanupIsPending_CompletesItBeforeCacheRestore()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		List<string> operations = new();
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteHandler = (_, _, _) =>
			{
				operations.Add("delete-cache");
				return Task.CompletedTask;
			},
			LoadHandler = (_, _) =>
			{
				operations.Add("load-cache");
				return Task.FromResult<UsageSnapshot?>(null);
			}
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				profile.Id,
				profile.Provider,
				CachePending: true,
				RuntimePending: false))
		{
			MarkCacheCompletedHandler = (_, _, _) =>
			{
				operations.Add("mark-cache-complete");
				return Task.CompletedTask;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(
			new[] { "delete-cache", "mark-cache-complete", "load-cache" },
			operations);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.CacheCompletedAccounts));
		Assert.Empty(cleanupPendingStore.RuntimeCompletedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
	}

	[Fact]
	public async Task InitializeAsync_WhenGrokCleanupTargetsConnectedProfile_DiscardsStaleIntentWithoutPurging()
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"user",
			"person@example.com");
		string publicBindingIdentity =
			GrokAccountBinding.CreatePublicBindingIdentity(binding.PublicBindingId);
		AccountProfile connectedGrok = new(
			accountId,
			ProviderKind.Grok,
			"Connected Grok",
			ProviderAccountIdentity: publicBindingIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(connectedGrok) with
		{
			ProviderAccountIdentity = publicBindingIdentity,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				connectedGrok.Id,
				connectedGrok.Provider,
				CachePending: true,
				RuntimePending: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(connectedGrok),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore,
			grokAccountBindingStore: new FakeGrokAccountBindingStore(binding),
			grokBindingCommitGate: new GrokBindingCommitGate());

		await viewModel.InitializeAsync();

		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		UsageSnapshot restoredSnapshot = Assert.IsType<UsageSnapshot>(
			Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Stale, restoredSnapshot.Status);
		Assert.Equal(cachedSnapshot.FetchedAt, restoredSnapshot.FetchedAt);
		Assert.Equal(
			cachedSnapshot.Account.ProviderAccountIdentity,
			restoredSnapshot.Account.ProviderAccountIdentity);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			connectedGrok.Id,
			connectedGrok.Provider));
	}

	[Fact]
	public async Task InitializeAsync_WhenCopilotCleanupTargetsRetainedProfile_DiscardsIntentWithoutPurging()
	{
		string providerAccountIdentity = CopilotAccountIdentityRules.Create(
			new CopilotAccountIdentity(
				"github.com",
				"NODE_RETAINED",
				1001,
				"octocat"));
		AccountProfile connectedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Connected Copilot",
			ProviderAccountIdentity: providerAccountIdentity);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				connectedCopilot.Id,
				connectedCopilot.Provider,
				CachePending: false,
				RuntimePending: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(connectedCopilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			connectedCopilot.Id,
			connectedCopilot.Provider));
	}

	[Fact]
	public async Task InitializeAsync_WhenCommittedPortableCopilotCleanupTargetsRetainedProfile_PurgesPrivateState()
	{
		AccountProfile retainedCopilot = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Imported Copilot");
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				retainedCopilot.Id,
				retainedCopilot.Provider,
				CachePending: false,
				RuntimePending: true,
				AllowRetainedPrivateStateDeletion: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(retainedCopilot),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(
			(retainedCopilot.Id, ProviderKind.Copilot),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			retainedCopilot.Id,
			retainedCopilot.Provider));
	}

	[Fact]
	public async Task InitializeAsync_WhenCopilotCleanupTargetsRemovedProfile_PurgesPrivateState()
	{
		Guid removedAccountId = Guid.NewGuid();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				removedAccountId,
				ProviderKind.Copilot,
				CachePending: false,
				RuntimePending: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.Equal(
			(removedAccountId, ProviderKind.Copilot),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			removedAccountId,
			ProviderKind.Copilot));
	}

	[Fact]
	public async Task InitializeAsync_WhenGrokConnectionIsStillPending_ProtectsGenericCleanupRetries()
	{
		AccountProfile unboundGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Unbound Grok");
		FakeUsageSnapshotStore snapshotStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				unboundGrok.Id,
				unboundGrok.Provider,
				CachePending: true,
				RuntimePending: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(unboundGrok),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore,
			grokConnectionPendingStore:
				new FakeGrokConnectionPendingStore());

		await viewModel.InitializeAsync();

		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Single(cleanupPendingStore.PendingWork);
		Assert.True(viewModel.IsAccountCleanupBlocked(
			unboundGrok.Id,
			unboundGrok.Provider));

		Assert.False(await viewModel.RetryPendingAccountCleanupsNowAsync());

		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Single(cleanupPendingStore.PendingWork);

		Assert.False(
			await viewModel.CompleteDeferredStartupAccountCleanupsAsync(
				new HashSet<Guid> { unboundGrok.Id }));

		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Single(cleanupPendingStore.PendingWork);
		Assert.True(viewModel.IsAccountCleanupBlocked(
			unboundGrok.Id,
			unboundGrok.Provider));

		Assert.True(
			await viewModel.CompleteDeferredStartupAccountCleanupsAsync(
				new HashSet<Guid>()));

		Assert.Equal(
			(unboundGrok.Id, unboundGrok.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Equal(
			(unboundGrok.Id, unboundGrok.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			unboundGrok.Id,
			unboundGrok.Provider));
	}

	[Fact]
	public async Task InitializeAsync_WhenRuntimeCleanupStillFails_BlocksCacheRestoreAndRefresh()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		int cacheLoadAttempts = 0;
		FakeUsageSnapshotStore snapshotStore = new()
		{
			LoadHandler = (account, _) =>
			{
				cacheLoadAttempts++;
				return Task.FromResult<UsageSnapshot?>(CreateReadySnapshot(account));
			}
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
				Task.FromException(new IOException("測試用執行狀態清理失敗。"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				profile.Id,
				profile.Provider,
				CachePending: false,
				RuntimePending: true));
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		Assert.Equal(0, cacheLoadAttempts);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Empty(cleanupPendingStore.CacheCompletedAccounts);
		Assert.Empty(cleanupPendingStore.RuntimeCompletedAccounts);
		AccountCleanupPendingWork remainingWork = Assert.Single(
			cleanupPendingStore.PendingWork);
		Assert.False(remainingWork.CachePending);
		Assert.True(remainingWork.RuntimePending);
		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.False(viewModel.CanRefresh);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenCleanupKeepsFailing_BacksOffWithoutBlockingHealthyAccounts()
	{
		DateTimeOffset now = new(
			2026,
			8,
			16,
			8,
			0,
			0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(now);
		AccountProfile healthyProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		Guid orphanAccountId = Guid.NewGuid();
		int purgeAttempts = 0;
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
			{
				purgeAttempts++;
				return Task.FromException(
					new IOException("測試用持續清理失敗。"));
			}
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				orphanAccountId,
				ProviderKind.Claude,
				CachePending: false,
				RuntimePending: true));
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
				Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(healthyProfile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: new FakeDashboardPreferencesStore(),
			accountRuntimeStatePurger: runtimeStatePurger,
			timeProvider: timeProvider,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageInBackgroundAsync();
		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(2, purgeAttempts);
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.All(
			refreshCoordinator.RefreshRequests,
			request => Assert.Equal(healthyProfile.Id, request.Id));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(3, purgeAttempts);
		Assert.Equal(3, refreshCoordinator.RefreshRequests.Count);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenPendingCleanupRecovers_UnblocksAndRefreshes()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		int purgeAttempts = 0;
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
			{
				purgeAttempts++;
				return purgeAttempts == 1
					? Task.FromException(new IOException(
						"測試用第一次執行狀態清理失敗。"))
					: Task.CompletedTask;
			}
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				profile.Id,
				profile.Provider,
				CachePending: false,
				RuntimePending: true));
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
				Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);

		await viewModel.InitializeAsync();

		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(2, purgeAttempts);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.RuntimeCompletedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Equal(profile.Id, Assert.Single(refreshCoordinator.RefreshRequests).Id);
		Assert.DoesNotContain(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ScheduleAccountCleanupRetryAsync_ForOrphan_RetriesDuringBackgroundRefresh()
	{
		Guid accountId = Guid.NewGuid();
		FakeUsageSnapshotStore snapshotStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		await viewModel.ScheduleAccountCleanupRetryAsync(
			accountId,
			ProviderKind.Claude);

		Assert.True(viewModel.IsAccountCleanupBlocked(
			accountId,
			ProviderKind.Claude));
		Assert.Equal(
			(accountId, ProviderKind.Claude),
			Assert.Single(cleanupPendingStore.UpsertedAccounts));
		Assert.Single(cleanupPendingStore.PendingWork);

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(
			(accountId, ProviderKind.Claude),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Equal(
			(accountId, ProviderKind.Claude),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			accountId,
			ProviderKind.Claude));
	}

	[Fact]
	public async Task ReplacePortableSettingsAsync_WhenAnotherCleanupRemainsBlocked_PreservesHealthWarning()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "person@example.com");
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
				Task.FromException(new IOException("測試用執行狀態清理失敗。"))
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new(
			new AccountCleanupPendingWork(
				profile.Id,
				profile.Provider,
				CachePending: false,
				RuntimePending: true));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				new[] { profile },
				UsageSortMode.Manual,
				UsageDisplayMode.Used));

		Assert.True(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.Contains(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task InitializeAsync_WhenUnboundProfileHasUniqueCachedIdentity_RestoresCachedUsageWithoutSavingProfile()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = "cached@example.com"
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot!.Status);
		Assert.Equal("cached@example.com", account.ProviderAccountIdentity);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.NotEmpty(account.UsageMetrics);
		Assert.Single(refreshCoordinator.SeededSnapshots);
		Assert.Empty(profileStore.SavedSnapshots);
	}

	[Fact]
	public async Task AcceptClaudeQuotaRiskAsync_WithLegacyCachedIdentity_DoesNotRestoreOrBindIt()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = "cached@example.com"
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore(cachedSnapshot));
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);

		bool wasAccepted = await viewModel.AcceptClaudeQuotaRiskAsync(account);

		Assert.True(wasAccepted);
		Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.CurrentSnapshot);
		Assert.Empty(account.UsageMetrics);
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		Assert.True(savedProfile.HasAcceptedClaudeQuotaRisk);
		Assert.Null(savedProfile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RemoveAndReAddAccount_WithSameId_DoesNotReuseRuntimeOnlyIdentityMarker()
	{
		AccountProfile originalProfile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Original");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(originalProfile) with
		{
			ProviderAccountIdentity = "old@example.com"
		};
		FakeAccountProfileStore profileStore = new(originalProfile);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		Assert.Equal(
			"old@example.com",
			Assert.Single(viewModel.Accounts).ProviderAccountIdentity);

		await viewModel.RemoveAccountAsync(originalProfile.Id);
		await viewModel.AddAccountAsync(new AccountProfile(
			originalProfile.Id,
			ProviderKind.Copilot,
			"Replacement"));
		refreshCoordinator.RefreshHandler = account => Task.FromResult(
			CreateReadySnapshot(account) with
			{
				Status = SnapshotStatus.Stale,
				ProviderAccountIdentity = "new@example.com"
			});

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel replacement = Assert.Single(viewModel.Accounts);
		Assert.Equal(
			"new@example.com",
			replacement.Profile.ProviderAccountIdentity);
		Assert.Equal(
			"new@example.com",
			replacement.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WithLegacyClaudeCache_DoesNotRestoreOrPersistIdentity()
	{
		AccountProfile cachedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Cached Claude");
		AccountProfile refreshedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "codex@example.com");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(cachedProfile) with
		{
			ProviderAccountIdentity = "cached@example.com"
		};
		FakeAccountProfileStore profileStore = new(cachedProfile, refreshedProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(
			new[] { refreshedProfile.Id },
			refreshCoordinator.RefreshRequests.Select(account => account.Id));
		AccountUsageViewModel cachedAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == cachedProfile.Id);
		Assert.Null(cachedAccount.ProviderAccountIdentity);
		Assert.Null(cachedAccount.Profile.ProviderAccountIdentity);
		Assert.Null(cachedAccount.CurrentSnapshot);
		Assert.Empty(cachedAccount.UsageMetrics);
		Assert.Empty(profileStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenRuntimeOnlyCachedIdentityGetsVerifiedReady_PersistsBinding()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = "cached@example.com"
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = "CACHED@example.com",
					Error = null
				})
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.True(ProviderAccountIdentityRules.Comparer.Equals(
			"cached@example.com",
			Assert.Single(refreshCoordinator.RefreshRequests)
				.ProviderAccountIdentity));
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		Assert.True(ProviderAccountIdentityRules.Comparer.Equals(
			"cached@example.com",
			savedProfile.ProviderAccountIdentity));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot!.Status);
		Assert.True(ProviderAccountIdentityRules.Comparer.Equals(
			"cached@example.com",
			account.Profile.ProviderAccountIdentity));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenRuntimeOnlyLegacyAntigravityIdentityGetsLiveOfficialLocalSession_MigratesBinding()
	{
		const string LegacyIdentity = "legacy@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			SourceTrust = SourceTrust.PrivateExperimental,
			ProviderAccountIdentity = LegacyIdentity
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini weekly",
						25d,
						"已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Equal(LegacyIdentity, account.ProviderAccountIdentity);

		await viewModel.RefreshUsageAsync();

		AccountProfile request = Assert.Single(refreshCoordinator.RefreshRequests);
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		Assert.Equal(LegacyIdentity, request.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, savedProfile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Contains(
			snapshotStore.SavedSnapshots,
			snapshot => string.Equals(
				snapshot.ProviderAccountIdentity,
				LocalSessionIdentity,
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenRuntimeOnlyIdentityGetsUnavailableReady_DoesNotReleaseOrPersistBinding()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = "cached@example.com"
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					SourceTrust = SourceTrust.Unavailable,
					ProviderAccountIdentity = "cached@example.com"
				})
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Equal("cached@example.com", account.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(
			"cached@example.com",
			Assert.Single(refreshCoordinator.RefreshRequests)
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task InitializeAsync_WhenUnboundCachedIdentitiesAreDuplicated_SkipsAllDuplicates()
	{
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"First");
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Second");
		UsageSnapshot firstSnapshot = CreateReadySnapshot(firstProfile) with
		{
			ProviderAccountIdentity = "duplicate@example.com"
		};
		UsageSnapshot secondSnapshot = CreateReadySnapshot(secondProfile) with
		{
			ProviderAccountIdentity = "DUPLICATE@example.com"
		};
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(firstSnapshot, secondSnapshot));

		await viewModel.InitializeAsync();

		Assert.All(viewModel.Accounts, account =>
		{
			Assert.Null(account.CurrentSnapshot);
			Assert.Null(account.ProviderAccountIdentity);
		});
		Assert.Empty(refreshCoordinator.SeededSnapshots);
		Assert.Empty(profileStore.SavedSnapshots);
	}

	[Fact]
	public async Task InitializeAsync_WhenUnboundCachedIdentityIsOwnedByAnotherProfile_RejectsCache()
	{
		AccountProfile ownerProfile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Owner",
			ProviderAccountIdentity: "owner@example.com");
		AccountProfile unboundProfile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Unbound");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(unboundProfile) with
		{
			ProviderAccountIdentity = "OWNER@example.com"
		};
		FakeAccountProfileStore profileStore = new(ownerProfile, unboundProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		AccountUsageViewModel owner = Assert.Single(
			viewModel.Accounts,
			account => account.Id == ownerProfile.Id);
		AccountUsageViewModel unbound = Assert.Single(
			viewModel.Accounts,
			account => account.Id == unboundProfile.Id);
		Assert.Null(owner.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, unbound.CurrentSnapshot!.Status);
		Assert.Empty(unbound.UsageMetrics);
		Assert.Null(unbound.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
		Assert.Empty(profileStore.SavedSnapshots);
	}

	[Fact]
	public async Task InitializeAsync_WhenUnboundCacheHasNoIdentity_SkipsCachedUsage()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = null
		};
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
		Assert.Empty(profileStore.SavedSnapshots);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("other@example.com")]
	public async Task InitializeAsync_WhenCachedIdentityDoesNotMatchBinding_SkipsCache(
		string? cachedIdentity)
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot",
			ProviderAccountIdentity: "bound@example.com");
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(profile) with
		{
			ProviderAccountIdentity = cachedIdentity
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		Assert.Null(Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenAccountIsReEnabled_RestoresCachedUsage()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 40, "已使用 40%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com");
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.UpdateAccountAsync(profile with { IsEnabled = false });
		Assert.Equal("已停止檢查", Assert.Single(viewModel.Accounts).StatusText);

		await viewModel.UpdateAccountAsync(profile with { IsEnabled = true });

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("顯示上次資料", account.StatusText);
		Assert.Equal("已使用 40%", Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Equal(2, refreshCoordinator.SeededSnapshots.Count);
	}

	[Fact]
	public async Task AddUpdateAndRemoveAsync_PersistsEachCandidateSnapshot()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"私人帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"工作帳號");

		await viewModel.AddAccountAsync(firstAccount);
		await viewModel.AddAccountAsync(secondAccount);
		await viewModel.UpdateAccountAsync(firstAccount with
		{
			DisplayName = "私人帳號（已更新）",
			IsEnabled = false
		});
		await viewModel.RemoveAccountAsync(secondAccount.Id);

		AccountUsageViewModel remainingAccount = Assert.Single(viewModel.Accounts);
		Assert.Equal(firstAccount.Id, remainingAccount.Id);
		Assert.Equal("私人帳號（已更新）", remainingAccount.AccountName);
		Assert.False(remainingAccount.IsEnabled);
		Assert.Collection(
			store.SavedSnapshots,
			firstSnapshot => Assert.Equal(new[] { firstAccount }, firstSnapshot),
			secondSnapshot =>
			{
				Assert.Contains(firstAccount, secondSnapshot);
				Assert.Contains(secondAccount, secondSnapshot);
			},
			thirdSnapshot =>
			{
				Assert.Contains(
					thirdSnapshot,
					account =>
						(account.Id == firstAccount.Id) &&
						(account.DisplayName == "私人帳號（已更新）") &&
						!account.IsEnabled);
				Assert.Contains(secondAccount, thirdSnapshot);
			},
			fourthSnapshot =>
			{
				AccountProfile persistedAccount = Assert.Single(fourthSnapshot);
				Assert.Equal(firstAccount.Id, persistedAccount.Id);
				Assert.Equal("私人帳號（已更新）", persistedAccount.DisplayName);
				Assert.False(persistedAccount.IsEnabled);
			});
	}

	[Fact]
	public async Task AddAccountAsync_WithBlankNicknames_PreservesBlankForEveryCard()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			string.Empty));
		await viewModel.AddAccountAsync(new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			string.Empty));

		Assert.Equal(
			new[] { "Claude", "Claude" },
			viewModel.Accounts.Select(account => account.AccountName));
		Assert.Equal(
			new[] { "Claude", "Claude" },
			viewModel.Accounts.Select(account => account.AccountTitle));
		Assert.All(
			viewModel.Accounts,
			account => Assert.False(account.HasAccountNickname));
		Assert.Equal(
			new[] { string.Empty, string.Empty },
			store.SavedSnapshots[^1].Select(account => account.DisplayName));
	}

	[Fact]
	public async Task AccountMutation_CannotInjectOrReplaceProviderAccountIdentity()
	{
		const string ExistingIdentity = "existing@example.com";
		AccountProfile existing = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: ExistingIdentity);
		AccountProfile added = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: "injected@example.com");
		FakeAccountProfileStore store = new(existing);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(added);
		await viewModel.UpdateAccountAsync(existing with
		{
			DisplayName = "Claude renamed",
			ProviderAccountIdentity = "replacement@example.com"
		});

		AccountProfile currentExisting = viewModel.Accounts.Single(
			account => account.Id == existing.Id).Profile;
		AccountProfile currentAdded = viewModel.Accounts.Single(
			account => account.Id == added.Id).Profile;
		Assert.Equal(ExistingIdentity, currentExisting.ProviderAccountIdentity);
		Assert.Null(currentAdded.ProviderAccountIdentity);
		Assert.Equal(
			ExistingIdentity,
			store.SavedSnapshots[^1].Single(
				account => account.Id == existing.Id).ProviderAccountIdentity);
		Assert.Null(store.SavedSnapshots[^1].Single(
			account => account.Id == added.Id).ProviderAccountIdentity);
	}

	[Fact]
	public async Task AddAccountAsync_WhenGrokAlreadyExists_AddsSecondCard()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"First");
		AccountProfile addedAccount = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Second");
		FakeAccountProfileStore store = new(existingAccount);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		await viewModel.AddAccountAsync(addedAccount);

		Assert.Equal(2, viewModel.Accounts.Count);
		Assert.Contains(
			viewModel.Accounts,
			account => account.Id == existingAccount.Id);
		Assert.Contains(
			viewModel.Accounts,
			account => account.Id == addedAccount.Id);
		IReadOnlyList<AccountProfile> savedAccounts =
			Assert.Single(store.SavedSnapshots);
		Assert.Equal(2, savedAccounts.Count);
		Assert.All(
			savedAccounts,
			account => Assert.Equal(ProviderKind.Grok, account.Provider));
		Assert.Contains(
			savedAccounts,
			account => account.Id == existingAccount.Id);
		Assert.Contains(
			savedAccounts,
			account => account.Id == addedAccount.Id);
	}

	[Fact]
	public async Task AddAccountAsync_WhenAntigravityAlreadyExists_RejectsSecondAccount()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity");
		FakeAccountProfileStore store = new(existingAccount);
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.AddAccountAsync(new AccountProfile(
					Guid.NewGuid(),
					ProviderKind.Antigravity,
					"另一個 Antigravity")));

		Assert.Equal(
			"Antigravity 目前只能建立一個帳號。",
			exception.Message);
		Assert.Equal(existingAccount, Assert.Single(viewModel.Accounts).Profile);
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task AddAccountAsync_WhenSaveFails_DoesNotPublishCandidate()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		store.ShouldFailSave = true;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Copilot,
			"Copilot 帳號");

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.AddAccountAsync(profile));

		Assert.Empty(viewModel.Accounts);
		Assert.True(viewModel.HasNoAccounts);
		Assert.Single(store.SavedSnapshots);
	}

	[Fact]
	public async Task AddAccountAsync_WhenSaveCommitsButBackupFails_PublishesCandidateAndWarning()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		store.ShouldFailAfterCommit = true;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");

		AccountProfileStoreException exception =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => viewModel.AddAccountAsync(profile));

		Assert.True(exception.HasCommittedChanges);
		Assert.Equal(profile, Assert.Single(viewModel.Accounts).Profile);
		Assert.False(viewModel.HasNoAccounts);
		Assert.Equal(exception.Message, viewModel.AccountSettingsMessage);
		Assert.False(viewModel.IsManagingAccounts);
		Assert.Single(store.SavedSnapshots);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenSaveCommitsButBackupFails_StillDeletesCachedSnapshot()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		FakeAccountProfileStore store = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();
		store.ShouldFailAfterCommit = true;

		AccountProfileStoreException exception =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => viewModel.RemoveAccountAsync(profile.Id));

		Assert.True(exception.HasCommittedChanges);
		Assert.Empty(viewModel.Accounts);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Equal(exception.Message, viewModel.AccountSettingsMessage);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenCacheDeleteSucceeds_ReportsCacheWasCleared()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		FakeAccountProfileStore store = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RemoveAccountAsync(profile.Id);

		Assert.Empty(viewModel.Accounts);
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		Assert.Contains(
			"並清除上次用量",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"可能仍保留",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenCacheDeleteTransientlyFails_RetriesAndCompletesDurableCleanup()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		int deleteAttempts = 0;
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteHandler = (_, _, _) =>
			{
				deleteAttempts++;
				return deleteAttempts < 3
					? Task.FromException(new IOException("測試用暫時性快取刪除失敗。"))
					: Task.CompletedTask;
			}
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountProfileStore accountStore = new(profile);
		bool cleanupIntentWasDurableBeforeAccountSave = false;
		accountStore.SaveHandler = (_, _) =>
		{
			cleanupIntentWasDurableBeforeAccountSave =
				cleanupPendingStore.UpsertedAccounts.Contains(
					(profile.Id, profile.Provider));
			return Task.CompletedTask;
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			new FakeDashboardPreferencesStore(),
			runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		await viewModel.RemoveAccountAsync(profile.Id);

		Assert.True(cleanupIntentWasDurableBeforeAccountSave);
		Assert.Equal(3, deleteAttempts);
		Assert.Equal(3, snapshotStore.DeletedSnapshots.Count);
		Assert.All(
			snapshotStore.DeletedSnapshots,
			deleted => Assert.Equal((profile.Id, profile.Provider), deleted));
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.UpsertedAccounts));
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.CacheCompletedAccounts));
		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(cleanupPendingStore.RuntimeCompletedAccounts));
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(profile.Id, profile.Provider));
		Assert.DoesNotContain(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenCleanupIntentFailsOnce_RestoresBlockAndAllowsRetry()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		FakeAccountProfileStore accountStore = new(profile);
		int upsertAttemptCount = 0;
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			UpsertHandler = (_, _) =>
				Interlocked.Increment(ref upsertAttemptCount) == 1
					? Task.FromException(new IOException(
						"測試用清理工作儲存失敗。"))
					: Task.CompletedTask
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();

		InvalidOperationException exception =
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => viewModel.RemoveAccountAsync(profile.Id));

		Assert.Contains(
			"帳號設定尚未變更",
			exception.Message,
			StringComparison.Ordinal);
		Assert.Empty(accountStore.SavedSnapshots);
		Assert.Equal(profile, Assert.Single(viewModel.Accounts).Profile);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			profile.Id,
			profile.Provider));

		await viewModel.RemoveAccountAsync(profile.Id);

		Assert.Equal(2, upsertAttemptCount);
		Assert.Empty(viewModel.Accounts);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			profile.Id,
			profile.Provider));
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenUnboundGrokSaveFails_PreservesRecoverableStateUntilSuccessfulRetry()
	{
		AccountProfile connectedGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recoverable Grok");
		Guid unrelatedAccountId = Guid.NewGuid();
		FakeAccountProfileStore accountStore = new(connectedGrok)
		{
			ShouldFailSave = true
		};
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (accountId, _, _) =>
				accountId == unrelatedAccountId
					? Task.FromException(new IOException(
						"測試用既有清理工作仍待重試。"))
					: Task.CompletedTask
		};
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		await cleanupPendingStore.UpsertAsync(
			new[] { (unrelatedAccountId, ProviderKind.Claude) });

		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.RemoveAccountAsync(connectedGrok.Id));
		await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.Equal(connectedGrok, Assert.Single(viewModel.Accounts).Profile);
		Assert.DoesNotContain(
			runtimeStatePurger.PurgedAccounts,
			purged =>
				(purged.AccountId == connectedGrok.Id) &&
				(purged.Provider == connectedGrok.Provider));
		Assert.DoesNotContain(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == connectedGrok.Id) &&
				(work.Provider == connectedGrok.Provider));
		Assert.Contains(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == unrelatedAccountId) &&
				(work.Provider == ProviderKind.Claude));
		Assert.False(viewModel.IsAccountCleanupBlocked(
			connectedGrok.Id,
			connectedGrok.Provider));

		accountStore.ShouldFailSave = false;
		await viewModel.RemoveAccountAsync(connectedGrok.Id);

		Assert.Empty(viewModel.Accounts);
		Assert.Equal(
			1,
			runtimeStatePurger.PurgedAccounts.Count(purged =>
				(purged.AccountId == connectedGrok.Id) &&
				(purged.Provider == connectedGrok.Provider)));
		Assert.Contains(
			cleanupPendingStore.PendingWork,
			work =>
				(work.AccountId == unrelatedAccountId) &&
				(work.Provider == ProviderKind.Claude));
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenGrokIntentRollbackFails_PreservesPrimaryFailureAndCleanupBlock()
	{
		AccountProfile recoverableGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recoverable Grok");
		FakeAccountProfileStore accountStore = new(recoverableGrok)
		{
			ShouldFailSave = true
		};
		int runtimeRollbackAttemptCount = 0;
		FakeAccountCleanupPendingStore cleanupPendingStore = new()
		{
			MarkRuntimeCompletedHandler = (_, _, _) =>
				Interlocked.Increment(ref runtimeRollbackAttemptCount) == 1
					? Task.FromException(new IOException(
						"測試用 cleanup intent 撤銷失敗。"))
					: Task.CompletedTask
		};
		DateTimeOffset startedAtUtc = new(
			2026,
			8,
			18,
			1,
			0,
			0,
			TimeSpan.Zero);
		FakeGrokConnectionPendingStore grokConnectionPendingStore = new(
			new GrokConnectionPendingWork(
				recoverableGrok.Id,
				Guid.NewGuid(),
				startedAtUtc,
				startedAtUtc + TimeSpan.FromSeconds(5),
				GrokConnectionPendingStage.BindingPersisted,
				Guid.NewGuid()));
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			accountStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore,
			grokConnectionPendingStore: grokConnectionPendingStore);
		await viewModel.InitializeAsync();

		AccountProfileStoreException exception =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => viewModel.RemoveAccountAsync(recoverableGrok.Id));

		Assert.Equal("測試用儲存失敗。", exception.Message);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		AccountCleanupPendingWork pendingWork = Assert.Single(
			cleanupPendingStore.PendingWork);
		Assert.False(pendingWork.CachePending);
		Assert.True(pendingWork.RuntimePending);
		Assert.True(viewModel.IsAccountCleanupBlocked(
			recoverableGrok.Id,
			recoverableGrok.Provider));
		Assert.Contains(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		bool cleanupCompleted =
			await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.True(cleanupCompleted);
		Assert.Equal(2, runtimeRollbackAttemptCount);
		Assert.True(grokConnectionPendingStore.LoadCallCount > 0);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Empty(cleanupPendingStore.PendingWork);
		Assert.False(viewModel.IsAccountCleanupBlocked(
			recoverableGrok.Id,
			recoverableGrok.Provider));
	}

	[Fact]
	public async Task RetryPendingAccountCleanupsNowAsync_WhenGrokJournalLoadFails_ProtectsRetainedProfile()
	{
		AccountProfile retainedGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recoverable Grok");
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeGrokConnectionPendingStore grokConnectionPendingStore = new()
		{
			LoadHandler = _ => Task.FromException<
				IReadOnlyList<GrokConnectionPendingWork>>(
				new IOException("測試用 Grok journal 讀取失敗。"))
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(retainedGrok),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore,
			grokConnectionPendingStore: grokConnectionPendingStore);
		await viewModel.InitializeAsync();
		await viewModel.ScheduleAccountCleanupRetryAsync(
			retainedGrok.Id,
			retainedGrok.Provider);

		bool cleanupCompleted =
			await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.False(cleanupCompleted);
		Assert.True(grokConnectionPendingStore.LoadCallCount > 0);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Single(cleanupPendingStore.PendingWork);
		Assert.True(viewModel.IsAccountCleanupBlocked(
			retainedGrok.Id,
			retainedGrok.Provider));
	}

	[Fact]
	public async Task RetryPendingAccountCleanupsNowAsync_WithoutGrokJournalStore_ProtectsRetainedProfile()
	{
		AccountProfile retainedGrok = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recoverable Grok");
		FakeAccountCleanupPendingStore cleanupPendingStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(retainedGrok),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			accountCleanupPendingStore: cleanupPendingStore);
		await viewModel.InitializeAsync();
		await viewModel.ScheduleAccountCleanupRetryAsync(
			retainedGrok.Id,
			retainedGrok.Provider);

		bool cleanupCompleted =
			await viewModel.RetryPendingAccountCleanupsNowAsync();

		Assert.False(cleanupCompleted);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Single(cleanupPendingStore.PendingWork);
		Assert.True(viewModel.IsAccountCleanupBlocked(
			retainedGrok.Id,
			retainedGrok.Provider));
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenCacheDeleteFails_WarnsCacheMayRemain()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		FakeAccountProfileStore store = new(profile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteHandler = (_, _, _) =>
				Task.FromException(new IOException("測試用快取刪除失敗。"))
		};
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RemoveAccountAsync(profile.Id);

		Assert.Empty(viewModel.Accounts);
		Assert.Empty(Assert.Single(store.SavedSnapshots));
		Assert.Equal(3, snapshotStore.DeletedSnapshots.Count);
		Assert.All(
			snapshotStore.DeletedSnapshots,
			deleted => Assert.Equal((profile.Id, profile.Provider), deleted));
		Assert.Contains(
			"資料可能仍保留在這台電腦",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"並清除上次用量",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenBackupAndCacheDeleteFail_ReportsBothWarnings()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號");
		FakeAccountProfileStore store = new(profile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteHandler = (_, _, _) =>
				Task.FromException(new IOException("測試用快取刪除失敗。"))
		};
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();
		store.ShouldFailAfterCommit = true;

		AccountProfileStoreException exception =
			await Assert.ThrowsAsync<AccountProfileStoreException>(
				() => viewModel.RemoveAccountAsync(profile.Id));

		Assert.True(exception.HasCommittedChanges);
		Assert.Empty(viewModel.Accounts);
		Assert.Contains(
			exception.Message,
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			"資料可能仍保留在這台電腦",
			viewModel.AccountSettingsMessage,
			StringComparison.Ordinal);
		Assert.Equal(3, snapshotStore.DeletedSnapshots.Count);
		Assert.All(
			snapshotStore.DeletedSnapshots,
			deleted => Assert.Equal((profile.Id, profile.Provider), deleted));
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task AddAccountAsync_WhenNicknameIsBlank_SavesWithoutNickname()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"   ");

		await viewModel.AddAccountAsync(profile);

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(string.Empty, account.Profile.DisplayName);
		Assert.False(account.HasAccountNickname);
		Assert.Equal("Claude", account.AccountTitle);
		Assert.Equal(
			string.Empty,
			Assert.Single(Assert.Single(store.SavedSnapshots)).DisplayName);
	}

	[Fact]
	public async Task AddAccountAsync_WhenNameContainsControlCharacter_DoesNotSave()
	{
		FakeAccountProfileStore store = new();
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude\n管理員");

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => viewModel.AddAccountAsync(profile));

		Assert.Contains("控制字元", exception.Message, StringComparison.Ordinal);
		Assert.Empty(viewModel.Accounts);
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenNameContainsControlCharacter_DoesNotSave()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore store = new(profile);
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => viewModel.UpdateAccountAsync(profile with
			{
				DisplayName = "Claude\t管理員"
			}));

		Assert.Contains("控制字元", exception.Message, StringComparison.Ordinal);
		Assert.Equal("Claude", Assert.Single(viewModel.Accounts).AccountName);
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task InitializeAsync_WhenStoreBlocksWrites_DisablesAccountManagement()
	{
		AccountProfile existingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"既有帳號");
		FakeAccountProfileStore store = new(existingAccount)
		{
			CanSaveOnLoad = false
		};
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();

		Assert.False(viewModel.CanManageAccounts);
		Assert.True(viewModel.HasAccountSettingsHealthMessage);
		Assert.Equal(
			"測試用停寫狀態。",
			viewModel.AccountSettingsHealthMessage);
		Assert.False(Assert.Single(viewModel.Accounts).CanManage);
		await viewModel.ToggleUsageDisplayModeAsync();
		Assert.Equal(
			"測試用停寫狀態。",
			viewModel.AccountSettingsHealthMessage);
		Assert.Equal(
			"已改為顯示剩餘用量。",
			viewModel.AccountSettingsMessage);
		await Assert.ThrowsAsync<AccountProfileStoreException>(
			() => viewModel.AddAccountAsync(new AccountProfile(
				Guid.NewGuid(),
				ProviderKind.Codex,
				"新帳號")));
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task InitializeAsync_WhenCorruptFileWasPreserved_ShowsRecoveryPath()
	{
		const string recoveryPath =
			@"C:\Users\tester\AppData\Local\AiUsageDashboard\accounts.corrupt.json";
		FakeAccountProfileStore store = new()
		{
			LoadStatusOnLoad = AccountProfileLoadStatus.RecoveredCorruptFile,
			LoadMessage = "帳號設定檔已復原；原損壞檔已保留。",
			RecoveryPathOnLoad = recoveryPath
		};
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());

		await viewModel.InitializeAsync();

		Assert.True(viewModel.HasAccountSettingsHealthMessage);
		Assert.Contains(
			"帳號設定檔已復原",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			recoveryPath,
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Equal(string.Empty, viewModel.AccountSettingsMessage);
	}

	[Fact]
	public async Task SuccessfulAccountSave_ClearsRepairableBackupHealthWarning()
	{
		FakeAccountProfileStore store = new()
		{
			LoadMessage =
				"帳號設定已載入，但無法建立或修復安全備份；目前仍可使用。"
		};
		DashboardViewModel viewModel = new(store, new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		Assert.True(viewModel.HasAccountSettingsHealthMessage);

		await viewModel.AddAccountAsync(new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 帳號"));

		Assert.False(viewModel.HasAccountSettingsHealthMessage);
		Assert.Equal(string.Empty, viewModel.AccountSettingsHealthMessage);
	}

	[Fact]
	public async Task UpdateAccountAsync_PreservesOtherAccountDisplayState()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第二個帳號");
		FakeAccountProfileStore store = new(firstAccount, secondAccount);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(store, refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel originalSecondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);
		string originalSecondaryText = originalSecondViewModel.SecondaryText;

		await viewModel.UpdateAccountAsync(firstAccount with
		{
			DisplayName = "第一個帳號（已更新）"
		});
		AccountUsageViewModel currentSecondViewModel = viewModel.Accounts.Single(
			account => account.Id == secondAccount.Id);

		Assert.Same(originalSecondViewModel, currentSecondViewModel);
		Assert.Equal(originalSecondaryText, currentSecondViewModel.SecondaryText);
	}

	[Fact]
	public async Task RefreshUsageAsync_AppliesSnapshotAndSkipsDisabledAccount()
	{
		AccountProfile enabledAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Pro",
			ProviderAccountIdentity: "enabled@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile disabledAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"停用帳號",
			IsEnabled: false);
		FakeAccountProfileStore store = new(enabledAccount, disabledAccount);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("five_hour", "5 小時", 32.5, "已使用 32.5%")
				},
				SourceTrust.Official,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					account.ProviderAccountIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(store, refreshCoordinator, snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountProfile request = Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(enabledAccount.Id, request.Id);
		AccountUsageViewModel enabledViewModel = viewModel.Accounts.Single(
			account => account.Id == enabledAccount.Id);
		AccountUsageViewModel disabledViewModel = viewModel.Accounts.Single(
			account => account.Id == disabledAccount.Id);
		Assert.Equal("已使用 32.5%", enabledViewModel.PrimaryText);
		Assert.Equal(32.5, enabledViewModel.UsedPercent);
		Assert.Equal("可用", enabledViewModel.StatusText);
		Assert.Equal("已停止檢查", disabledViewModel.StatusText);
		UsageSnapshot persistedSnapshot = Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Equal(enabledAccount.Id, persistedSnapshot.Account.Id);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenLiveIdentityIsUnbound_PersistsBindingAndCache()
	{
		const string LiveIdentity = "live@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = LiveIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		UsageSnapshot savedSnapshot = Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Equal(LiveIdentity, savedProfile.ProviderAccountIdentity);
		Assert.Equal(LiveIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LiveIdentity, account.ProviderAccountIdentity);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Equal(LiveIdentity, savedSnapshot.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenLegacyAntigravityBindingGetsLiveOfficialLocalSession_MigratesAndPersists()
	{
		const string LegacyIdentity = "legacy@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LegacyIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini weekly",
						25d,
						"已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		AccountProfile savedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		UsageSnapshot savedSnapshot = Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Equal(LocalSessionIdentity, savedProfile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Equal(LocalSessionIdentity, savedSnapshot.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenLegacyAntigravityMigrationSaveFails_RollsBackAndSkipsCache()
	{
		const string LegacyIdentity = "legacy@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LegacyIdentity);
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini weekly",
						25d,
						"已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		AccountProfile attemptedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		Assert.Equal(LocalSessionIdentity, attemptedProfile.ProviderAccountIdentity);
		Assert.Equal(LegacyIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LegacyIdentity, account.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhileLegacyAntigravityMigrationSaveIsPending_ShowsLastConfirmedIdentity()
	{
		const string LegacyIdentity = "legacy@example.com";
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		TaskCompletionSource saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowSave = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LegacyIdentity);
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = async (_, _) =>
			{
				saveStarted.TrySetResult();
				await allowSave.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini weekly",
						25d,
						"已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);
		Assert.Equal(LegacyIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LegacyIdentity, account.ProviderAccountIdentity);
		Assert.Equal($"{LegacyIdentity}（上次確認）", account.AccountDisplayText);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.Empty(snapshotStore.SavedSnapshots);

		allowSave.TrySetResult();
		await refreshTask;

		Assert.Equal(LocalSessionIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Single(snapshotStore.SavedSnapshots);
	}

	[Theory]
	[InlineData(SourceTrust.PrivateExperimental, SnapshotStatus.Ready, null)]
	[InlineData(SourceTrust.OfficialExperimental, SnapshotStatus.Stale, null)]
	[InlineData(SourceTrust.Official, SnapshotStatus.Ready, null)]
	[InlineData(SourceTrust.OfficialExperimental, SnapshotStatus.Ready, "warning")]
	public async Task RefreshUsageAsync_WhenAntigravityLocalSessionMigrationIsNotLiveOfficialReady_Rejects(
		SourceTrust sourceTrust,
		SnapshotStatus status,
		string? error)
	{
		const string LegacyIdentity = "legacy@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LegacyIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.gemini.weekly",
						"Gemini weekly",
						25d,
						"已使用 25%")
				},
				sourceTrust,
				status,
				DateTimeOffset.UtcNow,
				Error: error,
				ProviderAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(LegacyIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhileInitialIdentitySaveIsPending_DoesNotShowConfirmedIdentity()
	{
		const string LiveIdentity = "live@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowSave = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = async (_, _) =>
			{
				saveStarted.TrySetResult();
				await allowSave.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = LiveIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.False(account.HasProviderAccountIdentity);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.Equal("尚未確認", account.AccountDisplayText);
		Assert.Empty(snapshotStore.SavedSnapshots);

		allowSave.TrySetResult();
		await refreshTask;

		Assert.Equal(LiveIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LiveIdentity, account.ProviderAccountIdentity);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Single(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenInitialIdentityBindingSaveFails_RollsBackAndSkipsCache()
	{
		const string LiveIdentity = "live@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = LiveIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		AccountProfile attemptedProfile = Assert.Single(
			Assert.Single(profileStore.SavedSnapshots));
		Assert.Equal(LiveIdentity, attemptedProfile.ProviderAccountIdentity);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.False(account.HasProviderAccountIdentity);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.Equal("尚未確認", account.AccountDisplayText);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.Contains(
			"無法儲存這次連接的帳號資訊",
			account.SecondaryText,
			StringComparison.Ordinal);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenIdentitySaveCommitsButBackupFails_RetainsBindingAndCache()
	{
		const string LiveIdentity = "live@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailAfterCommit = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = LiveIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(LiveIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(LiveIdentity, account.ProviderAccountIdentity);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Contains(
			"安全備份更新失敗",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("new@example.com")]
	public async Task RefreshUsageAsync_WhenBoundIdentityIsMissingOrChanged_RejectsAndSkipsCache(
		string? liveIdentity)
	{
		const string BoundIdentity = "old@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: BoundIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = liveIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(BoundIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(BoundIdentity, account.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(snapshotStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenProviderIdentityIsRejected_InvalidatesOnlyForForcedRefresh()
	{
		const string BoundIdentity = "old@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: BoundIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = "new@example.com"
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(profile.Id);

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			account.RecoveryAction);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			new[] { profile.Id },
			refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenIdentityPersistenceKeepsFailing_BacksOffAcrossTicks()
	{
		DateTimeOffset now = new(
			2026,
			8,
			16,
			8,
			0,
			0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = "live@example.com"
				})
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: new FakeUsageSnapshotStore(),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageInBackgroundAsync();
		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Single(profileStore.SavedSnapshots);
		Assert.True(Assert.Single(viewModel.Accounts)
			.DidRejectProviderAccountSnapshot);

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Equal(2, profileStore.SavedSnapshots.Count);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenBoundIdentityDiffersOnlyByCase_AcceptsSnapshot()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "Person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = "person@example.com"
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Single(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenTwoUnboundCardsResolveToSameProviderIdentity_BindsOnlyFirst()
	{
		const string SharedIdentity = "shared@example.com";
		AccountProfile first = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 1",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile second = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 2",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(first, second);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = SharedIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == first.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == second.Id);
		IReadOnlyList<AccountProfile> savedProfiles = Assert.Single(
			profileStore.SavedSnapshots);
		Assert.Equal(
			SharedIdentity,
			savedProfiles.Single(account => account.Id == first.Id)
				.ProviderAccountIdentity);
		Assert.Null(savedProfiles.Single(account => account.Id == second.Id)
			.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Ready, firstAccount.StatusKind);
		Assert.Equal(AccountStatusKind.Error, secondAccount.StatusKind);
		Assert.Null(secondAccount.ProviderAccountIdentity);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			secondAccount.CurrentSnapshot?.RecoveryAction);
		Assert.Equal(first.Id, Assert.Single(snapshotStore.SavedSnapshots).Account.Id);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenBoundCardReturnsMismatch_DoesNotBlockUnboundCardClaim()
	{
		const string OldIdentity = "old@example.com";
		const string NewIdentity = "new@example.com";
		AccountProfile bound = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Bound",
			ProviderAccountIdentity: OldIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile unbound = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Unbound",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(bound, unbound);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = NewIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel boundAccount = viewModel.Accounts.Single(
			account => account.Id == bound.Id);
		AccountUsageViewModel unboundAccount = viewModel.Accounts.Single(
			account => account.Id == unbound.Id);
		Assert.Equal(AccountStatusKind.Error, boundAccount.StatusKind);
		Assert.Equal(OldIdentity, boundAccount.ProviderAccountIdentity);
		Assert.True(boundAccount.DidRejectProviderAccountSnapshot);
		Assert.Equal(AccountStatusKind.Ready, unboundAccount.StatusKind);
		Assert.Equal(NewIdentity, unboundAccount.ProviderAccountIdentity);
		Assert.True(unboundAccount.HasConfirmedProviderAccountBinding);
		Assert.Equal(
			NewIdentity,
			Assert.Single(profileStore.SavedSnapshots)
				.Single(account => account.Id == unbound.Id)
				.ProviderAccountIdentity);
		Assert.Equal(
			unbound.Id,
			Assert.Single(snapshotStore.SavedSnapshots).Account.Id);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenTargetLifecycleChanges_DoesNotBlockActiveCardClaim()
	{
		const string SharedIdentity = "shared@example.com";
		AccountProfile staleTarget = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Stale target",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile activeTarget = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Active target",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource staleRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> staleRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore profileStore = new(staleTarget, activeTarget);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (account.Id == staleTarget.Id)
				{
					staleRefreshStarted.TrySetResult();
					return staleRefreshResult.Task;
				}

				return Task.FromResult(
					CreateReadySnapshot(account) with
					{
						ProviderAccountIdentity = SharedIdentity
					});
			}
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await staleRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await viewModel.UpdateAccountAsync(staleTarget with { IsEnabled = false });
		staleRefreshResult.TrySetResult(
			CreateReadySnapshot(staleTarget) with
			{
				ProviderAccountIdentity = SharedIdentity
			});
		await refreshTask;

		AccountUsageViewModel staleAccount = viewModel.Accounts.Single(
			account => account.Id == staleTarget.Id);
		AccountUsageViewModel activeAccount = viewModel.Accounts.Single(
			account => account.Id == activeTarget.Id);
		Assert.Equal(AccountStatusKind.Disabled, staleAccount.StatusKind);
		Assert.Null(staleAccount.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Ready, activeAccount.StatusKind);
		Assert.Equal(SharedIdentity, activeAccount.ProviderAccountIdentity);
		Assert.Equal(
			SharedIdentity,
			profileStore.SavedSnapshots[^1]
				.Single(account => account.Id == activeTarget.Id)
				.ProviderAccountIdentity);
		Assert.Equal(
			activeTarget.Id,
			Assert.Single(snapshotStore.SavedSnapshots).Account.Id);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenDifferentProvidersShareIdentity_BindsBoth()
	{
		const string SharedIdentity = "shared@example.com";
		AccountProfile claude = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile codex = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(claude, codex);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				CreateReadySnapshot(account) with
				{
					ProviderAccountIdentity = SharedIdentity
				})
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		IReadOnlyList<AccountProfile> savedProfiles = Assert.Single(
			profileStore.SavedSnapshots);
		Assert.All(
			savedProfiles,
			account => Assert.Equal(
				SharedIdentity,
				account.ProviderAccountIdentity));
		Assert.All(
			viewModel.Accounts,
			account => Assert.Equal(AccountStatusKind.Ready, account.StatusKind));
		Assert.Equal(2, snapshotStore.SavedSnapshots.Count);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenUsableSnapshotIsStale_PersistsCache()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = requestedAccount => Task.FromResult(new UsageSnapshot(
				requestedAccount,
				new[]
				{
					new UsageMetric("five_hour", "5 小時用量", 45, "已使用 45%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Stale,
				DateTimeOffset.UtcNow,
				Error: "Provider 回傳 last-known 用量。",
				ProviderAccountIdentity: "person@example.com"))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		UsageSnapshot persistedSnapshot = Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Equal(SnapshotStatus.Stale, persistedSnapshot.Status);
		Assert.Equal("已使用 45%", Assert.Single(persistedSnapshot.Metrics).DisplayValue);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenClaudeUsageUnavailableHasMetrics_PreservesVerifiedDurableCache()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"team",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude Team",
			ProviderAccountIdentity: publicBindingIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		UsageSnapshot verifiedCache = new(
			profile,
			new[]
			{
				new UsageMetric("historical", "歷史用量", 45, "已使用 45%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
			Error: "已確認的歷史用量。",
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: context.AccountIdentity,
			SubscriptionScopeDisplayName: context.SubscriptionScopeDisplayName,
			PlanTier: context.PlanTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = requestedAccount => Task.FromResult(
				verifiedCache with
				{
					Account = requestedAccount,
					Error = "Team plan usage 尚未驗證。",
					SubscriptionVerificationState =
						SubscriptionVerificationState.UsageUnavailable
				})
		};
		FakeUsageSnapshotStore snapshotStore = new(verifiedCache);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("已使用 45%", Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Equal(
			SubscriptionVerificationState.UsageUnavailable,
			account.CurrentSnapshot?.SubscriptionVerificationState);
		Assert.Empty(snapshotStore.SavedSnapshots);
		Assert.Same(verifiedCache, await snapshotStore.LoadAsync(profile));
	}

	[Fact]
	public void ReportRefreshFailure_UsesPersistentCompactStatus()
	{
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator());

		viewModel.ReportRefreshFailure();

		Assert.Equal("檢查失敗，稍後會自動再試", viewModel.LastRefreshText);
		Assert.Equal(
			"檢查失敗，稍後自動再試",
			viewModel.CompactRefreshStatus.Text);
		Assert.False(viewModel.CompactRefreshStatus.IsTransient);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenCoordinatorFaults_ProjectsSafeErrorWithoutThrowing()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Pro",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => Task.FromException<UsageSnapshot>(
				new InvalidOperationException("unexpected"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.False(viewModel.IsRefreshing);
		Assert.Equal("檢查完成：失敗 1", viewModel.LastRefreshText);
		Assert.Equal(
			"1 個帳號稍後自動再試",
			viewModel.CompactRefreshStatus.Text);
		Assert.False(viewModel.CompactRefreshStatus.IsTransient);
		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		Assert.Equal("讀取失敗", accountViewModel.StatusText);
		Assert.Equal("用量檢查失敗，稍後會自動再試。", accountViewModel.SecondaryText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenSomeAccountsFail_ReportsPartialUpdate()
	{
		AccountProfile failingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"失敗帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile readyAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"成功帳號");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				account.Id == failingAccount.Id
					? new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						DateTimeOffset.UtcNow,
						Error: "測試用刷新失敗。")
					: CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(failingAccount, readyAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.StartsWith("檢查完成：可用 1、失敗 1", viewModel.LastRefreshText);
		Assert.Equal(
			AccountStatusKind.Error,
			viewModel.Accounts.Single(
				account => account.Id == failingAccount.Id).StatusKind);
		Assert.Equal(
			AccountStatusKind.Ready,
			viewModel.Accounts.Single(
				account => account.Id == readyAccount.Id).StatusKind);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenFailingTargetBecomesInapplicable_ReportsSuccessfulUpdate()
	{
		AccountProfile failingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"停用中的帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile readyAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"成功帳號");
		TaskCompletionSource<bool> failingRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> failingRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore store = new(failingAccount, readyAccount);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (account.Id != failingAccount.Id)
				{
					return Task.FromResult(CreateReadySnapshot(account));
				}

				failingRefreshStarted.TrySetResult(true);
				return failingRefreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(store, refreshCoordinator);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await failingRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await viewModel.UpdateAccountAsync(failingAccount with { IsEnabled = false });
		failingRefreshResult.SetResult(new UsageSnapshot(
			failingAccount,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "測試用刷新失敗。"));
		await refreshTask;

		Assert.StartsWith("檢查完成：可用 1", viewModel.LastRefreshText);
		Assert.Equal(
			AccountStatusKind.Disabled,
			viewModel.Accounts.Single(
				account => account.Id == failingAccount.Id).StatusKind);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAllAccountsSucceed_ReportsUpdatedTime()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"第一個帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"第二個帳號");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.StartsWith("檢查完成：可用 2", viewModel.LastRefreshText);
		Assert.StartsWith("已更新 ", viewModel.CompactRefreshStatus.Text);
		Assert.True(viewModel.CompactRefreshStatus.IsTransient);
	}

	[Fact]
	public async Task RefreshUsageAsync_WithMixedStatuses_ReportsCountsAndActualDataTime()
	{
		AccountProfile readyAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"可用帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile staleAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"過期帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile failingAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"失敗帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile notConfiguredAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"待設定帳號",
			HasAcceptedClaudeQuotaRisk: true);
		DateTimeOffset dataAt = new(
			2026,
			7,
			15,
			6,
			30,
			0,
			TimeSpan.Zero);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (account.Id == readyAccount.Id)
				{
					return Task.FromResult(CreateReadySnapshot(account) with
					{
						FetchedAt = dataAt + TimeSpan.FromMinutes(10),
						ObservedAt = dataAt
					});
				}

				if (account.Id == staleAccount.Id)
				{
					return Task.FromResult(CreateReadySnapshot(account) with
					{
						Status = SnapshotStatus.Stale,
						FetchedAt = dataAt - TimeSpan.FromMinutes(5),
						Error = "測試用過期資料。"
					});
				}

				if (account.Id == failingAccount.Id)
				{
					return Task.FromResult(new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						dataAt + TimeSpan.FromHours(1),
						Error: "測試用刷新失敗。"));
				}

				return Task.FromResult(new UsageSnapshot(
					account,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					dataAt + TimeSpan.FromHours(2),
					Error: "測試用未設定。"));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(
				readyAccount,
				staleAccount,
				failingAccount,
				notConfiguredAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		DateTimeOffset oldestDataAt = dataAt - TimeSpan.FromMinutes(5);
		Assert.Equal(
			$"檢查完成：可用 1、舊資料 1、失敗 1、待設定 1；最舊資料 " +
				$"{oldestDataAt.ToLocalTime():MM/dd HH:mm:ss}",
			viewModel.LastRefreshText);
		Assert.Equal("1 個帳號需處理", viewModel.CompactRefreshStatus.Text);
		Assert.False(viewModel.CompactRefreshStatus.IsTransient);
	}

	[Fact]
	public async Task RefreshUsageAsync_WithNoEnabledAccounts_ReportsNoTargets()
	{
		AccountProfile disabledAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"停用帳號",
			IsEnabled: false);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(disabledAccount),
			refreshCoordinator);

		await viewModel.InitializeAsync();

		Assert.False(viewModel.CanRefresh);
		Assert.Equal("沒有可檢查用量的帳號", viewModel.LastRefreshText);
		Assert.Equal(
			CompactRefreshStatus.Hidden,
			viewModel.CompactRefreshStatus);

		await viewModel.RefreshUsageAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Equal("沒有可檢查用量的帳號", viewModel.LastRefreshText);
	}

	[Fact]
	public async Task CanRefresh_WhenOnlyAccountIsChanging_NotifiesAndDisablesAction()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.Accounts[0].BeginProviderAccountChange();

		Assert.False(viewModel.CanRefresh);
		Assert.Contains(nameof(DashboardViewModel.CanRefresh), changedProperties);

		viewModel.Accounts[0].AbortProviderAccountChange();

		Assert.True(viewModel.CanRefresh);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhileActive_ExposesBindableProgressState()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = requestedAccount =>
			{
				refreshStarted.TrySetResult(true);
				return refreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Assert.True(viewModel.CanRefresh);
		Assert.Equal("檢查用量", viewModel.RefreshActionText);

		Task refreshTask = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(viewModel.IsRefreshing);
		Assert.False(viewModel.CanRefresh);
		Assert.Equal("正在檢查…", viewModel.RefreshActionText);

		refreshResult.SetResult(CreateReadySnapshot(account));
		await refreshTask;

		Assert.False(viewModel.IsRefreshing);
		Assert.True(viewModel.CanRefresh);
		Assert.Equal("檢查用量", viewModel.RefreshActionText);
	}

	[Fact]
	public async Task RefreshUsageInBackgroundAsync_WhenCoolingDown_ReportsCachedCheck()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = _ => TimeSpan.FromSeconds(30),
			RefreshHandler = requestedAccount =>
				Task.FromResult(CreateReadySnapshot(requestedAccount))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.StartsWith(
			"檢查完成：可用 1、稍後可更新 1",
			viewModel.LastRefreshText);
		Assert.Equal(
			"1 個帳號稍後更新",
			viewModel.CompactRefreshStatus.Text);
		Assert.False(viewModel.CompactRefreshStatus.IsTransient);
	}

	[Fact]
	public async Task TryBeginProviderAccountRecoveryAsync_WithAttachedGrok_ReservesWithoutManagingAccounts()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);

		bool reserved =
			await viewModel.TryBeginProviderAccountRecoveryAsync(account);

		Assert.True(reserved);
		Assert.True(account.IsProviderAccountChangeInProgress);
		Assert.False(viewModel.IsManagingAccounts);
		account.AbortProviderAccountChange();
	}

	[Fact]
	public async Task TryBeginProviderAccountRecoveryAsync_WithNonGrokAccount_RejectsReservation()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);

		bool reserved =
			await viewModel.TryBeginProviderAccountRecoveryAsync(account);

		Assert.False(reserved);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(viewModel.IsManagingAccounts);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WithinProviderCooldownWithoutSnapshot_ReportsNoData()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = _ => TimeSpan.FromSeconds(30)
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.EndsWith(
			"目前尚無可顯示的用量資料。",
			viewModel.LastRefreshText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WithinProviderCooldown_RemovesExpiredMetricsWithoutProviderRequest()
	{
		DateTimeOffset now = new(2026, 8, 18, 5, 0, 0, TimeSpan.Zero);
		ManualRefreshTimeProvider timeProvider = new(now);
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = _ => TimeSpan.FromSeconds(30)
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		accountViewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric(
					"weekly",
					"Weekly",
					37,
					"old period",
					ResetsAt: now)
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			now - TimeSpan.FromMinutes(5),
			ProviderAccountIdentity: account.ProviderAccountIdentity));

		await viewModel.RefreshAccountUsageAsync(account.Id);

		UsageSnapshot snapshot = Assert.IsType<UsageSnapshot>(
			accountViewModel.CurrentSnapshot);
		Assert.Empty(snapshot.Metrics);
		Assert.Equal(SnapshotStatus.Ready, snapshot.Status);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.EndsWith(
			"目前尚無可顯示的用量資料。",
			viewModel.LastRefreshText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WithinProviderCooldown_ReportsWaitWithoutProviderRequest()
	{
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"目標帳號",
			ProviderAccountIdentity: "target@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"其他帳號",
			ProviderAccountIdentity: "other@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		StatefulUsageProvider provider = new(
			ProviderKind.Claude,
			(account, callCount) => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"five_hour",
						"5 小時用量",
						callCount,
						$"已使用 {callCount}%")
				},
				SourceTrust.Official,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					account.ProviderAccountIdentity)));
		UsageRefreshCoordinator refreshCoordinator = new(
			new UsageProviderRegistry(new[] { provider }));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount, otherAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel targetViewModel = viewModel.Accounts.Single(
			account => account.Id == targetAccount.Id);
		AccountUsageViewModel otherViewModel = viewModel.Accounts.Single(
			account => account.Id == otherAccount.Id);
		UsageSnapshot? targetSnapshotBeforeRefresh = targetViewModel.CurrentSnapshot;
		UsageSnapshot? otherSnapshotBeforeRefresh = otherViewModel.CurrentSnapshot;
		int providerCallCountBeforeRefresh = provider.CallCount;
		provider.InvalidatedAccountIds.Clear();

		await viewModel.RefreshAccountUsageAsync(targetAccount.Id);

		Assert.Equal(providerCallCountBeforeRefresh, provider.CallCount);
		Assert.Empty(provider.InvalidatedAccountIds);
		Assert.Same(targetSnapshotBeforeRefresh, targetViewModel.CurrentSnapshot);
		Assert.Same(otherSnapshotBeforeRefresh, otherViewModel.CurrentSnapshot);
		Assert.StartsWith(
			"更新過於頻繁，請在 ",
			viewModel.LastRefreshText);
		Assert.EndsWith(
			"後再試。目前仍顯示上次資料。",
			viewModel.LastRefreshText);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WithinAutomaticRetryCooldown_ReportsAutomaticRetry()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = _ => TimeSpan.FromMinutes(1)
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		accountViewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "暫時無法取得用量，AI Usage 稍後會自動再試。",
			ProviderAccountIdentity: account.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));

		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			"AI Usage 已排定自動重試，將在 1 分鐘後再次檢查。目前尚無可顯示的用量資料。",
			viewModel.LastRefreshText);
		Assert.DoesNotContain("請", viewModel.LastRefreshText, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"更新過於頻繁",
			viewModel.LastRefreshText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_AfterCooldown_ForcesProviderRetry()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = _ => null,
			RefreshHandler = requestedAccount =>
				Task.FromResult(CreateReadySnapshot(requestedAccount))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.Equal(new[] { account.Id }, refreshCoordinator.InvalidatedAccountIds);
		Assert.Equal(account.Id, Assert.Single(
			refreshCoordinator.RefreshRequests).Id);
		Assert.StartsWith("檢查完成：可用 1", viewModel.LastRefreshText);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenProviderChangeStartsBeforeReservation_SkipsProviderRequest()
	{
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"目標帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"其他帳號",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> otherRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> otherRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> targetCooldownCheckStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> continueTargetCooldownCheck = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldownHandler = account =>
			{
				if (account.Id == targetAccount.Id)
				{
					targetCooldownCheckStarted.TrySetResult(true);
					if (!continueTargetCooldownCheck.Task.Wait(
							TimeSpan.FromSeconds(5)))
					{
						throw new TimeoutException(
							"Timed out waiting to resume the target cooldown check.");
					}
				}

				return null;
			},
			RefreshHandler = account =>
			{
				if (account.Id == otherAccount.Id)
				{
					otherRefreshStarted.TrySetResult(true);
					return otherRefreshResult.Task;
				}

				return Task.FromResult(CreateReadySnapshot(account));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount, otherAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task otherRefresh = viewModel.RefreshAccountUsageAsync(otherAccount.Id);
		await otherRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task targetRefresh = viewModel.RefreshAccountUsageAsync(targetAccount.Id);
		otherRefreshResult.SetResult(CreateReadySnapshot(otherAccount));
		await targetCooldownCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		AccountUsageViewModel targetViewModel = viewModel.Accounts.Single(
			account => account.Id == targetAccount.Id);
		Assert.True(viewModel.TryBeginProviderAccountChange(targetViewModel));
		await viewModel.QuiesceAccountRefreshAsync(targetAccount.Id)
			.WaitAsync(TimeSpan.FromSeconds(5));
		continueTargetCooldownCheck.SetResult(true);

		await Task.WhenAll(otherRefresh, targetRefresh);

		Assert.Equal(
			new[] { otherAccount.Id },
			refreshCoordinator.RefreshRequests.Select(account => account.Id));
		Assert.Equal(
			new[] { otherAccount.Id, targetAccount.Id },
			refreshCoordinator.InvalidatedAccountIds);
		Assert.True(targetViewModel.IsProviderAccountChangeInProgress);
		targetViewModel.AbortProviderAccountChange();
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenRefreshAllFollows_QueuesFullRefresh()
	{
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"目標帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"其他帳號");
		TaskCompletionSource<bool> targetRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> targetRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int targetRequestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if ((account.Id == targetAccount.Id) &&
					(Interlocked.Increment(ref targetRequestCount) == 1))
				{
					targetRefreshStarted.TrySetResult(true);
					return targetRefreshResult.Task;
				}

				return Task.FromResult(CreateReadySnapshot(account));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount, otherAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task targetRefresh = viewModel.RefreshAccountUsageAsync(targetAccount.Id);
		await targetRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task refreshAll = viewModel.RefreshUsageAsync();

		Assert.False(refreshAll.IsCompleted);
		Assert.Equal(targetAccount.Id, Assert.Single(
			refreshCoordinator.RefreshRequests).Id);
		targetRefreshResult.SetResult(CreateReadySnapshot(targetAccount));

		await Task.WhenAll(targetRefresh, refreshAll);

		Assert.Equal(3, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(
			2,
			refreshCoordinator.RefreshRequests.Count(
				account => account.Id == targetAccount.Id));
		Assert.Single(
			refreshCoordinator.RefreshRequests,
			account => account.Id == otherAccount.Id);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenTargetSeparatesFullRefreshes_QueuesInCallOrder()
	{
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"目標帳號",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"其他帳號");
		TaskCompletionSource<bool> fullRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> allowFullRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = async account =>
			{
				int currentRequest = Interlocked.Increment(ref requestCount);

				if (currentRequest <= 2)
				{
					if (currentRequest == 2)
					{
						fullRefreshStarted.TrySetResult(true);
					}

					await allowFullRefreshToFinish.Task;
				}

				return CreateReadySnapshot(account);
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount, otherAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		List<AccountRefreshProgress> refreshProgress = new();

		Task refreshAll = viewModel.RefreshUsageAsync();
		await fullRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task targetRefresh = viewModel.RefreshAccountUsageAsync(
			targetAccount.Id,
			refreshProgress.Add);
		Task secondRefreshAll = viewModel.RefreshUsageAsync();

		Assert.False(targetRefresh.IsCompleted);
		Assert.False(secondRefreshAll.IsCompleted);
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Equal(
			[AccountRefreshProgress.Queued],
			refreshProgress);
		allowFullRefreshToFinish.SetResult(true);

		await Task.WhenAll(refreshAll, targetRefresh, secondRefreshAll);

		Assert.Equal(targetAccount.Id, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
		Assert.Equal(5, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(
			3,
			refreshCoordinator.RefreshRequests.Count(
				account => account.Id == targetAccount.Id));
		Assert.Equal(
			2,
			refreshCoordinator.RefreshRequests.Count(
				account => account.Id == otherAccount.Id));
		Assert.Equal(
			[
				AccountRefreshProgress.Queued,
				AccountRefreshProgress.Started
			],
			refreshProgress);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenSameTargetRefreshIsActive_CoalescesRequest()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ =>
			{
				refreshStarted.TrySetResult(true);
				return refreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task firstRefresh = viewModel.RefreshAccountUsageAsync(account.Id);
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		List<AccountRefreshProgress> coalescedProgress = new();
		Task secondRefresh = viewModel.RefreshAccountUsageAsync(
			account.Id,
			coalescedProgress.Add);

		Assert.False(secondRefresh.IsCompleted);
		Assert.Equal(
			[AccountRefreshProgress.Started],
			coalescedProgress);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(account.Id, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
		refreshResult.SetResult(CreateReadySnapshot(account));

		await Task.WhenAll(firstRefresh, secondRefresh);

		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Single(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenProgressReporterThrows_StillRefreshesAndDrains()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(
			account.Id,
			_ => throw new InvalidOperationException("UI feedback failed."));
		await viewModel.QuiesceAccountRefreshAsync(account.Id)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.False(viewModel.IsRefreshing);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenRefreshReturnsErrorWithStaleFallback_ReportsStaleSummary()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = requestedAccount =>
				Interlocked.Increment(ref requestCount) == 1
					? Task.FromResult(CreateReadySnapshot(requestedAccount, 37))
					: Task.FromException<UsageSnapshot>(
						new InvalidOperationException("測試用 Provider 失敗。"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.StartsWith("檢查完成：舊資料 1", viewModel.LastRefreshText);
		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		Assert.Equal(AccountStatusKind.Stale, accountViewModel.StatusKind);
		Assert.Equal(37, accountViewModel.UsedPercent);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenRefreshReturnsPureError_ReportsFailure()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => Task.FromException<UsageSnapshot>(
				new InvalidOperationException("測試用 Provider 失敗。"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshAccountUsageAsync(account.Id);

		Assert.Equal("檢查完成：失敗 1", viewModel.LastRefreshText);
		Assert.Equal(
			AccountStatusKind.Error,
			Assert.Single(viewModel.Accounts).StatusKind);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenLifecycleChanges_DoesNotCoalesceWithOldRequest()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> firstRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> firstRefreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref requestCount) == 1)
				{
					firstRefreshStarted.TrySetResult(true);
					return firstRefreshResult.Task;
				}

				return Task.FromResult(CreateReadySnapshot(account, 20));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task firstRefresh = viewModel.RefreshAccountUsageAsync(profile.Id);
		await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplyProfile(profile with { IsEnabled = false }, canManage: true);
		account.ApplyProfile(profile with { IsEnabled = true }, canManage: true);
		Task secondRefresh = viewModel.RefreshAccountUsageAsync(profile.Id);

		Assert.False(secondRefresh.IsCompleted);
		Assert.Single(refreshCoordinator.InvalidatedAccountIds);
		firstRefreshResult.SetResult(CreateReadySnapshot(profile));

		await Task.WhenAll(firstRefresh, secondRefresh);

		Assert.Equal(2, refreshCoordinator.InvalidatedAccountIds.Count);
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(20, account.UsedPercent);
		Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenProviderFails_PreservesStaleUsageAndDiskCache()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		StatefulUsageProvider provider = new(
			ProviderKind.Claude,
			(requestedAccount, callCount) => callCount == 1
				? Task.FromResult(new UsageSnapshot(
					requestedAccount,
					new[]
					{
						new UsageMetric(
							"five_hour",
							"5 小時用量",
							37,
							"已使用 37%")
					},
					SourceTrust.Official,
					SnapshotStatus.Ready,
					DateTimeOffset.UtcNow,
					ProviderAccountIdentity:
						requestedAccount.ProviderAccountIdentity))
				: Task.FromException<UsageSnapshot>(
					new InvalidOperationException("測試用 Provider 失敗。")),
			TimeSpan.Zero);
		UsageRefreshCoordinator refreshCoordinator = new(
			new UsageProviderRegistry(new[] { provider }));
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.RefreshAccountUsageAsync(account.Id);

		AccountUsageViewModel accountViewModel = Assert.Single(viewModel.Accounts);
		Assert.Equal(2, provider.CallCount);
		Assert.Equal(AccountStatusKind.Stale, accountViewModel.StatusKind);
		Assert.Equal(37, accountViewModel.UsedPercent);
		Assert.Equal("已使用 37%", accountViewModel.PrimaryText);
		Assert.Empty(snapshotStore.DeletedSnapshots);
		UsageSnapshot? cachedSnapshot = await snapshotStore.LoadAsync(account);
		Assert.NotNull(cachedSnapshot);
		Assert.Equal(SnapshotStatus.Stale, cachedSnapshot.Status);
		Assert.Equal("已使用 37%", Assert.Single(cachedSnapshot.Metrics).DisplayValue);
	}

	[Fact]
	public void AccountUsageViewModel_ApplySnapshot_ProjectsEveryUsageMetric()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"工作帳號",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		DateTimeOffset resetsAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(3);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("five_hour", "5 小時", 25, "已使用 25%", resetsAt),
				new UsageMetric("seven_day", "本週（所有模型）", 60, "已使用 60%", resetsAt),
				new UsageMetric("fable", "本週（Fable）", 75, "已使用 75%", resetsAt)
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com"));

		Assert.Equal(SnapshotStatus.Ready, viewModel.CurrentSnapshot?.Status);
		Assert.False(viewModel.DidRejectProviderAccountSnapshot);
		Assert.True(viewModel.CanPersistCurrentSnapshot);
		Assert.True(viewModel.HasConfirmedProviderAccountBinding);
		Assert.Equal("person@example.com", viewModel.AccountDisplayText);
		Assert.Equal("暱稱 · 工作帳號", viewModel.AccountAliasText);
		Assert.True(viewModel.HasProviderAccountIdentity);
		Assert.True(viewModel.HasAccountAlias);
		Assert.Equal("切換 Claude 帳號", viewModel.ClaudeAccountActionText);
		Assert.True(viewModel.HasUsageMetrics);
		Assert.False(viewModel.HasNoUsageMetrics);
		Assert.Equal(
			new[] { "5小時用量", "週用量 · 所有模型", "週用量 · Fable" },
			viewModel.UsageMetrics.Select(metric => metric.Label));
		Assert.All(viewModel.UsageMetrics, metric => Assert.True(metric.HasUsageBar));

		viewModel.BeginProviderAccountChange();
		Assert.Equal("連接中", viewModel.AccountDisplayText);
		Assert.Null(viewModel.ProviderAccountIdentity);
		viewModel.SeedProviderAccountIdentity("person@example.com");
		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "person@example.com"));
		Assert.Equal("連接中", viewModel.AccountDisplayText);
		viewModel.EndProviderAccountChange();
		Assert.Equal("person@example.com", viewModel.AccountDisplayText);
	}

	[Fact]
	public void AccountUsageViewModel_SeededIdentity_SurvivesTransientSnapshotsUntilNotConfigured()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"工作帳號");
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.SeedProviderAccountIdentity("claude@example.com");

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "暫時失敗"));
		Assert.Equal("claude@example.com", viewModel.ProviderAccountIdentity);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("five_hour", "5 小時", 25, "已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			Error: "上次確認",
			ProviderAccountIdentity: "claude@example.com"));
		Assert.Equal("claude@example.com", viewModel.ProviderAccountIdentity);
		Assert.Contains("上次確認", viewModel.AccountDisplayText);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow));
		Assert.Null(viewModel.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountUsageViewModel_ReadySnapshotWithoutIdentity_RejectsAndPreservesBinding()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號",
			ProviderAccountIdentity: "old@example.com");
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 25, "已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow));

		Assert.Equal("old@example.com", viewModel.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, viewModel.StatusKind);
		Assert.Empty(viewModel.UsageMetrics);
		Assert.True(viewModel.DidRejectProviderAccountSnapshot);
		Assert.Contains(
			"無法確認登入帳號",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("old@example.com", "old@example.com")]
	public void AccountUsageViewModel_ErrorSnapshotIdentity_DoesNotCreateOrReplaceBinding(
		string? boundIdentity,
		string? expectedIdentity)
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號",
			ProviderAccountIdentity: boundIdentity);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "測試用錯誤。",
			ProviderAccountIdentity: "untrusted@example.com"));

		Assert.Equal(expectedIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(boundIdentity, viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, viewModel.StatusKind);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("old@example.com")]
	public void AccountUsageViewModel_NotConfiguredSnapshot_ReturnsToPersistedBinding(
		string? boundIdentity)
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: boundIdentity);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "untrusted@example.com",
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		Assert.Equal(boundIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(boundIdentity, viewModel.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountUsageViewModel_AbortAfterConcurrentDurableBind_PreservesLatestProfileIdentity()
	{
		const string DurableIdentity = "durable@example.com";
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.BeginProviderAccountChange();

		viewModel.ApplyProfile(
			account with
			{
				ProviderAccountIdentity = DurableIdentity
			},
			canManage: true);
		viewModel.AbortProviderAccountChange();

		Assert.Equal(DurableIdentity, viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(DurableIdentity, viewModel.ProviderAccountIdentity);
		Assert.False(viewModel.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public void AccountUsageViewModel_ProviderChange_UsesIdentityFromForcedRefreshOnly()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號");
		AccountUsageViewModel viewModel = new(account, canManage: true);
		viewModel.SeedProviderAccountIdentity("old@example.com");
		viewModel.BeginProviderAccountChange();

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 25, "已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "old@example.com"));

		Assert.Null(viewModel.ProviderAccountIdentity);
		Assert.Equal("連接中", viewModel.AccountDisplayText);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 30, "已使用 30%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "new@example.com"));

		Assert.Null(viewModel.ProviderAccountIdentity);
		viewModel.CompleteProviderAccountChange();
		Assert.Equal("new@example.com", viewModel.ProviderAccountIdentity);
		Assert.Equal("new@example.com", viewModel.AccountDisplayText);
		Assert.Equal(
			CodexLoginRefreshOutcome.Ready,
			AccountConnectionCoordinator.GetCodexLoginRefreshOutcome(viewModel));
	}

	[Theory]
	[InlineData(SnapshotStatus.Ready, true, nameof(CodexLoginRefreshOutcome.Ready))]
	[InlineData(SnapshotStatus.Ready, false, nameof(CodexLoginRefreshOutcome.Error))]
	[InlineData(SnapshotStatus.Stale, true, nameof(CodexLoginRefreshOutcome.Stale))]
	[InlineData(SnapshotStatus.Stale, false, nameof(CodexLoginRefreshOutcome.Error))]
	[InlineData(SnapshotStatus.Error, false, nameof(CodexLoginRefreshOutcome.Error))]
	[InlineData(SnapshotStatus.NotConfigured, false, nameof(CodexLoginRefreshOutcome.NotConfigured))]
	public void AccountConnectionCoordinator_GetCodexLoginRefreshOutcome_RequiresConfirmedIdentityForSuccess(
		SnapshotStatus status,
		bool hasIdentity,
		string expectedOutcome)
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號",
			ProviderAccountIdentity:
				hasIdentity ? "codex@example.com" : null);
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			(status == SnapshotStatus.Ready) || (status == SnapshotStatus.Stale)
				? new[]
				{
					new UsageMetric("codex", "5 小時用量", 25, "已使用 25%")
				}
				: Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			status,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: hasIdentity ? "codex@example.com" : null));

		Assert.Equal(
			expectedOutcome,
			AccountConnectionCoordinator
				.GetCodexLoginRefreshOutcome(viewModel)
				.ToString());
	}

	[Fact]
	public void AccountUsageViewModel_ApplyStaleSnapshot_PreservesFailureNotice()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"主要帳號",
			ProviderAccountIdentity: "codex@example.com");
		AccountUsageViewModel viewModel = new(account, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 25, "已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			Error: "目前顯示上次成功取得的用量。",
			ProviderAccountIdentity: "codex@example.com"));

		Assert.True(viewModel.HasUsageMetrics);
		Assert.True(viewModel.HasNotice);
		Assert.Equal(
			"codex@example.com（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Contains("上次成功讀取的資料", viewModel.NoticeText, StringComparison.Ordinal);
		Assert.Contains(
			"時間不明",
			viewModel.NoticeText,
			StringComparison.Ordinal);
		Assert.Contains(
			"目前顯示上次成功取得的用量。",
			viewModel.NoticeText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenRefreshIsActive_StartsFreshGeneration()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		TaskCompletionSource<UsageSnapshot> firstResultSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = requestedAccount =>
			{
				if (Interlocked.Increment(ref requestCount) == 1)
				{
					return firstResultSource.Task;
				}

				return Task.FromResult(new UsageSnapshot(
					requestedAccount,
					new[]
					{
						new UsageMetric("codex", "Codex", 25, "已使用 25%")
					},
					SourceTrust.OfficialExperimental,
					SnapshotStatus.Ready,
					DateTimeOffset.UtcNow,
					ProviderAccountIdentity:
						$"{requestedAccount.Id:N}@example.invalid"));
			}
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task activeRefresh = viewModel.RefreshUsageAsync();
		Task forcedRefresh = viewModel.RefreshUsageAfterInvalidationAsync(account.Id);
		Assert.False(forcedRefresh.IsCompleted);
		firstResultSource.SetResult(new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow));

		await Task.WhenAll(activeRefresh, forcedRefresh);

		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(account.Id, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
		Assert.Equal(2, snapshotStore.DeletedSnapshots.Count);
		Assert.All(
			snapshotStore.DeletedSnapshots,
			deletedSnapshot => Assert.Equal(
				(account.Id, account.Provider),
				deletedSnapshot));
		Assert.Equal("可用", Assert.Single(viewModel.Accounts).StatusText);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenCoordinatorAlreadyReportsProviderFailure_DoesNotReportDuplicateDiagnostic()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		InvalidOperationException providerException = new(
			"synthetic provider failure");
		List<Exception> providerDiagnostics = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ =>
			{
				providerDiagnostics.Add(providerException);
				return Task.FromException<UsageSnapshot>(providerException);
			}
		};
		List<(string Operation, string Summary, Exception? Exception)>
			outerDiagnostics = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			reportDiagnostic: (operation, summary, exception) =>
				outerDiagnostics.Add((operation, summary, exception)));
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.Same(providerException, Assert.Single(providerDiagnostics));
		Assert.Empty(outerDiagnostics);
	}

	[Fact]
	public async Task RevalidateClaudeUsageAsync_WhenRuntimeResetFails_ReportsExceptionDiagnostic()
	{
		const string AccountIdentity = "claude@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: AccountIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			ResetUsageSafetyStateHandler = async (_, _, _) =>
			{
				await Task.Yield();
				throw new IOException("synthetic runtime reset failure");
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		List<(string Operation, string Summary, Exception? Exception)>
			diagnostics = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			reportDiagnostic: (operation, summary, exception) =>
				diagnostics.Add((operation, summary, exception)));
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 10, "已使用 10%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			Error: "Claude 用量格式未通過安全驗證。",
			ProviderAccountIdentity: AccountIdentity,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		await viewModel.RevalidateClaudeUsageAsync(account.Id);

		(string operation, string summary, Exception? reportedException) =
			Assert.Single(diagnostics);
		Assert.Equal("usage-safety-revalidation", operation);
		Assert.Equal("stage=runtime-state-reset;result=failed", summary);
		IOException exception = Assert.IsType<IOException>(reportedException);
		Assert.False(string.IsNullOrWhiteSpace(exception.StackTrace));
		Assert.Empty(refreshCoordinator.RefreshRequests);
	}

	[Fact]
	public async Task RevalidateClaudeUsageAsync_ResetsOnlyTargetAndCoalescesOneProbe()
	{
		const string accountIdentity = "claude@example.com";
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Target",
			ProviderAccountIdentity: accountIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Other",
			ProviderAccountIdentity: "other@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> resetStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> allowReset = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			ResetUsageSafetyStateHandler = async (_, _, _) =>
			{
				resetStarted.TrySetResult(true);
				await allowReset.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("five_hour", "5 小時用量", 20, "已使用 20%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: account.ProviderAccountIdentity))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount, otherAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger);
		await viewModel.InitializeAsync();
		AccountUsageViewModel target = viewModel.Accounts.Single(
			account => account.Id == targetAccount.Id);
		target.ApplySnapshot(new UsageSnapshot(
			targetAccount,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 10, "已使用 10%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
			Error: "Claude 用量格式未通過安全驗證。",
			ProviderAccountIdentity: accountIdentity,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		Task firstRevalidation = viewModel.RevalidateClaudeUsageAsync(target.Id);
		await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(target.IsUsageSafetyRevalidationInProgress);
		Assert.False(target.CanExecuteRecoveryAction);
		Task secondRevalidation = viewModel.RevalidateClaudeUsageAsync(target.Id);
		Assert.False(secondRevalidation.IsCompleted);
		allowReset.TrySetResult(true);

		await Task.WhenAll(firstRevalidation, secondRevalidation);

		await WaitForConditionAsync(
			() => !target.IsUsageSafetyRevalidationInProgress,
			TimeSpan.FromSeconds(5));
		Assert.False(target.IsUsageSafetyRevalidationInProgress);
		Assert.Equal(
			(target.Id, ProviderKind.Claude),
			Assert.Single(runtimeStatePurger.ResetAccounts));
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		Assert.Equal(target.Id, Assert.Single(refreshCoordinator.RefreshRequests).Id);
		Assert.Equal(2, refreshCoordinator.InvalidatedAccountIds.Count);
		Assert.All(
			refreshCoordinator.InvalidatedAccountIds,
			accountId => Assert.Equal(target.Id, accountId));
		Assert.Equal("可用", target.StatusText);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.ConnectAccount)]
	[InlineData(UsageRecoveryAction.SwitchAccount)]
	[InlineData(UsageRecoveryAction.ConfirmSubscription)]
	public async Task RefreshAccountUsageAsync_WhenRefreshRequiresIdentityRecovery_DoesNotRestoreManualFallback(
		UsageRecoveryAction recoveryAction)
	{
		DateTimeOffset now = new(2026, 8, 18, 4, 0, 0, TimeSpan.Zero);
		ManualRefreshTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				Interlocked.Increment(ref requestCount) == 1
					? new UsageSnapshot(
						account,
						new[]
						{
							new UsageMetric(
								"weekly",
								"Weekly",
								37,
								"old period",
								now + TimeSpan.FromDays(1))
						},
						SourceTrust.Official,
						SnapshotStatus.Ready,
						now,
						ProviderAccountIdentity: account.ProviderAccountIdentity)
					: new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						timeProvider.GetUtcNow(),
						Error: "重新連線以確認帳號。",
						RecoveryAction: recoveryAction))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.RefreshAccountUsageAsync(profile.Id);

		UsageSnapshot snapshot = Assert.IsType<UsageSnapshot>(
			Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(recoveryAction, snapshot.RecoveryAction);
		Assert.Empty(snapshot.Metrics);
	}

	[Fact]
	public async Task RefreshAccountUsageAsync_WhenFallbackResetsDuringFailure_DoesNotRestoreExpiredMetric()
	{
		DateTimeOffset now = new(2026, 8, 18, 4, 10, 0, TimeSpan.Zero);
		ManualRefreshTimeProvider timeProvider = new(now);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref requestCount) == 1)
				{
					return Task.FromResult(new UsageSnapshot(
						account,
						new[]
						{
							new UsageMetric(
								"weekly",
								"Weekly",
								37,
								"old period",
								now + TimeSpan.FromMinutes(1))
						},
						SourceTrust.Official,
						SnapshotStatus.Ready,
						now,
						ProviderAccountIdentity: account.ProviderAccountIdentity));
				}

				refreshStarted.TrySetResult(true);
				return refreshResult.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		Task manualRefresh = viewModel.RefreshAccountUsageAsync(profile.Id);
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		refreshResult.SetResult(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			timeProvider.GetUtcNow(),
			Error: "暫時失敗。",
			RecoveryAction: UsageRecoveryAction.Retry));
		await manualRefresh;

		UsageSnapshot snapshot = Assert.IsType<UsageSnapshot>(
			Assert.Single(viewModel.Accounts).CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Empty(snapshot.Metrics);
	}

	[Fact]
	public async Task RevalidateUsageAsync_WhenAgyProbeHasNoFreshAccountCapture_FallsBackToLocalSessionLabel()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = new(2026, 8, 13, 4, 30, 0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			observedAt - TimeSpan.FromSeconds(10));
		FakeAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: LocalSessionIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				if (Interlocked.Increment(ref refreshCount) == 1)
				{
					reportedAccountSource.ReportedAccount =
						new AntigravityReportedAccount(
							"person@example.com",
							observedAt - TimeSpan.FromSeconds(5));
					timeProvider.Advance(TimeSpan.FromSeconds(11));
				}
				else
				{
					timeProvider.Advance(TimeSpan.FromSeconds(5));
				}

				return Task.FromResult(CreateReadyAntigravitySnapshot(
					account,
					timeProvider.GetUtcNow(),
					refreshCount == 1 ? "重試前用量" : "重試後用量"));
			}
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger,
			antigravityReportedAccountSource: reportedAccountSource,
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(account.CurrentSnapshot! with
		{
			Status = SnapshotStatus.Stale,
			Error = "前次 AGY 檢查未完成。",
			RecoveryAction = UsageRecoveryAction.RevalidateUsage
		});
		List<string> displayedIdentities = new();
		account.PropertyChanged += (_, args) =>
		{
			if ((args.PropertyName == nameof(AccountUsageViewModel.AccountName)) ||
				(args.PropertyName == nameof(
					AccountUsageViewModel.AccountDisplayText)))
			{
				displayedIdentities.Add(account.AccountDisplayText);
			}
		};

		await viewModel.RevalidateUsageAsync(account.Id);

		Assert.Equal(
			(account.Id, ProviderKind.Antigravity),
			Assert.Single(runtimeStatePurger.ResetAccounts));
		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
		Assert.DoesNotContain(
			"person@example.com",
			account.AccountName,
			StringComparison.Ordinal);
		Assert.Equal(AccountStatusKind.Ready, account.StatusKind);
		Assert.Equal(UsageRecoveryAction.None, account.RecoveryAction);
		Assert.Equal(
			"重試後用量",
			Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Contains(
			displayedIdentities,
			identity => identity.Contains(
				"目前登入的 Antigravity 帳號",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RevalidateClaudeUsageAsync_InterleavedRefreshAll_CoalescesProbe()
	{
		const string accountIdentity = "claude@example.com";
		AccountProfile targetAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Target",
			ProviderAccountIdentity: accountIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> resetStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> allowReset = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			ResetUsageSafetyStateHandler = async (_, _, _) =>
			{
				resetStarted.TrySetResult(true);
				await allowReset.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Stale,
				DateTimeOffset.UtcNow,
				ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
				Error: "Claude 用量格式未通過安全驗證。",
				ProviderAccountIdentity: account.ProviderAccountIdentity,
				RecoveryAction: UsageRecoveryAction.RevalidateUsage))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger);
		await viewModel.InitializeAsync();
		AccountUsageViewModel target = Assert.Single(viewModel.Accounts);
		target.ApplySnapshot(new UsageSnapshot(
			targetAccount,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
			Error: "Claude 用量格式未通過安全驗證。",
			ProviderAccountIdentity: accountIdentity,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		Task firstRevalidation = viewModel.RevalidateClaudeUsageAsync(target.Id);
		await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task refreshAll = viewModel.RefreshUsageAsync();
		Task secondRevalidation = viewModel.RevalidateClaudeUsageAsync(target.Id);
		allowReset.TrySetResult(true);

		await Task.WhenAll(firstRevalidation, refreshAll, secondRevalidation);

		Assert.Equal(
			(target.Id, ProviderKind.Claude),
			Assert.Single(runtimeStatePurger.ResetAccounts));
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenRefreshCompletesLater_DoesNotRewriteDeletedCache()
	{
		AccountProfile account = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<UsageSnapshot> refreshResultSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => refreshResultSource.Task
		};
		FakeUsageSnapshotStore snapshotStore = new();
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
			{
				Assert.True(refreshResultSource.Task.IsCompleted);
				return Task.CompletedTask;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(account),
			refreshCoordinator,
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger);
		await viewModel.InitializeAsync();
		Task refreshTask = viewModel.RefreshUsageAsync();

		Task removalTask = viewModel.RemoveAccountAsync(account.Id);
		Assert.False(removalTask.IsCompleted);
		Assert.Empty(runtimeStatePurger.PurgedAccounts);
		refreshResultSource.SetResult(new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 20, "已使用 20%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow));
		await Task.WhenAll(refreshTask, removalTask);

		Assert.Empty(viewModel.Accounts);
		Assert.Empty(snapshotStore.SavedSnapshots);
		Assert.Equal(
			(account.Id, account.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		Assert.Equal(
			(account.Id, account.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenAnotherAccountRefreshIsPending_DoesNotWaitForIt()
	{
		AccountProfile accountToRemove = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile otherAccount = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"Antigravity",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		TaskCompletionSource<UsageSnapshot> removedAccountResultSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> otherAccountResultSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => account.Id == accountToRemove.Id
				? removedAccountResultSource.Task
				: otherAccountResultSource.Task
		};
		FakeAccountRuntimeStatePurger runtimeStatePurger = new()
		{
			PurgeHandler = (_, _, _) =>
			{
				Assert.True(removedAccountResultSource.Task.IsCompleted);
				Assert.False(otherAccountResultSource.Task.IsCompleted);
				return Task.CompletedTask;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(accountToRemove, otherAccount),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: runtimeStatePurger);
		await viewModel.InitializeAsync();
		Task refreshTask = viewModel.RefreshUsageAsync();

		Task removalTask = viewModel.RemoveAccountAsync(accountToRemove.Id);
		Assert.False(removalTask.IsCompleted);
		removedAccountResultSource.SetResult(CreateReadySnapshot(accountToRemove));
		await removalTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(refreshTask.IsCompleted);
		Assert.Equal(
			(accountToRemove.Id, accountToRemove.Provider),
			Assert.Single(runtimeStatePurger.PurgedAccounts));
		otherAccountResultSource.SetResult(CreateReadySnapshot(otherAccount));
		await refreshTask;
		Assert.Equal(otherAccount.Id, Assert.Single(viewModel.Accounts).Id);
	}

	[Fact]
	public async Task RemoveAccountAsync_WhenCacheSaveCompletesLater_DeletesLateWrite()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "old@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> allowSaveToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("five_hour", "5 小時用量", 25, "已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					account.ProviderAccountIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SaveHandler = async (_, _) =>
			{
				saveStarted.TrySetResult(true);
				await allowSaveToFinish.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		Task refreshTask = viewModel.RefreshUsageAsync();
		await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await viewModel.RemoveAccountAsync(profile.Id);
		Assert.Single(snapshotStore.DeletedSnapshots);
		allowSaveToFinish.SetResult(true);
		await refreshTask;

		Assert.Equal(2, snapshotStore.DeletedSnapshots.Count);
		Assert.Null(await snapshotStore.LoadAsync(profile));
	}

	[Fact]
	public async Task InitializeAsync_WhenCoordinatorRejectsCachedSeed_DoesNotApplyCache()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 40, "已使用 40%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			ShouldAcceptSeed = false
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot));

		await viewModel.InitializeAsync();

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		Assert.Empty(account.UsageMetrics);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenAliasChangesAfterNotConfigured_DoesNotRestoreCache()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: publicBindingIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 40, "已使用 40%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: context.AccountIdentity,
			SubscriptionScopeDisplayName: context.SubscriptionScopeDisplayName,
			PlanTier: context.PlanTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				DateTimeOffset.UtcNow))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());
		await viewModel.InitializeAsync();
		await viewModel.RefreshUsageAsync();

		await viewModel.UpdateAccountAsync(profile with { DisplayName = "主要 Claude" });

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("尚未設定", account.StatusText);
		Assert.Empty(account.UsageMetrics);
		Assert.Equal(publicBindingIdentity, account.ProviderAccountIdentity);
		Assert.Single(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenCacheLoadCompletesAfterRefresh_DoesNotOverwriteFreshSnapshot()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			IsEnabled: false,
			ProviderAccountIdentity: "person@example.com");
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 80, "舊快取 80%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
			ProviderAccountIdentity: "person@example.com");
		TaskCompletionSource<bool> loadStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot?> cacheResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			LoadHandler = (_, _) =>
			{
				loadStarted.TrySetResult(true);
				return cacheResult.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("codex", "5 小時用量", 15, "最新資料 15%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: "person@example.com"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task enableTask = viewModel.UpdateAccountAsync(profile with { IsEnabled = true });
		await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await viewModel.RefreshUsageAsync();
		cacheResult.SetResult(cachedSnapshot);
		await enableTask;

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal("可用", account.StatusText);
		Assert.Equal("最新資料 15%", Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_WhenAccountIsDisabledAndReEnabled_DiscardsEarlierResult()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => refreshResult.Task
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await viewModel.UpdateAccountAsync(profile with { IsEnabled = false });
		await viewModel.UpdateAccountAsync(profile with { IsEnabled = true });
		refreshResult.SetResult(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 20, "舊查詢 20%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow));
		await refreshTask;

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		Assert.Empty(account.UsageMetrics);
		Assert.Equal("尚未連接", account.StatusText);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenStoppedDuringActiveRefresh_StillDeletesCache()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 35, "已使用 35%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "old@example.com");
		TaskCompletionSource<UsageSnapshot> refreshResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => refreshResult.Task
		};
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		Assert.Single(viewModel.Accounts).BeginProviderAccountChange();

		Task forcedRefresh = viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);

		Assert.Equal(
			(profile.Id, profile.Provider),
			Assert.Single(snapshotStore.DeletedSnapshots));
		viewModel.StopRefreshing();
		refreshResult.SetResult(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 10, "舊查詢 10%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow));
		await Task.WhenAll(activeRefresh, forcedRefresh);

		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Empty(snapshotStore.SavedSnapshots);
		Assert.True(refreshCoordinator.InvalidatedAccountIds.Count >= 2);
		Assert.Equal(2, snapshotStore.DeletedSnapshots.Count);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenClaudeIdentityChanges_DoesNotSeedOldUsage()
	{
		Guid accountId = Guid.NewGuid();
		ClaudeSubscriptionContext context =
			ClaudeSubscriptionContext.CreateVerified(
				"person@example.com",
				"org-company",
				"max",
				"Company Team");
		ClaudeAccountBinding binding = ClaudeAccountBinding.Create(
			accountId,
			context);
		string oldPublicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		string newPublicBindingIdentity = Guid.NewGuid().ToString("N");
		AccountProfile profile = new(
			accountId,
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: oldPublicBindingIdentity,
			HasAcceptedClaudeQuotaRisk: true);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 70, "舊帳號 70%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: oldPublicBindingIdentity,
			ProviderAccountDisplayIdentity: context.AccountIdentity,
			SubscriptionScopeDisplayName: context.SubscriptionScopeDisplayName,
			PlanTier: context.PlanTier,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				DateTimeOffset.UtcNow,
				Error: "暫時無法讀取。"))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore(cachedSnapshot),
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			claudeAccountBindingStore:
				new FakeClaudeAccountBindingStore(binding),
			claudeBindingCommitGate: new ClaudeBindingCommitGate());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity(newPublicBindingIdentity);

		await viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);

		Assert.Single(refreshCoordinator.SeededSnapshots);
		Assert.Equal("讀取失敗", account.StatusText);
		Assert.Empty(account.UsageMetrics);
		account.AbortProviderAccountChange();
		Assert.Equal(oldPublicBindingIdentity, account.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountUsageViewModel_WhenCodexSwitchUsesOldFallback_DoesNotConfirmOrPersistIdentity()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 30, "已使用 30%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "old@example.com"));
		account.BeginProviderAccountChange();
		UsageSnapshot fallbackSnapshot = Assert.IsType<UsageSnapshot>(
			account.GetProviderAccountChangeFallbackSnapshot());

		account.ApplySnapshot(fallbackSnapshot with
		{
			Status = SnapshotStatus.Stale,
			Error = "暫時無法讀取。"
		});
		account.CompleteProviderAccountChange();

		Assert.False(account.CanPersistCurrentSnapshot);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Equal("尚未確認", account.AccountDisplayText);
		Assert.Contains("old@example.com", account.NoticeText, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountUsageViewModel_WhenClaudeSwitchTargetsSameIdentity_FallbackCanPersist()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 30, "已使用 30%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "same@example.com"));
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity("same@example.com");
		UsageSnapshot fallbackSnapshot = Assert.IsType<UsageSnapshot>(
			account.GetProviderAccountChangeFallbackSnapshot());

		account.ApplySnapshot(fallbackSnapshot with
		{
			Status = SnapshotStatus.Stale,
			Error = "暫時無法讀取。"
		});
		account.EndProviderAccountChange();

		Assert.True(account.CanPersistCurrentSnapshot);
		Assert.Equal("same@example.com", account.ProviderAccountIdentity);
		Assert.Equal("顯示上次資料", account.StatusText);
		Assert.True(account.HasNotice);
	}

	[Fact]
	public void AccountUsageViewModel_WhenStaleObservationIsKnown_IncludesYear()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "claude@example.com");
		DateTimeOffset observedAt = new(2025, 12, 31, 20, 30, 0, TimeSpan.Zero);
		AccountUsageViewModel account = new(profile, canManage: true);

		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 30, "已使用 30%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			observedAt,
			ObservedAt: observedAt,
			Error: "暫時無法讀取。",
			ProviderAccountIdentity: profile.ProviderAccountIdentity));

		Assert.Contains(
			observedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
			account.NoticeText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenOldCacheWriteFinishesLate_DeletesItAgain()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> allowSaveToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Interlocked.Increment(ref requestCount) == 1
				? Task.FromResult(new UsageSnapshot(
					account,
					new[]
					{
						new UsageMetric("five_hour", "5 小時用量", 60, "舊帳號 60%")
					},
					SourceTrust.OfficialExperimental,
					SnapshotStatus.Ready,
					DateTimeOffset.UtcNow,
					ProviderAccountIdentity: "old@example.com"))
				: Task.FromResult(new UsageSnapshot(
					account,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					DateTimeOffset.UtcNow,
					Error: "暫時無法讀取。"))
		};
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SaveHandler = async (_, _) =>
			{
				saveStarted.TrySetResult(true);
				await allowSaveToFinish.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity("new@example.com");

		Task forcedRefresh = viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);
		Assert.Single(snapshotStore.DeletedSnapshots);
		allowSaveToFinish.SetResult(true);
		await Task.WhenAll(activeRefresh, forcedRefresh);

		Assert.Equal(2, snapshotStore.DeletedSnapshots.Count);
		Assert.Null(await snapshotStore.LoadAsync(profile));
		Assert.Equal("讀取失敗", account.StatusText);
		Assert.Equal("new@example.com", account.ProviderAccountIdentity);
	}

	[Theory]
	[InlineData("old@example.com")]
	[InlineData(null)]
	public async Task RefreshUsageAfterInvalidationAsync_WhenProviderIdentityCannotBeConfirmed_IgnoresUsage(
		string? providerIdentity)
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("five_hour", "5 小時用量", 75, "另一帳號 75%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: providerIdentity))
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 50, "舊帳號 50%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
			ProviderAccountIdentity: "old@example.com"));
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity("new@example.com");

		await viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);
		account.EndProviderAccountChange();

		Assert.Equal("讀取失敗", account.StatusText);
		Assert.Empty(account.UsageMetrics);
		Assert.Equal("new@example.com", account.ProviderAccountIdentity);
		Assert.Contains(
			providerIdentity is null
				? "無法確認登入帳號"
				: "與這個帳號不同",
			account.SecondaryText,
			StringComparison.Ordinal);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenCoordinatorCachedRejectedIdentity_PreservesThrottleBeforeQueryingAgain()
	{
		FixedTimeProvider timeProvider = new(
			new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero));
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		StatefulUsageProvider provider = new(
			ProviderKind.Claude,
			(account, callNumber) => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"five_hour",
						"5 小時用量",
						callNumber == 1 ? 75 : 25,
						callNumber == 1 ? "錯誤帳號 75%" : "新帳號 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				timeProvider.GetUtcNow(),
				ProviderAccountIdentity: callNumber == 1
					? "old@example.com"
					: "new@example.com")),
			minimumRefreshInterval: TimeSpan.FromMinutes(5));
		UsageRefreshCoordinator refreshCoordinator = new(
			new UsageProviderRegistry(new[] { provider }),
			timeProvider);
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric("five_hour", "5 小時用量", 50, "舊帳號 50%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			timeProvider.GetUtcNow() - TimeSpan.FromMinutes(1),
			ProviderAccountIdentity: "old@example.com"));
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity("new@example.com");

		await viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);
		account.EndProviderAccountChange();
		Assert.Equal("讀取失敗", account.StatusText);

		await viewModel.RefreshUsageAsync();
		Assert.Equal(1, provider.CallCount);
		Assert.Equal("讀取失敗", account.StatusText);

		timeProvider.Advance(TimeSpan.FromMinutes(5));
		await viewModel.RefreshUsageAsync();

		Assert.Equal(2, provider.CallCount);
		Assert.Equal("可用", account.StatusText);
		Assert.Equal("新帳號 25%", Assert.Single(account.UsageMetrics).DisplayValue);
		Assert.Equal("new@example.com", account.ProviderAccountIdentity);
		UsageSnapshot persistedSnapshot = Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Equal("new@example.com", persistedSnapshot.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_WhenLifecycleChanges_DoesNotSwallowFollowingRefreshAll()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		TaskCompletionSource<UsageSnapshot> firstResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = _ => firstResult.Task
		};
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		Task activeRefresh = viewModel.RefreshUsageAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Task forcedRefresh = viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);

		account.ApplyProfile(profile with { IsEnabled = false }, canManage: true);
		account.ApplyProfile(profile with { IsEnabled = true }, canManage: true);
		Task refreshAllAfterForced = viewModel.RefreshUsageAsync();
		firstResult.SetResult(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow));
		await Task.WhenAll(activeRefresh, forcedRefresh, refreshAllAfterForced);

		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(
			AccountStatusKind.NotConfigured,
			Assert.Single(viewModel.Accounts).StatusKind);
		Assert.Empty(snapshotStore.SavedSnapshots);
	}

	[Fact]
	public async Task AccountMutation_WhenProviderConnectionIsActive_IsRejected()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore store = new(profile);
		DashboardViewModel viewModel = new(
			store,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.UpdateAccountAsync(profile with { IsEnabled = false }));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.RemoveAccountAsync(profile.Id));

		Assert.True(Assert.Single(viewModel.Accounts).IsEnabled);
		Assert.Empty(store.SavedSnapshots);
	}

	[Fact]
	public async Task UpdateAccountAsync_WhenStoppedDuringCacheLoad_DoesNotRestoreCache()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			IsEnabled: false);
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric("codex", "5 小時用量", 50, "已使用 50%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow);
		TaskCompletionSource<bool> loadStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot?> cacheResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageSnapshotStore snapshotStore = new(cachedSnapshot)
		{
			LoadHandler = (_, _) =>
			{
				loadStarted.TrySetResult(true);
				return cacheResult.Task;
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Task enableTask = viewModel.UpdateAccountAsync(profile with { IsEnabled = true });
		await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		viewModel.StopRefreshing();
		cacheResult.SetResult(cachedSnapshot);
		await enableTask;

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		Assert.Empty(refreshCoordinator.SeededSnapshots);
	}

	[Fact]
	public async Task UpdateShutdownReservation_BlocksWritesAndRollbackRestoresActions()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Assert.True(viewModel.TryReserveAccountMutationsForUpdateShutdown());
		Assert.False(viewModel.CanStartAccountManagement);
		Assert.False(viewModel.CanChangeSortMode);
		Assert.False(viewModel.CanChangeUsageDisplayMode);
		Assert.False(viewModel.CanRefresh);
		await viewModel.RefreshUsageAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.ToggleUsageSortModeAsync());
		Assert.Empty(refreshCoordinator.RefreshRequests);

		Assert.True(await viewModel
			.RollbackAccountMutationsUpdateShutdownReservationAsync());

		Assert.True(viewModel.CanStartAccountManagement);
		Assert.True(viewModel.CanChangeSortMode);
		Assert.True(viewModel.CanChangeUsageDisplayMode);
		Assert.True(viewModel.CanRefresh);
		await viewModel.ToggleUsageSortModeAsync();
		Assert.Equal(UsageSortMode.Automatic, viewModel.SortMode);
	}

	[Fact]
	public async Task UpdateShutdownRollback_WhenDurableCommitHoldsGate_WaitsWithoutBlocking()
	{
		const string ProviderIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		TaskCompletionSource<bool> saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseSave = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		profileStore.SaveHandler = async (_, _) =>
		{
			saveStarted.TrySetResult(true);
			await releaseSave.Task;
		};
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		Assert.True(viewModel.TryReserveAccountMutationsForUpdateShutdown());
		Assert.True(account.TryBeginProviderAccountChangeCommit());
		Task<AuthenticatedProviderBindingCommitResult> commitTask =
			viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				ProviderIdentity,
				CancellationToken.None);
		await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Task<bool> rollbackTask = viewModel
			.RollbackAccountMutationsUpdateShutdownReservationAsync();

		Assert.False(rollbackTask.IsCompleted);
		releaseSave.TrySetResult(true);
		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			await commitTask.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.True(await rollbackTask.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.True(viewModel.CanStartAccountManagement);
	}

	[Fact]
	public async Task LockAccountMutationsForUpdateShutdown_HoldsGateUntilProcessExit()
	{
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(),
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		Assert.True(viewModel.TryReserveAccountMutationsForUpdateShutdown());

		Assert.True(await viewModel.LockAccountMutationsForUpdateShutdownAsync(
			TimeSpan.FromSeconds(1)));
		using CancellationTokenSource timeoutSource = new(
			TimeSpan.FromMilliseconds(50));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => viewModel.ToggleUsageDisplayModeAsync(timeoutSource.Token));
	}

	[Fact]
	public async Task StopRefreshing_InvalidatesAccountsAndPreventsNewRefresh()
	{
		AccountProfile firstAccount = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude Pro");
		AccountProfile secondAccount = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(firstAccount, secondAccount),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		viewModel.StopRefreshing();
		await viewModel.RefreshUsageAsync();

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			new[] { firstAccount.Id, secondAccount.Id }.OrderBy(id => id),
			refreshCoordinator.InvalidatedAccountIds.OrderBy(id => id));
	}

	[Fact]
	public async Task StopAndDrainRefreshingAsync_WhenRefreshIsActive_WaitsForCleanup()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> cleanupFinished = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				refreshStarted.TrySetResult(true);
				return cleanupFinished.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Task<bool> drainTask = viewModel.StopAndDrainRefreshingAsync(
			TimeSpan.FromSeconds(5));

		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.False(drainTask.IsCompleted);

		cleanupFinished.SetResult(CreateReadySnapshot(profile));

		Assert.True(await drainTask.WaitAsync(TimeSpan.FromSeconds(5)));
		await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.InvalidatedAccountIds));
		Assert.False(viewModel.IsRefreshing);
	}

	[Fact]
	public async Task StopAndDrainRefreshingAsync_WhenCleanupHangs_ReturnsFalse()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> cleanupFinished = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				refreshStarted.TrySetResult(true);
				return cleanupFinished.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task refreshTask = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Task<bool> drainTask = viewModel.StopAndDrainRefreshingAsync(
			TimeSpan.FromMilliseconds(50));

		bool wasDrained = await drainTask;

		Assert.False(wasDrained);
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.InvalidatedAccountIds));

		cleanupFinished.SetResult(CreateReadySnapshot(profile));
		await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(viewModel.IsRefreshing);
	}

	[Fact]
	public async Task StopAndDrainRefreshingAsync_RejectsNewBackgroundRefreshWhileDraining()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		TaskCompletionSource<bool> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> refreshFinished = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account =>
			{
				refreshStarted.TrySetResult(true);
				return refreshFinished.Task;
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		Task activeRefresh = viewModel.RefreshUsageAsync();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<bool> drainTask = viewModel.StopAndDrainRefreshingAsync(
			TimeSpan.FromSeconds(5));

		await viewModel.RefreshUsageInBackgroundAsync();

		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		refreshFinished.SetResult(CreateReadyAntigravitySnapshot(
			profile,
			DateTimeOffset.UtcNow));

		Assert.True(await drainTask.WaitAsync(TimeSpan.FromSeconds(5)));
		await activeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(
			profile.Id,
			Assert.Single(refreshCoordinator.InvalidatedAccountIds));
	}

	[Fact]
	public async Task RefreshUsageAfterInvalidationAsync_AfterSuccessfulDrain_DoesNotMutateRefreshStateOrCache()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		FakeUsageSnapshotStore snapshotStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();

		Assert.True(await viewModel.StopAndDrainRefreshingAsync(
			TimeSpan.FromSeconds(1)));
		int invalidationCount = refreshCoordinator.InvalidatedAccountIds.Count;

		await viewModel.RefreshUsageAfterInvalidationAsync(profile.Id);

		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			invalidationCount,
			refreshCoordinator.InvalidatedAccountIds.Count);
		Assert.Empty(snapshotStore.DeletedSnapshots);
	}

	[Fact]
	public async Task RefreshUsageAsync_PrioritizesActiveAgyBeforeOtherProviders()
	{
		AccountProfile[] profiles =
		[
			new(Guid.NewGuid(), ProviderKind.Claude, "Claude 1"),
			new(Guid.NewGuid(), ProviderKind.Codex, "Codex 1"),
			new(Guid.NewGuid(), ProviderKind.Claude, "Claude 2"),
			new(Guid.NewGuid(), ProviderKind.Codex, "Codex 2"),
			new(
				Guid.NewGuid(),
				ProviderKind.Antigravity,
				"AGY",
				ProviderAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity)
		];
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = account => Task.FromResult(
				account.Provider == ProviderKind.Antigravity
					? CreateReadyAntigravitySnapshot(
						account,
						DateTimeOffset.UtcNow)
					: CreateReadySnapshot(account))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profiles),
			refreshCoordinator);
		await viewModel.InitializeAsync();

		await viewModel.RefreshUsageAsync();

		Assert.NotEmpty(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			ProviderKind.Antigravity,
			refreshCoordinator.RefreshRequests.First().Provider);
	}

	private static void ApplyClaudeOrganizationSnapshot(
		AccountUsageViewModel account,
		string bindingIdentity,
		string organizationName,
		string email = "first@example.com")
	{
		account.ApplySnapshot(new UsageSnapshot(
			account.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: bindingIdentity,
			ProviderAccountDisplayIdentity: email,
			SubscriptionScopeDisplayName: organizationName,
			PlanTier: "max",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified));
	}

	private static async Task WaitForConditionAsync(
		Func<bool> condition,
		TimeSpan timeout)
	{
		ArgumentNullException.ThrowIfNull(condition);
		using CancellationTokenSource timeoutSource = new(timeout);

		while (!condition())
		{
			await Task.Delay(10, timeoutSource.Token);
		}
	}

	private static UsageSnapshot CreateReadyAntigravitySnapshot(
		AccountProfile account,
		DateTimeOffset observedAt,
		string displayValue = "已使用 25%")
	{
		return new UsageSnapshot(
			account,
			[
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini weekly",
					25d,
					displayValue)
			],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
	}
}
