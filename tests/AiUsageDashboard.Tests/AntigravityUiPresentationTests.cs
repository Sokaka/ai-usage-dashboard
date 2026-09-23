using System.Windows.Controls;
using System.Windows.Input;

using AiUsageDashboard.Antigravity.Setup;
using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityUiPresentationTests
{
	[Fact]
	public void SetupDisclosure_ExplainsSourceBoundaryWithoutUnverifiedGuarantees()
	{
		string xaml = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup",
			"SetupWindow.xaml"));

		Assert.Contains("官方 Antigravity CLI", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"只接受支援官方唯讀 /usage 的版本",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains("不會讀取登入憑證", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"跟隨這台電腦目前的 Antigravity 登入",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains("無法確認或固定企業專案範圍", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("不會開啟對話", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("送出模型請求", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"官方用量讀取流程回傳的四項用量、取得時間",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"不會安裝 status-line helper",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains("這台電腦核准的 Antigravity CLI 來源", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"目前登入的 Antigravity 帳號",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain("正常輸入指令", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"關閉其他 Antigravity 視窗",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain("讀取帳號識別", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("模型 turn", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("舊版相容", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("ConPTY", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("本機 Antigravity 登入", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("確認登入與用量", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("AGY", xaml, StringComparison.Ordinal);
	}

	[Fact]
	public void SetupCopy_UsesPlainTraditionalChineseForRecoveryAndTechnicalInfo()
	{
		string setupDirectory = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup");
		string setupWindowSource = File.ReadAllText(Path.Combine(
			setupDirectory,
			"SetupWindow.xaml.cs"));
		string setupAppPath = Path.Combine(
			setupDirectory,
			"App.xaml.cs");

		Assert.Contains("agy --version", setupWindowSource, StringComparison.Ordinal);
		Assert.Contains("已登入且能顯示用量", setupWindowSource, StringComparison.Ordinal);
		Assert.DoesNotContain("agy -p /usage", setupWindowSource, StringComparison.Ordinal);
		Assert.Contains(
			"Antigravity 連接技術資訊",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.Contains("失敗類型：", setupWindowSource, StringComparison.Ordinal);
		Assert.Contains("建議處理方式：", setupWindowSource, StringComparison.Ordinal);
		Assert.Contains(
			"連接設定已儲存，但收尾時發生問題",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"正在安全停止並清理本次設定",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"無法自動安全恢復",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"安全診斷資訊已複製",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.False(File.Exists(setupAppPath));
		Assert.DoesNotContain(
			"舊版相容模式",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain("新版官方", setupWindowSource, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"setup diagnostics",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain("App version:", setupWindowSource, StringComparison.Ordinal);
		Assert.DoesNotContain("Failure kind:", setupWindowSource, StringComparison.Ordinal);
		Assert.DoesNotContain("Next step:", setupWindowSource, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorDisclosure_ExplainsAntigravityBoundaries()
	{
		string notice =
			AccountEditorWindow.GetProviderNoticeText(ProviderKind.Antigravity)!;
		string storedData =
			AccountEditorWindow.GetProviderStoredDataText(ProviderKind.Antigravity);
		string connectionHint = AccountEditorWindow.GetConnectionHintText(
			ProviderKind.Antigravity,
			isEnabled: true);
		string disclosure = notice + storedData + connectionHint;
		string xaml = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AccountEditorWindow.xaml"));

		Assert.Contains("請先在這台電腦登入 Antigravity", notice, StringComparison.Ordinal);
		Assert.Contains(
			"支援官方唯讀 /usage 的 Antigravity 版本",
			notice,
			StringComparison.Ordinal);
		Assert.Contains("不會另開登入畫面", notice, StringComparison.Ordinal);
		Assert.DoesNotContain("跟著本機登入", notice, StringComparison.Ordinal);
		Assert.Contains(
			"使用企業帳號時，AI Usage 無法確認或指定專案",
			notice,
			StringComparison.Ordinal);
		Assert.Contains("Antigravity CLI", storedData, StringComparison.Ordinal);
		Assert.Contains("四項用量和讀取時間", storedData, StringComparison.Ordinal);
		Assert.Contains("官方 /usage 不提供電子郵件或方案", storedData, StringComparison.Ordinal);
		Assert.Contains("不會為此安裝 status-line helper", storedData, StringComparison.Ordinal);
		Assert.Contains(
			"不會讀取密碼或其他登入憑證",
			storedData,
			StringComparison.Ordinal);
		Assert.Contains(
			"使用企業帳號時，AI Usage 無法確認或指定專案",
			connectionHint,
			StringComparison.Ordinal);
		Assert.Contains(
			"沿用這台電腦目前登入的 Antigravity 帳號",
			connectionHint,
			StringComparison.Ordinal);
		Assert.DoesNotContain("不會開啟對話", disclosure, StringComparison.Ordinal);
		Assert.DoesNotContain("送出模型請求", disclosure, StringComparison.Ordinal);
		Assert.Contains("不會儲存密碼", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("Grok Build CLI", disclosure, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"只會儲存該電子郵件",
			disclosure,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"關閉其他 Antigravity 視窗",
			disclosure,
			StringComparison.Ordinal);
		Assert.DoesNotContain("讀取帳號識別", disclosure, StringComparison.Ordinal);
		Assert.DoesNotContain("模型 turn", disclosure, StringComparison.Ordinal);
		Assert.DoesNotContain("本機 Antigravity 登入", disclosure, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"讀取這台電腦上的帳號識別",
			disclosure,
			StringComparison.Ordinal);
		Assert.DoesNotContain("AGY", disclosure, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorProviderOptions_WhenAntigravityCardExists_KeepDisabledAnnotatedOption()
	{
		IReadOnlyList<AccountEditorWindow.ProviderOption> options =
			AccountEditorWindow.GetProviderOptionsForNewAccount(
				canAddAntigravity: false);
		AccountEditorWindow.ProviderOption antigravity = Assert.Single(
			options,
			option => option.Provider == ProviderKind.Antigravity);

		Assert.Equal(5, options.Count);
		Assert.False(antigravity.IsEnabled);
		Assert.Equal(
			"這個 Windows 帳號已有 Antigravity 卡片，不能再新增。",
			antigravity.UnavailableReason);
		Assert.Equal(string.Empty, antigravity.SearchText);
		Assert.False(AccountEditorWindow.TryGetEnabledProvider(
			antigravity,
			out _));
		Assert.All(
			options.Where(option => option.Provider != ProviderKind.Antigravity),
			option =>
			{
				Assert.True(option.IsEnabled);
				Assert.Null(option.UnavailableReason);
			});

		string xaml = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AccountEditorWindow.xaml"));

		Assert.Contains("Value=\"{Binding IsEnabled}\"", xaml, StringComparison.Ordinal);
		Assert.Contains(
			"TextSearch.TextPath=\"SearchText\"",
			xaml,
			StringComparison.Ordinal);
		Assert.Contains(
			"Text=\"{Binding UnavailableReason}\"",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ProviderAvailabilityTextBlock",
			xaml,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountEditorProviderOptions_WhenAntigravityCanBeAdded_EnableEveryOption()
	{
		IReadOnlyList<AccountEditorWindow.ProviderOption> options =
			AccountEditorWindow.GetProviderOptionsForNewAccount(
				canAddAntigravity: true);

		Assert.Equal(5, options.Count);
		Assert.All(
			options,
			option =>
			{
				Assert.True(option.IsEnabled);
				Assert.Null(option.UnavailableReason);
				Assert.Equal(option.DisplayName, option.SearchText);
				Assert.True(AccountEditorWindow.TryGetEnabledProvider(
					option,
					out ProviderKind provider));
				Assert.Equal(option.Provider, provider);
			});
	}

	[Fact]
	public async Task AccountEditorProviderComboBox_TypeSearchSkipsDisabledOption()
	{
		await RunOnStaThreadAsync(() =>
		{
			IReadOnlyList<AccountEditorWindow.ProviderOption> options =
				AccountEditorWindow.GetProviderOptionsForNewAccount(
					canAddAntigravity: false);
			ComboBox baselineComboBox = CreateProviderComboBox(
				options,
				nameof(AccountEditorWindow.ProviderOption.DisplayName));
			RaiseTextInput(baselineComboBox, "A");
			Assert.Equal(
				ProviderKind.Antigravity,
				baselineComboBox.SelectedValue);

			ComboBox fixedComboBox = CreateProviderComboBox(
				options,
				nameof(AccountEditorWindow.ProviderOption.SearchText));
			RaiseTextInput(fixedComboBox, "A");
			Assert.Equal(ProviderKind.Claude, fixedComboBox.SelectedValue);

			ComboBox enabledOptionComboBox = CreateProviderComboBox(
				options,
				nameof(AccountEditorWindow.ProviderOption.SearchText));
			RaiseTextInput(enabledOptionComboBox, "Git");
			Assert.Equal(
				ProviderKind.Copilot,
				enabledOptionComboBox.SelectedValue);
		});
	}

	[Fact]
	public void SetupReview_WhenAgyDoesNotReturnEmail_UsesLocalLoginLabel()
	{
		Assert.Equal(
			"目前登入的 Antigravity 帳號（未取得電子郵件）",
			SetupWindow.GetAccountIdentityDisplayText(
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity));
		Assert.Equal(
			"person@example.com",
			SetupWindow.GetAccountIdentityDisplayText(
				"person@example.com"));
	}

	[Theory]
	[InlineData("agy.gemini.rolling-5h", "5小時用量 · Gemini")]
	[InlineData("agy.gemini.weekly", "週用量 · Gemini")]
	[InlineData("agy.claude.rolling-5h", "5小時用量 · Claude + GPT")]
	[InlineData("agy.claude.weekly", "週用量 · Claude + GPT")]
	public void SetupReview_UsageWindowNamesMatchDashboardPresentation(
		string stableWindowId,
		string expected)
	{
		Assert.Equal(
			expected,
			SetupWindow.GetUsageWindowDisplayName(stableWindowId));
	}

	[Fact]
	public void AntigravityPreflight_ExplainsNewAndExistingConnectionBoundaries()
	{
		string message = AccountConnectionCoordinator.AntigravityPreflightMessage;

		Assert.Contains("請先登入 Antigravity", message, StringComparison.Ordinal);
		Assert.Contains("Antigravity CLI 與既有連接", message, StringComparison.Ordinal);
		Assert.Contains(
			"只接受 1.1.11 以上、2.0.0 未滿且支援官方唯讀 /usage 的版本",
			message,
			StringComparison.Ordinal);
		Assert.DoesNotContain("舊版相容", message, StringComparison.Ordinal);
		Assert.Contains("讀取目前帳號的四項用量", message, StringComparison.Ordinal);
		Assert.Contains("不會讀取登入憑證", message, StringComparison.Ordinal);
		Assert.Contains(
			"跟隨這台電腦目前的 Antigravity 登入",
			message,
			StringComparison.Ordinal);
		Assert.Contains(
			"無法確認或固定企業專案範圍",
			message,
			StringComparison.Ordinal);
		Assert.DoesNotContain("不會開啟對話", message, StringComparison.Ordinal);
		Assert.DoesNotContain("送出模型請求", message, StringComparison.Ordinal);
		Assert.DoesNotContain("模型 turn", message, StringComparison.Ordinal);
		Assert.DoesNotContain("正常輸入指令", message, StringComparison.Ordinal);
		Assert.DoesNotContain("關閉", message, StringComparison.Ordinal);
		Assert.DoesNotContain("AGY", message, StringComparison.Ordinal);
	}

	[Fact]
	public void DashboardCard_DoesNotShowAntigravityLocalLoginScope()
	{
		string xaml = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"FloatingWidgetWindow.xaml"));

		Assert.DoesNotContain(
			"跟隨本機 Antigravity 登入 · 未固定專案",
			xaml,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"這張卡會跟隨這台電腦目前的 Antigravity 登入；AI Usage 無法確認或固定企業專案範圍。",
			xaml,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountPresentation_WithLocalSessionIdentity_HidesSentinelButKeepsBinding()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		Assert.Equal(Identity, viewModel.ProviderAccountIdentity);
		Assert.Equal(Identity, viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(
			"Antigravity · 目前登入的 Antigravity 帳號",
			viewModel.AccountName);
		Assert.Equal(
			"目前登入的 Antigravity 帳號（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"目前登入的 Antigravity 帳號（上次確認）",
			viewModel.ProviderAccountDisplayText);
		Assert.Contains(
			"帳號：目前登入的 Antigravity 帳號",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Identity,
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Identity,
			viewModel.ProviderAccountDisplayText,
			StringComparison.Ordinal);

		viewModel.ApplySnapshot(CreateReadySnapshot(profile));

		Assert.Equal(
			"目前登入的 Antigravity 帳號",
			viewModel.AccountDisplayText);
		Assert.Equal(Identity, viewModel.ProviderAccountIdentity);
		Assert.Equal(Identity, viewModel.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountRecovery_WhenOfficialUsageHasHardSafetyLatch_OffersExplicitRecheck()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		Assert.True(viewModel.HasExecutableRecoveryAction);
		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.ShowRecoveryActionNotice);
		Assert.Equal(
			"重新檢查 Antigravity 用量",
			viewModel.RecoveryActionText);
		Assert.Contains(
			"上次 Antigravity 用量檢查未完成",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.Contains(
			"不會重新登入 Antigravity",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountRecovery_WhenAutomaticRetryIsPending_StaysNonIntrusive()
	{
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "AGY 用量檢查暫時未完成。",
			RecoveryAction: UsageRecoveryAction.Retry));

		Assert.True(viewModel.HasRecoveryAction);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		Assert.False(viewModel.HasExecutableRecoveryAction);
		Assert.Null(AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));
	}

	[Fact]
	public void AccountPresentation_WithRecentAgyEmail_UsesDisplayMetadataButKeepsBinding()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));

		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));

		Assert.Equal(
			"Antigravity · person@example.com",
			viewModel.AccountName);
		Assert.Equal(
			"person@example.com",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"person@example.com",
			viewModel.ProviderAccountDisplayText);
		Assert.Equal(Identity, viewModel.ProviderAccountIdentity);
		Assert.Equal(Identity, viewModel.Profile.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountPresentation_WithStaleAgyEmail_LabelsEveryDisplayEntry()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow -
			TimeSpan.FromHours(1);
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));

		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: true));

		Assert.Equal(
			"Antigravity · person@example.com（上次確認）",
			viewModel.AccountName);
		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.ProviderAccountDisplayText);
		Assert.Equal(Identity, viewModel.ProviderAccountIdentity);
	}

	[Fact]
	public void AccountPresentation_WhenAgyQuotaSnapshotGenerationChanges_PreservesEmailUntilReconciliation()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset firstObservedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, firstObservedAt));
		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"old-account@example.com",
			firstObservedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));

		viewModel.ApplySnapshot(CreateReadySnapshot(
			profile,
			firstObservedAt + TimeSpan.FromSeconds(10)));

		Assert.Equal(
			"old-account@example.com",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"Antigravity · old-account@example.com",
			viewModel.AccountName);
	}

	[Fact]
	public void AccountPresentation_WhenProfileAliasChanges_PreservesAssociatedAgyEmail()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));
		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));

		viewModel.ApplyProfile(
			profile with { DisplayName = "工作帳號" },
			canManage: true);

		Assert.Equal("工作帳號", viewModel.AccountName);
		Assert.Equal(
			"person@example.com",
			viewModel.AccountDisplayText);
		Assert.True(viewModel.HasAccountAlias);
	}

	[Fact]
	public void AccountFallbackNotice_WithLocalSessionIdentity_HidesSentinel()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));
		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"old-report@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));
		Assert.Contains(
			"old-report@example.com",
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
		viewModel.BeginProviderAccountChange();
		UsageSnapshot fallback = Assert.IsType<UsageSnapshot>(
			viewModel.GetProviderAccountChangeFallbackSnapshot());
		Assert.DoesNotContain(
			"old-report@example.com",
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
		Assert.False(viewModel.TrySetAntigravityReportedAccountEmail(
			"new-report@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));

		viewModel.ApplySnapshot(fallback with
		{
			Status = SnapshotStatus.Stale,
			Error = "暫時無法讀取。"
		});

		Assert.Contains(
			"上次確認帳號 目前登入的 Antigravity 帳號",
			viewModel.CurrentSnapshot?.Error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Identity,
			viewModel.CurrentSnapshot?.Error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"new-report@example.com",
			viewModel.CurrentSnapshot?.Error,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"new-report@example.com",
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountPresentation_WhenAgyEmailIsNewerThanQuota_DoesNotAssociateIt()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));

		Assert.False(viewModel.TrySetAntigravityReportedAccountEmail(
			"new-account@example.com",
			observedAt + TimeSpan.FromSeconds(1),
			TimeSpan.FromMinutes(1),
			isStale: false));
		Assert.Equal(
			"目前登入的 Antigravity 帳號",
			viewModel.AccountDisplayText);
		Assert.DoesNotContain(
			"new-account@example.com",
			viewModel.AccountName,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountPresentation_WhenReplacementAgyEmailIsRejected_PreservesExistingReport()
	{
		const string Identity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		DateTimeOffset observedAt = DateTimeOffset.UtcNow;
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity: Identity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(CreateReadySnapshot(profile, observedAt));
		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"old-account@example.com",
			observedAt - TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(1),
			isStale: false));

		Assert.False(viewModel.TrySetAntigravityReportedAccountEmail(
			"unrelated-account@example.com",
			observedAt + TimeSpan.FromSeconds(1),
			TimeSpan.FromMinutes(1),
			isStale: false));

		Assert.Equal(
			"old-account@example.com",
			viewModel.AccountDisplayText);
	}

	[Fact]
	public void AccountSnapshot_WithLegacyBinding_DoesNotMigrateFromNonLiveProjection()
	{
		const string LegacyIdentity = "legacy@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			"AGY",
			ProviderAccountIdentity: LegacyIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = CreateReadySnapshot(profile) with
		{
			SourceTrust = SourceTrust.OfficialExperimental
		};

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(AccountStatusKind.Error, viewModel.StatusKind);
		Assert.True(viewModel.DidRejectProviderAccountSnapshot);
		Assert.Equal(LegacyIdentity, viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(LegacyIdentity, viewModel.ProviderAccountIdentity);
	}

	[Theory]
	[InlineData(ProviderKind.Antigravity, "other@example.com")]
	[InlineData(
		ProviderKind.Claude,
		AntigravityOfficialPrintUsageClient.LocalSessionIdentity)]
	public void AccountLiveSnapshotMigration_WithDifferentIdentityOrProvider_Rejects(
		ProviderKind provider,
		string snapshotIdentity)
	{
		const string BoundIdentity = "bound@example.com";
		AccountProfile profile = new(
			Guid.NewGuid(),
			provider,
			"Account",
			ProviderAccountIdentity: BoundIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"usage",
					"Usage",
					25d,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: snapshotIdentity);

		viewModel.ApplyLiveSnapshot(snapshot);

		Assert.Equal(AccountStatusKind.Error, viewModel.StatusKind);
		Assert.True(viewModel.DidRejectProviderAccountSnapshot);
		Assert.Equal(BoundIdentity, viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(BoundIdentity, viewModel.ProviderAccountIdentity);
	}

	private static UsageSnapshot CreateReadySnapshot(
		AccountProfile profile,
		DateTimeOffset? observedAt = null)
	{
		DateTimeOffset snapshotObservedAt = observedAt ?? DateTimeOffset.UtcNow;
		return new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini 每週",
					25d,
					"剩餘 75%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			snapshotObservedAt,
			snapshotObservedAt,
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
	}

	private static Task RunOnStaThreadAsync(Action operation)
	{
		TaskCompletionSource<bool> taskSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			try
			{
				operation();
				taskSource.TrySetResult(true);
			}
			catch (Exception exception)
			{
				taskSource.TrySetException(exception);
			}
		})
		{
			IsBackground = true
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return taskSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static ComboBox CreateProviderComboBox(
		IReadOnlyList<AccountEditorWindow.ProviderOption> options,
		string textPath)
	{
		ComboBox comboBox = new()
		{
			ItemsSource = options,
			SelectedValuePath = nameof(
				AccountEditorWindow.ProviderOption.Provider),
			SelectedValue = ProviderKind.Claude
		};
		TextSearch.SetTextPath(comboBox, textPath);
		return comboBox;
	}

	private static void RaiseTextInput(ComboBox comboBox, string text)
	{
		TextComposition composition = new(
			InputManager.Current,
			comboBox,
			text);
		TextCompositionEventArgs eventArgs = new(
			Keyboard.PrimaryDevice,
			composition)
		{
			RoutedEvent = TextCompositionManager.TextInputEvent
		};
		comboBox.RaiseEvent(eventArgs);
	}
}
