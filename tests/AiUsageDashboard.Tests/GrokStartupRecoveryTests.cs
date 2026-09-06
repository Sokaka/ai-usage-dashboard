using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.Tests;

public sealed class GrokStartupRecoveryTests
{
	private sealed class InMemoryAccountProfileStore : IAccountProfileStore
	{
		private IReadOnlyList<AccountProfile> _profiles;

		internal bool CanSave { get; set; } = true;

		internal List<IReadOnlyList<AccountProfile>> SavedSnapshots { get; } =
			new();

		internal bool ShouldFailSave { get; set; }

		internal InMemoryAccountProfileStore(params AccountProfile[] profiles)
		{
			_profiles = profiles;
		}

		public Task<AccountProfileLoadResult> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(new AccountProfileLoadResult(
				_profiles,
				AccountProfileLoadStatus.Loaded,
				CanSave));
		}

		public Task SaveAsync(
			IReadOnlyList<AccountProfile> accounts,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (ShouldFailSave)
			{
				throw new AccountProfileStoreException(
					"測試用帳號設定儲存失敗。");
			}

			AccountProfile[] snapshot = accounts.ToArray();
			SavedSnapshots.Add(snapshot);
			_profiles = snapshot;
			return Task.CompletedTask;
		}
	}

	private sealed class TrackingUsageRefreshCoordinator :
		IUsageRefreshCoordinator
	{
		internal List<Guid> InvalidatedAccountIds { get; } = new();

		public TimeSpan? GetRemainingCooldown(AccountProfile account)
		{
			return null;
		}

		public Task<UsageSnapshot> RefreshAsync(
			AccountProfile account,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Startup recovery must not refresh usage directly.");
		}

		public bool SeedLastKnownGood(UsageSnapshot snapshot)
		{
			return false;
		}

		public void Invalidate(Guid accountId)
		{
			InvalidatedAccountIds.Add(accountId);
		}
	}

	private sealed class StubGrokAccountBindingStore :
		IGrokAccountBindingStore
	{
		private readonly Dictionary<Guid, GrokAccountBinding> _bindings = new();

		internal int LoadCallCount { get; private set; }

		internal int DeleteCallCount { get; private set; }

		internal List<Guid> DeletedAccountIds { get; } = new();

		internal int SaveCallCount { get; private set; }

		internal Func<Guid, CancellationToken, Task>? DeleteHandler
		{
			get;
			set;
		}

		internal StubGrokAccountBindingStore(GrokAccountBinding? binding)
		{
			if (binding is not null)
			{
				_bindings[binding.AccountId] = binding;
			}
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
			LoadCallCount++;
			_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SaveCallCount++;
			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		public async Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeleteCallCount++;
			DeletedAccountIds.Add(accountId);
			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, cancellationToken);
			}

			_bindings.Remove(accountId);
		}

		internal GrokAccountBinding? GetBinding(Guid accountId)
		{
			_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
			return binding;
		}

		internal void Seed(GrokAccountBinding binding)
		{
			_bindings[binding.AccountId] = binding;
		}
	}

	private sealed class StubGrokUsagePoller : IGrokUsagePoller
	{
		private readonly Func<Guid, CancellationToken, Task<GrokUsagePollResult>>
			_handler;

		internal int PollCallCount { get; private set; }

		internal int BoundPollCallCount { get; private set; }

		internal StubGrokUsagePoller(
			Func<Guid, GrokUsagePollResult> handler)
		{
			_handler = (accountId, _) => Task.FromResult(handler(accountId));
		}

		internal StubGrokUsagePoller(
			Func<Guid, CancellationToken, Task<GrokUsagePollResult>> handler)
		{
			_handler = handler;
		}

		public Task<GrokUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			PollCallCount++;
			return _handler(accountId, cancellationToken);
		}

		public Task<GrokUsagePollResult> PollBoundAsync(
			Guid accountId,
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			BoundPollCallCount++;
			return _handler(accountId, cancellationToken);
		}
	}

	private sealed class InMemoryGrokConnectionPendingStore :
		IGrokConnectionPendingStore
	{
		internal GrokConnectionPendingWork? Work { get; private set; }

		internal int RemoveCallCount { get; private set; }

		internal int RemoveFailuresRemaining { get; set; }

		internal int TryAdvanceCallCount { get; private set; }

		internal InMemoryGrokConnectionPendingStore(
			GrokConnectionPendingWork work)
		{
			Work = work;
		}

		public Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<GrokConnectionPendingWork> result = Work is null
				? Array.Empty<GrokConnectionPendingWork>()
				: new[] { Work! };
			return Task.FromResult(result);
		}

		public Task<GrokConnectionBeginResult> BeginAsync(
			Guid accountId,
			Guid attemptId,
			DateTimeOffset startedAtUtc,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"Startup recovery must not begin pending work.");
		}

		public Task<bool> TryAdvanceAsync(
			Guid accountId,
			Guid attemptId,
			GrokConnectionPendingStage stage,
			Guid? publicBindingId,
			DateTimeOffset updatedAtUtc,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TryAdvanceCallCount++;
			GrokConnectionPendingWork? work = Work;
			bool didAdvance = (work is not null) &&
				(work.AccountId == accountId) &&
				(work.AttemptId == attemptId) &&
				(stage > work.Stage);

			if (didAdvance)
			{
				Work = work! with
				{
					Stage = stage,
					PublicBindingId = publicBindingId,
					UpdatedAtUtc = updatedAtUtc
				};
			}

			return Task.FromResult(didAdvance);
		}

		public Task<bool> TryRemoveAsync(
			Guid accountId,
			Guid attemptId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RemoveCallCount++;
			if (RemoveFailuresRemaining > 0)
			{
				RemoveFailuresRemaining--;
				return Task.FromResult(false);
			}

			bool didRemove = Work is GrokConnectionPendingWork work &&
				(work.AccountId == accountId) &&
				(work.AttemptId == attemptId);

			if (didRemove)
			{
				Work = null;
			}

			return Task.FromResult(didRemove);
		}
	}

	private sealed class SequencedLoadGrokConnectionPendingStore :
		IGrokConnectionPendingStore
	{
		private readonly Func<
			CancellationToken,
			Task<IReadOnlyList<GrokConnectionPendingWork>>>[] _loadHandlers;
		private int _loadIndex;

		internal int LoadCallCount => Volatile.Read(ref _loadIndex);

		internal SequencedLoadGrokConnectionPendingStore(
			params Func<
				CancellationToken,
				Task<IReadOnlyList<GrokConnectionPendingWork>>>[] loadHandlers)
		{
			_loadHandlers = loadHandlers;
		}

		public Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			int index = Interlocked.Increment(ref _loadIndex) - 1;
			if ((index < 0) || (index >= _loadHandlers.Length))
			{
				throw new InvalidOperationException(
					"No Grok pending-store load result was configured.");
			}

			return _loadHandlers[index](cancellationToken);
		}

		public Task<GrokConnectionBeginResult> BeginAsync(
			Guid accountId,
			Guid attemptId,
			DateTimeOffset startedAtUtc,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"The sequenced load store only supports journal reads.");
		}

		public Task<bool> TryAdvanceAsync(
			Guid accountId,
			Guid attemptId,
			GrokConnectionPendingStage stage,
			Guid? publicBindingId,
			DateTimeOffset updatedAtUtc,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"The sequenced load store only supports journal reads.");
		}

		public Task<bool> TryRemoveAsync(
			Guid accountId,
			Guid attemptId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException(
				"The sequenced load store only supports journal reads.");
		}
	}

	private static readonly DateTimeOffset StartedAtUtc = new(
		2026,
		8,
		18,
		1,
		2,
		3,
		TimeSpan.Zero);

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingPersistedAndBindingMatches_CommitsProfileAndRemovesJournal()
	{
		Guid accountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(
			CreateBinding(accountId, publicBindingId));
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.BindingPersisted,
				publicBindingId));
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Binding-persisted recovery must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		string expectedIdentity = publicBindingId.ToString("N");
		Assert.Equal(
			expectedIdentity,
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Equal(
			expectedIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.Null(pendingStore.Work);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Equal(accountId, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
		Assert.False(
			Assert.Single(viewModel.Accounts)
				.IsProviderAccountChangeInProgress);
		Assert.Equal(0, usagePoller.PollCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingPersistedAndOrphanExists_RemovesOrphanBeforeCommit()
	{
		Guid accountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		GrokAccountBinding targetBinding = CreateBinding(
			accountId,
			publicBindingId);
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			"orphaned-principal");
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(targetBinding);
		bindingStore.Seed(orphanBinding);
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.BindingPersisted,
				publicBindingId));
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Binding-persisted recovery must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Same(targetBinding, bindingStore.GetBinding(accountId));
		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Equal(
			orphanBinding.AccountId,
			Assert.Single(bindingStore.DeletedAccountIds));
		Assert.Null(pendingStore.Work);
		Assert.Equal(
			publicBindingId.ToString("N"),
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Equal(0, usagePoller.PollCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingPersistedOrphanCleanupFails_RetainsWorkAndProfile()
	{
		Guid accountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		GrokAccountBinding targetBinding = CreateBinding(
			accountId,
			publicBindingId);
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			"orphaned-principal");
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(targetBinding)
		{
			DeleteHandler = (_, _) => throw new IOException(
				"synthetic orphan cleanup failure")
		};
		bindingStore.Seed(orphanBinding);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.BindingPersisted,
			publicBindingId);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Binding-persisted recovery must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Same(targetBinding, bindingStore.GetBinding(accountId));
		Assert.Same(
			orphanBinding,
			bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Equal(
			orphanBinding.AccountId,
			Assert.Single(bindingStore.DeletedAccountIds));
		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Null(
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.Equal(0, usagePoller.PollCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingGateIsBusy_AcquiresAccountMutationGateFirst()
	{
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		DashboardViewModel viewModel = new(
			profileStore,
			new TrackingUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.LoginCompleted,
				publicBindingId: null));
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult("startup-lock-order-principal"));
		GrokBindingCommitGate bindingGate = new();
		IDisposable blockingBindingLease = await bindingGate.EnterAsync();
		Task? recoveryTask = null;

		try
		{
			recoveryTask = GrokStartupRecovery
				.RecoverGrokConnectionPendingWorkAsync(
					viewModel,
					bindingStore,
					pendingStore,
					usagePoller,
					bindingCommitGate: bindingGate);

			bool wasAccountMutationGateObserved = false;
			for (int attempt = 0; attempt < 100; attempt++)
			{
				using CancellationTokenSource probeCancellationSource =
					new(TimeSpan.FromMilliseconds(25));

				try
				{
					await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
						() => Task.FromResult(true),
						probeCancellationSource.Token);
				}
				catch (OperationCanceledException) when (
					probeCancellationSource.IsCancellationRequested)
				{
					wasAccountMutationGateObserved = true;
					break;
				}

				await Task.Delay(TimeSpan.FromMilliseconds(10));
			}

			Assert.True(
				wasAccountMutationGateObserved,
				"Grok startup recovery 必須先取得 account mutation gate，再等待 binding gate。");
		}
		finally
		{
			blockingBindingLease.Dispose();

			if (recoveryTask is not null)
			{
				await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
			}
		}

		Assert.NotNull(bindingStore.GetBinding(accountId));
		Assert.Null(pendingStore.Work);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingPersistedConflictDisconnectSaveFails_RetainsRecoveryWork()
	{
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		Guid oldTargetBindingId = Guid.NewGuid();
		AccountProfile ownerProfile = CreateGrokProfile(ownerAccountId) with
		{
			ProviderAccountIdentity = publicBindingId.ToString("N")
		};
		AccountProfile targetProfile = CreateGrokProfile(targetAccountId) with
		{
			ProviderAccountIdentity = oldTargetBindingId.ToString("N")
		};
		InMemoryAccountProfileStore profileStore = new(
			ownerProfile,
			targetProfile);
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel firstViewModel = new(
			profileStore,
			refreshCoordinator);
		await firstViewModel.InitializeAsync();
		profileStore.ShouldFailSave = true;
		GrokAccountBinding targetBinding = CreateBinding(
			targetAccountId,
			publicBindingId);
		StubGrokAccountBindingStore bindingStore = new(targetBinding);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			targetAccountId,
			GrokConnectionPendingStage.BindingPersisted,
			publicBindingId);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Binding-persisted conflict recovery must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			firstViewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Equal(targetBinding, bindingStore.GetBinding(targetAccountId));
		Assert.Equal(0, bindingStore.DeleteCallCount);
		Assert.Equal(0, pendingStore.RemoveCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		AccountUsageViewModel firstTarget = Assert.Single(
			firstViewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.False(firstTarget.IsEnabled);
		Assert.Null(firstTarget.Profile.ProviderAccountIdentity);
		AccountProfileLoadResult persistedAfterFailure =
			await profileStore.LoadAsync();
		Assert.Equal(
			targetProfile.ProviderAccountIdentity,
			Assert.Single(
				persistedAfterFailure.Accounts,
				profile => profile.Id == targetAccountId)
				.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		DashboardViewModel restartedViewModel = new(
			profileStore,
			refreshCoordinator);
		await restartedViewModel.InitializeAsync();
		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			restartedViewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.Null(bindingStore.GetBinding(targetAccountId));
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Equal(0, usagePoller.PollCallCount);
		AccountUsageViewModel restartedTarget = Assert.Single(
			restartedViewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.True(restartedTarget.IsEnabled);
		Assert.Null(restartedTarget.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingPersistedConflictCleanupInitiallyFails_NextRecoveryRemovesBindingAndJournal()
	{
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		Guid oldTargetBindingId = Guid.NewGuid();
		string conflictingIdentity = publicBindingId.ToString("N");
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(ownerAccountId) with
			{
				ProviderAccountIdentity = conflictingIdentity
			},
			CreateGrokProfile(targetAccountId) with
			{
				ProviderAccountIdentity = oldTargetBindingId.ToString("N")
			});
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokAccountBinding targetBinding = CreateBinding(
			targetAccountId,
			publicBindingId);
		StubGrokAccountBindingStore bindingStore = new(targetBinding);
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				targetAccountId,
				GrokConnectionPendingStage.BindingPersisted,
				publicBindingId))
		{
			RemoveFailuresRemaining = 1
		};
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Binding-persisted conflict recovery must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.NotNull(pendingStore.Work);
		Assert.Equal(targetBinding, bindingStore.GetBinding(targetAccountId));
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Null(
			Assert.Single(
				viewModel.Accounts,
				account => account.Id == targetAccountId)
				.Profile.ProviderAccountIdentity);
		Assert.Contains(
			"Grok 帳號連接尚未完成",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.Null(bindingStore.GetBinding(targetAccountId));
		Assert.Equal(2, bindingStore.DeleteCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(2, pendingStore.RemoveCallCount);
		Assert.Equal(0, usagePoller.PollCallCount);
		Assert.DoesNotContain(
			"Grok 帳號連接尚未完成",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.All(
			viewModel.Accounts,
			account => Assert.False(
				account.IsProviderAccountChangeInProgress));
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLoginCompleted_ReprobesAndCommitsAttemptCorrelatedBinding()
	{
		const string PrincipalId = "recovered-principal";
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		GrokAccountBinding binding = Assert.IsType<GrokAccountBinding>(
			bindingStore.GetBinding(accountId));
		Assert.Equal(pendingWork.AttemptId, binding.PublicBindingId);
		Assert.True(binding.MatchesPrincipal("user", PrincipalId));
		Assert.Equal(
			pendingWork.AttemptId.ToString("N"),
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Null(pendingStore.Work);
		Assert.Equal(1, usagePoller.PollCallCount);
		Assert.Equal(0, usagePoller.BoundPollCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(1, pendingStore.TryAdvanceCallCount);
		Assert.Equal(accountId, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLoginCompletedAndMatchingOrphanExists_RemovesOrphanBeforeCommit()
	{
		const string PrincipalId = "recovered-orphan-principal";
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			PrincipalId);
		StubGrokAccountBindingStore bindingStore = new(orphanBinding);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		GrokAccountBinding binding = Assert.IsType<GrokAccountBinding>(
			bindingStore.GetBinding(accountId));
		Assert.Equal(pendingWork.AttemptId, binding.PublicBindingId);
		Assert.True(binding.MatchesPrincipal("user", PrincipalId));
		Assert.Null(pendingStore.Work);
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(accountId, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenOrphanCleanupFails_RetainsLoginCompletedWork()
	{
		const string PrincipalId = "recovered-orphan-principal";
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			PrincipalId);
		StubGrokAccountBindingStore bindingStore = new(orphanBinding)
		{
			DeleteHandler = (_, _) => throw new IOException(
				"synthetic orphan cleanup failure")
		};
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Same(
			orphanBinding,
			bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
		Assert.False(
			Assert.Single(viewModel.Accounts)
				.IsProviderAccountChangeInProgress);
	}

	[Theory]
	[InlineData(
		nameof(GrokUsageAvailability.AuthenticationRequired),
		nameof(GrokCurrentAuthUsability.Unusable))]
	[InlineData(
		nameof(GrokUsageAvailability.TransientProbeError),
		nameof(GrokCurrentAuthUsability.Unknown))]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenCurrentAuthenticationCannotBeVerified_RetainsLoginCompletedWork(
		string usageAvailabilityName,
		string currentAuthUsabilityName)
	{
		GrokUsageAvailability usageAvailability = Enum.Parse<GrokUsageAvailability>(
			usageAvailabilityName);
		GrokCurrentAuthUsability currentAuthUsability =
			Enum.Parse<GrokCurrentAuthUsability>(currentAuthUsabilityName);
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ => CreatePollResult(
			"expired-principal-metadata",
			usageAvailability,
			currentAuthUsability));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.Null(
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Equal(1, usagePoller.PollCallCount);
		Assert.Equal(0, usagePoller.BoundPollCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Equal(0, pendingStore.TryAdvanceCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLoginStartedHasCorrelatedBinding_VerifiesAndCompletes()
	{
		const string PrincipalId = "correlated-principal";
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginStarted,
			publicBindingId: null);
		StubGrokAccountBindingStore bindingStore = new(
			GrokAccountBinding.Create(
				accountId,
				pendingWork.AttemptId,
				"user",
				PrincipalId));
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(
			pendingWork.AttemptId.ToString("N"),
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Null(pendingStore.Work);
		Assert.Equal(0, usagePoller.PollCallCount);
		Assert.Equal(1, usagePoller.BoundPollCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Equal(1, pendingStore.TryAdvanceCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenCorrelatedBindingPrincipalDoesNotMatch_RetainsJournalAndProfile()
	{
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginStarted,
			publicBindingId: null);
		GrokAccountBinding existingBinding = GrokAccountBinding.Create(
			accountId,
			pendingWork.AttemptId,
			"user",
			"original-principal");
		StubGrokAccountBindingStore bindingStore = new(existingBinding);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult("different-principal"));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Same(existingBinding, bindingStore.GetBinding(accountId));
		Assert.Null(
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(0, usagePoller.PollCallCount);
		Assert.Equal(1, usagePoller.BoundPollCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Equal(0, pendingStore.TryAdvanceCallCount);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenPrincipalBelongsToAnotherCard_RejectsAttemptAndClearsJournal()
	{
		const string PrincipalId = "already-bound-principal";
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(firstAccountId),
			CreateGrokProfile(secondAccountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(
			GrokAccountBinding.Create(
				firstAccountId,
				"user",
				PrincipalId));
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				secondAccountId,
				GrokConnectionPendingStage.LoginCompleted,
				publicBindingId: null));
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.All(
			viewModel.Accounts,
			account => Assert.Null(account.Profile.ProviderAccountIdentity));
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(1, usagePoller.PollCallCount);
		Assert.Equal(0, bindingStore.DeleteCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Equal(0, pendingStore.TryAdvanceCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenDuplicateTargetHasPreviousBinding_FailClosesBeforeRetriableCleanup()
	{
		const string PrincipalId = "already-bound-principal";
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		Guid ownerBindingId = Guid.NewGuid();
		Guid oldTargetBindingId = Guid.NewGuid();
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			targetAccountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		GrokAccountBinding ownerBinding = GrokAccountBinding.Create(
			ownerAccountId,
			ownerBindingId,
			"user",
			PrincipalId);
		GrokAccountBinding oldTargetBinding = GrokAccountBinding.Create(
			targetAccountId,
			oldTargetBindingId,
			"user",
			"previous-principal");
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(ownerAccountId) with
			{
				ProviderAccountIdentity = ownerBindingId.ToString("N")
			},
			CreateGrokProfile(targetAccountId) with
			{
				ProviderAccountIdentity = oldTargetBindingId.ToString("N")
			});
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(ownerBinding);
		bindingStore.Seed(oldTargetBinding);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork)
		{
			RemoveFailuresRemaining = 1
		};
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Same(ownerBinding, bindingStore.GetBinding(ownerAccountId));
		Assert.Same(oldTargetBinding, bindingStore.GetBinding(targetAccountId));
		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(targetAccount.ProviderAccountIdentity);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		IReadOnlyList<AccountProfile> persistedProfiles = Assert.Single(
			profileStore.SavedSnapshots);
		Assert.Null(Assert.Single(
			persistedProfiles,
			profile => profile.Id == targetAccountId).ProviderAccountIdentity);
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Equal(targetAccountId, Assert.Single(
			refreshCoordinator.InvalidatedAccountIds));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.Same(ownerBinding, bindingStore.GetBinding(ownerAccountId));
		Assert.Null(bindingStore.GetBinding(targetAccountId));
		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		Assert.Single(profileStore.SavedSnapshots);
		Assert.Equal(2, bindingStore.DeleteCallCount);
		Assert.Equal(1, bindingStore.SaveCallCount);
		Assert.Equal(2, pendingStore.RemoveCallCount);
		Assert.Equal(2, usagePoller.PollCallCount);
		Assert.Equal(0, pendingStore.TryAdvanceCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLoginCompletedConflictDisconnectSaveFails_RetainsRecoveryWork()
	{
		const string PrincipalId = "already-bound-principal";
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		Guid ownerBindingId = Guid.NewGuid();
		Guid oldTargetBindingId = Guid.NewGuid();
		AccountProfile ownerProfile = CreateGrokProfile(ownerAccountId) with
		{
			ProviderAccountIdentity = ownerBindingId.ToString("N")
		};
		AccountProfile targetProfile = CreateGrokProfile(targetAccountId) with
		{
			ProviderAccountIdentity = oldTargetBindingId.ToString("N")
		};
		InMemoryAccountProfileStore profileStore = new(
			ownerProfile,
			targetProfile);
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel firstViewModel = new(
			profileStore,
			refreshCoordinator);
		await firstViewModel.InitializeAsync();
		profileStore.ShouldFailSave = true;
		GrokAccountBinding ownerBinding = GrokAccountBinding.Create(
			ownerAccountId,
			ownerBindingId,
			"user",
			PrincipalId);
		GrokAccountBinding oldTargetBinding = GrokAccountBinding.Create(
			targetAccountId,
			oldTargetBindingId,
			"user",
			"previous-principal");
		StubGrokAccountBindingStore bindingStore = new(ownerBinding);
		bindingStore.Seed(oldTargetBinding);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			targetAccountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			firstViewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Equal(oldTargetBinding, bindingStore.GetBinding(targetAccountId));
		Assert.Equal(0, bindingStore.DeleteCallCount);
		Assert.Equal(0, pendingStore.RemoveCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		AccountUsageViewModel firstTarget = Assert.Single(
			firstViewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.False(firstTarget.IsEnabled);
		Assert.Null(firstTarget.Profile.ProviderAccountIdentity);
		AccountProfileLoadResult persistedAfterFailure =
			await profileStore.LoadAsync();
		Assert.Equal(
			targetProfile.ProviderAccountIdentity,
			Assert.Single(
				persistedAfterFailure.Accounts,
				profile => profile.Id == targetAccountId)
				.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		DashboardViewModel restartedViewModel = new(
			profileStore,
			refreshCoordinator);
		await restartedViewModel.InitializeAsync();
		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			restartedViewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.Null(bindingStore.GetBinding(targetAccountId));
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Equal(2, usagePoller.PollCallCount);
		AccountUsageViewModel restartedTarget = Assert.Single(
			restartedViewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.True(restartedTarget.IsEnabled);
		Assert.Null(restartedTarget.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLoginCompletedConflictStoreIsReadOnly_AbortsRuntimeChangeAndRetainsRecoveryWork()
	{
		const string PrincipalId = "already-bound-principal";
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		Guid ownerBindingId = Guid.NewGuid();
		Guid oldTargetBindingId = Guid.NewGuid();
		AccountProfile ownerProfile = CreateGrokProfile(ownerAccountId) with
		{
			ProviderAccountIdentity = ownerBindingId.ToString("N")
		};
		AccountProfile targetProfile = CreateGrokProfile(targetAccountId) with
		{
			ProviderAccountIdentity = oldTargetBindingId.ToString("N")
		};
		InMemoryAccountProfileStore profileStore = new(
			ownerProfile,
			targetProfile)
		{
			CanSave = false
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new TrackingUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		GrokAccountBinding ownerBinding = GrokAccountBinding.Create(
			ownerAccountId,
			ownerBindingId,
			"user",
			PrincipalId);
		GrokAccountBinding oldTargetBinding = GrokAccountBinding.Create(
			targetAccountId,
			oldTargetBindingId,
			"user",
			"previous-principal");
		StubGrokAccountBindingStore bindingStore = new(ownerBinding);
		bindingStore.Seed(oldTargetBinding);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			targetAccountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Equal(oldTargetBinding, bindingStore.GetBinding(targetAccountId));
		Assert.Equal(0, bindingStore.DeleteCallCount);
		Assert.Equal(0, pendingStore.RemoveCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetAccountId);
		Assert.True(targetAccount.IsEnabled);
		Assert.Equal(
			targetProfile.ProviderAccountIdentity,
			targetAccount.Profile.ProviderAccountIdentity);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		Assert.Equal(1, usagePoller.PollCallCount);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenCorrelatedBindingDuplicatesAnotherCard_RemovesTargetBindingAndJournal()
	{
		const string PrincipalId = "already-bound-principal";
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(firstAccountId),
			CreateGrokProfile(secondAccountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			secondAccountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		GrokAccountBinding firstBinding = GrokAccountBinding.Create(
			firstAccountId,
			"user",
			PrincipalId);
		GrokAccountBinding targetBinding = GrokAccountBinding.Create(
			secondAccountId,
			pendingWork.AttemptId,
			"user",
			PrincipalId);
		StubGrokAccountBindingStore bindingStore = new(firstBinding);
		bindingStore.Seed(targetBinding);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(_ =>
			CreatePollResult(PrincipalId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Null(pendingStore.Work);
		Assert.Same(firstBinding, bindingStore.GetBinding(firstAccountId));
		Assert.Null(bindingStore.GetBinding(secondAccountId));
		Assert.All(
			viewModel.Accounts,
			account => Assert.Null(account.Profile.ProviderAccountIdentity));
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(0, usagePoller.PollCallCount);
		Assert.Equal(1, usagePoller.BoundPollCallCount);
		Assert.Equal(1, bindingStore.DeleteCallCount);
		Assert.Equal(0, bindingStore.SaveCallCount);
		Assert.Equal(0, pendingStore.TryAdvanceCallCount);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenLegacyPrincipalObservedCannotBeSafelyRetried_RetainsJournalAndProfile()
	{
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.PrincipalObserved,
				publicBindingId: null));
		StubGrokUsagePoller usagePoller = new(_ =>
			throw new InvalidOperationException(
				"Uncorrelated legacy work must not poll Grok."));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore,
			usagePoller);

		Assert.Equal(
			GrokConnectionPendingStage.PrincipalObserved,
			Assert.IsType<GrokConnectionPendingWork>(pendingStore.Work).Stage);
		Assert.Null(
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, pendingStore.RemoveCallCount);
		Assert.Equal(0, usagePoller.PollCallCount);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenBindingDoesNotMatch_RetainsJournalAndProfile()
	{
		Guid accountId = Guid.NewGuid();
		Guid journalBindingId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(
			CreateBinding(accountId, Guid.NewGuid()));
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.BindingPersisted,
				journalBindingId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore);

		Assert.NotNull(pendingStore.Work);
		Assert.Null(
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Equal(1, bindingStore.LoadCallCount);
		Assert.Equal(0, pendingStore.RemoveCallCount);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhenProfileAlreadyCommitted_RemovesJournalOnly()
	{
		Guid accountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		string publicBindingIdentity = publicBindingId.ToString("N");
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId) with
			{
				ProviderAccountIdentity = publicBindingIdentity
			});
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(
			CreateBinding(accountId, publicBindingId));
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.BindingPersisted,
				publicBindingId));

		await GrokStartupRecovery.RecoverGrokConnectionPendingWorkAsync(
			viewModel,
			bindingStore,
			pendingStore);

		Assert.Equal(
			publicBindingIdentity,
			Assert.Single(viewModel.Accounts).Profile.ProviderAccountIdentity);
		Assert.Null(pendingStore.Work);
		Assert.Equal(1, pendingStore.RemoveCallCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.InvalidatedAccountIds);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkWithinBudgetAsync_WhenPollDoesNotFinish_StopsAtBudgetAndRetainsJournal()
	{
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		GrokConnectionPendingWork pendingWork = CreatePendingWork(
			accountId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(pendingWork);
		StubGrokUsagePoller usagePoller = new(async (_, cancellationToken) =>
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("Unreachable after cancellation.");
		});

		await GrokStartupRecovery
			.RecoverGrokConnectionPendingWorkWithinBudgetAsync(
				viewModel,
				bindingStore,
				pendingStore,
				usagePoller,
				TimeSpan.FromMilliseconds(100))
			.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(pendingWork, pendingStore.Work);
		Assert.Equal(1, usagePoller.PollCallCount);
		Assert.Contains(
			"Grok 帳號連接尚未完成",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RecoverGrokConnectionPendingWorkAsync_WhilePollIsRunning_BlocksAccountRemovalUntilRecoveryFinishes()
	{
		Guid accountId = Guid.NewGuid();
		InMemoryAccountProfileStore profileStore = new(
			CreateGrokProfile(accountId));
		TrackingUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		StubGrokAccountBindingStore bindingStore = new(binding: null);
		InMemoryGrokConnectionPendingStore pendingStore = new(
			CreatePendingWork(
				accountId,
				GrokConnectionPendingStage.LoginCompleted,
				publicBindingId: null));
		TaskCompletionSource pollEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<GrokUsagePollResult> releasePoll = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		StubGrokUsagePoller usagePoller = new(async (_, cancellationToken) =>
		{
			pollEntered.TrySetResult();
			return await releasePoll.Task.WaitAsync(cancellationToken);
		});

		Task recoveryTask = GrokStartupRecovery
			.RecoverGrokConnectionPendingWorkAsync(
				viewModel,
				bindingStore,
				pendingStore,
				usagePoller);
		await pollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.True(account.IsProviderAccountChangeInProgress);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => viewModel.RemoveAccountAsync(accountId));
		Assert.Single(viewModel.Accounts);
		Assert.Null(bindingStore.GetBinding(accountId));
		Assert.NotNull(pendingStore.Work);

		releasePoll.TrySetResult(CreatePollResult("reserved-during-poll"));
		await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(viewModel.Accounts);
		Assert.NotNull(bindingStore.GetBinding(accountId));
		Assert.Null(pendingStore.Work);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ObserveDeferredGrokAccountCleanupAsync_AfterBackgroundCompletion_RecalculatesRecoveryWarning()
	{
		DashboardViewModel viewModel = new(
			new InMemoryAccountProfileStore(),
			new TrackingUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		viewModel.SetGrokConnectionRecoveryPending(isPending: true);
		TaskCompletionSource<bool> cleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		SequencedLoadGrokConnectionPendingStore pendingStore = new(
			_ => Task.FromResult<IReadOnlyList<GrokConnectionPendingWork>>(
				Array.Empty<GrokConnectionPendingWork>()));

		Task observationTask = GrokStartupRecovery
			.ObserveDeferredGrokAccountCleanupAsync(
				cleanupCompletion.Task,
				viewModel,
				pendingStore);
		Assert.Equal(0, pendingStore.LoadCallCount);

		cleanupCompletion.TrySetResult(true);
		await observationTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, pendingStore.LoadCallCount);
		Assert.DoesNotContain(
			"Grok 帳號連接尚未完成",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ObserveDeferredGrokAccountCleanupAsync_WhenJournalReloadFails_PreservesRecoveryWarning()
	{
		DashboardViewModel viewModel = new(
			new InMemoryAccountProfileStore(),
			new TrackingUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		viewModel.SetGrokConnectionRecoveryPending(isPending: true);
		SequencedLoadGrokConnectionPendingStore pendingStore = new(
			_ => Task.FromException<IReadOnlyList<GrokConnectionPendingWork>>(
				new IOException("synthetic journal reload failure")));

		await GrokStartupRecovery.ObserveDeferredGrokAccountCleanupAsync(
			Task.FromResult(true),
			viewModel,
			pendingStore);

		Assert.Equal(1, pendingStore.LoadCallCount);
		Assert.Contains(
			"Grok 帳號連接尚未完成",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AppStartup_QueuesGrokRecoveryAfterShellConstruction()
	{
		string source = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"App.xaml.cs"));
		int onStartupStart = source.IndexOf(
			"protected override async void OnStartup",
			StringComparison.Ordinal);
		int queueMethodStart = source.IndexOf(
			"private void QueuePostStartupRecovery",
			StringComparison.Ordinal);
		Assert.True(onStartupStart >= 0);
		Assert.True(queueMethodStart > onStartupStart);
		string onStartup = source[onStartupStart..queueMethodStart];
		int shellConstruction = onStartup.IndexOf(
			"_floatingWidgetWindow = new FloatingWidgetWindow",
			StringComparison.Ordinal);
		int restoreSurface = onStartup.IndexOf(
			"RestoreStartupSurface(",
			StringComparison.Ordinal);
		int queueRecovery = onStartup.IndexOf(
			"QueuePostStartupRecovery(",
			StringComparison.Ordinal);

		Assert.True(shellConstruction >= 0);
		Assert.True(restoreSurface > shellConstruction);
		Assert.True(queueRecovery > restoreSurface);
		Assert.DoesNotContain(
			"await grokStartupRecovery.RecoverAsync(",
			onStartup,
			StringComparison.Ordinal);

		int postStartupMethod = source.IndexOf(
			"private async Task CompletePostStartupRecoveryAsync",
			StringComparison.Ordinal);
		int grokRecovery = source.IndexOf(
			"await grokStartupRecovery.RecoverAsync(",
			postStartupMethod,
			StringComparison.Ordinal);
		Assert.True(postStartupMethod > queueMethodStart);
		Assert.True(grokRecovery > postStartupMethod);

		string recoverySource = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Providers",
			"GrokStartupRecovery.cs"));
		int recoverMethod = recoverySource.IndexOf(
			"public async Task RecoverAsync",
			StringComparison.Ordinal);
		int connectionRecovery = recoverySource.IndexOf(
			"await RecoverGrokConnectionPendingWorkWithinBudgetAsync(",
			recoverMethod,
			StringComparison.Ordinal);
		int deferredCleanup = recoverySource.IndexOf(
			"await CompleteDeferredGrokAccountCleanupWithinBudgetAsync(",
			recoverMethod,
			StringComparison.Ordinal);
		Assert.True(recoverMethod >= 0);
		Assert.True(connectionRecovery > recoverMethod);
		Assert.True(deferredCleanup > connectionRecovery);

		string dashboardSource = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"ViewModels",
			"DashboardViewModel.cs"));
		int initializeStart = dashboardSource.IndexOf(
			"public async Task InitializeAsync",
			StringComparison.Ordinal);
		int deferredMethodStart = dashboardSource.IndexOf(
			"internal async Task<bool> CompleteDeferredStartupAccountCleanupsAsync",
			StringComparison.Ordinal);
		Assert.True(initializeStart >= 0);
		Assert.True(deferredMethodStart > initializeStart);
		string initializeSource =
			dashboardSource[initializeStart..deferredMethodStart];
		Assert.Contains(
			"deferDestructiveGrokCleanup: true",
			initializeSource,
			StringComparison.Ordinal);
	}

	private static GrokAccountBinding CreateBinding(
		Guid accountId,
		Guid publicBindingId)
	{
		return new GrokAccountBinding(
			accountId,
			publicBindingId,
			Convert.ToBase64String(new byte[32]),
			new string('A', 64));
	}

	private static AccountProfile CreateGrokProfile(Guid accountId)
	{
		return new AccountProfile(
			accountId,
			ProviderKind.Grok,
			"Grok");
	}

	private static GrokConnectionPendingWork CreatePendingWork(
		Guid accountId,
		GrokConnectionPendingStage stage,
		Guid? publicBindingId)
	{
		return new GrokConnectionPendingWork(
			accountId,
			Guid.NewGuid(),
			StartedAtUtc,
			StartedAtUtc.AddMinutes(1),
			stage,
			publicBindingId);
	}

	private static GrokUsagePollResult CreatePollResult(
		string principalId,
		GrokUsageAvailability usageAvailability =
			GrokUsageAvailability.Available,
		GrokCurrentAuthUsability currentAuthUsability =
			GrokCurrentAuthUsability.Usable)
	{
		return new GrokUsagePollResult(
			new GrokPrincipal("user", principalId),
			usageAvailability == GrokUsageAvailability.Available
				? new GrokWeeklyUsage(
					12,
					StartedAtUtc,
					StartedAtUtc.AddDays(7))
				: null,
			StartedAtUtc.AddMinutes(1),
			usageAvailability: usageAvailability,
			currentAuthUsability: currentAuthUsability);
	}
}
