using System.Diagnostics;

using AiUsageDashboard.App;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

using Xunit.Abstractions;

namespace AiUsageDashboard.Tests;

public sealed class LocalizationPresentationTests
{
	private readonly ITestOutputHelper _output;

	public LocalizationPresentationTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task LanguageScopes_AreIndependentAcrossConcurrentAsyncFlows()
	{
		using IDisposable originalScope = UiText.UseLanguage(AppLanguage.English);
		Task english = Task.Run(async () =>
		{
			using IDisposable scope = UiText.UseLanguage(AppLanguage.English);
			await Task.Yield();
			Assert.Equal("Ready", UiText.Translate("可用"));
			Assert.Equal("en", UiText.Culture.Name);
		});
		Task chinese = Task.Run(async () =>
		{
			using IDisposable scope = UiText.UseLanguage(AppLanguage.TraditionalChinese);
			await Task.Yield();
			Assert.Equal("可用", UiText.Translate("Ready"));
			Assert.Equal("zh-TW", UiText.Culture.Name);
		});
		await Task.WhenAll(english, chinese);
		Assert.Equal(AppLanguage.English, UiText.CurrentLanguage);
	}

	[Fact]
	public void Translate_KnownTemplatesPreserveIdentifiersAndUnknownDiagnostics()
	{
		const string Nickname = "正在檢查… · Ready";
		const string Diagnostic = "正在檢查… provider returned opaque diagnostic /週/Ready";
		using (UiText.UseLanguage(AppLanguage.English))
		{
			Assert.Equal($"Added account “{Nickname}”.", UiText.Translate($"已新增帳號「{Nickname}」。"));
			Assert.Equal(Diagnostic, UiText.Translate(Diagnostic));
			string longDiagnostic = "正在檢查… " + new string('x', 8000);
			Assert.Equal(longDiagnostic, UiText.Translate(longDiagnostic));
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal($"已新增帳號「{Nickname}」。", UiText.Translate($"Added account “{Nickname}”."));
			Assert.Equal(Diagnostic, UiText.Translate(Diagnostic));
		}
	}

	[Theory]
	[InlineData(AppLanguage.English, 3000)]
	[InlineData(AppLanguage.English, 4000)]
	[InlineData(AppLanguage.English, 4096)]
	[InlineData(AppLanguage.TraditionalChinese, 3000)]
	[InlineData(AppLanguage.TraditionalChinese, 4000)]
	[InlineData(AppLanguage.TraditionalChinese, 4096)]
	public void Translate_LongUnknownDiagnosticWithinLimitPreservesOriginal(
		AppLanguage language,
		int characterCount)
	{
		using IDisposable scope = UiText.UseLanguage(language);
		const string Prefix = "Unknown provider diagnostic: ";
		string diagnostic = Prefix + new string('x', characterCount - Prefix.Length);

		Assert.Equal(diagnostic, UiText.Translate(diagnostic));
		Assert.Equal(diagnostic, UiText.Translate(diagnostic));
	}

	[Fact]
	public void AccountPresentation_LongUnknownDiagnosticRemainsReadableAfterLanguageSwitch()
	{
		using IDisposable english = UiText.UseLanguage(AppLanguage.English);
		const string Prefix = "Unknown provider diagnostic: ";
		string diagnostic = Prefix + new string('x', 4000 - Prefix.Length);
		AccountProfile profile = new(
			Guid.Parse("5215b806-d899-479d-a83d-e04b9cacd609"),
			ProviderKind.Grok,
			"Synthetic account",
			ProviderAccountIdentity: "synthetic-account");
		DateTimeOffset fetchedAt = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			[],
			SourceTrust.Official,
			SnapshotStatus.Error,
			fetchedAt,
			Error: diagnostic,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(snapshot);
		UsageSnapshot appliedSnapshot = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		Assert.Equal(diagnostic, account.SecondaryText);

		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account.RefreshLocalizedPresentation();
			Assert.Equal(diagnostic, account.SecondaryText);
			Assert.Same(appliedSnapshot, account.CurrentSnapshot);
		}

		account.RefreshLocalizedPresentation();
		Assert.Equal(diagnostic, account.SecondaryText);
		Assert.Equal(diagnostic, appliedSnapshot.Error);
		Assert.Same(appliedSnapshot, account.CurrentSnapshot);
	}

	[Fact]
	public void Translate_KnownWorkspaceIdentityStillPreservesBothAdjacentArguments()
	{
		const string Chinese = "研究團隊 · 進階 workspace · 本機連接碼 ABCD1234";
		const string English = "研究團隊 · Advanced workspace · Local connection code ABCD1234";
		using (UiText.UseLanguage(AppLanguage.English))
		{
			Assert.Equal(English, UiText.Translate(Chinese));
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal(Chinese, UiText.Translate(English));
		}
	}

	[Fact]
	public void AccountPresentation_HotSwitchPreservesSnapshotIdentityAndMetricEvidence()
	{
		AccountProfile profile = new(Guid.NewGuid(), ProviderKind.Grok, "用量狀態", ProviderAccountIdentity: "synthetic-account");
		UsageMetric metric = new("grok.weekly", "週用量", 84, "已使用 84%");
		DateTimeOffset observedAt = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);
		UsageSnapshot snapshot = new(profile, [metric], SourceTrust.Official, SnapshotStatus.Ready,
			observedAt.AddSeconds(10), ObservedAt: observedAt, ProviderAccountIdentity: profile.ProviderAccountIdentity);
		AccountUsageViewModel account;
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account = new AccountUsageViewModel(profile, canManage: true, UsageDisplayMode.Remaining);
			account.ApplySnapshot(snapshot);
			Assert.Equal("週用量", Assert.Single(account.UsageMetrics).Label);
		}
		UsageSnapshot projectedSnapshot = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		long revision = account.LifecycleRevision;
		List<string?> notifications = new();
		account.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
		using (UiText.UseLanguage(AppLanguage.English))
		{
			account.RefreshLocalizedPresentation();
			UsageMetricViewModel row = Assert.Single(account.UsageMetrics);
			Assert.Equal("Weekly usage", row.Label);
			Assert.Equal("16% remaining", row.DisplayValue);
			Assert.Equal("Weekly usage, remaining", row.ProgressAutomationName);
			Assert.Equal(UsageLevel.Warning, row.Level);
			Assert.Equal("Ready", account.DisplayStatusText);
			Assert.StartsWith("Data time ", account.StatusToolTip);
			Assert.Equal("用量狀態", account.AccountName);
			Assert.Same(projectedSnapshot, account.CurrentSnapshot);
			Assert.Equal(revision, account.LifecycleRevision);
			Assert.Equal("已使用 84%", metric.DisplayValue);
			Assert.Contains(string.Empty, notifications);
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account.RefreshLocalizedPresentation();
			Assert.Equal("剩餘 16%", Assert.Single(account.UsageMetrics).DisplayValue);
			Assert.Equal("可用", account.DisplayStatusText);
			Assert.StartsWith("資料時間 ", account.StatusToolTip);
			Assert.Same(projectedSnapshot, account.CurrentSnapshot);
		}
	}

	[Fact]
	public void AccountPresentation_HotSwitchDoesNotProjectDeferredConnectionMetrics()
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		AccountProfile profile = new(Guid.NewGuid(), ProviderKind.Codex, "synthetic");
		AccountUsageViewModel account = new(profile, canManage: true);
		account.BeginProviderAccountChange();
		account.SeedProviderAccountIdentity("connected@example.test");
		Assert.True(account.TryBeginProviderAccountChangeCommit());
		AccountProfile committed = profile with { ProviderAccountIdentity = "connected@example.test" };
		account.ApplyProfile(committed, canManage: true);
		account.SeedProviderAccountIdentity(committed.ProviderAccountIdentity!);
		account.PrepareAuthenticatedProviderConnectionRefresh();
		UsageSnapshot incoming = new(committed, [new("codex.primary", "5小時用量", 80, "已使用 80%")],
			SourceTrust.Official, SnapshotStatus.Ready, DateTimeOffset.UtcNow,
			ProviderAccountIdentity: committed.ProviderAccountIdentity);
		account.ApplySnapshot(incoming);
		UsageSnapshot projectedSnapshot = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		Assert.Equal(incoming.Metrics, projectedSnapshot.Metrics);
		Assert.Equal(incoming.ProviderAccountIdentity, projectedSnapshot.ProviderAccountIdentity);
		account.RefreshLocalizedPresentation();
		Assert.Equal("Connecting", account.DisplayStatusText);
		Assert.True(account.IsProviderAccountChangeInProgress);
		Assert.Equal(committed.ProviderAccountIdentity, account.ProviderAccountOwnershipIdentity);
		Assert.Same(projectedSnapshot, account.CurrentSnapshot);
		Assert.Null(account.StatusToolTip);
		Assert.Empty(account.UsageMetrics);
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account.RefreshLocalizedPresentation();
			Assert.Equal("連接中", account.DisplayStatusText);
			Assert.Empty(account.UsageMetrics);
			Assert.Same(projectedSnapshot, account.CurrentSnapshot);
		}
		account.CompleteProviderAccountChange();
		Assert.Equal("Used 80%", Assert.Single(account.UsageMetrics).DisplayValue);
	}

	[Fact]
	public void ClaudeAllModelsLabel_LocalizesKnownQualifierAndKeepsSource()
	{
		UsageMetric metric = new("claude.rate_limit.seven_day.all_models", "本週（所有模型）", 10, "已使用 10%");
		using (UiText.UseLanguage(AppLanguage.English))
		{
			UsageMetricViewModel row = new(metric);
			Assert.Equal("Weekly usage · All models", row.Label);
			Assert.Equal("Weekly usage · All models, used", row.ProgressAutomationName);
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal("週用量 · 所有模型", new UsageMetricViewModel(metric).Label);
		}
		Assert.Equal("本週（所有模型）", metric.Label);
	}

	[Fact]
	public void CopilotPresentation_EnglishKeepsExtraUsageAndOrganizationScopeInTooltip()
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		UsageMetric metric = new("copilot-quota-premium-interactions", "Premium requests", null,
			"已使用 12 次（此來源未提供上限），額外用量已開啟");
		UsageMetricViewModel row = new(metric, UsageDisplayMode.Used, showResetText: false, usePercentageDisplayValue: true);
		Assert.Contains("Used 12 requests", row.ToolTipValue);
		Assert.Contains("extra usage enabled", row.ToolTipValue, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Business/Enterprise organization AI Credits", row.ToolTipValue);
		Assert.Contains("GitHub Copilot settings", row.ToolTipValue);
		Assert.DoesNotContain('額', row.DisplayValue);
		Assert.False(row.HasResetText);
		Assert.False(row.HasUsageBar);
		Assert.Equal("已使用 12 次（此來源未提供上限），額外用量已開啟", metric.DisplayValue);
	}

	[Fact]
	public void AntigravityPreflight_BothLanguagesKeepVersionScopeAndCredentialWarnings()
	{
		using (UiText.UseLanguage(AppLanguage.English))
		{
			string message = AccountConnectionCoordinator.AntigravityPreflightMessage;
			Assert.Contains("1.1.11", message);
			Assert.Contains("excluding, 2.0.0", message);
			Assert.Contains("read-only /usage", message);
			Assert.Contains("cannot verify or pin an enterprise project scope", message);
			Assert.Contains("does not read sign-in credentials", message);
			Assert.Contains("metrics. AI Usage", message);
			Assert.Contains("command. This card", message);
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal(
				"請先登入 Antigravity。AI Usage 會確認這台電腦上的 Antigravity CLI 與既有連接，再讀取目前帳號的四項用量。" +
				"AI Usage 只接受 1.1.11 以上、2.0.0 未滿且支援官方唯讀 /usage 的版本。" +
				"這張卡會跟隨這台電腦目前的 Antigravity 登入；AI Usage 無法確認或固定企業專案範圍。這項操作不會讀取登入憑證。",
				AccountConnectionCoordinator.AntigravityPreflightMessage);
		}
	}

	[Fact]
	public void CatalogLoadAndRepeatedTranslations_ReportMeasuredCost()
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		Stopwatch stopwatch = Stopwatch.StartNew();
		Assert.Equal("Ready", UiText.Translate("可用"));
		stopwatch.Stop();
		_output.WriteLine($"First catalog access in this test: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
		const string Source = "已新增帳號「synthetic」。";
		Assert.Equal("Added account “synthetic”.", UiText.Translate(Source));
		stopwatch.Restart();
		for (int index = 0; index < 1000; index++)
		{
			Assert.Equal("Added account “synthetic”.", UiText.Translate(Source));
		}
		stopwatch.Stop();
		_output.WriteLine($"1000 cached translations: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
	}
}
