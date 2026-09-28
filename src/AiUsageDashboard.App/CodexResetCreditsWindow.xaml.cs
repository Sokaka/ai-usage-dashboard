using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

using FormsScreen = System.Windows.Forms.Screen;

namespace AiUsageDashboard.App;

public sealed partial class CodexResetCreditsWindow : Window
{
	public sealed record CreditRow
	{
		public string Heading { get; init; } = string.Empty;

		public string ExpiresAtText { get; init; } = string.Empty;

		public bool IsResetImminent { get; init; }
	}

	public sealed record ViewState
	{
		public string AvailableCountText { get; init; } = string.Empty;

		public string AccountText { get; init; } = string.Empty;

		public string FetchedAtText { get; init; } = string.Empty;

		public string DetailStateText { get; init; } = string.Empty;

		public string StaleText { get; init; } = string.Empty;

		public IReadOnlyList<CreditRow> CreditRows { get; init; } =
			Array.Empty<CreditRow>();

		public bool HasDetailState => DetailStateText.Length > 0;

		public bool HasFetchedAtText => FetchedAtText.Length > 0;

		public bool IsStale => StaleText.Length > 0;
	}

	private const double PreferredMaximumHeight = 600;
	private const double PreferredWidth = 410;
	private const double PreferredMinimumWidth = 320;
	private static readonly TimeSpan ExpiryTransitionDelay =
		TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan MaximumExpiryRecheckInterval =
		TimeSpan.FromDays(1);
	private readonly AccountUsageViewModel _account;
	private readonly ObservableCollection<AccountUsageViewModel> _accounts;
	private readonly DispatcherTimer _expiryTimer;
	private readonly TimeProvider _timeProvider;
	private bool _isClosed;

	internal AccountUsageViewModel Account => _account;

	private static string StaleSnapshotText =>
		UiText.Get("Windows.Credits.ThisDataIsOldCheckUsageForThis");

	public CodexResetCreditsWindow(
		AccountUsageViewModel account,
		ObservableCollection<AccountUsageViewModel> accounts)
		: this(account, accounts, null)
	{
	}

	internal CodexResetCreditsWindow(
		AccountUsageViewModel account,
		ObservableCollection<AccountUsageViewModel> accounts,
		ResourceDictionary? resources,
		TimeProvider? timeProvider = null)
	{
		_account = account ?? throw new ArgumentNullException(nameof(account));
		_accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
		_timeProvider = timeProvider ?? TimeProvider.System;
		if (!_account.IsCodex || !_accounts.Contains(_account))
		{
			throw new ArgumentException(
				UiText.Get("Windows.Credits.TheResetCreditsWindowRequiresACodexAccount"),
				nameof(account));
		}

		if (resources is not null)
		{
			Resources.MergedDictionaries.Add(resources);
		}

		_expiryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
		_expiryTimer.Tick += ExpiryTimer_Tick;
		InitializeComponent();
		UiText.LanguageChanged += LanguageChanged;
		IsVisibleChanged += Window_IsVisibleChanged;
		RefreshViewState();
		Loaded += (_, _) => CloseButton.Focus();
		DpiChanged += (_, _) => UpdateWorkAreaConstraints();
		LocationChanged += (_, _) => UpdateWorkAreaConstraints();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_account.PropertyChanged += Account_PropertyChanged;
		_accounts.CollectionChanged += Accounts_CollectionChanged;
		UpdateWorkAreaConstraints();
	}

	protected override void OnClosed(EventArgs e)
	{
		_isClosed = true;
		UiText.LanguageChanged -= LanguageChanged;
		_expiryTimer.Stop();
		_expiryTimer.Tick -= ExpiryTimer_Tick;
		IsVisibleChanged -= Window_IsVisibleChanged;
		_account.PropertyChanged -= Account_PropertyChanged;
		_accounts.CollectionChanged -= Accounts_CollectionChanged;
		base.OnClosed(e);
	}

	private void LanguageChanged(object? sender, EventArgs e)
	{
		RefreshViewState();
	}

	internal static UsageSnapshot? GetCompatibleSnapshot(
		AccountUsageViewModel account)
	{
		ArgumentNullException.ThrowIfNull(account);
		UsageSnapshot? snapshot = account.CurrentSnapshot;
		return account.IsEnabled &&
			account.IsCodex &&
			(snapshot is not null) &&
			(snapshot.Account.Id == account.Id) &&
			(snapshot.Account.Provider == ProviderKind.Codex) &&
			!string.IsNullOrWhiteSpace(account.ProviderAccountIdentity) &&
			!string.IsNullOrWhiteSpace(snapshot.ProviderAccountIdentity) &&
			ProviderAccountIdentityRules.Comparer.Equals(
				account.ProviderAccountIdentity,
				snapshot.ProviderAccountIdentity)
			? snapshot
			: null;
	}

	internal static ViewState CreateViewState(
		string accountName,
		UsageSnapshot? snapshot,
		DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
		if ((snapshot is not null) &&
			(snapshot.Account.Provider != ProviderKind.Codex))
		{
			snapshot = null;
		}

		ViewState state = new()
		{
			AvailableCountText = GetAvailableCountText(snapshot),
			AccountText = accountName,
			FetchedAtText = snapshot is null
				? string.Empty
				: UiText.Format("Windows.Credits.Fetched0G", snapshot.FetchedAt.ToLocalTime()),
			StaleText = snapshot?.IsStaleAt(now) == true
				? StaleSnapshotText
				: string.Empty
		};

		if (snapshot is null)
		{
			return state with
			{
				DetailStateText =
					UiText.Get("Windows.Credits.NoDataYetCheckUsageForThisAccount")
			};
		}

		if (snapshot.CodexResetCredits is not CodexResetCreditDetails details)
		{
			return state with
			{
				DetailStateText =
					UiText.Get("Windows.Credits.OnlyTheAvailableCountIsProvidedIndividualCredit")
			};
		}

		if (details.HasCountConflict)
		{
			return state with
			{
				DetailStateText =
					UiText.Get("Windows.Credits.CreditDetailsDoNotMatchTheAvailableCount")
			};
		}

		if (details.Credits is null)
		{
			return state with
			{
				DetailStateText =
					UiText.Get("Windows.Credits.OnlyTheAvailableCountIsProvidedIndividualCredit")
			};
		}

		CodexResetCredit[] availableCredits = details.Credits
			.Where(credit => string.Equals(
				credit.Status,
				CodexResetCredit.AvailableStatus,
				StringComparison.Ordinal))
			.ToArray();
		if (availableCredits.LongLength > details.AvailableCount)
		{
			return state with
			{
				DetailStateText =
					UiText.Get("Windows.Credits.CreditDetailsDoNotMatchTheAvailableCount")
			};
		}

		CodexResetCredit[] unexpiredCredits = availableCredits
			.Where(credit => (credit.ExpiresAt is null) ||
				(credit.ExpiresAt > now))
			.ToArray();
		bool hasExpiredCredits = unexpiredCredits.Length < availableCredits.Length;
		CreditRow[] rows = unexpiredCredits
			.Select((credit, index) => CreateCreditRow(credit, index, now))
			.ToArray();
		return state with
		{
			AvailableCountText = hasExpiredCredits
				? UiText.Format("Windows.Credits.LastResult0CreditsAvailable", details.AvailableCount)
				: state.AvailableCountText,
			DetailStateText = hasExpiredCredits
				? UiText.Get("Windows.Credits.SomeCreditsHaveExpiredCheckUsageForThis")
				: GetDetailStateText(details, rows.Length),
			CreditRows = rows
		};
	}

	private static string GetAvailableCountText(UsageSnapshot? snapshot)
	{
		if (snapshot?.CodexResetCredits is CodexResetCreditDetails details)
		{
			return UiText.Format("Windows.Credits.0CreditsAvailable", details.AvailableCount);
		}

		UsageMetric? countMetric = snapshot?.Metrics.FirstOrDefault(metric =>
			metric.Key == UsageMetricPresentation.CodexResetCreditsKey);
		return countMetric is null
			? UiText.Get("Windows.Credits.AvailableCountNotYetRetrieved")
			: UiText.Format("Windows.Credits.Available0", UiText.Translate(countMetric.DisplayValue));
	}

	private static string GetDetailStateText(
		CodexResetCreditDetails details,
		int visibleAvailableCreditCount)
	{
		if (details.AvailableCount == 0)
		{
			return UiText.Get("Windows.Credits.NoResetCreditsAreCurrentlyAvailable");
		}

		if (!details.IsComplete)
		{
			return UiText.Format("Windows.Credits.DetailsAreIncompleteShowingThe0AvailableCredits", visibleAvailableCreditCount);
		}

		if (visibleAvailableCreditCount == 0)
		{
			return UiText.Get("Windows.Credits.OnlyTheAvailableCountIsProvidedIndividualCredit");
		}

		return string.Empty;
	}

	private static CreditRow CreateCreditRow(
		CodexResetCredit credit,
		int index,
		DateTimeOffset now)
	{
		string expiresAtText = credit.ExpiresAt is DateTimeOffset expiresAt
			? UiText.Format("Windows.Credits.Expires0G", expiresAt.ToLocalTime())
			: credit.HasExpiresAtField
				? UiText.Get("Windows.Credits.DoesNotExpire")
				: UiText.Get("Windows.Credits.ExpiryTimeNotProvided");
		return new CreditRow
		{
			Heading = string.IsNullOrWhiteSpace(credit.Title)
				? UiText.Format("Windows.Credits.ResetCredit0", index + 1)
				: credit.Title.Trim(),
			ExpiresAtText = expiresAtText,
			IsResetImminent = credit.ExpiresAt is DateTimeOffset target &&
				UsageMetricPresentation.IsWithinTwoDayWarningWindow(target, now)
		};
	}

	internal static TimeSpan? GetNextExpiryRecheckDelay(
		CodexResetCreditDetails? details,
		DateTimeOffset now)
	{
		if (details?.Credits is not IReadOnlyList<CodexResetCredit> credits)
		{
			return null;
		}

		TimeSpan? shortestDelay = null;
		foreach (CodexResetCredit credit in credits)
		{
			if (!string.Equals(
				credit.Status,
				CodexResetCredit.AvailableStatus,
				StringComparison.Ordinal) ||
				(credit.ExpiresAt is not DateTimeOffset expiresAt))
			{
				continue;
			}

			TimeSpan remaining = expiresAt - now;
			if ((remaining > TimeSpan.Zero) &&
				((shortestDelay is null) || (remaining < shortestDelay.Value)))
			{
				shortestDelay = remaining;
			}
		}

		if (shortestDelay is null)
		{
			return null;
		}

		return shortestDelay.Value >=
			MaximumExpiryRecheckInterval - ExpiryTransitionDelay
			? MaximumExpiryRecheckInterval
			: shortestDelay.Value + ExpiryTransitionDelay;
	}

	private void Window_IsVisibleChanged(
		object sender,
		DependencyPropertyChangedEventArgs e)
	{
		if (IsVisible)
		{
			RefreshViewState();
		}
		else
		{
			_expiryTimer.Stop();
		}
	}

	private void ExpiryTimer_Tick(object? sender, EventArgs e)
	{
		_expiryTimer.Stop();
		RefreshViewState();
	}

	private void ScheduleExpiryRecheck(
		CodexResetCreditDetails? details,
		DateTimeOffset now)
	{
		_expiryTimer.Stop();
		if (!IsVisible ||
			(GetNextExpiryRecheckDelay(details, now) is not TimeSpan delay))
		{
			return;
		}

		_expiryTimer.Interval = delay;
		_expiryTimer.Start();
	}

	private void Account_PropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (!string.IsNullOrEmpty(e.PropertyName) &&
			(e.PropertyName != nameof(AccountUsageViewModel.CurrentSnapshot)) &&
			(e.PropertyName != nameof(AccountUsageViewModel.ProviderAccountIdentity)) &&
			(e.PropertyName != nameof(AccountUsageViewModel.AccountNickname)) &&
			(e.PropertyName != nameof(AccountUsageViewModel.AccountCardDisplayText)) &&
			(e.PropertyName != nameof(AccountUsageViewModel.IsEnabled)))
		{
			return;
		}

		RefreshViewState();
	}

	private void Accounts_CollectionChanged(
		object? sender,
		NotifyCollectionChangedEventArgs e)
	{
		if (_isClosed)
		{
			return;
		}

		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(() => Accounts_CollectionChanged(sender, e));
			return;
		}

		if (!_accounts.Contains(_account))
		{
			Close();
		}
	}

	private void RefreshViewState()
	{
		if (_isClosed)
		{
			return;
		}

		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.Invoke(RefreshViewState);
			return;
		}

		UsageSnapshot? snapshot = _accounts.Contains(_account)
			? GetCompatibleSnapshot(_account)
			: null;
		DateTimeOffset now = _timeProvider.GetUtcNow();
		DataContext = CreateViewState(
			_account.HasAccountNickname
				? _account.AccountNickname
				: _account.AccountCardDisplayText,
			snapshot,
			now);
		ScheduleExpiryRecheck(snapshot?.CodexResetCredits, now);
	}

	private void UpdateWorkAreaConstraints()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}

		System.Drawing.Rectangle workArea = FormsScreen.FromHandle(handle).WorkingArea;
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		double maximumWidth = WindowWorkAreaLayout.CalculateMaxWidth(
			PreferredMinimumWidth,
			workArea.Width,
			dpi.DpiScaleX);
		MinWidth = Math.Min(PreferredMinimumWidth, maximumWidth);
		MaxWidth = Math.Min(PreferredWidth, maximumWidth);
		MaxHeight = Math.Min(
			PreferredMaximumHeight,
			WindowWorkAreaLayout.CalculateMaxHeight(
				0,
				workArea.Height,
				dpi.DpiScaleY));
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
