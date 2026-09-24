using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using AiUsageDashboard.App;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class CodexResetCreditsWindowTests
{
	private static readonly DateTimeOffset Now = new(
		2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void CreateViewState_WithCountOnlyOrOldSnapshot_DoesNotInventCreditRows()
	{
		UsageSnapshot snapshot = CreateSnapshot(
			2,
			Now.AddMinutes(-1),
			new CodexResetCreditDetails(2, null, false),
			staleAfter: Now.AddMinutes(1));

		CodexResetCreditsWindow.ViewState countOnly =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);
		CodexResetCreditsWindow.ViewState oldSnapshot =
			CodexResetCreditsWindow.CreateViewState(
				"工作帳號",
				snapshot with { CodexResetCredits = null },
				Now);

		Assert.Equal("可用 2 張", countOnly.AvailableCountText);
		Assert.Empty(countOnly.CreditRows);
		Assert.Contains("沒有逐張資料", countOnly.DetailStateText);
		Assert.False(countOnly.IsStale);
		Assert.Equal(
			"可用 2 次",
			oldSnapshot.AvailableCountText);
		Assert.Empty(oldSnapshot.CreditRows);
		Assert.Contains("沒有逐張資料", oldSnapshot.DetailStateText);
	}

	[Fact]
	public void CreateViewState_WithPartialDetails_ShowsOnlyAvailableRows()
	{
		CodexResetCreditDetails details = new(
			3,
			[
				new CodexResetCredit(
					"available", "codexRateLimits", Now.AddDays(-2),
					Now.AddDays(2), "可用券", null),
				new CodexResetCredit(
					"redeemed", "codexRateLimits", Now.AddDays(-3),
					Now.AddDays(1), "已兌換券", null)
			],
			false);
		UsageSnapshot snapshot = CreateSnapshot(
			3,
			Now.AddMinutes(-1),
			details,
			staleAfter: Now.AddMinutes(1));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal("可用 3 張", state.AvailableCountText);
		Assert.Equal("工作帳號", state.AccountText);
		Assert.StartsWith("資料時間 ", state.FetchedAtText);
		Assert.True(state.HasFetchedAtText);
		Assert.Contains("已取得的 1 張可用券", state.DetailStateText);
		Assert.Equal("可用券", Assert.Single(state.CreditRows).Heading);
		Assert.False(state.IsStale);
	}

	[Fact]
	public void CreateViewState_WithMixedStatuses_NumbersOnlyAvailableRows()
	{
		CodexResetCreditDetails details = new(
			2,
			[
				new CodexResetCredit(
					"redeemed", null, null, null, "已兌換券", null),
				new CodexResetCredit(
					"available", null, null, Now.AddDays(1), null, null),
				new CodexResetCredit(
					"expired", null, null, null, "已到期券", null),
				new CodexResetCredit(
					"futureStatus", null, null, null, "未知狀態券", null),
				new CodexResetCredit(
					"available", null, null, Now.AddDays(2), null, null)
			],
			true);
		UsageSnapshot snapshot = CreateSnapshot(
			2, Now.AddMinutes(-1), details, staleAfter: Now.AddMinutes(1));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal("可用 2 張", state.AvailableCountText);
		Assert.Empty(state.DetailStateText);
		Assert.Collection(
			state.CreditRows,
			credit => Assert.Equal("重置券 1", credit.Heading),
			credit => Assert.Equal("重置券 2", credit.Heading));
	}

	[Fact]
	public void CreateViewState_WithNoAvailableCredits_HidesUsedRows()
	{
		CodexResetCreditDetails details = new(
			0,
			[
				new CodexResetCredit(
					"redeemed", null, null, null, "已兌換券", null),
				new CodexResetCredit(
					"expired", null, null, null, "已到期券", null)
			],
			true);
		UsageSnapshot snapshot = CreateSnapshot(
			0, Now.AddMinutes(-1), details, staleAfter: Now.AddMinutes(1));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal("可用 0 張", state.AvailableCountText);
		Assert.Empty(state.CreditRows);
		Assert.Equal("目前沒有可用重置券。", state.DetailStateText);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 2)]
	public void CreateViewState_WhenAvailableRowsExceedCount_HidesContradictoryDetails(
		long availableCount,
		int rowCount)
	{
		CodexResetCredit[] credits = Enumerable.Range(1, rowCount)
			.Select(index => new CodexResetCredit(
				"available", null, null, Now.AddDays(index),
				$"可用券 {index}", null))
			.ToArray();
		UsageSnapshot snapshot = CreateSnapshot(
			availableCount,
			Now.AddMinutes(-1),
			new CodexResetCreditDetails(availableCount, credits, false));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal($"可用 {availableCount} 張", state.AvailableCountText);
		Assert.Empty(state.CreditRows);
		Assert.Contains("不一致", state.DetailStateText);
		Assert.DoesNotContain("目前沒有可用重置券", state.DetailStateText);
	}

	[Fact]
	public void CreateViewState_WithParserCountConflict_ShowsMismatchWithEmptyDetails()
	{
		UsageSnapshot snapshot = CreateSnapshot(
			0,
			Now.AddMinutes(-1),
			new CodexResetCreditDetails(
				0, Array.Empty<CodexResetCredit>(), false,
				HasCountConflict: true));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal("可用 0 張", state.AvailableCountText);
		Assert.Empty(state.CreditRows);
		Assert.Contains("不一致", state.DetailStateText);
		Assert.DoesNotContain("目前沒有可用重置券", state.DetailStateText);
	}

	[Fact]
	public void CreateViewState_ColorsEachExpiryWithinTwoDays()
	{
		DateTimeOffset?[] expiryTimes =
		[
			Now.AddTicks(1),
			Now.AddHours(47),
			Now.AddDays(2),
			Now.AddDays(2).AddTicks(1),
			null
		];
		CodexResetCreditDetails details = new(
			expiryTimes.Length,
			expiryTimes.Select((expiresAt, index) =>
				new CodexResetCredit(
					"available", null, null, expiresAt,
					$"可用券 {index + 1}", null)).ToArray(),
			true);
		UsageSnapshot snapshot = CreateSnapshot(
			expiryTimes.Length, Now, details);

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal(
			[true, true, true, false, false],
			state.CreditRows.Select(row => row.IsResetImminent));
		Assert.All(state.CreditRows, row =>
			Assert.DoesNotContain("時間已過", row.ExpiresAtText));
	}

	[Fact]
	public void CreateViewState_WhenAvailableCreditsExpireAfterSnapshot_HidesExpiredRows()
	{
		CodexResetCreditDetails details = new(
			3,
			[
				new CodexResetCredit(
					"available", null, null, Now.AddTicks(-1), "剛到期", null),
				new CodexResetCredit(
					"available", null, null, Now, "此刻到期", null),
				new CodexResetCredit(
					"available", null, null, Now.AddTicks(1), "仍可用", null)
			],
			true);
		UsageSnapshot snapshot = CreateSnapshot(
			3, Now.AddMinutes(-1), details, staleAfter: Now.AddMinutes(1));

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Equal("上次資料可用 3 張", state.AvailableCountText);
		Assert.Equal("仍可用", Assert.Single(state.CreditRows).Heading);
		Assert.Contains("已到期", state.DetailStateText);
		Assert.False(state.IsStale);
	}

	[Fact]
	public void CreateViewState_DistinguishesNeverExpiresFromMissingExpiry()
	{
		CodexResetCreditDetails details = new(
			2,
			[
				new CodexResetCredit(
					"available", null, null, null, "無期限券", null,
					HasExpiresAtField: true),
				new CodexResetCredit(
					"available", null, null, null, "舊版未附時間", null)
			],
			true);
		UsageSnapshot snapshot = CreateSnapshot(2, Now, details);

		CodexResetCreditsWindow.ViewState state =
			CodexResetCreditsWindow.CreateViewState("工作帳號", snapshot, Now);

		Assert.Collection(
			state.CreditRows,
			credit => Assert.Equal("不會到期", credit.ExpiresAtText),
			credit => Assert.Equal("未提供到期時間", credit.ExpiresAtText));
	}

	[Fact]
	public void GetNextExpiryRecheckDelay_UsesNearestFutureAvailableExpiry()
	{
		CodexResetCreditDetails details = new(
			3,
			[
				new CodexResetCredit(
					"available", null, null, Now.AddMinutes(2), null, null),
				new CodexResetCredit(
					"redeemed", null, null, Now.AddSeconds(1), null, null),
				new CodexResetCredit(
					"available", null, null, Now.AddSeconds(30), null, null),
				new CodexResetCredit(
					"available", null, null, null, null, null,
					HasExpiresAtField: true)
			],
			true);

		TimeSpan? delay = CodexResetCreditsWindow.GetNextExpiryRecheckDelay(
			details, Now);

		Assert.Equal(TimeSpan.FromSeconds(30).Add(TimeSpan.FromMilliseconds(100)),
			delay);
	}

	[Fact]
	public void GetNextExpiryRecheckDelay_WhenNoFutureAvailableExpiry_ReturnsNull()
	{
		CodexResetCreditDetails details = new(
			3,
			[
				new CodexResetCredit(
					"available", null, null, Now.AddTicks(-1), null, null),
				new CodexResetCredit(
					"available", null, null, Now, null, null),
				new CodexResetCredit(
					"available", null, null, null, null, null,
					HasExpiresAtField: true),
				new CodexResetCredit(
					"redeemed", null, null, Now.AddMinutes(1), null, null)
			],
			true);

		Assert.Null(CodexResetCreditsWindow.GetNextExpiryRecheckDelay(
			details, Now));
	}

	[Fact]
	public void CreateViewState_WithStaleOrMissingSnapshot_DoesNotPresentCurrentInventory()
	{
		UsageSnapshot staleSnapshot = CreateSnapshot(
			1,
			Now.AddHours(-3),
			new CodexResetCreditDetails(
				1,
				[
					new CodexResetCredit(
						"available", null, null, Now.AddHours(-1),
						null, null)
				],
				true),
			status: SnapshotStatus.Stale,
			staleAfter: Now.AddHours(-1));

		CodexResetCreditsWindow.ViewState stale =
			CodexResetCreditsWindow.CreateViewState(
				"工作帳號", staleSnapshot, Now);
		CodexResetCreditsWindow.ViewState missing =
			CodexResetCreditsWindow.CreateViewState("工作帳號", null, Now);

		Assert.True(stale.IsStale);
		Assert.Contains("資料較舊", stale.StaleText);
		Assert.Equal("上次資料可用 1 張", stale.AvailableCountText);
		Assert.Empty(stale.CreditRows);
		Assert.Contains("已到期", stale.DetailStateText);
		Assert.Equal("可用張數尚未取得", missing.AvailableCountText);
		Assert.Equal("工作帳號", missing.AccountText);
		Assert.Empty(missing.FetchedAtText);
		Assert.False(missing.HasFetchedAtText);
		Assert.Contains("尚未取得", missing.DetailStateText);
		Assert.Empty(missing.CreditRows);
	}

	[Fact]
	public async Task Window_WhenCardChanges_UpdatesAndClearsItsOwnDetails()
	{
		await RunOnStaThreadAsync(() =>
		{
			ResourceDictionary resources = CreateWindowResources();
			AccountProfile profile = new(
				Guid.Parse("22222222-2222-2222-2222-222222222222"),
				ProviderKind.Codex,
				string.Empty,
				ProviderAccountIdentity: "codex-one");
			AccountUsageViewModel account = new(profile, canManage: true);
			ObservableCollection<AccountUsageViewModel> accounts = [account];
			account.ApplySnapshot(CreateSnapshot(
				1,
				Now,
				new CodexResetCreditDetails(
					1,
					[new CodexResetCredit(
						"available", null, null, Now.AddDays(1), null, null)],
					true)) with
			{
				Account = profile,
				ProviderAccountIdentity = "codex-one"
			});
			CodexResetCreditsWindow window = new(account, accounts, resources)
			{
				Opacity = 0,
				ShowActivated = false,
				ShowInTaskbar = false
			};
			bool isClosed = false;
			try
			{
				window.Show();
				Dispatcher.CurrentDispatcher.Invoke(
					DispatcherPriority.ApplicationIdle,
					new Action(() => { }));
				Assert.Equal(
					"可用 1 張",
					Assert.IsType<CodexResetCreditsWindow.ViewState>(
						window.DataContext).AvailableCountText);
				CodexResetCreditsWindow.ViewState initial =
					Assert.IsType<CodexResetCreditsWindow.ViewState>(window.DataContext);
				Assert.Equal(account.AccountCardDisplayText, initial.AccountText);
				Assert.False(initial.AccountText.StartsWith(
					"Codex · ", StringComparison.Ordinal));

				account.ApplySnapshot(CreateSnapshot(
					2,
					Now.AddMinutes(1),
					new CodexResetCreditDetails(
						2,
						[
							new CodexResetCredit(
								"available", null, null, Now.AddDays(1), null, null),
							new CodexResetCredit(
								"available", null, null, Now.AddDays(2), null, null)
						],
						true)) with
				{
					Account = profile,
					ProviderAccountIdentity = "codex-one"
				});
				CodexResetCreditsWindow.ViewState refreshed =
					Assert.IsType<CodexResetCreditsWindow.ViewState>(window.DataContext);
				Assert.Equal("可用 2 張", refreshed.AvailableCountText);
				Assert.Equal(2, refreshed.CreditRows.Count);
				AssertExpiryTextUsesWarningBrush(window);

				account.SeedProviderAccountIdentity("codex-two");
				CodexResetCreditsWindow.ViewState switched =
					Assert.IsType<CodexResetCreditsWindow.ViewState>(window.DataContext);
				Assert.Equal("可用張數尚未取得", switched.AvailableCountText);
				Assert.Empty(switched.CreditRows);

				accounts.Remove(account);
				Assert.False(window.IsVisible);
				isClosed = true;
			}
			finally
			{
				if (!isClosed)
				{
					window.Close();
				}
			}
		});
	}

	private static UsageSnapshot CreateSnapshot(
		long availableCount,
		DateTimeOffset fetchedAt,
		CodexResetCreditDetails? details,
		SnapshotStatus status = SnapshotStatus.Ready,
		DateTimeOffset? staleAfter = null)
	{
		AccountProfile account = new(
			Guid.Parse("11111111-1111-1111-1111-111111111111"),
			ProviderKind.Codex,
			"工作帳號");
		return new UsageSnapshot(
			account,
			[
				new UsageMetric(
					"codex:rate_limit_reset_credits",
					"可用重置次數",
					null,
					$"{availableCount} 次")
			],
			SourceTrust.OfficialExperimental,
			status,
			fetchedAt,
			ObservedAt: fetchedAt,
			StaleAfter: staleAfter,
			CodexResetCredits: details);
	}

	private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
		where T : DependencyObject
	{
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, index);
			if (child is T matchingChild)
			{
				yield return matchingChild;
			}

			foreach (T descendant in FindVisualChildren<T>(child))
			{
				yield return descendant;
			}
		}
	}

	private static void AssertExpiryTextUsesWarningBrush(
		CodexResetCreditsWindow window)
	{
		CodexResetCreditDetails details = new(
			2,
			[
				new CodexResetCredit(
					"available", null, null, Now.AddDays(1), "即將到期", null),
				new CodexResetCredit(
					"available", null, null, Now.AddDays(3), "較晚到期", null)
			],
			true);
		window.DataContext = CodexResetCreditsWindow.CreateViewState(
			"工作帳號", CreateSnapshot(2, Now, details), Now);
		Dispatcher.CurrentDispatcher.Invoke(
			DispatcherPriority.ApplicationIdle,
			new Action(() => { }));
		TextBlock[] expiryTextBlocks = FindVisualChildren<TextBlock>(window)
			.Where(block => block.Text.StartsWith(
				"到期 ", StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(2, expiryTextBlocks.Length);
		SolidColorBrush warningBrush = Assert.IsType<SolidColorBrush>(
			window.Resources["WarningTextBrush"]);
		SolidColorBrush secondaryBrush = Assert.IsType<SolidColorBrush>(
			window.Resources["SecondaryTextBrush"]);
		Assert.Equal(
			warningBrush.Color,
			Assert.IsType<SolidColorBrush>(expiryTextBlocks[0].Foreground).Color);
		Assert.Equal(FontWeights.SemiBold, expiryTextBlocks[0].FontWeight);
		Assert.Equal(
			secondaryBrush.Color,
			Assert.IsType<SolidColorBrush>(expiryTextBlocks[1].Foreground).Color);
	}

	private static ResourceDictionary CreateWindowResources()
	{
		ResourceDictionary controls =
			(ResourceDictionary)Application.LoadComponent(new Uri(
				"/AiUsageDashboard.App;component/Themes/Controls.xaml",
				UriKind.Relative));
		ResourceDictionary resources = new();
		resources.MergedDictionaries.Add(controls);
		resources["BooleanToVisibilityConverter"] =
			new BooleanToVisibilityConverter();
		resources["WarningTextBrush"] = Brushes.Orange;
		resources["SecondaryTextBrush"] = Brushes.Gray;
		return resources;
	}

	private static Task RunOnStaThreadAsync(Action operation)
	{
		TaskCompletionSource<bool> completion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			Exception? failure = null;
			try
			{
				operation();
			}
			catch (Exception exception)
			{
				failure = exception;
			}

			if (failure is null)
			{
				completion.TrySetResult(true);
			}
			else
			{
				completion.TrySetException(failure);
			}
		})
		{
			IsBackground = true
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}
}
