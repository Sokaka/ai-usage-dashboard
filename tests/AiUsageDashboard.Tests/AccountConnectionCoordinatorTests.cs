using System.Windows;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;
using AiUsageDashboard.Core.Refreshing;

namespace AiUsageDashboard.Tests;

public sealed class AccountConnectionCoordinatorTests
{
	private static readonly Guid CodexWorkspaceId =
		Guid.Parse("22222222-2222-2222-2222-222222222222");

	private sealed class FakeAccountProfileStore : IAccountProfileStore
	{
		private IReadOnlyList<AccountProfile> _accounts;

		internal Func<
			IReadOnlyList<AccountProfile>,
			CancellationToken,
			Task>? SaveHandler { get; set; }

		internal bool ShouldFailSave { get; set; }

		internal List<IReadOnlyList<AccountProfile>> SavedSnapshots { get; } =
			new();

		internal FakeAccountProfileStore(params AccountProfile[] accounts)
		{
			_accounts = accounts;
		}

		public Task<AccountProfileLoadResult> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new AccountProfileLoadResult(
				_accounts,
				AccountProfileLoadStatus.Loaded,
				CanSave: true));
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
		}
	}

	private sealed class FakeUsageSnapshotStore : IUsageSnapshotStore
	{
		internal List<UsageSnapshot> SavedSnapshots { get; } = new();
		internal List<Guid> DeletedAccountIds { get; } = new();

		internal int DeleteFailuresRemaining { get; set; }

		internal UsageSnapshot? SnapshotToLoad { get; set; }

		public Task<UsageSnapshot?> LoadAsync(
			AccountProfile account,
			CancellationToken cancellationToken = default)
		{
			UsageSnapshot? snapshot = SnapshotToLoad;
			return Task.FromResult(
				(snapshot is not null) &&
				(snapshot.Account.Id == account.Id) &&
				(snapshot.Account.Provider == account.Provider)
					? snapshot
					: null);
		}

		public Task SaveAsync(
			UsageSnapshot snapshot,
			CancellationToken cancellationToken = default)
		{
			SavedSnapshots.Add(snapshot);
			SnapshotToLoad = snapshot;
			return Task.CompletedTask;
		}

		public Task DeleteAsync(
			Guid accountId,
			ProviderKind provider,
			CancellationToken cancellationToken = default)
		{
			DeletedAccountIds.Add(accountId);

			if (DeleteFailuresRemaining > 0)
			{
				DeleteFailuresRemaining--;
				throw new IOException("Synthetic transient cache delete failure.");
			}

			UsageSnapshot? snapshot = SnapshotToLoad;

			if ((snapshot is not null) &&
				(snapshot.Account.Id == accountId) &&
				(snapshot.Account.Provider == provider))
			{
				SnapshotToLoad = null;
			}

			return Task.CompletedTask;
		}
	}

	private sealed class FakeAntigravityAccountSetupLauncher :
		IAntigravityAccountSetupLauncher
	{
		private readonly Func<
			Guid,
			CancellationToken,
			Task<AntigravityAccountSetupOutcome>>
			_handler;

		internal int RunCount { get; private set; }

		internal Guid LastSetupAttemptId { get; private set; }

		internal FakeAntigravityAccountSetupLauncher(
			AntigravityAccountSetupOutcome outcome)
			: this(_ => Task.FromResult(outcome))
		{
		}

		internal FakeAntigravityAccountSetupLauncher(
			Func<CancellationToken, Task<AntigravityAccountSetupOutcome>> handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			_handler = (_, cancellationToken) => handler(cancellationToken);
		}

		internal FakeAntigravityAccountSetupLauncher(
			Func<Guid, CancellationToken, Task<AntigravityAccountSetupOutcome>>
				handler)
		{
			_handler = handler ?? throw new ArgumentNullException(nameof(handler));
		}

		public Task<AntigravityAccountSetupOutcome> RunAsync(
			Guid setupAttemptId,
			CancellationToken cancellationToken)
		{
			RunCount++;
			LastSetupAttemptId = setupAttemptId;
			return _handler(setupAttemptId, cancellationToken);
		}
	}

	private sealed class TrackingAntigravityReportedAccountSource :
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

		internal int RemoveCount { get; private set; }

		internal Func<
			CancellationToken,
			ValueTask<AntigravityReportedAccountObservation>>? ReadHandler
		{
			get;
			set;
		}

		internal void SignalFreshStatusLineInvocation()
		{
			_generation++;
			_hasFreshStatusLineInvocation = true;
		}

		public ValueTask<AntigravityReportedAccountObservation> ReadAsync(
			CancellationToken cancellationToken = default)
		{
			if (ReadHandler is not null)
			{
				return ReadHandler(cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();
			bool hasFreshStatusLineInvocation =
				_hasFreshStatusLineInvocation;
			_hasFreshStatusLineInvocation = false;
			return ValueTask.FromResult(
				new AntigravityReportedAccountObservation(
					_generation,
					ReportedAccount,
					hasFreshStatusLineInvocation,
					AntigravityReportedAccountObservationStatus.Available));
		}

		public ValueTask<bool> RemoveOwnedAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RemoveCount++;
			return ValueTask.FromResult(true);
		}
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private DateTimeOffset _utcNow;

		internal FixedTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}

		internal void Advance(TimeSpan elapsed)
		{
			_utcNow += elapsed;
		}
	}

	private sealed class FakeAntigravityVerifiedAccountBindingStore :
		IAntigravityVerifiedAccountBindingStore
	{
		internal List<Guid> DeletedAccountIds { get; } = new();

		public Task<AntigravityVerifiedAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<AntigravityVerifiedAccountBinding?>(null);
		}

		public Task SaveAsync(
			AntigravityVerifiedAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
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

	private sealed class FakeUsageRefreshCoordinator : IUsageRefreshCoordinator
	{
		internal Func<
			AccountProfile,
			CancellationToken,
			Task<UsageSnapshot>>? RefreshHandler { get; set; }

		internal List<Guid> InvalidatedAccountIds { get; } = new();

		internal List<AccountProfile> RefreshRequests { get; } = new();

		internal TimeSpan? RemainingCooldown { get; set; }

		public TimeSpan? GetRemainingCooldown(AccountProfile account)
		{
			return RemainingCooldown;
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

			return RefreshHandler?.Invoke(account, cancellationToken) ??
				Task.FromResult(new UsageSnapshot(
					account,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					DateTimeOffset.UtcNow));
		}

		public bool SeedLastKnownGood(UsageSnapshot snapshot)
		{
			return true;
		}
	}

	private sealed class UnusedClaudeAccountLogin : IClaudeAccountLogin
	{
		public Task<ClaudeAccountLoginResult> LoginAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Claude login is not used.");
		}
	}

	private sealed class DelegateClaudeAccountLogin : IClaudeAccountLogin
	{
		private readonly Dictionary<Guid, ClaudeSubscriptionObservation>
			_observations = new();
		private readonly object _observationsLock = new();
		private readonly Func<
			Guid,
			CancellationToken,
			Task<ClaudeAccountLoginResult>> _handler;

		internal DelegateClaudeAccountLogin(
			Func<CancellationToken, Task<ClaudeAccountLoginResult>> handler)
			: this((_, cancellationToken) => handler(cancellationToken))
		{
		}

		internal DelegateClaudeAccountLogin(
			Func<Guid, CancellationToken, Task<ClaudeAccountLoginResult>> handler)
		{
			_handler = handler ?? throw new ArgumentNullException(nameof(handler));
		}

		public async Task<ClaudeAccountLoginResult> LoginAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			ClaudeAccountLoginResult result = await _handler(
				accountId,
				cancellationToken);

			if ((result.SubscriptionContext is ClaudeSubscriptionContext context) &&
				(result.VersionEvidence is CliVersionEvidence versionEvidence))
			{
				lock (_observationsLock)
				{
					_observations[accountId] = new ClaudeSubscriptionObservation(
						context,
						versionEvidence);
				}
			}

			return result;
		}

		internal Task<ClaudeSubscriptionObservation> ProbeAsync(
			Guid accountId,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			lock (_observationsLock)
			{
				if (_observations.TryGetValue(
						accountId,
						out ClaudeSubscriptionObservation? observation))
				{
					return Task.FromResult(observation);
				}
			}

			throw new InvalidOperationException(
				"Claude fresh probe ran before the corresponding test login.");
		}
	}

	private sealed class DelegateClaudeSubscriptionContextProbe :
		IClaudeSubscriptionContextProbe
	{
		private readonly Func<
			Guid,
			CancellationToken,
			Task<ClaudeSubscriptionObservation>> _handler;

		internal DelegateClaudeSubscriptionContextProbe(
			Func<
				Guid,
				CancellationToken,
				Task<ClaudeSubscriptionObservation>> handler)
		{
			_handler = handler ?? throw new ArgumentNullException(nameof(handler));
		}

		public Task<ClaudeSubscriptionObservation> ProbeAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return _handler(accountId, cancellationToken);
		}
	}

	private sealed class FakeClaudeAccountBindingStore :
		IClaudeAccountBindingStore
	{
		private readonly Dictionary<Guid, ClaudeAccountBinding> _bindings = new();
		private readonly List<Guid> _deletedAccountIds = new();
		private readonly object _lock = new();
		private readonly List<ClaudeAccountBinding> _savedBindings = new();

		internal Func<Guid, CancellationToken, Task>? DeleteHandler { get; set; }

		internal Func<ClaudeAccountBinding, CancellationToken, Task>? SaveHandler
		{
			get;
			set;
		}

		internal bool ShouldRetainDeletedBindings { get; set; }

		internal IReadOnlyList<Guid> DeletedAccountIds
		{
			get
			{
				lock (_lock)
				{
					return _deletedAccountIds.ToArray();
				}
			}
		}

		internal IReadOnlyList<ClaudeAccountBinding> SavedBindings
		{
			get
			{
				lock (_lock)
				{
					return _savedBindings.ToArray();
				}
			}
		}

		public Task<IReadOnlyList<ClaudeAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			lock (_lock)
			{
				return Task.FromResult<IReadOnlyList<ClaudeAccountBinding>>(
					_bindings.Values.ToArray());
			}
		}

		public Task<ClaudeAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			lock (_lock)
			{
				_bindings.TryGetValue(accountId, out ClaudeAccountBinding? binding);
				return Task.FromResult(binding);
			}
		}

		public async Task SaveAsync(
			ClaudeAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (SaveHandler is not null)
			{
				await SaveHandler(binding, cancellationToken);
			}

			lock (_lock)
			{
				_savedBindings.Add(binding);
				_bindings[binding.AccountId] = binding;
			}
		}

		public async Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, cancellationToken);
			}

			lock (_lock)
			{
				_deletedAccountIds.Add(accountId);
				if (!ShouldRetainDeletedBindings)
				{
					_bindings.Remove(accountId);
				}
			}
		}

		internal ClaudeAccountBinding? GetBinding(Guid accountId)
		{
			lock (_lock)
			{
				_bindings.TryGetValue(accountId, out ClaudeAccountBinding? binding);
				return binding;
			}
		}

		internal void Seed(ClaudeAccountBinding binding)
		{
			lock (_lock)
			{
				_bindings[binding.AccountId] = binding;
			}
		}
	}

	private sealed class FakeCodexWorkspaceBindingStore :
		ICodexWorkspaceBindingStore
	{
		private readonly Dictionary<Guid, CodexWorkspaceBinding> _bindings = new();
		private readonly HashSet<Guid> _malformedAccountIds = new();
		private Exception? _nextDeleteFailure;
		private Exception? _nextLoadFailure;
		private Exception? _restartQuarantineSaveFailure;
		private int _restartQuarantineSaveFailuresRemaining;

		internal IReadOnlyCollection<CodexWorkspaceBinding> Bindings =>
			_bindings.Values.ToArray();

		internal int DeleteCount { get; private set; }

		internal int InjectedDeleteFailureCount { get; private set; }

		internal int InjectedLoadFailureCount { get; private set; }

		internal int InjectedRestartQuarantineSaveFailureCount { get; private set; }

		internal int SaveCount { get; private set; }

		public Task<IReadOnlyList<CodexWorkspaceBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_malformedAccountIds.Count > 0)
			{
				return Task.FromException<IReadOnlyList<CodexWorkspaceBinding>>(
					new InvalidDataException("Synthetic malformed binding inventory."));
			}
			return Task.FromResult<IReadOnlyList<CodexWorkspaceBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<CodexWorkspaceBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Exception? loadFailure = _nextLoadFailure;
			_nextLoadFailure = null;
			if (loadFailure is not null)
			{
				InjectedLoadFailureCount++;
				return Task.FromException<CodexWorkspaceBinding?>(loadFailure);
			}

			if (_malformedAccountIds.Contains(accountId))
			{
				return Task.FromException<CodexWorkspaceBinding?>(
					new InvalidDataException("Synthetic malformed current binding."));
			}
			_bindings.TryGetValue(
				accountId,
				out CodexWorkspaceBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			CodexWorkspaceBinding binding,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SaveCount++;
			if (binding.IsRestartQuarantine &&
				(_restartQuarantineSaveFailuresRemaining > 0))
			{
				_restartQuarantineSaveFailuresRemaining--;
				InjectedRestartQuarantineSaveFailureCount++;
				return Task.FromException(
					_restartQuarantineSaveFailure ??
						new IOException("Synthetic restart quarantine save failure."));
			}

			_bindings[binding.AccountId] = binding;
			return Task.CompletedTask;
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DeleteCount++;
			Exception? deleteFailure = _nextDeleteFailure;
			_nextDeleteFailure = null;
			if (deleteFailure is not null)
			{
				InjectedDeleteFailureCount++;
				return Task.FromException(deleteFailure);
			}

			_bindings.Remove(accountId);
			_malformedAccountIds.Remove(accountId);
			return Task.CompletedTask;
		}

		internal void Seed(CodexWorkspaceBinding binding)
		{
			_bindings[binding.AccountId] = binding;
		}

		internal void SeedMalformed(Guid accountId)
		{
			_malformedAccountIds.Add(accountId);
		}

		internal void FailNextLoad(Exception exception)
		{
			_nextLoadFailure = exception ??
				throw new ArgumentNullException(nameof(exception));
		}

		internal void FailNextRestartQuarantineSaves(
			int failureCount,
			Exception exception)
		{
			if (failureCount <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(failureCount));
			}

			_restartQuarantineSaveFailuresRemaining = failureCount;
			_restartQuarantineSaveFailure = exception ??
				throw new ArgumentNullException(nameof(exception));
		}

		internal void FailNextDelete(Exception exception)
		{
			_nextDeleteFailure = exception ??
				throw new ArgumentNullException(nameof(exception));
		}
	}

	private sealed class UnusedCodexAccountLogin : ICodexAccountLogin
	{
		public Task<ICodexAccountLoginSession> StartAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Codex login is not used.");
		}

		public Task<ICodexAccountLoginSession> StartAsync(
			Guid accountId,
			Guid workspaceId,
			CancellationToken cancellationToken = default)
		{
			throw new InvalidOperationException("Codex login is not used.");
		}
	}

	private sealed class DelegateCodexAccountLogin : ICodexAccountLogin
	{
		private sealed class WorkspaceConfigurationTransaction :
			ICodexWorkspaceConfigurationTransaction
		{
			private readonly Action _commitHandler;
			private readonly Action _rollbackHandler;
			private bool _isCommitted;
			private bool _isDisposed;

			internal WorkspaceConfigurationTransaction(
				Action commitHandler,
				Action rollbackHandler)
			{
				_commitHandler = commitHandler;
				_rollbackHandler = rollbackHandler;
			}

			public void Commit()
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				if (_isCommitted)
				{
					return;
				}

				_isCommitted = true;
				_commitHandler();
			}

			public ValueTask DisposeAsync()
			{
				if (_isDisposed)
				{
					return ValueTask.CompletedTask;
				}

				_isDisposed = true;
				if (!_isCommitted)
				{
					_rollbackHandler();
				}

				return ValueTask.CompletedTask;
			}
		}

		private sealed class Session : ICodexAccountLoginSession
		{
			private readonly Func<Task> _disposeHandler;
			private readonly Func<
				CancellationToken,
				Task<CodexAccountLoginResult>> _handler;
			private readonly Func<ICodexWorkspaceConfigurationTransaction>
				_workspaceConfigurationTransactionFactory;

			public Uri AuthorizationUri { get; } =
				new("https://example.invalid/codex-login");

			internal Session(
				Func<CancellationToken, Task<CodexAccountLoginResult>> handler,
				Func<Task> disposeHandler,
				Func<ICodexWorkspaceConfigurationTransaction>
					workspaceConfigurationTransactionFactory)
			{
				_handler = handler;
				_disposeHandler = disposeHandler;
				_workspaceConfigurationTransactionFactory =
					workspaceConfigurationTransactionFactory;
			}

			public Task<CodexAccountLoginResult> WaitForCompletionAsync(
				CancellationToken cancellationToken = default)
			{
				return _handler(cancellationToken);
			}

			public ICodexWorkspaceConfigurationTransaction
				TakeWorkspaceConfigurationTransaction()
			{
				return _workspaceConfigurationTransactionFactory();
			}

			public ValueTask DisposeAsync()
			{
				return new ValueTask(_disposeHandler());
			}
		}

		private readonly Func<Task> _disposeHandler;
		private readonly Func<
			Guid?,
			CancellationToken,
			Task<CodexAccountLoginResult>> _handler;

		internal Guid? LastWorkspaceId { get; private set; }
		internal int WorkspaceConfigurationCommitCount { get; private set; }
		internal int WorkspaceConfigurationRollbackCount { get; private set; }

		internal DelegateCodexAccountLogin(
			Func<CancellationToken, Task<CodexAccountLoginResult>> handler,
			Func<Task>? disposeHandler = null)
		{
			ArgumentNullException.ThrowIfNull(handler);
			_handler = (_, cancellationToken) => handler(cancellationToken);
			_disposeHandler = disposeHandler ?? (() => Task.CompletedTask);
		}

		internal DelegateCodexAccountLogin(
			Func<Guid, CancellationToken, Task<CodexAccountLoginResult>> handler,
			Func<Task>? disposeHandler = null)
		{
			ArgumentNullException.ThrowIfNull(handler);
			_handler = (workspaceId, cancellationToken) =>
				workspaceId is Guid value
					? handler(value, cancellationToken)
					: throw new InvalidOperationException(
						"This fake login requires a workspace ID.");
			_disposeHandler = disposeHandler ?? (() => Task.CompletedTask);
		}

		public Task<ICodexAccountLoginSession> StartAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			LastWorkspaceId = null;
			return Task.FromResult<ICodexAccountLoginSession>(
				new Session(
					token => _handler(null, token),
					_disposeHandler,
					CreateWorkspaceConfigurationTransaction));
		}

		public Task<ICodexAccountLoginSession> StartAsync(
			Guid accountId,
			Guid workspaceId,
			CancellationToken cancellationToken = default)
		{
			LastWorkspaceId = workspaceId;
			return Task.FromResult<ICodexAccountLoginSession>(
				new Session(
					token => _handler(workspaceId, token),
					_disposeHandler,
					CreateWorkspaceConfigurationTransaction));
		}

		private ICodexWorkspaceConfigurationTransaction
			CreateWorkspaceConfigurationTransaction()
		{
			return new WorkspaceConfigurationTransaction(
				() => WorkspaceConfigurationCommitCount++,
				() => WorkspaceConfigurationRollbackCount++);
		}
	}

	private sealed class DelegateGrokAccountLogin : IGrokAccountLogin
	{
		private readonly Func<CancellationToken, Task> _handler;

		internal DelegateGrokAccountLogin(
			Func<CancellationToken, Task> handler)
		{
			_handler = handler;
		}

		public Task LoginAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return _handler(cancellationToken);
		}
	}

	private sealed class DelegateGrokUsagePoller : IGrokUsagePoller
	{
		private readonly Func<Guid, CancellationToken, Task<GrokUsagePollResult>>
			_handler;

		internal DelegateGrokUsagePoller(
			Func<CancellationToken, Task<GrokUsagePollResult>> handler)
			: this((_, cancellationToken) => handler(cancellationToken))
		{
		}

		internal DelegateGrokUsagePoller(
			Func<Guid, CancellationToken, Task<GrokUsagePollResult>> handler)
		{
			_handler = handler;
		}

		public Task<GrokUsagePollResult> PollAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			return _handler(accountId, cancellationToken);
		}

		public Task<GrokUsagePollResult> PollBoundAsync(
			Guid accountId,
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			return _handler(accountId, cancellationToken);
		}
	}

	private sealed class FakeGrokAccountBindingStore : IGrokAccountBindingStore
	{
		private readonly Dictionary<Guid, GrokAccountBinding> _bindings = new();
		private readonly object _lock = new();
		private readonly List<string>? _operations;

		internal Func<GrokAccountBinding, CancellationToken, Task>? SaveHandler
		{
			get;
			set;
		}

		internal Func<Guid, CancellationToken, Task>? DeleteHandler
		{
			get;
			set;
		}

		internal GrokAccountBinding? SavedBinding { get; private set; }

		internal FakeGrokAccountBindingStore(List<string>? operations = null)
		{
			_operations = operations;
		}

		public Task<IReadOnlyList<GrokAccountBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			lock (_lock)
			{
				return Task.FromResult<IReadOnlyList<GrokAccountBinding>>(
					_bindings.Values.ToArray());
			}
		}

		public Task<GrokAccountBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			lock (_lock)
			{
				_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
				return Task.FromResult(binding);
			}
		}

		public async Task SaveAsync(
			GrokAccountBinding binding,
			CancellationToken cancellationToken = default)
		{
			_operations?.Add("binding:save");

			if (SaveHandler is not null)
			{
				await SaveHandler(binding, cancellationToken);
			}

			lock (_lock)
			{
				SavedBinding = binding;
				_bindings[binding.AccountId] = binding;
			}
		}

		public async Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			if (DeleteHandler is not null)
			{
				await DeleteHandler(accountId, cancellationToken);
			}

			lock (_lock)
			{
				_bindings.Remove(accountId);
				if (SavedBinding?.AccountId == accountId)
				{
					SavedBinding = null;
				}
			}
		}

		internal void Seed(GrokAccountBinding binding)
		{
			lock (_lock)
			{
				_bindings[binding.AccountId] = binding;
			}
		}

		internal GrokAccountBinding? GetBinding(Guid accountId)
		{
			lock (_lock)
			{
				_bindings.TryGetValue(accountId, out GrokAccountBinding? binding);
				return binding;
			}
		}
	}

	private sealed class FakeGrokConnectionPendingStore :
		IGrokConnectionPendingStore
	{
		private readonly object _lock = new();
		private readonly List<string>? _operations;
		private readonly Dictionary<Guid, GrokConnectionPendingWork> _works = new();

		internal bool WasRemoved { get; private set; }

		internal Func<Guid, Guid, CancellationToken, Task<bool>>?
			TryRemoveHandler { get; set; }

		internal GrokConnectionBeginResult BeginResult { get; set; } =
			GrokConnectionBeginResult.Started;

		internal Guid LastAttemptId { get; private set; }

		internal GrokConnectionPendingWork? Work
		{
			get
			{
				lock (_lock)
				{
					return _works.Values.SingleOrDefault();
				}
			}
		}

		internal FakeGrokConnectionPendingStore(List<string>? operations = null)
		{
			_operations = operations;
		}

		public Task<IReadOnlyList<GrokConnectionPendingWork>> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			lock (_lock)
			{
				IReadOnlyList<GrokConnectionPendingWork> result =
					_works.Values.ToArray();
				return Task.FromResult(result);
			}
		}

		public Task<GrokConnectionBeginResult> BeginAsync(
			Guid accountId,
			Guid attemptId,
			DateTimeOffset startedAtUtc,
			CancellationToken cancellationToken = default)
		{
			lock (_lock)
			{
				if (BeginResult ==
					GrokConnectionBeginResult.BlockedByExistingRecoverableWork)
				{
					return Task.FromResult(BeginResult);
				}

				_operations?.Add("journal:LoginStarted");
				LastAttemptId = attemptId;
				_works[accountId] = new GrokConnectionPendingWork(
					accountId,
					attemptId,
					startedAtUtc,
					startedAtUtc,
					GrokConnectionPendingStage.LoginStarted,
					PublicBindingId: null);
				return Task.FromResult(BeginResult);
			}
		}

		public Task<bool> TryAdvanceAsync(
			Guid accountId,
			Guid attemptId,
			GrokConnectionPendingStage stage,
			Guid? publicBindingId,
			DateTimeOffset updatedAtUtc,
			CancellationToken cancellationToken = default)
		{
			lock (_lock)
			{
				if (!_works.TryGetValue(
						accountId,
						out GrokConnectionPendingWork? work) ||
					(work.AttemptId != attemptId))
				{
					return Task.FromResult(false);
				}

				_operations?.Add($"journal:{stage}");
				_works[accountId] = work with
				{
					Stage = stage,
					PublicBindingId = publicBindingId,
					UpdatedAtUtc = updatedAtUtc
				};
				return Task.FromResult(true);
			}
		}

		public Task<bool> TryRemoveAsync(
			Guid accountId,
			Guid attemptId,
			CancellationToken cancellationToken = default)
		{
			if (TryRemoveHandler is not null)
			{
				return TryRemoveHandler(
					accountId,
					attemptId,
					cancellationToken);
			}

			lock (_lock)
			{
				bool didRemove = _works.TryGetValue(
					accountId,
					out GrokConnectionPendingWork? work) &&
					(work.AttemptId == attemptId) &&
					_works.Remove(accountId);
				if (didRemove)
				{
					_operations?.Add("journal:remove");
					WasRemoved = true;
				}

				return Task.FromResult(didRemove);
			}
		}

		internal GrokConnectionPendingWork? GetWork(Guid accountId)
		{
			lock (_lock)
			{
				_works.TryGetValue(
					accountId,
					out GrokConnectionPendingWork? work);
				return work;
			}
		}
	}

	private sealed record GrokBindingPersistedConflictScenario(
		AccountUsageViewModel TargetAccount,
		DashboardViewModel ViewModel,
		FakeGrokAccountBindingStore BindingStore,
		FakeGrokConnectionPendingStore PendingStore,
		GrokAccountBinding CreatedBinding,
		IReadOnlyList<string> Statuses,
		IReadOnlyList<string> Diagnostics,
		int BindingSaveCount);

	private static async Task<GrokBindingPersistedConflictScenario>
		RunGrokBindingPersistedConflictScenarioAsync(
		bool shouldThrowWhenRemovingJournal,
		bool shouldThrowWhenRestoringBinding)
	{
		Guid ownerAccountId = Guid.NewGuid();
		Guid targetAccountId = Guid.NewGuid();
		GrokAccountBinding oldTargetBinding = GrokAccountBinding.Create(
			targetAccountId,
			"user",
			"old-principal");
		AccountProfile ownerProfile = new(
			ownerAccountId,
			ProviderKind.Grok,
			"Grok owner");
		AccountProfile targetProfile = new(
			targetAccountId,
			ProviderKind.Grok,
			"Grok target",
			ProviderAccountIdentity:
				GrokAccountBinding.CreatePublicBindingIdentity(
					oldTargetBinding.PublicBindingId));
		FakeAccountProfileStore profileStore = new(ownerProfile, targetProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel ownerAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == ownerAccountId);
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetAccountId);
		GrokAccountBinding? createdBinding = null;
		int bindingSaveCount = 0;
		FakeGrokAccountBindingStore bindingStore = new();
		bindingStore.SaveHandler = (binding, _) =>
		{
			bindingSaveCount++;
			if (bindingSaveCount == 1)
			{
				createdBinding = binding;
				string sharedIdentity =
					GrokAccountBinding.CreatePublicBindingIdentity(
						binding.PublicBindingId);
				ownerAccount.BeginProviderAccountChange();
				ownerAccount.SeedProviderAccountIdentity(sharedIdentity);
				Assert.True(ownerAccount.TryBeginProviderAccountChangeCommit());
				ownerAccount.EndProviderAccountChange();
				return Task.CompletedTask;
			}

			if (shouldThrowWhenRestoringBinding)
			{
				throw new IOException("synthetic binding restore failure");
			}

			return Task.CompletedTask;
		};
		FakeGrokConnectionPendingStore pendingStore = new()
		{
			TryRemoveHandler = (_, _, _) =>
				shouldThrowWhenRemovingJournal
					? Task.FromException<bool>(
						new IOException("synthetic journal removal failure"))
					: Task.FromResult(false)
		};
		List<string> statuses = new();
		List<string> diagnostics = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ => Task.FromResult(
					CreateGrokPollResult("new-principal"))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30),
				(operation, summary, _) =>
					diagnostics.Add($"{operation}:{summary}"));

		await coordinator.ConnectGrokAsync(
			targetAccount,
			viewModel,
			statuses.Add);

		return new GrokBindingPersistedConflictScenario(
			targetAccount,
			viewModel,
			bindingStore,
			pendingStore,
			Assert.IsType<GrokAccountBinding>(createdBinding),
			statuses,
			diagnostics,
			bindingSaveCount);
	}

	private static readonly TimeSpan AsyncWatchdogTimeout =
		TimeSpan.FromSeconds(15);

	[Theory]
	[InlineData(
		UsageRecoveryAction.UpdateApplication,
		"Antigravity 帳號已連接；AI Usage 尚未支援目前的用量格式。請更新 AI Usage。既有連接通常可沿用；若卡片後續要求，請重新確認連接。")]
	[InlineData(
		UsageRecoveryAction.InstallOrUpdate,
		"Antigravity 帳號已連接；找不到支援的 Antigravity CLI。請確認安裝與版本。既有連接通常可沿用；若卡片後續要求，請重新確認連接。")]
	public void GetAntigravityConnectionCompletionMessage_WhenConnectionMayNeedRevalidation_UsesConditionalGuidance(
		UsageRecoveryAction recoveryAction,
		string expectedMessage)
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: recoveryAction));

		string message =
			AccountConnectionCoordinator.GetAntigravityConnectionCompletionMessage(
				account,
				hasFreshUsage: false);

		Assert.Equal(expectedMessage, message);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCompletedWithoutFreshMetadata_FallsBackToLocalSessionLabel()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = CreateAntigravityProfile(string.Empty) with
		{
			ProviderAccountIdentity = LocalSessionIdentity
		};
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric(
						"agy.test",
						"AGY 測試用量",
						25,
						"已使用 25%")
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: LocalSessionIdentity))
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: null,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.test",
					"AGY 測試用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: LocalSessionIdentity));
		Assert.True(account.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt,
			TimeSpan.FromMinutes(1),
			isStale: false));
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint));

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Empty(bindingStore.DeletedAccountIds);
		Assert.Equal("目前登入的 Antigravity 帳號", account.AccountDisplayText);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCancelled_DoesNotClearVerifiedDisplayBinding()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = CreateAntigravityProfile(string.Empty) with
		{
			ProviderAccountIdentity = LocalSessionIdentity
		};
		FakeAntigravityVerifiedAccountBindingStore bindingStore = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: null,
			antigravityVerifiedAccountBindingStore: bindingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.test",
					"AGY 測試用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: LocalSessionIdentity));
		Assert.True(account.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt,
			TimeSpan.FromMinutes(1),
			isStale: false));
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Empty(bindingStore.DeletedAccountIds);
		Assert.Equal("person@example.com", account.AccountDisplayText);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WritesIntentBeforeLauncherAndRemovesItWhenCancelled()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile profile = CreateAntigravityProfile("Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore,
			attemptStateStore);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		bool wasIntentDurableBeforeLauncher = false;
		FakeAntigravityAccountSetupLauncher launcher = new(async _ =>
		{
			AntigravityConnectionPendingWork intent =
				Assert.Single(await pendingStore.LoadAsync());
			wasIntentDurableBeforeLauncher = intent.IsSetupPending;
			return AntigravityAccountSetupOutcome.Cancelled;
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.True(wasIntentDurableBeforeLauncher);
		Assert.Empty(await pendingStore.LoadAsync());
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.True(account.CanConnectProviderAccount);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCompletionIsUnknownBeforeApproval_DiscardsOnRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile profile = CreateAntigravityProfile("Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore,
			attemptStateStore);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			async (attemptId, cancellationToken) =>
			{
				AntigravitySetupProcessIdentity process =
					AntigravitySetupProcessIdentity.CaptureCurrent();
				await attemptStateStore.BeginLaunchAsync(
					attemptId,
					process,
					DateTimeOffset.UtcNow,
					cancellationToken);
				await attemptStateStore.MarkActiveAsync(
					attemptId,
					process,
					cancellationToken);
				return AntigravityAccountSetupOutcome.CompletionUnknown;
			});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		AntigravityConnectionPendingWork pending =
			Assert.Single(await pendingStore.LoadAsync());
		Assert.True(pending.IsSetupPending);
		Assert.Equal(launcher.LastSetupAttemptId, pending.SetupAttemptId);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Empty(profileStore.SavedSnapshots);

		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new()
		{
			RefreshHandler = (request, _) => Task.FromResult(new UsageSnapshot(
				request,
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
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await restartedViewModel.InitializeAsync();

		Assert.Empty(restartedRefreshCoordinator.RefreshRequests);
		Assert.Empty(await pendingStore.LoadAsync());
		Assert.Null(
			Assert.Single(restartedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
		Assert.Null(await receiptStore.ReadAsync(launcher.LastSetupAttemptId));
		Assert.Null(
			await attemptStateStore.ReadAsync(launcher.LastSetupAttemptId));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenInProcessApprovalReceiptIsMissing_CompletesWithoutRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		const string TargetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = CreateAntigravityProfile("Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore,
			attemptStateStore);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (request, _) => Task.FromResult(new UsageSnapshot(
				request,
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
		FakeAntigravityAccountSetupLauncher launcher = new(
			async (attemptId, cancellationToken) =>
			{
				AntigravitySetupProcessIdentity process =
					AntigravitySetupProcessIdentity.CaptureCurrent();
				await attemptStateStore.BeginLaunchAsync(
					attemptId,
					process,
					DateTimeOffset.UtcNow,
					cancellationToken);
				await attemptStateStore.MarkActiveAsync(
					attemptId,
					process,
					cancellationToken);
				await attemptStateStore.MarkApprovalRequestedAsync(
					attemptId,
					AntigravityMachineSetupSourceKind.OfficialPrint,
					TargetIdentity,
					cancellationToken);
				return AntigravityAccountSetupOutcome.CompletionUnknown;
			});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.True(Assert.Single(await pendingStore.LoadAsync()).IsSetupPending);
		Assert.Null(await receiptStore.ReadAsync(launcher.LastSetupAttemptId));
		Assert.True(await viewModel.RetryPendingAntigravityConnectionsNowAsync());
		Assert.Equal(TargetIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Empty(await pendingStore.LoadAsync());
		Assert.Null(
			await attemptStateStore.ReadAsync(launcher.LastSetupAttemptId));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCancelledAfterReceipt_RestartContinuesApprovedAttempt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile profile = CreateAntigravityProfile("Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore,
			attemptStateStore);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			async (attemptId, cancellationToken) =>
			{
				await receiptStore.WriteAsync(
					attemptId,
					AntigravityMachineSetupSourceKind.OfficialPrint,
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
					cancellationToken);
				return AntigravityAccountSetupOutcome.Cancelled;
			});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.True(Assert.Single(await pendingStore.LoadAsync()).IsSetupPending);
		Assert.False(account.CanConnectProviderAccount);

		FakeUsageRefreshCoordinator restartedRefreshCoordinator = new()
		{
			RefreshHandler = (request, _) => Task.FromResult(new UsageSnapshot(
				request,
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
		DashboardViewModel restartedViewModel = new(
			profileStore,
			restartedRefreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);

		await restartedViewModel.InitializeAsync();
		Assert.Empty(restartedRefreshCoordinator.RefreshRequests);
		Assert.True(
			await restartedViewModel
				.RetryPendingAntigravityConnectionsNowAsync());

		Assert.Equal(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			Assert.Single(restartedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
		Assert.Empty(await pendingStore.LoadAsync());
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCompleted_QuiescesRefreshesAndCompletes()
	{
		const string AccountIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		const string OriginalIdentity = "old-agy@example.com";
		AccountProfile profile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = OriginalIdentity
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
				Task.FromResult(CreateReadySnapshot(
					account,
					AccountIdentity) with
					{
						SourceTrust = SourceTrust.OfficialExperimental
					})
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			usageSnapshotStore: null);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(
			new[] { profile.Id, profile.Id, profile.Id },
			refreshCoordinator.InvalidatedAccountIds);
		Assert.All(
			refreshCoordinator.RefreshRequests,
			refreshRequest => Assert.Equal(profile.Id, refreshRequest.Id));
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Equal(AccountIdentity, account.ProviderAccountIdentity);
		Assert.Equal(AccountIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			AccountIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);
		Assert.False(coordinator.IsAntigravitySetupInProgress);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"已連接",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WithDurableProof_CompletesAndCleansAttemptArtifacts(
		)
	{
		AntigravityMachineSetupSourceKind sourceKind =
			AntigravityMachineSetupSourceKind.OfficialPrint;
		SourceTrust sourceTrust = SourceTrust.OfficialExperimental;
		using TemporaryDirectory temporaryDirectory = new();
		string targetIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AntigravityAccountSetupOutcome outcome =
			AntigravityAccountSetupOutcome.CompletedOfficialPrint;
		AccountProfile profile = CreateAntigravityProfile("Antigravity");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		AntigravitySetupAttemptStateStore attemptStateStore = new(Path.Combine(
			temporaryDirectory.Path,
			"states"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore,
			attemptStateStore);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) => Task.FromResult(new UsageSnapshot(
				account,
				new[]
				{
					new UsageMetric("agy.test", "AGY test", 25, "used 25%")
				},
				sourceTrust,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: targetIdentity))
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
		FakeAntigravityAccountSetupLauncher launcher = new(
			async (attemptId, cancellationToken) =>
			{
				AntigravitySetupProcessIdentity process =
					AntigravitySetupProcessIdentity.CaptureCurrent();
				await attemptStateStore.BeginLaunchAsync(
					attemptId,
					process,
					DateTimeOffset.UtcNow,
					cancellationToken);
				await attemptStateStore.MarkActiveAsync(
					attemptId,
					process,
					cancellationToken);
				await attemptStateStore.MarkApprovalRequestedAsync(
					attemptId,
					sourceKind,
					targetIdentity,
					cancellationToken);
				await receiptStore.WriteAsync(
					attemptId,
					sourceKind,
					targetIdentity,
					cancellationToken);
				return outcome;
			});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Equal(targetIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Empty(await pendingStore.LoadAsync());
		Assert.Null(await receiptStore.ReadAsync(launcher.LastSetupAttemptId));
		Assert.Null(
			await attemptStateStore.ReadAsync(launcher.LastSetupAttemptId));
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenPostSetupRefreshIsStale_DoesNotReportFreshUsage()
	{
		const string AccountIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = AccountIdentity
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				UsageSnapshot ready = CreateReadySnapshot(
					account,
					AccountIdentity) with
					{
						SourceTrust = SourceTrust.OfficialExperimental
					};
				return Task.FromResult(ready with
				{
					Status = SnapshotStatus.Stale,
					Error = "Synthetic stale fallback.",
					RecoveryAction = UsageRecoveryAction.RevalidateUsage
				});
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint));
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(SnapshotStatus.Stale, account.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.RevalidateUsage, account.RecoveryAction);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"重新檢查 Antigravity 用量",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(
				"並已重新讀取用量",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenPostSetupRefreshWasNotApplied_DoesNotReportFreshUsage()
	{
		const string AccountIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = CreateAntigravityProfile("Antigravity") with
		{
			ProviderAccountIdentity = AccountIdentity
		};
		UsageSnapshot previousSnapshot = CreateReadySnapshot(
			profile,
			AccountIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (_, _) => throw new InvalidOperationException(
				"停止刷新後不應執行 Antigravity 用量讀取。")
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(previousSnapshot);
		UsageSnapshot previousAppliedSnapshot = Assert.IsType<UsageSnapshot>(
			account.CurrentSnapshot);
		viewModel.StopRefreshing();
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint));
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.NotSame(previousAppliedSnapshot, account.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, account.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.False(account.CanExecuteRecoveryAction);
		Assert.True(account.CanConnectProviderAccount);
		Assert.Equal(AccountIdentity, account.Profile.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"暫時無法讀取用量",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(
				"並已重新讀取用量",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenSameEmailIsRecapturedInsideThrottleWindow_KeepsEmailAssociated()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset setupCapturedAt = new(
			2026,
			8,
			13,
			0,
			0,
			0,
			TimeSpan.Zero);
		FixedTimeProvider timeProvider = new(
			setupCapturedAt + TimeSpan.FromSeconds(10));
		TrackingAntigravityReportedAccountSource reportedAccountSource = new();
		AccountProfile profile = CreateAntigravityProfile(string.Empty) with
		{
			ProviderAccountIdentity = LocalSessionIdentity
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				reportedAccountSource.SignalFreshStatusLineInvocation();
				timeProvider.Advance(TimeSpan.FromSeconds(5));
				DateTimeOffset observedAt = timeProvider.GetUtcNow();
				return Task.FromResult(new UsageSnapshot(
					account,
					new[]
					{
						new UsageMetric(
							"agy.test",
							"AGY 測試用量",
							25,
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
			timeProvider: timeProvider);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(_ =>
		{
			reportedAccountSource.ReportedAccount =
				new AntigravityReportedAccount(
					"person@example.com",
					setupCapturedAt);
			return Task.FromResult(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Equal("Antigravity · person@example.com", account.AccountName);
		Assert.Equal(
			"person@example.com",
			account.AccountDisplayText);
		Assert.Equal(
			setupCapturedAt,
			reportedAccountSource.ReportedAccount?.CapturedAtUtc);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenBindingSaveFails_AbortsToPersistedIdentity()
	{
		const string OriginalIdentity = "old-agy@example.com";
		const string NewIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = OriginalIdentity
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
				Task.FromResult(CreateReadySnapshot(
					account,
					NewIdentity) with
					{
						SourceTrust = SourceTrust.OfficialExperimental
					})
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint));
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Equal(OriginalIdentity, account.ProviderAccountIdentity);
		Assert.Equal(OriginalIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Error, account.StatusKind);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"連接尚未套用",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));
		Assert.Equal(
			NewIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenImportedOfficialConnectionRefreshFails_KeepsDurableBindingAndCanRetry()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile importedProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = "portable-account@example.com"
		};
		FakeAccountProfileStore profileStore = new();
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedProfile],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);

		profileStore.SavedSnapshots.Clear();
		bool identityWasDurablySaved = false;
		profileStore.SaveHandler = (profiles, _) =>
		{
			identityWasDurablySaved = string.Equals(
				Assert.Single(profiles).ProviderAccountIdentity,
				LocalSessionIdentity,
				StringComparison.Ordinal);
			return Task.CompletedTask;
		};
		int refreshCount = 0;
		refreshCoordinator.RefreshHandler = (request, _) =>
		{
			Assert.True(identityWasDurablySaved);
			Assert.Equal(
				LocalSessionIdentity,
				request.ProviderAccountIdentity);
			refreshCount++;

			return Task.FromResult(refreshCount == 1
				? new UsageSnapshot(
					request,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.Error,
					DateTimeOffset.UtcNow,
					Error: "測試用 AGY 重讀失敗。",
					RecoveryAction: UsageRecoveryAction.Retry)
				: CreateReadySnapshot(request, LocalSessionIdentity));
		};
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		List<(string Operation, string Summary)> diagnostics = new();
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher, diagnostics: diagnostics);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(1, refreshCount);
		Assert.Equal(
			LocalSessionIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(account.IsProviderAccountChangeCommitInProgress);
		Assert.Equal("重新連接 Antigravity 帳號", account.AntigravityAccountActionText);
		Assert.True(account.CanRefreshUsage);
		Assert.Equal(SnapshotStatus.Error, account.CurrentSnapshot?.Status);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.Contains(
			statuses,
			status =>
				status.Contains("已連接", StringComparison.Ordinal) &&
				status.Contains("稍後會自動再試", StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(
				"尚未安全綁定",
				StringComparison.Ordinal));
		Assert.Equal(
			LocalSessionIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);

		await viewModel.RefreshUsageAsync();

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(2, refreshCount);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Single(snapshotStore.SavedSnapshots);
		Assert.Contains(
			diagnostics,
			entry => entry == (
				"antigravity-account-connection",
				"stage=setup-complete;outcome=CompletedOfficialPrint"));
		Assert.Contains(
			diagnostics,
			entry => entry == (
				"antigravity-account-connection",
				"stage=durable-persist;source=OfficialPrint;result=succeeded"));
		Assert.Contains(
			diagnostics,
			entry => entry == (
				"antigravity-account-connection",
				"stage=durable-complete;source=OfficialPrint;refresh=automatic-retry"));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenOfficialCacheDeleteFailsTransiently_RetriesWithoutRelaunch()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile importedProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = "portable-account@example.com"
		};
		FakeAccountProfileStore profileStore = new();
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (request, _) => Task.FromResult(
				CreateReadySnapshot(request, LocalSessionIdentity))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedProfile],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		snapshotStore.DeleteFailuresRemaining = 2;
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.True(snapshotStore.DeletedAccountIds.Count >= 3);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			LocalSessionIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"並已重新讀取用量",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(
				"無法保存",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenOfficialCacheDeleteKeepsFailing_QueuesAutomaticContinuation()
	{
		AccountProfile importedProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = "portable-account@example.com"
		};
		FakeAccountProfileStore profileStore = new();
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedProfile],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int deleteCountBeforeSetup = snapshotStore.DeletedAccountIds.Count;
		snapshotStore.DeleteFailuresRemaining = 3;
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(
			3,
			snapshotStore.DeletedAccountIds.Count - deleteCountBeforeSetup);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Contains(
			statuses,
			status =>
				status.Contains("切換前的上次用量資料仍待清除", StringComparison.Ordinal) &&
				status.Contains("不需要重新連接", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenOfficialRefreshRequiresManualRevalidation_DoesNotPromiseAutomaticRetry()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile importedProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = "portable-account@example.com"
		};
		FakeAccountProfileStore profileStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (request, _) => Task.FromResult(new UsageSnapshot(
				request,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				DateTimeOffset.UtcNow,
				Error: "AGY 用量安全檢查已暫停。",
				ProviderAccountIdentity: LocalSessionIdentity,
				RecoveryAction: UsageRecoveryAction.RevalidateUsage))
		};
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedProfile],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		List<(string Operation, string Summary)> diagnostics = new();
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher, diagnostics: diagnostics);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.Equal(UsageRecoveryAction.RevalidateUsage, account.RecoveryAction);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"重新檢查 Antigravity 用量",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("自動再試", StringComparison.Ordinal));
		Assert.Contains(
			diagnostics,
			entry => entry == (
				"antigravity-account-connection",
				"stage=durable-complete;source=OfficialPrint;refresh=manual-recovery"));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenImportedOfficialBindingSaveFails_QueuesAutomaticContinuation()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile importedProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity = "portable-account@example.com"
		};
		FakeAccountProfileStore profileStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		await viewModel.ReplacePortableSettingsAsync(
			new PortableSettingsSnapshot(
				[importedProfile],
				UsageSortMode.Manual,
				UsageDisplayMode.Used));
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);

		profileStore.SavedSnapshots.Clear();
		profileStore.ShouldFailSave = true;
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		List<(string Operation, string Summary)> diagnostics = new();
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher, diagnostics: diagnostics);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(
			LocalSessionIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(account.IsProviderAccountChangeCommitInProgress);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.ShowAntigravityDefaultConnectionAction);
		Assert.Equal("連接 Antigravity 帳號", account.AntigravityAccountActionText);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status =>
				status.Contains("連接資料仍待儲存", StringComparison.Ordinal) &&
				status.Contains("不需要重新連接", StringComparison.Ordinal));
		Assert.Contains(
			"重新啟動後也會繼續",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			diagnostics,
			entry => entry == (
				"antigravity-account-connection",
				"stage=durable-persist;source=OfficialPrint;result=failed"));
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenOfficialRefreshIsStopped_ClearsOldUsageCacheAndDisplayIdentity()
	{
		const string LocalSessionIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = CreateAntigravityProfile(string.Empty) with
		{
			ProviderAccountIdentity = LocalSessionIdentity
		};
		UsageSnapshot cachedSnapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.old",
					"切換前的 AGY 用量",
					75,
					"已使用 75%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity: LocalSessionIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = cachedSnapshot
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (_, _) => throw new InvalidOperationException(
				"停止刷新後不應執行 AGY 用量讀取。")
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(cachedSnapshot);
		Assert.True(account.TrySetAntigravityReportedAccountEmail(
			"old-account@example.com",
			observedAt,
			TimeSpan.Zero,
			isStale: false));
		Assert.Equal("old-account@example.com", account.AccountDisplayText);
		Assert.True(await viewModel.StopAndDrainRefreshingAsync(
			TimeSpan.FromSeconds(1)));
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		List<string> statuses = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			statuses.Add);

		Assert.Equal(1, launcher.RunCount);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Equal(LocalSessionIdentity, account.ProviderAccountIdentity);
		Assert.Equal(
			LocalSessionIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, account.CurrentSnapshot?.Status);
		Assert.Empty(Assert.IsType<UsageSnapshot>(account.CurrentSnapshot).Metrics);
		Assert.False(account.HasConfirmedProviderAccountBinding);
		Assert.StartsWith(
			"目前登入的 Antigravity 帳號",
			account.AccountDisplayText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"old-account@example.com",
			account.AccountDisplayText,
			StringComparison.Ordinal);
		Assert.Equal("重新連接 Antigravity 帳號", account.AntigravityAccountActionText);
		Assert.Contains(profile.Id, snapshotStore.DeletedAccountIds);
		Assert.Null(snapshotStore.SnapshotToLoad);
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(
				"並已重新讀取",
				StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains(
				"稍後會自動再試",
				StringComparison.Ordinal));

		DashboardViewModel reloadedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await reloadedViewModel.InitializeAsync();
		Assert.Null(
			Assert.Single(reloadedViewModel.Accounts).CurrentSnapshot);
	}

	[Theory]
	[InlineData("Cancelled", 0)]
	[InlineData("Failed", 0)]
	public async Task RunAntigravitySetupAsync_WhenNotCompleted_AbortsWithoutRefresh(
		string outcomeName,
		int expectedNoticeCount)
	{
		AntigravityAccountSetupOutcome outcome =
			Enum.Parse<AntigravityAccountSetupOutcome>(outcomeName);
		const string OriginalIdentity = "original-agy@example.com";
		AccountProfile profile = CreateAntigravityProfile("AGY");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.SeedProviderAccountIdentity(OriginalIdentity);
		FakeAntigravityAccountSetupLauncher launcher = new(outcome);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		List<string> statuses = new();
		List<string> notices = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			reportStatus: statuses.Add,
			reportNotice: (message, _, _) => notices.Add(message));

		Assert.Equal(1, launcher.RunCount);
		Assert.Equal(
			new[] { profile.Id },
			refreshCoordinator.InvalidatedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Equal(OriginalIdentity, account.ProviderAccountIdentity);
		Assert.Equal(expectedNoticeCount, notices.Count);

		if (outcome == AntigravityAccountSetupOutcome.Failed)
		{
			Assert.Contains(
				statuses,
				status =>
					status.Contains(
						"具體原因",
						StringComparison.Ordinal) &&
					status.Contains(
						"已保留原本顯示的用量",
						StringComparison.Ordinal));
			Assert.Empty(notices);
		}

		Assert.False(coordinator.IsAntigravitySetupInProgress);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCancelledAfterPriorCleanup_NextRefreshCleansAgain()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		TrackingAntigravityReportedAccountSource reportedAccountSource = new();
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource);
		await viewModel.InitializeAsync();
		Assert.Equal(0, reportedAccountSource.RemoveCount);
		await viewModel.RestoreDeferredStartupDisplayStateAsync();
		Assert.Equal(1, reportedAccountSource.RemoveCount);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.Cancelled);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Equal(1, launcher.RunCount);
		Assert.False(account.IsProviderAccountChangeInProgress);
		await viewModel.RefreshUsageAsync();
		Assert.Equal(2, reportedAccountSource.RemoveCount);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenQuiesceTimesOut_DoesNotLaunchSetup()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity =
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity
		};
		TaskCompletionSource refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = async (account, _) =>
			{
				refreshStarted.TrySetResult();
				await allowRefreshToFinish.Task;
				return CreateReadySnapshot(account);
			}
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await refreshStarted.Task;
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher, TimeSpan.Zero);
		List<string> notices = new();

		await coordinator.RunAntigravitySetupAsync(
			account,
			viewModel,
			reportNotice: (message, _, _) => notices.Add(message));

		Assert.Equal(0, launcher.RunCount);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(
			new[] { profile.Id },
			refreshCoordinator.InvalidatedAccountIds);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Single(notices);
		Assert.False(coordinator.IsAntigravitySetupInProgress);

		allowRefreshToFinish.TrySetResult();
		await activeRefresh;
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenCompletedAndUnrelatedRefreshRemainsActive_RefreshesTargetAndCompletes()
	{
		const string ConnectedIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile targetProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity =
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity
		};
		AccountProfile unrelatedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: "unrelated@example.com");
		TaskCompletionSource targetRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource targetPostCommitRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource unrelatedRefreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowTargetRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowUnrelatedRefreshToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int targetRefreshCount = 0;
		int unrelatedRefreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = async (account, _) =>
			{
				if (account.Id == targetProfile.Id)
				{
					int refreshCount = Interlocked.Increment(
						ref targetRefreshCount);
					if (refreshCount == 1)
					{
						targetRefreshStarted.TrySetResult();
						await allowTargetRefreshToFinish.Task;
					}
					else if (refreshCount == 2)
					{
						targetPostCommitRefreshStarted.TrySetResult();
					}

					string identity = refreshCount == 1
						? AntigravityOfficialPrintUsageClient.LocalSessionIdentity
						: ConnectedIdentity;
					return CreateReadySnapshot(account, identity) with
					{
						SourceTrust = SourceTrust.OfficialExperimental
					};
				}

				Interlocked.Increment(ref unrelatedRefreshCount);
				unrelatedRefreshStarted.TrySetResult();
				await allowUnrelatedRefreshToFinish.Task;
				return CreateReadySnapshot(
					account,
					account.ProviderAccountIdentity);
			}
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			targetProfile,
			unrelatedProfile);
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await Task.WhenAll(
			targetRefreshStarted.Task,
			unrelatedRefreshStarted.Task).WaitAsync(AsyncWatchdogTimeout);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator = CreateCoordinator(launcher);
		Task setup = coordinator.RunAntigravitySetupAsync(
			targetAccount,
			viewModel);

		try
		{
			allowTargetRefreshToFinish.TrySetResult();
			await targetPostCommitRefreshStarted.Task.WaitAsync(
				AsyncWatchdogTimeout);
			await setup.WaitAsync(AsyncWatchdogTimeout);

			Assert.Equal(1, launcher.RunCount);
			Assert.False(activeRefresh.IsCompleted);
			Assert.True(viewModel.IsRefreshing);
			Assert.Equal(2, Volatile.Read(ref targetRefreshCount));
			Assert.Equal(1, Volatile.Read(ref unrelatedRefreshCount));
			Assert.False(targetAccount.IsProviderAccountChangeInProgress);
			Assert.Equal(ConnectedIdentity, targetAccount.ProviderAccountIdentity);
			Assert.Equal(SnapshotStatus.Ready, targetAccount.CurrentSnapshot?.Status);
		}
		finally
		{
			allowTargetRefreshToFinish.TrySetResult();
			allowUnrelatedRefreshToFinish.TrySetResult();
			await activeRefresh.WaitAsync(AsyncWatchdogTimeout);
		}

		Assert.False(viewModel.IsRefreshing);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenUnrelatedRefreshRemainsActive_LaunchesAfterTargetIsIdle()
	{
		AccountProfile targetProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity =
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity
		};
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
			RefreshHandler = async (account, _) =>
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

				return CreateReadySnapshot(
					account,
					account.ProviderAccountIdentity);
			}
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			targetProfile,
			unrelatedProfile);
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await Task.WhenAll(
			targetRefreshStarted.Task,
			unrelatedRefreshStarted.Task).WaitAsync(AsyncWatchdogTimeout);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.Cancelled);
		using AccountConnectionCoordinator coordinator = CreateCoordinator(launcher);

		Task setup = coordinator.RunAntigravitySetupAsync(
			targetAccount,
			viewModel);

		try
		{
			Assert.Equal(0, launcher.RunCount);

			allowTargetRefreshToFinish.TrySetResult();
			await setup.WaitAsync(AsyncWatchdogTimeout);

			Assert.Equal(1, launcher.RunCount);
			Assert.False(activeRefresh.IsCompleted);
			Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		}
		finally
		{
			allowTargetRefreshToFinish.TrySetResult();
			allowUnrelatedRefreshToFinish.TrySetResult();
			await activeRefresh.WaitAsync(AsyncWatchdogTimeout);
		}
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenRefreshPreludeIsActive_WaitsForTargetProviderOnly()
	{
		AccountProfile targetProfile = CreateAntigravityProfile("AGY") with
		{
			ProviderAccountIdentity =
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity
		};
		AccountProfile unrelatedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: "unrelated@example.com");
		TaskCompletionSource preludeStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowPreludeToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TrackingAntigravityReportedAccountSource reportedAccountSource = new()
		{
			ReadHandler = async cancellationToken =>
			{
				preludeStarted.TrySetResult();
				await allowPreludeToFinish.Task.WaitAsync(cancellationToken);
				return new AntigravityReportedAccountObservation(
					Generation: 1,
					Account: null,
					HasFreshStatusLineInvocation: false,
					Status:
						AntigravityReportedAccountObservationStatus.Available);
			}
		};
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
			RefreshHandler = async (account, _) =>
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

				return CreateReadySnapshot(
					account,
					account.ProviderAccountIdentity);
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(targetProfile, unrelatedProfile),
			refreshCoordinator,
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityReportedAccountSource: reportedAccountSource);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		Task activeRefresh = viewModel.RefreshUsageAsync();
		await preludeStarted.Task.WaitAsync(AsyncWatchdogTimeout);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.Cancelled);
		using AccountConnectionCoordinator coordinator = CreateCoordinator(launcher);
		Task setup = coordinator.RunAntigravitySetupAsync(
			targetAccount,
			viewModel);

		try
		{
			Assert.Equal(0, launcher.RunCount);

			allowPreludeToFinish.TrySetResult();
			await Task.WhenAll(
				targetRefreshStarted.Task,
				unrelatedRefreshStarted.Task).WaitAsync(AsyncWatchdogTimeout);
			Assert.Equal(0, launcher.RunCount);

			allowTargetRefreshToFinish.TrySetResult();
			await setup.WaitAsync(AsyncWatchdogTimeout);

			Assert.Equal(1, launcher.RunCount);
			Assert.False(activeRefresh.IsCompleted);
			Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		}
		finally
		{
			allowPreludeToFinish.TrySetResult();
			allowTargetRefreshToFinish.TrySetResult();
			allowUnrelatedRefreshToFinish.TrySetResult();
			await activeRefresh.WaitAsync(AsyncWatchdogTimeout);
		}
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WhenReentered_LaunchesOnlyOnce()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource setupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<AntigravityAccountSetupOutcome> finishSetup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAntigravityAccountSetupLauncher launcher = new(async token =>
		{
			setupStarted.TrySetResult();
			return await finishSetup.Task.WaitAsync(token);
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		Task firstRun = coordinator.RunAntigravitySetupAsync(
			account,
			viewModel);

		try
		{
			await setupStarted.Task.WaitAsync(AsyncWatchdogTimeout);
			Task secondRun = coordinator.RunAntigravitySetupAsync(
				account,
				viewModel);
			await secondRun.WaitAsync(AsyncWatchdogTimeout);

			Assert.Equal(1, launcher.RunCount);
			Assert.False(firstRun.IsCompleted);
			Assert.True(account.IsProviderAccountChangeInProgress);
		}
		finally
		{
			finishSetup.TrySetResult(AntigravityAccountSetupOutcome.Cancelled);
			await firstRun.WaitAsync(AsyncWatchdogTimeout);
		}

		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAntigravitySetupInProgress);
	}

	[Fact]
	public async Task TryReserveShutdownForUpdate_WhenIdle_PreventsNewAntigravitySetup()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		Assert.True(coordinator.TryReserveShutdownForUpdate());

		await coordinator.RunAntigravitySetupAsync(account, viewModel);

		Assert.Equal(0, launcher.RunCount);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAntigravitySetupInProgress);
	}

	[Fact]
	public void RollbackShutdownForUpdateReservation_RestoresCoordinatorAvailability()
	{
		using AccountConnectionCoordinator coordinator = CreateCoordinator(
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		Assert.True(coordinator.TryReserveShutdownForUpdate());
		Assert.False(coordinator.TryReserveShutdownForUpdate());

		coordinator.RollbackShutdownForUpdateReservation();

		Assert.True(coordinator.TryReserveShutdownForUpdate());
	}

	[Fact]
	public async Task TryReserveShutdownForUpdate_WhenAntigravitySetupIsActive_RejectsReservation()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource setupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<AntigravityAccountSetupOutcome> finishSetup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAntigravityAccountSetupLauncher launcher = new(async token =>
		{
			setupStarted.TrySetResult();
			return await finishSetup.Task.WaitAsync(token);
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		Task setupTask = coordinator.RunAntigravitySetupAsync(
			account,
			viewModel);

		try
		{
			await setupStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(coordinator.TryReserveShutdownForUpdate());
			Assert.True(coordinator.IsAntigravitySetupInProgress);
		}
		finally
		{
			finishSetup.TrySetResult(AntigravityAccountSetupOutcome.Cancelled);
			await setupTask.WaitAsync(AsyncWatchdogTimeout);
		}

		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAntigravitySetupInProgress);
	}

	[Fact]
	public async Task CancelAndDrain_WhenAntigravitySetupIsRunning_WaitsForLauncherCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AccountProfile profile = CreateAntigravityProfile("AGY");
		FakeAccountProfileStore profileStore = new(profile);
		AntigravitySetupApprovalReceiptStore receiptStore = new(Path.Combine(
			temporaryDirectory.Path,
			"receipts"));
		JsonAntigravityConnectionPendingStore pendingStore = new(
			Path.Combine(temporaryDirectory.Path, "agy-pending.json"),
			receiptStore);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			usageSnapshotStore: null,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			antigravityConnectionPendingStore: pendingStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource setupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cleanupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		bool wasCancelled = false;
		FakeAntigravityAccountSetupLauncher launcher = new(async token =>
		{
			setupStarted.TrySetResult();

			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
				return AntigravityAccountSetupOutcome.CompletedOfficialPrint;
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				wasCancelled = true;
				cleanupStarted.TrySetResult();
				await allowCleanup.Task;
				return AntigravityAccountSetupOutcome.CompletionUnknown;
			}
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		Task setupTask = coordinator.RunAntigravitySetupAsync(
			account,
			viewModel);
		Task<bool>? drainTask = null;
		bool wasFullyDrained = false;
		try
		{
			await setupStarted.Task.WaitAsync(AsyncWatchdogTimeout);
			Assert.True(coordinator.IsAntigravitySetupInProgress);

			drainTask = coordinator.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));
			await cleanupStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.True(wasCancelled);
			Assert.False(drainTask.IsCompleted);
		}
		finally
		{
			allowCleanup.TrySetResult();

			if (drainTask is not null)
			{
				wasFullyDrained =
					await drainTask.WaitAsync(AsyncWatchdogTimeout);
				await setupTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.True(wasFullyDrained);

		AntigravityConnectionPendingWork pending =
			Assert.Single(await pendingStore.LoadAsync());
		Assert.True(pending.IsSetupPending);
		Assert.Equal(launcher.LastSetupAttemptId, pending.SetupAttemptId);
		Assert.False(account.CanConnectProviderAccount);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAntigravitySetupInProgress);
	}

	[Fact]
	public async Task CancelAndDrain_WhenLauncherCleanupHangs_ReturnsFalseWithinBound()
	{
		AccountProfile profile = CreateAntigravityProfile("AGY");
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource setupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource finishSetup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAntigravityAccountSetupLauncher launcher = new(async _ =>
		{
			setupStarted.TrySetResult();
			await finishSetup.Task;
			return AntigravityAccountSetupOutcome.Cancelled;
		});
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);
		Task setupTask = coordinator.RunAntigravitySetupAsync(account, viewModel);

		try
		{
			await setupStarted.Task.WaitAsync(AsyncWatchdogTimeout);
			bool wasDrained = await coordinator
				.CancelAndDrainAccountConnectionsAsync(
					TimeSpan.FromMilliseconds(30))
				.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(wasDrained);
			Assert.True(coordinator.IsAntigravitySetupInProgress);
		}
		finally
		{
			finishSetup.TrySetResult();

			if (setupStarted.Task.IsCompletedSuccessfully)
			{
				await setupTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenConsentIsDeclined_DoesNotSaveOrLogin()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult("unused@example.com"));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.No);

		Assert.Equal(0, loginCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.False(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.ConnectAccount)]
	[InlineData(UsageRecoveryAction.ConfirmSubscription)]
	public async Task ConnectClaudeAsync_WhenConsentIsAcceptedAndProbeRequiresLogin_SavesBeforeLogin(
		UsageRecoveryAction recoveryAction)
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.NotConfigured,
				DateTimeOffset.UtcNow,
				RecoveryAction: recoveryAction))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
			Assert.True(
				Assert.Single(
					Assert.Single(profileStore.SavedSnapshots))
					.HasAcceptedClaudeQuotaRisk);
			return Task.FromException<ClaudeAccountLoginResult>(
				new ClaudeAccountLoginException("測試用登入終止。"));
		});
		List<string> notices = new();
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes,
			reportNotice: (message, _, _) => notices.Add(message));

		Assert.Equal(1, loginCount);
		Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.Single(notices);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenConsentIsAcceptedForExistingIdentity_RefreshesWithoutLogin()
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: accountIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				Assert.True(account.HasAcceptedClaudeQuotaRisk);
				return Task.FromResult(
					CreateReadySnapshot(account, accountIdentity));
			}
		};
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes,
			statuses.Add);

		Assert.Equal(0, loginCount);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Contains(
			statuses,
			status => status.Contains("已沿用", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenConsentIsAcceptedWithoutIdentityAndRefreshIsReady_DoesNotLogin()
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				Assert.True(account.HasAcceptedClaudeQuotaRisk);
				Assert.Null(account.ProviderAccountIdentity);
				return Task.FromResult(
					CreateReadySnapshot(account, accountIdentity));
			}
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes,
			statuses.Add);

		Assert.Equal(0, loginCount);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("連接完成", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenQuotaRiskWasConfirmedInline_SkipsPromptAndStillProbes()
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				Assert.True(account.HasAcceptedClaudeQuotaRisk);
				return Task.FromResult(
					CreateReadySnapshot(account, accountIdentity));
			}
		};
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Inline confirmation must skip the prompt."),
			hasConfirmedQuotaRisk: true);

		Assert.Equal(0, loginCount);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.True(
			Assert.Single(profileStore.SavedSnapshots[0])
				.HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenExistingIdentityIsNotAuthenticated_LogsInAfterProbe()
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: accountIdentity);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) =>
			{
				refreshCount++;

				if (refreshCount == 1)
				{
					return Task.FromResult(new UsageSnapshot(
						account,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.NotConfigured,
						DateTimeOffset.UtcNow,
						Error: "Claude 尚未登入。",
						RecoveryAction: UsageRecoveryAction.ConnectAccount));
				}

				return Task.FromResult(
					CreateReadySnapshot(account, accountIdentity));
			}
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes);

		Assert.Equal(1, loginCount);
		Assert.Equal(2, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(2, refreshCount);
		Assert.True(account.HasConfirmedProviderAccountBinding);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenOldConnectActionWasNotRefreshed_DoesNotLogin()
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: accountIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RemainingCooldown = TimeSpan.FromMinutes(1)
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: "先前尚未確認。",
			RecoveryAction: UsageRecoveryAction.ConnectAccount));
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes);

		Assert.Equal(0, loginCount);
		Assert.Empty(refreshCoordinator.RefreshRequests);
	}

	[Theory]
	[InlineData(UsageRecoveryAction.None)]
	[InlineData(UsageRecoveryAction.Retry)]
	[InlineData(UsageRecoveryAction.SwitchAccount)]
	[InlineData(UsageRecoveryAction.ReconfigureUsageSource)]
	[InlineData(UsageRecoveryAction.RestartApplication)]
	[InlineData(UsageRecoveryAction.RevalidateUsage)]
	[InlineData(UsageRecoveryAction.InstallOrUpdate)]
	[InlineData(UsageRecoveryAction.UpdateApplication)]
	public async Task ConnectClaudeAsync_WhenExistingIdentityProbeDoesNotRequireLogin_DoesNotLogin(
		UsageRecoveryAction recoveryAction)
	{
		const string accountIdentity = "existing@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: accountIdentity);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (account, _) => Task.FromResult(new UsageSnapshot(
				account,
				Array.Empty<UsageMetric>(),
				SourceTrust.Unavailable,
				SnapshotStatus.Error,
				DateTimeOffset.UtcNow,
				Error: "測試用檢查失敗。",
				RecoveryAction: recoveryAction))
		};
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult(accountIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes,
			reportStatus: statuses.Add);

		Assert.Equal(0, loginCount);
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.Equal(2, statuses.Count);

		if (recoveryAction == UsageRecoveryAction.Retry)
		{
			Assert.Equal(
				"「Claude」已保留原本的 Claude 帳號，AI Usage 稍後會自動再檢查用量。",
				statuses[^1]);
			Assert.DoesNotContain("請依卡片提示處理", statuses[^1]);
		}
		else if (recoveryAction == UsageRecoveryAction.RevalidateUsage)
		{
			Assert.Contains("上次用量檢查未完成", statuses[^1]);
			Assert.Contains("重新檢查 Claude 用量", statuses[^1]);
		}
		else
		{
			Assert.Contains("請依卡片提示處理", statuses[^1]);
		}
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenConsentSaveFails_DoesNotLogin()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		int noticeCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult("unused@example.com"));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes,
			reportNotice: (_, _, _) => noticeCount++);

		Assert.Single(profileStore.SavedSnapshots);
		Assert.False(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.Equal(0, loginCount);
		Assert.Equal(1, noticeCount);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenUnexpectedFailure_ReportsExceptionDiagnostic()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		List<(string Operation, string Summary, Exception? Exception)>
			diagnostics = new();
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(ThrowUnexpectedClaudeLoginAsync),
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			(operation, summary, exception) =>
				diagnostics.Add((operation, summary, exception)));
		int noticeCount = 0;

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportNotice: (_, _, _) => noticeCount++);

		(string operation, string summary, Exception? reportedException) =
			Assert.Single(diagnostics);
		Assert.Equal("claude-account-connection", operation);
		Assert.Equal("stage=coordinator;result=failed", summary);
		InvalidOperationException exception =
			Assert.IsType<InvalidOperationException>(reportedException);
		Assert.False(string.IsNullOrWhiteSpace(exception.StackTrace));
		Assert.Equal(1, noticeCount);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenUserCancels_StopsConnectionAndCleansUp()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource loginStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DelegateClaudeAccountLogin claudeLogin = new(async token =>
		{
			loginStarted.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return new ClaudeAccountLoginResult("unused@example.com");
		});
		List<(string Operation, string Summary, Exception? Exception)>
			diagnostics = new();
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			(operation, summary, exception) =>
				diagnostics.Add((operation, summary, exception)));
		List<string> statuses = new();

		Task connectionTask = coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);
		await loginStarted.Task.WaitAsync(AsyncWatchdogTimeout);

		Assert.True(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.True(account.CanCancelProviderAccountConnection);
		Assert.True(coordinator.TryCancelAccountConnection(account.Id));
		await connectionTask.WaitAsync(AsyncWatchdogTimeout);

		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.False(coordinator.TryCancelAccountConnection(account.Id));
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Contains(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
		Assert.Empty(diagnostics);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenCancelledLoginReturnsSuccess_PersistsExternalSuccessAndDefersRefresh()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int initialInvalidationCount = refreshCoordinator.InvalidatedAccountIds.Count;
		int initialRefreshCount = refreshCoordinator.RefreshRequests.Count;
		TaskCompletionSource loginStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<ClaudeAccountLoginResult> finishLogin = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginStarted.TrySetResult();
			return finishLogin.Task;
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		Task connectionTask = coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);
		await loginStarted.Task.WaitAsync(AsyncWatchdogTimeout);

		Assert.True(coordinator.TryCancelAccountConnection(account.Id));
		finishLogin.TrySetResult(
			new ClaudeAccountLoginResult(NewIdentity));
		await connectionTask.WaitAsync(AsyncWatchdogTimeout);

		Assert.Equal(
			initialInvalidationCount + 1,
			refreshCoordinator.InvalidatedAccountIds.Count);
		Assert.Equal(initialRefreshCount, refreshCoordinator.RefreshRequests.Count);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(
			NewIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		UsageSnapshot retrySnapshot =
			Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, retrySnapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, retrySnapshot.RecoveryAction);
		Assert.Equal(NewIdentity, retrySnapshot.ProviderAccountIdentity);
		Assert.Contains(
			"AI Usage 稍後會自動再試，不需要操作",
			account.SecondaryText,
			StringComparison.Ordinal);
		Assert.False(account.ShowRecoveryActionNotice);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));

		DashboardViewModel reloadedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await reloadedViewModel.InitializeAsync();
		Assert.Equal(
			NewIdentity,
			Assert.Single(reloadedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenAuthorizationOriginIsDeclined_DoesNotLaunchOrWait()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int waitCount = 0;
		int disposeCount = 0;
		int launchCount = 0;
		int confirmationCount = 0;
		DelegateCodexAccountLogin codexLogin = new(
			_ =>
			{
				waitCount++;
				return Task.FromResult(
					new CodexAccountLoginResult(
						"unexpected@example.com",
						CodexWorkspaceId));
			},
			() =>
			{
				disposeCount++;
				return Task.CompletedTask;
			});
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			codexLogin,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			statuses.Add,
			_ => launchCount++,
			reportNotice: null,
			confirmAuthorizationUri: authorizationUri =>
			{
				confirmationCount++;
				Assert.Equal("example.invalid", authorizationUri.Host);
				return false;
			});

		Assert.Equal(1, confirmationCount);
		Assert.Equal(0, launchCount);
		Assert.Equal(0, waitCount);
		Assert.Equal(1, disposeCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Contains(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenCancelledDuringAuthorizationConfirmation_DoesNotLaunchOrWait()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int waitCount = 0;
		int disposeCount = 0;
		int launchCount = 0;
		int confirmationCount = 0;
		DelegateCodexAccountLogin codexLogin = new(
			_ =>
			{
				waitCount++;
				return Task.FromResult(
					new CodexAccountLoginResult(
						"unexpected@example.com",
						CodexWorkspaceId));
			},
			() =>
			{
				disposeCount++;
				return Task.CompletedTask;
			});
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			codexLogin,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			statuses.Add,
			_ => launchCount++,
			reportNotice: null,
			confirmAuthorizationUri: authorizationUri =>
			{
				confirmationCount++;
				Assert.Equal("example.invalid", authorizationUri.Host);
				Assert.True(coordinator.TryCancelAccountConnection(account.Id));
				return true;
			});

		Assert.Equal(1, confirmationCount);
		Assert.Equal(0, launchCount);
		Assert.Equal(0, waitCount);
		Assert.Equal(1, disposeCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.False(coordinator.TryCancelAccountConnection(account.Id));
		Assert.Contains(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ConnectCodexAsync_WithDistinctMemberContext_AllowsSeparateOpaqueBindings(
		bool usesSameEmail)
	{
		const string FirstEmail = "member@example.com";
		string secondEmail = usesSameEmail
			? FirstEmail
			: "other-member@example.com";
		Guid secondWorkspaceId = usesSameEmail
			? Guid.Parse("33333333-3333-3333-3333-333333333333")
			: CodexWorkspaceId;
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex first");
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex second");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel first = Assert.Single(
			viewModel.Accounts,
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel second = Assert.Single(
			viewModel.Accounts,
			account => account.Id == secondProfile.Id);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		CodexBindingCommitGate bindingGate = new();

		using (AccountConnectionCoordinator firstCoordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(
					FirstEmail,
					workspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate))
		{
			await firstCoordinator.ConnectCodexAsync(
				first,
				viewModel,
				CodexWorkspaceId,
				reportStatus: null,
				_ => { });
		}

		using (AccountConnectionCoordinator secondCoordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(
					secondEmail,
					workspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate))
		{
			await secondCoordinator.ConnectCodexAsync(
				second,
				viewModel,
				secondWorkspaceId,
				reportStatus: null,
				_ => { });
		}

		Assert.Equal(2, bindingStore.Bindings.Count);
		CodexWorkspaceBinding firstBinding = Assert.Single(
			bindingStore.Bindings,
			binding => binding.AccountId == first.Id);
		CodexWorkspaceBinding secondBinding = Assert.Single(
			bindingStore.Bindings,
			binding => binding.AccountId == second.Id);
		Assert.True(firstBinding.MatchesEntitlement(
			FirstEmail,
			CodexWorkspaceId));
		Assert.True(secondBinding.MatchesEntitlement(
			secondEmail,
			secondWorkspaceId));
		Assert.NotEqual(firstBinding.PublicBindingId, secondBinding.PublicBindingId);
		Assert.True(CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
			first.Profile.ProviderAccountIdentity,
			out _));
		Assert.True(CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
			second.Profile.ProviderAccountIdentity,
			out _));
		Assert.DoesNotContain(
			FirstEmail,
			first.Profile.ProviderAccountIdentity!,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			secondEmail,
			second.Profile.ProviderAccountIdentity!,
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenSessionDisposalFails_RollsBackConfigurationTransaction()
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		DelegateCodexAccountLogin login = new(
			(workspaceId, _) => Task.FromResult(
				new CodexAccountLoginResult(Email, workspaceId)),
			() => Task.FromException(
				new IOException("synthetic session cleanup failure")));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			reportStatus: null,
			_ => { });

		Assert.Equal(0, login.WorkspaceConfigurationCommitCount);
		Assert.Equal(1, login.WorkspaceConfigurationRollbackCount);
		Assert.Empty(bindingStore.Bindings);
		Assert.Null(account.Profile.ProviderAccountIdentity);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConnectCodexAsync_WhenCurrentPrivateBindingIsMalformed_ReconnectsOnce(
		bool useWorkspace)
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		bindingStore.SeedMalformed(account.Id);
		DelegateCodexAccountLogin login = useWorkspace
			? new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(Email, workspaceId)))
			: new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(Email)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			useWorkspace ? CodexWorkspaceId : null,
			reportStatus: null,
			_ => { });

		Assert.Equal(1, login.WorkspaceConfigurationCommitCount);
		Assert.Equal(0, login.WorkspaceConfigurationRollbackCount);
		Assert.True(bindingStore.DeleteCount >= 1);
		if (useWorkspace)
		{
			Assert.True(CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
				account.Profile.ProviderAccountIdentity,
				out _));
			Assert.Single(bindingStore.Bindings);
		}
		else
		{
			Assert.Equal(Email, account.Profile.ProviderAccountIdentity);
			Assert.Empty(bindingStore.Bindings);
		}
	}

	[Fact]
	public async Task ConnectCodexAsync_WithSameMemberContext_FailsClosedForSecondCard()
	{
		const string Email = "member@example.com";
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex first");
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex second");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel first = Assert.Single(
			viewModel.Accounts,
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel second = Assert.Single(
			viewModel.Accounts,
			account => account.Id == secondProfile.Id);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		CodexBindingCommitGate bindingGate = new();
		DelegateCodexAccountLogin login = new((workspaceId, _) =>
			Task.FromResult(new CodexAccountLoginResult(Email, workspaceId)));
		List<string> statuses = new();

		using (AccountConnectionCoordinator firstCoordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate))
		{
			await firstCoordinator.ConnectCodexAsync(
				first,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		using (AccountConnectionCoordinator secondCoordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate))
		{
			await secondCoordinator.ConnectCodexAsync(
				second,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		CodexWorkspaceBinding ownerBinding =
			Assert.Single(bindingStore.Bindings);
		Assert.Equal(first.Id, ownerBinding.AccountId);
		Assert.True(ownerBinding.MatchesEntitlement(
			Email,
			CodexWorkspaceId));
		Assert.NotNull(first.Profile.ProviderAccountIdentity);
		Assert.Null(second.Profile.ProviderAccountIdentity);
		Assert.Null(second.CurrentSnapshot);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"已連到另一張卡片",
				StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains(
				"每張卡片（包含原本第一張）都要填自己的 workspace ID",
				StringComparison.Ordinal));
		Assert.Equal(1, login.WorkspaceConfigurationCommitCount);
		Assert.Equal(1, login.WorkspaceConfigurationRollbackCount);
	}

	[Fact]
	public async Task ConnectCodexAsync_WithoutWorkspace_PersistsRawAccountBinding()
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		DelegateCodexAccountLogin login = new(_ =>
			Task.FromResult(new CodexAccountLoginResult(Email)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			workspaceId: null,
			reportStatus: null,
			_ => { });

		Assert.Equal(Email, account.Profile.ProviderAccountIdentity);
		Assert.Empty(bindingStore.Bindings);
		Assert.Null(login.LastWorkspaceId);
		Assert.Equal(1, login.WorkspaceConfigurationCommitCount);
		Assert.Equal(0, login.WorkspaceConfigurationRollbackCount);
	}

	[Fact]
	public async Task ConnectCodexAsync_WithoutWorkspace_WorksWithoutBindingStore()
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		DelegateCodexAccountLogin login = new(_ =>
			Task.FromResult(new CodexAccountLoginResult(Email)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			workspaceId: null,
			reportStatus: null,
			_ => { });

		Assert.Equal(Email, account.Profile.ProviderAccountIdentity);
		Assert.Null(login.LastWorkspaceId);
	}

	[Fact]
	public async Task ConnectCodexAsync_WithoutWorkspace_RejectsSecondRawOwner()
	{
		const string Email = "member@example.com";
		AccountProfile ownerProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex owner",
			ProviderAccountIdentity: Email);
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex target");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(ownerProfile, targetProfile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel target = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(Email))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			new FakeCodexWorkspaceBindingStore(),
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			target,
			viewModel,
			workspaceId: null,
			statuses.Add,
			_ => { });

		Assert.Equal(Email, ownerProfile.ProviderAccountIdentity);
		Assert.Null(target.Profile.ProviderAccountIdentity);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"已連到另一張卡片",
				StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains(
				"每張卡片（包含原本第一張）都要用自己的 workspace ID 重新連接",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectCodexAsync_WithoutWorkspace_RejectsOpaqueOwnerWithSameAccount()
	{
		const string Email = "member@example.com";
		CodexWorkspaceBinding ownerBinding = CodexWorkspaceBinding.Create(
			Guid.NewGuid(),
			Email,
			CodexWorkspaceId);
		AccountProfile ownerProfile = new(
			ownerBinding.AccountId,
			ProviderKind.Codex,
			"Codex owner",
			ProviderAccountIdentity:
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					ownerBinding.PublicBindingId));
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex target");
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(ownerProfile, targetProfile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel target = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		bindingStore.Seed(ownerBinding);
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(Email))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			target,
			viewModel,
			workspaceId: null,
			reportStatus: null,
			_ => { });

		Assert.Null(target.Profile.ProviderAccountIdentity);
		Assert.Equal(ownerBinding, Assert.Single(bindingStore.Bindings));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task ConnectCodexAsync_WithRuntimeOnlyRawCacheOwner_RejectsDuplicate(
		bool ownerChangeInProgress,
		bool useWorkspace)
	{
		const string Email = "member@example.com";
		AccountProfile ownerProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex owner");
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex target");
		UsageSnapshot cachedSnapshot = new(
			ownerProfile,
			[new UsageMetric("weekly", "週用量", 50, "舊資料 50%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: Email,
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = cachedSnapshot
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(ownerProfile, targetProfile),
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel owner = Assert.Single(
			viewModel.Accounts,
			account => account.Id == ownerProfile.Id);
		AccountUsageViewModel target = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		DelegateCodexAccountLogin login = useWorkspace
			? new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(
					Email,
					workspaceId)))
			: new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(Email)));
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());
		if (ownerChangeInProgress)
		{
			owner.BeginProviderAccountChange();
		}

		try
		{
			await coordinator.ConnectCodexAsync(
				target,
				viewModel,
				useWorkspace ? CodexWorkspaceId : null,
				statuses.Add,
				_ => { });

			Assert.Null(owner.Profile.ProviderAccountIdentity);
			Assert.Equal(
				ownerChangeInProgress ? null : Email,
				owner.ProviderAccountIdentity);
			Assert.Equal(Email, owner.ProviderAccountOwnershipIdentity);
			Assert.Null(target.Profile.ProviderAccountIdentity);
			Assert.Empty(bindingStore.Bindings);
			Assert.Contains(
				statuses,
				status => status.Contains(
					"已連到另一張卡片",
					StringComparison.Ordinal));
		}
		finally
		{
			owner.AbortProviderAccountChange();
		}
	}

	[Fact]
	public async Task ConnectCodexAsync_FromForcedToAccountLevel_DeletesPrivateBinding()
	{
		const string Email = "member@example.com";
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			Email,
			CodexWorkspaceId);
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity:
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId));
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		bindingStore.Seed(binding);
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(Email))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			workspaceId: null,
			reportStatus: null,
			_ => { });

		Assert.Equal(Email, account.Profile.ProviderAccountIdentity);
		Assert.Empty(bindingStore.Bindings);
	}

	[Fact]
	public async Task ConnectCodexAsync_FromAccountLevelToForced_CreatesPrivateBinding()
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: Email);
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(
					Email,
					workspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			new CodexBindingCommitGate());

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			reportStatus: null,
			_ => { });

		Assert.True(CodexWorkspaceBinding.TryNormalizePublicBindingIdentity(
			account.Profile.ProviderAccountIdentity,
			out _));
		CodexWorkspaceBinding persisted = Assert.Single(bindingStore.Bindings);
		Assert.True(persisted.MatchesEntitlement(Email, CodexWorkspaceId));
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenBindingGateIsBusy_AcquiresAccountMutationGateFirst()
	{
		const string Email = "member@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		CodexBindingCommitGate bindingGate = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new DelegateCodexAccountLogin((workspaceId, _) =>
				Task.FromResult(new CodexAccountLoginResult(
					Email,
					workspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate);
		IDisposable blockingBindingLease =
			await bindingGate.EnterAsync();
		Task? connectionTask = null;

		try
		{
			connectionTask = coordinator.ConnectCodexAsync(
				account,
				viewModel,
				CodexWorkspaceId,
				reportStatus: null,
				_ => { });

			bool wasAccountMutationGateObserved = false;
			for (int attempt = 0; attempt < 100; attempt++)
			{
				using CancellationTokenSource probeCancellationSource =
					new(TimeSpan.FromMilliseconds(25));

				try
				{
					await viewModel
						.ExecuteAuthenticatedProviderBindingMutationAsync(
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
				"Codex commit 必須先取得 account mutation gate，再等待 binding gate。");
		}
		finally
		{
			blockingBindingLease.Dispose();

			if (connectionTask is not null)
			{
				await connectionTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.Single(bindingStore.Bindings);
	}

	[Theory]
	[InlineData(false, false, false, false)]
	[InlineData(true, false, false, false)]
	[InlineData(false, true, false, false)]
	[InlineData(true, true, false, false)]
	[InlineData(false, true, true, false)]
	[InlineData(true, true, true, false)]
	[InlineData(false, true, false, true)]
	[InlineData(true, true, false, true)]
	[InlineData(false, true, true, true)]
	[InlineData(true, true, true, true)]
	public async Task ConnectCodexAsync_FromRestartQuarantine_WhenDurableCleanupFails_PreservesQuarantineAcrossRestart(
		bool useWorkspace,
		bool failNextBindingLoad,
		bool useUnauthorizedAccessException,
		bool failFirstBindingDelete)
	{
		const string AccountIdentity = "member@example.com";
		Guid accountId = Guid.NewGuid();
		Guid? requestedWorkspaceId = useWorkspace ? CodexWorkspaceId : null;
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: AccountIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(
			profile,
			AccountIdentity) with
		{
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = cachedSnapshot,
			DeleteFailuresRemaining = int.MaxValue
		};
		CodexWorkspaceBinding restartQuarantine =
			CodexWorkspaceBinding.CreateRestartQuarantine(accountId);
		FakeCodexWorkspaceBindingStore bindingStore = new();
		bindingStore.Seed(restartQuarantine);
		CodexBindingCommitGate bindingGate = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		if (failNextBindingLoad)
		{
			bindingStore.FailNextLoad(useUnauthorizedAccessException
				? new UnauthorizedAccessException(
					"Synthetic transient binding access failure.")
				: new IOException("Synthetic transient binding read failure."));
			if (failFirstBindingDelete)
			{
				bindingStore.FailNextDelete(
					new IOException("Synthetic transient binding delete failure."));
			}
		}

		DelegateCodexAccountLogin login = useWorkspace
			? new DelegateCodexAccountLogin((workspaceId, _) =>
			{
				Assert.Equal(CodexWorkspaceId, workspaceId);
				return Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					workspaceId));
			})
			: new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					WorkspaceId: null)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate);

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			requestedWorkspaceId,
			reportStatus: null,
			_ => { });

		CodexWorkspaceBinding persistedQuarantine =
			Assert.Single(bindingStore.Bindings);
		Assert.Equal(
			failNextBindingLoad ? 1 : 0,
			bindingStore.InjectedLoadFailureCount);
		Assert.Equal(
			failFirstBindingDelete ? 1 : 0,
			bindingStore.InjectedDeleteFailureCount);
		Assert.True(persistedQuarantine.IsRestartQuarantine);
		Assert.Equal(accountId, persistedQuarantine.AccountId);
		if (!failNextBindingLoad)
		{
			Assert.Equal(restartQuarantine, persistedQuarantine);
		}

		Assert.NotNull(snapshotStore.SnapshotToLoad);
		AccountProfile persistedProfile = Assert.Single(
			(await profileStore.LoadAsync()).Accounts);
		Assert.True(persistedProfile.IsEnabled);
		Assert.Equal(AccountIdentity, persistedProfile.ProviderAccountIdentity);

		profileStore.ShouldFailSave = false;
		DashboardViewModel restartedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await restartedViewModel.InitializeAsync();

		Assert.Null(Assert.Single(restartedViewModel.Accounts).CurrentSnapshot);
		Assert.Equal(
			persistedQuarantine,
			Assert.Single(bindingStore.Bindings));
		Assert.NotNull(snapshotStore.SnapshotToLoad);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task ConnectCodexAsync_WithoutUsablePrivateBinding_WhenDurableCleanupFails_CreatesRestartQuarantine(
		bool useWorkspace,
		bool startWithMalformedBinding)
	{
		const string AccountIdentity = "member@example.com";
		Guid accountId = Guid.NewGuid();
		Guid? requestedWorkspaceId = useWorkspace ? CodexWorkspaceId : null;
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: AccountIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(
			profile,
			AccountIdentity) with
		{
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = cachedSnapshot,
			DeleteFailuresRemaining = int.MaxValue
		};
		FakeCodexWorkspaceBindingStore bindingStore = new();
		if (startWithMalformedBinding)
		{
			bindingStore.SeedMalformed(accountId);
		}

		CodexBindingCommitGate bindingGate = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		if (startWithMalformedBinding)
		{
			Assert.Null(account.CurrentSnapshot);
		}
		else
		{
			Assert.NotNull(account.CurrentSnapshot);
		}

		DelegateCodexAccountLogin login = useWorkspace
			? new DelegateCodexAccountLogin((workspaceId, _) =>
			{
				Assert.Equal(CodexWorkspaceId, workspaceId);
				return Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					workspaceId));
			})
			: new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					WorkspaceId: null)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate);

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			requestedWorkspaceId,
			reportStatus: null,
			_ => { });

		CodexWorkspaceBinding quarantine = Assert.Single(bindingStore.Bindings);
		Assert.True(quarantine.IsRestartQuarantine);
		Assert.Equal(accountId, quarantine.AccountId);
		Assert.NotNull(snapshotStore.SnapshotToLoad);

		profileStore.ShouldFailSave = false;
		DashboardViewModel restartedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await restartedViewModel.InitializeAsync();

		Assert.Null(Assert.Single(restartedViewModel.Accounts).CurrentSnapshot);
		Assert.Equal(quarantine, Assert.Single(bindingStore.Bindings));
		Assert.NotNull(snapshotStore.SnapshotToLoad);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task ConnectCodexAsync_WhenRestartQuarantineSaveFailsTwice_RetriesCompletedCleanupAndPreservesQuarantineAcrossRestart(
		bool useWorkspace,
		bool startWithRestartQuarantine)
	{
		const string AccountIdentity = "member@example.com";
		Guid accountId = Guid.NewGuid();
		Guid? requestedWorkspaceId = useWorkspace ? CodexWorkspaceId : null;
		AccountProfile profile = new(
			accountId,
			ProviderKind.Codex,
			"Codex",
			ProviderAccountIdentity: AccountIdentity);
		UsageSnapshot cachedSnapshot = CreateReadySnapshot(
			profile,
			AccountIdentity) with
		{
			SubscriptionVerificationState =
				SubscriptionVerificationState.Unverified
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = cachedSnapshot,
			DeleteFailuresRemaining = int.MaxValue
		};
		FakeCodexWorkspaceBindingStore bindingStore = new();
		CodexWorkspaceBinding? originalRestartQuarantine = null;
		if (startWithRestartQuarantine)
		{
			originalRestartQuarantine =
				CodexWorkspaceBinding.CreateRestartQuarantine(accountId);
			bindingStore.Seed(originalRestartQuarantine);
		}

		bindingStore.FailNextRestartQuarantineSaves(
			2,
			new IOException("Synthetic restart quarantine save failure."));
		CodexBindingCommitGate bindingGate = new();
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Equal(
			startWithRestartQuarantine,
			account.CurrentSnapshot is null);

		DelegateCodexAccountLogin login = useWorkspace
			? new DelegateCodexAccountLogin((workspaceId, _) =>
			{
				Assert.Equal(CodexWorkspaceId, workspaceId);
				return Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					workspaceId));
			})
			: new DelegateCodexAccountLogin(_ =>
				Task.FromResult(new CodexAccountLoginResult(
					AccountIdentity,
					WorkspaceId: null)));
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			login,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			bindingStore,
			bindingGate);

		await coordinator.ConnectCodexAsync(
			account,
			viewModel,
			requestedWorkspaceId,
			reportStatus: null,
			_ => { });

		Assert.Equal(2, bindingStore.InjectedRestartQuarantineSaveFailureCount);
		CodexWorkspaceBinding quarantine = Assert.Single(bindingStore.Bindings);
		Assert.True(quarantine.IsRestartQuarantine);
		Assert.Equal(accountId, quarantine.AccountId);
		if (originalRestartQuarantine is not null)
		{
			Assert.Equal(originalRestartQuarantine, quarantine);
		}

		Assert.NotNull(snapshotStore.SnapshotToLoad);

		profileStore.ShouldFailSave = false;
		DashboardViewModel restartedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore,
			dashboardPreferencesStore: null,
			accountRuntimeStatePurger: null,
			codexWorkspaceBindingStore: bindingStore,
			codexBindingCommitGate: bindingGate);
		await restartedViewModel.InitializeAsync();

		Assert.Null(Assert.Single(restartedViewModel.Accounts).CurrentSnapshot);
		Assert.Equal(quarantine, Assert.Single(bindingStore.Bindings));
		Assert.NotNull(snapshotStore.SnapshotToLoad);
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenCancelledLoginReturnsSuccess_PersistsExternalSuccessAndDefersRefresh()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int initialInvalidationCount = refreshCoordinator.InvalidatedAccountIds.Count;
		int initialRefreshCount = refreshCoordinator.RefreshRequests.Count;
		TaskCompletionSource loginStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<CodexAccountLoginResult> finishLogin = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DelegateCodexAccountLogin codexLogin = new(_ =>
		{
			loginStarted.TrySetResult();
			return finishLogin.Task;
		});
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			codexLogin,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		Task connectionTask = coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			statuses.Add,
			_ => { });
		await loginStarted.Task.WaitAsync(AsyncWatchdogTimeout);

		Assert.True(coordinator.TryCancelAccountConnection(account.Id));
		finishLogin.TrySetResult(new CodexAccountLoginResult(
			NewIdentity,
			CodexWorkspaceId));
		await connectionTask.WaitAsync(AsyncWatchdogTimeout);

		Assert.Equal(
			initialInvalidationCount + 1,
			refreshCoordinator.InvalidatedAccountIds.Count);
		Assert.Equal(initialRefreshCount, refreshCoordinator.RefreshRequests.Count);
		CodexWorkspaceBinding persistedBinding =
			Assert.Single(codexBindingStore.Bindings);
		string publicBindingIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				persistedBinding.PublicBindingId);
		Assert.True(persistedBinding.MatchesAccount(NewIdentity));
		Assert.True(persistedBinding.MatchesWorkspace(CodexWorkspaceId));
		Assert.True(persistedBinding.MatchesEntitlement(
			NewIdentity,
			CodexWorkspaceId));
		Assert.Equal(
			publicBindingIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(publicBindingIdentity, account.ProviderAccountIdentity);
		Assert.Equal(
			publicBindingIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		UsageSnapshot retrySnapshot =
			Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, retrySnapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, retrySnapshot.RecoveryAction);
		Assert.Equal(
			publicBindingIdentity,
			retrySnapshot.ProviderAccountIdentity);
		Assert.Contains(
			"AI Usage 稍後會自動再試，不需要操作",
			account.SecondaryText,
			StringComparison.Ordinal);
		Assert.False(account.ShowRecoveryActionNotice);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));

		DashboardViewModel reloadedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await reloadedViewModel.InitializeAsync();
		Assert.Equal(
			publicBindingIdentity,
			Assert.Single(reloadedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	public async Task ConnectAuthenticatedProvider_WhenUpdateReservationStartsBeforeSuccessfulResult_PersistsBindingAndDrains(
		ProviderKind provider)
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			provider,
			provider.ToString(),
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int initialRefreshCount = refreshCoordinator.RefreshRequests.Count;
		AccountConnectionCoordinator? coordinator = null;
		Task<bool>? drainTask = null;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			Assert.True(
				viewModel.TryReserveAccountMutationsForUpdateShutdown());
			Assert.True(coordinator!.TryReserveShutdownForUpdate());
			drainTask = coordinator!.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));
			Assert.False(drainTask.IsCompleted);
			return Task.FromResult(new ClaudeAccountLoginResult(NewIdentity));
		});
		DelegateCodexAccountLogin codexLogin = new(_ =>
		{
			Assert.True(
				viewModel.TryReserveAccountMutationsForUpdateShutdown());
			Assert.True(coordinator!.TryReserveShutdownForUpdate());
			drainTask = coordinator!.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));
			Assert.False(drainTask.IsCompleted);
			return Task.FromResult(new CodexAccountLoginResult(
				NewIdentity,
				CodexWorkspaceId));
		});
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator ownedCoordinator = new(
			claudeLogin,
			codexLogin,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		coordinator = ownedCoordinator;
		List<string> statuses = new();

		if (provider == ProviderKind.Claude)
		{
			await ownedCoordinator.ConnectClaudeAsync(
				account,
				viewModel,
				() => throw new InvalidOperationException(
					"Persisted consent must skip the prompt."),
				statuses.Add);
		}
		else
		{
			await ownedCoordinator.ConnectCodexAsync(
				account,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		Assert.NotNull(drainTask);
		Assert.True(await drainTask.WaitAsync(AsyncWatchdogTimeout));
		Assert.Equal(initialRefreshCount, refreshCoordinator.RefreshRequests.Count);
		string expectedPublicIdentity = NewIdentity;
		if (provider == ProviderKind.Codex)
		{
			CodexWorkspaceBinding binding =
				Assert.Single(codexBindingStore.Bindings);
			Assert.True(binding.MatchesEntitlement(
				NewIdentity,
				CodexWorkspaceId));
			expectedPublicIdentity =
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId);
		}
		Assert.Equal(
			expectedPublicIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(expectedPublicIdentity, account.ProviderAccountIdentity);
		Assert.Equal(
			expectedPublicIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(ownedCoordinator.IsAccountConnectionInProgress(account.Id));
		Assert.True(await viewModel.LockAccountMutationsForUpdateShutdownAsync(
			TimeSpan.FromSeconds(1)));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));

		DashboardViewModel reloadedViewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await reloadedViewModel.InitializeAsync();
		Assert.Equal(
			expectedPublicIdentity,
			Assert.Single(reloadedViewModel.Accounts)
				.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenRefreshStartsAfterDurableBinding_CannotCancelOrRollback()
	{
		const string OriginalIdentity = "original@example.com";
		const string NewIdentity = "replacement@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: OriginalIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		TaskCompletionSource<AccountProfile> refreshStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<UsageSnapshot> finishRefresh = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				refreshStarted.TrySetResult(refreshAccount);
				return finishRefresh.Task;
			}
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(CreateReadySnapshot(profile, OriginalIdentity));
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
			Task.FromResult(new ClaudeAccountLoginResult(NewIdentity)));
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		Task connectionTask = coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);
		AccountProfile refreshAccount = await refreshStarted.Task.WaitAsync(
			AsyncWatchdogTimeout);

		Assert.False(coordinator.TryCancelAccountConnection(account.Id));
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		finishRefresh.TrySetResult(
			CreateReadySnapshot(refreshAccount, NewIdentity));
		await connectionTask.WaitAsync(AsyncWatchdogTimeout);
		await viewModel.QuiesceAccountRefreshAsync(account.Id).WaitAsync(
			AsyncWatchdogTimeout);

		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			NewIdentity,
			Assert.IsType<UsageSnapshot>(account.CurrentSnapshot)
				.ProviderAccountIdentity);
		Assert.Contains(
			profileStore.SavedSnapshots.SelectMany(snapshot => snapshot),
			savedProfile => string.Equals(
				savedProfile.ProviderAccountIdentity,
				NewIdentity,
				StringComparison.OrdinalIgnoreCase));
		Assert.Single(snapshotStore.SavedSnapshots);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
		Assert.Contains(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenRefreshingIsStopped_KeepsDurableNewIdentityAndDropsOldSnapshot()
	{
		const string OriginalIdentity = "original@example.com";
		const string NewIdentity = "replacement@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: OriginalIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		UsageSnapshot oldSnapshot = CreateReadySnapshot(
			profile,
			OriginalIdentity);
		account.ApplySnapshot(oldSnapshot);
		snapshotStore.SnapshotToLoad = oldSnapshot;
		viewModel.StopRefreshing();
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
			Task.FromResult(new ClaudeAccountLoginResult(NewIdentity)));
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		UsageSnapshot currentSnapshot = Assert.IsType<UsageSnapshot>(
			account.CurrentSnapshot);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, currentSnapshot.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, currentSnapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, currentSnapshot.RecoveryAction);
		Assert.Empty(currentSnapshot.Metrics);
		Assert.Null(snapshotStore.SnapshotToLoad);
		Assert.Contains(profile.Id, snapshotStore.DeletedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenFirstUsageRefreshFails_PersistsNewBindingAndRetriesWithoutLogin()
	{
		const string OriginalIdentity = "original@example.com";
		const string NewIdentity = "replacement@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: OriginalIdentity);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new();
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				Assert.Equal(
					NewIdentity,
					refreshAccount.ProviderAccountIdentity);
				refreshCount++;
				return Task.FromResult(refreshCount == 1
					? new UsageSnapshot(
						refreshAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.Error,
						DateTimeOffset.UtcNow,
						Error: "Synthetic transient Claude usage failure.",
						ProviderAccountIdentity: NewIdentity,
						RecoveryAction: UsageRecoveryAction.Retry)
					: CreateReadySnapshot(refreshAccount, NewIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(CreateReadySnapshot(profile, OriginalIdentity));
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(new ClaudeAccountLoginResult(NewIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		Assert.Equal(1, loginCount);
		Assert.Equal(1, refreshCount);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, account.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.Retry, account.RecoveryAction);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Contains(
			statuses,
			status => status.Contains("自動再試", StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("重新連接", StringComparison.Ordinal));

		await viewModel.RefreshUsageAsync();

		Assert.Equal(1, loginCount);
		Assert.Equal(2, refreshCount);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);
		Assert.True(account.HasConfirmedProviderAccountBinding);
		Assert.Equal(
			NewIdentity,
			Assert.Single(snapshotStore.SavedSnapshots)
				.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenFirstUsageRefreshIsNotConfigured_RetriesBeforeRequiringReconnect()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				refreshCount++;
				return Task.FromResult(refreshCount == 1
					? new UsageSnapshot(
						refreshAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.NotConfigured,
						DateTimeOffset.UtcNow,
						Error: "Synthetic login propagation delay.",
						RecoveryAction: UsageRecoveryAction.ConnectAccount)
					: CreateReadySnapshot(refreshAccount, NewIdentity));
			}
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(new ClaudeAccountLoginResult(NewIdentity));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		Assert.Equal(1, loginCount);
		Assert.Equal(2, refreshCount);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Ready, account.CurrentSnapshot?.Status);
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("重新連接", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenTransitionalRetryReturnsDifferentIdentity_RejectsItWithoutOverwritingVerifiedBinding()
	{
		const string VerifiedIdentity = "connected@example.com";
		const string UnexpectedIdentity = "different@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				refreshCount++;
				return Task.FromResult(refreshCount == 1
					? new UsageSnapshot(
						refreshAccount,
						Array.Empty<UsageMetric>(),
						SourceTrust.Unavailable,
						SnapshotStatus.NotConfigured,
						DateTimeOffset.UtcNow,
						Error: "Synthetic login propagation delay.",
						RecoveryAction: UsageRecoveryAction.ConnectAccount)
					: CreateReadySnapshot(
						refreshAccount,
						UnexpectedIdentity));
			}
		};
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(_ => Task.FromResult(
				new ClaudeAccountLoginResult(VerifiedIdentity))),
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		UsageSnapshot currentSnapshot = Assert.IsType<UsageSnapshot>(
			account.CurrentSnapshot);
		Assert.Equal(2, refreshCount);
		Assert.Equal(
			VerifiedIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(VerifiedIdentity, account.ProviderAccountIdentity);
		Assert.Equal(
			VerifiedIdentity,
			currentSnapshot.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, currentSnapshot.Status);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			currentSnapshot.RecoveryAction);
		Assert.True(account.DidRejectProviderAccountSnapshot);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Single(profileStore.SavedSnapshots);
		Assert.DoesNotContain(
			profileStore.SavedSnapshots.SelectMany(snapshot => snapshot),
			savedProfile => string.Equals(
				savedProfile.ProviderAccountIdentity,
				UnexpectedIdentity,
				StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenUsageRemainsNotConfigured_StopsAfterBoundedRetryWithExplicitError()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				refreshCount++;
				return Task.FromResult(new UsageSnapshot(
					refreshAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					DateTimeOffset.UtcNow,
					Error: "Synthetic persistent account error.",
					RecoveryAction: UsageRecoveryAction.ConnectAccount));
			}
		};
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profile),
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(_ => Task.FromResult(
				new ClaudeAccountLoginResult(NewIdentity))),
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		Assert.Equal(3, refreshCount);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.NotConfigured, account.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, account.RecoveryAction);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"多次重新檢查後仍無法確認",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("自動重試", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PersistAuthenticatedBinding_WhenSaveContentionIsTransient_RetriesUntilItSucceeds()
	{
		const string AccountIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		int saveAttempts = 0;
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = (_, _) =>
			{
				saveAttempts++;

				if (saveAttempts < 3)
				{
					throw new AccountProfileStoreException(
						"Synthetic transient save contention.",
						new IOException("Synthetic sharing violation."));
				}

				return Task.CompletedTask;
			}
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity(AccountIdentity);
		Assert.True(account.TryBeginProviderAccountChangeCommit());

		AuthenticatedProviderBindingCommitResult result =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				account,
				AccountIdentity);

		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			result);
		Assert.Equal(3, saveAttempts);
		Assert.Equal(AccountIdentity, account.Profile.ProviderAccountIdentity);
		Assert.DoesNotContain(
			"無法保存",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		account.AbortProviderAccountChange();
	}

	[Fact]
	public async Task PersistAuthenticatedBinding_CacheWarningClearsOnlyAfterFailedAccountCleanupSucceeds()
	{
		const string AccountIdentity = "connected@example.com";
		AccountProfile failedProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex A");
		AccountProfile otherProfile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex B");
		FakeAccountProfileStore profileStore = new(
			failedProfile,
			otherProfile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			DeleteFailuresRemaining = 3
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel failedAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == failedProfile.Id);
		AccountUsageViewModel otherAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == otherProfile.Id);
		failedAccount.BeginProviderAccountChange();
		failedAccount.SeedProviderAccountIdentity(AccountIdentity);
		Assert.True(failedAccount.TryBeginProviderAccountChangeCommit());

		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				failedAccount,
				AccountIdentity));
		Assert.Contains(
			"無法清除切換前的上次用量資料",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		await viewModel.RefreshUsageAfterInvalidationAsync(otherAccount.Id);

		Assert.Contains(
			"無法清除切換前的上次用量資料",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);

		await viewModel.RefreshUsageAfterInvalidationAsync(failedAccount.Id);

		Assert.Equal(5, snapshotStore.DeletedAccountIds.Count);
		Assert.DoesNotContain(
			"無法清除切換前的上次用量資料",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		failedAccount.AbortProviderAccountChange();
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	public async Task ConnectAuthenticatedProvider_WhenTransientBindingSaveExhaustsRetries_ReportsSplitStateHonestly(
		ProviderKind provider)
	{
		const string OriginalIdentity = "original@example.com";
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			provider,
			provider.ToString(),
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude,
			ProviderAccountIdentity: OriginalIdentity);
		int saveAttempts = 0;
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = (_, _) =>
			{
				saveAttempts++;
				throw new AccountProfileStoreException(
					"Synthetic transient save contention.",
					new IOException("Synthetic sharing violation."));
			}
		};
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) => Task.FromResult(
				new UsageSnapshot(
					refreshAccount,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					DateTimeOffset.UtcNow,
					Error: "Synthetic unauthenticated account.",
					RecoveryAction: UsageRecoveryAction.ConnectAccount))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginAttempts = 0;
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(_ =>
			{
				loginAttempts++;
				return Task.FromResult(
					new ClaudeAccountLoginResult(NewIdentity));
			}),
			new DelegateCodexAccountLogin(_ =>
			{
				loginAttempts++;
				return Task.FromResult(
					new CodexAccountLoginResult(
						NewIdentity,
						CodexWorkspaceId));
			}),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		if (provider == ProviderKind.Claude)
		{
			await coordinator.ConnectClaudeAsync(
				account,
				viewModel,
				() => throw new InvalidOperationException(
					"Persisted consent must skip the prompt."),
				statuses.Add);
		}
		else
		{
			await coordinator.ConnectCodexAsync(
				account,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		Assert.Equal(1, loginAttempts);
		Assert.Equal(provider == ProviderKind.Codex ? 6 : 3, saveAttempts);
		if (provider == ProviderKind.Codex)
		{
			Assert.False(account.IsEnabled);
			Assert.Null(account.Profile.ProviderAccountIdentity);
			Assert.Null(account.ProviderAccountIdentity);
			CodexWorkspaceBinding quarantine =
				Assert.Single(codexBindingStore.Bindings);
			Assert.True(quarantine.IsRestartQuarantine);
			Assert.Equal(account.Id, quarantine.AccountId);
		}
		else
		{
			Assert.Equal(
				OriginalIdentity,
				account.Profile.ProviderAccountIdentity);
			Assert.Equal(OriginalIdentity, account.ProviderAccountIdentity);
		}
		Assert.False(account.IsProviderAccountChangeInProgress);
		string finalStatus = Assert.IsType<string>(statuses.LastOrDefault());
		Assert.Contains(
			provider == ProviderKind.Codex
				? "已停止檢查這張卡片"
				: "可能已切換到新帳號",
			finalStatus,
			StringComparison.Ordinal);
		Assert.Equal(
			provider != ProviderKind.Codex,
			finalStatus.Contains(
				"AI Usage 仍保留原本的連接資料",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			"原本帳號仍會保留",
			finalStatus,
			StringComparison.Ordinal);
		Assert.Contains(
			"多次嘗試後仍無法儲存這次帳號連接",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	public async Task ConnectAuthenticatedProvider_WhenDuplicateIdentityAndTargetWasBound_FailsClosed(
		ProviderKind provider)
	{
		const string DuplicateIdentity = "duplicate@example.com";
		const string OriginalIdentity = "original@example.com";
		AccountProfile ownerProfile = new(
			Guid.NewGuid(),
			provider,
			$"{provider} owner",
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude,
			ProviderAccountIdentity: DuplicateIdentity);
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			provider,
			$"{provider} target",
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude,
			ProviderAccountIdentity: OriginalIdentity);
		FakeAccountProfileStore profileStore = new(ownerProfile, targetProfile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		targetAccount.ApplySnapshot(CreateReadySnapshot(
			targetAccount.Profile,
			OriginalIdentity));
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(_ => Task.FromResult(
				new ClaudeAccountLoginResult(DuplicateIdentity))),
			new DelegateCodexAccountLogin(_ => Task.FromResult(
				new CodexAccountLoginResult(
					DuplicateIdentity,
					CodexWorkspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		if (provider == ProviderKind.Claude)
		{
			await coordinator.ConnectClaudeAsync(
				targetAccount,
				viewModel,
				() => throw new InvalidOperationException(
					"Persisted consent must skip the prompt."),
				statuses.Add);
		}
		else
		{
			await coordinator.ConnectCodexAsync(
				targetAccount,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(targetAccount.ProviderAccountIdentity);
		Assert.Null(targetAccount.CurrentSnapshot);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		AccountProfile[] savedProfiles = Assert.Single(
			profileStore.SavedSnapshots).ToArray();
		Assert.Equal(
			DuplicateIdentity,
			Assert.Single(savedProfiles, profile => profile.Id == ownerProfile.Id)
				.ProviderAccountIdentity);
		Assert.Null(
			Assert.Single(savedProfiles, profile => profile.Id == targetProfile.Id)
				.ProviderAccountIdentity);
		Assert.Contains(targetProfile.Id, refreshCoordinator.InvalidatedAccountIds);
		Assert.Contains(targetProfile.Id, snapshotStore.DeletedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		string expectedConflictText = provider == ProviderKind.Codex
			? "已連到另一張卡片"
			: "已連接到另一張帳號卡片";
		Assert.Contains(
			statuses,
			status => status.Contains(
				expectedConflictText,
				StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	public async Task ConnectAuthenticatedProvider_WhenConflictDisconnectSaveFails_DisablesCurrentRuntime(
		ProviderKind provider)
	{
		const string DuplicateIdentity = "duplicate@example.com";
		const string OriginalIdentity = "original@example.com";
		AccountProfile ownerProfile = new(
			Guid.NewGuid(),
			provider,
			$"{provider} owner",
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude,
			ProviderAccountIdentity: DuplicateIdentity);
		AccountProfile targetProfile = new(
			Guid.NewGuid(),
			provider,
			$"{provider} target",
			HasAcceptedClaudeQuotaRisk: provider == ProviderKind.Claude,
			ProviderAccountIdentity: OriginalIdentity);
		FakeAccountProfileStore profileStore = new(ownerProfile, targetProfile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		targetAccount.ApplySnapshot(CreateReadySnapshot(
			targetAccount.Profile,
			OriginalIdentity));
		profileStore.ShouldFailSave = true;
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new DelegateClaudeAccountLogin(_ => Task.FromResult(
				new ClaudeAccountLoginResult(DuplicateIdentity))),
			new DelegateCodexAccountLogin(_ => Task.FromResult(
				new CodexAccountLoginResult(
					DuplicateIdentity,
					CodexWorkspaceId))),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();

		if (provider == ProviderKind.Claude)
		{
			await coordinator.ConnectClaudeAsync(
				targetAccount,
				viewModel,
				() => throw new InvalidOperationException(
					"Persisted consent must skip the prompt."),
				statuses.Add);
		}
		else
		{
			await coordinator.ConnectCodexAsync(
				targetAccount,
				viewModel,
				CodexWorkspaceId,
				statuses.Add,
				_ => { });
		}

		Assert.False(targetAccount.IsEnabled);
		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(targetAccount.ProviderAccountIdentity);
		Assert.Null(targetAccount.CurrentSnapshot);
		Assert.False(targetAccount.CanRefreshUsage);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		Assert.Contains(targetProfile.Id, refreshCoordinator.InvalidatedAccountIds);
		Assert.Contains(targetProfile.Id, snapshotStore.DeletedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Single(profileStore.SavedSnapshots);
		AccountProfileLoadResult persisted = await profileStore.LoadAsync();
		AccountProfile persistedTarget = Assert.Single(
			persisted.Accounts,
			profile => profile.Id == targetProfile.Id);
		Assert.True(persistedTarget.IsEnabled);
		Assert.Equal(OriginalIdentity, persistedTarget.ProviderAccountIdentity);
		Assert.Contains(
			"重新啟動後",
			viewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			statuses,
			status =>
				status.Contains("這次已停止檢查", StringComparison.Ordinal) &&
				status.Contains("重新啟動後", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PersistAuthenticatedBinding_WhenTwoTransientClaimsMatch_FirstCommitWins()
	{
		const string SharedIdentity = "shared@example.com";
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 1",
			HasAcceptedClaudeQuotaRisk: true);
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude 2",
			HasAcceptedClaudeQuotaRisk: true);
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator(),
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel first = Assert.Single(
			viewModel.Accounts,
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel second = Assert.Single(
			viewModel.Accounts,
			account => account.Id == secondProfile.Id);
		first.BeginProviderAccountChange();
		second.BeginProviderAccountChange();
		first.SeedProviderAccountIdentity(SharedIdentity);
		second.SeedProviderAccountIdentity(SharedIdentity);
		Assert.True(first.TryBeginProviderAccountChangeCommit());
		Assert.True(second.TryBeginProviderAccountChangeCommit());

		AuthenticatedProviderBindingCommitResult firstResult =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				first,
				SharedIdentity);
		AuthenticatedProviderBindingCommitResult secondResult =
			await viewModel.TryPersistAuthenticatedProviderBindingAsync(
				second,
				SharedIdentity);

		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.Succeeded,
			firstResult);
		Assert.Equal(
			AuthenticatedProviderBindingCommitResult.IdentityConflict,
			secondResult);
		Assert.Equal(SharedIdentity, first.Profile.ProviderAccountIdentity);
		Assert.Null(second.Profile.ProviderAccountIdentity);
		Assert.Single(profileStore.SavedSnapshots);
		second.AbortProviderAccountChange();
	}

	[Fact]
	public async Task ConnectCodexAsync_WhenShutdownStartsDuringCompletedSessionDisposal_WaitsForGateReleaseBeforeBindingCommit()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Codex,
			"Codex");
		FakeAccountProfileStore profileStore = new(profile);
		int refreshCount = 0;
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) =>
			{
				refreshCount++;
				return Task.FromResult(
					CreateReadySnapshot(refreshAccount, NewIdentity));
			}
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			new FakeUsageSnapshotStore());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource disposalStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowDisposalToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DelegateCodexAccountLogin codexLogin = new(
			_ => Task.FromResult(new CodexAccountLoginResult(
				NewIdentity,
				CodexWorkspaceId)),
			async () =>
			{
				disposalStarted.TrySetResult();
				await allowDisposalToFinish.Task;
			});
		FakeCodexWorkspaceBindingStore codexBindingStore = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			codexLogin,
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			codexBindingStore,
			new CodexBindingCommitGate());
		List<string> statuses = new();
		Task connectionTask = coordinator.ConnectCodexAsync(
			account,
			viewModel,
			CodexWorkspaceId,
			statuses.Add,
			_ => { });
		Task<bool>? drainTask = null;
		bool wasFullyDrained = false;

		try
		{
			await disposalStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(connectionTask.IsCompleted);
			Assert.True(account.IsProviderAccountChangeCommitInProgress);
			Assert.False(account.CanCancelProviderAccountConnection);
			Assert.Null(account.Profile.ProviderAccountIdentity);
			Assert.Empty(profileStore.SavedSnapshots);
			Assert.Empty(codexBindingStore.Bindings);

			drainTask = coordinator.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));

			Assert.False(drainTask.IsCompleted);
			Assert.False(coordinator.TryCancelAccountConnection(account.Id));
		}
		finally
		{
			allowDisposalToFinish.TrySetResult();

			if (drainTask is not null)
			{
				wasFullyDrained =
					await drainTask.WaitAsync(AsyncWatchdogTimeout);
				await connectionTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.True(wasFullyDrained);
		Assert.Equal(0, refreshCount);
		CodexWorkspaceBinding binding =
			Assert.Single(codexBindingStore.Bindings);
		Assert.True(binding.MatchesEntitlement(
			NewIdentity,
			CodexWorkspaceId));
		string publicBindingIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		Assert.Equal(
			publicBindingIdentity,
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			publicBindingIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(account.IsProviderAccountChangeCommitInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenProfileSaveHasStarted_CommitCannotBeCancelled()
	{
		const string NewIdentity = "connected@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		TaskCompletionSource saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowSaveToFinish = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = async (_, _) =>
			{
				saveStarted.TrySetResult();
				await allowSaveToFinish.Task;
			}
		};
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (refreshAccount, _) => Task.FromResult(
				CreateReadySnapshot(refreshAccount, NewIdentity))
		};
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
			Task.FromResult(new ClaudeAccountLoginResult(NewIdentity)));
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		List<string> statuses = new();
		Task connectionTask = coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		try
		{
			await saveStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(connectionTask.IsCompleted);
			Assert.True(account.IsProviderAccountChangeInProgress);
			Assert.True(account.IsProviderAccountChangeCommitInProgress);
			Assert.False(account.CanCancelProviderAccountConnection);
			Assert.False(account.CanInvokeProviderAccountAction);
			Assert.Equal(
				"正在完成 Claude 帳號連接",
				account.ClaudeAccountActionText);
			Assert.True(coordinator.IsAccountConnectionInProgress(account.Id));
			Assert.False(coordinator.TryCancelAccountConnection(account.Id));
		}
		finally
		{
			allowSaveToFinish.TrySetResult();
		}

		await connectionTask.WaitAsync(AsyncWatchdogTimeout);

		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.False(account.IsProviderAccountChangeCommitInProgress);
		Assert.False(coordinator.IsAccountConnectionInProgress(account.Id));
		Assert.Equal(NewIdentity, account.ProviderAccountIdentity);
		Assert.Equal(NewIdentity, account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			NewIdentity,
			Assert.Single(Assert.Single(profileStore.SavedSnapshots))
				.ProviderAccountIdentity);
		Assert.Equal(
			NewIdentity,
			Assert.Single(snapshotStore.SavedSnapshots)
				.ProviderAccountIdentity);
		Assert.Contains(
			statuses,
			status => status.Contains("已連接", StringComparison.Ordinal));
		Assert.DoesNotContain(
			statuses,
			status => status.Contains("已取消", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenPromptDisablesAccount_DoesNotSaveConsentOrLogin()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult("unused@example.com"));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() =>
			{
				account.ApplyProfile(
					profile with { IsEnabled = false },
					canManage: true);
				return MessageBoxResult.Yes;
			});

		Assert.Equal(0, loginCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.False(account.IsEnabled);
		Assert.False(account.Profile.HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenPromptRemovesAccount_DoesNotSaveConsentOrLogin()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		FakeAccountProfileStore profileStore = new(profile);
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult("unused@example.com"));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() =>
			{
				viewModel.Accounts.Remove(account);
				return MessageBoxResult.Yes;
			});

		Assert.Equal(0, loginCount);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(viewModel.Accounts);
		Assert.False(account.Profile.HasAcceptedClaudeQuotaRisk);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task CancelAndDrain_WhenClaudeConsentSaveIsRunning_WaitsForSaveCleanup()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude");
		TaskCompletionSource saveStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cleanupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		FakeAccountProfileStore profileStore = new(profile)
		{
			SaveHandler = async (_, token) =>
			{
				saveStarted.TrySetResult();

				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, token);
				}
				catch (OperationCanceledException)
					when (token.IsCancellationRequested)
				{
					cleanupStarted.TrySetResult();
					await allowCleanup.Task;
					throw;
				}
			}
		};
		DashboardViewModel viewModel = new(
			profileStore,
			new FakeUsageRefreshCoordinator());
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		int loginCount = 0;
		DelegateClaudeAccountLogin claudeLogin = new(_ =>
		{
			loginCount++;
			return Task.FromResult(
				new ClaudeAccountLoginResult("unused@example.com"));
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		Task connectionTask = coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => MessageBoxResult.Yes);
		Task<bool>? drainTask = null;
		bool wasFullyDrained = false;

		try
		{
			await saveStarted.Task.WaitAsync(AsyncWatchdogTimeout);
			drainTask = coordinator.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));
			await cleanupStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(drainTask.IsCompleted);
		}
		finally
		{
			allowCleanup.TrySetResult();

			if (drainTask is not null)
			{
				wasFullyDrained =
					await drainTask.WaitAsync(AsyncWatchdogTimeout);
				await connectionTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.True(wasFullyDrained);
		Assert.Equal(0, loginCount);
		Assert.False(account.Profile.HasAcceptedClaudeQuotaRisk);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task CancelAndDrain_WhenClaudeLoginIsRunning_WaitsForLoginCleanup()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			HasAcceptedClaudeQuotaRisk: true);
		DashboardViewModel viewModel = await CreateDashboardAsync(
			new FakeUsageRefreshCoordinator(),
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		TaskCompletionSource loginStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cleanupStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DelegateClaudeAccountLogin claudeLogin = new(async token =>
		{
			loginStarted.TrySetResult();

			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
				return new ClaudeAccountLoginResult("unused@example.com");
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				cleanupStarted.TrySetResult();
				await allowCleanup.Task;
				throw;
			}
		});
		using AccountConnectionCoordinator coordinator = new(
			claudeLogin,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled));
		Task connectionTask = await StartOnStaThreadAsync(
			() => coordinator.ConnectClaudeAsync(
				account,
				viewModel,
				() => throw new InvalidOperationException(
					"Persisted consent must skip the prompt.")));
		Task<bool>? drainTask = null;
		bool wasFullyDrained = false;
		try
		{
			await loginStarted.Task.WaitAsync(AsyncWatchdogTimeout);
			drainTask = coordinator.CancelAndDrainAccountConnectionsAsync(
				TimeSpan.FromSeconds(7));
			await cleanupStarted.Task.WaitAsync(AsyncWatchdogTimeout);

			Assert.False(drainTask.IsCompleted);
		}
		finally
		{
			allowCleanup.TrySetResult();

			if (drainTask is not null)
			{
				wasFullyDrained =
					await drainTask.WaitAsync(AsyncWatchdogTimeout);
				await connectionTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.True(wasFullyDrained);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task RunAntigravitySetupAsync_WithLegacySecondEnabledAccount_FailsClosed()
	{
		AccountProfile firstProfile = CreateAntigravityProfile("第一個 AGY");
		AccountProfile secondProfile = CreateAntigravityProfile("第二個 AGY");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			firstProfile,
			secondProfile);
		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		FakeAntigravityAccountSetupLauncher launcher = new(
			AntigravityAccountSetupOutcome.CompletedOfficialPrint);
		using AccountConnectionCoordinator coordinator =
			CreateCoordinator(launcher);

		await coordinator.RunAntigravitySetupAsync(
			secondAccount,
			viewModel);

		Assert.True(firstAccount.CanConfigureAntigravityAccount);
		Assert.True(firstAccount.ShowAntigravityDefaultConnectionAction);
		Assert.False(secondAccount.CanConfigureAntigravityAccount);
		Assert.False(secondAccount.CanConnectProviderAccount);
		Assert.False(secondAccount.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(0, launcher.RunCount);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(secondAccount.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public void GetClaudeLoginCompletionNotice_WhenSafetyRetryStops_PointsToImmediateRetry()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Claude,
			"Claude",
			ProviderAccountIdentity: "claude@example.com",
			HasAcceptedClaudeQuotaRisk: true);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "測試用安全失敗。",
			ProviderAccountIdentity: "claude@example.com",
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		(string message, string caption, MessageBoxImage image) =
			AccountConnectionCoordinator.GetClaudeLoginCompletionNotice(account);

		Assert.Contains("上次用量檢查未完成", message);
		Assert.Contains("重新檢查 Claude 用量", message);
		Assert.Equal("Claude 用量檢查已暫停", caption);
		Assert.Equal(MessageBoxImage.Warning, image);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenLoginAndProbeSucceed_CommitsDurableStateInOrder()
	{
		const string RawPrincipalId = "raw-principal-must-never-be-displayed";
		List<string> operations = new();
		List<string> statuses = new();
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		profileStore.SaveHandler = (_, cancellationToken) =>
		{
			Assert.False(cancellationToken.CanBeCanceled);
			operations.Add("profile:save");
			return Task.CompletedTask;
		};
		refreshCoordinator.RefreshHandler = (refreshedProfile, _) =>
		{
			operations.Add("refresh");
			return Task.FromResult(new UsageSnapshot(
				refreshedProfile,
				new[]
				{
					new UsageMetric(
						"grok.weekly",
						"每週用量",
						12,
						"已使用 12%",
						DateTimeOffset.UtcNow.AddDays(5))
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					refreshedProfile.ProviderAccountIdentity));
		};
		DelegateGrokAccountLogin login = new(_ =>
		{
			operations.Add("login");
			return Task.CompletedTask;
		});
		DelegateGrokUsagePoller poller = new(cancellationToken =>
		{
			Assert.False(cancellationToken.CanBeCanceled);
			operations.Add("poll");
			return Task.FromResult(CreateGrokPollResult(RawPrincipalId));
		});
		FakeGrokAccountBindingStore bindingStore = new(operations);
		FakeGrokConnectionPendingStore pendingStore = new(operations);
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				login,
				poller,
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			statuses.Add);

		GrokAccountBinding binding = Assert.IsType<GrokAccountBinding>(
			bindingStore.SavedBinding);
		string expectedPublicIdentity = binding.PublicBindingId.ToString("N");
		Assert.True(binding.MatchesPrincipal("user", RawPrincipalId));
		Assert.Equal(pendingStore.LastAttemptId, binding.PublicBindingId);
		Assert.Equal(expectedPublicIdentity, account.Profile.ProviderAccountIdentity);
		Assert.True(pendingStore.WasRemoved);
		Assert.Equal(
			new[]
			{
				"journal:LoginStarted",
				"login",
				"journal:LoginCompleted",
				"poll",
				"binding:save",
				"journal:BindingPersisted",
				"profile:save",
				"journal:remove",
				"refresh"
			},
			operations);
		Assert.DoesNotContain(
			statuses,
			status => status.Contains(RawPrincipalId, StringComparison.Ordinal));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenBindingGateIsBusy_AcquiresAccountMutationGateFirst()
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
		FakeGrokAccountBindingStore bindingStore = new();
		FakeGrokConnectionPendingStore pendingStore = new();
		GrokBindingCommitGate bindingGate = new();
		using AccountConnectionCoordinator coordinator = new(
			new UnusedClaudeAccountLogin(),
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.CompletedOfficialPrint),
			new DelegateGrokAccountLogin(_ => Task.CompletedTask),
			new DelegateGrokUsagePoller(_ => Task.FromResult(
				CreateGrokPollResult("lock-order-principal"))),
			bindingStore,
			pendingStore,
			bindingGate);
		IDisposable blockingBindingLease = await bindingGate.EnterAsync();
		Task? connectionTask = null;

		try
		{
			connectionTask = coordinator.ConnectGrokAsync(account, viewModel);

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
				"Grok commit 必須先取得 account mutation gate，再等待 binding gate。");
		}
		finally
		{
			blockingBindingLease.Dispose();

			if (connectionTask is not null)
			{
				await connectionTask.WaitAsync(AsyncWatchdogTimeout);
			}
		}

		Assert.NotNull(bindingStore.GetBinding(profile.Id));
	}

	[Theory]
	[InlineData(
		nameof(GrokUsageAvailability.Unsupported),
		nameof(GrokCurrentAuthUsability.Usable),
		true)]
	[InlineData(
		nameof(GrokUsageAvailability.TransientProbeError),
		nameof(GrokCurrentAuthUsability.Usable),
		true)]
	[InlineData(
		nameof(GrokUsageAvailability.AuthenticationRequired),
		nameof(GrokCurrentAuthUsability.Unusable),
		false)]
	[InlineData(
		nameof(GrokUsageAvailability.TransientProbeError),
		nameof(GrokCurrentAuthUsability.Unknown),
		false)]
	public async Task ConnectGrokAsync_CommitsOnlyWhenCurrentAuthenticationIsUsable(
		string usageAvailabilityName,
		string currentAuthUsabilityName,
		bool expectsCommit)
	{
		GrokUsageAvailability usageAvailability = Enum.Parse<GrokUsageAvailability>(
			usageAvailabilityName);
		GrokCurrentAuthUsability currentAuthUsability =
			Enum.Parse<GrokCurrentAuthUsability>(currentAuthUsabilityName);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokAccountBindingStore bindingStore = new();
		FakeGrokConnectionPendingStore pendingStore = new();
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ => Task.FromResult(
					CreateGrokPollResult(
						"principal",
						usageAvailability,
						currentAuthUsability))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		if (expectsCommit)
		{
			GrokAccountBinding binding = Assert.IsType<GrokAccountBinding>(
				bindingStore.SavedBinding);
			Assert.Equal(
				binding.PublicBindingId.ToString("N"),
				account.Profile.ProviderAccountIdentity);
			Assert.Null(pendingStore.Work);
			Assert.True(pendingStore.WasRemoved);
		}
		else
		{
			Assert.Null(bindingStore.SavedBinding);
			Assert.Null(account.Profile.ProviderAccountIdentity);
			Assert.Equal(
				GrokConnectionPendingStage.LoginCompleted,
				Assert.IsType<GrokConnectionPendingWork>(pendingStore.Work).Stage);
			Assert.Contains(
				notices,
				notice => notice.Caption == "Grok 帳號尚未驗證");
		}

		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenCliPreflightCannotFindExecutable_RemovesLoginStartedJournal()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokConnectionPendingStore pendingStore = new();
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.FromException(
					new GrokCliNotFoundException("synthetic preflight failure"))),
				new DelegateGrokUsagePoller(_ =>
					throw new InvalidOperationException(
						"Preflight failure must not poll Grok.")),
				new FakeGrokAccountBindingStore(),
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		Assert.True(pendingStore.WasRemoved);
		Assert.Null(pendingStore.Work);
		Assert.Contains(
			notices,
			notice => notice.Caption == "需要安裝或更新 Grok Build CLI");
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenProcessContainmentIsCompromised_RequiresApplicationRestart()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokConnectionPendingStore pendingStore = new();
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.FromException(
					new GrokProcessContainmentException(
						"synthetic containment failure"))),
				new DelegateGrokUsagePoller(_ =>
					throw new InvalidOperationException(
						"Containment failure must not poll Grok.")),
				new FakeGrokAccountBindingStore(),
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		GrokConnectionPendingWork work =
			Assert.IsType<GrokConnectionPendingWork>(pendingStore.Work);
		Assert.Equal(GrokConnectionPendingStage.LoginStarted, work.Stage);
		Assert.Contains(
			notices,
			notice =>
				(notice.Caption == "必須重新啟動 AI Usage") &&
				notice.Message.Contains(
					"重新啟動 AI Usage",
					StringComparison.Ordinal) &&
				(notice.Image == MessageBoxImage.Error));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenInteractiveLoginIsCancelled_PreservesAmbiguousLoginStartedJournal()
	{
		TaskCompletionSource loginEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokConnectionPendingStore pendingStore = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(async cancellationToken =>
				{
					loginEntered.TrySetResult();
					await Task.Delay(
						Timeout.InfiniteTimeSpan,
						cancellationToken);
				}),
				new DelegateGrokUsagePoller(_ =>
					throw new InvalidOperationException(
						"Cancelled login must not poll Grok.")),
				new FakeGrokAccountBindingStore(),
				pendingStore,
				TimeSpan.FromSeconds(30));

		Task connectionTask = coordinator.ConnectGrokAsync(account, viewModel);
		await loginEntered.Task.WaitAsync(AsyncWatchdogTimeout);
		Assert.True(coordinator.TryCancelAccountConnection(account.Id));
		await connectionTask.WaitAsync(AsyncWatchdogTimeout);

		GrokConnectionPendingWork work =
			Assert.IsType<GrokConnectionPendingWork>(pendingStore.Work);
		Assert.Equal(GrokConnectionPendingStage.LoginStarted, work.Stage);
		Assert.False(pendingStore.WasRemoved);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenRecoverableJournalExists_DoesNotStartOrOverwriteLogin()
	{
		int loginCallCount = 0;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = await CreateDashboardAsync(
			refreshCoordinator,
			profile);
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokConnectionPendingStore pendingStore = new()
		{
			BeginResult =
				GrokConnectionBeginResult.BlockedByExistingRecoverableWork
		};
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ =>
				{
					loginCallCount++;
					return Task.CompletedTask;
				}),
				new DelegateGrokUsagePoller(_ =>
					throw new InvalidOperationException(
						"Blocked login must not poll Grok.")),
				new FakeGrokAccountBindingStore(),
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		Assert.Equal(0, loginCallCount);
		Assert.False(pendingStore.WasRemoved);
		Assert.Contains(
			notices,
			notice => notice.Caption == "Grok 帳號連接仍待恢復");
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_AfterExternalSuccess_RejectsCancelAndUsesCommitBarrier()
	{
		TaskCompletionSource pollEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releasePoll = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		refreshCoordinator.RefreshHandler = (refreshedProfile, _) =>
			Task.FromResult(new UsageSnapshot(
				refreshedProfile,
				new[]
				{
					new UsageMetric(
						"grok.weekly",
						"每週用量",
						1,
						"已使用 1%",
						DateTimeOffset.UtcNow.AddDays(5))
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity:
					refreshedProfile.ProviderAccountIdentity));
		DelegateGrokUsagePoller poller = new(async cancellationToken =>
		{
			Assert.False(cancellationToken.CanBeCanceled);
			pollEntered.TrySetResult();
			await releasePoll.Task;
			return CreateGrokPollResult("principal-after-login");
		});
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				poller,
				new FakeGrokAccountBindingStore(),
				new FakeGrokConnectionPendingStore(),
				TimeSpan.FromSeconds(30));

		Task connectTask = coordinator.ConnectGrokAsync(account, viewModel);
		await pollEntered.Task.WaitAsync(AsyncWatchdogTimeout);

		Assert.True(account.IsProviderAccountChangeCommitInProgress);
		Assert.False(coordinator.TryCancelAccountConnection(account.Id));

		releasePoll.TrySetResult();
		await connectTask.WaitAsync(AsyncWatchdogTimeout);
		Assert.NotNull(account.Profile.ProviderAccountIdentity);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenBindingSaveFails_RetainsLoginCompletedJournalAndOldProfile()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		FakeGrokAccountBindingStore bindingStore = new()
		{
			SaveHandler = (_, _) => throw new IOException(
				"synthetic binding persistence failure")
		};
		FakeGrokConnectionPendingStore pendingStore = new();
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ =>
					Task.FromResult(CreateGrokPollResult("principal"))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		Assert.NotNull(pendingStore.Work);
		Assert.Equal(
			GrokConnectionPendingStage.LoginCompleted,
			pendingStore.Work.Stage);
		Assert.Null(pendingStore.Work.PublicBindingId);
		Assert.False(pendingStore.WasRemoved);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			notices,
			notice =>
				(notice.Caption == "Grok 帳號連接未完成") &&
				notice.Message.Contains(
					"重新啟動後會自動繼續處理",
					StringComparison.Ordinal) &&
				!notice.Message.Contains(
					"重新啟動後會自動完成",
					StringComparison.Ordinal));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenPrincipalIsAlreadyBoundToAnotherCard_RejectsDuplicateBinding()
	{
		const string PrincipalId = "shared-principal";
		GrokAccountBinding firstBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			PrincipalId);
		AccountProfile firstProfile = new(
			firstBinding.AccountId,
			ProviderKind.Grok,
			"Grok 1",
			ProviderAccountIdentity:
				GrokAccountBinding.CreatePublicBindingIdentity(
					firstBinding.PublicBindingId));
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok 2");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		FakeGrokAccountBindingStore bindingStore = new();
		bindingStore.Seed(firstBinding);
		FakeGrokConnectionPendingStore pendingStore = new();
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ =>
					Task.FromResult(CreateGrokPollResult(PrincipalId))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			secondAccount,
			viewModel,
			statuses.Add);

		Assert.Null(secondAccount.Profile.ProviderAccountIdentity);
		Assert.Null(bindingStore.SavedBinding);
		Assert.True(pendingStore.WasRemoved);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"已連接到另一張帳號卡片",
				StringComparison.Ordinal));
		Assert.False(secondAccount.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenMatchingOrphanBindingExists_RemovesOrphanBeforeCommit()
	{
		const string PrincipalId = "orphaned-principal";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			PrincipalId);
		FakeGrokAccountBindingStore bindingStore = new();
		bindingStore.Seed(orphanBinding);
		FakeGrokConnectionPendingStore pendingStore = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ =>
					Task.FromResult(CreateGrokPollResult(PrincipalId))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(account, viewModel);

		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		GrokAccountBinding committedBinding = Assert.IsType<GrokAccountBinding>(
			bindingStore.GetBinding(account.Id));
		Assert.True(committedBinding.MatchesPrincipal("user", PrincipalId));
		Assert.Equal(
			GrokAccountBinding.CreatePublicBindingIdentity(
				committedBinding.PublicBindingId),
			account.Profile.ProviderAccountIdentity);
		Assert.True(pendingStore.WasRemoved);
		Assert.Single(refreshCoordinator.RefreshRequests);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenOrphanBindingCleanupFails_DoesNotCommitNewBinding()
	{
		const string PrincipalId = "orphaned-principal";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		GrokAccountBinding orphanBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			PrincipalId);
		FakeGrokAccountBindingStore bindingStore = new()
		{
			DeleteHandler = (_, _) => throw new IOException(
				"synthetic orphan cleanup failure")
		};
		bindingStore.Seed(orphanBinding);
		FakeGrokConnectionPendingStore pendingStore = new();
		List<(string Message, string Caption, MessageBoxImage Image)> notices =
			new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ =>
					Task.FromResult(CreateGrokPollResult(PrincipalId))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			account,
			viewModel,
			reportNotice: (message, caption, image) =>
				notices.Add((message, caption, image)));

		Assert.Same(
			orphanBinding,
			bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Null(bindingStore.GetBinding(account.Id));
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			GrokConnectionPendingStage.LoginCompleted,
			Assert.IsType<GrokConnectionPendingWork>(pendingStore.Work).Stage);
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			notices,
			notice => notice.Caption == "Grok 帳號連接未完成");
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenPrincipalIsAlreadyBoundAndTargetWasBound_FailsClosed()
	{
		const string DuplicatePrincipalId = "shared-principal";
		GrokAccountBinding ownerBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			DuplicatePrincipalId);
		GrokAccountBinding targetBinding = GrokAccountBinding.Create(
			Guid.NewGuid(),
			"user",
			"old-principal");
		AccountProfile ownerProfile = new(
			ownerBinding.AccountId,
			ProviderKind.Grok,
			"Grok owner",
			ProviderAccountIdentity:
				GrokAccountBinding.CreatePublicBindingIdentity(
					ownerBinding.PublicBindingId));
		AccountProfile targetProfile = new(
			targetBinding.AccountId,
			ProviderKind.Grok,
			"Grok target",
			ProviderAccountIdentity:
				GrokAccountBinding.CreatePublicBindingIdentity(
					targetBinding.PublicBindingId));
		FakeAccountProfileStore profileStore = new(ownerProfile, targetProfile);
		FakeUsageSnapshotStore snapshotStore = new();
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = Assert.Single(
			viewModel.Accounts,
			account => account.Id == targetProfile.Id);
		targetAccount.ApplySnapshot(CreateReadySnapshot(
			targetAccount.Profile,
			targetProfile.ProviderAccountIdentity));
		FakeGrokAccountBindingStore bindingStore = new();
		bindingStore.Seed(ownerBinding);
		bindingStore.Seed(targetBinding);
		FakeGrokConnectionPendingStore pendingStore = new();
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				new DelegateGrokUsagePoller(_ => Task.FromResult(
					CreateGrokPollResult(DuplicatePrincipalId))),
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		await coordinator.ConnectGrokAsync(
			targetAccount,
			viewModel,
			statuses.Add);

		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(targetAccount.ProviderAccountIdentity);
		Assert.Null(targetAccount.CurrentSnapshot);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
		Assert.NotNull(bindingStore.GetBinding(ownerProfile.Id));
		Assert.Null(bindingStore.GetBinding(targetProfile.Id));
		Assert.True(pendingStore.WasRemoved);
		AccountProfile[] savedProfiles = Assert.Single(
			profileStore.SavedSnapshots).ToArray();
		Assert.Equal(
			ownerProfile.ProviderAccountIdentity,
			Assert.Single(savedProfiles, profile => profile.Id == ownerProfile.Id)
				.ProviderAccountIdentity);
		Assert.Null(
			Assert.Single(savedProfiles, profile => profile.Id == targetProfile.Id)
				.ProviderAccountIdentity);
		Assert.Contains(targetProfile.Id, refreshCoordinator.InvalidatedAccountIds);
		Assert.Contains(targetProfile.Id, snapshotStore.DeletedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"已連接到另一張帳號卡片",
				StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConnectGrokAsync_WhenBindingPersistedConflictJournalRemovalFails_RestoresExactBinding(
		bool shouldThrowWhenRemovingJournal)
	{
		GrokBindingPersistedConflictScenario scenario =
			await RunGrokBindingPersistedConflictScenarioAsync(
				shouldThrowWhenRemovingJournal,
				shouldThrowWhenRestoringBinding: false);

		Assert.Equal(2, scenario.BindingSaveCount);
		Assert.Equal(
			scenario.CreatedBinding,
			scenario.BindingStore.GetBinding(scenario.TargetAccount.Id));
		GrokConnectionPendingWork work =
			Assert.IsType<GrokConnectionPendingWork>(scenario.PendingStore.Work);
		Assert.Equal(GrokConnectionPendingStage.BindingPersisted, work.Stage);
		Assert.Equal(scenario.CreatedBinding.PublicBindingId, work.PublicBindingId);
		Assert.False(scenario.PendingStore.WasRemoved);
		Assert.Null(scenario.TargetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(scenario.TargetAccount.ProviderAccountIdentity);
		Assert.False(scenario.TargetAccount.IsProviderAccountChangeInProgress);
		Assert.Contains(
			scenario.Diagnostics,
			diagnostic => diagnostic.Contains(
				"result=binding-restored",
				StringComparison.Ordinal));
		Assert.Contains(
			"Grok 帳號連接尚未完成",
			scenario.ViewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			scenario.Statuses,
			status => status.Contains(
				"本機清理仍待完成",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenBindingPersistedConflictBindingRestoreFails_MarksRecoveryPending()
	{
		GrokBindingPersistedConflictScenario scenario =
			await RunGrokBindingPersistedConflictScenarioAsync(
				shouldThrowWhenRemovingJournal: false,
				shouldThrowWhenRestoringBinding: true);

		Assert.Equal(2, scenario.BindingSaveCount);
		Assert.Null(
			scenario.BindingStore.GetBinding(scenario.TargetAccount.Id));
		GrokConnectionPendingWork work =
			Assert.IsType<GrokConnectionPendingWork>(scenario.PendingStore.Work);
		Assert.Equal(GrokConnectionPendingStage.BindingPersisted, work.Stage);
		Assert.Equal(scenario.CreatedBinding.PublicBindingId, work.PublicBindingId);
		Assert.False(scenario.PendingStore.WasRemoved);
		Assert.Null(scenario.TargetAccount.Profile.ProviderAccountIdentity);
		Assert.Null(scenario.TargetAccount.ProviderAccountIdentity);
		Assert.False(scenario.TargetAccount.IsProviderAccountChangeInProgress);
		Assert.Contains(
			scenario.Diagnostics,
			diagnostic => diagnostic.Contains(
				"result=binding-restore-failed",
				StringComparison.Ordinal));
		Assert.Contains(
			"Grok 帳號連接尚未完成",
			scenario.ViewModel.AccountSettingsHealthMessage,
			StringComparison.Ordinal);
		Assert.Contains(
			scenario.Statuses,
			status => status.Contains(
				"也無法還原原本的 Grok 連接資料",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenTwoCardsConcurrentlyUseSamePrincipal_CommitsOnlyOneBinding()
	{
		const string PrincipalId = "concurrent-shared-principal";
		AccountProfile firstProfile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok 1");
		AccountProfile secondProfile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Grok 2");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new()
		{
			RefreshHandler = (profile, _) => Task.FromResult(new UsageSnapshot(
				profile,
				new[]
				{
					new UsageMetric(
						"grok.weekly",
						"每週用量",
						1,
						"已使用 1%",
						DateTimeOffset.UtcNow.AddDays(5))
				},
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: profile.ProviderAccountIdentity))
		};
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		TaskCompletionSource bothPollsEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releasePolls = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int pollCount = 0;
		DelegateGrokUsagePoller poller = new(async (_, cancellationToken) =>
		{
			Assert.False(cancellationToken.CanBeCanceled);
			if (Interlocked.Increment(ref pollCount) == 2)
			{
				bothPollsEntered.TrySetResult();
			}

			await releasePolls.Task;
			return CreateGrokPollResult(PrincipalId);
		});
		FakeGrokAccountBindingStore bindingStore = new();
		FakeGrokConnectionPendingStore pendingStore = new();
		List<string> firstStatuses = new();
		List<string> secondStatuses = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				poller,
				bindingStore,
				pendingStore,
				TimeSpan.FromSeconds(30));

		Task firstConnect = coordinator.ConnectGrokAsync(
			firstAccount,
			viewModel,
			firstStatuses.Add);
		Task secondConnect = coordinator.ConnectGrokAsync(
			secondAccount,
			viewModel,
			secondStatuses.Add);
		await bothPollsEntered.Task.WaitAsync(AsyncWatchdogTimeout);
		releasePolls.TrySetResult();
		await Task.WhenAll(firstConnect, secondConnect)
			.WaitAsync(AsyncWatchdogTimeout);

		GrokAccountBinding? firstBinding = bindingStore.GetBinding(firstProfile.Id);
		GrokAccountBinding? secondBinding = bindingStore.GetBinding(secondProfile.Id);
		Assert.Equal(1, new[] { firstBinding, secondBinding }.Count(
			binding => binding is not null));
		Assert.Equal(1, viewModel.Accounts.Count(
			account => account.Profile.ProviderAccountIdentity is not null));
		Assert.Null(pendingStore.GetWork(firstProfile.Id));
		Assert.Null(pendingStore.GetWork(secondProfile.Id));
		Assert.Contains(
			firstStatuses.Concat(secondStatuses),
			status => status.Contains(
				"已連接到另一張帳號卡片",
				StringComparison.Ordinal));
		Assert.Single(refreshCoordinator.RefreshRequests);
		Assert.All(
			viewModel.Accounts,
			account => Assert.False(account.IsProviderAccountChangeInProgress));
	}

	[Fact]
	public async Task ConnectGrokAsync_WhenStartupRecoveryUsesSamePrincipal_CommitsOnlyOneBinding()
	{
		const string PrincipalId = "recovery-and-live-shared-principal";
		AccountProfile recoveryProfile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Recovered Grok");
		AccountProfile liveProfile = new(
			Guid.NewGuid(),
			ProviderKind.Grok,
			"Live Grok");
		FakeAccountProfileStore profileStore = new(
			recoveryProfile,
			liveProfile);
		FakeUsageRefreshCoordinator refreshCoordinator = new();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel liveAccount = viewModel.Accounts.Single(
			account => account.Id == liveProfile.Id);
		TaskCompletionSource bothPollsEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releasePolls = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int pollCount = 0;
		DelegateGrokUsagePoller poller = new(async (_, _) =>
		{
			if (Interlocked.Increment(ref pollCount) == 2)
			{
				bothPollsEntered.TrySetResult();
			}

			await releasePolls.Task;
			return CreateGrokPollResult(PrincipalId);
		});
		FakeGrokAccountBindingStore bindingStore = new();
		FakeGrokConnectionPendingStore pendingStore = new();
		Guid recoveryAttemptId = Guid.NewGuid();
		DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
		Assert.Equal(
			GrokConnectionBeginResult.Started,
			await pendingStore.BeginAsync(
				recoveryProfile.Id,
				recoveryAttemptId,
				startedAtUtc));
		Assert.True(await pendingStore.TryAdvanceAsync(
			recoveryProfile.Id,
			recoveryAttemptId,
			GrokConnectionPendingStage.LoginCompleted,
			publicBindingId: null,
			DateTimeOffset.UtcNow));
		GrokBindingCommitGate bindingCommitGate = new();
		using AccountConnectionCoordinator coordinator =
			new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				new FakeAntigravityAccountSetupLauncher(
					AntigravityAccountSetupOutcome.CompletedOfficialPrint),
				new DelegateGrokAccountLogin(_ => Task.CompletedTask),
				poller,
				bindingStore,
				pendingStore,
				bindingCommitGate);

		Task recoveryTask = GrokStartupRecovery
			.RecoverGrokConnectionPendingWorkAsync(
				viewModel,
				bindingStore,
				pendingStore,
				poller,
				bindingCommitGate: bindingCommitGate);
		Task liveConnectTask = coordinator.ConnectGrokAsync(
			liveAccount,
			viewModel);
		await bothPollsEntered.Task.WaitAsync(AsyncWatchdogTimeout);
		releasePolls.TrySetResult();
		await Task.WhenAll(recoveryTask, liveConnectTask)
			.WaitAsync(AsyncWatchdogTimeout);

		GrokAccountBinding? recoveryBinding =
			bindingStore.GetBinding(recoveryProfile.Id);
		GrokAccountBinding? liveBinding =
			bindingStore.GetBinding(liveProfile.Id);
		Assert.Equal(1, new[] { recoveryBinding, liveBinding }.Count(
			binding => binding is not null));
		Assert.Equal(1, viewModel.Accounts.Count(
			account => account.Profile.ProviderAccountIdentity is not null));
		Assert.Null(pendingStore.GetWork(recoveryProfile.Id));
		Assert.Null(pendingStore.GetWork(liveProfile.Id));
		Assert.All(
			viewModel.Accounts,
			account => Assert.False(account.IsProviderAccountChangeInProgress));
	}

	[Theory]
	[InlineData(
		"shared@example.com",
		"org-personal",
		"shared@example.com",
		"org-company")]
	[InlineData(
		"first@example.com",
		"org-shared",
		"second@example.com",
		"org-shared")]
	public async Task ConnectClaudeAsync_WhenOnlyOneEntitlementComponentMatches_BindsBothCards(
		string firstEmail,
		string firstOrganizationId,
		string secondEmail,
		string secondOrganizationId)
	{
		AccountProfile firstProfile = CreateClaudeProfile("Claude 1");
		AccountProfile secondProfile = CreateClaudeProfile("Claude 2");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		ClaudeSubscriptionContext firstContext = CreateClaudeSubscriptionContext(
			firstEmail,
			firstOrganizationId);
		ClaudeSubscriptionContext secondContext = CreateClaudeSubscriptionContext(
			secondEmail,
			secondOrganizationId);
		DelegateClaudeAccountLogin login = new((accountId, _) =>
			Task.FromResult(CreateClaudeLoginResult(
				accountId == firstProfile.Id ? firstContext : secondContext)));
		FakeClaudeAccountBindingStore bindingStore = new();
		ClaudeBindingCommitGate bindingCommitGate = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				login,
				bindingStore,
				bindingCommitGate);

		await coordinator.ConnectClaudeAsync(
			firstAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));
		await coordinator.ConnectClaudeAsync(
			secondAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		ClaudeAccountBinding firstBinding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(firstProfile.Id));
		ClaudeAccountBinding secondBinding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(secondProfile.Id));
		Assert.True(firstBinding.MatchesEntitlement(firstContext));
		Assert.True(secondBinding.MatchesEntitlement(secondContext));
		Assert.False(firstBinding.MatchesEntitlement(secondContext));
		Assert.False(secondBinding.MatchesEntitlement(firstContext));
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				firstBinding.PublicBindingId),
			firstAccount.Profile.ProviderAccountIdentity);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				secondBinding.PublicBindingId),
			secondAccount.Profile.ProviderAccountIdentity);
		Assert.NotEqual(
			firstAccount.Profile.ProviderAccountIdentity,
			secondAccount.Profile.ProviderAccountIdentity);
		Assert.Contains(
			refreshCoordinator.RefreshRequests,
			request =>
				(request.Id == firstProfile.Id) &&
				(request.ProviderAccountIdentity ==
					firstAccount.Profile.ProviderAccountIdentity));
		Assert.Contains(
			refreshCoordinator.RefreshRequests,
			request =>
				(request.Id == secondProfile.Id) &&
				(request.ProviderAccountIdentity ==
					secondAccount.Profile.ProviderAccountIdentity));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenSecondCardUsesSameEntitlement_FailsClosed()
	{
		AccountProfile firstProfile = CreateClaudeProfile("Claude 1");
		AccountProfile secondProfile = CreateClaudeProfile("Claude 2");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"shared@example.com",
			"org-shared");
		DelegateClaudeAccountLogin login = new((_, _) =>
			Task.FromResult(CreateClaudeLoginResult(context)));
		FakeClaudeAccountBindingStore bindingStore = new();
		ClaudeBindingCommitGate bindingCommitGate = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				login,
				bindingStore,
				bindingCommitGate);
		List<string> secondStatuses = new();

		await coordinator.ConnectClaudeAsync(
			firstAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));
		await coordinator.ConnectClaudeAsync(
			secondAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			secondStatuses.Add);

		Assert.NotNull(bindingStore.GetBinding(firstProfile.Id));
		Assert.Null(bindingStore.GetBinding(secondProfile.Id));
		Assert.NotNull(firstAccount.Profile.ProviderAccountIdentity);
		Assert.Null(secondAccount.Profile.ProviderAccountIdentity);
		Assert.Contains(secondProfile.Id, bindingStore.DeletedAccountIds);
		Assert.Single(await bindingStore.LoadAllAsync());
		Assert.Contains(
			refreshCoordinator.RefreshRequests,
			request =>
				(request.Id == firstProfile.Id) &&
				(request.ProviderAccountIdentity ==
					firstAccount.Profile.ProviderAccountIdentity));
		Assert.DoesNotContain(
			refreshCoordinator.RefreshRequests,
			request =>
				(request.Id == secondProfile.Id) &&
				(request.ProviderAccountIdentity is not null));
		Assert.Contains(
			secondStatuses,
			status => status.Contains(
				"訂閱範圍已連接到另一張帳號卡片",
				StringComparison.Ordinal));
		Assert.False(secondAccount.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenOrphanInventoryBindingOwnsEntitlement_DoesNotBlockConnection()
	{
		AccountProfile profile = CreateClaudeProfile("Claude");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"orphan@example.com",
			"org-orphan");
		ClaudeAccountBinding orphanBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			context);
		FakeClaudeAccountBindingStore bindingStore = new();
		bindingStore.Seed(orphanBinding);
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		Assert.Null(bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Contains(
			orphanBinding.AccountId,
			bindingStore.DeletedAccountIds);
		ClaudeAccountBinding activeBinding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(profile.Id));
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				activeBinding.PublicBindingId),
			account.Profile.ProviderAccountIdentity);
		Assert.Single(await bindingStore.LoadAllAsync());
		Assert.Single(refreshCoordinator.RefreshRequests);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenOrphanCleanupFails_DoesNotCommitOrRefresh()
	{
		AccountProfile profile = CreateClaudeProfile("Claude");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"orphan@example.com",
			"org-orphan");
		ClaudeAccountBinding orphanBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			context);
		FakeClaudeAccountBindingStore bindingStore = new()
		{
			DeleteHandler = (accountId, _) =>
			{
				Assert.Equal(orphanBinding.AccountId, accountId);
				return Task.FromException(
					new IOException("Synthetic orphan cleanup failure."));
			}
		};
		bindingStore.Seed(orphanBinding);
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		Assert.Equal(orphanBinding, bindingStore.GetBinding(orphanBinding.AccountId));
		Assert.Null(bindingStore.GetBinding(profile.Id));
		Assert.Empty(bindingStore.SavedBindings);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			statuses,
			status => status.Contains("無法儲存", StringComparison.Ordinal));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenFreshProbeDiffersFromLoginContext_DoesNotPersistAndReportsNotice()
	{
		AccountProfile profile = CreateClaudeProfile("Claude");
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		ClaudeSubscriptionContext loginContext =
			CreateClaudeSubscriptionContext(
				"login@example.com",
				"org-login");
		ClaudeSubscriptionContext freshContext =
			CreateClaudeSubscriptionContext(
				"fresh@example.com",
				"org-fresh");
		FakeClaudeAccountBindingStore bindingStore = new();
		List<(string Message, string Caption)> notices = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(loginContext))),
				bindingStore,
				new ClaudeBindingCommitGate(),
				new DelegateClaudeSubscriptionContextProbe((accountId, _) =>
				{
					Assert.Equal(profile.Id, accountId);
					return Task.FromResult(CreateClaudeObservation(freshContext));
				}));

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportNotice: (message, caption, _) =>
				notices.Add((message, caption)));

		Assert.Empty(bindingStore.SavedBindings);
		Assert.Empty(bindingStore.DeletedAccountIds);
		Assert.Empty(await bindingStore.LoadAllAsync());
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			notices,
			notice =>
				(notice.Caption == "Claude 登入失敗") &&
				notice.Message.Contains(
					"登入狀態在確認期間已改變",
					StringComparison.Ordinal));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenPublicProfileCommitFails_DeletesNewPrivateBinding()
	{
		AccountProfile profile = CreateClaudeProfile("Claude");
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"new@example.com",
			"org-new");
		FakeClaudeAccountBindingStore bindingStore = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		Assert.Single(bindingStore.SavedBindings);
		Assert.Contains(profile.Id, bindingStore.DeletedAccountIds);
		Assert.Null(bindingStore.GetBinding(profile.Id));
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenRebindPublicProfileCommitFails_RestoresExactPrivateBinding()
	{
		ClaudeSubscriptionContext originalContext = CreateClaudeSubscriptionContext(
			"original@example.com",
			"org-original");
		ClaudeAccountBinding originalBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			originalContext);
		AccountProfile profile = CreateClaudeProfile(
			"Claude",
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				originalBinding.PublicBindingId)) with
		{
			Id = originalBinding.AccountId
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		ClaudeSubscriptionContext replacementContext =
			CreateClaudeSubscriptionContext(
				"replacement@example.com",
				"org-replacement");
		FakeClaudeAccountBindingStore bindingStore = new();
		bindingStore.Seed(originalBinding);
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(replacementContext))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		Assert.Equal(originalBinding, bindingStore.GetBinding(profile.Id));
		Assert.Equal(2, bindingStore.SavedBindings.Count);
		Assert.NotEqual(originalBinding, bindingStore.SavedBindings[0]);
		Assert.Equal(originalBinding, bindingStore.SavedBindings[1]);
		Assert.Empty(bindingStore.DeletedAccountIds);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				originalBinding.PublicBindingId),
			account.Profile.ProviderAccountIdentity);
		Assert.Equal(
			account.Profile.ProviderAccountIdentity,
			account.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenPublicCommitAndPreviousBindingRestoreFail_DeletesCandidateAndReportsRollbackFailure()
	{
		ClaudeSubscriptionContext originalContext = CreateClaudeSubscriptionContext(
			"original@example.com",
			"org-original");
		ClaudeAccountBinding originalBinding = ClaudeAccountBinding.Create(
			Guid.NewGuid(),
			originalContext);
		AccountProfile profile = CreateClaudeProfile(
			"Claude",
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				originalBinding.PublicBindingId)) with
		{
			Id = originalBinding.AccountId
		};
		FakeAccountProfileStore profileStore = new(profile)
		{
			ShouldFailSave = true
		};
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(CreateReadySnapshot(
			profile,
			profile.ProviderAccountIdentity) with
		{
			ProviderAccountDisplayIdentity = originalContext.AccountIdentity,
			SubscriptionScopeDisplayName =
				originalContext.SubscriptionScopeDisplayName,
			PlanTier = originalContext.PlanTier,
			SubscriptionVerificationState =
				SubscriptionVerificationState.Verified
		});
		ClaudeSubscriptionContext replacementContext =
			CreateClaudeSubscriptionContext(
				"replacement@example.com",
				"org-replacement");
		FakeClaudeAccountBindingStore bindingStore = new();
		bindingStore.Seed(originalBinding);
		int saveAttemptCount = 0;
		bindingStore.SaveHandler = (_, _) =>
		{
			if (Interlocked.Increment(ref saveAttemptCount) == 2)
			{
				throw new IOException("Synthetic previous binding restore failure.");
			}

			return Task.CompletedTask;
		};
		List<string> statuses = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(replacementContext))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			statuses.Add);

		Assert.Equal(2, saveAttemptCount);
		Assert.Single(bindingStore.SavedBindings);
		Assert.Contains(profile.Id, bindingStore.DeletedAccountIds);
		Assert.Null(bindingStore.GetBinding(profile.Id));
		Assert.Null(account.Profile.ProviderAccountIdentity);
		Assert.Null(account.CurrentSnapshot);
		Assert.Empty(account.UsageMetrics);
		Assert.Equal(AccountStatusKind.Disabled, account.StatusKind);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			statuses,
			status => status.Contains(
				"無法安全還原本機訂閱連接資料",
				StringComparison.Ordinal));
		Assert.False(account.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenUnpairedPrivateBindingMatchesEntitlement_DoesNotTreatItAsOwner()
	{
		AccountProfile orphanProfile = CreateClaudeProfile("Claude orphan");
		AccountProfile targetProfile = CreateClaudeProfile("Claude target");
		FakeAccountProfileStore profileStore = new(orphanProfile, targetProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = viewModel.Accounts.Single(
			account => account.Id == targetProfile.Id);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"shared@example.com",
			"org-shared");
		FakeClaudeAccountBindingStore bindingStore = new();
		await bindingStore.SaveAsync(
			ClaudeAccountBinding.Create(orphanProfile.Id, context));
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate());

		await coordinator.ConnectClaudeAsync(
			targetAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		ClaudeAccountBinding targetBinding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(targetProfile.Id));
		Assert.True(targetBinding.MatchesEntitlement(context));
		Assert.Null(viewModel.Accounts.Single(
			account => account.Id == orphanProfile.Id)
			.Profile.ProviderAccountIdentity);
		Assert.Null(bindingStore.GetBinding(orphanProfile.Id));
		Assert.Contains(
			orphanProfile.Id,
			bindingStore.DeletedAccountIds);
		Assert.Equal(
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				targetBinding.PublicBindingId),
			targetAccount.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenTwoCardsConcurrentlyUseSameEntitlement_CommitsOnlyOneBinding()
	{
		AccountProfile firstProfile = CreateClaudeProfile("Claude 1");
		AccountProfile secondProfile = CreateClaudeProfile("Claude 2");
		FakeAccountProfileStore profileStore = new(firstProfile, secondProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel firstAccount = viewModel.Accounts.Single(
			account => account.Id == firstProfile.Id);
		AccountUsageViewModel secondAccount = viewModel.Accounts.Single(
			account => account.Id == secondProfile.Id);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			"concurrent@example.com",
			"org-concurrent");
		TaskCompletionSource bothLoginsEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseLogins = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int loginCount = 0;
		DelegateClaudeAccountLogin login = new(async (_, _) =>
		{
			if (Interlocked.Increment(ref loginCount) == 2)
			{
				bothLoginsEntered.TrySetResult();
			}

			await releaseLogins.Task;
			return CreateClaudeLoginResult(context);
		});
		FakeClaudeAccountBindingStore bindingStore = new();
		ClaudeBindingCommitGate bindingCommitGate = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				login,
				bindingStore,
				bindingCommitGate);
		List<string> statuses = new();
		object statusesLock = new();
		Action<string> reportStatus = status =>
		{
			lock (statusesLock)
			{
				statuses.Add(status);
			}
		};

		Task firstConnect = coordinator.ConnectClaudeAsync(
			firstAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportStatus);
		Task secondConnect = coordinator.ConnectClaudeAsync(
			secondAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportStatus);
		await bothLoginsEntered.Task.WaitAsync(AsyncWatchdogTimeout);
		releaseLogins.TrySetResult();
		await Task.WhenAll(firstConnect, secondConnect)
			.WaitAsync(AsyncWatchdogTimeout);

		Assert.Single(await bindingStore.LoadAllAsync());
		Assert.Equal(1, viewModel.Accounts.Count(
			account => account.Profile.ProviderAccountIdentity is not null));
		Assert.Single(refreshCoordinator.RefreshRequests);
		lock (statusesLock)
		{
			Assert.Contains(
				statuses,
				status => status.Contains(
					"訂閱範圍已連接到另一張帳號卡片",
					StringComparison.Ordinal));
		}
		Assert.All(
			viewModel.Accounts,
			account => Assert.False(account.IsProviderAccountChangeInProgress));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenLegacySameEmailUsesDifferentOrganization_AllowsBinding()
	{
		const string Email = "legacy@example.com";
		AccountProfile legacyProfile = CreateClaudeProfile(
			"Claude legacy",
			Email);
		AccountProfile targetProfile = CreateClaudeProfile("Claude new");
		FakeAccountProfileStore profileStore = new(legacyProfile, targetProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = viewModel.Accounts.Single(
			account => account.Id == targetProfile.Id);
		ClaudeSubscriptionContext legacyContext = CreateClaudeSubscriptionContext(
			Email,
			"org-legacy");
		ClaudeSubscriptionContext targetContext = CreateClaudeSubscriptionContext(
			Email,
			"org-new");
		int legacyProbeCount = 0;
		DelegateClaudeSubscriptionContextProbe probe = new((accountId, _) =>
		{
			if (accountId == targetProfile.Id)
			{
				return Task.FromResult(CreateClaudeObservation(targetContext));
			}

			Assert.Equal(legacyProfile.Id, accountId);
			legacyProbeCount++;
			return Task.FromResult(CreateClaudeObservation(legacyContext));
		});
		FakeClaudeAccountBindingStore bindingStore = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(targetContext))),
				bindingStore,
				new ClaudeBindingCommitGate(),
				probe);

		await coordinator.ConnectClaudeAsync(
			targetAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		Assert.Equal(1, legacyProbeCount);
		ClaudeAccountBinding targetBinding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(targetProfile.Id));
		Assert.True(targetBinding.MatchesEntitlement(targetContext));
		Assert.Equal(
			Email,
			viewModel.Accounts.Single(account => account.Id == legacyProfile.Id)
				.Profile.ProviderAccountIdentity);
		Assert.Contains(
			refreshCoordinator.RefreshRequests,
			request =>
				(request.Id == targetProfile.Id) &&
				(request.ProviderAccountIdentity ==
					targetAccount.Profile.ProviderAccountIdentity));
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenLegacySameEmailOwnsSameEntitlement_BlocksBinding()
	{
		const string Email = "legacy@example.com";
		AccountProfile legacyProfile = CreateClaudeProfile(
			"Claude legacy",
			Email);
		AccountProfile targetProfile = CreateClaudeProfile("Claude new");
		FakeAccountProfileStore profileStore = new(legacyProfile, targetProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = viewModel.Accounts.Single(
			account => account.Id == targetProfile.Id);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			Email,
			"org-shared");
		FakeClaudeAccountBindingStore bindingStore = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate(),
				new DelegateClaudeSubscriptionContextProbe((_, _) =>
					Task.FromResult(CreateClaudeObservation(context))));

		await coordinator.ConnectClaudeAsync(
			targetAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."));

		Assert.Empty(await bindingStore.LoadAllAsync());
		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Equal(
			Email,
			viewModel.Accounts.Single(account => account.Id == legacyProfile.Id)
				.Profile.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
	}

	[Fact]
	public async Task ConnectClaudeAsync_WhenLegacyOwnerProbeThrowsTypedFailure_ReportsNoticeAndFailsClosed()
	{
		const string Email = "legacy@example.com";
		AccountProfile legacyProfile = CreateClaudeProfile(
			"Claude legacy",
			Email);
		AccountProfile targetProfile = CreateClaudeProfile("Claude new");
		FakeAccountProfileStore profileStore = new(legacyProfile, targetProfile);
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(profileStore, refreshCoordinator);
		await viewModel.InitializeAsync();
		AccountUsageViewModel targetAccount = viewModel.Accounts.Single(
			account => account.Id == targetProfile.Id);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			Email,
			"org-new");
		FakeClaudeAccountBindingStore bindingStore = new();
		List<(string Message, string Caption)> notices = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate(),
				new DelegateClaudeSubscriptionContextProbe((accountId, _) =>
					accountId == targetProfile.Id
						? Task.FromResult(CreateClaudeObservation(context))
						: Task.FromException<ClaudeSubscriptionObservation>(
							new ClaudeAccountLoginException(
								"synthetic typed legacy probe failure"))));

		await coordinator.ConnectClaudeAsync(
			targetAccount,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportNotice: (message, caption, _) =>
				notices.Add((message, caption)));

		Assert.Empty(await bindingStore.LoadAllAsync());
		Assert.Null(targetAccount.Profile.ProviderAccountIdentity);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			notices,
			notice =>
				(notice.Caption == "Claude 登入失敗") &&
				notice.Message.Contains(
					"暫時無法確認它的訂閱範圍",
					StringComparison.Ordinal));
		Assert.False(targetAccount.IsProviderAccountChangeInProgress);
	}

	[Theory]
	[InlineData((int)ClaudeLegacyCacheDisposition.Retain, true)]
	[InlineData((int)ClaudeLegacyCacheDisposition.Clear, true)]
	[InlineData((int)ClaudeLegacyCacheDisposition.Separate, true)]
	[InlineData((int)ClaudeLegacyCacheDisposition.Retain, false)]
	[InlineData((int)ClaudeLegacyCacheDisposition.Clear, false)]
	[InlineData((int)ClaudeLegacyCacheDisposition.Separate, false)]
	public async Task ConnectClaudeAsync_WhenLegacyCacheDispositionIsChosen_AppliesDecision(
		int dispositionValue,
		bool hasAcceptedQuotaRisk)
	{
		ClaudeLegacyCacheDisposition disposition =
			(ClaudeLegacyCacheDisposition)dispositionValue;
		const string Email = "legacy-cache@example.com";
		const string OrganizationId = "org-legacy-cache";
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = CreateClaudeProfile(
			"Claude legacy",
			Email) with { HasAcceptedClaudeQuotaRisk = hasAcceptedQuotaRisk };
		UsageSnapshot legacySnapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"claude.legacy",
					"Claude 舊版用量",
					27,
					"已使用 27%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			observedAt,
			ObservedAt: observedAt,
			ProviderAccountIdentity: Email);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = legacySnapshot
		};
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		if (!hasAcceptedQuotaRisk)
		{
			refreshCoordinator.RefreshHandler = (request, _) =>
			{
				Assert.True(request.HasAcceptedClaudeQuotaRisk);
				Assert.Equal(Email, request.ProviderAccountIdentity);
				return Task.FromResult(new UsageSnapshot(
					request,
					Array.Empty<UsageMetric>(),
					SourceTrust.Unavailable,
					SnapshotStatus.NotConfigured,
					observedAt,
					RecoveryAction: UsageRecoveryAction.ConfirmSubscription));
			};
		}

		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		account.ApplySnapshot(legacySnapshot);
		Assert.Equal(legacySnapshot, account.CurrentSnapshot);
		if (hasAcceptedQuotaRisk)
		{
			viewModel.StopRefreshing();
		}

		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			Email,
			OrganizationId);
		FakeClaudeAccountBindingStore bindingStore = new();
		int loginCount = 0;
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ =>
				{
					loginCount++;
					Assert.True(account.Profile.HasAcceptedClaudeQuotaRisk);
					Assert.Equal(Email, account.Profile.ProviderAccountIdentity);
					return Task.FromResult(CreateClaudeLoginResult(context));
				}),
				bindingStore,
				new ClaudeBindingCommitGate());
		ClaudeLegacyCacheConfirmation? observedConfirmation = null;
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => hasAcceptedQuotaRisk
				? throw new InvalidOperationException(
					"Persisted consent must skip the prompt.")
				: MessageBoxResult.Yes,
			reportStatus: statuses.Add,
			confirmLegacyCache: confirmation =>
			{
				Assert.Null(observedConfirmation);
				observedConfirmation = confirmation;
				viewModel.StopRefreshing();
				return disposition;
			});

		Assert.Equal(1, loginCount);
		Assert.Equal(
			hasAcceptedQuotaRisk ? 0 : 1,
			refreshCoordinator.RefreshRequests.Count);
		ClaudeLegacyCacheConfirmation confirmation =
			Assert.IsType<ClaudeLegacyCacheConfirmation>(observedConfirmation);
		Assert.Equal(Email, confirmation.AccountIdentity);
		Assert.Equal(
			$"Organization {OrganizationId}",
			confirmation.SubscriptionScopeDisplayName);
		Assert.Equal("max", confirmation.PlanTier);
		Assert.False(account.IsProviderAccountChangeInProgress);

		if (disposition == ClaudeLegacyCacheDisposition.Separate)
		{
			Assert.Empty(await bindingStore.LoadAllAsync());
			AccountProfile expectedProfile = profile with
			{
				HasAcceptedClaudeQuotaRisk = true
			};
			Assert.Equal(expectedProfile, account.Profile);
			AccountProfileLoadResult persistedProfiles = await profileStore.LoadAsync();
			Assert.Equal(expectedProfile, Assert.Single(persistedProfiles.Accounts));
			Assert.Equal(
				hasAcceptedQuotaRisk ? 0 : 1,
				profileStore.SavedSnapshots.Count);
			Assert.Empty(bindingStore.SavedBindings);
			Assert.Empty(bindingStore.DeletedAccountIds);
			UsageSnapshot displayedSnapshot = Assert.IsType<UsageSnapshot>(
				account.CurrentSnapshot);
			Assert.Equal(legacySnapshot.Metrics, displayedSnapshot.Metrics);
			Assert.Equal(SnapshotStatus.Stale, displayedSnapshot.Status);
			Assert.Equal(
				SubscriptionVerificationState.Unverified,
				displayedSnapshot.SubscriptionVerificationState);
			Assert.Equal(
				UsageRecoveryAction.ConfirmSubscription,
				displayedSnapshot.RecoveryAction);
			Assert.Equal("需要確認 Claude 訂閱", account.RecoveryPanelTitle);
			Assert.Equal("確認 Claude 訂閱", account.RecoveryActionText);
			Assert.Equal("確認 Claude 訂閱", account.ClaudeAccountActionText);
			Assert.True(account.CanExecuteRecoveryAction);
			Assert.Contains(
				"舊版歷史用量",
				displayedSnapshot.Error,
				StringComparison.Ordinal);
			Assert.Equal(legacySnapshot, snapshotStore.SnapshotToLoad);
			Assert.Empty(snapshotStore.SavedSnapshots);
			Assert.Empty(snapshotStore.DeletedAccountIds);
			Assert.Contains(
				statuses,
				status => status.Contains("另建卡片", StringComparison.Ordinal));
			return;
		}

		ClaudeAccountBinding binding = Assert.IsType<ClaudeAccountBinding>(
			bindingStore.GetBinding(profile.Id));
		string publicBindingIdentity =
			ClaudeAccountBinding.CreatePublicBindingIdentity(
				binding.PublicBindingId);
		Assert.True(binding.MatchesEntitlement(context));
		Assert.Equal(
			publicBindingIdentity,
			account.Profile.ProviderAccountIdentity);

		if (disposition == ClaudeLegacyCacheDisposition.Clear)
		{
			Assert.Empty(snapshotStore.SavedSnapshots);
			Assert.Contains(profile.Id, snapshotStore.DeletedAccountIds);
			Assert.Null(snapshotStore.SnapshotToLoad);
			return;
		}

		UsageSnapshot retainedSnapshot = Assert.Single(
			snapshotStore.SavedSnapshots);
		Assert.Equal(
			"claude.legacy",
			Assert.Single(retainedSnapshot.Metrics).Key);
		Assert.Equal(SnapshotStatus.Stale, retainedSnapshot.Status);
		Assert.Equal(
			publicBindingIdentity,
			retainedSnapshot.ProviderAccountIdentity);
		Assert.Equal(
			publicBindingIdentity,
			retainedSnapshot.Account.ProviderAccountIdentity);
		Assert.Equal(Email, retainedSnapshot.ProviderAccountDisplayIdentity);
		Assert.Equal(
			$"Organization {OrganizationId}",
			retainedSnapshot.SubscriptionScopeDisplayName);
		Assert.Equal("max", retainedSnapshot.PlanTier);
		Assert.Equal(
			SubscriptionVerificationState.Verified,
			retainedSnapshot.SubscriptionVerificationState);
		Assert.Equal(UsageRecoveryAction.Retry, retainedSnapshot.RecoveryAction);
		Assert.Contains(
			"已確認歸屬於此 Claude 訂閱範圍",
			retainedSnapshot.Error,
			StringComparison.Ordinal);
		Assert.Equal(retainedSnapshot, snapshotStore.SnapshotToLoad);
		Assert.Empty(snapshotStore.DeletedAccountIds);
	}

	[Fact]
	public async Task ConnectClaudeAsync_FromFreshStartup_WhenLegacyCacheIsSeparated_ShowsUnverifiedHistoryWithoutPersistingChanges()
	{
		const string Email = "legacy-cache@example.com";
		const string OrganizationId = "org-legacy-cache";
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = CreateClaudeProfile(
			"Claude legacy",
			Email);
		UsageSnapshot legacySnapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"claude.legacy",
					"Claude 舊版用量",
					27,
					"已使用 27%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			observedAt,
			ObservedAt: observedAt,
			ProviderAccountIdentity: Email);
		FakeAccountProfileStore profileStore = new(profile);
		FakeUsageSnapshotStore snapshotStore = new()
		{
			SnapshotToLoad = legacySnapshot
		};
		FakeUsageRefreshCoordinator refreshCoordinator =
			CreateClaudeReadyRefreshCoordinator();
		DashboardViewModel viewModel = new(
			profileStore,
			refreshCoordinator,
			snapshotStore);
		await viewModel.InitializeAsync();
		AccountUsageViewModel account = Assert.Single(viewModel.Accounts);
		Assert.Null(account.CurrentSnapshot);
		ClaudeSubscriptionContext context = CreateClaudeSubscriptionContext(
			Email,
			OrganizationId);
		FakeClaudeAccountBindingStore bindingStore = new();
		using AccountConnectionCoordinator coordinator =
			CreateClaudeBindingCoordinator(
				new DelegateClaudeAccountLogin(_ => Task.FromResult(
					CreateClaudeLoginResult(context))),
				bindingStore,
				new ClaudeBindingCommitGate());
		List<string> statuses = new();

		await coordinator.ConnectClaudeAsync(
			account,
			viewModel,
			() => throw new InvalidOperationException(
				"Persisted consent must skip the prompt."),
			reportStatus: statuses.Add,
			confirmLegacyCache: _ => ClaudeLegacyCacheDisposition.Separate);

		UsageSnapshot displayedSnapshot = Assert.IsType<UsageSnapshot>(
			account.CurrentSnapshot);
		Assert.Equal(legacySnapshot.Metrics, displayedSnapshot.Metrics);
		Assert.Equal(SnapshotStatus.Stale, displayedSnapshot.Status);
		Assert.Equal(
			SubscriptionVerificationState.Unverified,
			displayedSnapshot.SubscriptionVerificationState);
		Assert.Equal(
			UsageRecoveryAction.ConfirmSubscription,
			displayedSnapshot.RecoveryAction);
		Assert.Equal("需要確認 Claude 訂閱", account.RecoveryPanelTitle);
		Assert.Equal("確認 Claude 訂閱", account.RecoveryActionText);
		Assert.Equal("確認 Claude 訂閱", account.ClaudeAccountActionText);
		Assert.True(account.CanExecuteRecoveryAction);
		Assert.Contains(
			"舊版歷史用量",
			displayedSnapshot.Error,
			StringComparison.Ordinal);
		Assert.False(account.IsProviderAccountChangeInProgress);
		Assert.Equal(profile, account.Profile);
		AccountProfileLoadResult persistedProfiles = await profileStore.LoadAsync();
		Assert.Equal(profile, Assert.Single(persistedProfiles.Accounts));
		Assert.Empty(profileStore.SavedSnapshots);
		Assert.Empty(bindingStore.SavedBindings);
		Assert.Empty(bindingStore.DeletedAccountIds);
		Assert.Empty(await bindingStore.LoadAllAsync());
		Assert.Same(legacySnapshot, snapshotStore.SnapshotToLoad);
		Assert.Empty(snapshotStore.SavedSnapshots);
		Assert.Empty(snapshotStore.DeletedAccountIds);
		Assert.Empty(refreshCoordinator.RefreshRequests);
		Assert.Contains(
			statuses,
			status => status.Contains("另建卡片", StringComparison.Ordinal));
	}

	[Fact]
	public void GetClaudeLoginFailureNotice_PreservesTypedReasonAndAddsRecoveryContext()
	{
		const string Reason = "無法確認 Claude 登入，請確認使用 claude.ai 訂閱帳號。";
		ClaudeAccountLoginException exception = new(Reason);

		(string message, string caption, MessageBoxImage image) =
			AccountConnectionCoordinator.GetClaudeLoginFailureNotice(exception);

		Assert.StartsWith(Reason, message, StringComparison.Ordinal);
		Assert.Contains("原本顯示的用量不會被清除", message, StringComparison.Ordinal);
		Assert.Contains("可重新連接", message, StringComparison.Ordinal);
		Assert.Equal("Claude 登入失敗", caption);
		Assert.Equal(MessageBoxImage.Warning, image);
	}

	[Fact]
	public void GetClaudeLoginFailureNotice_WhenFreePlanIsReported_UsesSubscriptionCaption()
	{
		ClaudeUsageNotConfiguredException cause = new(
			"Claude Code 目前回報 Free，請確認訂閱。",
			UsageRecoveryAction.ConfirmSubscription);
		ClaudeAccountLoginException exception = new(cause.Message, cause);

		(string message, string caption, MessageBoxImage image) =
			AccountConnectionCoordinator.GetClaudeLoginFailureNotice(exception);

		Assert.StartsWith(cause.Message, message, StringComparison.Ordinal);
		Assert.Equal("需要確認 Claude 訂閱", caption);
		Assert.Equal(MessageBoxImage.Warning, image);
	}

	[Fact]
	public void GetCodexLoginFailureNotice_UsesTypedReasonWithoutExposingInnerException()
	{
		const string Reason = "Codex 登入逾時，請再試一次。";
		const string RawInnerMessage = "raw process details must stay hidden";
		CodexAccountLoginException exception = new(
			Reason,
			new InvalidOperationException(RawInnerMessage));

		(string message, string caption, MessageBoxImage image) =
			AccountConnectionCoordinator.GetCodexLoginFailureNotice(exception);

		Assert.StartsWith(Reason, message, StringComparison.Ordinal);
		Assert.Contains("原本顯示的用量不會被清除", message, StringComparison.Ordinal);
		Assert.Contains("可重新連接", message, StringComparison.Ordinal);
		Assert.DoesNotContain(RawInnerMessage, message, StringComparison.Ordinal);
		Assert.Equal("Codex 登入失敗", caption);
		Assert.Equal(MessageBoxImage.Warning, image);
	}

	[Fact]
	public void Constructor_WithClaudeBindingStoreButNoSharedGate_Throws()
	{
		FakeClaudeAccountBindingStore bindingStore = new();
		DelegateClaudeAccountLogin login = new(_ => Task.FromResult(
			CreateClaudeLoginResult(CreateClaudeSubscriptionContext(
				"person@example.com",
				"org-company"))));

		Assert.Throws<ArgumentNullException>(() => new AccountConnectionCoordinator(
			login,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			grokAccountLogin: null,
			grokUsagePoller: null,
			grokAccountBindingStore: null,
			grokConnectionPendingStore: null,
			antigravityRefreshQuiesceTimeout: TimeSpan.FromSeconds(30),
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: null,
			claudeSubscriptionContextProbe:
				new DelegateClaudeSubscriptionContextProbe(login.ProbeAsync)));
	}

	[Fact]
	public void GetGrokLoginFailureNotice_UsesTypedReasonWithoutRawProcessDetails()
	{
		const string Reason = "Grok 登入逾時，請再試一次。";
		const string RawInnerMessage = "raw grok process output";
		GrokAccountLoginException exception = new(
			Reason,
			new InvalidOperationException(RawInnerMessage));

		(string message, string caption, MessageBoxImage image) =
			AccountConnectionCoordinator.GetGrokLoginFailureNotice(exception);

		Assert.StartsWith(Reason, message, StringComparison.Ordinal);
		Assert.DoesNotContain(RawInnerMessage, message, StringComparison.Ordinal);
		Assert.Equal("Grok 登入失敗", caption);
		Assert.Equal(MessageBoxImage.Warning, image);
	}

	private static AccountConnectionCoordinator CreateClaudeBindingCoordinator(
		IClaudeAccountLogin login,
		IClaudeAccountBindingStore bindingStore,
		ClaudeBindingCommitGate bindingCommitGate,
		IClaudeSubscriptionContextProbe? subscriptionContextProbe = null)
	{
		subscriptionContextProbe ??= login is DelegateClaudeAccountLogin delegateLogin
			? new DelegateClaudeSubscriptionContextProbe(delegateLogin.ProbeAsync)
			: null;

		return new AccountConnectionCoordinator(
			login,
			new UnusedCodexAccountLogin(),
			new FakeAntigravityAccountSetupLauncher(
				AntigravityAccountSetupOutcome.Cancelled),
			grokAccountLogin: null,
			grokUsagePoller: null,
			grokAccountBindingStore: null,
			grokConnectionPendingStore: null,
			antigravityRefreshQuiesceTimeout: TimeSpan.FromSeconds(30),
			claudeAccountBindingStore: bindingStore,
			claudeBindingCommitGate: bindingCommitGate,
			claudeSubscriptionContextProbe: subscriptionContextProbe);
	}

	private static ClaudeAccountLoginResult CreateClaudeLoginResult(
		ClaudeSubscriptionContext context)
	{
		return new ClaudeAccountLoginResult(
			context.AccountIdentity,
			context,
			CliVersionPolicies.AssessClaude(new Version(2, 1, 169)));
	}

	private static ClaudeSubscriptionObservation CreateClaudeObservation(
		ClaudeSubscriptionContext context)
	{
		return new ClaudeSubscriptionObservation(
			context,
			CliVersionPolicies.AssessClaude(new Version(2, 1, 169)));
	}

	private static AccountProfile CreateClaudeProfile(
		string displayName,
		string? providerAccountIdentity = null)
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Claude,
			displayName,
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: providerAccountIdentity);
	}

	private static FakeUsageRefreshCoordinator
		CreateClaudeReadyRefreshCoordinator()
	{
		return new FakeUsageRefreshCoordinator
		{
			RefreshHandler = (profile, _) => Task.FromResult(
				CreateReadySnapshot(
					profile,
					profile.ProviderAccountIdentity) with
				{
					SubscriptionVerificationState =
						SubscriptionVerificationState.Verified
				})
		};
	}

	private static ClaudeSubscriptionContext CreateClaudeSubscriptionContext(
		string email,
		string organizationId)
	{
		return ClaudeSubscriptionContext.CreateVerified(
			email,
			organizationId,
			"max",
			$"Organization {organizationId}");
	}

	private static AccountConnectionCoordinator CreateCoordinator(
		IAntigravityAccountSetupLauncher launcher,
		TimeSpan? quiesceTimeout = null,
		List<(string Operation, string Summary)>? diagnostics = null)
	{
		Action<string, string, Exception?> reportDiagnostic =
			(operation, summary, _) => diagnostics?.Add((operation, summary));
		return quiesceTimeout is TimeSpan timeout
			? new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				launcher,
				timeout,
				reportDiagnostic)
			: new AccountConnectionCoordinator(
				new UnusedClaudeAccountLogin(),
				new UnusedCodexAccountLogin(),
				launcher,
				reportDiagnostic);
	}

	private static async Task<ClaudeAccountLoginResult>
		ThrowUnexpectedClaudeLoginAsync(CancellationToken cancellationToken)
	{
		await Task.Yield();
		cancellationToken.ThrowIfCancellationRequested();
		throw new InvalidOperationException("synthetic unexpected login failure");
	}

	private static async Task<DashboardViewModel> CreateDashboardAsync(
		FakeUsageRefreshCoordinator refreshCoordinator,
		params AccountProfile[] profiles)
	{
		DashboardViewModel viewModel = new(
			new FakeAccountProfileStore(profiles),
			refreshCoordinator);
		await viewModel.InitializeAsync();
		return viewModel;
	}

	private static Task<Task> StartOnStaThreadAsync(Func<Task> operation)
	{
		TaskCompletionSource<Task> taskSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			try
			{
				taskSource.TrySetResult(operation());
			}
			catch (Exception exception)
			{
				taskSource.TrySetException(exception);
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return taskSource.Task;
	}

	private static AccountProfile CreateAntigravityProfile(string displayName)
	{
		return new AccountProfile(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			displayName);
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile account,
		string? providerAccountIdentity = null)
	{
		return new UsageSnapshot(
			account,
			new[]
			{
				new UsageMetric(
					"agy.test",
					"AGY 測試用量",
					25,
					"已使用 25%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: providerAccountIdentity);
	}

	private static GrokUsagePollResult CreateGrokPollResult(
		string principalId,
		GrokUsageAvailability usageAvailability =
			GrokUsageAvailability.Available,
		GrokCurrentAuthUsability currentAuthUsability =
			GrokCurrentAuthUsability.Usable)
	{
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		return new GrokUsagePollResult(
			new GrokPrincipal("user", principalId),
			usageAvailability == GrokUsageAvailability.Available
				? new GrokWeeklyUsage(
					UsedPercent: 12,
					PeriodStartsAt: observedAt.AddDays(-2),
					ResetsAt: observedAt.AddDays(5))
				: null,
			observedAt,
			usageAvailability: usageAvailability,
			currentAuthUsability: currentAuthUsability);
	}
}
