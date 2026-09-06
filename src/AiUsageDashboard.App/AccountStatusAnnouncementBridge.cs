using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;

using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App;

internal static class AccountStatusAnnouncementPolicy
{
	internal static string? CreateAnnouncement(AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.IsProviderAccountChangeInProgress)
		{
			return $"帳號「{account.AccountName}」：{account.DisplayStatusText}。";
		}

		bool hasInlineAutomaticRetryStatus =
			(account.StatusKind == AccountStatusKind.Error) &&
			(account.RecoveryAction == UsageRecoveryAction.Retry) &&
			!account.IsAntigravity;

		if (account.HasRecoveryAction &&
			!account.ShowRecoveryActionNotice &&
			!account.HasNotice &&
			!hasInlineAutomaticRetryStatus)
		{
			return null;
		}

		if ((account.StatusKind is AccountStatusKind.Ready or
				AccountStatusKind.Refreshing) &&
			!account.HasNotice &&
			!account.HasRecoveryAction)
		{
			return null;
		}

		List<string> details = new();

		if (account.HasNotice)
		{
			details.Add(account.NoticeText.Trim());
		}

		if (account.ShowRecoveryActionNotice &&
			!string.IsNullOrWhiteSpace(account.RecoveryActionDescription))
		{
			details.Add(account.RecoveryActionDescription.Trim());
		}

		if ((details.Count == 0) &&
			!string.IsNullOrWhiteSpace(account.SecondaryText))
		{
			details.Add(account.SecondaryText.Trim());
		}

		string announcement =
			$"帳號「{account.AccountName}」：{account.DisplayStatusText}。";

		if (details.Count > 0)
		{
			announcement += $" {string.Join(" ", details.Distinct())}";
		}

		return announcement;
	}

	internal static bool IsAnnouncementProperty(string? propertyName)
	{
		return string.IsNullOrEmpty(propertyName) ||
			(propertyName == nameof(AccountUsageViewModel.DisplayStatusText)) ||
			(propertyName == nameof(AccountUsageViewModel.NoticeText)) ||
			(propertyName ==
				nameof(AccountUsageViewModel.RecoveryActionDescription)) ||
			(propertyName == nameof(AccountUsageViewModel.SecondaryText));
	}
}

internal sealed class AccountStatusAnnouncementBridge : IDisposable
{
	private const int MaximumAnnouncementsPerBatch = 3;
	private readonly Action<string> _announce;
	private readonly Dispatcher _dispatcher;
	private readonly List<AccountUsageViewModel> _pendingAccounts = new();
	private readonly HashSet<Guid> _pendingAccountIds = new();
	private readonly HashSet<AccountUsageViewModel> _subscribedAccounts = new();
	private readonly DashboardViewModel _viewModel;
	private bool _isDisposed;
	private bool _isFlushQueued;

	internal AccountStatusAnnouncementBridge(
		DashboardViewModel viewModel,
		Dispatcher dispatcher,
		Action<string> announce)
	{
		_viewModel = viewModel ??
			throw new ArgumentNullException(nameof(viewModel));
		_dispatcher = dispatcher ??
			throw new ArgumentNullException(nameof(dispatcher));
		_announce = announce ??
			throw new ArgumentNullException(nameof(announce));

		_viewModel.Accounts.CollectionChanged += Accounts_CollectionChanged;
		_viewModel.PropertyChanged += ViewModel_PropertyChanged;
		SynchronizeAccountSubscriptions();
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		_viewModel.Accounts.CollectionChanged -= Accounts_CollectionChanged;
		_viewModel.PropertyChanged -= ViewModel_PropertyChanged;

		foreach (AccountUsageViewModel account in _subscribedAccounts)
		{
			account.PropertyChanged -= Account_PropertyChanged;
		}

		_subscribedAccounts.Clear();
		_pendingAccounts.Clear();
		_pendingAccountIds.Clear();
	}

	private void Account_PropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (_isDisposed ||
			(sender is not AccountUsageViewModel account) ||
			!AccountStatusAnnouncementPolicy.IsAnnouncementProperty(
				e.PropertyName))
		{
			return;
		}

		if (_pendingAccountIds.Add(account.Id))
		{
			_pendingAccounts.Add(account);
		}

		QueueFlushWhenStable();
	}

	private void Accounts_CollectionChanged(
		object? sender,
		NotifyCollectionChangedEventArgs e)
	{
		SynchronizeAccountSubscriptions();
	}

	private void Flush()
	{
		_isFlushQueued = false;

		if (_isDisposed || _viewModel.IsRefreshing)
		{
			return;
		}

		List<string> announcements = _pendingAccounts
			.Where(account => _viewModel.Accounts.Contains(account))
			.Select(AccountStatusAnnouncementPolicy.CreateAnnouncement)
			.Where(message => !string.IsNullOrWhiteSpace(message))
			.Cast<string>()
			.Distinct(StringComparer.Ordinal)
			.ToList();
		_pendingAccounts.Clear();
		_pendingAccountIds.Clear();

		if (announcements.Count == 0)
		{
			return;
		}

		int remainingCount = Math.Max(
			0,
			announcements.Count - MaximumAnnouncementsPerBatch);
		string message = string.Join(
			"；",
			announcements.Take(MaximumAnnouncementsPerBatch));

		if (remainingCount > 0)
		{
			message += $"；另有 {remainingCount} 個帳號狀態已更新。";
		}

		_announce(message);
	}

	private void QueueFlushWhenStable()
	{
		if (_isDisposed || _viewModel.IsRefreshing || _isFlushQueued)
		{
			return;
		}

		_isFlushQueued = true;
		_ = _dispatcher.BeginInvoke(
			Flush,
			DispatcherPriority.Background);
	}

	private void SynchronizeAccountSubscriptions()
	{
		AccountUsageViewModel[] removedAccounts = _subscribedAccounts
			.Where(account => !_viewModel.Accounts.Contains(account))
			.ToArray();

		foreach (AccountUsageViewModel account in removedAccounts)
		{
			account.PropertyChanged -= Account_PropertyChanged;
			_subscribedAccounts.Remove(account);
			_pendingAccountIds.Remove(account.Id);
			_pendingAccounts.Remove(account);
		}

		foreach (AccountUsageViewModel account in _viewModel.Accounts)
		{
			if (_subscribedAccounts.Add(account))
			{
				account.PropertyChanged += Account_PropertyChanged;
			}
		}
	}

	private void ViewModel_PropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (string.Equals(
			e.PropertyName,
			nameof(DashboardViewModel.IsRefreshing),
			StringComparison.Ordinal) &&
			!_viewModel.IsRefreshing)
		{
			QueueFlushWhenStable();
		}
	}
}
