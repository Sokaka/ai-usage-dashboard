using System.IO;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.ViewModels;

namespace AiUsageDashboard.App.Providers;

internal interface IGrokStartupRecovery
{
	Task RecoverAsync(
		DashboardViewModel viewModel,
		CancellationToken cancellationToken = default);
}

internal sealed class GrokStartupRecovery : IGrokStartupRecovery
{
	private readonly TimeSpan _accountCleanupBudget;
	private readonly IGrokAccountBindingStore _bindingStore;
	private readonly GrokBindingCommitGate _bindingCommitGate;
	private readonly TimeSpan _connectionRecoveryTimeout;
	private readonly IGrokConnectionPendingStore _pendingStore;
	private readonly IGrokUsagePoller _usagePoller;

	internal GrokStartupRecovery(
		IGrokAccountBindingStore bindingStore,
		IGrokConnectionPendingStore pendingStore,
		IGrokUsagePoller usagePoller,
		GrokBindingCommitGate bindingCommitGate,
		TimeSpan connectionRecoveryTimeout,
		TimeSpan accountCleanupBudget)
	{
		_bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
		_pendingStore = pendingStore ?? throw new ArgumentNullException(nameof(pendingStore));
		_usagePoller = usagePoller ?? throw new ArgumentNullException(nameof(usagePoller));
		_bindingCommitGate = bindingCommitGate ??
			throw new ArgumentNullException(nameof(bindingCommitGate));
		if (connectionRecoveryTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(connectionRecoveryTimeout));
		}

		if (accountCleanupBudget <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(accountCleanupBudget));
		}

		_connectionRecoveryTimeout = connectionRecoveryTimeout;
		_accountCleanupBudget = accountCleanupBudget;
	}

	public async Task RecoverAsync(
		DashboardViewModel viewModel,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(viewModel);

		await RecoverGrokConnectionPendingWorkWithinBudgetAsync(
			viewModel,
			_bindingStore,
			_pendingStore,
			_usagePoller,
			_connectionRecoveryTimeout,
			cancellationToken,
			_bindingCommitGate);
		await CompleteDeferredGrokAccountCleanupWithinBudgetAsync(
			viewModel,
			_pendingStore,
			_accountCleanupBudget,
			cancellationToken);
	}

	internal static async Task
		CompleteDeferredGrokAccountCleanupWithinBudgetAsync(
			DashboardViewModel viewModel,
			IGrokConnectionPendingStore pendingStore,
			TimeSpan timeout,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		ArgumentNullException.ThrowIfNull(pendingStore);
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		HashSet<Guid> existingGrokAccountIds = viewModel.Accounts
			.Where(account => account.IsGrok)
			.Select(account => account.Id)
			.ToHashSet();
		HashSet<Guid> protectedGrokAccountIds;

		try
		{
			IReadOnlyList<GrokConnectionPendingWork> pendingWork =
				await pendingStore.LoadAsync(cancellationToken);
			protectedGrokAccountIds = pendingWork
				.Select(work => work.AccountId)
				.Where(existingGrokAccountIds.Contains)
				.ToHashSet();
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			protectedGrokAccountIds = existingGrokAccountIds;
			viewModel.SetGrokConnectionRecoveryPending(isPending: true);
			AppDiagnostics.TryWrite(
				"grok_account_cleanup_post_startup",
				$"result=protection-load-failed;" +
				$"error_type={exception.GetType().Name}");
		}

		Task<bool> cleanupTask =
			viewModel.CompleteDeferredStartupAccountCleanupsAsync(
				protectedGrokAccountIds,
				cancellationToken);

		try
		{
			bool completed = await cleanupTask.WaitAsync(
				timeout,
				cancellationToken);
			if (!completed)
			{
				AppDiagnostics.TryWrite(
					"grok_account_cleanup_post_startup",
					"result=incomplete");
			}

			await RefreshGrokConnectionRecoveryPendingStateAsync(
				viewModel,
				pendingStore,
				cancellationToken);
		}
		catch (TimeoutException)
		{
			_ = ObserveDeferredGrokAccountCleanupAsync(
				cleanupTask,
				viewModel,
				pendingStore);
			AppDiagnostics.TryWrite(
				"grok_account_cleanup_post_startup",
				"result=budget-exhausted;continuation=background");
		}
	}

	internal static async Task ObserveDeferredGrokAccountCleanupAsync(
		Task<bool> cleanupTask,
		DashboardViewModel viewModel,
		IGrokConnectionPendingStore pendingStore)
	{
		try
		{
			await cleanupTask;
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception exception)
		{
			AppDiagnostics.TryWrite(
				"grok_account_cleanup_post_startup",
				"result=background-fault",
				exception);
		}

		await RefreshGrokConnectionRecoveryPendingStateAsync(
			viewModel,
			pendingStore,
			CancellationToken.None);
	}

	private static async Task RefreshGrokConnectionRecoveryPendingStateAsync(
		DashboardViewModel viewModel,
		IGrokConnectionPendingStore pendingStore,
		CancellationToken cancellationToken)
	{
		try
		{
			IReadOnlyList<GrokConnectionPendingWork> pendingWork =
				await pendingStore.LoadAsync(cancellationToken);
			viewModel.SetGrokConnectionRecoveryPending(pendingWork.Count > 0);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			viewModel.SetGrokConnectionRecoveryPending(isPending: true);
			AppDiagnostics.TryWrite(
				"grok_account_cleanup_post_startup",
				$"result=recovery-state-refresh-failed;" +
				$"error_type={exception.GetType().Name}");
		}
	}

	internal static async Task
		RecoverGrokConnectionPendingWorkWithinBudgetAsync(
			DashboardViewModel viewModel,
			IGrokAccountBindingStore bindingStore,
			IGrokConnectionPendingStore pendingStore,
			IGrokUsagePoller usagePoller,
			TimeSpan timeout,
			CancellationToken cancellationToken = default,
			GrokBindingCommitGate? bindingCommitGate = null)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		ArgumentNullException.ThrowIfNull(bindingStore);
		ArgumentNullException.ThrowIfNull(pendingStore);
		ArgumentNullException.ThrowIfNull(usagePoller);
		bindingCommitGate ??= new GrokBindingCommitGate();
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		using CancellationTokenSource timeoutSource = new(timeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			await RecoverGrokConnectionPendingWorkAsync(
				viewModel,
				bindingStore,
				pendingStore,
				usagePoller,
				linkedSource.Token,
				bindingCommitGate);
		}
		catch (OperationCanceledException) when (
			timeoutSource.IsCancellationRequested &&
			!cancellationToken.IsCancellationRequested)
		{
			viewModel.SetGrokConnectionRecoveryPending(isPending: true);
			AppDiagnostics.TryWrite(
				"grok_connection_startup_recovery",
				"stage=post-startup-budget;result=timed-out");
		}
	}

	internal static async Task RecoverGrokConnectionPendingWorkAsync(
		DashboardViewModel viewModel,
		IGrokAccountBindingStore bindingStore,
		IGrokConnectionPendingStore pendingStore,
		IGrokUsagePoller? usagePoller = null,
		CancellationToken cancellationToken = default,
		GrokBindingCommitGate? bindingCommitGate = null)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		ArgumentNullException.ThrowIfNull(bindingStore);
		ArgumentNullException.ThrowIfNull(pendingStore);
		bindingCommitGate ??= new GrokBindingCommitGate();
		IReadOnlyList<GrokConnectionPendingWork> pendingWork;

		try
		{
			pendingWork = await pendingStore.LoadAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			viewModel.SetGrokConnectionRecoveryPending(isPending: true);
			AppDiagnostics.TryWrite(
				"grok_connection_startup_recovery",
				$"stage=journal-load;error_type={exception.GetType().Name}");
			return;
		}

		foreach (GrokConnectionPendingWork work in pendingWork)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				await TryCompleteGrokConnectionPendingWorkAsync(
					viewModel,
					bindingStore,
					pendingStore,
					usagePoller,
					bindingCommitGate,
					work,
					cancellationToken);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				AppDiagnostics.TryWrite(
					"grok_connection_startup_recovery",
					$"stage=entry;account_id={work.AccountId:N};" +
					$"error_type={exception.GetType().Name}");
			}
		}

		await RefreshGrokConnectionRecoveryPendingStateAsync(
			viewModel,
			pendingStore,
			cancellationToken);
	}

	private static async Task<bool> TryCompleteGrokConnectionPendingWorkAsync(
		DashboardViewModel viewModel,
		IGrokAccountBindingStore bindingStore,
		IGrokConnectionPendingStore pendingStore,
		IGrokUsagePoller? usagePoller,
		GrokBindingCommitGate bindingCommitGate,
		GrokConnectionPendingWork work,
		CancellationToken cancellationToken)
	{
		AccountUsageViewModel? account = viewModel.Accounts.FirstOrDefault(
			candidate => candidate.IsGrok && (candidate.Id == work.AccountId));
		if ((account is null) || !account.IsEnabled)
		{
			return false;
		}

		if (!await viewModel.TryBeginProviderAccountRecoveryAsync(
				account,
				cancellationToken))
		{
			return false;
		}

		bool didFinishAccountChange = false;

		try
		{
			if (!IsGrokRecoveryAccountAttached(viewModel, account))
			{
				return false;
			}

			GrokAccountBinding? binding = await bindingStore.LoadAsync(
				work.AccountId,
				cancellationToken);
			if (!IsGrokRecoveryAccountAttached(viewModel, account))
			{
				return false;
			}

			bool hasCorrelatedBinding =
				(binding is not null) &&
				(binding.PublicBindingId == work.AttemptId);
			GrokUsagePollResult? usage = null;

			if (work.Stage != GrokConnectionPendingStage.BindingPersisted)
			{
				bool canRetryCompletedLogin =
					work.Stage == GrokConnectionPendingStage.LoginCompleted;
				if ((usagePoller is null) ||
					(!hasCorrelatedBinding && !canRetryCompletedLogin))
				{
					return false;
				}

				usage = hasCorrelatedBinding
					? await usagePoller.PollBoundAsync(
						work.AccountId,
						binding!,
						cancellationToken)
					: await usagePoller.PollAsync(
						work.AccountId,
						cancellationToken);
				if (!IsGrokRecoveryAccountAttached(viewModel, account))
				{
					return false;
				}

				if (!usage.CanCommitBinding)
				{
					return false;
				}
			}

			return await viewModel.ExecuteAuthenticatedProviderBindingMutationAsync(
				async () =>
				{
					using (await bindingCommitGate.EnterAsync(cancellationToken))
					{
						if (!IsGrokRecoveryAccountAttached(viewModel, account))
						{
							return false;
						}

						await DeleteOrphanGrokBindingsAsync(
							viewModel,
							bindingStore,
							work.AccountId,
							cancellationToken);

						Guid publicBindingId;

						if (work.Stage == GrokConnectionPendingStage.BindingPersisted)
						{
							if ((work.PublicBindingId is not Guid persistedBindingId) ||
								(persistedBindingId == Guid.Empty) ||
								(binding is null) ||
								(binding.PublicBindingId != persistedBindingId))
							{
								return false;
							}

							publicBindingId = persistedBindingId;
						}
						else
						{
							GrokUsagePollResult verifiedUsage = usage!;

							if (await IsGrokPrincipalBoundToAnotherAccountAsync(
									viewModel,
									bindingStore,
									work.AccountId,
									verifiedUsage.Principal,
									cancellationToken))
							{
								if (!IsGrokRecoveryAccountAttached(viewModel, account))
								{
									return false;
								}

								if (!string.IsNullOrWhiteSpace(
										account.Profile.ProviderAccountIdentity))
								{
									if (!account.TryBeginProviderAccountChangeCommit())
									{
										return false;
									}

									bool wasConflictDisconnectPersisted =
										await viewModel
										.FailClosedAuthenticatedProviderBindingConflictAsync(
											account,
											CancellationToken.None,
											isAccountMutationGateHeld: true);
									didFinishAccountChange =
										!account.IsProviderAccountChangeInProgress;
									if (!wasConflictDisconnectPersisted ||
										account.IsProviderAccountChangeInProgress ||
										!string.IsNullOrWhiteSpace(
											account.Profile.ProviderAccountIdentity))
									{
										return false;
									}
								}

								return await RejectDuplicateGrokConnectionPendingWorkAsync(
									bindingStore,
									pendingStore,
									work,
									binding);
							}

							if (!IsGrokRecoveryAccountAttached(viewModel, account))
							{
								return false;
							}

							if (hasCorrelatedBinding)
							{
								if (!binding!.MatchesPrincipal(
										verifiedUsage.Principal.PrincipalType,
										verifiedUsage.Principal.PrincipalId))
								{
									return false;
								}
							}
							else
							{
								binding = GrokAccountBinding.Create(
									work.AccountId,
									work.AttemptId,
									verifiedUsage.Principal.PrincipalType,
									verifiedUsage.Principal.PrincipalId);
								await bindingStore.SaveAsync(binding, cancellationToken);
							}

							if (!IsGrokRecoveryAccountAttached(viewModel, account))
							{
								return false;
							}

							bool didAdvance = await pendingStore.TryAdvanceAsync(
								work.AccountId,
								work.AttemptId,
								GrokConnectionPendingStage.BindingPersisted,
								work.AttemptId,
								DateTimeOffset.UtcNow,
								cancellationToken);
							if (!didAdvance ||
								!IsGrokRecoveryAccountAttached(viewModel, account))
							{
								return false;
							}

							publicBindingId = work.AttemptId;
						}

						string publicBindingIdentity =
							GrokAccountBinding.CreatePublicBindingIdentity(publicBindingId);

						if (string.Equals(
								account.Profile.ProviderAccountIdentity,
								publicBindingIdentity,
								StringComparison.Ordinal))
						{
							bool wasCommittedJournalRemoved = await pendingStore.TryRemoveAsync(
								work.AccountId,
								work.AttemptId,
								cancellationToken);
							if (wasCommittedJournalRemoved)
							{
								account.AbortProviderAccountChange();
								didFinishAccountChange = true;
							}

							return wasCommittedJournalRemoved;
						}

						if (!IsGrokRecoveryAccountAttached(viewModel, account))
						{
							return false;
						}

						account.SeedProviderAccountIdentity(publicBindingIdentity);

						if (!account.TryBeginProviderAccountChangeCommit())
						{
							return false;
						}

						AuthenticatedProviderBindingCommitResult result =
							await viewModel.TryPersistAuthenticatedProviderBindingAsync(
								account,
								publicBindingIdentity,
								cancellationToken,
								isAccountMutationGateHeld: true);

						if (result == AuthenticatedProviderBindingCommitResult
								.IdentityConflictRuntimeFailClosed)
						{
							didFinishAccountChange = true;
							return false;
						}

						if (result ==
							AuthenticatedProviderBindingCommitResult.IdentityConflict)
						{
							didFinishAccountChange = true;
							return await RejectDuplicateGrokConnectionPendingWorkAsync(
								bindingStore,
								pendingStore,
								work,
								binding);
						}

						if ((result != AuthenticatedProviderBindingCommitResult.Succeeded) ||
							!IsGrokRecoveryAccountAttached(viewModel, account))
						{
							return false;
						}

						bool didRemove = await pendingStore.TryRemoveAsync(
							work.AccountId,
							work.AttemptId,
							cancellationToken);
						account.CompleteProviderAccountChange();
						didFinishAccountChange = true;
						return didRemove;
					}
				},
				cancellationToken);
		}
		finally
		{
			if (!didFinishAccountChange)
			{
				account.AbortProviderAccountChange();
			}
		}
	}

	private static bool IsGrokRecoveryAccountAttached(
		DashboardViewModel viewModel,
		AccountUsageViewModel account)
	{
		return account.IsGrok &&
			account.IsEnabled &&
			account.IsProviderAccountChangeInProgress &&
			viewModel.Accounts.Contains(account);
	}

	private static async Task<bool>
		RejectDuplicateGrokConnectionPendingWorkAsync(
			IGrokAccountBindingStore bindingStore,
			IGrokConnectionPendingStore pendingStore,
			GrokConnectionPendingWork work,
			GrokAccountBinding? correlatedBinding)
	{
		if (correlatedBinding is null)
		{
			return await pendingStore.TryRemoveAsync(
				work.AccountId,
				work.AttemptId,
				CancellationToken.None);
		}

		await bindingStore.DeleteAsync(
			work.AccountId,
			CancellationToken.None);

		try
		{
			bool didRemove = await pendingStore.TryRemoveAsync(
				work.AccountId,
				work.AttemptId,
				CancellationToken.None);

			if (!didRemove)
			{
				await bindingStore.SaveAsync(
					correlatedBinding,
					CancellationToken.None);
			}

			return didRemove;
		}
		catch (Exception removalException)
		{
			try
			{
				await bindingStore.SaveAsync(
					correlatedBinding,
					CancellationToken.None);
			}
			catch (Exception restorationException)
			{
				throw new AggregateException(
					removalException,
					restorationException);
			}

			throw;
		}
	}

	private static async Task DeleteOrphanGrokBindingsAsync(
		DashboardViewModel viewModel,
		IGrokAccountBindingStore bindingStore,
		Guid accountId,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<GrokAccountBinding> bindings =
			await bindingStore.LoadAllAsync(cancellationToken);
		foreach (GrokAccountBinding binding in bindings.Where(
			binding => binding.AccountId != accountId))
		{
			AccountUsageViewModel? candidate = viewModel.Accounts.FirstOrDefault(
				candidate =>
					candidate.IsGrok &&
					(candidate.Id == binding.AccountId));
			if (candidate is null)
			{
				await bindingStore.DeleteAsync(
					binding.AccountId,
					cancellationToken);
				GrokAccountBinding? deletedBinding = await bindingStore.LoadAsync(
					binding.AccountId,
					cancellationToken);
				if (deletedBinding is not null)
				{
					throw new IOException(
						"Grok orphan private binding 清理未通過 read-back 驗證。");
				}
			}
		}
	}

	private static async Task<bool> IsGrokPrincipalBoundToAnotherAccountAsync(
		DashboardViewModel viewModel,
		IGrokAccountBindingStore bindingStore,
		Guid accountId,
		GrokPrincipal principal,
		CancellationToken cancellationToken)
	{
		foreach (AccountUsageViewModel candidate in viewModel.Accounts.Where(
			candidate => candidate.IsGrok && (candidate.Id != accountId)))
		{
			GrokAccountBinding? binding = await bindingStore.LoadAsync(
				candidate.Id,
				cancellationToken);
			if ((binding is not null) &&
				binding.MatchesPrincipal(
					principal.PrincipalType,
					principal.PrincipalId))
			{
				return true;
			}
		}

		return false;
	}
}
