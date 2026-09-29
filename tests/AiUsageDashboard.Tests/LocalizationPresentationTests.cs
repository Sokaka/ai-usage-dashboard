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

	[Theory]
	[InlineData(ProviderKind.Claude,
		"Claude 暫時未回傳用量額度；AI Usage 稍後會自動再試，不需要操作。",
		"Claude has not returned a usage quota")]
	[InlineData(ProviderKind.Codex,
		"暫時無法讀取 Codex 用量，稍後會自動再試。",
		"Codex usage")]
	[InlineData(ProviderKind.Grok,
		"暫時無法讀取 Grok 用量，稍後會自動再試。",
		"Grok usage")]
	[InlineData(ProviderKind.Antigravity,
		"暫時無法讀取 Antigravity 用量，稍後會自動再試。",
		"Antigravity usage")]
	[InlineData(ProviderKind.Antigravity,
		"Antigravity 用量檢查未完成，稍後會自動再試。",
		"Antigravity usage check")]
	[InlineData(ProviderKind.Antigravity,
		"Antigravity 用量檢查尚未完成。AI Usage 稍後會自動再試，並保留現有帳號與用量資料。",
		"Antigravity usage check")]
	[InlineData(ProviderKind.Antigravity,
		"AI Usage 無法確認這次 Antigravity 用量讀取是否仍符合安全條件，稍後會自動重新檢查；既有登入不受影響。",
		"your existing sign-in is unaffected")]
	[InlineData(ProviderKind.Claude,
		"Claude Code 已登入有效訂閱，但暫時無法確認目前登入的帳號。AI Usage 稍後會自動再試。",
		"signed-in account")]
	[InlineData(ProviderKind.Claude,
		"Claude Code 已登入有效訂閱，但暫時無法確認目前的組織。AI Usage 稍後會自動再試。",
		"organization")]
	[InlineData(ProviderKind.Claude,
		"Claude `auth status` 暫時缺少可驗證的訂閱資訊。AI Usage 稍後會自動再試。",
		"subscription information")]
	[InlineData(ProviderKind.Claude,
		"暫時無法讀取 Claude 用量，稍後會自動再試。",
		"Claude usage")]
	[InlineData(ProviderKind.Claude,
		"Claude Code 已登入有效訂閱，但暫時無法確認目前的訂閱範圍。AI Usage 稍後會自動再試。",
		"subscription scope")]
	[InlineData(ProviderKind.Codex,
		"暫時無法確認 Codex 帳號與 workspace，稍後會自動再試。",
		"Codex account and workspace")]
	[InlineData(ProviderKind.Codex,
		"暫時無法確認 Codex CLI 版本，稍後會自動再試。",
		"Codex CLI version")]
	[InlineData(ProviderKind.Codex,
		"Codex 回傳無法辨識的錯誤；AI Usage 稍後會自動再試，不需要操作。",
		"Codex returned an unrecognized error")]
	[InlineData(ProviderKind.Grok,
		"暫時無法取得用量，AI Usage 稍後會自動再試。",
		"Usage is temporarily unavailable")]
	[InlineData(ProviderKind.Grok,
		"用量已重置，AI Usage 稍後會自動再試。",
		"Usage has reset")]
	[InlineData(ProviderKind.Claude,
		"Claude Code `/usage` 尚未確認是否產生用量就中斷。AI Usage 稍後會自動再試，並保留上次成功讀取的資料。",
		"before it could be determined whether it consumed usage")]
	[InlineData(ProviderKind.Claude,
		"Claude Code 2.1.3 的 `/usage` 回報錯誤，但用量結果顯示成功，暫時無法確認這次結果。AI Usage 稍後會自動再試，並保留上次成功讀取的資料。",
		"Claude Code 2.1.3 `/usage` reported an error")]
	[InlineData(ProviderKind.Codex,
		"暫時無法讀取 Codex 用量，稍後會自動再試。 CLI 版本診斷：偵測到 Codex CLI 0.145.0；本版相容性基準為 0.144.1。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
		"Detected Codex CLI 0.145.0; this version's compatibility baseline is 0.144.1")]
	[InlineData(ProviderKind.Codex,
		"Codex app-server 回應逾時。AI Usage 稍後會自動再試，並保留上次成功讀取的資料。 CLI 版本診斷：偵測到 Codex CLI 0.145.0；本版相容性基準為 0.144.1。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
		"Detected Codex CLI 0.145.0; this version's compatibility baseline is 0.144.1")]
	[InlineData(ProviderKind.Grok,
		"暫時無法讀取 Grok 用量，稍後會自動再試。 CLI 版本診斷：無法辨識 Grok CLI 版號；本版相容性基準為 1.0.3。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
		"Grok CLI version could not be recognized; this version's compatibility baseline is 1.0.3")]
	public void AccountWarning_StaleRetryLocalizesTheReasonAfterRemovingDuplicateGuidance(
		ProviderKind provider,
		string error,
		string expectedReason)
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		AccountProfile profile = new(
			Guid.Parse("c90a8f4d-1e52-4cb4-9571-201d3077ac68"),
			provider,
			"Synthetic account",
			HasAcceptedClaudeQuotaRisk: true,
			ProviderAccountIdentity: "synthetic-account");
		DateTimeOffset observedAt = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			[new("synthetic.primary", "5 小時用量", 10, "已使用 10%")],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			observedAt.AddMinutes(1),
			ObservedAt: observedAt,
			Error: error,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(snapshot);
		UsageSnapshot appliedSnapshot = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);

		string englishDescription = account.RecoveryActionDescription;
		Assert.DoesNotMatch(@"[\p{IsCJKUnifiedIdeographs}]", englishDescription);
		Assert.DoesNotContain('。', englishDescription);
		Assert.DoesNotContain("..", englishDescription);
		Assert.Contains(expectedReason, englishDescription, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("no action is needed", englishDescription, StringComparison.OrdinalIgnoreCase);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(englishDescription, "retry automatically"));
		Assert.DoesNotContain("check again automatically", englishDescription, StringComparison.OrdinalIgnoreCase);
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account.RefreshLocalizedPresentation();
			Assert.Contains("目前顯示", account.RecoveryActionDescription);
			Assert.Contains("AI Usage 稍後會自動再試，不需要手動操作。", account.RecoveryActionDescription);
			Assert.Same(appliedSnapshot, account.CurrentSnapshot);
		}
		account.RefreshLocalizedPresentation();
		Assert.Equal(englishDescription, account.RecoveryActionDescription);
		Assert.Same(appliedSnapshot, account.CurrentSnapshot);
		Assert.Equal(error, appliedSnapshot.Error);
		Assert.Equal(snapshot.ObservedAt, appliedSnapshot.ObservedAt);
		Assert.Equal(snapshot.Metrics, appliedSnapshot.Metrics);
	}

	[Theory]
	[InlineData("Opaque provider text: It will retry automatically later. [raw_271]")]
	[InlineData("Unknown provider diagnostic: 生資料 [opaque_271]\r\nOpaque note: AI Usage will retry automatically. No action is needed.")]
	[InlineData("Opaque provider text: It will retry automatically later. [raw_271] CLI 版本診斷：偵測到 Grok CLI 1.0.4；本版相容性基準為 1.0.3。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。")]
	public void AccountWarning_StaleRetryPreservesUnknownDiagnosticsThatMentionRetry(string diagnostic)
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		AccountProfile profile = new(
			Guid.Parse("6baabf08-33f4-46c1-8669-126b197ef96c"),
			ProviderKind.Grok,
			"Synthetic account",
			ProviderAccountIdentity: "synthetic-account");
		DateTimeOffset observedAt = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			[new("grok.weekly", "週用量", 10, "已使用 10%")],
			SourceTrust.Official,
			SnapshotStatus.Stale,
			observedAt.AddMinutes(1),
			ObservedAt: observedAt,
			Error: diagnostic,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(snapshot);
		UsageSnapshot appliedSnapshot = Assert.IsType<UsageSnapshot>(account.CurrentSnapshot);
		string englishDescription = account.RecoveryActionDescription;
		string rawReason = diagnostic.Split(" CLI 版本診斷：", StringSplitOptions.None)[0];
		Assert.Contains(rawReason, englishDescription);
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			account.RefreshLocalizedPresentation();
			Assert.Contains(rawReason, account.RecoveryActionDescription);
			Assert.Same(appliedSnapshot, account.CurrentSnapshot);
		}
		account.RefreshLocalizedPresentation();
		Assert.Equal(englishDescription, account.RecoveryActionDescription);
		Assert.Equal(diagnostic, appliedSnapshot.Error);
		Assert.Same(appliedSnapshot, account.CurrentSnapshot);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void AccountWarning_StaleRetryPreservesReasonWhenOnlyCliDiagnosticCanBeTranslated(bool hasObservedAt)
	{
		using IDisposable language = UiText.UseLanguage(AppLanguage.English);
		const string reason = "Unknown provider diagnostic: 生資料 [opaque_271]\r\nOpaque note: It will retry automatically later.";
		string error = $"{reason}。AI Usage 稍後會自動再試，並保留上次成功讀取的資料。 CLI 版本診斷：偵測到 Grok CLI 1.0.4；本版相容性基準為 1.0.3。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。";
		AccountProfile profile = new(
			Guid.Parse("56895652-e8fb-47e7-b699-dc3f8fb29a58"),
			ProviderKind.Grok,
			"Synthetic account",
			ProviderAccountIdentity: "synthetic-account");
		DateTimeOffset fetchedAt = new(2026, 9, 29, 0, 1, 0, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			[new("grok.weekly", "週用量", 10, "已使用 10%")],
			SourceTrust.Official,
			SnapshotStatus.Stale,
			fetchedAt,
			ObservedAt: hasObservedAt ? fetchedAt.AddMinutes(-1) : null,
			Error: error,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);
		AccountUsageViewModel account = new(profile, canManage: true);
		account.ApplySnapshot(snapshot);
		Assert.Contains(reason, account.RecoveryActionDescription);
		Assert.Contains("Detected Grok CLI 1.0.4", account.RecoveryActionDescription);
		Assert.Contains("compatibility baseline is 1.0.3", account.RecoveryActionDescription);
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(account.RecoveryActionDescription, "retry automatically").Count);
		Assert.Contains("AI Usage will retry automatically; no action is needed.", account.RecoveryActionDescription);
		Assert.DoesNotContain("AI Usage 稍後會自動再試", account.RecoveryActionDescription);
		Assert.Equal(error, account.CurrentSnapshot?.Error);
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
